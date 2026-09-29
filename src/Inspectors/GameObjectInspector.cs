using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityExplorer.Runtime;
using UnityExplorer.UI.Panels;
using UnityExplorer.UI.Widgets;
using UnityExplorer.UI.Widgets.AutoComplete;
using UniverseLib.UI;
using UniverseLib.UI.Models;
using UniverseLib.UI.Widgets;
using UniverseLib.UI.Widgets.ScrollView;

namespace UnityExplorer.Inspectors
{
    public class GameObjectInspector : InspectorBase
    {
        public new GameObject Target => base.Target as GameObject;

        public GameObject Content;

        public GameObjectControls Controls;

        public TransformTree TransformTree;
        private ScrollPool<TransformCell> transformScroll;
        private readonly List<GameObject> cachedChildren = new();

        public ComponentList ComponentList;
        private ScrollPool<ComponentCell> componentScroll;

        private InputFieldRef addChildInput;
        private InputFieldRef addCompInput;

        // 资源导出
        private GameObject exportPanel;
        // v16ba：Live2D 实时位置面板（Cubism 动画不经过 Transform，用顶点中心实时显示）
        private Live2DLivePosPanel livePosPanel;
        private GameObject exportListHolder;
        private GameObject exportListScrollRoot;    // v16w：ScrollView 根节点，展开/收起时直接操作它
        private UnityEngine.UI.LayoutElement mainScrollLayoutElement;   // v16ac：主滚动区 LayoutElement，动态高度用
        private ButtonRef exportToggleBtn;

        // v16aa：底部“子对象/组件”面板，导出展开时自动收起
        private GameObject bottomInfoPanel;
        private GameObject bottomContentPanel;
        private ButtonRef bottomToggleBtn;
        private bool bottomInfoExpanded = true;
        private ButtonRef reloadCycleBtn;
        private ButtonRef sceneScanBtn;     // v16ag：全场景扫描按钮（仅 Live2D/Spine 类型显示）
        private Text exportInfoText;
        private Toggle recursiveToggle;
        private bool exportPanelExpanded;
        private readonly List<GameObject> exportItemRows = new();
        private readonly List<Toggle> exportItemToggles = new();
        private bool useRecursiveExport = false;
        private Dropdown exportDropdown;
        private ButtonRef exportExecuteBtn;
        private int selectedExportMode = 0;
        private List<AssetExporter.ExportableItem> currentExportables = new();
        private AssetExporter.AssetKind detectedKind = AssetExporter.AssetKind.Unknown;

        // 导出列表最多显示的条目数（防止几百个 ArtMesh 时 UI 卡死）
        private const int MaxExportRows = 150;

        // 三种导出模式
        private static readonly string[] ExportModeNames = { "导出全部", "导出已勾选", "导出未勾选" };

        // Live2D 录制（v12）
        public override void OnBorrowedFromPool(object target)
        {
            base.OnBorrowedFromPool(target);

            base.Target = target as GameObject;

            Controls.UpdateGameObjectInfo(true, true);
            Controls.TransformControl.UpdateTransformControlValues(true);

            // v16bb：Live2D 实时位置跟随目标
            livePosPanel?.SetTarget(target as GameObject);

            // 重置导出面板
            if (exportPanelExpanded)
                ToggleExportPanel();

            RuntimeHelper.StartCoroutine(InitCoroutine());
        }

        private IEnumerator InitCoroutine()
        {
            yield return null;

            LayoutRebuilder.ForceRebuildLayoutImmediate(InspectorPanel.Instance.ContentRect);

            TransformTree.Rebuild();

            ComponentList.ScrollPool.Refresh(true, true);
            UpdateComponents();
        }

        public override void OnReturnToPool()
        {
            base.OnReturnToPool();

            addChildInput.Text = "";
            addCompInput.Text = "";

            TransformTree.Clear();
            UpdateComponents();

            // v16ba：回收时清空 Live2D 实时位置面板
            livePosPanel?.ClearTarget();
        }

        public override void CloseInspector()
        {
            InspectorManager.ReleaseInspector(this);
        }

        public void OnTransformCellClicked(GameObject newTarget)
        {
            base.Target = newTarget;
            Controls.UpdateGameObjectInfo(true, true);
            Controls.TransformControl.UpdateTransformControlValues(true);

            // v16ba：Live2D 实时位置面板跟随目标
            livePosPanel?.SetTarget(newTarget);
            TransformTree.RefreshData(true, false, true, false);
            UpdateComponents();

            // 如果导出面板展开，刷新列表
            if (exportPanelExpanded)
                RefreshExportList();
        }

        private float timeOfLastUpdate;
        private bool scrollHeightInitialized;   // v16ac：首帧布局完成后校正一次滚动区高度
        private float lastUiHeight;             // v16bd：跟踪窗口高度变化，resize 后自动校正滚动区

        public override void Update()
        {
            if (!this.IsActive)
                return;

            // v16ac：首帧时 UIRoot rect 已有效，校正一次滚动区高度（创建时 rect 还是 0 无法计算）
            if (!scrollHeightInitialized)
            {
                scrollHeightInitialized = true;
                UpdateScrollHeight();
            }

            if (base.Target.IsNullOrDestroyed(false))
            {
                InspectorManager.ReleaseInspector(this);
                return;
            }

            Controls.UpdateVectorSlider();
            Controls.TransformControl.UpdateTransformControlValues(false);

            // v16bb：Live2D 实时位置每帧刷新（写入现有 Transform 区字段，内部自带节流）
            livePosPanel?.TickUpdate();

            // Slow update
            if (timeOfLastUpdate.OccuredEarlierThan(1))
            {
                timeOfLastUpdate = Time.realtimeSinceStartup;

                Controls.UpdateGameObjectInfo(false, false);

                TransformTree.RefreshData(true, false, false, false);
                UpdateComponents();

                // v16bd：窗口高度变化（resize/拖动）后自动校正滚动区高度，
                //   否则拉伸窗口后下方留白或内容溢出（UpdateScrollHeight 原本只在展开/收起时触发）
                try
                {
                    float uiH = UIRoot != null ? UIRoot.GetComponent<RectTransform>().rect.height : 0f;
                    if (uiH > 100f && Mathf.Abs(uiH - lastUiHeight) > 1f)
                    {
                        lastUiHeight = uiH;
                        UpdateScrollHeight();
                    }
                }
                catch { }

                // v16: 录制状态文本块（UpdateRecordStatusUI 已被删除）
            }
        }

        // Child and Component Lists

        private IEnumerable<GameObject> GetTransformEntries()
        {
            if (!Target)
                return Enumerable.Empty<GameObject>();

            cachedChildren.Clear();
            for (int i = 0; i < Target.transform.childCount; i++)
                cachedChildren.Add(Target.transform.GetChild(i).gameObject);
            return cachedChildren;
        }

