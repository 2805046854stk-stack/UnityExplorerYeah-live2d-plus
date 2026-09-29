using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityExplorer.Runtime;
using UniverseLib.Input;
using UniverseLib.UI;
using UniverseLib.UI.Models;

namespace UnityExplorer.UI
{
    /// <summary>
    /// 右键上下文菜单：在对象树中右键点击 GameObject 时显示资源导出选项
    /// </summary>
    public static class ContextMenuHelper
    {
        private static GameObject menuRoot;
        private static GameObject contentHolder;
        private static GameObject targetObject;
        private static bool isShowing;

        private static readonly List<GameObject> menuItems = new();

        /// <summary>
        /// 初始化右键菜单 UI
        /// </summary>
        public static void Init()
        {
            if (menuRoot)
                return;

            // 创建菜单根对象，挂在 UIRoot 下，设置为最高层级
            menuRoot = UIFactory.CreateUIObject("ContextMenu", UIManager.UIRoot);
            RectTransform menuRect = menuRoot.GetComponent<RectTransform>();
            menuRect.anchorMin = new Vector2(0, 0);
            menuRect.anchorMax = new Vector2(0, 0);
            menuRect.pivot = new Vector2(0, 1); // 左上角为锚点
            menuRect.sizeDelta = new Vector2(280, 0);

            // 背景
            Image bg = menuRoot.AddComponent<Image>();
            bg.color = new Color(0.12f, 0.12f, 0.12f, 0.98f);

            // 内容容器
            contentHolder = UIFactory.CreateVerticalGroup(menuRoot, "Content", false, false, true, true, 2, new Vector4(3, 3, 3, 3),
                new Color(0, 0, 0, 0), TextAnchor.UpperLeft);

            menuRoot.SetActive(false);
        }

        /// <summary>
        /// 在指定位置显示右键菜单
        /// </summary>
        public static void Show(Vector2 screenPosition, GameObject target)
        {
            if (!menuRoot)
                Init();

            targetObject = target;

            // 收集可导出资源
            List<AssetExporter.ExportableItem> exportables = AssetExporter.CollectExportables(target);

            // 检测资产类型并按类型过滤（各自只导出各自对应的文件）
            try
            {
                AssetExporter.AssetKind kind = AssetExporter.DetectAssetKind(target);
                AssetExporter.FilterItemsByKind(exportables, kind);
            }
            catch { }

            // 清除旧菜单项
            foreach (GameObject item in menuItems)
            {
                if (item)
                    UnityEngine.Object.Destroy(item);
            }
            menuItems.Clear();

            // 标题
            GameObject titleObj = UIFactory.CreateUIObject("Title", contentHolder);
            UIFactory.SetLayoutElement(titleObj, minHeight: 22, flexibleWidth: 9999);
            UIFactory.CreateLabel(titleObj, "TitleText",
                $"<b><color=#00FFFF>{target.name}</color></b>  右键菜单",
                TextAnchor.MiddleLeft, Color.white, false, 12);
            menuItems.Add(titleObj);

            // 分隔线
            menuItems.Add(CreateSeparator());

            if (exportables.Count == 0)
            {
                GameObject noItem = UIFactory.CreateUIObject("NoItem", contentHolder);
                UIFactory.SetLayoutElement(noItem, minHeight: 25, flexibleWidth: 9999);
                UIFactory.CreateLabel(noItem, "NoText", "  （无可导出资源）",
                    TextAnchor.MiddleLeft, Color.grey, false, 12);
                menuItems.Add(noItem);
            }
            else
            {
                // "导出全部" 按钮
                ButtonRef exportAllBtn = UIFactory.CreateButton(contentHolder, "ExportAllBtn",
                    $"导出全部 ({exportables.Count}个资源)", new Color(0.2f, 0.35f, 0.2f));
                UIFactory.SetLayoutElement(exportAllBtn.Component.gameObject, minHeight: 25, flexibleWidth: 9999);
                exportAllBtn.OnClick += () =>
                {
                    string dir = AssetExporter.GetDefaultOutputDir();
                    foreach (AssetExporter.ExportableItem item in exportables)
                    {
                        string path = System.IO.Path.Combine(dir, SanitizeFileName(item.Name));
                        item.ExportAction?.Invoke(path);
                    }
                    Hide();
                };
                menuItems.Add(exportAllBtn.Component.gameObject);

                menuItems.Add(CreateSeparator());

                // 各个资源的导出按钮
                foreach (AssetExporter.ExportableItem item in exportables)
                {
                    string label = $"  {item.Type}: {item.Name}";
                    if (label.Length > 48)
                        label = label.Substring(0, 45) + "...";

                    ButtonRef btn = UIFactory.CreateButton(contentHolder, "ExportBtn", label, new Color(0.15f, 0.2f, 0.25f));
                    UIFactory.SetLayoutElement(btn.Component.gameObject, minHeight: 22, flexibleWidth: 9999);
                    btn.ButtonText.alignment = TextAnchor.MiddleLeft;
                    btn.ButtonText.fontSize = 11;

                    AssetExporter.ExportableItem capturedItem = item;
                    btn.OnClick += () =>
                    {
                        string path = System.IO.Path.Combine(AssetExporter.GetDefaultOutputDir(),
                            SanitizeFileName(capturedItem.Name));
                        capturedItem.ExportAction?.Invoke(path);
                        Hide();
                    };
                    menuItems.Add(btn.Component.gameObject);
                }
            }

            menuItems.Add(CreateSeparator());

            // "检查对象" 按钮
            ButtonRef inspectBtn = UIFactory.CreateButton(contentHolder, "InspectBtn", "检查对象", new Color(0.2f, 0.2f, 0.3f));
            UIFactory.SetLayoutElement(inspectBtn.Component.gameObject, minHeight: 25, flexibleWidth: 9999);
            inspectBtn.OnClick += () =>
            {
                InspectorManager.Inspect(target);
                Hide();
            };
            menuItems.Add(inspectBtn.Component.gameObject);

            // "复制名称" 按钮
            ButtonRef copyBtn = UIFactory.CreateButton(contentHolder, "CopyBtn", "复制对象名称", new Color(0.2f, 0.2f, 0.3f));
            UIFactory.SetLayoutElement(copyBtn.Component.gameObject, minHeight: 25, flexibleWidth: 9999);
            copyBtn.OnClick += () =>
            {
                GUIUtility.systemCopyBuffer = target.name;
                ExplorerCore.Log($"已复制: {target.name}");
                Hide();
            };
            menuItems.Add(copyBtn.Component.gameObject);

            // 定位菜单
            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                UIManager.UIRootRect, screenPosition, null, out Vector2 localPos);

