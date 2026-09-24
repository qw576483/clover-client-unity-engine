using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading;
using UnityEngine;

namespace CloverEngine
{
    /// <summary>
    /// 局域网寻服实现（<see cref="ILanBrowser"/>）：UDP 广播/单播查询 → 收集应答 → 主线程收敛结果。
    ///
    /// <para>
    /// 一轮扫描的生命周期：
    /// <code>
    /// Scan(options)
    ///   → 算目标集合（回环 / 广播 / 各网卡子网广播 / ExtraTargets）
    ///   → 建 socket、发查询（**在主线程发**：收包线程只负责收）
    ///   → 收包线程收应答 → Accept(datagram, 来源 IP) → 校验 → Dispatcher.Post 到主线程 → 入结果集
    ///     （LanSocket 的回调给的是**来源端点**；本类只用其中的 IP 做日志，端口扔掉）
    ///   → 窗口到点 / Stop / 新一轮 Scan / 平台不支持 → **收尾一次**（关 socket → State=Idle → OnScanFinished）
    /// </code>
    /// </para>
    ///
    /// <para>
    /// <b>为什么收尾要"一次且一定"</b>：调用方（选服界面）靠 <see cref="OnScanFinished"/> 出 loading；
    /// 只要有一条路径漏掉它，玩家就会永久卡在"扫描中"。所以每条提前结束路径都收敛到
    /// <see cref="FinishRound"/>，并用轮次号（<c>_round</c>）丢弃过期收尾 —— 否则
    /// 「Stop 后立刻再 Scan」里的旧收尾会把新一轮的 <see cref="State"/> 打回 Idle。
    /// </para>
    ///
    /// <para>
    /// <b>与既有约定的边界</b>（结构规则.md §五 N13）：不占 EMsg 消息号、不进 <see cref="IRouter"/>、
    /// 不碰 <c>TransportKind</c> / <see cref="CloverNet"/>；本类<b>不依赖</b> <see cref="INetwork"/>；
    /// <b>不新增 asmdef</b> —— 仍属 <c>CloverEngine.Network</c>（asmdef 引用只有 <c>Core</c>）。
    /// </para>
    /// </summary>
    internal sealed class LanBrowser : ILanBrowser
    {
        private const string LogTag = "Lan";

        /// <summary>扫描窗口默认值（<see cref="LanScanOptions.DurationMs"/> &lt;= 0 时用它）。</summary>
        private const int DefaultScanDurationMs = 1500;

        private readonly List<LanHostInfo> _hosts = new();
        private readonly Dictionary<string, LanHostInfo> _byGateway = new(StringComparer.Ordinal);
        private readonly List<IPEndPoint> _targets = new();

        /// <summary>对外只读视图：内部只在主线程改，外面拿不到可写副本（G8）。</summary>
        private readonly IReadOnlyList<LanHostInfo> _hostsView;

        private LanSocket _socket;

        /// <summary>
        /// 本轮 nonce：主线程写（BeginScan），收包线程读（Accept 用它校验应答）。
        /// volatile：跨线程读写必须保证可见性，否则并发下可能用过期 nonce 校验、误丢有效应答。
        /// </summary>
        private volatile string _nonce;

        /// <summary>
        /// 当前轮次号：每次 Scan 自增，用来判定「这条延迟到达的收尾/结果属于哪一轮」。
        /// 主线程写（BeginScan），收包线程读（Accept 的轮次快照），volatile 保证可见性。
        /// </summary>
        private volatile int _round;

        /// <summary>本轮的「超上限」Warn 只打一次（防刷屏）。</summary>
        private bool _limitWarned;

        /// <summary>已收尾的轮次号：跨线程（收包线程 + 主线程）保证收尾只执行一次。</summary>
        private int _finishedRound;

        public LanBrowser()
        {
            _hostsView = _hosts.AsReadOnly();
        }

        /// <inheritdoc/>
        public bool IsSupported => LanCapabilities.IsSupported;

        /// <inheritdoc/>
        public string UnsupportedReason =>
            LanCapabilities.IsSupported ? string.Empty : LanCapabilities.ReasonFor(Application.platform);

        /// <inheritdoc/>
        public LanBrowserState State { get; private set; }

        /// <inheritdoc/>
        public IReadOnlyList<LanHostInfo> Hosts => _hostsView;

        /// <inheritdoc/>
        public event Action<LanHostInfo> OnHostFound;

