using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using AOT;

namespace CloverEngine
{
    /// <summary>
    /// QUIC 长连接实现（原生 msquic）。把服务端 <c>internal/transport/net/quic</c> 的线协议搬到客户端。
    ///
    /// <para><b>线协议（与 <c>clover-server-tools/msg-client</c> 严格一致，见 <c>Tools~/native/README.md</c>）</b></para>
    /// <list type="number">
    /// <item>ALPN = <c>clover-quic</c>；</item>
    /// <item><b>客户端主动打开首条双向流</b>（服务端 <c>AcceptStream</c> 等我们开流，方向不能反）；</item>
    /// <item>可靠数据走该流，每帧前加 <c>[4B 大端长度]</c>（流是字节流，必须自己分帧）；</item>
    /// <item>不可靠数据走 <b>QUIC Datagram</b>（无长度前缀，一整条报文即一帧）；</item>
    /// <item>QUIC 模式下**不需要** <c>EMsgBindUDP</c> 绑定（Datagram 就是不可靠通道）。</item>
    /// </list>
    ///
    /// <para><b>线程模型（与 <see cref="TcpConnection"/> 同构，便于上层无差别使用）</b>：</para>
    /// <list type="bullet">
    /// <item>msquic 的回调（收包/连接事件）来自其自有工作线程 —— 回调里**只做**「拷贝字节 + 入队」，
    /// 绝不触碰 Unity API（Unity 的对象只能在主线程碰）；</item>
    /// <item>主线程经 <see cref="Tick"/> → <see cref="TryTakePacket"/> 排空收包队列；</item>
    /// <item>发送由独立后台线程串行执行（<c>StreamSend</c> 需要保证"同一时刻一个写者"的调用纪律，
    /// 串行化同时也让"发出顺序 = 入队顺序"成立）。</item>
    /// </list>
    ///
    /// <para><b>为什么这里要有应用层保活</b>：msquic 的 <c>KeepAliveIntervalMs</c> 只维持 QUIC 连接本身，
    /// 而**服务端的空闲判定看的是应用层流读写**（<c>quic/conn.go</c> 的 <c>touch()</c>/<c>touchWrite()</c>，
    /// <c>server.go</c> 的 <c>cleanLoop</c>，空闲 30s 即关连接）。
    /// 因此这里每 <see cref="HeartbeatIntervalMs"/> 毫秒在流上补一帧保活帧
    /// （<c>reqID=0</c> + <see cref="KeepAliveMsgId"/>），确保服务端看到"这条连接还在收数据"。
    /// </para>
    /// </summary>
    internal sealed class QuicConnection : ITransportConnection, IUnreliableTransport
    {
        /// <summary>流帧长度前缀字节数（与服务端 <c>quicFrameLenSize</c> 一致）。</summary>
        private const int FrameHeaderLen = 4;

        /// <summary>
        /// 单帧上限 **10 MiB**，与服务端一致（TCP/WS/QUIC 统一 10MiB，对应服务端 <c>maxQUICFrameSize</c>）。
        /// ⚠️ 与服务端的帧上限口径是**跨端约定**：两端不一致会导致大帧被判流错位并断链，
        /// 要改必须双端同步确认（不要在本地单方面调数值）。
        /// </summary>
        private const int MaxFrameSize = 10 << 20;

        /// <summary>
        /// 保活帧的消息号。**0 是刻意的**：0 不是任何业务/引擎消息号，
        /// 服务端 dispatch 查不到 handler 会直接返回（不回包、不改状态），
        /// 但**流上有字节**——这正是服务端 <c>touch()</c> 需要的"应用层活跃度"。
        /// 用真实业务消息号（如 RankQuery）保活会触发真实业务查询，绝对不可以。
        /// </summary>
        private const uint KeepAliveMsgId = 0;

        /// <summary>发送线程空转等待间隔（毫秒），兼作保活检查周期。</summary>
        private const int SendLoopWaitMs = 1000;

        /// <summary>心跳间隔（毫秒）：N 毫秒无任何发送则补一帧保活。</summary>
        public int HeartbeatIntervalMs = 15_000;

        /// <summary>连接超时（毫秒）。</summary>
        public int ConnectTimeoutMs = 5_000;

        /// <summary>可靠发送队列的帧数上限（背压）：发送线程落后时拒绝新帧而不是无界堆积内存。</summary>
        private const int MaxSendQueueFrames = 256;

        /// <summary>
        /// 各收发队列允许积压的最大帧数（背压上限）。口径与 <see cref="TcpConnection"/> /
        /// WebSocketConnection 一致（同为 4096，超限丢**最旧** + 降频告警；**不要另立第二套数值**）。
        /// 覆盖三条队列：接收队列（可靠流帧与 Datagram **共用**）、出站 Datagram 队列 ——
        /// 主线程 Tick 停顿或链路半死时，对端/业务发多快就积多快。
        /// </summary>
        private const int MaxQueuedFrames = 4096;

        /// <summary>接收队列丢弃累计次数，用于把告警降频（第 1 次与每 64 次打印）。</summary>
        private int _recvQueueDropCount;

        /// <summary>出站 Datagram 队列丢弃累计次数（与接收队列各一份，避免互相压制降频）。</summary>
        private int _datagramQueueDropCount;

        private readonly ConcurrentQueue<byte[]> _recvQueue = new();
        private readonly ConcurrentQueue<byte[]> _sendQueue = new();
        private readonly ConcurrentQueue<byte[]> _datagramQueue = new();
        private readonly SemaphoreSlim _sendSignal = new(0, int.MaxValue);

        /// <summary>发送队列当前帧数（背压用：入队 +1、出队/清空 -1）。</summary>
        private int _sendQueueFrames;

        /// <summary>最近一次"队列满丢帧"日志时间（Stopwatch ticks），用于降频。</summary>
        private long _sendDropLogTicks;

        /// <summary>流字节流的重组缓冲：QUIC 流是字节流，收到的是任意切分，必须自己按长度前缀切帧。</summary>
        private readonly List<byte> _rxBuffer = new();
        private readonly object _rxGate = new();

