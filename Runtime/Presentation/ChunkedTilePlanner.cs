// ─────────────────────────────────────────────────────────────────────────────
// CloverEngine · Runtime/Presentation/ChunkedTilePlanner.cs
//
// 【出处】下沉自 `clover-project-diablo2` 的 `Module/Map/MapView.cs`
//   可见格范围 → chunk 编号（`:1352-1609` ComputeVisibleChunkRange / PlannedChunks）、
//   每帧节点预算准入（`:822-852` FrameAccepts）、整图重铺的双缓冲换帧（`:2660-2679`）。
//
// 【为什么下沉】「大地图按可见范围只建可见块 + 每帧节点预算分帧 + 整图重铺双缓冲」
//   与题材无关：任何瓦片/网格大地图都要这一层，而引擎此前**一个 chunk/fog 设施都没有**
//   （全仓 grep `chunk` 只在网络层命中无关词）。本件是**纯逻辑规划器** —— 不碰渲染、
//   不建节点、不知素材，只回答「这一帧该处理哪些块、还剩多少节点额度、该不该换缓冲」。
//
// 【用法】持有一个实例（每张地图一个），每帧：
//   planner.BeginFrame();
//   planner.ChunkRangeOf(visMinX, visMaxX, visMinY, visMaxY, out cx0, out cx1, out cy0, out cy1);
//   planner.EnumerateChunks(cx0, cx1, cy0, cy1, _scratch);
//   foreach (var key in _scratch) { if (!planner.TryAccept(estimatedNodes)) break; BuildChunk(key); }
//   planner.RequestRebuild();  ... 铺完一帧后再 planner.ConsumeRebuild();
//
// 【已知边界】⛔ 不含：视锥计算（可见格范围由调用方给）、节点池、贴图请求、迷雾状态、
//   碰撞/可走性 —— 那些分别属 `TileWorld` / `TileNodePool` / `TileRenderer` / 业务。
//   负坐标用「向下取整」口径的整除（`FloorDiv`），别用 C# 的截断除法。
// ⛔ 本件是引擎新增件，项目侧 `MapView.cs` 的接线**尚未做**（其现有实现保持可用）。
// ─────────────────────────────────────────────────────────────────────────────

using System.Collections.Generic;
using UnityEngine;

namespace CloverEngine
{
    /// <summary>一个 chunk 的编号（格坐标除以 <see cref="ChunkedTilePlanner.ChunkSize"/> 向下取整）。</summary>
    public struct ChunkKey : System.IEquatable<ChunkKey>
    {
        public int X;
        public int Y;

        public ChunkKey(int x, int y) { X = x; Y = y; }

        public bool Equals(ChunkKey other) => X == other.X && Y == other.Y;

        public override bool Equals(object obj) => obj is ChunkKey k && Equals(k);

        public override int GetHashCode() => (X * 397) ^ Y;

        public override string ToString() => "chunk(" + X + "," + Y + ")";
    }

    /// <summary>
    /// 大地图分块渲染的**纯逻辑**规划器：可见块范围 / 每帧节点预算 / 双缓冲换帧。
    /// <para>⛔ 不依赖 Unity 渲染设施、不建节点、不查素材；全部副作用只有本类内部的两个计数器。</para>
    /// </summary>
    public sealed class ChunkedTilePlanner
    {
        private int _front;
        private bool _rebuildPending;
        private int _accepted;
        private int _rejected;

        /// <param name="chunkSize">一个 chunk 的边长（格）。≤0 ⇒ 取 1 并留一次告警。</param>
        /// <param name="maxNodesPerFrame">每帧允许新建/重写的节点上限（≤0 = 不限）。</param>
        public ChunkedTilePlanner(int chunkSize, int maxNodesPerFrame)
        {
            if (chunkSize <= 0)
            {
                LogThrottle.WarnOnce("ChunkedTilePlanner", "ctor.chunk-size",
                    "chunkSize ≤ 0 ⇒ 强制取 1（否则整除与块编号全错）");
                chunkSize = 1;
            }

            ChunkSize = chunkSize;
            MaxNodesPerFrame = maxNodesPerFrame < 0 ? 0 : maxNodesPerFrame;
        }

        /// <summary>一个 chunk 的边长（格）。</summary>
        public int ChunkSize { get; }

        /// <summary>每帧节点预算（**0 = 不限**，与引擎音效闸门同一口径）。</summary>
        public int MaxNodesPerFrame { get; }

        /// <summary>当前前缓冲编号（0/1）——「现在画着的那一份」。</summary>
        public int FrontBuffer => _front;

        /// <summary>当前后缓冲编号（0/1）——「正在重铺的那一份」。</summary>
        public int BackBuffer => 1 - _front;

        /// <summary>本帧已被准入的节点数。</summary>
        public int AcceptedThisFrame => _accepted;

        /// <summary>本帧被预算拒掉的节点数（&gt;0 ⇒ 铺图被推迟到下一帧，属正常节流）。</summary>
        public int RejectedThisFrame => _rejected;

        /// <summary>是否有已铺完、等待换帧的重铺结果。</summary>
        public bool RebuildPending => _rebuildPending;

        /// <summary>开一帧：清零本帧预算计数（⛔ 每帧**必须**调一次，否则预算永不被复位）。</summary>
        public void BeginFrame()
        {
            _accepted = 0;
            _rejected = 0;
        }

