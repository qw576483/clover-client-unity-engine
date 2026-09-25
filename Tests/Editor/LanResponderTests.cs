using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using NUnit.Framework;
using UnityEngine;

namespace CloverEngine.Tests
{
    /// <summary>
    /// 局域网**应答端**（<see cref="ILanResponder"/>）的用例（EditMode）：
    /// 协议编/解往返 + 常量冻结 + 四条边界不抛 + 真实 UDP 的「应答端被浏览器扫到」。
    ///
    /// <para>
    /// 本份用**真实实现**把浏览器与应答端接起来（<c>Tests/PlayMode/LanBrowserLoopbackTests.cs</c> 里的
    /// <c>ResponderLoop</c> 只是测试用替身，业务用不到），让「有主机在跑却扫不到」变成一条可判定的
    /// 断言 —— 否则「Find Servers 恒为 0 台」**分不清**是"没有主机在跑"还是"没人应答"。
    /// </para>
    /// </summary>
    public class LanResponderTests
    {
        /// <summary>固定 nonce（32 个小写 hex，与真实 nonce 同长度）。</summary>
        private const string Nonce = "0123456789abcdef0123456789abcdef";

        private ILanResponder _responder;
        private LanBrowser _browser;
        private int _queryEvents;
        private volatile string _lastQueryFrom;

        /// <summary>
        /// 无论成败都收干净：应答端 socket + 收包线程 + 浏览器扫描。
        /// 应答端用 <c>Dispose</c>（它内部会 <c>Stop</c>）；<c>Stop</c> 不 Join，
        /// 收包线程是 background，会在 ≤ ReceiveTimeoutMs 内自行退出。
        /// </summary>
        [TearDown]
        public void Cleanup()
        {
            try
            {
                _browser?.Stop();
            }
            catch (Exception ex)
            {
                Game.Logger?.Warn("LanTest", $"清理浏览器扫描失败：{ex.Message}");
            }

            _browser = null;

            try
            {
                _responder?.Dispose();
            }
            catch (Exception ex)
            {
                Game.Logger?.Warn("LanTest", $"清理应答端失败：{ex.Message}");
            }

            _responder = null;
            _queryEvents = 0;
            _lastQueryFrom = null;
        }

        // ====================================================================
        //  协议：编 / 解往返 + 常量冻结
        // ====================================================================

        /// <summary>自己构造的应答必须能被自己的 <see cref="LanProtocol.TryParseReply"/> 逐字段解析回来。</summary>
        [Test]
        public void BuildReply_RoundTrip_MapsEveryField()
        {
            var self = LanHostInfo.Create("192.168.1.7", 8002, 8003, "http://192.168.1.7:8051",
                "张三的服", 3, 8, "1.0.0", "任意透传串");

            var bytes = LanProtocol.BuildReply(Nonce, self);

            // 报文形状：单包、无长度前缀、首段是应答魔数。
            Assert.LessOrEqual(bytes.Length, LanProtocol.MaxDatagramBytes, "应答必须落在单包上限内");
            StringAssert.StartsWith($"{LanProtocol.ReplyMagic}|", Encoding.UTF8.GetString(bytes));

            Assert.IsTrue(
                LanProtocol.TryParseReply(bytes, bytes.Length, Nonce, out var host, out var error, out var warning),
                $"自己构造的应答必须能被自己解析（错误：{error}）");
            Assert.IsNull(warning, "完整应答不该有告警");

            Assert.AreEqual("192.168.1.7", host.Host);
            Assert.AreEqual(8002, host.GatewayPort);
            Assert.AreEqual(8003, host.UdpPort);
            Assert.AreEqual("192.168.1.7:8002", host.Address);
            Assert.AreEqual("192.168.1.7:8003", host.UdpAddress);
            Assert.AreEqual("http://192.168.1.7:8051", host.AuthAddr);
            Assert.AreEqual("张三的服", host.Name);
            Assert.AreEqual(3, host.Players);
            Assert.AreEqual(8, host.MaxPlayers);
            Assert.AreEqual("1.0.0", host.Version);
            Assert.AreEqual("任意透传串", host.Extra);
        }