        private readonly List<Component> componentEntries = new();
        private readonly HashSet<int> compInstanceIDs = new();
        private readonly List<Behaviour> behaviourEntries = new();
        private readonly List<bool> behaviourEnabledStates = new();

        // ComponentList.GetRootEntriesMethod
        private List<Component> GetComponentEntries() => Target ? componentEntries : Enumerable.Empty<Component>().ToList();

        public void UpdateComponents()
        {
            if (!Target)
            {
                componentEntries.Clear();
                compInstanceIDs.Clear();
                behaviourEntries.Clear();
                behaviourEnabledStates.Clear();
                ComponentList.RefreshData();
                ComponentList.ScrollPool.Refresh(true, true);
                return;
            }

            // Check if we actually need to refresh the component cells or not.
            IEnumerable<Component> comps = Target.GetComponents<Component>();
            IEnumerable<Behaviour> behaviours = Target.GetComponents<Behaviour>();

            bool needRefresh = false;

            int count = 0;
            foreach (Component comp in comps)
            {
                if (!comp)
                    continue;
                count++;
                if (!compInstanceIDs.Contains(comp.GetInstanceID()))
                {
                    needRefresh = true;
                    break;
                }
            }
            if (!needRefresh)
            {
                if (count != componentEntries.Count)
                    needRefresh = true;
                else
                {
                    count = 0;
                    foreach (Behaviour behaviour in behaviours)
                    {
                        if (!behaviour)
                            continue;
                        if (count >= behaviourEnabledStates.Count || behaviour.enabled != behaviourEnabledStates[count])
                        {
                            needRefresh = true;
                            break;
                        }
                        count++;
                    }
                    if (!needRefresh && count != behaviourEntries.Count)
                        needRefresh = true;
                }
            }

            if (!needRefresh)
                return;

            componentEntries.Clear();
            compInstanceIDs.Clear();
            foreach (Component comp in comps)
            {
                if (!comp) 
                    continue;
                componentEntries.Add(comp);
                compInstanceIDs.Add(comp.GetInstanceID());
            }

            behaviourEntries.Clear();
            behaviourEnabledStates.Clear();
            foreach (Behaviour behaviour in behaviours)
            {
                if (!behaviour) 
                    continue;

                // Don't ask me how, but in some games this can be true for certain components.
                // They get picked up from GetComponents<Behaviour>, but they are not actually Behaviour...?
                if (!typeof(Behaviour).IsAssignableFrom(behaviour.GetType()))
                    continue;

                try
                {
                    behaviourEntries.Add(behaviour);
                }
                catch (Exception ex)
                {
                    ExplorerCore.LogWarning(ex);
                }

                behaviourEnabledStates.Add(behaviour.enabled);
            }

            ComponentList.RefreshData();
            ComponentList.ScrollPool.Refresh(true);
        }


        private void OnAddChildClicked(string input)
        {
            GameObject newObject = new(input);
            newObject.transform.parent = Target.transform;

            TransformTree.RefreshData(true, false, true, false);
        }

        private void OnAddComponentClicked(string input)
        {
            if (ReflectionUtility.GetTypeByName(input) is Type type)
            {
                try
                {
                    RuntimeHelper.AddComponent<Component>(Target, type);
                    UpdateComponents();
                }
                catch (Exception ex)
                {
                    ExplorerCore.LogWarning($"Exception adding component: {ex.ReflectionExToString()}");
                }
            }
            else
            {
                ExplorerCore.LogWarning($"Could not find any Type by the name '{input}'!");
            }
        }

        #region UI Construction

        public override GameObject CreateContent(GameObject parent)
        {
            UIRoot = UIFactory.CreateVerticalGroup(parent, "GameObjectInspector", true, false, true, true, 5,
                new Vector4(4, 4, 4, 4), new Color(0.065f, 0.065f, 0.065f));

            GameObject scrollObj = UIFactory.CreateScrollView(UIRoot, "GameObjectInspector", out Content, out AutoSliderScrollbar scrollbar,
                new Color(0.065f, 0.065f, 0.065f));
            // v16ac(回退v16ad)：滚动区高度动态调整（UpdateScrollHeight）：内容不满一屏贴合内容，超出封顶
            UIFactory.SetLayoutElement(scrollObj, minHeight: 250, preferredHeight: 300, flexibleHeight: 0, flexibleWidth: 9999);
            mainScrollLayoutElement = scrollObj.GetComponent<UnityEngine.UI.LayoutElement>();

            UIFactory.SetLayoutGroup<VerticalLayoutGroup>(Content, spacing: 3, padTop: 2, padBottom: 2, padLeft: 2, padRight: 2);

            // Construct GO Controls
            Controls = new GameObjectControls(this);

            // 导出面板在滚动区内，子对象/组件面板挂在 UIRoot（滚动区外 = 固定窗口底部）
            ConstructExportPanel();

            // v16bb：Live2D 实时位置注入器（无独立 UI，写入现有 Transform 区字段）
            livePosPanel = new Live2DLivePosPanel(this);

            ConstructLists();

            return UIRoot;
        }

        // 资源导出面板

