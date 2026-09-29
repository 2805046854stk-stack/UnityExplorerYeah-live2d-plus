using System.Reflection;
using UnityEngine.SceneManagement;

namespace UnityExplorer.Runtime
{
    /// <summary>
    /// 运行时反射访问 Scene.handle / m_Handle。
    /// Unity 6000+ 中 Scene 结构体的字段可见性和程序集归属发生变化，
    /// 编译时直接绑定 m_Handle 会导致运行时 MissingFieldException。
    /// 用反射在运行时动态查找字段，兼容各 Unity 版本。
    /// </summary>
    internal static class SceneHandleHelper
    {
        private static FieldInfo _handleField;
        private static bool _initialized;

        private static void Init()
        {
            if (_initialized)
                return;
            _initialized = true;

            Type sceneType = typeof(Scene);

            // 优先尝试已知字段名
            string[] candidates = { "m_Handle", "m_handle", "handle" };
            foreach (string name in candidates)
            {
                _handleField = sceneType.GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                if (_handleField != null && _handleField.FieldType == typeof(int))
                    return;
            }

            // 兜底：找第一个 int 实例字段
            foreach (FieldInfo fi in sceneType.GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance))
            {
                if (fi.FieldType == typeof(int))
                {
                    _handleField = fi;
                    return;
                }
            }
        }

        /// <summary>获取 Scene 的内部 handle 值。</summary>
        public static int Get(Scene scene)
        {
            Init();
            if (_handleField == null)
                return -1;
            return (int)_handleField.GetValue(scene);
        }

        /// <summary>创建一个带有指定 handle 的 Scene 结构体。</summary>
        public static Scene Create(int handle)
        {
            Init();
            Scene scene = default;
            if (_handleField != null)
            {
                object boxed = scene;
                _handleField.SetValue(boxed, handle);
                scene = (Scene)boxed;
            }
            return scene;
        }
    }
}