        /// <summary>可选字段为空 ⇒ 不出现在报文里，对端按缺省值处理（不算失败）。</summary>
        [Test]
        public void BuildReply_MissingOptionals_OmittedAndParsedAsDefaults()
        {
            var self = LanHostInfo.Create("10.0.0.9", 8002, 0, null, null, 0, 0, null, null);

            var bytes = LanProtocol.BuildReply(Nonce, self);
            var text = Encoding.UTF8.GetString(bytes);

            Assert.IsFalse(text.Contains("\"udp\""), "UdpPort<=0 时不得写 udp 字段");
            Assert.IsFalse(text.Contains("\"name\""), "Name 为空时不得写 name 字段");

            Assert.IsTrue(
                LanProtocol.TryParseReply(bytes, bytes.Length, Nonce, out var host, out var error, out _),
                $"错误：{error}");
            Assert.AreEqual("10.0.0.9:8002", host.Address);
            Assert.AreEqual(0, host.UdpPort);
            Assert.AreEqual(string.Empty, host.UdpAddress);
            Assert.IsNull(host.Name);
            Assert.AreEqual(0, host.MaxPlayers, "0 = 人数上限未知");
        }

        /// <summary>名字里带引号 / 反斜杠 / 换行时，JSON 必须被正确转义（手工拼串最容易漏的一处）。</summary>
        [Test]
        public void BuildReply_WeirdName_IsJsonEscaped()
        {
            var self = LanHostInfo.Create("192.168.1.7", 8002, 0, null,
                "a\"b\\c\nd", 0, 0, null, null);

            var bytes = LanProtocol.BuildReply(Nonce, self);

            Assert.IsTrue(
                LanProtocol.TryParseReply(bytes, bytes.Length, Nonce, out var host, out var error, out _),
                $"名字含特殊字符也必须能解析（错误：{error}）");
            Assert.AreEqual("a\"b\\c\nd", host.Name);
        }

        /// <summary>查询报文往返 + 坏包一律「拒绝并给原因」。</summary>
        [Test]
        public void TryParseQuery_RoundTrip_AndRejectsBadPackets()
        {
            var query = LanProtocol.BuildQuery(Nonce);
            Assert.IsTrue(LanProtocol.TryParseQuery(query, query.Length, out var nonce, out var error), error);
            Assert.AreEqual(Nonce, nonce);

            // 容忍尾随空白：对端可能是别的实现（带 \r\n 的文本工具）
            var padded = Encoding.UTF8.GetBytes($"{LanProtocol.QueryMagic}|{Nonce} \r\n");
            Assert.IsTrue(LanProtocol.TryParseQuery(padded, padded.Length, out var trimmed, out _));
            Assert.AreEqual(Nonce, trimmed);

            AssertQueryRejected(null, "空引用");
            AssertQueryRejected(new byte[0], "空包");
            AssertQueryRejected(Encoding.UTF8.GetBytes(new string('x', LanProtocol.MaxDatagramBytes + 1)), "超长包");
            AssertQueryRejected(Encoding.UTF8.GetBytes($"{LanProtocol.ReplyMagic}|{Nonce}"), "应答包当查询");
            AssertQueryRejected(Encoding.UTF8.GetBytes($"CLOVER-LAN-QUERY/2|{Nonce}"), "版本不符");
            AssertQueryRejected(Encoding.UTF8.GetBytes(LanProtocol.QueryMagic), "缺少分隔符");
            AssertQueryRejected(Encoding.UTF8.GetBytes($"{LanProtocol.QueryMagic}|"), "nonce 为空");
            AssertQueryRejected(Encoding.UTF8.GetBytes($"{LanProtocol.QueryMagic}|   "), "nonce 全空白");
        }