            RectTransform menuRect = menuRoot.GetComponent<RectTransform>();

            // 确保菜单不超出屏幕
            float menuWidth = 280f;
            float menuHeight = 30f + menuItems.Count * 24f;
            Vector2 screenSize = new Vector2(Screen.width, Screen.height);

            if (localPos.x + menuWidth > screenSize.x)
                localPos.x = screenSize.x - menuWidth - 5;
            if (localPos.y - menuHeight < -screenSize.y)
                localPos.y = -screenSize.y + menuHeight + 5;

            menuRect.anchoredPosition = localPos;

            // 设置为最后一个兄弟（最上层显示）
            menuRoot.transform.SetAsLastSibling();
            menuRoot.SetActive(true);
            isShowing = true;
        }

        /// <summary>
        /// 创建分隔线
        /// </summary>
        static GameObject CreateSeparator()
        {
            GameObject sep = UIFactory.CreateUIObject("Separator", contentHolder);
            UIFactory.SetLayoutElement(sep, minHeight: 2, flexibleWidth: 9999);
            Image sepImg = sep.AddComponent<Image>();
            sepImg.color = new Color(0.3f, 0.3f, 0.3f, 0.5f);
            return sep;
        }

        /// <summary>
        /// 隐藏右键菜单
        /// </summary>
        public static void Hide()
        {
            if (menuRoot)
                menuRoot.SetActive(false);
            isShowing = false;
            targetObject = null;
        }

        /// <summary>
        /// 每帧更新（检测点击外部关闭）
        /// </summary>
        public static void Update()
        {
            if (!isShowing || !menuRoot)
                return;

            // 按 Esc 关闭
            if (InputManager.GetKeyDown(KeyCode.Escape))
            {
                Hide();
                return;
            }

            // 左键点击外部关闭
            if (InputManager.GetMouseButtonDown(0))
            {
                if (EventSystem.current == null)
                {
                    Hide();
                    return;
                }

                PointerEventData ped = new(EventSystem.current)
                {
                    position = InputManager.MousePosition
                };
#if CPP
                Il2CppSystem.Collections.Generic.List<RaycastResult> results = new();
#else
                List<RaycastResult> results = new();
#endif
                EventSystem.current.RaycastAll(ped, results);

                bool clickedInside = false;
                foreach (RaycastResult result in results)
                {
                    if (result.gameObject != null && result.gameObject.transform.IsChildOf(menuRoot.transform))
                    {
                        clickedInside = true;
                        break;
                    }
                }

                if (!clickedInside)
                    Hide();
            }
        }

        /// <summary>
        /// 清理文件名中的非法字符
        /// </summary>
        static string SanitizeFileName(string name)
        {
            foreach (char c in System.IO.Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name;
        }
    }
}
