using UnityEngine;
using UniverseLib.Utility;
using UnityExplorer.UI.Widgets;

namespace UnityExplorer.Inspectors
{
    /// <summary>
    /// v16bb/v16bo/v16bp：实时位置注入器（无独立 UI）。
    ///
    /// 背景：部分类型的对象动画不经过 Transform——
    ///   - Cubism（Live2D）：motion3 → CubismParameter.value → Deformer 求值 → 直接改写 ArtMesh 顶点缓冲；
    ///   - 普通 Unity 蒙皮模型：Animator/Animation 驱动骨骼变形网格，挂 SkinnedMeshRenderer 的
    ///     对象自身 Transform 恒定（如 Sister 系列的 Face/Hair/Body）。
    /// 因此检查器 Transform 区的位置/本地位置恒定，无法反映动画。
    ///
    /// v16bp 架构：**类型检测 → 位置源策略分发**。
    ///   DetectSource 按组件类型识别目标属于哪一类，构造对应的 IPosSource；
    ///   每种类型一个 Source 类，各自实现"用什么方法取位置"：
    ///     1. CubismArtMeshSource   — ArtMesh：MeshRenderer.bounds（顶点缓冲驱动，世界 AABB）
    ///     2. CubismRootSource      — Cubism 模型根：0.25s 节流聚合全部 CubismRenderer bounds
    ///     3. SkinnedSource         — 蒙皮对象：SkinnedMeshRenderer.bounds（蒙皮变形驱动，世界 AABB）
    ///     4. SkinnedRootSource     — 蒙皮模型根：0.25s 节流聚合全部子 SkinnedMeshRenderer bounds
    ///   新增类型 = 新增一个 IPosSource 实现 + DetectSource 加一条分支，面板/写入逻辑零改动。
    ///
    /// 写入方式：TransformControls.PositionControl / LocalPositionControl 的 MainInput.Text。
    /// Vector3Control.Update 只在 Transform 值变化时才重写文本（此类对象 Transform 恒定 → 不会覆盖），
    /// 且输入框聚焦时跳过写入，用户仍可手动输入改 Transform。
    ///
    /// 检测：Cubism 用类型名字符串匹配（无编译期依赖，4 配置通用）；
    /// SkinnedMeshRenderer 为 UnityEngine 内建类型，直接引用。
    /// </summary>
    internal class Live2DLivePosPanel
    {
        // ==================== 位置源策略接口 ====================

        private interface IPosSource
        {
            /// <summary>true = 聚合模式，需要 0.25s 节流</summary>
            bool IsRootMode { get; }

            /// <summary>
            /// 取实时位置。返回 false = 当前无有效数据（渲染器禁用/不可见等）。
            /// world = 世界空间中心；localSpace = 用于 InverseTransformPoint 的"模型根"。
            /// </summary>
            bool TryGetCenter(bool force, out Vector3 world, out Transform localSpace);
        }

        // ---------- 类型 1：Cubism ArtMesh（单对象） ----------

        private class CubismArtMeshSource : IPosSource
        {
            private readonly GameObject target;

            public bool IsRootMode => false;

            public CubismArtMeshSource(GameObject target) { this.target = target; }

            public bool TryGetCenter(bool force, out Vector3 world, out Transform localSpace)
            {
                world = default;
                localSpace = target != null ? target.transform : null;
                if (target == null)
                    return false;

                MeshRenderer mr = target.GetComponent<MeshRenderer>();
                if (mr == null || !mr.enabled)
                    return false;

                world = mr.bounds.center;
                return true;
            }
        }

        // ---------- 类型 2：Cubism 模型根（聚合） ----------

        private class CubismRootSource : IPosSource
        {
            private readonly Transform modelRoot;
            private float lastAggTime;
            private Vector3 lastCenter;
            private bool hasData;

            public bool IsRootMode => true;

            public CubismRootSource(Transform modelRoot) { this.modelRoot = modelRoot; }