        /// <summary>
        /// 协议常量**逐字冻结**：值被改动 = 与既有主机 / 既有客户端不兼容
        /// （改一个字符，现象是"一台都扫不到"）。
        /// </summary>
        [Test]
        public void ProtocolConstants_AreFrozen_ForInterop()
        {
            Assert.AreEqual(47777, LanProtocol.DefaultPort);
            Assert.AreEqual(64, LanProtocol.MaxHosts);
            Assert.AreEqual(512, LanProtocol.MaxDatagramBytes);
            Assert.AreEqual(16, LanProtocol.NonceBytes);
            Assert.AreEqual(32, LanProtocol.NonceHexLength);
            Assert.AreEqual("CLOVER-LAN-QUERY/1", LanProtocol.QueryMagic);
            Assert.AreEqual("CLOVER-LAN-REPLY/1", LanProtocol.ReplyMagic);
        }

        // ====================================================================
        //  四条边界（都不能抛）
        // ====================================================================

        /// <summary>平台不支持：Start 返回 false + 给出原因，且 Stop / Dispose 仍幂等不抛。</summary>
        [Test]
        public void Responder_UnsupportedPlatform_ReportsReasonWithoutThrowing()
        {
            // Editor 里改不了 Application.platform ⇒ 用注入缝换掉平台判定
            var responder = new LanResponder(() => false,
                () => "WebGL 无 BSD socket，浏览器不能收发 UDP 广播，局域网寻服不可用");
            _responder = responder;

            Assert.IsFalse(responder.IsSupported);
            StringAssert.Contains("WebGL", responder.UnsupportedReason);

            var self = LanHostInfo.Create("127.0.0.1", 8002, 0, null, "x", 0, 0, null, null);
            var started = true;

            Assert.DoesNotThrow(() => started = responder.Start(self));
            Assert.IsFalse(started, "不支持的平台上 Start 必须返回 false");
            Assert.IsFalse(responder.IsRunning);
            Assert.AreEqual(0, responder.ListeningPort);
            Assert.IsFalse(string.IsNullOrEmpty(responder.LastError), "失败必须留痕（禁止静默）");

            Assert.DoesNotThrow(() => responder.Stop());
            Assert.DoesNotThrow(() => responder.Stop());
            Assert.DoesNotThrow(() => responder.Dispose());
            Assert.DoesNotThrow(() => responder.Dispose());
            Assert.IsFalse(responder.IsRunning);
        }

        /// <summary>非法参数 / 未启动 Stop / Dispose 后再 Start：全部不抛，只报错。</summary>
        [Test]
        public void Responder_IllegalArguments_ReportErrorWithoutThrowing()
        {
            var responder = new LanResponder();
            _responder = responder;

            Assert.IsTrue(responder.IsSupported, $"Editor 平台应支持应答，实际 {Application.platform}");

            // self 为 null
            Assert.DoesNotThrow(() => responder.Start(null));
            Assert.IsFalse(responder.IsRunning);
            StringAssert.Contains("self", responder.LastError);

            // gateway 端口越界（70000）
            var badGateway = LanHostInfo.Create("127.0.0.1", 70000, 0, null, "x", 0, 0, null, null);
            Assert.DoesNotThrow(() => responder.Start(badGateway));
            Assert.IsFalse(responder.IsRunning);
            Assert.IsFalse(string.IsNullOrEmpty(responder.LastError));

            // 监听端口越界
            var self = LanHostInfo.Create("127.0.0.1", 8002, 0, null, "x", 0, 0, null, null);
            Assert.DoesNotThrow(() => responder.Start(self, new LanRespondOptions { Port = 70000 }));
            Assert.IsFalse(responder.IsRunning);
            Assert.IsFalse(string.IsNullOrEmpty(responder.LastError));

            // 未启动 Stop：幂等空操作
            Assert.DoesNotThrow(() => responder.Stop());
            Assert.IsFalse(responder.IsRunning);

            // Dispose 之后再 Start：报错、不抛
            responder.Dispose();
            Assert.DoesNotThrow(() => responder.Start(self));
            Assert.IsFalse(responder.IsRunning);
            Assert.IsFalse(string.IsNullOrEmpty(responder.LastError));
        }

