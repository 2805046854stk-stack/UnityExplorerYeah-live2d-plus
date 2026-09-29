#if ML073
using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace UnityExplorer.Runtime
{
    /// <summary>
    /// v16j Tier A：Harmony hook Animator.Play/CrossFade 系列（IL2CPP 原生 detour）。
    ///
    /// 游戏代码请求播放时传入的 stateName / clip 名是最准确的动画名来源，
    /// 比 GetCurrentAnimatorClipInfo 轮询更早（请求瞬间即捕获，不用等状态机转移 + 轮询间隔），
    /// 也不受 transition 期间两个 clip 同时"当前"的歧义影响。
    ///
    /// 原理：IL2CPP 游戏里 UnityEngine.Animator.Play 是 il2cpp 编译出的托管方法（内部 icall
    /// 到引擎 C++），游戏代码调用它时走同一个方法指针 —— MelonLoader 的
    /// Il2CppInterop.HarmonySupport 在该指针上打 native detour，我们在请求进入引擎前截获参数。
    ///
    /// 仅 MelonLoader 0.7.x Il2CppInterop 环境启用（ML073 符号 + 0Harmony 引用）。
    /// </summary>
    public static class AnimatorPlayHook
    {
        private static bool _installAttempted;
        private static bool _installed;
        private static int _patchedCount;
        private static HarmonyLib.Harmony _harmony;

        private struct PlayRequest
        {
            public string Name;    // 游戏传入的名字（state 名或 "#hash"）
            public float Time;     // Environment.TickCount/1000 秒
            public string Method;  // "Animator.Play" / "Animator.CrossFade" / ...
            public long Ptr;       // Animator 的 il2cpp 指针
        }

        // 目标 Animator(il2cpp 指针) → 最近一次播放请求
        private static readonly Dictionary<long, PlayRequest> _requests = new Dictionary<long, PlayRequest>();
        // 全局最近一次请求（诊断：游戏可能操作的是另一个 Animator）
        private static PlayRequest _lastAny = new PlayRequest();
        private static bool _otherAnimatorLogged;

        private static float NowS() { return Environment.TickCount / 1000f; }

        public static void Install()
        {
            if (_installAttempted) return;
            _installAttempted = true; // 只试一次，失败不重试（防刷屏）
            try
            {
                _harmony = new HarmonyLib.Harmony("com.buddy.unityexplorer.animatorplayhook");

                // Animator.Play(string, int, float) —— il2cpp 下默认参数在调用点展开，
                // 游戏实际调用的是全参版本，hook 全参即可覆盖所有重载入口
                TryPatch(typeof(Animator), "Play",
                    new[] { typeof(string), typeof(int), typeof(float) }, nameof(PlayStrPrefix));
                TryPatch(typeof(Animator), "Play",
                    new[] { typeof(int), typeof(int), typeof(float) }, nameof(PlayHashPrefix));
                TryPatch(typeof(Animator), "PlayInFixedTime",
                    new[] { typeof(string), typeof(int), typeof(float) }, nameof(PlayFixedStrPrefix));
                TryPatch(typeof(Animator), "CrossFade",
                    new[] { typeof(string), typeof(float), typeof(int), typeof(float) }, nameof(CrossFadeStrPrefix));
                TryPatch(typeof(Animator), "CrossFadeInFixedTime",
                    new[] { typeof(string), typeof(float), typeof(int), typeof(float) }, nameof(CrossFadeFixedStrPrefix));
                // legacy Animation（有些 Cubism 老工程用 Animation 组件播 .anim）
                TryPatch(typeof(Animation), "Play",
                    new[] { typeof(string), typeof(PlayMode) }, nameof(LegacyPlayPrefix));
                TryPatch(typeof(Animation), "PlayQueued",
                    new[] { typeof(string), typeof(QueueMode), typeof(PlayMode) }, nameof(LegacyPlayPrefix));

                _installed = _patchedCount > 0;
                ExplorerCore.Log("[AnimatorPlayHook] Harmony hooks 安装完成: " + _patchedCount + " 个入口" +
                    (_patchedCount == 0 ? "（全部失败，Tier A 不可用，靠 Tier C/D 轮询兜底）" : ""));
            }
            catch (Exception ex)
            {
                ExplorerCore.LogWarning("[AnimatorPlayHook] 安装失败: " + ex.Message + "（Tier A 不可用，靠 Tier C/D 轮询兜底）");
            }
        }

        private static void TryPatch(Type type, string methodName, Type[] paramTypes, string prefixName)
        {
            try
            {
                var target = AccessTools.Method(type, methodName, paramTypes);
                if (target == null)
                {
                    ExplorerCore.LogWarning("[AnimatorPlayHook] 未找到 " + type.Name + "." + methodName +
                        "(" + paramTypes.Length + "参) —— 该入口 hook 跳过");
                    return;
                }
                var prefix = new HarmonyMethod(typeof(AnimatorPlayHook).GetMethod(prefixName,
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static));
                if (prefix == null)
                {
                    ExplorerCore.LogWarning("[AnimatorPlayHook] prefix 方法缺失: " + prefixName);
                    return;
                }
                _harmony.Patch(target, prefix: prefix);
                _patchedCount++;
            }
            catch (Exception ex)
            {
                ExplorerCore.LogWarning("[AnimatorPlayHook] patch " + type.Name + "." + methodName + " 失败: " + ex.Message);
            }
        }

        // ---------- 查询接口（Live2DRecorder 调用） ----------

        /// <summary>最近 maxAge 秒内，目标 Animator 收到的播放请求名。无则 null。method out 请求来源。</summary>
        public static string GetRecentName(Component animator, float maxAge, out string method)
        {
            method = null;
            if (animator == null) return null;
            long ptr = GetPtr(animator);
            if (ptr != 0 && _requests.TryGetValue(ptr, out var r))
            {
                if (NowS() - r.Time <= maxAge)
                {
                    method = r.Method;
                    return r.Name;
                }
            }
            return null;
        }

        /// <summary>目标 Animator 的 il2cpp 指针（供 GetOtherAnimatorRecentRequest 排除自身）。</summary>
        public static long GetPointer(Component animator)
        {
            return GetPtr(animator);
        }

        /// <summary>v16k 诊断：最近 maxAge 秒内收到的所有 Animator 播放请求总数（含其他 Animator）。</summary>
        public static int GetTotalRequests(float maxAge)
        {
            int n = 0;
            float now = NowS();
            foreach (var kv in _requests)
            {
                if (now - kv.Value.Time <= maxAge) n++;
            }
            if (now - _lastAny.Time <= maxAge) n++; // _lastAny 可能不在 _requests 里
            return n;
        }

        /// <summary>
        /// 诊断：游戏最近是否向"别的" Animator 发了播放请求（只 log 一次）。
        /// 返回那个请求的名字 —— 出现时说明模型上挂的 Animator 不是真正的播放源，
        /// 应该去 hook 捕获到的那个 ptr 对应的 GameObject 上找。
        /// </summary>
        public static string GetOtherAnimatorRecentRequest(long excludePtr, float maxAge)
        {
            var r = _lastAny;
            if (string.IsNullOrEmpty(r.Name)) return null;
            if (r.Ptr == 0 || r.Ptr == excludePtr) return null;
            if (NowS() - r.Time > maxAge) return null;
            if (!_otherAnimatorLogged)
            {
                _otherAnimatorLogged = true;
                ExplorerCore.LogWarning("[AnimatorPlayHook] 注意：游戏最近向另一个 Animator(ptr=" + r.Ptr +
                    ") 发送了 " + r.Method + "('" + r.Name + "') —— 模型上的 Animator 可能不是动画播放源");
            }
            return r.Name;
        }

        // ---------- 记录 ----------

        private static long GetPtr(object instance)
        {
            try { return ((Il2CppSystem.Object)instance).Pointer.ToInt64(); }
            catch { return 0; }
        }

        private static void Record(object instance, string name, string method)
        {
            if (string.IsNullOrEmpty(name)) return;
            try
            {
                long ptr = GetPtr(instance);
                var r = new PlayRequest { Name = name, Time = NowS(), Method = method, Ptr = ptr };
                if (ptr != 0) _requests[ptr] = r;
                _lastAny = r;
            }
            catch { }
        }

        // ---------- Harmony Prefix（注入点） ----------
        // 用 object __0 规避 il2cpp 元数据参数名差异；object __instance 规避代理类型强绑定。
        // Harmony 失败会被 TryPatch 捕获，只影响单入口不影响其他。

        private static void PlayStrPrefix(object __instance, object __0)
        { Record(__instance, __0 as string, "Animator.Play"); }

        private static void PlayHashPrefix(object __instance, object __0)
        { Record(__instance, __0 == null ? null : "#" + __0, "Animator.Play#hash"); }

        private static void PlayFixedStrPrefix(object __instance, object __0)
        { Record(__instance, __0 as string, "Animator.PlayInFixedTime"); }

        private static void CrossFadeStrPrefix(object __instance, object __0)
        { Record(__instance, __0 as string, "Animator.CrossFade"); }

        private static void CrossFadeFixedStrPrefix(object __instance, object __0)
        { Record(__instance, __0 as string, "Animator.CrossFadeInFixedTime"); }

        private static void LegacyPlayPrefix(object __instance, object __0)
        { Record(__instance, __0 as string, "Animation.Play"); }
    }
}
#endif
