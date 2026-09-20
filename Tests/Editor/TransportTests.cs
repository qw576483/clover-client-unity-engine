using System;
using NUnit.Framework;

namespace CloverEngine.Tests
{
    /// <summary>
    /// 线路选择与传输契约的单元测试（不涉及 socket）。
    /// 覆盖线路构造、WebSocket URL 拼装、**平台家族裁剪**与传输工厂分派；
    /// 真实链路上的收发行为见 PlayMode 的 TCP / WebSocket 回环测试。
    /// </summary>
    public class TransportTests
    {
        /// <summary>只给 TCP 地址时，计划里只有一条 TCP 线路。</summary>
        [Test]
        public void From_OnlyTcp_BuildsSingleLine()
        {
            var plan = TransportOptions.From("127.0.0.1:8002");

            Assert.AreEqual(1, plan.ReliableOrder.Count);
            Assert.AreEqual(TransportKind.Tcp, plan.ReliableOrder[0].Kind);
            Assert.IsNull(plan.UdpAddr, "未配置 UDP 端点时应为 null（不启用不可靠通道）");
        }

        /// <summary>
        /// <c>From</c> 会把配置里给的线路**都收进计划**（含 Web 家族的 WS），
        /// 因为一份配置可能同时供多端使用；**是否生效由平台家族裁剪决定**
        /// （见 <see cref="ApplyPlatformRules_EditorDropsWebFamily"/>）。
        /// 注意：这**不是**「TCP 主链 → WS 降级链」，两条线属不同家族，永远只有一条生效。
        /// </summary>
        [Test]
        public void From_CollectsBothFamilies_OrderAndUdp()
        {
            var plan = TransportOptions.From("127.0.0.1:8002", "127.0.0.1:8003", "127.0.0.1:8001");

            Assert.AreEqual(2, plan.ReliableOrder.Count);
            Assert.AreEqual(TransportKind.Tcp, plan.ReliableOrder[0].Kind, "TCP 在计划里排在前");
            Assert.AreEqual(TransportKind.WebSocket, plan.ReliableOrder[1].Kind, "WS 也在计划里，但属另一个家族");
            Assert.AreEqual("127.0.0.1:8003", plan.UdpAddr);
        }

        /// <summary>地址为空的线路不进入计划（缺失配置不等于「配了一条空线路」）。</summary>
        [Test]
        public void From_EmptyAddr_DropsLine()
        {
            var plan = TransportOptions.From(null, null, "127.0.0.1:8001");

            Assert.AreEqual(1, plan.ReliableOrder.Count);
            Assert.AreEqual(TransportKind.WebSocket, plan.ReliableOrder[0].Kind);
        }

        /// <summary>WebSocket URL 拼装：默认路径 /ws、TLS 走 wss、缺前导斜杠自动补。</summary>
        [Test]
        public void WebSocketUrl_DefaultPathAndTls()
        {
            Assert.AreEqual("ws://127.0.0.1:8001/ws",
                new TransportLine(TransportKind.WebSocket, "127.0.0.1:8001").BuildWebSocketUrl(),
                "未指定路径时用服务端默认 /ws");

            Assert.AreEqual("wss://game.example.com:8001/ws",
                new TransportLine(TransportKind.WebSocket, "game.example.com:8001", null, true).BuildWebSocketUrl(),
                "启用 TLS 时应拼 wss");

            Assert.AreEqual("ws://127.0.0.1:8001/chat/ws",
                new TransportLine(TransportKind.WebSocket, "127.0.0.1:8001", "chat/ws").BuildWebSocketUrl(),
                "路径缺前导斜杠时要补上，否则会拼成 host:8001chat/ws");

            Assert.AreEqual(string.Empty,
                new TransportLine(TransportKind.Tcp, "127.0.0.1:8002").BuildWebSocketUrl(),
                "非 WebSocket 线路不产生 URL");
        }

        /// <summary>连接目标：WebSocket 传完整 URL，TCP 传 host:port。</summary>
        [Test]
        public void ConnectTarget_WebSocketIsUrl_OthersAreHostPort()
        {
            Assert.AreEqual("ws://127.0.0.1:8001/ws",
                TransportPlanner.ConnectTargetOf(new TransportLine(TransportKind.WebSocket, "127.0.0.1:8001")));
            Assert.AreEqual("127.0.0.1:8002",
                TransportPlanner.ConnectTargetOf(new TransportLine(TransportKind.Tcp, "127.0.0.1:8002")));
        }

        /// <summary>传输工厂按线路类型分派；未实现的线路必须显式抛错，不能静默返回空实现。</summary>
        [Test]
        public void Create_DispatchByKind()
        {
            Assert.IsInstanceOf<TcpConnection>(
                TransportPlanner.Create(new TransportLine(TransportKind.Tcp, "127.0.0.1:8002")));
            Assert.IsInstanceOf<WebSocketConnection>(
                TransportPlanner.Create(new TransportLine(TransportKind.WebSocket, "127.0.0.1:8001")));

            Assert.Throws<NotSupportedException>(
                () => TransportPlanner.Create(new TransportLine(TransportKind.RawUdp, "127.0.0.1:8003")),
                "裸 UDP 只作不可靠通道使用，不应被当作可靠线路创建");
        }

