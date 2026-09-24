using System;
using System.Collections;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CloverEngine.Tests
{
    /// <summary>
    /// QUIC 线路的**真实链路**用例（PlayMode）：连本机网关的 QUIC 端口 → 握手 → 发一帧 → 收回包。
    ///
    /// <para>
    /// <b>⚠️ 默认不跑，必须显式开启</b>：设环境变量 <c>CLOVER_QUIC_E2E=1</c> 后启动编辑器才会执行。
    /// 原因不是"懒得测"，而是这条用例会**真的驱动 msquic 原生栈**：
    /// 一旦它失败（比如网关没开 QUIC），若清理不彻底，活连接会在退出 Play / 域卸载时让
    /// msquic 回调打到已卸载的托管代码 —— 表现是**编辑器进程直接消失**
    /// （Windows 事件日志 `Faulting module: msquic.dll`，异常码 0xc0000420 = STATUS_ASSERTION_FAILURE）。
    /// 所以：① 默认跳过；② <see cref="TearDownQuic"/> 里**无论如何**强制关闭（断言失败时方法体内的清理不会执行）；
    /// ③ 原生层另有一道"域卸载前全关"的兜底（见 <see cref="MsQuicRuntime.ShutdownAllConnections"/>）。
    /// </para>
    ///
    /// <para><b>前置条件（缺一即失败，属环境问题而非代码问题）</b></para>
    /// <list type="number">
    /// <item>网关在跑，且**配了 TLS 证书**（<c>gateway.tls_cert</c> / <c>tls_key</c>）——
    /// 不配则服务端 QUIC 起不来（日志 <c>quic start failed ... TLSConfig required</c>）；</item>
    /// <item>QUIC 监听在网关 <c>listen_udp</c>（默认 <c>127.0.0.1:8003</c>，与裸 UDP 共享端口）；</item>
    /// <item>客户端所在机器**信任**该证书（本地开发用 <c>mkcert -install</c>）。引擎不提供"跳过证书校验"开关
    /// （结构规则 §N7），不受信时握手失败 —— 这是**期望行为**。</item>
    /// </list>
    /// </summary>
    public class QuicLoopbackTests
    {
        /// <summary>网关 QUIC 端点（与裸 UDP 共享 <c>gateway.listen_udp</c>）。</summary>
        private const string GatewayQuicAddr = "127.0.0.1:8003";

        /// <summary>网关 TCP 端点（全栈用例的降级链用）。</summary>
        private const string GatewayTcpAddr = "127.0.0.1:8002";

        /// <summary>
        /// 真实链路 QUIC 用例的开关：<c>CLOVER_QUIC_E2E=1</c> 才跑，**默认整条跳过**（设计意图如下）。
        ///
        /// <para>
        /// **为什么默认关**（不是"懒得测"）：这些用例会真的驱动 msquic 原生栈去连本机网关，前置条件
        /// 是「网关在跑 + 配了 TLS 证书 + 本机信任该证书 + 该平台的 msquic 二进制在位」——
        /// 少了任何一条都必然失败，而失败**不是代码缺陷**。若默认就跑，任何一台没做这套环境准备的机器
        /// 上都是整片红；红得没有信息量时，真正的回归就会被当成"又是环境问题"忽略掉。
        /// </para>
        ///
        /// <para>
        /// **CI 上怎么办**：CI 的职责是跑"必须有结论"的用例（帧编解码、线路规划、家族裁剪 —— 见
        /// <c>QuicInteropTests</c> 与 <c>TransportTests</c>，它们在无插件时明确 Ignore、有插件时真跑）。
        /// 真链路往返由**人工/按需**开启：准备好环境后设 <c>CLOVER_QUIC_E2E=1</c> 再启动编辑器。
        /// 一旦开启而不满足前置条件，用例会给出环境检查清单（见下方失败分支），不静默跳过。
        /// </para>
        /// </summary>
        private static bool E2EEnabled => Environment.GetEnvironmentVariable("CLOVER_QUIC_E2E") == "1";

        private static bool HasNativePlugin =>
            Application.platform == RuntimePlatform.WindowsEditor ||
            Application.platform == RuntimePlatform.WindowsPlayer;

        private QuicConnection _conn;
        private bool _touchedGameNet;

        /// <summary>本用例是否由**自己**启动了引擎（Launch 生效）：收尾据此调 Game.Shutdown。</summary>
        private bool _launchedGame;

        /// <summary>
        /// **必须有的兜底清理**：断言失败会抛异常，方法体末尾的 <c>Disconnect()</c> 根本不会执行，
        /// 于是活连接被留到 Play 模式退出 —— 那就是崩溃的来源。这里无论成败都强制关。
        /// </summary>
        [UnityTearDown]
        public IEnumerator TearDownQuic()
        {
            if (_conn != null)
            {
                _conn.CloseNow("test teardown");
                _conn = null;
            }

            if (_touchedGameNet)
            {
                Game.Net?.Disconnect();
                yield return new WaitForSecondsRealtime(0.5f);
                _touchedGameNet = false;
            }

            if (_launchedGame)
            {
                // ★ 补收尾：用例调过 Game.Launch 就必须退回到"未启动"——
                //   否则 IsRunning / Dispatcher / Net 全部残留给同进程后续用例，
                //   下一次 Launch 变成 no-op（只留一条 "Already running, Launch ignored" 警告）。
                Game.Shutdown();
                _launchedGame = false;
            }

            MsQuicRuntime.ShutdownAllConnections("test teardown");
            yield return null;
        }

        [TearDown]
        public void ResetSessionFlags() => TransportCapabilities.ResetQuicSessionFailureForTesting();

        private static IEnumerator WaitFor(Func<bool> condition, float timeoutSeconds, string what)
        {
            var deadline = Time.realtimeSinceStartup + timeoutSeconds;

            // ★ 只求值一次、用它做断言，**绝不能在 Assert 里再求值一次**：
            //   这里的条件常有副作用（例如 `TryTakePacket` 会**出队**），
            //   循环里那次成功后包已被取走，断言里再求值就变 false ⇒ 明明成功却报"等待超时"。
            //   （浪费：一次真链路用例就是这样被判失败的，而引擎日志显示回包早已收到 `recv frame len=50`。）
            var ok = condition();
            while (!ok && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
                ok = condition();
            }

            Assert.IsTrue(ok, $"等待超时（{timeoutSeconds}s）：{what}");
        }

        /// <summary>
        /// 传输层往返：握手 → 发一帧登录请求（token 故意非法）→ 收回包。
        ///
        /// 断言的是**链路**而不是登录成功：非法 token 会让服务端回一个 <c>success=false</c>
        /// （或 <c>EMsg.Error</c>）回包，而**回包 requestID 必须与请求一致** —— 这一条同时证明了：
        /// QUIC 握手成功、TLS 校验通过、客户端主动开流被服务端 AcceptStream 接住、
        /// <c>[4B 大端长度]</c> 分帧双向都能通、网关到逻辑服的派发与回包路径都在。
        /// </summary>
        [UnityTest]
        [Timeout(90000)]
        public IEnumerator Quic_RealGateway_RequestReplyRoundTrip()
        {
            if (!HasNativePlugin)
                Assert.Ignore("msquic 原生插件未提供本平台二进制（见 Tools~/native/README.md）");
            if (!E2EEnabled)
                Assert.Ignore("真实链路 QUIC 用例默认不跑：设 CLOVER_QUIC_E2E=1 后启动编辑器再执行");

            var connected = false;
            var disconnected = false;

            _conn = new QuicConnection { ConnectTimeoutMs = 3000 };
            _conn.Connected += () => connected = true;
            _conn.Disconnected += () => disconnected = true;

            try
            {
                _conn.ConnectAsync(GatewayQuicAddr);
            }
            catch (NotSupportedException e)
            {
                // ★ 只有 ConnectAsync 的"插件不可用"异常（TryEnsureReady 失败时抛 NotSupportedException）
                //   才属环境缺失、跳过；地址非法（FormatException）等真实故障必须让用例红。
                Assert.Ignore($"QUIC 原生插件不可用（本机未提供 / 加载失败），跳过用例: {e.Message}");
            }

            yield return WaitFor(() => connected || disconnected, 10f, "QUIC 握手");

            if (!connected)
            {
                Assert.Fail(
                    $"QUIC 未连上 {GatewayQuicAddr}。环境检查：\n" +
                    "1) 网关是否配了 gateway.tls_cert / tls_key（不配则 QUIC 不启动，日志见 quic start failed）；\n" +
                    "2) 8003/UDP 是否有监听（QUIC 与裸 UDP 共享该端口）；\n" +
                    "3) 本机是否信任该证书（mkcert -install）—— 引擎不做跳过校验，不受信就会握手失败。");
            }

            // Datagram 协商：服务端会发 DATAGRAM_STATE_CHANGED{SendEnabled=true}，
            // 说明"不可靠通道可用"—— QUIC 模式下因此不需要 EMsgBindUDP。
            yield return WaitFor(() => _conn.SupportsUnreliable, 5f, "QUIC Datagram 协商");
            Assert.IsTrue(_conn.SupportsUnreliable, "QUIC 应协商出 Datagram（服务端具备 SendDatagram 能力）");

            // 请求/回包往返（requestID=42 便于在回包里认领）
            const uint requestId = 42;
            var body = Encoding.UTF8.GetBytes("{\"token\":\"invalid-token-for-line-test\"}");
            var frame = ClientFrame.Encode(requestId, EMsg.Login, body);
            _conn.Send(frame, 0, frame.Length);

            byte[] reply = null;
            // 等待 25s：回包要先经账号服 HTTP 校验（invalid token 会被拒），
            // 实测服务端日志显示"客户端断开 40ms 后服务端才记下拒绝"，响应可能远慢于一次本地往返。
            yield return WaitFor(
                () => _conn.TryTakePacket(out reply) && reply != null && reply.Length > 0,
                25f,
                "QUIC 回包（网关 ELoginReply 或 EMsg.Error）");

            Assert.IsTrue(ClientFrame.TryDecode(reply, out var replyRequestId, out var replyMsgId, out _),
                "回包必须能按客户端帧解码（8 字节帧头）");
            Assert.AreEqual(requestId, replyRequestId,
                $"回包 requestID 必须与请求一致（实际 msgID={replyMsgId}，说明拿到的不是本次请求的回包）");

            // 不可靠通道冒烟：发一帧 Datagram（服务端查不到 handler 会静默忽略，不回包、不断连）。
            // 消息号引用 EMsg 常量（4003 = EMsg.PushDataSync）：硬编码会与常量脱节，号一改用例照发旧号。
            var datagram = ClientFrame.Encode(0, EMsg.PushDataSync, Encoding.UTF8.GetBytes("{}"));
            _conn.SendUnreliable(datagram, 0, datagram.Length);
            yield return null;
            Assert.AreEqual(ConnectionState.Connected, _conn.State, "发 Datagram 不应影响连接状态");

            _conn.Disconnect();
            Game.Logger?.Info("QuicTest", "QUIC 请求/回包往返用例通过");
        }

        /// <summary>
        /// 全栈规划：<c>CloverNet.Init(tcp, udp)</c> 之后，原生平台应当**真的走 QUIC**
        /// （QUIC 排在主链，TCP 是它的降级链）。这条守住的是"接线"而不是"能连"。
        /// </summary>
        [UnityTest]
        [Timeout(90000)]
        public IEnumerator NetworkManager_PrefersQuic_OnNativePlatform()
        {
            if (!HasNativePlugin)
                Assert.Ignore("msquic 原生插件未提供本平台二进制");
            if (!E2EEnabled)
                Assert.Ignore("真实链路 QUIC 用例默认不跑：设 CLOVER_QUIC_E2E=1 后启动编辑器再执行");

            MsQuicRuntime.TryEnsureReady(out var reason);
            Assume.That(MsQuicRuntime.IsAvailable, Is.True, $"插件不可用: {reason}");

            // UseTls：本仓开发网关（clover-mmo-1/server/configs/all/server.yaml）TCP 口已走 TLS
            // （tcp_tls_disabled=false），降级链里的 TCP 线路必须同协议，否则一旦 QUIC 不可用必然连不上。
            // 记录"引擎是本用例启动的"：收尾（TearDownQuic）据此 Game.Shutdown 退回未启动态 ——
            // 不退回的话 IsRunning / Dispatcher / Net 全部残留给同进程后续用例，其 Launch 会变成 no-op。
            _launchedGame = !Game.IsRunning;
            if (!_launchedGame)
                Game.Logger?.Warn("QuicTest", "Game 已在运行（前一用例未收尾）：本用例不主动 Shutdown 它");

            Game.Launch(new GameConfig { LogDir = "logs", CallTimeoutSeconds = 5, MaxReconnectCount = 1, UseTls = true });
            CloverNet.Init(GatewayTcpAddr, GatewayQuicAddr);
            _touchedGameNet = true;

            yield return WaitFor(() => Game.Net != null && Game.Net.IsConnected, 15f,
                $"连接网关（QUIC {GatewayQuicAddr}，失败会退到 TCP {GatewayTcpAddr}）");

            var active = (Game.Net as NetworkManager)?.ActiveTransport;
            Assert.AreEqual(TransportKind.Quic, active,
                $"应当真的走 QUIC，实际={active}（若为 Tcp 说明 QUIC 被裁掉或握手失败后退化了）");

            Game.Net.Disconnect();
            _touchedGameNet = false;
        }
    }
}
