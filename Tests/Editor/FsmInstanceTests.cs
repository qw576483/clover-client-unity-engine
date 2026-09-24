using NUnit.Framework;
using UnityEngine.TestTools;

namespace CloverEngine.Tests
{
    /// <summary>
    /// <see cref="Game.NewFsm"/> / <see cref="Fsm"/> 可实例化（P0 缺口①）的编辑期单元测试。
    /// <para>
    /// <b>判据</b>：多个 <see cref="Game.NewFsm"/> 实例之间、以及它们与全局单例 <see cref="Game.Fsm"/>
    /// 之间，**状态表 / 触发器表 / 变更监听器 / Current / Tick 全部互不影响** ——
    /// 不能只断言"两个对象引用不同"（那只能证明工厂没返回同一个对象，证明不了状态隔离）。
    /// </para>
    /// <para>
    /// ⛔ 本文件**不启动引擎**（不调 <see cref="Game.Launch"/>）：启动会建落盘日志/设置目录、
    /// 跑启动钩子，给编辑器进程留副作用。全局单例那一侧改为断言"**槽位未被本方法触碰**"
    /// （<see cref="Game.Fsm"/> 是 private set，除 Launch/Shutdown 外无人能改）——
    /// 若 <see cref="Game.NewFsm"/> 误把新实例写回全局，或复用了全局实例，这条会红。
    /// </para>
    /// </summary>
    public class FsmInstanceTests
    {
        /// <summary>
        /// 部分用例**故意**走告警分支（未注册触发器 / 未注册状态），按引擎规则这些必须打日志，
        /// 而 Unity Test Runner 会把日志当失败 ⇒ 显式放行日志（只放行日志，不放宽任何断言）。
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

        /// <summary>工厂必须给**新实例**：每次调用都不一样，且都不是全局单例、也不改全局单例槽位。</summary>
        [Test]
        public void NewFsm_ReturnsFreshInstance_AndLeavesGlobalSlotUntouched()
        {
            var globalBefore = Game.Fsm;

            var a = Game.NewFsm();
            var b = Game.NewFsm();
            var c = Game.NewFsm();

            Assert.IsNotNull(a, "NewFsm 不应返回 null");
            Assert.IsNotNull(b, "NewFsm 不应返回 null");
            Assert.IsNotNull(c, "NewFsm 不应返回 null");

            Assert.AreNotSame(a, b, "两次 NewFsm 必须是两个独立实例");
            Assert.AreNotSame(b, c, "两次 NewFsm 必须是两个独立实例");
            Assert.AreNotSame(a, c, "两次 NewFsm 必须是两个独立实例");

            Assert.IsFalse(ReferenceEquals(a, globalBefore), "NewFsm 不许把全局单例直接返回");
            Assert.IsFalse(ReferenceEquals(b, globalBefore), "NewFsm 不许把全局单例直接返回");
            Assert.IsFalse(ReferenceEquals(c, globalBefore), "NewFsm 不许把全局单例直接返回");

            Assert.AreSame(globalBefore, Game.Fsm,
                "NewFsm 不许改动全局单例槽位（Game.Fsm 只有 Launch/Shutdown 能改）");

            // 新实例是白的：没有状态 ⇒ Tick 不该抛、也不该有任何 Current。
            Assert.IsNull(a.Current, "新实例初始应无状态（与引擎自身用法一致：先 RegisterState 再 Transition）");
            Assert.DoesNotThrow(() => a.Tick(0.016f), "未注册任何状态时 Tick 不许抛");
        }

