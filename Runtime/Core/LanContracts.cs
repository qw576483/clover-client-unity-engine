using System;
using System.Collections.Generic;

namespace CloverEngine
{
    // ============================================================================
    // 局域网寻服（LAN server browser）跨模块契约层
    //
    // 为什么这些类型必须放 Core：`Game.LanBrowser` 会把它（及其事件参数类型）直接暴露给业务，
    // 而 asmdef 依赖方向是 `Network → Core` 单向（结构规则 §2.1 铁律 3）——
    // 契约类型留在 Network 会让 Core 反向依赖 Network，整个包编译不过。
    // 实现类 `LanBrowser` / `LanSocket` / `LanProtocol` 全部落在 `Runtime/Network/Lan/`。
    //
    // 命名刻意与三套易撞义的东西划清界限（见 结构规则.md §五 N13）：
    //   ① 服务端 etcd 服务发现（clover-server-engine/internal/app/discovery.go）—— 无关；
    //   ② EMsg / Router / TransportKind 游戏数据通道 —— 无关，本能力是**旁路协议**；
    //   ③ EMsg.UDPBindGrant / EMsgBindUDP 网关不可靠端点登记 —— 无关，不是打洞、不是发现。
    // ============================================================================

    /// <summary>
    /// 局域网寻服状态。
    /// </summary>
    public enum LanBrowserState
    {
        /// <summary>空闲（未在扫描；也是每轮扫描收尾后的状态）。</summary>
        Idle,

        /// <summary>扫描中（一轮窗口内，结果随应答到达持续追加）。</summary>
        Scanning,
    }

    /// <summary>
    /// 局域网内一台**可加入的主机**（发现结果，只读快照）。
    ///
    /// <para>
    /// 刻意不叫 <c>ServerInfo</c>：它就是「局域网内一台可连的主机」，回答的是
    /// 「谁开了服、能不能进」，与服务器下发的任何列表无关，也不是 <see cref="Game.CloverScene"/>
    /// （服务端逻辑场景）或 <see cref="Game.Map"/>（逻辑地图）。
    /// </para>
    ///
    /// <para>
    /// <b>只读契约</b>（结构规则 §5.2 G8，判据见 §5.3）：外部即使持有实例也改不动内部状态，
    /// 因此不做逐次拷贝 —— 实例只能由本类工厂 <see cref="Create"/> 生成，字段只能读。
    /// </para>
    /// </summary>
    public sealed class LanHostInfo
    {
        /// <summary>主机地址（IPv4 字面量，如 "192.168.1.7"）。</summary>
        public string Host { get; private set; }

        /// <summary>网关 TCP 口（服务端 <c>gateway.listen_tcp</c>）。</summary>
        public int GatewayPort { get; private set; }

        /// <summary>裸 UDP 口（服务端 <c>gateway.listen_udp</c>）；0 = 未提供。</summary>
        public int UdpPort { get; private set; }

        /// <summary>账号服 HTTP 基址，如 "http://192.168.1.7:8051"；可为空。</summary>
        public string AuthAddr { get; private set; }

        /// <summary>主机显示名；可为空。</summary>
        public string Name { get; private set; }

        /// <summary>当前在线人数。</summary>
        public int Players { get; private set; }

        /// <summary>人数上限；0 = 未知。</summary>
        public int MaxPlayers { get; private set; }

        /// <summary>主机版本串；可为空。</summary>
        public string Version { get; private set; }

        /// <summary>透传字段（协议里的 extra 原样带出）；可为空。</summary>
        public string Extra { get; private set; }

        /// <summary>"host:gatewayPort"，可直接喂 <c>CloverNet.Init(addr, udpAddr)</c> 的 addr。</summary>
        public string Address => $"{Host}:{GatewayPort}";

        /// <summary>"host:udpPort"；<see cref="UdpPort"/> &lt;= 0 时为空串（表示该主机未提供裸 UDP 口）。</summary>
        public string UdpAddress => UdpPort > 0 ? $"{Host}:{UdpPort}" : string.Empty;

        private LanHostInfo()
        {
        }

        /// <summary>
        /// 生成一份主机快照。<b>由 <see cref="ILanBrowser"/> 的实现（Network 域的 <c>LanProtocol</c>）调用</b>；
        /// 只读契约下外部即使创建也影响不到浏览器内部状态，故公开无风险。
        /// </summary>
        /// <param name="host">主机地址（IPv4 字面量）</param>
        /// <param name="gatewayPort">网关 TCP 口</param>
        /// <param name="udpPort">裸 UDP 口；&lt;= 0 表示未提供</param>
        /// <param name="authAddr">账号服 HTTP 基址；可为 null</param>
        /// <param name="name">主机显示名；可为 null</param>
        /// <param name="players">当前在线人数（负值由调用方钳制为 0）</param>
        /// <param name="maxPlayers">人数上限；0 = 未知</param>
        /// <param name="version">主机版本串；可为 null</param>
        /// <param name="extra">透传字段；可为 null</param>
        public static LanHostInfo Create(string host, int gatewayPort, int udpPort, string authAddr,
            string name, int players, int maxPlayers, string version, string extra)
        {
            return new LanHostInfo
            {
                Host = host,
                GatewayPort = gatewayPort,
                UdpPort = udpPort,
                AuthAddr = authAddr,
                Name = name,
                Players = players,
                MaxPlayers = maxPlayers,
                Version = version,
                Extra = extra,
            };
        }
    }

