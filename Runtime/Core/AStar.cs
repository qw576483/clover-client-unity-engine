// ─────────────────────────────────────────────────────────────────────────────
// CloverEngine · Runtime/Core/AStar.cs
// 格子 A*（**8 邻接、对角需两侧都可走**）—— 回调式寻路，通用底座。
//
// 公开签名：`DefaultMaxNodes` / `Find` / `FindSmoothed` / `Smooth` / `HasLineOfSight` / `Describe`。
//
// 为什么用**回调**而不是直接依赖地图类型：
//   引擎不认识业务的地形数据结构 —— 只要给一个 `Func<Vector2Int,bool> walkable`，任何
//   任何格网表示（二维枚举数组 / 位图 / 体素切片）都能直接复用，**不必重造二进制格式**。
//   （引擎的 `MapBake` 产出的是**静态**烘焙地图，与「每局随机生成」的格网语义不同，故不是替代品。）
//
// 接口形状（契约）：
//   Find(Func<Vector2Int,bool> walkable, Vector2Int from, Vector2Int to, int maxNodes)
//   → List<Vector2Int>（含 from 与 to）；**不可达/入参非法返回 null**（并打日志）。
//
// 移动代价：直走 10、斜走 14（整数，避免浮点误差导致路径抖动）；启发式用八向距离（octile）。
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using UnityEngine;

namespace CloverEngine
{
    /// <summary>格子 A* 寻路（纯逻辑，无 MonoBehaviour；非线程安全，主线程使用）。</summary>
    public static class AStar
    {
        /// <summary>默认展开节点上限（超过即判不可达，防止大图/坏数据把帧卡死）。</summary>
        public const int DefaultMaxNodes = 20000;

        private const int CostStraight = 10;
        private const int CostDiagonal = 14;

        /// <summary>8 邻接（前 4 个是直走，后 4 个是斜走）。</summary>
        private static readonly Vector2Int[] Neighbors =
        {
            new Vector2Int(1, 0), new Vector2Int(-1, 0), new Vector2Int(0, 1), new Vector2Int(0, -1),
            new Vector2Int(1, 1), new Vector2Int(1, -1), new Vector2Int(-1, 1), new Vector2Int(-1, -1),
        };

        /// <summary>
        /// 求路径（**不平滑**，返回逐格路径）。
        /// </summary>
        /// <param name="walkable">可走查询（一般为地图模块的 `Walkable`）。为 null 时返回 null 并报错。</param>
        /// <param name="from">起点格（必须可走）。</param>
        /// <param name="to">终点格（必须可走）。</param>
        /// <param name="maxNodes">展开节点上限（&lt;= 0 时用 <see cref="DefaultMaxNodes"/>）。</param>
        /// <returns>含起点与终点的路径；**不可达/入参非法 → null**（已打日志）。起点==终点时返回单元素列表。</returns>
        public static List<Vector2Int> Find(Func<Vector2Int, bool> walkable, Vector2Int from, Vector2Int to,
            int maxNodes = DefaultMaxNodes)
        {
            if (walkable == null)
            {
                Game.Logger?.Error("AStar", "Find: walkable 为 null（调用方必须传可走查询），返回 null");
                return null;
            }

            if (!walkable(from))
            {
                LogThrottle.WarnThrottled("AStar", "astar.badstart", $"Find: 起点不可走 from={from}（角色被卡在障碍里？），返回 null");
                return null;
            }

            if (!walkable(to))
            {
                LogThrottle.WarnThrottled("AStar", "astar.badgoal", $"Find: 终点不可走 to={to}（点击落在障碍/图外？），返回 null");
                return null;
            }

            if (from == to)
            {
                return new List<Vector2Int> { from };
            }

            if (maxNodes <= 0) maxNodes = DefaultMaxNodes;

            var gScore = new Dictionary<Vector2Int, int> { [from] = 0 };
            var cameFrom = new Dictionary<Vector2Int, Vector2Int>();
            var closed = new HashSet<Vector2Int>();
            var open = new MinHeap();

            open.Push(from, Heuristic(from, to));
            var expanded = 0;

            while (open.Count > 0)
            {
                var current = open.Pop();
                if (!closed.Add(current)) continue;      // 惰性删除：同一格可能被多次入堆

                if (current == to)
                {
                    return Reconstruct(cameFrom, from, to);
                }

                expanded++;
                if (expanded > maxNodes)
                {
                    LogThrottle.WarnThrottled("AStar", "astar.budget",
                        $"Find: 展开节点超过上限 maxNodes={maxNodes}（from={from} to={to}），判为不可达");
                    return null;
                }

                var gCur = gScore[current];
                for (var i = 0; i < Neighbors.Length; i++)
                {
                    var step = Neighbors[i];
                    var next = new Vector2Int(current.x + step.x, current.y + step.y);
                    if (closed.Contains(next)) continue;
                    if (!walkable(next)) continue;

                    if (step.x != 0 && step.y != 0)
                    {
                        // 对角：两侧格都必须可走，否则会「贴着墙角穿过去」
                        if (!walkable(new Vector2Int(current.x + step.x, current.y))) continue;
                        if (!walkable(new Vector2Int(current.x, current.y + step.y))) continue;
                    }

                    var tentative = gCur + (step.x != 0 && step.y != 0 ? CostDiagonal : CostStraight);
                    if (gScore.TryGetValue(next, out var known) && tentative >= known) continue;

                    gScore[next] = tentative;
                    cameFrom[next] = current;
                    open.Push(next, tentative + Heuristic(next, to));
                }
            }

            LogThrottle.WarnThrottled("AStar", "astar.nopath", $"Find: 无可达路径 from={from} to={to}（地图连通性问题？），返回 null");
            return null;
        }