        private void ConstructExportPanel()
        {
            exportPanel = UIFactory.CreateVerticalGroup(Content, "ExportPanel", false, false, true, true, 2,
                new Vector4(2, 2, 2, 2), new Color(0.1f, 0.1f, 0.1f));
            UIFactory.SetLayoutElement(exportPanel, minHeight: 25, flexibleWidth: 9999);

            // 标题行（v16bd：拆成两行。原来 8 个控件挤一行 ≈920px 固定宽，窗口不够宽时
            //   信息文本被挤成 0 宽、按钮文字换行 —— 文本显示错乱的主因）
            // Row1：展开/收起 + 含子对象开关 + 检测信息（flexible 吃满剩余宽）
            GameObject titleRow = UIFactory.CreateUIObject("ExportTitleRow", exportPanel);
            UIFactory.SetLayoutGroup<HorizontalLayoutGroup>(titleRow, false, false, true, true, 5);
            UIFactory.SetLayoutElement(titleRow, minHeight: 25, flexibleWidth: 9999);

            exportToggleBtn = UIFactory.CreateButton(titleRow, "ExportToggleBtn", "查看可提取资源 ▼", new Color(0.2f, 0.3f, 0.2f));
            UIFactory.SetLayoutElement(exportToggleBtn.Component.gameObject, minHeight: 25, minWidth: 150, flexibleWidth: 0);
            exportToggleBtn.ButtonText.fontSize = 13;
            exportToggleBtn.OnClick += ToggleExportPanel;

            // 递归子对象开关
            GameObject toggleObj = UIFactory.CreateToggle(titleRow, "RecursiveToggle", out Toggle recToggle, out Text recText);
            UIFactory.SetLayoutElement(toggleObj, minWidth: 110, flexibleWidth: 0);
            recText.text = "含子对象";
            recText.fontSize = 11;
            recText.color = Color.white;
            recToggle.isOn = true;
            recToggle.onValueChanged.AddListener((bool val) => useRecursiveExport = val);
            useRecursiveExport = true;
            recursiveToggle = recToggle;

            // 信息文本（Row1 内 flexible，宽度充足不再被挤没）
            exportInfoText = UIFactory.CreateLabel(titleRow, "ExportInfo", "",
                TextAnchor.MiddleLeft, Color.cyan, false, 10);
            UIFactory.SetLayoutElement(exportInfoText.gameObject, flexibleWidth: 9999, minWidth: 60, minHeight: 25);

            // Row2：操作按钮行（循环导出 / 全场景扫描 / 导出记录 / 执行导出 / 模式下拉）
            GameObject titleRow2 = UIFactory.CreateUIObject("ExportTitleRow2", exportPanel);
            UIFactory.SetLayoutGroup<HorizontalLayoutGroup>(titleRow2, false, false, true, true, 5);
            UIFactory.SetLayoutElement(titleRow2, minHeight: 25, flexibleWidth: 9999);

            // 隐藏/显示循环导出按钮（Live2D用）
            reloadCycleBtn = UIFactory.CreateButton(titleRow2, "ReloadCycleBtn", "循环加载导出", new Color(0.3f, 0.2f, 0.3f));
            UIFactory.SetLayoutElement(reloadCycleBtn.Component.gameObject, minHeight: 25, minWidth: 110, flexibleWidth: 0);
            reloadCycleBtn.ButtonText.fontSize = 11;
            reloadCycleBtn.OnClick += OnReloadCycleExport;

            // v16ag：全场景扫描按钮 —— 扫描场景里所有 Live2D/Spine 模型加入资源列表。
            //   仅 Live2D/Spine 类型显示（UpdateExportInfo 按检测结果显隐），初始隐藏
            sceneScanBtn = UIFactory.CreateButton(titleRow2, "SceneScanBtn", "全场景扫描", new Color(0.25f, 0.32f, 0.45f));
            UIFactory.SetLayoutElement(sceneScanBtn.Component.gameObject, minHeight: 25, minWidth: 100, flexibleWidth: 0);
            sceneScanBtn.ButtonText.fontSize = 11;
            sceneScanBtn.Component.gameObject.SetActive(false);
            sceneScanBtn.OnClick += OnSceneScanClicked;

            // v16n：导出音频记录按钮（仅 RecordingEnabledByConfig=true 才创建）
            if (Runtime.Live2DRecorder.RecordingEnabledByConfig)
            {
                live2dExportBtn = UIFactory.CreateButton(titleRow2, "Live2DExportBtn", "导出音频记录 (live2d.txt)", new Color(0.25f, 0.35f, 0.45f));
                UIFactory.SetLayoutElement(live2dExportBtn.Component.gameObject, minHeight: 25, minWidth: 200, flexibleWidth: 0);
                live2dExportBtn.ButtonText.fontSize = 11;
                live2dExportBtn.OnClick += OnLive2DManualExport;
            }

            // 执行导出按钮（真正触发导出，按下拉框当前选择的模式执行）
            exportExecuteBtn = UIFactory.CreateButton(titleRow2, "ExportExecuteBtn", $"执行导出：{ExportModeNames[0]}", new Color(0.2f, 0.3f, 0.4f));
            UIFactory.SetLayoutElement(exportExecuteBtn.Component.gameObject, minHeight: 25, minWidth: 130, flexibleWidth: 0);
            exportExecuteBtn.ButtonText.fontSize = 11;
            exportExecuteBtn.OnClick += () => DoExportByMode(selectedExportMode);

            // 导出模式下拉框（旁边的三角形：仅选择模式，不执行）
            GameObject dropdownObj = UIFactory.CreateDropdown(titleRow2, "ExportDropdown", out exportDropdown, ExportModeNames[0], 11, OnExportModeChanged);
            UIFactory.SetLayoutElement(dropdownObj, minHeight: 25, minWidth: 120, flexibleWidth: 0);
            exportDropdown.options.Clear();
            foreach (string mode in ExportModeNames)
                exportDropdown.options.Add(new Dropdown.OptionData(mode));
            exportDropdown.value = 0;
            exportDropdown.captionText.text = ExportModeNames[0];

            // v16n：可导出资源列表（ScrollView 包装，展开后可滚动；初始隐藏）
            // v16v：Content 必须带 VerticalLayoutGroup + ContentSizeFitter，
            //   否则子行不会自动堆叠，内容高度为 0 → 展开后看起来空白。
            GameObject exportListScroll = UIFactory.CreateScrollView(exportPanel, "ExportListScroll",
                out exportListHolder, out _, new Color(0.08f, 0.08f, 0.08f));
            exportListScrollRoot = exportListScroll;    // v16w：存根，供 ToggleExportPanel 直接开关
            // v16x：初始给一个很小的 minHeight，实际高度在 RefreshExportListWithItems 里按资源数动态设置
            UIFactory.SetLayoutElement(exportListScroll, minHeight: 30, preferredHeight: 30, flexibleHeight: 0, flexibleWidth: 9999);
            exportListScroll.SetActive(false);

            if (exportListHolder != null)
            {
                VerticalLayoutGroup vlg = exportListHolder.GetComponent<VerticalLayoutGroup>();
                if (vlg == null) vlg = exportListHolder.AddComponent<VerticalLayoutGroup>();
                vlg.childControlWidth = true;
                // v16z：childControlHeight 必须为 true，否则子行 RectTransform 高度保持 0，
                //   所有行会挤在同一水平线上。行高由 LayoutElement.minHeight=22 决定，
                //   ContentSizeFitter 汇总后得到 Content 高度；两者不再冲突。
                vlg.childControlHeight = true;
                vlg.childForceExpandWidth = false;
                vlg.childForceExpandHeight = false;
                vlg.spacing = 1;
                RectOffset pad = new RectOffset();
                pad.left = 2; pad.right = 2; pad.top = 2; pad.bottom = 2;
                vlg.padding = pad;

                ContentSizeFitter csf = exportListHolder.GetComponent<ContentSizeFitter>();
                if (csf == null) csf = exportListHolder.AddComponent<ContentSizeFitter>();
                csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
                csf.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            }

            // ============ Live2D 状态行（v16n：按钮已挪到 titleRow，这里只剩状态文本；config 关闭则整行不创建）============
            //   旧三按钮：[开始记录] [停止记录] [导出记录] + 状态文本（v12 引入，v16 删除）
            //   v16: 插件加载即自动记录；导出通过 ExplorerCore.Update() 触发
            //   v16i: 用户反馈 auto-dump 太吵、auto-export 5s 太频
            //        改为 Inspector 单个 "导出音频记录" 按钮手动触发
            //   v16n: 按钮挪到 titleRow 与"循环加载导出"同行；config 关闭时整行隐藏
            if (Runtime.Live2DRecorder.RecordingEnabledByConfig)
            {
                GameObject l2dRow = UIFactory.CreateUIObject("L2DRecordRow", exportPanel);
                UIFactory.SetLayoutGroup<HorizontalLayoutGroup>(l2dRow, false, false, true, true, 5);
                UIFactory.SetLayoutElement(l2dRow, minHeight: 25, flexibleWidth: 9999);

                live2dStatusText = UIFactory.CreateLabel(l2dRow, "L2DStatusText",
                    "Live2D 自动记录中（每帧）",
                    TextAnchor.MiddleLeft, Color.cyan, false, 10);
                UIFactory.SetLayoutElement(live2dStatusText.gameObject, flexibleWidth: 9999, minHeight: 25);
            }
        }