            public bool TryGetCenter(bool force, out Vector3 world, out Transform localSpace)
            {
                localSpace = modelRoot;

                // 节流窗口内沿用上次结果
                if (!force && hasData && Time.realtimeSinceStartup - lastAggTime < 0.25f)
                {
                    world = lastCenter;
                    return true;
                }

                lastAggTime = Time.realtimeSinceStartup;
                hasData = false;

                try
                {
                    bool first = true;
                    Bounds bounds = new Bounds(Vector3.zero, Vector3.zero);
                    MeshRenderer[] renderers = modelRoot.GetComponentsInChildren<MeshRenderer>(false);
                    foreach (MeshRenderer r in renderers)
                    {
                        if (r == null || !r.enabled || !r.gameObject.activeInHierarchy)
                            continue;

                        // 只统计 ArtMesh 子对象（带 CubismRenderer），跳过无关渲染器
                        bool isCub = false;
                        foreach (Component c in r.GetComponents<Component>())
                        {
                            if (c == null)
                                continue;
                            if (c.GetType().Name == "CubismRenderer")
                            {
                                isCub = true;
                                break;
                            }
                        }
                        if (!isCub)
                            continue;

                        if (first) { bounds = r.bounds; first = false; }
                        else bounds.Encapsulate(r.bounds);
                    }

                    if (!first)
                    {
                        lastCenter = bounds.center;
                        hasData = true;
                    }
                }
                catch { }

                world = lastCenter;
                return hasData;
            }
        }

        // ---------- 类型 3：SkinnedMeshRenderer 对象（单对象） ----------

        private class SkinnedSource : IPosSource
        {
            private readonly SkinnedMeshRenderer smr;
            private readonly Transform modelRoot;   // 最顶层 Animator/Animation 祖先

            public bool IsRootMode => false;

            public SkinnedSource(SkinnedMeshRenderer smr, Transform modelRoot)
            {
                this.smr = smr;
                this.modelRoot = modelRoot;
            }

            public bool TryGetCenter(bool force, out Vector3 world, out Transform localSpace)
            {
                world = default;
                localSpace = modelRoot;
                if (smr == null || !smr.enabled || !smr.gameObject.activeInHierarchy)
                    return false;

                // Unity 随蒙皮变形自动重算的世界空间 AABB
                world = smr.bounds.center;
                return true;
            }
        }

        // ---------- 类型 4：蒙皮模型根（聚合） ----------

        private class SkinnedRootSource : IPosSource
        {
            private readonly Transform root;
            private float lastAggTime;
            private Vector3 lastCenter;
            private bool hasData;

            public bool IsRootMode => true;

            public SkinnedRootSource(Transform root) { this.root = root; }

            public bool TryGetCenter(bool force, out Vector3 world, out Transform localSpace)
            {
                localSpace = root;

                if (!force && hasData && Time.realtimeSinceStartup - lastAggTime < 0.25f)
                {
                    world = lastCenter;
                    return true;
                }

                lastAggTime = Time.realtimeSinceStartup;
                hasData = false;

                try
                {
                    bool first = true;
                    Bounds bounds = new Bounds(Vector3.zero, Vector3.zero);
                    SkinnedMeshRenderer[] skins = root.GetComponentsInChildren<SkinnedMeshRenderer>(false);
                    foreach (SkinnedMeshRenderer r in skins)
                    {
                        if (r == null || !r.enabled || !r.gameObject.activeInHierarchy)
                            continue;

                        if (first) { bounds = r.bounds; first = false; }
                        else bounds.Encapsulate(r.bounds);
                    }

                    if (!first)
                    {
                        lastCenter = bounds.center;
                        hasData = true;
                    }
                }
                catch { }

                world = lastCenter;
                return hasData;
            }
        }

        // ==================== 类型检测 ====================

