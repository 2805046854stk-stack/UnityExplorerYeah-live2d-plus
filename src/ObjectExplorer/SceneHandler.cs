using UnityEngine.SceneManagement;
using UnityExplorer.Runtime;

namespace UnityExplorer.ObjectExplorer
{
    public static class SceneHandler
    {
        /// <summary>The currently inspected Scene.</summary>
        public static Scene? SelectedScene
        {
            get => selectedScene;
            internal set
            {
                if (selectedScene.HasValue && selectedScene == value)
                    return;
                selectedScene = value;
                OnInspectedSceneChanged?.Invoke((Scene)selectedScene);
            }
        }
        private static Scene? selectedScene;

        /// <summary>The GameObjects in the currently inspected scene.</summary>
        public static IEnumerable<GameObject> CurrentRootObjects { get; private set; } = new GameObject[0];

        /// <summary>All currently loaded Scenes.</summary>
        public static List<Scene> LoadedScenes { get; private set; } = new();
        //private static HashSet<Scene> previousLoadedScenes;

        /// <summary>The names of all scenes in the build settings, if they could be retrieved.</summary>
        public static List<string> AllSceneNames { get; private set; } = new();

        /// <summary>Invoked when the currently inspected Scene changes. The argument is the new scene.</summary>
        public static event Action<Scene> OnInspectedSceneChanged;

        /// <summary>Invoked whenever the list of currently loaded Scenes changes. The argument contains all loaded scenes after the change.</summary>
        public static event Action<List<Scene>> OnLoadedScenesUpdated;

        /// <summary>Generally will be 2, unless DontDestroyExists == false, then this will be 1.</summary>
        internal static int DefaultSceneCount => 1 + (DontDestroyExists ? 1 : 0);

        /// <summary>Whether or not we are currently inspecting the "HideAndDontSave" asset scene.</summary>
        public static bool InspectingAssetScene => SelectedScene.HasValue && SceneHandleHelper.Get(SelectedScene.Value) == -1;

        /// <summary>Whether or not we successfuly retrieved the names of the scenes in the build settings.</summary>
        public static bool WasAbleToGetScenesInBuild { get; private set; }

        /// <summary>Whether or not the "DontDestroyOnLoad" scene exists in this game.</summary>
        public static bool DontDestroyExists { get; private set; }

        internal static void Init()
        {
            // Check if the game has "DontDestroyOnLoad"
            // Use reflection because GetNameInternal was removed in newer Unity versions (e.g. Unity 6000+)
            try
            {
                Type sceneType = ReflectionUtility.GetTypeByName("UnityEngine.SceneManagement.Scene");
                if (sceneType == null)
                    throw new Exception("This version of Unity does not ship with the 'Scene' class, or it was not unstripped.");

                System.Reflection.MethodInfo method = sceneType.GetMethod("GetNameInternal",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                string sceneName = (string)method?.Invoke(null, new object[] { -12 });
                if (string.IsNullOrEmpty(sceneName))
                    throw new Exception("Scene.GetNameInternal returned null for DontDestroyOnLoad scene.");

                DontDestroyExists = sceneName == "DontDestroyOnLoad";
            }
            catch (Exception ex)
            {
                ExplorerCore.LogWarning($"Unable to check for DontDestroyOnLoad via Scene.GetNameInternal: {ex.Message}");
                ExplorerCore.LogWarning("Falling back to SceneManager.GetAllScenes() for DontDestroyOnLoad detection.");
#pragma warning disable CS0618
                DontDestroyExists = SceneManager.GetAllScenes().Any(s => s.name == "DontDestroyOnLoad");
#pragma warning restore CS0618
            }

            // Try to get all scenes in the build settings. This may not work.
            try
            {
                Type sceneUtil = ReflectionUtility.GetTypeByName("UnityEngine.SceneManagement.SceneUtility");
                if (sceneUtil == null)
                    throw new Exception("This version of Unity does not ship with the 'SceneUtility' class, or it was not unstripped.");

                System.Reflection.MethodInfo method = sceneUtil.GetMethod("GetScenePathByBuildIndex", ReflectionUtility.FLAGS);
                int sceneCount = SceneManager.sceneCountInBuildSettings;
                for (int i = 0; i < sceneCount; i++)
                {
                    string scenePath = (string)method.Invoke(null, new object[] { i });
                    AllSceneNames.Add(scenePath);
                }

                WasAbleToGetScenesInBuild = true;
            }
            catch (Exception ex)
            {
                WasAbleToGetScenesInBuild = false;
                ExplorerCore.LogWarning($"Unable to generate list of all Scenes in the build: {ex}");
            }
        }

        internal static void Update()
        {
            // Inspected scene will exist if it's DontDestroyOnLoad or HideAndDontSave
            bool inspectedExists =
                SelectedScene.HasValue
                && ((DontDestroyExists && SceneHandleHelper.Get(SelectedScene.Value) == -12)
                    || SceneHandleHelper.Get(SelectedScene.Value) == -1);

            LoadedScenes.Clear();

            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (scene == default || !scene.isLoaded || !scene.IsValid())
                    continue;

                // If we have not yet confirmed inspectedExists, check if this scene is our currently inspected one.
                if (!inspectedExists && scene == SelectedScene)
                    inspectedExists = true;

                LoadedScenes.Add(scene);
            }

            if (DontDestroyExists)
                LoadedScenes.Add(SceneHandleHelper.Create(-12));
            LoadedScenes.Add(SceneHandleHelper.Create(-1));

            // Default to first scene if none selected or previous selection no longer exists.
            if (!inspectedExists)
                SelectedScene = LoadedScenes.First();

            // Notify on the list changing at all
            OnLoadedScenesUpdated?.Invoke(LoadedScenes);

            // Finally, update the root objects list.
            if (SelectedScene != null && ((Scene)SelectedScene).IsValid())
                CurrentRootObjects = RuntimeHelper.GetRootGameObjects((Scene)SelectedScene);
            else
            {
                UnityEngine.Object[] allObjects = RuntimeHelper.FindObjectsOfTypeAll(typeof(GameObject));
                List<GameObject> objects = new();
                foreach (UnityEngine.Object obj in allObjects)
                {
                    GameObject go = obj.TryCast<GameObject>();
                    if (go.transform.parent == null && !go.scene.IsValid())
                        objects.Add(go);
                }
                CurrentRootObjects = objects;
            }
        }
    }
}
