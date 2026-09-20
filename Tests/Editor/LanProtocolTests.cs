using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using UnityEngine;

namespace CloverEngine.Tests
{
    /// <summary>
    /// 局域网寻服的纯逻辑用例（EditMode，**不建 socket**）：协议编解码 + 去重 + 上限 + 状态机 + 平台裁剪。
    ///
    /// <para>
    /// 真实 UDP 收发（收包线程、窗口收尾、回环应答）在
    /// <c>Tests/PlayMode/LanBrowserLoopbackTests.cs</c>；这里全部走
    /// <see cref="LanBrowser.BeginScanForTesting"/> + <see cref="LanBrowser.Accept"/> 直接把字节喂进去 ——
    /// 断言的是「解析与收敛规则」，不该受本机网卡/防火墙/广播可达性影响。
    /// </para>
    /// </summary>
    public class LanProtocolTests
    {
        /// <summary>固定 nonce（32 个小写 hex，长度与真实 nonce 一致，便于断言长度可控）。</summary>
        private const string Nonce = "0123456789abcdef0123456789abcdef";

        // ====================================================================
        //  协议编解码
        // ====================================================================

        /// <summary>查询报文：单包、无长度前缀、格式为 <c>CLOVER-LAN-QUERY/1|&lt;nonce&gt;</c>。</summary>
        [Test]
        public void BuildQuery_UsesMagicAndNonce_SingleDatagram()
        {
            var bytes = LanProtocol.BuildQuery(Nonce);

            Assert.AreEqual($"{LanProtocol.QueryMagic}|{Nonce}", Encoding.UTF8.GetString(bytes));
            Assert.LessOrEqual(bytes.Length, LanProtocol.MaxDatagramBytes, "查询包必须落在单包上限内");
            Assert.AreEqual(LanProtocol.QueryMagic.Length + 1 + LanProtocol.NonceHexLength, bytes.Length,
                "长度可控：魔数段 + 分隔符 + 32 字符 nonce");
        }

        /// <summary>每轮一个 nonce，且必须是 32 个小写 hex（大小写/长度错会让应答对不上）。</summary>
        [Test]
        public void NewNonce_Is32LowercaseHex_AndVariesPerCall()
        {
            var first = LanProtocol.NewNonce();
            var second = LanProtocol.NewNonce();

            Assert.AreEqual(LanProtocol.NonceHexLength, first.Length);
            foreach (var c in first)
            {
                Assert.IsTrue(c >= '0' && c <= '9' || c >= 'a' && c <= 'f', $"nonce 必须是小写 hex，实到 '{c}'");
            }

            Assert.AreNotEqual(first, second, "每轮 nonce 必须不同（否则上一轮的应答会串进本轮结果）");
        }

