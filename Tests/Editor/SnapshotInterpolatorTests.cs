using System;
using NUnit.Framework;

namespace CloverEngine.Tests
{
    /// <summary>
    /// <see cref="SnapshotInterpolator"/> 的公开面单测（EditMode，注入时钟 ⇒ 完全离线可复现，不依赖真实链路）：
    /// ① 按历史槽读载荷 / 时间戳（0 = 最旧）与越界口径；② <c>Reset(resetWarnOnce)</c> 对「只报一次」记账的作用
    /// （带"默认参数下语义不变"的对照组）；③ 未就绪时窗口载荷为 <c>null</c> 的契约；④ <c>ClockLeadCapped</c>
    /// 在停推 / 恢复两个方向上的取值。
    /// 记账类断言用日志键（<c>_logKey</c> = 类名 + 实例哈希）反查 <c>LogThrottle</c> 的记账状态 ——
    /// 引擎的告警默认走 <c>Game.Logger</c>（<c>private set</c>，测试换不掉），所以只能从记账侧观察。
    /// </summary>
    public class SnapshotInterpolatorTests
    {
        private const string Tag = "SnapshotInterpolator";

        /// <summary>注入时钟的读数（秒），与 <c>Time.realtimeSinceStartup</c> 同语义。</summary>
        private float _realSeconds;

        private SnapshotInterpolator Create(SnapshotInterpolatorOptions? options = null)
        {
            return new SnapshotInterpolator(options, () => _realSeconds);
        }

        /// <summary>该实例「快照时间戳缺失」那条告警在 <c>LogThrottle</c> 里的记账键。</summary>
        private static string NoTimestampKeyOf(SnapshotInterpolator it)
        {
            return Tag + "[" + it.GetHashCode() + "]/push.noTimestamp";
        }

        /// <summary>按 100 ms 间隔推一段快照，载荷用给定标签（字符串），返回最后一个槽号。</summary>
        private static void PushSeries(SnapshotInterpolator it, int count, float firstMs = 1000f)
        {
            for (var i = 0; i < count; i++) it.Push(firstMs + i * 100f, "s" + i);
        }

        [Test]
        public void PayloadAt_ReadsSlotsOldestToNewest()
        {
            var it = Create();
            PushSeries(it, 3);

            Assert.AreEqual(3, it.HistoryLength);
            Assert.AreEqual("s0", it.PayloadAt(0), "槽号 0 必须是最旧那一格");
            Assert.AreEqual("s1", it.PayloadAt(1));
            Assert.AreEqual("s2", it.PayloadAt(2), "最后一个槽号 = 最新那一格");
        }

        [Test]
        public void TimestampAt_MatchesPushedStamps()
        {
            var it = Create();
            PushSeries(it, 3);

            Assert.AreEqual(1000f, it.TimestampAt(0), 0.001f);
            Assert.AreEqual(1100f, it.TimestampAt(1), 0.001f);
            Assert.AreEqual(1200f, it.TimestampAt(2), 0.001f, "最新槽的时间戳应等于最后一帧的 server_ms");
            Assert.AreEqual(it.NewestSnapshotMs, it.TimestampAt(it.HistoryLength - 1), 0.001f);
        }

        [Test]
        public void HistoryStartIndex_TracksFilledSlots()
        {
            var it = Create();
            Assert.AreEqual(it.Options.HistSlots, it.HistoryStartIndex, "一格都没有时没有有效下标");

            PushSeries(it, 3);
            Assert.AreEqual(it.Options.HistSlots - 3, it.HistoryStartIndex, "未填满时最旧槽在下标 HistSlots-HistoryLength");

            // 槽号 k ↔ 下标 HistoryStartIndex + k：填满后起点回到 0。
            PushSeries(it, it.Options.HistSlots, 2000f);
            Assert.AreEqual(it.Options.HistSlots, it.HistoryLength);
            Assert.AreEqual(0, it.HistoryStartIndex, "填满后最旧槽回到下标 0");
        }

        [Test]
        public void PayloadAt_RollsOver_OldestDropsOut()
        {
            var it = Create();
            PushSeries(it, it.Options.HistSlots + 1); // 多推一格 ⇒ 最旧的 s0 被挤出历史

            Assert.AreEqual(it.Options.HistSlots, it.HistoryLength);
            Assert.AreEqual("s1", it.PayloadAt(0), "最旧那一格必须是 s1（s0 已被挤出）");
            Assert.AreEqual("s" + it.Options.HistSlots, it.PayloadAt(it.HistoryLength - 1));
        }