        /// <summary>
        /// 状态表隔离：两个实例注册**同名不同语义**的状态、各自转换，Current 与回调计数互不串台。
        /// </summary>
        [Test]
        public void NewFsm_InstancesHaveIsolatedStatesAndCallbacks()
        {
            var bot1 = Game.NewFsm();
            var bot2 = Game.NewFsm();

            var bot1Enter = 0;
            var bot2Enter = 0;
            var bot1Tick = 0;
            var bot2Tick = 0;

            bot1.RegisterState("Idle");
            bot1.RegisterState("Engage",
                onEnter: () => bot1Enter++,
                onTick: _ => bot1Tick++);

            bot2.RegisterState("Idle");
            bot2.RegisterState("Engage",
                onEnter: () => bot2Enter++,
                onTick: _ => bot2Tick++);

            bot1.Transition("Engage");
            Assert.AreEqual("Engage", bot1.Current, "bot1 应进入 Engage");
            Assert.IsNull(bot2.Current, "bot2 未转换过 ⇒ Current 必须仍是 null（状态表不许共享）");
            Assert.AreEqual(1, bot1Enter, "bot1 的 OnEnter 应只触发一次");
            Assert.AreEqual(0, bot2Enter, "bot2 的 OnEnter 绝不能被 bot1 的转换触发");

            bot2.Transition("Engage");
            Assert.AreEqual("Engage", bot2.Current, "bot2 应进入 Engage");
            Assert.AreEqual(1, bot1Enter, "bot2 的转换不许影响 bot1 的回调计数");
            Assert.AreEqual(1, bot2Enter, "bot2 的 OnEnter 应只触发一次");

            // Tick 隔离：只 Tick bot1，bot1 的 OnTick 走一次、bot2 的零次。
            bot1.Tick(0.5f);
            Assert.AreEqual(1, bot1Tick, "bot1.Tick 应推进 bot1 的当前状态");
            Assert.AreEqual(0, bot2Tick, "bot1.Tick 不许推进 bot2");

            bot2.Tick(0.5f);
            bot2.Tick(0.5f);
            Assert.AreEqual(1, bot1Tick, "bot2.Tick 不许推进 bot1");
            Assert.AreEqual(2, bot2Tick, "bot2.Tick 应推进 bot2 自己的当前状态");
        }

        /// <summary>触发器表隔离：一边注册的触发器在另一边不存在（不是"共用一张表"）。</summary>
        [Test]
        public void NewFsm_InstancesHaveIsolatedTransitionTables()
        {
            var a = Game.NewFsm();
            var b = Game.NewFsm();

            foreach (var fsm in new[] { a, b })
            {
                fsm.RegisterState("Idle");
                fsm.RegisterState("Combat");
            }

            a.AddTransition("contact", "Combat"); // 只注册在 a 上

            a.Trigger("contact");
            Assert.AreEqual("Combat", a.Current, "a 自己注册的触发器应生效");

            b.Trigger("contact"); // b 没有这个触发器 ⇒ 告警并忽略（不切换）
            Assert.IsNull(b.Current, "b 未注册该触发器 ⇒ 不许被 a 的注册影响");
        }

        /// <summary>变更监听器隔离：OnChange 只挂在自己那份订阅表上；OffChange 也不影响别人。</summary>
        [Test]
        public void NewFsm_InstancesHaveIsolatedChangeHandlers()
        {
            var a = Game.NewFsm();
            var b = Game.NewFsm();

            var aChanges = 0;
            var bChanges = 0;

            a.RegisterState("Idle");
            a.RegisterState("Combat");
            b.RegisterState("Idle");
            b.RegisterState("Combat");

            void OnA(string from, string to) => aChanges++;
            void OnB(string from, string to) => bChanges++;

            a.OnChange(OnA);
            b.OnChange(OnB);

            a.Transition("Combat");
            Assert.AreEqual(1, aChanges, "a 的监听器应收到 a 的切换");
            Assert.AreEqual(0, bChanges, "b 的监听器不许收到 a 的切换");

            // a 注销自己的监听器，b 的订阅不受影响（两张订阅表）。
            a.OffChange(OnA);
            b.Transition("Combat");
            b.Transition("Idle");
            Assert.AreEqual(1, aChanges, "a 已注销 ⇒ 不再收到任何通知");
            Assert.AreEqual(2, bChanges, "b 的订阅只受 b 自己影响（切换两次 = 两次通知）");

            Assert.AreEqual("Idle", b.Current);
            Assert.AreEqual("Combat", a.Current, "b 的转换不许改动 a 的 Current");
            Assert.AreNotSame(Game.Fsm, a);
        }

