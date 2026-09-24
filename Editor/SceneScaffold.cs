// ─────────────────────────────────────────────────────────────────────────────
// CloverEngine.Editor · SceneScaffold —— 「最小可运行场景」脚手架（编辑器横切）
//
// 【范围】本件覆盖「最小可运行场景」全套：命令行入口（`-executeMethod` + 失败 Exit(1)）、
//   启动自愈体检、生成最小场景（相机 / 2D 灯光 / 命名根节点 / 入口脚本）、
//   幂等写 Build Settings、设 Play 起始场景、按名字反射解析入口类型。
//   面板空壳预制体那半**不在本件** —— 引擎已有 `Editor/PanelPrefabBuilder.cs`。
//
// 【通用性依据】「造一个能跑的最小场景 + 把它写进 Build Settings + 设成 Play 起始场景」
//   是**每个工程开工都要写一遍**的活 —— 引擎既有件只有预制体生成器（`PanelPrefabBuilder`）
//   与「打开时打开启动场景」（`EditorStartScene`），这三步没有件负责。
//
// 【用法】
//   代码：`SceneScaffold.Create("Assets/Scenes/Boot.unity", new SceneScaffoldOptions{...}, out var err)`
//   菜单：`Clover/场景脚手架/生成最小可运行场景…`
//   批处理：`unity -executeMethod CloverEngine.Editor.SceneScaffold.ExecuteFromCommandLine -scene Assets/Scenes/Boot.unity`
//
// 【已知边界】⛔ 不写任何业务取值：场景名 / 相机参数 / 根节点名 / 入口类型全部由调用方给。
//   ⛔ 2D 灯光（URP `Light2D`）**用反射可选接入** —— Editor 程序集只引用引擎自己的程序集，
//   硬引用 URP 会让没装 URP 的工程编不过；拿不到类型就跳过并留一行日志。
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace CloverEngine.Editor
{
    /// <summary>场景脚手架参数（⛔ 引擎侧一个业务默认值都不带，全部由调用方给）。</summary>
    public struct SceneScaffoldOptions
    {
        /// <summary>主相机正交尺寸。</summary>
        public float OrthoSize;

        /// <summary>主相机 Z（2D 工程一般是负值，保证画在实体前面）。</summary>
        public float CameraZ;

        /// <summary>地图根节点名（空 ⇒ 不建）。</summary>
        public string MapRootName;

        /// <summary>实体根节点名（空 ⇒ 不建）。</summary>
        public string EntityRootName;

        /// <summary>入口脚本类型全名（空 ⇒ 不挂；找不到 ⇒ 报错返回 false）。</summary>
        public string EntryTypeName;

        /// <summary>是否创建 URP 2D 灯光（拿不到 `Light2D` 类型时自动跳过）。</summary>
        public bool Create2DLight;

        /// <summary>相机是否用纯色清屏（false = 保留 Unity 默认的天空盒）。</summary>
        public bool SolidColorBackground;

        /// <summary>纯色清屏的颜色（配合 <see cref="SolidColorBackground"/>）。</summary>
        public Color ClearColor;

        /// <summary>近裁剪面（≤0 用默认 0.3）。</summary>
        public float NearClip;

        /// <summary>远裁剪面（≤0 用默认 1000）。</summary>
        public float FarClip;

        /// <summary>是否反射加 URP 的 `UniversalAdditionalCameraData`（拿不到不算错，URP 渲染时会自己补）。</summary>
        public bool AddUrpCameraData;

        /// <summary>
        /// 是否用 **Additive** 模式建场景（true = 不替换用户当前打开的场景，避免弹保存框）。
        /// <para>配合 <see cref="CloseAfterSave"/>：存完立刻关掉临时场景。</para>
        /// </summary>
        public bool AdditiveMode;

        /// <summary>保存后是否立刻关闭该场景（只在 <see cref="AdditiveMode"/> 下有意义）。</summary>
        public bool CloseAfterSave;

        /// <summary>是否把该场景写进 `EditorBuildSettings`（幂等）。</summary>
        public bool ApplyBuildSettings;

        /// <summary>是否把它设为 Play 起始场景（幂等）。</summary>
        public bool SetPlayModeStartScene;
    }

    /// <summary>最小可运行场景脚手架（编辑器横切；⛔ 不含任何业务取值）。</summary>
    public static class SceneScaffold
    {
        private const string MenuRoot = "Clover/场景脚手架/";

        /// <summary>
        /// 生成（覆盖）一个最小可运行场景并保存到 <paramref name="scenePath"/>。
        /// <para>内容 = 主相机（正交 + `MainCamera` tag + `AudioListener`）+ 可选 2D 灯光
        /// + 命名根节点 + 入口脚本；随后按选项写 Build Settings / Play 起始场景。</para>
        /// </summary>
        public static bool Create(string scenePath, SceneScaffoldOptions o, out string error)
        {
            error = null;

            if (string.IsNullOrEmpty(scenePath))
            {
                error = "scenePath 为空";
                return false;
            }

            var dir = Path.GetDirectoryName(scenePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,
                o.AdditiveMode ? NewSceneMode.Additive : NewSceneMode.Single);

            // Additive 模式下新场景里的对象默认不在该场景（NewScene 的根对象已属它，但
            // `new GameObject(...)` 会落进当前**活动**场景）⇒ 统一移到新场景里。
            var camGo = new GameObject("Main Camera");
            if (o.AdditiveMode) EditorSceneManager.MoveGameObjectToScene(camGo, scene);
            camGo.tag = "MainCamera";
            var cam = camGo.AddComponent<Camera>();
            cam.orthographic = true;
            cam.orthographicSize = o.OrthoSize > 0f ? o.OrthoSize : 5f;
            cam.nearClipPlane = o.NearClip > 0f ? o.NearClip : 0.3f;
            cam.farClipPlane = o.FarClip > 0f ? o.FarClip : 1000f;
            if (o.SolidColorBackground)
            {
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = o.ClearColor;
            }

            camGo.transform.position = new Vector3(0f, 0f, o.CameraZ);
            camGo.AddComponent<AudioListener>();

            if (o.AddUrpCameraData)
            {
                var urpCamData = FindTypeByName("UnityEngine.Rendering.Universal.UniversalAdditionalCameraData");
                if (urpCamData != null) camGo.AddComponent(urpCamData);
            }

            // ── 可选 2D 灯光（反射，避免硬依赖 URP）────────────────────────────
            if (o.Create2DLight)
            {
                var light2D = FindTypeByName("UnityEngine.Rendering.Universal.Light2D")
                              ?? FindTypeBySimpleName("Light2D");
                if (light2D != null && typeof(Component).IsAssignableFrom(light2D))
                {
                    var go = CreateInScene(scene, "Global Light 2D", o.AdditiveMode);
                    go.transform.SetParent(null);
                    go.AddComponent(light2D);
                }
                else
                {
                    LogThrottle.WarnOnce("SceneScaffold", "light2d.missing",
                        "拿不到 `Light2D` 类型（未装 URP？）⇒ 跳过 2D 灯光；场景仍可用，只是没有全局光");
                }
            }

            // ── 命名根节点 ────────────────────────────────────────────────────
            if (!string.IsNullOrEmpty(o.MapRootName)) CreateInScene(scene, o.MapRootName, o.AdditiveMode);
            if (!string.IsNullOrEmpty(o.EntityRootName)) CreateInScene(scene, o.EntityRootName, o.AdditiveMode);

            // ── 入口脚本 ──────────────────────────────────────────────────────
            if (!string.IsNullOrEmpty(o.EntryTypeName))
            {
                var entry = FindTypeByName(o.EntryTypeName);
                if (entry == null)
                {
                    error = "入口脚本类型找不到：" + o.EntryTypeName +
                            "（提示：Editor 工具不应依赖业务程序集；请确认它已编译）";
                    CloseIfNeeded(scene, o);
                    return false;
                }

                CreateInScene(scene, entry.Name, o.AdditiveMode).AddComponent(entry);
            }

            if (!EditorSceneManager.SaveScene(scene, scenePath))
            {
                error = "保存场景失败：" + scenePath;
                CloseIfNeeded(scene, o);
                return false;
            }

            CloseIfNeeded(scene, o);
            if (o.ApplyBuildSettings) ApplyBuildSettings(new[] { scenePath });
            if (o.SetPlayModeStartScene) SetPlayModeStartScene(scenePath);

            AssetDatabase.Refresh();
            return true;
        }

        /// <summary>造一个 GameObject；<paramref name="additive"/> = true 时把它移进指定场景（Additive 模式必需）。</summary>
        private static GameObject CreateInScene(UnityEngine.SceneManagement.Scene scene, string name, bool additive)
        {
            var go = new GameObject(name);
            if (additive && scene.IsValid() && scene.isLoaded) EditorSceneManager.MoveGameObjectToScene(go, scene);
            return go;
        }

        /// <summary>按选项关掉临时场景（Additive 模式 + <see cref="SceneScaffoldOptions.CloseAfterSave"/>）。</summary>
        private static void CloseIfNeeded(UnityEngine.SceneManagement.Scene scene, SceneScaffoldOptions o)
        {
            if (!o.AdditiveMode || !o.CloseAfterSave) return;
            if (scene.IsValid() && scene.isLoaded) EditorSceneManager.CloseScene(scene, true);
        }

        /// <summary>
        /// 写 Build Settings：给定场景按序在最前，**返回是否发生了写入**（幂等 ⇒ 已正确时返回 false 且不写）。
        /// <para><paramref name="replace"/> = true ⇒ 结果**只有**给定场景（按序，全 enabled）；
        /// false ⇒ 放在最前，其余既有项按原相对次序保留。</para>
        /// <para>依据：`Game.Scene.Load(name)` 走 Unity 场景加载 ⇒ 场景必须在 Build Settings 里。</para>
        /// </summary>
        public static bool ApplyBuildSettings(IList<string> scenePaths, bool replace = false)
        {
            if (scenePaths == null || scenePaths.Count == 0)
            {
                LogThrottle.WarnOnce("SceneScaffold", "buildsettings.empty",
                    "ApplyBuildSettings 传入空清单 ⇒ 本次不写 Build Settings");
                return false;
            }

            var wanted = new HashSet<string>(scenePaths);
            var result = new List<EditorBuildSettingsScene>(scenePaths.Count + EditorBuildSettings.scenes.Length);
            foreach (var p in scenePaths) result.Add(new EditorBuildSettingsScene(p, true));

            if (!replace)
            {
                foreach (var s in EditorBuildSettings.scenes)
                {
                    if (s != null && !wanted.Contains(s.path)) result.Add(s);
                }
            }

            var current = EditorBuildSettings.scenes;
            var same = current != null && current.Length == result.Count;
            if (same)
            {
                for (var i = 0; i < result.Count; i++)
                {
                    if (current[i].path != result[i].path || !current[i].enabled) { same = false; break; }
                }
            }

            if (same) return false;

            EditorBuildSettings.scenes = result.ToArray();
            return true;
        }

        /// <summary>
        /// 把某场景设为 Play 起始场景；**返回是否发生了写入**。
        /// <para><paramref name="onlyIfNull"/> = true ⇒ 只在当前为空时设置（自愈路径：不覆盖用户自己的选择）。</para>
        /// <para>找不到场景资产 ⇒ 限频告警并返回 false（保持原值，⛔ 不抛）。</para>
        /// </summary>
        public static bool SetPlayModeStartScene(string scenePath, bool onlyIfNull = false)
        {
            var asset = AssetDatabase.LoadAssetAtPath<SceneAsset>(scenePath);
            if (asset == null)
            {
                LogThrottle.WarnOnce("SceneScaffold", "playstart.missing:" + scenePath,
                    $"找不到场景资产 ⇒ Play 起始场景保持原值：{scenePath}");
                return false;
            }

            if (EditorSceneManager.playModeStartScene == asset) return false;
            if (onlyIfNull && EditorSceneManager.playModeStartScene != null) return false;

            EditorSceneManager.playModeStartScene = asset;
            return true;
        }

        // ── 反射解析（Editor 工具不依赖业务程序集）────────────────────────────

        /// <summary>按**全名**找类型（拿不到 ⇒ null）。用途：Editor 工具在业务程序集编译失败时仍能给出诊断。</summary>
        public static Type FindTypeByName(string fullName)
        {
            if (string.IsNullOrEmpty(fullName)) return null;

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType(fullName, false);
                if (t != null) return t;
            }

            return null;
        }

        /// <summary>按**简名**找类型（同名多个 ⇒ 取第一个并限频告警，避免静默选错）。</summary>
        public static Type FindTypeBySimpleName(string simpleName)
        {
            if (string.IsNullOrEmpty(simpleName)) return null;

            Type found = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (Exception) { continue; }   // 反射型加载失败的程序集：跳过（不是错误）

                foreach (var t in types)
                {
                    if (t.Name != simpleName) continue;

                    if (found != null)
                    {
                        LogThrottle.WarnOnce("SceneScaffold", "type.ambiguous:" + simpleName,
                            $"简名 `{simpleName}` 命中多个类型 ⇒ 取第一个（{found.FullName}，忽略 {t.FullName}）；" +
                            "生产路径请用全名");
                        return found;
                    }

                    found = t;
                }
            }

            return found;
        }

        // ── 菜单 / 命令行 / 启动自愈 ──────────────────────────────────────────

        [MenuItem(MenuRoot + "生成最小可运行场景…", false, 230)]
        private static void MenuCreate()
        {
            var path = EditorUtility.SaveFilePanelInProject("生成最小可运行场景", "Boot", "unity",
                "选择保存位置", "Assets/Scenes");
            if (string.IsNullOrEmpty(path)) return;

            var entry = Selection.activeObject != null ? Selection.activeObject.name : null;
            var o = new SceneScaffoldOptions
            {
                OrthoSize = 5f,
                CameraZ = -10f,
                MapRootName = "MapRoot",
                EntityRootName = "EntityRoot",
                EntryTypeName = null,
                Create2DLight = true,
                ApplyBuildSettings = true,
                SetPlayModeStartScene = true
            };

            if (!Create(path, o, out var err))
            {
                Debug.LogError("[SceneScaffold] " + err);
                return;
            }

            Debug.Log("[SceneScaffold] 已生成：" + path + "（入口脚本未挂 —— 用 " + entry + " 自行添加）");
        }

        /// <summary>批处理入口：`unity -executeMethod CloverEngine.Editor.SceneScaffold.ExecuteFromCommandLine -scene &lt;path&gt;`。</summary>
        public static void ExecuteFromCommandLine()
        {
            var scenePath = ArgValue("-scene") ?? "Assets/Scenes/Boot.unity";
            var o = new SceneScaffoldOptions
            {
                OrthoSize = ArgFloat("-ortho", 5f),
                CameraZ = ArgFloat("-cameraZ", -10f),
                MapRootName = ArgValue("-mapRoot") ?? "MapRoot",
                EntityRootName = ArgValue("-entityRoot") ?? "EntityRoot",
                EntryTypeName = ArgValue("-entry"),
                Create2DLight = true,
                ApplyBuildSettings = true,
                SetPlayModeStartScene = true
            };

            if (Create(scenePath, o, out var err))
            {
                Debug.Log("[SceneScaffold] CLI 生成成功：" + scenePath);
                return;
            }

            Debug.LogError("[SceneScaffold] CLI 生成失败：" + err);
            // `Exit()` 只在批处理下真退出（Editor 里调它会直接把编辑器关掉）
            if (Application.isBatchMode) EditorApplication.Exit(1);
        }

        /// <summary>
        /// 启动自愈体检：只在**缺**的时候发声（Build Settings 为空 ⇒ `Game.Scene.Load` 必失败），
        /// 正常工程**零日志**。廉价（一次长度判断），用 `delayCall` 避开加载期。
        /// </summary>
        [InitializeOnLoadMethod]
        private static void ScheduleSelfCheck()
        {
            EditorApplication.delayCall += () =>
            {
                if (EditorBuildSettings.scenes.Length == 0)
                {
                    LogThrottle.WarnOnce("SceneScaffold", "selfcheck.no-scenes",
                        "Build Settings 里一个场景都没有 ⇒ `Game.Scene.Load(name)` 会失败；" +
                        "用 `Clover/场景脚手架/生成最小可运行场景…` 生成一个");
                }
            };
        }

        private static string ArgValue(string flag)
        {
            var args = Environment.GetCommandLineArgs();
            for (var i = 0; i + 1 < args.Length; i++)
            {
                if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            }

            return null;
        }

        private static float ArgFloat(string flag, float fallback)
        {
            var raw = ArgValue(flag);
            return raw != null && float.TryParse(raw, out var v) ? v : fallback;
        }
    }
}