        /// <summary>
        /// 平台**家族**裁剪：Editor 属原生家族，只保留 TCP；WebSocket 属 Web 家族会被裁掉，
        /// 且裁剪必须写进 <c>note</c>（可见），不能静默忽略。
        /// 依据架构 §N10 —— 两个家族互斥，不存在「TCP 连不上就退到 WS」。
        /// </summary>
        [Test]
        public void ApplyPlatformRules_EditorDropsWebFamily()
        {
            var plan = TransportOptions.From("127.0.0.1:8002", "127.0.0.1:8003", "127.0.0.1:8001");

            var trimmed = TransportPlanner.ApplyPlatformRules(plan, out var note);

            Assert.AreEqual(1, trimmed.ReliableOrder.Count, "Editor 是原生家族，只应保留 TCP");
            Assert.AreEqual(TransportKind.Tcp, trimmed.ReliableOrder[0].Kind);
            StringAssert.Contains("ws:", note, "被裁掉的线路必须写进说明，不能静默忽略");
            Assert.AreEqual("127.0.0.1:8003", trimmed.UdpAddr, "裁剪线路不应连带丢掉 UDP 端点");
        }

        /// <summary>
        /// 显式放行（仅测试用）打开后，Web 家族线路才会被保留 ——
        /// 这是 WS 实现能在 Editor 上被测到的唯一途径，同时**不恢复**自动跨家族降级。
        /// </summary>
        [Test]
        public void ApplyPlatformRules_AllowCrossFamilyForTesting_KeepsWeb()
        {
            TransportCapabilities.AllowCrossFamilyForTesting = true;
            try
            {
                var plan = TransportOptions.From("127.0.0.1:8002", null, "127.0.0.1:8001");
                var trimmed = TransportPlanner.ApplyPlatformRules(plan, out var note);

                Assert.AreEqual(2, trimmed.ReliableOrder.Count, "放行后 WS 应被保留");
                Assert.AreEqual(string.Empty, note, "未被裁剪时不应产生说明");
            }
            finally
            {
                TransportCapabilities.AllowCrossFamilyForTesting = false;
            }
        }

        /// <summary>线路描述格式（日志与调试面板依赖它可读）。</summary>
        [Test]
        public void Describe_ListsMainAndFallback()
        {
            var plan = TransportOptions.From("127.0.0.1:8002", "127.0.0.1:8003", "127.0.0.1:8001");
            Assert.AreEqual("tcp:127.0.0.1:8002 -> ws:127.0.0.1:8001/ws | udp:127.0.0.1:8003", plan.Describe());

            Assert.AreEqual("(无可靠线路)", new TransportOptions((TransportLine[])null).Describe(),
                "空计划要有明确文案，避免日志里出现空行");
        }

        /// <summary>
        /// 能力表按**平台家族**判定（Editor 属原生家族）：TCP / 裸 UDP 可用，
        /// **WebSocket 不可用** —— 它在 Editor 上底层跑得起来，但不属于原生家族。
        /// 这条断言守的是「不许把跨家族降级放回来」。
        /// </summary>
        [Test]
        public void Capabilities_FollowPlatformFamily()
        {
            Assert.IsTrue(TransportCapabilities.IsSupported(TransportKind.Tcp), "原生家族含 TCP");
            Assert.IsTrue(TransportCapabilities.IsSupported(TransportKind.RawUdp), "原生家族含裸 UDP");
            Assert.IsFalse(TransportCapabilities.IsSupported(TransportKind.WebSocket),
                "WebSocket 属 Web 家族，原生平台不应把纳入线路计划");

            CollectionAssert.DoesNotContain(TransportCapabilities.Supported(), TransportKind.WebSocket,
                "Supported() 也应排除 Web 家族");

            // 显式放行（仅测试用）后才允许跨家族
            TransportCapabilities.AllowCrossFamilyForTesting = true;
            try
            {
                Assert.IsTrue(TransportCapabilities.IsSupported(TransportKind.WebSocket),
                    "放行开关打开后 WS 才可用（WS 实现只能在 Editor 上验证）");
            }
            finally
            {
                TransportCapabilities.AllowCrossFamilyForTesting = false;
            }

            // 未实现的能力必须显式记录，未来谁把 QUIC 标成可用，必须先改这里并给出实现
            StringAssert.Contains("QUIC", TransportCapabilities.UnsupportedSummary);
            StringAssert.Contains("WebTransport", TransportCapabilities.UnsupportedSummary);

            // 平台能力必须显式声明（本机为编辑器/桌面端），而不是让使用方以为「配了 QUIC 就通」
            StringAssert.Contains("TCP", TransportCapabilities.PlatformSummary);
            StringAssert.Contains("QUIC", TransportCapabilities.PlatformSummary);

            // 移动端明文限制必须可被查到：Android 9+ 会直接拦掉明文 TCP / ws://
            StringAssert.Contains("usesCleartextTraffic", TransportCapabilities.MobileCleartextNote);
        }
    }
}