        /// <summary>
        /// 与全局单例的关系：本类**不启动引擎**，所以可观测的判据只有一条 ——
        /// <see cref="Game.Fsm"/> 槽位在整套操作前后**引用不变**；
        /// 另外顺带断言：全局单例（若已被别的用例启动过）与独立实例不是同一个对象。
        /// </summary>
        [Test]
        public void NewFsm_DoesNotTouchGlobalFsm()
        {
            var globalBefore = Game.Fsm;

            var fsm = Game.NewFsm();
            fsm.RegisterState("A");
            fsm.RegisterState("B");
            fsm.Transition("B");
            fsm.Tick(1f);
            fsm.Force("A");

            Assert.AreSame(globalBefore, Game.Fsm, "NewFsm + 独立实例上的操作不许改动全局单例槽位");

            if (Game.Fsm != null)
            {
                Assert.AreNotSame(Game.Fsm, fsm, "独立实例不许是全局单例本身");
            }
        }

        /// <summary>
        /// <see cref="IFsm.Reset"/>（业务每回合 / 对象池复用要的那块）：回到"未初始化"、
        /// **保留订阅表**、可重复调用、不触发任何状态回调、**不销毁实例**、不波及其它实例。
        /// </summary>
        [Test]
        public void Reset_ReturnsToUninitialized_KeepsSubscriptions_AndIsIdempotent()
        {
            var fsm = Game.NewFsm();
            var other = Game.NewFsm();

            var enters = 0;
            var exits = 0;
            var ticks = 0;
            var changes = 0;

            fsm.RegisterState("Idle", onEnter: () => enters++, onTick: _ => ticks++, onExit: () => exits++);
            fsm.RegisterState("Combat");
            other.RegisterState("Idle");
            other.RegisterState("Combat");

            fsm.OnChange((from, to) => changes++);

            fsm.Force("Idle");
            fsm.Tick(0.5f);
            fsm.Transition("Combat");
            Assert.AreEqual("Combat", fsm.Current, "前置：应已进入 Combat");

            var entersBefore = enters;
            var exitsBefore = exits;
            var changesBefore = changes;

            fsm.Reset();

            Assert.IsNull(fsm.Current, "Reset 后 Current 必须为空（回到未初始化）");
            Assert.AreEqual(entersBefore, enters, "Reset 不是一次状态转换 ⇒ 不许补跑 OnEnter");
            Assert.AreEqual(exitsBefore, exits, "Reset 不许触发 OnExit（⛔ 不要指望它通知「离开」）");

            fsm.Tick(1f);
            Assert.AreEqual(1, ticks, "Reset 后 Tick 空转：旧状态的 OnTick 不许再被调用");

            fsm.Transition("Combat");
            Assert.IsNull(fsm.Current, "旧状态已被清出状态表 ⇒ 转换走「状态未注册」分支被忽略");

            // 订阅表**刻意保留**：重新注册状态后再转换，Reset 之前挂的监听器仍应收到通知
            // （静默解绑会让"重置一次以后 OnChange 就再也不触发"变成无从定位的现象）。
            fsm.RegisterState("Idle");
            fsm.RegisterState("Combat");
            fsm.Transition("Combat");
            Assert.AreEqual("Combat", fsm.Current, "重新注册后转换应生效");
            Assert.AreEqual(changesBefore + 1, changes,
                "Reset 保留订阅表：Reset 之前挂的旧监听器仍应收到通知（要解绑请显式 OffChange）");

            // 幂等 + 不销毁实例（本行之下仍在用同一个引用）+ 不波及其它实例
            var otherBefore = other.Current;
            Assert.DoesNotThrow(() => fsm.Reset(), "Reset 可重复调用（第二次起是空操作）");
            Assert.IsNull(fsm.Current, "重复 Reset 后仍是未初始化态");
            Assert.AreEqual(otherBefore, other.Current, "Reset 只作用于自己那份实例");
        }
    }
}
