using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CloverEngine.Editor
{
    /// <summary>
    /// 地图烘焙器：把 Unity 关卡烘焙成**服务端与客户端都能读的** CloverMap 二进制数据。
    ///
    /// # 为什么导出这件事必须在 Unity 里做（而不是"一个独立 exe 工具"）
    ///
    /// 场景的真身在 <c>.unity</c> / Prefab / Terrain / MeshCollider 里，**只有 Unity 进程能正确解析**
    /// （地形高度、Mesh 碰撞、Prefab 变体、静态合批……）。脱离 Unity 写资产解析器 = 重造 Unity，
    /// 而且永远追不上它的版本。所以形态是 **C# Editor 工具**：既有可视化面板（<c>Clover/地图烘焙</c>），
    /// 也有静态方法入口可被 <c>-executeMethod</c> 调用 —— 在 CI 里它就是一个命令行工具，同一段代码。
    ///
    /// 产物是**同源两份**（服务端与客户端各读一份，读的是同一份字节）：
    /// <code>
    /// Assets/MapData/&lt;名字&gt;.bytes            服务端：碰撞 / 寻路 / 出生点
    /// Assets/Resources/MapData/&lt;名字&gt;.bytes  客户端：本地碰撞查询
    /// </code>
    /// 客户端必须读同一份：本地与服务端跑不同规则 ⇒ 本地能穿墙 / 出图 ⇒ 服务端拒绝 ⇒ 橡皮带。
    ///
    /// # 用法
    ///
    /// <code>
    /// 面板：Clover/地图烘焙/打开烘焙窗口          ← 改参数（换地图只需在这里改）
    /// 菜单：Clover/地图烘焙/导出当前场景（用已保存参数）
    /// CLI ：unity run &lt;client 工程&gt; -- -executeMethod CloverEngine.Editor.MapBaker.Export
    /// </code>
    /// 参数存在 EditorPrefs 里，所以命令行复用面板上配好的那一份，不必再写一遍。
    /// </summary>
    public static class MapBaker
    {
        private const string Tag = "[MapBake]";

        // ---------------------------------------------------------------- 入口

        /// <summary>导出当前配置的场景（菜单入口）。</summary>
        [MenuItem("Clover/地图烘焙/导出当前场景（用已保存参数）", false, 100)]
        public static void ExportFromMenu()
        {
            var o = MapBakeOptions.Load();
            if (!o.IsConfigured)
            {
                Debug.LogError($"{Tag} 还没配置过烘焙参数：请先打开 Clover/地图烘焙/打开烘焙窗口 配置一次" +
                               $"（参数会保存下来，命令行也复用这一份）");
                return;
            }

            // 菜单是**编辑器交互**上下文：走帧驱动导出 —— 逐格烘焙期间编辑器不冻住、进度条可取消。
            ExportInteractive(o, (ok, summary) => Debug.Log($"{Tag} 菜单导出{(ok ? "成功" : "失败")}：{summary}"));
        }

        /// <summary>打开烘焙面板。</summary>
        [MenuItem("Clover/地图烘焙/打开烘焙窗口", false, 101)]
        public static void OpenWindow() => MapBakeWindow.ShowWindow();

        /// <summary>
        /// 命令行入口（<c>-executeMethod CloverEngine.Editor.MapBaker.Export</c>）。
        /// 用面板里保存过的参数；失败时以非零退出码结束，让 CI 能看见。
        /// </summary>
        public static void Export()
        {
            var o = MapBakeOptions.Load();
            if (!o.IsConfigured)
            {
                Debug.LogError($"{Tag} 命令行导出失败：没有已保存的烘焙参数（先在面板 Clover/地图烘焙 里配置一次）");
                EditorApplication.Exit(1);
                return;
            }
            if (!Export(o, out string summary))
            {
                Debug.LogError($"{Tag} 命令行导出失败：{summary}");
                EditorApplication.Exit(1);
                return;
            }
            Debug.Log($"{Tag} 命令行导出成功：{summary}");
        }

        // ---------------------------------------------------------------- 导出

        /// <summary>
        /// 按给定参数导出，并回传一行摘要。
        /// <b>同步跑完</b>（CLI / 拿不到帧循环的调用方走这里）；编辑器交互上下文请用
        /// <see cref="ExportInteractive"/>（帧驱动 + 可取消，避免冻住编辑器）。
        /// </summary>
        public static bool Export(MapBakeOptions o, out string summary)
        {
            if (!TryPrepare(o, out var input, out summary))
                return false;

            var job = new WalkableBakeJob(input.Obstacles, o);
            if (!RunBakeSynchronously(job, out summary))
                return false;

            return TryFinish(o, input, job.Cells, job.BlockedCount, out summary);
        }

        /// <summary>
        /// 交互式（编辑器帧驱动）导出：**面板与菜单走这条**。
        /// 逐格烘焙被切成「每帧一块」并显示**可取消**的进度条 —— 这是
        /// 「MaxDimension 放到 32768 时点一次烘焙把编辑器冻住数分钟、且毫无反馈」的修复点。
        /// 完成回调 (成功, 摘要)；取消回调 (false, "已取消…")。
        /// </summary>
        public static void ExportInteractive(MapBakeOptions o, Action<bool, string> onDone)
        {
            if (!TryPrepare(o, out var input, out var summary))
            {
                onDone?.Invoke(false, summary);
                return;
            }

            var job = new WalkableBakeJob(input.Obstacles, o);
            // 每帧预算：单行成本 ≈ MapWidth × 障碍数，按 ~20 万次相交运算/帧切块
            //（小图一帧跑完不白等；大图也不会让单帧卡到丢输入）。
            var perRowCost = Math.Max(1L, (long)o.MapWidth * Math.Max(1, input.Obstacles.Count));
            var rowsPerFrame = Math.Max(1, (int)Math.Min(int.MaxValue, 200_000L / perRowCost));

            void Pump()
            {
                job.Step(rowsPerFrame);

                if (ShowCancelableProgress("逐格判定可行走性", job))
                {
                    // 取消：**不写出任何文件**（不留半份产物），并把进度条收掉
                    EditorApplication.update -= Pump;
                    EditorUtility.ClearProgressBar();
                    summary = "已取消烘焙：未写出任何文件";
                    Debug.LogWarning($"{Tag} {summary}");
                    onDone?.Invoke(false, summary);
                    return;
                }

                if (!job.Done) return;

                EditorApplication.update -= Pump;
                EditorUtility.ClearProgressBar();
                onDone?.Invoke(TryFinish(o, input, job.Cells, job.BlockedCount, out var s), s);
            }

            EditorApplication.update += Pump;
            Pump(); // 先推进一块，避免"点了没反应"的错觉
        }

        /// <summary>
        /// 一次烘焙的**输入集合**：收集阶段的产物（障碍 / 出生点 / 标记点）与诊断计数。
        /// 打包成一个对象是因为"收集 → 编码"两段要共用的东西变多了（层过滤计数、标记点），
        /// 继续用 out 参数会把两个入口（同步 / 帧驱动）的签名撑爆，也容易漏传一处。
        /// </summary>
        internal sealed class BakeInput
        {
            /// <summary>参与阻挡烘焙的障碍物世界 AABB（已过「地面阈值 + 层过滤」两道筛）。</summary>
            public List<Bounds> Obstacles = new List<Bounds>();

            /// <summary>出生点（服务端会做净空校验，见 <see cref="BuildSpawns"/>）。</summary>
            public Vector3[] Spawns = Array.Empty<Vector3>();

            /// <summary>命名标记点（<see cref="MapBakeOptions.MarkerRootName"/> 为空时为空数组 ⇒ 不写该段）。</summary>
            public CloverMapMarker[] Markers = Array.Empty<CloverMapMarker>();

            /// <summary>场景里参与统计的碰撞体总数（含被排除的）。</summary>
            public int Seen;

            /// <summary>按「地面阈值」排除的碰撞体数（沿用旧口径）。</summary>
            public int SkippedAsGround;

            /// <summary>层过滤排除的碰撞体数（按 Unity Layer 排除）。</summary>
            public int FilteredByLayer;

            /// <summary>层过滤排除的碰撞体数（按高度带排除）。</summary>
            public int FilteredByHeight;

            /// <summary>
            /// 被层过滤排除的 Unity Layer（升序，去重）—— 这就是日志里要报的「被过滤掉的层数」，
            /// ⛔ 不许静默：过滤掉了哪几层必须能从日志上看出来。
            /// </summary>
            public readonly SortedSet<int> FilteredLayers = new SortedSet<int>();

            /// <summary>参与烘焙的碰撞体所在 Unity Layer（升序，去重）。</summary>
            public readonly SortedSet<int> KeptLayers = new SortedSet<int>();

            /// <summary>层过滤排除总数。</summary>
            public int FilteredTotal => FilteredByLayer + FilteredByHeight;

            /// <summary>层过滤的一行人类可读摘要（开关关着时为空串：没有任何过滤发生）。</summary>
            public string FilterSummary(MapBakeOptions o)
            {
                if (!o.LayerFilterEnabled) return string.Empty;
                string layers = FilteredLayers.Count == 0
                    ? "无"
                    : string.Join(",", FilteredLayers);
                string upper = o.LayerMaxY > o.LayerMinY ? o.LayerMaxY.ToString("F1") : "+∞";
                return $"层过滤(Layer[{o.LayerMin},{o.LayerMax}] Y[{o.LayerMinY:F1},{upper}]) " +
                       $"排除={FilteredTotal}(按层={FilteredByLayer}/按高度={FilteredByHeight}) " +
                       $"被排除的层={FilteredLayers.Count}个[{layers}] 保留的层={KeptLayers.Count}个";
            }
        }

        /// <summary>
        /// 校验参数 → 显式打开目标场景 → 收集障碍物 / 出生点 / 标记点。失败时 <paramref name="summary"/> 为原因。
        /// </summary>
        private static bool TryPrepare(MapBakeOptions o, out BakeInput input, out string summary)
        {
            input = null;
            summary = null;

            string bad = o.Validate();
            if (bad != null)
            {
                summary = "参数非法：" + bad;
                Debug.LogError($"{Tag} {summary}");
                return false;
            }

            // ★ 必须按路径**显式打开**目标场景再导出。
            // 踩过的坑：批处理里刚保存完场景可能触发域重载，此时 active scene 会变成一个空的无标题场景,
            // 结果导出「0 障碍物 + 全可走」的空地图（日志里能自证：根对象=0）。
            if (!File.Exists(o.ScenePath))
            {
                summary = $"场景不存在: {o.ScenePath}";
                Debug.LogError($"{Tag} {summary}");
                return false;
            }

            // OpenSceneMode.Single 会静默关闭当前打开的场景：未保存改动会丢，先留痕再开。
            WarnIfUnsavedScenes(o.ScenePath);
            var scene = EditorSceneManager.OpenScene(o.ScenePath, OpenSceneMode.Single);

            input = new BakeInput();
            CollectObstacles(scene, o, input);
            int rootCount = scene.GetRootGameObjects().Length;
            Debug.Log($"{Tag} 烘焙场景 name={scene.name} path={scene.path} 根对象={rootCount} " +
                      $"碰撞体总数={input.Seen} 按地面排除={input.SkippedAsGround} " +
                      $"层过滤排除={input.FilteredTotal}（按层={input.FilteredByLayer}/按高度={input.FilteredByHeight}）" +
                      $" 计为障碍={input.Obstacles.Count}");
            // ★ 层过滤必须**有声音**：被排除了多少、排除了哪几层，一行写清（默认关闭时这一行是"层过滤未启用"）。
            if (o.LayerFilterEnabled)
            {
                Debug.Log($"{Tag} {input.FilterSummary(o)}｜被排除的层={DescribeLayers(input.FilteredLayers)}");
            }
            else
            {
                Debug.Log($"{Tag} 层过滤未启用（LayerFilterEnabled=false）= 现状行为：全部非地面碰撞体参与烘焙");
            }
            if (input.Obstacles.Count == 0)
            {
                // 非预期分支：没有障碍物说明场景不对（或全被当成地面 / 层过滤排除了），导出会是"全可走空地图"。
                Debug.LogWarning($"{Tag} 未收集到任何障碍物碰撞体：导出的地图将是一张空的可走平面，" +
                                 "请确认场景内容，以及「地面阈值 / 层过滤」是否把该参与的几何全筛掉了");
            }

            input.Spawns = BuildSpawns(scene, o);
            input.Markers = BuildMarkers(scene, o);
            return true;
        }

        /// <summary>把一组 Unity Layer 序号写成 <c>层号(碰撞体数)</c> 列表（诊断用；不排序外部输入由调用方保证）。</summary>
        private static string DescribeLayers(IEnumerable<int> layers)
        {
            var parts = new List<string>();
            foreach (var l in layers) parts.Add(l.ToString());
            return parts.Count == 0 ? "无" : string.Join(",", parts);
        }

        /// <summary>
        /// 导出收尾：编码 → 回读自检 → 原子写盘（服务端 + 客户端各一份）。
        /// 同步与帧驱动两条路径共用。
        /// </summary>
        private static bool TryFinish(MapBakeOptions o, BakeInput input,
            bool[] cells, int blockedCount, out string summary)
        {
            summary = null;

            byte[] bytes;
            try
            {
                bytes = CloverMapWriter.Encode(new CloverMapWriter.MapData
                {
                    SceneId = o.SceneId,
                    Name = o.MapName,
                    CellSize = o.CellSize,
                    Origin = o.Origin,
                    Width = o.MapWidth,
                    Depth = o.MapDepth,
                    Cells = cells,
                    Colliders = input.Obstacles.ToArray(),
                    Spawns = input.Spawns,
                    // 标记点为**空**时编码器不写该段、也不置 FlagMarkers 位 ⇒ 产物与旧版逐字节一致。
                    Markers = input.Markers,
                });
            }
            catch (Exception e)
            {
                // 非预期分支：编码失败（参数非法 / 布局算错）必须显式报错，不能留下半份文件。
                summary = "编码失败: " + e.Message;
                Debug.LogError($"{Tag} {summary}");
                return false;
            }

            // 写完立刻回读自检：失败是"静默"的（文件在、能加载、只是地图不对），必须当场抓住。
            string verify;
            try
            {
                verify = CloverMapWriter.Verify(bytes);
            }
            catch (Exception e)
            {
                summary = "自检失败: " + e.Message;
                Debug.LogError($"{Tag} {summary}");
                return false;
            }

            // 写盘包 try/catch 且逐文件**原子替换**：磁盘满 / 路径非法时不能把异常直接抛出去
            //（否则 CLI 不会走到 Exit(1)，用户只看到半份产物且已退出成功）。
            string outPath;
            try
            {
                Directory.CreateDirectory(o.ServerDir);
                outPath = $"{o.ServerDir}/{o.FileName}";
                WriteFileAtomic(outPath, bytes);

                // ★ 同一份数据再写一份到 Resources：客户端要**按同一套规则**做本地碰撞，
                //   否则客户端能穿墙 / 出图 → 服务端拒绝 → 表现成"被拽回来"（橡皮带）。
                Directory.CreateDirectory(o.ClientDir);
                WriteFileAtomic($"{o.ClientDir}/{o.FileName}", bytes);
            }
            catch (Exception e)
            {
                // 非预期分支：写盘失败显式报错并走失败路径（面板红框 / CLI 非零退出），
                // 不留下"服务端新、客户端旧（或缺失）"却没人知道的状态。
                summary = "写盘失败: " + e.Message;
                Debug.LogError($"{Tag} {summary}");
                return false;
            }
            AssetDatabase.Refresh();

            o.Save(); // 成功的这份配置留下来，命令行与下次打开直接复用

            // 摘要里必须带上「层过滤排除了多少（含被排除的层数）」与「标记点多少」：
            // 这两样一旦静默，"烘出来的地图不对 / 按名取点全落空"就只能靠联调现场去猜。
            string layerPart = input.FilterSummary(o);
            summary = $"{o.MapName} {o.MapWidth}x{o.MapDepth} cell={o.CellSize:F2} " +
                      $"可走={o.MapWidth * o.MapDepth - blockedCount} 阻挡={blockedCount} " +
                      $"碰撞体={input.Obstacles.Count} 出生点={input.Spawns.Length} 标记点={input.Markers.Length} " +
                      (layerPart.Length > 0 ? layerPart + " " : string.Empty) +
                      $"字节={bytes.Length} → {outPath}";

            Debug.Log($"{Tag} 地图已导出: {outPath}\n{Tag} 自检: {verify}\n{Tag} {summary}");
            return true;
        }

        // ---------------------------------------------------------------- 烘焙

        /// <summary>
        /// 收集「高于地面阈值 + 通过层过滤」的碰撞体（排除地面本身），bounds 为世界 AABB。
        /// 各类排除计数写进 <paramref name="input"/>（用于日志与摘要，⛔ 不许静默）。
        /// <para>
        /// ⚠️ <b>历史的绕法，以及为什么不该把它搬进引擎</b>：多层地图项目（cs16 的 de_dust2）为了绕开
        /// "位图是单层 2D"这个限制，做法是「把真实几何（Visual MeshCollider）在烘焙前**临时整体关掉**
        /// 并存盘，只留一层每格一个 BoxCollider 的"烘焙代理"，烘完再把碰撞体恢复」——
        /// 这条链路要改场景、要存盘、中断就会在磁盘上留下"碰撞体全关"的场景，是**项目侧**的绕法。
        /// 引擎该给的替代品是这里的**层过滤**：用不变量（Unity Layer / 高度带）描述"谁参与烘焙"，
        /// 不改场景、可复现、可审计。真·逐格高度场（一份数据带多层）列 V2
        /// （见 <see cref="CloverMapFormat.FlagHeightField"/>），V1 解码器见到该段仍**明确拒绝**。
        /// </para>
        /// </summary>
        private static void CollectObstacles(Scene scene, MapBakeOptions o, BakeInput input)
        {
            foreach (var root in scene.GetRootGameObjects())
            {
                foreach (var col in root.GetComponentsInChildren<Collider>(true))
                {
                    if (col == null || !col.enabled) continue;
                    input.Seen++;

                    // 层过滤在**收集阶段**完成：被排除的几何根本不进障碍表，
                    // 逐格判定的成本也跟着降（大图上这是"能不能烘得动"的差别）。
                    int layer = col.gameObject.layer;
                    if (!o.ShouldBakeCollider(layer, col.bounds.min.y, col.bounds.max.y))
                    {
                        // 分开计「层排除 / 高度排除」，是为了配错时能一眼看出是哪一半筛掉了几何。
                        if (layer < o.LayerMin || layer > o.LayerMax) input.FilteredByLayer++;
                        else input.FilteredByHeight++;
                        input.FilteredLayers.Add(layer);
                        continue;
                    }
                    input.KeptLayers.Add(layer);

                    var b = col.bounds;
                    if (b.max.y - o.GroundTopY <= o.ObstacleMinHeight)
                    {
                        input.SkippedAsGround++;   // 地面 / 贴地薄板
                        continue;
                    }
                    input.Obstacles.Add(b);
                }
            }
        }

        /// <summary>
        /// 命名标记点：<see cref="MapBakeOptions.MarkerRootName"/> 指定的**根对象**下，
        /// 每个子物体 → 一个标记点（对象名 = 标记名，世界坐标 = 点位，Y 取**真实高度**）。
        /// <para>
        /// 与出生点（<see cref="BuildSpawns"/> 按前缀扫全场景）的分工不同：标记点按名字取用，
        /// 同一个名字常有多点（一组出生点、一条路线的路点），所以用"根对象下逐个子物体"这种
        /// 最直白的映射 —— **对象名就是契约**，名字写错在项目侧的契约校验里就能发现。
        /// </para>
        /// <para>
        /// 引擎**不做**业务吸附（"落阻挡格就挪到最近可走格心"是项目规则，见 cs16 的 SnapMarkerToWalkable）；
        /// 也不做名字白名单（引擎不知道哪些名字有意义）。留空根对象名 ⇒ 返回空数组 ⇒ 不写标记点段。
        /// </para>
        /// </summary>
        private static CloverMapMarker[] BuildMarkers(Scene scene, MapBakeOptions o)
        {
            if (string.IsNullOrEmpty(o.MarkerRootName))
                return Array.Empty<CloverMapMarker>();

            Transform root = null;
            foreach (var go in scene.GetRootGameObjects())
            {
                if (go.name == o.MarkerRootName) { root = go.transform; break; }
            }
            if (root == null)
            {
                // 非预期分支：配了根对象名却找不到 ⇒ 导出的文件**没有**标记点段，客户端按名取点全落空。
                Debug.LogWarning($"{Tag} 配置了 MarkerRootName=\"{o.MarkerRootName}\" 但场景根对象里没有它 ⇒ " +
                                 "本次导出**不含**标记点段（客户端按名取点会全部落空）");
                return Array.Empty<CloverMapMarker>();
            }

            var list = new List<CloverMapMarker>();
            int blankNames = 0;
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t == root) continue;
                if (string.IsNullOrWhiteSpace(t.name)) { blankNames++; continue; }
                // 位置取世界坐标（不是 localPosition）：标记点写在根下只是**组织方式**，
                // 点位必须与运行时/服务端同一坐标系。
                list.Add(new CloverMapMarker(t.name, t.position));
            }

            if (blankNames > 0)
                Debug.LogWarning($"{Tag} 标记点根 \"{o.MarkerRootName}\" 下有 {blankNames} 个空名字对象，" +
                                 "已跳过（空名字无法按名取点，且编码器会直接报错）");

            var names = new HashSet<string>();
            int dup = 0;
            foreach (var m in list) if (!names.Add(m.Name)) dup++;
            Debug.Log($"{Tag} 从 \"{o.MarkerRootName}\" 收集到标记点 {list.Count} 点 / {names.Count} 个名字" +
                      (dup > 0 ? $"（其中同名多点 {dup} 个：同名代表一组，按名取点会按文件顺序全部返回）" : string.Empty));
            return list.ToArray();
        }

        /// <summary>
        /// 逐格烘焙任务（**可分帧执行**）：格的柱体范围 [ProbeBottomY, ProbeTopY] 内与任一障碍物相交 → 阻挡。
        /// 与解码端的位图约定一致：行主序（行 = z），字节内 LSB 优先。
        ///
        /// <para>
        /// 为什么要做成"可分步的任务"而不是一个函数：逐格 × 全障碍是 O(W×D×N)，大图（MaxDimension=32768）
        /// 下是数分钟的**纯主线程**运算，一次性跑完就是把编辑器冻住数分钟且毫无反馈。
        /// 拆成 <see cref="Step"/> 之后，编辑器交互路径可以每帧推进一块并显示**可取消**的进度条。
        /// </para>
        /// </summary>
        internal sealed class WalkableBakeJob
        {
            private readonly List<Bounds> _obstacles;
            private readonly MapBakeOptions _o;
            private readonly Vector3 _half;
            private readonly float _probeCenterY;
            private int _z;

            /// <summary>可行走位图（行主序，行 = z）。任务完成后即为最终结果。</summary>
            public readonly bool[] Cells;

            /// <summary>已判定的阻挡格数。</summary>
            public int BlockedCount;

            public WalkableBakeJob(List<Bounds> obstacles, MapBakeOptions o)
            {
                _obstacles = obstacles;
                _o = o;
                Cells = new bool[o.MapWidth * o.MapDepth];

                // ★ 取样柱以地面顶面（GroundTopY）为基准：ProbeBottomY / ProbeTopY 是**相对地面**的高度。
                // 旧实现按绝对 Y 摆放（默认 0.2~2.2）：地面 Y≠0 的关卡（如地面在 10m）取样柱整根埋在
                // 地面之下，逐格都不与障碍相交 → 烘出"全可走"空地图且无告警。
                float probeBottomY = o.GroundTopY + o.ProbeBottomY;
                float probeTopY = o.GroundTopY + o.ProbeTopY;
                _half = new Vector3(o.CellSize * 0.5f, (probeTopY - probeBottomY) * 0.5f, o.CellSize * 0.5f);
                _probeCenterY = (probeTopY + probeBottomY) * 0.5f;
            }

            /// <summary>总行数（= MapDepth）。</summary>
            public int TotalRows => _o.MapDepth;

            /// <summary>已处理行数。</summary>
            public int ProcessedRows => _z;

            /// <summary>进度 0~1。</summary>
            public float Progress => TotalRows <= 0 ? 1f : Math.Min(1f, (float)_z / TotalRows);

            /// <summary>是否已全部处理完。</summary>
            public bool Done => _z >= TotalRows;

            /// <summary>推进至多 <paramref name="maxRows"/> 行；返回是否已完成。</summary>
            public bool Step(int maxRows)
            {
                if (maxRows < 1) maxRows = 1;
                var end = Math.Min(TotalRows, _z + maxRows);
                for (; _z < end; _z++)
                {
                    var z = _z;
                    for (int x = 0; x < _o.MapWidth; x++)
                    {
                        var center = new Vector3(
                            _o.Origin.x + (x + 0.5f) * _o.CellSize,
                            _probeCenterY,
                            _o.Origin.z + (z + 0.5f) * _o.CellSize);
                        bool blocked = false;
                        for (int i = 0; i < _obstacles.Count; i++)
                        {
                            if (Intersects(_obstacles[i], center, _half)) { blocked = true; break; }
                        }
                        Cells[z * _o.MapWidth + x] = !blocked;
                        if (blocked) BlockedCount++;
                    }
                }
                return Done;
            }
        }

        /// <summary>
        /// 同步把烘焙任务跑完（CLI / 无帧循环的调用方）。显示可取消进度条；被取消返回 false 且**不写文件**。
        /// 批处理模式下不弹进度条（无人看，且部分平台会阻塞主线程）。
        /// </summary>
        private static bool RunBakeSynchronously(WalkableBakeJob job, out string summary)
        {
            summary = null;
            const int rowsPerChunk = 256;
            try
            {
                while (!job.Done)
                {
                    job.Step(rowsPerChunk);
                    if (ShowCancelableProgress("逐格判定可行走性", job))
                    {
                        summary = "已取消烘焙：未写出任何文件";
                        Debug.LogWarning($"{Tag} {summary}");
                        return false;
                    }
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
            return true;
        }

        /// <summary>
        /// 显示可取消进度条；返回 true 表示用户点了取消。批处理模式下恒返回 false（不显示、不可取消）。
        /// </summary>
        private static bool ShowCancelableProgress(string what, WalkableBakeJob job)
        {
            if (Application.isBatchMode)
                return false;
            return EditorUtility.DisplayCancelableProgressBar(
                "Clover 地图烘焙",
                $"{what}… {job.Progress:P0}（{job.ProcessedRows}/{job.TotalRows} 行）",
                job.Progress);
        }

        /// <summary>
        /// AABB 相交判定。★ 必须用**严格小于**：用 &lt;= 会把「仅与墙面相切」的邻格也算成阻挡，
        /// 结果整张地图的可行走区被墙「外扩一格」，玩家隔着 1 米就撞空气墙
        /// （曾观察到 64x64 地图阻挡数 784，实际应为约 388）。
        /// </summary>
        private static bool Intersects(Bounds b, Vector3 center, Vector3 half)
        {
            return Mathf.Abs(center.x - b.center.x) < half.x + b.extents.x &&
                   Mathf.Abs(center.y - b.center.y) < half.y + b.extents.y &&
                   Mathf.Abs(center.z - b.center.z) < half.z + b.extents.z;
        }

        /// <summary>
        /// 出生点：优先取场景里名字以 <see cref="MapBakeOptions.SpawnMarkerPrefix"/> 开头的对象
        /// （业务在关卡里摆个空物体即可精确控制出生位置）；一个都没有时回退到「地图两角内缩 4m」。
        ///
        /// 回退值只是"能跑"：它可能正好落在建筑外墙的夹角里，那种位置会让玩家一出生就被本地碰撞锁死。
        /// 服务端加载时还会做净空校验兜底（见服务端 <c>mapdata/spawn.go</c>），但**根治**是业务把标记摆对。
        /// </summary>
        private static Vector3[] BuildSpawns(Scene scene, MapBakeOptions o)
        {
            var found = new List<Vector3>();
            if (!string.IsNullOrEmpty(o.SpawnMarkerPrefix))
            {
                foreach (var root in scene.GetRootGameObjects())
                {
                    foreach (var t in root.GetComponentsInChildren<Transform>(true))
                    {
                        if (t.name.StartsWith(o.SpawnMarkerPrefix, StringComparison.Ordinal))
                        {
                            found.Add(new Vector3(t.position.x, o.GroundTopY, t.position.z));
                        }
                    }
                }
            }
            if (found.Count > 0)
            {
                Debug.Log($"{Tag} 从场景标记收集到 {found.Count} 个出生点（前缀 \"{o.SpawnMarkerPrefix}\"）");
                return found.ToArray();
            }

            Debug.LogWarning($"{Tag} 场景里没有以 \"{o.SpawnMarkerPrefix}\" 开头的出生点标记，" +
                             "回退到「地图两角内缩 4m」—— 建议在关卡里摆标记以精确控制出生位置");
            // ★ 地图尺寸的单位是**格**：换算成米必须乘 CellSize —— 旧实现漏乘，
            //   CellSize≠1 时第二个角落到地图中间或图外，玩家一出生就被本地碰撞锁死。
            return new[]
            {
                new Vector3(o.Origin.x + 4f, o.GroundTopY, o.Origin.z + 4f),
                new Vector3(o.Origin.x + o.MapWidth * o.CellSize - 4f, o.GroundTopY,
                            o.Origin.z + o.MapDepth * o.CellSize - 4f),
            };
        }

        // ---------------------------------------------------------------- 场景包围盒 → 烘焙参数

        /// <summary>
        /// 按场景里所有 Renderer / Collider 的包围盒推算 Origin / MapWidth / MapDepth / GroundTopY。
        ///
        /// 用途：换一张美术地图时不必手算尺寸与原点。**它会改变导出结果**（尺寸 / 原点都变），
        /// 所以只在面板上由人显式触发 —— 不自动跑，免得把一张既有地图已调好的数值悄悄改掉。
        /// 返回一行人类可读的推算结果（失败原因也是字符串；要区分成败请调 <see cref="TryFitBoundsToScene"/>）。
        /// </summary>
        public static string FitBoundsToScene(MapBakeOptions o)
        {
            TryFitBoundsToScene(o, out var message);
            return message;
        }

        /// <summary>
        /// <see cref="FitBoundsToScene"/> 的带结果版本：成功返回 true（<paramref name="message"/> 为一行推算结果），
        /// 失败返回 false（<paramref name="message"/> 为人类可读的原因）。
        /// 面板据此把失败摆成错误态 —— 旧实现拿不到成败信息，推算失败也按成功样式显示。
        /// </summary>
        public static bool TryFitBoundsToScene(MapBakeOptions o, out string message)
        {
            if (o == null) { message = "烘焙参数为 null"; return false; }
            if (string.IsNullOrEmpty(o.ScenePath)) { message = "场景路径为空"; return false; }
            if (!File.Exists(o.ScenePath)) { message = $"场景不存在: {o.ScenePath}"; return false; }

            // OpenSceneMode.Single 会静默关闭当前打开的场景：未保存改动会丢，先留痕再开。
            WarnIfUnsavedScenes(o.ScenePath);
            var scene = EditorSceneManager.OpenScene(o.ScenePath, OpenSceneMode.Single);

            bool has = false;
            Bounds acc = default;
            foreach (var root in scene.GetRootGameObjects())
            {
                foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                {
                    if (!has) { acc = r.bounds; has = true; } else { acc.Encapsulate(r.bounds); }
                }
                foreach (var c in root.GetComponentsInChildren<Collider>(true))
                {
                    if (!has) { acc = c.bounds; has = true; } else { acc.Encapsulate(c.bounds); }
                }
            }
            if (!has) { message = "场景里没有任何 Renderer / Collider，无法推算包围盒"; return false; }

            float cell = o.CellSize > 0 ? o.CellSize : 1f;
            // X/Z 取整到格并**向外**扩一格，保证边界上的几何也算进位图内
            o.Origin = new Vector3(
                Mathf.Floor(acc.min.x / cell) * cell,
                acc.min.y,
                Mathf.Floor(acc.min.z / cell) * cell);
            o.MapWidth = Mathf.Max(1, Mathf.CeilToInt((acc.max.x - o.Origin.x) / cell) + 1);
            o.MapDepth = Mathf.Max(1, Mathf.CeilToInt((acc.max.z - o.Origin.z) / cell) + 1);
            o.GroundTopY = Mathf.Floor(acc.min.y); // 地面顶面 ≈ 最低几何的底面

            message = $"包围盒 min=({acc.min.x:F2},{acc.min.y:F2},{acc.min.z:F2}) " +
                      $"max=({acc.max.x:F2},{acc.max.y:F2},{acc.max.z:F2}) " +
                      $"→ origin=({o.Origin.x:F1},{o.Origin.z:F1}) 尺寸={o.MapWidth}x{o.MapDepth} 地面Y={o.GroundTopY:F2}";
            return true;
        }

        // ---------------------------------------------------------------- 辅助

        /// <summary>
        /// 打开目标场景前检查已打开场景是否有未保存改动：<c>OpenSceneMode.Single</c> 会**静默关闭
        /// 当前打开的场景**，未保存的改动会丢。这里**不弹保存对话框**（无人值守 / CI 下模态框会
        /// 卡死编辑器主线程），只把风险写进日志 —— 是"可见"而不是静默丢弃。
        /// </summary>
        private static void WarnIfUnsavedScenes(string target)
        {
            var dirty = 0;
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                if (SceneManager.GetSceneAt(i).isDirty) dirty++;
            }
            if (dirty > 0)
            {
                Debug.LogWarning($"{Tag} 打开 {target} 前检测到 {dirty} 个已打开场景有未保存改动：" +
                                 "OpenSceneMode.Single 会丢弃它们。本工具不弹保存对话框（无人值守时会卡死编辑器），" +
                                 "请先手动保存（Ctrl+S）再烘焙 / 推算");
            }
        }

        /// <summary>
        /// 原子写盘：先写同目录下的临时文件，再替换目标 —— 写入过程中失败（磁盘满 / 权限 /
        /// 进程被杀）时目标文件保持原样，不会留下"写了一半"的字节（半份 Map 数据会被
        /// 服务端 / 客户端按错位内容读，属静默故障）。
        /// </summary>
        private static void WriteFileAtomic(string path, byte[] bytes)
        {
            var tmpPath = path + ".tmp";
            File.WriteAllBytes(tmpPath, bytes);
            try
            {
                if (File.Exists(path))
                {
                    // Windows ReplaceFile / Unix rename：原子替换（目标必须已存在）。
                    File.Replace(tmpPath, path, null);
                }
                else
                {
                    File.Move(tmpPath, path);
                }
            }
            catch (PlatformNotSupportedException)
            {
                // 极少数平台不支持 File.Replace：退回"先删后移"（仍有极短窗口，但优于直接覆写半份）。
                Debug.LogWarning($"{Tag} 本平台不支持 File.Replace，退回先删后移写盘: {path}");
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmpPath, path);
            }
            catch
            {
                // 替换失败：清掉临时文件，让目标保持**旧内容**；异常交给调用方按失败处理并留痕。
                try { if (File.Exists(tmpPath)) File.Delete(tmpPath); }
                catch { /* 临时文件清理失败不影响主错误上报 */ }
                throw;
            }
        }
    }
}
