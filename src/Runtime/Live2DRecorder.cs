using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace UnityExplorer.Runtime
{
    /// <summary>
    /// Live2D 运行时录制器（v12 新增，v14 重写抓取层）。
    ///
    /// 需求：
    ///   用户在 UnityExplorer 选中 Live2D 模型 GameObject 后，实时录制：
    ///     ① 播放的 motion 名称
    ///     ② 动画播放期间触发的 AudioSource.clip 名（"动画：out → Out_mouth_SE"）
    ///       v16ae：归属方向反转为「播放动画 → 记录动画期间播放的音频」：
    ///       音频开始时必须有一段动画在播（段时间窗内）才归属到该动画；无动画在播的音频单列 (无动画)
    ///     ③ 录制期间所有 Cubism 参数 / Part / Drawable 的实际值（时序），用于反推 motion
    ///   然后导出为 JSON/CSV/JSONL/motion3.json/动画音频映射txt。
    ///
    /// v14 抓取层重写（修复 v13 在 IL2CPP(Interop/Unhollower) 下全部为 0 的问题）：
    ///   根因1: GetComponentsInChildren&lt;Component&gt;() 返回的组件包装为静态类型 Component，
    ///          对 CubismParameter.Value 等代理类型成员做裸 System 反射会抛
    ///          "object does not match target type"（被 try/catch 静默吞掉）。
    ///   根因2: model.parameters 返回 Il2CppReferenceArray，不实现非泛型 System.Collections.IList，
    ///          v13 的 `is IList` 判断失败 → 参数列表为空。
    ///   根因3: 音频只扫模型子树，但很多游戏（含目标游戏）的 AudioSource 挂在模型外。
    ///   根因4: 目标游戏无 CubismMotionController（用 Animator+AnimationClip 播 motion），
    ///          v13 的 motion 名检测永远为空。
    ///
    /// v14 方案（复用 AssetExporter 已在目标游戏验证可行的三级读取链）：
    ///   - 启动录制时一次性遍历模型子树，按类型名（GetIl2CppType）分类缓存
    ///     CubismParameter / CubismPart / CubismDrawable 组件；
    ///   - 每个组件建 CubismMemberReader：Tier B 代理类 IntPtr 重包装 + System 反射
    ///     （主力，与 AssetExporter.GetViaProxy 相同），失败降级 Il2Cpp 反射 get_ 方法 /
    ///     字段（INTEROP），再降级裸 System 反射（Mono）；
    ///   - 音频事件改为全场景 FindObjectsOfType&lt;AudioSource&gt;() 轮询（0.5s 刷新），
    ///     检测 开始播放 / 换 clip / time 回卷(重播) 三种事件；
    ///   - motion 名检测：CubismMotionController 反射 → Animator.GetCurrentAnimatorClipInfo(0)
    ///     反射轮询（代理类重包装后 Invoke）→ 沿用上一段名；
    ///   - segment 切分只在检测到真实 motion 名变化时进行（v13 的 param_hash 兜底名
    ///     会导致逐帧切段的 bug 已移除）。
    /// </summary>
    public static class Live2DRecorder
    {
        // ============= v16 公开状态（替代 v14/v15e 的手动录制 API）=============
        // 用户从 v16 起改为"插件加载就开始追踪"，不再提供开始/停止按钮。
        // 下面 4 个属性供调试/状态显示使用。
        public static bool AutoActive { get; private set; } = false;
        public static int DiscoveredModelCount { get; private set; } = 0;
        public static int TriggeredMotionCount { get; private set; } = 0;
        public static int TriggeredAudioEventCount { get; private set; } = 0;

        // v16m：data.cfg 配置开关（默认 神户盐 = 不记录；写 松坂砂糖 才开启）
        //   文件位置：Mods/sinai-dev-UnityExplorer/data.cfg
        //   内容示例：Happy Sugar Life=松坂砂糖
        //   读取时机：AutoStartAllScenes 入口（init 时调一次）；每次启动游戏生效一次
        public static bool RecordingEnabledByConfig { get; private set; } = false;
        public static string RecordingConfigSummary { get; private set; } = "未读取";

        // ============= 数据缓冲（与 v15e 兼容，导出 motion_audio_map 时仍用）=============

        /// <summary>每帧快照（参数时序仍记录，便于 motion3 反推，但 v16 默认导出只取切段映射）</summary>
        public class FrameSnapshot
        {
            public float Time;
            public string MotionName;               // 该帧观察到的 motion 名（可能为 null → 沿用上段）

            public float[] ParamValues;             // 与 _params 顺序一致
            public float[] VisualOpacities;         // 与 _visuals 顺序一致（Part + Drawable）
            public bool[] VisualVisibles;
        }

        /// <summary>检测到的 motion 片段（连续相同 motion 名的帧合并）</summary>
        public class MotionSegment
        {
            public string MotionName;
            public string RootName;                         // v16am：该段属于哪个 Live2D 模型 root（"" = 主绑定轨/未知）
            public float StartTime;
            public float EndTime;
            public int StartFrame;
            public int EndFrame;
            public List<string> TriggeredAudios = new();   // 期间触发的 audio clip 名
            public int ParameterChangeCount;                // 该段中偏离默认值的参数个数（峰值）
            public int DrawableChangeCount;                 // 该段中变化的 Part/Drawable 个数（峰值）

            public string Label                           // v16am：输出用 "模型/动画"（无模型名时退化为纯动画名）
            {
                get { return string.IsNullOrEmpty(RootName) ? MotionName : (RootName + "/" + MotionName); }
            }
        }

        /// <summary>AudioSource 播放事件</summary>
        public class AudioEvent
        {
            public float Time;
            public string ClipName;
            public string SourceGoName;
            public float ClipLength;
            public string TriggeredMotion;   // v16ae：音频开始时正在播放的动画名（按段窗口归属，无动画在播则为空）
            public int SegmentIndex = -1;    // v16ae：归属的动画段实例索引（-1 = 触发时无动画在播）
            public List<string> ModelHits = new();   // v16am：触发瞬间所有激活模型的 "模型/动画"（多模型并行轨）
        }

        /// <summary>变化点日志项（用于调试/核对）</summary>
        public class ChangeLogEntry
        {
            public float Time;
            public string MotionName;
            public string Kind;      // "Parameter" / "Part" / "Drawable"
            public string Id;
            public string OldValue;
            public string NewValue;
        }

        private static readonly List<FrameSnapshot> frames = new();
        private static readonly List<MotionSegment> segments = new();
        private static readonly List<AudioEvent> audioEvents = new();
        private static readonly List<ChangeLogEntry> changeLog = new();

        // 上次 AudioSource 状态（TrackedAudio 内缓存；列表定期刷新时按键保留旧状态）
        private static readonly List<TrackedAudio> trackedAudios = new();
        private static float lastAudioScanTime = -10f;
        private static float lastAudioCheckTime = -10f;
        private static float lastDiscoveryScanTime = -10f;   // v16f: 没找到 model 时每 1s 重扫
        private static bool firstScanFailedLogged = false;
        private const float AUDIO_SCAN_INTERVAL = 0.5f;    // 全场景列表刷新间隔
        private const float AUDIO_CHECK_INTERVAL = 0.1f;   // 播放状态检查间隔
        private static bool audioScanCountLogged = false;
        private static bool audioScanFailedLogged = false;
        private const float DISCOVERY_SCAN_INTERVAL = 1.0f; // CubismModel 重扫间隔

        // 阈值：只有变化超过这个值才记为关键帧（减少 motion3.json 体积）
        private const float KEYFRAME_THRESHOLD = 0.001f;

        // ============= 模型组件跟踪（启动时扫描一次）=============

        /// <summary>跟踪的 CubismParameter</summary>
        private sealed class TrackedParam
        {
            public string Id;
            public CubismMemberReader Value;
            public CubismMemberReader Default;   // DefaultValue（可选）
            public float DefaultValue;
            public float LastValue;
        }

        /// <summary>跟踪的 CubismPart / CubismDrawable（统一顺序）</summary>
        private sealed class TrackedVisual
        {
            public string Id;
            public bool IsPart;
            public CubismMemberReader Opacity;
            public CubismMemberReader Visible;   // IsVisible（Drawable 专用，可选）
            public float LastOpacity;
            public bool LastVisible;
        }

        /// <summary>
        /// 跟踪的全场景 AudioSource（v14：音频常挂在模型子树之外，改为全场景扫描）。
        /// isPlaying/clip/time 通过 CubismMemberReader 缓存反射读取，
        /// 避免直接访问 AudioSource 成员（Unhollower 门面缺少部分成员的编译期定义）。
        /// </summary>
        private sealed class TrackedAudio
        {
            public Component Comp;
            public long Key;                        // CPP: IntPtr；Mono: instanceID
            public string GoName;
            public CubismMemberReader IsPlaying;
            public CubismMemberReader Clip;
            public CubismMemberReader Time;
            public bool WasPlaying;
            public float WasTime;
            public long WasClipKey;
        }

        private static readonly List<TrackedParam> trackedParams = new();
        private static readonly List<TrackedVisual> trackedVisuals = new();
        private static Component cubismModelComp;
        private static Component motionControllerComp;
        private static Component animatorComp;

        // Animator 当前 clip 轮询
        private static object animatorInvokeTarget;          // 代理实例（CPP）或组件（Mono）
        private static MethodInfo animatorGetCurrentClipInfo;
        private static float lastMotionPollTime = -1f;
        private static string cachedMotionName;
        private static bool animatorPollFailedLogged = false;
        private const float MOTION_POLL_INTERVAL = 0.2f;

        // v16j 多级动画名抓取链（Tier C/D 反射通道 + 诊断）
        private static MethodInfo animatorGetNextClipInfo;      // GetNextAnimatorClipInfo(int)
        private static MethodInfo animatorIsInTransition;       // IsInTransition(int)
        private static MethodInfo animatorGetCurrentStateInfo;  // GetCurrentAnimatorStateInfo(int)
        private static MethodInfo animatorGetNextStateInfo;     // GetNextAnimatorStateInfo(int)
        private static MethodInfo animatorStringToHash;         // static Animator.StringToHash(string)
        private static bool animatorHookInstallAttempted = false;
        private static bool motionDiagLogged = false;           // v16j 首次解析诊断日志
        private static string lastMotionVia = null;             // 上次解析用的 Tier（切段时对比）
        private static bool animatorAccessDiagLogged = false;   // v16k 反射通道绑定诊断
        private static bool motionNullDiagLogged = false;       // v16k resolver 全 null 诊断
        private static int pollCallCount = 0;                   // v16l 入口计数器，确认 PollAnimatorMotionName 真的在跑
        private static bool configLoaded = false;               // v16m data.cfg 已读取

        // ---- v16aj：绑定健康 + 动画失联兜底 ----
        //   背景：本游戏同场景并存 7 个 Live2D 模型（标题 立ち絵 + 6 个 H 场景模型）。
        //   DiscoverModelComponents() 会清空并重新绑定 animatorComp，而 StartFromRoot 对每个
        //   root 都调一次 → 最终只绑到「最后一个 root」的 Animator。一旦该 root 被销毁
        //   （标题界面切到 H 场景），animatorComp 变 Unity-null，resolver 全 null，
        //   PollAnimatorMotionName 只能返回 cachedMotionName（陈旧值），
        //   UpdateMotionOnlyViaAnimator 走 else 分支不断延长同一个段
        //   → 段永不切换 → 所有音频都被归到那一个动画上。
        private static GameObject boundRoot;                    // animatorComp / trackedParams 当前绑定到哪个 root
        private static float motionLostSince = -1f;             // 解析不到动画的起始时刻（-1 = 正常）
        private const float MOTION_LOST_GRACE = 1.0f;           // 无可靠动画超过该时长即换绑/关闭当前段（秒）
        // v16al：周期性补扫新 instantiate 出来的 Cubism root（旧逻辑只在「全部 root 销毁」时才重扫）
        private const float NEWROOT_SCAN_INTERVAL = 2.0f;
        private static float lastNewRootScanTime = -10f;
        private static bool newRootScanFailedLogged = false;
        private static int lastScannedRootTotal = -1;           // 上次补扫时场景内 Cubism root 总数（诊断）
        private static bool pendingSegmentBreak = false;        // v16al：动画源已换，下帧切开旧动画段
        private static int animatorRebindCount = 0;             // 重绑次数（诊断）
        private static bool motionLostDiagLogged = false;       // 首次失联关闭段的诊断日志
        private static bool lastMotionReliable = false;         // v16aj：上次解析是否「真在播」（tier G 占位名 = false）

        // ================= v16am：多模型并行动画轨 =================
        // 背景：本游戏 H 场景里同时有 4 个「激活且在播」的 Cubism 模型
        //   （胸 / raiden_shougun / 局部・臀部 / 断面図），各自有自己的 AnimatorController。
        //   旧的单绑定轨（cachedMotionName + segments）只跟其中一个 → 一个场景只产出一条动画轨，
        //   用户看到的就是「一个场景就只会记录一个动画」。
        // 做法：每个「激活 root」各维护一条独立轨，段里带上 RootName；
        //   modelSegments 汇总所有模型的段（导出 <baseName>.model_motions.csv）。
        //   ★ 完全不动旧的 segments / cachedMotionName 逻辑 → 主绑定轨与既有产物格式向后兼容。
        private sealed class RootMotionTrack
        {
            public GameObject root;
            public string rootName;
            public string motion;            // 当前动画名（null = 未在播）
            public MotionSegment seg;        // 当前段
            public float lastReliableTime;   // 最后一次「真在播」的时刻
            public string lastVia;           // 名字来源（诊断）
            public bool logOnce;             // 每个 (root,motion) 只打一次日志
        }
        private static readonly Dictionary<int, RootMotionTrack> rootTracks = new();
        private static readonly List<MotionSegment> modelSegments = new();   // 所有模型的动画段（按加入顺序）
        private static int modelSegmentLogged = 0;                           // v16am 日志条数上限保护
        private static bool placeholderDiagLogged = false;      // v16aj：占位名诊断只打一次（换绑后重新武装）
        private static int rootStateLogTick = 0;                // v16aj：root 状态表日志节流（每 50 次轮询 ≈ 10s）

        // ---- v16as：v16j 诊断日志去重 ----
        //   背景：不可靠结果（tier G 占位名）下 cachedMotionName 恒为 null，
        //   而旧的打点条件里有 `result != cachedMotionName` → 永远成立
        //   → 每个轮询周期（0.2s）打一行同样的 'Standing_Idol'。
        //   本游戏「标题界面 → H 场景」切换的 4 秒里刷了 14 行完全相同的日志（11:17:37~11:17:41）。
        //   改为：同 (result, via) 组合只打一次；可靠↔不可靠翻转必打；另加 30s 心跳兜底。
        private static string lastMotionDiagResult = null;
        private static string lastMotionDiagVia = null;
        private static bool lastMotionDiagReliable = false;
        private static float lastMotionDiagTime = -999f;
        private const float MOTION_DIAG_HEARTBEAT = 30f;
        private static readonly Dictionary<int, Component> rootAnimatorCache = new();  // root instanceID → 子树 Animator

        // ============= 成员读取器（三级策略，跨 Mono/Unhollower/Interop）=============

        /// <summary>
        /// Cubism 组件成员读取器：录制启动时解析一次，之后每帧只做缓存的反射调用。
        /// 解析顺序：
        ///   Tier B（CPP 主力）：il2cpp 实际类型 FullName → 托管代理类（AppDomain 扫描）→
        ///                       Activator.CreateInstance(proxyType, IntPtr) 重包装 → System 反射读属性/字段。
        ///                       解决 GetComponents 返回 Component 基类包装导致的
        ///                       "object does not match target type"。
        ///   Tier A（INTEROP）：Il2CppSystem.Reflection 直接 get_ 方法 / 字段。
        ///   Tier C（Mono / 兜底）：裸 System 反射（真实类型时有效）。
        /// </summary>
        private sealed class CubismMemberReader
        {
            private byte mode;                 // 0=未解析 1=代理属性 2=代理字段 3=托管属性 4=托管字段 5=Il2Getter 6=Il2Field
            private object inst;               // 代理实例(1/2) 或 原始组件(3/4)
            private PropertyInfo prop;
            private FieldInfo field;
#if CPP
            private Il2CppSystem.Reflection.MethodInfo il2Getter;
            private Il2CppSystem.Reflection.FieldInfo il2Field;
            private Il2CppSystem.Object il2Target;
#endif
            public bool Resolved { get { return mode != 0; } }

            public static CubismMemberReader Create(Component comp, params string[] names)
            {
                CubismMemberReader r = new CubismMemberReader();
                try { r.Resolve(comp, names); }
                catch { }
                return r;
            }

            private void Resolve(Component comp, string[] names)
            {
                if (comp == null || names == null) return;
#if CPP
                try
                {
                    Il2CppSystem.Object iobj = comp.TryCast<Il2CppSystem.Object>();
                    if (iobj != null)
                    {
                        Il2CppSystem.Type itype = null;
                        string fullName = null;
                        try { itype = iobj.GetIl2CppType(); } catch { }
                        if (itype != null)
                        {
                            try { fullName = itype.FullName ?? itype.Name; } catch { }
                        }

                        // ---- Tier B: 代理类重包装 + System 反射（主力）----
                        System.Type proxyType = null;
                        if (!string.IsNullOrEmpty(fullName))
                        {
                            try { proxyType = AssetExporter.FindProxyType(fullName); } catch { }
                        }
                        if (proxyType != null)
                        {
                            try
                            {
                                object wrapped = System.Activator.CreateInstance(proxyType, new object[] { iobj.Pointer });
                                if (wrapped != null)
                                {
                                    foreach (string name in names)
                                    {
                                        if (string.IsNullOrEmpty(name)) continue;
                                        PropertyInfo p = GetPropertyWithBase(proxyType, name);
                                        if (p != null && p.CanRead && p.GetIndexParameters().Length == 0)
                                        {
                                            mode = 1; inst = wrapped; prop = p; return;
                                        }
                                        FieldInfo f = GetFieldWithBase(proxyType, name);
                                        if (f != null)
                                        {
                                            mode = 2; inst = wrapped; field = f; return;
                                        }
                                    }
                                }
                            }
                            catch { }
                        }

                        // ---- Tier A: Il2Cpp 反射 get_ 方法 / 字段（INTEROP）----
#if INTEROP
                        if (itype != null)
                        {
                            var flags = Il2CppSystem.Reflection.BindingFlags.Instance
                                | Il2CppSystem.Reflection.BindingFlags.Public
                                | Il2CppSystem.Reflection.BindingFlags.NonPublic;
                            foreach (string name in names)
                            {
                                if (string.IsNullOrEmpty(name)) continue;
                                try
                                {
                                    var ms = itype.GetMethods(flags);
                                    if (ms != null)
                                    {
                                        foreach (var m in ms)
                                        {
                                            if (m.Name != "get_" + name) continue;
                                            var ps = m.GetParameters();
                                            if (ps != null && ps.Length != 0) continue;
                                            mode = 5; il2Getter = m; il2Target = iobj; return;
                                        }
                                    }
                                }
                                catch { }
                                try
                                {
                                    var f = itype.GetField(name, flags);
                                    if (f != null)
                                    {
                                        mode = 6; il2Field = f; il2Target = iobj; return;
                                    }
                                }
                                catch { }
                            }
                        }
#endif
                    }
                }
                catch { }
#endif
                // ---- Tier C: 裸 System 反射（Mono 真实类型 / 代理已正确包装时）----
                System.Type t = comp.GetType();
                if (t != null)
                {
                    foreach (string name in names)
                    {
                        if (string.IsNullOrEmpty(name)) continue;
                        PropertyInfo p = GetPropertyWithBase(t, name);
                        if (p != null && p.CanRead && p.GetIndexParameters().Length == 0)
                        {
                            mode = 3; inst = comp; prop = p; return;
                        }
                        FieldInfo f = GetFieldWithBase(t, name);
                        if (f != null)
                        {
                            mode = 4; inst = comp; field = f; return;
                        }
                    }
                }
            }

            public object Read()
            {
                try
                {
                    if (mode == 1 || mode == 3) return prop.GetValue(inst, null);
                    if (mode == 2 || mode == 4) return field.GetValue(inst);
#if CPP
                    if (mode == 5) return il2Getter.Invoke(il2Target, new Il2CppSystem.Object[0]);
                    if (mode == 6) return il2Field.GetValue(il2Target);
#endif
                }
                catch { }
                return null;
            }

            public float ReadFloat(float fallback)
            {
                object v = Read();
                if (v == null) return fallback;
                try
                {
                    if (v is float) return (float)v;
                    if (v is double) return (float)(double)v;
                    if (v is int) return (float)(int)v;
                    if (v is long) return (float)(long)v;
                    // v15e：若 v 在 .NET 端被声明为 Il2CppSystem.Object 但底层实际是
                    // Il2CppSystem.Single/Double/Int32 等，挖一层 GetIl2CppType 拿真实类型。
                    Type vt = ResolveActualIl2CppType(v) ?? v.GetType();
                    if (vt != null)
                    {
                        string tn = vt.FullName ?? "";
                        if (tn.StartsWith("Il2CppSystem.") || tn.StartsWith("System."))
                        {
                            // (1) op_Implicit 到 .NET 基元
                            try
                            {
                                MethodInfo mi = vt.GetMethod("op_Implicit",
                                    BindingFlags.Public | BindingFlags.Static,
                                    null, new Type[] { vt }, null);
                                if (mi != null)
                                {
                                    object conv = mi.Invoke(null, new object[] { v });
                                    if (conv is float) return (float)conv;
                                    if (conv is double) return (float)(double)conv;
                                    if (conv is int) return (float)(int)conv;
                                    if (conv is long) return (float)(long)conv;
                                    if (conv != null)
                                    {
                                        float f2;
                                        if (float.TryParse(conv.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out f2))
                                            return f2;
                                    }
                                }
                            }
                            catch { }
                            // (2) 直接读 m_value 字段（IL2CPP 装箱数值类型的内部字段）
                            try
                            {
                                FieldInfo fmv = vt.GetField("m_value",
                                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                if (fmv != null)
                                {
                                    object mv = fmv.GetValue(v);
                                    if (mv is float) return (float)mv;
                                    if (mv is double) return (float)(double)mv;
                                    if (mv is int) return (float)(int)mv;
                                    if (mv is long) return (float)(long)mv;
                                    if (mv != null)
                                    {
                                        float f3;
                                        if (float.TryParse(mv.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out f3))
                                            return f3;
                                    }
                                }
                            }
                            catch { }
                            // (3) v15e：若实际底层是 Int/Double 但 il2cpp 包装成 Object → 试显式
                            //     unbox 到所有已知基元
                            if (tn.StartsWith("Il2CppSystem."))
                            {
                                try
                                {
                                    foreach (var primName in new[] { "Single", "Double", "Int32", "Int64", "UInt32", "UInt64", "Boolean", "SByte", "Byte" })
                                    {
                                        try
                                        {
                                            Type primType = Type.GetType("Il2CppSystem." + primName + ", Il2CppSystem");
                                            if (primType == null) continue;
                                            // 通过 op_Implicit(primType) → 拿 .NET 基元
                                            var miPrim = primType.GetMethod("op_Implicit",
                                                BindingFlags.Public | BindingFlags.Static,
                                                null, new Type[] { primType }, null);
                                            if (miPrim != null && miPrim.ReturnType == typeof(float))
                                            {
                                                // cast v to primType first
                                                object primInst = Convert.ChangeType(v, primType);
                                                object conv = miPrim.Invoke(null, new object[] { primInst });
                                                if (conv is float) return (float)conv;
                                            }
                                        }
                                        catch { }
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                    float f;
                    if (float.TryParse(v.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out f))
                        return f;
                }
                catch { }
                return fallback;
            }

            public bool ReadBool(bool fallback)
            {
                object v = Read();
                if (v == null) return fallback;
                try
                {
                    if (v is bool) return (bool)v;
                    if (v is int) return (int)v != 0;
                    bool b;
                    if (bool.TryParse(v.ToString(), out b)) return b;
                }
                catch { }
                return fallback;
            }

            public string ReadString()
            {
                object v = Read();
                return UnwrapIl2CppString(v);
            }
        }

        // ============= Il2CppSystem 解包工具 =============

        /// <summary>
        /// 把任意反射返回值包成 System.String。优先处理 Il2CppSystem.String
        /// （Il2CppInterop 生成的代理类有时把 `Id` 属性声明为 Il2CppSystem.String，
        /// 而不是 string；直接 .ToString() 会得到类型名"Il2CppSystem.Object"，导致
        /// 上百个 component 的 Id 全部 collapse 成同一个伪值，motion3 严重失真）。
        /// </summary>
        internal static string UnwrapIl2CppString(object v)
        {
            if (v == null) return null;
            string s = v as string;
            if (s != null) return s;
            try
            {
                Type vt = v.GetType();
                if (vt != null)
                {
                    string tn = vt.FullName ?? "";
                    bool looksLikeIl2Str =
                        tn == "Il2CppSystem.String" ||
                        (tn.StartsWith("Il2CppSystem.") &&
                         vt.GetMethod("op_Implicit",
                             BindingFlags.Public | BindingFlags.Static,
                             null, new Type[] { vt }, null) != null);
                    if (looksLikeIl2Str)
                    {
                        // (1) op_Implicit(string) → System.String
                        try
                        {
                            MethodInfo mi = vt.GetMethod("op_Implicit",
                                BindingFlags.Public | BindingFlags.Static,
                                null, new Type[] { vt }, null);
                            if (mi != null && mi.ReturnType == typeof(string))
                            {
                                object conv = mi.Invoke(null, new object[] { v });
                                if (conv is string) return (string)conv;
                            }
                        }
                        catch { }
                        // (2) 兜底：m_string 字段（如有）
                        try
                        {
                            FieldInfo fmv = vt.GetField("m_string",
                                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            if (fmv != null)
                            {
                                object mv = fmv.GetValue(v);
                                s = mv as string;
                                if (s != null) return s;
                                if (mv is string) return (string)mv;
                            }
                        }
                        catch { }
                        // (3) 字符数组兜底：依次读 Chars[i]
                        try
                        {
                            PropertyInfo pchars = vt.GetProperty("Chars");
                            if (pchars != null && pchars.PropertyType == typeof(char))
                            {
                                PropertyInfo plen = vt.GetProperty("Length");
                                int len = (plen != null) ? (int)plen.GetValue(v, null) : 0;
                                if (len > 0 && len < 4096)
                                {
                                    StringBuilder sb = new StringBuilder(len);
                                    for (int ci = 0; ci < len; ci++)
                                    {
                                        object ch = pchars.GetValue(v, new object[] { ci });
                                        if (ch is char) sb.Append((char)ch);
                                        else if (ch != null)
                                        {
                                            int ic;
                                            if (int.TryParse(ch.ToString(), out ic)) sb.Append((char)ic);
                                        }
                                    }
                                    if (sb.Length > 0) return sb.ToString();
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
            // v15e 修复：拒绝明显是类名的 .ToString() 输出（Il2CppSystem.Object
            // 包装器的 Object.ToString 默认实现返回类名），强制返回 null，让调用方
            // 的 fallback（Id → gameObject.name；Value → LastValue/DefaultValue）接管。
            try
            {
                string ts = v.ToString();
                if (ts == "Il2CppSystem.Object" || ts == "System.Object" || ts == "Object" ||
                    ts.EndsWith(".Object") || (ts != null && ts.Length > 256))
                    return null;
                return ts;
            }
            catch { return null; }
        }

        // ============= v15e: IL2CPP 实际类型解包 =============

        /// <summary>
        /// 若 v 是 Il2CppSystem.Object 包装但实际底层是 Il2CppSystem.Single/String 等
        /// 具体类型，尝试通过 INTEROP GetIl2CppType() 拿到真实的 System.Type。
        /// 用途：Il2CppInterop 代理类有时把 Id/Value 等属性声明为 Il2CppSystem.Object
        /// 返回类型（包裹实参 IL2CPP 对象），prop.GetValue 在 .NET 端只看到 Object，
        /// 不挖一层就拿不到 op_Implicit/m_value 等成员。
        /// </summary>
        internal static Type ResolveActualIl2CppType(object v)
        {
#if INTEROP
            if (v == null) return null;
            try
            {
                Type vt = v.GetType();
                if (vt == null) return null;
                string tn = vt.FullName ?? "";
                // 快路径：已经是具体类型（不是 Object 基类包装）
                if (tn != "Il2CppSystem.Object" && !string.IsNullOrEmpty(tn)) return vt;

                Il2CppSystem.Object iobj = v as Il2CppSystem.Object;
                if (iobj == null) return vt;
                IntPtr ptr = iobj.Pointer;
                if (ptr == IntPtr.Zero) return vt;

                // IL2CPP native ptr → 缓存
                if (s_il2PtrToType.TryGetValue(ptr, out Type cached)) return cached;

                Il2CppSystem.Type il2type = iobj.GetIl2CppType();
                if (il2type == null) return vt;
                string typeName = il2type.FullName ?? il2type.Name;
                if (string.IsNullOrEmpty(typeName)) return vt;

                // 现实 IL2CPP 类型 → 找匹配的 .NET System.Type
                Type sysType = null;
                try { sysType = AssetExporter.FindProxyType(typeName); } catch { }
                if (sysType == null && typeName.StartsWith("Il2Cpp"))
                {
                    try { sysType = AssetExporter.FindProxyType(typeName.Substring(6)); } catch { }
                }
                if (sysType == null) sysType = vt;
                s_il2PtrToType[ptr] = sysType;
                return sysType;
            }
            catch { return v?.GetType(); }
#else
            // Mono 构建走 fallback：直接返回 v 的实际类型（无 Il2CppSystem 概念）
            return v?.GetType();
#endif
        }

#if INTEROP
        private static readonly System.Collections.Generic.Dictionary<IntPtr, System.Type> s_il2PtrToType =
            new System.Collections.Generic.Dictionary<IntPtr, System.Type>();
#endif

        private static PropertyInfo GetPropertyWithBase(System.Type t, string name)
        {
            System.Type cur = t;
            int guard = 0;
            while (cur != null && guard++ < 10)
            {
                try
                {
                    PropertyInfo p = cur.GetProperty(name,
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                    if (p != null) return p;
                }
                catch { }
                cur = cur.BaseType;
            }
            return null;
        }

        private static FieldInfo GetFieldWithBase(System.Type t, string name)
        {
            System.Type cur = t;
            int guard = 0;
            while (cur != null && guard++ < 10)
            {
                try
                {
                    FieldInfo f = cur.GetField(name,
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                    if (f != null) return f;
                }
                catch { }
                cur = cur.BaseType;
            }
            return null;
        }

        // ============= v16 公有 API =============

        /// <summary>
        /// v16t：查找 data.cfg 真实路径。
        /// 核心思路：只用 **UE 自己暴露的 ExplorerCore.ExplorerFolder**（所有 loader 下都是
        /// sinai-dev-UnityExplorer 子目录，ML=Mods/.../，BIE=BepInEx/plugins/.../）作为搜索根，
        /// 不再用 Assembly.Location（ML 下 asmDir 是 Mods 根，子目录多/权限敏感，
        /// Directory.GetFiles(AllDirectories) 易炸，炸了就掉兜底分支在 Mods/ 创建错的 data.cfg，
        /// 第二次启动又被字母序优先命中，从此永远用错文件 —— v16p/v16q 的实际 bug）。
        /// 搜索顺序：
        ///   1. ExplorerFolder/data.cfg（最直接的命中点）
        ///   2. ExplorerFolder 下的全子树（用户在子目录里放也能找到）
        ///   3. &lt;BaseDirectory&gt;/Mods/sinai-dev-UnityExplorer 和 &lt;BaseDirectory&gt;/BepInEx/plugins/sinai-dev-UnityExplorer
        ///      （ExplorerFolder 尚未初始化或返回空时的兜底）
        ///   4. 都没有 → 返回 null（由 LoadRecordingConfig 决定是否创建；创建时用 ExplorerFolder 作根）
        /// </summary>
        private static string ResolveDataCfgPath(out string searchNote)
        {
            searchNote = null;
            var createCandidates = new List<string>();   // 找不到时供 LoadRecordingConfig 选一个创建位置

            // 1. 优先 ExplorerFolder（UE 自己暴露的，最可靠）
            try
            {
                string explorerFolder = ExplorerCore.ExplorerFolder;
                if (!string.IsNullOrEmpty(explorerFolder) && Directory.Exists(explorerFolder))
                {
                    string direct = Path.Combine(explorerFolder, "data.cfg");
                    if (File.Exists(direct))
                    {
                        searchNote = "ExplorerFolder/data.cfg: " + direct;
                        return direct;
                    }
                    // 子树扫描（用户在子目录里放 data.cfg 也能找到）
                    try
                    {
                        string[] hits = Directory.GetFiles(explorerFolder, "data.cfg", SearchOption.AllDirectories);
                        if (hits != null && hits.Length > 0)
                        {
                            System.Array.Sort(hits, StringComparer.OrdinalIgnoreCase);
                            searchNote = "ExplorerFolder 子树扫描: " + hits[0];
                            return hits[0];
                        }
                    }
                    catch { /* 单个子目录权限问题不影响，吞掉 */ }
                    createCandidates.Add(explorerFolder);
                }
            }
            catch { /* ExplorerFolder getter 异常时跳过 */ }

            // 2. 兜底：游戏根目录下两个已知的 UnityExplorer 插件目录
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                if (!string.IsNullOrEmpty(baseDir))
                {
                    string[] knownRoots = {
                        Path.Combine(Path.Combine(baseDir, "Mods"), "sinai-dev-UnityExplorer"),
                        Path.Combine(Path.Combine(Path.Combine(baseDir, "BepInEx"), "plugins"), "sinai-dev-UnityExplorer"),
                    };
                    foreach (var root in knownRoots)
                    {
                        if (!Directory.Exists(root)) continue;
                        string direct = Path.Combine(root, "data.cfg");
                        if (File.Exists(direct))
                        {
                            searchNote = "已知位置/data.cfg: " + direct;
                            return direct;
                        }
                        try
                        {
                            string[] hits = Directory.GetFiles(root, "data.cfg", SearchOption.AllDirectories);
                            if (hits != null && hits.Length > 0)
                            {
                                System.Array.Sort(hits, StringComparer.OrdinalIgnoreCase);
                                searchNote = "已知位置子树扫描: " + hits[0];
                                return hits[0];
                            }
                        }
                        catch { }
                        if (!createCandidates.Contains(root)) createCandidates.Add(root);
                    }
                }
            }
            catch { }

            // 3. 都没找到 → 告诉调用方哪些位置可写
            if (createCandidates.Count > 0)
                searchNote = "未找到 data.cfg，可创建到: " + string.Join(" | ", createCandidates.ToArray());
            else
                searchNote = "未找到任何已知的 UnityExplorer 插件目录（ExplorerFolder 未初始化且 BaseDirectory 也不在预期位置）";
            return null;
        }

        /// <summary>
        /// v16m：读取 data.cfg 中 Happy Sugar Life 条目。
        /// =松坂砂糖 → 开启记录；=神户盐 → 不记录；文件缺失或条目缺失 → 默认 神户盐（不记录）。
        /// 每次启动游戏读取一次（不在运行时监听文件变化——避免意外启用触发大量 IO）。
        /// v16o：文件不存在 → 自动创建到第一个找到的搜索路径（向后兼容 Mods\sinai-dev-UnityExplorer）；
        ///       条目不存在 → 追加到该文件末尾。
        ///       搜索路径：v16m 旧路径 > 插件目录 + 全子目录。
        /// v16t：自动创建的目标改为 ExplorerCore.ExplorerFolder（不再用 asmDir，否则在 ML 下会
        ///       在 Mods/ 根创建错误路径的 data.cfg —— v16p/v16q 的实际 bug）。
        /// </summary>
        public static void LoadRecordingConfig()
        {
            // 1. 解析 cfg 路径
            string cfgPath = ResolveDataCfgPath(out string searchNote);
            if (string.IsNullOrEmpty(cfgPath))
            {
                // v16t：完全找不到位置 → 用 ExplorerFolder 作兜底（不再用 asmDir，避免 ML 下
                //   在 Mods/ 根创建错路径的 data.cfg，被 v16p 递归搜索字母序优先命中后永远用错文件）
                try
                {
                    string createRoot = ExplorerCore.ExplorerFolder;
                    if (string.IsNullOrEmpty(createRoot))
                    {
                        // ExplorerFolder 尚未初始化（极端情况）→ 用 BaseDirectory + 已知子目录名
                    string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                    if (!string.IsNullOrEmpty(baseDir))
                    {
                        // ML 优先（Mods/），BIE 兜底（v16t：Path.Combine 嵌套调用，net35 没有 3/4 参重载）
                        string mlRoot = Path.Combine(Path.Combine(baseDir, "Mods"), "sinai-dev-UnityExplorer");
                        string bieRoot = Path.Combine(Path.Combine(Path.Combine(baseDir, "BepInEx"), "plugins"), "sinai-dev-UnityExplorer");
                        createRoot = Directory.Exists(mlRoot) ? mlRoot
                                  : Directory.Exists(bieRoot) ? bieRoot
                                  : mlRoot;  // 都不存在就用 ML 那个，CreateDirectory 会自动建
                    }
                    }
                    if (string.IsNullOrEmpty(createRoot))
                        throw new InvalidOperationException("ExplorerFolder 和 BaseDirectory 都为空，无法确定 data.cfg 创建位置");
                    cfgPath = Path.Combine(createRoot, "data.cfg");
                    searchNote += "；将创建到 ExplorerFolder: " + cfgPath;
                }
                catch (Exception ex)
                {
                    RecordingConfigSummary = "data.cfg 路径解析失败: " + ex.Message + "（默认=神户盐 不记录）";
                    RecordingEnabledByConfig = false;
                    return;
                }
            }

            string value = null;
            bool fileExisted = false;
            bool keyFound = false;
            try
            {
                if (!File.Exists(cfgPath))
                {
                    // v16o：文件不存在 → 创建默认文件（注释 + 默认条目），写到搜索到的第一个位置
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(cfgPath));
                        File.WriteAllText(cfgPath, DefaultCfgContent, new System.Text.UTF8Encoding(false));
                        RecordingConfigSummary = "data.cfg 不存在，已自动创建默认文件: " + cfgPath + "（" + searchNote + "）";
                    }
                    catch (Exception createEx)
                    {
                        RecordingConfigSummary = "data.cfg 不存在且自动创建失败: " + createEx.Message + "（默认=神户盐 不记录）";
                        RecordingEnabledByConfig = false;
                        return;
                    }
                    // 创建完直接当默认条目处理
                    value = "神户盐";
                    keyFound = true;
                }
                else
                {
                    fileExisted = true;
                    // 解析 key=value，UTF-8，去 BOM，去 #// 注释，去空白
                    //   v16r：key/value 都 Trim 引号 —— 兼容用户手写裸格式（Happy Sugar Life=松坂砂糖）
                    //   和我们写入的合法 TOML 格式（"Happy Sugar Life" = "神户盐"）
                    foreach (var raw in File.ReadAllLines(cfgPath, System.Text.Encoding.UTF8))
                    {
                        string line = raw?.Trim();
                        if (string.IsNullOrEmpty(line)) continue;
                        if (line.StartsWith("#") || line.StartsWith("//")) continue;
                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        string key = line.Substring(0, eq).Trim().Trim('"').Trim();
                        string v = line.Substring(eq + 1).Trim().Trim('"').Trim();
                        if (string.Equals(key, "Happy Sugar Life", StringComparison.OrdinalIgnoreCase))
                        {
                            value = v;
                            keyFound = true;
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                RecordingConfigSummary = "data.cfg 读取失败: " + ex.Message + "（默认=神户盐 不记录）";
                RecordingEnabledByConfig = false;
                return;
            }

            if (fileExisted && !keyFound)
            {
                // v16r：文件存在但没有 Happy Sugar Life 条目 → 追加合法 TOML 格式条目
                //   （带引号 —— UE 的 TomlParser.ParseFile 解析 data.cfg 时裸键带空格会抛异常，
                //    导致面板布局重置 + 条目被 SaveConfig 抹掉）
                try
                {
                    File.AppendAllText(cfgPath,
                        Environment.NewLine + "\"Happy Sugar Life\" = \"神户盐\"" + Environment.NewLine,
                        new System.Text.UTF8Encoding(false));
                    value = "神户盐";
                    keyFound = true;
                    RecordingConfigSummary = "data.cfg 无 Happy Sugar Life 条目，已自动追加默认条目: " + cfgPath + "（" + searchNote + "）";
                }
                catch (Exception appendEx)
                {
                    RecordingConfigSummary = "data.cfg 无 Happy Sugar Life 条目且追加失败: " + appendEx.Message + "（默认=神户盐 不记录）";
                    RecordingEnabledByConfig = false;
                    return;
                }
            }

            if (string.IsNullOrEmpty(value))
            {
                RecordingConfigSummary = "data.cfg 无 Happy Sugar Life 条目（默认=神户盐 不记录）";
                RecordingEnabledByConfig = false;
                return;
            }

            if (string.Equals(value, "松坂砂糖", StringComparison.Ordinal))
            {
                RecordingEnabledByConfig = true;
                RecordingConfigSummary = "data.cfg: Happy Sugar Life=松坂砂糖 → 开启记录 (" + cfgPath + ")";
            }
            else if (string.Equals(value, "神户盐", StringComparison.Ordinal))
            {
                RecordingEnabledByConfig = false;
                RecordingConfigSummary = "data.cfg: Happy Sugar Life=神户盐 → 不记录 (" + cfgPath + ")";
            }
            else
            {
                // 未知值 → 沿用默认（不记录），但告诉用户写错了
                RecordingEnabledByConfig = false;
                RecordingConfigSummary = "data.cfg: Happy Sugar Life='" + value + "'（未知值，默认=神户盐 不记录，" + cfgPath + "）";
            }
        }

        // v16r：自动创建 data.cfg 时的默认内容（合法 TOML 格式，UE 的 TomlParser 可正常解析）
        private const string DefaultCfgContent =
            "# UnityExplorer Live2DRecorder 配置" + "\n" +
            "# \"Happy Sugar Life\" 条目控制是否记录 Live2D 动画名+音频映射：" + "\n" +
            "#   = 松坂砂糖  开启记录（导出到 sinai-dev-UnityExplorer/Output/）" + "\n" +
            "#   = 神户盐    不记录（默认，纯游戏体验）" + "\n" +
            "# 改完保存 + 重启游戏生效" + "\n" +
            "\"Happy Sugar Life\" = \"神户盐\"" + "\n";

        /// <summary>
        /// 插件加载时由 loader 钩子（OnInitializeMelon / Awake）调用。
        /// 扫描所有场景中的 Live2D 模型根，自动 Start + 启动 AutoTracker 钩子。
        /// 幂等：已找到模型时直接 return；AudioSource 监控一直启动。
        /// </summary>
        public static void AutoStartAllScenes()
        {
            // v16m：先读 data.cfg 配置开关（默认 神户盐 = 不记录；写 松坂砂糖 才记录）。
            //   配置关闭时不启动任何追踪、不扫模型、不挂 hook（Hook 仍装，见下注释，方便临时打开）
            //   → 玩家能玩到一个完全不监控音频/animator 的纯净游戏体验
            if (!configLoaded)
            {
                configLoaded = true;
                LoadRecordingConfig();
                ExplorerCore.Log("[Live2DRecorder] v16m config: " + RecordingConfigSummary);
            }

            // v16k：plugin init 阶段就尝试装 Tier A Harmony hook（不依赖模型是否已发现）。
            //   之前的版本把 Install 放进 SetupAnimatorAccess，结果 Init 时还没模型 → 没装 hook →
            //   Tier A 永远不工作 → 所有音频事件 motion= 空。v16k 拆出来在 init 路径直接调。
            //   v16m：即使 RecordingEnabledByConfig=false 也装 hook —— 万一用户中途改成松坂砂糖，
            //   按理说要重启游戏才生效（LoadRecordingConfig 只在 init 读一次），但 hook 装上无副作用，留着
#if ML073
            if (!animatorHookInstallAttempted)
            {
                animatorHookInstallAttempted = true;
                AnimatorPlayHook.Install();
            }
#endif

            if (!RecordingEnabledByConfig)
            {
                // v16n：按用户要求，config 关闭时静默（不发 WARNING）—— 默认配置就是不记录，
                //   每次启动打 WARNING 太吵。status 改由 Inspector 按钮显示，console 不刷屏。
                return;
            }

            if (trackedRoots.Count > 0)
            {
                ExplorerCore.Log("[Live2DRecorder] AutoStart 已是激活状态（" + trackedRoots.Count + " 个 root 在追踪），跳过");
                return;
            }

            // v16f: 改成"找到就退出循环"——找到 0 个不阻塞，立即设 AutoActive 让 AudioSource 监控接管
            int started = ScanForCubismModels();

            AutoActive = true;
            RecomputeTrackingState();

            if (started > 0)
            {
                firstScanFailedLogged = false;
                ExplorerCore.Log(string.Format(
                    "[Live2DRecorder] AutoStart 启动 {0} 个 Live2D 模型，全场景 AudioSource 监控开启",
                    started));
            }
            else
            {
                ExplorerCore.LogWarning("[Live2DRecorder] AutoStart 未发现 CubismModel 组件（仍启动全场景 AudioSource 监控，将每 1s 重试扫描）");
            }
        }

        /// <summary>
        /// v16f: 把扫描+启动模型根抽出来，AutoStartAllScenes 和 Tick 都能复用。
        /// 返回新启动成功的 root 数。
        /// </summary>
        private static int ScanForCubismModels()
        {
            int started = 0;
            try
            {
                foreach (GameObject go in FindCubismModelRoots())
                {
                    if (go == null || go.IsNullOrDestroyed(true)) continue;
                    if (StartFromRoot(go))
                    {
                        started++;
                    }
                }
            }
            catch (Exception ex)
            {
                ExplorerCore.LogWarning("[Live2DRecorder] ScanForCubismModels 失败: " + ex.Message);
            }
            return started;
        }

        /// <summary>
        /// 由 ExplorerCore.Update() 每帧调用（always-on）。
        /// 内部先确保至少一个 Cubism root 在线，然后只处理 audio/animator 切段。
        /// </summary>
        public static void Tick()
        {
            if (!AutoActive) return;

            // 模型被销毁 → 重置状态，下帧重新找
            bool allRootsGone = true;
            for (int i = 0; i < trackedRoots.Count; i++)
            {
                GameObject go = trackedRoots[i];
                if (go != null && !go.IsNullOrDestroyed(true))
                {
                    allRootsGone = false;
                    break;
                }
            }
            if (allRootsGone)
            {
                // 重新扫描所有场景（新增的 Live2D 模型也会被纳入）
                try
                {
                    int started = 0;
                    foreach (GameObject go in FindCubismModelRoots())
                    {
                        if (go == null || go.IsNullOrDestroyed(true)) continue;
                        if (StartFromRoot(go))
                            started++;
                    }
                    if (started > 0)
                    {
                        // v16aj：整批模型换了一轮（场景切换）→ 旧动画段就此结束，强制重新开段，
                        //   否则新旧动画同名时会被并成同一段。
                        float tc = autoClock.IsRunning ? (float)autoClock.Elapsed.TotalSeconds : 0f;
                        if (cachedMotionName != null)
                        {
                            CloseLastSegment(tc);
                            cachedMotionName = null;
                        }
                        lastMotionVia = null;
                        motionLostSince = -1f;
                        ExplorerCore.Log("[Live2DRecorder] Tick: 自动重新发现 " + started + " 个 Live2D 模型");
                    }
                }
                catch { }
            }

            // 按当前第一个在线 root 关联 segment（不再每帧捕获所有 param）
            GameObject liveRoot = PickLiveRoot();
            if (liveRoot == null)
            {
                // 没有 CubismModel 时也继续扫 AudioSource + 周期导出 + 节流重扫
                TickAudioOnly();
                float t2 = autoClock.IsRunning ? (float)autoClock.Elapsed.TotalSeconds : 0f;

                // v16f: 还没找到任何 CubismModel 时，每 1s 重扫一次（场景晚加载补救）
                if (t2 - lastDiscoveryScanTime >= DISCOVERY_SCAN_INTERVAL)
                {
                    lastDiscoveryScanTime = t2;
                    try
                    {
                        int found = ScanForCubismModels();
                        if (found > 0)
                        {
                            firstScanFailedLogged = false;
                            ExplorerCore.Log("[Live2DRecorder] Tick: 自动重新发现 " + found + " 个 Live2D 模型");
                            RecomputeTrackingState();   // 让下面的 PickLiveRoot 拿到新 root
                        }
                        else if (!firstScanFailedLogged)
                        {
                            firstScanFailedLogged = true;
                            // 不重复告警，只在 Tick 主分支日志一次 + AutoStart 已经在 LateInit 提示过
                        }
                    }
                    catch (Exception dx) { ExplorerCore.LogWarning("[Live2DRecorder] Tick 重扫失败: " + dx.Message); }
                }

                return;
            }

            try
            {
                if (trackedParams.Count == 0 && trackedVisuals.Count == 0
                    && cubismModelComp == null && animatorComp == null)
                {
                    DiscoverModelComponents(liveRoot);
                    SetupAnimatorAccess();
                    boundRoot = liveRoot;   // v16aj：保持 boundRoot 与实际绑定一致
                }

                float t = autoClock.IsRunning ? (float)autoClock.Elapsed.TotalSeconds : 0f;

                // ---------- v16al：周期性补扫新出现的 Cubism root ----------
                //   本游戏切场景时会隐藏/销毁旧的预载模型并 **Instantiate 新模型**。
                //   旧逻辑只在「全部 root 都销毁（allRootsGone）」时才重扫，而这里是
                //   「7 个都还活着但全部隐藏」→ 永远不重扫 → 真正在演的新模型进不了 trackedRoots
                //   → 绑定/解析一直停在那个空闲模型上（root 状态表全是「隐藏,未播」即此症状）。
                if (t - lastNewRootScanTime >= NEWROOT_SCAN_INTERVAL)
                {
                    lastNewRootScanTime = t;
                    try
                    {
                        // v16as：先清掉已销毁的旧 root（否则计数会自相矛盾：场景内 10 / 已追踪 11）
                        int pruned = PruneDestroyedRoots();

                        int total = 0, added = 0;
                        foreach (GameObject go in FindCubismModelRoots())
                        {
                            if (go == null || go.IsNullOrDestroyed(true)) continue;
                            total++;
                            if (StartFromRoot(go)) added++;
                        }
                        if (added > 0 || pruned > 0)
                        {
                            ExplorerCore.Log("[Live2DRecorder] v16al 补扫: 场景内 " + total +
                                " 个 Cubism root，新增 " + added + " 个" +
                                (pruned > 0 ? ("，清理已销毁 " + pruned + " 个") : "") +
                                "（已追踪 " + trackedRoots.Count + " 个）");
                            // 新模型可能才是当前在演的 → 立刻尝试换绑
                            if (added > 0 && TryRebindToPlayingRoot(t, "补扫发现新模型"))
                                lastMotionPollTime = -1f;   // 本帧立即重解析
                        }
                        lastScannedRootTotal = total;
                    }
                    catch (Exception nx)
                    {
                        if (!newRootScanFailedLogged)
                        {
                            newRootScanFailedLogged = true;
                            ExplorerCore.LogWarning("[Live2DRecorder] v16al 补扫异常: " + nx.Message);
                        }
                    }
                }

                // 0.1s 检查一次 animator 切段（不每帧查动画状态）
                if (trackedRoots.Count > 0 && (t - lastMotionPollTime) >= MOTION_POLL_INTERVAL)
                {
                    lastMotionPollTime = t;
                    UpdateMotionOnlyViaAnimator(t);
                    UpdatePerRootMotions(t);      // v16am：每个激活模型各一条独立动画轨
                }

                UpdateAudioEvents(t);
            }
            catch (Exception ex)
            {
                ExplorerCore.LogWarning("[Live2DRecorder] Tick 异常: " + ex.Message);
            }
        }

        /// <summary>
        /// 把内存中追踪到的 motion → audio 映射写到 <paramref name="dirPath"/> 下
        /// 固定文件名 "音频记录.txt"（按用户要求：每行 "动画：动画名称 → 音频名称"）。返回写入的绝对路径列表。
        /// </summary>
        public static List<string> ExportMotionAudioMap(string dirPath, string baseName)
        {
            var written = new List<string>();
            if (!AutoActive)
            {
                ExplorerCore.LogWarning("[Live2DRecorder] AutoStart 未激活，跳过 motion_audio_map 导出");
                return written;
            }
            if (string.IsNullOrEmpty(dirPath))
            {
                ExplorerCore.LogWarning("[Live2DRecorder] 导出目录为空");
                return written;
            }
            try
            {
                if (!Directory.Exists(dirPath)) Directory.CreateDirectory(dirPath);
                if (string.IsNullOrEmpty(baseName)) baseName = "live2d";

                string mapPath = Path.Combine(dirPath, "音频记录.txt");
                File.WriteAllText(mapPath, BuildMotionAudioMapTxt(baseName), new UTF8Encoding(false));
                written.Add(mapPath);

                string csvPath = Path.Combine(dirPath, baseName + ".audio_events.csv");
                File.WriteAllText(csvPath, BuildMotionAudioCsv(baseName), new UTF8Encoding(false));
                written.Add(csvPath);

                // v16am：每个模型各自的动画轨 + 该动画期间播放的音频（用户要的「每个模型播放什么动画、
                //   每个动画播放什么音频」）。多模型场景（本游戏 H 场景 4 个模型同时在播）必看这一份。
                string mmPath = Path.Combine(dirPath, baseName + ".model_motions.csv");
                File.WriteAllText(mmPath, BuildModelMotionsCsv(baseName), new UTF8Encoding(false));
                written.Add(mmPath);

                string manifestPath = Path.Combine(dirPath, baseName + ".recordings.json");
                File.WriteAllText(manifestPath, BuildManifestJson(baseName), new UTF8Encoding(false));
                written.Add(manifestPath);

                ExplorerCore.Log("[Live2DRecorder] motion_audio_map 导出 " + written.Count + " 个文件到 " + dirPath);
            }
            catch (Exception ex)
            {
                ExplorerCore.LogWarning("[Live2DRecorder] ExportMotionAudioMap 失败: " + ex.Message);
            }
            return written;
        }

        /// <summary>
        /// 把扫描到的 Live2D 资源（moc3 字节 + 所有 Texture2D PNG + 关联 AudioClip WAV +
        /// 可选 mesh OBJ）按 <paramref name="modelRoot"/> 名建子目录落到 <paramref name="dirPath"/> 下。
        /// 返回写入的文件路径列表。
        /// </summary>
        public static List<string> DumpLive2DResources(GameObject modelRoot, string dirPath)
        {
            var written = new List<string>();
            if (modelRoot == null) return written;
            if (string.IsNullOrEmpty(dirPath)) return written;
            try
            {
                if (!Directory.Exists(dirPath)) Directory.CreateDirectory(dirPath);
                string modelName = SanitizeFileName(modelRoot.name);
                if (string.IsNullOrEmpty(modelName)) modelName = "live2d_model";

                // 一次性扫描资源引用（不分每个 tick，可行数十毫秒到几秒）
                DumpLive2DResourcesWorker(modelRoot, dirPath, modelName, written);
            }
            catch (Exception ex)
            {
                ExplorerCore.LogWarning("[Live2DRecorder] DumpLive2DResources 失败: " + ex.Message);
            }
            return written;
        }

        /// <summary>
        /// 便利方法：写到默认导出目录（优先 Mods/sinai-dev-UnityExplorer/Live2DRecordings，
        /// 失败回退 persistentDataPath/UnityExplorer/Live2DRecordings 再回退游戏根）。
        /// </summary>
        public static List<string> AutoExportMotionAudioMap()
        {
            string dir = GetExportDir();
            if (string.IsNullOrEmpty(dir)) return new List<string>();
            return ExportMotionAudioMap(dir, "live2d");
        }

        /// <summary>
        /// 便利方法：把当前场景中所有 Live2D 模型的资源一并导出到默认目录。
        /// </summary>
        public static List<string> AutoDumpAllLive2DResources()
        {
            var written = new List<string>();
            string dir = GetExportDir();
            if (string.IsNullOrEmpty(dir)) return written;
            string resDir = Path.Combine(dir, "Resources");
            try { if (!Directory.Exists(resDir)) Directory.CreateDirectory(resDir); }
            catch { return written; }

            foreach (GameObject go in FindCubismModelRoots())
            {
                if (go == null || go.IsNullOrDestroyed(true)) continue;
                written.AddRange(DumpLive2DResources(go, resDir));
            }
            return written;
        }

        // ============= v16 内部 helper =============

        /// <summary>v16 维护当前追踪的多个 model root（可能在多角色场景里有多个）。</summary>
        private static readonly List<GameObject> trackedRoots = new();
        /// <summary>全局时钟累加（避免 Time.realtimeSinceStartup 在 IL2CPP 下精度问题）</summary>
        private static readonly System.Diagnostics.Stopwatch autoClock = new System.Diagnostics.Stopwatch();
        private static bool autoClockStarted = false;
        /// <summary>仅扫描 audio / animator 时使用，单帧驱动节流（沿用 v14 同名字段在 L168 的 0.2f）</summary>

        /// <summary>
        /// 找出当前 AppDomain 中所有活跃 Live2D 模型根（带 CubismModel 组件的 GameObject）。
        /// 跨场景返回，可能包含 DontDestroyOnLoad 模型。
        /// </summary>
        private static IEnumerable<GameObject> FindCubismModelRoots()
        {
            var found = new List<GameObject>();
            try
            {
                // 全部 UnityObject（不只是当前激活的）—— Cubism root 可能在 prefab 池里
                //   用 RuntimeHelper.FindObjectsOfTypeAll(typeof(GameObject)) 跨 Mono/IL2CPP/Unhollower 都安全
                //   （IL2CPP 下 Resources.FindObjectsOfTypeAll 是 Il2CppSystem.Object 重载，需要 Il2CppSystem.Type，
                //    而 UniverseLib 的 RuntimeHelper 已经包了一层兼容调用 —— 与 L1839 同步 AudioSource 的做法一致）
                UnityEngine.Object[] allObj = RuntimeHelper.FindObjectsOfTypeAll(typeof(GameObject));
                var allModels = new List<GameObject>(allObj == null ? 0 : allObj.Length);
                if (allObj != null)
                {
                    foreach (UnityEngine.Object o in allObj)
                    {
                        GameObject go = o as GameObject;
                        if (go != null) allModels.Add(go);
                    }
                }
                foreach (GameObject go in allModels)
                {
                    if (go == null) continue;
                    // 仅保留真实场景对象，跳过 prefab asset / 构造中 GO
                    if (go.scene == null) continue;
                    try
                    {
                        Component[] comps = go.GetComponents<Component>();
                        bool hasCubism = false;
                        if (comps != null)
                        {
                            foreach (Component c in comps)
                            {
                                if (c == null) continue;
                                string tn = AssetExporter.GetComponentTypeName(c);
                                if (!string.IsNullOrEmpty(tn) && (tn.EndsWith("CubismModel") || tn.EndsWith(".CubismModel")))
                                {
                                    hasCubism = true;
                                    break;
                                }
                            }
                        }
                        if (hasCubism) found.Add(go);
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                ExplorerCore.LogWarning("[Live2DRecorder] FindCubismModelRoots 异常: " + ex.Message);
            }
            return found;
        }

        /// <summary>对一个 root 启动追踪；返回 true 表示新启动，false 表示已在追踪或失败。</summary>
        private static bool StartFromRoot(GameObject modelRoot)
        {
            if (modelRoot == null) return false;
            if (trackedRoots.Contains(modelRoot)) return false;
            try
            {
                // v16al：不再无条件把 animatorComp 抢到本 root。
                //   旧行为「谁最后被发现就绑谁」会让真正在演的模型被其它预载/空闲模型挤掉
                //   （本游戏 7 个模型同场景并存，AutoStart 时最后一个是 立ち絵）。
                //   规则：当前绑定已失效 → 必须重绑；否则只有本 root 评分更高才重绑。
                bool boundOk = boundRoot != null && !boundRoot.IsNullOrDestroyed(true)
                               && animatorComp != null && !animatorComp.IsNullOrDestroyed();
                bool takeOver = !boundOk || ScoreLiveRoot(modelRoot) > ScoreLiveRoot(boundRoot);

                if (takeOver)
                {
                    DiscoverModelComponents(modelRoot);
                    SetupAnimatorAccess();
                    boundRoot = modelRoot;
                    pendingSegmentBreak = true;   // v16al：换了动画源，下帧在已知 t 处切开旧段
                }

                int partCount = 0, drawableCount = 0;
                foreach (var v in trackedVisuals) { if (v.IsPart) partCount++; else drawableCount++; }

                ExplorerCore.Log(string.Format(
                    "[Live2DRecorder] AutoStart root '{0}': 参数={1}, Part={2}, Drawable={3}, Animator={4}{5}",
                    modelRoot.name, trackedParams.Count, partCount, drawableCount, animatorComp != null,
                    takeOver ? "" : " (仅加入追踪，未抢占绑定)"));

                // 启动全局时钟
                if (!autoClockStarted)
                {
                    // v16i：Mono net35 兼容（Stopwatch.Restart 在 .NET 4.0+ 才有，net35 用 Reset+Start）
                    autoClock.Reset();
                    autoClock.Start();
                    autoClockStarted = true;
                }

                // 重置缓冲与节流（仅首次有效）
                if (DiscoveredModelCount == 0)
                {
                    frames.Clear();
                    segments.Clear();
                    audioEvents.Clear();
                    changeLog.Clear();
                    trackedAudios.Clear();
                    lastAudioScanTime = -10f;
                    lastAudioCheckTime = -10f;
                    audioScanCountLogged = false;
                    lastMotionPollTime = -1f;
                    cachedMotionName = null;
                    // v16aj：重置绑定与失联状态
                    boundRoot = null;
                    motionLostSince = -1f;
                    animatorRebindCount = 0;
                    motionLostDiagLogged = false;
                    lastNewRootScanTime = -10f;
                    newRootScanFailedLogged = false;
                    lastScannedRootTotal = -1;
                    pendingSegmentBreak = false;
                    // v16am：清空多模型轨
                    rootTracks.Clear();
                    modelSegments.Clear();
                    modelSegmentLogged = 0;
                    foreach (var p in trackedParams) p.LastValue = p.DefaultValue;
                    foreach (var v in trackedVisuals) { v.LastOpacity = 1f; v.LastVisible = true; }
                }

                trackedRoots.Add(modelRoot);
                // v16al：只有本 root 真的接管了绑定才更新 boundRoot，否则 animatorComp 仍属于旧 root，
                //   乱改 boundRoot 会让健康检查误判「绑定还在」。
                if (takeOver) boundRoot = modelRoot;
                DiscoveredModelCount = trackedRoots.Count;
                return true;
            }
            catch (Exception ex)
            {
                ExplorerCore.LogWarning("[Live2DRecorder] StartFromRoot 失败: " + ex.Message);
                return false;
            }
        }

        private static GameObject PickLiveRoot()
        {
            for (int i = 0; i < trackedRoots.Count; i++)
            {
                GameObject go = trackedRoots[i];
                if (go != null && !go.IsNullOrDestroyed(true)) return go;
            }
            return null;
        }

        /// <summary>
        /// v16aj：子树里是否存在「激活且正在播放 clip」的 Animator。
        /// 用于在同场景多模型（本游戏 7 个）中挑出当前真正在演的那个。
        ///
        /// ★ 必须用反射，不能写 `Animator` / `AnimatorClipInfo` 类型：
        ///   这两个类型在 `UnityEngine.AnimationModule` 里，而 Unhollower 系列配置
        ///   （ML_Cpp_net6 / ML_Cpp_net472 / BIE_Cpp 等）只引用 unhollowed 的 Unity 模块子集，
        ///   直接写类型名会报 CS1069「未能在命名空间 UnityEngine 中找到类型名 Animator」。
        ///   这里改成「按类型名找组件 + 反射调方法」，Mono 下正常；IL2CPP 下取不到方法就
        ///   返回 false，退化为只按 activeInHierarchy 打分（不影响正确性）。
        /// </summary>
        private static bool RootHasPlayingAnimator(GameObject root)
        {
            if (root == null || root.IsNullOrDestroyed(true)) return false;
            try
            {
                Component anim = GetRootAnimatorCached(root);
                if (anim == null || anim.IsNullOrDestroyed()) return false;

                MethodInfo mi = null;
                try
                {
                    mi = anim.GetType().GetMethod("GetCurrentAnimatorClipInfo",
                        new System.Type[] { typeof(int) });
                }
                catch { }
                if (mi == null) return false;

                object arr = null;
                try { arr = mi.Invoke(anim, new object[] { 0 }); } catch { }
                return ArrayLengthOf(arr) > 0;
            }
            catch { }
            return false;
        }

        /// <summary>v16aj：跨后端安全取数组长度（Mono = 托管数组；IL2CPP = 代理数组用 Count/Length 成员）。</summary>
        private static int ArrayLengthOf(object arr)
        {
            if (arr == null) return 0;
            try
            {
                System.Array a = arr as System.Array;
                if (a != null) return a.Length;
            }
            catch { }
            try
            {
                System.Type t = arr.GetType();
                System.Reflection.PropertyInfo p = t.GetProperty("Length") ?? t.GetProperty("Count");
                if (p != null) return System.Convert.ToInt32(p.GetValue(arr, null));
                System.Reflection.FieldInfo f = t.GetField("Length") ?? t.GetField("Count");
                if (f != null) return System.Convert.ToInt32(f.GetValue(arr));
            }
            catch { }
            return 0;
        }

        /// <summary>v16am：跨后端安全取数组元素（Mono = System.Array.GetValue；IL2CPP = Item 索引器 / Get 方法）。</summary>
        private static object ArrayElementAt(object arr, int idx)
        {
            if (arr == null || idx < 0) return null;
            try
            {
                System.Array a = arr as System.Array;
                if (a != null) return idx < a.Length ? a.GetValue(idx) : null;
            }
            catch { }
            try
            {
                System.Type t = arr.GetType();
                System.Reflection.PropertyInfo p = t.GetProperty("Item");
                if (p != null) return p.GetValue(arr, new object[] { idx });
                System.Reflection.MethodInfo m = t.GetMethod("Get", new System.Type[] { typeof(int) });
                if (m != null) return m.Invoke(arr, new object[] { idx });
            }
            catch { }
            return null;
        }

        /// <summary>v16am：跨后端安全取成员（属性优先，其次字段）。取不到返回 null。</summary>
        private static object GetMemberOrProp(object obj, string name)
        {
            if (obj == null) return null;
            try
            {
                System.Type t = obj.GetType();
                System.Reflection.PropertyInfo p = t.GetProperty(name);
                if (p != null && p.CanRead) return p.GetValue(obj, null);
                System.Reflection.FieldInfo f = t.GetField(name);
                if (f != null) return f.GetValue(obj);
            }
            catch { }
            return null;
        }

        /// <summary>
        /// v16am：按 root 解析「当前第 0 层（主体层）正在播的动画 clip 名」。
        ///   与 RootHasPlayingAnimator 同样用反射（不能写 Animator / AnimatorClipInfo 类型，见上方注释）。
        ///   GetCurrentAnimatorClipInfo(0) 返回按权重降序排列的数组 → 取 [0] 即当前主导 clip。
        ///   返回 null 表示「该 root 没有在播的动画」（调用方据此关闭该模型的动画段）。
        ///   <paramref name="via"/> 为诊断信息（"clipInfo" / "null" / 失败原因）。
        /// </summary>
        private static string GetRootClipName(GameObject root, out string via)
        {
            via = "-";
            if (root == null || root.IsNullOrDestroyed(true)) { via = "root空"; return null; }
            try
            {
                Component anim = GetRootAnimatorCached(root);
                if (anim == null || anim.IsNullOrDestroyed()) { via = "无Animator"; return null; }

                MethodInfo mi = null;
                try { mi = anim.GetType().GetMethod("GetCurrentAnimatorClipInfo", new System.Type[] { typeof(int) }); }
                catch { }
                if (mi == null) { via = "无方法"; return null; }

                object arr = null;
                try { arr = mi.Invoke(anim, new object[] { 0 }); } catch { }
                if (ArrayLengthOf(arr) <= 0) { via = "空"; return null; }

                object info0 = ArrayElementAt(arr, 0);
                object clip = GetMemberOrProp(info0, "clip");
                if (clip == null) { via = "无clip"; return null; }

                // 先按 UnityEngine.Object 取 name（Mono/IL2CPP 都能直接转型），失败再反射
                string nm = null;
                try { UnityEngine.Object uo = clip as UnityEngine.Object; if (uo != null) nm = uo.name; } catch { }
                if (string.IsNullOrEmpty(nm))
                {
                    try { nm = GetMemberOrProp(clip, "name") as string; } catch { }
                }
                if (string.IsNullOrEmpty(nm)) { via = "无名"; return null; }

                via = "clipInfo";
                return nm;
            }
            catch (Exception e) { via = "异常:" + e.GetType().Name; return null; }
        }

        /// <summary>
        /// v16aj：找 root 子树里的 Animator 组件（按类型名后缀匹配，不写 Animator 类型），带缓存。
        /// 缓存避免每次打分都递归整棵子树（H 场景模型 Drawable 上百，7 个 root 全扫代价高）。
        /// </summary>
        private static Component GetRootAnimatorCached(GameObject root)
        {
            int key;
            try { key = root.GetInstanceID(); } catch { return null; }

            Component cached;
            if (rootAnimatorCache.TryGetValue(key, out cached))
            {
                if (cached != null && !cached.IsNullOrDestroyed()) return cached;
                rootAnimatorCache.Remove(key);
            }

            Component found = FindAnimatorRecursive(root.transform, 0);
            try { rootAnimatorCache[key] = found; } catch { }
            return found;
        }

        private static Component FindAnimatorRecursive(Transform t, int depth)
        {
            if (t == null || depth > 24) return null;
            try
            {
                Component[] comps = t.gameObject.GetComponents<Component>();
                if (comps != null)
                {
                    for (int i = 0; i < comps.Length; i++)
                    {
                        Component c = comps[i];
                        if (c == null) continue;
                        string tn = null;
                        try { tn = AssetExporter.GetComponentTypeName(c); } catch { }
                        if (!string.IsNullOrEmpty(tn) && tn.EndsWith("Animator")) return c;
                    }
                }
            }
            catch { }
            for (int i = 0; i < t.childCount; i++)
            {
                try
                {
                    Component got = FindAnimatorRecursive(t.GetChild(i), depth + 1);
                    if (got != null) return got;
                }
                catch { }
            }
            return null;
        }

        /// <summary>
        /// v16aj：root 优先级打分（越大越可能是「当前场景正在演的那个」）。
        ///   2 = 场景中激活 且 有动画在播
        ///   1 = 有动画在播（但自身未激活，多为主界面残留/prefab 池）
        ///   0 = 其他活着的 root（兜底）
        /// 本游戏同场景并存 7 个模型，且多数时间只有 1 个是 activeInHierarchy，
        /// 所以「激活」是最可靠的判别信号。
        /// </summary>
        private static int ScoreLiveRoot(GameObject root)
        {
            if (root == null || root.IsNullOrDestroyed(true)) return -1;
            bool playing = RootHasPlayingAnimator(root);
            bool active = false;
            try { active = root.activeInHierarchy; } catch { }
            if (active && playing) return 2;
            if (playing) return 1;
            return 0;
        }

        /// <summary>
        /// v16aj：返回「当前场景里最可能正在演」的活 root（按 ScoreLiveRoot 取最高分）。
        /// 这是修「只绑到最后一个 root 的 Animator」的关键：绑定对象应随场景切换而变化。
        /// </summary>
        private static GameObject PickLiveRootPreferringPlaying()
        {
            GameObject best = null;
            int bestScore = -1;
            for (int i = 0; i < trackedRoots.Count; i++)
            {
                GameObject go = trackedRoots[i];
                if (go == null || go.IsNullOrDestroyed(true)) continue;
                int sc = ScoreLiveRoot(go);
                if (sc > bestScore) { bestScore = sc; best = go; }
                if (bestScore >= 2) break;   // 已经是最高分，不再找
            }
            return best;
        }

        /// <summary>v16aj：关闭最后一个动画段（EndTime 定在 <paramref name="endTime"/>）。</summary>
        private static void CloseLastSegment(float endTime)
        {
            if (segments.Count == 0) return;
            MotionSegment last = segments[segments.Count - 1];
            if (last.EndTime > endTime) endTime = last.EndTime;   // 不回退
            last.EndTime = endTime;
            last.EndFrame = frames.Count;
        }

        /// <summary>
        /// v16as：清掉 trackedRoots 里已销毁的 root，并同步清理 rootTracks / rootAnimatorCache。
        ///
        /// 背景：旧逻辑只「跳过」已销毁条目（状态表打 "[N]已销毁"），从不移除，
        ///   于是状态表里永远挂着一个幽灵条目，计数也自相矛盾
        ///   （11:17 日志：`场景内 10 个 Cubism root ... 已追踪 11 个` / `场景内10/已追踪11`）。
        ///   顺带避免 rootTracks 随场景反复切换无限增长（每次 Instantiate 都是新 instanceID）。
        /// </summary>
        private static int PruneDestroyedRoots()
        {
            int removed = 0;
            for (int i = trackedRoots.Count - 1; i >= 0; i--)
            {
                GameObject r = trackedRoots[i];
                bool dead;
                try { dead = r == null || r.IsNullOrDestroyed(true); } catch { dead = true; }
                if (!dead) continue;
                trackedRoots.RemoveAt(i);
                removed++;
            }

            if (rootTracks.Count > 0)
            {
                List<int> deadKeys = null;
                foreach (var kv in rootTracks)
                {
                    GameObject r = (kv.Value != null) ? kv.Value.root : null;
                    bool dead;
                    try { dead = r == null || r.IsNullOrDestroyed(true); } catch { dead = true; }
                    if (!dead) continue;
                    if (deadKeys == null) deadKeys = new List<int>(4);
                    deadKeys.Add(kv.Key);
                }
                if (deadKeys != null)
                {
                    for (int i = 0; i < deadKeys.Count; i++)
                    {
                        rootTracks.Remove(deadKeys[i]);
                        rootAnimatorCache.Remove(deadKeys[i]);
                    }
                }
            }

            if (removed > 0) rootStateLogTick = 0;   // 让下一次状态表立刻重打（条目变了）
            return removed;
        }

        /// <summary>
        /// v16aj：把动画解析目标换绑到「当前有动画在播」的活 root。
        /// 返回 true 表示真的换了绑定（调用方可立即重解析一次动画名）。
        /// </summary>
        private static bool TryRebindToPlayingRoot(float t, string reason)
        {
            try
            {
                GameObject cand = PickLiveRootPreferringPlaying();
                if (cand == null || cand == boundRoot) return false;
                // 宁可保持现状也不乱绑：候选必须真的在播，或当前绑定已彻底失效
                bool boundDead = animatorComp == null || animatorComp.IsNullOrDestroyed()
                                 || boundRoot == null || boundRoot.IsNullOrDestroyed();
                if (!boundDead && !RootHasPlayingAnimator(cand)) return false;

                string oldName = cachedMotionName;
                DiscoverModelComponents(cand);
                SetupAnimatorAccess();
                boundRoot = cand;
                animatorRebindCount++;

                if (oldName != null) CloseLastSegment(t);
                cachedMotionName = null;      // 换源 → 强制开新段
                lastMotionVia = null;
                motionDiagLogged = false;
                motionLostSince = -1f;
                // v16as：换绑 = 新对象，诊断状态重新武装（否则新绑定的「未在播」诊断永远不会再打）
                placeholderDiagLogged = false;
                lastMotionDiagResult = null;
                lastMotionDiagVia = null;

                ExplorerCore.Log("[Live2DRecorder] v16aj 换绑 #" + animatorRebindCount + " → '" + cand.name +
                    "' (" + reason + ", 旧动画=" + (oldName ?? "-") + ", 参数=" + trackedParams.Count +
                    ", animator=" + (animatorComp != null ? "✓" : "✗") + ")");
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// v16am：为「每个激活的 root」独立维护一条动画轨。
        ///
        /// 这是「一个场景只会记录一个动画」的正解：
        ///   H 场景里 胸 / raiden_shougun / 局部・臀部 / 断面図 四个模型同时激活且各自在播，
        ///   旧逻辑只跟单一绑定 root → 只产出一条动画轨。
        ///   本函数对每个 activeInHierarchy 的 root 各自解析第 0 层当前 clip 名，
        ///   名字变化就关旧段、开新段（段带 RootName），汇入 modelSegments。
        ///
        /// ★ 与主绑定轨（cachedMotionName / segments）完全解耦，主轨行为与产物格式不变。
        /// </summary>
        private static void UpdatePerRootMotions(float t)
        {
            try
            {
                for (int i = 0; i < trackedRoots.Count; i++)
                {
                    GameObject r = trackedRoots[i];
                    if (r == null || r.IsNullOrDestroyed(true)) continue;

                    bool act = false;
                    try { act = r.activeInHierarchy; } catch { }
                    if (!act) continue;                 // 只追踪场景里真正启用着的模型

                    int key;
                    try { key = r.GetInstanceID(); } catch { continue; }

                    RootMotionTrack tr;
                    if (!rootTracks.TryGetValue(key, out tr) || tr == null)
                    {
                        tr = new RootMotionTrack { root = r, rootName = r.name, lastReliableTime = -1f };
                        rootTracks[key] = tr;
                    }
                    else if (tr.rootName != r.name)
                    {
                        tr.rootName = r.name;           // 同名对象复用（对象池）时校正
                    }

                    string via;
                    string clip = GetRootClipName(r, out via);

                    if (!string.IsNullOrEmpty(clip))
                    {
                        tr.lastReliableTime = t;
                        tr.lastVia = via;

                        if (tr.motion != clip)
                        {
                            // 该模型换动画 → 关旧段、开新段
                            if (tr.seg != null)
                            {
                                if (tr.seg.EndTime < t) tr.seg.EndTime = t;
                                tr.seg.EndFrame = frames.Count;
                            }
                            tr.motion = clip;
                            tr.seg = new MotionSegment
                            {
                                MotionName = clip,
                                RootName = tr.rootName,
                                StartTime = t,
                                EndTime = t,
                                StartFrame = frames.Count,
                                EndFrame = frames.Count
                            };
                            modelSegments.Add(tr.seg);
                            if (modelSegmentLogged < 400)
                            {
                                modelSegmentLogged++;
                                ExplorerCore.Log("[Live2DRecorder] v16am 模型动画: " + tr.rootName +
                                    " → " + clip + " (t=" + t.ToString("F2") + "s, via " + via + ")");
                            }
                        }
                        else
                        {
                            if (tr.seg != null) { tr.seg.EndTime = t; tr.seg.EndFrame = frames.Count; }
                        }
                    }
                    else if (tr.seg != null && (t - tr.lastReliableTime) >= MOTION_LOST_GRACE)
                    {
                        // 该模型停播（被隐藏/动画停止）→ 关闭它的段，避免把音频一直挂到它身上
                        if (tr.seg.EndTime < tr.lastReliableTime) tr.seg.EndTime = tr.lastReliableTime;
                        tr.seg.EndFrame = frames.Count;
                        if (modelSegmentLogged < 400)
                        {
                            modelSegmentLogged++;
                            ExplorerCore.Log("[Live2DRecorder] v16am 模型停播: " + tr.rootName +
                                " 关闭 '" + tr.motion + "' (t=" + t.ToString("F2") + "s, " + via + ")");
                        }
                        tr.seg = null;
                        tr.motion = null;
                    }
                }
            }
            catch (Exception ex)
            {
                ExplorerCore.LogWarning("[Live2DRecorder] v16am UpdatePerRootMotions 异常: " + ex.Message);
            }
        }

        /// <summary>
        /// v16am：取时间 t 正在播放的所有模型段（"模型/动画" 列表，去重）。用于把一条音频
        /// 同时挂到当时在演的所有模型动画上（用户要的「每个动画播放的音频名」）。
        /// </summary>
        private static List<string> FindActiveModelLabels(float t)
        {
            List<string> hits = new List<string>();
            for (int i = 0; i < modelSegments.Count; i++)
            {
                MotionSegment s = modelSegments[i];
                if (t >= s.StartTime - 0.3f && t <= s.EndTime + 0.5f)
                {
                    string tag = s.Label;
                    if (!hits.Contains(tag)) hits.Add(tag);
                }
            }
            return hits;
        }

        private static void RecomputeTrackingState()
        {
            int live = 0;
            for (int i = 0; i < trackedRoots.Count; i++)
            {
                if (trackedRoots[i] != null && !trackedRoots[i].IsNullOrDestroyed(true)) live++;
            }
            // 仅用于诊断：记录的 active root count
            // （DiscoveredModelCount 保留为历史累计）
        }

        /// <summary>v16 精简：每帧只管 audio + animator 切段，不再每帧 CaptureCurrentFrame</summary>
        private static void TickAudioOnly()
        {
            try
            {
                float t = autoClock.IsRunning ? (float)autoClock.Elapsed.TotalSeconds : 0f;
                UpdateAudioEvents(t);
            }
            catch { }
        }

        /// <summary>仅查 animator 当前 clip 名（不读任何 param）</summary>
        private static void UpdateMotionOnlyViaAnimator(float t)
        {
            try
            {
                // ---------- v16aj 步骤 1：绑定健康检查 ----------
                //   同场景并存多个 Live2D 模型时，绑定的 root 被销毁（场景切换）必须立刻重绑，
                //   否则 animatorComp 永远 Unity-null、resolver 全 null，动画名被冻结在旧值。
                if (animatorComp == null || animatorComp.IsNullOrDestroyed()
                    || boundRoot == null || boundRoot.IsNullOrDestroyed())
                {
                    TryRebindToPlayingRoot(t, "绑定失效");
                }

                // v16al：补扫/启动刚换了动画源 → 在已知时刻切开旧段（否则新旧同名动画会被并成一段）
                if (pendingSegmentBreak)
                {
                    pendingSegmentBreak = false;
                    if (cachedMotionName != null)
                    {
                        CloseLastSegment(t);
                        ExplorerCore.Log("[Live2DRecorder] v16al 动画源已切换，切成新段 (t=" + t.ToString("F2") + "s)");
                        cachedMotionName = null;
                    }
                    lastMotionVia = null;
                    motionLostSince = -1f;
                }

                // ---------- v16aj 诊断：每 ~10s 打一次全部 root 的状态表 ----------
                //   多模型场景里最关键的信息：哪个 root 活着 / 激活 / 真的在播。
                //   典型预期：标题界面 → 立ち絵* (激活,在播)；H 场景 → 对应姿势模型* (激活,在播)，立ち絵 (隐藏,-)。
                if (++rootStateLogTick >= 50)
                {
                    rootStateLogTick = 0;
                    try
                    {
                        StringBuilder sb = new StringBuilder();
                        sb.Append("[Live2DRecorder] v16aj root 状态 (绑定=");
                        sb.Append(boundRoot != null && !boundRoot.IsNullOrDestroyed(true) ? boundRoot.name : "-");
                        sb.Append(animatorComp != null ? ", 通道✓" : ", 通道✗");
                        sb.Append(", 场景内" + (lastScannedRootTotal >= 0 ? lastScannedRootTotal.ToString() : "?") +
                                  "/已追踪" + trackedRoots.Count + "): ");
                        for (int i = 0; i < trackedRoots.Count; i++)
                        {
                            GameObject r = trackedRoots[i];
                            if (r == null || r.IsNullOrDestroyed(true)) { sb.Append("[" + i + "]已销毁 "); continue; }
                            bool act = false;
                            try { act = r.activeInHierarchy; } catch { }
                            // v16am：直接打出「每个模型当前在播什么动画」，而不只是 在播/未播。
                            string via;
                            string clip = GetRootClipName(r, out via);
                            string playState = string.IsNullOrEmpty(clip)
                                ? ("未播(" + via + ")")
                                : ("在播:" + clip);
                            sb.Append("[" + i + "]" + r.name + (r == boundRoot ? "*" : "") +
                                      "(" + (act ? "激活" : "隐藏") + "," + playState + ") ");
                        }
                        ExplorerCore.Log(sb.ToString());
                    }
                    catch { }
                }

                string motion = PollAnimatorMotionName(t);

                // ---------- v16aj 步骤 2：没解析到「真在播」的动画 → 计时 → 超时换绑 / 关闭当前段 ----------
                //   修「一个段被无限延长 → 所有音频都归到这一个动画上」。
                //   ★ 关键：只有 reliable=true（真从播放状态取到的名字）才算有效。
                //     tier G 占位名（= controller 的第一项 clip，与当前在播无关）在这里被当作"没解析到"，
                //     否则空闲模型会一直报同一个名字，换绑永远不会触发（就是本次故障）。
                if (!lastMotionReliable)
                {
                    if (motionLostSince < 0f) motionLostSince = t;

                    if ((t - motionLostSince) >= MOTION_LOST_GRACE)
                    {
                        // 尝试换绑到「当前真在播」的模型（本游戏在标题↔H 场景间切换模型）
                        if (TryRebindToPlayingRoot(t,
                                "无可靠动画 " + (t - motionLostSince).ToString("F2") + "s (仅占位名 " + (motion ?? "-") + ")"))
                        {
                            motion = PollAnimatorMotionName(t);
                        }

                        if (!lastMotionReliable && cachedMotionName != null)
                        {
                            CloseLastSegment(motionLostSince);
                            string closeMsg = "[Live2DRecorder] v16aj 无可靠动画 " +
                                (t - motionLostSince).ToString("F2") + "s，关闭动画段 '" + cachedMotionName +
                                "' (t=" + motionLostSince.ToString("F2") + "s)；" +
                                "此后的音频在解析出真在播的动画前记为 SegmentIndex=-1（无动画）";
                            if (!motionLostDiagLogged) { motionLostDiagLogged = true; ExplorerCore.LogWarning(closeMsg); }
                            else ExplorerCore.Log(closeMsg);
                            cachedMotionName = null;
                            lastMotionVia = null;
                        }
                        motionLostSince = -1f;
                        if (!lastMotionReliable) return;
                    }

                    if (!lastMotionReliable) return;
                }
                motionLostSince = -1f;

                // 切段检测：与缓存的 motion 名比较
                if (cachedMotionName == null)
                {
                    cachedMotionName = motion;
                    var seg = new MotionSegment
                    {
                        MotionName = motion,
                        StartTime = t,
                        EndTime = t,
                        StartFrame = frames.Count,
                        EndFrame = frames.Count
                    };
                    segments.Add(seg);
                    TriggeredMotionCount = segments.Count;
                    ExplorerCore.Log("[Live2DRecorder] 动画切入: " + motion + " (t=" + t.ToString("F2") + "s)");
                }
                else if (motion != cachedMotionName)
                {
                    // 切段
                    if (segments.Count > 0)
                    {
                        var last = segments[segments.Count - 1];
                        last.EndTime = t;
                        last.EndFrame = frames.Count;
                    }
                    var seg = new MotionSegment
                    {
                        MotionName = motion,
                        StartTime = t,
                        EndTime = t,
                        StartFrame = frames.Count,
                        EndFrame = frames.Count
                    };
                    segments.Add(seg);
                    TriggeredMotionCount = segments.Count;
                    ExplorerCore.Log(string.Format("[Live2DRecorder] 动画切换: {0} → {1} (t={2:F2}s)", cachedMotionName, motion, t));
                    cachedMotionName = motion;
                }
                else
                {
                    if (segments.Count > 0)
                    {
                        var last = segments[segments.Count - 1];
                        last.EndTime = t;
                        last.EndFrame = frames.Count;
                    }
                }
            }
            catch (Exception ex)
            {
                if (!animatorPollFailedLogged)
                {
                    animatorPollFailedLogged = true;
                    ExplorerCore.LogWarning("[Live2DRecorder] UpdateMotionOnlyViaAnimator 异常: " + ex.Message);
                }
            }
        }

        private static void DumpLive2DResourcesWorker(GameObject root, string dirPath, string modelName, List<string> written)
        {
            int dumped = 0;
            HashSet<UnityEngine.Object> visited = new HashSet<UnityEngine.Object>();

            // 1) 收集 root 子树所有 Texture2D / AudioClip / Mesh 引用
            //   不依赖 AssetExporter.GetAllReferencedObjects（v16 暂未实现）——直接 GetComponent* 扫：
            //   Textures：CubismRenderer / MeshRenderer / RawImage 等
            //   AudioClips：AudioSource
            //   Meshes：MeshFilter / SkinnedMeshRenderer
            try
            {
                // 1a. 全部 Renderer 的 sharedMaterial / Material.mainTexture 链
                Renderer[] renderers = null;
                try { renderers = root.GetComponentsInChildren<Renderer>(true); } catch { }
                if (renderers != null)
                {
                    foreach (Renderer r in renderers)
                    {
                        if (r == null) continue;
                        Material[] mats = null;
                        try { mats = r.sharedMaterials; } catch { }
                        if (mats == null) continue;
                        foreach (Material m in mats)
                        {
                            if (m == null) continue;
                            // 主纹理
                            try { DumpTexture(m.mainTexture, visited, dirPath, modelName, written); } catch { }
                            // 全部 _MainTex 等颜色纹理字段
                            // v16i：Material.GetTexturePropertyNames() 是 Unity 2021.2+ API，
                            //   lib/net35/UnityEngine.dll 是旧版没有这 API；降级为遍历 Shader 属性
                            string[] texProps = SafeGetTexturePropertyNames(m);
                            if (texProps != null)
                            {
                                foreach (string propName in texProps)
                                {
                                    try
                                    {
                                        Texture t = m.GetTexture(propName);
                                        if (t != null) DumpTexture(t, visited, dirPath, modelName, written);
                                    }
                                    catch { }
                                }
                            }
                        }
                    }
                }

                // 1b. 全部 AudioSource 的 clip
                AudioSource[] sources = null;
                try { sources = root.GetComponentsInChildren<AudioSource>(true); } catch { }
                if (sources != null)
                {
                    foreach (AudioSource a in sources)
                    {
                        if (a == null || a.clip == null) continue;
                        AudioClip clip = a.clip;
                        if (visited.Contains(clip)) continue;
                        visited.Add(clip);
                        string safeClipName = SanitizeFileName(clip.name);
                        if (string.IsNullOrEmpty(safeClipName)) safeClipName = "audio_" + clip.GetInstanceID();
                        string path = Path.Combine(dirPath, modelName + "." + safeClipName + ".wav");
                        try { AssetExporter.ExportAudioAsWAV(clip, path); written.Add(path); dumped++; }
                        catch (Exception ex) { ExplorerCore.LogWarning("[Live2DRecorder] dump audio '" + clip.name + "' 失败: " + ex.Message); }
                    }
                }

                // 1c. 全部 Mesh（MeshFilter + SkinnedMeshRenderer）
                MeshFilter[] filters = null;
                try { filters = root.GetComponentsInChildren<MeshFilter>(true); } catch { }
                if (filters != null)
                {
                    foreach (MeshFilter mf in filters)
                    {
                        if (mf == null || mf.sharedMesh == null) continue;
                        DumpMesh(mf.sharedMesh, visited, dirPath, modelName, written);
                    }
                }
                SkinnedMeshRenderer[] smrs = null;
                try { smrs = root.GetComponentsInChildren<SkinnedMeshRenderer>(true); } catch { }
                if (smrs != null)
                {
                    foreach (SkinnedMeshRenderer smr in smrs)
                    {
                        if (smr == null || smr.sharedMesh == null) continue;
                        DumpMesh(smr.sharedMesh, visited, dirPath, modelName, written);
                    }
                }
            }
            catch (Exception ex)
            {
                ExplorerCore.LogWarning("[Live2DRecorder] DumpLive2DResources 资源扫描异常: " + ex.Message);
            }

            // 2) dump moc3 字节流（沿反射链 CubismModel → model → moc）
            try
            {
                byte[] moc3Bytes = TryGetMoc3Bytes(root);
                if (moc3Bytes != null && moc3Bytes.Length > 0)
                {
                    string path = Path.Combine(dirPath, modelName + ".moc3");
                    File.WriteAllBytes(path, moc3Bytes);
                    written.Add(path);
                    dumped++;
                }
            }
            catch (Exception ex)
            {
                ExplorerCore.LogWarning("[Live2DRecorder] dump moc3 失败: " + ex.Message);
            }

            if (dumped > 0)
                ExplorerCore.Log("[Live2DRecorder] DumpLive2DResources '" + modelName + "': 写出 " + dumped + " 个资源到 " + dirPath);
        }

        /// <summary>内部 helper：dump 单个 Texture（如不是 Texture2D 则跳过）</summary>
        private static void DumpTexture(Texture tex, HashSet<UnityEngine.Object> visited, string dirPath, string modelName, List<string> written)
        {
            if (tex == null) return;
            Texture2D t2d = tex as Texture2D;
            if (t2d == null) return;
            if (visited.Contains(t2d)) return;
            visited.Add(t2d);

            string safeTexName = SanitizeFileName(t2d.name);
            if (string.IsNullOrEmpty(safeTexName)) safeTexName = "tex_" + t2d.GetInstanceID();
            string path = Path.Combine(dirPath, modelName + "." + safeTexName + ".png");
            try
            {
                AssetExporter.ExportTextureAsPNG(t2d, path);
                written.Add(path);
            }
            catch (Exception ex)
            {
                ExplorerCore.LogWarning("[Live2DRecorder] dump texture '" + t2d.name + "' 失败: " + ex.Message);
            }
        }

        /// <summary>内部 helper：dump Mesh 为 OBJ（带已访问 dedup）</summary>
        private static void DumpMesh(Mesh mesh, HashSet<UnityEngine.Object> visited, string dirPath, string modelName, List<string> written)
        {
            if (mesh == null) return;
            if (visited.Contains(mesh)) return;
            visited.Add(mesh);

            string safeMeshName = SanitizeFileName(mesh.name);
            if (string.IsNullOrEmpty(safeMeshName)) safeMeshName = "mesh_" + mesh.GetInstanceID();
            string path = Path.Combine(dirPath, modelName + "." + safeMeshName + ".obj");
            try
            {
                AssetExporter.ExportMeshAsOBJ(mesh, path);
                written.Add(path);
            }
            catch (Exception ex)
            {
                ExplorerCore.LogWarning("[Live2DRecorder] dump mesh '" + mesh.name + "' 失败: " + ex.Message);
            }
        }

        /// <summary>
        /// 反射链尝试：root → CubismModel → get/Model → IModel（如 CubismCoreModel 或 Native 持有） → moc3。
        /// 容错：每一层都 try-catch，失败返回 null。
        /// </summary>
        private static byte[] TryGetMoc3Bytes(GameObject root)
        {
            try
            {
                Component[] comps = root.GetComponentsInChildren<Component>(true);
                if (comps == null) return null;
                foreach (Component c in comps)
                {
                    if (c == null) continue;
                    string tn = null;
                    try { tn = AssetExporter.GetComponentTypeName(c); } catch { }
                    if (string.IsNullOrEmpty(tn)) continue;
                    if (!tn.EndsWith("CubismModel")) continue;

                    // 第一跳：CubismModel.model 字段（Cubism 5.x 持 ICubismModel，可能含 m_Moc 文件引用）
                    object modelObj = AssetExporter.GetFieldOrProp(c, "model");
                    if (modelObj != null)
                    {
                        byte[] buf = AssetExporter.GetFieldOrProp(modelObj, "moc") as byte[];
                        if (buf != null && buf.Length > 0) return buf;
                        buf = AssetExporter.GetFieldOrProp(modelObj, "_moc") as byte[];
                        if (buf != null && buf.Length > 0) return buf;
                        buf = AssetExporter.GetFieldOrProp(modelObj, "bytes") as byte[];
                        if (buf != null && buf.Length > 0) return buf;
                    }
                    // 第二跳：CubismModel.asset（Moc3Asset 之类）
                    object assetObj = AssetExporter.GetFieldOrProp(c, "asset");
                    if (assetObj != null)
                    {
                        byte[] buf = AssetExporter.GetFieldOrProp(assetObj, "moc") as byte[];
                        if (buf != null && buf.Length > 0) return buf;
                        buf = AssetExporter.GetFieldOrProp(assetObj, "_moc") as byte[];
                        if (buf != null && buf.Length > 0) return buf;
                    }
                }
            }
            catch (Exception ex)
            {
                ExplorerCore.Log("[Live2DRecorder] TryGetMoc3Bytes 路径异常（已忽略）: " + ex.Message);
            }
            return null;
        }

        // ============= 兼容保留：内部追踪仍走 v14/v15e 的核心逻辑 =============
        // UpdateAudioEvents / GetCurrentAnimatorMotionName / DiscoverRecursive 等函数原样保留。
        // 原 StartRecording/StopRecording/ClearRecordings/RecorderUpdate/ExportRecordings 已替换为：
        //   - AutoStartAllScenes() / Tick()：自动开始 + 每帧驱动
        //   - ExportMotionAudioMap(dir, baseName)：导出 animation→audio 映射（txt+csv+manifest）
        //   - DumpLive2DResources(modelRoot, dir)：dump moc3 + texture + audio + mesh
        //   - AutoExportMotionAudioMap() / AutoDumpAllLive2DResources()：默认目录版

        /// <summary>
        /// 获取 Material 的纹理属性名列表。
        /// v16i：Material.GetTexturePropertyNames() 是 Unity 2021.2+ API，
        ///   lib/net35/UnityEngine.dll（旧版 Unity 引用）没有这 API。
        ///   Shader.GetPropertyCount 也需要 Unity 2019+。最稳：硬编码常见纹理属性名。
        /// </summary>
        public static string[] SafeGetTexturePropertyNames(Material m)
        {
            if (m == null) return new string[0];
            // 优先尝试新版 API（Unity 2021.2+），失败则降级硬编码列表
            try
            {
                // 用 reflection 探测是否存在 GetTexturePropertyNames（避免编译期硬依赖）
                var method = typeof(Material).GetMethod("GetTexturePropertyNames",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (method != null)
                {
                    var result = method.Invoke(m, null) as string[];
                    if (result != null) return result;
                }
            }
            catch { }
            // 降级：常用纹理属性名硬编码（_MainTex 是 Unity 标准，绝大多数 shader 都用这个名）
            return new string[] { "_MainTex", "_BaseMap", "_BumpMap", "_EmissionMap",
                                  "_DetailAlbedoMap", "_OcclusionMap", "_MetallicGlossMap",
                                  "_SpecGlossMap", "_ParallaxMap" };
        }

        /// <summary>
        /// 获取导出目录（优先：插件运行目录/Live2DRecordings，即 Mods（或 plugins）下的
        /// sinai-dev-UnityExplorer 文件夹内新建 Live2DRecordings 子文件夹存放导出物；
        /// 失败时回退 Application.persistentDataPath/UnityExplorer/Live2DRecordings，
        /// 再回退游戏根目录/UnityExplorer_Live2DRecordings）。
        /// </summary>
        public static string GetExportDir()
        {
            // v16d 用户要求：导出到与 UE 主导出器一致的位置（ExplorerCore.ExplorerFolder/Output）
            //   这样和另一个 Live2D exporter 的 Output 目录在一起，找起来方便
            try
            {
                string pluginDir = ExplorerCore.ExplorerFolder;
                if (!string.IsNullOrEmpty(pluginDir))
                {
                    string dir = Path.Combine(Path.Combine(pluginDir, "Output"), "");
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    return dir;
                }
            }
            catch { }
            try
            {
                string dir = Path.Combine(Path.Combine(Application.persistentDataPath, "UnityExplorer"), "Live2DRecordings");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                return dir;
            }
            catch { }
            try
            {
                string gameRoot = Path.GetDirectoryName(Application.dataPath ?? ".");
                string dir = Path.Combine(Path.Combine(gameRoot ?? ".", "UnityExplorer_Live2DRecordings"), "");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                return dir;
            }
            catch { }
            return null;
        }

        // ============= 模型组件扫描（v14：启动时一次）=============

        private static void DiscoverModelComponents(GameObject root)
        {
            trackedParams.Clear();
            trackedVisuals.Clear();
            cubismModelComp = null;
            motionControllerComp = null;
            animatorComp = null;
            DiscoverRecursive(root.transform, 0);
        }

        private static void DiscoverRecursive(Transform t, int depth)
        {
            if (t == null || depth > 24) return;
            try
            {
                Component[] comps = t.gameObject.GetComponents<Component>();
                if (comps != null)
                {
                    foreach (Component c in comps)
                    {
                        if (c == null) continue;
                        string tn = null;
                        try { tn = AssetExporter.GetComponentTypeName(c); } catch { }
                        if (string.IsNullOrEmpty(tn)) continue;

                        if (tn.Contains("CubismParameter"))
                        {
                            AddTrackedParam(c);
                        }
                        else if (tn.Contains("CubismPart"))
                        {
                            AddTrackedVisual(c, true);
                        }
                        else if (tn.Contains("CubismDrawable"))
                        {
                            AddTrackedVisual(c, false);
                        }
                        else if (cubismModelComp == null && tn.EndsWith("CubismModel"))
                        {
                            cubismModelComp = c;
                        }
                        else if (motionControllerComp == null && tn.Contains("CubismMotionController"))
                        {
                            motionControllerComp = c;
                        }
                        else if (animatorComp == null && tn.EndsWith("Animator"))
                        {
                            animatorComp = c;
                        }
                    }
                }
            }
            catch { }
            for (int i = 0; i < t.childCount; i++)
            {
                try { DiscoverRecursive(t.GetChild(i), depth + 1); }
                catch { }
            }
        }

        private static void AddTrackedParam(Component c)
        {
            TrackedParam tp = new TrackedParam();
            tp.Value = CubismMemberReader.Create(c, "Value", "value", "_value");

            CubismMemberReader idReader = CubismMemberReader.Create(c, "Id", "id", "_id");
            string id = idReader.ReadString();
            if (string.IsNullOrEmpty(id))
            {
                try { id = c.gameObject.name; } catch { }
            }
            tp.Id = string.IsNullOrEmpty(id) ? ("param_" + trackedParams.Count) : id;

            tp.Default = CubismMemberReader.Create(c, "DefaultValue", "defaultValue", "_defaultValue");
            float dv = float.NaN;
            if (tp.Default != null && tp.Default.Resolved) dv = tp.Default.ReadFloat(float.NaN);
            if (float.IsNaN(dv) && tp.Value != null) dv = tp.Value.ReadFloat(0f);
            if (float.IsNaN(dv)) dv = 0f;
            tp.DefaultValue = dv;
            tp.LastValue = dv;

            trackedParams.Add(tp);
        }

        private static void AddTrackedVisual(Component c, bool isPart)
        {
            TrackedVisual tv = new TrackedVisual();
            tv.IsPart = isPart;
            tv.Opacity = CubismMemberReader.Create(c, "Opacity", "opacity", "_opacity");
            if (!isPart)
                tv.Visible = CubismMemberReader.Create(c, "IsVisible", "isVisible", "_isVisible", "Visible", "visible");

            CubismMemberReader idReader = CubismMemberReader.Create(c, "Id", "id", "_id");
            string id = idReader.ReadString();
            if (string.IsNullOrEmpty(id))
            {
                try { id = c.gameObject.name; } catch { }
            }
            tv.Id = string.IsNullOrEmpty(id) ? ((isPart ? "part_" : "drawable_") + trackedVisuals.Count) : id;

            tv.LastOpacity = 1f;
            tv.LastVisible = true;

            trackedVisuals.Add(tv);
        }

        /// <summary>一次性诊断：子树组件类型统计（未发现 Cubism 组件时调用）。</summary>
        private static void DumpComponentCensus(GameObject root)
        {
            try
            {
                Dictionary<string, int> census = new Dictionary<string, int>();
                CensusRecursive(root.transform, census, 0);
                StringBuilder sb = new StringBuilder();
                foreach (var kv in census)
                {
                    if (sb.Length > 0) sb.Append(", ");
                    sb.Append(kv.Key).Append("=").Append(kv.Value);
                    if (sb.Length > 1500) { sb.Append(" ..."); break; }
                }
                ExplorerCore.LogWarning("[Live2DRecorder] 子树组件类型统计: " + sb);
            }
            catch { }
        }

        private static void CensusRecursive(Transform t, Dictionary<string, int> census, int depth)
        {
            if (t == null || depth > 24) return;
            try
            {
                Component[] comps = t.gameObject.GetComponents<Component>();
                if (comps != null)
                {
                    foreach (Component c in comps)
                    {
                        if (c == null) continue;
                        string tn = null;
                        try { tn = AssetExporter.GetComponentTypeName(c); } catch { }
                        if (string.IsNullOrEmpty(tn)) continue;
                        int n;
                        census.TryGetValue(tn, out n);
                        census[tn] = n + 1;
                    }
                }
            }
            catch { }
            for (int i = 0; i < t.childCount; i++)
            {
                try { CensusRecursive(t.GetChild(i), census, depth + 1); }
                catch { }
            }
        }

        // ============= 每帧抓取（v14：走缓存读取器）=============

        private static FrameSnapshot CaptureCurrentFrame(float t)
        {
            FrameSnapshot snap = new FrameSnapshot();
            snap.Time = t;

            // motion 名：CubismMotionController → Animator 轮询 → null（沿用上段名）
            string name = ResolveMotionControllerName();
            if (string.IsNullOrEmpty(name))
                name = PollAnimatorMotionName(t);
            snap.MotionName = name;

            // 参数
            if (trackedParams.Count > 0)
            {
                float[] vals = new float[trackedParams.Count];
                for (int i = 0; i < trackedParams.Count; i++)
                {
                    TrackedParam p = trackedParams[i];
                    // 读取失败沿用上次值，避免伪变化点
                    float v = (p.Value != null && p.Value.Resolved) ? p.Value.ReadFloat(p.LastValue) : p.LastValue;
                    vals[i] = v;
                    p.LastValue = v;
                }
                snap.ParamValues = vals;
            }

            // Part + Drawable
            if (trackedVisuals.Count > 0)
            {
                float[] ops = new float[trackedVisuals.Count];
                bool[] vis = new bool[trackedVisuals.Count];
                for (int i = 0; i < trackedVisuals.Count; i++)
                {
                    TrackedVisual tv = trackedVisuals[i];
                    float op = (tv.Opacity != null && tv.Opacity.Resolved) ? tv.Opacity.ReadFloat(tv.LastOpacity) : tv.LastOpacity;
                    bool vb;
                    if (tv.Visible != null && tv.Visible.Resolved)
                        vb = tv.Visible.ReadBool(op > 0.001f);
                    else
                        vb = op > 0.001f;
                    ops[i] = op;
                    vis[i] = vb;
                    tv.LastOpacity = op;
                    tv.LastVisible = vb;
                }
                snap.VisualOpacities = ops;
                snap.VisualVisibles = vis;
            }

            return snap;
        }

        // ============= motion 名检测 =============

        /// <summary>CubismMotionController._motionStates[].motionName（其他游戏用，目标游戏无此组件）</summary>
        private static string ResolveMotionControllerName()
        {
            if (motionControllerComp == null) return null;
            try
            {
                object states = AssetExporter.GetFieldOrProp(motionControllerComp,
                    "_motionStates", "MotionStates", "motionStates");
                foreach (object s in EnumerateAny(states))
                {
                    string n = AssetExporter.GetFieldOrProp(s, "_motionName", "motionName", "MotionName") as string;
                    if (!string.IsNullOrEmpty(n)) return n;
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 建立 Animator.GetCurrentAnimatorClipInfo(int) 的反射调用通道。
        /// CPP：代理类重包装后 System 反射 Invoke（返回数组由代理层封送）。
        /// Mono：组件真实类型上直接反射。
        /// </summary>
        private static void SetupAnimatorAccess()
        {
            animatorInvokeTarget = null;
            animatorGetCurrentClipInfo = null;
            animatorGetNextClipInfo = null;
            animatorIsInTransition = null;
            animatorGetCurrentStateInfo = null;
            animatorGetNextStateInfo = null;
            animatorStringToHash = null;
            if (animatorComp == null) return;

#if CPP
            try
            {
                Il2CppSystem.Object iobj = animatorComp.TryCast<Il2CppSystem.Object>();
                if (iobj != null)
                {
                    string fullName = null;
                    try
                    {
                        Il2CppSystem.Type it = iobj.GetIl2CppType();
                        if (it != null) fullName = it.FullName ?? it.Name;
                    }
                    catch { }
                    if (!string.IsNullOrEmpty(fullName))
                    {
                        System.Type proxyType = AssetExporter.FindProxyType(fullName);
                        if (proxyType != null)
                        {
                            object inst = System.Activator.CreateInstance(proxyType, new object[] { iobj.Pointer });
                            MethodInfo m = proxyType.GetMethod("GetCurrentAnimatorClipInfo",
                                new System.Type[] { typeof(int) });
                            if (inst != null && m != null)
                            {
                                animatorInvokeTarget = inst;
                                animatorGetCurrentClipInfo = m;
                                // v16j Tier C/D 反射通道（失败不影响主通道）
                                animatorGetNextClipInfo = proxyType.GetMethod("GetNextAnimatorClipInfo", new System.Type[] { typeof(int) });
                                animatorIsInTransition = proxyType.GetMethod("IsInTransition", new System.Type[] { typeof(int) });
                                animatorGetCurrentStateInfo = proxyType.GetMethod("GetCurrentAnimatorStateInfo", new System.Type[] { typeof(int) });
                                animatorGetNextStateInfo = proxyType.GetMethod("GetNextAnimatorStateInfo", new System.Type[] { typeof(int) });
                                animatorStringToHash = proxyType.GetMethod("StringToHash", new System.Type[] { typeof(string) });
                            }
                        }
                    }
                }
            }
            catch { }
#endif
            // Mono / 兜底：组件托管类型直接反射
            try
            {
                System.Type at = animatorComp.GetType();
                MethodInfo m = at.GetMethod("GetCurrentAnimatorClipInfo",
                    new System.Type[] { typeof(int) });
                if (m != null)
                {
                    animatorInvokeTarget = animatorComp;
                    animatorGetCurrentClipInfo = m;
                    // v16j Tier C/D 反射通道（失败不影响主通道）
                    animatorGetNextClipInfo = at.GetMethod("GetNextAnimatorClipInfo", new System.Type[] { typeof(int) });
                    animatorIsInTransition = at.GetMethod("IsInTransition", new System.Type[] { typeof(int) });
                    animatorGetCurrentStateInfo = at.GetMethod("GetCurrentAnimatorStateInfo", new System.Type[] { typeof(int) });
                    animatorGetNextStateInfo = at.GetMethod("GetNextAnimatorStateInfo", new System.Type[] { typeof(int) });
                    animatorStringToHash = at.GetMethod("StringToHash", new System.Type[] { typeof(string) });
                }
            }
            catch { }

            // v16k：每次模型（重新）发现时，强制打一次 Tier C/D 反射绑定状态，
            // 这样即使 Tier A 没装、resolver 全 null，也能看到是不是反射通道本身就没绑上。
            if (!animatorAccessDiagLogged)
            {
                animatorAccessDiagLogged = true;
                ExplorerCore.Log(string.Format(
                    "[Live2DRecorder] v16k AnimatorAccess 反射绑定: GetCurrentClipInfo={0}, GetNextClipInfo={1}, IsInTransition={2}, GetCurrentStateInfo={3}, GetNextStateInfo={4}, StringToHash={5}, invokeTarget={6}",
                    animatorGetCurrentClipInfo != null,
                    animatorGetNextClipInfo != null,
                    animatorIsInTransition != null,
                    animatorGetCurrentStateInfo != null,
                    animatorGetNextStateInfo != null,
                    animatorStringToHash != null,
                    animatorInvokeTarget != null ? animatorInvokeTarget.GetType().FullName : "null"));
            }

#if ML073
            // v16j Tier A：Harmony hook 播放入口（v16k：移到方法末尾，
            //   无论 CPP 成功 / Mono 成功 / 兜底成功，都确保 Install 至少执行一次）
            if (!animatorHookInstallAttempted)
            {
                animatorHookInstallAttempted = true;
                AnimatorPlayHook.Install();
            }
#endif
        }

        /// <summary>
        /// v16j 多级动画名抓取链主调度（0.2s 节流，结果缓存）。
        /// Tier A: Harmony hook 播放请求（最准：游戏传的名字，请求瞬间捕获）
        /// Tier C: GetCurrentAnimatorClipInfo 按 weight 加权 + transition 期间优先 GetNextAnimatorClipInfo
        /// Tier D: GetCurrentAnimatorStateInfo.shortNameHash ↔ Animator.StringToHash(clip名) 匹配
        /// Tier E: legacy Animation 组件 IsPlaying 遍历
        /// Tier F: CubismMotionController 反射（其他游戏用）
        /// Tier G: controller 第一项 placeholder（最后手段）
        /// </summary>
        private static string PollAnimatorMotionName(float t)
        {
            // v16l 修复：删内部 throttle。
            //   原因：Tick L812 入口处已用同一阈值 (MOTION_POLL_INTERVAL=0.2s) 节流并把
            //   lastMotionPollTime=t；这里再判一次 `t - lastMotionPollTime < 0.2` 必然为 0 < 0.2 = TRUE，
            //   → resolver 从未被调用 → cachedMotionName 永远是空 → 音频事件 motion= 一直空。
            //   CaptureCurrentFrame() 虽也调用本函数，但它是 dead code（无调用方），不会受影响。
            // v16l：每 50 次调用打一次 entry 日志（确认 PollAnimatorMotionName 真的在跑、resolver 真被调）
            pollCallCount++;
            if (pollCallCount == 1 || pollCallCount % 50 == 0)
            {
                ExplorerCore.Log("[Live2DRecorder] v16l PollAnimatorMotionName #" + pollCallCount +
                    " t=" + t.ToString("F2") + "s animatorComp=" + (animatorComp != null ? "✓" : "✗") +
                    " invokeTarget=" + (animatorInvokeTarget != null ? "✓" : "✗"));
            }

            string result = ResolveMotionNameV16j(out string via, out string diag, out bool reliable);
            lastMotionReliable = reliable;   // v16aj：供 UpdateMotionOnlyViaAnimator 判定「是否真在播」

            if (string.IsNullOrEmpty(result))
            {
                // v16k：resolver 全 null 时打一次诊断（之后只有 audio 事件时才打，避免日志洪水）
                if (!motionNullDiagLogged && animatorInvokeTarget != null)
                {
                    motionNullDiagLogged = true;
#if ML073
                    int hookCount = AnimatorPlayHook.GetTotalRequests(1.5f);
                    string hookInfo = "hook请求=" + hookCount;
#else
                    string hookInfo = "hook=未启用(非ML073)";
#endif
                    ExplorerCore.LogWarning("[Live2DRecorder] v16l resolver 全 null 诊断: animatorComp=" +
                        (animatorComp != null ? animatorComp.GetType().FullName : "null") +
                        ", invokeTarget=" + (animatorInvokeTarget != null ? animatorInvokeTarget.GetType().FullName : "null") +
                        ", hookAttempted=" + animatorHookInstallAttempted +
                        ", " + hookInfo + ", tier详情: " + diag);
                }
                return cachedMotionName;
            }

            // v16aj：占位名（tier G）打一次诊断，说明「当前绑定模型其实没在播动画」
            if (!reliable && !placeholderDiagLogged)
            {
                placeholderDiagLogged = true;
                ExplorerCore.LogWarning("[Live2DRecorder] v16aj 当前绑定模型未在播动画，仅得到占位名 '" +
                    result + "' (via " + via + ") → 视为无效，将尝试换绑到正在播放的模型 | " + diag);
            }

            // v16j 诊断：首次解析成功 + 每次名字变化（切段）时打全 Tier 结果
            // v16as：加去重 —— 不可靠结果下 cachedMotionName 恒为 null，旧条件 `result != cachedMotionName`
            //   永远成立 → 每个轮询周期打一行相同的占位名（标题↔H 场景切换那 4 秒刷了 14 行）。
            //   新条件：同 (result, via) 组合只打一次 / 可靠↔不可靠翻转必打 / 30s 心跳兜底。
            bool diagSame = (result == lastMotionDiagResult && via == lastMotionDiagVia
                             && reliable == lastMotionDiagReliable);
            bool diagHeartbeat = (t - lastMotionDiagTime) >= MOTION_DIAG_HEARTBEAT;
            if (!motionDiagLogged || !diagSame || diagHeartbeat)
            {
                motionDiagLogged = true;
                lastMotionDiagResult = result;
                lastMotionDiagVia = via;
                lastMotionDiagReliable = reliable;
                lastMotionDiagTime = t;
                ExplorerCore.Log("[Live2DRecorder] v16j motion 解析 → '" + result + "' (via " + via + ") | " + diag);
            }
            lastMotionVia = via;
            return result;
        }

        /// <summary>
        /// v16j 多级解析：返回最佳动画名。via=命中的 Tier；diag=全部 Tier 结果（诊断用）；
        /// reliable=v16aj 新增：是否「真的解析到当前正在播放的动画」。
        ///
        /// ★ 为什么需要 reliable：Tier G 是「取 runtimeAnimatorController.animationClips[0]」的**占位名**，
        ///   与「此刻在播什么」毫无关系。旧逻辑把它当成有效动画名返回，导致绑到一个空闲 Animator 上时
        ///   永远解析出同一个名字（本游戏 = Standing_Idol），于是：
        ///     1) 失联/换绑判定永远不触发（名字非空）；
        ///     2) 动画段被无限延长 → 所有音频都归到那一个动画。
        ///   所以 tier G 与未解析的 tier A("#hash") 一律标记为不可靠。
        /// </summary>
        private static string ResolveMotionNameV16j(out string via, out string diag, out bool reliable)
        {
            via = null;
            string tierA = null, tierAMethod = null, tierC = null, tierD = null, tierE = null, tierF = null, tierG = null;

#if ML073
            // ---- Tier A: Harmony hook 播放请求（仅 ML 0.7 Il2CppInterop）----
            try
            {
                tierA = AnimatorPlayHook.GetRecentName(animatorComp, 1.5f, out tierAMethod);
                if (string.IsNullOrEmpty(tierA))
                {
                    // 诊断：游戏是否在向别的 Animator 发请求
                    AnimatorPlayHook.GetOtherAnimatorRecentRequest(AnimatorPlayHook.GetPointer(animatorComp), 1.5f);
                }
                else if (tierA.StartsWith("#"))
                {
                    // hash 请求 → 尝试解析成 clip 名
                    int hash;
                    if (int.TryParse(tierA.Substring(1), out hash))
                    {
                        string matched = MatchHashToClipName(hash, GetAllControllerClipNames());
                        if (!string.IsNullOrEmpty(matched)) tierA = matched;
                    }
                }
            }
            catch { }
#endif
            // ---- Tier C: clipInfo 加权 + transition 感知 ----
            try { tierC = ResolveViaClipInfoWeighted(); } catch { }
            // ---- Tier D: stateInfo hash 匹配 ----
            try { tierD = ResolveViaStateInfoHash(); } catch { }
            // ---- Tier E: legacy Animation 组件 ----
            try { tierE = ResolveViaLegacyAnimation(); } catch { }
            // ---- Tier F: CubismMotionController ----
            try { tierF = ResolveMotionControllerName(); } catch { }
            // ---- Tier G: controller 第一项 placeholder ----
            try { tierG = TryGetFirstControllerClipName(); } catch { }

            // 决策（Tier A 的 "#hash" 未解析成名字时降级到 C/D 之后）
            string chosen = null;
            bool aUsable = !string.IsNullOrEmpty(tierA) && !tierA.StartsWith("#");
            if (aUsable) { via = "A:" + tierAMethod; chosen = tierA; }
            else if (!string.IsNullOrEmpty(tierC)) { via = "C:clipInfo加权"; chosen = tierC; }
            else if (!string.IsNullOrEmpty(tierD)) { via = "D:stateHash"; chosen = tierD; }
            else if (!string.IsNullOrEmpty(tierE)) { via = "E:legacy"; chosen = tierE; }
            else if (!string.IsNullOrEmpty(tierF)) { via = "F:cubism"; chosen = tierF; }
            else if (!string.IsNullOrEmpty(tierA)) { via = "A:hash原始"; chosen = tierA; }
            else if (!string.IsNullOrEmpty(tierG)) { via = "G:placeholder"; chosen = tierG; }

            diag = "A=" + (tierA ?? "-") + " C=" + (tierC ?? "-") + " D=" + (tierD ?? "-") +
                   " E=" + (tierE ?? "-") + " F=" + (tierF ?? "-") + " G=" + (tierG ?? "-");

            // v16aj：只有「从正在播放的状态里取到的名字」才算可靠
            reliable = !string.IsNullOrEmpty(chosen) && via != "G:placeholder" && via != "A:hash原始";
            return chosen;
        }

        /// <summary>安全反射调用（失败返回 null，不抛）。</summary>
        private static object InvokeOn(MethodInfo mi, object target, params object[] args)
        {
            if (mi == null) return null;
            try { return mi.Invoke(target, args); }
            catch { return null; }
        }

        /// <summary>
        /// Tier C：GetCurrentAnimatorClipInfo 按 weight 加权取最大；transition 期间优先
        /// GetNextAnimatorClipInfo（incoming 动画 —— 切段瞬间就能看到新名字，不用等淡入完成）。
        /// </summary>
        private static string ResolveViaClipInfoWeighted()
        {
            if (animatorInvokeTarget == null || animatorGetCurrentClipInfo == null) return null;
            string best = null;
            for (int layer = 0; layer <= 3; layer++)
            {
                // transition 检测
                bool inTransition = false;
                object itRes = InvokeOn(animatorIsInTransition, animatorInvokeTarget, layer);
                if (itRes is bool) inTransition = (bool)itRes;

                // incoming 动画（transition 期间的新 clip）
                string nextName = null;
                if (inTransition && animatorGetNextClipInfo != null)
                {
                    object nextArr = InvokeOn(animatorGetNextClipInfo, animatorInvokeTarget, layer);
                    float nw;
                    BestClipFromInfoArray(nextArr, out nextName, out nw);
                }

                // 当前动画（按 weight 加权）
                object curArr = InvokeOn(animatorGetCurrentClipInfo, animatorInvokeTarget, layer);
                string curName = null;
                float curW;
                BestClipFromInfoArray(curArr, out curName, out curW);

                // 有数据的层：transition 时 incoming 优先，否则当前加权最大者
                if (!string.IsNullOrEmpty(nextName)) return nextName;
                if (!string.IsNullOrEmpty(curName)) { best = curName; break; }

                // 本层无数据 → 继续扫下一层（有些游戏用非 0 层播 motion）
            }
            return best;
        }

        /// <summary>从 AnimatorClipInfo[] 提取 (weight 最大的 clip 名, weight)。失败 name=null。</summary>
        private static void BestClipFromInfoArray(object arr, out string name, out float weight)
        {
            name = null;
            weight = -1f;
            if (arr == null) return;
            foreach (object info in EnumerateAny(arr))
            {
                if (info == null) continue;
                float w = -1f;
                try
                {
                    object wObj = AssetExporter.GetFieldOrProp(info, "weight", "m_Weight");
                    if (wObj is float) w = (float)wObj;
                    else if (wObj != null) w = Convert.ToSingle(wObj);
                }
                catch { }
                object clip = AssetExporter.GetFieldOrProp(info, "clip", "m_Clip");
                string n = ExtractClipName(clip);
                if (!string.IsNullOrEmpty(n) && w > weight)
                {
                    name = n;
                    weight = w;
                }
            }
        }

        /// <summary>
        /// Tier D：GetCurrentAnimatorStateInfo / GetNextAnimatorStateInfo 的 shortNameHash
        /// 与 Animator.StringToHash(controller 里每个 clip 名) 匹配（state 名通常与 clip 名一致）。
        /// transition 期间先试 next hash（incoming），再试 current。
        /// </summary>
        private static string ResolveViaStateInfoHash()
        {
            if (animatorInvokeTarget == null || animatorGetCurrentStateInfo == null) return null;
            List<string> clipNames = GetAllControllerClipNames();
            if (clipNames.Count == 0) return null;

            for (int layer = 0; layer <= 1; layer++)
            {
                foreach (MethodInfo mi in new[] { animatorGetNextStateInfo, animatorGetCurrentStateInfo })
                {
                    if (mi == null) continue;
                    object st = InvokeOn(mi, animatorInvokeTarget, layer);
                    if (st == null) continue;
                    object hObj = AssetExporter.GetFieldOrProp(st, "shortNameHash", "m_ShortNameHash");
                    if (hObj == null) continue;
                    int hash;
                    try { hash = Convert.ToInt32(hObj); }
                    catch { continue; }
                    string m = MatchHashToClipName(hash, clipNames);
                    if (!string.IsNullOrEmpty(m)) return m;
                }
            }
            return null;
        }

        /// <summary>hash ↔ clip 名匹配（Animator.StringToHash 比对）。</summary>
        private static string MatchHashToClipName(int hash, List<string> clipNames)
        {
            if (animatorStringToHash == null || clipNames == null) return null;
            foreach (string n in clipNames)
            {
                object r = InvokeOn(animatorStringToHash, null, n);
                if (r != null)
                {
                    try { if (Convert.ToInt32(r) == hash) return n; }
                    catch { }
                }
            }
            return null;
        }

        /// <summary>controller 全部 clip 名列表（剥 .anim 后缀）。Tier D/G 共用。</summary>
        private static List<string> GetAllControllerClipNames()
        {
            var list = new List<string>();
            try
            {
                if (animatorComp == null) return list;
                object rac = AssetExporter.GetFieldOrProp(animatorComp,
                    "runtimeAnimatorController", "m_RuntimeAnimatorController");
                if (rac == null) return list;
                object clipsObj = AssetExporter.GetFieldOrProp(rac,
                    "animationClips", "m_AnimationClips");
                if (clipsObj == null) return list;
                foreach (object c in EnumerateAny(clipsObj))
                {
                    string n = AssetExporter.GetFieldOrProp(c, "name") as string;
                    if (string.IsNullOrEmpty(n)) continue;
                    if (n.EndsWith(".anim", StringComparison.OrdinalIgnoreCase))
                        n = n.Substring(0, n.Length - 5);
                    list.Add(n);
                }
            }
            catch { }
            return list;
        }

        /// <summary>
        /// Tier E：legacy Animation 组件（老 Cubism 工程用 Animation 播 .anim）。
        /// 遍历 AnimationState，IsPlaying(name) 命中即返回。挂在 Animator 同物体或模型根上。
        /// </summary>
        private static string ResolveViaLegacyAnimation()
        {
            try
            {
                if (animatorComp == null) return null;
                // 候选物体：Animator 同物体 + 追踪的模型根
                var candidates = new List<GameObject>();
                try { candidates.Add(animatorComp.gameObject); } catch { }
                if (trackedRoots.Count > 0 && trackedRoots[0] != null)
                    candidates.Add(trackedRoots[0]);

                foreach (GameObject host in candidates)
                {
                    if (host == null) continue;
                    object anim = null;
                    try
                    {
                        Component[] comps = host.GetComponents<Component>();
                        foreach (Component c in comps)
                        {
                            if (c == null) continue;
                            string tn = AssetExporter.GetComponentTypeName(c);
                            if (tn == "Animation" || tn.EndsWith(".Animation") || tn.EndsWith("UnityEngine.Animation"))
                            {
                                anim = c;
                                break;
                            }
                        }
                    }
                    catch { }
                    if (anim == null) continue;

                    // isPlaying 检查
                    object playing = AssetExporter.GetFieldOrProp(anim, "isPlaying");
                    if (playing is bool && !(bool)playing) continue;

                    // 遍历 AnimationState（Animation 实现 IEnumerable）+ IsPlaying(name)
                    System.Type animType = anim.GetType();
                    MethodInfo isPlayingM = animType.GetMethod("IsPlaying", new System.Type[] { typeof(string) });
                    MethodInfo ge = animType.GetMethod("GetEnumerator", System.Type.EmptyTypes);
                    if (isPlayingM == null || ge == null) continue;
                    object en = ge.Invoke(anim, null);
                    if (en == null) continue;
                    System.Type ent = en.GetType();
                    MethodInfo moveNext = ent.GetMethod("MoveNext", System.Type.EmptyTypes);
                    PropertyInfo current = ent.GetProperty("Current");
                    if (moveNext == null || current == null) continue;
                    int guard = 0;
                    while ((bool)moveNext.Invoke(en, null) && guard++ < 256)
                    {
                        object s = current.GetValue(en, null);
                        if (s == null) continue;
                        string sn = AssetExporter.GetFieldOrProp(s, "name") as string;
                        if (string.IsNullOrEmpty(sn)) continue;
                        object ip = isPlayingM.Invoke(anim, new object[] { sn });
                        if (ip is bool && (bool)ip)
                        {
                            if (sn.EndsWith(".anim", StringComparison.OrdinalIgnoreCase))
                                sn = sn.Substring(0, sn.Length - 5);
                            return sn;
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>从 AnimatorClipInfo.clip 提取名称（剥 .anim 后缀）。失败返回 null。</summary>
        private static string ExtractClipName(object clip)
        {
            if (clip == null) return null;
            string n = AssetExporter.GetFieldOrProp(clip, "name") as string;
            if (string.IsNullOrEmpty(n)) return null;
            if (n.EndsWith(".anim", StringComparison.OrdinalIgnoreCase))
                n = n.Substring(0, n.Length - 5);
            return n;
        }

        /// <summary>
        /// 从 Animator.runtimeAnimatorController.animationClips 拿 controller 内嵌的全部 clip 名。
        /// 返回第一个非空名字。仅在 GetCurrentAnimatorClipInfo 全空时兜底用，
        /// 因为该返回值未必是当前播放的 clip，但能给 audio 事件一个 motion 标签。
        /// </summary>
        private static string TryGetFirstControllerClipName()
        {
            try
            {
                if (animatorComp == null) return null;
                object rac = AssetExporter.GetFieldOrProp(animatorComp,
                    "runtimeAnimatorController", "m_RuntimeAnimatorController");
                if (rac == null) return null;
                object clipsObj = AssetExporter.GetFieldOrProp(rac,
                    "animationClips", "m_AnimationClips");
                if (clipsObj == null) return null;
                foreach (object c in EnumerateAny(clipsObj))
                {
                    string n = AssetExporter.GetFieldOrProp(c, "name") as string;
                    if (!string.IsNullOrEmpty(n))
                    {
                        if (n.EndsWith(".anim", StringComparison.OrdinalIgnoreCase))
                            n = n.Substring(0, n.Length - 5);
                        return n;
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 通用枚举：托管数组 → IEnumerable → Il2CppSystem.Array → GetEnumerator 反射。
        /// 兼容 AnimationClip[]（Mono）、Il2CppStructArray&lt;AnimatorClipInfo&gt;（Interop）、
        /// Il2CppReferenceArray（Unhollower）等返回形态。
        /// </summary>
        private static List<object> EnumerateAny(object val)
        {
            List<object> list = new List<object>();
            if (val == null) return list;
            try
            {
                System.Array arr = val as System.Array;
                if (arr != null)
                {
                    foreach (object o in arr) if (o != null) list.Add(o);
                    return list;
                }
            }
            catch { }
            try
            {
                System.Collections.IEnumerable en = val as System.Collections.IEnumerable;
                if (en != null)
                {
                    foreach (object o in en) if (o != null) list.Add(o);
                    return list;
                }
            }
            catch { }
#if CPP
            try
            {
                Il2CppSystem.Array a = val.TryCast<Il2CppSystem.Array>();
                if (a != null)
                {
                    int len = a.Length;
                    for (int i = 0; i < len; i++)
                    {
                        object o = a.GetValue(i);
                        if (o != null) list.Add(o);
                    }
                    return list;
                }
            }
            catch { }
#endif
            // GetEnumerator 反射（覆盖 IEnumerable<T> 代理）
            try
            {
                MethodInfo gm = val.GetType().GetMethod("GetEnumerator", System.Type.EmptyTypes);
                if (gm != null)
                {
                    object en = gm.Invoke(val, null);
                    if (en != null)
                    {
                        System.Type ent = en.GetType();
                        MethodInfo moveNext = ent.GetMethod("MoveNext", System.Type.EmptyTypes);
                        PropertyInfo current = ent.GetProperty("Current");
                        int guard = 0;
                        while (moveNext != null && current != null && guard++ < 10000)
                        {
                            bool ok = false;
                            try { ok = (bool)moveNext.Invoke(en, null); }
                            catch { break; }
                            if (!ok) break;
                            object cur = current.GetValue(en, null);
                            if (cur != null) list.Add(cur);
                        }
                    }
                }
            }
            catch { }
            return list;
        }

        // ============= segment 维护（v14：只在真实 motion 名变化时切分；v16 dead code）=============
        //   v15 之前由 RecorderUpdate 每帧调用。v16 改为 Tick 只查 audio + animator 切段
        //   （UpdateMotionOnlyViaAnimator），不再维护 segments 由 param/drawable 变化统计。
        //   函数签名保留以避免破坏其他可能仍然引用它的 hack，但函数体在 v16 已 no-op。
        private static void UpdateSegments(FrameSnapshot snap, int frameIdx)
        {
            // v16: 切段检测移交给 UpdateMotionOnlyViaAnimator(t)。本函数保留为 no-op。
            // v15e 旧逻辑（写在 git history）：比较 snap.MotionName 与 segments 末尾，
            //     不同时 new segment + 增量 RecordedMotionCount；
            //     同时统计 param/drawable 偏离默认值的 peak 到 segment 上。
            // v16 删除：因为 v16 不再每帧 CaptureCurrentFrame，segments 仅由 animator 切段驱动。
        }

        // 上一个 motion 名（保留供 v14/v15 老逻辑引用，v16 实际由 UpdateMotionOnlyViaAnimator 直接维护）
        private static string _legacy_cachedMotionName
        {
            get { return cachedMotionName; }
        }

        // v16: v15e 旧 UpdateSegments 函数体已 no-op，下面是原 LogParameterChanges/LogVisualChanges 函数。
        // 它们原本在 UpdateSegments 末尾由 ParamValues/VisualOpacities 变化触发——v16 Tick 不再维护，
        // 因此不再调用。本节保留 v15e 实现以备未来 v17 重新启用 param 时序追踪时复用。
        private static void LogParameterChanges(FrameSnapshot prev, FrameSnapshot cur)
        {
            if (cur.ParamValues == null) return;
            int prevLen = prev.ParamValues != null ? prev.ParamValues.Length : 0;
            for (int i = 0; i < cur.ParamValues.Length; i++)
            {
                float oldv = i < prevLen ? prev.ParamValues[i] : trackedParams[i].DefaultValue;
                float pv = cur.ParamValues[i];
                if (System.Math.Abs(pv - oldv) > KEYFRAME_THRESHOLD)
                {
                    changeLog.Add(new ChangeLogEntry
                    {
                        Time = cur.Time,
                        MotionName = cur.MotionName,
                        Kind = "Parameter",
                        Id = trackedParams[i].Id,
                        OldValue = oldv.ToString("F4"),
                        NewValue = pv.ToString("F4"),
                    });
                }
            }
        }

        private static void LogVisualChanges(FrameSnapshot prev, FrameSnapshot cur)
        {
            if (cur.VisualOpacities == null) return;
            int prevLen = prev.VisualOpacities != null ? prev.VisualOpacities.Length : 0;
            for (int i = 0; i < cur.VisualOpacities.Length && i < trackedVisuals.Count; i++)
            {
                TrackedVisual tv = trackedVisuals[i];
                float op = cur.VisualOpacities[i];
                float oldOp = i < prevLen ? prev.VisualOpacities[i] : 1f;
                bool vis = cur.VisualVisibles != null && i < cur.VisualVisibles.Length ? cur.VisualVisibles[i] : true;
                bool oldVis = prev.VisualVisibles != null && i < prev.VisualVisibles.Length ? prev.VisualVisibles[i] : true;
                if (System.Math.Abs(op - oldOp) > KEYFRAME_THRESHOLD || vis != oldVis)
                {
                    changeLog.Add(new ChangeLogEntry
                    {
                        Time = cur.Time,
                        MotionName = cur.MotionName,
                        Kind = tv.IsPart ? "Part" : "Drawable",
                        Id = tv.Id,
                        OldValue = oldVis ? oldOp.ToString("F4") : "hidden",
                        NewValue = vis ? op.ToString("F4") : "hidden",
                    });
                }
            }
        }

        // ============= 音频事件（v14：全场景扫描 + 重播检测）=============

        /// <summary>
        /// 对象稳定键：CPP 用 Il2Cpp 对象指针，Mono 用 GetInstanceID。
        /// （不用 AudioSource.GetInstanceID —— Unhollower 门面下编译不过）
        /// </summary>
        private static long GetUnityObjectKey(object obj)
        {
            if (obj == null) return 0;
#if CPP
            try
            {
                Il2CppSystem.Object io = obj.TryCast<Il2CppSystem.Object>();
                if (io != null) return io.Pointer.ToInt64();
            }
            catch { }
            return 0;
#else
            try
            {
                UnityEngine.Object uo = obj as UnityEngine.Object;
                if (uo != null) return uo.GetInstanceID();
            }
            catch { }
            return 0;
#endif
        }

        private static float ToFloat(object v, float fallback)
        {
            if (v == null) return fallback;
            try
            {
                if (v is float) return (float)v;
                if (v is double) return (float)(double)v;
                if (v is int) return (float)(int)v;
                if (v is long) return (float)(long)v;
                float f;
                if (float.TryParse(v.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out f))
                    return f;
            }
            catch { }
            return fallback;
        }

        /// <summary>刷新全场景 AudioSource 列表（保留旧播放状态）。</summary>
        private static void RefreshAudioSources()
        {
            Dictionary<long, TrackedAudio> old = new Dictionary<long, TrackedAudio>();
            foreach (TrackedAudio ta in trackedAudios) old[ta.Key] = ta;
            trackedAudios.Clear();

            UnityEngine.Object[] objs = null;
            try
            {
                // UniverseLib 封装：Mono/Unhollower/Interop 全配置可用，
                // 且包含未激活对象（DontDestroyOnLoad 等）
                objs = RuntimeHelper.FindObjectsOfTypeAll(typeof(AudioSource));
            }
            catch (Exception ex)
            {
                if (!audioScanFailedLogged)
                {
                    audioScanFailedLogged = true;
                    ExplorerCore.LogWarning("[Live2DRecorder] 全场景 AudioSource 扫描失败: " + ex.Message);
                }
                return;
            }
            if (objs == null) return;

            foreach (UnityEngine.Object o in objs)
            {
                try
                {
                    if (o == null) continue;
                    Component c = o.TryCast<Component>();
                    if (c == null) continue;
                    long key = GetUnityObjectKey(c);
                    if (key == 0) continue;

                    bool dup = false;
                    foreach (TrackedAudio t in trackedAudios)
                    {
                        if (t.Key == key) { dup = true; break; }
                    }
                    if (dup) continue;

                    TrackedAudio ta = new TrackedAudio();
                    ta.Comp = c;
                    ta.Key = key;
                    try { ta.GoName = c.gameObject != null ? c.gameObject.name : ""; }
                    catch { ta.GoName = ""; }
                    ta.IsPlaying = CubismMemberReader.Create(c, "isPlaying", "IsPlaying", "m_IsPlaying");
                    ta.Clip = CubismMemberReader.Create(c, "clip", "Clip", "m_clip");
                    ta.Time = CubismMemberReader.Create(c, "time");

                    TrackedAudio prev;
                    if (old.TryGetValue(key, out prev))
                    {
                        ta.WasPlaying = prev.WasPlaying;
                        ta.WasTime = prev.WasTime;
                        ta.WasClipKey = prev.WasClipKey;
                    }
                    trackedAudios.Add(ta);
                }
                catch { }
            }
        }

        private static void UpdateAudioEvents(float t)
        {
            try
            {
                // 定期刷新全场景 AudioSource 列表
                if (t - lastAudioScanTime >= AUDIO_SCAN_INTERVAL)
                {
                    lastAudioScanTime = t;
                    RefreshAudioSources();
                    if (!audioScanCountLogged)
                    {
                        audioScanCountLogged = true;
                        ExplorerCore.Log("[Live2DRecorder] 场景 AudioSource 扫描: " + trackedAudios.Count + " 个");
                    }
                }

                // 播放状态检查（0.1s 粒度足够检测播放开始）
                if (t - lastAudioCheckTime < AUDIO_CHECK_INTERVAL) return;
                lastAudioCheckTime = t;

                foreach (TrackedAudio ta in trackedAudios)
                {
                    bool nowPlaying = ta.IsPlaying.ReadBool(false);
                    object clipObj = ta.Clip.Read();
                    long clipKey = GetUnityObjectKey(clipObj);
                    float nowTime = ta.Time.ReadFloat(0f);

                    // 开始播放 / 运行中换 clip / time 回卷（同 clip 重播）
                    bool started = nowPlaying && !ta.WasPlaying;
                    bool restarted = nowPlaying && ta.WasPlaying && clipKey != 0 &&
                        (clipKey != ta.WasClipKey || nowTime < ta.WasTime - 0.15f);

                    if ((started || restarted) && clipObj != null)
                    {
                        string clipName = AssetExporter.GetFieldOrProp(clipObj, "name") as string;
                        if (string.IsNullOrEmpty(clipName)) clipName = "audio_" + clipKey;
                        float clipLen = ToFloat(AssetExporter.GetFieldOrProp(clipObj, "length"), 0f);

                        // v16ae：归属方向反转 —— 「播放动画 → 记录动画期间播放的音频」。
                        //   以动画段时间窗 [StartTime-0.3, EndTime+0.5] 为锚：
                        //   音频开始时必须有一段动画正在播放才归属；无动画在播 → SegmentIndex=-1
                        int segIdx = FindActiveSegmentIndex(t);
                        string motion = segIdx >= 0 ? segments[segIdx].MotionName : "";

                        // v16am：记录触发瞬间「所有激活模型各自在播的动画」，并按模型段挂音频
                        List<string> hits = null;
                        try
                        {
                            hits = FindActiveModelLabels(t);
                            for (int hi = 0; hi < modelSegments.Count; hi++)
                            {
                                MotionSegment ms = modelSegments[hi];
                                if (t >= ms.StartTime - 0.3f && t <= ms.EndTime + 0.5f)
                                {
                                    if (!ms.TriggeredAudios.Contains(clipName))
                                        ms.TriggeredAudios.Add(clipName);
                                }
                            }
                        }
                        catch { }

                        audioEvents.Add(new AudioEvent
                        {
                            Time = t,
                            ClipName = clipName,
                            SourceGoName = ta.GoName,
                            ClipLength = clipLen,
                            TriggeredMotion = motion,
                            SegmentIndex = segIdx,
                            ModelHits = hits ?? new List<string>(),
                        });
                        TriggeredAudioEventCount++;

                        if (segIdx >= 0)
                        {
                            MotionSegment seg = segments[segIdx];
                            if (!seg.TriggeredAudios.Contains(clipName))
                                seg.TriggeredAudios.Add(clipName);
                        }

                        ExplorerCore.Log(string.Format(
                            "[Live2DRecorder] 音频触发: {0} (motion={1}, t={2:F2}s, src={3})",
                            clipName, motion, t, ta.GoName));

                        // v16am：多模型场景下同时打出「每个模型此刻在播什么」，一行看清全部
                        if (hits != null && hits.Count > 0)
                        {
                            ExplorerCore.Log("[Live2DRecorder] v16am 音频归属: " + clipName + " ← " +
                                string.Join(" | ", hits.ToArray()));
                        }
                    }

                    ta.WasPlaying = nowPlaying;
                    ta.WasTime = nowTime;
                    ta.WasClipKey = clipKey;
                }
            }
            catch (Exception ex)
            {
                ExplorerCore.LogWarning("[Live2DRecorder] Audio 事件检测异常: " + ex.Message);
            }
        }

        /// <summary>
        /// v16ae：查找时间 t 正在播放的动画段实例索引（从最新段向前找）。
        /// 窗口：[StartTime - 0.3, EndTime + 0.5]——
        ///   容忍语音比动画早 0.3s 开始、动画段结束判定（animator 轮询粒度）晚 0.5s 内仍算在播。
        /// 返回 -1 表示触发音频时没有任何动画在播放。
        /// </summary>
        private static int FindActiveSegmentIndex(float t)
        {
            for (int i = segments.Count - 1; i >= 0; i--)
            {
                MotionSegment s = segments[i];
                if (t >= s.StartTime - 0.3f && t <= s.EndTime + 0.5f)
                    return i;
            }
            return -1;
        }

        // ============= 导出格式 =============

        private static string BuildManifestJson(string baseName)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine("  \"model\": \"" + EscapeJson(baseName) + "\",");
            sb.AppendLine("  \"duration_sec\": " + (frames.Count > 0 ? frames[frames.Count - 1].Time : 0f).ToString("F3") + ",");
            sb.AppendLine("  \"frame_count\": " + frames.Count + ",");
            sb.AppendLine("  \"param_count\": " + trackedParams.Count + ",");
            sb.AppendLine("  \"visual_count\": " + trackedVisuals.Count + ",");
            sb.AppendLine("  \"motion_count\": " + segments.Count + ",");
            sb.AppendLine("  \"model_motion_count\": " + modelSegments.Count + ",");
            sb.AppendLine("  \"audio_event_count\": " + audioEvents.Count + ",");
            sb.AppendLine("  \"change_log_count\": " + changeLog.Count + ",");
            sb.AppendLine("  \"motions\": [");
            for (int i = 0; i < segments.Count; i++)
            {
                var s = segments[i];
                if (i > 0) sb.AppendLine(",");
                sb.Append("    {");
                sb.Append("\"name\": \"" + EscapeJson(s.MotionName) + "\"");
                sb.Append(", \"start\": " + s.StartTime.ToString("F3"));
                sb.Append(", \"end\": " + s.EndTime.ToString("F3"));
                sb.Append(", \"duration\": " + (s.EndTime - s.StartTime).ToString("F3"));
                sb.Append(", \"start_frame\": " + s.StartFrame);
                sb.Append(", \"end_frame\": " + s.EndFrame);
                sb.Append(", \"param_changes\": " + s.ParameterChangeCount);
                sb.Append(", \"drawable_changes\": " + s.DrawableChangeCount);
                sb.Append(", \"triggered_audios\": [");
                for (int j = 0; j < s.TriggeredAudios.Count; j++)
                {
                    if (j > 0) sb.Append(", ");
                    sb.Append("\"" + EscapeJson(s.TriggeredAudios[j]) + "\"");
                }
                sb.Append("]");
                sb.Append("}");
            }
            sb.AppendLine();
            sb.AppendLine("  ],");
            sb.AppendLine("  \"audio_events\": [");
            for (int i = 0; i < audioEvents.Count; i++)
            {
                var ae = audioEvents[i];
                if (i > 0) sb.AppendLine(",");
                sb.Append("    {");
                sb.Append("\"time\": " + ae.Time.ToString("F3"));
                sb.Append(", \"clip\": \"" + EscapeJson(ae.ClipName) + "\"");
                sb.Append(", \"source\": \"" + EscapeJson(ae.SourceGoName) + "\"");
                sb.Append(", \"length\": " + ae.ClipLength.ToString("F3"));
                sb.Append(", \"motion_at_trigger\": \"" + EscapeJson(ae.TriggeredMotion ?? "") + "\"");
                sb.Append("}");
            }
            sb.AppendLine();
            sb.AppendLine("  ]");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private static string BuildMotionAudioCsv(string baseName)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("# Live2D Recordings CSV (model=" + baseName + ")");
            sb.AppendLine("# format: 每行一条 motion→audio 触发配对");
            sb.AppendLine("motion,motion_start_sec,motion_end_sec,audio_clip,audio_trigger_time_sec,audio_source");

            foreach (var ae in audioEvents)
            {
                string motion = ae.TriggeredMotion ?? "";
                sb.Append(EscapeCsv(motion));
                sb.Append(",");

                float ms = 0f, me = 0f;
                foreach (var s in segments)
                {
                    if (s.MotionName == motion) { ms = s.StartTime; me = s.EndTime; break; }
                }
                sb.Append(ms.ToString("F3"));
                sb.Append(",");
                sb.Append(me.ToString("F3"));
                sb.Append(",");
                sb.Append(EscapeCsv(ae.ClipName));
                sb.Append(",");
                sb.Append(ae.Time.ToString("F3"));
                sb.Append(",");
                sb.Append(EscapeCsv(ae.SourceGoName));
                sb.AppendLine();
            }

            foreach (var s in segments)
            {
                bool hasAudio = false;
                foreach (var ae in audioEvents)
                {
                    if (ae.TriggeredMotion == s.MotionName) { hasAudio = true; break; }
                }
                if (!hasAudio)
                {
                    sb.Append(EscapeCsv(s.MotionName));
                    sb.Append(",");
                    sb.Append(s.StartTime.ToString("F3"));
                    sb.Append(",");
                    sb.Append(s.EndTime.ToString("F3"));
                    sb.Append(",,");
                    sb.Append(s.EndTime.ToString("F3"));
                    sb.AppendLine(",(no_audio)");
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// v16am：每模型动画轨 CSV —— 直接回答「每个模型播放什么动画、每个动画播放什么音频」。
        /// 列：model,motion,start_sec,end_sec,audio_count,audios(用 | 分隔)
        /// 多模型场景（本游戏 H 场景同时 4 个模型在播）下，每个模型各占若干行。
        /// </summary>
        private static string BuildModelMotionsCsv(string baseName)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("# Live2D per-model motion tracks (v16am)  model=" + baseName);
            sb.AppendLine("# 一行 = 某个模型上的一段连续动画；audios 为该动画期间播放的音频 clip 名");
            sb.AppendLine("model,motion,motion_start_sec,motion_end_sec,audio_count,audios");

            for (int i = 0; i < modelSegments.Count; i++)
            {
                MotionSegment s = modelSegments[i];
                sb.Append(EscapeCsv(s.RootName ?? ""));
                sb.Append(",");
                sb.Append(EscapeCsv(s.MotionName ?? ""));
                sb.Append(",");
                sb.Append(s.StartTime.ToString("F3"));
                sb.Append(",");
                sb.Append(s.EndTime.ToString("F3"));
                sb.Append(",");
                sb.Append(s.TriggeredAudios.Count);
                sb.Append(",");
                sb.Append(EscapeCsv(string.Join("|", s.TriggeredAudios.ToArray())));
                sb.AppendLine();
            }
            return sb.ToString();
        }

        private static string BuildMotionAudioMapTxt(string baseName)
        {
            // v16c 用户要求：固定文件名 "音频记录.txt"，内容只有一行一条，
            //   格式 "动画：动画名称→音频名称"（多个音频则连续写多行，每行一个组合）。
            // v16ae：归属方向反转 —— 「播放动画 → 记录动画期间播放的音频」。
            //   音频按段窗口归属；触发时无动画在播的音频单独列为 (无动画)。
            // v16am：★ 改用「每个模型各一条动画轨」输出，动画名写成 "模型/动画"。
            //   旧的主绑定轨（segments）在任何多模型场景下都只会包含 1 个动画 ——
            //   用户反馈的「一个场景还是只有一个动画」就是它造成的。
            //   modelSegments 为空（纯单模型游戏/尚未轮询到）时自动退回旧的单轨输出。
            StringBuilder sb = new StringBuilder();

            bool byModel = modelSegments.Count > 0;
            System.Collections.Generic.List<MotionSegment> src = byModel ? modelSegments : segments;

            for (int i = 0; i < src.Count; i++)
            {
                MotionSegment s = src[i];
                string label = byModel ? s.Label : s.MotionName;   // Label = "模型/动画"
                if (s.TriggeredAudios.Count == 0)
                {
                    sb.Append("动画：").Append(label).AppendLine("→(无音频)");
                }
                else
                {
                    for (int k = 0; k < s.TriggeredAudios.Count; k++)
                    {
                        sb.Append("动画：").Append(label).Append("→").AppendLine(s.TriggeredAudios[k]);
                    }
                }
            }

            // 没有任何模型在播时触发的音频（避免数据丢失，单独标注，不混入任何动画名下）
            for (int i = 0; i < audioEvents.Count; i++)
            {
                AudioEvent ae = audioEvents[i];
                bool noMotion = byModel
                    ? (ae.ModelHits == null || ae.ModelHits.Count == 0)
                    : (ae.SegmentIndex < 0);
                if (noMotion)
                {
                    sb.Append("动画：(无动画)").Append("→").AppendLine(ae.ClipName);
                }
            }

            return sb.ToString();
        }

        private static string BuildChangeLogTxt(string baseName)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("# Live2D 录制变化点日志 (model=" + baseName + ")");
            sb.AppendLine("# 格式: [时间(s)] [动画名] [类型] [ID]: 旧值 -> 新值");
            sb.AppendLine();

            foreach (var e in changeLog)
            {
                sb.Append("[").Append(e.Time.ToString("F3")).Append("s] ");
                sb.Append("[").Append(e.MotionName).Append("] ");
                sb.Append("[").Append(e.Kind).Append("] ");
                sb.Append(e.Id).Append(": ").Append(e.OldValue).Append(" -> ").AppendLine(e.NewValue);
            }

            return sb.ToString();
        }

        private static string BuildParamJsonl(string baseName)
        {
            StringBuilder sb = new StringBuilder();
            foreach (var f in frames)
            {
                sb.Append("{\"t\":");
                sb.Append(f.Time.ToString("F3"));
                sb.Append(",\"motion\":\"");
                sb.Append(EscapeJson(f.MotionName ?? ""));
                sb.Append("\",\"params\":{");
                if (f.ParamValues != null)
                {
                    for (int i = 0; i < f.ParamValues.Length && i < trackedParams.Count; i++)
                    {
                        if (i > 0) sb.Append(",");
                        sb.Append("\"").Append(EscapeJson(trackedParams[i].Id)).Append("\":");
                        sb.Append(f.ParamValues[i].ToString("F4"));
                    }
                }
                sb.Append("},\"visuals\":{");
                if (f.VisualOpacities != null)
                {
                    for (int i = 0; i < f.VisualOpacities.Length && i < trackedVisuals.Count; i++)
                    {
                        if (i > 0) sb.Append(",");
                        sb.Append("\"").Append(EscapeJson(trackedVisuals[i].Id)).Append("\":");
                        sb.Append(f.VisualVisibles != null && i < f.VisualVisibles.Length && !f.VisualVisibles[i]
                            ? "0"
                            : f.VisualOpacities[i].ToString("F4"));
                    }
                }
                sb.Append("}}\n");
            }
            return sb.ToString();
        }

        private static string BuildMotion3Json(MotionSegment seg)
        {
            StringBuilder sb = new StringBuilder();
            float dur = System.Math.Max(0.001f, seg.EndTime - seg.StartTime);

            // 收集所有 Parameter 关键帧
            var paramCurves = new Dictionary<string, List<KeyValuePair<float, float>>>();
            CollectParameterKeyframes(seg, dur, paramCurves);

            // 收集 Part/Drawable 关键帧
            var partCurves = new Dictionary<string, List<KeyValuePair<float, float>>>();
            var drawableCurves = new Dictionary<string, List<KeyValuePair<float, float>>>();
            CollectVisualKeyframes(seg, dur, partCurves, drawableCurves);

            int totalCurveCount = paramCurves.Count + partCurves.Count + drawableCurves.Count;
            int totalSegmentCount = 0;
            int totalPointCount = 0;
            foreach (var kvp in paramCurves) { totalSegmentCount++; totalPointCount += kvp.Value.Count; }
            foreach (var kvp in partCurves) { totalSegmentCount++; totalPointCount += kvp.Value.Count; }
            foreach (var kvp in drawableCurves) { totalSegmentCount++; totalPointCount += kvp.Value.Count; }

            sb.AppendLine("{");
            sb.AppendLine("  \"Version\": 3,");
            sb.AppendLine("  \"Meta\": {");
            sb.AppendLine("    \"Duration\": " + dur.ToString("F4") + ",");
            sb.AppendLine("    \"Fps\": 30.0,");
            sb.AppendLine("    \"Loop\": false,");
            sb.AppendLine("    \"AreBeziersRestricted\": false,");
            sb.AppendLine("    \"CurveCount\": " + totalCurveCount + ",");
            sb.AppendLine("    \"TotalSegmentCount\": " + totalSegmentCount + ",");
            sb.AppendLine("    \"TotalPointCount\": " + totalPointCount + ",");
            sb.AppendLine("    \"UserDataCount\": 0,");
            sb.AppendLine("    \"MetadataSource\": \"Live2DRecorder_RuntimeRecording\"");
            sb.AppendLine("  },");
            sb.AppendLine("  \"Curves\": [");

            int curveIdx = 0;
            foreach (var curve in paramCurves)
            {
                if (curveIdx > 0) sb.AppendLine(",");
                AppendCurve(sb, "Parameter", curve.Key, curve.Value);
                curveIdx++;
            }
            foreach (var curve in partCurves)
            {
                if (curveIdx > 0) sb.AppendLine(",");
                AppendCurve(sb, "PartOpacity", curve.Key, curve.Value);
                curveIdx++;
            }
            foreach (var curve in drawableCurves)
            {
                if (curveIdx > 0) sb.AppendLine(",");
                AppendCurve(sb, "Part", curve.Key, curve.Value);
                curveIdx++;
            }

            sb.AppendLine();
            sb.AppendLine("  ]");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private static void CollectParameterKeyframes(MotionSegment seg, float duration,
            Dictionary<string, List<KeyValuePair<float, float>>> outCurves)
        {
            if (trackedParams.Count == 0) return;
            for (int i = seg.StartFrame; i <= seg.EndFrame && i < frames.Count; i++)
            {
                var f = frames[i];
                if (f.ParamValues == null) continue;
                float rt = f.Time - seg.StartTime;
                if (rt < 0f) rt = 0f;
                if (rt > (seg.EndTime - seg.StartTime)) rt = seg.EndTime - seg.StartTime;

                for (int j = 0; j < f.ParamValues.Length && j < trackedParams.Count; j++)
                {
                    string pid = trackedParams[j].Id;
                    float v = f.ParamValues[j];
                    List<KeyValuePair<float, float>> list;
                    if (!outCurves.TryGetValue(pid, out list))
                    {
                        list = new List<KeyValuePair<float, float>>();
                        list.Add(new KeyValuePair<float, float>(rt, v));
                        outCurves[pid] = list;
                    }
                    else
                    {
                        float lastV = list[list.Count - 1].Value;
                        if (System.Math.Abs(v - lastV) > KEYFRAME_THRESHOLD || i == seg.EndFrame)
                            list.Add(new KeyValuePair<float, float>(rt, v));
                    }
                }
            }

            foreach (var kvp in outCurves)
            {
                var list = kvp.Value;
                if (list.Count == 0) continue;
                if (list.Count == 1)
                    list.Add(new KeyValuePair<float, float>(duration, list[0].Value));
            }
        }

        private static void CollectVisualKeyframes(MotionSegment seg, float duration,
            Dictionary<string, List<KeyValuePair<float, float>>> outPartCurves,
            Dictionary<string, List<KeyValuePair<float, float>>> outDrawableCurves)
        {
            if (trackedVisuals.Count == 0) return;
            for (int i = seg.StartFrame; i <= seg.EndFrame && i < frames.Count; i++)
            {
                var f = frames[i];
                if (f.VisualOpacities == null) continue;
                float rt = f.Time - seg.StartTime;
                if (rt < 0f) rt = 0f;
                if (rt > (seg.EndTime - seg.StartTime)) rt = seg.EndTime - seg.StartTime;

                for (int j = 0; j < f.VisualOpacities.Length && j < trackedVisuals.Count; j++)
                {
                    TrackedVisual tv = trackedVisuals[j];
                    var outCurves = tv.IsPart ? outPartCurves : outDrawableCurves;
                    float v = f.VisualVisibles != null && j < f.VisualVisibles.Length && !f.VisualVisibles[j]
                        ? 0f
                        : f.VisualOpacities[j];
                    List<KeyValuePair<float, float>> list;
                    if (!outCurves.TryGetValue(tv.Id, out list))
                    {
                        list = new List<KeyValuePair<float, float>>();
                        list.Add(new KeyValuePair<float, float>(rt, v));
                        outCurves[tv.Id] = list;
                    }
                    else
                    {
                        float lastV = list[list.Count - 1].Value;
                        if (System.Math.Abs(v - lastV) > KEYFRAME_THRESHOLD || i == seg.EndFrame)
                            list.Add(new KeyValuePair<float, float>(rt, v));
                    }
                }
            }

            EnsureTailKeyframe(outPartCurves, duration);
            EnsureTailKeyframe(outDrawableCurves, duration);
        }

        private static void EnsureTailKeyframe(Dictionary<string, List<KeyValuePair<float, float>>> curves, float duration)
        {
            foreach (var kvp in curves)
            {
                var list = kvp.Value;
                if (list.Count == 0) continue;
                if (list.Count == 1)
                    list.Add(new KeyValuePair<float, float>(duration, list[0].Value));
            }
        }

        private static void AppendCurve(StringBuilder sb, string target, string id, List<KeyValuePair<float, float>> series)
        {
            sb.Append("    {");
            sb.Append("\"Target\": \"" + EscapeJson(target) + "\"");
            sb.Append(", \"Id\": \"" + EscapeJson(id) + "\"");
            sb.Append(", \"Segments\": [");

            // v15d 修复：运动 3.json v3 Segment 格式 = [t0, v0, INTERP, t1, v1, t2, v2, ...]
            // INTERP 只在每段开头写一次（0=线性 1=贝塞尔 2=阶梯 3=反向阶梯）。
            // 老版本每个 keyframe 后都加 ",0"，被 CubismViewer5 视为非法 Spacing。
            int n = series.Count;
            if (n == 1)
            {
                // 单点：按 [0, v, 0, dur, v] 5 浮点输出（CubismViewer5 也接受 3 浮点常量段 [0, v, 0]）
                float v0 = series[0].Value;
                sb.Append("0.0000, ").Append(v0.ToString("F4")).Append(", 0");
            }
            else if (n >= 2)
            {
                // 多点：第一帧加 interp 类型 = 1（贝塞尔，呼吸类动画基本都用贝塞尔平滑）
                sb.Append(series[0].Key.ToString("F4")).Append(", ")
                  .Append(series[0].Value.ToString("F4")).Append(", 1");
                for (int i = 1; i < n; i++)
                {
                    sb.Append(", ").Append(series[i].Key.ToString("F4"))
                      .Append(", ").Append(series[i].Value.ToString("F4"));
                }
            }

            sb.Append("]");
            sb.Append("}");
        }

        // ============= 工具 =============

        private static string SanitizeFileName(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            char[] bad = Path.GetInvalidFileNameChars();
            StringBuilder sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (Array.IndexOf(bad, c) >= 0) sb.Append('_');
                else if (c == ' ' || c == ':') sb.Append('_');
                else sb.Append(c);
            }
            return sb.ToString();
        }

        private static string EscapeCsv(string s)
        {
            if (s == null) return "";
            if (s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0)
                return "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }

        private static string EscapeJson(string s)
        {
            if (s == null) return "";
            StringBuilder sb = new StringBuilder(s.Length + 2);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.AppendFormat("\\u{0:X4}", (int)c);
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }
}