        /// <inheritdoc/>
        public event Action OnScanFinished;

        /// <summary>本轮 nonce（单测用它构造合法应答包）。</summary>
        internal string CurrentNonce => _nonce;

        /// <summary>本轮目标集合（单测/调试面板用，验证广播/回环/ExtraTargets 的裁剪结果）。</summary>
        internal IReadOnlyList<IPEndPoint> Targets => _targets;

        /// <inheritdoc/>
        public void Scan(LanScanOptions options = null)
        {
            if (!IsSupported)
            {
                var reason = LanCapabilities.ReasonFor(Application.platform);
                Game.Logger?.Error(LogTag, $"当前平台不支持局域网寻服，本轮立即收尾：{reason}");
                // 不静默失败：仍要给调用方一次收尾信号（经主线程派发），否则选服界面会一直转圈。
                PostOrRunUnconditional(() =>
                {
                    OnScanFinished?.Invoke();
                    Game.Event?.Emit(CloverEvents.Net.LanScanFinished);
                });
                return;
            }

            BeginScan(options, startSocket: true);
        }

        /// <inheritdoc/>
        public void Stop()
        {
            if (State != LanBrowserState.Scanning)
                return;   // 未扫描时是幂等空操作（契约行为，不是异常分支）

            var round = _round;
            Game.Logger?.Info(LogTag, $"第 {round} 轮扫描被提前停止（保留已发现结果）");
            // 收尾**同步执行**、不再经 Dispatcher 投递：Stop 的调用方（选服 UI 取消按钮 /
            // Game.Shutdown）都在主线程，事件仍在主线程触发；而引擎关停后 Game.Tick 不再排空
            // 派发器队列 —— 投递会把收尾扔进永不执行的队列（socket 不关、OnScanFinished 永不触发，
            // 选服界面卡在"扫描中"）。旧的 PostFinish 投递路径只保留给收包线程的窗口到点。
            FinishRound(round);
        }

        /// <summary>
        /// 单测入口：只进入扫描态并算好目标集合，**不建 socket、不发包、不起线程**。
        /// 需要它是因为协议/去重/上限/状态机这几类断言不该依赖真实网络（见 Tests/Editor/LanProtocolTests.cs）。
        /// </summary>
        /// <param name="options">扫描参数；null = 全默认</param>
        internal void BeginScanForTesting(LanScanOptions options = null)
        {
            BeginScan(options, startSocket: false);
        }

        /// <summary>
        /// 喂一份收到的报文（由收包线程调用，单测可直接调）。
        ///
        /// <para>
        /// <paramref name="fromIp"/> **只用于日志**，绝不作为主机地址 —— 主机可能多网卡，
        /// 应答来源 IP 只是它发出这一包的网卡；真实地址一律取报文里的 <c>gateway</c>。
        /// </para>
        /// </summary>
        /// <param name="datagram">收到的字节</param>
        /// <param name="fromIp">来源 IP（仅日志）</param>
        internal void Accept(byte[] datagram, string fromIp)
        {
            if (datagram == null)
            {
                Game.Logger?.Warn(LogTag, "丢弃应答报文：字节为空引用");
                return;
            }

            if (State != LanBrowserState.Scanning)
            {
                Game.Logger?.Warn(LogTag, $"未在扫描中，丢弃应答报文（来源 {fromIp ?? "<未知>"}，{datagram.Length} 字节）");
                return;
            }

            var round = _round;
            var nonce = _nonce;

            if (!LanProtocol.TryParseReply(datagram, datagram.Length, nonce, out var host, out var error, out var warning))
            {
                Game.Logger?.Warn(LogTag, $"丢弃应答报文（来源 {fromIp ?? "<未知>"}）：{error}");
                return;
            }

            if (!string.IsNullOrEmpty(warning))
            {
                Game.Logger?.Warn(LogTag, $"应答报文告警（来源 {fromIp ?? "<未知>"}）：{warning}");
            }

            PostOrRun(round, () => ApplyHost(host));
        }

        // ====================================================================
        //  扫描生命周期
        // ====================================================================

