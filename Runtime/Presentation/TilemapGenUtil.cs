// ─────────────────────────────────────────────────────────────────────────────
// CloverEngine · Runtime/Presentation/TilemapGenUtil.cs
// **程序化瓦片地图**的通用生成算法（与题材无关）：块级迷宫（全连通 + 环路）+ 按"四边开口 + 镜像"
// 拼块（含盖章叠加）。⛔ 块库 / 组码 / 方向位语义 / 原版规则表**全部由调用方以数据 + 委托传入**。
//
// 出处：clover-project-diablo2 `client/Assets/Scripts/Module/Map/`：
//   ① 块级随机 DFS 生成树 + 少量环路（保证全连通）
//      `MapGenCave.cs:109-154`（`conn[slotsX, slotsY, 4]` / `need[slotsX, slotsY]` / `visited` /
//       `Stack<Vector2Int>` DFS / `LoopMin..LoopMax` 随机环路）、`MapGenCave.cs:568-582`（`Connect`
//      双向对称 + `need |= DirBits` / `Opposite`）、`:584-596`（`PopCount` / `DescribeMask`）；
//   ② 按 tile 四边开口 + 镜像拼 tileset
//      `MapGenWilderness.cs:264-356`（周圈槽位的四边要求：**朝外闭 + 沿环开 + 朝内开**）、
//      `:388-458`（两轮蓄水池抽样：先"四条边全中"、再放宽为"朝外那条闭"；`EffectiveOpen` 翻 = 换边）、
//      `:719-755`（`Stamp` 盖章：逐格解出地形 / 分类 / 地面键 / 物件键，纯可走格与"原版什么都没有"的格不写）、
//      `:550-566`（`NearestWalkable` 环形搜索）。
//   ⇒ 本类逐条复刻上述**算法与判据**（连"候选顺序 = 方向下标升序""y 索引在前"这类细节都保留），
//     只把「槽/块/格长什么样、写下去算什么」换成委托与只读数据。
//
// 为什么下沉：程序化地图是**跨项目共性**（洞穴 / 荒野 / 地牢 / 战棋关卡都要），
//   而它最容易出的两类缺陷都是静默的：**连通性缺口**（玩家卡在走不到的口袋）与
//   **开口拼接错位**（走廊接不上、崖壁断开）。这两类判据一旦各项目各写一份，改一处必漏一处。
//
// 用法 + 首个消费方：
//   var loops = TilemapGenUtil.TryBuildSlotMaze(rng, sx, sy, 2, 5, out var conn, out var need, out var visited, out var added);
//   need[0, gateSlotY] |= Dir4Mask.W;                       // 强制开口由调用方自己 |=（例：洞口朝西）
//   TilemapGenUtil.RingSlotRequirements(i, j, cells, out var rn, out var rs, out var rw, out var re);
//   TilemapGenUtil.TryPickByEdges(rng, pieces, groupBorder, rn, rs, rw, re, out var idx, out var fx, out var fy, out var exact);
//   var blocked = TilemapGenUtil.StampPiece(pw, ph, fx, fy, si, sj, pitch, mapW, mapH, Decode, WriteCell);
//   项目侧 `Module/Map/{MapGenCave, MapGenWilderness}` 是它的薄封装（收尾片接线，本片不改项目文件）。
//
// 边界（⛔ 防止当万能药用）：
//   · **不含任何素材 / 区域 / 块名 / 组码含义** —— 组码与方向位对引擎而言只是整数与位掩码；
//   · **不含"什么算可走 / 什么算阻挡"** —— 那是调用方 `classChar → TileKind` 的事（`PieceCell` 只是载体）；
//   · 不含地图尺寸策略 / 边界环封闭 / 出生点挑选（那些依赖项目语义，仍留在项目侧）；
//   · 主线程使用；生成是冷路径，允许分配（但方法本身除返回的矩阵外不额外分配）。
// ─────────────────────────────────────────────────────────────────────────────

using System.Collections.Generic;

namespace CloverEngine
{
    /// <summary>四方向（**y 轴向北**：<c>N = +y</c>、<c>S = -y</c>、<c>W = -x</c>、<c>E = +x</c>，顺序即契约）。</summary>
    public enum Dir4
    {
        /// <summary>北（格增量 <c>+y</c>）。</summary>
        N = 0,

