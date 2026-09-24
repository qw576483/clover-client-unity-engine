// ─────────────────────────────────────────────────────────────────────────────
// CloverEngine · Runtime/Presentation/TileWorld.cs
// 2D 瓦片世界的**空间事实面**：实心格位图 + 移动托台的小数顶高 + 世界边界。通用底座。
//
// 口径（与 Runtime/Presentation/Map.cs:14-15 **逐字一致**，⛔ 不许在这里长第二套）：
//   ★ 本接口**只回答空间事实**（这一格实不实心 / 托台顶在哪 / 世界到哪为止）。
//     「输入 → 位移 → 贴墙滑动」那套解算**不在引擎** —— 用多大半径、几点采样、撞墙是停还是滑，
//     都是玩法手感，业务拿这里的事实自己写。
//   ⛔ 所以本接口**没有** `Move()` / `Step()` / `Resolve()` 之类方法 —— 加了就是把玩法塞进引擎，
//     两个项目会开始互相打架（一个要滑、一个要停）。
//
// ★ 为什么服务端 `CloverMap V1` 托不住 2D 语义（所以这里必须另起一个接口，而不是塞进 `IMapData`）：
//   引擎的 `IMapData` / `CloverMapFormat` 是 **3D MMO 单层平地** —— 查询签名就是
//   `WalkableAt(float x, float z)`（Runtime/Presentation/Map.cs:149，X-Z 平面 + 位图），
//   一张图只有一层、高度由地面标量决定。2D 平台要的是「同一 x 上有**多层**格 + 每格可有**小数顶高**
//   + 托台会动」，两套语义不同 ⇒ 不硬塞。
//
// 存储：两个哈希表 —— 实心 <c>HashSet&lt;long&gt;</c>、托台 <c>Dictionary&lt;long,float&gt;</c>，
//   键 = `((long)tx &lt;&lt; 32) | (uint)ty`（高 32 位 tx、低 32 位 ty，**双射**、不会碰撞）。
//   · 稀疏地图（典型 2D 关卡实心格 ~10^3）远小于 `bool[w*d]` 二维数组；
//   · ⛔ 键**不用** 32 位写法 `(tx &lt;&lt; 16) ^ (ty + 512)`：32 位键在
//     `|tx| ≥ 2^15`（`tx &lt;&lt; 16` 溢出）或 ty 超出 [-512, 65022] 时会**键碰撞** ⇒ 误判实心
//     （"明明没有砖却撞上了"，且不报错）。64 位键每键 8 字节，换掉一类不可诊断的 bug。
// ─────────────────────────────────────────────────────────────────────────────

using System.Collections.Generic;
using UnityEngine;

namespace CloverEngine
{
    /// <summary>
    /// 2D 瓦片世界的空间事实面（**只回答事实，不给位移解算**，口径同 <c>Map.cs:14-15</c>）。
    /// <para>
    /// 实现要求：稀疏存储（不要 <c>bool[宽*高]</c>）；<see cref="IsSolid"/> 越界一律 <c>false</c>、
    /// 不抛；<see cref="TryGetCarrierTop"/> 查不到返回 <c>false</c>、不抛（"这一格没有托台"是正常查询）。
    /// </para>
    /// <para>
    /// 边界（<see cref="MinX"/> / <see cref="MaxX"/> / <see cref="GroundTopY"/>）是**世界事实**，
    /// 由构建期 <see cref="SetBounds"/> 给一次；<see cref="Clear"/> 只清格子数据、不动边界。
    /// </para>
    /// </summary>
    public interface ITileWorld
    {
        /// <summary>该格是否实心（**越界一律 false**，不抛、不打日志）。</summary>
        bool IsSolid(int tx, int ty);

        /// <summary>
        /// 世界坐标所在格是否实心。世界 → 格**必须** <see cref="Mathf.FloorToInt"/>：
        /// `(int)` 强转对负数向零截断（x = -0.5 ⇒ 第 0 格），会让"站在坑里也能踩到地"。
        /// </summary>
        bool IsSolidAt(float x, float y);