        /// <summary>拷贝原生缓冲用的复用 scratch（只增不减）：避免每个 QUIC_BUFFER 都 new 一次。
        /// 只在 <see cref="_rxGate"/> 内使用，无并发。</summary>
        private byte[] _copyScratch = Array.Empty<byte>();

        /// <summary>
        /// 一次发送占用的原生资源。用**托管对象 + GCHandle** 而不是直接拿原生块当地址：
        /// msquic 的发送完成回调可能不回来（发送失败即不排队）、也可能回来两次路径重叠，
        /// 用托管对象就能带"已释放"标记做幂等，避免 double free（原生堆 double free = 直接崩进程）。
        /// </summary>
        private sealed class SendContext
        {
            public IntPtr DataPtr;
            public IntPtr DescPtr;
            public int Released;
        }

        private MsQuicNative.ApiTable _api;
        private IntPtr _connection = IntPtr.Zero;
        private IntPtr _stream = IntPtr.Zero;

        /// <summary>发送与"关闭句柄"之间的互斥：绝不允许在句柄被关的同时还向它发送（use-after-free）。</summary>
        private readonly object _sendGate = new();

        private int _streamClosed;
        private int _connectionClosed;

        /// <summary>收包诊断日志预算：前 N 帧打 Info（定位"收没收到"），之后静默。</summary>
        private int _recvLogBudget = 5;
        private string _addr;
        private volatile bool _running;
        private int _generation;
        private int _linkDown = 1;
        private long _lastSendTicks;
        private volatile bool _datagramSendEnabled;
        private Timer _connectWatchdog;

        // GC 根：msquic 只保存函数指针，委托实例必须由托管侧保持引用。
        // 少了这几行，委托被回收后 msquic 会回调到已释放的地址（进程级崩溃，且堆栈完全对不上）。
        //
        // ⚠️ 这两个委托**必须指向静态方法**（见 OnConnectionEventStatic / OnStreamEventStatic）：
        //   实例方法委托交给原生在 Mono 下能用，在 IL2CPP 下会抛
        //   "IL2CPP does not support marshaling delegates that point to instance methods to native code"
        //   ⇒ QUIC 在真机/发布包里永远连不上（IL2CPP 包如此，且只在运行到连接时才暴露）。
        private MsQuicNative.ConnectionCallback _connectionCallback;
        private MsQuicNative.StreamCallback _streamCallback;

        /// <summary>
        /// <c>this</c> 的 GCHandle：作为 msquic 的 <c>context</c> 传给原生，
        /// 静态回调靠它反查所属连接（IL2CPP 不能用实例方法委托，这是替代方案）。
        /// 生命周期与句柄一致：连接时 Alloc、句柄关闭后 Free（见 CloseHandlesQuietly）。
        /// </summary>
        private GCHandle _selfHandle;

        /// <summary>线路类型。</summary>
        public TransportKind Kind => TransportKind.Quic;

        /// <summary>
        /// 连接状态的后备字段：msquic 回调线程与调用方线程（Disconnect/Send/看门狗）并发读写，
        /// 用 volatile 保证可见性 —— 旧实现是普通自动属性，Send 可能读到陈旧状态而误丢帧。
        /// </summary>
        private volatile ConnectionState _state = ConnectionState.Disconnected;

        /// <summary>当前连接状态。</summary>
        public ConnectionState State
        {
            get => _state;
            private set => _state = value;
        }

        /// <summary>连接建立成功回调（来自 msquic 回调线程）。</summary>
        public event Action Connected;

        /// <summary>链路断开回调（来自 msquic 回调线程）；用户主动断开不触发。</summary>
        public event Action Disconnected;

        /// <summary>
        /// 是否支持不可靠发送。**由 QUIC 对端是否协商出 Datagram 决定**
        /// （收到 <c>DATAGRAM_STATE_CHANGED{SendEnabled=true}</c> 才算支持）。
        /// 不支持时上层应退化为可靠发送，而不是丢包 —— 丢包会让业务静默不同步。
        /// </summary>
        public bool SupportsUnreliable => _datagramSendEnabled;

        /// <summary>
        /// 发起连接（非阻塞）。地址格式 <c>host:port</c>（支持 IPv6 字面量 <c>[::1]:8003</c>）；
        /// 非法时同步抛 <see cref="FormatException"/>。
        /// </summary>
        public void ConnectAsync(string addr)
        {
            // 先校验地址与可用性：二者失败都在动旧连接**之前**同步抛（旧实现先回收句柄再校验，
            // 传入非法地址时原可用连接已被拆掉且无回滚）。
            if (!NetAddr.TryParse(addr, out var host, out var port))
                throw new FormatException($"invalid addr: {addr}");

            if (!MsQuicRuntime.TryEnsureReady(out var unavailable))
                throw new NotSupportedException($"QUIC 不可用: {unavailable}");
            MsQuicRuntime.Require(out _api, out var registration, out var configuration);

            var gen = Interlocked.Increment(ref _generation);
            CloseHandlesQuietly(); // 先回收上一轮的句柄（已 shutdown 的才会走到这里）
            _streamClosed = 0;     // 新一轮连接：重新武装两个幂等关闭标记
            _connectionClosed = 0;
            _linkDown = 0;
            // Datagram 支持状态随旧连接失效：新连接的协商结果由 DatagramStateChanged 重新写入。
            // 不复位会让重连后在新协商完成前沿用 stale true，向对端发送它尚未接受的 Datagram。
            _datagramSendEnabled = false;
            _addr = addr;

            State = ConnectionState.Connecting;
            // 见字段注释：IL2CPP 只接受**静态**回调，实例靠 context 里的 GCHandle 反查
            _connectionCallback = OnConnectionEventStatic;
            _streamCallback = OnStreamEventStatic;
            _selfHandle = GCHandle.Alloc(this);
            MsQuicRuntime.Register(this); // 登记在册：域卸载/退出前会被强制关掉（否则回调打到已卸载代码 = 进程崩溃）

            var openConnection = _api.Fn<MsQuicNative.ConnectionOpenFn>(_api.ConnectionOpen);
            var status = openConnection(registration, _connectionCallback, GCHandle.ToIntPtr(_selfHandle), out _connection);
            if (Failed(status, "ConnectionOpen"))
            {
                // 早退兜底：该路径不会有 SHUTDOWN_COMPLETE 回调（msquic 没建出连接对象），
                // GCHandle 与 LiveConnections 登记必须就地回收 —— 否则永久泄漏
                //（旧实现直接 HandleLinkFailure 走人，只等 SHUTDOWN_COMPLETE 的清理永不发生）。
                CloseHandlesQuietly();
                HandleLinkFailure(gen, $"ConnectionOpen failed: 0x{status:X8}");
                return;
            }

            var serverName = Marshal.StringToHGlobalAnsi(host);
            try
            {
                var start = _api.Fn<MsQuicNative.ConnectionStartFn>(_api.ConnectionStart);
                // Family=0（UNSPEC）：让 msquic 自己解析主机名（IPv4/IPv6 都可）。
                // ServerName 同时用于 SNI 与证书主机名校验，必须是地址里的主机名原文。
                status = start(_connection, configuration, 0, serverName, (ushort)port);
            }
            finally
            {
                Marshal.FreeHGlobal(serverName);
            }

            if (Failed(status, "ConnectionStart"))
            {
                HandleLinkFailure(gen, $"ConnectionStart failed: 0x{status:X8}");
                return;
            }

            // 连接超时看门狗：QUIC 握手失败时不一定回调（如端口没有 UDP 监听会静默重试），
            // 不能只依赖回调，否则 State 会永远停在 Connecting（上层降级链就卡死了）。
            _connectWatchdog = new Timer(_ =>
            {
                if (_generation == gen && State == ConnectionState.Connecting)
                    HandleLinkFailure(gen, $"connect timeout: {addr}");
            }, null, ConnectTimeoutMs, Timeout.Infinite);
        }

