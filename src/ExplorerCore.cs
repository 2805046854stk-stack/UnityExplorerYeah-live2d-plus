global using System;
global using System.Collections.Generic;
global using System.IO;
global using System.Linq;
global using System.Reflection;
global using UnityEngine;
global using UnityEngine.UI;
global using UniverseLib;
global using UniverseLib.Utility;
using UnityExplorer.Config;
using UnityExplorer.ObjectExplorer;
using UnityExplorer.Runtime;
using UnityExplorer.UI;
using UnityExplorer.UI.Panels;
using UniverseLib.Input;

namespace UnityExplorer
{
    public static class ExplorerCore
    {
        public const string NAME = "UnityExplorer";
        public const string VERSION = "4.9.2";
        public const string AUTHOR = "Sinai";
        public const string GUID = "com.sinai.unityexplorer";

        public static IExplorerLoader Loader { get; private set; }
        public static string ExplorerFolder => Path.Combine(Loader.ExplorerFolderDestination, Loader.ExplorerFolderName);
        public const string DEFAULT_EXPLORER_FOLDER_NAME = "sinai-dev-UnityExplorer";

        public static HarmonyLib.Harmony Harmony { get; } = new HarmonyLib.Harmony(GUID);

        // Win32 控制台代码页修复: Mono 的 Console.OutputEncoding setter 不会真正调用
        // SetConsoleOutputCP, 必须 P/Invoke。ML 0.6+ 原生日志 / BepInEx 控制台均写 UTF-8
        // 字节, 中文 Windows 控制台默认 CP936(GBK) → 中文全部 mojibake, 切 65001 修复。
        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetConsoleOutputCP(uint wCodePageID);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetConsoleCP(uint wCodePageID);

        internal static void FixConsoleEncoding()
        {
            try
            {
                if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                {
                    SetConsoleOutputCP(65001);
                    SetConsoleCP(65001);
                }
            }
            catch { }
        }

        /// <summary>
        /// Initialize UnityExplorer with the provided Loader implementation.
        /// </summary>
        public static void Init(IExplorerLoader loader)
        {
            if (Loader != null)
                throw new Exception("UnityExplorer 已经加载.");

            Loader = loader;

            // Fix Chinese garbled text in console/log (UTF-8 bytes vs GBK codepage mismatch).
            // Mono 的 Console.OutputEncoding 不改 Win32 代码页, 必须直接 P/Invoke (见 FixConsoleEncoding)。
            FixConsoleEncoding();
            try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }

            Log($"{NAME} {VERSION} 初始化...");

            CheckLegacyExplorerFolder();
            Directory.CreateDirectory(ExplorerFolder);
            ConfigManager.Init(Loader.ConfigHandler);

#if INTEROP
            UniverseLibCacheTypesPatch.Apply();
#endif

#if INTEROP && ML
            UniverseLibUnhollowedTypePatch.Apply();
#endif

#if MONO
            Universe.Init(ConfigManager.Startup_Delay_Time.Value, LateInit, Log, new()
            {
                Disable_EventSystem_Override = ConfigManager.Disable_EventSystem_Override.Value,
                Force_Unlock_Mouse = ConfigManager.Force_Unlock_Mouse.Value,
                Disable_Setup_Force_ReLoad_ManagedAssemblies = ConfigManager.Disable_Setup_Force_ReLoad_ManagedAssemblies.Value,
                Bypass_UniverseLib_ICall = ConfigManager.Bypass_UniverseLib_ICall.Value,
                Unhollowed_Modules_Folder = loader.UnhollowedModulesFolder
            });
#else
            Universe.Init(ConfigManager.Startup_Delay_Time.Value, LateInit, Log, new()
            {
                Disable_EventSystem_Override = ConfigManager.Disable_EventSystem_Override.Value,
                Force_Unlock_Mouse = ConfigManager.Force_Unlock_Mouse.Value,
                Unhollowed_Modules_Folder = loader.UnhollowedModulesFolder
            });
#endif

            UERuntimeHelper.Init();
            ExplorerBehaviour.Setup();
            UnityCrashPrevention.Init();
        }

