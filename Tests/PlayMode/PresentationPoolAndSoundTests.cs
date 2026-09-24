using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CloverEngine.Tests
{
    /// <summary>
    /// 表现域本次新增能力的 PlayMode 回归用例（**未实跑声明**：本轮禁止启动 Unity，本文件只做静态审查）。
    /// <para>
    /// 覆盖：① <c>IObjectPool.Register</c> 的代码工厂路径（工厂优先 / 复用时不重复调用 / 工厂返回 null
    /// 明确失败 / 重复注册覆盖 / 传 null 注销并回落 Resources）；② <c>ISoundManager.IsMuted</c> 与
    /// <c>SetMute</c> 同源。
    /// </para>
    /// <para>
    /// 为什么放 PlayMode 而不是 EditMode：这两条都会创建真实 <c>GameObject</c>，
    /// 且 <c>ObjectPool.Despawn</c> 会经 <c>DontDestroyOnLoad</c> 建常驻池根节点 ——
    /// <c>DontDestroyOnLoad</c> 的语义只在播放态成立。
    /// <c>FloatText</c> 的升距/淡出（需真实 Canvas + 相机投影）与 <c>CameraManager.Main</c>
    /// （依赖场景里有没有 MainCamera 标签）属人工验证范围，见交付回报。
    /// </para>
    /// </summary>
    public class PresentationPoolAndSoundTests
    {
        private ObjectPool _pool;

        [SetUp]
        public void SetUp()
        {
            _pool = new ObjectPool();
        }

        [TearDown]
        public void TearDown()
        {
            // ClearAll 会销毁池内全部对象与常驻根节点（[ObjectPool]），不留跨用例残留。
            _pool?.ClearAll();
            _pool = null;
        }

        /// <summary>注册工厂后 Spawn 必须走工厂（不再要求 Resources 里有对应预制体），并按 key 命名。</summary>
        [Test]
        public void Pool_Spawn_WithRegisteredFactory_UsesFactory()
        {
            var calls = 0;
            _pool.Register("Effects/Bullet", () =>
            {
                calls++;
                return new GameObject("fresh");
            });

            var go = _pool.Spawn("Effects/Bullet");

            Assert.IsNotNull(go, "注册了工厂的 key 必须能 Spawn 出来（代码造的对象也能入池）");
            Assert.AreEqual(1, calls, "工厂应被调用一次");
            Assert.AreEqual("Effects/Bullet", go.name, "与预制体路径同口径：池内对象按 key 命名");
            Assert.AreEqual(1, _pool.GetActiveCount("Effects/Bullet"), "取出的实例应登记为活跃");
        }

        /// <summary>复用路径不重新调工厂：归还后再取到的是同一个实例。</summary>
        [Test]
        public void Pool_SpawnAfterDespawn_ReusesInstanceAndSkipsFactory()
        {
            var calls = 0;
            _pool.Register("Effects/Bullet", () =>
            {
                calls++;
                return new GameObject("fresh");
            });
            var first = _pool.Spawn("Effects/Bullet");

            _pool.Despawn(first);
            var second = _pool.Spawn("Effects/Bullet");

            Assert.AreSame(first, second, "池里有空闲实例时必须复用，而不是再调一次工厂");
            Assert.AreEqual(1, calls, "工厂只在池空时被调用（复用路径逐字不变）");
        }

        /// <summary>工厂返回 null：本次 Spawn 失败并记 Error —— ⛔ 不许静默产出空壳对象。</summary>
        [Test]
        public void Pool_FactoryReturnsNull_SpawnFailsWithError()
        {
            _pool.Register("Effects/Bullet", () => null);

            LogAssert.Expect(LogType.Error, new Regex("代码工厂返回 null"));
            var go = _pool.Spawn("Effects/Bullet");

            Assert.IsNull(go, "工厂造不出时必须返回 null，不能给一个空壳对象把失败掩盖掉");
            Assert.AreEqual(0, _pool.GetActiveCount("Effects/Bullet"), "失败的实例不许登记进池");
        }

        /// <summary>同一 key 重复注册 = 覆盖（后注册的生效）。</summary>
        [Test]
        public void Pool_RegisterTwice_LastFactoryWins()
        {
            var used = "";
            _pool.Register("Effects/Bullet", () =>
            {
                used = "first";
                return new GameObject();
            });
            _pool.Register("Effects/Bullet", () =>
            {
                used = "second";
                return new GameObject();
            });

            _pool.Spawn("Effects/Bullet");

            Assert.AreEqual("second", used, "同一 key 重复注册应覆盖为新的工厂");
        }

        /// <summary>传 null 工厂 = 注销：之后该 key 回落 Resources 路径（本用例没有该预制体 ⇒ Error + null）。</summary>
        [Test]
        public void Pool_RegisterNull_UnregistersAndFallsBackToResources()
        {
            _pool.Register("Effects/Bullet", () => new GameObject());
            _pool.Register("Effects/Bullet", null);

            LogAssert.Expect(LogType.Error, new Regex("Prefab not found: Effects/Bullet"));
            var go = _pool.Spawn("Effects/Bullet");

            Assert.IsNull(go, "注销后应回落 Resources 路径（没有该预制体 ⇒ null），而不是继续用工厂");
        }

        /// <summary><c>IsMuted</c> 与 <c>SetMute</c> 同源：设置后立刻能读回，分组之间互不影响。</summary>
        [Test]
        public void Sound_IsMuted_ReadsSameStateAsSetMute()
        {
            var sound = new SoundManager();
            try
            {
                Assert.IsFalse(sound.IsMuted(SoundGroup.BGM),
                    "未设置过的分组按未静音（与 GetVolume 未设置返回 1f 同口径）");

                sound.SetMute(SoundGroup.BGM, true);
                Assert.IsTrue(sound.IsMuted(SoundGroup.BGM), "SetMute 之后必须能读回同一份状态");
                Assert.IsFalse(sound.IsMuted(SoundGroup.SFX), "分组之间互不影响");

                sound.SetMute(SoundGroup.BGM, false);
                Assert.IsFalse(sound.IsMuted(SoundGroup.BGM), "取消静音后必须立刻读回未静音");
            }
            finally
            {
                sound.Dispose();
            }
        }
    }
}