        /// <summary>用户主动断开：不触发 <see cref="Disconnected"/> 回调。</summary>
        public void Disconnect()
        {
            var gen = Interlocked.Increment(ref _generation);
            _linkDown = 1;
            _running = false;
            _datagramSendEnabled = false; // 断开后不再支持不可靠发送（重连由新连接重新协商）
            State = ConnectionState.Disconnected;
            ShutdownConnection(0);
            ClearQueues();
            // 兜底回收：正常路径的句柄 / GCHandle / 登记回收都押在 SHUTDOWN_COMPLETE 上；
            // 它因故未送达（断言/异常路径）时连接会永久留在 MsQuicRuntime.LiveConnections 被强引用。
            ScheduleDisconnectCleanup(gen);
        }

        /// <summary>断开兜底回收的超时（毫秒）：超过它仍未等到 SHUTDOWN_COMPLETE 就强制回收。</summary>
        private const int DisconnectCleanupTimeoutMs = 500;

        /// <summary>
        /// 断开后的兜底回收（异步、不阻塞调用线程）：等 SHUTDOWN_COMPLETE 最多
        /// <see cref="DisconnectCleanupTimeoutMs"/>；超时仍未送达（断言/异常路径）则强制走一次
        /// 幂等的句柄关闭，避免连接永久滞留在 LiveConnections（GCHandle 与强引用均不释放）。
        /// 捕获代际：新一轮 ConnectAsync 已接管时立即退出（清理责任移交给它）。
        /// </summary>
        private void ScheduleDisconnectCleanup(int gen)
        {
            if (!_selfHandle.IsAllocated && _connection == IntPtr.Zero && _stream == IntPtr.Zero)
                return; // 已回收，无需兜底

            _ = Task.Run(() =>
            {
                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < DisconnectCleanupTimeoutMs)
                {
                    if (_generation != gen)
                        return; // 新一轮连接已接管（ConnectAsync 开头会回收上一轮）
                    if (_stream == IntPtr.Zero && _connection == IntPtr.Zero)
                        return; // SHUTDOWN_COMPLETE 已正常送达
                    Thread.Sleep(10);
                }

                if (_generation != gen)
                    return; // 双检：避免误关新一轮的句柄

                Game.Logger?.Warn("Quic",
                    $"SHUTDOWN_COMPLETE 未在 {DisconnectCleanupTimeoutMs}ms 内送达，强制回收句柄（gen={gen}）");
                CloseHandlesQuietly();
            });
        }

        /// <summary>投递一帧可靠数据（内部自动加 <c>[4B 大端长度]</c> 前缀）。</summary>
        public void Send(byte[] data, int offset, int count)
        {
            if (State != ConnectionState.Connected)
            {
                Game.Logger?.Warn("Quic", "quic send ignored: not connected");
                return;
            }

            // 背压：发送队列积压（对端慢 / 网络阻塞）时拒绝新帧而不是无界占内存。
            // 可靠帧被丢等价于"该帧未发出"（与真实拥塞下的表现一致），日志按秒降频防刷屏。
            if (Interlocked.Increment(ref _sendQueueFrames) > MaxSendQueueFrames)
            {
                Interlocked.Decrement(ref _sendQueueFrames);
                LogSendQueueFull();
                return;
            }

            // 一次分配：直接编码成 [4B 大端长度][客户端帧]，由发送线程原样写出 ——
            // 旧实现 Send 拷一份 payload、发送线程 Encode 再分配一次（每包两次托管分配）。
            var frame = count > 0
                ? QuicStreamFraming.EncodeFrom(data, offset, count)
                : QuicStreamFraming.Encode(Array.Empty<byte>()); // 空体沿用原语义：合法空客户端帧

            _sendQueue.Enqueue(frame);
            SignalSend();
        }

        /// <summary>"发送队列满"日志降频（最多每秒一条）。</summary>
        private void LogSendQueueFull()
        {
            var now = Stopwatch.GetTimestamp();
            var last = Interlocked.Read(ref _sendDropLogTicks);
            if ((now - last) * 1000 / Stopwatch.Frequency < 1000)
                return;
            if (Interlocked.CompareExchange(ref _sendDropLogTicks, now, last) == last)
                Game.Logger?.Warn("Quic",
                    $"quic send queue full ({MaxSendQueueFrames} frames), frame dropped (backpressure)");
        }

