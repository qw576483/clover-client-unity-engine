using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CloverEngine
{
    /// <summary>
    /// 服务端错误回包异常：Call&lt;T&gt; 收到 msgID = EMsg.Error 的错误回包时，
    /// 任务以此异常结束。携带原始 requestID / msgID 与服务端错误描述，供业务侧细分类处理。
    ///
    /// <code>
    /// try { await Game.Net.Call&lt;ELoginReply&gt;(EMsg.Login, req); }
    /// catch (CloverCallException ex) { Game.Logger?.Warn("Net", ex.ServerError); }
    /// catch (System.TimeoutException)  { /* 超时 */ }
    /// </code>
    /// </summary>
    public class CloverCallException : Exception
    {
        /// <summary>发起请求时分配的请求 ID</summary>
        public uint RequestID { get; }

        /// <summary>错误回包的消息 ID（恒为 EMsg.Error）</summary>
        public uint MsgID { get; }

        /// <summary>服务端 EErrorReply.err 错误描述</summary>
        public string ServerError { get; }

        /// <summary>
        /// 服务端错误码（0 = 未分类），取值见 <see cref="ErrCode"/>，如 401 = 未认证。
        /// 业务应据码判断（如 401 触发重新登录），不要匹配 <see cref="ServerError"/> 文案。
        /// </summary>
        public int Code { get; }

        /// <summary>
        /// 构造服务端错误回包异常
        /// </summary>
        /// <param name="requestID">发起请求时分配的请求 ID</param>
        /// <param name="msgID">错误回包的消息 ID</param>
        /// <param name="serverError">服务端错误描述</param>
        /// <param name="code">服务端错误码（0 = 未分类）</param>
        public CloverCallException(uint requestID, uint msgID, string serverError, int code = 0)
            : base(code != 0
                ? $"server rejected request {requestID}: {serverError} (code={code})"
                : $"server rejected request {requestID}: {serverError}")
        {
            RequestID = requestID;
            MsgID = msgID;
            ServerError = serverError;
            Code = code;
        }
    }

    /// <summary>
    /// 网络管理实现：TCP/UDP 双通道连接管理、客户端帧收发、请求-响应配对（Call）、
    /// 裸 UDP 通道绑定（EMsg.UDPBindGrant 令牌下发 → EMsg.BindUDP 上报 + 周期保活）、
    /// 断线自动重连（指数退避）与会话恢复（EMsg.ResumeSession）。
    ///
    /// 线协议约定（与 clover-server-engine 对齐）：
    ///   - 客户端帧：[4B requestID][4B msgID][JSON body]，大端；
    ///   - 回包 msgID 恒为 0，按 requestID 配对；错误回包 msgID 为 EMsg.Error（body 为 EErrorReply{err, code}）；
    ///   - 推送 requestID 恒为 0；不可靠消息 requestID 恒为 0；
    ///   - 登录后网关经 TCP 下发 EMsg.UDPBindGrant（体为绑定令牌 UTF-8 原文，非 JSON），
    ///     客户端建 UDP socket 后经 UDP 发 EMsg.BindUDP（体为令牌原文）完成端点绑定；
    ///   - 会话通道加密：登录请求声明 ELoginRequest.encrypt（本类按平台能力自动置位），
    ///     服务端在登录回包里明文回 session_key，之后双方**整帧** AES-256-GCM 加解密
    ///     （线格式 [12B nonce][ciphertext||tag]，见 SessionCrypto）。未协商则全程明文。
    ///
    /// 事件（经 Game.Event 发布，均在主线程回调，事件名见 Contracts.INetwork 注释）：
    ///   Net.OnConnected / Net.OnDisconnected / Net.OnConnectFailed / Net.OnKicked /
    ///   Net.OnResumed / Net.OnResumeFailed / Net.OnUnauthorized /
    ///   Net.PlayerFullSync / Net.Alert / Net.SceneChanged / Net.QueuePosition。
    ///
    /// Net.OnUnauthorized 在「任一请求被拒为未认证（code=401）」时发布（参数为 EErrorReply）：
    /// 网关登录门禁直接拒绝（未登录就发业务消息）、或逻辑服返回 401 业务错误，两者归一。
    ///
    /// 路由能力：本类同时实现 <see cref="IRouter"/>，但 INetwork **不含** OnMsg/OffMsg，
    /// 因此业务侧唯一入口是 Game.OnMsg / Game.OffMsg；内部模块（WorldSync / AlertManager /
    /// CloverScene / FrameRoomManager）由 CloverNet.Init 注入 IRouter 使用。
    /// </summary>
    internal class NetworkManager : INetwork, IRouter
    {
        /// <summary>重连退避间隔上限（秒）：2^n 秒增长封顶 60 秒</summary>
        private const double MaxReconnectDelaySeconds = 60;

        /// <summary>
        /// 单个 Call 的**总期限**下限（秒）：排队期间每收到一条位置帧就顺延一次超时，
        /// 没有总期限时服务端只要持续刷新位置，在途 Call 就永远不会超时。
        /// 实际取值 = max(本值, CallTimeoutSeconds)，保证总期限不会反过来短于单次超时。
        /// <para>
        /// **为什么客户端必须有这个总期限**：队列停滞期（服务端排队中）**不下发位置帧**，
        /// 此时没有任何"顺延"事件，靠顺延语义本身不足以兜住 —— 必须有一条与位置帧无关的硬上限，
        /// 否则 Call 在排队期会一直挂着、业务永远等不到失败结论。
        /// </para>
        /// </summary>
        private const double MinPendingHardTimeoutSeconds = 60;

        /// <summary>登录请求最近一次使用的 requestID（0 = 无）；用于定位"哪条回包可能带 session_key"</summary>
        private uint _loginRequestId;

        /// <summary>RTT 指数移动平均的旧值权重（EMA：avg = avg*0.9 + last*0.1）</summary>
        private const double RttEmaAlpha = 0.9;

        /// <summary>
        /// QUIC 线路的连接超时（毫秒）：**比 TCP 的默认 5s 短**。
        /// UDP 上"对端没监听 / 证书不受信"不会有错误回包，只会静默无响应 ——
        /// 用完整超时会让"服务端没开 QUIC"的每次启动都白等 5 秒。
        /// 失败一次后本进程内不再排 QUIC（见 TransportCapabilities），因此这个超时只付一次。
        /// </summary>
        private const int QuicConnectTimeoutMs = 2000;

        /// <summary>可靠通道实现（TCP 或 WebSocket）；由线路选择决定，降级时整体替换</summary>
        private ITransportConnection _reliable;

        private UdpConnection _udp;

        /// <summary>线路配置（已按平台裁剪的可尝试序列），Connect 时快照，重连复用</summary>
        private TransportOptions _options;

        /// <summary>当前使用的可靠线路下标（降级链游标）</summary>
        private int _lineIndex;

        /// <summary>是否已成功连上过任意一条线路（判断「换线路重试」还是「退避重连」）</summary>
        private bool _anyLineConnected;

        /// <summary>首个线路地址（"host:port" 或 ws URL），作为「是否配置过服务器」的判据</summary>
        private string _serverAddr;

        /// <summary>弱网模拟器（调试用）：装饰在可靠传输之外，可靠线路共用同一套参数（裸 UDP 不包裹）</summary>
        private readonly NetworkSimulator _simulator = new NetworkSimulator();

        /// <summary>共享裸 UDP 端点（"host:port"），null 表示不启用不可靠通道</summary>
        private string _udpAddr;

        /// <summary>EMsg.UDPBindGrant 下发的 UDP 绑定令牌（UTF-8 原文，非 JSON）</summary>
        private string _udpBindToken;

        /// <summary>UDP 通道是否已绑定（发出 BindUDP 帧即视为已绑定）</summary>
        private volatile bool _udpBound;

        /// <summary>
        /// 会话通道加密密钥（从登录回包 session_key 提取的 32B AES-256 密钥）；
        /// null = 本次连接为明文会话（未协商 / 平台不支持 / 服务端未下发密钥）。
        /// </summary>
        private byte[] _channelKey;

        /// <summary>
        /// 通道加密是否已确认可用：首次成功解密一帧后置位。
        /// 语义与服务端 gwcore.Session.handshakeOK 镜像——握手窗口内（登录回包本身就是明文）
        /// 解密失败按明文处理，确认之后解密失败才是异常。
        /// </summary>
        private bool _channelHandshakeOk;

        /// <summary>排队位置：前面还有多少人；-1 = 当前不在排队（见 EMsg.QueuePosition）</summary>
        private volatile int _queueAhead = -1;

        /// <summary>排队位置：当前队列总人数（含自己）；不在排队时无意义</summary>
        private volatile int _queueTotal;

        /// <summary>排队编号（入队序号，仅用于展示与排障）</summary>
        private long _queueTicket;

        /// <summary>本次 Connect 生命周期内是否成功连上过（区分 OnConnectFailed 与 OnKicked）</summary>
        private bool _everConnected;

        /// <summary>上行被丢弃的告警是否已经打过（每个连接周期最多一条，防刷屏）</summary>
        private bool _sendDropWarned;

        private readonly Session _session;

        private readonly Router _router;

        /// <summary>进行中的请求-响应配对表，key 为 requestID（回包/超时/断线三路竞争移除）</summary>
        private readonly ConcurrentDictionary<uint, PendingCall> _pendingCalls = new();

        /// <summary>当前自动重连已尝试次数（成功连上即清零）</summary>
        private int _reconnectCount;

        /// <summary>延迟自动重连的一次性定时器（线程池线程触发，回调内切主线程）</summary>
        private Timer _reconnectTimer;

        /// <summary>裸 UDP 绑定保活定时器（周期重发 BindUDP 帧维持 NAT 映射）</summary>
        private Timer _udpKeepaliveTimer;

        /// <summary>最近一次回包往返耗时（毫秒）</summary>
        private double _rttLastMs;

        /// <summary>回包往返耗时指数移动平均（毫秒）</summary>
        private double _rttAvgMs;

        /// <summary>历史最大回包往返耗时（毫秒）</summary>
        private double _rttMaxMs;

        /// <summary>最近收到的错误回包（调试器展示用）</summary>
        private EErrorReply _lastErrorReply;

        /// <summary>最近收到的错误回包对应的请求 ID（调试器展示用）</summary>
        private uint _lastErrorRequestID;

        // 下面几项的兜底值**只取自 GameConfig 的默认常量**（唯一出处，见 GameConfig.Default*）：
        // 不要在本文件里再写字面量，否则「配置缺省时用哪套默认值」会两处静默分歧。

        /// <summary>Call 超时（秒），来自 GameConfig，0 值兜底为 <see cref="GameConfig.DefaultCallTimeoutSeconds"/></summary>
        private readonly double _callTimeoutSeconds;

        /// <summary>断线自动重连最大尝试次数，来自 GameConfig，0 值兜底为 <see cref="GameConfig.DefaultMaxReconnectCount"/></summary>
        private readonly int _maxReconnectCount;

        /// <summary>裸 UDP 保活周期（毫秒），来自 GameConfig，0 值兜底为 <see cref="GameConfig.DefaultUdpKeepaliveIntervalSeconds"/> * 1000</summary>
        private readonly long _udpKeepaliveIntervalMs;

        /// <summary>线路心跳间隔（毫秒），来自 GameConfig，0 值兜底为 <see cref="GameConfig.DefaultHeartbeatIntervalMs"/></summary>
        private readonly int _heartbeatIntervalMs;

        /// <summary>线路连接超时（毫秒），来自 GameConfig，0 值兜底为 <see cref="GameConfig.DefaultConnectTimeoutMs"/></summary>
        private readonly int _connectTimeoutMs;

        /// <summary>WebSocket 升级路径，来自 GameConfig.WsPath，空则用 <see cref="GameConfig.DefaultWsPath"/></summary>
        private readonly string _wsPath;

        /// <summary>是否对线路启用 TLS，来自 GameConfig.UseTls</summary>
        private readonly bool _useTls;

        /// <summary>
        /// 进行中的请求-响应配对项
        /// </summary>
        private sealed class PendingCall
        {
            /// <summary>请求发出时间戳（Stopwatch ticks），回包到达时计算 RTT</summary>
            public long StartTicks;

            /// <summary>
            /// 总期限截止时间戳（Stopwatch ticks）：排队顺延**不会**越过它。
            /// 只顺延不封顶会让服务端持续刷新排队位置即可让在途 Call 永不超时。
            /// </summary>
            public long HardDeadlineTicks;

            /// <summary>超时定时器（一次性），回包到达或请求失败时释放</summary>
            public Timer TimeoutTimer;

            /// <summary>回包体反序列化函数（参数：body 字节、偏移、长度），闭包持有目标类型</summary>
            public Func<byte[], int, int, object> Deserialize;

            /// <summary>正常回包完成回调（参数为反序列化结果）</summary>
            public Action<object> Complete;

            /// <summary>失败回调：超时（TimeoutException）/ 错误回包（CloverCallException）/ 断线（InvalidOperationException）</summary>
            public Action<Exception> Fail;
        }

        /// <summary>
        /// 可靠通道是否已建立连接（TCP 或 WebSocket，取决于当前线路）
        /// </summary>
        public bool IsConnected => _reliable != null && _reliable.State == ConnectionState.Connected;

        /// <summary>
        /// 上行总闸（默认 true，见 <see cref="INetwork.SendEnabled"/>）。
        /// 置 false 后 Send / SendUnreliable 直接丢弃并只告警一次，Call 立即失败。
        /// </summary>
        public bool SendEnabled { get; set; } = true;

        private const string NotConnectedReason = "not connected";
        private const string GateClosedReason = "send gate closed (Net.SendEnabled=false)";

        /// <summary>
        /// 上行被丢弃时的**降频**告警：同一连接周期内只提示一次。
        ///
        /// 为什么必须降频：未进图 / 断线期间业务往往还在按 20Hz 推移动（一次断线刷出 3262 条），
        /// 逐条 Warn 会把日志刷爆并掩盖真问题；但完全不报又会让"我这条消息到底发出去没有"变成悬案。
        /// 连接建立时复位，保证每个连接周期至少有一条可查。
        /// </summary>
        private void WarnSendDropped(string reason, uint msgID)
        {
            if (_sendDropWarned) return;
            _sendDropWarned = true;
            Game.Logger?.Warn("Network",
                $"send dropped: {reason} (msgID={msgID})；同一连接周期内后续同类不再重复打印");
        }

        /// <summary>当前可靠线路类型（尚未连接时为配置里主链的类型）</summary>
        public TransportKind ActiveTransport => _reliable != null ? _reliable.Kind : TransportKind.Tcp;

        /// <summary>当前使用的线路描述（调试面板展示用）</summary>
        public string ActiveLine
        {
            get
            {
                if (_options == null || _options.ReliableOrder.Count == 0)
                    return "(未配置线路)";
                var i = Math.Min(_lineIndex, _options.ReliableOrder.Count - 1);
                return _options.ReliableOrder[i].Describe();
            }
        }

        /// <summary>线路计划全貌：主链 → 降级链 | udp（调试面板展示用）</summary>
        public string LinePlan => _options?.Describe() ?? "(未配置线路)";

        /// <summary>
        /// 弱网模拟器（延迟 / 抖动 / 丢包）。默认关闭，装饰在可靠传输之外，
        /// 因此对 TCP / WebSocket 两条可靠线路生效（裸 UDP 未被包裹）；连接建立后改参数也立即生效。
        /// </summary>
        public NetworkSimulator Simulator => _simulator;

        /// <summary>
        /// 常驻裸 UDP 通道是否已绑定
        /// </summary>
        public bool IsUdpBound => _udpBound && _udp != null && _udp.State == ConnectionState.Connected;

        /// <summary>
        /// 是否正处于服务端排队中（服务器限流/满载时连入等候队列）。
        /// 由 EMsg.QueuePosition 置位、收到任意回包或断线时清除——排队中服务端会丢弃后续帧，
        /// 因此「收到回包」即等价于已被放行。业务据此显示「您前面还有 N 人」的等待界面。
        /// </summary>
        public bool IsQueued => _queueAhead >= 0;

        /// <summary>
        /// 排队位置：前面还有多少人（0 = 队首，下一个就放行）。未排队时为 -1。
        /// 与 <see cref="INetwork.QueueAhead"/> 同源，刷新时机见 EMsg.QueuePosition。
        /// </summary>
        public int QueueAhead => _queueAhead;

        /// <summary>排队位置：当前队列总人数（含自己）。未排队时为 0。</summary>
        public int QueueTotal => _queueTotal;

        /// <summary>排队编号（入队序号，单调递增；仅用于展示与排障）。未排队时为 0。</summary>
        public long QueueTicket => _queueTicket;

        /// <summary>
        /// 本次连接是否已启用会话通道加密（AES-256-GCM，成功从登录回包拿到密钥）。
        /// 平台不支持 AES-GCM（如 WebGL）时为 false —— 此时登录请求不会声明 encrypt，
        /// 服务端保持明文，链路加密由 wss / QUIC / WebTransport 的 TLS 承担。
        /// </summary>
        public bool IsChannelEncrypted => _channelKey != null;

        /// <summary>清除排队态（放行 / 断线时调用；幂等）。</summary>
        private void ClearQueueState()
        {
            _queueAhead = -1;
            _queueTotal = 0;
            _queueTicket = 0;
        }

        /// <summary>
        /// 清除通道加密态（建立新连接时调用；幂等）。
        /// 为什么在"建连"而不是"断线"清：通道加密是**连接级**的，重连后服务端是一个全新会话、
        /// 没有密钥；若沿用旧密钥发送，密文在服务端当明文解析会直接失败（表现为重连后所有消息石沉大海）。
        /// </summary>
        private void ClearChannelCrypto()
        {
            // 废弃的密钥就地清零：仅清引用不够——密钥会长期滞留托管堆，堆转储即可提取。
            SessionCrypto.ZeroKey(_channelKey);
            _channelKey = null;
            _channelHandshakeOk = false;
        }

        /// <summary>
        /// 出站帧加密：未启用通道加密时原样返回（含首帧登录请求——那时还没有密钥）；
        /// 启用后整帧（含 8B 帧头）加密，**失败返回 null**：调用方必须丢弃该帧，
        /// 绝不能退化成明文发送——服务端在握手确认状态下收到明文会解密失败并断开连接。
        /// 长度护栏在 <see cref="SessionCrypto.Encrypt"/> 内，上限取**服务端权威值**
        /// （<see cref="SessionCrypto.MaxPlaintextBytes"/> = 服务端 maxDecryptSize 10 MiB − nonce − tag；
        /// 服务端该上限 = <c>session.MaxFrameSize</c>，与网关帧上限同源）：
        /// 超过它的密文服务端解不开且会按"篡改"断连，故这里先拦下来丢帧。
        /// </summary>
        private byte[] EncryptOutbound(byte[] frame)
        {
            if (_channelKey == null) return frame;
            var enc = SessionCrypto.Encrypt(_channelKey, frame);
            if (enc == null)
                Game.Logger?.Error("Network", "channel encrypt failed — frame dropped (never fall back to plaintext)");
            return enc;
        }

        /// <summary>
        /// 从一条回包里协商通道加密：带 session_key（base64 的 32B）则启用。
        /// 放引擎而不放业务：回包没有消息号（只有 requestID），业务无法判断"这条是不是登录回包"，
        /// 而引擎只需按字段名解析——与服务端 <c>auth.ExtractSessionKey</c> 同款做法。
        /// </summary>
        private void TryEnableChannelCrypto(byte[] data, int offset, int length)
        {
            var key = SessionCrypto.TryExtractKey(data, offset, length);
            if (key == null) return;

            // 同一连接上二次登录（切号）会拿到**新**密钥：服务端把这帧用旧密钥加密发出后才切换密钥，
            // 因此这里必须"覆盖 + 重开握手窗口"，否则客户端留在旧密钥上、之后所有帧解密失败。
            var rotated = _channelKey != null;
            if (rotated)
                SessionCrypto.ZeroKey(_channelKey); // 旧密钥立即作废并清零（切号重登轮换）
            _channelKey = key;
            _channelHandshakeOk = false; // 握手窗口：下一条密文帧解密成功后才置位
            Game.Logger?.Info("Network", rotated
                ? "channel encryption key rotated (re-login)"
                : "channel encryption enabled (AES-256-GCM)");
        }

        /// <summary>
        /// 把全部未决请求的超时重新计时（排队期间收到位置帧时调用）。
        ///
        /// 为什么需要：排队中服务端会丢弃后续帧，首帧（登录）的回包要等放行后才到。
        /// 若等待时间超过 CallTimeoutSeconds，登录请求会先被判超时失败——界面上却还显示
        /// 「您前面还有 N 人」，放行后回包到达时配对项已不在，玩家只能重来一次（还可能再排一轮）。
        /// 语义边界：只在服务端**仍在刷新位置**期间延期（刷新即「还在正常排队」的证据）；
        /// 服务端停止刷新（连接断开 / 排队超时被踢）后不再延期，超时定时器自然触发。
        /// </summary>
        private void ExtendPendingDeadlines()
        {
            var dueMs = Math.Max(1, (long)(_callTimeoutSeconds * 1000));
            var now = Stopwatch.GetTimestamp();
            foreach (var kv in _pendingCalls)
            {
                var pending = kv.Value;

                // 总期限：只顺延到 HardDeadlineTicks 为止（服务端持续刷新排队位置也不能无限延期）。
                var remainMs = (pending.HardDeadlineTicks - now) * 1000L / Stopwatch.Frequency;
                if (remainMs <= 0)
                {
                    Game.Logger?.Warn("Network",
                        $"request {kv.Key} 已达总期限（{MinPendingHardTimeoutSeconds:0}s 起），不再因排队位置顺延");
                    continue;
                }

                // 定时器可能已被回包路径 Dispose（Change 对已释放定时器会抛 ObjectDisposedException）
                try
                {
                    pending.TimeoutTimer?.Change(Math.Min(dueMs, remainMs), Timeout.Infinite);
                }
                catch (ObjectDisposedException)
                {
                    // 回包与排队位置帧同时到达：该请求已完成，忽略即可。
                }
            }
        }

        /// <summary>
        /// 当前会话信息（账号 / player_id / 会话密钥 / 线路）。
        /// 全量同步推送到达时自动补齐缺失字段。
        /// </summary>
        public SessionInfo Session => _session.Info;

        /// <summary>
        /// 构造网络管理器，网络参数取 Game.Config（Game.Launch 快照），未启动引擎时使用内置默认值
        /// </summary>
        public NetworkManager() : this(null)
        {
        }

        /// <summary>
        /// 内部构造：允许注入配置覆盖（仅供编辑器单元测试使用，免 Game.Launch 直接构造实例）。
        /// 各参数为 0 值时逐项兜底引擎默认值。
        /// </summary>
        /// <param name="configOverride">配置覆盖，null 时回退 Game.Config</param>
        internal NetworkManager(GameConfig configOverride)
        {
            var cfg = configOverride ?? Game.Config;
            // 兜底值一律取自 GameConfig 的默认常量：**值只在那一处定义**（原先这里又抄了一份同样的
            // 字面量，两处各改一半就会出现「配置缺省时到底用哪套默认值」的静默分歧）。
            _callTimeoutSeconds = cfg != null && cfg.CallTimeoutSeconds > 0 ? cfg.CallTimeoutSeconds : GameConfig.DefaultCallTimeoutSeconds;
            _maxReconnectCount = cfg != null && cfg.MaxReconnectCount > 0 ? cfg.MaxReconnectCount : GameConfig.DefaultMaxReconnectCount;
            _udpKeepaliveIntervalMs = (long)(cfg != null && cfg.UdpKeepaliveIntervalSeconds > 0 ? cfg.UdpKeepaliveIntervalSeconds : GameConfig.DefaultUdpKeepaliveIntervalSeconds) * 1000;
            _heartbeatIntervalMs = cfg != null && cfg.HeartbeatIntervalMs > 0 ? cfg.HeartbeatIntervalMs : GameConfig.DefaultHeartbeatIntervalMs;
            _connectTimeoutMs = cfg != null && cfg.ConnectTimeoutMs > 0 ? cfg.ConnectTimeoutMs : GameConfig.DefaultConnectTimeoutMs;
            _wsPath = cfg != null && !string.IsNullOrEmpty(cfg.WsPath) ? cfg.WsPath : GameConfig.DefaultWsPath;
            _useTls = cfg != null && cfg.UseTls;

            _session = new Session();
            _router = new Router();

            // 内部全量同步处理器先于 WorldSync.Attach 注册：Router 按注册顺序分发，
            // 保证本类先补齐会话凭证后再通知业务侧监听器
            _router.OnMsg(EMsg.PushPlayerFullSync, HandlePlayerFullSync);

            // 传输实例在 Connect 时按线路创建（降级会整体替换），构造期只快照参数。
        }

        /// <summary>
        /// 连接到指定地址（非阻塞，不卡主线程；不启用裸 UDP 通道，不可靠消息降级走 TCP）
        /// </summary>
        /// <param name="addr">目标地址，格式为 "host:port"</param>
        public void Connect(string addr)
        {
            Connect(addr, null);
        }

        /// <summary>
        /// 连接到指定地址（非阻塞），并配置共享 UDP 端点；登录成功后收到 EMsg.UDPBindGrant
        /// 将自动建立常驻 UDP 通道（承载不可靠消息）。
        /// 地址格式非法（TcpConnection.ConnectAsync 同步抛 FormatException）时记录错误并发布 Net.OnConnectFailed。
        /// </summary>
        /// <param name="addr">TCP 目标地址，格式为 "host:port"</param>
        /// <param name="udpAddr">裸 UDP 共享端点，格式为 "host:port"，传 null 不启用</param>
        public void Connect(string addr, string udpAddr)
        {
            // 兼容旧签名：只给了 TCP 地址（+ 可选 UDP）时，用 GameConfig 里的 WS 配置补齐降级链。
            // QUIC 与裸 UDP 共用服务端 listen_udp，所以 QUIC 线路地址就是 udpAddr；
            // 是否真的加进去由 TransportCapabilities 决定（原生平台 + 插件可用 + 本进程未失败过），
            // 被裁掉时会在"已按平台裁剪线路"日志里写明原因 —— 不静默。
            Connect(TransportOptions.From(addr, udpAddr, Game.Config?.WsAddr, _wsPath, _useTls, QuicLineAddrFor(udpAddr)));
        }

        /// <summary>
        /// QUIC 线路地址：QUIC 与裸 UDP 共用服务端 <c>listen_udp</c>，故取 <paramref name="udpAddr"/>；
        /// 平台/插件/本进程失败史任一不满足时返回 null（即不把 QUIC 排进线路，理由见
        /// <see cref="TransportCapabilities"/>）。
        /// </summary>
        private static string QuicLineAddrFor(string udpAddr)
        {
            if (string.IsNullOrEmpty(udpAddr))
                return null;
            if (TransportCapabilities.IsSupported(TransportKind.Quic))
                return udpAddr;

            // 配了 UDP 端点却没排 QUIC ⇒ 必须让"为什么"可见（否则就是静默降级）：
            // 之前真机包就是这样只剩 TCP 且全包无一条 QUIC 日志，查起来靠猜。
            if (!_quicExclusionLogged)
            {
                _quicExclusionLogged = true;
                Game.Logger?.Warn("Network",
                    $"QUIC 未排入线路（已配 udp={udpAddr}）：{MsQuicRuntime.UnavailableReason ?? TransportCapabilities.QuicSessionFailureReason ?? "未知原因"}");
            }

            return null;
        }

        /// <summary>"QUIC 被排除的原因"只打一次（每次规划都打会刷屏）。</summary>
        private static bool _quicExclusionLogged;

        /// <summary>
        /// 按线路计划连接：取首个线路发起连接；连不上时自动沿降级链前进
        /// （见 <see cref="HandleDisconnected"/>），全部线路用尽才进入指数退避重连。
        /// </summary>
        /// <param name="options">线路计划（主链 → 降级链）。</param>
        public void Connect(TransportOptions options)
        {
            var trimmed = TransportPlanner.ApplyPlatformRules(options, out var note);
            if (!string.IsNullOrEmpty(note))
                Game.Logger?.Warn("Network", note);

            if (trimmed.ReliableOrder.Count == 0)
            {
                // 没有可用线路时明确报错，不做静默降级（WebGL 需配 GameConfig.WsAddr）
                Game.Logger?.Error("Network",
                    $"no usable transport line: {options?.Describe() ?? "(null)"}；WebGL 平台需配置 GameConfig.WsAddr");
                PostOrRun(() => Game.Event?.Emit(CloverEvents.Net.OnConnectFailed));
                return;
            }

            // 重复 Connect 必须先断开：ConnectCurrentLine 会直接覆盖 _reliable，
            // 旧线路的 socket 与收发线程会泄漏，其 Disconnected 回调稍后还会二次触发 HandleDisconnected
            // （表现为"降级链被旧连接的断开事件打断"）。Disconnect 幂等且不发布任何事件。
            if (_reliable != null || _options != null)
                Disconnect();

            _options = trimmed;
            _udpAddr = trimmed.UdpAddr;
            _lineIndex = 0;
            _serverAddr = TransportPlanner.ConnectTargetOf(trimmed.ReliableOrder[0]);
            _everConnected = false;
            _anyLineConnected = false;
            _reconnectCount = 0;
            DisposeReconnectTimer();

            Game.Logger?.Info("Network", $"line plan: {trimmed.Describe()}");
            ConnectCurrentLine();
        }

        /// <summary>
        /// 用当前线路下标创建传输实例并发起连接（降级时由 <see cref="_lineIndex"/> 前进）。
        /// 地址非法或线路不受支持时记录错误并发布 <c>Net.OnConnectFailed</c>。
        /// </summary>
        private void ConnectCurrentLine()
        {
            var line = _options.ReliableOrder[_lineIndex];
            // 新连接 = 服务端上的新会话：通道加密密钥随旧连接一起作废，
            // 必须清掉（否则重连后用旧密钥发密文，服务端当明文解析，全部消息静默失败）。
            // 密钥由新连接的登录回包重新协商下发。
            ClearChannelCrypto();
            try
            {
                var transport = TransportPlanner.Create(line);
                switch (transport)
                {
                    case TcpConnection tcp:
                        tcp.HeartbeatIntervalMs = _heartbeatIntervalMs;
                        tcp.ConnectTimeoutMs = _connectTimeoutMs;
                        tcp.UseTls = line.UseTls;
                        break;
                    case WebSocketConnection ws:
                        ws.HeartbeatIntervalMs = _heartbeatIntervalMs;
                        ws.ConnectTimeoutMs = _connectTimeoutMs;
                        break;
                    case QuicConnection quic:
                        quic.HeartbeatIntervalMs = _heartbeatIntervalMs;
                        // QUIC 的连接超时**刻意更短**：UDP 上"对端没监听/证书不受信"不报错、只是没响应，
                        // 用完整的 TCP 超时（默认 5s）会让每次启动都白等 5 秒。失败后本进程内不再排它。
                        quic.ConnectTimeoutMs = QuicConnectTimeoutMs;
                        break;
                }

                // 传输回调可能来自后台线程，统一经 PostOrRun 切回主线程
                transport.Connected += () => PostOrRun(HandleConnected);
                transport.Disconnected += () => PostOrRun(HandleDisconnected);

                // 弱网模拟装饰在内层传输之外：未启用时仅一次虚调用，启用后可靠线路共用同一套延迟/丢包参数
                _reliable = new SimulatedTransport(transport, _simulator);
                Game.Logger?.Info("Network", $"connecting via {line.Describe()}");
                transport.ConnectAsync(TransportPlanner.ConnectTargetOf(line));
            }
            catch (Exception e)
            {
                Game.Logger?.Error("Network", $"connect failed on {line.Describe()}: {e.Message}");

                // 必须推进降级链游标：地址非法 / Create 抛 NotSupportedException 时若只发
                // OnConnectFailed，后续线路**永远不会被尝试**（只配了 WsAddr 的 WebGL 工程
                // 首条 TCP 线解析失败即整个连不上）。
                if (_options != null && _lineIndex + 1 < _options.ReliableOrder.Count)
                {
                    if (line.Kind == TransportKind.Quic)
                        TransportCapabilities.MarkQuicUnavailableForSession("connect or handshake failed");

                    _lineIndex++;
                    Game.Logger?.Info("Network", $"falling back to next line: {ActiveLine}");
                    ConnectCurrentLine();
                    return;
                }

                PostOrRun(() => Game.Event?.Emit(CloverEvents.Net.OnConnectFailed));
            }
        }

        /// <summary>
        /// 立即重连：清除退避计数并用上次地址发起连接（手动恢复入口，调试器 GM 使用）。
        /// 未记录过服务器地址时仅告警忽略。
        /// </summary>
        public void Reconnect()
        {
            if (string.IsNullOrEmpty(_serverAddr))
            {
                Game.Logger?.Warn("Network", "reconnect ignored: no server address recorded");
                return;
            }

            _reconnectCount = 0;
            DisposeReconnectTimer();

            if (_options == null || _options.ReliableOrder.Count == 0)
            {
                Game.Logger?.Warn("Network", "reconnect ignored: no line plan recorded");
                return;
            }

            // 旧链路可能仍在线（调试器 GM 可在链路正常时呼叫手动重连）：ConnectCurrentLine
            // 会直接覆盖 _reliable，不先断开会让旧 socket 与收发线程泄漏，其断开回调稍后
            // 还会二次触发 HandleDisconnected。这里只拆链路与裸 UDP 通道、**不清会话** ——
            // 重连的意义就是带凭证走恢复（清会话是 Disconnect() 的语义）。
            _reliable?.Disconnect();
            _reliable = null;
            TeardownUdpChannel();

            // 手动重连从主链重新开始（而不是停在降级后的线路）；
            // **两个"曾连上"标志也必须一起复位**：否则曾连上过后手动重连时，主链一次失败
            // 就被当成"已连上过"，既不再走降级链，HandleDisconnected 也会直接判成 OnKicked。
            _everConnected = false;
            _anyLineConnected = false;
            _lineIndex = 0;
            Game.Logger?.Info("Network", "manual reconnect: restart from the primary line");
            ConnectCurrentLine();
        }

        /// <summary>
        /// 主动断开：停止重连/保活定时器、关闭双通道、使全部未决请求失败并清除会话数据。
        /// 手动断开不触发 Disconnected 回调（TcpConnection 语义），因此不发布任何事件。
        /// </summary>
        public void Disconnect()
        {
            DisposeReconnectTimer();
            _reliable?.Disconnect();
            _reliable = null;
            TeardownUdpChannel();
            FailAllPending("连接已主动断开");
            _session.Clear();
            _reconnectCount = 0;

            // 残留态一并复位：否则断开后 IsChannelEncrypted 仍为 true（下一条上行会拿已作废的密钥加密）、
            // IsQueued 仍为 true（业务界面卡在"排队中"）、"曾连上"标志让下一次连接误判成重连。
            ClearChannelCrypto();
            ClearQueueState();
            _everConnected = false;
            _anyLineConnected = false;
            _sendDropWarned = false;
            _loginRequestId = 0;

            // 断线/登出后服务端场景投影必须失效：契约（ICloverScene.Clear）写明"断线/登出时调用"，
            // 不调则业务从 Game.CloverScene 读到过期场景且 IsValid 仍为 true。
            if (Game.CloverScene != null && Game.CloverScene.IsValid)
            {
                Game.CloverScene.Clear();
                Game.Logger?.Info("Network", "clover scene cleared on disconnect");
            }
        }

        /// <summary>
        /// 登记会话凭证（登录成功后调用）：进入恢复会话态，断线后自动按指数退避重连
        /// 并提交 EMsg.ResumeSession。player_id 可先缺省，由全量同步推送补齐。
        /// </summary>
        /// <param name="account">账号标识</param>
        /// <param name="resumeCredential">
        /// 断线恢复凭证；**传 null / 空串**。恢复凭证**不来自登录回包**：
        /// <see cref="ELoginReply.session_key"/> 是会话通道加密密钥（AES-256），
        /// 真正的恢复凭证由随后 <see cref="EMsg.PushPlayerFullSync"/> 下发的 <c>session_token</c> 覆盖
        /// （见 <see cref="HandlePlayerFullSync"/>，非空覆盖）。token 到位前引擎不会提交恢复请求。
        /// </param>
        /// <param name="line">服务器线路编号</param>
        public void SetupSession(string account, string resumeCredential, int line = 0)
        {
            // 保留现有 PlayerID（可能已由全量同步推送补齐），其余字段按入参覆盖
            _session.Setup(account, _session.Info.PlayerID, resumeCredential, line);
            _reconnectCount = 0;

            // 非空恢复凭证是**误用信号**：登录回包的 session_key 是通道加密密钥，不是恢复凭证。
            // 不再把它当恢复凭证写入（见契约注释）：留一条告警指明正确来源。
            if (!string.IsNullOrEmpty(resumeCredential))
                Game.Logger?.Warn("Network",
                    "SetupSession 收到非空恢复凭证：恢复凭证应由 EMsg.PushPlayerFullSync.session_token 下发，" +
                    "登录回包的 session_key 是通道加密密钥、不可当作恢复凭证");
        }

        /// <summary>
        /// 注册指定消息 ID 的处理器（IRouter 实现；业务侧请用 Game.OnMsg）
        /// </summary>
        /// <param name="msgID">消息 ID</param>
        /// <param name="handler">消息处理器</param>
        public void OnMsg(uint msgID, MsgHandler handler)
        {
            _router.OnMsg(msgID, handler);
        }

        /// <summary>
        /// 注销指定消息 ID 的所有处理器（IRouter 实现；业务侧请用 Game.OffMsg）
        /// </summary>
        /// <param name="msgID">消息 ID</param>
        public void OffMsg(uint msgID)
        {
            _router.OffMsg(msgID);
        }

        /// <summary>
        /// 注销指定消息 ID 的特定处理器（IRouter 实现；业务侧请用 Game.OffMsg）
        /// </summary>
        /// <param name="msgID">消息 ID</param>
        /// <param name="handler">要移除的处理器</param>
        public void OffMsg(uint msgID, MsgHandler handler)
        {
            _router.OffMsg(msgID, handler);
        }

        /// <summary>
        /// 分发消息给所有已注册的处理器（IRouter 实现；由 Tick / DrainFrame 内部调用，业务侧无需使用）
        /// </summary>
        /// <param name="msgID">消息 ID</param>
        /// <param name="requestID">请求 ID</param>
        /// <param name="traceID">追踪 ID</param>
        /// <param name="body">消息正文原始数据</param>
        /// <param name="offset">正文数据起始偏移量</param>
        /// <param name="length">正文数据长度</param>
        public void Dispatch(uint msgID, uint requestID, string traceID, byte[] body, int offset, int length)
        {
            _router.Dispatch(msgID, requestID, traceID, body, offset, length);
        }

        /// <summary>
        /// 分配一个**非 0** 的请求 ID。
        ///
        /// 为什么必须跳过 0：回包配对分支的前提是 <c>requestID != 0</c>（推送与不可靠消息
        /// 的 requestID 恒为 0），SessionInfo.AllocateRequestID 回绕后可能返回 0 —— 那样该 Call
        /// 永不配对，只能干等到超时，还会覆盖同名表项让旧请求悬挂。
        /// </summary>
        private uint AllocateRequestId()
        {
            var id = _session.Info.AllocateRequestID();
            if (id == 0)
            {
                id = _session.Info.AllocateRequestID();
                Game.Logger?.Warn("Network", $"request ID wrapped to 0, re-allocated as {id}");
            }
            return id;
        }

        /// <summary>
        /// 发送可靠消息（自增分配 requestID，走 TCP）；未连接时告警忽略
        /// </summary>
        /// <param name="msgID">消息 ID</param>
        /// <param name="msg">消息内容对象（JsonUtility 序列化，null 时发送空体）</param>
        public void Send(uint msgID, object msg)
        {
            if (!IsConnected || !SendEnabled)
            {
                WarnSendDropped(IsConnected ? GateClosedReason : NotConnectedReason, msgID);
                return;
            }

            var body = msg != null ? Serializer.Serialize(msg) : null;
            var requestID = AllocateRequestId();
            // 会话加密已启用时整帧加密；加密失败返回 null → 不发（绝不退化明文，见 EncryptOutbound）。
            var packet = EncryptOutbound(ClientFrame.Encode(requestID, msgID, body));
            if (packet == null) return;
            _reliable?.Send(packet, 0, packet.Length);
        }

        /// <summary>
        /// 发送不可靠消息：requestID 固定为 0（无回包），已绑定裸 UDP 通道时走裸 UDP
        /// （**不加密**，见下），否则走线路自身的不可靠通道或降级可靠发送；未连接时告警忽略。
        /// </summary>
        /// <param name="msgID">消息 ID</param>
        /// <param name="msg">消息内容对象（JsonUtility 序列化，null 时发送空体）</param>
        public void SendUnreliable(uint msgID, object msg)
        {
            if (!IsConnected || !SendEnabled)
            {
                WarnSendDropped(IsConnected ? GateClosedReason : NotConnectedReason, msgID);
                return;
            }

            var body = msg != null ? Serializer.Serialize(msg) : null;
            // 协议约定：不可靠消息不配对回包，requestID 恒为 0
            var frame = ClientFrame.Encode(0, msgID, body);

            // 三条路径，优先级即"最贴近不可靠语义的那条"：
            //   ① 已绑裸 UDP → 走 UDP（老路径，TCP/WS 线路用）；
            //   ② 线路自身能给不可靠语义（QUIC Datagram）→ 走它（**此时无需 EMsg.BindUDP**，
            //      见 Tools~/native/README.md 规则 6）；
            //   ③ 都不是 → 退化为可靠发送（WS/未绑 UDP 的 TCP）。退化是显式且可解释的：
            //      WS 没有不可靠语义，调用方只能接受，而不是"以为发了不可靠其实没有"。
            //
            // ⚠️ ① 裸 UDP 这条**必须发明文、不得整帧加密**（口径与服务端裸 UDP 解帧一致）：
            //    服务端裸 UDP 的 conn 有**独立 connID**（`internal/transport/net/udp/client.go:28`
            //    的 session.NewConnID()），所以裸 UDP 帧落在一个**没有会话 crypto** 的会话上 ——
            //    网关 `gwcore/session.go:494` 的 forwardFrame 取不到 crypto，直接按明文解帧
            //    （`proto.DecodeClientFrame`）。客户端若先加密（本方法旧行为），服务端读到的
            //    requestID/msgID 其实是密文（nonce 前 8 字节），解不出合法帧 → 不可靠上行静默全丢。
            //    这与 `SendUdpBindFrame` 是同一条口径（那一帧同样刻意不加密）。
            // ② / ③ 用的是**同一条会话**（QUIC Datagram / 可靠线路），服务端凭该会话的 crypto
            //    解密，故必须整帧加密；加密失败返回 null → 不发（绝不退化明文，见 EncryptOutbound）。
            if (IsUdpBound)
            {
                _udp.Send(frame, 0, frame.Length);
                return;
            }

            var packet = EncryptOutbound(frame);
            if (packet == null) return;
            if (_reliable is IUnreliableTransport unreliable && unreliable.SupportsUnreliable)
                unreliable.SendUnreliable(packet, 0, packet.Length);
            else
                _reliable?.Send(packet, 0, packet.Length);
        }

        /// <summary>
        /// 发送请求并异步等待响应：按 requestID 配对回包。
        /// 超时以 TimeoutException 结束；收到 EMsg.Error 错误回包以 CloverCallException 结束；
        /// 断线/主动断开以 InvalidOperationException 结束。
        /// 不做连接前置拦截：未连接时 TcpConnection.Send 静默丢弃帧，请求最终按超时失败，
        /// 这使回包配对逻辑可脱离真实链路经 DrainFrame 单元测试。
        /// </summary>
        /// <typeparam name="T">响应消息类型</typeparam>
        /// <param name="msgID">消息 ID</param>
        /// <param name="msg">请求内容对象（JsonUtility 序列化，null 时发送空体）</param>
        /// <returns>包含响应结果的异步任务</returns>
        public Task<T> Call<T>(uint msgID, object msg) where T : class
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

            // 闸门关闭时**立即失败**：否则请求会落进配对表干等超时（默认 10s），
            // 调用方拿到的是 TimeoutException —— 把"业务自己关了上行"误报成"服务端没回"。
            if (!SendEnabled)
            {
                tcs.TrySetException(new InvalidOperationException(
                    $"call msg {msgID} rejected: {GateClosedReason}"));
                return tcs.Task;
            }

            var requestID = AllocateRequestId();

            // 总期限：排队顺延的上限（见 ExtendPendingDeadlines）
            var hardTicks = Stopwatch.GetTimestamp()
                + (long)(Math.Max(_callTimeoutSeconds, MinPendingHardTimeoutSeconds) * Stopwatch.Frequency);

            var pending = new PendingCall
            {
                StartTicks = Stopwatch.GetTimestamp(),
                HardDeadlineTicks = hardTicks,
                Deserialize = (body, offset, length) => Serializer.Deserialize<T>(body, offset, length),
                Complete = result => tcs.TrySetResult((T)result),
                Fail = ex => tcs.TrySetException(ex),
            };

            // 先入配对表再建超时定时器：消除「定时器先触发而配对项未入表」的竞争窗口
            _pendingCalls[requestID] = pending;

            var dueMs = Math.Max(1, (long)(_callTimeoutSeconds * 1000));
            pending.TimeoutTimer = new Timer(_ =>
            {
                // TryRemove + ReferenceEquals 原子竞争：回包先到则配对项已被移除，超时不生效
                if (_pendingCalls.TryRemove(requestID, out var removed) && ReferenceEquals(removed, pending))
                {
                    DisposePendingTimer(pending);
                    pending.Fail?.Invoke(new TimeoutException($"call msg {msgID} timeout ({_callTimeoutSeconds}s)"));
                }
            }, null, dueMs, Timeout.Infinite);

            // 序列化与组帧**必须**在 try 内：ClientFrame.Encode 在 body > 10 MiB（ClientFrame.MaxBodySize）时抛 ArgumentException，
            // 放在 try 外会让异常逃逸，而此时配对项与超时定时器都已建好 —— 请求只能干等超时，
            // 字典项也残留到超时为止。
            try
            {
                // 通道加密协商：登录请求自动声明「支持加密」，业务无需知道这个字段的存在。
                // 仅当平台确实能跑 AES-GCM 时才声明——不支持却声明，会拿到密钥却解不开，
                // 结果是登录成功后所有下行帧失败（比不加密更糟）。
                if (msgID == EMsg.Login && SessionCrypto.IsSupported && msg is ELoginRequest loginReq)
                {
                    loginReq.encrypt = true;
                    _loginRequestId = requestID;
                }

                var body = msg != null ? Serializer.Serialize(msg) : null;
                // 会话加密已启用时整帧加密；加密失败返回 null → 不发（绝不退化明文，见 EncryptOutbound）。
                var packet = EncryptOutbound(ClientFrame.Encode(requestID, msgID, body));
                if (packet == null)
                {
                    // 加密失败（密钥不可用）：立即失败本次请求，而不是发一条明文让服务端断开。
                    FailPending(requestID, pending, new InvalidOperationException("channel encrypt failed"));
                    return tcs.Task;
                }

                _reliable?.Send(packet, 0, packet.Length);
            }
            catch (Exception e)
            {
                // 发送 / 序列化 / 组帧同步异常：立即失败请求并清理配对项与定时器，不向外重抛
                Game.Logger?.Error("Network", $"call msg {msgID} send failed: {e.Message}", e);
                FailPending(requestID, pending, e);
            }

            return tcs.Task;
        }

        /// <summary>
        /// 以指定异常结束一个未决请求并清理配对项与定时器（竞争安全：已被回包/超时处理则跳过）
        /// </summary>
        /// <param name="requestID">请求 ID</param>
        /// <param name="pending">配对项</param>
        /// <param name="error">失败原因异常</param>
        private void FailPending(uint requestID, PendingCall pending, Exception error)
        {
            if (!_pendingCalls.TryRemove(requestID, out var removed) || !ReferenceEquals(removed, pending))
                return;

            DisposePendingTimer(pending);
            if (requestID == _loginRequestId)
                _loginRequestId = 0;
            pending.Fail?.Invoke(error);
        }

        /// <summary>
        /// 每帧调用：驱动双通道收发并按序处理收到的客户端帧
        /// </summary>
        public void Tick()
        {
            _reliable?.Tick();
            _udp?.Tick();

            while (_reliable != null && _reliable.TryTakePacket(out var data))
                DrainFrame(data);

            while (_udp != null && _udp.TryTakePacket(out var udpData))
                DrainFrame(udpData);
        }

        /// <summary>
        /// 解析并处理一条客户端帧，四段优先级：
        /// 0) 通道解密（已协商 AES-256-GCM 时；登录回包本身是明文，见 <see cref="EncryptOutbound"/>）；
        /// 1) 引擎内部帧 EMsg.UDPBindGrant / EMsg.QueuePosition（最高优先，不做回包配对）；
        /// 2) 回包配对（requestID != 0 且在配对表中，含 EMsg.Error 错误回包）；
        /// 3) 推送/未配对帧 → 路由分发。
        /// internal 供编辑器单元测试直接注入帧，验证拦截顺序与配对逻辑。
        /// </summary>
        /// <param name="data">完整客户端帧（8B 帧头 + body）；已协商加密时为密文</param>
        internal void DrainFrame(byte[] data)
        {
            // 0) 通道解密（已协商加密时）：登录回包**自身是明文**（客户端要靠它拿到 session_key），
            //    之后所有下行帧都是密文。故按「握手窗口」处理，与服务端 gwcore.forwardFrame 镜像：
            //      - 尚未确认握手：解密失败 = 这条是明文（登录回包）→ 按原样继续；
            //      - 已确认握手：解密失败 = 密钥不符 / 密文被篡改 → 丢弃并告警，不处理脏帧。
            if (_channelKey != null)
            {
                if (SessionCrypto.TryDecrypt(_channelKey, data, out var plain))
                {
                    data = plain;
                    _channelHandshakeOk = true;
                }
                else if (_channelHandshakeOk)
                {
                    Game.Logger?.Warn("Network", "channel decrypt failed — frame dropped");
                    return;
                }
            }

            if (!ClientFrame.TryDecode(data, out var requestID, out var msgID, out var bodyOffset))
            {
                Game.Logger?.Warn("Network", $"discard short frame, len={data?.Length ?? 0}");
                return;
            }

            var bodyLength = data.Length - bodyOffset;

            // 1) UDP 绑定令牌下发：登录成功后仅走可靠通道；体为令牌 UTF-8 原文（非 JSON）
            if (msgID == EMsg.UDPBindGrant)
            {
                _udpBindToken = Encoding.UTF8.GetString(data, bodyOffset, bodyLength);
                Game.Logger?.Info("Network", "UDP bind token granted");
                EnsureUdpChannel();
                return;
            }

            // 1.5) 排队位置通知（网关直发，requestID=0）：
            //     必须在回包配对之前拦下——排队中的连接尚未建立会话，本帧既不参与配对（无 requestID），
            //     也不该走业务路由（业务只订阅 Net.QueuePosition 事件与 IsQueued/QueueAhead 状态）。
            if (msgID == EMsg.QueuePosition)
            {
                // JsonUtility 对非法 JSON 是**抛异常**而不是返回 null：不包 try/catch 会让异常
                // 穿出 DrainFrame → Game.Tick，主循环每帧抛一次。
                EQueuePositionNotify pos = null;
                if (bodyLength > 0)
                {
                    try
                    {
                        pos = Serializer.Deserialize<EQueuePositionNotify>(data, bodyOffset, bodyLength);
                    }
                    catch (Exception e)
                    {
                        Game.Logger?.Error("Network", $"queue position payload parse failed: {e.Message}", e);
                    }
                }
                if (pos == null)
                    return;

                _queueAhead = pos.ahead;
                _queueTotal = pos.total;
                _queueTicket = pos.ticket;
                Game.Logger?.Info("Network",
                    $"queued: {pos.ahead} ahead (total={pos.total}, ticket={pos.ticket})");
                // 排队期间不判超时：位置每刷新一次就顺延未决请求的超时（理由见 ExtendPendingDeadlines）。
                ExtendPendingDeadlines();
                Game.Event?.Emit(CloverEvents.Net.QueuePosition, pos);
                return;
            }

            // 2) 回包配对：requestID != 0 且在配对表中（正常回包 msgID 恒为 0，错误回包为 EMsg.Error）
            if (requestID != 0 && _pendingCalls.TryRemove(requestID, out var pending))
            {
                // 能收到回包 ⇒ 已被服务端放行（排队中后续帧被丢弃、不会有回包）：清掉排队态，
                // 否则业务的「排队中」界面会一直挂着（表现为卡在等待页进不去游戏）。
                ClearQueueState();

                // 通道加密协商：回包携带 session_key 时启用整帧加解密（登录回包本身是明文）。
                // 只在「尚未启用」或「本次就是登录回包」时才解析 body —— 否则每条回包都要
                // MiniJson.Parse 整段 body 找 session_key，是热路径上的纯浪费（握手完成后不会再有密钥）。
                if (_channelKey == null || requestID == _loginRequestId)
                {
                    TryEnableChannelCrypto(data, bodyOffset, bodyLength);
                    if (requestID == _loginRequestId)
                        _loginRequestId = 0;
                }

                DisposePendingTimer(pending);
                RecordRtt(pending.StartTicks);

                if (msgID == EMsg.Error)
                {
                    // 统一错误回包：body 为 EErrorReply{err, code}，记录后以异常结束请求。
                    // 来源两处：逻辑服 handler 返回 error，或网关登录门禁拒绝未登录连接。
                    // 反序列化同样必须包 try/catch：非法 body 抛异常会穿出主循环（同排队位置帧）。
                    EErrorReply errReply = null;
                    if (bodyLength > 0)
                    {
                        try
                        {
                            errReply = Serializer.Deserialize<EErrorReply>(data, bodyOffset, bodyLength);
                        }
                        catch (Exception e)
                        {
                            Game.Logger?.Error("Network",
                                $"error reply payload parse failed (requestID={requestID}): {e.Message}", e);
                        }
                    }
                    _lastErrorReply = errReply;
                    _lastErrorRequestID = requestID;
                    var serverError = errReply?.err ?? $"request {requestID} rejected";
                    var code = errReply?.code ?? 0;
                    Game.Logger?.Warn("Network", $"request {requestID} rejected: {serverError} (code={code})");
                    // 未认证（401）：发布统一事件，业务据此回到登录流程（弹登录框 / 清会话）。
                    // 用事件而非直接跳转，是因为「如何重登」属业务决策；触发来源有两处，
                    // 而客户端无需区分——网关门禁直接拒绝，或逻辑服返回 401 业务错误。
                    if (code == ErrCode.Unauthenticated)
                    {
                        Game.Event?.Emit(CloverEvents.Net.OnUnauthorized,
                            errReply ?? new EErrorReply { err = serverError, code = code });
                    }
                    pending.Fail?.Invoke(new CloverCallException(requestID, msgID, serverError, code));
                    return;
                }

                // 正常回包：空体时结果为 null，有体时经闭包反序列化（解析失败按异常结束）
                object result = null;
                if (bodyLength > 0 && pending.Deserialize != null)
                {
                    try
                    {
                        result = pending.Deserialize(data, bodyOffset, bodyLength);
                    }
                    catch (Exception e)
                    {
                        Game.Logger?.Error("Network", $"reply deserialize failed (requestID={requestID}): {e.Message}");
                        pending.Fail?.Invoke(e);
                        return;
                    }
                }

                pending.Complete?.Invoke(result);
                return;
            }

            // 3) 推送（requestID == 0）或未配对帧 → 路由分发
            _router.Dispatch(msgID, requestID, null, data, bodyOffset, bodyLength);
        }

        /// <summary>
        /// 连接建立回调（已在主线程）：清掉退避定时器、发布 Net.OnConnected；
        /// 处于恢复会话态且凭证齐备时自动提交 EMsg.ResumeSession。
        ///
        /// 注：**不重置重连计数**（<see cref="_reconnectCount"/>）—— 计数只在会话恢复成功后
        /// 清零（见 <see cref="TryResumeSession"/>），连上即清零会让"连上→立刻断"的环路
        /// 永远耗尽不了重连次数，表现为无限重连。本方法也不重置退避计数以外的线路状态。
        /// </summary>
        private void HandleConnected()
        {
            _everConnected = true;
            _anyLineConnected = true;
            DisposeReconnectTimer();

            Game.Logger?.Info("Network", $"reliable link connected ({ActiveLine})");
            _sendDropWarned = false; // 新连接周期：允许再提示一次上行被丢弃的原因
            Game.Event?.Emit(CloverEvents.Net.OnConnected);

            if (_session.IsResuming && !string.IsNullOrEmpty(_session.Info.Token))
                TryResumeSession();
        }

        /// <summary>
        /// 提交会话恢复请求（EMsg.ResumeSession）：成功发布 Net.OnResumed（参数为回包结构），
        /// 失败发布 Net.OnResumeFailed（参数为失败原因字符串）。
        /// 失败不自动清除会话：服务端会踢除连接，走断线重连流程直至次数耗尽发布 Net.OnKicked。
        /// </summary>
        private void TryResumeSession()
        {
            var info = _session.Info;
            var continuation = Call<EResumeSessionReply>(EMsg.ResumeSession, new EResumeSessionRequest
            {
                player_id = info.PlayerID ?? string.Empty,
                session_token = info.Token ?? string.Empty,
            }).ContinueWith(t =>
            {
                // ContinueWith 在线程池执行，结果处理切回主线程
                PostOrRun(() =>
                {
                    if (t.Status == TaskStatus.RanToCompletion && t.Result != null && t.Result.success)
                    {
                        Game.Logger?.Info("Network", $"session resumed (player={t.Result.player_id})");
                        // 修复无限重连bug：ResumeSession成功后重置重连计数
                        _reconnectCount = 0;
                        Game.Event?.Emit(CloverEvents.Net.OnResumed, t.Result);
                    }
                    else
                    {
                        string reason;
                        if (t.Status == TaskStatus.RanToCompletion)
                            reason = t.Result?.err ?? "resume rejected";
                        else if (t.Exception?.InnerException is CloverCallException ce)
                            reason = ce.ServerError;
                        else
                            reason = t.Exception?.InnerException?.Message ?? "resume failed";

                        Game.Logger?.Warn("Network", $"session resume failed: {reason}");
                        Game.Event?.Emit(CloverEvents.Net.OnResumeFailed, reason);
                    }
                });
            });

            // 观察续延任务：无派发器时上面的回调内联执行，其中抛出的异常若无人观察会变成
            // 未观察 Task 异常（默认吞掉），排障时表现为"恢复会话静默无结果"。
            ObserveTask(continuation, "session resume continuation");
        }

        /// <summary>
        /// 观察一个"发后不理"的任务：只在失败时记一条 Error，避免未观察异常被静默吞掉。
        /// </summary>
        /// <param name="task">被丢弃的任务</param>
        /// <param name="what">用途描述（进日志）</param>
        internal static void ObserveTask(Task task, string what)
        {
            if (task == null) return;
            task.ContinueWith(t =>
            {
                var ex = t.Exception?.InnerException ?? t.Exception;
                if (ex != null)
                    Game.Logger?.Error("Network", $"{what} faulted: {ex.Message}", ex);
            }, TaskContinuationOptions.OnlyOnFaulted);
        }

        /// <summary>
        /// 连接断开回调（已在主线程）：使全部未决请求失败、拆除 UDP 通道，发布 Net.OnDisconnected。
        /// 随后按优先级处理：
        ///   1) 尚未连上过任何线路且降级链还有下一条 → 立刻换线重试（不退避、不计次）；
        ///   2) 处于恢复会话态且凭证齐备 → 按指数退避（2^n 秒，封顶 60 秒）自动重连；
        ///   3) 其余 → 发布 Net.OnKicked（曾连上过）或 Net.OnConnectFailed（从未连上）。
        /// </summary>
        private void HandleDisconnected()
        {
            FailAllPending("连接断开");
            TeardownUdpChannel();
            // 断线后排队态作废：重连是新连接、新排队（位置会重新下发），旧位置留着会误导业务界面。
            ClearQueueState();
            // 通道加密同样作废：新连接的密钥由新的登录回包重新协商（断线即视为会话结束）。
            ClearChannelCrypto();

            Game.Logger?.Warn("Network", $"reliable link lost ({ActiveLine})");

            // 只在「确实连上过」时发 Net.OnDisconnected：冷启动首条线从未连上就失败时，
            // 业务会先收到一条"断开"，而此时降级/退避决策还没做 —— 在该回调里 Connect 会与
            // 内置降级并发。未连上过的失败统一由 OnConnectFailed / OnKicked 表达。
            if (_everConnected)
                Game.Event?.Emit(CloverEvents.Net.OnDisconnected);
            else
                Game.Logger?.Info("Network",
                    "not connected yet — Net.OnDisconnected suppressed (falling back / giving up below)");

            // 降级链：只要还没成功连上过任何线路，就立即换下一条线路重试。
            // 这样「TCP 口没开 / 连不上」会平滑退到 WebSocket，而不是让玩家等满重连次数。
            if (!_anyLineConnected && _options != null && _lineIndex + 1 < _options.ReliableOrder.Count)
            {
                // QUIC 失败就记住（本进程内不再排它）：UDP 上的失败只是"没响应"，
                // 不记住的话每次重连都要再白等一次连接超时，桌面端体验与"没接 QUIC"不一致。
                if (_options.ReliableOrder[_lineIndex].Kind == TransportKind.Quic)
                    TransportCapabilities.MarkQuicUnavailableForSession("connect or handshake failed");

                _lineIndex++;
                Game.Logger?.Info("Network", $"falling back to next line: {ActiveLine}");
                ConnectCurrentLine();
                return;
            }

            // 恢复会话可用性判定：凭证齐备（player_id 缺失时恢复必败，直接按不可用处理）
            var tokenReady = _session.IsResuming
                && !string.IsNullOrEmpty(_session.Info.Token)
                && !string.IsNullOrEmpty(_session.Info.PlayerID);

            if (tokenReady && _reconnectCount < _maxReconnectCount && !string.IsNullOrEmpty(_serverAddr))
            {
                _reconnectCount++;
                var delaySeconds = Math.Min(Math.Pow(2, _reconnectCount), MaxReconnectDelaySeconds);
                Game.Logger?.Info("Network", $"reconnecting in {delaySeconds:0}s (attempt {_reconnectCount}/{_maxReconnectCount})");
                ScheduleReconnect(delaySeconds);
            }
            else
            {
                Game.Event?.Emit(_everConnected ? CloverEvents.Net.OnKicked : CloverEvents.Net.OnConnectFailed);
            }
        }

        /// <summary>
        /// 调度延迟自动重连：一次性定时器到期后在主线程复验
        /// （会话仍处于恢复态、链路仍断开、地址有效）后才发起连接，避免过期重连
        /// </summary>
        /// <param name="delaySeconds">延迟秒数（指数退避）</param>
        private void ScheduleReconnect(double delaySeconds)
        {
            DisposeReconnectTimer();
            _reconnectTimer = new Timer(_ =>
            {
                PostOrRun(() =>
                {
                    // 到期复验：期间可能已手动断开/重连/清除会话。
                    // 每条放弃路径都必须留日志——静默 return 在排障时表现为"重连凭空消失"。
                    if (!_session.IsResuming || string.IsNullOrEmpty(_session.Info.Token))
                    {
                        Game.Logger?.Warn("Network",
                            "scheduled reconnect cancelled: session no longer resumable (已清除会话/凭证为空)");
                        return;
                    }
                    if (string.IsNullOrEmpty(_serverAddr))
                    {
                        Game.Logger?.Warn("Network", "scheduled reconnect cancelled: no server address recorded");
                        return;
                    }
                    if (_options == null || _options.ReliableOrder.Count == 0)
                    {
                        Game.Logger?.Warn("Network", "scheduled reconnect cancelled: no line plan recorded");
                        return;
                    }
                    if (_reliable != null && _reliable.State != ConnectionState.Disconnected)
                    {
                        Game.Logger?.Info("Network",
                            $"scheduled reconnect skipped: link already in state {_reliable.State}");
                        return;
                    }

                    ConnectCurrentLine();
                });
            }, null, Math.Max(1, (long)(delaySeconds * 1000)), Timeout.Infinite);
        }

        /// <summary>
        /// 释放重连延迟定时器（无定时器时无副作用）
        /// </summary>
        private void DisposeReconnectTimer()
        {
            _reconnectTimer?.Dispose();
            _reconnectTimer = null;
        }

        /// <summary>
        /// 建立常驻裸 UDP 通道并用令牌完成绑定，启动周期保活。
        /// 未配置 UDP 端点或已绑定时忽略；建通道异常仅告警不回滚（令牌保留下次复用）。
        /// </summary>
        private void EnsureUdpChannel()
        {
            if (string.IsNullOrEmpty(_udpAddr) || IsUdpBound)
                return;

            try
            {
                _udp = new UdpConnection();
                _udp.Connect(_udpAddr);
            }
            catch (Exception e)
            {
                Game.Logger?.Error("Network", $"udp channel setup failed: {e.Message}");
                return;
            }

            _udpBound = true;
            SendUdpBindFrame();
            StartUdpKeepalive();

            Game.Logger?.Info("Network", $"UDP channel bound to {_udpAddr}");
        }

        /// <summary>
        /// 经裸 UDP 通道上报绑定帧（EMsg.BindUDP，体为令牌 UTF-8 原文，requestID 恒为 0）。
        /// 服务端凭令牌校验并登记本端点，重复发送按幂等处理。
        /// </summary>
        private void SendUdpBindFrame()
        {
            if (_udp == null || _udp.State != ConnectionState.Connected || string.IsNullOrEmpty(_udpBindToken))
                return;

            // 本帧**刻意不加密**（即便会话已启用通道加密）：网关在 onUDPFrame 里按最外层协议
            // 直接读原文令牌（先于任何解密），把密文当令牌会校验失败、UDP 通道永远绑不上。
            var body = Encoding.UTF8.GetBytes(_udpBindToken);
            var frame = ClientFrame.Encode(0, EMsg.BindUDP, body);
            _udp.Send(frame, 0, frame.Length);
        }

        /// <summary>
        /// 启动裸 UDP 保活定时器：周期重发绑定帧维持服务端五元组映射（NAT 保活）
        /// </summary>
        private void StartUdpKeepalive()
        {
            StopUdpKeepalive();
            var intervalMs = Math.Max(1000, _udpKeepaliveIntervalMs);
            _udpKeepaliveTimer = new Timer(_ => PostOrRun(SendUdpBindFrame), null, intervalMs, intervalMs);
        }

        /// <summary>
        /// 停止裸 UDP 保活定时器（无定时器时无副作用）
        /// </summary>
        private void StopUdpKeepalive()
        {
            _udpKeepaliveTimer?.Dispose();
            _udpKeepaliveTimer = null;
        }

        /// <summary>
        /// 拆除裸 UDP 通道：停止保活、关闭 socket、清空绑定状态（断线与主动断开共用）
        /// </summary>
        private void TeardownUdpChannel()
        {
            StopUdpKeepalive();
            _udp?.Disconnect();
            _udp = null;
            _udpBound = false;
            _udpBindToken = null;
        }

        /// <summary>
        /// 使全部未决请求以指定原因失败（断线/主动断开场景）：
        /// 逐项 TryRemove 竞争安全，失败说明已被回包/超时路径处理
        /// </summary>
        /// <param name="reason">失败原因描述</param>
        private void FailAllPending(string reason)
        {
            foreach (var kv in _pendingCalls)
            {
                if (!_pendingCalls.TryRemove(kv.Key, out var pending))
                    continue;

                DisposePendingTimer(pending);
                try
                {
                    pending.Fail?.Invoke(new InvalidOperationException(reason));
                }
                catch (Exception e)
                {
                    Game.Logger?.Error("Network", $"pending fail callback error: {e.Message}");
                }
            }
        }

        /// <summary>
        /// 释放配对项持有的超时定时器并置空（定时器回调内调用自身 Dispose 是安全操作）
        /// </summary>
        /// <param name="pending">目标配对项</param>
        private static void DisposePendingTimer(PendingCall pending)
        {
            pending.TimeoutTimer?.Dispose();
            pending.TimeoutTimer = null;
        }

        /// <summary>
        /// 记录一次请求-回包往返耗时：更新最近值、指数移动平均（0.9/0.1）与历史最大值。
        /// 仅在主线程 Tick → DrainFrame 路径调用，无需加锁。
        /// </summary>
        /// <param name="startTicks">请求发出时的 Stopwatch 时间戳</param>
        private void RecordRtt(long startTicks)
        {
            var ms = (Stopwatch.GetTimestamp() - startTicks) * 1000.0 / Stopwatch.Frequency;
            _rttLastMs = ms;
            _rttAvgMs = _rttAvgMs <= 0 ? ms : _rttAvgMs * RttEmaAlpha + ms * (1 - RttEmaAlpha);
            if (ms > _rttMaxMs)
                _rttMaxMs = ms;
        }

        /// <summary>
        /// 玩家全量同步推送处理器（EMsg.PushPlayerFullSync，登录成功后服务端主动下发）：
        /// 用 MiniJson 动态解析（map 字段 JsonUtility 无法表达），补齐会话缺失字段
        /// （player_id / account 仅空缺时补，session_token 非空覆盖），
        /// 随后发布 Net.PlayerFullSync（参数为解析后的字典）。
        /// 分线编号（SessionInfo.Line）由场景标识推送 EMsg.PushSceneInfo 填充，不在此处理。
        /// </summary>
        /// <param name="ctx">消息上下文</param>
        private void HandlePlayerFullSync(NetCtx ctx)
        {
            if (ctx.Body == null || ctx.BodyLength <= 0)
            {
                Game.Logger?.Warn("Network", "player full sync ignored: empty body");
                return;
            }

            Dictionary<string, object> root;
            try
            {
                root = MiniJson.Parse(ctx.Body, ctx.BodyOffset, ctx.BodyLength) as Dictionary<string, object>;
            }
            catch (Exception e)
            {
                Game.Logger?.Error("Network", $"player full sync parse failed: {e.Message}");
                return;
            }

            if (root == null)
            {
                Game.Logger?.Error("Network", "player full sync ignored: payload is not an object");
                return;
            }

            var info = _session.Info;

            if (root.TryGetValue("player_id", out var pidObj)
                && pidObj is string pid && !string.IsNullOrEmpty(pid)
                && string.IsNullOrEmpty(info.PlayerID))
            {
                info.PlayerID = pid;
            }

            if (root.TryGetValue("account", out var accObj)
                && accObj is string acc && !string.IsNullOrEmpty(acc)
                && string.IsNullOrEmpty(info.Account))
            {
                info.Account = acc;
            }

            if (root.TryGetValue("session_token", out var tokObj)
                && tokObj is string token && !string.IsNullOrEmpty(token))
            {
                info.Token = token;
            }

            Game.Logger?.Info("Network", $"player full sync received (player={info.PlayerID})");
            Game.Event?.Emit(CloverEvents.Net.PlayerFullSync, root);
        }

        /// <summary>
        /// 主线程切换：有派发器时投递到主线程队列（Game.Tick 期间 Flush 执行），
        /// 无派发器（编辑器测试环境）时内联同步执行
        /// </summary>
        /// <param name="action">待执行动作</param>
        private void PostOrRun(Action action)
        {
            var dispatcher = Game.Dispatcher;
            if (dispatcher == null)
            {
                // 无派发器（编辑器测试环境）时内联同步执行
                action();
                return;
            }

            if (!Game.IsRunning)
            {
                // 派发器存在但引擎已停（Shutdown 之后才送达的传输回调）：队列不会再被排空，
                // 投进去等于把回调丢掉。这里改内联执行并留日志，至少不静默。
                Game.Logger?.Warn("Network", "engine not running — dispatcher queue is dead, callback executed inline");
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    Game.Logger?.Error("Network", $"inline callback error: {e.Message}", e);
                }
                return;
            }

            dispatcher.Post(action);
        }

        /// <summary>
        /// 当前未配对的进行中请求数量（调试器展示用）
        /// </summary>
        internal int PendingCallCount => _pendingCalls.Count;

        /// <summary>
        /// 最近收到的错误回包（调试器展示用，无记录时为 null）
        /// </summary>
        internal EErrorReply LastErrorReply => _lastErrorReply;

        /// <summary>
        /// 最近收到的错误回包对应的请求 ID（调试器展示用）
        /// </summary>
        internal uint LastErrorRequestID => _lastErrorRequestID;

        /// <summary>
        /// 最近一次回包往返耗时（毫秒，调试器展示用）
        /// </summary>
        internal double RttLastMs => _rttLastMs;

        /// <summary>
        /// 回包往返耗时指数移动平均（毫秒，调试器展示用）
        /// </summary>
        internal double RttAvgMs => _rttAvgMs;

        /// <summary>
        /// 历史最大回包往返耗时（毫秒，调试器展示用）
        /// </summary>
        internal double RttMaxMs => _rttMaxMs;

        /// <summary>
        /// 当前记录的 TCP 服务器地址（调试器展示用，未连接过时为 null）
        /// </summary>
        internal string ServerAddr => _serverAddr;

        /// <summary>
        /// 当前配置的裸 UDP 共享端点（调试器展示用，未启用时为 null）
        /// </summary>
        internal string UdpEndpoint => _udpAddr;

        /// <summary>
        /// 会话是否处于恢复态（调试器展示用）
        /// </summary>
        internal bool SessionResuming => _session.IsResuming;
    }
}
