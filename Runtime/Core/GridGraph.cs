// ─────────────────────────────────────────────────────────────────────────────
// CloverEngine · Runtime/Core/GridGraph.cs
// 格子图**通用算法底座**（8 邻接 BFS 连通性 / 边界环封 / 可走格索引与 O(1) 抽样 /
// 矩形·直线·圆盘批写）—— 纯逻辑、无状态、不持有任何 Unity 对象。下沉到引擎。
//
// 出处：Diablo2 项目 `client/Assets/Scripts/Module/Map/GridMap.cs`（**逐行等价搬运**）。
//   被搬走的行（旧行号）：8 邻接表 `:28-32` · 批操作 `:332-480` · 边界环封 `SealBorderRing :386-411`
//   · 可走格缓存 + O(1) 抽样 `:521-536` · BFS 连通性 / 孤立口袋填充 / 必需可达校验 `:557-652`。
//   ⛔ **没搬**：`TileKind` / `TileKindInfo` / `AreaId` / 原版地形语义 / 地图名 / `MapLog`
//   —— 全部留在项目侧；引擎件一律用**回调**替代（`Func<Vector2Int,bool> isWalkable` +
//   `Action<int,int> write`），因此引擎不认识任何一款游戏的地形枚举。
//
// ★ 为什么用回调而不是自持一张 `bool[,]` 位图：
//   ① **与 `CloverEngine.AStar` 同一种形状** —— `AStar.Find(Func<Vector2Int,bool> walkable, …)`
//      本来就是回调式。本件的 `isWalkable` 与它是**同一个委托类型、同一套越界口径**
//      ⇒ 业务侧同一个 `Walkable` 方法组可以**同时**喂给 AStar 和本件，
//      不会出现「BFS 说通、A* 走不过去」的假通过（这正是旧 `GridMap.FloodFillFrom`
//      注释里点名的风险）。
//   ② 引擎自持位图就要自己管「谁写格、什么时候重建」，那是地图容器的职责（`ITileWorld` 那种）；
//      本件只做**算法**，格数据仍归业务。
//
// ★ 两条**契约**（调用方必须满足，否则结果无意义；引擎不替调用方兜底）：
//   ① `isWalkable` 对**图外坐标必须返回 false**（与 `AStar` 的 `walkable` 契约逐字相同）；
//   ② `width` / `height` 是**真实格数**，`visited` 必须由调用方按 `[width, height]` 分配且初值全 false。
//   违反 ① 的典型症状：BFS 从地图外绕过去（而 AStar 不会），于是自检通过、实际走不通。
//
// ★ 邻接 / 对角口径（**逐字照搬旧实现**，⛔ 不许"顺手优化"）：
//   8 邻接、前 4 直走后 4 斜走；**对角要求两侧格都可走**（否则 BFS 会贴着墙角穿过去）。
//   这条与 `AStar.Neighbors` / `AStar.Find` 的对角判定必须**保持逐行一致** ——
//   两处任何一处改口径，都会产生「可达性自检通过、寻路失败」的静默不一致。
//
// ★ 可走格索引（`WalkableIndex`）为什么值得单独成件：
//   在「大部分是墙」的洞里**均匀随机撒点**几乎全落空（旧注释原话）。缓存成列表后
//   `Pick(rng.Next(Count))` 是 **O(1)**，既快又天然不空手而归。
//   ⚠️ **遍历顺序是契约的一部分**：`Rebuild` 的填充顺序 = `x` 外层升序、`y` 内层升序
//   （与旧 `RecountIfNeeded` 逐字一致）⇒ 同一 seed 的抽样序列才可逐项复现。
//   ⛔ 换顺序不会报错，只会让「同 seed 两次生成的刷怪/掉落落点」不再一致。
//
// ★ ⛔ **本件不做**：寻路（走 `AStar`）、位移解算、任何地形枚举语义、任何日志埋点
//   （`MapLog` / 业务日志一律留在业务侧；本件只对「调用方违约」这类**编程错误**留痕，
//   用引擎 `LogThrottle`，见 `Runtime/Core/LogThrottle.cs`）。
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using UnityEngine;

namespace CloverEngine
{
    /// <summary>
    /// 格子图通用算法（纯逻辑、无状态、无 MonoBehaviour；主线程使用）。
    /// <para>
    /// 契约与口径见文件头 ★。所有方法对**图外坐标**一律按「不可走」处理，
    /// 且 <c>width</c> / <c>height</c> 必须与调用方手上的格图一致。
    /// </para>
    /// </summary>
    public static class GridGraph
    {
        private const string Tag = "GridGraph";