        /// <summary>南（格增量 <c>-y</c>）。</summary>
        S = 1,

        /// <summary>西（格增量 <c>-x</c>）。</summary>
        W = 2,

        /// <summary>东（格增量 <c>+x</c>）。</summary>
        E = 3,
    }

    /// <summary>四方向位掩码（值 = <c>1 &lt;&lt; (int)Dir4</c>；出处 `MapGenCave.cs:45` 的 <c>DirBits</c>）。</summary>
    [System.Flags]
    public enum Dir4Mask
    {
        /// <summary>不开通任何方向。</summary>
        None = 0,

        /// <summary>北。</summary>
        N = 1,

        /// <summary>南。</summary>
        S = 2,

        /// <summary>西。</summary>
        W = 4,

        /// <summary>东。</summary>
        E = 8,

        /// <summary>四方向全通。</summary>
        All = N | S | W | E,
    }

    /// <summary>某槽位对某条边的要求（<c>Free</c> = 不要求；出处 `MapGenWilderness.cs:371-381` 的 <c>Req</c>）。</summary>
    public enum EdgeRequirement
    {
        /// <summary>不要求。</summary>
        Free = 0,

        /// <summary>这条边必须是"开"的（该块这边能走出去）。</summary>
        Open = 1,

        /// <summary>这条边必须是"闭"的（崖壁 / 实心）。</summary>
        Closed = 2,
    }

    /// <summary>一块自身的四边开口（出处 `MapGenWildLayout.Piece.OpenN/S/W/E`）。</summary>
    public readonly struct PieceEdges
    {
        /// <summary>北边是否开通。</summary>
        public readonly bool N;

        /// <summary>南边是否开通。</summary>
        public readonly bool S;

        /// <summary>西边是否开通。</summary>
        public readonly bool W;

        /// <summary>东边是否开通。</summary>
        public readonly bool E;

        /// <summary>按 N/S/W/E 给定四边开口。</summary>
        public PieceEdges(bool n, bool s, bool w, bool e)
        {
            N = n;
            S = s;
            W = w;
            E = e;
        }
    }

    /// <summary>候选块（调用方自己的块 = <paramref name="Group"/> 组码 + 四边开口；下标即调用方数组下标）。</summary>
    public readonly struct EdgePiece
    {
        /// <summary>组码（含义由调用方定：边界块 / 过渡带 / 房间…）。</summary>
        public readonly int Group;

        /// <summary>该块的四边开口（未翻之前的自身朝向）。</summary>
        public readonly PieceEdges Edges;

        /// <summary>给定组码与四边开口。</summary>
        public EdgePiece(int group, PieceEdges edges)
        {
            Group = group;
            Edges = edges;
        }
    }

    /// <summary>块内一格的内容（**由调用方解码**：引擎只搬运）。</summary>
    public struct PieceCell
    {
        /// <summary>地形码（约定：<c>'.'</c> = 纯可走（不写）、<c>' '</c> = 原版这格什么都没有（不写）、其它 = 有内容）。</summary>
        public char Kind;

        /// <summary>阻挡物分类码（含义由调用方定，如 <c>'T'</c> 树 / <c>'C'</c> 崖壁）。</summary>
        public char Class;

        /// <summary>地面瓦片键（空串 = 保留目标格已有的地面，避免把基底抹掉）。</summary>
        public string Ground;

        /// <summary>物件瓦片键（空串 = 无物件层）。</summary>
        public string Object;

        /// <summary>给定四段内容。</summary>
        public PieceCell(char kind, char cellClass, string ground, string obj)
        {
            Kind = kind;
            Class = cellClass;
            Ground = ground;
            Object = obj;
        }
    }

    /// <summary>读块内一格（<c>(px, py)</c>，**0,0 = 块左上**）；返回 <c>false</c> = 该格在块里没有定义（跳过）。</summary>
    /// <param name="px">块内列（0 = 最左）。</param>
    /// <param name="py">块内行（0 = 最上）。</param>
    /// <param name="cell">解出的那一格。</param>
    public delegate bool PieceCellReader(int px, int py, out PieceCell cell);

