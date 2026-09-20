using System;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace CloverEngine.Tests
{
    /// <summary>
    /// QUIC 线路的原生互操作与线路规划测试（对应 <c>客户端待做.md</c> #1 / #3）。
    ///
    /// <para>
    /// 覆盖两件**互相独立**的事，分开断言以便失败时一眼定位：
    /// ① 原生绑定是否真的能用（加载 msquic.dll → 打开 API 表 → 建 Registration/Configuration/凭据）；
    /// ② 线路规划是否正确（QUIC 排在最前、能创建出实现、失败后被裁掉）。
    /// </para>
    ///
    /// <para>
    /// 真机/真链路的往返（连上本地网关收发一帧）在 PlayMode 侧（<c>QuicLoopbackTests</c>），
    /// 因为它需要一个真实 QUIC 服务端（自签证书在 Unity 进程里生成不了，见既有 TLS 用例的跳过说明）。
    /// </para>
    /// </summary>
    public class QuicInteropTests
    {
        /// <summary>
        /// 本平台是不是「原生家族」的桌面平台（Windows / Linux / macOS）。
        /// QUIC 在移动端需自建（msquic 无官方移动端构件）、WebGL 属 Web 家族，均不在本用例范围。
        /// </summary>
        private static bool IsDesktopNative =>
            Application.platform == RuntimePlatform.WindowsEditor ||
            Application.platform == RuntimePlatform.WindowsPlayer ||
            Application.platform == RuntimePlatform.LinuxEditor ||
            Application.platform == RuntimePlatform.LinuxPlayer ||
            Application.platform == RuntimePlatform.OSXEditor ||
            Application.platform == RuntimePlatform.OSXPlayer;

        /// <summary>
        /// 门控原生用例：**桌面平台 且 msquic 真的能加载**才继续，否则 <see cref="Assert.Ignore"/>。
        ///
        /// <para>
        /// 两段判据分开的理由：
        /// ① 平台判据（<see cref="IsDesktopNative"/>）—— 移动端/WebGL 上这些用例没有意义；
        /// ② 插件判据 —— **仓库只随 Windows x64 提供 msquic 二进制**（Tools~/native/README.md 的二进制清单），
        /// Linux/macOS 需自行补 libmsquic。平台对但本机没放二进制时属**环境缺失、不是代码缺陷**，
        /// 因此跳过而不是判失败（否则一上 Linux/macOS CI 就整片红，反而掩盖真问题）。
        /// 二进制补齐后这些用例在 Linux/macOS 上会**自动开始跑**，非 Windows 平台不再是零覆盖。
        /// </para>
        /// </summary>
        private static void RequireNativeQuic()
        {
            if (!IsDesktopNative)
                Assert.Ignore("QUIC 原生插件只在桌面平台有意义（移动端需自建、WebGL 属 Web 家族）");
            if (!MsQuicRuntime.TryEnsureReady(out var reason))
                Assert.Ignore($"msquic 原生插件不可用（本平台未提供二进制 / 加载失败，见 Tools~/native/README.md）: {reason}");
        }

        [TearDown]
        public void TearDown()
        {
            TransportCapabilities.ResetQuicSessionFailureForTesting();
        }

        /// <summary>
        /// 原生绑定冒烟：这一条能过，就说明 QUIC_API_TABLE 的**前 12 项偏移**与
        /// ConfigurationOpen / LoadCredential 的结构体布局（QUIC_SETTINGS / QUIC_CREDENTIAL_CONFIG）都对。
        /// 它失败时给出的原因比"连不上"有用得多（宿主缺 DLL / 结构体错位 / 凭据被拒）。
        /// </summary>
        [Test]
        public void MsQuic_LoadsApiTable_RegistrationAndConfiguration()
        {
            RequireNativeQuic();

            MsQuicRuntime.Require(out var api, out var registration, out var configuration);
            Assert.AreNotEqual(IntPtr.Zero, registration, "RegistrationOpen 未返回句柄");
            Assert.AreNotEqual(IntPtr.Zero, configuration, "ConfigurationOpen 未返回句柄");

            // 表项非空 = 至少把函数表读成了正确的指针宽度与排名（顺序错会读到别的函数地址）
            Assert.AreNotEqual(IntPtr.Zero, api.RegistrationOpen);
            Assert.AreNotEqual(IntPtr.Zero, api.ConfigurationOpen);
            Assert.AreNotEqual(IntPtr.Zero, api.ConfigurationLoadCredential);
            Assert.AreNotEqual(IntPtr.Zero, api.ConnectionOpen);
            Assert.AreNotEqual(IntPtr.Zero, api.ConnectionStart);
            Assert.AreNotEqual(IntPtr.Zero, api.StreamOpen);
            Assert.AreNotEqual(IntPtr.Zero, api.StreamSend);
            Assert.AreNotEqual(IntPtr.Zero, api.DatagramSend);
        }

        /// <summary>QUIC 线路地址为 <c>host:port</c>（不是 URL），描述串用于日志/调试面板。</summary>
        [Test]
        public void TransportLine_Quic_DescribesAsQuicHostPort()
        {
            var line = new TransportLine(TransportKind.Quic, "127.0.0.1:8003");
            Assert.AreEqual("quic:127.0.0.1:8003", line.Describe());
        }

        /// <summary>原生家族顺序：QUIC 必须排在 TCP 之前（QUIC 能用就用它，一条通道兼可靠+不可靠）。</summary>
        [Test]
        public void TransportOptions_WithQuicAddr_PutsQuicFirst()
        {
            var options = TransportOptions.From(
                tcpAddr: "127.0.0.1:8002",
                udpAddr: "127.0.0.1:8003",
                wsAddr: "127.0.0.1:8001",
                wsPath: "/ws",
                useTls: false,
                quicAddr: "127.0.0.1:8003");

            Assert.AreEqual(3, options.ReliableOrder.Count);
            Assert.AreEqual(TransportKind.Quic, options.ReliableOrder[0].Kind, "QUIC 必须是主链");
            Assert.AreEqual(TransportKind.Tcp, options.ReliableOrder[1].Kind, "TCP 是它的降级链");
            Assert.AreEqual(TransportKind.WebSocket, options.ReliableOrder[2].Kind);
            Assert.AreEqual("127.0.0.1:8003", options.UdpAddr);
        }

        /// <summary>不给 quicAddr 时**不得**凭空冒出 QUIC 线路（显式才是显式）。</summary>
        [Test]
        public void TransportOptions_WithoutQuicAddr_HasNoQuicLine()
        {
            var options = TransportOptions.From("127.0.0.1:8002", "127.0.0.1:8003");
            foreach (var line in options.ReliableOrder)
                Assert.AreNotEqual(TransportKind.Quic, line.Kind);
        }

        /// <summary>规划器能创建出 QUIC 实现（新线路只需加一个 case，这是它的验收点）。</summary>
        [Test]
        public void TransportPlanner_CreatesQuicConnection()
        {
            var transport = TransportPlanner.Create(new TransportLine(TransportKind.Quic, "127.0.0.1:8003"));
            Assert.IsInstanceOf<QuicConnection>(transport);
            Assert.AreEqual(TransportKind.Quic, transport.Kind);
            Assert.AreEqual(ConnectionState.Disconnected, transport.State);
        }

        /// <summary>桌面端（且插件可用时）QUIC 属于"应当使用"的线路。</summary>
        [Test]
        public void Capabilities_QuicSupported_OnDesktopWithPlugin()
        {
            RequireNativeQuic();

            Assert.IsTrue(MsQuicRuntime.IsAvailable, "RequireNativeQuic 通过后插件必须可用");
            Assert.IsTrue(TransportCapabilities.IsSupported(TransportKind.Quic));
            CollectionAssert.Contains(TransportCapabilities.Supported(), TransportKind.Quic);
        }

        /// <summary>
        /// 能力判定必须**主动探测**（<c>TryEnsureReady</c>），不能读被动缓存 <c>IsAvailable</c>。
        ///
        /// <para>
        /// 真事故（IL2CPP 真机包）：编辑器里因为测试先探测过，QUIC"看起来能走"；
        /// 真 Player 是全新进程、没人探测 ⇒ 判定恒 false ⇒ QUIC 被静默排除，只剩 TCP，
        /// 而且全包**没有一条** QUIC 日志（排查靠猜）。
        /// 断言口径：能力判定结果必须与"显式探测的结果"一致 —— 若改成读被动缓存，这里就会红。
        /// </para>
        /// </summary>
        [Test]
        public void Capabilities_QuicProbeIsActive_NotPassiveCache()
        {
            if (Application.platform == RuntimePlatform.WebGLPlayer)
                Assert.Ignore("WebGL 属 Web 家族，本用例只在原生平台有意义");

            TransportCapabilities.ResetQuicSessionFailureForTesting();

            var supportedHasQuic = TransportCapabilities.Supported().Contains(TransportKind.Quic);
            var probed = MsQuicRuntime.TryEnsureReady(out var reason);

            Assert.AreEqual(probed, supportedHasQuic,
                $"能力判定与主动探测结果不一致（probed={probed}, reason={reason}）：" +
                "说明 IsQuicUsable 读的是被动缓存 IsAvailable 而不是主动探测 TryEnsureReady");
        }

        /// <summary>
        /// 本进程内 QUIC 失败一次后，规划器必须把它裁掉（否则每次重连都白等一个连接超时）。
        /// 这条同时锁住 <c>Supported()</c> 与 <c>IsSupported()</c> 两个入口，避免只改一处。
        /// </summary>
        [Test]
        public void Capabilities_QuicDropped_AfterSessionFailure()
        {
            RequireNativeQuic();

            try
            {
                TransportCapabilities.MarkQuicUnavailableForSession("unit-test");
                Assert.IsFalse(TransportCapabilities.IsSupported(TransportKind.Quic));
                CollectionAssert.DoesNotContain(TransportCapabilities.Supported(), TransportKind.Quic);
                Assert.IsNotNull(TransportCapabilities.QuicSessionFailureReason);
            }
            finally
            {
                TransportCapabilities.ResetQuicSessionFailureForTesting();
            }
        }

        /// <summary>
        /// 家族互斥：Web 家族**不含** QUIC / TCP / 裸 UDP（浏览器没有 BSD socket）；
        /// 原生家族**不含** WebSocket —— 跨家族换线换掉的不只是协议，还有通道语义
        /// （WS 没有真正的不可靠语义，SendUnreliable 会退化成可靠发送）。
        ///
        /// 本程序集（Tests.Editor）只在 Editor 编译运行（原生家族），跑到的是原生那一半：
        /// 断言 WebSocket 必须被判不可用 + 原生家族线路仍可用；WebGL 分支留给 WebGL 承载工程。
        /// </summary>
        [Test]
        public void Capabilities_WebFamily_ExcludesQuic()
        {
            // 测试开关打开时家族判定被整体放行，本用例无意义（正常路径没有 EditMode 用例会打开它）
            Assume.That(TransportCapabilities.AllowCrossFamilyForTesting, Is.False,
                "AllowCrossFamilyForTesting 被污染（前一用例漏复位）时本用例结果无意义");

            if (Application.platform == RuntimePlatform.WebGLPlayer)
            {
                // Web 家族：浏览器无 BSD socket，原生家族三条线路都必须被判不可用
                Assert.IsFalse(TransportCapabilities.IsSupported(TransportKind.Quic), "WebGL 不得把 QUIC 纳入可用线路");
                Assert.IsFalse(TransportCapabilities.IsSupported(TransportKind.Tcp), "WebGL 不得把 TCP 纳入可用线路");
                Assert.IsFalse(TransportCapabilities.IsSupported(TransportKind.RawUdp), "WebGL 不得把裸 UDP 纳入可用线路");
                CollectionAssert.DoesNotContain(TransportCapabilities.Supported(), TransportKind.Quic);
                return;
            }

            // 原生家族（Editor / Standalone…）：Web 家族的 WebSocket 必须被排除。
            Assert.IsFalse(TransportCapabilities.IsSupported(TransportKind.WebSocket),
                "原生平台不得把 WebSocket 纳入可用线路（家族互斥，跨家族会换掉通道语义）");
            // 反向 sanity：家族过滤不能把本家族线路也一并砍掉（原生家族必有 TCP / 裸 UDP）。
            Assert.IsTrue(TransportCapabilities.IsSupported(TransportKind.Tcp), "原生平台必须支持 TCP");
            Assert.IsTrue(TransportCapabilities.IsSupported(TransportKind.RawUdp), "原生平台必须支持裸 UDP");
        }
    }
}
