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
    // 实现类 `LanBrowser` / `LanResponder` / `LanSocket` / `LanProtocol` 全部落在 `Runtime/Network/Lan/`。
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

    /// <summary>
    /// 局域网应答端的启动参数（<c>Start</c> 的第二参数；传 null = 全默认）。
    /// </summary>
    public sealed class LanRespondOptions
    {
        /// <summary>监听端口；0 = <c>LanProtocol.DefaultPort</c>(47777)。</summary>
        public int Port { get; set; } = 0;
    }

    /// <summary>
    /// 局域网应答端（"我开的主机要被别人扫到"）——<see cref="ILanBrowser"/> 的**对侧**。
    ///
    /// <para>
    /// 存在理由：只有「问」的一半时，工程里 <c>Find Servers</c> 恒为 0 台**不是"没扫到"、是"没人应答"**
    /// —— 与"没有发现能力"在现象上无法区分（最难查的一类问题）。有了应答端，
    /// 「有主机在跑却扫不到」才成为一条**可判定的**断言。
    /// </para>
    ///
    /// <para>
    /// <b>旁路能力</b>（与 <see cref="ILanBrowser"/> 同一套口径）：不走 EMsg / 不进 <see cref="IRouter"/> /
    /// 不占消息号 / 不走线路族（依据 结构规则.md §五 N13）；<b>不依赖</b> <see cref="INetwork"/>。
    /// 本接口不放进 <see cref="Game"/> 门面 —— 由 <c>CloverLan.CreateResponder()</c> 取得，
    /// 因为「我这台是不是主机」是**业务决策**（进哪个模式 / 开哪张图），不是引擎生命周期的一部分。
    /// </para>
    ///
    /// <para>
    /// <b>⚠️ 它只管「能被发现」</b>：应答里广播的 <c>gateway</c> 端口是**参数**
    /// （调用方给的那台真网关的端口），不是本接口自己起的服务。
    /// 「列表里看得到、点加入却接不上」= 网关没起来，与本接口无关。
    /// </para>
    ///
    /// <para>
    /// <b>线程模型</b>：收包在后台线程，<see cref="OnQuery"/> 经 <see cref="IDispatcher.Post"/> 收敛到主线程
    /// （<see cref="Game.Dispatcher"/> 为 null 时直接执行）。公开方法**仅主线程可调**（结构规则 §5.2 G2）。
    /// </para>
    ///
    /// <para>
    /// <b>四条边界都不抛异常</b>（调用方的"开主机"流程不该被环境差异打断，但必须**留痕**）：
    /// <list type="bullet">
    /// <item>平台不支持（WebGL 无 BSD socket）⇒ <c>Start</c> 返回 false，<see cref="LastError"/> 给出原因；</item>
    /// <item>端口被占用 ⇒ <c>Start</c> 返回 false，<see cref="LastError"/> 给出原因；</item>
    /// <item><c>Start</c> 重复调用 ⇒ 幂等：已在跑时只更新广播参数并返回 true（端口不可在运行中更换，打 Warn）；</item>
    /// <item><c>Stop</c> 未启动 ⇒ 幂等空操作。</item>
    /// </list>
    /// </para>
    /// </summary>
    public interface ILanResponder : IDisposable
    {
        /// <summary>当前平台能否应答（判定与 <see cref="ILanBrowser.IsSupported"/> 同源，避免"能问不能答"）。</summary>
        bool IsSupported { get; }

        /// <summary>不能应答时的原因（可直接显示给玩家）；能时为空串。</summary>
        string UnsupportedReason { get; }

        /// <summary>是否正在应答。</summary>
        bool IsRunning { get; }

        /// <summary>最近一次失败原因（成功后清空）；可直接显示给玩家 / 进日志。</summary>
        string LastError { get; }

        /// <summary>当前对外广播的主机快照（<c>Start</c> 后非 null；未 <c>Start</c> 时为 null）。</summary>
        LanHostInfo Self { get; }

        /// <summary>实际监听端口；未在跑时为 0（<c>Start</c> 时端口非法/被占用不会改它）。</summary>
        int ListeningPort { get; }

        /// <summary>收到的**合法查询**总数（判据用：&gt;0 说明"确实有人在找服"）。</summary>
        long QueryCount { get; }

        /// <summary>回出去的应答总数（判据用：正常情况下应等于 <see cref="QueryCount"/>）。</summary>
        long ReplyCount { get; }

        /// <summary>丢弃的非法包数（不是查询 / 超长 / 空）—— 留痕，不静默。</summary>
        long DroppedCount { get; }

        /// <summary>
        /// 开始应答（幂等）。已在跑时**只更新广播参数**（主机名 / 人数可以变）并返回 true，
        /// socket 与收包线程不动；端口在运行中不可更换（会打 Warn）。
        /// </summary>
        /// <param name="self">要对外广播的主机信息；<c>Host</c> 留空 = 自动取本机 IPv4</param>
        /// <param name="options">启动参数；null = 全默认（<c>LanProtocol.DefaultPort</c>）</param>
        /// <returns>是否处于"在跑"状态（失败原因见 <see cref="LastError"/>）</returns>
        bool Start(LanHostInfo self, LanRespondOptions options = null);

        /// <summary>停止应答（幂等；未启动时是空操作）。收包线程在 ≤ <c>LanSocket.ReceiveTimeoutMs</c> 内退出。</summary>
        void Stop();

        /// <summary>一行状态（日志 / 调试面板 / 探针共用，避免三处文案各写一套）。</summary>
        string Describe();

        /// <summary>
        /// 收到一次**合法查询**触发一次（主线程；参数 = 查询来源的 <c>ip:port</c>，未知时为 <c>"&lt;未知&gt;"</c>）。
        /// 便于业务留痕 / 计数；高频查询由实现负责限频日志，订阅方无需自己限频。
        /// </summary>
        event Action<string> OnQuery;
    }
}