    /// <summary>把一格写下去（调用方决定落什么 `TileKind` / 瓦片键；引擎不认内容语义）。</summary>
    /// <param name="gx">地图格 x。</param>
    /// <param name="gy">地图格 y。</param>
    /// <param name="cell">要落的那一格。</param>
    public delegate void PieceCellWriter(int gx, int gy, PieceCell cell);

    /// <summary>
    /// 程序化瓦片地图的通用生成算法（块级迷宫 + 四边开口拼块 + 盖章）。
    /// <para>⛔ 零项目专有：块库 / 组码 / 方向位语义 / 规则表**全部由调用方以数据 + 委托传入**。</para>
    /// </summary>
    public static class TilemapGenUtil
    {
        private const string Tag = "TilemapGenUtil";

        /// <summary>方向个数（<see cref="Dir4"/> 的取值个数）。</summary>
        public const int DirCount = 4;

        /// <summary>方向 → x 增量（<c>W = -1</c> / <c>E = +1</c> / 其余 0）。</summary>
        public static int Dx(Dir4 dir) => dir == Dir4.W ? -1 : (dir == Dir4.E ? 1 : 0);

        /// <summary>方向 → y 增量（<c>N = +1</c> / <c>S = -1</c> / 其余 0；y 轴向北 ⇒ 北在 y 大的一侧）。</summary>
        public static int Dy(Dir4 dir) => dir == Dir4.N ? 1 : (dir == Dir4.S ? -1 : 0);

        /// <summary>方向 → 位掩码（<c>1 &lt;&lt; (int)dir</c>）。</summary>
        public static Dir4Mask Bit(Dir4 dir) => (Dir4Mask)(1 << (int)dir);

        /// <summary>反方向（<c>N↔S</c>、<c>W↔E</c>；出处 `MapGenCave.cs:578-582`）。</summary>
        public static Dir4 Opposite(Dir4 dir)
            => dir == Dir4.N ? Dir4.S : (dir == Dir4.S ? Dir4.N : (dir == Dir4.W ? Dir4.E : Dir4.W));

        /// <summary>掩码里置位的方向个数（出处 `MapGenCave.cs:584-589`）。</summary>
        public static int PopCount(Dir4Mask mask)
        {
            var n = 0;
            for (var k = 0; k < DirCount; k++)
            {
                if ((mask & Bit((Dir4)k)) != Dir4Mask.None) n++;
            }
            return n;
        }

        /// <summary>掩码 → <c>"NSEW"</c> 子序列（空 = <c>"(无)"</c>；出处 `MapGenCave.cs:591-596`，供日志用）。</summary>
        public static string DescribeMask(Dir4Mask mask)
        {
            var s = string.Empty;
            for (var k = 0; k < DirCount; k++)
            {
                if ((mask & Bit((Dir4)k)) != Dir4Mask.None) s += Names[k];
            }
            return s.Length == 0 ? "(无)" : s;
        }

        private static readonly string[] Names = { "N", "S", "W", "E" };

        // ── ① 块级迷宫 ────────────────────────────────────────────────────────