        // ============ Live2D 手动导出支持 ============
        private ButtonRef live2dExportBtn;
        private Text live2dStatusText;

        private static string _lastL2DExportSummary = "";
        private static int _l2dExportClickCount = 0;

        private void OnLive2DManualExport()
        {
            try
            {
                // v16m：data.cfg 关闭录制时拒绝手动导出（兜底，正常 UI 已显示关闭状态）
                if (!Runtime.Live2DRecorder.RecordingEnabledByConfig)
                {
                    if (live2dStatusText != null) live2dStatusText.text = "录制已关闭（" + Runtime.Live2DRecorder.RecordingConfigSummary + "），无法导出";
                    ExplorerCore.LogWarning("[Live2DRecorder] 手动导出被 data.cfg 阻止: " + Runtime.Live2DRecorder.RecordingConfigSummary);
                    return;
                }

                var paths = Live2DRecorder.AutoExportMotionAudioMap();
                _l2dExportClickCount++;
                _lastL2DExportSummary = paths != null && paths.Count > 0
                    ? $"已写入 {paths.Count} 个文件 (累计点击 {_l2dExportClickCount})"
                    : "(无可写内容，可能模型尚未上线)";
                if (live2dStatusText != null) live2dStatusText.text = _lastL2DExportSummary;
                if (paths != null && paths.Count > 0)
                    ExplorerCore.Log("[Live2DRecorder] 手动导出成功: " + paths.Count + " 个文件");
                else
                    ExplorerCore.LogWarning("[Live2DRecorder] 手动导出：无可写内容");
            }
            catch (Exception ex)
            {
                if (live2dStatusText != null) live2dStatusText.text = "导出失败: " + ex.Message;
                ExplorerCore.LogWarning("[Live2DRecorder] 手动导出失败: " + ex.Message);
            }
        }

        private void ToggleExportPanel()
        {
            exportPanelExpanded = !exportPanelExpanded;

            // v16w：直接开关 ScrollView 根节点。v16n 用 exportListHolder.transform.parent 容易
            // 取到 Viewport；在根节点 inactive 时单独把 Viewport active 设为 true 并不会真的显示，
            // 导致列表空白。
            // v16be：展开/收起都统一走 RebuildAndResizeScroll（先强制重建布局再测高）。
            //   ★ 原收起分支没有先重建就测高：LayoutUtility.GetPreferredHeight(Content) 拿到
            //   展开态旧值 → preferredHeight 不缩 → 空白视口残留（用户截图确认的 bug）。
            if (exportPanelExpanded)
            {
                if (exportListScrollRoot != null) exportListScrollRoot.SetActive(true);
                else if (exportListHolder != null) exportListHolder.SetActive(true);
                UpdateExportInfo();
                RefreshExportList();
                exportToggleBtn.ButtonText.text = "查看可提取资源 ▲";
                // v16aa：导出展开时自动收起底部“子对象/组件”
                SetBottomInfoExpanded(false);
            }
            else
            {
                if (exportListScrollRoot != null) exportListScrollRoot.SetActive(false);
                else if (exportListHolder != null) exportListHolder.SetActive(false);
                exportToggleBtn.ButtonText.text = "查看可提取资源 ▼";
                // v16aa：导出收起时恢复底部“子对象/组件”
                SetBottomInfoExpanded(true);
            }

            // v16be：统一收尾——先重建布局刷新实际内容高度，再按新高度调整滚动区
            RebuildAndResizeScroll();
        }

        /// <summary>
        /// v16be：强制重建导出面板/Content 布局 → 测高并调整滚动区 → 重建 UIRoot 收尾。
        /// 任何改变内容高度的开关操作（展开/收起导出、底部面板互斥）之后都应走这里，
        /// 否则 GetPreferredHeight 会拿到脏的旧布局值。
        /// </summary>
        private void RebuildAndResizeScroll()
        {
            try
            {
                if (exportPanel != null)
                    UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(exportPanel.GetComponent<RectTransform>());
                if (Content != null)
                    UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(Content.GetComponent<RectTransform>());
                UpdateScrollHeight();
                if (UIRoot != null)
                    UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(UIRoot.GetComponent<RectTransform>());
            }
            catch { }
        }

        /// <summary>
        /// v16ac：动态调整主滚动区高度。
        ///   内容（GameObjectControls + 导出面板当前状态）不足一屏 → 滚动区贴合内容，
        ///   “子对象/组件”按钮紧跟最后一行资源；
        ///   超出一屏 → 滚动区高度 = 窗口剩余空间（减去底部面板），
        ///   “子对象/组件”按钮固定在窗口底部，滚动区内部滚动。
        /// </summary>
        private void UpdateScrollHeight()
        {
            if (mainScrollLayoutElement == null || UIRoot == null || Content == null) return;
            try
            {
                float uiH = UIRoot.GetComponent<RectTransform>().rect.height;
                // UIRoot 尚未完成布局（刚构建、rect 无效）→ 保留默认高度，待首次交互时再校正
                if (uiH < 100f) return;

                float contentPref = UnityEngine.UI.LayoutUtility.GetPreferredHeight(Content.GetComponent<RectTransform>());
                float bottomH = 0f;
                if (bottomInfoPanel != null && bottomInfoPanel.activeSelf)
                    bottomH = UnityEngine.UI.LayoutUtility.GetPreferredHeight(bottomInfoPanel.GetComponent<RectTransform>());

                float avail = uiH - bottomH - 12f;
                if (avail < 250f) avail = 250f;   // 窗口过小时的兜底下限

                mainScrollLayoutElement.preferredHeight = Mathf.Clamp(contentPref, 250f, avail);
                mainScrollLayoutElement.flexibleHeight = 0;
            }
            catch { }
        }

        /// <summary>
        /// v16aa：切换底部“子对象/组件”面板的显示/隐藏。
        /// </summary>
        private void ToggleBottomInfoPanel()
        {
            SetBottomInfoExpanded(!bottomInfoExpanded);
        }