        /// <summary>
        /// 8 邻接（前 4 直走、后 4 斜走）。
        /// <para>
        /// ⛔ **顺序与取值与 `AStar` 内部的 `Neighbors` 逐项相同**：BFS 与 A* 的
        /// 「哪些邻格先被访问」会影响 `visited` 的填充顺序 / 孤立口袋的发现顺序，
        /// 两处顺序不一致排查起来极其隐蔽（结果"看起来都对"，只是序列不同）。
        /// </para>
        /// <para>
        /// ⚠️ **与 `AStar.Neighbors` 目前仍是两份字面量相同的表**（`Runtime/Core/AStar.cs:38-42`）——
        /// 本轮下沉按授权范围**未改既有文件**，故没有把 `AStar` 改成引用本表。
        /// 两者若要收敛成一份，需另行授权改 `AStar.cs`（已登记在片回报的「未决」里）。
        /// </para>
        /// </summary>
        public static readonly Vector2Int[] Neighbors8 =
        {
            new Vector2Int(1, 0), new Vector2Int(-1, 0), new Vector2Int(0, 1), new Vector2Int(0, -1),
            new Vector2Int(1, 1), new Vector2Int(1, -1), new Vector2Int(-1, 1), new Vector2Int(-1, -1),
        };

        /// <summary>是否在 <c>[0,width) × [0,height)</c> 内。</summary>
        public static bool InBounds(int x, int y, int width, int height)
            => x >= 0 && y >= 0 && x < width && y < height;

        /// <summary>该格是否在界内（<see cref="Vector2Int"/> 版）。</summary>
        public static bool InBounds(Vector2Int g, int width, int height) => InBounds(g.x, g.y, width, height);

        /// <summary>该步长是否是对角步（<see cref="Neighbors8"/> 后 4 个）。</summary>
        public static bool IsDiagonalStep(Vector2Int step) => step.x != 0 && step.y != 0;

        // ═════════════════════════════════════════════════════════════════════
        // 连通性（8 邻接 BFS）
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 从 <paramref name="from"/> 做一次 8 邻接 BFS，把可达格在 <paramref name="visited"/> 里标 <c>true</c>。
        /// </summary>
        /// <param name="isWalkable">可走查询（**图外必须返回 false**，见文件头契约 ①）。</param>
        /// <param name="width">格图宽（格）。</param>
        /// <param name="height">格图高（格）。</param>
        /// <param name="from">起点格（必须可走；不可走 ⇒ 返回 0）。</param>
        /// <param name="visited">由调用方按 <c>[width,height]</c> 分配、初值全 <c>false</c> 的数组。</param>
        /// <returns>可达格数（含起点）；起点不可走 / 入参非法 ⇒ 0。</returns>
        public static int FloodFill(Func<Vector2Int, bool> isWalkable, int width, int height,
            Vector2Int from, bool[,] visited)
        {
            if (isWalkable == null)
            {
                // 非预期分支：漏传可走查询属编程错误（每条链路只报一次）
                LogThrottle.ErrorOnce(Tag, "floodfill.null-pred", "FloodFill: isWalkable 为 null ⇒ 返回 0");
                return 0;
            }
            if (width <= 0 || height <= 0) return 0;
            if (visited == null || visited.GetLength(0) != width || visited.GetLength(1) != height)
            {
                // 非预期分支：visited 尺寸不符（调用方按别的图分配了）⇒ 结果不可信，拒答
                LogThrottle.ErrorThrottled(Tag, "floodfill.bad-visited",
                    $"FloodFill: visited 尺寸不符（应为 {width}x{height}）⇒ 返回 0");
                return 0;
            }
            if (!InBounds(from, width, height) || !isWalkable(from)) return 0;

            var queue = new Queue<Vector2Int>();
            queue.Enqueue(from);
            visited[from.x, from.y] = true;
            var reached = 1;

            while (queue.Count > 0)
            {
                var cur = queue.Dequeue();
                for (var i = 0; i < Neighbors8.Length; i++)
                {
                    var step = Neighbors8[i];
                    var nx = cur.x + step.x;
                    var ny = cur.y + step.y;
                    if (!InBounds(nx, ny, width, height) || visited[nx, ny]) continue;
                    if (!isWalkable(new Vector2Int(nx, ny))) continue;

                    if (IsDiagonalStep(step))
                    {
                        // 对角：两侧格都必须可走（否则 BFS 会「贴着墙角穿过去」而 A* 不会）
                        if (!InBounds(cur.x + step.x, cur.y, width, height)) continue;
                        if (!InBounds(cur.x, cur.y + step.y, width, height)) continue;
                        if (!isWalkable(new Vector2Int(cur.x + step.x, cur.y))) continue;
                        if (!isWalkable(new Vector2Int(cur.x, cur.y + step.y))) continue;
                    }

                    visited[nx, ny] = true;
                    reached++;
                    queue.Enqueue(new Vector2Int(nx, ny));
                }
            }
            return reached;
        }