        /// <summary>置 / 清一个实心格（<paramref name="solid"/> 默认 <c>true</c> = 置实心）。</summary>
        void SetSolid(int tx, int ty, bool solid = true);

        /// <summary>
        /// 清空全部**格子数据**（实心 + 托台）。
        /// **不动**世界边界 / 地面顶高 —— 那是"世界事实"，重进关卡时由 <see cref="SetBounds"/> 重新给。
        /// </summary>
        void Clear();

        /// <summary>
        /// 移动托台的**小数顶高**（世界 y，例如平台顶面在 y = 3.25）。
        /// 查不到 ⇒ 返回 <c>false</c>（<paramref name="topY"/> 置 0），**不抛、不打日志**
        /// —— "这一格没有托台"是正常查询，不是错误分支。
        /// </summary>
        bool TryGetCarrierTop(int tx, int ty, out float topY);

        /// <summary>登记移动托台：该格顶面在世界 y = <paramref name="topY"/>。非有限值 ⇒ 忽略 + 报一次 Error。</summary>
        void SetCarrierTop(int tx, int ty, float topY);

        /// <summary>撤销托台登记（托台离开该格）。</summary>
        void ClearCarrierTop(int tx, int ty);

        /// <summary>世界左边界（世界 x）。未 <see cref="SetBounds"/> 前为 0（配合 <see cref="HasBounds"/> 判）。</summary>
        float MinX { get; }

        /// <summary>世界右边界（世界 x）。未 <see cref="SetBounds"/> 前为 0。</summary>
        float MaxX { get; }

        /// <summary>地面顶面高度（世界 y）。未 <see cref="SetBounds"/> 前为 0。</summary>
        float GroundTopY { get; }

        /// <summary>是否已给过边界（<c>false</c> 时上面三个值都是 0，**不代表**世界真的到 0）。</summary>
        bool HasBounds { get; }

        /// <summary>
        /// 给一次世界边界与地面顶高（关卡构建期调用）。
        /// 非有限值 ⇒ 忽略 + Error；<paramref name="minX"/> &gt; <paramref name="maxX"/> ⇒ 降频 Warn 后**交换**（归一化）。
        /// </summary>
        void SetBounds(float minX, float maxX, float groundTopY);

        /// <summary>实心格数量（诊断 / 自检用）。</summary>
        int SolidCount { get; }

        /// <summary>已登记托台格数量（诊断 / 自检用）。</summary>
        int CarrierCount { get; }
    }

    /// <summary>
    /// <see cref="ITileWorld"/> 的默认实现：两个哈希表 + 64 位压缩键（口径见文件头）。
    /// <para>纯数据，无 MonoBehaviour、无 GameObject；<see cref="TryGetCarrierTop"/> 等查询不产生日志。</para>
    /// </summary>
    public sealed class TileWorld : ITileWorld
    {
        private const string Tag = "TileWorld";

        /// <summary>实心格。初值容量按典型 2D 关卡给（几千格），避免构建期反复扩容。</summary>
        private readonly HashSet<long> _solid = new HashSet<long>(1024);

        /// <summary>托台格 → 顶面世界 y（数量极少：典型关卡 0~2 个）。</summary>
        private readonly Dictionary<long, float> _carriers = new Dictionary<long, float>(8);

        /// <inheritdoc />
        public float MinX { get; private set; }

        /// <inheritdoc />
        public float MaxX { get; private set; }

        /// <inheritdoc />
        public float GroundTopY { get; private set; }

        /// <inheritdoc />
        public bool HasBounds { get; private set; }

        /// <inheritdoc />
        public int SolidCount => _solid.Count;

        /// <inheritdoc />
        public int CarrierCount => _carriers.Count;

        /// <inheritdoc />
        public bool IsSolid(int tx, int ty) => _solid.Contains(Key(tx, ty));

        /// <inheritdoc />
        public bool IsSolidAt(float x, float y) => IsSolid(Mathf.FloorToInt(x), Mathf.FloorToInt(y));