        private void SetBottomInfoExpanded(bool expanded)
        {
            bottomInfoExpanded = expanded;
            // v16bd：双向互斥补全——展开底部时自动收起导出列表（原来只有反向互斥，
            //   两个面板同时展开会抢高度，导出列表被压瘪）。
            //   经 ToggleExportPanel 收起，其内部会回调 SetBottomInfoExpanded(true)，
            //   此时 exportPanelExpanded 已为 false，不会无限递归；收起分支已处理
            //   flexibleHeight 与滚动高度校正，直接返回避免重复执行。
            if (expanded && exportPanelExpanded)
            {
                ToggleExportPanel();
                return;
            }
            if (bottomContentPanel != null)
            {
                bottomContentPanel.SetActive(expanded);
                // v16ac：展开时内容区弹性填满窗口剩余空间（列表区拉伸，不留下方空块）；
                //   收起时只占实际高度，避免按钮下面空出一大块
                UnityEngine.UI.LayoutElement cle = bottomContentPanel.GetComponent<UnityEngine.UI.LayoutElement>();
                if (cle != null) cle.flexibleHeight = expanded ? 9999f : 0f;
            }
            if (bottomInfoPanel != null)
            {
                UnityEngine.UI.LayoutElement ble = bottomInfoPanel.GetComponent<UnityEngine.UI.LayoutElement>();
                if (ble != null) ble.flexibleHeight = expanded ? 9999f : 0f;
            }
            if (bottomToggleBtn != null)
                bottomToggleBtn.ButtonText.text = expanded ? "子对象/组件 ▲" : "子对象/组件 ▼";
            // v16ac：底部面板高度变化 → 可用空间变化 → 滚动区高度联动校正
            UpdateScrollHeight();
        }

        /// <summary>
        /// 更新导出信息（2D/3D 检测、Live2D/Spine 检测、子对象数量）
        /// </summary>
        private void UpdateExportInfo()
        {
            if (Target == null)
            {
                exportInfoText.text = "";
                SetSceneScanBtnVisible(false);
                return;
            }

            try
            {
                int childCount = Target.transform.childCount;

                // 统一检测：3D模型 / Live2D / Spine / 2D精灵 / 未知
                detectedKind = AssetExporter.DetectAssetKind(Target);
                string kindStr = detectedKind == AssetExporter.AssetKind.Model3D ? "3D 模型"
                    : detectedKind == AssetExporter.AssetKind.Live2D ? "Live2D"
                    : detectedKind == AssetExporter.AssetKind.Spine ? "Spine"
                    : detectedKind == AssetExporter.AssetKind.Sprite2D ? "2D 精灵"
                    : "未知";

                exportInfoText.text = $"{kindStr} | 子对象:{childCount}";
                exportInfoText.color = (detectedKind == AssetExporter.AssetKind.Live2D || detectedKind == AssetExporter.AssetKind.Spine)
                    ? new Color(1f, 0.6f, 1f)
                    : Color.cyan;

                // v16ag：仅 Live2D / Spine 类型显示「全场景扫描」按钮
                SetSceneScanBtnVisible(detectedKind == AssetExporter.AssetKind.Live2D
                    || detectedKind == AssetExporter.AssetKind.Spine);
            }
            catch
            {
                exportInfoText.text = "";
                SetSceneScanBtnVisible(false);
            }
        }

        /// <summary>v16ag：「全场景扫描」按钮显隐</summary>
        private void SetSceneScanBtnVisible(bool visible)
        {
            if (sceneScanBtn != null && sceneScanBtn.Component != null)
                sceneScanBtn.Component.gameObject.SetActive(visible);
        }

        /// <summary>
        /// v16ag：全场景扫描 —— 扫描场景里所有 Live2D Cubism / Spine 模型加入资源列表。
        /// 按钮仅在检测类型为 Live2D/Spine 时显示。
        /// </summary>
        private void OnSceneScanClicked()
        {
            try
            {
                if (exportInfoText != null) exportInfoText.text = "全场景扫描中...";
                List<AssetExporter.ExportableItem> found = AssetExporter.CollectAllLive2DSpineSceneModels();

                // 列表未展开则先展开（内部会刷新一次目标对象的列表，随后被扫描结果覆盖）
                if (!exportPanelExpanded)
                    ToggleExportPanel();
                RefreshExportListWithItems(found);

                if (exportInfoText != null)
                    exportInfoText.text = $"全场景扫描完成: {found.Count} 个条目";
                ExplorerCore.Log("[Live2D] 全场景扫描: " + found.Count + " 个导出条目");
            }
            catch (Exception ex)
            {
                if (exportInfoText != null) exportInfoText.text = "全场景扫描失败: " + ex.Message;
                ExplorerCore.LogWarning("[Live2D] 全场景扫描失败: " + ex.Message);
            }
        }

        /// <summary>
        /// 隐藏/显示循环导出（Live2D专用）- 协程，间隔0.2秒
        /// </summary>
        private void OnReloadCycleExport()
        {
            if (Target == null)
                return;

            ExplorerCore.Log($"开始隐藏/显示循环扫描: {Target.name}");

            // 确保面板展开
            if (!exportPanelExpanded)
                ToggleExportPanel();

            // 启动协程执行隐藏/显示循环
            RuntimeHelper.StartCoroutine(ReloadCycleCoroutine(Target));
        }

        /// <summary>
        /// 输出对象树结构（诊断用）
        /// </summary>
        private void LogObjectTree(GameObject go, int depth, int maxDepth)
        {
            if (go == null || depth > maxDepth)
                return;

            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            for (int i = 0; i < depth; i++)
                sb.Append("  ");
            sb.Append(go.name);

            // 组件类型（IL2CPP 下用真实类型名）
            try
            {
                Component[] comps = go.GetComponents<Component>();
                if (comps != null && comps.Length > 0)
                {
                    sb.Append(" [");
                    int shown = 0;
                    foreach (Component c in comps)
                    {
                        if (c == null) continue;
                        string cn = AssetExporter.GetComponentTypeName(c);
                        if (string.IsNullOrEmpty(cn)) cn = "null";
                        // 截断过长的命名空间
                        int idx = cn.LastIndexOf('.');
                        if (idx >= 0 && idx < cn.Length - 1)
                            cn = cn.Substring(idx + 1);
                        sb.Append(cn).Append(',');
                        if (++shown > 8) { sb.Append("..."); break; }
                    }
                    sb.Append("]");
                }
            }
            catch { }

            ExplorerCore.Log(sb.ToString());

            for (int i = 0; i < go.transform.childCount && i < 8; i++)
            {
                try
                {
                    Transform child = go.transform.GetChild(i);
                    if (child != null)
                        LogObjectTree(child.gameObject, depth + 1, maxDepth);
                }
                catch { }
            }
        }