        /// <summary>
        /// 从 <paramref name="from"/> 做一次 BFS，数出 <paramref name="targets"/> 里**不可达**的目标数。
        /// </summary>
        /// <param name="reachedCount">可达格数（= <see cref="FloodFill"/> 的返回值；0 = 起点不可走/图未生成）。</param>
        /// <param name="firstUnreachable">第一个不可达的目标格（<c>unreachableCount == 0</c> 时无意义）。</param>
        /// <returns>不可达的目标数。<paramref name="targets"/> 为 null/空 ⇒ 0。</returns>
        /// <remarks>
        /// ⚠️ <paramref name="reachedCount"/> == 0 时**本方法仍会数**（结果是「全部目标都不可达」）。
        /// 调用方必须先判 <c>reachedCount == 0</c> 并当作整体失败提前返回 —— 旧实现就是这么做的。
        /// </remarks>
        public static int CountUnreachableTargets(Func<Vector2Int, bool> isWalkable, int width, int height,
            Vector2Int from, IReadOnlyList<Vector2Int> targets,
            out int reachedCount, out Vector2Int firstUnreachable)
        {
            reachedCount = 0;
            firstUnreachable = default;

            if (width <= 0 || height <= 0) return 0;

            var visited = new bool[width, height];
            reachedCount = FloodFill(isWalkable, width, height, from, visited);

            var unreachable = 0;
            if (targets == null) return 0;
            for (var i = 0; i < targets.Count; i++)
            {
                var t = targets[i];
                if (!InBounds(t, width, height) || !visited[t.x, t.y])
                {
                    if (unreachable == 0) firstUnreachable = t;
                    unreachable++;
                }
            }
            return unreachable;
        }

        /// <summary>
        /// 把「从 <paramref name="from"/> 走不到的孤立可走口袋」逐格交给 <paramref name="fill"/> 去填。
        /// <para>
        /// 为什么必须做（旧实现注释原话）：随机撒点（掉落 / 刷怪）一旦落进这种口袋，
        /// 掉落物永远拿不到、怪物永远打不了 —— 静默的玩法缺陷。
        /// </para>
        /// </summary>
        /// <param name="visited">
        /// **已被 <see cref="FloodFill"/> 填好的可达标记**（`true` = 从起点可达）。
        /// 由调用方先跑一次 BFS 再传进来 —— 这样「起点是否可达」由调用方判定并留痕（引擎不认识业务日志），
        /// 且**只跑一次 BFS**（本方法内部不再重跑）。
        /// </param>
        /// <param name="isRequired">
        /// 该格是否「必需可达目标」。非 null 且返回 true 的格**不填**（留给连通性自检判失败）；
        /// 传 <c>null</c> = 不做保护。
        /// </param>
        /// <param name="fill">对每个要填的格调用一次（写什么地形由调用方决定）。</param>
        /// <param name="keptProtected">被保护因而**未填**的孤立可走格数。</param>
        /// <returns>实际交给 <paramref name="fill"/> 的格数（<c>visited</c> 非法 ⇒ 0，**未做任何填充**）。</returns>
        public static int FillUnreachablePockets(Func<Vector2Int, bool> isWalkable, int width, int height,
            bool[,] visited, Func<Vector2Int, bool> isRequired, Action<int, int> fill, out int keptProtected)
        {
            keptProtected = 0;
            if (fill == null || width <= 0 || height <= 0) return 0;
            if (visited == null || visited.GetLength(0) != width || visited.GetLength(1) != height)
            {
                // 非预期分支：visited 没按本图 BFS 过（调用方传错数组）⇒ 结果会把可走区整片填掉，必须拒答
                LogThrottle.ErrorThrottled(Tag, "pockets.bad-visited",
                    $"FillUnreachablePockets: visited 尺寸不符（应为 {width}x{height}）⇒ 本次不做任何填充");
                return 0;
            }

            var filled = 0;
            // ⛔ 遍历顺序（x 外层升序、y 内层升序）与旧实现逐字一致：填充顺序决定不了结果，
            //    但保持同序可以让「改前/改后」的逐行取证脚本直接 diff。
            for (var x = 0; x < width; x++)
            {
                for (var y = 0; y < height; y++)
                {
                    if (visited[x, y]) continue;
                    var g = new Vector2Int(x, y);
                    if (!isWalkable(g)) continue;

                    if (isRequired != null && isRequired(g)) { keptProtected++; continue; }
                    fill(x, y);
                    filled++;
                }
            }
            return filled;
        }

