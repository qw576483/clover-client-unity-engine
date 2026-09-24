using System;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

namespace CloverEngine
{
    /// <summary>
    /// 局域网应答端实现（<see cref="ILanResponder"/>）：收到查询 → **单播回**一条含主机信息的应答。
    ///
    /// <para>
    /// 生命周期：
    /// <code>
    /// Start(self, options)
    ///   → 校验平台 / 参数 → 解析对外广播地址（Host 留空则取本机 IPv4）
    ///   → 绑 0.0.0.0:47777（固定端口，⛔ 与 LanBrowser 的临时端口不同）
    ///   → LanSocket 常驻收包（窗口 = 无，直到 Stop）
    ///   → 每条报文：TryParseQuery 校验 → 合法则回 BuildReply（**回给查询来源端点**）
    /// Stop()  → 关 socket → 收包线程 ≤ ReceiveTimeoutMs 内退出
    /// </code>
    /// </para>
    ///
    /// <para>
    /// <b>与既有约定的边界</b>（结构规则.md §五 N13）：不占 EMsg 消息号、不进 <see cref="IRouter"/>、
    /// 不碰 <c>TransportKind</c> / <see cref="CloverNet"/>；<b>不新增 asmdef</b>（仍属 <c>CloverEngine.Network</c>）。
    /// 收发层**复用** <c>LanSocket</c>（⛔ 不写第二套 UDP 收发）；协议编解码**复用** <c>LanProtocol</c>
    /// （⛔ 业务不再需要逐字复制线格式）。
    /// </para>
    ///
    /// <para>
    /// <b>四条边界都不抛</b>（见 <see cref="ILanResponder"/> 的注释）：平台不支持 / 端口被占用 →
    /// <see cref="Start"/> 返回 false 且 <see cref="LastError"/> 给出原因；重复 <see cref="Start"/> → 幂等；
    /// <see cref="Stop"/> 未启动 → 空操作。
    /// </para>
    /// </summary>
    internal sealed class LanResponder : ILanResponder
    {
        private const string LogTag = "Lan";

        /// <summary>可选字段（name / version / extra / auth）超长时截到这个字符数（只为让应答装进单包）。</summary>
        private const int FieldMaxChars = 48;

        /// <summary>
        /// 查询日志最小间隔（毫秒）：一轮 <c>Scan</c> 会同时打回环 + 广播 + 各网卡子网广播，
        /// 每个目标一份查询 ⇒ 不限频必然刷屏。
        /// </summary>
        private const int QueryLogIntervalMs = 1000;

        private static readonly object QueryLogLock = new object();

        private readonly Func<bool> _isSupported;
        private readonly Func<string> _unsupportedReason;

        /// <summary>对外广播的主机快照：主线程写（Start）、收包线程读（每条查询）⇒ volatile 保可见性。</summary>
        private volatile LanHostInfo _self;

        /// <summary>是否在跑：主线程写、收包线程读 ⇒ volatile。</summary>
        private volatile bool _running;

        private LanSocket _socket;
        private int _listeningPort;
        private string _lastError = string.Empty;
        private bool _disposed;

        private long _queries;
        private long _replies;
        private long _dropped;

        /// <summary>「非法包」告警只打一次（广播域里 47777 上会有各种系统噪声，全报必刷屏）。</summary>
        private int _dropLogged;

        private int _lastQueryLogAt;

        public LanResponder()
            : this(null, null)
        {
        }

        /// <summary>
        /// 单测注入点：换掉平台判定（<c>Application.platform</c> 在 Editor 里改不了，
        /// 没有这个缝就断言不了"不可用平台"的行为）。传 null = 走真实平台判定。
        /// 先例：<c>LanBrowser.BeginScanForTesting</c>。
        /// </summary>
        internal LanResponder(Func<bool> isSupported, Func<string> unsupportedReason)
        {
            _isSupported = isSupported ?? (() => LanCapabilities.IsSupported);
            _unsupportedReason = unsupportedReason ?? DefaultUnsupportedReason;
        }