        // Do a delayed setup so that objects aren't destroyed instantly.
        // This can happen for a multitude of reasons.
        // Default delay is 1 second which is usually enough.
        static void LateInit()
        {
            SceneHandler.Init();

            Log($"创建用户界面...");

            UIManager.InitUI();

            Log($"{NAME} {VERSION} ({Universe.Context}) 初始化.");

            // v16b: Live2D 录制器自动启动（生命周期改靠 UnityExplorer 自带 MonoBehaviour 框架）
            //   - 取消 v16 引入的独立 Live2DAutoTracker.cs：单独 AddComponent<Live2DAutoTracker>
            //     在 IL2CPP Interop 下会触发 MethodInfoStoreGeneric<T> 静态构造异常，
            //     而 UE 框架本身已通过 ClassInjector.RegisterTypeInIl2Cpp 准备好了 ExplorerBehaviour，
            //     直接钩其 Update 循环更稳定（见 ExplorerCore.Update）。
            try { UnityExplorer.Runtime.Live2DRecorder.AutoStartAllScenes(); }
            catch (Exception ex) { LogWarning("[Live2DRecorder] AutoStartAllScenes 失败: " + ex.Message); }

            // InspectorManager.Inspect(typeof(Tests.TestClass));
        }

        internal static void Update()
        {
            try { Live2DRecorder.Tick(); } catch (Exception ex) { /* 单帧异常吞掉，不影响 UE 主循环 */ }
            ExplorerKeybind.Update();
        }


        #region LOGGING

        public static void Log(object message)
            => Log(message, LogType.Log);

        public static void LogWarning(object message)
            => Log(message, LogType.Warning);

        public static void LogError(object message)
            => Log(message, LogType.Error);

        public static void LogUnity(object message, LogType logType)
        {
            if (!ConfigManager.Log_Unity_Debug.Value)
                return;

            Log($"[Unity] {message}", logType);
        }

        private static void Log(object message, LogType logType)
        {
            string log = message?.ToString() ?? "";

            LogPanel.Log(log, logType);

            switch (logType)
            {
                case LogType.Assert:
                case LogType.Log:
                    Loader.OnLogMessage(log);
                    break;

                case LogType.Warning:
                    Loader.OnLogWarning(log);
                    break;

                case LogType.Error:
                case LogType.Exception:
                    Loader.OnLogError(log);
                    break;
            }
        }

        #endregion


        #region LEGACY FOLDER MIGRATION

        // Can be removed eventually. For migration from <4.7.0
        static void CheckLegacyExplorerFolder()
        {
            string legacyPath = Path.Combine(Loader.ExplorerFolderDestination, "UnityExplorer");
            if (Directory.Exists(legacyPath))
            {
                LogWarning($"正在尝试将旧的 'UnityExplorer/' 文件夹迁移到 'sinai-dev-UnityExplorer/'...");

                // If new folder doesn't exist yet, let's just use Move().
                if (!Directory.Exists(ExplorerFolder))
                {
                    try
                    {
                        Directory.Move(legacyPath, ExplorerFolder);
                        Log("迁移成功.");
                    }
                    catch (Exception ex)
                    {
                        LogWarning($"异常迁移文件夹: {ex}");
                    }
                }
                else // We have to merge
                {
                    try
                    {
                        CopyAll(new(legacyPath), new(ExplorerFolder));
                        Directory.Delete(legacyPath, true);
                        Log("Migrated successfully.");
                    }
                    catch (Exception ex)
                    {
                        LogWarning($"Exception migrating folder: {ex}");
                    }
                }
            }
        }

        public static void CopyAll(DirectoryInfo source, DirectoryInfo target)
        {
            Directory.CreateDirectory(target.FullName);

            // Copy each file into it's new directory.
            foreach (FileInfo fi in source.GetFiles())
                fi.MoveTo(Path.Combine(target.ToString(), fi.Name));

            // Copy each subdirectory using recursion.
            foreach (DirectoryInfo diSourceSubDir in source.GetDirectories())
            {
                DirectoryInfo nextTargetSubDir = target.CreateSubdirectory(diSourceSubDir.Name);
                CopyAll(diSourceSubDir, nextTargetSubDir);
            }
        }

        #endregion
    }
}
