using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace CloverEngine
{
    /// <summary>
    /// 弱网模拟器（调试用）：单向延迟 + 抖动 + 丢包。
    ///
    /// 为什么放在传输层之上而不是业务里：延迟/丢包必须在**收发边界**生效才有意义
    /// （改业务代码模拟出来的是「业务慢」，不是「网络差」）。装饰器 <see cref="SimulatedTransport"/>
    /// 包在真实传输外面，因此**可靠线路**（TCP / WebSocket）共用同一套模拟参数；
    /// 裸 UDP 与 QUIC Datagram 通道不走该装饰器，不受模拟影响，
    /// 关掉时只剩一次虚调用，不改变任何线上行为。
    ///
    /// 丢包对可靠线路的含义：TCP 本身会重传，应用层「丢一帧」等价于**该请求从未发出**，
    /// 用来验证超时与重试路径；对裸 UDP 则与真实丢包一致。
    /// </summary>
    public sealed class NetworkSimulator
    {
        private readonly object _randLock = new object();
        private readonly Random _rand;

        private int _enabled;
        private int _latencyMs;
        private int _jitterMs;
        private int _lossRatePermille; // 千分比，避免用 float 做跨线程读写
        private long _dropped;
        private long _delayed;

        /// <summary>最近一次丢包日志时间（Stopwatch ticks），用于降频。</summary>
        private long _lastDropLogTicks;

        /// <summary>
        /// 构造：默认用时间种子（丢包/抖动序列不可复现）。需要复现同一弱网场景（测试 / 问题定位）时，
        /// 用 <see cref="NetworkSimulator(int?)"/> 传固定种子，把随机序列钉死。
        /// </summary>
        public NetworkSimulator() : this(null)
        {
        }

        /// <summary>构造（可指定随机种子）。</summary>
        /// <param name="seed">随机种子；null = 时间种子（不可复现）</param>
        public NetworkSimulator(int? seed)
        {
            _rand = seed.HasValue ? new Random(seed.Value) : new Random();
        }

        /// <summary>是否启用模拟。</summary>
        public bool Enabled => Volatile.Read(ref _enabled) != 0;

        /// <summary>单向延迟（毫秒）。</summary>
        public int LatencyMs => Volatile.Read(ref _latencyMs);

        /// <summary>延迟抖动（±毫秒）。</summary>
        public int JitterMs => Volatile.Read(ref _jitterMs);

        /// <summary>丢包率，取值 0~1。</summary>
        public float LossRate => Volatile.Read(ref _lossRatePermille) / 1000f;

        /// <summary>累计被模拟丢弃的帧数。</summary>
        public long DroppedCount => Interlocked.Read(ref _dropped);

        /// <summary>累计被延迟过的帧数。</summary>
        public long DelayedCount => Interlocked.Read(ref _delayed);

        /// <summary>
        /// 设置模拟参数并启用。参数越界时按合法区间收敛（延迟 0~60000ms、抖动 0~延迟、丢包 0~1）。
        /// </summary>
        /// <param name="latencyMs">单向延迟毫秒数。</param>
        /// <param name="jitterMs">抖动毫秒数（上下浮动幅度）。</param>
        /// <param name="lossRate">丢包率 0~1。</param>
        public void Configure(int latencyMs, int jitterMs = 0, float lossRate = 0f)
        {
            latencyMs = Math.Max(0, Math.Min(60_000, latencyMs));
            jitterMs = Math.Max(0, Math.Min(latencyMs, jitterMs));
            var permille = (int)Math.Round(Math.Max(0f, Math.Min(1f, lossRate)) * 1000f);

            Volatile.Write(ref _latencyMs, latencyMs);
            Volatile.Write(ref _jitterMs, jitterMs);
            Volatile.Write(ref _lossRatePermille, permille);
            Volatile.Write(ref _enabled, 1);
        }

        /// <summary>关闭模拟并清零参数（累计计数保留，便于观察本轮共丢/延迟了多少）。</summary>
        public void Disable()
        {
            Volatile.Write(ref _enabled, 0);
            Volatile.Write(ref _latencyMs, 0);
            Volatile.Write(ref _jitterMs, 0);
            Volatile.Write(ref _lossRatePermille, 0);
        }

        /// <summary>重置累计计数（不影响当前参数与启用状态）。</summary>
        public void ResetCounters()
        {
            Interlocked.Exchange(ref _dropped, 0);
            Interlocked.Exchange(ref _delayed, 0);
        }

        /// <summary>用于日志与调试面板的单行描述。</summary>
        public string Describe()
        {
            if (!Enabled)
                return "关闭";

            var loss = LossRate;
            return $"延迟 {LatencyMs}±{JitterMs}ms，丢包 {loss * 100:0.#}%（已丢 {DroppedCount}，已延迟 {DelayedCount}）";
        }

        /// <summary>本条是否应模拟丢弃（禁用时恒 false）。</summary>
        internal bool ShouldDrop()
        {
            if (!Enabled)
                return false;

            var permille = Volatile.Read(ref _lossRatePermille);
            if (permille <= 0)
                return false;

            bool drop;
            lock (_randLock)
            {
                drop = _rand.Next(1000) < permille;
            }

            if (drop)
                Interlocked.Increment(ref _dropped);
            return drop;
        }

        /// <summary>本条应延迟的毫秒数（禁用时为 0；正抖动不会把延迟压成负数）。</summary>
        internal int NextDelayMs()
        {
            if (!Enabled)
                return 0;

            var baseMs = Volatile.Read(ref _latencyMs);
            var jitter = Volatile.Read(ref _jitterMs);
            if (baseMs <= 0 && jitter <= 0)
                return 0;

            var jitterOffset = 0;
            if (jitter > 0)
            {
                lock (_randLock)
                {
                    jitterOffset = _rand.Next(-jitter, jitter + 1);
                }
            }

            var delay = Math.Max(0, baseMs + jitterOffset);
            if (delay > 0)
                Interlocked.Increment(ref _delayed);
            return delay;
        }

        /// <summary>
        /// 丢包日志降频：高丢包率下逐包打日志会刷屏又拖慢帧率；返回 true 表示本条丢包可以打日志
        /// （保证最多每秒一条，日志里带累计计数供观察总量）。
        /// </summary>
        internal bool ShouldLogDrop()
        {
            var now = Stopwatch.GetTimestamp();
            var last = Interlocked.Read(ref _lastDropLogTicks);
            if ((now - last) * 1000 / Stopwatch.Frequency < 1000)
                return false;
            return Interlocked.CompareExchange(ref _lastDropLogTicks, now, last) == last;
        }
    }

    /// <summary>
    /// 传输装饰器：按 <see cref="NetworkSimulator"/> 的设置对收发帧施加延迟与丢包。
    /// 未启用时逐个成员直通内层实现（一次虚调用开销）。
    /// </summary>
    internal sealed class SimulatedTransport : ITransportConnection, IUnreliableTransport
    {
        /// <summary>收包延迟项：帧 + 到期时间（Stopwatch ticks）。</summary>
        private struct DelayedPacket
        {
            /// <summary>帧字节。</summary>
            public byte[] Data;

            /// <summary>到期时间（Stopwatch ticks）。</summary>
            public long DueTicks;
        }

        /// <summary>发送延迟项：帧 + 到期时间（Stopwatch ticks）。</summary>
        private struct DelayedSend
        {
            /// <summary>帧字节。</summary>
            public byte[] Data;

            /// <summary>到期时间（Stopwatch ticks）。</summary>
            public long DueTicks;
        }

        /// <summary>发送泵空闲轮询间隔（毫秒）：兼顾"零延迟帧尽快写出"与"Disconnect 后尽快退出"。</summary>
        private const int PumpIdleWaitMs = 10;

        private readonly ITransportConnection _inner;
        private readonly NetworkSimulator _sim;
        private readonly ConcurrentQueue<DelayedPacket> _delayed = new();

        /// <summary>待发送队列：所有发送（含零延迟）经它由单一泵**顺序**写出，保证可靠线路的发送不乱序。</summary>
        private readonly ConcurrentQueue<DelayedSend> _pendingSend = new();

        /// <summary>发送泵是否已启动（0/1，Interlocked 使用）。</summary>
        private int _sendPumpRunning;

        /// <summary>发送泵停止标志：Disconnect 置位（清队列后泵退出）；ConnectAsync 复位。</summary>
        private volatile bool _sendPumpStop;

        /// <summary>构造装饰器。</summary>
        /// <param name="inner">被装饰的真实传输。</param>
        /// <param name="sim">模拟参数来源（可为 null，等于不模拟）。</param>
        public SimulatedTransport(ITransportConnection inner, NetworkSimulator sim)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _sim = sim;
        }

        /// <summary>线路类型（透传）。</summary>
        public TransportKind Kind => _inner.Kind;

        /// <summary>连接状态（透传）。</summary>
        public ConnectionState State => _inner.State;

        /// <summary>连接建立回调（透传）。</summary>
        public event Action Connected
        {
            add => _inner.Connected += value;
            remove => _inner.Connected -= value;
        }

        /// <summary>链路断开回调（透传）。</summary>
        public event Action Disconnected
        {
            add => _inner.Disconnected += value;
            remove => _inner.Disconnected -= value;
        }

        /// <summary>发起连接（透传；连接本身不模拟延迟）。</summary>
        /// <param name="addr">目标地址。</param>
        public void ConnectAsync(string addr)
        {
            // 新一轮连接：重新武装发送泵（上一轮 Disconnect 已把它停掉）
            _sendPumpStop = false;
            _inner.ConnectAsync(addr);
        }

        /// <summary>断开（透传）并丢弃尚未到期的延迟帧/未发出的延迟发送。</summary>
        public void Disconnect()
        {
            // 先停发送泵再清两个方向：避免清空后泵仍向已关闭的内层传输写入
            _sendPumpStop = true;
            while (_delayed.TryDequeue(out _)) { }
            while (_pendingSend.TryDequeue(out _)) { }
            _inner.Disconnect();
        }

        /// <summary>发送：按丢包率丢弃，或延迟指定毫秒后写出（顺序由发送泵保证）。</summary>
        /// <param name="data">源缓冲区。</param>
        /// <param name="offset">起始偏移。</param>
        /// <param name="count">字节数。</param>
        public void Send(byte[] data, int offset, int count)
        {
            if (_sim == null || !_sim.Enabled)
            {
                _inner.Send(data, offset, count);
                return;
            }

            if (_sim.ShouldDrop())
            {
                // 降频：高丢包率下逐包打日志会刷屏又拖慢帧率（日志里带累计计数供观察总量）
                if (_sim.ShouldLogDrop())
                    Game.Logger?.Warn("Network",
                        $"sim: outbound frame dropped (loss simulation)，累计丢 {_sim.DroppedCount} 帧");
                return;
            }

            // 延迟发送必须拷贝：调用方的缓冲区可能在返回后被复用。
            // 所有帧（含零延迟）都经同一队列由单一泵按入队顺序写出 —— 否则逐帧独立
            // Task.Run + Task.Delay 会让后发的帧可能先写出（在 TCP 等可靠线路上造成应用层乱序），
            // 且该 Task 不被跟踪，Disconnect 清空后仍会向已关闭的内层传输写入。
            var copy = new byte[count];
            Buffer.BlockCopy(data, offset, copy, 0, count);
            _pendingSend.Enqueue(new DelayedSend
            {
                Data = copy,
                DueTicks = Stopwatch.GetTimestamp() + _sim.NextDelayMs() * Stopwatch.Frequency / 1000,
            });
            EnsureSendPump();
        }

        /// <summary>不可靠发送能力（透传内层；内层没有不可靠通道时恒 false）。</summary>
        public bool SupportsUnreliable =>
            _inner is IUnreliableTransport unreliable && unreliable.SupportsUnreliable;

        /// <summary>
        /// 不可靠发送：透传内层且**不施加弱网模拟**（裸 UDP 与 QUIC Datagram 通道不走本装饰器的
        /// 延迟/丢包——对不可靠通道再叠一层模拟丢包会失真）。内层没有不可靠通道时留痕并丢弃。
        /// </summary>
        public void SendUnreliable(byte[] data, int offset, int count)
        {
            if (_inner is IUnreliableTransport unreliable)
            {
                unreliable.SendUnreliable(data, offset, count);
                return;
            }

            Game.Logger?.Warn("Network", "sim: inner transport has no unreliable channel, SendUnreliable dropped");
        }

        /// <summary>确保发送泵在运行（首次入队时懒启动；Disconnect 后由下一次 ConnectAsync 复位重启）。</summary>
        private void EnsureSendPump()
        {
            if (Interlocked.CompareExchange(ref _sendPumpRunning, 1, 0) != 0)
                return;
            Task.Run(SendPumpLoop);
        }

        /// <summary>
        /// 发送泵：单线程按入队顺序等待每帧到期后写出（保序 = 可靠线路的语义）。
        /// 退出条件：<see cref="_sendPumpStop"/> 置位（Disconnect），队列由 Disconnect 清空。
        /// </summary>
        private void SendPumpLoop()
        {
            try
            {
                while (!_sendPumpStop)
                {
                    if (!_pendingSend.TryPeek(out var head))
                    {
                        Thread.Sleep(PumpIdleWaitMs);
                        continue;
                    }

                    var waitMs = (head.DueTicks - Stopwatch.GetTimestamp()) * 1000 / Stopwatch.Frequency;
                    if (waitMs > 0)
                    {
                        // 小步睡（不一次睡到到期）：Disconnect 置位后能尽快退出
                        Thread.Sleep((int)Math.Min(waitMs, PumpIdleWaitMs));
                        continue;
                    }

                    if (_pendingSend.TryDequeue(out head) && !_sendPumpStop)
                        _inner.Send(head.Data, 0, head.Data.Length);
                }
            }
            catch (Exception e)
            {
                Game.Logger?.Warn("Network", $"sim: send pump aborted: {e.GetType().Name}: {e.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _sendPumpRunning, 0);
            }
        }

        /// <summary>主线程驱动（透传）。</summary>
        public void Tick()
        {
            _inner.Tick();
        }

        /// <summary>
        /// 收包：先放行到期的延迟帧，再从内层取一帧并按模拟参数决定「立即返回 / 丢弃 / 延后」。
        /// 返回 false 表示本轮没有可交付的帧（调用方的 while 循环随即结束，下个 Tick 再试）。
        /// </summary>
        /// <param name="data">输出的帧字节。</param>
        public bool TryTakePacket(out byte[] data)
        {
            data = null;

            // 1) 到期的延迟帧优先放行（保持 FIFO，避免乱序）
            while (_delayed.TryPeek(out var head))
            {
                if (head.DueTicks > Stopwatch.GetTimestamp())
                    break;
                if (_delayed.TryDequeue(out head))
                {
                    data = head.Data;
                    return true;
                }
            }

            // 2) 从内层取一帧
            if (!_inner.TryTakePacket(out var packet))
                return false;

            if (_sim == null || !_sim.Enabled)
            {
                data = packet;
                return true;
            }

            if (_sim.ShouldDrop())
            {
                // 与发送侧同样降频：高丢包率下逐包打日志会刷屏又拖慢帧率
                if (_sim.ShouldLogDrop())
                    Game.Logger?.Warn("Network",
                        $"sim: inbound frame dropped (loss simulation)，累计丢 {_sim.DroppedCount} 帧");
                return false;
            }

            var delay = _sim.NextDelayMs();
            if (delay <= 0)
            {
                data = packet;
                return true;
            }

            _delayed.Enqueue(new DelayedPacket
            {
                Data = packet,
                DueTicks = Stopwatch.GetTimestamp() + delay * Stopwatch.Frequency / 1000,
            });
            return false;
        }
    }
}
