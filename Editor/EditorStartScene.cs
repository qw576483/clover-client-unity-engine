using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CloverEngine.Editor
{
    /// <summary>
    /// 打开编辑器时把「启动场景」打开 —— 各工程共用这一份，工程侧不写任何脚本。
    ///
    /// <para><b>启动场景怎么定</b>：<see cref="EditorBuildSettings.scenes"/> 里第一条 enabled 的场景。
    /// 它本来就是各工程自己声明的首场景（如 Boot / Main），因此引擎侧**不写死任何场景路径**，
    /// 也不要求工程额外配置。</para>
    ///
    /// <para><b>为什么需要</b>：编辑器按 Play 用的是**当前打开的场景**，不是 Build Settings 首项。
    /// 开在关卡场景上点 Play 会整条跳过启动链路（`Game.Launch` / 流程装配都在启动场景里），
    /// 现象是「点 Play 直接进关卡、缺初始化」且**不报错** —— 最难查的一类问题。</para>
    ///
    /// <para><b>三条不打扰原则</b>：
    /// ① 批处理（CI / `-executeMethod`）下不动场景；
    /// ② 已经打开着启动场景（含多场景叠加）时不动，尊重用户当前的布局；
    /// ③ 当前场景**有未保存改动**时不动 —— `OpenScene` 会弹原生模态保存框，
    /// Editor 脚本里禁止弹模态（会卡住批处理与用户）。
    /// 另外**每次编辑器会话只切一次**（`SessionState` 记），改代码触发的域重载不会把人拽回启动场景。</para>
    ///
    /// <para><b>开关</b>：`Clover/编辑器启动场景/启用…`（存 EditorPrefs、按工程隔离，默认开）；
    /// 想立刻回去用 `Clover/编辑器启动场景/立即打开启动场景`。</para>
    /// </summary>
    internal static class EditorStartScene
    {
        private const string MenuEnabled = "Clover/编辑器启动场景/启用（打开编辑器时自动切到启动场景）";
        private const string MenuOpenNow = "Clover/编辑器启动场景/立即打开启动场景";

        private const string SessionDoneKey = "CloverEngine.Editor.EditorStartScene.Done";
        private const string SessionRetryKey = "CloverEngine.Editor.EditorStartScene.Retry";
        private const string EnabledKeyPrefix = "CloverEngine.Editor.EditorStartScene.Enabled.";

        /// <summary>首次导入/编译未结束时最多再等这么多帧（约 10 秒），超了本次会话就放弃。</summary>
        private const int MaxRetry = 600;

        [InitializeOnLoadMethod]
        private static void OnEditorLoad() => EditorApplication.delayCall += TryOpenStartSceneOnLoad;

        /// <summary>等首次导入稳定后再动手；未稳定就往后推，直到超限。</summary>
        private static void TryOpenStartSceneOnLoad()
        {
            if (SessionState.GetBool(SessionDoneKey, false))
                return;

            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                int retry = SessionState.GetInt(SessionRetryKey, 0);
                if (retry >= MaxRetry)
                    return;

                SessionState.SetInt(SessionRetryKey, retry + 1);
                EditorApplication.delayCall += TryOpenStartSceneOnLoad;
                return;
            }

            SessionState.SetBool(SessionDoneKey, true);

            if (IsEnabled)
                OpenStartScene("打开编辑器");
        }

        /// <summary>切到启动场景；任一条保护命中就只记日志、不改场景。</summary>
        private static void OpenStartScene(string reason)
        {
            string path = StartScenePath();
            if (string.IsNullOrEmpty(path))
            {
                Debug.LogWarning("[Clover] 未自动打开启动场景：EditorBuildSettings 里没有 enabled 的场景。");
                return;
            }

            if (Application.isBatchMode) return;                                        // CI / -executeMethod
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            if (IsSceneOpen(path)) return;

            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(path) == null)
            {
                Debug.LogWarning($"[Clover] 启动场景 `{path}` 取不到（文件缺失或尚未导入），跳过自动打开。");
                return;
            }

            string dirty = DirtyScenePath();
            if (dirty != null)
            {
                Debug.LogWarning($"[Clover] 场景 `{dirty}` 有未保存改动，不自动切到启动场景 `{path}`（避免弹保存框）。" +
                                 $"保存后可用菜单 `{MenuOpenNow}` 手动切。");
                return;
            }

            EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
            Debug.Log($"[Clover] 已打开启动场景 `{path}`（{reason}）。");
        }

        [MenuItem(MenuEnabled, false, 200)]
        private static void ToggleEnabled()
        {
            bool next = !IsEnabled;
            EditorPrefs.SetBool(EnabledKey, next);
            Debug.Log($"[Clover] 打开编辑器自动切到启动场景：{(next ? "已启用" : "已关闭")}");
        }

        [MenuItem(MenuEnabled, true)]
        private static bool ToggleEnabledChecked()
        {
            Menu.SetChecked(MenuEnabled, IsEnabled);
            return true;
        }

        [MenuItem(MenuOpenNow, false, 201)]
        private static void OpenNow() => OpenStartScene("手动菜单");

        /// <summary>启动场景 = Build Settings 里第一条 enabled 的场景；没有就返回 null。</summary>
        private static string StartScenePath()
        {
            foreach (EditorBuildSettingsScene scene in EditorBuildSettings.scenes)
                if (scene != null && scene.enabled && !string.IsNullOrEmpty(scene.path))
                    return scene.path;

            return null;
        }

        private static bool IsSceneOpen(string path)
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (scene.IsValid() && scene.path == path)
                    return true;
            }

            return false;
        }

        /// <summary>任一已打开场景有未保存改动就返回它的路径（未命名场景返回占位串）。</summary>
        private static string DirtyScenePath()
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (scene.IsValid() && scene.isDirty)
                    return string.IsNullOrEmpty(scene.path) ? "(未命名场景)" : scene.path;
            }

            return null;
        }

        private static bool IsEnabled => EditorPrefs.GetBool(EnabledKey, true);

        private static string EnabledKey => EnabledKeyPrefix + ProjectTag();

        /// <summary>
        /// 工程标识：EditorPrefs 是**机器全局**的（所有工程共用一份），必须按工程路径隔离，
        /// 否则在 A 工程关掉开关会连带把 B 工程也关掉。FNV-1a 32 位，同一工程恒定。
        /// </summary>
        private static string ProjectTag()
        {
            string root = Path.GetDirectoryName(Application.dataPath) ?? Application.dataPath;

            unchecked
            {
                uint hash = 2166136261u;
                foreach (char c in root)
                {
                    hash ^= c;
                    hash *= 16777619u;
                }

                return hash.ToString("x8");
            }
        }
    }
}