        /// <summary>
        /// 块级迷宫：**随机 DFS 生成树（保证全连通）+ 少量环路**（出处 `MapGenCave.cs:109-154`）。
        /// <para>返回 <c>false</c> = 入参非法（<paramref name="rng"/> 为 null / 槽数为非正），
        /// 此时三个 out 参数分别为 <c>null / null / 0</c>（⛔ 不产生"半个迷宫"）。</para>
        /// <para>⛔ 强制开口（如"洞口那槽必须朝西开通"）由调用方在返回的 <c>requiredOpen</c> 上自行
        /// <c>|=</c> —— 那是项目规则，引擎不该知道。</para>
        /// </summary>
        /// <param name="rng">可复现随机器（⛔ 不用 `UnityEngine.Random`）。</param>
        /// <param name="slotsX">x 方向槽数（&gt; 0）。</param>
        /// <param name="slotsY">y 方向槽数（&gt; 0）。</param>
        /// <param name="loopsMin">环路条数下限（&lt; 0 按 0）。</param>
        /// <param name="loopsMax">环路条数上限（&lt; 下限按等于下限；闭区间随机）。</param>
        /// <param name="connections">出参：<c>[slotsX, slotsY, 4]</c> 双向对称的连通位（下标 = <see cref="Dir4"/>）。</param>
        /// <param name="requiredOpen">出参：每槽需要开通的方向位（生成树 + 环路累计）。</param>
        /// <param name="visitedSlots">出参：生成树覆盖的槽数（全连通时应 == slotsX × slotsY）。</param>
        /// <param name="addedLoops">出参：**实际新增**的环路条数（越界 / 已连通的那几次不算）。</param>
        public static bool TryBuildSlotMaze(Rng rng, int slotsX, int slotsY, int loopsMin, int loopsMax,
            out bool[,,] connections, out Dir4Mask[,] requiredOpen, out int visitedSlots, out int addedLoops)
        {
            connections = null;
            requiredOpen = null;
            visitedSlots = 0;
            addedLoops = 0;

            if (rng == null)
            {
                LogThrottle.WarnThrottled(Tag, "maze.null-rng", "TryBuildSlotMaze 的 rng 为 null ⇒ 不生成");
                return false;
            }
            if (slotsX <= 0 || slotsY <= 0)
            {
                LogThrottle.ErrorThrottled(Tag, "maze.bad-size",
                    $"TryBuildSlotMaze 槽数非正（slotsX={slotsX}, slotsY={slotsY}）⇒ 不生成");
                return false;
            }

            if (loopsMin < 0) loopsMin = 0;
            if (loopsMax < loopsMin) loopsMax = loopsMin;

            var conn = new bool[slotsX, slotsY, DirCount];
            var need = new Dir4Mask[slotsX, slotsY];
            var visited = new bool[slotsX, slotsY];

            // 槽 → 单个 int（`x * slotsY + y`）⇒ 栈里不装结构体；解码见下（除法 / 取余）
            var stack = new Stack<int>(slotsX * slotsY);
            var startX = rng.Next(slotsX);
            var startY = rng.Next(slotsY);
            stack.Push(startX * slotsY + startY);
            visited[startX, startY] = true;
            var count = 1;

            while (stack.Count > 0)
            {
                var cur = stack.Peek();
                var cx = cur / slotsY;
                var cy = cur % slotsY;

                // 候选 = 未访问的邻居；**方向下标升序**（顺序即契约：随机取第 k 个依赖于它）
                var total = 0;
                for (var k = 0; k < DirCount; k++)
                {
                    var nx = cx + Dx((Dir4)k);
                    var ny = cy + Dy((Dir4)k);
                    if (nx < 0 || ny < 0 || nx >= slotsX || ny >= slotsY) continue;
                    if (visited[nx, ny]) continue;
                    total++;
                }
                if (total == 0) { stack.Pop(); continue; }

                var pick = rng.Next(total);
                var dir = Dir4.N;
                for (var k = 0; k < DirCount; k++)
                {
                    var nx = cx + Dx((Dir4)k);
                    var ny = cy + Dy((Dir4)k);
                    if (nx < 0 || ny < 0 || nx >= slotsX || ny >= slotsY) continue;
                    if (visited[nx, ny]) continue;
                    if (pick == 0) { dir = (Dir4)k; break; }
                    pick--;
                }

                ConnectSlots(conn, need, cx, cy, dir);
                var mx = cx + Dx(dir);
                var my = cy + Dy(dir);
                visited[mx, my] = true;
                count++;
                stack.Push(mx * slotsY + my);
            }

            // 环路：随机挑若干「相邻但没连」的槽对连上（原地牢有环，纯树状动线太单一）
            var wantLoops = rng.Next(loopsMin, loopsMax + 1);
            for (var k = 0; k < wantLoops; k++)
            {
                var si = rng.Next(slotsX);
                var sj = rng.Next(slotsY);
                var dir = (Dir4)rng.Next(DirCount);
                var nx = si + Dx(dir);
                var ny = sj + Dy(dir);
                if (nx < 0 || ny < 0 || nx >= slotsX || ny >= slotsY) continue;   // 出界：这次作废（同出处）
                if (conn[si, sj, (int)dir]) continue;                             // 已连通：跳过
                ConnectSlots(conn, need, si, sj, dir);
                addedLoops++;
            }

            connections = conn;
            requiredOpen = need;
            visitedSlots = count;
            return true;
        }