        /// <summary>投递一帧不可靠数据（QUIC Datagram，无长度前缀）。</summary>
        public void SendUnreliable(byte[] data, int offset, int count)
        {
            if (State != ConnectionState.Connected)
            {
                Game.Logger?.Warn("Quic", "quic unreliable send ignored: not connected");
                return;
            }

            var payload = new byte[count];
            Buffer.BlockCopy(data, offset, payload, 0, count);
            // 背压（见 MaxQueuedFrames）：出站 Datagram 与接收队列共用同一个 EnqueueBounded，超限丢**最旧**。
            // Datagram 本就是不可靠通道，丢旧帧等价于拥塞下的自然丢失；但仍必须留痕降频，
            // 否则业务只看到「发了却没到」而无从判断是丢了还是没发。
            EnqueueBounded(_datagramQueue, payload, "datagram send", ref _datagramQueueDropCount);
            SignalSend();
        }

        /// <summary>主线程驱动点：msquic 自己有心跳与收包线程，这里保持空实现（与 TCP 一致）。</summary>
        public void Tick()
        {
        }

        /// <summary>取出一条完整客户端帧（长度前缀已剥去；可靠与 Datagram 共用同一出口）。</summary>
        public bool TryTakePacket(out byte[] data)
        {
            return _recvQueue.TryDequeue(out data);
        }

        /// <summary>
        /// 入队并施加背压（见 <see cref="MaxQueuedFrames"/>）：超出上限时丢弃**最旧**的帧。
        /// 丢弃必须留痕且降频，否则现场只表现为「莫名少收/少发消息」。与 <see cref="TcpConnection"/>
        /// 的 EnqueueBounded 同款（口径统一，勿另写一套）。可在 msquic 回调线程调用（队列与计数均线程安全）。
        /// 接收队列（可靠流帧与 Datagram 共用）与**出站 Datagram 队列**共用本方法，只有
        /// <paramref name="what"/> 与计数器不同 —— 不要再写第三份队列实现。
        /// </summary>
        /// <param name="queue">目标队列（接收 / 出站 Datagram）。</param>
        /// <param name="frame">待入队帧。</param>
        /// <param name="what">日志中的队列名（<c>recv</c> / <c>datagram send</c>）。</param>
        /// <param name="dropCount">该队列自己的丢弃累计计数（各队列各一份，避免互相压制降频）。</param>
        private void EnqueueBounded(ConcurrentQueue<byte[]> queue, byte[] frame, string what, ref int dropCount)
        {
            queue.Enqueue(frame);
            var dropped = 0;
            while (queue.Count > MaxQueuedFrames && queue.TryDequeue(out _))
                dropped++;
            if (dropped == 0)
                return;
            var total = Interlocked.Add(ref dropCount, dropped);
            if (total == dropped || total % 64 < dropped)
            {
                Game.Logger?.Warn("Quic",
                    $"quic {what} queue exceeded {MaxQueuedFrames} frames, dropped {dropped} oldest (total={total}) — " +
                    "链路积压：检查主线程 Tick 是否被阻塞或对端是否停止读取");
            }
        }

        // ------------------------------------------------------------------ msquic 回调（msquic 工作线程）

        /// <summary>
        /// 连接事件回调的**静态入口**（IL2CPP 硬性要求，见字段注释）。
        /// 从 context（= <see cref="_selfHandle"/>）反查连接实例，再走原来的实例逻辑。
        /// </summary>
        [MonoPInvokeCallback(typeof(MsQuicNative.ConnectionCallback))]
        private static uint OnConnectionEventStatic(IntPtr connection, IntPtr context,
            ref MsQuicNative.ConnectionEventData evt)
        {
            var self = ResolveFrom(context);
            return self == null ? 0u : self.OnConnectionEvent(connection, context, ref evt);
        }

        /// <summary>流事件回调的**静态入口**（同上）。</summary>
        [MonoPInvokeCallback(typeof(MsQuicNative.StreamCallback))]
        private static uint OnStreamEventStatic(IntPtr stream, IntPtr context,
            ref MsQuicNative.StreamEventData evt)
        {
            var self = ResolveFrom(context);
            return self == null ? 0u : self.OnStreamEvent(stream, context, ref evt);
        }

        /// <summary>
        /// 从 msquic 的 context 反查连接实例。
        /// 拿不到（context 为空 / 句柄已被释放 / 不是本类型）时返回 null ——
        /// 调用方按"无人认领"处理并返回 0，**不能让异常穿过原生边界**。
        /// </summary>
        private static QuicConnection ResolveFrom(IntPtr context)
        {
            if (context == IntPtr.Zero)
                return null;
            try
            {
                return GCHandle.FromIntPtr(context).Target as QuicConnection;
            }
            catch
            {
                return null; // 句柄已释放（关闭收尾阶段的竞态）：静默跳过
            }
        }