        [Test]
        public void PayloadAt_OutOfRange_ReturnsNullWithoutThrowing()
        {
            var it = Create();

            Assert.IsNull(it.PayloadAt(0), "一格都没有时任何槽号都读不到");
            Assert.IsNull(it.PayloadAt(-1), "负数槽号不得夹到最旧那一格");
            Assert.IsNull(it.PayloadAt(999), "越界槽号不得夹到最新那一格");
            Assert.IsTrue(float.IsNaN(it.TimestampAt(0)), "越界时间戳给 NaN（0 是合法时间戳，不能当哨兵）");
            Assert.DoesNotThrow(() => it.PayloadAt(int.MaxValue));
            Assert.DoesNotThrow(() => it.TimestampAt(int.MinValue));

            PushSeries(it, 1);
            Assert.AreEqual("s0", it.PayloadAt(0));
            Assert.IsNull(it.PayloadAt(1), "只有 1 格时槽号 1 越界");
        }

        [Test]
        public void WindowPayloads_NullBeforeFirstPush()
        {
            var it = Create();

            Assert.IsFalse(it.HasWindow);
            Assert.IsNull(it.WindowStartPayload, "未就绪时窗口载荷为 null（调用方必须先判 HasWindow）");
            Assert.IsNull(it.WindowEndPayload);
            Assert.IsFalse(it.ClockLeadCapped);
        }

        [Test]
        public void Reset_KeepsWarnOnceByDefault()
        {
            var it = Create();
            it.Push(-1f); // server_ms <= 0 ⇒ 报一次「时间戳缺失」
            var key = NoTimestampKeyOf(it);
            Assert.IsFalse(LogThrottle.WarnOnce(Tag, key, "probe"), "该实例的记账应已存在（告警已出过）");

            it.Reset();

            Assert.IsFalse(LogThrottle.WarnOnce(Tag, key, "probe"),
                "不带参数的 Reset() 不得动「只报一次」记账（现行为不变）");
            Assert.AreEqual(0, it.HistoryLength);
        }

        [Test]
        public void Reset_WithResetWarnOnce_EmitsAgain()
        {
            var it = Create();
            it.Push(-1f);
            var key = NoTimestampKeyOf(it);
            Assert.IsFalse(LogThrottle.WarnOnce(Tag, key, "probe"), "该实例的记账应已存在（告警已出过）");

            it.Reset(resetWarnOnce: true);

            Assert.IsTrue(LogThrottle.WarnOnce(Tag, key, "probe"),
                "resetWarnOnce:true 后本实例的记账应被清掉 ⇒ 下一局同类分支仍能留痕");
        }

        [Test]
        public void Forget_ClearsOnlyMatchingPrefix()
        {
            var scope = "tests.forget." + Guid.NewGuid().ToString("N");
            var inside = scope + "/a";
            var outside = "tests.forgetother." + Guid.NewGuid().ToString("N");

            Assert.IsTrue(LogThrottle.WarnOnce("T", inside, "m"));
            Assert.IsTrue(LogThrottle.WarnOnce("T", outside, "m"));

            LogThrottle.Forget(scope);

            Assert.IsTrue(LogThrottle.WarnOnce("T", inside, "m"), "前缀内的记账应被清掉");
            Assert.IsFalse(LogThrottle.WarnOnce("T", outside, "m"), "前缀外的记账不得受影响（⛔ 不等于 Reset()）");
        }

        [Test]
        public void Forget_EmptyOrNullPrefix_DoesNothing()
        {
            var key = "tests.forget.empty." + Guid.NewGuid().ToString("N");
            Assert.IsTrue(LogThrottle.WarnOnce("T", key, "m"));

            LogThrottle.Forget("");
            LogThrottle.Forget(null);

            Assert.IsFalse(LogThrottle.WarnOnce("T", key, "m"), "空前缀不得被当成清空全部（要清全部用 Reset()）");
        }

        [Test]
        public void ClockLeadCapped_TracksStallAndRecovery()
        {
            _realSeconds = 0f;
            var it = Create();
            it.Push(1000f);
            it.Push(1100f);

            _realSeconds = 0.1f;
            it.Tick();
            Assert.IsFalse(it.ClockLeadCapped, "正常推进时不该触发超前上限");

            // 快照停推：真实时间照走，时钟被夹在「最新快照 + ClockMaxLeadMs」。
            _realSeconds = 5f;
            it.Tick();
            Assert.IsTrue(it.ClockLeadCapped, "停推 5s 后时钟应收在超前上限上");
            Assert.AreEqual(it.NewestSnapshotMs + it.Options.ClockMaxLeadMs, it.RenderClockMs, 1f);

            // 快照恢复：上限挂在最新快照上 ⇒ 立刻松开（不靠速率追）。
            it.Push(10000f);
            it.Tick();
            Assert.IsFalse(it.ClockLeadCapped, "快照一恢复，超前上限应立即松开");
        }
    }
}
