using System;
using System.Collections.Generic;
using UnityEngine;

namespace CloverEngine
{
    /// <summary>
    /// 传输连接的统一契约：可靠通道（TCP / WebSocket）与不可靠通道（裸 UDP）共用同一套成员，
    /// 使网络模块不必关心底下是哪条线路——消息、会话恢复、Router 语义全部线路一致。
    ///
    /// 实现约束：
    ///   - 收发在后台线程完成，<see cref="TryTakePacket"/> 供主线程 Tick 排空（不阻塞主线程）；
    ///   - <see cref="Send"/> 只入队，实际写出由后台发送线程完成（避免并发写同一 socket）；
    ///   - <see cref="Connected"/> / <see cref="Disconnected"/> 可能在后台线程触发，消费方自行切主线程。
    /// </summary>
    internal interface ITransportConnection
    {
        /// <summary>本条连接的线路类型。</summary>
        TransportKind Kind { get; }

        /// <summary>当前连接状态。</summary>
        ConnectionState State { get; }

        // 「对端地址」不是契约成员：展示一律走 NetworkManager 的 ActiveLine / LinePlan / ServerAddr。

        /// <summary>连接建立成功回调（可能在后台线程触发）。</summary>
        event Action Connected;

        /// <summary>链路断开回调（可能在后台线程触发）；用户主动断开不触发。</summary>
        event Action Disconnected;

        /// <summary>发起连接（非阻塞）。地址格式非法时同步抛 <see cref="FormatException"/>。</summary>
        /// <param name="addr">目标地址；WebSocket 传完整 URL，其它线路传「host:port」。</param>
        void ConnectAsync(string addr);

        /// <summary>用户主动断开：终止后台线程、释放资源、清空收发队列，不触发回调。</summary>
        void Disconnect();

        /// <summary>投递一帧（数据体，不含线路自身封装），由后台线程写出。</summary>
        /// <param name="data">源缓冲区。</param>
        /// <param name="offset">起始偏移。</param>
        /// <param name="count">字节数。</param>
        void Send(byte[] data, int offset, int count);

        /// <summary>主线程驱动点（心跳等在后台线程自驱的实现留空）。</summary>
        void Tick();

        /// <summary>取出一条完整客户端帧（线路自身封装已剥去）。</summary>
        /// <param name="data">输出的帧字节。</param>
        /// <returns>取到返回 true。</returns>
        bool TryTakePacket(out byte[] data);
    }

    /// <summary>
    /// 可选能力：某些线路**自身就能承载不可靠数据**（QUIC 的 Datagram 通道），
    /// 此时不再需要独立的裸 UDP 通道（也就省掉 <c>EMsgBindUDP</c> 握手与一条常驻 socket）。
    /// 与 <see cref="ITransportConnection"/> 分开的理由：TCP/WS 天然没有不可靠语义，
    /// 让它们也实现一个必然抛/降级的成员，只会让"发不可靠"的调用点看不出差别。
    /// </summary>
    internal interface IUnreliableTransport
    {
        /// <summary>
        /// 当前是否**真的**支持不可靠发送。QUIC 要看对端是否协商出 Datagram
        /// （只有收到 <c>DATAGRAM_STATE_CHANGED{SendEnabled=true}</c> 才是 true）；
        /// false 时调用方必须走可靠发送，不能丢包（丢包会让业务静默不同步）。
        /// </summary>
        bool SupportsUnreliable { get; }

        /// <summary>发送一帧不可靠数据（不做重传、不保序）。</summary>
        void SendUnreliable(byte[] data, int offset, int count);
    }

    /// <summary>
    /// 线路能力表：引擎**真实支持**的线路，以及**尚未支持**线路的明确原因。
    /// 用途是让「不支持」可见而不是静默降级——线路规划（<see cref="TransportPlanner"/>）与
    /// 业务提示都读这里；<see cref="Describe"/> 为汇总文本，已接进编辑器调试面板
    /// （<c>Editor/Debugger.cs</c> 的能力区块）。
    /// </summary>
    public static class TransportCapabilities
    {
        private static readonly object QuicGate = new object();
        private static string _quicSessionFailure;