        private uint OnConnectionEvent(IntPtr connection, IntPtr context, ref MsQuicNative.ConnectionEventData evt)
        {
            try
            {
                MsQuicRuntime.Trace($"conn-event type={evt.Type}");
                switch (evt.Type)
                {
                    case MsQuicNative.ConnectionEvent.Connected:
                        // 握手完成：**立刻**打开首条双向流（服务端在等我们开流，超时 10s 按异常连接关闭）。
                        if (!OpenFirstStream())
                            return 0;
                        State = ConnectionState.Connected;
                        _lastSendTicks = Stopwatch.GetTimestamp();
                        StartWorkerThreads();
                        Game.Logger?.Info("Quic", $"QUIC connected: {_addr}");
                        RaiseConnected();
                        break;

                    case MsQuicNative.ConnectionEvent.DatagramSendStateChanged:
                        // Datagram 缓冲的释放点：只有到终结状态后 msquic 才不再引用它（提前释放 = 野指针发送）。
                        if (MsQuicNative.DatagramSendState.IsFinal(evt.DatagramSendState))
                            FreeSendContext(evt.DatagramSendContext);
                        break;

                    case MsQuicNative.ConnectionEvent.DatagramStateChanged:
                        _datagramSendEnabled = evt.DatagramSendEnabled != 0;
                        Game.Logger?.Info("Quic",
                            $"datagram send enabled={_datagramSendEnabled} maxLen={evt.DatagramMaxSendLength}");
                        break;

                    case MsQuicNative.ConnectionEvent.DatagramReceived:
                    {
                        // Datagram 是**不可靠**通道：拿到的报文即一整帧（没有长度前缀）。
                        var payload = CopyBuffer(evt.DatagramBufferPtr);
                        if (payload != null && payload.Length > 0)
                            EnqueueBounded(_recvQueue, payload, "recv", ref _recvQueueDropCount);
                        break;
                    }

                    case MsQuicNative.ConnectionEvent.ShutdownInitiatedByTransport:
                        Game.Logger?.Info("Quic",
                            $"QUIC transport shutdown: status=0x{evt.TransportShutdownStatus:X8} (常见原因：握手失败/证书不受信/对端无 UDP 监听)");
                        HandleLinkFailure(_generation, $"transport shutdown 0x{evt.TransportShutdownStatus:X8}");
                        break;

                    case MsQuicNative.ConnectionEvent.ShutdownInitiatedByPeer:
                        Game.Logger?.Info("Quic", $"QUIC peer shutdown: code={evt.PeerShutdownErrorCode}");
                        HandleLinkFailure(_generation, $"peer shutdown code={evt.PeerShutdownErrorCode}");
                        break;

                    case MsQuicNative.ConnectionEvent.ShutdownComplete:
                        // 到这里句柄才允许关闭（msquic 的既定流程）。连接回调里可以关两者。
                        CloseHandlesQuietly();
                        break;
                }
            }
            catch (Exception e)
            {
                // 回调里抛异常会穿过原生栈 —— 必须吞掉并留痕，否则表现为随机崩溃。
                Game.Logger?.Error("Quic", $"connection callback error: {e.Message}", e);
            }

            return 0;
        }

        private uint OnStreamEvent(IntPtr stream, IntPtr context, ref MsQuicNative.StreamEventData evt)
        {
            try
            {
                MsQuicRuntime.Trace($"stream-event type={evt.Type}");
                switch (evt.Type)
                {
                    case MsQuicNative.StreamEvent.StartComplete:
                        if (Failed(evt.StartStatus, "StreamStart"))
                            HandleLinkFailure(_generation, $"stream start failed: 0x{evt.StartStatus:X8}");
                        break;

                    case MsQuicNative.StreamEvent.Receive:
                    {
                        // 这里的字节由 msquic 持有，回调返回后即失效 —— 必须**先拷出来**。
                        int copied;
                        lock (_rxGate)
                        {
                            copied = AppendBuffers(evt.ReceiveBuffersPtr, evt.ReceiveBufferCount);
                        }

                        MsQuicRuntime.Trace(
                            $"recv totalLen={evt.ReceiveTotalLength} buffers={evt.ReceiveBufferCount} copied={copied}");

                        // ★★ 这里**绝对不要**调 StreamReceiveComplete ★★
                        //
                        // 它只用于**挂起式**收包：即回调里返回 QUIC_STATUS_PENDING、
                        // 之后再把缓冲还给 msquic 的那种流程（此时才需要 StreamReceiveComplete +
                        // StreamReceiveSetEnabled(true)）。我们上面已经把数据同步拷进托管内存，
                        // 回调返回 SUCCESS 就等于"已消费完毕"——**再调一次会把 msquic 的收包记账写坏**。
                        //
                        // 代价不是立刻报错，而是**之后关闭连接时原生断言崩溃**：
                        // `Faulting module: msquic.dll`，异常码 0xC0000420（STATUS_ASSERTION_FAILURE），
                        // 在 Unity 里表现为"编辑器直接消失"（崩了 3 次）。
                        // 这也解释了为什么"只连不发的用例永远不崩"——它根本没收到过流数据。
                        //
                        // 真踩过的排查路径（保留给后人）：控制台试验台逐步二分 ⇒ 崩点随"是否收到过数据"变化
                        // ⇒ 打印 totalLen/copied 发现记账数值本身是对的 ⇒ 才怀疑"这个 API 本就不该在这里调"。
                        lock (_rxGate)
                        {
                            ExtractFrames();
                        }
                        break;
                    }

                    case MsQuicNative.StreamEvent.SendComplete:
                        // 发送完成（或取消）：msquic 不再引用我们的缓冲，这里释放全部三段原生内存。
                        FreeSendContext(evt.SendClientContext);
                        break;

                    case MsQuicNative.StreamEvent.PeerSendShutdown:
                        // 对端关闭了发送方向：可靠链路已不可用，按断开处理（与 TCP 收到 EOF 等价）。
                        HandleLinkFailure(_generation, "peer closed stream");
                        break;

                    case MsQuicNative.StreamEvent.PeerSendAborted:
                    case MsQuicNative.StreamEvent.PeerReceiveAborted:
                        HandleLinkFailure(_generation, "peer aborted stream");
                        break;

                    case MsQuicNative.StreamEvent.ShutdownComplete:
                        // 流回调里**只关流句柄**：连接句柄要留给连接的 SHUTDOWN_COMPLETE
                        //（提前关连接会让 msquic 后续仍投递的连接事件落在已释放句柄上）。
                        CloseStreamHandle();
                        break;
                }
            }
            catch (Exception e)
            {
                Game.Logger?.Error("Quic", $"stream callback error: {e.Message}", e);
            }

            return 0;
        }

        // ------------------------------------------------------------------ 内部实现

        private bool OpenFirstStream()
        {
            var openStream = _api.Fn<MsQuicNative.StreamOpenFn>(_api.StreamOpen);
            // 流的 context 同样传 this 的 GCHandle（流的静态回调也靠它反查连接）
            var status = openStream(_connection, 0, _streamCallback,
                _selfHandle.IsAllocated ? GCHandle.ToIntPtr(_selfHandle) : IntPtr.Zero, out _stream);
            if (Failed(status, "StreamOpen"))
            {
                HandleLinkFailure(_generation, $"StreamOpen failed: 0x{status:X8}");
                return false;
            }

            var startStream = _api.Fn<MsQuicNative.StreamStartFn>(_api.StreamStart);
            // IMMEDIATE：立刻告知对端"流已开"——服务端 AcceptStream 在等这个信号。
            status = startStream(_stream, 0x0001);
            if (Failed(status, "StreamStart"))
            {
                HandleLinkFailure(_generation, $"StreamStart failed: 0x{status:X8}");
                return false;
            }

            return true;
        }