        /// <summary>端口被占用：Start 失败 + 留痕，**不去抢绑**（抢绑会让两个应答端随机分走查询）。</summary>
        [Test]
        public void Responder_PortOccupied_ReportsErrorWithoutThrowing()
        {
            if (!LanCapabilities.IsSupported)
                Assert.Ignore($"本平台不支持局域网寻服：{LanCapabilities.ReasonFor(Application.platform)}");

            using (var holder = new UdpClient(new IPEndPoint(IPAddress.Any, 0)))
            {
                var port = ((IPEndPoint)holder.Client.LocalEndPoint).Port;

                var responder = new LanResponder();
                _responder = responder;
                var self = LanHostInfo.Create("127.0.0.1", 8002, 0, null, "x", 0, 0, null, null);
                var started = true;

                Assert.DoesNotThrow(() => started = responder.Start(self, new LanRespondOptions { Port = port }));
                Assert.IsFalse(started, $"端口 {port} 已被占用，Start 必须失败（不得抢绑）");
                Assert.IsFalse(responder.IsRunning);
                Assert.IsFalse(string.IsNullOrEmpty(responder.LastError), "端口占用必须留痕");
            }
        }

        // ====================================================================
        //  真实 UDP：应答端被浏览器扫到（"问"与"答"接起来）
        // ====================================================================

        /// <summary>
        /// 端到端：真实 <see cref="CloverLan.CreateResponder"/> + 真实 <see cref="LanBrowser"/>，
        /// 本机回环发包 → 应答 → 结果集。这正是不再需要在业务侧复制协议的那条链路。
        /// </summary>
        [Test]
        [Timeout(30000)]
        public void Responder_IsDiscoveredByBrowser_RealUdpRoundTrip()
        {
            if (!LanCapabilities.IsSupported)
                Assert.Ignore($"本平台不支持局域网寻服：{LanCapabilities.ReasonFor(Application.platform)}");

            var port = ReservePort();

            var self = LanHostInfo.Create("127.0.0.1", 8002, 8003, "http://127.0.0.1:8051",
                "引擎应答端", 2, 7, "9.9.9", "extra");

            var responder = CloverLan.CreateResponder();
            _responder = responder;
            responder.OnQuery += from =>
            {
                Interlocked.Increment(ref _queryEvents);
                _lastQueryFrom = from;
            };

            Assert.IsTrue(responder.Start(self, new LanRespondOptions { Port = port }),
                $"应答端必须能在本机端口 {port} 起起来：{responder.LastError}");
            Assert.IsTrue(responder.IsRunning);
            Assert.AreEqual(port, responder.ListeningPort);

            var browser = new LanBrowser();
            _browser = browser;

            browser.Scan(new LanScanOptions
            {
                Port = port,
                IncludeBroadcast = false,
                IncludeSubnetBroadcast = false,
                IncludeLoopback = true,
                DurationMs = 1500,
            });

            Assert.AreEqual(LanBrowserState.Scanning, browser.State, "Scan 后应立即进入扫描态");

            var deadline = DateTime.UtcNow.AddSeconds(6);
            while (browser.Hosts.Count == 0 && DateTime.UtcNow < deadline)
            {
                Game.Dispatcher?.Flush();
                Thread.Sleep(20);
            }

            Assert.AreEqual(1, browser.Hosts.Count,
                $"应答端在跑却扫不到 = 本能力失效（已收查询 {responder.QueryCount} / 已回应答 {responder.ReplyCount}）");

            var host = browser.Hosts[0];
            Assert.AreEqual("127.0.0.1:8002", host.Address, "Address 必须可直接喂 CloverNet.Init");
            Assert.AreEqual("127.0.0.1:8003", host.UdpAddress);
            Assert.AreEqual("http://127.0.0.1:8051", host.AuthAddr);
            Assert.AreEqual("引擎应答端", host.Name);
            Assert.AreEqual(2, host.Players);
            Assert.AreEqual(7, host.MaxPlayers);
            Assert.AreEqual("9.9.9", host.Version);
            Assert.AreEqual("extra", host.Extra);

            Assert.GreaterOrEqual(responder.QueryCount, 1, "应答端应收到查询");
            Assert.AreEqual(responder.QueryCount, responder.ReplyCount, "每条合法查询都必须有应答");
            Assert.AreEqual(0, responder.DroppedCount, "回环查询里没有脏包");

            Game.Dispatcher?.Flush();
            Assert.GreaterOrEqual(_queryEvents, 1, "OnQuery 应报出查询来源（留痕）");
            StringAssert.Contains(":", _lastQueryFrom);

            browser.Stop();
        }

