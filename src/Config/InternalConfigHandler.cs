using Tomlet;
using Tomlet.Models;
using UnityExplorer.UI;

namespace UnityExplorer.Config
{
    public class InternalConfigHandler : ConfigHandler
    {
        internal static string CONFIG_PATH;

        public override void Init()
        {
            CONFIG_PATH = Path.Combine(ExplorerCore.ExplorerFolder, "data.cfg");
        }

        public override void LoadConfig()
        {
            if (!TryLoadConfig())
                SaveConfig();
        }

        public override void RegisterConfigElement<T>(ConfigElement<T> element)
        {
            // Not necessary
        }

        public override void SetConfigValue<T>(ConfigElement<T> element, T value)
        {
            // Not necessary
        }

        // Not necessary, just return the value.
        public override T GetConfigValue<T>(ConfigElement<T> element) => element.Value;

        // Always just auto-save.
        public override void OnAnyConfigChanged() => SaveConfig();

        public bool TryLoadConfig()
        {
            try
            {
                if (!File.Exists(CONFIG_PATH))
                    return false;

                TomlDocument document = TomlParser.ParseFile(CONFIG_PATH);
                foreach (string key in document.Keys)
                {
                    if (!Enum.IsDefined(typeof(UIManager.Panels), key))
                        continue;

                    UIManager.Panels panelKey = (UIManager.Panels)Enum.Parse(typeof(UIManager.Panels), key);
                    ConfigManager.GetPanelSaveData(panelKey).Value = document.GetString(key);
                }

                return true;
            }
            catch (Exception ex)
            {
                ExplorerCore.LogWarning("Error loading internal data: " + ex.ToString());
                return false;
            }
        }

        public override void SaveConfig()
        {
            if (UIManager.Initializing)
                return;

            TomlDocument tomlDocument = TomlDocument.CreateEmpty();
            foreach (KeyValuePair<string, IConfigElement> entry in ConfigManager.InternalConfigs)
                tomlDocument.Put(entry.Key, entry.Value.BoxedValue as string, false);

            string content = tomlDocument.SerializedValue;

            // v16r：保留非面板自定义条目（如 Live2DRecorder 的 "Happy Sugar Life"）。
            //   SaveConfig 是全量重写——不保留的话，用户/插件追加的自定义键会在任何面板操作后被抹掉，
            //   导致录制开关永远读不到（默认关闭）+ 手动写入的 松坂砂糖 丢失。
            //   文本级按行处理（不走 TOML 解析），键名 Trim 引号后不属于 Panels 枚举的原样带回。
            try
            {
                if (File.Exists(CONFIG_PATH))
                {
                    System.Collections.Generic.List<string> preserved = new System.Collections.Generic.List<string>();
                    foreach (string raw in File.ReadAllLines(CONFIG_PATH))
                    {
                        string line = raw == null ? "" : raw.Trim();
                        if (string.IsNullOrEmpty(line)) continue;
                        if (line.StartsWith("#") || line.StartsWith("//")) continue;
                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        string key = line.Substring(0, eq).Trim().Trim('"').Trim();
                        if (!Enum.IsDefined(typeof(UIManager.Panels), key))
                            preserved.Add(raw);
                    }
                    if (preserved.Count > 0)
                        content += string.Join(Environment.NewLine, preserved.ToArray()) + Environment.NewLine;
                }
            }
            catch { /* 保留失败不阻断正常保存 */ }

            File.WriteAllText(CONFIG_PATH, content);
        }
    }
}