        /// <summary>
        /// 按组件类型识别目标属于哪一类，构造对应位置源；返回 null = 普通对象，
        /// 走默认 Transform 行为（Transform 本身会动，无需注入）。
        /// 检测优先级：Cubism → 自身 SkinnedMeshRenderer → 子孙 SkinnedMeshRenderer。
        /// </summary>
        private static IPosSource DetectSource(GameObject target)
        {
            try
            {
                if (target == null)
                    return null;

                // --- Cubism（Live2D）---
                if (HasComponentNamed(target, "CubismModel"))
                    return new CubismRootSource(target.transform);

                if (HasComponentNamed(target, "CubismDrawable")
                    || HasComponentNamed(target, "CubismRenderer"))
                {
                    // 向上找 CubismModel 根做"模型根"
                    Transform p = target.transform.parent;
                    while (p != null)
                    {
                        if (HasComponentNamed(p.gameObject, "CubismModel"))
                            return new CubismArtMeshSource(target);
                        p = p.parent;
                    }
                    return null;   // 孤立 Cubism 组件，视为普通对象
                }

                // --- 普通 Unity 蒙皮模型 ---
                SkinnedMeshRenderer smr = target.GetComponent<SkinnedMeshRenderer>();
                if (smr != null)
                    return new SkinnedSource(smr, FindSkinnedModelRoot(target.transform));

                if (target.GetComponentsInChildren<SkinnedMeshRenderer>(false).Length > 0)
                    return new SkinnedRootSource(target.transform);
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 蒙皮模型的"模型根" = 最顶层带 Animator/Animation 的祖先
        /// （骨骼动画的驱动源通常在角色根上），找不到则回退 transform.root。
        /// </summary>
        private static Transform FindSkinnedModelRoot(Transform t)
        {
            try
            {
                Transform best = null;
                Transform p = t.parent;
                while (p != null)
                {
                    if (HasComponentNamed(p.gameObject, "Animator")
                        || HasComponentNamed(p.gameObject, "Animation"))
                        best = p;
                    p = p.parent;
                }
                if (best != null)
                    return best;
                return t.root;
            }
            catch
            {
                return t.root;
            }
        }

        // ==================== 面板主体（类型无关） ====================

        private readonly GameObjectInspector inspector;
        private IPosSource source;
        private GameObject targetGO;

        public Live2DLivePosPanel(GameObjectInspector inspector)
        {
            this.inspector = inspector;
        }

        public void SetTarget(GameObject target)
        {
            targetGO = target;
            source = DetectSource(target);

            if (source != null)
                TickNow(true);
        }

        public void ClearTarget()
        {
            targetGO = null;
            source = null;
        }

        public void TickUpdate()
        {
            if (source == null || targetGO == null)
                return;

            // 聚合模式 0.25s 节流；单对象模式每帧
            if (!source.IsRootMode || Time.realtimeSinceStartup - LastRootTick > 0.25f)
                TickNow(!source.IsRootMode);
        }

        private float LastRootTick;

        private void TickNow(bool force)
        {
            LastRootTick = Time.realtimeSinceStartup;
            try
            {
                if (source.TryGetCenter(force, out Vector3 world, out Transform localSpace))
                    WriteToControls(world, localSpace);
            }
            catch { }
        }

        private void WriteToControls(Vector3 worldCenter, Transform localSpace)
        {
            try
            {
                TransformControls tc = inspector?.Controls?.TransformControl;
                if (tc == null)
                    return;

                Vector3 localCenter = localSpace != null
                    ? localSpace.InverseTransformPoint(worldCenter)
                    : worldCenter;

                Vector3Control pos = tc.PositionControl;
                Vector3Control local = tc.LocalPositionControl;

                // 聚焦时跳过，避免打断用户手动输入
                if (pos?.MainInput != null && !pos.MainInput.Component.isFocused)
                    pos.MainInput.Text = ParseUtility.ToStringForInput<Vector3>(worldCenter);

                if (local?.MainInput != null && !local.MainInput.Component.isFocused)
                    local.MainInput.Text = ParseUtility.ToStringForInput<Vector3>(localCenter);
            }
            catch { }
        }

        private static bool HasComponentNamed(GameObject go, string typeName)
        {
            try
            {
                foreach (Component c in go.GetComponents<Component>())
                {
                    if (c == null)
                        continue;
                    if (c.GetType().Name == typeName)
                        return true;
                }
            }
            catch { }
            return false;
        }
    }
}