        private System.Collections.IEnumerator ReloadCycleCoroutine(GameObject target)
        {
            if (target == null)
                yield break;

            // 诊断：输出对象树结构（帮助定位 Live2D 资源位置）
            try
            {
                LogObjectTree(target, 0, 3);
            }
            catch { }

            // 启动时扫描并缓存资源到托管内存（moc3/贴图/网格）
            try
            {
                AssetExporter.ScanAndCacheAssets();
            }
            catch (System.Exception ex)
            {
                ExplorerCore.LogWarning($"[循环] 缓存扫描失败: {ex.Message}");
            }

            bool originalActive = false;
            try { originalActive = target.activeSelf; } catch { }

            List<AssetExporter.ExportableItem> allItems = new();
            HashSet<string> allNames = new();

            // 初始收集
            try
            {
                List<AssetExporter.ExportableItem> initial = AssetExporter.CollectLive2DExportables(target);
                foreach (AssetExporter.ExportableItem it in initial)
                {
                    string key = it.Name + "_" + it.Type;
                    if (!allNames.Contains(key))
                    {
                        allNames.Add(key);
                        allItems.Add(it);
                    }
                }
                ExplorerCore.Log($"[循环] 初始扫描: {allItems.Count} 个资源");
            }
            catch (System.Exception ex)
            {
                ExplorerCore.LogWarning($"[循环] 初始扫描失败: {ex.Message}");
            }

            // 隐藏/显示循环，间隔约0.2秒（12帧逐帧等待，兼容 Unhollower 代理库无 WaitForSeconds 构造）
            for (int i = 0; i < 6; i++)
            {
                // 隐藏
                try { target.SetActive(false); } catch { }
                for (int f = 0; f < 12; f++) yield return null;

                // 显示
                try { target.SetActive(true); } catch { }
                for (int f = 0; f < 12; f++) yield return null;

                // 收集
                try
                {
                    List<AssetExporter.ExportableItem> items = AssetExporter.CollectLive2DExportables(target);
                    int added = 0;
                    foreach (AssetExporter.ExportableItem it in items)
                    {
                        string key = it.Name + "_" + it.Type;
                        if (!allNames.Contains(key))
                        {
                            allNames.Add(key);
                            allItems.Add(it);
                            added++;
                        }
                    }
                    ExplorerCore.Log($"[循环] 第{i + 1}次: 扫描到 {items.Count} 个，新增 {added} 个");
                    if (added == 0 && i > 1)
                        break;
                }
                catch (System.Exception ex)
                {
                    ExplorerCore.LogWarning($"[循环] 第{i + 1}次收集失败: {ex.Message}");
                    break;
                }
            }

            // 恢复原始激活状态
            try { target.SetActive(originalActive); } catch { }

            // 检测资产类型并按类型过滤（各自只导出各自对应的文件）
            try { detectedKind = AssetExporter.DetectAssetKind(target); } catch { detectedKind = AssetExporter.AssetKind.Unknown; }
            try { AssetExporter.FilterItemsByKind(allItems, detectedKind); } catch { }

            // 刷新列表
            RefreshExportListWithItems(allItems);
            ExplorerCore.Log($"[循环] 完成，共发现 {allItems.Count} 个资源，请点击导出");
        }

        private void RefreshExportList()
        {
            if (Target == null)
                return;

            List<AssetExporter.ExportableItem> exportables = CollectFiltered();
            RefreshExportListWithItems(exportables);
        }

        /// <summary>
        /// 收集 + 检测资产类型 + 按类型过滤（各自只导出各自对应的文件）。
        /// </summary>
        private List<AssetExporter.ExportableItem> CollectFiltered()
        {
            List<AssetExporter.ExportableItem> exportables = new();

            if (Target == null)
                return exportables;

            // 确保已扫描缓存（moc3/贴图等）
            if (!AssetExporter.AssetCache.Scanned)
            {
                try { AssetExporter.ScanAndCacheAssets(); } catch { }
            }

            try
            {
                // 检测资产类型：3D / Live2D / Spine / 2D精灵
                detectedKind = AssetExporter.DetectAssetKind(Target);
            }
            catch { detectedKind = AssetExporter.AssetKind.Unknown; }

            try
            {
                if (useRecursiveExport)
                    exportables = AssetExporter.CollectExportables(Target);
                else
                    exportables = AssetExporter.CollectSingleObject(Target);
            }
            catch (System.Exception ex)
            {
                ExplorerCore.LogWarning($"[导出] 收集失败: {ex.Message}");
            }

            // 按检测类型过滤
            try
            {
                AssetExporter.FilterItemsByKind(exportables, detectedKind);
            }
            catch { }

            string kindLabel = detectedKind == AssetExporter.AssetKind.Model3D ? "3D模型"
                : detectedKind == AssetExporter.AssetKind.Live2D ? "Live2D"
                : detectedKind == AssetExporter.AssetKind.Spine ? "Spine"
                : detectedKind == AssetExporter.AssetKind.Sprite2D ? "2D精灵"
                : "未知";
            ExplorerCore.Log($"[导出] 检测类型: {kindLabel}，过滤后 {exportables.Count} 个文件");

            return exportables;
        }

