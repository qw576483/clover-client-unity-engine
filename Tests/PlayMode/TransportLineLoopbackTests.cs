using System;
using System.Collections;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CloverEngine.Tests
{
    /// <summary>
    /// 传输线路的真实链路回环测试（PlayMode）：补齐 <c>TcpLoopbackTests</c>（TCP）与
    /// <c>WebSocketLoopbackTests</c>（WS）之外的线路与生命周期场景 ——
    /// 裸 UDP、TLS 语义、断线检测、首连失败、降级换线、主动推送。
    ///
    /// 与已有两条测试的分工：已有的是「一条线路能通」，本文件是「线路的**边界行为**对」。
    /// </summary>
    public class TransportLineLoopbackTests
    {
        /// <summary>
        /// 每条用例开始前复位家族放行开关：**默认必须关**，
        /// 因为本文件里有用例要验证的正是「原生平台不得跨家族换线路」。
        /// 需要跑 Web 家族线路的用例自己调 <see cref="AllowWebFamily"/>。
        /// </summary>
        [SetUp]
        public void SetUp() => TransportCapabilities.AllowCrossFamilyForTesting = false;

        /// <summary>用例结束后复位，避免污染同进程内其它用例。</summary>
        [TearDown]
        public void TearDown() => TransportCapabilities.AllowCrossFamilyForTesting = false;

        /// <summary>
        /// 显式放行 Web 家族线路：WebSocket 属 Web 家族，在 Editor（原生平台）上默认被裁剪，
        /// 而它的实现只能在 Editor 验证（WebGL 跑不了 headless 测试）。
        /// 仅测试用，见 <c>TransportCapabilities.AllowCrossFamilyForTesting</c>。
        /// </summary>
        private static void AllowWebFamily() => TransportCapabilities.AllowCrossFamilyForTesting = true;

        /// <summary>
        /// 等待条件成立，超时即失败。PlayMode 下用 <see cref="Time.realtimeSinceStartup"/> 计时。
        /// <para>
        /// ★ 谓词**只求值一次**（结果存变量，断言用它）：条件允许带副作用（如 <c>TryTakePacket</c> 出队），
        /// 循环里成功那次已把结果取走，断言时再求值会变 false ⇒ 明明成功却报"等待超时"。
        /// </para>
        /// </summary>
        private static IEnumerator WaitUntil(Func<bool> condition, float timeoutSeconds, string what)
        {
            var deadline = Time.realtimeSinceStartup + timeoutSeconds;
            var ok = condition();
            while (!ok && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
                ok = condition();
            }
            Assert.IsTrue(ok, $"等待超时（{timeoutSeconds}s）：{what}");
        }

        /// <summary>
        /// 等待条件成立，**每帧额外泵一次 <paramref name="pump"/>**。
        ///
        /// 收包不是自动的：<c>NetworkManager</c> 的收发队列只在 <c>Tick()</c> 里被排空
        /// （<c>Tick → TryTakePacket → DrainFrame</c>），生产环境由 <c>Game.Tick</c> 驱动。
        /// 测试里若只 <c>yield return null</c>，收到的帧会一直躺在队列里，
        /// 表现为「连不上 / 没回包 / 推送没到」——那是**脚手架**缺陷，不是链路问题。
        /// </summary>
        private static IEnumerator WaitUntilPumping(Func<bool> condition, Action pump, float timeoutSeconds, string what)
        {
            var deadline = Time.realtimeSinceStartup + timeoutSeconds;

            // ★ 同 WaitUntil：谓词只求值一次做断言（可能带副作用，不能二次求值）。
            var ok = condition();
            while (!ok && Time.realtimeSinceStartup < deadline)
            {
                pump();
                yield return null;
                ok = condition();
            }
            pump();
            Assert.IsTrue(ok, $"等待超时（{timeoutSeconds}s）：{what}");
        }

        /// <summary>取一个空闲端口：绑 0 让系统分配后立刻释放。仅用于「需要一个没人监听的口」的场景。</summary>
        private static int GetFreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        /// <summary>取一个**确定无人监听**的端口：绑 0 → 读端口 → 立刻关闭，连接它必然被拒。</summary>
        private static int GetDeadPort()
        {
            return GetFreePort();
        }

        // ───────────────────────── 断线检测与首连失败 ─────────────────────────

        /// <summary>
        /// 首连失败必须上报 <c>Disconnected</c> 且状态落到 <c>Disconnected</c>。
        ///
        /// 回归用例：<c>_linkDown</c> 初值曾为 1（「未连接视为已上报」），而它只在**连接成功之后**
        /// 才被清 0，于是「第一次就连不上」这条路径会被 <c>HandleLinkFailure</c> 的
        /// <c>Interlocked.Exchange(ref _linkDown, 1) == 1</c> 早退掉 —— 不回调、State 卡在
        /// <c>Connecting</c>、socket 不释放。NetworkManager 的降级换线与退避重连**完全挂在这个回调上**，
        /// 所以这个标记必须在每次 <c>ConnectAsync</c> 开始时清 0。
        /// </summary>
        [UnityTest]
        public IEnumerator TcpConnection_ConnectToClosedPort_ReportsDisconnectedOnce()
        {
            var deadPort = GetDeadPort();

            var conn = new TcpConnection { ConnectTimeoutMs = 2000 };
            var downCount = 0;
            conn.Disconnected += () => Interlocked.Increment(ref downCount);

            conn.ConnectAsync($"127.0.0.1:{deadPort}");
            Assert.AreEqual(ConnectionState.Connecting, conn.State, "刚发起连接应为 Connecting");

            yield return WaitUntil(() => Volatile.Read(ref downCount) > 0, 8f, "首连失败应回调 Disconnected");

            // 再等一会，确认没有第二次回调（标记一次性）
            yield return new WaitForSeconds(0.3f);
            Assert.AreEqual(1, Volatile.Read(ref downCount), "Disconnected 只应回调一次");
            Assert.AreEqual(ConnectionState.Disconnected, conn.State, "失败后状态必须回到 Disconnected，不能卡在 Connecting");
        }

        /// <summary>
        /// 服务端主动关闭连接：客户端应检测到并**只回调一次** Disconnected，状态回到 Disconnected。
        /// 覆盖「已建立连接后的断线检测」路径（与首连失败是两条不同的代码路径）。
        /// </summary>
        [UnityTest]
        public IEnumerator TcpConnection_ServerClose_ReportsDisconnectedOnce()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;

            var serverTask = Task.Run(async () =>
            {
                var client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                // 接受了就立刻关闭：客户端 RecvLoop 读到 0 字节 → 链路失败
                client.Close();
            });

            TcpConnection conn = null;
            try
            {
                conn = new TcpConnection { ConnectTimeoutMs = 3000 };
                var upCount = 0;
                var downCount = 0;
                conn.Connected += () => Interlocked.Increment(ref upCount);
                conn.Disconnected += () => Interlocked.Increment(ref downCount);

                conn.ConnectAsync($"127.0.0.1:{port}");

                yield return WaitUntil(() => Volatile.Read(ref downCount) > 0, 8f, "服务端关闭后应回调 Disconnected");

                yield return new WaitForSeconds(0.3f);
                Assert.AreEqual(1, Volatile.Read(ref upCount), "连接建立回调应恰好一次");
                Assert.AreEqual(1, Volatile.Read(ref downCount), "Disconnected 只应回调一次（_linkDown 幂等）");
                Assert.AreEqual(ConnectionState.Disconnected, conn.State);
            }
            finally
            {
                // 清理必须走 finally：任一断言失败也要断开，否则漏下的收发线程与 socket 污染后续用例。
                conn?.Disconnect();
                listener.Stop();
            }
        }

        /// <summary>
        /// 用户主动 <c>Disconnect</c> **不得**触发 <c>Disconnected</c>：
        /// 主动断开与链路意外断开必须有区别，否则业务会把「我关的」当成「掉线了」而去重连。
        /// </summary>
        [UnityTest]
        public IEnumerator TcpConnection_UserDisconnect_DoesNotReport()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;

            var serverTask = Task.Run(async () =>
            {
                var client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                await Task.Delay(3000).ConfigureAwait(false);
                client.Close();
            });

            TcpConnection conn = null;
            try
            {
                conn = new TcpConnection { ConnectTimeoutMs = 3000 };
                var upCount = 0;
                var downCount = 0;
                conn.Connected += () => Interlocked.Increment(ref upCount);
                conn.Disconnected += () => Interlocked.Increment(ref downCount);

                conn.ConnectAsync($"127.0.0.1:{port}");
                yield return WaitUntil(() => Volatile.Read(ref upCount) > 0, 8f, "应建立连接");

                // 这一句是**用例动作**（不是收尾），必须留在断言之前
                conn.Disconnect();
                yield return new WaitForSeconds(0.4f);

                Assert.AreEqual(0, Volatile.Read(ref downCount), "主动断开不应回调 Disconnected");
                Assert.AreEqual(ConnectionState.Disconnected, conn.State);
            }
            finally
            {
                // 断言中途失败时的兜底清理（Disconnect 幂等：正常路径已断开，这里提前 return 代价为零）。
                conn?.Disconnect();
                listener.Stop();
            }
        }

        // ───────────────────────── 裸 UDP ─────────────────────────

        /// <summary>
        /// <c>UdpConnection</c> 回环：发出的报文带 <c>0x55</c> 魔数、收到的报文剥掉魔数；
        /// **不带魔数的报文必须丢弃**（否则会被当成客户端帧解析，产生随机垃圾帧）。
        /// </summary>
        [UnityTest]
        public IEnumerator RawUdp_EchoLoopback_StripsMagic_AndDropsNonMagic()
        {
            var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            var port = ((IPEndPoint)server.Client.LocalEndPoint).Port;

            var serverTask = Task.Run(async () =>
            {
                // 第一条：客户端发来的帧
                var recv = await server.ReceiveAsync().ConfigureAwait(false);
                var ep = recv.RemoteEndPoint;

                // 服务端先回一条**不带魔数**的报文：客户端应当丢弃
                server.Send(new byte[] { 0x01, 0x02, 0x03 }, 3, ep);
                await Task.Delay(150).ConfigureAwait(false);

                // 再回带魔数的报文：客户端应剥掉首字节后入队。
                //
                // 注意这里**必须先剥掉客户端自己那层魔数**：UdpConnection.Send 会自动加 0x55，
                // 所以服务端收到的已经是 [0x55][客户端帧]。把整包再套一层魔数会得到
                // [0x55][0x55][帧]，而客户端只剥一层 —— 测试就会拿到带魔数的脏数据
                // （这正是本用例第一次跑起来时报的 "Expected 16 bytes, actual 17"）。
                var echo = new byte[recv.Buffer.Length];
                echo[0] = 0x55;
                Buffer.BlockCopy(recv.Buffer, 1, echo, 1, recv.Buffer.Length - 1);
                server.Send(echo, echo.Length, ep);
            });

            UdpConnection conn = null;
            try
            {
                conn = new UdpConnection();
                conn.Connect($"127.0.0.1:{port}");
                Assert.AreEqual(ConnectionState.Connected, conn.State);
                Assert.AreEqual(TransportKind.RawUdp, conn.Kind, "裸 UDP 线路类型");

                var frame = ClientFrame.Encode(0u, EMsg.PushAlert, Encoding.UTF8.GetBytes("udp-ping"));
                conn.Send(frame, 0, frame.Length);

                byte[] received = null;
                var deadline = Time.realtimeSinceStartup + 6f;
                while (received == null && Time.realtimeSinceStartup < deadline)
                {
                    conn.Tick();
                    conn.TryTakePacket(out received);
                    if (received == null) yield return null;
                }

                Assert.IsNotNull(received, "带魔数的回包应到达");
                // 若不带魔数的那条没被丢弃，这里会拿到 {1,2,3}
                CollectionAssert.AreEqual(frame, received, "收到的必须是剥掉 0x55 魔数后的原始客户端帧");
            }
            finally
            {
                // 清理必须走 finally：任一断言失败也要关闭，否则漏下的 socket 污染后续用例。
                conn?.Disconnect();
                server.Close();
            }
        }

        // ───────────────────────── TLS 语义 ─────────────────────────

        /// <summary>
        /// 对**明文**服务端开 <c>UseTls</c>：握手必须失败。
        /// 这条断言守的是「TLS 开关真的生效」—— 如果 <c>UseTls</c> 被忽略（例如封装代码被删掉、
        /// 或异常被吞），明文服务端会「连上」，测试就会红。
        /// </summary>
        [UnityTest]
        public IEnumerator TcpConnection_UseTls_AgainstPlainServer_FailsHandshake()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;

            var serverTask = Task.Run(async () =>
            {
                var client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                using (var stream = client.GetStream())
                {
                    var buf = new byte[4096];
                    while (true)
                    {
                        int n;
                        try { n = await stream.ReadAsync(buf, 0, buf.Length).ConfigureAwait(false); }
                        catch { break; }
                        if (n <= 0) break;
                        try { await stream.WriteAsync(buf, 0, n).ConfigureAwait(false); }
                        catch { break; }
                    }
                }
                client.Close();
            });

            try
            {
                var conn = new TcpConnection { UseTls = true, ConnectTimeoutMs = 3000 };
                var downCount = 0;
                conn.Disconnected += () => Interlocked.Increment(ref downCount);

                conn.ConnectAsync($"127.0.0.1:{port}");

                yield return WaitUntil(() => Volatile.Read(ref downCount) > 0, 12f, "明文服务端上的 TLS 握手应失败");
                Assert.AreEqual(ConnectionState.Disconnected, conn.State,
                    "TLS 握手失败绝不能落到 Connected（否则就是静默降级成明文）");
            }
            finally
            {
                listener.Stop();
            }
        }

        /// <summary>
        /// 服务端出示**自签证书**时，客户端必须握手失败。
        ///
        /// 这是 §N7「引擎不提供跳过证书校验的开关」的行为级守门：引擎走系统默认信任链，
        /// 自签证书只能由使用者**导入系统信任链**来放行，客户端代码里没有任何放行的口子。
        /// 本用例的证书 CN/SAN 都指向 127.0.0.1，即**唯一**可能的失败原因就是「签发链不受信任」。
        /// </summary>
        [UnityTest]
        public IEnumerator TcpConnection_UseTls_AgainstSelfSignedServer_FailsHandshake()
        {
            var cert = CreateSelfSignedCert(out var error);
            if (cert == null)
            {
                Assert.Ignore($"本平台无法生成自签证书，跳过该用例：{error}");
                yield break;
            }

            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;

            var serverTask = Task.Run(async () =>
            {
                var client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                try
                {
                    using (var ssl = new SslStream(client.GetStream(), false))
                    {
                        ssl.AuthenticateAsServer(cert, false, SslProtocols.None, false);
                        // 握手成功也保持连接：让客户端自己完成证书校验判定
                        await Task.Delay(1500).ConfigureAwait(false);
                    }
                }
                catch
                {
                    // 客户端拒绝证书时服务端侧也会抛，属预期
                }
                client.Close();
            });

            try
            {
                var conn = new TcpConnection { UseTls = true, ConnectTimeoutMs = 3000 };
                var downCount = 0;
                conn.Disconnected += () => Interlocked.Increment(ref downCount);

                conn.ConnectAsync($"127.0.0.1:{port}");

                yield return WaitUntil(() => Volatile.Read(ref downCount) > 0, 12f, "自签证书应被拒绝");
                Assert.AreEqual(ConnectionState.Disconnected, conn.State,
                    "自签证书必须被拒绝：引擎不提供任何跳过证书校验的开关");
            }
            finally
            {
                listener.Stop();
            }
        }

        /// <summary>
        /// 生成一张 CN/SAN 均为 127.0.0.1 的自签证书。
        /// 失败（部分平台不支持运行时生成）时返回 null，由调用方 <c>Assert.Ignore</c> 跳过。
        /// </summary>
        private static X509Certificate2 CreateSelfSignedCert(out string error)
        {
            error = null;
            try
            {
                using (var rsa = RSA.Create(2048))
                {
                    var req = new CertificateRequest("CN=127.0.0.1", rsa,
                        HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                    req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
                    req.CertificateExtensions.Add(new X509KeyUsageExtension(
                        X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
                    req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                        new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));

                    var san = new SubjectAlternativeNameBuilder();
                    san.AddIpAddress(IPAddress.Loopback);
                    req.CertificateExtensions.Add(san.Build());

                    var selfSigned = req.CreateSelfSigned(
                        DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

                    // 自签证书的私钥句柄是临时的，做服务端认证时部分平台会不认；
                    // 过一次 PFX 落成可用句柄。
                    return new X509Certificate2(selfSigned.Export(X509ContentType.Pfx));
                }
            }
            catch (Exception e)
            {
                error = e.Message;
                return null;
            }
        }

        // ───────────────────────── 降级换线 ─────────────────────────

        /// <summary>
        /// **原生平台不得跨家族换线路**：TCP（原生家族主链）连不上就是网络错误，
        /// 不能悄悄退到 WebSocket。
        ///
        /// 依据架构 §N10 —— 两个家族互斥：原生 QUIC → TCP + RawUDP；Web WT → WS。
        /// 跨家族退到 WS 换掉的不只是协议，还有**通道语义**：WS 没有真正的不可靠语义，
        /// SendUnreliable 会退化成可靠发送，实时同步会吃满队头阻塞，而调用方完全看不出来。
        ///
        /// 本用例在 Editor（原生平台）上给一条「TCP 死端口 + WS 真服务端」的计划：
        /// 断言 WS 那条**被平台裁掉**（不出现在计划里），且最终**没有**连上任何线路。
        /// </summary>
        [UnityTest]
        public IEnumerator NetworkManager_NativePlatform_DoesNotFallBackToWebSocket()
        {
            // 起一个**活的** WS 服务端：它是「万一实现错误地跨家族退到 WS」时的诱饵 ——
            // 服务端真在监听，退过去就会连上，这条断言才拦得住。
            var wsServer = MiniWsServer.HoldOnly();

            NetworkManager nm = null;
            try
            {
                var deadPort = GetDeadPort();
                nm = new NetworkManager(new GameConfig { CallTimeoutSeconds = 10, MaxReconnectCount = 5 });

                nm.Connect(TransportOptions.From($"127.0.0.1:{deadPort}", null, $"127.0.0.1:{wsServer.Port}", "/ws"));

                // 计划里不该有 ws —— 它属于 Web 家族，在原生平台上被裁掉（日志会记一条裁剪说明）
                StringAssert.DoesNotContain("ws:", nm.LinePlan,
                    "原生平台不得把 WebSocket 纳入线路计划（那是跨家族，不是降级）");

                // 给足时间：若实现错误地跨家族退到 WS，这里会连上（本地 WS 服务端是活的）
                yield return new WaitForSeconds(4f);

                Assert.IsFalse(nm.IsConnected, "TCP 连不上就该是失败，不应跨家族连上 WebSocket");
                Assert.AreNotEqual(TransportKind.WebSocket, nm.ActiveTransport);
            }
            finally
            {
                // 清理必须走 finally：任一断言失败也要断开，否则漏下的重连定时器 / socket 污染后续用例。
                nm?.Disconnect();
                wsServer.Dispose();
            }
        }

        // ───────────────────────── 主动推送（服务端 → 客户端） ─────────────────────────

        /// <summary>
        /// TCP 线路上的主动推送（requestID == 0，msgID = EMsg.PushAlert）应路由到业务处理器。
        /// 已有测试只覆盖「请求 → 回包」方向，推送是另一条分支（不查配对表、直接分发）。
        /// </summary>
        [UnityTest]
        public IEnumerator NetworkManager_PushOverTcp_RoutedToHandler()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;

            var body = Serializer.Serialize(new EAlertNotify { title = "tcp", content = "push-over-tcp" });
            var frame = ClientFrame.Encode(0u, EMsg.PushAlert, body);

            var serverTask = Task.Run(async () =>
            {
                var client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                using (var stream = client.GetStream())
                {
                    // 传输层帧：[1B type=0][4B 大端 len][客户端帧]
                    var wire = new byte[5 + frame.Length];
                    wire[0] = 0;
                    BigEndian.WriteUInt32(wire, 1, (uint)frame.Length);
                    Buffer.BlockCopy(frame, 0, wire, 5, frame.Length);
                    await stream.WriteAsync(wire, 0, wire.Length).ConfigureAwait(false);
                    await Task.Delay(4000).ConfigureAwait(false);
                }
                client.Close();
            });

            NetworkManager nm = null;
            try
            {
                nm = new NetworkManager(new GameConfig { CallTimeoutSeconds = 10, MaxReconnectCount = 5 });
                EAlertNotify pushed = null;
                nm.OnMsg(EMsg.PushAlert, ctx => pushed = ctx.Bind<EAlertNotify>());

                nm.Connect($"127.0.0.1:{port}");
                yield return WaitUntilPumping(() => nm.IsConnected, nm.Tick, 8f, "TCP 应连上");

                // 必须泵 Tick：推送帧进来后要经 Tick → TryTakePacket → DrainFrame 才会路由到处理器
                yield return WaitUntilPumping(() => pushed != null, nm.Tick, 8f, "TCP 推送应到达业务处理器");
                Assert.AreEqual("push-over-tcp", pushed.content);
            }
            finally
            {
                // 清理必须走 finally：任一断言失败也要断开，否则漏下的连接 / 定时器污染后续用例。
                nm?.Disconnect();
                listener.Stop();
            }
        }

        /// <summary>
        /// WS 线路上的主动推送同样应路由到处理器 —— 证明「换线路不换语义」。
        /// WebSocket 的一条二进制消息就是一个客户端帧（无长度前缀、无魔数）。
        /// </summary>
        [UnityTest]
        public IEnumerator NetworkManager_PushOverWebSocket_RoutedToHandler()
        {
            AllowWebFamily(); // Editor 是原生平台，显式放行 Web 家族线路（仅测试）

            var body = Serializer.Serialize(new EAlertNotify { title = "ws", content = "push-over-ws" });
            var frame = ClientFrame.Encode(0u, EMsg.PushAlert, body);
            // 一条二进制消息 = 一个客户端帧：握手完成后立刻推，随后挂住连接
            var server = MiniWsServer.PushThenHold(frame);

            NetworkManager nm = null;
            try
            {
                nm = new NetworkManager(new GameConfig { CallTimeoutSeconds = 10, MaxReconnectCount = 5 });
                EAlertNotify pushed = null;
                nm.OnMsg(EMsg.PushAlert, ctx => pushed = ctx.Bind<EAlertNotify>());

                nm.Connect(TransportOptions.From(null, null, $"127.0.0.1:{server.Port}", "/ws"));
                yield return WaitUntilPumping(() => nm.IsConnected, nm.Tick, 8f, "WS 应连上");

                yield return WaitUntilPumping(() => pushed != null, nm.Tick, 8f, "WS 推送应到达业务处理器");
                Assert.AreEqual("push-over-ws", pushed.content);
            }
            finally
            {
                // 清理必须走 finally：任一断言失败也要断开，否则漏下的连接 / 定时器污染后续用例。
                nm?.Disconnect();
                server.Dispose();
            }
        }
    }
}
