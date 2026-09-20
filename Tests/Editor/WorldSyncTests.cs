using System.Text;
using NUnit.Framework;

namespace CloverEngine.Tests
{
    /// <summary>
    /// WorldSync 位置插值单测（EditMode，免真实链路）：
    /// 经 NetworkManager 的 internal DrainFrame 注入 EPushDataSync 帧，验证
    /// 「进入即贴合目标 → 移动后每帧单调逼近 → 足够帧后收敛到目标 → 离开后不可读」。
    ///
    /// 这条链路此前**没有读取出口**（插值只写不读、结果不可达），本测试同时锁住新加的
    /// <c>TryGetPosition</c> 契约；它也是首个真正走通 EPushDataSync → MiniJson 解析 → 实体事件分发的用例。
    /// </summary>
    public class WorldSyncTests
    {
        private static NetworkManager CreateNet()
        {
            return new NetworkManager(new GameConfig
            {
                CallTimeoutSeconds = 5,
                MaxReconnectCount = 5,
            });
        }

        private static void Push(NetworkManager nm, string json)
        {
            nm.DrainFrame(ClientFrame.Encode(0u, EMsg.PushDataSync, Encoding.UTF8.GetBytes(json)));
        }

        [Test]
        public void Interpolation_ConvergesToServerTarget()
        {
            var nm = CreateNet();
            var sync = new WorldSyncManager();
            sync.Attach(nm);

            // 进入视野：位置直接贴合服务端值（首次不该从原点插值飞过去）。
            Push(nm, "{\"entity\":{\"event\":\"enter\",\"entity_id\":7,\"x\":10,\"y\":0,\"z\":0}}");
            Assert.IsTrue(sync.TryGetPosition(7, out var xEnter, out _, out _));
            Assert.AreEqual(10f, xEnter, 0.0001f);

            // 服务端下发新目标（离散推送，10Hz 量级）。
            Push(nm, "{\"entity\":{\"event\":\"move\",\"entity_id\":7,\"x\":30,\"y\":0,\"z\":0}}");

            // 第一帧只走一部分：既已前进、又没到目标——这正是「平滑」的定义。
            sync.Tick(1f / 60f);
            Assert.IsTrue(sync.TryGetPosition(7, out var x1, out _, out _));
            Assert.Greater(x1, 10f, "should start moving toward the new target");
            Assert.Less(x1, 30f, "should not snap to the target in a single frame");

            // 单调逼近：不允许回退。
            sync.Tick(1f / 60f);
            sync.TryGetPosition(7, out var x2, out _, out _);
            Assert.GreaterOrEqual(x2, x1);

            // 足够帧后收敛到目标（指数平滑，1 秒足以贴合）。
            for (var i = 0; i < 120; i++)
                sync.Tick(1f / 60f);
            sync.TryGetPosition(7, out var xEnd, out _, out _);
            Assert.AreEqual(30f, xEnd, 0.01f);
        }

        [Test]
        public void Interpolation_UnknownOrLeftEntity_NotReadable()
        {
            var nm = CreateNet();
            var sync = new WorldSyncManager();
            sync.Attach(nm);

            // 从未出现过的实体：不可读。
            Assert.IsFalse(sync.TryGetPosition(999, out _, out _, out _));

            Push(nm, "{\"entity\":{\"event\":\"enter\",\"entity_id\":7,\"x\":1,\"y\":2,\"z\":3}}");
            Assert.IsTrue(sync.TryGetPosition(7, out _, out _, out _));

            // 离开视野后必须不可读，否则业务会拿幽灵位置驱动表现。
            Push(nm, "{\"entity\":{\"event\":\"leave\",\"entity_id\":7}}");
            Assert.IsFalse(sync.TryGetPosition(7, out var x, out var y, out var z));
            Assert.AreEqual(0f, x);
            Assert.AreEqual(0f, y);
            Assert.AreEqual(0f, z);
        }

        [Test]
        public void Tick_WithoutMoveData_IsNoop()
        {
            // Game.Tick 每帧都会调 Sync.Tick，没有实体数据时不能抛异常。
            var nm = CreateNet();
            var sync = new WorldSyncManager();
            sync.Attach(nm);

            Assert.DoesNotThrow(() => sync.Tick(1f / 60f));
            Assert.DoesNotThrow(() => sync.Tick(0f)); // dt <= 0 直接早退
        }
    }
}
