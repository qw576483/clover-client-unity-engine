using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace CloverEngine
{
    /// <summary>
    /// 大端字节序读写工具。线协议中所有多字节整数（requestID/msgID/帧长度等）均为大端，
    /// 与服务端 Go binary.BigEndian 对齐；不能使用 BitConverter（小端平台会写出错误字节序）。
    /// </summary>
    internal static class BigEndian
    {
        /// <summary>将 uint 以大端写入缓冲区指定偏移处</summary>
        public static void WriteUInt32(byte[] buf, int offset, uint value)
        {
            buf[offset] = (byte)(value >> 24);
            buf[offset + 1] = (byte)(value >> 16);
            buf[offset + 2] = (byte)(value >> 8);
            buf[offset + 3] = (byte)value;
        }

        /// <summary>从缓冲区指定偏移处以大端读取 uint</summary>
        public static uint ReadUInt32(byte[] buf, int offset)
        {
            return ((uint)buf[offset] << 24)
                 | ((uint)buf[offset + 1] << 16)
                 | ((uint)buf[offset + 2] << 8)
                 | buf[offset + 3];
        }
    }

    /// <summary>
    /// "host:port" 地址解析（传输层各线路共用）：支持主机名、IPv4 与**带方括号的 IPv6 字面量**
    /// （<c>[::1]:8002</c>）。
    ///
    /// <para>
    /// 为什么不用 <c>addr.Split(':')</c>：IPv6 地址本身含多个冒号，按冒号切分要么直接抛
    /// FormatException（<c>[::1]:8002</c> 被切成多段），要么产出垃圾 host（<c>fe80::1</c> 被切成
    /// <c>"fe80:"</c> + <c>"1"</c>）。IPv6 字面量必须带方括号，无括号的多冒号一律判非法。
    /// </para>
    /// </summary>
    internal static class NetAddr
    {
        /// <summary>解析 <c>host:port</c>；格式非法返回 false。</summary>
        /// <param name="addr">"host:port" 或 "[ipv6]:port" 形式</param>
        /// <param name="host">输出的主机名 / IP 字面量（IPv6 已剥去方括号）</param>
        /// <param name="port">输出的端口（1~65535）</param>
        public static bool TryParse(string addr, out string host, out int port)
        {
            host = null;
            port = 0;
            if (string.IsNullOrEmpty(addr))
                return false;

            string hostPart;
            string portPart;
            if (addr[0] == '[')
            {
                // [ipv6]:port 形式
                var close = addr.IndexOf(']');
                if (close < 0 || close + 2 > addr.Length || addr[close + 1] != ':')
                    return false;
                hostPart = addr.Substring(1, close - 1);
                portPart = addr.Substring(close + 2);
            }
            else
            {
                var colon = addr.LastIndexOf(':');
                if (colon <= 0 || colon == addr.Length - 1)
                    return false;
                hostPart = addr.Substring(0, colon);
                // 无括号的多冒号（如 "fe80::1"）不是合法 host:port：显式拒绝而不是产出垃圾 host
                if (hostPart.IndexOf(':') >= 0)
                    return false;
                portPart = addr.Substring(colon + 1);
            }

            if (hostPart.Length == 0)
                return false;
            if (!int.TryParse(portPart, out port) || port < 1 || port > 65535)
                return false;

            host = hostPart;
            return true;
        }
    }

    /// <summary>
    /// 客户端帧编解码：[4B requestID][4B msgID][JSON body]，全部大端。
    /// 约定：requestID != 0 为请求/回包（按 ID 配对），requestID == 0 为推送或不可靠消息；
    /// 回包 msgID 恒为 0，错误回包 msgID 为 EMsg.Error。
    /// </summary>
    internal static class ClientFrame
    {
        /// <summary>帧头长度：requestID(4) + msgID(4)</summary>
        public const int HeaderLen = 8;

        /// <summary>
        /// 单帧 JSON 体上限 **10 MiB**，与服务端一致（TCP/WS/QUIC 统一 10MiB）。
        /// ⚠️ 帧/体上限常量（本类 / <see cref="TcpConnection"/> 的 MaxFramePayload /
        /// WebSocketConnection / QuicConnection.MaxFrameSize）是各线路对**跨端约定**的独立副本，
        /// 数值须与对应服务端配置（max_frame_size / maxQUICFrameSize 等）对齐：改前先双端确认，
        /// 不要只改一处。三条线路的帧上限现已统一为 10MiB（服务端同步改）。
        /// </summary>
        public const int MaxBodySize = 10 << 20;

        /// <summary>编码完整客户端帧（帧头 + body）</summary>
        /// <exception cref="ArgumentException">body 超过 <see cref="MaxBodySize"/> 时抛出</exception>
        public static byte[] Encode(uint requestID, uint msgID, byte[] body)
        {
            // 与服务端网关同一条上限：本地先拦下来。
            // 否则超限帧发出去只会被服务端拒绝/断连，客户端只能看到「连接已断开」，
            // 定位成本远高于在这里直接抛一条写明字节数与消息号的信息。
            if (body != null && body.Length > MaxBodySize)
            {
                throw new ArgumentException(
                    $"client frame body too large: {body.Length} bytes > {MaxBodySize} (msgID={msgID})",
                    nameof(body));
            }

            var packet = new byte[HeaderLen + (body?.Length ?? 0)];
            BigEndian.WriteUInt32(packet, 0, requestID);
            BigEndian.WriteUInt32(packet, 4, msgID);
            if (body != null && body.Length > 0)
                Buffer.BlockCopy(body, 0, packet, HeaderLen, body.Length);
            return packet;
        }

        /// <summary>
        /// 尝试解码客户端帧
        /// </summary>
        /// <param name="data">完整客户端帧字节（含 8B 帧头）</param>
        /// <param name="requestID">输出请求 ID</param>
        /// <param name="msgID">输出消息 ID</param>
        /// <param name="bodyOffset">输出 body 起始偏移</param>
        /// <returns>帧头合法返回 true；长度不足返回 false</returns>
        public static bool TryDecode(byte[] data, out uint requestID, out uint msgID, out int bodyOffset)
        {
            requestID = msgID = 0;
            bodyOffset = 0;
            if (data == null || data.Length < HeaderLen)
                return false;
            requestID = BigEndian.ReadUInt32(data, 0);
            msgID = BigEndian.ReadUInt32(data, 4);
            bodyOffset = HeaderLen;
            return true;
        }
    }

    /// <summary>
    /// 连接状态。
    /// </summary>
    public enum ConnectionState
    {
        /// <summary>尚未发起连接或已断开</summary>
        Disconnected,
        /// <summary>连接握手进行中</summary>
        Connecting,
        /// <summary>链路已建立，可收发数据</summary>
        Connected,
    }

    /// <summary>
    /// TCP 长连接实现，传输层帧格式：[1B type][4B 大端 len][payload]。
    /// type 约定：0=数据帧（payload 为客户端帧） 1=ping 2=pong；类型 3（原 migrate「连接迁移令牌帧」）
    /// **两端**都按「未知帧类型」处理（服务端 tcp/codec.go 无 frameTypeMigrate，整条迁移链路不可达）。
    /// 单帧 payload 软上限 10MiB、硬上限 10MiB（与服务端一致：TCP/WS/QUIC 统一 10MiB）。
    /// 收发与心跳均由后台线程承担（非阻塞连接 + 15 秒无发送自动 ping + 收 ping 回 pong），
    /// 即使主线程挂起（OnApplicationPause）心跳也不会停发，避免被服务端读超时踢除。
    /// 链路异常通过 Connected/Disconnected 回调上抛（回调可能来自后台线程，消费方自行切主线程）。
    /// </summary>
    internal class TcpConnection : ITransportConnection, IDisposable
    {
        private const byte FrameTypeData = 0;
        private const byte FrameTypePing = 1;
        private const byte FrameTypePong = 2;

        /// <summary>
        /// 传输层帧 payload 软上限 **10 MiB**，与服务端一致（TCP/WS/QUIC 统一 10MiB），超出仅告警。
        /// ⚠️ payload = 8 字节客户端帧头 + body：本值与 <see cref="ClientFrame.MaxBodySize"/> 同值时，
        /// 按最大合法 body 组出的 payload 会略超本软限（软限路径只告警、不放行值变动）。
        /// 本常量是**跨端约定**的本地副本，改动需双端确认。
        /// </summary>
        private const int MaxFramePayload = 10 << 20;

        /// <summary>
        /// 传输层帧 payload 硬上限 **10 MiB**（与服务端一致：TCP/WS/QUIC 统一 10MiB），超出直接断线防攻击。
        /// </summary>
        private const int HardMaxFramePayload = 10 << 20;

        /// <summary>发送线程空转等待间隔（毫秒），兼作心跳检查周期</summary>
        private const int SendLoopWaitMs = 1000;

        /// <summary>
        /// 收发队列各自允许积压的最大帧数（背压上限）。
        /// 无界队列在「主线程 Tick 长时间停顿」或「链路半死但仍在 Enqueue」时会持续吃内存，
        /// 最终把进程撑爆。超过上限即丢弃**最旧**的帧并降频告警：保留最新的帧更接近当前状态，
        /// 而被积压的旧帧在这种场景下基本已经过期（QUIC 侧已有同类发送背压）。
        /// </summary>
        private const int MaxQueuedFrames = 4096;

        /// <summary>队列丢弃累计次数，用于把告警降频（只在第 1 次与每 64 次时打印）。</summary>
        private int _queueDropCount;

        private TcpClient _client;
        private Stream _stream;
        private volatile bool _running;
        private readonly ConcurrentQueue<byte[]> _recvQueue = new();
        private readonly ConcurrentQueue<byte[]> _sendQueue = new();
        private readonly SemaphoreSlim _sendSignal = new(0, int.MaxValue);

        /// <summary>连接代际：每次 ConnectAsync/Disconnect 自增，用于让旧代际的后台线程与挂起回调失效</summary>
        private int _generation;

        /// <summary>
        /// 链路失败已上报标记（Interlocked 使用），防止重复回调。
        /// 语义是「**本次连接尝试**是否已上报过失败」：<see cref="ConnectAsync"/> 开始时清 0，
        /// <see cref="Disconnect"/> 与 <see cref="HandleLinkFailure"/> 置 1。
        /// 初值取 1（= 未连接状态视为已上报），保证「从未连接就断开」不会误报回调。
        /// </summary>
        private int _linkDown = 1;

        /// <summary>最近一次发送的时间戳（Stopwatch ticks，仅发送线程写）</summary>
        private long _lastSendTicks;

        /// <summary>心跳间隔（毫秒）：N 毫秒无发送则发 ping，默认 15 秒，与服务端读超时探测匹配</summary>
        public int HeartbeatIntervalMs = 15_000;

        /// <summary>
        /// 接收超时（毫秒）：连续该时长**一个字节都没收到**即判链路失败。&lt;=0 时取
        /// <see cref="EffectiveReceiveTimeoutMs"/> 的派生值（心跳间隔的 3 倍，下限 30 秒）。
        ///
        /// 为什么必须设：<see cref="RecvLoop"/> 是阻塞读，此前只在 TLS 握手期设过超时、
        /// 握手后立刻复位为 0（= 永不超时）。半开连接（对端进程已崩、NAT 静默丢弃、
        /// 网线拔出）下这个读线程会**永久阻塞**，既不报错也不退出，重连逻辑永远等不到失败信号。
        /// 取 3 倍心跳是因为正常情况下每个心跳周期内都有 ping/pong 往返，3 倍是给抖动留的余量。
        /// </summary>
        public int ReceiveTimeoutMs;

        /// <summary>连接超时（毫秒），默认 5 秒</summary>
        public int ConnectTimeoutMs = 5_000;

        /// <summary>
        /// 是否用 TLS 封装链路（<see cref="SslStream"/>），需与服务端 <c>gateway.tls_cert</c> 是否配置一致
        /// （服务端可用 <c>tcp_tls_disabled</c> 单独保住明文 TCP 入口）。默认 false（明文）。
        /// §N7：引擎不提供「跳过证书校验」的开关，握手走系统默认信任链 + 主机名校验。
        /// </summary>
        public bool UseTls;

        /// <summary>线路类型</summary>
        public TransportKind Kind => TransportKind.Tcp;

        /// <summary>当前连接状态</summary>
        public ConnectionState State { get; private set; } = ConnectionState.Disconnected;

        /// <summary>连接建立成功回调（可能在后台线程触发）</summary>
        public event Action Connected;

        /// <summary>链路断开回调（可能在后台线程触发），仅在非用户主动断开时触发</summary>
        public event Action Disconnected;

        /// <summary>
        /// 发起连接（非阻塞）：异步完成握手后启动收发线程并回调 Connected；
        /// 超时或失败回调 Disconnected（State 置回 Disconnected）。
        /// 地址格式非法时同步抛 FormatException。
        /// </summary>
        /// <param name="addr">"host:port" 形式地址</param>
        public void ConnectAsync(string addr)
        {
            // 先校验地址：非法地址在动旧连接**之前**同步抛
            //（否则传入非法地址时原可用连接已被拆掉且无回滚）。
            if (!NetAddr.TryParse(addr, out var host, out var port))
                throw new FormatException($"invalid addr: {addr}");

            // 校验通过后再推进代际，使旧代际的后台线程与挂起的连接回调立即失效
            var gen = Interlocked.Increment(ref _generation);
            DisconnectCore();
            // 本次尝试「尚未上报失败」：否则首次连接就失败（端口没开 / TLS 握手失败）时
            // HandleLinkFailure 会因标记已是 1 而早退，导致 Disconnected 不回调、State 卡在
            // Connecting、socket 不释放 —— NetworkManager 的降级链与退避重连都挂在那个回调上。
            Interlocked.Exchange(ref _linkDown, 0);

            var client = new TcpClient();
            _client = client;
            State = ConnectionState.Connecting;

            var connectTask = client.ConnectAsync(host, port);
            Task.WhenAny(connectTask, Task.Delay(ConnectTimeoutMs)).ContinueWith(t =>
            {
                if (_generation != gen)
                    return;
                if (connectTask.Status != TaskStatus.RanToCompletion)
                {
                    // 失败原因藏在 connectTask 上：读它的异常（= 观察，避免未观察 Task 异常）
                    // 并写进日志。
                    if (!connectTask.IsCompleted)
                    {
                        // 超时路径：connectTask 之后仍可能以异常收尾，挂个观察器兜住
                        _ = connectTask.ContinueWith(ft => { _ = ft.Exception; },
                            TaskContinuationOptions.OnlyOnFaulted);
                    }

                    HandleLinkFailure(gen, DescribeConnectFailure(connectTask, addr));
                    return;
                }
                try
                {
                    client.NoDelay = true;
                    client.ReceiveBufferSize = 1 << 20;
                    client.SendBufferSize = 1 << 20;
                    _stream = client.GetStream();

                    if (UseTls)
                    {
                        // TLS 握手：证书校验走系统默认信任链与主机名校验（§N7 禁止提供跳过校验的开关）。
                        // 自签证书必须由使用者导入系统信任链，而不是在这里放行。
                        // 握手期临时设收发超时，避免对端不回握手包时永久阻塞连接协程；成功后复位为不超时。
                        var ssl = new SslStream(_stream, false);
                        client.ReceiveTimeout = ConnectTimeoutMs;
                        client.SendTimeout = ConnectTimeoutMs;
                        try
                        {
                            ssl.AuthenticateAsClient(host);
                        }
                        finally
                        {
                            client.ReceiveTimeout = 0;
                            client.SendTimeout = 0;
                        }

                        _stream = ssl;
                    }

                    // 半开连接防护（见 ReceiveTimeoutMs）：TLS 与非 TLS 两条路径都要设接收超时，
                    // 否则 RecvLoop 会永久阻塞在阻塞读上（此前只有 TLS 握手期设过，握手后被复位成 0）。
                    // 发送侧不设超时：SendLoop 自带节流，socket 写超时只会把正常的大帧写出打断。
                    client.ReceiveTimeout = EffectiveReceiveTimeoutMs;
                    client.SendTimeout = 0;

                    _linkDown = 0;
                    _running = true;
                    _lastSendTicks = Stopwatch.GetTimestamp();
                    State = ConnectionState.Connected;

                    var recvThread = new Thread(() => RecvLoop(gen)) { IsBackground = true, Name = "CloverNet-TcpRecv" };
                    var sendThread = new Thread(() => SendLoop(gen)) { IsBackground = true, Name = "CloverNet-TcpSend" };
                    recvThread.Start();
                    sendThread.Start();

                    Game.Logger?.Info("Network", $"TCP connected: {addr}");
                    // Connected 回调单独隔离：订阅方异常不允许被下面的 catch 当"连接失败"，
                    // 否则刚建立的链路会被拆掉并误报断开（Disconnected 侧已有同样隔离）。
                    try
                    {
                        Connected?.Invoke();
                    }
                    catch (Exception e)
                    {
                        Game.Logger?.Error("Network", $"tcp connected callback error: {e.Message}", e);
                    }
                }
                catch (Exception e)
                {
                    HandleLinkFailure(gen, $"post-connect setup failed: {e.Message}");
                }
            });
        }

        /// <summary>把连接失败写成人话（含真实异常）：失败原因优先取 connectTask 的异常，其次区分超时/取消。</summary>
        private static string DescribeConnectFailure(Task connectTask, string addr)
        {
            if (connectTask.IsFaulted)
            {
                var ex = connectTask.Exception?.InnerException ?? connectTask.Exception;
                return $"connect failed: {addr}: {ex?.GetType().Name}: {ex?.Message}";
            }

            return connectTask.IsCanceled ? $"connect canceled: {addr}" : $"connect timeout: {addr}";
        }

        /// <summary>
        /// 兜底释放（等价 <see cref="Disconnect"/>）。持有方应显式断开；若未断开就丢弃，
        /// 收发线程经闭包持有本对象与 socket，GC 也回收不了 —— 本方法给这类使用一条显式释放通道。
        /// </summary>
        public void Dispose() => Disconnect();

        /// <summary>
        /// 用户主动断开：终止收发线程、释放 socket、清空收发队列，不触发 Disconnected 回调
        /// </summary>
        public void Disconnect()
        {
            Interlocked.Increment(ref _generation);
            // 主动断开按「已上报」处理，压掉回调（代际推进已覆盖绝大多数竞态，这里是显式兜底）
            Interlocked.Exchange(ref _linkDown, 1);
            DisconnectCore();
        }

        /// <summary>
        /// 投递数据帧：包装为数据帧（type=0）入队，由后台发送线程写出
        /// </summary>
        public void Send(byte[] data, int offset, int count)
        {
            if (State != ConnectionState.Connected)
            {
                Game.Logger?.Warn("Network", "tcp send ignored: not connected");
                return;
            }

            // 直接编码成整帧（一次分配）："帧头 + 数据"一次布局完成。
            EnqueueBounded(_sendQueue, EncodeFrame(FrameTypeData, data, offset, count), "send");
            try
            {
                _sendSignal.Release();
            }
            catch (SemaphoreFullException)
            {
                // 信号量已满时忽略：发送线程必然已被唤醒
            }
        }

        /// <summary>收发与心跳均由后台线程驱动，主线程驱动点保留为空实现</summary>
        public void Tick()
        {
        }

        /// <summary>
        /// 取出一条完整客户端帧（传输层已剥去帧头，且 ping/pong 不入队）
        /// </summary>
        public bool TryTakePacket(out byte[] data)
        {
            return _recvQueue.TryDequeue(out data);
        }

        /// <summary>关闭 socket 并清空收发队列（不动代际与回调标记）</summary>
        private void DisconnectCore()
        {
            State = ConnectionState.Disconnected;
            _running = false;
            _stream?.Close();
            _client?.Close();
            _stream = null;
            _client = null;

            while (_recvQueue.TryDequeue(out _)) { }
            while (_sendQueue.TryDequeue(out _)) { }
            try
            {
                _sendSignal.Release();
            }
            catch (SemaphoreFullException)
            {
            }
        }

        /// <summary>
        /// 链路失败统一入口：按代际与一次性标记去重后关闭资源并回调 Disconnected
        /// </summary>
        private void HandleLinkFailure(int gen, string reason)
        {
            if (_generation != gen)
                return;
            if (Interlocked.Exchange(ref _linkDown, 1) == 1)
                return;
            DisconnectCore();
            Game.Logger?.Info("Network", $"TCP link down: {reason}");
            try
            {
                Disconnected?.Invoke();
            }
            catch (Exception e)
            {
                Game.Logger?.Error("Network", $"tcp disconnected callback error: {e.Message}", e);
            }
        }

        /// <summary>
        /// 收包循环：解析传输层帧并入队数据帧，收 ping 即回 pong。
        /// 仅当代际未变化时入队，避免旧会话残留帧污染新会话。
        /// </summary>
        private void RecvLoop(int gen)
        {
            var firstFrame = true;
            while (_running && _generation == gen)
            {
                try
                {
                    var type = ReadByte();

                    // 首帧帧头不合法 → 极可能连错了端口。最常见的是把网关 WS 口（8001）当成 TCP 口
                    // 填给 CloverNet.Init：服务端会回 HTTP 响应，首字节是 'H'/'G'，不是帧类型 0-2。
                    // 这里给出明确指向，避免业务只看到「连上 → 秒断」而无从判断。
                    if (firstFrame && type > FrameTypePong)
                    {
                        HandleLinkFailure(gen,
                            $"tcp first byte 0x{type:X2} is not a valid frame type (expect 0-2); " +
                            "the endpoint looks like a WebSocket port — use gateway.listen_tcp (default 8002), " +
                            "or set GameConfig.WsAddr to use the WebSocket line (listen_ws, default 8001)");
                        return;
                    }
                    firstFrame = false;

                    // 长度前缀是 4B 大端**无符号**数：直接强转 int 会让 ≥ 0x80000000 的值变负，
                    // 既不超硬上限也不 > 0 → 被当空 payload 丢弃、实际字节没读走，流从此错位且零报错。
                    // 先用 uint 比上限、再转 int（QUIC 侧 QuicStreamFraming.Extract 同样先判负）。
                    var rawLen = BigEndian.ReadUInt32(ReadExact(4), 0);
                    if (rawLen > (uint)HardMaxFramePayload)
                    {
                        HandleLinkFailure(gen, $"tcp frame too large: {rawLen}");
                        return;
                    }
                    if (rawLen > (uint)MaxFramePayload)
                        Game.Logger?.Warn("Network", $"tcp frame exceeds soft limit: {rawLen}");

                    var len = (int)rawLen;
                    var payload = len > 0 ? ReadExact(len) : Array.Empty<byte>();
                    switch (type)
                    {
                        case FrameTypeData:
                            if (payload.Length > 0 && _generation == gen)
                                EnqueueBounded(_recvQueue, payload, "recv");
                            break;
                        case FrameTypePing:
                            // 收到 ping 立即回 pong，保证服务端写探测通过
                            EnqueueBounded(_sendQueue, EncodeFrame(FrameTypePong, Array.Empty<byte>()), "send");
                            SignalSend();
                            break;
                        case FrameTypePong:
                            // 心跳应答，无需处理
                            break;
                        // 服务端已无 frameTypeMigrate：类型 3 落到 default 按「未知帧类型」留痕，两端口径一致。
                        default:
                            Game.Logger?.Warn("Network", $"tcp unknown frame type: {type}");
                            break;
                    }
                }
                catch (Exception e)
                {
                    // 连接被断开或读取出错：原因必须带出来（区分读取超时 / 协议错 / 编程错）
                    if (_running && _generation == gen)
                        HandleLinkFailure(gen, $"recv failed: {e.GetType().Name}: {e.Message}");
                    return;
                }
            }
        }

        /// <summary>
        /// 发送循环：写出待发帧；心跳间隔内无任何发送则补发 ping。
        /// 在独立后台线程执行，主线程挂起（OnApplicationPause）期间心跳照常，链路不被服务端读超时踢除。
        /// </summary>
        private void SendLoop(int gen)
        {
            try
            {
                while (_running && _generation == gen)
                {
                    _sendSignal.Wait(SendLoopWaitMs);

                    while (_running && _generation == gen && _sendQueue.TryDequeue(out var frame))
                    {
                        if (!WriteFrame(frame))
                        {
                            HandleLinkFailure(gen, "send failed");
                            return;
                        }
                    }

                    if (_running && _generation == gen && State == ConnectionState.Connected)
                    {
                        var elapsedMs = (Stopwatch.GetTimestamp() - _lastSendTicks) * 1000 / Stopwatch.Frequency;
                        if (elapsedMs >= HeartbeatIntervalMs)
                        {
                            if (!WriteFrame(EncodeFrame(FrameTypePing, Array.Empty<byte>())))
                            {
                                HandleLinkFailure(gen, "ping failed");
                                return;
                            }
                        }
                    }
                }
            }
            catch
            {
                // 退出阶段 stream/socket 已被并发关闭，按链路失败收尾
                HandleLinkFailure(gen, "send loop aborted");
            }
        }

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

        /// <summary>编码传输层帧：[1B type][4B 大端 len][payload]</summary>
        private static byte[] EncodeFrame(byte type, byte[] payload)
        {
            return EncodeFrame(type, payload, 0, payload?.Length ?? 0);
        }

        /// <summary>
        /// 编码传输层帧（数据直接从源缓冲布局进帧体）：Send 热路径用，避免
        /// "先拷一份 payload、再拷一份整帧"的二次分配。
        /// </summary>
        private static byte[] EncodeFrame(byte type, byte[] data, int offset, int count)
        {
            var frame = new byte[5 + count];
            frame[0] = type;
            BigEndian.WriteUInt32(frame, 1, (uint)count);
            if (count > 0)
                Buffer.BlockCopy(data, offset, frame, 5, count);
            return frame;
        }

        /// <summary>同步写出整帧，失败返回 false（发送线程是唯一写者，无需加锁）</summary>
        private bool WriteFrame(byte[] frame)
        {
            try
            {
                _stream.Write(frame, 0, frame.Length);
                _lastSendTicks = Stopwatch.GetTimestamp();
                return true;
            }
            catch (Exception e)
            {
                // 写失败的具体异常要留痕（调用方只会报一句笼统的 send failed，原因会丢失）
                Game.Logger?.Warn("Network", $"tcp write frame failed: {e.GetType().Name}: {e.Message}");
                return false;
            }
        }

        /// <summary>生效的接收超时（毫秒）：未显式配置时取心跳间隔的 3 倍，下限 30 秒。</summary>
        private int EffectiveReceiveTimeoutMs =>
            ReceiveTimeoutMs > 0 ? ReceiveTimeoutMs : Math.Max(HeartbeatIntervalMs * 3, 30_000);

        /// <summary>
        /// 入队并施加背压（见 <see cref="MaxQueuedFrames"/>）：超出上限时丢弃**最旧**的帧。
        /// 丢弃必须留痕且降频，否则现场只表现为「莫名少收到消息」，无从判断是丢了还是没发。
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
                    $"tcp {what} queue exceeded {MaxQueuedFrames} frames, dropped {dropped} oldest (total={total}) — " +
                    "链路积压：检查主线程 Tick 是否被阻塞或对端是否停止读取");
            }
        }

        private byte ReadByte()
        {
            var b = _stream.ReadByte();
            if (b < 0) throw new SocketException((int)SocketError.Disconnecting);
            return (byte)b;
        }

        /// <summary>精确读取 count 字节，连接断开时抛异常</summary>
        private byte[] ReadExact(int count)
        {
            var buf = new byte[count];
            var read = 0;
            while (read < count)
            {
                var n = _stream.Read(buf, read, count - read);
                if (n <= 0) throw new SocketException((int)SocketError.Disconnecting);
                read += n;
            }
            return buf;
        }
    }

    /// <summary>
    /// 裸 UDP 通道实现，承载不可靠消息与网关推送。
    /// 帧格式：[0x55 魔数][客户端帧]，与服务端约定一致；非魔数报文直接丢弃。
    /// 无连接语义，连接即代表 socket 已绑定并可收发；发送立即写出。
    /// </summary>
    internal class UdpConnection : ITransportConnection, IDisposable
    {
        /// <summary>裸 UDP 协议魔数，仅收发首字节为 0x55 的报文</summary>
        private const byte Magic = 0x55;

        /// <summary>
        /// 单个 UDP 报文（1 字节魔数 + 客户端帧）的字节上限 = UDP payload 理论上限 65507
        /// （65535 - 8 字节 UDP 头 - 20 字节 IP 头）。
        /// 与服务端 <c>udp.defaultMaxPacketSize</c>（65507，见服务端 udp/config.go）同口径：
        /// 超过它的报文会被 OS 直接丢弃、服务端永远收不到 —— 本地先拦并留痕。
        /// </summary>
        private const int MaxDatagramBytes = 65507;

        private UdpClient _client;
        private Thread _recvThread;
        private volatile bool _running;
        private readonly ConcurrentQueue<byte[]> _recvQueue = new();

        /// <summary>
        /// UDP 收包队列上限（背压）。无界队列在「主线程长时间不 Tick」时会持续吃内存，
        /// 而 UDP 无流控 —— 对端发多快就积多快。超过上限丢弃**最旧**的报文并降频告警。
        /// </summary>
        private const int MaxQueuedDatagrams = 4096;

        /// <summary>UDP 队列丢弃累计次数，用于把告警降频。</summary>
        private int _queueDropCount;

        /// <summary>入队并按 <see cref="MaxQueuedDatagrams"/> 施加背压（丢最旧 + 降频告警）。</summary>
        private void EnqueueRecvBounded(byte[] frame)
        {
            _recvQueue.Enqueue(frame);
            var dropped = 0;
            while (_recvQueue.Count > MaxQueuedDatagrams && _recvQueue.TryDequeue(out _))
                dropped++;
            if (dropped == 0)
                return;
            var total = Interlocked.Add(ref _queueDropCount, dropped);
            if (total == dropped || total % 64 < dropped)
            {
                Game.Logger?.Warn("Network",
                    $"udp recv queue exceeded {MaxQueuedDatagrams} datagrams, dropped {dropped} oldest (total={total}) — " +
                    "主线程 Tick 可能被阻塞");
            }
        }

        /// <summary>线路类型</summary>
        public TransportKind Kind => TransportKind.RawUdp;

        /// <summary>当前连接状态</summary>
        public ConnectionState State { get; private set; } = ConnectionState.Disconnected;

        // UDP 无握手，因此不存在「连接建立 / 链路断开」事件。两个事件仅为满足
        // ITransportConnection 契约而声明，**永不触发**（可靠链路的生命周期由 TCP / WebSocket 驱动）。
        // 显式压掉「事件从未使用」告警：这是有意为之，不是遗漏。
#pragma warning disable CS0067
        /// <summary>
        /// UDP 无握手，因此不存在「连接建立」事件；本事件仅为满足 <see cref="ITransportConnection"/> 契约，
        /// 永不触发（可靠链路的生命周期由 TCP / WebSocket 侧驱动）。
        /// </summary>
        public event Action Connected;

        /// <summary>UDP 无链路断开事件，永不触发（原因见 <see cref="Connected"/>）。</summary>
        public event Action Disconnected;
#pragma warning restore CS0067

        /// <summary>
        /// 建立 UDP 通道（异步，不涉及握手）：地址校验同步完成（契约：非法地址同步抛
        /// <see cref="FormatException"/>），其余工作（DNS 解析 + 建 socket + 起收包线程）移出调用线程 ——
        /// <c>UdpClient.Connect(host, port)</c> 对**域名**会同步做 DNS 解析，可能长时间阻塞，
        /// 不能占住主线程（契约的非阻塞语义）。
        /// </summary>
        /// <param name="addr">"host:port" 形式的共享 UDP 端点</param>
        public void ConnectAsync(string addr)
        {
            if (!NetAddr.TryParse(addr, out var host, out var port))
                throw new FormatException($"invalid addr: {addr}");

            _ = Task.Run(() =>
            {
                try
                {
                    ConnectCore(addr, host, port);
                }
                catch (Exception e)
                {
                    // 后台失败没有同步返回通道：清掉半初始化状态并留 Error（Disconnected 事件按契约永不触发）
                    Game.Logger?.Error("Network", $"udp connect failed: {addr}: {e.GetType().Name}: {e.Message}", e);
                    Disconnect();
                }
            });
        }

        /// <summary>
        /// 建立 UDP 通道（同步版：NetworkManager 与测试经此调用）。不涉及握手，绑定本地端口并启动收包线程。
        /// </summary>
        /// <param name="addr">"host:port" 形式的共享 UDP 端点</param>
        public void Connect(string addr)
        {
            if (!NetAddr.TryParse(addr, out var host, out var port))
                throw new FormatException($"invalid addr: {addr}");

            ConnectCore(addr, host, port);
        }

        private void ConnectCore(string addr, string host, int port)
        {
            Disconnect();

            var client = new UdpClient();
            _client = client;
            try
            {
                client.Connect(host, port);
            }
            catch
            {
                // 半初始化状态不保留：关掉刚建的 socket（否则句柄泄漏到 GC 才回收、收包线程也未启动），
                // 再原样上抛给调用方（Connect 的调用方会打日志）
                _client = null;
                client.Close();
                throw;
            }

            _running = true;
            _recvThread = new Thread(RecvLoop) { IsBackground = true, Name = "CloverNet-UdpRecv" };
            _recvThread.Start();

            State = ConnectionState.Connected;
            Game.Logger?.Info("Network", $"UDP channel connected: {addr}");
        }

        /// <summary>
        /// 关闭 UDP socket 并清空收包队列
        /// </summary>
        public void Disconnect()
        {
            State = ConnectionState.Disconnected;
            _running = false;
            _client?.Close();
            _client = null;
            while (_recvQueue.TryDequeue(out _)) { }
        }

        /// <summary>
        /// 兜底释放（等价 <see cref="Disconnect"/>）：持有方应显式断开；若未断开就丢弃，
        /// 收包线程经闭包持有本对象与 socket，GC 也回收不了 —— 本方法给这类使用一条显式释放通道。
        /// </summary>
        public void Dispose() => Disconnect();

        /// <summary>
        /// 投递数据帧：自动加 0x55 魔数前缀后立即写出
        /// </summary>
        public void Send(byte[] data, int offset, int count)
        {
            if (State != ConnectionState.Connected || _client == null)
            {
                Game.Logger?.Warn("Network", "udp send ignored: not connected");
                return;
            }

            // 长度校验（服务端口径：udp.defaultMaxPacketSize = 65507）：超限的 datagram 发出去
            // 必被 OS 丢弃（服务端永远收不到，仅远端 "udp send failed" 告警），本地先拦并明确告警。
            if (count < 0 || count > MaxDatagramBytes - 1)
            {
                Game.Logger?.Error("Network",
                    $"udp send refused: frame too large (framed={1L + count} bytes, payload={count}, max={MaxDatagramBytes})");
                return;
            }

            var datagram = new byte[1 + count];
            datagram[0] = Magic;
            Buffer.BlockCopy(data, offset, datagram, 1, count);

            try
            {
                _client.Send(datagram, datagram.Length);
            }
            catch (Exception e)
            {
                // UDP 丢弃报文不视为链路失败，仅告警
                Game.Logger?.Warn("Network", $"udp send failed: {e.Message}");
            }
        }

        /// <summary>UDP 无连接、无心跳需求，Tick 为空实现</summary>
        public void Tick()
        {
        }

        /// <summary>
        /// 取出一条完整客户端帧（魔数已剥去）
        /// </summary>
        public bool TryTakePacket(out byte[] data)
        {
            return _recvQueue.TryDequeue(out data);
        }

        private void RecvLoop()
        {
            // 使用本地端点副本作为 Receive 的输出参数载体；
            // 不直接解引用 _client.Client.LocalEndPoint，避免与 Disconnect 并发时的空引用
            var ep = new IPEndPoint(IPAddress.Any, 0);
            while (_running)
            {
                try
                {
                    var datagram = _client.Receive(ref ep);
                    // 仅接受带魔数的本协议报文，其余丢弃。
                    // 线协议最小帧 = 1 字节魔数 + 8 字节客户端帧头：2~8 字节的脏包到解析层也只会被当
                    // short frame 丢弃（多一次无谓流转与告警），这里直接拦下。
                    if (datagram.Length < 1 + ClientFrame.HeaderLen || datagram[0] != Magic)
                        continue;

                    var frame = new byte[datagram.Length - 1];
                    Buffer.BlockCopy(datagram, 1, frame, 0, frame.Length);
                    EnqueueRecvBounded(frame);
                }
                catch (Exception e)
                {
                    if (_running)
                    {
                        // 收包循环失败必须留痕（否则 UDP 通道静默失效无法排障）
                        Game.Logger?.Warn("Network", $"udp recv failed: {e.GetType().Name}: {e.Message}");
                        Disconnect();
                    }
                    return;
                }
            }
        }
    }
}