    /// <summary>
    /// 一轮局域网扫描的参数（<c>Scan(null)</c> 时全部取默认值）。
    /// </summary>
    public sealed class LanScanOptions
    {
        /// <summary>目标端口；0 = <c>LanProtocol.DefaultPort</c>(47777)。</summary>
        public int Port { get; set; } = 0;

        /// <summary>扫描窗口（毫秒）；&lt;= 0 视为默认（1500）。窗口到点由收包线程自判收尾，不依赖 <see cref="Game.Timer"/>。</summary>
        public int DurationMs { get; set; } = 1500;

        /// <summary>是否发 255.255.255.255（有限广播）。</summary>
        public bool IncludeBroadcast { get; set; } = true;

        /// <summary>是否发各网卡子网广播地址（x.x.x.255，按网卡掩码算）。</summary>
        public bool IncludeSubnetBroadcast { get; set; } = true;

        /// <summary>是否发 127.0.0.1（「本机起了服」这种常见场景）。</summary>
        public bool IncludeLoopback { get; set; } = true;

        /// <summary>额外单播目标，形如 <c>"ip"</c> 或 <c>"ip:port"</c>（带端口时覆盖本轮端口）；可为 null。</summary>
        public string[] ExtraTargets { get; set; }
    }

    /// <summary>
    /// 局域网寻服（LAN server browser）：找同网段里「谁开了服、能不能进」。
    ///
    /// <para>
    /// <b>旁路能力</b>：不走 EMsg / 不进 <see cref="IRouter"/> / 不占消息号 / 不走线路族
    /// （依据 结构规则.md §五 N13）。与服务器端 etcd 服务发现
    /// （<c>clover-server-engine/internal/app/discovery.go</c>）<b>无关</b>：
    /// 不共享协议、不共享端口、不共享代码。
    /// </para>
    ///
    /// <para>
    /// 用法顺序（硬要求）：<c>Game.Launch → Game.LanBrowser.Scan()</c> → 玩家选一台 →
    /// <c>CloverNet.Init(选中主机的 Address, UdpAddress)</c>（<see cref="LanHostInfo.Address"/> /
    /// <see cref="LanHostInfo.UdpAddress"/>）。
    /// 引擎<b>不提供运行中切服</b>（<c>CloverNet.Init</c> 幂等、只生效一次）。
    /// </para>
    ///
    /// <para>
    /// 线程模型：收包在后台线程，<b>所有状态与事件都收敛到主线程</b> —— 经
    /// <see cref="IDispatcher.Post"/> 投递（<see cref="Game.Dispatcher"/> 为 null 时直接执行），
    /// 与 <c>NetworkManager.PostOrRun</c> 同一写法。
    /// </para>
    ///
    /// <para>
    /// 平台：仅原生平台可用；WebGL 无 BSD socket ⇒ <see cref="IsSupported"/> 为 false 且
    /// <see cref="Scan"/> 会打 Error 日志 + 立即 <see cref="OnScanFinished"/>（禁止静默失败）。
    /// </para>
    /// </summary>
    public interface ILanBrowser
    {
        /// <summary>当前平台能否寻服。</summary>
        bool IsSupported { get; }

        /// <summary>不能寻服时的原因（可直接显示给玩家）；能时为空串。</summary>
        string UnsupportedReason { get; }

        /// <summary>当前状态。</summary>
        LanBrowserState State { get; }

        /// <summary>最近一轮结果（只读快照，结构规则 §5.2 G8）。新一轮 <see cref="Scan"/> 开始时清空。</summary>
        IReadOnlyList<LanHostInfo> Hosts { get; }

        /// <summary>
        /// 开始新一轮扫描。扫描中重复调用 = 停旧的、开新的（旧轮结果被丢弃）。
        /// 窗口到点或提前结束都会收尾一次，调用方不会卡在 <see cref="LanBrowserState.Scanning"/>。
        /// </summary>
        /// <param name="options">扫描参数；null = 全默认。</param>
        void Scan(LanScanOptions options = null);

        /// <summary>提前结束本轮（保留已发现结果）；未在扫描时为幂等空操作。</summary>
        void Stop();

        /// <summary>每发现一台主机触发一次（主线程；同 gateway 的重复应答不重复触发，只覆盖数据）。</summary>
        event Action<LanHostInfo> OnHostFound;

        /// <summary>一轮结束触发一次（主线程；无论有无结果，任何提前结束路径也一定触发）。</summary>
        event Action OnScanFinished;
    }
}