        /// <inheritdoc/>
        public bool IsSupported => _isSupported();

        /// <inheritdoc/>
        public string UnsupportedReason => _isSupported() ? string.Empty : (_unsupportedReason() ?? string.Empty);

        /// <inheritdoc/>
        public bool IsRunning => _running;

        /// <inheritdoc/>
        public string LastError => _lastError;

        /// <inheritdoc/>
        public LanHostInfo Self => _self;

        /// <inheritdoc/>
        public int ListeningPort => _running ? _listeningPort : 0;

        /// <inheritdoc/>
        public long QueryCount => Interlocked.Read(ref _queries);

        /// <inheritdoc/>
        public long ReplyCount => Interlocked.Read(ref _replies);

        /// <inheritdoc/>
        public long DroppedCount => Interlocked.Read(ref _dropped);

        /// <inheritdoc/>
        public event Action<string> OnQuery;

        /// <inheritdoc/>
        public bool Start(LanHostInfo self, LanRespondOptions options = null)
        {
            if (_disposed)
            {
                _lastError = "应答端已释放（Dispose 之后不能再 Start；请重新 CreateResponder）";
                Game.Logger?.Warn(LogTag, _lastError);
                return false;
            }

            if (!IsSupported)
            {
                // 不支持是**环境事实**不是调用错误：与 ILanBrowser.Scan 同口径 —— 明确留痕、不抛
                //（调用方的"开主机"流程不该被平台差异打断，但也不能静默）。
                _lastError = UnsupportedReason;
                Game.Logger?.Warn(LogTag, $"不启动局域网应答端：{_lastError}");
                return false;
            }

            if (self == null)
            {
                _lastError = "self 为空：必须给一份要对外广播的主机信息";
                Game.Logger?.Error(LogTag, _lastError);
                return false;
            }

            if (!TryResolveAdvertised(self, out var advertised, out var resolveError))
            {
                _lastError = resolveError;
                Game.Logger?.Error(LogTag, _lastError);
                return false;
            }

            var port = options != null && options.Port > 0 ? options.Port : LanProtocol.DefaultPort;
            if (port < 1 || port > 65535)
            {
                _lastError = $"监听端口非法：{port}（须在 1~65535）";
                Game.Logger?.Error(LogTag, _lastError);
                return false;
            }

            if (_running)
            {
                // 幂等：参数照更新（名字 / 人数会变），socket 与收包线程不动。
                if (port != _listeningPort)
                {
                    Game.Logger?.Warn(LogTag,
                        $"应答端已在跑，端口 {port} 无法在运行中更换（仍监听 {_listeningPort}）：需先 Stop 再 Start");
                }

                _self = advertised;
                _lastError = string.Empty;
                Game.Logger?.Info(LogTag, $"局域网应答端已在跑，仅更新广播参数：{Describe()}");
                return true;
            }

            var socket = LanSocket.TryCreate(port, out var bindError);
            if (socket == null)
            {
                // 端口被占用：**不抛**，如实报错（同一台机器上只能有一个应答端）。
                _lastError = $"绑定 UDP {port} 失败：{bindError}（同一端口上已有应答端在跑？）";
                Game.Logger?.Error(LogTag, _lastError);
                return false;
            }

            // 先落状态再起线程：收包线程一启动就会读 _running / _self。
            _self = advertised;
            _listeningPort = port;
            _lastError = string.Empty;
            _dropLogged = 0;
            Interlocked.Exchange(ref _queries, 0);
            Interlocked.Exchange(ref _replies, 0);
            Interlocked.Exchange(ref _dropped, 0);
            _socket = socket;
            _running = true;

            // durationMs = 0 ⇒ 常驻（无窗口，直到 Stop）；onFinished 传 null（收尾由 Stop 负责）。
            socket.Start(0, (datagram, remote) => HandleDatagram(socket, datagram, remote), null);

            Game.Logger?.Info(LogTag,
                $"局域网应答端已启动：{Describe()}（端口 {port}，协议 {LanProtocol.QueryMagic} → {LanProtocol.ReplyMagic}）");
            return true;
        }