        /// <summary>
        /// 本进程内 QUIC 是否已被判定不可用（连接失败过一次）。
        ///
        /// <para>
        /// 存在的理由：QUIC 走 UDP，而"对端没有 UDP 监听 / 证书不受信"这类失败**不报错、只是超时**，
        /// 每次重连都白等一个连接超时（哪怕只有 2 秒）。失败一次就记住，本进程后续线路规划里
        /// 直接把 QUIC 裁掉（原因写进裁剪说明，仍然可见），桌面端用户体验与"没接 QUIC"时一致。
        /// </para>
        /// </summary>
        public static string QuicSessionFailureReason
        {
            get
            {
                lock (QuicGate)
                {
                    return _quicSessionFailure;
                }
            }
        }

        /// <summary>记录"本进程内 QUIC 不可用"（由网络模块在线路失败时调用）。</summary>
        internal static void MarkQuicUnavailableForSession(string reason)
        {
            lock (QuicGate)
            {
                if (_quicSessionFailure == null)
                    Game.Logger?.Warn("Network", $"QUIC 本进程内标记为不可用，后续线路规划将裁掉它: {reason}");
                _quicSessionFailure = string.IsNullOrEmpty(reason) ? "line failed" : reason;
            }
        }

        /// <summary>仅供测试：清掉"本进程内 QUIC 不可用"标记。</summary>
        internal static void ResetQuicSessionFailureForTesting()
        {
            lock (QuicGate)
            {
                _quicSessionFailure = null;
            }
        }

        /// <summary>
        /// QUIC 当前是否可用：**主动探测**原生插件 + 本进程尚未失败过。
        ///
        /// <para>
        /// ⚠️ 这里必须调 <see cref="MsQuicRuntime.TryEnsureReady"/>（主动探测），
        /// **不能**读 <c>MsQuicRuntime.IsAvailable</c> —— 后者是"探测结果缓存"，**未探测时恒为 false**。
        /// </para>
        /// <para>
        /// ⚠️ 不主动探测就会误判：编辑器里测试/探针可能先探测过，QUIC"看起来能走"；
        /// 而 **真 Player / 移动端是全新进程、没有人探测过 ⇒ 能力判定恒 false ⇒ QUIC 被静默排除**，
        /// 线路规划里只剩 TCP（包内日志 `line plan: tcp:127.0.0.1:8002`，且全包**没有一条** QUIC 日志）。
        /// </para>
        /// <para>
        /// 探测本身只做一次（内部加锁 + 缓存结果），失败会把原因写进日志（能力缺失可见，不静默降级）。
        /// </para>
        /// </summary>
        private static bool IsQuicUsable =>
            MsQuicRuntime.TryEnsureReady(out _) && QuicSessionFailureReason == null;
        /// <summary>
        /// 当前平台**应使用**的线路集合（属于本平台家族的那些）。
        /// 注意是「应当使用」而不是「底层能跑」：判断依据见 <see cref="IsSupported"/>。
        /// </summary>
        public static IReadOnlyList<TransportKind> Supported()
        {
            var list = new List<TransportKind>();
            if (Application.platform == RuntimePlatform.WebGLPlayer)
            {
                // Web 家族：WebTransport（待 jslib 桥接）→ WebSocket（待 jslib 桥接）。
                // 两者当前都未实现（WebSocketConnection 在 WebGL 直接抛 NotSupportedException），
                // 因此**没有任何"应使用"的线路**——如实为空，不做"假可用"（假可用比"没有"更糟：
                // 声明可用、连接必失败，还会把失败掩盖到连接期）。
                return list;
            }
            else
            {
                // 原生家族：QUIC（原生插件可用时）→ TCP + 裸 UDP。**不含 WebSocket**。
                // 顺序即"主链 → 降级链"：QUIC 能用就用 QUIC（一条通道同时给可靠+不可靠，
                // 省掉裸 UDP 的绑定握手），它不可用（无插件/本进程已失败过）时自然落到 TCP。
                if (IsQuicUsable)
                    list.Add(TransportKind.Quic);
                list.Add(TransportKind.Tcp);
                list.Add(TransportKind.RawUdp);
            }

            return list;
        }

        /// <summary>
        /// **仅供测试**：允许在当前平台上使用非本家族的线路。
        /// <para>
        /// 存在的唯一理由：Web 家族（WebSocket / WebTransport）的实现只有在 Editor 上才跑得起来
        /// （WebGL 无法 headless 测试），若严格按家族裁剪就永远测不到 WS。
        /// 打开它只是**放行显式声明的线路**，不会恢复「连不上就换家族」的自动降级。
        /// </para>
        /// <para>
        /// **生产代码不得打开**。测试里用 <c>[SetUp]</c> 打开、<c>[TearDown]</c> 复位。
        /// </para>
        /// </summary>
        public static bool AllowCrossFamilyForTesting { get; set; }

