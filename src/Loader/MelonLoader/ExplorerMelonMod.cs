#if ML
using System;
using System.IO;
using MelonLoader;
using UnityExplorer;
using UnityExplorer.Config;
using UnityExplorer.Loader.ML;

#if CPP
[assembly: MelonPlatformDomain(MelonPlatformDomainAttribute.CompatibleDomains.IL2CPP)]
#else
[assembly: MelonPlatformDomain(MelonPlatformDomainAttribute.CompatibleDomains.MONO)]
#endif

[assembly: MelonInfo(typeof(ExplorerMelonMod), ExplorerCore.NAME, ExplorerCore.VERSION, ExplorerCore.AUTHOR)]
[assembly: MelonGame(null, null)]
#if ML073
[assembly: MelonColor(0, 139, 139, 255)]
#else
[assembly: MelonColor(ConsoleColor.DarkCyan)]
#endif

namespace UnityExplorer
{
    public class ExplorerMelonMod : MelonMod, IExplorerLoader
    {
        public string ExplorerFolderName => ExplorerCore.DEFAULT_EXPLORER_FOLDER_NAME;
#if ML073
        private static string ModsDirectory => MelonLoader.Utils.MelonEnvironment.ModsDirectory;
#else
        private static string ModsDirectory => MelonHandler.ModsDirectory;
#endif
        public string ExplorerFolderDestination => ModsDirectory;

#if ML073
        // MelonLoader 0.7.x：Il2CppInterop 互操作代理程序集位于 MelonLoader/Il2CppAssemblies。
        // 旧版 Unhollower 的 MelonLoader/Managed 目录在 0.7 中已不存在，
        // 指向不存在的目录会导致 UniverseLib 启动时打印 "Expected Unhollowed folder path does not exist" 告警。
        public string UnhollowedModulesFolder => Path.Combine(
            Path.GetDirectoryName(ModsDirectory),
            Path.Combine("MelonLoader", "Il2CppAssemblies"));
#else
        public string UnhollowedModulesFolder => Path.Combine(
            Path.GetDirectoryName(ModsDirectory),
            Path.Combine("MelonLoader", "Managed"));
#endif

        public ConfigHandler ConfigHandler => _configHandler;
        public MelonLoaderConfigHandler _configHandler;

        // v16bf 兼容 ML 0.4.x：LoggerInstance（MelonLogger.Instance）是 0.5.0 才引入的嵌套类，
        // 0.4.x 没有该类型会直接 TypeLoadException。静态 MelonLogger.Msg/Warning/Error 在
        // 0.4.3 ~ 0.7.3 全部存在（已用 dnfile 双版本核验），改用静态调用实现全版本兼容。
        public Action<object> OnLogMessage => log => MelonLogger.Msg(log?.ToString() ?? "");
        public Action<object> OnLogWarning => log => MelonLogger.Warning(log?.ToString() ?? "");
        public Action<object> OnLogError   => log => MelonLogger.Error(log?.ToString() ?? "");

#if ML073
        public override void OnInitializeMelon()
#else
        public override void OnApplicationStart()
#endif
        {
            _configHandler = new MelonLoaderConfigHandler();
            ExplorerCore.Init(this);
        }
    }
}
#endif