        private void BeginScan(LanScanOptions options, bool startSocket)
        {
            options ??= new LanScanOptions();

            if (State == LanBrowserState.Scanning)
            {
                // 中断旧轮：旧轮也必须收尾一次（契约见 ILanBrowser.OnScanFinished：
                // "任何提前结束路径也一定触发一次"）—— 旧实现只 CloseSocket 后翻轮号，
                // 被中断轮永远不触发收尾，按轮等收尾的业务会永久卡在"扫描中"。
                // 同步收尾（此刻 _round 尚未自增，轮次守卫放行；FinishRound 内部会关 socket），
                // 再开新一轮：之后旧轮迟到的收尾会被轮次守卫丢弃。
                Game.Logger?.Warn(LogTag, $"扫描中再次 Scan：中断第 {_round} 轮，其未完成结果被丢弃");
                FinishRound(_round);
            }

            _round++;
            var round = _round;
            _hosts.Clear();
            _byGateway.Clear();
            _limitWarned = false;

            var port = options.Port > 0 ? options.Port : LanProtocol.DefaultPort;
            var durationMs = options.DurationMs > 0 ? options.DurationMs : DefaultScanDurationMs;
            _nonce = LanProtocol.NewNonce();

            BuildTargets(options, port);
            if (_targets.Count == 0)
            {
                Game.Logger?.Warn(LogTag,
                    "本轮目标集合为空（回环/广播/子网广播全关且未给 ExtraTargets），立即收尾");
                State = LanBrowserState.Idle;
                PostOrRunUnconditional(() =>
                {
                    OnScanFinished?.Invoke();
                    Game.Event?.Emit(CloverEvents.Net.LanScanFinished);
                });
                return;
            }

            State = LanBrowserState.Scanning;
            Game.Logger?.Info(LogTag,
                $"开始扫描：第 {round} 轮，目标 {_targets.Count} 个，端口 {port}，窗口 {durationMs}ms，nonce {Shorten(_nonce)}");

            if (!startSocket)
                return;   // 单测路径：目标集合已算好，直接喂包给 Accept

            var socket = LanSocket.TryCreate(out var error);
            if (socket == null)
            {
                Game.Logger?.Error(LogTag, $"UDP socket 创建失败，本轮立即收尾：{error}");
                FinishRound(round);
                return;
            }

            _socket = socket;

            // 发送在主线程：收包线程只负责收（结构规则.md §五 N13 的线程模型）。
            // 本轮所有目标共用同一个 nonce（"循环复用"），应答必须回显它才算数。
            var query = LanProtocol.BuildQuery(_nonce);
            foreach (var target in _targets)
                socket.Send(query, target);

            // 窗口由收包线程自判（不依赖 Game.Timer：可能没 Launch / 没 Tick）。
            // Accept 的 fromIp **只用于日志**（主机地址一律取报文里的 gateway）：来源端点里的端口
            // 是查询方自己的临时端口，对本类无意义，丢掉即可。
            socket.Start(durationMs, (datagram, remote) => Accept(datagram, remote?.Address?.ToString()), () => PostFinish(round));
        }

        /// <summary>把收尾投递到主线程（带轮次守卫：过期轮次的收尾直接丢弃）。</summary>
        private void PostFinish(int round)
        {
            PostOrRun(round, () => FinishRound(round));
        }

        /// <summary>
        /// 收尾：关 socket → State = Idle → OnScanFinished（主线程）。
        /// 幂等 + 轮次守卫，因此 Stop / 窗口到点 / 新一轮中断三条路径同时触发也只会收尾一次。
        /// </summary>
        private void FinishRound(int round)
        {
            if (round != _round)
            {
                Game.Logger?.Info(LogTag, $"第 {round} 轮收尾被忽略（当前已是第 {_round} 轮）");
                return;
            }

            // 跨线程一次性（每轮只准收尾一次）：Stop（主线程）与窗口到点（收包线程）可能几乎同时到达。
            // 旧实现的 CompareExchange(value: round, comparand: round) 比对数与写入值相同、恒为无操作，
            // 守卫形同虚设；正确做法：读旧值 → CAS 成功者才算抢到本轮的收尾权。
            var prev = Volatile.Read(ref _finishedRound);
            if (prev >= round)
                return; // 该轮已收尾过（或已翻篇到更新的轮）
            if (Interlocked.CompareExchange(ref _finishedRound, round, prev) != prev)
                return; // 另一线程抢先收尾

            if (State != LanBrowserState.Scanning)
                return;   // 已收尾过（幂等）

            CloseSocket();
            State = LanBrowserState.Idle;
            Game.Logger?.Info(LogTag, $"扫描结束：第 {round} 轮，发现 {_hosts.Count} 台主机");

            OnScanFinished?.Invoke();
            Game.Event?.Emit(CloverEvents.Net.LanScanFinished);
        }