        /// <summary>
        /// 求路径并做**视线拉直**（沿可直视的线段删掉中间点，走路更自然）。
        /// 判定结果与 <see cref="Find"/> 一致（不可达同样返回 null）。
        /// </summary>
        public static List<Vector2Int> FindSmoothed(Func<Vector2Int, bool> walkable, Vector2Int from, Vector2Int to,
            int maxNodes = DefaultMaxNodes)
        {
            var raw = Find(walkable, from, to, maxNodes);
            return raw == null ? null : Smooth(raw, walkable);
        }

        /// <summary>
        /// 路径拉直：尽量用「可直视」的长线段替换折线。返回新列表，不改动入参。
        /// </summary>
        public static List<Vector2Int> Smooth(IReadOnlyList<Vector2Int> path, Func<Vector2Int, bool> walkable)
        {
            if (path == null)
            {
                LogThrottle.WarnThrottled("AStar", "smooth.null", "Smooth: path 为 null，返回 null");
                return null;
            }
            if (path.Count <= 2 || walkable == null)
            {
                return new List<Vector2Int>(path);
            }

            var result = new List<Vector2Int> { path[0] };
            var anchor = 0;

            for (var i = 2; i < path.Count; i++)
            {
                if (HasLineOfSight(walkable, path[anchor], path[i])) continue;

                result.Add(path[i - 1]);
                anchor = i - 1;
            }

            var last = path[path.Count - 1];
            if (result[result.Count - 1] != last) result.Add(last);
            return result;
        }

        /// <summary>
        /// 两格之间是否可直视（Bresenham 超覆盖：**对角步要求两侧格都可走**，
        /// 与 <see cref="Find"/> 的移动规则一致 —— 否则平滑出来的路径会穿墙）。
        /// </summary>
        public static bool HasLineOfSight(Func<Vector2Int, bool> walkable, Vector2Int a, Vector2Int b)
        {
            if (walkable == null)
            {
                Game.Logger?.Error("AStar", "HasLineOfSight: walkable 为 null，返回 false");
                return false;
            }

            var x = a.x;
            var y = a.y;
            var dx = Mathf.Abs(b.x - a.x);
            var dy = Mathf.Abs(b.y - a.y);
            var sx = a.x < b.x ? 1 : -1;
            var sy = a.y < b.y ? 1 : -1;
            var err = dx - dy;

            // 上限保护：异常入参不至于死循环（正常情况下步数 = max(dx,dy)+1）
            var guard = dx + dy + 8;
            while (guard-- > 0)
            {
                if (!walkable(new Vector2Int(x, y))) return false;
                if (x == b.x && y == b.y) return true;

                var e2 = 2 * err;
                var stepX = false;
                var stepY = false;

                if (e2 > -dy) { err -= dy; x += sx; stepX = true; }
                if (e2 < dx) { err += dx; y += sy; stepY = true; }

                if (stepX && stepY)
                {
                    if (!walkable(new Vector2Int(x - sx, y))) return false;
                    if (!walkable(new Vector2Int(x, y - sy))) return false;
                }
            }

            LogThrottle.WarnThrottled("AStar", "los.guard", $"HasLineOfSight: 超过步数保护（a={a} b={b}），判为不可直视");
            return false;
        }

