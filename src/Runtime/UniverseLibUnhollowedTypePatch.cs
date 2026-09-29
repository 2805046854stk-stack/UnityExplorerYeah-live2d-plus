#if INTEROP && ML
using System;
using System.Reflection;
using HarmonyLib;
using UniverseLib;

namespace UnityExplorer.Runtime
{
    // 修复 MelonLoader 0.7.x (Il2CppInterop 后端) 下 UniverseLib 2.0.3 的
    // "Failed to get Unhollowed type from 'xxx, Assembly-CSharp...'" 警告刷屏：
    //
    // ML 0.7 生成的互操作代理程序集会给游戏类型的命名空间加 "Il2Cpp" 前缀
    // （如 Il2CppLive2D.Cubism.Core.CubismModel；无命名空间的全局类型则位于 Il2Cpp 命名空间，
    //   即 Il2Cpp.stand），而 UniverseLib 的 ML 命名规则分支未启用，
    //   GetUnhollowedType 按原始名查 AllTypes 全部 miss → Type.GetType 也失败
    //   → Inspector 每次 UI 刷新都对每个游戏组件类型重复打印一条警告。
    //
    // 本补丁用 Harmony 前缀在原实现之前优先按 "Il2Cpp" 前缀查找 ReflectionUtility.AllTypes，
    // 命中则直接返回代理类型（同时改善 Inspector 对游戏类型的成员显示）；
    // 未命中则回退原实现，保留其基元/数组/字符串/反混淆等原有逻辑。
    internal static class UniverseLibUnhollowedTypePatch
    {
        internal static void Apply()
        {
            try
            {
                Type il2cppReflection = AccessTools.TypeByName("UniverseLib.Il2CppReflection");
                if (il2cppReflection == null)
                {
                    ExplorerCore.LogWarning("未找到 UniverseLib.Il2CppReflection，跳过 GetUnhollowedType 补丁。");
                    return;
                }

                MethodBase target = AccessTools.Method(il2cppReflection, "GetUnhollowedType");
                if (target == null)
                {
                    ExplorerCore.LogWarning("未找到 GetUnhollowedType 方法，跳过 GetUnhollowedType 补丁。");
                    return;
                }

                var prefix = new HarmonyMethod(AccessTools.Method(typeof(UniverseLibUnhollowedTypePatch), nameof(Prefix)));
                ExplorerCore.Harmony.Patch(target, prefix);
                ExplorerCore.Log("已应用 UniverseLib GetUnhollowedType 补丁。");
            }
            catch (Exception ex)
            {
                ExplorerCore.LogWarning($"应用 UniverseLib GetUnhollowedType 补丁失败: {ex}");
            }
        }

        // 返回 false 表示已处理（跳过原实现）；返回 true 则继续执行原实现。
        static bool Prefix(Il2CppSystem.Type cppType, ref Type __result)
        {
            try
            {
                if (cppType == null)
                    return true;

                // 数组需包装为 Il2Cpp*Array<>，交由原实现处理
                if (cppType.IsArray)
                    return true;

                string fullname = cppType.FullName;
                if (string.IsNullOrEmpty(fullname))
                    return true;

                // System.*（基元/字符串/泛型）与 UnityEngine.* 代理类型不加前缀，交由原实现处理
                if (fullname.StartsWith("System.") || fullname == "System.String" || fullname.StartsWith("Il2Cpp"))
                    return true;
                if (fullname.StartsWith("Unity") && fullname.Contains('.'))
                    return true;

                // ML 0.7 Il2CppInterop：游戏程序集（Assembly-CSharp 等）的代理类型
                // 命名空间带 "Il2Cpp" 前缀；无命名空间类型的代理位于 Il2Cpp 命名空间下。
                string prefixed = fullname.Contains('.')
                    ? "Il2Cpp" + fullname
                    : "Il2Cpp." + fullname;

                if (ReflectionUtility.AllTypes.TryGetValue(prefixed, out Type proxyType))
                {
                    __result = proxyType;
                    return false;
                }
            }
            catch
            {
                // 任何异常都回退原实现，不影响原有行为
            }

            return true;
        }
    }
}
#endif