        private void CloseSocket()
        {
            var socket = _socket;
            _socket = null;
            socket?.Stop();
        }

        // ====================================================================
        //  结果集
        // ====================================================================

        /// <summary>
        /// 结果入集：去重键为 <c>gateway</c> 的 host:port（规范化后）。
        /// 重复主机**不重复触发** <see cref="OnHostFound"/>（否则选服列表会跳），
        /// 但用新数据覆盖（players 之类会变），仅在数据真的变了时打 Info。
        /// </summary>
        private void ApplyHost(LanHostInfo host)
        {
            if (State != LanBrowserState.Scanning)
                return;

            var key = LanProtocol.NormalizeKey(host.Address);
            if (_byGateway.TryGetValue(key, out var known))
            {
                var index = _hosts.IndexOf(known);
                if (index >= 0)
                    _hosts[index] = host;
                _byGateway[key] = host;

                if (Changed(known, host))
                {
                    Game.Logger?.Info(LogTag,
                        $"主机信息更新（不重复触发 OnHostFound）：{host.Address} " +
                        $"players={host.Players}/{host.MaxPlayers} name={host.Name}");
                }

                return;
            }

            if (_hosts.Count >= LanProtocol.MaxHosts)
            {
                if (!_limitWarned)
                {
                    _limitWarned = true;
                    Game.Logger?.Warn(LogTag,
                        $"结果已达上限 {LanProtocol.MaxHosts} 台，后续新主机将被丢弃（首个被丢弃：{host.Address}）");
                }

                return;
            }

            _hosts.Add(host);
            _byGateway[key] = host;
            Game.Logger?.Info(LogTag,
                $"发现主机 {host.Address}（udp={(host.UdpPort > 0 ? host.UdpAddress : "未提供")}，" +
                $"{host.Players}/{host.MaxPlayers}，name={host.Name}）");

            OnHostFound?.Invoke(host);
            Game.Event?.Emit(CloverEvents.Net.LanHostFound, host);
        }

        private static bool Changed(LanHostInfo before, LanHostInfo after)
        {
            return before.Players != after.Players
                   || before.MaxPlayers != after.MaxPlayers
                   || before.UdpPort != after.UdpPort
                   || !string.Equals(before.Name, after.Name, StringComparison.Ordinal)
                   || !string.Equals(before.AuthAddr, after.AuthAddr, StringComparison.Ordinal)
                   || !string.Equals(before.Version, after.Version, StringComparison.Ordinal)
                   || !string.Equals(before.Extra, after.Extra, StringComparison.Ordinal);
        }

        // ====================================================================
        //  目标地址集合（每轮扫描时算一次）
        // ====================================================================

        private void BuildTargets(LanScanOptions options, int port)
        {
            _targets.Clear();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            void Add(IPAddress address, int targetPort)
            {
                if (address == null || targetPort < 1 || targetPort > 65535)
                    return;

                var key = $"{address}:{targetPort}";
                if (!seen.Add(key))
                    return;

                _targets.Add(new IPEndPoint(address, targetPort));
            }

            if (options.IncludeLoopback)
                Add(IPAddress.Loopback, port);

            if (options.IncludeBroadcast)
                Add(IPAddress.Broadcast, port);

            if (options.IncludeSubnetBroadcast)
                AddSubnetBroadcasts(port, Add);

            if (options.ExtraTargets != null)
            {
                foreach (var raw in options.ExtraTargets)
                {
                    if (string.IsNullOrEmpty(raw))
                        continue;

                    if (raw.IndexOf(':') < 0)
                    {
                        if (LanProtocol.TryParseAddress(raw, out var onlyIp))
                            Add(onlyIp, port);
                        else
                            Game.Logger?.Warn(LogTag, $"ExtraTargets 目标无法解析，跳过：\"{raw}\"");
                        continue;
                    }

                    if (!LanProtocol.TryParseEndpoint(raw, out var hostText, out var explicitPort))
                    {
                        Game.Logger?.Warn(LogTag, $"ExtraTargets 目标无法解析（需 ip 或 ip:port），跳过：\"{raw}\"");
                        continue;
                    }

                    if (!IPAddress.TryParse(hostText, out var ip))
                    {
                        Game.Logger?.Warn(LogTag, $"ExtraTargets 目标不是 IP 字面量，跳过：\"{raw}\"");
                        continue;
                    }

                    Add(ip, explicitPort);
                }
            }
        }

