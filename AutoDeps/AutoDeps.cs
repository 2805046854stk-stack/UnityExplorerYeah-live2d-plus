using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using MelonLoader;

[assembly: MelonInfo(typeof(UnityExplorer.AutoDeps), "0_AutoDeps", "1.0.0", "UE v16m fork")]

namespace UnityExplorer
{
    /// <summary>
    /// 0_AutoDeps: 自动依赖切换器 (MelonPlugin, 在 Plugins/ 目录, 早于所有 Mods 加载)。
    /// 检测游戏 CLR 版本, 从 deps-net35 / deps-net472 子目录抢先加载配套的
    /// 0Harmony / MonoMod / Cecil, 保证弱命名程序集身份竞争由我们赢。
    /// 若 ML 已自带依赖 (0.5.4+ 的 MelonLoader/ 目录) 或 UserLibs 有依赖, 则不干预。
    /// </summary>
    public class AutoDeps : MelonPlugin
    {
        private static string _depsDir = null;

        // Win32 控制台代码页: Mono 的 Console.OutputEncoding setter 不会真正调用
        // SetConsoleOutputCP, 必须直接 P/Invoke。ML 0.6+ 原生日志写 UTF-8 字节,
        // 中文 Windows 控制台默认 CP936(GBK) → 中文变 mojibake。切到 65001(UTF-8) 修复。
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetConsoleOutputCP(uint wCodePageID);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetConsoleCP(uint wCodePageID);

        private static void FixConsoleEncoding()
        {
            try
            {
                if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                {
                    SetConsoleOutputCP(65001);
                    SetConsoleCP(65001);
                    MelonLogger.Msg("[AutoDeps] 控制台代码页 -> UTF-8 (65001)");
                }
            }
            catch (Exception encEx)
            {
                MelonLogger.Warning("[AutoDeps] 控制台编码修复失败: " + encEx.Message);
            }
        }

        public override void OnPreInitialization()
        {
            try
            {
                // -1. 控制台输出编码修复 (必须在任何中文日志输出前执行)
                FixConsoleEncoding();

                string baseDir = MelonUtils.BaseDirectory;
                string modsDir = Path.Combine(baseDir, "Mods");

                // 0. 兜底: deps 子目录加入 AssemblyResolve (强命名程序集如 Mono.Cecil 需要精确版本命中)
                AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
                {
                    if (_depsDir == null) return null;
                    try
                    {
                        string name = new AssemblyName(e.Name).Name;
                        string path = Path.Combine(_depsDir, name + ".dll");
                        if (File.Exists(path)) return Assembly.LoadFrom(path);
                    }
                    catch { }
                    return null;
                };

                // 1. ML 自带依赖 (0.5.4+: MelonLoader/0Harmony.dll) → 不干预
                if (File.Exists(Path.Combine(Path.Combine(baseDir, "MelonLoader"), "0Harmony.dll")))
                {
                    MelonLogger.Msg("[AutoDeps] ML 自带 Harmony/MonoMod, 跳过");
                    return;
                }

                // 2. UserLibs 有依赖 (0.5.1~0.5.3 布局) → 不干预 (ML 自己的 resolver 会加载)
                if (File.Exists(Path.Combine(Path.Combine(baseDir, "UserLibs"), "0Harmony.dll")))
                {
                    MelonLogger.Msg("[AutoDeps] UserLibs 已有依赖, 跳过");
                    return;
                }

                // 3. CLR 版本检测: mscorlib 4.x → net472, 2.x → net35
                // 依赖目录放在 MelonLoader/AutoDeps_Deps/ (不放 Mods/ 下!)
                // 原因: ML 0.4.3 的 AssemblyResolver 会扫描 Mods/ 并用 LoadFile 重复加载,
                // 产生第二个 MonoMod.Utils 镜像, 导致 ILGeneratorProxy 泛型约束校验失败。
                int clrMajor = typeof(object).Assembly.GetName().Version.Major;
                string sub = clrMajor >= 4 ? "deps-net472" : "deps-net35";
                string depsRoot = Path.Combine(Path.Combine(baseDir, "MelonLoader"), "AutoDeps_Deps");
                string dir = Path.Combine(depsRoot, sub);
                // 兼容旧布局: 若 MelonLoader/AutoDeps_Deps 不存在, 回退 Mods/ 下的旧位置
                if (!Directory.Exists(dir))
                {
                    string legacy = Path.Combine(modsDir, sub);
                    if (Directory.Exists(legacy)) dir = legacy;
                }

                if (!Directory.Exists(dir))
                {
                    MelonLogger.Warning("[AutoDeps] 未找到 " + sub + " 目录, 依赖未自动加载");
                    return;
                }

                // 4. 按依赖顺序加载 (底层库在前): Cecil → Backports → ILHelpers → Utils → RuntimeDetour → 0Harmony → 其他
                string[] order = new string[]
                {
                    "Mono.Cecil.dll", "Mono.Cecil.Mdb.dll", "Mono.Cecil.Pdb.dll", "Mono.Cecil.Rocks.dll",
                    "MonoMod.Backports.dll", "MonoMod.ILHelpers.dll",
                    "MonoMod.Utils.dll", "MonoMod.RuntimeDetour.dll",
                    "0Harmony.dll",
                    "System.Memory.dll", "System.Buffers.dll", "System.Numerics.Vectors.dll",
                    "System.Runtime.CompilerServices.Unsafe.dll", "System.ValueTuple.dll", "IndexRange.dll"
                };

                int loaded = 0;
                _depsDir = dir;
                foreach (string name in order)
                {
                    string path = Path.Combine(dir, name);
                    if (!File.Exists(path)) continue;
                    try
                    {
                        Assembly.LoadFrom(path);
                        loaded++;
                    }
                    catch (Exception e)
                    {
                        MelonLogger.Warning("[AutoDeps] " + name + " 加载失败: " + e.Message);
                    }
                }
                MelonLogger.Msg("[AutoDeps] CLR" + clrMajor + " → " + sub + ", 已加载 " + loaded + " 个依赖");
            }
            catch (Exception ex)
            {
                MelonLogger.Error("[AutoDeps] 初始化失败: " + ex);
            }
        }
    }
}