        /// <inheritdoc/>
        public void Stop()
        {
            var wasRunning = _running;
            _running = false;

            var socket = _socket;
            _socket = null;
            // 幂等：未启动 / 已停过时 socket 为 null，这里是空操作（且**绝不抛**）。
            socket?.Stop();

            if (wasRunning)
            {
                _lastError = string.Empty;
                Game.Logger?.Info(LogTag,
                    $"局域网应答端已停止：收到查询 {QueryCount} 条 / 回出应答 {ReplyCount} 条 / 丢弃非法 {DroppedCount} 条");
            }
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (_disposed)
                return;

            var wasRunning = _running;
            _disposed = true;
            Stop();

            if (wasRunning)
                Game.Logger?.Info(LogTag, "局域网应答端已释放");
        }

        /// <inheritdoc/>
        public string Describe()
        {
            var self = _self;
            if (!_running || self == null)
                return "局域网应答端未启动";

            return $"LAN host name=\"{self.Name}\" gateway={self.Address} " +
                   $"udp={(self.UdpPort > 0 ? self.UdpAddress : "未提供")} " +
                   $"players={self.Players}/{self.MaxPlayers} " +
                   $"收到查询={QueryCount} 回出={ReplyCount} 丢弃={DroppedCount}";
        }

        // ====================================================================
        //  收包 → 校验 → 回包
        // ====================================================================

        /// <summary>
        /// 处理一份收到的报文（跑在收包线程上，**必须不抛**）。
        /// 只回给**查询来源端点**：不做广播回包（否则 N 台主机互相扫描会变成广播风暴）。
        /// </summary>
        private void HandleDatagram(LanSocket socket, byte[] datagram, IPEndPoint remote)
        {
            if (!_running)
                return;   // Stop 之后到达的包：不再回（此刻 socket 也可能已关）

            if (!LanProtocol.TryParseQuery(datagram, datagram?.Length ?? 0, out var nonce, out var error))
            {
                Interlocked.Increment(ref _dropped);
                LogDropOnce(remote, error);
                return;
            }

            Interlocked.Increment(ref _queries);
            var from = remote == null ? "<未知>" : remote.ToString();
            NotifyQuery(from);
            LogQuery(from, nonce);

            var self = _self;
            if (self == null)
            {
                // 只可能出现在 Stop 与收包线程交错的瞬间；不回包并计数，不抛。
                Interlocked.Increment(ref _dropped);
                return;
            }

            byte[] reply;
            try
            {
                reply = LanProtocol.BuildReply(nonce, self);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _dropped);
                Game.Logger?.Warn(LogTag, $"构造应答报文失败：{ex.GetType().Name}: {ex.Message}");
                return;
            }

            if (reply.Length > LanProtocol.MaxDatagramBytes)
            {
                // 名字 / 透传串太长把包撑爆 ⇒ 对端会整包丢弃。截短可选字段重来一次（⛔ 不静默失败）。
                Game.Logger?.Warn(LogTag,
                    $"应答包 {reply.Length} 字节 > 上限 {LanProtocol.MaxDatagramBytes}" +
                    "（主机名 / 透传串太长？）→ 截短后重发");
                reply = LanProtocol.BuildReply(nonce, Shrink(self));
            }

            if (reply.Length > LanProtocol.MaxDatagramBytes)
            {
                Interlocked.Increment(ref _dropped);
                Game.Logger?.Warn(LogTag,
                    $"截短后仍超长（{reply.Length} 字节 > {LanProtocol.MaxDatagramBytes}），丢弃本次应答（来源 {from}）");
                return;
            }

            if (remote == null)
            {
                Interlocked.Increment(ref _dropped);
                Game.Logger?.Warn(LogTag, "应答目标端点缺失，丢弃本次应答");
                return;
            }

