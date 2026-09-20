using System;
using NUnit.Framework;
using UnityEngine;

namespace CloverEngine.Tests
{
    /// <summary>
    /// <see cref="IInputManager"/> 的回归用例（EditMode）。
    ///
    /// <para>
    /// 守三件事（都是"实现了但容易被后人改坏"的点）：
    /// ① <b>后端选择语义</b>：<see cref="IInputManager.Available"/> 与
    ///    <see cref="IInputManager.BackendName"/> 一致（有后端才 Available，后端名只能是
    ///    Legacy / InputSystem / None）；
    /// ② <b>锁语义</b>：<see cref="IInputManager.Lock"/> 期间一切读取返回默认值（含 MousePosition / HasTouch），
    ///    <see cref="IInputManager.Unlock"/> 后恢复；
    /// ③ <b>State 是副本</b>：业务改 <c>Game.Input.State.*</c> 不得污染引擎内部输入快照（且不每帧分配）。
    /// </para>
    /// <para>
    /// 另有"无后端不抛异常"的用例：<see cref="NullInputBackend"/> 逐读断言，
    /// 加一条 <see cref="InputManager"/> 级兜底（本机通常有键鼠 ⇒ 走不到该分支，此时按 Pass 记）。
    /// </para>
    /// <para>
    /// <b>刻意不测</b>：真实按键 / 触摸的读取结果 —— 那些依赖真机与编辑器焦点，
    /// 断言它们会因为"跑测试的机器没有键鼠"而 flaky。这里只测与设备无关的行为。
    /// </para>
    /// </summary>
    public class InputManagerTests
    {
        /// <summary>后端名只允许这三个（"None" = 降级到空后端）。</summary>
        private static readonly string[] KnownBackendNames = { "Legacy", "InputSystem", "None" };

        // ───────────────────────── ① 后端选择语义 ─────────────────────────

        [Test]
        public void BackendSelection_BackendNameIsAlwaysOneOfKnownValues()
        {
            var mgr = new InputManager();

            CollectionAssert.Contains(KnownBackendNames, mgr.BackendName,
                "后端名只能是 Legacy / InputSystem / None（降级到空后端）");
        }

        [Test]
        public void BackendSelection_AvailableAgreesWithBackendName()
        {
            var mgr = new InputManager();

            Assert.AreEqual(mgr.BackendName != "None", mgr.Available,
                "Available 与后端名必须一致：选了空后端就意味着「键鼠完全不可读」");
        }

        [Test]
        public void BackendSelection_ReadsNeverThrowForAnyGameKey()
        {
            var mgr = new InputManager();

            // 与设备无关：不管有没有后端、按键是否按下，读取都不得抛（抛出去会打断整帧 Update）。
            foreach (GameKey key in Enum.GetValues(typeof(GameKey)))
            {
                var k = key;
                Assert.DoesNotThrow(() => mgr.GetKey(k), $"GetKey({k}) 不得抛异常");
                Assert.DoesNotThrow(() => mgr.GetKeyDown(k), $"GetKeyDown({k}) 不得抛异常");
                Assert.DoesNotThrow(() => mgr.GetKeyUp(k), $"GetKeyUp({k}) 不得抛异常");
            }

            Assert.DoesNotThrow(() => mgr.GetAxis("Horizontal"));
            Assert.DoesNotThrow(() => mgr.GetAxis("Vertical", raw: true));
            // 轴名不存在时返回 0 而不是抛异常（对齐 IInputManager.GetAxis 的契约）
            Assert.AreEqual(0f, mgr.GetAxis("__不存在的轴__"));
        }

        // ───────────────────────── ② 锁定语义 ─────────────────────────

        [Test]
        public void Locked_AllReadsReturnDefaults()
        {
            var mgr = new InputManager();
            mgr.Lock();
            mgr.Tick(); // 锁定期间照常被 Tick 驱动：不得把后端值写回快照

            Assert.IsTrue(mgr.IsLocked);

            var state = mgr.State;
            Assert.AreEqual(Vector2.zero, state.MoveDirection);
            Assert.IsFalse(state.Skill1Down);
            Assert.IsFalse(state.Skill2Down);
            Assert.IsFalse(state.Skill3Down);
            Assert.IsFalse(state.Skill4Down);
            Assert.IsFalse(state.JumpDown);
            Assert.IsFalse(state.DodgeDown);
            Assert.IsFalse(state.InteractDown);
            Assert.IsFalse(state.TouchDown);

            Assert.IsFalse(mgr.GetKey(GameKey.Space));
            Assert.IsFalse(mgr.GetKeyDown(GameKey.Space));
            Assert.IsFalse(mgr.GetKeyUp(GameKey.Space));
            Assert.IsFalse(mgr.GetMouseButton(0));
            Assert.IsFalse(mgr.GetMouseButtonDown(0));
            Assert.IsFalse(mgr.GetMouseButtonUp(0));
            Assert.AreEqual(Vector3.zero, mgr.MousePosition);
            Assert.AreEqual(Vector2.zero, mgr.MouseDelta);
            Assert.AreEqual(0f, mgr.GetAxis("Horizontal"));
            Assert.AreEqual(0f, mgr.GetAxis("Horizontal", raw: true));
            Assert.IsFalse(mgr.HasTouch);
        }