        /// <summary>
        /// 指定线路是否**属于当前平台的家族**。
        /// <para>
        /// 关键区分：这里是「该不该用」而不是「底层能不能跑」。WebSocket 在桌面/移动端其实跑得起来
        /// （<c>ClientWebSocket</c> 可用），但它**不属于原生家族**，因此不参与原生端的线路规划。
        /// 依据 <c>结构规则.md</c> §五 N10 —— 两个家族互斥：
        /// </para>
        /// <list type="bullet">
        /// <item>**Web 家族**（WebGL）：WebTransport → WebSocket</item>
        /// <item>**原生家族**（Standalone / 移动端）：QUIC → TCP + 裸 UDP</item>
        /// </list>
        /// <para>
        /// 为什么必须互斥：原生端一旦「TCP 连不上就退到 WS」，换掉的不只是协议，
        /// 还有**通道语义** —— WS 没有真正的不可靠语义，`SendUnreliable` 会退化成可靠发送
        /// （服务端 <c>ws/conn.go</c> 原文如此），实时同步会吃满队头阻塞，而调用方完全看不出来。
        /// 正确行为是：原生端 TCP 连不上就报 `Net.OnConnectFailed`，不换家族。
        /// </para>
        /// </summary>
        /// <param name="kind">线路类型。</param>
        public static bool IsSupported(TransportKind kind)
        {
            if (AllowCrossFamilyForTesting)
                return kind == TransportKind.Tcp || kind == TransportKind.WebSocket ||
                       kind == TransportKind.RawUdp || kind == TransportKind.Quic;

            var web = Application.platform == RuntimePlatform.WebGLPlayer;
            if (web)
            {
                // Web 家族当前**没有任何可用线路**：WebTransport 在客户端从未实现；
                // WebSocket 需要浏览器侧 jslib 桥接（WebSocketConnection.ConnectAsync 在 WebGL 会直接抛
                // NotSupportedException）⇒ 如实判 false，由线路规划把 WS 裁掉并写明原因（可见，不静默）。
                // jslib 桥接落地后，从这里放行对应线路。
                return false;
            }

            return kind == TransportKind.Tcp || kind == TransportKind.RawUdp ||
                   (kind == TransportKind.Quic && IsQuicUsable);
        }

        /// <summary>
        /// 尚未支持的传输与原因（一句话，用于日志与文档对齐）。
        /// 这不是「配置没打开」，而是底层能力缺失，因此不做静默降级、不提供假实现：
        ///   - QUIC：**原生平台已实现**（<c>Runtime/Network/Quic/</c>，经 msquic 原生插件）；
        ///     插件缺失或本进程内失败过一次时，线路会被裁掉并把原因写进裁剪说明；
        ///   - WebTransport / WebSocket(WebGL)：都需浏览器侧 jslib 桥接，当前均未实现
        ///     —— WebGL 因此没有任何可用线路，能力表如实为空（**不会**伪装成可用）。
        /// </summary>
        public static string UnsupportedSummary
        {
            get
            {
                // 与 IsQuicUsable 用同一口径：**主动探测**（TryEnsureReady），不能读 IsAvailable ——
                // 后者是"探测结果缓存"，未探测时恒为 false，会误报 QUIC 不可用（见字段区注释）。
                var quic = MsQuicRuntime.TryEnsureReady(out var reason)
                    ? "QUIC 已就绪(msquic)"
                    : $"QUIC 不可用({reason})";
                var sessionFail = QuicSessionFailureReason;
                if (sessionFail != null)
                    quic += $"，本进程已因失败裁掉它({sessionFail})";
                return quic + "；WebTransport / WebSocket(WebGL) 需浏览器 jslib 桥接，当前均未实现";
            }
        }