        private void StartWorkerThreads()
        {
            _running = true;
            var sendThread = new Thread(SendLoop) { IsBackground = true, Name = "CloverNet-QuicSend" };
            sendThread.Start();
        }

        /// <summary>发送线程：串行发送可靠帧 + Datagram，并在空闲时补保活帧。</summary>
        private void SendLoop()
        {
            var gen = _generation;
            try
            {
                while (_running && _generation == gen)
                {
                    _sendSignal.Wait(SendLoopWaitMs);
                    if (!_running || _generation != gen)
                        return;

                    while (_sendQueue.TryDequeue(out var frame))
                    {
                        Interlocked.Decrement(ref _sendQueueFrames); // 背压计数：出队 -1
                        if (!SendStreamFrame(frame))
                        {
                            HandleLinkFailure(gen, "stream send failed");
                            return;
                        }
                    }

                    while (_datagramQueue.TryDequeue(out var dgram))
                    {
                        if (!SendDatagram(dgram))
                        {
                            // Datagram 失败不视为链路故障（可能只是对端未协商 Datagram / 超出单包上限）：
                            // 不可靠语义本就允许丢包，只留痕。
                            Game.Logger?.Warn("Quic", $"datagram send dropped: {dgram.Length} bytes");
                        }
                    }

                    // 保活：N 毫秒内没有任何发送就补一帧（见类注释：服务端按应用层活跃度判空闲）。
                    var idleMs = (Stopwatch.GetTimestamp() - _lastSendTicks) * 1000 / Stopwatch.Frequency;
                    if (idleMs >= HeartbeatIntervalMs)
                    {
                        if (!SendStreamFrame(QuicStreamFraming.Encode(Array.Empty<byte>(), KeepAliveMsgId)))
                        {
                            HandleLinkFailure(gen, "keepalive failed");
                            return;
                        }
                    }
                }
            }
            catch
            {
                // 退出阶段句柄已被并发关闭：按链路失败收尾
                HandleLinkFailure(gen, "send loop aborted");
            }
        }

        /// <summary>
        /// 把一帧**已编码**的流帧（<c>[4B 大端长度][客户端帧]</c>，由 <see cref="Send"/> /
        /// 保活路径编码）交给 msquic 发送。发送线程不再二次编码/分配。
        /// </summary>
        private bool SendStreamFrame(byte[] encodedFrame)
        {
            lock (_sendGate)
            {
                if (!_running || _stream == IntPtr.Zero)
                    return false;

                // 数据与缓冲描述符都放**原生堆**：msquic 异步发送，直到 SEND_COMPLETE 才会读它们。
                // 把描述符放在托管栈上会读到垃圾（症状：服务端日志 `quic read: frame too large: 1351873247`）。
                var ctx = AllocSendContext(encodedFrame, out var descPtr);
                var send = _api.Fn<MsQuicNative.StreamSendFn>(_api.StreamSend);
                var status = send(_stream, descPtr, 1, 0, ctx);
                if (Failed(status, "StreamSend"))
                {
                    // 发送未被排队 ⇒ 不会有 SEND_COMPLETE；但为了防"msquic 仍然回调"，
                    // FreeSendContext 是幂等的（见 SendContext.Released），两边都调不冲突。
                    FreeSendContext(ctx);
                    return false;
                }
            }

            _lastSendTicks = Stopwatch.GetTimestamp();
            return true;
        }

        private bool SendDatagram(byte[] payload)
        {
            if (!_datagramSendEnabled || payload.Length == 0)
                return false;

            lock (_sendGate)
            {
                if (!_running || _connection == IntPtr.Zero)
                    return false;

                var ctx = AllocSendContext(payload, out var descPtr);
                var send = _api.Fn<MsQuicNative.DatagramSendFn>(_api.DatagramSend);
                var status = send(_connection, descPtr, 1, 0, ctx);
                if (Failed(status, "DatagramSend"))
                {
                    FreeSendContext(ctx);
                    return false;
                }
            }
            // 注意：成功路径**不能**在这里释放 —— msquic 会引用缓冲直到发送状态终结
            //（DATAGRAM_SEND_STATE_CHANGED 报 LOST_DISCARDED 及以后），释放点在 OnConnectionEvent 里。

            _lastSendTicks = Stopwatch.GetTimestamp();
            return true;
        }

        /// <summary>
        /// 把一帧数据放进原生内存并配套描述符，返回可作为 <c>ClientSendContext</c> 的 <c>GCHandle</c> 指针。
        /// 用 GCHandle 而不是裸原生块：回调里能安全地拿到托管对象做"是否已释放"判重。
        /// </summary>
        private static IntPtr AllocSendContext(byte[] bytes, out IntPtr descPtr)
        {
            var dataPtr = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, dataPtr, bytes.Length);
            descPtr = Marshal.AllocHGlobal(Marshal.SizeOf<MsQuicNative.QuicBuffer>());
            var descriptor = new MsQuicNative.QuicBuffer { Length = (uint)bytes.Length, Buffer = dataPtr };
            Marshal.StructureToPtr(descriptor, descPtr, false);