        /// <summary>
        /// 申请 <paramref name="nodeCost"/> 个节点额度。
        /// <para>返回 false ⇒ 本帧额度已满，调用方应**停止本帧铺图**（不是报错、不是丢掉这一块：
        /// 下一帧再来）。`MaxNodesPerFrame == 0` 时恒 true。</para>
        /// </summary>
        public bool TryAccept(int nodeCost)
        {
            if (nodeCost <= 0) return true;

            if (MaxNodesPerFrame <= 0)
            {
                _accepted += nodeCost;
                return true;
            }

            if (_accepted + nodeCost > MaxNodesPerFrame)
            {
                _rejected += nodeCost;
                return false;
            }

            _accepted += nodeCost;
            return true;
        }

        /// <summary>标记「整图重铺完毕，下一次 <see cref="ConsumeRebuild"/> 时换缓冲」。</summary>
        public void RequestRebuild() => _rebuildPending = true;

        /// <summary>
        /// 消费一次重铺：换缓冲（back → front）并返回 true；没有待换 ⇒ 返回 false（幂等）。
        /// </summary>
        public bool ConsumeRebuild()
        {
            if (!_rebuildPending) return false;

            _rebuildPending = false;
            _front = 1 - _front;
            return true;
        }

        /// <summary>格坐标 → chunk 编号（**向下取整**，负坐标安全；C# 的 `/` 会向 0 截断，别直接用）。</summary>
        public int FloorDiv(int v)
        {
            var q = v / ChunkSize;
            if (v < 0 && q * ChunkSize != v) q--;
            return q;
        }

        /// <summary>把可见格矩形（含端点）换算成 chunk 编号区间（含端点）。</summary>
        public void ChunkRangeOf(int minGx, int maxGx, int minGy, int maxGy,
            out int cx0, out int cx1, out int cy0, out int cy1)
        {
            if (maxGx < minGx) { var t = minGx; minGx = maxGx; maxGx = t; }
            if (maxGy < minGy) { var t = minGy; minGy = maxGy; maxGy = t; }

            cx0 = FloorDiv(minGx);
            cx1 = FloorDiv(maxGx);
            cy0 = FloorDiv(minGy);
            cy1 = FloorDiv(maxGy);
        }

        /// <summary>区间内 chunk 总数（含端点）。</summary>
        public static int ChunkCount(int cx0, int cx1, int cy0, int cy1)
        {
            var w = cx1 - cx0 + 1;
            var h = cy1 - cy0 + 1;
            return w <= 0 || h <= 0 ? 0 : w * h;
        }

        /// <summary>
        /// **追加**区间内的 chunk 坐标（`Vector2Int` 形态）到 <paramref name="into"/>；
        /// ⛔ **不清空**（与消费方 `MapView.PlannedChunks` 的既有语义一致：调用方自己决定清不清）。
        /// <para>⚠️ <paramref name="xOuter"/> 决定**遍历次序**，它会影响同 `sortingOrder` 的兄弟序 ⇒
        /// **画面逐像素**：`true` = cx 外层 / cy 内层（diablo2 `MapView.PlannedChunks` 的既有顺序），
        /// `false` = cy 外层（<see cref="EnumerateChunks"/> 的顺序）。改这条参数 = 改画面，别随手改。</para>
        /// </summary>
        public static void AppendChunkCoords(int cx0, int cx1, int cy0, int cy1, List<Vector2Int> into,
            bool xOuter = true, int maxCount = 0)
        {
            if (into == null)
            {
                LogThrottle.WarnOnce("ChunkedTilePlanner", "append.null",
                    "AppendChunkCoords 传入 null 列表 ⇒ 本次不枚举");
                return;
            }

            var limit = maxCount > 0 ? maxCount : int.MaxValue;
            var added = 0;

            if (xOuter)
            {
                for (var x = cx0; x <= cx1; x++)
                for (var y = cy0; y <= cy1; y++)
                {
                    if (added >= limit) return;
                    into.Add(new Vector2Int(x, y));
                    added++;
                }

                return;
            }

            for (var y = cy0; y <= cy1; y++)
            for (var x = cx0; x <= cx1; x++)
            {
                if (added >= limit) return;
                into.Add(new Vector2Int(x, y));
                added++;
            }
        }

        /// <summary>
        /// 枚举区间内的 chunk 到 <paramref name="into"/>（**先清空**再填，避免调用方忘清导致累积）。
        /// <paramref name="maxCount"/> &gt; 0 时最多写这么多（用于「本帧只看前 N 块」）。
        /// </summary>
        public void EnumerateChunks(int cx0, int cx1, int cy0, int cy1, List<ChunkKey> into, int maxCount = 0)
        {
            if (into == null)
            {
                LogThrottle.WarnOnce("ChunkedTilePlanner", "enum.null",
                    "EnumerateChunks 传入 null 列表 ⇒ 本次不枚举");
                return;
            }

            into.Clear();

            var limit = maxCount > 0 ? maxCount : int.MaxValue;
            for (var y = cy0; y <= cy1; y++)
            {
                for (var x = cx0; x <= cx1; x++)
                {
                    if (into.Count >= limit) return;
                    into.Add(new ChunkKey(x, y));
                }
            }
        }
    }
}