        /// <summary>
        /// 各平台的**真实**线路可用性。与架构文档 §N10 的期望存在差异，这里显式写出，
        /// 避免读者以为「配了就通」：
        /// <list type="bullet">
        /// <item>Standalone / Editor Windows：TCP + 裸 UDP；QUIC 走 msquic 原生插件
        /// —— 注意二进制**只随 Windows x64 入库**（<c>Runtime/Plugins/x86_64/msquic.dll</c>），
        /// Linux / macOS 需自行补 libmsquic 二进制后 QUIC 才会可用（能力探测会自动裁掉无二进制的平台）。</item>
        /// <item><b>Android / iOS：TCP + 裸 UDP 已就绪；QUIC 需自建</b> ——
        /// 移动端**并非做不了 QUIC**（QUIC 本身只是 UDP + TLS 1.3，两端平台都有栈），
        /// 缺的是 Unity 侧的现成实现：<c>System.Net.Quic</c> 在 Unity 与 .NET 移动端都不存在，
        /// msquic 也没有官方移动端构件（其 Android 支持未经官方验证，见 microsoft/msquic#4041）。
        /// 可选路径：① 自行交叉编译 msquic（可复用同一套 P/Invoke 绑定，但需产出 NDK / Xcode 产物）；
        /// ② 接平台原生栈 —— Android Cronet（AAR + JNI）、iOS Network.framework 的
        /// <c>NWProtocolQUIC</c>。两者都是独立工程量，不是配置项。</item>
        /// <item>WebGL：当前**无可用线路**（WebSocket 与 WebTransport 均需浏览器 jslib 桥接，尚未落地；
        /// 浏览器也无 BSD socket，原生家族线路不可用）。</item>
        /// </list>
        /// </summary>
        public static string PlatformSummary
        {
            get
            {
                var platform = Application.platform;
                if (platform == RuntimePlatform.WebGLPlayer)
                    return "WebGL（Web 家族）：当前无可用线路 —— WebSocket / WebTransport 均需浏览器 jslib 桥接（尚未落地）；" +
                           "浏览器无 BSD socket，原生家族线路不可用";

                if (platform == RuntimePlatform.Android || platform == RuntimePlatform.IPhonePlayer)
                    return "移动端（原生家族）：TCP + 裸 UDP；QUIC 需自建（自行交叉编译 msquic，" +
                           "或接 Android Cronet / iOS NWProtocolQUIC，两者均为平台原生绑定）。" +
                           "WebSocket 属 Web 家族，不参与原生线路";

                // 桌面端：msquic 二进制当前只随 Windows x64 入库，Linux/macOS 单独说明，避免
                // 读者以为"桌面全平台可以走 QUIC"（能力探测会按实际二进制自动裁掉）。
                if (platform == RuntimePlatform.LinuxPlayer || platform == RuntimePlatform.LinuxEditor ||
                    platform == RuntimePlatform.OSXPlayer || platform == RuntimePlatform.OSXEditor)
                    return "桌面端 Linux/macOS（原生家族）：TCP + 裸 UDP；QUIC 需自行补对应平台的 libmsquic 二进制" +
                           "（引擎包内当前只带 Windows x64 的 msquic.dll）。WebSocket 属 Web 家族，不参与原生线路";

                return "桌面端（原生家族）：QUIC（msquic 原生插件）→ TCP + 裸 UDP。" +
                       "WebSocket 属 Web 家族，不参与原生线路";
            }
        }

        /// <summary>
        /// 移动端的明文流量注意事项。不是引擎能替业务决定的事，但踩了会表现为「连不上」：
        /// Android 9+ 默认禁止明文流量，明文 TCP / <c>ws://</c> 会被系统直接拦掉，
        /// 需在 AndroidManifest 打开 <c>usesCleartextTraffic</c> 或配 network security config；
        /// iOS 访问局域网地址需要 <c>NSLocalNetworkUsageDescription</c>。
        /// 因此移动端**建议直接启用 TLS**（<see cref="GameConfig.UseTls"/>），而不是改清单去放开明文。
        /// </summary>
        public static string MobileCleartextNote =>
            "移动端明文限制：Android 9+ 默认拦明文 TCP/ws://（需 usesCleartextTraffic 或改用 TLS）；" +
            "iOS 访问局域网需 NSLocalNetworkUsageDescription。建议移动端直接启用 GameConfig.UseTls";