        /// <inheritdoc />
        public void SetSolid(int tx, int ty, bool solid = true)
        {
            var k = Key(tx, ty);
            if (solid)
            {
                // 越界仍记入（静默丢弃会让"数据里有、查询却没有"变成不可诊断），只降频留痕
                WarnIfOutsideBounds(tx, "SetSolid");
                _solid.Add(k);
            }
            else
            {
                _solid.Remove(k);
            }
        }

        /// <inheritdoc />
        public void Clear()
        {
            _solid.Clear();
            _carriers.Clear();
        }

        /// <inheritdoc />
        public bool TryGetCarrierTop(int tx, int ty, out float topY) =>
            _carriers.TryGetValue(Key(tx, ty), out topY);

        /// <inheritdoc />
        public void SetCarrierTop(int tx, int ty, float topY)
        {
            if (float.IsNaN(topY) || float.IsInfinity(topY))
            {
                // 非预期分支：NaN/Inf 顶高会让"落在台面上"的解算结果不可预测 ⇒ 忽略并只报一次
                LogThrottle.ErrorOnce(Tag, "carrier.non-finite",
                    $"SetCarrierTop({tx},{ty}) 收到非有限顶高（{topY}）⇒ 忽略本次登记");
                return;
            }

            WarnIfOutsideBounds(tx, "SetCarrierTop");
            _carriers[Key(tx, ty)] = topY;
        }

        /// <inheritdoc />
        public void ClearCarrierTop(int tx, int ty) => _carriers.Remove(Key(tx, ty));

        /// <inheritdoc />
        public void SetBounds(float minX, float maxX, float groundTopY)
        {
            if (float.IsNaN(minX) || float.IsInfinity(minX) ||
                float.IsNaN(maxX) || float.IsInfinity(maxX) ||
                float.IsNaN(groundTopY) || float.IsInfinity(groundTopY))
            {
                // 非预期分支：边界是非有限值 ⇒ 保持原边界（写进去会让所有边界判定变成 NaN 比较）
                LogThrottle.ErrorOnce(Tag, "bounds.non-finite",
                    $"SetBounds({minX}, {maxX}, {groundTopY}) 含非有限值 ⇒ 忽略本次设置（保留原边界）");
                return;
            }

            if (minX > maxX)
            {
                // 非预期分支：左右反了（构建期传参顺序错）⇒ 归一化后仍可用，但必须留痕
                LogThrottle.WarnThrottled(Tag, "bounds.reversed",
                    $"SetBounds 的 minX({minX}) > maxX({maxX}) ⇒ 已交换后使用（检查调用方的传参顺序）");
                var t = minX;
                minX = maxX;
                maxX = t;
            }

            MinX = minX;
            MaxX = maxX;
            GroundTopY = groundTopY;
            HasBounds = true;
        }

        /// <summary>
        /// 压缩键：高 32 位 = tx、低 32 位 = (uint)ty ⇒ 对 (tx, ty) 是**双射**（任意 int 组合都不同键）。
        /// ⛔ 不要换成 `(tx &lt;&lt; 16) ^ (ty + 512)` 那种 32 位方案，见文件头说明。
        /// </summary>
        private static long Key(int tx, int ty) => ((long)tx << 32) | (uint)ty;

        /// <summary>只在**已给过边界**时检查横向越界，并降频留痕（纵向不设限：地面之上全是空中格）。</summary>
        private void WarnIfOutsideBounds(int tx, string source)
        {
            if (!HasBounds) return;
            var lo = Mathf.FloorToInt(MinX);
            var hi = Mathf.FloorToInt(MaxX);
            if (tx >= lo && tx <= hi) return;

            LogThrottle.WarnThrottled(Tag, "tile.out-of-bounds",
                $"{source}: 格 tx={tx} 在世界横向边界 [{lo},{hi}] 之外（MinX={MinX} MaxX={MaxX}）" +
                "—— 数据越界通常来自关卡数据的行/列偏移；本次仍记入，不静默丢弃");
        }
    }
}