        private void RefreshExportListWithItems(List<AssetExporter.ExportableItem> exportables)
        {
            // 清除旧的导出项
            foreach (GameObject row in exportItemRows)
            {
                if (row)
                    UnityEngine.Object.Destroy(row);
            }
            exportItemRows.Clear();
            exportItemToggles.Clear();
            currentExportables = exportables ?? new List<AssetExporter.ExportableItem>();

            // v16x：根据资源数量动态调整 ScrollView 高度，避免 3 个文件下面空出 200 像素的尴尬
            //   高度估算：kindRow(20) + checkAllRow(22) + N*itemRow(22) + (N+1)*spacing(1) + padding(4)
            //   v16ac：不再内部封顶 260 —— 列表按资源数自然撑开，溢出交给 UpdateScrollHeight
            //   把主滚动区压回窗口可用空间（此时按钮固定在底部、主滚动区出现滚动条）
            if (exportListScrollRoot != null)
            {
                int visibleCount = (exportables == null) ? 0 : exportables.Count;
                if (visibleCount > MaxExportRows) visibleCount = MaxExportRows;
                int estimatedHeight = (visibleCount == 0) ? 30 : 48 + visibleCount * 23;
                int preferredHeight = (int)Mathf.Clamp((float)estimatedHeight, 30f, 2000f);
                UIFactory.SetLayoutElement(exportListScrollRoot,
                    minHeight: 30, preferredHeight: preferredHeight,
                    flexibleHeight: 0, flexibleWidth: 9999);
            }
            UpdateScrollHeight();

            if (exportables == null || exportables.Count == 0)
            {
                GameObject noItem = UIFactory.CreateUIObject("NoExportItem", exportListHolder);
                UIFactory.SetLayoutElement(noItem, minHeight: 22, flexibleWidth: 9999);
                UIFactory.CreateLabel(noItem, "NoExportText", "  （没有可提取的资源，试试循环加载导出）",
                    TextAnchor.MiddleLeft, Color.grey, false, 11);
                exportItemRows.Add(noItem);
                return;
            }

            // 检测类型标题
            GameObject kindRow = UIFactory.CreateUIObject("ExportKindRow", exportListHolder);
            UIFactory.SetLayoutElement(kindRow, minHeight: 20, flexibleWidth: 9999);
            string kindLabel = detectedKind == AssetExporter.AssetKind.Model3D ? "3D 模型"
                : detectedKind == AssetExporter.AssetKind.Live2D ? "Live2D"
                : detectedKind == AssetExporter.AssetKind.Spine ? "Spine"
                : detectedKind == AssetExporter.AssetKind.Sprite2D ? "2D 精灵"
                : "未知类型";
            UIFactory.CreateLabel(kindRow, "ExportKindText", $"  检测: {kindLabel}（共 {exportables.Count} 个文件）",
                TextAnchor.MiddleLeft, Color.cyan, false, 11);
            exportItemRows.Add(kindRow);

            // 全选/反选行
            GameObject checkAllRow = UIFactory.CreateUIObject("CheckAllRow", exportListHolder);
            UIFactory.SetLayoutElement(checkAllRow, minHeight: 22, flexibleWidth: 9999);
            UIFactory.SetLayoutGroup<HorizontalLayoutGroup>(checkAllRow, false, false, true, true, 3);
            GameObject checkAllObj = UIFactory.CreateToggle(checkAllRow, "CheckAllToggle", out Toggle checkAllToggle, out Text checkAllText);
            checkAllText.text = " 全选";
            checkAllText.fontSize = 11;
            checkAllText.color = Color.white;
            checkAllToggle.isOn = true;
            checkAllToggle.onValueChanged.AddListener((bool val) =>
            {
                foreach (Toggle t in exportItemToggles)
                    t.isOn = val;
                foreach (AssetExporter.ExportableItem it in exportables)
                    it.IsChecked = val;
            });
            exportItemRows.Add(checkAllRow);

            // 各个资源（带勾选框，点击行切换勾选）
            int shown = 0;
            foreach (AssetExporter.ExportableItem item in exportables)
            {
                if (shown >= MaxExportRows)
                {
                    GameObject moreRow = UIFactory.CreateUIObject("ExportMoreRow", exportListHolder);
                    UIFactory.SetLayoutElement(moreRow, minHeight: 22, flexibleWidth: 9999);
                    UIFactory.CreateLabel(moreRow, "ExportMoreText",
                        $"  …还有 {exportables.Count - shown} 个文件未列出，可用顶部「导出」下拉一键导出",
                        TextAnchor.MiddleLeft, Color.grey, false, 11);
                    exportItemRows.Add(moreRow);
                    break;
                }
                shown++;

                GameObject row = UIFactory.CreateUIObject("ExportItemRow", exportListHolder);
                UIFactory.SetLayoutElement(row, minHeight: 22, flexibleWidth: 9999);
                UIFactory.SetLayoutGroup<HorizontalLayoutGroup>(row, false, false, true, true, 3);

                // Toggle + 标签
                GameObject toggleObj = UIFactory.CreateToggle(row, "ExportItemToggle", out Toggle toggle, out Text label);
                toggle.isOn = item.IsChecked; // 默认 true
                // v16ai：同一模型批量导出时会有大量同名文件（texture_00 等），
                //        把所属模型/子目录显示在类型后面，便于区分与定位。
                string groupTag = string.IsNullOrEmpty(item.Group) ? "" : $"[{item.Group}] ";
                label.text = $"  {item.Type}: {groupTag}{item.Name}";
                if (label.text.Length > 60)
                    label.text = label.text.Substring(0, 57) + "...";
                label.fontSize = 11;
                label.color = Color.white;
                label.alignment = TextAnchor.MiddleLeft;

                AssetExporter.ExportableItem capturedItem = item;
                toggle.onValueChanged.AddListener((bool val) => capturedItem.IsChecked = val);

                // 整行可点击：切换勾选
                ButtonRef rowBtn = UIFactory.CreateButton(row, "ExportItemRowBtn", "", new Color(0.12f, 0.18f, 0.22f, 0.0f));
                UIFactory.SetLayoutElement(rowBtn.Component.gameObject, minHeight: 22, flexibleWidth: 9999);
                rowBtn.OnClick += () =>
                {
                    toggle.isOn = !toggle.isOn; // 触发 onValueChanged 同步 IsChecked
                };

                exportItemToggles.Add(toggle);
                exportItemRows.Add(row);
            }

            // v16z：子行创建完后，先重建 Content 让 VLG 给每行分配正确高度（避免挤成一行），
            //       再重建 ScrollView 根节点让动态高度生效。
            try
            {
                if (exportListHolder != null)
                    UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(exportListHolder.GetComponent<RectTransform>());
                if (exportListScrollRoot != null)
                    UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(exportListScrollRoot.GetComponent<RectTransform>());
            }
            catch { }
        }

        /// <summary>
        /// 下拉框选择导出模式时触发：仅记录所选模式并更新按钮文字，
        /// 实际导出由「执行导出」按钮触发。
        /// </summary>
        private void OnExportModeChanged(int index)
        {
            if (index < 0 || index >= ExportModeNames.Length)
                return;

            selectedExportMode = index;
            if (exportExecuteBtn != null)
                exportExecuteBtn.ButtonText.text = $"执行导出：{ExportModeNames[index]}";
        }

        // ============= Live2D 录制按钮处理（v12 → v16 移除 → v16i 重新加回单按钮 → v16k 修复 hook init bug）=============
        //   旧 v12 实现：OnRecordStart / OnRecordStop / OnRecordExport / UpdateRecordStatusUI
        //   v16: 用户改为插件加载自动 AutoStart（Live2DRecorder.AutoStartAllScenes 在 ExplorerCore.Init 末尾调用 + Tick 由 ExplorerCore.Update 每帧驱动）持续追踪 animation→audio，
        //        v16g: 加 auto-dump on model found（吵，被回滚）
        //        v16d: 加 5s auto-export（频，被回滚）
        //   v16i: 全部自动行为回滚，只保留 Inspector 上一个 "导出音频记录" 手动按钮（OnLive2DManualExport）
        //        导出走 Live2DRecorder.AutoExportMotionAudioMap() 程序化触发

        /// <summary>
        /// 按模式执行导出：0=全部，1=已勾选，2=未勾选。
        /// </summary>
        private void DoExportByMode(int mode)
        {
            if (currentExportables == null || currentExportables.Count == 0)
            {
                ExplorerCore.LogWarning("[导出] 当前无可提取资源");
                return;
            }

            List<AssetExporter.ExportableItem> toExport = new();
            foreach (AssetExporter.ExportableItem item in currentExportables)
            {
                bool check = item.IsChecked;
                if (mode == 0 || (mode == 1 && check) || (mode == 2 && !check))
                    toExport.Add(item);
            }

            if (toExport.Count == 0)
            {
                ExplorerCore.LogWarning($"[导出] 模式 {ExportModeNames[mode]}：没有符合的资源");
                return;
            }

            string dir = AssetExporter.GetDefaultOutputDir();
            int success = 0;
            foreach (AssetExporter.ExportableItem item in toExport)
            {
                try
                {
                    string path = System.IO.Path.Combine(dir, SanitizeFileName(item.Name));
                    item.ExportAction?.Invoke(path);
                    success++;
                }
                catch (System.Exception ex)
                {
                    ExplorerCore.LogWarning($"[导出] {item.Name} 失败: {ex.Message}");
                }
            }
            ExplorerCore.Log($"[导出] {ExportModeNames[mode]}：成功 {success}/{toExport.Count} 个到 {dir}");
        }