        // ═════════════════════════════════════════════════════════════════════
        // 边界环封
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 把**边界环**（距任一地图边 &lt; <paramref name="n"/> 格的所有格）里现在还可走的格，
        /// 逐格交给 <paramref name="seal"/> 去封成不可走地形。
        /// <para>
        /// 为什么封环而不是在相机侧夹：相机侧夹要求「机位离边界 ≥ 半屏可见格数」，
        /// 而可走区铺到最外圈时玩家自己能走到离边界 1 格处 ⇒ 两侧数学互斥（旧实现注释原话）。
        /// </para>
        /// </summary>
        /// <param name="n">环宽（格，必须 &gt; 0；<c>&lt;= 0</c> ⇒ 返回 0 且不写任何格）。</param>
        /// <param name="seal">对每个要封的格调用一次（封成什么地形由调用方决定）。</param>
        /// <returns>被封掉的格数（= 0 属非预期分支，由调用方留痕）。</returns>
        public static int SealBorderRing(int width, int height, int n,
            Func<Vector2Int, bool> isWalkable, Action<int, int> seal)
        {
            if (n <= 0 || seal == null || isWalkable == null || width <= 0 || height <= 0) return 0;

            var sealedCount = 0;
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var d = Mathf.Min(Mathf.Min(x, width - 1 - x), Mathf.Min(y, height - 1 - y));
                    if (d >= n) continue;
                    if (!isWalkable(new Vector2Int(x, y))) continue;
                    seal(x, y);
                    sealedCount++;
                }
            }
            return sealedCount;
        }

        // ═════════════════════════════════════════════════════════════════════
        // 可走格索引（缓存 + O(1) 抽样）
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 「可走格列表」索引：<see cref="Rebuild"/> 一次填好，之后 <see cref="Pick"/> 是 O(1)。
        /// <para>
        /// ⚠️ **填充顺序是契约**：<c>x</c> 外层升序、<c>y</c> 内层升序（见文件头 ★）。
        /// </para>
        /// <para>非线程安全；主线程使用。</para>
        /// </summary>
        public sealed class WalkableIndex
        {
            private readonly List<Vector2Int> _cells = new List<Vector2Int>();

            /// <summary>可走格（**只读**，顺序 = 重建时的遍历顺序）。</summary>
            public IReadOnlyList<Vector2Int> Cells => _cells;

            /// <summary>可走格数。</summary>
            public int Count => _cells.Count;

            /// <summary>清空（不重建）。</summary>
            public void Clear() => _cells.Clear();

            /// <summary>
            /// 按「<c>x</c> 升序外层、<c>y</c> 升序内层」重扫整图，重建可走格列表。
            /// </summary>
            /// <returns>可走格数（= <see cref="Count"/>）。</returns>
            public int Rebuild(Func<Vector2Int, bool> isWalkable, int width, int height)
            {
                _cells.Clear();
                if (isWalkable == null || width <= 0 || height <= 0) return 0;

                for (var x = 0; x < width; x++)
                {
                    for (var y = 0; y < height; y++)
                    {
                        if (isWalkable(new Vector2Int(x, y))) _cells.Add(new Vector2Int(x, y));
                    }
                }
                return _cells.Count;
            }

            /// <summary>
            /// 取第 <paramref name="index"/> 个可走格（调用方通常传 <c>rng.Next(Count)</c>）。
            /// <paramref name="index"/> 越界 ⇒ 收敛到合法下标 + 降频 Warn（正常流程不该发生）。
            /// </summary>
            public Vector2Int Pick(int index)
            {
                if (_cells.Count == 0)
                {
                    LogThrottle.WarnThrottled(Tag, "walkable-index.empty",
                        "WalkableIndex.Pick: 索引为空（本图没有任何可走格？）⇒ 返回 (0,0)");
                    return default;
                }
                if (index < 0 || index >= _cells.Count)
                {
                    LogThrottle.WarnThrottled(Tag, "walkable-index.oob",
                        $"WalkableIndex.Pick: index={index} 越界（Count={_cells.Count}）⇒ 收敛到第 0 格");
                    return _cells[0];
                }
                return _cells[index];
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        // 批写：整图 / 矩形 / 直线 / 圆盘
        // ═════════════════════════════════════════════════════════════════════
        // ⛔ 这些方法只负责「遍历哪些格」，写什么由调用方在 write 里决定 ——
        //    因此**越界格也会原样交给 write**（旧实现经 `Set` 写，越界时由 `Set` 自己拦 + 留痕）。
        //    引擎不在这里替调用方吞掉越界，否则「数据写到图外」这类缺陷会静默消失。

        /// <summary>整图逐格（<c>x</c> 升序外层、<c>y</c> 升序内层）。</summary>
        public static void Fill(int width, int height, Action<int, int> write)
        {
            if (write == null || width <= 0 || height <= 0) return;
            for (var x = 0; x < width; x++)
            {
                for (var y = 0; y < height; y++) write(x, y);
            }
        }

        /// <summary>
        /// 矩形逐格（左下角 = <c>(x0,y0)</c>，尺寸 <c>w×h</c>，**超出图的部分自动裁剪**）。
        /// </summary>
        /// <returns><c>false</c> = 尺寸非法（<c>w &lt;= 0 || h &lt;= 0</c>），**一次都没写**；由调用方留痕。</returns>
        public static bool FillRect(int x0, int y0, int w, int h, int width, int height, Action<int, int> write)
        {
            if (w <= 0 || h <= 0 || write == null) return false;
            for (var x = Mathf.Max(0, x0); x < Mathf.Min(width, x0 + w); x++)
            {
                for (var y = Mathf.Max(0, y0); y < Mathf.Min(height, y0 + h); y++) write(x, y);
            }
            return true;
        }

        /// <summary>水平线（**含两端**；端点顺序无所谓，内部归一化成升序）。</summary>
        public static void LineH(int y, int xFrom, int xTo, Action<int, int> write)
        {
            if (write == null) return;
            var lo = Mathf.Min(xFrom, xTo);
            var hi = Mathf.Max(xFrom, xTo);
            for (var x = lo; x <= hi; x++) write(x, y);
        }

        /// <summary>垂直线（**含两端**；端点顺序无所谓，内部归一化成升序）。</summary>
        public static void LineV(int x, int yFrom, int yTo, Action<int, int> write)
        {
            if (write == null) return;
            var lo = Mathf.Min(yFrom, yTo);
            var hi = Mathf.Max(yFrom, yTo);
            for (var y = lo; y <= hi; y++) write(x, y);
        }

        /// <summary>
        /// 圆盘逐格（<c>dx*dx + dy*dy &lt;= r*r</c>，**无条件**写；挖洞 / 开地用它）。
        /// 越界格照样交给 <paramref name="write"/>（见本节说明）。
        /// </summary>
        public static void FillDisk(Vector2Int center, int r, Action<int, int> write)
        {
            if (write == null) return;
            for (var x = center.x - r; x <= center.x + r; x++)
            {
                for (var y = center.y - r; y <= center.y + r; y++)
                {
                    var dx = x - center.x;
                    var dy = y - center.y;
                    if (dx * dx + dy * dy > r * r) continue;
                    write(x, y);
                }
            }
        }

        /// <summary>
        /// 圆盘逐格，但**只对当前可走的格**调用 <paramref name="write"/>（铺路 / 铺地砖用它）。
        /// <para>⚠️ 判定用 <paramref name="isWalkable"/>，因此**图外格不会被写**（契约 ①）。</para>
        /// </summary>
        public static void PaintDiskWalkable(Func<Vector2Int, bool> isWalkable, Vector2Int center, int r,
            Action<int, int> write)
        {
            if (write == null) return;
            for (var x = center.x - r; x <= center.x + r; x++)
            {
                for (var y = center.y - r; y <= center.y + r; y++)
                {
                    var dx = x - center.x;
                    var dy = y - center.y;
                    if (dx * dx + dy * dy > r * r) continue;
                    SetOnWalkable(isWalkable, x, y, write);
                }
            }
        }

        /// <summary>
        /// 该格可走才写（避免把土路 / 出入口覆盖掉）。
        /// </summary>
        /// <returns>是否真的写了。</returns>
        public static bool SetOnWalkable(Func<Vector2Int, bool> isWalkable, int x, int y, Action<int, int> write)
        {
            if (isWalkable == null || write == null) return false;
            if (!isWalkable(new Vector2Int(x, y))) return false;
            write(x, y);
            return true;
        }
    }
}
