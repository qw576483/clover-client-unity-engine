using System;
using UnityEditor;
using UnityEngine;

namespace CloverEngine.Editor
{
    /// <summary>
    /// 地图烘焙面板（<c>Clover/地图烘焙/打开烘焙窗口</c>）。
    ///
    /// 为什么要有面板，而不是只有菜单项：菜单项的参数是写死的，换一张美术地图就得改代码。
    /// 面板把「场景 → 数据」这条链路上的每个旋钮摊开：选场景、定 scene_id / 格边长 / 原点 / 尺寸 /
    /// 烘焙阈值 / 输出目录，点一下就导出，并当场显示「导出 + 回读自检」的结果。
    ///
    /// 与命令行等价：面板只是 <see cref="MapBaker"/> 静态方法的 UI 壳 ——
    /// CI 里走 <c>-executeMethod CloverEngine.Editor.MapBaker.Export</c> 是同一段代码，
    /// 用的是同一次配置（参数持久化在 EditorPrefs）。
    /// </summary>
    public sealed class MapBakeWindow : EditorWindow
    {
        private MapBakeOptions _o;
        private string _result;
        private bool _ok;
        private Vector2 _scroll;

        public static void ShowWindow()
        {
            var w = GetWindow<MapBakeWindow>("Clover 地图烘焙");
            w.minSize = new Vector2(500, 620);
            w.Show();
        }

        private void OnEnable() => _o = MapBakeOptions.Load();

        private void OnDisable() => _o?.Save();

        private void OnGUI()
        {
            if (_o == null) _o = new MapBakeOptions();

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            // try/finally 保证任何异常（如推算包围盒时场景读取失败）都不会让 EndScrollView 被跳过 ——
            // 否则异常穿出 OnGUI 后 IMGUI 的布局栈从此错位，整个窗口的控件都会陆续报错。
            try
            {
                EditorGUILayout.HelpBox(
                    "把 Unity 关卡烘焙成 CloverMap 二进制地图数据（服务端 + 客户端同一份字节）。\n" +
                    "输出：" + _o.ServerDir + "/<名字>.bytes（服务端）与 " + _o.ClientDir + "/<名字>.bytes（客户端）。\n" +
                    "改了场景几何必须重新烘焙，否则服务端还在用旧地图。\n" +
                    "参数会保存下来，命令行 -executeMethod 复用这一份。",
                    MessageType.Info);

                EditorGUILayout.Space();

                // ---------------- 输入 ----------------
                EditorGUILayout.LabelField("① 输入场景", EditorStyles.boldLabel);
                DrawSceneField();
                EditorGUILayout.Space();

                // ---------------- 数据契约 ----------------
                EditorGUILayout.LabelField("② 数据契约（写进文件头）", EditorStyles.boldLabel);
                _o.MapName = EditorGUILayout.TextField("地图名", _o.MapName);
                _o.SceneId = (ulong)Math.Max(0, EditorGUILayout.LongField("scene_id（逻辑地图 id）", (long)_o.SceneId));
                _o.CellSize = EditorGUILayout.FloatField("格边长（米）", _o.CellSize);
                _o.Origin = EditorGUILayout.Vector3Field("原点（格子 (0,0) 的角）", _o.Origin);
                _o.MapWidth = EditorGUILayout.IntField("宽（格，东西）", _o.MapWidth);
                _o.MapDepth = EditorGUILayout.IntField("深（格，南北）", _o.MapDepth);

                if (GUILayout.Button("按场景包围盒推算原点与尺寸（换美术地图时用）"))
                {
                    // 推算失败必须显示为**失败态**（旧实现无条件 _ok = true，失败原因用 Info 蓝框显示，
                    // 用户会把"场景不存在 / 没有几何"当成推算成功）。
                    try
                    {
                        _ok = MapBaker.TryFitBoundsToScene(_o, out _result);
                    }
                    catch (Exception e)
                    {
                        _ok = false;
                        _result = "推算异常：" + e.Message;
                        Debug.LogException(e);
                    }
                    Repaint();
                }

                EditorGUILayout.Space();

                // ---------------- 烘焙规则 ----------------
                EditorGUILayout.LabelField("③ 烘焙规则", EditorStyles.boldLabel);
                _o.GroundTopY = EditorGUILayout.FloatField("地面顶面 Y", _o.GroundTopY);
                _o.ObstacleMinHeight = EditorGUILayout.FloatField("障碍最小高度（低于它算地面）", _o.ObstacleMinHeight);
                // 取样柱以地面为基准：两个值是**相对地面顶面**的高度（见 MapBakeOptions 字段注释）。
                _o.ProbeBottomY = EditorGUILayout.FloatField("取样柱底面（相对地面高度，米）", _o.ProbeBottomY);
                _o.ProbeTopY = EditorGUILayout.FloatField("取样柱顶面（相对地面高度，米）", _o.ProbeTopY);

                EditorGUILayout.Space();

                // ---------------- 层过滤（多层地图）----------------
                EditorGUILayout.LabelField("③b 层过滤（多层地图：逐层各烘一份）", EditorStyles.boldLabel);
                _o.LayerFilterEnabled = EditorGUILayout.Toggle("启用层过滤（关=现状行为）", _o.LayerFilterEnabled);
                using (new EditorGUI.DisabledScope(!_o.LayerFilterEnabled))
                {
                    _o.LayerMin = EditorGUILayout.IntSlider("参与烘焙的 Layer 下界", _o.LayerMin, 0, 31);
                    _o.LayerMax = EditorGUILayout.IntSlider("参与烘焙的 Layer 上界", _o.LayerMax, 0, 31);
                    _o.LayerMinY = EditorGUILayout.FloatField("高度带下界 Y（世界，米）", _o.LayerMinY);
                    _o.LayerMaxY = EditorGUILayout.FloatField("高度带上界 Y（≤下界 = 不设上界）", _o.LayerMaxY);
                }
                EditorGUILayout.LabelField("", "高度带按「垂直跨度相交」判定：跨层的大墙仍算障碍，只有整层在带外的被排除",
                    EditorStyles.miniLabel);

                EditorGUILayout.Space();

                // ---------------- 输出 ----------------
                EditorGUILayout.LabelField("④ 输出位置", EditorStyles.boldLabel);
                _o.ServerDir = EditorGUILayout.TextField("服务端目录", _o.ServerDir);
                _o.ClientDir = EditorGUILayout.TextField("客户端目录（Resources）", _o.ClientDir);
                _o.SpawnMarkerPrefix = EditorGUILayout.TextField("出生点标记前缀", _o.SpawnMarkerPrefix);
                _o.MarkerRootName = EditorGUILayout.TextField("标记点根对象名（空=不导出）", _o.MarkerRootName);
                EditorGUILayout.LabelField("", "该根对象下每个子物体的「对象名 = 标记名、世界坐标 = 点位」写进 .bytes（客户端 Game.Map.GetPoints）",
                    EditorStyles.miniLabel);
                EditorGUILayout.LabelField("文件名", _o.FileName);

                EditorGUILayout.Space();

                // ---------------- 动作 ----------------
                string bad = _o.Validate();
                if (bad != null)
                {
                    EditorGUILayout.HelpBox("参数非法：" + bad, MessageType.Error);
                }

                using (new EditorGUI.DisabledScope(bad != null))
                {
                    if (GUILayout.Button("烘焙并导出", GUILayout.Height(30)))
                    {
                        Run();
                    }
                }

                if (GUILayout.Button("重置为默认参数"))
                {
                    _o = new MapBakeOptions();
                    _result = "已重置";
                    _ok = true;
                }

                EditorGUILayout.Space();

                if (!string.IsNullOrEmpty(_result))
                {
                    EditorGUILayout.LabelField("⑤ 结果", EditorStyles.boldLabel);
                    EditorGUILayout.HelpBox(_result, _ok ? MessageType.Info : MessageType.Error);
                    if (GUILayout.Button("复制结果到剪贴板"))
                    {
                        EditorGUIUtility.systemCopyBuffer = _result;
                    }
                }
            }
            finally
            {
                EditorGUILayout.EndScrollView();
            }
        }

