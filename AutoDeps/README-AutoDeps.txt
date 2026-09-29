UnityExplorer 4.9.3 (Mono) + AutoDeps 自动依赖切换器 — v16m 2026-09-26
========================================================================

包结构：
  fmod.dll                                 (随包附带，勿删)
  Mods\UnityExplorer.ML.Mono.dll           UE 主体 (v16m)
  Mods\UniverseLib.Mono.dll                UniverseLib 1.6.2 (ML <=0.4.3 布局用)
  UserLibs\UniverseLib.Mono.dll            UniverseLib 1.6.2 (ML 0.5.1+ 布局用)
  Plugins\0_AutoDeps.dll                   自动依赖切换器 (MelonPlugin)
  MelonLoader\AutoDeps_Deps\deps-net472\   CLR4 游戏依赖 (0Harmony 2.10.2 手术版 + MonoMod 22.7.31.1 + Cecil 0.11.4 等 15 个)
  MelonLoader\AutoDeps_Deps\deps-net35\    CLR2 游戏依赖 (0Harmony net35 + MonoMod 22.3.23.4 + Cecil 0.10.4 等 8 个)

使用：解压到游戏根目录（与游戏 exe 同级）。

说明：
- 0_AutoDeps 启动时自动检测游戏 CLR 版本并抢先加载对应依赖目录；
  ML 0.5.4+（自带依赖）或已有 UserLibs 的环境会自动跳过，不干预。
- 日志中 [0_AutoDeps] CLR4 → deps-net472, 已加载 15 个依赖 即为生效。
- ML <=0.4.3 下可能出现 5 条 UniverseLib patch WARNING（Cursor/EventSystem/AssetBundle/Dropdown），
  系老版本 mono 的已知限制，不影响 UI 与主功能。
- 日志中 "Failed to Load Assembly for UniverseLib.Mono.dll: No Compatibility Layer Found" 为 ML
  尝试把 UniverseLib 当 Mod 加载的良性提示，可忽略。