        /// <summary>
        /// 多行能力描述（可用线路 / 平台 / 不支持原因 / 移动端明文注意事项）。
        /// 消费方：编辑器调试面板 <c>Editor/Debugger.cs</c> 的能力区块。
        /// </summary>
        public static string Describe()
        {
            var list = Supported();
            var names = new string[list.Count];
            for (var i = 0; i < list.Count; i++)
                names[i] = list[i].ToString();
            return "可用线路: " + string.Join(", ", names) + "\n" + PlatformSummary + "\n" +
                   UnsupportedSummary + "\n" + MobileCleartextNote;
        }
    }

    /// <summary>
    /// 线路规划：把配置里的线路顺序按运行平台裁剪成「可尝试序列」，并记录被裁掉的原因。
    /// 与网络模块分离，便于单测（不涉及 socket）。
    /// </summary>
    internal static class TransportPlanner
    {
        /// <summary>
        /// 按**家族**裁剪线路顺序：其余平台只保留原生家族（TCP / 裸 UDP / QUIC）；
        /// WebGL 当前无可用线路（WebSocket 也需 jslib 桥接，见 <see cref="TransportCapabilities.IsSupported"/>）。
        /// <para>
        /// 被判定的依据是 <see cref="TransportCapabilities.IsSupported"/>（「该不该用」而非「能不能跑」），
        /// 因此**配了 WebSocket 的原生工程不会退化成 WS**，而且被裁掉的线路会写进
        /// <paramref name="note"/> —— 是**可见的**，不是静默忽略。
        /// </para>
        /// </summary>
        /// <param name="options">配置给出的线路。</param>
        /// <param name="note">被裁剪线路的说明；无裁剪时为空串。</param>
        /// <returns>裁剪后的线路配置。</returns>
        internal static TransportOptions ApplyPlatformRules(TransportOptions options, out string note)
        {
            note = string.Empty;
            if (options == null)
                return new TransportOptions(null, null);

            var kept = new List<TransportLine>();
            var dropped = new List<string>();
            foreach (var line in options.ReliableOrder)
            {
                if (TransportCapabilities.IsSupported(line.Kind))
                {
                    kept.Add(line);
                    continue;
                }

                dropped.Add(line.Describe() +
                            (Application.platform == RuntimePlatform.WebGLPlayer
                                ? "（WebGL：线路需浏览器 jslib 桥接，当前未落地）"
                                : "（当前平台不支持）"));
            }

            if (dropped.Count > 0)
                note = "已按平台裁剪线路: " + string.Join(", ", dropped);

            return new TransportOptions(kept, options.UdpAddr);
        }

        /// <summary>
        /// 按线路类型创建传输实现。
        /// 新线路只需在此加一个分支 + 在 <see cref="TransportKind"/> 加一个枚举值。
        /// </summary>
        /// <param name="line">目标线路。</param>
        /// <returns>传输实现；线路类型不可用时抛 <see cref="NotSupportedException"/>。</returns>
        internal static ITransportConnection Create(TransportLine line)
        {
            switch (line.Kind)
            {
                case TransportKind.Tcp:
                    return new TcpConnection();
                case TransportKind.WebSocket:
                    return new WebSocketConnection();
                case TransportKind.Quic:
                    return new QuicConnection();
                case TransportKind.RawUdp:
                    // 裸 UDP 是**次级不可靠通道**，不是可靠线路：它由 NetworkManager 在 UdpAddr 就绪时
                    // 单独建立（见 Runtime/Network/NetworkManager.cs 的 UDP 绑定路径），不经本工厂。
                    // 但 TransportCapabilities.IsSupported(RawUdp) 为 true，因此一条含 RawUdp 的自定义
                    // 可靠序列会被 ApplyPlatformRules 原样保留、落到这里的 default —— 本条异常给的是
                    // 可执行改法（笼统的 "transport kind not implemented" 排查时看不出该怎么改）。
                    throw new NotSupportedException(
                        "RawUdp 不是可靠线路，不能出现在可靠线路序列里；" +
                        "启用不可靠上行请配置 TransportOptions.UdpAddr（服务端经 EMsg.UDPBindGrant 下发令牌后自动绑定）");
                default:
                    throw new NotSupportedException($"transport kind not implemented: {line.Kind}");
            }
        }

        /// <summary>线路连接时实际传给传输实现的地址（WebSocket 需要完整 URL，其它线路是 host:port）。</summary>
        /// <param name="line">目标线路。</param>
        internal static string ConnectTargetOf(TransportLine line)
        {
            return line.Kind == TransportKind.WebSocket ? line.BuildWebSocketUrl() : line.Addr;
        }
    }
}