            if (socket.Send(reply, remote))
                Interlocked.Increment(ref _replies);
        }

        /// <summary>
        /// 把「谁的查询」投递到主线程后再触发 <see cref="OnQuery"/>（结构规则 §5.2 G2：
        /// 业务回调必须在主线程，而本方法跑在收包后台线程上）。
        /// 派发器为 null（编辑器测试里未 Launch）时直接执行 —— 同 <c>LanBrowser.PostOrRunUnconditional</c> 的写法。
        /// </summary>
        private void NotifyQuery(string from)
        {
            var handler = OnQuery;
            if (handler == null)
                return;

            var dispatcher = Game.Dispatcher;
            if (dispatcher != null)
                dispatcher.Post(() => InvokeQueryHandler(handler, from));
            else
                InvokeQueryHandler(handler, from);
        }

        private static void InvokeQueryHandler(Action<string> handler, string from)
        {
            try
            {
                handler(from);
            }
            catch (Exception ex)
            {
                // 订阅方的异常绝不能冒出收包线程（异常逃逸会杀进程）。
                Game.Logger?.Warn(LogTag, $"OnQuery 订阅回调异常（继续收包）：{ex.Message}");
            }
        }

        /// <summary>非法包只报警一次（广播域噪声多，全报必刷屏）；总数看 <see cref="DroppedCount"/>。</summary>
        private void LogDropOnce(IPEndPoint remote, string error)
        {
            if (Interlocked.Exchange(ref _dropLogged, 1) == 0)
            {
                Game.Logger?.Warn(LogTag,
                    $"丢弃非查询报文（来源 {remote?.ToString() ?? "<未知>"}）：{error}" +
                    "（后续同类只计数，见 DroppedCount）");
            }
        }

        private void LogQuery(string from, string nonce)
        {
            // ⛔ 用 Environment.TickCount 而不是 UnityEngine.Time.*：本方法跑在收包后台线程上，
            // Unity 的 Time 属性按官方口径不保证可在非主线程读。
            var now = Environment.TickCount;
            lock (QueryLogLock)
            {
                if (now - _lastQueryLogAt < QueryLogIntervalMs)
                    return;
                _lastQueryLogAt = now;
            }

            var self = _self;
            Game.Logger?.Info(LogTag,
                $"收到局域网查询：from={from} nonce={Shorten(nonce)}… → 回 {self?.Address ?? "<未就绪>"}");
        }

        // ====================================================================
        //  参数校验与规范化
        // ====================================================================

        /// <summary>
        /// 校验并规范化对外广播的主机信息。
        /// <c>Host</c> 留空 = 自动取本机一个 IPv4（多网卡 / 想广播成别的地址时由调用方显式给 Host —— ⛔ 引擎不猜）。
        /// </summary>
        private static bool TryResolveAdvertised(LanHostInfo self, out LanHostInfo advertised, out string error)
        {
            advertised = null;
            error = null;

            var host = string.IsNullOrEmpty(self.Host) ? PickLocalIPv4() : self.Host.Trim();
            if (host.Length == 0)
            {
                error = "取不到本机 IPv4 地址（没有已启用的网卡？）—— 广播出去 gateway 无法解析，对端必然丢弃";
                return false;
            }

            // 复用协议侧的 host:port 解析：它同时管「IPv4 字面量 / 主机名」与端口范围，
            // 免得应答端另写一套校验（两套必然漂移）。
            if (!LanProtocol.TryParseEndpoint($"{host}:{self.GatewayPort}", out var parsedHost, out var parsedPort))
            {
                error = $"gateway 非法：\"{host}:{self.GatewayPort}\"" +
                        "（需 host:port 且端口 1~65535；IPv6 不支持）";
                return false;
            }

            var udpPort = self.UdpPort;
            if (udpPort < 0 || udpPort > 65535)
            {
                Game.Logger?.Warn(LogTag, $"udp 端口非法（{udpPort}），按「未提供」处理");
                udpPort = 0;
            }

            advertised = LanHostInfo.Create(
                parsedHost,
                parsedPort,
                udpPort,
                self.AuthAddr,
                self.Name,
                self.Players < 0 ? 0 : self.Players,
                self.MaxPlayers < 0 ? 0 : self.MaxPlayers,
                self.Version,
                self.Extra);
            return true;
        }