        /// <summary>
        /// 双向连通一个槽对（<c>conn</c> 对称置位 + <c>need</c> 两侧各加开口位；
        /// 出处 `MapGenCave.cs:568-576`）。
        /// </summary>
        /// <returns>落位成功返回 true；越界 / 数据结构非 <c>[,,4]</c> 返回 false 并留痕。</returns>
        public static bool ConnectSlots(bool[,,] connections, Dir4Mask[,] requiredOpen, int slotX, int slotY, Dir4 dir)
        {
            if (connections == null || connections.Rank != 3 || connections.GetLength(2) < DirCount)
            {
                LogThrottle.ErrorOnce(Tag, "connect.bad-conn",
                    "ConnectSlots: connections 必须是非 null 的 [x, y, 4] 三维数组 ⇒ 本次不连通");
                return false;
            }

            var nx = slotX + Dx(dir);
            var ny = slotY + Dy(dir);
            if (slotX < 0 || slotY < 0 || nx < 0 || ny < 0
                || slotX >= connections.GetLength(0) || nx >= connections.GetLength(0)
                || slotY >= connections.GetLength(1) || ny >= connections.GetLength(1))
            {
                // 非预期分支：调用方挑到了界外的邻居槽（⛔ 出界是"这次作废"，不是异常）
                LogThrottle.WarnThrottled(Tag, "connect.out-of-range",
                    $"ConnectSlots 邻居槽越界（slot=({slotX},{slotY}) dir={dir} → ({nx},{ny})）⇒ 本次不连通");
                return false;
            }

            connections[slotX, slotY, (int)dir] = true;
            connections[nx, ny, (int)Opposite(dir)] = true;
            if (requiredOpen != null)
            {
                requiredOpen[slotX, slotY] |= Bit(dir);
                requiredOpen[nx, ny] |= Bit(Opposite(dir));
            }
            return true;
        }

        /// <summary>
        /// 连通性自检：从槽 (0,0) 起 BFS，返回是否**全连通**（出处 `MapGenCave.cs:256-260` 的
        /// `map.VerifyConnectivity` 的块级对应物 —— 离线判据，不必进 Play）。
        /// </summary>
        /// <param name="connections"><c>[slotsX, slotsY, 4]</c> 连通位。</param>
        /// <param name="unreachableSlots">出参：从 (0,0) 走不到的槽数（全连通应为 0）。</param>
        public static bool TryVerifySlotConnectivity(bool[,,] connections, int slotsX, int slotsY,
            out int unreachableSlots)
        {
            unreachableSlots = slotsX * slotsY;
            if (connections == null || slotsX <= 0 || slotsY <= 0)
            {
                LogThrottle.ErrorThrottled(Tag, "verify.bad-args",
                    $"TryVerifySlotConnectivity 入参非法（connections={(connections == null ? "null" : "非 null")}, " +
                    $"slotsX={slotsX}, slotsY={slotsY}）⇒ 判为不连通");
                return false;
            }

            var seen = new bool[slotsX, slotsY];
            var queue = new Queue<int>(slotsX * slotsY);
            seen[0, 0] = true;
            queue.Enqueue(0);
            var reached = 1;

            while (queue.Count > 0)
            {
                var cur = queue.Dequeue();
                var cx = cur / slotsY;
                var cy = cur % slotsY;
                for (var k = 0; k < DirCount; k++)
                {
                    if (!connections[cx, cy, k]) continue;
                    var nx = cx + Dx((Dir4)k);
                    var ny = cy + Dy((Dir4)k);
                    if (nx < 0 || ny < 0 || nx >= slotsX || ny >= slotsY) continue;
                    if (seen[nx, ny]) continue;
                    seen[nx, ny] = true;
                    reached++;
                    queue.Enqueue(nx * slotsY + ny);
                }
            }

            unreachableSlots = slotsX * slotsY - reached;
            return unreachableSlots == 0;
        }

        // ── ② 按四边开口 + 镜像挑块 / 拼块 ───────────────────────────────────

