using NUnit.Framework;
using UnityEngine.TestTools;

namespace CloverEngine.Tests
{
    /// <summary>
    /// <see cref="TileWorld"/>（<see cref="ITileWorld"/> 默认实现）的单元测试。
    /// <para>
    /// 重点判三类：① 稀疏键**不碰撞**（含 32 位键会碰撞的具体坐标对）；
    /// ② 世界→格用 FloorToInt（负数坐标）；③ 边界 / 托台 / Clear 的语义（Clear 不动边界）。
    /// </para>
    /// </summary>
    public class TileWorldTests
    {
        /// <summary>
        /// 本类有两条用例**故意**走异常分支（非有限顶高 / 非有限边界），按引擎规则必须打 Error，
        /// 而 Unity Test Runner 默认把 Error 日志判为测试失败 ⇒ 显式放行日志（不放宽断言），
        /// TearDown 里恢复，避免影响其它测试类。
        /// </summary>
        private bool _prevIgnoreFailingMessages;

        [SetUp]
        public void SetUp()
        {
            _prevIgnoreFailingMessages = LogAssert.ignoreFailingMessages;
            LogAssert.ignoreFailingMessages = true;
        }

        [TearDown]
        public void TearDown() => LogAssert.ignoreFailingMessages = _prevIgnoreFailingMessages;

        /// <summary>未设置的格不实心；置/清一个格能往返。</summary>
        [Test]
        public void SetSolid_And_IsSolid_RoundTrip()
        {
            var w = new TileWorld();

            Assert.IsFalse(w.IsSolid(3, 4));
            Assert.AreEqual(0, w.SolidCount);

            w.SetSolid(3, 4);
            Assert.IsTrue(w.IsSolid(3, 4));
            Assert.IsFalse(w.IsSolid(4, 3), "键必须区分 tx / ty 顺序");
            Assert.AreEqual(1, w.SolidCount);

            w.SetSolid(3, 4, false);
            Assert.IsFalse(w.IsSolid(3, 4));
            Assert.AreEqual(0, w.SolidCount);
        }

        /// <summary>
        /// 64 位压缩键是双射：32 位键方案 `(tx &lt;&lt; 16) ^ (ty + 512)` 会把 (1, 65024) 与 (0, -512)
        /// 映射到同一个键（= 误判实心），本实现必须区分开。
        /// </summary>
        [Test]
        public void Keys_DoNotCollide_WhereLegacy32BitKeyDid()
        {
            var w = new TileWorld();

            w.SetSolid(1, 65024);

            Assert.IsTrue(w.IsSolid(1, 65024));
            Assert.IsFalse(w.IsSolid(0, -512), "(0,-512) 与 (1,65024) 在 32 位键方案下会碰撞");
            Assert.IsFalse(w.IsSolid(-1, 65024));
            Assert.AreEqual(1, w.SolidCount);
        }

        /// <summary>可大量置格且互不干扰（负坐标、大坐标一起用）。</summary>
        [Test]
        public void NegativeAndLargeCoordinates_AreDistinct()
        {
            var w = new TileWorld();

            w.SetSolid(-1, -1);
            w.SetSolid(-1, 1);
            w.SetSolid(1, -1);
            w.SetSolid(70000, 70000);

            Assert.IsTrue(w.IsSolid(-1, -1));
            Assert.IsTrue(w.IsSolid(-1, 1));
            Assert.IsTrue(w.IsSolid(1, -1));
            Assert.IsTrue(w.IsSolid(70000, 70000));
            Assert.IsFalse(w.IsSolid(1, 1));
            Assert.AreEqual(4, w.SolidCount);
        }

        /// <summary>世界 → 格必须 FloorToInt：实心格 (-1,-1) 必须能挡住世界坐标 (-0.5,-0.5)。</summary>
        [Test]
        public void IsSolidAt_WorldToTile_UsesFloor()
        {
            var w = new TileWorld();
            w.SetSolid(-1, -1);

            Assert.IsTrue(w.IsSolidAt(-0.5f, -0.5f));
            Assert.IsTrue(w.IsSolidAt(-0.0001f, -0.0001f));
            Assert.IsFalse(w.IsSolidAt(0f, 0f), "x=0 属于第 0 格，不该命中第 -1 格");
            Assert.IsFalse(w.IsSolidAt(0.9999f, -0.0001f));

            // 对照：(int) 强转把 -0.5 截成 0 ⇒ 拿错格（这就是"站在坑里也能踩到地"）
            Assert.AreEqual(0, (int)(-0.5f));
        }