        /// <summary>把可能很长的可选字段截短到 <see cref="FieldMaxChars"/>（只为让应答能装进单包）。</summary>
        private static LanHostInfo Shrink(LanHostInfo self)
        {
            return LanHostInfo.Create(
                self.Host,
                self.GatewayPort,
                self.UdpPort,
                Truncate(self.AuthAddr),
                Truncate(self.Name),
                self.Players,
                self.MaxPlayers,
                Truncate(self.Version),
                Truncate(self.Extra));
        }

        private static string Truncate(string value)
        {
            return string.IsNullOrEmpty(value) || value.Length <= FieldMaxChars
                ? value
                : value.Substring(0, FieldMaxChars);
        }

        /// <summary>
        /// 取本机一个「局域网内可达」的 IPv4 字面量：遍历**已启用**网卡的单播地址，
        /// 返回第一个非回环、非 APIPA（<c>169.254.x.x</c> = 没拿到 DHCP 的兜底地址）的地址。
        ///
        /// <para>
        /// 用 <see cref="NetworkInterface"/> 而不是 <c>Dns.GetHostAddresses</c>：后者在多网卡 /
        /// 有 VPN 虚拟网卡时给的不一定是「对方连得上」的那一个。本方法只保证返回的是**一个局域网字面量**，
        /// 具体选哪块网卡由调用方用 <see cref="LanHostInfo.Host"/> 显式覆盖（⛔ 引擎不猜）。
        /// </para>
        /// </summary>
        private static string PickLocalIPv4()
        {
            try
            {
                var interfaces = NetworkInterface.GetAllNetworkInterfaces();
                if (interfaces == null)
                    return string.Empty;

                string fallback = null;
                foreach (var nic in interfaces)
                {
                    try
                    {
                        if (nic.OperationalStatus != OperationalStatus.Up)
                            continue;
                        if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                            continue;

                        var properties = nic.GetIPProperties();
                        if (properties == null)
                            continue;

                        foreach (var unicast in properties.UnicastAddresses)
                        {
                            var address = unicast?.Address;
                            if (address == null)
                                continue;
                            if (address.AddressFamily != AddressFamily.InterNetwork)
                                continue;
                            if (IPAddress.IsLoopback(address))
                                continue;

                            var text = address.ToString();
                            if (text.StartsWith("169.254.", StringComparison.Ordinal))
                            {
                                if (fallback == null)
                                    fallback = text;   // APIPA：实在没有别的才用它
                                continue;
                            }

                            return text;
                        }
                    }
                    catch (Exception ex)
                    {
                        // 单块网卡枚举失败不该让整件事失败（VPN / 虚拟网卡上很常见）。
                        Game.Logger?.Warn(LogTag, $"网卡 {nic?.Name ?? "<未知>"} 枚举失败，跳过：{ex.Message}");
                    }
                }

                if (fallback != null)
                    return fallback;
            }
            catch (Exception ex)
            {
                Game.Logger?.Warn(LogTag, $"列举本机地址失败：{ex.GetType().Name}: {ex.Message}");
            }

            return string.Empty;
        }

        /// <summary>日志里只打 nonce 前 8 位（够定位又不至于把整串刷进日志）。</summary>
        private static string Shorten(string nonce)
        {
            return string.IsNullOrEmpty(nonce) ? "<空>" : nonce.Substring(0, Math.Min(8, nonce.Length));
        }

        private static string DefaultUnsupportedReason()
        {
            return LanCapabilities.IsSupported ? string.Empty : LanCapabilities.ReasonFor(Application.platform);
        }
    }
}