        /// <summary>
        /// 周圈（边界环）某槽位的**四边要求**：**朝外的那条边必须闭**（崖壁朝地图外圈）、
        /// **沿环上相邻的两条边必须开**（崖壁带连续、周圈能走通）、**朝内那条边必须开**（内部接得上）。
        /// <para>索引约定（出处 `MapGenWilderness.cs:294-330`）：<paramref name="i"/> = 列（0 = 西）、
        /// <paramref name="j"/> = 行（**0 = 北**，`cells-1` = 南）。</para>
        /// <para>非周圈槽位（i、j 都既不是 0 也不是 cells-1）四条边全 <c>Free</c>。</para>
        /// </summary>
        public static void RingSlotRequirements(int i, int j, int cells,
            out EdgeRequirement n, out EdgeRequirement s, out EdgeRequirement w, out EdgeRequirement e)
        {
            n = EdgeRequirement.Free;
            s = EdgeRequirement.Free;
            w = EdgeRequirement.Free;
            e = EdgeRequirement.Free;

            if (cells <= 0 || i < 0 || j < 0 || i >= cells || j >= cells)
            {
                // 非预期分支：槽位越界（调用方循环写错）⇒ 明确留痕，不静默给一套"看着像对"的要求
                LogThrottle.ErrorThrottled(Tag, "ring.out-of-range",
                    $"RingSlotRequirements 槽位越界（i={i}, j={j}, cells={cells}）⇒ 四条边都不要求");
                return;
            }

            // 朝外 = 闭
            if (j == 0) n = EdgeRequirement.Closed;                 // 北边一行：朝外是北
            if (j == cells - 1) s = EdgeRequirement.Closed;         // 南边一行：朝外是南
            if (i == 0) w = EdgeRequirement.Closed;
            if (i == cells - 1) e = EdgeRequirement.Closed;

            // 沿环相邻 = 开（崖壁带连续）；朝内 = 开（内部接得上）
            if (j == 0)
            {
                if (i > 0) w = EdgeRequirement.Open;
                if (i < cells - 1) e = EdgeRequirement.Open;
                s = EdgeRequirement.Open;
            }
            else if (j == cells - 1)
            {
                if (i > 0) w = EdgeRequirement.Open;
                if (i < cells - 1) e = EdgeRequirement.Open;
                n = EdgeRequirement.Open;
            }
            else if (i == 0)
            {
                n = EdgeRequirement.Open;
                s = EdgeRequirement.Open;
                e = EdgeRequirement.Open;
            }
            else if (i == cells - 1)
            {
                n = EdgeRequirement.Open;
                s = EdgeRequirement.Open;
                w = EdgeRequirement.Open;
            }
        }

        /// <summary>
        /// 翻之后的四边开口：**左右翻交换 W/E，上下翻交换 N/S**（出处 `MapGenWilderness.cs:451-458`）。
        /// <para>⛔ 这是"镜像 == 原版对每条边用不同朝向的块"的全部实现 —— 翻错一边就会让走廊接不上，
        /// 且**不报错**（只会看到"这条路走不通"）。</para>
        /// </summary>
        public static PieceEdges EffectiveEdges(PieceEdges edges, bool flipX, bool flipY)
            => new PieceEdges(
                flipY ? edges.S : edges.N,
                flipY ? edges.N : edges.S,
                flipX ? edges.E : edges.W,
                flipX ? edges.W : edges.E);