        /// <summary>托台顶高是小数，能读回原值；未登记 / 撤销后返回 false（不抛）。</summary>
        [Test]
        public void CarrierTop_RoundTrip_And_MissReturnsFalse()
        {
            var w = new TileWorld();

            Assert.IsFalse(w.TryGetCarrierTop(2, 3, out var missing));
            Assert.AreEqual(0f, missing);

            w.SetCarrierTop(2, 3, 3.25f);
            Assert.IsTrue(w.TryGetCarrierTop(2, 3, out var top));
            Assert.AreEqual(3.25f, top, 1e-6f);
            Assert.AreEqual(1, w.CarrierCount);

            w.ClearCarrierTop(2, 3);
            Assert.IsFalse(w.TryGetCarrierTop(2, 3, out _));
            Assert.AreEqual(0, w.CarrierCount);
        }

        /// <summary>非有限顶高被忽略（不写进去、不抛）。</summary>
        [Test]
        public void CarrierTop_NonFinite_Ignored()
        {
            var w = new TileWorld();

            w.SetCarrierTop(1, 1, float.NaN);
            w.SetCarrierTop(1, 1, float.PositiveInfinity);

            Assert.IsFalse(w.TryGetCarrierTop(1, 1, out _));
            Assert.AreEqual(0, w.CarrierCount);
        }

        /// <summary>SetBounds 生效；未设置时 HasBounds 为 false（三个值都是 0，不代表世界真的到 0）。</summary>
        [Test]
        public void SetBounds_SetsFacts_And_Flag()
        {
            var w = new TileWorld();
            Assert.IsFalse(w.HasBounds);

            w.SetBounds(-10f, 4000f, 8.5f);

            Assert.IsTrue(w.HasBounds);
            Assert.AreEqual(-10f, w.MinX);
            Assert.AreEqual(4000f, w.MaxX);
            Assert.AreEqual(8.5f, w.GroundTopY);
        }

        /// <summary>左右反了 ⇒ 交换后使用；非有限值 ⇒ 整次忽略（保留原边界）。</summary>
        [Test]
        public void SetBounds_Reversed_Swaps_NonFinite_Ignored()
        {
            var w = new TileWorld();
            w.SetBounds(0f, 100f, 1f);

            w.SetBounds(500f, 200f, 2f);
            Assert.AreEqual(200f, w.MinX);
            Assert.AreEqual(500f, w.MaxX);
            Assert.AreEqual(2f, w.GroundTopY);

            w.SetBounds(float.NaN, 999f, 3f);
            Assert.AreEqual(200f, w.MinX, "非有限值应整次忽略，边界保持上一次的值");
            Assert.AreEqual(500f, w.MaxX);
            Assert.AreEqual(2f, w.GroundTopY);
        }

        /// <summary>Clear 只清格子数据，**不动**世界边界 / 地面顶高。</summary>
        [Test]
        public void Clear_KeepsBounds()
        {
            var w = new TileWorld();
            w.SetBounds(-5f, 50f, 4f);
            w.SetSolid(1, 1);
            w.SetCarrierTop(2, 2, 1.5f);

            w.Clear();

            Assert.AreEqual(0, w.SolidCount);
            Assert.AreEqual(0, w.CarrierCount);
            Assert.IsFalse(w.IsSolid(1, 1));
            Assert.IsFalse(w.TryGetCarrierTop(2, 2, out _));
            Assert.IsTrue(w.HasBounds);
            Assert.AreEqual(-5f, w.MinX);
            Assert.AreEqual(50f, w.MaxX);
            Assert.AreEqual(4f, w.GroundTopY);
        }

        /// <summary>越界格**仍记入**（不静默丢弃），只留一条降频 Warn。</summary>
        [Test]
        public void SetSolid_OutsideBounds_StillRecorded()
        {
            var w = new TileWorld();
            w.SetBounds(0f, 10f, 0f);

            w.SetSolid(99, 3);

            Assert.IsTrue(w.IsSolid(99, 3));
        }

        /// <summary>接口默认实现可经 <see cref="ITileWorld"/> 使用（业务只依赖接口）。</summary>
        [Test]
        public void UsableThroughInterface()
        {
            ITileWorld w = new TileWorld();
            w.SetBounds(0f, 8f, 0f);
            w.SetSolid(1, 0);

            Assert.IsTrue(w.IsSolid(1, 0));
            Assert.IsTrue(w.IsSolidAt(1.5f, 0.5f));
            Assert.AreEqual(8f, w.MaxX);
        }
    }
}