        [Test]
        public void Unlock_RestoresReadableState()
        {
            var mgr = new InputManager();
            mgr.Lock();
            mgr.Unlock();

            Assert.IsFalse(mgr.IsLocked, "Unlock 之后必须解除锁定");
            Assert.DoesNotThrow(() => mgr.Tick(), "解锁后 Tick 不得抛异常");

            InputState snapshot = null;
            Assert.DoesNotThrow(() => snapshot = mgr.State, "解锁后读取 State 不得抛异常");
            Assert.IsNotNull(snapshot);
        }

        // ─────────────────── ③ State 是副本（不污染引擎） ───────────────────

        [Test]
        public void State_MutationDoesNotAffectEngineSnapshot()
        {
            var mgr = new InputManager();

            var snapshot = mgr.State;
            snapshot.MoveDirection = new Vector2(9f, 9f);
            snapshot.JumpDown = true;
            snapshot.Skill1Down = true;
            snapshot.TouchDown = true;

            var again = mgr.State;
            Assert.AreEqual(Vector2.zero, again.MoveDirection, "业务改 State 不得改动引擎内部输入快照");
            Assert.IsFalse(again.JumpDown, "业务改 State 不得改动引擎内部输入快照");
            Assert.IsFalse(again.Skill1Down, "业务改 State 不得改动引擎内部输入快照");
            Assert.IsFalse(again.TouchDown, "业务改 State 不得改动引擎内部输入快照");
        }

        [Test]
        public void State_ReusesInternalBuffer_NoPerFrameAllocation()
        {
            var mgr = new InputManager();

            Assert.AreSame(mgr.State, mgr.State,
                "State 必须把内部快照拷进复用的缓冲再返回（每次读取 new 一个会逐帧产生 GC 垃圾）");
        }

        // ─────────────────── 无后端：降级到空后端且不抛 ───────────────────

        [Test]
        public void NullBackend_IsUnavailableAndNamedNone()
        {
            var backend = new NullInputBackend("unit-test：两个后端都探测失败");

            Assert.IsFalse(backend.Available);
            Assert.AreEqual("None", backend.Name);
            Assert.IsFalse(string.IsNullOrEmpty(backend.UnavailableReason), "降级必须给出可操作的原因");
        }

        [Test]
        public void NullBackend_EveryReadReturnsDefaultWithoutThrowing()
        {
            var backend = new NullInputBackend("unit-test：两个后端都探测失败");

            foreach (GameKey key in Enum.GetValues(typeof(GameKey)))
            {
                var k = key;
                Assert.IsFalse(backend.GetKeyDown(k), $"空后端对 {k} 的 GetKeyDown 必须为 false");
                Assert.DoesNotThrow(() => backend.GetKey(k));
                Assert.DoesNotThrow(() => backend.GetKeyUp(k));
            }

            Assert.IsFalse(backend.GetMouseButton(0));
            Assert.IsFalse(backend.GetMouseButtonDown(1));
            Assert.IsFalse(backend.GetMouseButtonUp(2));
            Assert.AreEqual(Vector3.zero, backend.MousePosition);
            Assert.AreEqual(Vector2.zero, backend.MouseDelta);
            Assert.AreEqual(0f, backend.GetAxis("Horizontal", false));
            Assert.AreEqual(0, backend.TouchCount);

            var state = new InputState();
            Assert.DoesNotThrow(() => backend.SampleActions(state), "空后端的采样不得抛异常");
            Assert.IsFalse(state.JumpDown);
        }

        [Test]
        public void NoBackend_InputManagerFallsBackToNoneWithoutThrowing()
        {
            var mgr = new InputManager();
            if (mgr.Available)
            {
                // 本机（编辑器）通常有键鼠 ⇒ InputManager 会选到可用后端，走不到空后端分支；
                // 空后端本身的行为已由 NullBackend_* 两条用例直接覆盖。
                Assert.Pass($"本机后端可用（{mgr.BackendName}），跳过 InputManager 的降级分支");
            }

            Assert.AreEqual("None", mgr.BackendName);

            mgr.Tick();
            Assert.IsFalse(mgr.GetKey(GameKey.Space));
            Assert.IsFalse(mgr.GetKeyDown(GameKey.Space));
            Assert.AreEqual(0f, mgr.GetAxis("Horizontal"));
            Assert.AreEqual(Vector3.zero, mgr.MousePosition);
            Assert.IsFalse(mgr.HasTouch);
            Assert.AreEqual(Vector2.zero, mgr.State.MoveDirection);
        }
    }
}