        /// <summary>
        /// 停止之后不再应答：<c>Stop</c> 必须真的收干净（否则"退到菜单还占着 47777"，
        /// 下一次开主机就报端口占用）。
        /// </summary>
        [Test]
        [Timeout(30000)]
        public void Responder_AfterStop_NoLongerReplies()
        {
            if (!LanCapabilities.IsSupported)
                Assert.Ignore($"本平台不支持局域网寻服：{LanCapabilities.ReasonFor(Application.platform)}");

            var port = ReservePort();
            var self = LanHostInfo.Create("127.0.0.1", 8002, 0, null, "停服后", 0, 0, null, null);

            var responder = CloverLan.CreateResponder();
            _responder = responder;
            Assert.IsTrue(responder.Start(self, new LanRespondOptions { Port = port }), responder.LastError);

            var before = responder.QueryCount;
            responder.Stop();
            Assert.IsFalse(responder.IsRunning);
            Assert.AreEqual(0, responder.ListeningPort);

            // 停了之后发一条查询出去：应答端不该再回（自己也不该再收到）
            using (var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
            {
                probe.Client.ReceiveTimeout = 500;
                var query = LanProtocol.BuildQuery(Nonce);
                probe.Send(query, query.Length, new IPEndPoint(IPAddress.Loopback, port));

                var gotReply = false;
                try
                {
                    var from = new IPEndPoint(IPAddress.Any, 0);
                    probe.Receive(ref from);
                    gotReply = true;
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut
                                                || ex.SocketErrorCode == SocketError.ConnectionReset
                                                || ex.SocketErrorCode == SocketError.Interrupted)
                {
                    // 期望路径：端口已关 ⇒ 收不到应答（回环上还可能收到 ICMP 回执 ConnectionReset）
                    gotReply = false;
                }

                Assert.IsFalse(gotReply, "Stop 之后不得再应答");
            }

            Assert.AreEqual(before, responder.QueryCount, "Stop 之后不该再计入查询");

            // 同一端口可以立刻重新起（UDP 无 TIME_WAIT）
            Assert.IsTrue(responder.Start(self, new LanRespondOptions { Port = port }),
                $"Stop 后应能立刻在同一端口重启（否则会报端口占用）：{responder.LastError}");
        }

        // ====================================================================
        //  辅助
        // ====================================================================

        /// <summary>占一个临时端口再立刻放掉（UDP 无连接，不需要真占住）。</summary>
        private static int ReservePort()
        {
            using (var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
            {
                return ((IPEndPoint)probe.Client.LocalEndPoint).Port;
            }
        }

        private static void AssertQueryRejected(byte[] datagram, string what)
        {
            var length = datagram?.Length ?? 0;
            var accepted = LanProtocol.TryParseQuery(datagram, length, out var nonce, out var error);

            Assert.IsFalse(accepted, $"{what} 必须被拒绝（不回包）");
            Assert.IsNull(nonce, $"{what} 被拒绝时不得产出 nonce");
            Assert.IsFalse(string.IsNullOrEmpty(error), $"{what} 拒绝必须带原因（日志要用它）");
        }
    }
}