        /// <summary>
        /// 在「<paramref name="group"/> 组 × 左右翻 × 上下翻」里挑一块满足四边要求的（**蓄水池抽样**，
        /// 一次遍历、均匀随机；出处 `MapGenWilderness.cs:388-437`）。
        /// <para>两轮：第一轮要求四条边**全部**命中；全不中时放宽为**只要求"要求闭的边确实是闭的"**
        /// （并降频 Warn 一次）。仍不中 ⇒ 返回 <c>false</c>（调用方自行兜底，如任取一块）。</para>
        /// <para><paramref name="exact"/> = 挑中的这块是否**精确**满足四条边（放宽分支下为 false，
        /// 供"拼接质量"自证用）。</para>
        /// </summary>
        /// <param name="index">出参：挑中的下标（调用方数组下标）。</param>
        /// <param name="flipX">出参：左右翻。</param>
        /// <param name="flipY">出参：上下翻。</param>
        public static bool TryPickByEdges(Rng rng, IReadOnlyList<EdgePiece> pieces, int group,
            EdgeRequirement n, EdgeRequirement s, EdgeRequirement w, EdgeRequirement e,
            out int index, out bool flipX, out bool flipY, out bool exact)
        {
            index = -1;
            flipX = false;
            flipY = false;
            exact = false;

            if (rng == null || pieces == null || pieces.Count == 0)
            {
                LogThrottle.WarnThrottled(Tag, "pick.bad-args",
                    $"TryPickByEdges 入参非法（rng={(rng == null ? "null" : "非 null")}, " +
                    $"pieces={(pieces == null ? "null" : pieces.Count.ToString())}）⇒ 挑不到块");
                return false;
            }

            var pick = -1;
            var pickFx = false;
            var pickFy = false;
            var count = 0;

            for (var pass = 0; pass < 2 && pick < 0; pass++)
            {
                var strict = pass == 0;
                for (var k = 0; k < pieces.Count; k++)
                {
                    var p = pieces[k];
                    if (p.Group != group) continue;
                    for (var m = 0; m < 4; m++)
                    {
                        var fx = (m & 1) != 0;
                        var fy = (m & 2) != 0;
                        var fe = EffectiveEdges(p.Edges, fx, fy);
                        var ok = strict
                            ? Check(n, fe.N) && Check(s, fe.S) && Check(w, fe.W) && Check(e, fe.E)
                            : CheckClosed(n, fe.N) && CheckClosed(s, fe.S)
                              && CheckClosed(w, fe.W) && CheckClosed(e, fe.E);
                        if (!ok) continue;
                        count++;
                        if (rng.Next(count) == 0) { pick = k; pickFx = fx; pickFy = fy; }
                    }
                }

                if (strict && pick < 0)
                {
                    LogThrottle.WarnThrottled(Tag, "pick.relax",
                        $"Group={group} 里没有「四条开口条件全中」的块 ⇒ 放宽为只要求" +
                        "「要求闭的那条边是闭的（崖壁朝外）」（本条只报一次）");
                }
            }

            if (pick < 0) return false;

            var chosen = EffectiveEdges(pieces[pick].Edges, pickFx, pickFy);
            exact = Check(n, chosen.N) && Check(s, chosen.S) && Check(w, chosen.W) && Check(e, chosen.E);
            index = pick;
            flipX = pickFx;
            flipY = pickFy;
            return true;
        }

        /// <summary>
        /// 在某一组里**任取一块**（蓄水池抽样；出处 `MapGenWilderness.cs:463-470` 的 <c>PickOne</c>）。
        /// </summary>
        /// <returns>下标；该组一块都没有时返回 <c>-1</c> 并降频 Warn。</returns>
        public static int PickByGroup(Rng rng, IReadOnlyList<EdgePiece> pieces, int group)
        {
            if (rng == null || pieces == null || pieces.Count == 0)
            {
                LogThrottle.WarnThrottled(Tag, "pickone.bad-args", "PickByGroup 入参非法 ⇒ 返回 -1");
                return -1;
            }

            var pick = -1;
            var count = 0;
            for (var i = 0; i < pieces.Count; i++)
            {
                if (pieces[i].Group != group) continue;
                count++;
                if (rng.Next(count) == 0) pick = i;
            }

            if (pick < 0)
            {
                LogThrottle.WarnThrottled(Tag, "pickone.empty", $"候选里没有 Group={group} 的块 ⇒ 返回 -1");
            }
            return pick;
        }