        /// <summary>完整应答 → <see cref="LanHostInfo"/> 每个字段都对，Address / UdpAddress 可直接喂 CloverNet.Init。</summary>
        [Test]
        public void TryParseReply_FullPacket_MapsEveryField()
        {
            var datagram = Packet(ReplyText(Nonce, "192.168.1.7:8002",
                udp: "192.168.1.7:8003", auth: "http://192.168.1.7:8051", name: "张三的服",
                players: "3", max: "8", version: "1.0.0", extra: "任意透传串"));

            Assert.IsTrue(
                LanProtocol.TryParseReply(datagram, datagram.Length, Nonce, out var host, out var error, out var warning),
                $"合法应答必须解析成功（错误：{error}）");
            Assert.IsNull(warning, "完整包不该有告警");

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

        /// <summary>缺可选字段（无 udp/auth/name/version/extra）→ 取默认值，**不算失败**。</summary>
        [Test]
        public void TryParseReply_MissingOptionals_UsesDefaults()
        {
            var datagram = Packet(ReplyText(Nonce, "192.168.1.7:8002"));

            Assert.IsTrue(
                LanProtocol.TryParseReply(datagram, datagram.Length, Nonce, out var host, out var error, out _),
                $"只给必备字段的应答必须解析成功（错误：{error}）");

            Assert.AreEqual("192.168.1.7", host.Host);
            Assert.AreEqual(8002, host.GatewayPort);
            Assert.AreEqual(0, host.UdpPort, "未提供 udp 时为 0");
            Assert.AreEqual(string.Empty, host.UdpAddress, "UdpPort<=0 时 UdpAddress 必须是空串");
            Assert.IsNull(host.AuthAddr);
            Assert.IsNull(host.Name);
            Assert.IsNull(host.Version);
            Assert.IsNull(host.Extra);
            Assert.AreEqual(0, host.Players);
            Assert.AreEqual(0, host.MaxPlayers, "0 = 人数上限未知");
        }

        /// <summary>players / max 非法值钳制（负值与非数字 → 0），不导致整包丢弃。</summary>
        [Test]
        public void TryParseReply_IllegalCounts_ClampedToZero()
        {
            var negative = Packet(ReplyText(Nonce, "192.168.1.7:8002", players: "-3", max: "-1"));
            Assert.IsTrue(
                LanProtocol.TryParseReply(negative, negative.Length, Nonce, out var host, out var error, out _),
                $"负数人数不该让整包被丢（错误：{error}）");
            Assert.AreEqual(0, host.Players, "负人数钳制为 0");
            Assert.AreEqual(0, host.MaxPlayers, "负上限钳制为 0");

            // 字符串数字服务端也可能给（JSON 里 "players":"3"），解析不出就是 0，但不能抛。
            var textual = Packet(ReplyText(Nonce, "192.168.1.7:8002", players: "\"3\"", max: "\"x\""));
            Assert.IsTrue(
                LanProtocol.TryParseReply(textual, textual.Length, Nonce, out var host2, out var error2, out _),
                $"人数为非数字时也不能失败（错误：{error2}）");
            Assert.AreEqual(3, host2.Players, "可解析的字符串数字按数字处理");
            Assert.AreEqual(0, host2.MaxPlayers, "不可解析的计数按 0 处理");
        }

        /// <summary>坏包一律「丢弃 + 给原因」，且**绝不抛异常**（收包线程里抛异常会杀进程）。</summary>
        [Test]
        public void TryParseReply_BadPackets_RejectedWithReason()
        {
            AssertRejected(new byte[0], "空包");
            AssertRejected(Encoding.UTF8.GetBytes(new string('x', LanProtocol.MaxDatagramBytes + 1)), "超长包");
            AssertRejected(Packet("CLOVER-NOT-LAN/1|{}"), "魔数不符");
            AssertRejected(Packet(ReplyText(Nonce, "192.168.1.7:8002", magic: "CLOVER-LAN-REPLY/2")), "版本不符");
            AssertRejected(Packet(ReplyText("ffffffffffffffffffffffffffffffff", "192.168.1.7:8002")), "nonce 不匹配");
            AssertRejected(Packet("CLOVER-LAN-REPLY/1"), "缺少分隔符");
            AssertRejected(Packet("CLOVER-LAN-REPLY/1|{not-json}"), "JSON 非法");
            AssertRejected(Packet("CLOVER-LAN-REPLY/1|[1,2]"), "JSON 体不是对象");
            AssertRejected(Packet(ReplyText(Nonce, gateway: null)), "gateway 缺失");
            AssertRejected(Packet(ReplyText(Nonce, "192.168.1.7")), "gateway 无端口");
            AssertRejected(Packet(ReplyText(Nonce, ":8002")), "gateway 主机为空");
            AssertRejected(Packet(ReplyText(Nonce, "192.168.1.7:0")), "gateway 端口越界(0)");
            AssertRejected(Packet(ReplyText(Nonce, "192.168.1.7:70000")), "gateway 端口越界(70000)");
            AssertRejected(Packet(ReplyText(Nonce, "192.168.1.7:80 02")), "gateway 端口含空白");
        }

        /// <summary>可选的 <c>udp</c> 字段坏掉时：**不丢整包**，按「未提供」处理并给出告警。</summary>
        [Test]
        public void TryParseReply_BrokenOptionalUdp_KeepsPacketWithWarning()
        {
            var datagram = Packet(ReplyText(Nonce, "192.168.1.7:8002", udp: "not-a-hostport"));

            Assert.IsTrue(
                LanProtocol.TryParseReply(datagram, datagram.Length, Nonce, out var host, out var error, out var warning),
                $"udp 是可选字段，坏掉不该让整包消失（错误：{error}）");
            Assert.AreEqual(0, host.UdpPort);
            Assert.IsFalse(string.IsNullOrEmpty(warning), "可选字段坏掉必须留下告警（否则排障时看不到它）");
        }

        /// <summary>主机地址取报文里的 gateway，**不信任来源 IP**（主机可能多网卡）。</summary>
        [Test]
        public void LanBrowser_GatewayWinsOverSourceIp()
        {
            var browser = new LanBrowser();
            browser.BeginScanForTesting();

            browser.Accept(Packet(ReplyText(browser.CurrentNonce, "10.0.0.9:8002")), "192.168.1.7");
            Drain();

            Assert.AreEqual(1, browser.Hosts.Count);
            Assert.AreEqual("10.0.0.9:8002", browser.Hosts[0].Address,
                "地址必须取自报文 gateway（来源 IP 只是它发出这一包的网卡）");
        }

        // ====================================================================
        //  去重 / 上限 / 状态机
        // ====================================================================

        /// <summary>同一台主机两次到达：OnHostFound 只触发一次、结果集只有一条、字段被新值覆盖。</summary>
        [Test]
        public void LanBrowser_DuplicateGateway_FiresOnceAndOverwrites()
        {
            var browser = new LanBrowser();
            var found = new List<LanHostInfo>();
            browser.OnHostFound += host => found.Add(host);

            browser.BeginScanForTesting();
            Assert.AreEqual(LanBrowserState.Scanning, browser.State, "开始一轮后应处于扫描中");

            browser.Accept(Packet(ReplyText(browser.CurrentNonce, "192.168.1.7:8002", name: "A", players: "3")), "192.168.1.7");
            Drain();
            Assert.AreEqual(1, found.Count);
            Assert.AreEqual(1, browser.Hosts.Count);
            Assert.AreEqual(3, browser.Hosts[0].Players);

            // 大小写/空白不同也算同一台（去重键规范化）
            browser.Accept(Packet(ReplyText(browser.CurrentNonce, " 192.168.1.7:8002 ", name: "A", players: "5")), "192.168.1.7");
            Drain();

            Assert.AreEqual(1, found.Count, "重复主机不得重复触发 OnHostFound（否则选服列表会跳）");
            Assert.AreEqual(1, browser.Hosts.Count);
            Assert.AreEqual(5, browser.Hosts[0].Players, "重复到达必须用新数据覆盖旧数据");
        }

        /// <summary>结果集上限：超出部分丢弃（打 Warn 一次），不静默膨胀。</summary>
        [Test]
        public void LanBrowser_OverMaxHosts_DropsNewEntries()
        {
            var browser = new LanBrowser();
            var found = new List<LanHostInfo>();
            browser.OnHostFound += host => found.Add(host);

            browser.BeginScanForTesting();
            for (var i = 0; i < LanProtocol.MaxHosts + 1; i++)
            {
                browser.Accept(Packet(ReplyText(browser.CurrentNonce, $"192.168.1.{i}:8002")), "192.168.1.1");
            }

            Drain();

            Assert.AreEqual(LanProtocol.MaxHosts, browser.Hosts.Count,
                $"结果上限 {LanProtocol.MaxHosts}：超出部分必须被丢弃");
            Assert.AreEqual(LanProtocol.MaxHosts, found.Count, "被丢弃的主机不得触发 OnHostFound");
        }

        /// <summary>未扫描时：State==Idle、Hosts 为空，Stop() 是幂等空操作（且不触发 OnScanFinished）。</summary>
        [Test]
        public void LanBrowser_BeforeScan_IsIdleAndEmpty_StopIsNoOp()
        {
            var browser = new LanBrowser();
            var finished = 0;
            browser.OnScanFinished += () => finished++;

            Assert.AreEqual(LanBrowserState.Idle, browser.State);
            Assert.AreEqual(0, browser.Hosts.Count);

            browser.Stop();
            browser.Stop();
            Drain();

            Assert.AreEqual(LanBrowserState.Idle, browser.State, "未扫描时 Stop 是幂等空操作");
            Assert.AreEqual(0, finished, "没有轮次被收尾时不得触发 OnScanFinished");
        }

        /// <summary>未扫描时喂应答 → 丢弃（否则上一轮的包会污染"最近一轮结果"）。</summary>
        [Test]
        public void LanBrowser_AcceptOutsideScan_Dropped()
        {
            var browser = new LanBrowser();
            var found = new List<LanHostInfo>();
            browser.OnHostFound += host => found.Add(host);

            browser.Accept(Packet(ReplyText(Nonce, "192.168.1.7:8002")), "192.168.1.7");
            Drain();

            Assert.AreEqual(0, browser.Hosts.Count);
            Assert.AreEqual(0, found.Count);
        }

        /// <summary>提前结束：收尾恰好一次，且结束后回到 Idle（调用方不会卡在 Scanning）。</summary>
        [Test]
        public void LanBrowser_ScanThenStop_FinishesExactlyOnce()
        {
            var browser = new LanBrowser();
            var finished = 0;
            browser.OnScanFinished += () => finished++;

            browser.BeginScanForTesting();
            Assert.AreEqual(LanBrowserState.Scanning, browser.State);

            browser.Stop();
            Drain();
            Assert.AreEqual(LanBrowserState.Idle, browser.State);
            Assert.AreEqual(1, finished, "提前结束必须收尾一次");

            // 再 Stop（模拟收包线程同刻也走到收尾）不得重复触发
            browser.Stop();
            Drain();
            Assert.AreEqual(1, finished, "收尾是幂等的：不得重复触发 OnScanFinished");
        }

        /// <summary>新一轮扫描清空上一轮结果（Hosts 语义是「最近一轮结果」）。</summary>
        [Test]
        public void LanBrowser_NewScan_ClearsPreviousRound()
        {
            var browser = new LanBrowser();
            browser.BeginScanForTesting();
            var firstRoundNonce = browser.CurrentNonce;
            browser.Accept(Packet(ReplyText(firstRoundNonce, "192.168.1.7:8002")), "192.168.1.7");
            Drain();
            Assert.AreEqual(1, browser.Hosts.Count);

            browser.BeginScanForTesting();
            Assert.AreEqual(0, browser.Hosts.Count, "新一轮开始必须清空上一轮结果");
            Assert.AreNotEqual(firstRoundNonce, browser.CurrentNonce, "每轮必须换新 nonce");

            // 上一轮的 nonce 已失效：回显旧 nonce 的包必须被丢弃
            browser.Accept(Packet(ReplyText(firstRoundNonce, "192.168.1.7:8002")), "192.168.1.7");
            Drain();
            Assert.AreEqual(0, browser.Hosts.Count, "旧 nonce 的应答属于上一轮，必须丢弃（防串味）");
        }

        // ====================================================================
        //  平台裁剪与门面接线
        // ====================================================================

        /// <summary>WebGL 无 BSD socket ⇒ 不支持，且必须给出可直接显示给玩家的原因（禁止静默）。</summary>
        [Test]
        public void LanCapabilities_WebGL_UnsupportedWithPlayerFacingReason()
        {
            Assert.IsFalse(LanCapabilities.IsSupportedOn(RuntimePlatform.WebGLPlayer));

            var reason = LanCapabilities.ReasonFor(RuntimePlatform.WebGLPlayer);
            Assert.IsFalse(string.IsNullOrEmpty(reason), "不支持必须给出原因（禁止静默失败）");
            StringAssert.Contains("WebGL", reason);

            Assert.IsTrue(LanCapabilities.IsSupportedOn(RuntimePlatform.WindowsPlayer));
            Assert.IsTrue(LanCapabilities.IsSupportedOn(RuntimePlatform.Android));
            Assert.AreEqual(string.Empty, LanCapabilities.ReasonFor(RuntimePlatform.OSXPlayer),
                "支持时原因必须是空串");
        }

        /// <summary>浏览器实例按当前平台接线（Editor 属原生家族 ⇒ 支持，原因为空串）。</summary>
        [Test]
        public void LanBrowser_OnNativePlatform_ReportsSupported()
        {
            var browser = new LanBrowser();

            Assert.IsTrue(browser.IsSupported, $"Editor 平台应支持寻服，实际平台 {Application.platform}");
            Assert.AreEqual(string.Empty, browser.UnsupportedReason);
        }

        /// <summary>
        /// 事件名常量必须与线上字符串一致（值被改动 = 所有既有订阅静默失效）。
        /// </summary>
        [Test]
        public void CloverEvents_LanNames_AreStable()
        {
            Assert.AreEqual("Net.LanHostFound", CloverEvents.Net.LanHostFound);
            Assert.AreEqual("Net.LanScanFinished", CloverEvents.Net.LanScanFinished);
        }

        // ====================================================================
        //  辅助
        // ====================================================================

        private static byte[] Packet(string text) => Encoding.UTF8.GetBytes(text);

        /// <summary>拼一条应答报文（首段魔数 + 扁平 JSON；只拼传入的字段，便于构造缺字段/坏字段的包）。</summary>
        private static string ReplyText(string nonce, string gateway, string udp = null, string auth = null,
            string name = null, string players = null, string max = null, string version = null,
            string extra = null, string magic = null)
        {
            var json = new StringBuilder("{");

            void Str(string key, string value)
            {
                if (value == null) return;
                if (json.Length > 1) json.Append(',');
                json.Append('"').Append(key).Append("\":\"").Append(value).Append('"');
            }

            void Raw(string key, string value)
            {
                if (value == null) return;
                if (json.Length > 1) json.Append(',');
                json.Append('"').Append(key).Append("\":").Append(value);
            }

            Str("nonce", nonce);
            Str("name", name);
            Str("gateway", gateway);
            Str("udp", udp);
            Str("auth", auth);
            Raw("players", players);
            Raw("max", max);
            Str("version", version);
            Str("extra", extra);
            json.Append('}');

            return $"{magic ?? LanProtocol.ReplyMagic}|{json}";
        }

        private static void AssertRejected(byte[] datagram, string what)
        {
            var accepted = LanProtocol.TryParseReply(datagram, datagram.Length, Nonce, out var host, out var error, out _);

            Assert.IsFalse(accepted, $"{what} 必须被丢弃");
            Assert.IsNull(host, $"{what} 被丢弃时不得产出结果");
            Assert.IsFalse(string.IsNullOrEmpty(error), $"{what} 丢弃必须带原因（日志要用它）");
        }

        /// <summary>
        /// 排空主线程队列：未 Launch 时 <c>Game.Dispatcher</c> 为 null（事件在调用线程直接执行），
        /// 已 Launch 时事件会排队 —— 两种情况下都在这里统一收敛，测试不依赖运行环境。
        /// </summary>
        private static void Drain()
        {
            Game.Dispatcher?.Flush();
        }
    }
}
