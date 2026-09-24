using NUnit.Framework;

namespace CloverEngine.Tests
{
    /// <summary>
    /// <see cref="OrderedAsyncResult{T}"/>（乱序异步结果按下标落位）的回归用例：
    /// 交付顺序恒为下标序、越界忽略、重复覆盖、清空后可复用、零任务立即 complete。
    /// <para>纯逻辑，不依赖 Unity 对象，因此是 EditMode 用例；<see cref="LogThrottle.Suppress"/> 打开以静音
    /// 这些用例**故意触发**的非预期分支日志（越界 / 重复 / count=0）。</para>
    /// </summary>
    public class OrderedAsyncResultTests
    {
        [SetUp]
        public void SetUp() => LogThrottle.Suppress = true;

        [TearDown]
        public void TearDown() => LogThrottle.Suppress = false;

        /// <summary>乱序到达 + 齐了才交付：交付顺序必须按下标升序（帧序不能随回调次序抖动）。</summary>
        [Test]
        public void Put_OutOfOrder_TakeOrderedReturnsIndexOrder()
        {
            var pending = new OrderedAsyncResult<string>(3);

            pending.Put(2, "c");
            pending.Put(0, "a");
            Assert.IsFalse(pending.TryTakeOrdered(out var half), "未收齐时必须返回 false");
            Assert.IsNull(half, "未收齐时不许交付半成品");

            pending.Put(1, "b");
            Assert.IsTrue(pending.IsComplete);
            Assert.AreEqual(3, pending.FilledCount);

            Assert.IsTrue(pending.TryTakeOrdered(out var ordered));
            Assert.AreEqual(new[] { "a", "b", "c" }, ordered, "交付顺序必须恒为下标 0..N-1");
        }

        /// <summary>交付即清空：同一实例可复用（第二轮重新 Put 后能再交付一次）。</summary>
        [Test]
        public void TakeOrdered_ClearsForReuse()
        {
            var pending = new OrderedAsyncResult<int>(2);
            pending.Put(0, 1);
            pending.Put(1, 2);
            Assert.IsTrue(pending.TryTakeOrdered(out var first));
            Assert.AreEqual(new[] { 1, 2 }, first);

            Assert.AreEqual(0, pending.FilledCount, "交付后必须清空已落位计数");
            Assert.IsFalse(pending.IsComplete, "交付后应回到未完成态（可复用）");
            Assert.IsFalse(pending.TryTakeOrdered(out var again), "清空后未重新 Put 不许再交付");
            Assert.IsNull(again);

            pending.Put(1, 20);
            pending.Put(0, 10);
            Assert.IsTrue(pending.TryTakeOrdered(out var second));
            Assert.AreEqual(new[] { 10, 20 }, second, "复用后第二轮仍按下标序交付");
        }

        /// <summary>越界下标：忽略本次、不计数、不影响后续交付。</summary>
        [Test]
        public void Put_IndexOutOfRange_IsIgnored()
        {
            var pending = new OrderedAsyncResult<int>(2);

            pending.Put(-1, 99);
            pending.Put(2, 99);
            Assert.AreEqual(0, pending.FilledCount, "越界项不得计入已落位");
            Assert.IsFalse(pending.IsComplete);

            pending.Put(0, 1);
            pending.Put(1, 2);
            Assert.IsTrue(pending.TryTakeOrdered(out var ordered));
            Assert.AreEqual(new[] { 1, 2 }, ordered);
        }

        /// <summary>同一 index 重复 Put：覆盖旧值，且不重复计数。</summary>
        [Test]
        public void Put_DuplicateIndex_OverwritesAndDoesNotDoubleCount()
        {
            var pending = new OrderedAsyncResult<int>(2);
            pending.Put(0, 1);
            pending.Put(0, 111);       // 覆盖
            Assert.AreEqual(1, pending.FilledCount, "重复落位不得重复计数");
            Assert.IsFalse(pending.IsComplete, "另一格没到就还不算齐");

            pending.Put(1, 2);
            Assert.IsTrue(pending.TryTakeOrdered(out var ordered));
            Assert.AreEqual(new[] { 111, 2 }, ordered, "重复 Put 应以后到者为准");
        }

        /// <summary>count &lt;= 0：立即视为 complete，交付空数组 + true（零任务也是"齐了"）。</summary>
        [Test]
        public void CountZero_IsCompleteWithEmptyArray()
        {
            var pending = new OrderedAsyncResult<int>(0);
            Assert.AreEqual(0, pending.Count);
            Assert.IsTrue(pending.IsComplete, "零任务应立即 complete（否则调用方永远等不到交付）");

            Assert.IsTrue(pending.TryTakeOrdered(out var ordered));
            Assert.IsNotNull(ordered);
            Assert.AreEqual(0, ordered.Length);
            Assert.IsTrue(pending.TryTakeOrdered(out var again), "零任务可反复交付（每次空数组 + true）");
            Assert.AreEqual(0, again.Length);

            var negative = new OrderedAsyncResult<int>(-3);
            Assert.AreEqual(0, negative.Count, "负 count 按 0 处理");
            Assert.IsTrue(negative.IsComplete);
        }

        /// <summary>泛型值允许为 null：<c>Put(index, null)</c> 必须算"已落位"（不能靠值非 null 判定）。</summary>
        [Test]
        public void Put_NullValue_CountsAsFilled()
        {
            var pending = new OrderedAsyncResult<string>(2);
            pending.Put(0, null);
            pending.Put(1, "b");
            Assert.IsTrue(pending.IsComplete, "null 是合法结果，必须计入已落位");
            Assert.IsTrue(pending.TryTakeOrdered(out var ordered));
            Assert.IsNull(ordered[0]);
            Assert.AreEqual("b", ordered[1]);
        }
    }
}
