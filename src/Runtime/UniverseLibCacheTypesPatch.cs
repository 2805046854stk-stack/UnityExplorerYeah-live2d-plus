#if INTEROP
using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UniverseLib;

namespace UnityExplorer.Runtime
{
    // 修复 UniverseLib 2.0.3 (Il2CppInterop 后端) 在 IL2CPP 下的初始化崩溃：
    // ReflectionUtility.CacheTypes 遍历程序集类型时，对泛型类型
    // （如 UnityEngine.UIElements.BaseField<T>）访问 type.Namespace / type.FullName
    // 会触发 RuntimeTypeHandle.GetDeclaringType 抛 TypeLoadException，且该版本
    // CacheTypes 为裸调用（无 try-catch），导致 Universe.Init 直接失败。
    // 本补丁用 Harmony 前缀完全接管 CacheTypes，按原逻辑做安全缓存并跳过异常类型。
    internal static class UniverseLibCacheTypesPatch
    {
        private static readonly FieldInfo f_uniqueNamespaces =
            typeof(ReflectionUtility).GetField("uniqueNamespaces", BindingFlags.NonPublic | BindingFlags.Static);

        private static readonly FieldInfo f_onTypeLoaded =
            typeof(ReflectionUtility).GetField("OnTypeLoaded", BindingFlags.NonPublic | BindingFlags.Static);

        internal static void Apply()
        {
            try
            {
                MethodBase target = AccessTools.Method(typeof(ReflectionUtility), "CacheTypes", new[] { typeof(Assembly) });
                var prefix = new HarmonyMethod(AccessTools.Method(typeof(UniverseLibCacheTypesPatch), nameof(Prefix)));
                ExplorerCore.Harmony.Patch(target, prefix);
                ExplorerCore.Log("已应用 UniverseLib CacheTypes 安全补丁。");
            }
            catch (Exception ex)
            {
                ExplorerCore.LogWarning($"应用 UniverseLib CacheTypes 补丁失败: {ex}");
            }
        }

        static bool Prefix(Assembly asm)
        {
            SafeCacheTypes(asm);
            return false;
        }

        static void SafeCacheTypes(Assembly asm)
        {
            Type[] types;
            try
            {
                types = asm.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types;
            }
            catch
            {
                return;
            }

            if (types == null)
                return;

            SortedDictionary<string, Type> allTypes = ReflectionUtility.AllTypes;
            List<string> allNamespaces = ReflectionUtility.AllNamespaces;
            var uniqueNamespaces = (HashSet<string>)f_uniqueNamespaces.GetValue(null);
            var onTypeLoaded = (Action<Type>)f_onTypeLoaded?.GetValue(null);

            foreach (Type type in types)
            {
                if (type == null)
                    continue;

                try
                {
                    string ns = type.Namespace;
                    string fullName = type.FullName;

                    if (!string.IsNullOrEmpty(ns) && !uniqueNamespaces.Contains(ns))
                    {
                        uniqueNamespaces.Add(ns);
                        int j = 0;
                        while (j < allNamespaces.Count && ns.CompareTo(allNamespaces[j]) >= 0)
                            j++;
                        allNamespaces.Insert(j, ns);
                    }

                    allTypes[fullName] = type;
                    onTypeLoaded?.Invoke(type);
                }
                catch
                {
                    // 跳过无法加载的类型（IL2CPP 下泛型类型访问 Namespace/FullName 可能抛 TypeLoadException）
                }
            }
        }
    }
}
#endif
