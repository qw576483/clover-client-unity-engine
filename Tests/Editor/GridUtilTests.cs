using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CloverEngine.Tests
{
    /// <summary>
    /// <see cref="GridUtil"/> 矩形 → 整数格遍历的单元测试。
    /// <para>
    /// 核心判据 = **与 4 个调用点的手写实现逐格等价**：测试里内联一份
    /// <see cref="ReferenceOverlap"/>（= super-mario 那四份 `Overlap(Rect)` 的逐字复制），
    /// 逐个矩形比对"格集合 + 顺序"，而不是只断言元素个数。
    /// </para>
    /// </summary>
    public class GridUtilTests
    {
        /// <summary>
        /// 本类有若干用例**故意**走异常分支（非有限坐标 / 超大矩形 / 漏传回调），
        /// 按引擎规则这些分支必须打 Error —— 而 Unity Test Runner 默认把 Error 日志判为测试失败。
        /// 故显式放行日志（只放行日志，不放宽任何断言），TearDown 里恢复，避免影响其它测试类。
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

        /// <summary>
        /// 参考实现：clover-project-super-mario 的 4 个调用点的逐字复制
        /// （PlayerActor.cs:1047 / EnemyModule.cs:293 / ItemModule.cs:356 / FireballModule.cs:248）。
        /// </summary>
        private static List<Vector2Int> ReferenceOverlap(Rect r)
        {
            var x0 = Mathf.FloorToInt(r.xMin);
            var x1 = Mathf.FloorToInt(r.xMax - 0.0001f);
            var y0 = Mathf.FloorToInt(r.yMin);
            var y1 = Mathf.FloorToInt(r.yMax - 0.0001f);
            var list = new List<Vector2Int>();
            for (var y = y0; y <= y1; y++)
                for (var x = x0; x <= x1; x++)
                    list.Add(new Vector2Int(x, y));
            return list;
        }

        private static List<Vector2Int> Collect(Rect r, float epsilon = GridUtil.EdgeEpsilon)
        {
            var list = new List<Vector2Int>();
            GridUtil.ForEach(r, (x, y) => list.Add(new Vector2Int(x, y)), epsilon);
            return list;
        }

        /// <summary>覆盖"整数 / 负数 / 小数 / 零宽高 / 负宽 / 正好落在格边界"这几类矩形，逐格与参考实现比对。</summary>
        [Test]
        public void ForEach_MatchesReferenceImplementation()
        {
            var rects = new[]
            {
                new Rect(0f, 0f, 1f, 1f),                   // 完整一格
                new Rect(-1.5f, -1.5f, 1f, 1f),             // 负数坐标跨 4 格
                new Rect(0.25f, 0.5f, 2.5f, 1.25f),         // 小数起点 / 小数尺寸
                new Rect(-3.2f, 2.7f, 0.9f, 0.4f),          // 窄盒（只覆盖 1 格） + 负坐标
                new Rect(0f, 0f, 0f, 0f),                   // 零宽高 ⇒ 无格
                new Rect(0f, 0f, -2f, 3f),                  // 负宽（反向矩形）⇒ 无格
                new Rect(0f, 0f, 5f, 5f),                   // 右/上边界正好在格线上
                new Rect(-0.5f, -0.5f, 1f, 1f),             // 中心落在格角上
            };

            foreach (var r in rects)
            {
                CollectionAssert.AreEqual(ReferenceOverlap(r), Collect(r), $"矩形 {r} 的格集合/顺序应与手写实现一致");
            }
        }

        /// <summary>负数坐标必须 FloorToInt：矩形 (-1.5,-1.5,1,1) 覆盖 x/y ∈ {-2,-1}（`(int)` 强转会错成 {-1,0}）。</summary>
        [Test]
        public void ForEach_NegativeRect_UsesFloorNotTruncate()
        {
            var tiles = Collect(new Rect(-1.5f, -1.5f, 1f, 1f));

            Assert.AreEqual(4, tiles.Count);
            CollectionAssert.AreEqual(
                new[]
                {
                    new Vector2Int(-2, -2), new Vector2Int(-1, -2),
                    new Vector2Int(-2, -1), new Vector2Int(-1, -1),
                }, tiles);

            // 对照：向零截断（`(int)`）会落在第 -1 格 —— 这就是"站在坑里也能踩到地"的来源
            Assert.AreEqual(-1, (int)(-1.5f));
            Assert.AreEqual(-2, Mathf.FloorToInt(-1.5f));
        }

        /// <summary>右/上边界按开区间收边：Rect(0,0,5,5) 到第 4 格为止（第 5 格只在零宽边上接触）。</summary>
        [Test]
        public void ForEach_ExactBoundaryRect_ExcludesTouchingEdge()
        {
            var tiles = Collect(new Rect(0f, 0f, 5f, 5f));

            Assert.AreEqual(25, tiles.Count);
            Assert.IsFalse(tiles.Contains(new Vector2Int(5, 0)), "第 5 列只在右边界上接触，不该被算进来");
            Assert.IsFalse(tiles.Contains(new Vector2Int(0, 5)), "第 5 行只在上边界上接触，不该被算进来");
            Assert.IsTrue(tiles.Contains(new Vector2Int(4, 4)));
        }

        /// <summary>epsilon 传 0 = 闭区间（含边界格）—— 与默认收边量给出不同格数。</summary>
        [Test]
        public void ForEach_EpsilonZero_IncludesBoundaryTile()
        {
            Assert.AreEqual(36, Collect(new Rect(0f, 0f, 5f, 5f), 0f).Count);
            Assert.AreEqual(25, Collect(new Rect(0f, 0f, 5f, 5f)).Count);
        }

        /// <summary>迭代顺序 = y 升序外层、x 升序内层（调用方在回调里 break 时，顺序决定"先撞到哪一格"）。</summary>
        [Test]
        public void ForEach_IterationOrder_YMajorThenXAscending()
        {
            CollectionAssert.AreEqual(
                new[]
                {
                    new Vector2Int(0, 0), new Vector2Int(1, 0),
                    new Vector2Int(0, 1), new Vector2Int(1, 1),
                }, Collect(new Rect(0f, 0f, 2f, 2f)));
        }

        /// <summary>空矩形 / 反向矩形一次回调都不发（正常情形，不是错误）。</summary>
        [Test]
        public void ForEach_EmptyAndReversedRect_NoCallback()
        {
            Assert.AreEqual(0, Collect(new Rect(0f, 0f, 0f, 0f)).Count);
            Assert.AreEqual(0, Collect(new Rect(0f, 0f, 0f, 3f)).Count);
            Assert.AreEqual(0, Collect(new Rect(0f, 0f, -2f, 3f)).Count);
            Assert.AreEqual(0, Collect(new Rect(0f, 0f, 3f, -1f)).Count);

            // epsilon 比矩形还宽 ⇒ 也被收成空范围（不是错误）
            Assert.AreEqual(0, Collect(new Rect(0f, 0f, 0.00005f, 1f)).Count);
        }

        /// <summary>非有限坐标（NaN / +Inf）⇒ 不回调、不抛（内部会留一条降频 Error）。</summary>
        [Test]
        public void ForEach_NonFiniteRect_NoCallbackNoThrow()
        {
            Assert.AreEqual(0, Collect(new Rect(float.NaN, 0f, 1f, 1f)).Count);
            Assert.AreEqual(0, Collect(new Rect(0f, 0f, float.PositiveInfinity, 1f)).Count);
        }

        /// <summary>action 为 null ⇒ 不抛（留一条一次性的 Error）。</summary>
        [Test]
        public void ForEach_NullAction_DoesNotThrow()
        {
            Assert.DoesNotThrow(() => GridUtil.ForEach(new Rect(0f, 0f, 1f, 1f), null));
        }

        /// <summary>Enumerate 与 ForEach 结果一致（Enumerate 每次调用都会分配，只用于冷路径）。</summary>
        [Test]
        public void Enumerate_MatchesForEach()
        {
            var r = new Rect(-2.3f, 0.7f, 3.6f, 2.2f);

            var viaEnumerate = new List<Vector2Int>();
            foreach (var t in GridUtil.Enumerate(r)) viaEnumerate.Add(t);

            CollectionAssert.AreEqual(Collect(r), viaEnumerate);
        }

        /// <summary>
        /// TryGetTileRange：格范围出参与 ForEach 实际遍历的范围一致；非有限坐标返回 false 并把出参清零。
        /// </summary>
        [Test]
        public void TryGetTileRange_OutParams_And_NonFiniteRejected()
        {
            Assert.IsTrue(GridUtil.TryGetTileRange(new Rect(-1.5f, -1.5f, 1f, 1f),
                out var xMin, out var xMax, out var yMin, out var yMax));
            Assert.AreEqual(-2, xMin);
            Assert.AreEqual(-1, xMax);
            Assert.AreEqual(-2, yMin);
            Assert.AreEqual(-1, yMax);

            Assert.IsFalse(GridUtil.TryGetTileRange(new Rect(float.NaN, 0f, 1f, 1f),
                out xMin, out xMax, out yMin, out yMax));
            Assert.AreEqual(0, xMin);
            Assert.AreEqual(0, xMax);
            Assert.AreEqual(0, yMin);
            Assert.AreEqual(0, yMax);

            Assert.IsFalse(GridUtil.TryGetTileRange(new Rect(0f, 0f, -1f, 1f), out _, out _, out _, out _));
        }

        /// <summary>
        /// 超大矩形（&gt; 1e6 格）**不截断**：仍逐格回调（截断会静默丢格 = 碰撞漏判），只留一条降频 Error。
        /// </summary>
        [Test]
        public void ForEach_HugeRect_TraversesEveryTile()
        {
            var count = 0;
            GridUtil.ForEach(new Rect(0f, 0f, 1200f, 1200f), (x, y) => count++);

            Assert.AreEqual(1200 * 1200, count);
        }
    }
}