        /// <summary>
        /// 把一块**叠加**到槽 <c>(slotX, slotY)</c> 上（出处 `MapGenWilderness.cs:719-755` 的 `Stamp`）：
        /// <para>· 该块里"纯可走"的格（<c>Kind == '.'</c>）**不写** —— 那几格本来就是引擎铺的基底；</para>
        /// <para>· 该块里"原版什么都没有"的格（<c>Kind == ' '</c> 且地面键与物件键都空）也**不写**；</para>
        /// <para>· 只有"真的画了东西"的格才调 <paramref name="write"/>（地面 / 物件键照抄原版；地面为空
        /// 的格由调用方保留已有地面 —— 那是项目侧 `SetTiles` 的事，引擎只把空串如实传出去）。</para>
        /// <para>翻：<paramref name="flipY"/> ⇒ 块的"南半"落到本槽低 gy；<paramref name="flipX"/> ⇒ 列序左右翻转。</para>
        /// </summary>
        /// <returns>本次写入的格数（**阻挡与否由调用方在 `write` 里决定**，引擎不数它）。</returns>
        public static int StampPiece(int pieceW, int pieceH, bool flipX, bool flipY,
            int slotX, int slotY, int pitch, int mapW, int mapH,
            PieceCellReader read, PieceCellWriter write,
            bool skipPlainWalkable = true, bool skipEmpty = true)
        {
            if (read == null || write == null)
            {
                LogThrottle.ErrorOnce(Tag, "stamp.null-delegate",
                    "StampPiece 的 read / write 为 null ⇒ 本次不盖章");
                return 0;
            }
            if (pieceW <= 0 || pieceH <= 0 || pitch <= 0)
            {
                LogThrottle.ErrorThrottled(Tag, "stamp.bad-shape",
                    $"StampPiece 形状非法（pieceW={pieceW}, pieceH={pieceH}, pitch={pitch}）⇒ 本次不盖章");
                return 0;
            }

            var written = 0;
            for (var py = 0; py < pieceH; py++)
            {
                var dy = flipY ? (pieceH - 1 - py) : py;
                var gy = slotY * pitch + dy;
                for (var px = 0; px < pieceW; px++)
                {
                    var dx = flipX ? (pieceW - 1 - px) : px;
                    var gx = slotX * pitch + dx;
                    if (gx < 0 || gy < 0 || gx >= mapW || gy >= mapH) continue;
                    if (!read(px, py, out var cell)) continue;

                    if (skipPlainWalkable && cell.Kind == '.') continue;          // 纯可走：保持基底
                    if (skipEmpty && cell.Kind == ' '
                        && string.IsNullOrEmpty(cell.Ground) && string.IsNullOrEmpty(cell.Object)) continue;

                    write(gx, gy, cell);
                    written++;
                }
            }
            return written;
        }

        // ── ③ 环形搜索 ───────────────────────────────────────────────────────

        /// <summary>
        /// 离 <c>(cx, cy)</c> 最近的、满足 <paramref name="isWalkable"/> 的格（**环形搜索**，半径内逐环展开；
        /// 出处 `MapGenCave.cs:550-566` 的 `NearestWalkable`）。
        /// <para>搜索顺序即契约：<c>r = 0..radius</c>，每环 <c>dx = -r..r</c> 外层、<c>dy = -r..r</c> 内层，
        /// 只取"在环上"（<c>|dx| == r || |dy| == r</c>）的格 ⇒ 同输入恒得同一格。</para>
        /// </summary>
        /// <returns>找到返回 true；半径内一个都没有返回 false（<paramref name="gx"/> / <paramref name="gy"/> 置 0）。</returns>
        public static bool FindNearest(int cx, int cy, int radius,
            System.Func<int, int, bool> isWalkable, out int gx, out int gy)
        {
            gx = 0;
            gy = 0;
            if (isWalkable == null || radius < 0)
            {
                LogThrottle.WarnThrottled(Tag, "nearest.bad-args",
                    $"FindNearest 入参非法（isWalkable={(isWalkable == null ? "null" : "非 null")}, radius={radius}）");
                return false;
            }

            for (var r = 0; r <= radius; r++)
            {
                for (var dx = -r; dx <= r; dx++)
                {
                    for (var dy = -r; dy <= r; dy++)
                    {
                        if (r != 0 && System.Math.Abs(dx) != r && System.Math.Abs(dy) != r) continue;
                        var x = cx + dx;
                        var y = cy + dy;
                        if (!isWalkable(x, y)) continue;
                        gx = x;
                        gy = y;
                        return true;
                    }
                }
            }
            return false;
        }

        // ── 内部 ──────────────────────────────────────────────────────────────

        /// <summary>某个槽位要求 <paramref name="req"/> 与该块的实际开口 <paramref name="have"/> 是否相容。</summary>
        private static bool Check(EdgeRequirement req, bool have)
            => req == EdgeRequirement.Free || (req == EdgeRequirement.Open) == have;

        /// <summary>放宽口径：只校验"要求闭的边确实是闭的"，开 / 不要求都不管。</summary>
        private static bool CheckClosed(EdgeRequirement req, bool have)
            => req != EdgeRequirement.Closed || !have;
    }
}