        /// <summary>
        /// 逐网卡算 IPv4 子网广播地址（<c>ip | ~mask</c>）。
        /// **网卡枚举/掩码失败只跳过该网卡**（打 Warn），不中断整轮 ——
        /// 一块虚拟网卡（VPN/虚拟机）出问题不该让整个局域网都找不到服。
        /// </summary>
        private static void AddSubnetBroadcasts(int port, Action<IPAddress, int> add)
        {
            NetworkInterface[] interfaces;
            try
            {
                interfaces = NetworkInterface.GetAllNetworkInterfaces();
            }
            catch (Exception ex)
            {
                Game.Logger?.Warn(LogTag, $"网卡枚举失败，本轮不发子网广播：{ex.Message}");
                return;
            }

            if (interfaces == null)
                return;

            foreach (var nic in interfaces)
            {
                var nicName = "<未知网卡>";
                try
                {
                    nicName = nic.Name;

                    if (nic.OperationalStatus != OperationalStatus.Up)
                        continue;   // 未启用的网卡（含已断开的虚拟网卡）：不发，不吵
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                        continue;   // 回环由 IncludeLoopback 单独控制

                    var properties = nic.GetIPProperties();
                    if (properties == null)
                    {
                        Game.Logger?.Warn(LogTag, $"网卡 {nicName} 无 IP 属性，跳过");
                        continue;
                    }

                    foreach (var unicast in properties.UnicastAddresses)
                    {
                        if (unicast?.Address == null)
                            continue;
                        if (unicast.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                            continue;

                        var mask = unicast.IPv4Mask;
                        if (mask == null || mask.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                        {
                            Game.Logger?.Warn(LogTag, $"网卡 {nicName} 地址 {unicast.Address} 无 IPv4 掩码，跳过该地址");
                            continue;
                        }

                        var broadcast = ComputeBroadcast(unicast.Address, mask);
                        if (broadcast == null)
                        {
                            Game.Logger?.Warn(LogTag, $"网卡 {nicName} 掩码 {mask} 非法，跳过该地址 {unicast.Address}");
                            continue;
                        }

                        add(broadcast, port);
                    }
                }
                catch (Exception ex)
                {
                    Game.Logger?.Warn(LogTag, $"网卡 {nicName} 枚举失败，跳过该网卡（不中断整轮）：{ex.Message}");
                }
            }
        }

        private static IPAddress ComputeBroadcast(IPAddress address, IPAddress mask)
        {
            var addressBytes = address.GetAddressBytes();
            var maskBytes = mask.GetAddressBytes();
            if (addressBytes.Length != 4 || maskBytes.Length != 4)
                return null;

            var broadcast = new byte[4];
            for (var i = 0; i < 4; i++)
                broadcast[i] = (byte)(addressBytes[i] | (byte)~maskBytes[i]);

            return new IPAddress(broadcast);
        }

        // ====================================================================
        //  主线程收敛
        // ====================================================================

        /// <summary>
        /// 主线程收敛（同 <c>NetworkManager.PostOrRun</c> 的写法）：有派发器就投递
        /// （由 <see cref="Game.Tick"/> 排空），没有（编辑器测试里未 Launch）就直接执行 ——
        /// 否则测试里事件永远不来。
        /// </summary>
        private static void PostOrRunUnconditional(Action action)
        {
            var dispatcher = Game.Dispatcher;
            if (dispatcher != null)
                dispatcher.Post(action);
            else
                action();
        }

        /// <summary>带轮次守卫的主线程收敛：过期轮次的动作直接丢弃并留痕。</summary>
        private void PostOrRun(int round, Action action)
        {
            PostOrRunUnconditional(() =>
            {
                if (round != _round)
                {
                    Game.Logger?.Info(LogTag, $"丢弃第 {round} 轮的延迟动作（当前已是第 {_round} 轮）");
                    return;
                }

                action();
            });
        }

        /// <summary>日志里只打 nonce 前 8 位（够定位又不至于把整串刷进日志）。</summary>
        private static string Shorten(string nonce)
        {
            return string.IsNullOrEmpty(nonce) ? "<空>" : nonce.Substring(0, Math.Min(8, nonce.Length));
        }
    }
}