            var ctx = new SendContext { DataPtr = dataPtr, DescPtr = descPtr };
            return GCHandle.ToIntPtr(GCHandle.Alloc(ctx));
        }

        /// <summary>
        /// 释放一次发送占用的资源（数据 + 描述符 + GCHandle）。**幂等**：重复调用安全返回。
        /// 幂等性是硬要求：发送失败路径与 SEND_COMPLETE 回调都会调它，两者可能都发生。
        /// </summary>
        private static void FreeSendContext(IntPtr ctxPtr)
        {
            if (ctxPtr == IntPtr.Zero)
                return;

            // 诊断开关（CLOVER_QUIC_NOFREE=1）：故意不释放发送缓冲，用来判定
            // "崩溃是否由释放时机引起"。仅在排查时打开，正常路径不读这个变量。
            if (MsQuicRuntime.NoFreeSendBuffers)
                return;

            try
            {
                var handle = GCHandle.FromIntPtr(ctxPtr);
                if (!(handle.Target is SendContext ctx))
                    return;
                if (Interlocked.Exchange(ref ctx.Released, 1) == 1)
                    return; // 已经释放过（双路径竞争），直接返回

                if (ctx.DataPtr != IntPtr.Zero)
                    Marshal.FreeHGlobal(ctx.DataPtr);
                if (ctx.DescPtr != IntPtr.Zero)
                    Marshal.FreeHGlobal(ctx.DescPtr);
                handle.Free();
            }
            catch (Exception e)
            {
                // 双释放防护的兜底：句柄已释放时 GCHandle.Target 会**抛异常**而不是返回 null
                //（发送失败路径与发送完成回调可能都会走到这里）。异常若穿出：
                // 回调线程里会把一次重复释放打成 Error 噪声，发送线程里会被 SendLoop 误判为
                // "send loop aborted" 的链路失败——两者都不该发生。这里就地吞掉并留痕。
                Game.Logger?.Warn("Quic", $"FreeSendContext 重复/异常释放已忽略: {e.Message}");
            }
        }

        /// <summary>把 msquic 回调给的缓冲数组拷进托管内存；返回实际拷贝的字节数（用于记账诊断）。</summary>
        private int AppendBuffers(IntPtr buffersPtr, uint count)
        {
            if (buffersPtr == IntPtr.Zero || count == 0)
                return 0;

            var copied = 0;
            var size = Marshal.SizeOf<MsQuicNative.QuicBuffer>();
            for (uint i = 0; i < count; i++)
            {
                var b = Marshal.PtrToStructure<MsQuicNative.QuicBuffer>(buffersPtr + (int)(i * (uint)size));
                if (b.Buffer == IntPtr.Zero || b.Length == 0)
                    continue;

                // 复用 scratch（旧实现每个 QUIC_BUFFER 都 new byte[] 一次），再按有效长度段追加 ——
                // 保留一次中间拷贝（List<byte> 语义所需），但消掉了收包热路径的每包分配。
                if (_copyScratch.Length < b.Length)
                    _copyScratch = new byte[b.Length];
                Marshal.Copy(b.Buffer, _copyScratch, 0, (int)b.Length);
                _rxBuffer.AddRange(new ArraySegment<byte>(_copyScratch, 0, (int)b.Length));
                copied += (int)b.Length;
            }

            return copied;
        }

        /// <summary>从重组缓冲里按 <c>[4B 大端长度]</c> 切出完整帧（切帧逻辑本身是纯函数，见 <see cref="QuicStreamFraming"/>）。</summary>
        private void ExtractFrames()
        {
            var frames = new List<byte[]>();
            QuicStreamFraming.Extract(_rxBuffer, frames, out var badLength, MaxFrameSize);

            // 先按错误收尾、后交付已解析帧：badLength 说明流已错位（继续解析只会读出垃圾），
            // 按链路失败收尾；但错位点**之前**已完整解析出的合法帧仍属"已收到的数据"，
            // 不能跟着被静默丢弃。HandleLinkFailure 会清空收包队列，所以顺序必须反过来先收尾。
            if (badLength)
                HandleLinkFailure(_generation, "quic frame length invalid (stream desynced)");

            foreach (var frame in frames)
            {
                // 前几帧打一条 Info：线上问题里"到底收没收到"是最关键的一分叉，
                // 全量打会刷屏，只打前 N 帧既够定位又不吵（发出去的帧同理）。
                if (_recvLogBudget > 0)
                {
                    _recvLogBudget--;
                    Game.Logger?.Info("Quic", $"recv frame len={frame.Length}（首帧诊断，剩余 {_recvLogBudget} 条）");
                }

                EnqueueBounded(_recvQueue, frame, "recv", ref _recvQueueDropCount);
            }
        }

        /// <summary>拷贝单个 <c>QUIC_BUFFER</c>（Datagram 用）。</summary>
        private static byte[] CopyBuffer(IntPtr bufferPtr)
        {
            if (bufferPtr == IntPtr.Zero)
                return null;
            var b = Marshal.PtrToStructure<MsQuicNative.QuicBuffer>(bufferPtr);
            if (b.Buffer == IntPtr.Zero || b.Length == 0)
                return null;
            var tmp = new byte[b.Length];
            Marshal.Copy(b.Buffer, tmp, 0, (int)b.Length);
            return tmp;
        }

        private void ShutdownConnection(uint flags)
        {
            if (_connection == IntPtr.Zero)
                return;
            try
            {
                // 诊断开关（CLOVER_QUIC_SKIP_SHUTDOWN=1）：跳过 ConnectionShutdown，直接走 ConnectionClose，
                // 用于判定"崩溃是否由 shutdown 序列本身引起"。
                if (MsQuicRuntime.SkipConnectionShutdown)
                {
                    MsQuicRuntime.Trace("ConnectionShutdown(skipped by CLOVER_QUIC_SKIP_SHUTDOWN)");
                    return;
                }

                MsQuicRuntime.Trace($"ConnectionShutdown(begin) flags={flags} conn=0x{_connection.ToInt64():X}");
                var shutdown = _api.Fn<MsQuicNative.ConnectionShutdownFn>(_api.ConnectionShutdown);
                shutdown(_connection, flags, 0);
                MsQuicRuntime.Trace("ConnectionShutdown(ok)");
            }
            catch (Exception e)
            {
                Game.Logger?.Warn("Quic", $"ConnectionShutdown failed: {e.Message}");
            }
        }

        /// <summary>
        /// 关闭句柄。**幂等**（连接回调与流回调都会走到这里，且可能来自不同线程）。
        /// 由 SHUTDOWN_COMPLETE 触发或主动断开时调用；顺序遵循 msquic：先流后连接。
        /// </summary>
        /// <summary>
        /// 关闭流句柄。**幂等**，且与发送路径用同一把 <see cref="_sendGate"/> 串行
        /// （lock 用 Monitor，同线程可重入，因此 msquic 若在本线程内联回调也不会自锁）。
        /// </summary>
        private void CloseStreamHandle()
        {
            if (Interlocked.Exchange(ref _streamClosed, 1) == 1)
                return;

            lock (_sendGate)
            {
                try
                {
                    if (_stream != IntPtr.Zero)
                    {
                        MsQuicRuntime.Trace($"StreamClose(begin) stream=0x{_stream.ToInt64():X}");
                        _api.Fn<MsQuicNative.StreamCloseFn>(_api.StreamClose)(_stream);
                        MsQuicRuntime.Trace("StreamClose(ok)");
                    }
                }
                catch (Exception e)
                {
                    Game.Logger?.Warn("Quic", $"StreamClose failed: {e.Message}");
                }

                _stream = IntPtr.Zero;
            }
        }

        /// <summary>关闭连接句柄。幂等；串行理由同 <see cref="CloseStreamHandle"/>。</summary>
        private void CloseConnectionHandle()
        {
            if (Interlocked.Exchange(ref _connectionClosed, 1) == 1)
                return;

            lock (_sendGate)
            {
                try
                {
                    if (_connection != IntPtr.Zero)
                    {
                        MsQuicRuntime.Trace($"ConnectionClose(begin) conn=0x{_connection.ToInt64():X}");
                        _api.Fn<MsQuicNative.ConnectionCloseFn>(_api.ConnectionClose)(_connection);
                        MsQuicRuntime.Trace("ConnectionClose(ok)");
                    }
                }
                catch (Exception e)
                {
                    Game.Logger?.Warn("Quic", $"ConnectionClose failed: {e.Message}");
                }

                _connection = IntPtr.Zero;
            }
        }

        /// <summary>关闭两个句柄（顺序：先流后连接）。由连接的 SHUTDOWN_COMPLETE 或新一轮连接触发。</summary>
        private void CloseHandlesQuietly()
        {
            _connectWatchdog?.Dispose();
            _connectWatchdog = null;
            CloseStreamHandle();
            CloseConnectionHandle();
            State = ConnectionState.Disconnected;
            // 句柄关完才能放掉 GCHandle：msquic 保证 ConnectionClose 之后不再回调；
            // 万一还有排队的流回调，ResolveFrom 会因句柄已释放而返回 null（已兜住，不会崩）。
            if (_selfHandle.IsAllocated)
                _selfHandle.Free();
            MsQuicRuntime.Unregister(this);
        }

        /// <summary>
        /// **同步**强制关闭（关闭路径的兜底）：立刻停发送线程、发起 shutdown、最多等 500ms 让
        /// SHUTDOWN_COMPLETE 把句柄关掉，超时就自己强关。
        ///
        /// 用途：① 测试 TearDown（断言失败时后续代码不会执行，必须由 TearDown 兜底）；
        /// ② 域卸载/退出前（<see cref="MsQuicRuntime.ShutdownAllConnections"/>）。
        /// 为什么必须"等"：msquic 只有在句柄真正关闭后才保证不再回调；
        /// 不等就卸载脚本域 ⇒ 原生回调打到已卸载的托管代码 ⇒ 进程崩溃。
        /// </summary>
        internal void CloseNow(string why)
        {
            try
            {
                Interlocked.Increment(ref _generation); // 让挂起的看门狗/回调失效
                _running = false;
                _linkDown = 1;

                if (_connection != IntPtr.Zero && _api.ConnectionShutdown != IntPtr.Zero)
                    ShutdownConnection(1 /* QUIC_CONNECTION_SHUTDOWN_FLAG_SILENT：不等对端，立即关 */);

                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 500 && (_stream != IntPtr.Zero || _connection != IntPtr.Zero))
                    Thread.Sleep(10);

                CloseHandlesQuietly();
            }
            catch (Exception e)
            {
                Game.Logger?.Warn("Quic", $"CloseNow({why}) 失败: {e.Message}");
            }
        }

        /// <summary>
        /// 链路失败统一入口：代际 + 一次性标记去重后，关闭资源并回调 <see cref="Disconnected"/>。
        /// 与 <see cref="TcpConnection"/> 完全同构：上层（NetworkManager 的降级链与退避重连）依赖这个回调。
        /// </summary>
        private void HandleLinkFailure(int gen, string reason)
        {
            if (gen != _generation || gen == 0)
                return;
            if (Interlocked.Exchange(ref _linkDown, 1) == 1)
                return;

            _running = false;        // 先停发送线程，再动句柄（顺序不能反）
            _datagramSendEnabled = false; // 链路已断：Datagram 支持状态不再有效（防上层据 stale true 继续投递）
            ShutdownConnection(0);
            ClearQueues();
            Game.Logger?.Info("Quic", $"QUIC link down: {reason}");
            try
            {
                Disconnected?.Invoke();
            }
            catch (Exception e)
            {
                Game.Logger?.Error("Quic", $"disconnected callback error: {e.Message}", e);
            }
        }

        private void RaiseConnected()
        {
            try
            {
                Connected?.Invoke();
            }
            catch (Exception e)
            {
                Game.Logger?.Error("Quic", $"connected callback error: {e.Message}", e);
            }
        }

        private void ClearQueues()
        {
            while (_recvQueue.TryDequeue(out _)) { }
            while (_sendQueue.TryDequeue(out _)) { }
            while (_datagramQueue.TryDequeue(out _)) { }
            Interlocked.Exchange(ref _sendQueueFrames, 0); // 背压计数与队列一起清零
            lock (_rxGate)
            {
                _rxBuffer.Clear();
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

        /// <summary>QUIC_STATUS 失败判定（Windows/Schannel 版是 HRESULT 语义：&lt;0 为失败）。</summary>
        private static bool Failed(uint status, string what)
        {
            if ((int)status >= 0)
                return false;
            Game.Logger?.Warn("Quic", $"{what} returned 0x{status:X8}");
            return true;
        }
    }
}
