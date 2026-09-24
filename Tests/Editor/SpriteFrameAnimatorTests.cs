using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CloverEngine.Tests
{
    /// <summary>
    /// <see cref="SpriteFrameAnimator"/> 的单元测试（EditMode，无需运行 Play）。
    /// <para>
    /// 判据是**帧下标随 dt 的实际推进序列**（0.1s/帧 ⇒ 每满 0.1s 推一帧）与几条容错契约：
    /// 空帧表、非法 fps、重播换表、Stop、回调只触发一次、回调抛异常不打断 Tick 链。
    /// </para>
    /// <para>
    /// 大多数用例传 <c>target = null</c>（= 离线宿主 / 只推进状态）；另有一条用真
    /// <see cref="SpriteRenderer"/> 验证 sprite 确实被写进去。
    /// </para>
    /// </summary>
    public class SpriteFrameAnimatorTests
    {
        private readonly List<UnityEngine.Object> _created = new List<UnityEngine.Object>();

        /// <summary>
        /// 有两条用例**故意**走降级/异常分支（非法 fps、完成回调抛异常），按引擎规则必须打
        /// Warn/Error，而 Unity Test Runner 默认把 Error 日志判为测试失败 ⇒ 显式放行日志
        /// （不放宽任何断言），TearDown 里恢复，避免影响其它测试类。
        /// </summary>
        private bool _prevIgnoreFailingMessages;

        [SetUp]
        public void SetUp()
        {
            _prevIgnoreFailingMessages = LogAssert.ignoreFailingMessages;
            LogAssert.ignoreFailingMessages = true;
        }

        [TearDown]
        public void TearDown()
        {
            LogAssert.ignoreFailingMessages = _prevIgnoreFailingMessages;

            for (var i = _created.Count - 1; i >= 0; i--)
            {
                if (_created[i] != null) UnityEngine.Object.DestroyImmediate(_created[i]);
            }
            _created.Clear();
        }

        /// <summary>造 <paramref name="count"/> 张 1x1 的 Sprite（同一张纹理，TearDown 统一销毁）。</summary>
        private Sprite[] MakeFrames(int count)
        {
            var tex = new Texture2D(1, 1);
            _created.Add(tex);

            var frames = new Sprite[count];
            for (var i = 0; i < count; i++)
            {
                var s = Sprite.Create(tex, new Rect(0f, 0f, 1f, 1f), new Vector2(0.5f, 0.5f));
                s.name = "frame" + i;
                _created.Add(s);
                frames[i] = s;
            }
            return frames;
        }

        private SpriteRenderer MakeRenderer()
        {
            var go = new GameObject("sprite-frame-animator-test");
            _created.Add(go);
            return go.AddComponent<SpriteRenderer>();
        }

        /// <summary>10 fps（0.1s/帧）下：满一个周期推一帧，不足则原地不动。</summary>
        [Test]
        public void Play_Advance_StepsOneFramePerPeriod()
        {
            var anim = new SpriteFrameAnimator(null);
            anim.Play(MakeFrames(4), 10f);

            Assert.AreEqual(0, anim.FrameIndex, "Play 后应立刻是第 0 帧");
            Assert.IsTrue(anim.IsPlaying);

            anim.Advance(0.06f);
            Assert.AreEqual(0, anim.FrameIndex, "0.06s < 0.1s ⇒ 不推帧");

            anim.Advance(0.06f);
            Assert.AreEqual(1, anim.FrameIndex, "累计 0.12s ⇒ 推一帧，余 0.02s");

            anim.Advance(0.02f);
            Assert.AreEqual(1, anim.FrameIndex, "累计 0.04s ⇒ 仍不推帧");
        }

        /// <summary>循环播放：跑满一圈回到第 0 帧且仍在播。</summary>
        [Test]
        public void Play_Loop_WrapsToZero()
        {
            var anim = new SpriteFrameAnimator(null);
            anim.Play(MakeFrames(4), 10f);

            anim.Advance(0.45f); // 4 个周期

            Assert.AreEqual(0, anim.FrameIndex);
            Assert.IsTrue(anim.IsPlaying);
        }

        /// <summary>PlayOnce：停在最后一帧、IsPlaying 变 false、完成回调只触发一次。</summary>
        [Test]
        public void PlayOnce_HoldsLastFrame_And_FiresCallbackOnce()
        {
            var calls = 0;
            var anim = new SpriteFrameAnimator(null);
            anim.PlayOnce(MakeFrames(3), 10f, () => calls++);

            anim.Advance(0.12f);
            Assert.AreEqual(1, anim.FrameIndex);
            Assert.AreEqual(0, calls, "还没播完不该回调");

            anim.Advance(0.1f);
            Assert.AreEqual(2, anim.FrameIndex, "到末帧后停住");
            Assert.AreEqual(0, calls);

            anim.Advance(0.1f);
            Assert.AreEqual(2, anim.FrameIndex);
            Assert.IsFalse(anim.IsPlaying);
            Assert.AreEqual(1, calls);

            anim.Advance(1f);
            Assert.AreEqual(1, calls, "播完后再 Advance 不得重复回调");
            Assert.AreEqual(2, anim.FrameIndex);
        }

        /// <summary>PlayOnce + 超大 dt：仍只播完一次、回调一次（不因跨多帧而漏回调或重复）。</summary>
        [Test]
        public void PlayOnce_HugeDt_CompletesExactlyOnce()
        {
            var calls = 0;
            var anim = new SpriteFrameAnimator(null);
            anim.PlayOnce(MakeFrames(3), 10f, () => calls++);

            anim.Advance(100f);

            Assert.IsFalse(anim.IsPlaying);
            Assert.AreEqual(2, anim.FrameIndex);
            Assert.AreEqual(1, calls);
        }

        /// <summary>Stop 之后不再推进；未触发的完成回合作废（"停了还回调"是错的）。</summary>
        [Test]
        public void Stop_HaltsAdvance_And_DropsPendingCallback()
        {
            var calls = 0;
            var anim = new SpriteFrameAnimator(null);
            anim.PlayOnce(MakeFrames(3), 10f, () => calls++);

            anim.Advance(0.1f);
            anim.Stop();

            Assert.IsFalse(anim.IsPlaying);

            anim.Advance(5f);

            Assert.AreEqual(1, anim.FrameIndex, "Stop 后不得再推进");
            Assert.AreEqual(0, calls);
        }

        /// <summary>SetFrame：越界收敛到两端并立刻生效；不改播放状态。</summary>
        [Test]
        public void SetFrame_ClampsAndApplies()
        {
            var anim = new SpriteFrameAnimator(null);
            anim.Play(MakeFrames(4), 10f);

            anim.SetFrame(2);
            Assert.AreEqual(2, anim.FrameIndex);
            Assert.IsTrue(anim.IsPlaying, "SetFrame 不改播放状态");

            anim.SetFrame(99);
            Assert.AreEqual(3, anim.FrameIndex);

            anim.SetFrame(-5);
            Assert.AreEqual(0, anim.FrameIndex);

            // 定帧后累计时间归零：紧接着的一次 Advance 不足一个周期时不应把帧推走
            anim.SetFrame(1);
            anim.Advance(0.05f);
            Assert.AreEqual(1, anim.FrameIndex);
        }

        /// <summary>帧表为空（null / 长度 0）⇒ 不抛、不播；Advance / SetFrame 也不抛。</summary>
        [Test]
        public void EmptyFrameTable_NoThrow_NoPlay()
        {
            var anim = new SpriteFrameAnimator(null);

            Assert.DoesNotThrow(() => anim.Play(null, 10f));
            Assert.IsFalse(anim.IsPlaying);
            Assert.AreEqual(0, anim.FrameCount);

            Assert.DoesNotThrow(() => anim.PlayOnce(new Sprite[0], 10f, () => { }));
            Assert.IsFalse(anim.IsPlaying);
            Assert.DoesNotThrow(() => anim.Advance(1f));
            Assert.DoesNotThrow(() => anim.SetFrame(0));
            Assert.AreEqual(0, anim.FrameIndex);
        }

        /// <summary>fps 非法（0 / 负数 / NaN / +Inf）⇒ 按 1 fps 处理并仍可播。</summary>
        [Test]
        public void InvalidFps_FallsBackToOne()
        {
            var bad = new[] { 0f, -5f, float.NaN, float.PositiveInfinity };

            foreach (var fps in bad)
            {
                var anim = new SpriteFrameAnimator(null);
                anim.Play(MakeFrames(2), fps);

                Assert.AreEqual(1f, anim.Fps, $"fps={fps} 应归一化为 1");
                Assert.IsTrue(anim.IsPlaying);

                anim.Advance(0.5f);
                Assert.AreEqual(0, anim.FrameIndex, "1 fps ⇒ 0.5s 还不到一帧");

                anim.Advance(0.6f);
                Assert.AreEqual(1, anim.FrameIndex, "累计 1.1s ⇒ 推一帧");
            }
        }

        /// <summary>dt &lt;= 0 不推进（0 是合法的暂停帧，负值按 0 处理）。</summary>
        [Test]
        public void Advance_NonPositiveDt_DoesNotMove()
        {
            var anim = new SpriteFrameAnimator(null);
            anim.Play(MakeFrames(4), 10f);

            anim.Advance(0f);
            anim.Advance(-1f);

            Assert.AreEqual(0, anim.FrameIndex);
        }

        /// <summary>运行中换帧表（重播）⇒ 下标归零、帧数换成新表的（不会拿旧下标索引新表）。</summary>
        [Test]
        public void Play_SwapsFrameTable_ResetsIndex()
        {
            var anim = new SpriteFrameAnimator(null);
            anim.Play(MakeFrames(4), 10f);
            anim.Advance(0.25f);
            Assert.AreEqual(2, anim.FrameIndex);

            var shorter = MakeFrames(2);
            anim.Play(shorter, 10f);

            Assert.AreEqual(0, anim.FrameIndex);
            Assert.AreEqual(2, anim.FrameCount);
            Assert.AreSame(shorter, anim.Frames);
        }

        /// <summary>完成回调抛异常 ⇒ 接住并留痕，异常不得穿出 Advance（否则会打断调用方的 Tick 链）。</summary>
        [Test]
        public void PlayOnce_CallbackThrows_IsCaught()
        {
            var anim = new SpriteFrameAnimator(null);
            anim.PlayOnce(MakeFrames(2), 10f, () => throw new InvalidOperationException("测试注入的异常"));

            Assert.DoesNotThrow(() => anim.Advance(0.5f));
            Assert.IsFalse(anim.IsPlaying);
        }

        /// <summary>有渲染器时，帧确实被写进 SpriteRenderer.sprite（第 0 帧起，逐帧跟进）。</summary>
        [Test]
        public void WithRenderer_WritesSprite()
        {
            var sr = MakeRenderer();
            var frames = MakeFrames(3);
            var anim = new SpriteFrameAnimator(sr);

            anim.Play(frames, 10f);
            Assert.AreSame(frames[0], sr.sprite, "Play 立刻显示第 0 帧");

            anim.Advance(0.1f);
            Assert.AreSame(frames[1], sr.sprite);

            anim.SetFrame(2);
            Assert.AreSame(frames[2], sr.sprite);

            Assert.AreSame(sr, anim.Target);
        }

        /// <summary>超大 dt（1000s @1fps = 1000 帧）不得把一帧跑成死循环（内部有步数上限）。</summary>
        [Test]
        public void Advance_HugeDt_DoesNotHang()
        {
            var anim = new SpriteFrameAnimator(null);
            anim.Play(MakeFrames(2), 1f);

            Assert.DoesNotThrow(() => anim.Advance(1000f));

            Assert.IsTrue(anim.IsPlaying);
            Assert.GreaterOrEqual(anim.FrameIndex, 0);
            Assert.Less(anim.FrameIndex, 2);
        }
    }
}
