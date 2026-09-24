using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace CloverEngine
{
    /// <summary>
    /// WebSocket 长连接实现（服务端 <c>internal/transport/net/ws</c>）。
    ///
    /// 线格式（与 TCP 的差别都是「少一层封装」）：
    ///   - **一条二进制消息 = 一个客户端帧**（<c>[4B requestID][4B msgID][body]</c>）；
    ///     没有 TCP 的 <c>[1B type][4B len]</c> 传输层帧（消息边界由 WS 协议保证），
    ///     也没有裸 UDP 的 0x55 魔数；
    ///   - 仅接受 <see cref="WebSocketMessageType.Binary"/>；文本帧丢弃并告警（服务端同样如此）；
    ///   - 心跳用 WS 控制帧：<c>KeepAliveInterval</c> 会周期性发 **Ping** 帧（.NET 的
    ///     <c>ClientWebSocket</c> 在空闲时自行发送，对端以 Pong 应答；服务端收到探测帧刷新读超时），
    ///     等价于 TCP 侧的 ping 探测，因此这里不自行构造心跳数据帧——空二进制帧会被服务端
    ///     当成 0 长度客户端帧解析，属于脏流量。
    ///
    /// 线程模型与 <see cref="TcpConnection"/> 一致：连接/收/发在后台线程，主线程只做
    /// <see cref="Tick"/> 排空与代际校验。
    /// </summary>
    internal sealed class WebSocketConnection : ITransportConnection, IDisposable
    {
        /// <summary>
        /// 单条消息软上限 **10 MiB**（与服务端一致：TCP/WS/QUIC 统一 10MiB），超出仅告警。
        /// ⚠️ 该常量是对**跨端约定**的本地副本，改前先双端确认。
        /// </summary>
        private const int MaxMsgPayload = 10 << 20;

        /// <summary>
        /// 单条消息硬上限 **10 MiB**（与服务端一致：TCP/WS/QUIC 统一 10MiB），超出断线。
        /// （跨端口径同上：修改需双端确认。）
        /// </summary>
        private const int HardMaxMsgPayload = 10 << 20;

        /// <summary>发送线程空转等待间隔（毫秒）。</summary>
        private const int SendLoopWaitMs = 1000;

        /// <summary>接收缓冲块大小（单块，跨块由累加器拼接）。</summary>
        private const int RecvChunkSize = 16 * 1024;

        /// <summary>
        /// 收发队列各自允许积压的最大帧数（背压上限）。口径与 <see cref="TcpConnection"/> 完全一致
        /// （那里的 MaxQueuedFrames 也是 4096，**不要另立第二套数值**）：无界队列在「主线程 Tick
        /// 长时间停顿」或「链路半死但仍在 Enqueue」时会持续吃内存。超过上限即丢弃**最旧**的帧并
        /// 降频告警 —— 保留最新的帧更接近当前状态，被积压的旧帧在这种场景下基本已过期。
        /// </summary>
        private const int MaxQueuedFrames = 4096;

        /// <summary>队列丢弃累计次数，用于把告警降频（只在第 1 次与每 64 次时打印）。</summary>
        private int _queueDropCount;

        private ClientWebSocket _ws;
        private Thread _recvThread;
        private Thread _sendThread;
        private volatile bool _running;
        private readonly ConcurrentQueue<byte[]> _recvQueue = new();
        private readonly ConcurrentQueue<byte[]> _sendQueue = new();
        private readonly SemaphoreSlim _sendSignal = new(0, int.MaxValue);
        private string _url;

        /// <summary>连接代际：每次 ConnectAsync/Disconnect 自增，让旧代际的后台线程与挂起回调失效。</summary>
        private int _generation;

        /// <summary>
        /// 链路失败已上报标记（Interlocked 使用），防止重复回调。
        /// 语义是「**本次连接尝试**是否已上报过失败」：<see cref="ConnectAsync"/> 开始时清 0，
        /// <see cref="Disconnect"/> 与 <see cref="HandleLinkFailure"/> 置 1。原因见 TcpConnection 同名字段。
        /// </summary>
        private int _linkDown = 1;

        /// <summary>心跳间隔（毫秒），默认 15 秒；同时作为 <c>KeepAliveInterval</c>。</summary>
        public int HeartbeatIntervalMs = 15_000;

        /// <summary>连接超时（毫秒），默认 5 秒。</summary>
        public int ConnectTimeoutMs = 5_000;

        /// <summary>连接状态。</summary>
        public ConnectionState State { get; private set; } = ConnectionState.Disconnected;

        /// <summary>线路类型。</summary>
        public TransportKind Kind => TransportKind.WebSocket;

        /// <summary>连接建立成功回调（后台线程触发）。</summary>
        public event Action Connected;

        /// <summary>链路断开回调（后台线程触发），仅非主动断开时触发。</summary>
        public event Action Disconnected;

        /// <summary>
        /// 发起连接（非阻塞）。地址必须是完整 URL（<c>ws://host:port/ws</c> 或 <c>wss://...</c>）。
        /// 地址非法时同步抛 <see cref="FormatException"/>；WebGL 平台抛 <see cref="NotSupportedException"/>。
        /// </summary>
        /// <param name="addr">完整 WebSocket URL。</param>
        public void ConnectAsync(string addr)
        {
            if (Application.platform == RuntimePlatform.WebGLPlayer)
            {
                // 浏览器没有 BSD socket，ClientWebSocket 不可用；WebGL 需要 WebSocket 的 jslib 桥接
                // （或改用 WebTransport）。这里明确失败而不是让连接静默超时
                // （TransportCapabilities 也据此把 WebGL 的 WebSocket 判为不可用，避免"假可用"）。
                throw new NotSupportedException(
                    "WebSocket 在 WebGL 需要浏览器侧桥接（jslib）；当前引擎未提供，请改用原生平台或等待桥接落地");
            }

            // 先校验 URL：非法 URL 在动旧连接**之前**同步抛
            //（否则传入非法 URL 时原可用连接已被拆掉且无回滚）。
            if (!Uri.TryCreate(addr, UriKind.Absolute, out var uri) ||
                (uri.Scheme != "ws" && uri.Scheme != "wss"))
            {
                throw new FormatException($"invalid websocket url: {addr}");
            }

            var gen = Interlocked.Increment(ref _generation);
            DisconnectCore();
            // 本次尝试「尚未上报失败」：否则首次连不上时 HandleLinkFailure 会早退，
            // Disconnected 不回调 → NetworkManager 的降级链与退避重连全部失效。
            Interlocked.Exchange(ref _linkDown, 0);
            _url = addr;

            var ws = new ClientWebSocket();
            _ws = ws;
            State = ConnectionState.Connecting;

            // 服务端读超时为 2*heartbeat 且以「收到探测帧」刷新；KeepAliveInterval 会周期性发 **Ping**
            // （.NET 的 Keep-Alive 实际发 Ping 控制帧，不是 Pong），正好承担保活职责
            // （等价于 TCP 侧无发送时补 ping）。
            ws.Options.KeepAliveInterval = TimeSpan.FromMilliseconds(Math.Max(1000, HeartbeatIntervalMs));

            // 证书校验：wss 走系统默认信任链。§N7 要求引擎不提供「跳过校验」的开关，
            // 这里显式保持默认（不设 RemoteCertificateValidationCallback），防止后人误改成恒真委托。
            if (uri.Scheme == "wss")
                ws.Options.RemoteCertificateValidationCallback = null;

            Task.Run(() =>
            {
                try
                {
                    using (var cts = new CancellationTokenSource(Math.Max(1000, ConnectTimeoutMs)))
                    {
                        ws.ConnectAsync(uri, cts.Token).GetAwaiter().GetResult();
                    }
                }
                catch (Exception e)
                {
                    if (_generation == gen)
                        HandleLinkFailure(gen, $"ws connect failed: {e.Message}");
                    return;
                }

                if (_generation != gen)
                {
                    // 连接期间已被新的 ConnectAsync/Disconnect 取代，丢弃本次连接
                    SafeAbort(ws);
                    return;
                }

                try
                {
                    _linkDown = 0;
                    _running = true;
                    State = ConnectionState.Connected;

                    _recvThread = new Thread(() => RecvLoop(gen)) { IsBackground = true, Name = "CloverNet-WsRecv" };
                    _sendThread = new Thread(() => SendLoop(gen)) { IsBackground = true, Name = "CloverNet-WsSend" };
                    _recvThread.Start();
                    _sendThread.Start();

                    Game.Logger?.Info("Network", $"WS connected: {_url}");
                    // Connected 回调单独隔离：订阅方异常不允许被下面的 catch 当"连接失败"，
                    // 否则刚建立的 WS 链路会被拆掉并误报断开（Disconnected 侧已有同样隔离）。
                    try
                    {
                        Connected?.Invoke();
                    }
                    catch (Exception e)
                    {
                        Game.Logger?.Error("Network", $"ws connected callback error: {e.Message}", e);
                    }
                }
                catch (Exception e)
                {
                    HandleLinkFailure(gen, $"ws post-connect setup failed: {e.Message}");
                }
            });
        }

        /// <summary>用户主动断开：终止收发线程、释放连接、清空收发队列，不触发回调。</summary>
        public void Disconnect()
        {
            Interlocked.Increment(ref _generation);
            // 主动断开按「已上报」处理，压掉回调
            Interlocked.Exchange(ref _linkDown, 1);
            DisconnectCore();
        }

        /// <summary>
        /// 兜底释放（等价 <see cref="Disconnect"/>）。持有方应显式断开；若未断开就丢弃，
        /// 收发线程经闭包持有本对象与连接，GC 也回收不了 —— 本方法给这类使用一条显式释放通道。
        /// </summary>
        public void Dispose() => Disconnect();

        /// <summary>投递一帧（作为一条独立二进制消息写出）。</summary>
        /// <param name="data">源缓冲区。</param>
        /// <param name="offset">起始偏移。</param>
        /// <param name="count">字节数。</param>
        public void Send(byte[] data, int offset, int count)
        {
            if (State != ConnectionState.Connected)
            {
                Game.Logger?.Warn("Network", "ws send ignored: not connected");
                return;
            }

            var payload = new byte[count];
            Buffer.BlockCopy(data, offset, payload, 0, count);
            EnqueueBounded(_sendQueue, payload, "send");
            SignalSend();
        }

        /// <summary>收发与心跳都在后台线程自驱，主线程驱动点留空。</summary>
        public void Tick()
        {
        }

        /// <summary>取出一条完整客户端帧（WS 消息边界即帧边界，无需再拆包）。</summary>
        /// <param name="data">输出的帧字节。</param>
        public bool TryTakePacket(out byte[] data)
        {
            return _recvQueue.TryDequeue(out data);
        }

        /// <summary>
        /// 入队并施加背压（见 <see cref="MaxQueuedFrames"/>）：超出上限时丢弃**最旧**的帧。
        /// 丢弃必须留痕且降频，否则现场只表现为「莫名少收到消息」，无从判断是丢了还是没发。
        /// 与 <see cref="TcpConnection"/> 的 EnqueueBounded 同款（口径统一，勿另写一套）。
        /// </summary>
        private void EnqueueBounded(ConcurrentQueue<byte[]> queue, byte[] frame, string what)
        {
            queue.Enqueue(frame);
            var dropped = 0;
            while (queue.Count > MaxQueuedFrames && queue.TryDequeue(out _))
                dropped++;
            if (dropped == 0)
                return;
            var total = Interlocked.Add(ref _queueDropCount, dropped);
            if (total == dropped || total % 64 < dropped)
            {
                Game.Logger?.Warn("Network",
                    $"ws {what} queue exceeded {MaxQueuedFrames} frames, dropped {dropped} oldest (total={total}) — " +
                    "链路积压：检查主线程 Tick 是否被阻塞或对端是否停止读取");
            }
        }

        /// <summary>关闭连接并清空收发队列（不动代际与回调标记）。</summary>
        private void DisconnectCore()
        {
            State = ConnectionState.Disconnected;
            _running = false;
            SafeAbort(_ws);
            _ws = null;

            while (_recvQueue.TryDequeue(out _)) { }
            while (_sendQueue.TryDequeue(out _)) { }
            SignalSend(); // 唤醒可能阻塞在 Wait 的发送线程
        }

        /// <summary>安全中止连接：Abort 立即释放底层 socket，不等待对端 Close 握手。</summary>
        private static void SafeAbort(ClientWebSocket ws)
        {
            if (ws == null)
                return;
            try
            {
                ws.Abort();
            }
            catch (Exception e)
            {
                Game.Logger?.Warn("Network", $"ws abort failed: {e.Message}");
            }

            try
            {
                ws.Dispose();
            }
            catch (Exception e)
            {
                Game.Logger?.Warn("Network", $"ws dispose failed: {e.Message}");
            }
        }

        /// <summary>链路失败统一入口：按代际与一次性标记去重后关闭资源并回调。</summary>
        /// <param name="gen">触发时的连接代际。</param>
        /// <param name="reason">失败原因（写入日志）。</param>
        private void HandleLinkFailure(int gen, string reason)
        {
            if (_generation != gen)
                return;
            if (Interlocked.Exchange(ref _linkDown, 1) == 1)
                return;

            DisconnectCore();
            Game.Logger?.Info("Network", $"WS link down: {reason}");
            try
            {
                Disconnected?.Invoke();
            }
            catch (Exception e)
            {
                Game.Logger?.Error("Network", $"ws disconnected callback error: {e.Message}", e);
            }
        }

        /// <summary>
        /// 收包循环：跨块拼接一条完整二进制消息后入队；文本帧丢弃并告警。
        /// 仅当代际未变化时入队，避免旧会话残留帧污染新会话。
        /// </summary>
        /// <param name="gen">连接代际。</param>
        private void RecvLoop(int gen)
        {
            var buffer = new byte[RecvChunkSize];
            var acc = new MemoryStream();
            try
            {
                while (_running && _generation == gen)
                {
                    WebSocketReceiveResult result;
                    try
                    {
                        result = _ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None)
                            .GetAwaiter().GetResult();
                    }
                    catch (Exception e)
                    {
                        // 收包失败的具体原因（关闭码/协议错）必须留痕
                        if (_running && _generation == gen)
                            HandleLinkFailure(gen, $"ws recv failed: {e.GetType().Name}: {e.Message}");
                        return;
                    }

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        if (_running && _generation == gen)
                            HandleLinkFailure(gen, $"ws closed by peer: {result.CloseStatus}");
                        return;
                    }

                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        // 与服务端 readLoop 行为一致：只支持二进制，文本帧丢弃并留痕便于排障
                        Game.Logger?.Warn("Network", "ws text message received, only binary supported, dropped");
                        acc.SetLength(0);
                        continue;
                    }

                    acc.Write(buffer, 0, result.Count);
                    // 增量校验硬上限：不能等整条消息拼完再查（对端发超大或永不结束的分片流会先把
                    // 内存耗光，10 MiB 硬上限失去防护意义）。超限立即断链。
                    if (acc.Length > HardMaxMsgPayload)
                    {
                        HandleLinkFailure(gen,
                            $"ws message too large: {acc.Length} bytes (hard limit {HardMaxMsgPayload})");
                        return;
                    }

                    if (!result.EndOfMessage)
                        continue;

                    var message = acc.ToArray();
                    acc.SetLength(0);
                    if (message.Length == 0)
                        continue;

                    if (message.Length > MaxMsgPayload)
                        Game.Logger?.Warn("Network", $"ws message exceeds soft limit: {message.Length}");

                    if (_generation == gen)
                        EnqueueBounded(_recvQueue, message, "recv");
                }
            }
            finally
            {
                // 循环退出（断线/重连/异常）时释放累加器：否则每次连接都残留一个托管流
                acc.Dispose();
            }
        }

        /// <summary>发送循环：串行写出待发消息（ClientWebSocket 禁止并发 Send）。</summary>
        /// <param name="gen">连接代际。</param>
        private void SendLoop(int gen)
        {
            try
            {
                while (_running && _generation == gen)
                {
                    _sendSignal.Wait(SendLoopWaitMs);

                    while (_running && _generation == gen && _sendQueue.TryDequeue(out var message))
                    {
                        if (!WriteMessage(message))
                        {
                            HandleLinkFailure(gen, "ws send failed");
                            return;
                        }
                    }
                }
            }
            catch
            {
                // 退出阶段连接已被并发关闭，按链路失败收尾
                HandleLinkFailure(gen, "ws send loop aborted");
            }
        }

        /// <summary>同步写出一条二进制消息（发送线程是唯一写者，无需加锁）。</summary>
        /// <param name="message">消息体。</param>
        private bool WriteMessage(byte[] message)
        {
            try
            {
                _ws.SendAsync(new ArraySegment<byte>(message), WebSocketMessageType.Binary, true,
                        CancellationToken.None)
                    .GetAwaiter().GetResult();
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>唤醒发送线程；信号量已满时忽略（发送线程必然已被唤醒）。</summary>
        private void SignalSend()
        {
            try
            {
                _sendSignal.Release();
            }
            catch (SemaphoreFullException)
            {
            }
        }
    }
}
