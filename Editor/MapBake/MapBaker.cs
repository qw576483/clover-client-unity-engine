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
            if (!TryPrepare(o, out var obstacleColliders, out var spawns, out summary))
                return false;

            var job = new WalkableBakeJob(obstacleColliders, o);
            if (!RunBakeSynchronously(job, out summary))
                return false;

            return TryFinish(o, obstacleColliders, spawns, job.Cells, job.BlockedCount, out summary);
        }

        /// <summary>
        /// 交互式（编辑器帧驱动）导出：**面板与菜单走这条**。
        /// 逐格烘焙被切成「每帧一块」并显示**可取消**的进度条 —— 这是
        /// 「MaxDimension 放到 32768 时点一次烘焙把编辑器冻住数分钟、且毫无反馈」的修复点。
        /// 完成回调 (成功, 摘要)；取消回调 (false, "已取消…")。
        /// </summary>
        public static void ExportInteractive(MapBakeOptions o, Action<bool, string> onDone)
        {
            if (!TryPrepare(o, out var obstacleColliders, out var spawns, out var summary))
            {
                onDone?.Invoke(false, summary);
                return;
            }

            var job = new WalkableBakeJob(obstacleColliders, o);
            // 每帧预算：单行成本 ≈ MapWidth × 障碍数，按 ~20 万次相交运算/帧切块
            //（小图一帧跑完不白等；大图也不会让单帧卡到丢输入）。
            var perRowCost = Math.Max(1L, (long)o.MapWidth * Math.Max(1, obstacleColliders.Count));
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
                onDone?.Invoke(TryFinish(o, obstacleColliders, spawns, job.Cells, job.BlockedCount, out var s), s);
            }

            EditorApplication.update += Pump;
            Pump(); // 先推进一块，避免"点了没反应"的错觉
        }

        /// <summary>
        /// 校验参数 → 显式打开目标场景 → 收集障碍物 → 生成出生点。失败时 <paramref name="summary"/> 为原因。
        /// </summary>
        private static bool TryPrepare(MapBakeOptions o, out List<Bounds> obstacleColliders,
            out Vector3[] spawns, out string summary)
        {
            obstacleColliders = null;
            spawns = null;
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

            obstacleColliders = new List<Bounds>();
            int seen = 0, skipped = 0;
            CollectObstacles(scene, o, obstacleColliders, ref seen, ref skipped);
            int rootCount = scene.GetRootGameObjects().Length;
            Debug.Log($"{Tag} 烘焙场景 name={scene.name} path={scene.path} 根对象={rootCount} " +
                      $"碰撞体总数={seen} 按地面排除={skipped} 计为障碍={obstacleColliders.Count}");
            if (obstacleColliders.Count == 0)
            {
                // 非预期分支：没有障碍物说明场景不对（或全被当成地面排除了），导出会是"全可走空地图"。
                Debug.LogWarning($"{Tag} 未收集到任何障碍物碰撞体：导出的地图将是一张空的可走平面，请确认场景内容");
            }

            spawns = BuildSpawns(scene, o);
            return true;
        }

        /// <summary>
        /// 导出收尾：编码 → 回读自检 → 原子写盘（服务端 + 客户端各一份）。
        /// 同步与帧驱动两条路径共用。
        /// </summary>
        private static bool TryFinish(MapBakeOptions o, List<Bounds> obstacleColliders, Vector3[] spawns,
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
                    Colliders = obstacleColliders.ToArray(),
                    Spawns = spawns,
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

            summary = $"{o.MapName} {o.MapWidth}x{o.MapDepth} cell={o.CellSize:F2} " +
                      $"可走={o.MapWidth * o.MapDepth - blockedCount} 阻挡={blockedCount} " +
                      $"碰撞体={obstacleColliders.Count} 出生点={spawns.Length} 字节={bytes.Length} → {outPath}";

            Debug.Log($"{Tag} 地图已导出: {outPath}\n{Tag} 自检: {verify}\n{Tag} {summary}");
            return true;
        }

        // ---------------------------------------------------------------- 烘焙

        /// <summary>收集「高于地面阈值」的碰撞体（排除地面本身），bounds 为世界 AABB。seen/skipped 为诊断计数。</summary>
        private static void CollectObstacles(Scene scene, MapBakeOptions o, List<Bounds> outBounds,
                                             ref int seen, ref int skipped)
        {
            foreach (var root in scene.GetRootGameObjects())
            {
                foreach (var col in root.GetComponentsInChildren<Collider>(true))
                {
                    if (col == null || !col.enabled) continue;
                    seen++;
                    var b = col.bounds;
                    if (b.max.y - o.GroundTopY <= o.ObstacleMinHeight) { skipped++; continue; } // 地面 / 贴地薄板
                    outBounds.Add(b);
                }
            }
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