        /// <summary>把路径打成正向可读字符串（日志取证用）：`(0,0)->(1,0)->…`。</summary>
        public static string Describe(IReadOnlyList<Vector2Int> path)
        {
            if (path == null) return "(null)";
            if (path.Count == 0) return "(empty)";
            var sb = new System.Text.StringBuilder(path.Count * 8);
            for (var i = 0; i < path.Count; i++)
            {
                if (i > 0) sb.Append("->");
                sb.Append('(').Append(path[i].x).Append(',').Append(path[i].y).Append(')');
            }
            return sb.ToString();
        }

        // ── 内部 ─────────────────────────────────────────────────────────────
        /// <summary>八向（octile）启发式：直走 10 / 斜走 14，**必须与移动代价同量纲**才是可采纳的。</summary>
        private static int Heuristic(Vector2Int a, Vector2Int b)
        {
            var dx = Mathf.Abs(a.x - b.x);
            var dy = Mathf.Abs(a.y - b.y);
            var min = Mathf.Min(dx, dy);
            var max = Mathf.Max(dx, dy);
            return CostStraight * max + (CostDiagonal - CostStraight) * min;
        }

        private static List<Vector2Int> Reconstruct(Dictionary<Vector2Int, Vector2Int> cameFrom,
            Vector2Int from, Vector2Int to)
        {
            var path = new List<Vector2Int> { to };
            var cur = to;
            var guard = 0;

            while (cur != from)
            {
                if (!cameFrom.TryGetValue(cur, out var prev))
                {
                    Game.Logger?.Error("AStar", $"Reconstruct: 回溯链在 {cur} 断开（from={from} to={to}），返回 null");
                    return null;
                }
                cur = prev;
                path.Add(cur);

                if (++guard > 100000)
                {
                    Game.Logger?.Error("AStar", $"Reconstruct: 回溯超过 100000 步（from={from} to={to}），数据异常，返回 null");
                    return null;
                }
            }

            path.Reverse();
            return path;
        }

        /// <summary>二叉最小堆（惰性删除：不实现 decrease-key，重复入堆由 `closed` 过滤）。</summary>
        private sealed class MinHeap
        {
            private struct Entry
            {
                public Vector2Int Pos;
                public int F;
            }

            private Entry[] _items = new Entry[256];

            public int Count { get; private set; }

            public void Push(Vector2Int pos, int f)
            {
                if (Count == _items.Length) Array.Resize(ref _items, _items.Length * 2);
                _items[Count] = new Entry { Pos = pos, F = f };
                var i = Count++;
                while (i > 0)
                {
                    var parent = (i - 1) / 2;
                    if (_items[parent].F <= _items[i].F) break;
                    (_items[parent], _items[i]) = (_items[i], _items[parent]);
                    i = parent;
                }
            }

            public Vector2Int Pop()
            {
                var root = _items[0].Pos;
                Count--;
                if (Count > 0)
                {
                    _items[0] = _items[Count];
                    var i = 0;
                    while (true)
                    {
                        var l = 2 * i + 1;
                        var r = l + 1;
                        var smallest = i;
                        if (l < Count && _items[l].F < _items[smallest].F) smallest = l;
                        if (r < Count && _items[r].F < _items[smallest].F) smallest = r;
                        if (smallest == i) break;
                        (_items[smallest], _items[i]) = (_items[i], _items[smallest]);
                        i = smallest;
                    }
                }
                return root;
            }
        }
    }
}
