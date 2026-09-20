using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace CloverEngine.Tests
{
    /// <summary>
    /// <see cref="IEventBus"/> 通配订阅（<c>*</c> / <c>**</c>）的回归用例。
    ///
    /// <para>
    /// 守三件事（都是"实现了但容易被后人改坏"的点）：
    /// ① <c>*</c> 只匹配**一段**、<c>**</c> 匹配**一段或多段**；
    /// ② **精确匹配的订阅先执行**（泛订阅不得抢在具体订阅前面改共享状态）；
    /// ③ 退订语义与精确订阅一致 —— 用注册时**同一个订阅名**（含通配符）一次 <c>Off</c> 注销干净。
    /// </para>
    /// <para>
    /// 另有"精确匹配行为不变"的对照用例：没有通配注册时，包含 <c>*</c> 的事件名只是普通字符串。
    /// </para>
    /// </summary>
    public class EventBusWildcardTests
    {
        [Test]
        public void Wildcard_ExactMatchRunsBeforeWildcard()
        {
            var bus = new EventBus();
            var order = new List<string>();
            bus.On("Net.*", () => order.Add("star"));
            bus.On("Net.OnConnected", () => order.Add("exact"));
            bus.On("Net.**", () => order.Add("double-star"));

            bus.Emit("Net.OnConnected");

            CollectionAssert.AreEqual(new[] { "exact", "star", "double-star" }, order,
                "精确匹配必须最先执行；通配之间按注册顺序");
        }

        [Test]
        public void Wildcard_StarMatchesExactlyOneSegment()
        {
            var bus = new EventBus();
            var hits = 0;
            bus.On("Net.*", () => hits++);

            bus.Emit("Net");            // 0 段：不匹配
            bus.Emit("Net.OnConnected"); // 1 段：匹配
            bus.Emit("Net.A.B");         // 2 段：不匹配

            Assert.AreEqual(1, hits);
        }

        [Test]
        public void Wildcard_DoubleStarMatchesOneOrMoreSegments()
        {
            var bus = new EventBus();
            var hits = 0;
            bus.On("Net.**", () => hits++);

            bus.Emit("Net");       // 0 段：不匹配
            bus.Emit("Net.A");     // 1 段：匹配
            bus.Emit("Net.A.B.C"); // 3 段：匹配

            Assert.AreEqual(2, hits);
        }

        [Test]
        public void Wildcard_DoubleStarInMiddle_MatchesArbitraryMiddle()
        {
            var bus = new EventBus();
            var hits = 0;
            bus.On("Net.**.Done", () => hits++);

            bus.Emit("Net.Done");           // ** 至少吃 1 段 → 不匹配
            bus.Emit("Net.A.Done");         // 匹配
            bus.Emit("Net.A.B.C.Done");     // 匹配
            bus.Emit("Net.A.Other");        // 尾段不符 → 不匹配

            Assert.AreEqual(2, hits);
        }

        [Test]
        public void Wildcard_Off_UsesSamePatternName()
        {
            var bus = new EventBus();
            var hits = 0;
            Action handler = () => hits++;
            bus.On("Entity.*", handler);

            bus.Emit("Entity.Created");
            bus.Off("Entity.*", handler);
            bus.Emit("Entity.Created");

            Assert.AreEqual(1, hits, "用同一订阅名 Off 之后不得再触发");
        }

        [Test]
        public void Wildcard_OffAll_RemovesWildcardSubscription()
        {
            var bus = new EventBus();
            var hits = 0;
            bus.On("Foo.*", () => hits++);

            bus.OffAll("Foo.*");
            bus.Emit("Foo.Bar");

            Assert.AreEqual(0, hits);
        }

        [Test]
        public void Wildcard_GenericArgsAndOnce_BothWork()
        {
            var bus = new EventBus();
            var sum = 0;
            bus.On<int>("Net.*", v => sum += v);
            bus.Once<int>("Net.**", v => sum += v * 10);

            bus.Emit("Net.Queue", 2); // star: +2，once: +20（随后自动退订）
            bus.Emit("Net.Queue", 2); // 只剩 star: +2

            Assert.AreEqual(24, sum);
        }

        [Test]
        public void ExactMatch_BehaviourUnchangedWithoutWildcardRegistration()
        {
            var bus = new EventBus();
            var hits = 0;
            bus.On("A.B", () => hits++);

            bus.Emit("A.B");
            bus.Emit("A.*"); // 没有通配注册时，这只是"一个叫 A.* 的事件名"，不得命中 A.B

            Assert.AreEqual(1, hits);
        }
    }
}