        private static string SanitizeFileName(string name)
        {
            foreach (char c in System.IO.Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name;
        }

        // Child and Comp Lists

        private void ConstructLists()
        {
            // v16aa：把原来的 listHolder 包进底部信息面板，加一个独立切换按钮。
            //   导出面板展开时自动收起底部内容，底部按钮始终可见，点击可再展开。
            //   v16ac(回退v16ad)：面板挂回 UIRoot（滚动区外，固定窗口底部）
            bottomInfoPanel = UIFactory.CreateUIObject("BottomInfoPanel", UIRoot);
            UIFactory.SetLayoutGroup<VerticalLayoutGroup>(bottomInfoPanel, false, false, true, true, 2);
            // 初始为展开态 → flexibleHeight 9999 吃掉剩余空间；收起时 SetBottomInfoExpanded 会改回 0
            UIFactory.SetLayoutElement(bottomInfoPanel, minHeight: 25, flexibleWidth: 9999, flexibleHeight: 9999);

            // 底部切换按钮行（始终可见）
            GameObject toggleRow = UIFactory.CreateUIObject("BottomToggleRow", bottomInfoPanel);
            UIFactory.SetLayoutGroup<HorizontalLayoutGroup>(toggleRow, false, false, true, true, 2);
            UIFactory.SetLayoutElement(toggleRow, minHeight: 25, flexibleWidth: 9999);
            bottomToggleBtn = UIFactory.CreateButton(toggleRow, "BottomToggleBtn", "子对象/组件 ▲", new Color(0.2f, 0.2f, 0.2f));
            UIFactory.SetLayoutElement(bottomToggleBtn.Component.gameObject, minHeight: 25, flexibleWidth: 9999);
            bottomToggleBtn.OnClick += ToggleBottomInfoPanel;

            // 底部内容区：子对象 + 组件
            bottomContentPanel = UIFactory.CreateUIObject("BottomContentPanel", bottomInfoPanel);
            UIFactory.SetLayoutGroup<HorizontalLayoutGroup>(bottomContentPanel, false, true, true, true, 8, 2, 2, 2, 2);
            // 初始为展开态 → flexibleHeight 9999；收起时 SetBottomInfoExpanded 会改回 0
            UIFactory.SetLayoutElement(bottomContentPanel, minHeight: 120, flexibleWidth: 9999, flexibleHeight: 9999);

            // 子对象 + 组件 双栏直接放进 bottomContentPanel
            GameObject listHolder = bottomContentPanel;

            // Left group (Children)
            GameObject leftGroup = UIFactory.CreateUIObject("ChildrenGroup", listHolder);
            UIFactory.SetLayoutElement(leftGroup, flexibleWidth: 9999, flexibleHeight: 9999);
            UIFactory.SetLayoutGroup<VerticalLayoutGroup>(leftGroup, false, false, true, true, 2);

            Text childrenLabel = UIFactory.CreateLabel(leftGroup, "ChildListTitle", "子对象", TextAnchor.MiddleCenter, default, false, 13);
            UIFactory.SetLayoutElement(childrenLabel.gameObject, flexibleWidth: 9999);

            // Add Child
            GameObject addChildRow = UIFactory.CreateUIObject("AddChildRow", leftGroup);
            UIFactory.SetLayoutGroup<HorizontalLayoutGroup>(addChildRow, false, false, true, true, 2);

            addChildInput = UIFactory.CreateInputField(addChildRow, "AddChildInput", "输入名称...");
            UIFactory.SetLayoutElement(addChildInput.Component.gameObject, minHeight: 25, preferredWidth: 9999);

            ButtonRef addChildButton = UIFactory.CreateButton(addChildRow, "AddChildButton", "添加子对象");
            UIFactory.SetLayoutElement(addChildButton.Component.gameObject, minHeight: 25, minWidth: 80);
            addChildButton.OnClick += () => { OnAddChildClicked(addChildInput.Text); };

            // TransformTree

            transformScroll = UIFactory.CreateScrollPool<TransformCell>(leftGroup, "TransformTree", out GameObject transformObj,
                out GameObject transformContent, new Color(0.11f, 0.11f, 0.11f));

            TransformTree = new TransformTree(transformScroll, GetTransformEntries, OnTransformCellClicked);

            // Right group (Components)

            GameObject rightGroup = UIFactory.CreateUIObject("ComponentGroup", listHolder);
            UIFactory.SetLayoutElement(rightGroup, flexibleWidth: 9999, flexibleHeight: 9999);
            UIFactory.SetLayoutGroup<VerticalLayoutGroup>(rightGroup, false, false, true, true, 2);

            Text compLabel = UIFactory.CreateLabel(rightGroup, "CompListTitle", "组件", TextAnchor.MiddleCenter, default, false, 13);
            UIFactory.SetLayoutElement(compLabel.gameObject, flexibleWidth: 9999);

            // Add Comp
            GameObject addCompRow = UIFactory.CreateUIObject("AddCompRow", rightGroup);
            UIFactory.SetLayoutGroup<HorizontalLayoutGroup>(addCompRow, false, false, true, true, 2);

            addCompInput = UIFactory.CreateInputField(addCompRow, "AddCompInput", "输入组件类型...");
            UIFactory.SetLayoutElement(addCompInput.Component.gameObject, minHeight: 25, preferredWidth: 9999);

            ButtonRef addCompButton = UIFactory.CreateButton(addCompRow, "AddCompButton", "添加组件");
            UIFactory.SetLayoutElement(addCompButton.Component.gameObject, minHeight: 25, minWidth: 80);
            addCompButton.OnClick += () => { OnAddComponentClicked(addCompInput.Text); };

            // comp autocompleter
            new TypeCompleter(typeof(Component), addCompInput, false, false, false);

            // Component List

            componentScroll = UIFactory.CreateScrollPool<ComponentCell>(rightGroup, "ComponentList", out GameObject compObj,
                out GameObject compContent, new Color(0.11f, 0.11f, 0.11f));
            UIFactory.SetLayoutElement(compObj, flexibleHeight: 9999);
            UIFactory.SetLayoutElement(compContent, flexibleHeight: 9999);

            ComponentList = new ComponentList(componentScroll, GetComponentEntries)
            {
                Parent = this
            };
            componentScroll.Initialize(ComponentList);
        }


        #endregion
    }
}