        /// <summary>场景选择：文本框（相对路径）+ 资产槽（拖 .unity 进来），两个方向都同步。</summary>
        private void DrawSceneField()
        {
            EditorGUILayout.BeginHorizontal();
            _o.ScenePath = EditorGUILayout.TextField("场景路径", _o.ScenePath);

            var current = AssetDatabase.LoadAssetAtPath<SceneAsset>(_o.ScenePath);
            var picked = (SceneAsset)EditorGUILayout.ObjectField(current, typeof(SceneAsset), false, GUILayout.Width(160));
            if (picked != null && picked != current)
            {
                string p = AssetDatabase.GetAssetPath(picked);
                if (!string.IsNullOrEmpty(p)) _o.ScenePath = p;
            }
            EditorGUILayout.EndHorizontal();
        }

        private void Run()
        {
            var o = _o.Clone();

            // ★ 走**帧驱动**导出：逐格烘焙被切成每帧一块 + 可取消进度条。
            //   旧实现同步跑 Export：大图（MaxDimension 32768）下点一次就把编辑器冻住数分钟、无任何反馈，
            //   连"还在跑"都看不出来。这里立刻返回，结果由回调写回面板。
            _ok = true;
            _result = "烘焙中…（进度见进度条，可点取消）";
            Repaint();

            try
            {
                MapBaker.ExportInteractive(o, (ok, summary) =>
                {
                    _ok = ok;
                    _result = summary;
                    if (ok) _o.Save(); // 成功的配置留下来给命令行复用
                    Repaint();
                });
            }
            catch (Exception e)
            {
                // 非预期分支：面板必须把异常摆到脸上，而不是只丢 console（否则用户以为点过了）。
                _ok = false;
                _result = "烘焙异常：" + e.Message;
                Debug.LogException(e);
                Repaint();
            }
        }
    }
}
