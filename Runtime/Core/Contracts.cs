using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace CloverEngine
{
    // ============================================================================
    // 引擎跨模块契约层
    //
    // 依赖规则（见 结构规则.md 的 asmdef 依赖方向审查清单）：
    //   Network / Data / Resource / Presentation → Core 单向依赖，禁止反向引用。
    //
    // 因此全部对外契约（接口 + 接口签名用到的辅助类型）统一定义于本文件（Core 程序集），
    // 各模块程序集只保留实现类；模块实例化通过各模块引导类完成：
    //   网络/HTTP/世界同步 → CloverNet（Network 程序集）
    //   数据表/本地化     → CloverData（Data 程序集）
    //   资源             → CloverRes（Resource 程序集）
    // ============================================================================

    /// <summary>
    /// 会话信息，存储当前网络会话的状态数据。
    /// 安全约定：session_token（会话密钥）仅驻留内存，禁止持久化到磁盘或日志。
    /// </summary>
    public class SessionInfo
    {
        /// <summary>
        /// 账号标识
        /// </summary>
        public string Account;

        /// <summary>
        /// 玩家唯一 ID。
        /// 线协议中 player_id 为字符串（服务端 Go 结构定义为 string），此处保持同类型。
        /// </summary>
        public string PlayerID;

        /// <summary>
        /// 会话密钥（登录回包 session_key / 全量同步推送 session_token），
        /// 断线重连时随 EMsg.ResumeSession 提交服务端校验。
        /// </summary>
        public string Token;

        /// <summary>
        /// 服务器线路编号
        /// </summary>
        public int Line;

        private int _nextId;

        /// <summary>
        /// 分配一个唯一的请求 ID，线程安全。
        ///
        /// 跳过 0：协议里 requestID == 0 专门表示「推送 / 不可靠消息」，不能拿它去配对回包。
        /// <see cref="_nextId"/> 是 int，递增到 <see cref="int.MaxValue"/> 后回绕为负、
        /// 强转 uint 再过一遍 0——虽然要 2^32 次分配才轮到，但一旦命中就是「请求被当推送」的静默错配。
        /// </summary>
        /// <returns>新分配的请求 ID（保证 != 0）</returns>
        public uint AllocateRequestID()
        {
            var id = (uint)Interlocked.Increment(ref _nextId);
            if (id == 0)
                id = (uint)Interlocked.Increment(ref _nextId);
            return id;
        }

        /// <summary>
        /// 重置所有会话数据
        /// </summary>
        public void Reset()
        {
            Account = null;
            PlayerID = null;
            Token = null;
            Line = 0;
            _nextId = 0;
        }
    }

    /// <summary>
    /// 网络消息上下文，包含消息的元数据和正文数据
    /// </summary>
    public class NetCtx
    {
        /// <summary>
        /// 消息 ID
        /// </summary>
        public uint MsgID;

        /// <summary>
        /// 请求 ID，用于匹配请求-响应对
        /// </summary>
        public uint RequestID;

        /// <summary>
        /// 追踪 ID，用于链路追踪
        /// </summary>
        public string TraceID;

        /// <summary>
        /// 消息正文原始数据
        /// </summary>
        public byte[] Body;

        /// <summary>
        /// 正文数据起始偏移量
        /// </summary>
        public int BodyOffset;

        /// <summary>
        /// 正文数据长度
        /// </summary>
        public int BodyLength;

        /// <summary>
        /// 将正文数据反序列化为指定类型
        /// </summary>
        /// <typeparam name="T">目标类型</typeparam>
        /// <returns>反序列化后的对象；参数非法（正文缺失 / 偏移或长度越界）时返回 null 并留 Warn</returns>
        public T Bind<T>() where T : class
        {
            // 参数校验（兜底）：Body 为空 / offset、length 越界时不做「裸抛 ArgumentException」——
            // 返回 null 并留一条 Warn（调用方按「反序列化失败」处理；异常裸抛会打断消息分发）。
            if (Body == null || BodyOffset < 0 || BodyLength < 0 || BodyOffset > Body.Length - BodyLength)
            {
                Game.Logger?.Warn("Network",
                    $"Bind<{typeof(T).Name}> 参数非法：body={(Body == null ? "<null>" : Body.Length.ToString())} " +
                    $"offset={BodyOffset} length={BodyLength} msgID={MsgID} requestID={RequestID}");
                return null;
            }

            return Serializer.Deserialize<T>(Body, BodyOffset, BodyLength);
        }
    }

    /// <summary>
    /// 序列化工具类，提供基于 JSON 的对象序列化和反序列化功能。
    /// 协议体字段名即 JSON 键（snake_case 对齐服务端 Go json tag）。
    /// </summary>
    public static class Serializer
    {
        /// <summary>
        /// 将字节流反序列化为指定类型的对象
        /// </summary>
        /// <typeparam name="T">目标类型</typeparam>
        /// <param name="data">原始字节数据（UTF-8 编码的 JSON）</param>
        /// <param name="offset">数据起始偏移量</param>
        /// <param name="length">数据长度</param>
        /// <returns>反序列化后的对象</returns>
        public static T Deserialize<T>(byte[] data, int offset, int length) where T : class
        {
            var json = Encoding.UTF8.GetString(data, offset, length);
            return JsonUtility.FromJson<T>(json);
        }

        /// <summary>
        /// 将对象序列化为字节数组
        /// </summary>
        /// <typeparam name="T">对象类型</typeparam>
        /// <param name="obj">待序列化的对象</param>
        /// <returns>序列化后的字节数组（UTF-8 编码的 JSON）</returns>
        public static byte[] Serialize<T>(T obj) where T : class
        {
            var json = JsonUtility.ToJson(obj);
            return Encoding.UTF8.GetBytes(json);
        }
    }

    /// <summary>
    /// 消息处理器委托，用于处理网络消息
    /// </summary>
    /// <param name="ctx">消息上下文</param>
    public delegate void MsgHandler(NetCtx ctx);

    /// <summary>
    /// 消息路由接口：注册 / 注销 / 分发消息处理器。
    /// 命名与 INetwork、服务端 g.OnMsg 完全统一（OnMsg / OffMsg），由 NetworkManager 实现。
    /// 业务侧只经 <see cref="Game.OnMsg"/> / <see cref="Game.OffMsg"/> 访问，不直接持有本接口。
    /// </summary>
    public interface IRouter
    {
        /// <summary>
        /// 注册指定消息 ID 的处理器（同一消息可注册多个，按注册顺序调用）
        /// </summary>
        /// <param name="msgID">消息 ID</param>
        /// <param name="handler">消息处理器</param>
        void OnMsg(uint msgID, MsgHandler handler);

        /// <summary>
        /// 注销指定消息 ID 的所有处理器
        /// </summary>
        /// <param name="msgID">消息 ID</param>
        void OffMsg(uint msgID);

        /// <summary>
        /// 注销指定消息 ID 的特定处理器
        /// </summary>
        /// <param name="msgID">消息 ID</param>
        /// <param name="handler">要移除的处理器</param>
        void OffMsg(uint msgID, MsgHandler handler);

        /// <summary>
        /// 分发指定消息给所有已注册的处理器
        /// </summary>
        /// <param name="msgID">消息 ID</param>
        /// <param name="requestID">请求 ID</param>
        /// <param name="traceID">追踪 ID</param>
        /// <param name="body">消息正文原始数据</param>
        /// <param name="offset">正文数据起始偏移量</param>
        /// <param name="length">正文数据长度</param>
        void Dispatch(uint msgID, uint requestID, string traceID, byte[] body, int offset, int length);
    }

    /// <summary>
    /// 网络管理接口：连接管理 + 消息发送 + 请求-响应配对。
    /// **不含消息路由**（注册/注销处理器）——路由能力由 <see cref="IRouter"/> 承载，
    /// 业务侧统一经 <see cref="Game.OnMsg"/> / <see cref="Game.OffMsg"/> 使用，
    /// 避免出现「Game.OnMsg 与 Game.Net.OnMsg 两个入口」的重复。
    /// 线协议约定：回包 msgID 恒为 0（按 requestID 配对），错误回包 msgID 为 EMsg.Error，
    /// 推送 requestID 为 0，不可靠消息 requestID 固定为 0。
    /// 事件（经 Game.Event 发布，均在主线程回调）：
    ///   Net.OnConnected / Net.OnDisconnected      连接建立 / 断开
    ///   Net.OnConnectFailed                        首次连接即失败（从未连上过）
    ///   Net.OnKicked                               断线且会话恢复不可用（需重新登录）
    ///   Net.OnResumed / Net.OnResumeFailed        会话恢复成功 / 失败
    ///   Net.OnUnauthorized                         某请求被拒为「未认证」(code=401)：网关登录门禁
    ///                                              直接拒绝，或逻辑服返回 401。参数为 EErrorReply；
    ///                                              业务应据此回到登录流程
    ///   Net.PlayerFullSync                         收到玩家全量同步推送（参数为 MiniJson 解析字典）
    ///   Net.Alert                                  收到公告推送（参数为 EAlertNotify）
    ///   Net.SceneChanged                           服务端场景标识变更（参数为 ICloverScene）
    ///   Net.QueuePosition                          收到排队位置通知（参数为 EQueuePositionNotify）：
    ///                                              服务器限流/满载时连入等候队列，业务据此显示
    ///                                              「您前面还有 N 人」；也可用 IsQueued / QueueAhead 读取
    ///
    /// ★ 这些事件名**不要**手写字符串：一律用 <see cref="CloverEvents.Net"/> 里的常量
    ///（引擎内部同样只用常量），理由见该类的注释。
    /// </summary>
    public interface INetwork
    {
        /// <summary>
        /// 可靠通道（TCP）是否已建立连接
        /// </summary>
        bool IsConnected { get; }

        /// <summary>
        /// 常驻裸 UDP 通道是否已绑定（登录后凭 EMsg.UDPBindGrant 令牌完成绑定）
        /// </summary>
        bool IsUdpBound { get; }

        /// <summary>
        /// 是否正处于服务端排队中（服务器限流/满载时连入等候队列）。
        /// 由 EMsg.QueuePosition 置位，收到任意回包或断线时清除。
        /// </summary>
        bool IsQueued { get; }

        /// <summary>
        /// 排队位置：前面还有多少人（0 = 队首，下一个就放行）。未排队时为 -1。
        /// </summary>
        int QueueAhead { get; }

        /// <summary>
        /// 排队位置：当前队列总人数（含自己）。未排队时为 0。
        /// </summary>
        int QueueTotal { get; }

        /// <summary>
        /// 本次连接是否已启用会话通道加密（AES-256-GCM）。
        /// 由登录时声明 ELoginRequest.encrypt 协商得到：平台不支持 AES-GCM（如 WebGL）时为 false，
        /// 此时链路安全由 TLS（wss / QUIC / WebTransport）承担。
        /// </summary>
        bool IsChannelEncrypted { get; }

        /// <summary>
        /// 当前会话信息（账号 / player_id / 会话密钥 / 线路）。
        /// 全量同步推送到达时会自动补齐缺失字段。
        /// </summary>
        SessionInfo Session { get; }

        /// <summary>
        /// 连接到指定地址（非阻塞，不卡主线程；不启用裸 UDP 通道，不可靠消息降级走 TCP）。
        /// 连接结果通过 IsConnected 与 Net.OnConnected / Net.OnConnectFailed 反馈。
        /// </summary>
        /// <param name="addr">目标地址，格式为 "host:port"</param>
        void Connect(string addr);

        /// <summary>
        /// 连接到指定地址（非阻塞），并配置共享 UDP 端点；登录成功后收到 EMsg.UDPBindGrant
        /// 将自动建立常驻 UDP 通道（承载不可靠消息）
        /// </summary>
        /// <param name="addr">TCP 目标地址，格式为 "host:port"</param>
        /// <param name="udpAddr">裸 UDP 共享端点，格式为 "host:port"，传 null 不启用</param>
        void Connect(string addr, string udpAddr);

        /// <summary>
        /// 立即重连：清除退避计数并用上次地址发起连接（手动恢复入口，调试器 GM 使用）
        /// </summary>
        void Reconnect();

        /// <summary>
        /// 断开连接并清除会话数据
        /// </summary>
        void Disconnect();

        /// <summary>
        /// 登记会话凭证（登录成功后调用）：进入恢复会话态，
        /// 断线后自动按指数退避重连并提交 EMsg.ResumeSession。
        /// <para>
        /// <b>恢复凭证参数应传空</b>：登录回包的 <c>session_key</c> 是**会话通道加密密钥**，
        /// 不是断线恢复凭证 —— 把它当恢复凭证上交，恢复会话必然 token mismatch 被踢。
        /// 恢复凭证由服务端随后经 <see cref="EMsg.PushPlayerFullSync"/> 下发的 <c>session_token</c>
        /// 覆盖到位（非空覆盖）；在此之前引擎**不会**提交恢复请求
        /// （提交条件是「会话处于恢复态且 token 非空」）。
        /// </para>
        /// </summary>
        /// <param name="account">账号标识</param>
        /// <param name="resumeCredential">断线恢复凭证；**传 null / 空串**（恢复凭证由全量同步推送覆盖）</param>
        /// <param name="line">服务器线路编号</param>
        void SetupSession(string account, string resumeCredential, int line = 0);

        /// <summary>
        /// 上行总闸（默认 true）。置 false 后 <see cref="Send"/> / <see cref="SendUnreliable"/> 直接丢弃
        /// 并**只告警一次**，<see cref="Call{T}"/> 立即以 InvalidOperationException 结束
        ///（不再干等超时）。
        /// <para>
        /// 用途：还没进图 / 断线重连中 / 已被判未认证(code=401) 期间，业务应关掉闸门 ——
        /// 否则 20Hz 上行会被网关按未鉴权拒绝并刷屏（实测一次断线刷出 3262 条），
        /// 既浪费带宽，又把真问题淹在噪音里。
        /// </para>
        /// <para>
        /// 边界：**连接级**不可发（未连接）引擎已自动拦截；这个开关表达的是
        /// 「连接没问题，但业务状态现在不允许发」（未进图 / 切场景中 / 已死亡等）。
        /// </para>
        /// </summary>
        bool SendEnabled { get; set; }

        /// <summary>
        /// 发送可靠消息（自增分配 requestID，走 TCP）
        /// </summary>
        /// <param name="msgID">消息 ID</param>
        /// <param name="msg">消息内容对象</param>
        void Send(uint msgID, object msg);

        /// <summary>
        /// 发送不可靠消息：requestID 固定为 0（无回包），已绑定裸 UDP 通道时走 UDP，否则降级 TCP
        /// </summary>
        /// <param name="msgID">消息 ID</param>
        /// <param name="msg">消息内容对象</param>
        void SendUnreliable(uint msgID, object msg);

        /// <summary>
        /// 发送请求并异步等待响应：按 requestID 配对回包；超时或收到
        /// EMsg.Error 错误回包时任务以异常结束
        /// </summary>
        /// <typeparam name="T">响应消息类型</typeparam>
        /// <param name="msgID">消息 ID</param>
        /// <param name="msg">请求内容对象</param>
        /// <returns>包含响应结果的异步任务</returns>
        Task<T> Call<T>(uint msgID, object msg) where T : class;

        /// <summary>
        /// 每帧调用，驱动连接收发队列并分发接收到的消息（回包配对优先于路由分发）
        /// </summary>
        void Tick();
    }

    /// <summary>
    /// 引擎发布的事件名常量（**唯一出处**）。
    ///
    /// 为什么必须有它：事件总线按**字符串**匹配，名字拼错**不会编译报错**，只会静默不触发；
    /// 更糟的是「撞名」—— 实测代价：业务自建事件蹭了引擎的 <c>Net.</c> 前缀，
    /// 与引擎的 <c>Net.SceneChanged</c> 撞在一起，表现为
    /// <c>Handler error [Net.SceneChanged]: CloverSceneManager cannot be converted to SceneChangedData</c>
    ///（两侧各自 Emit 同名事件，订阅方拿到对方的对象当自己的结构体解）。把字符串收成常量后，
    /// 引用处可被 IDE 索引、改名可编译期抓全，撞名一眼可见。
    ///
    /// 命名与事件名**同构**，读到 <see cref="Net.OnConnected"/> 就知道线上字符串是 <c>"Net.OnConnected"</c>：
    /// <list type="bullet">
    ///   <item><see cref="Net"/> = 引擎地盘（<c>Net.*</c>）：<b>业务不要占用这个前缀</b>；</item>
    ///   <item><see cref="Entity"/> = <c>Entity.*</c>（实体生命周期）；</item>
    ///   <item><see cref="App"/> = <c>Application.*</c>（Unity 应用级）。</item>
    /// </list>
    ///
    /// ★ 字符串**值不可改**（改了会断掉所有既有订阅与业务侧写死的监听），只能新增常量名。
    /// ★ 业务自己的事件请用自己的域前缀（<c>App.Xxx</c> / <c>Combat.Xxx</c> …），不要蹭上面这些名字。
    /// </summary>
    public static class CloverEvents
    {
        /// <summary>网络与会话生命周期事件（发布方：<c>NetworkManager</c> / <c>AlertManager</c> /
        /// <c>CloverSceneManager</c> / 局域网寻服的 <c>LanBrowser</c>）。
        /// 语义与参数类型见 <see cref="INetwork"/> / <see cref="ILanBrowser"/> 的注释。</summary>
        public static class Net
        {
            /// <summary>可靠连接已建立（无参数）。</summary>
            public const string OnConnected = "Net.OnConnected";

            /// <summary>连接断开（无参数）；处于恢复会话态时会自动重连。</summary>
            public const string OnDisconnected = "Net.OnDisconnected";

            /// <summary>从未连上过就失败（首连失败或地址非法，无参数）。</summary>
            public const string OnConnectFailed = "Net.OnConnectFailed";

            /// <summary>断开且会话恢复不可用（重连次数耗尽 / 无恢复凭证，无参数）——需重新登录。</summary>
            public const string OnKicked = "Net.OnKicked";

            /// <summary>会话恢复成功（参数：<c>EResumeSessionReply</c>）。</summary>
            public const string OnResumed = "Net.OnResumed";

            /// <summary>会话恢复被拒（参数：原因字符串）。</summary>
            public const string OnResumeFailed = "Net.OnResumeFailed";

            /// <summary>某请求被拒为「未认证」(code=401)（参数：<c>EErrorReply</c>）——业务应回到登录流程。</summary>
            public const string OnUnauthorized = "Net.OnUnauthorized";

            /// <summary>收到玩家全量同步推送（参数：MiniJson 解析后的字典）。</summary>
            public const string PlayerFullSync = "Net.PlayerFullSync";

            /// <summary>收到公告推送（参数：<c>EAlertNotify</c>）。</summary>
            public const string Alert = "Net.Alert";

            /// <summary>服务端场景标识变更（参数：<c>ICloverScene</c>）。</summary>
            public const string SceneChanged = "Net.SceneChanged";

            /// <summary>收到排队位置通知（参数：<c>EQueuePositionNotify</c>）；也可读 <c>IsQueued/QueueAhead</c>。</summary>
            public const string QueuePosition = "Net.QueuePosition";

            /// <summary>
            /// 发现一台局域网主机（参数：<see cref="LanHostInfo"/>，只读快照）。
            /// 发布方是局域网寻服的 <c>LanBrowser</c>（与 <see cref="ILanBrowser.OnHostFound"/> 同时触发，等价入口）；
            /// 同一台主机在本轮内只发布一次（重复应答只覆盖数据）。旁路能力，见 结构规则.md §五 N13。
            /// </summary>
            public const string LanHostFound = "Net.LanHostFound";

            /// <summary>
            /// 一轮局域网扫描结束（无参数）。
            /// 发布方是局域网寻服的 <c>LanBrowser</c>（与 <see cref="ILanBrowser.OnScanFinished"/> 同时触发）；
            /// 任何提前结束路径也一定发布一次，调用方不会卡在「扫描中」。
            /// </summary>
            public const string LanScanFinished = "Net.LanScanFinished";
        }

        /// <summary>实体生命周期事件（发布方：<c>EntityManager</c>，程序集 <c>CloverEngine.Core</c>）。</summary>
        public static class Entity
        {
            /// <summary>实体已创建（参数：<c>EntityInfo</c>）。</summary>
            public const string Created = "Entity.Created";

            /// <summary>实体已销毁（参数：<c>EntityInfo</c>）。</summary>
            public const string Destroyed = "Entity.Destroyed";

            /// <summary>实体与视图绑定完成（参数：<c>EntityInfo</c>）。</summary>
            public const string ViewBound = "Entity.ViewBound";
        }

        /// <summary>Unity 应用级事件（发布方：<c>EngineRunner</c>）。</summary>
        public static class App
        {
            /// <summary>应用切到后台（无参数）。</summary>
            public const string Pause = "Application.Pause";

            /// <summary>应用回到前台（无参数）。</summary>
            public const string Resume = "Application.Resume";
        }
    }

    /// <summary>
    /// 世界同步接口，用于订阅服务器下发的世界数据推送与实体事件。
    /// 推送来源：EMsg.PushPlayerFullSync（登录后玩家全量数据）与 EMsg.PushDataSync（数据增量，**服务端恒以批量形态下发**）。
    /// 动态 map 字段 JsonUtility 无法表达，统一经 MiniJson 解析为动态字典后回调。
    /// </summary>
    public interface IWorldSync
    {
        /// <summary>
        /// 每帧调用，驱动实体位置插值：把服务端**离散下发**的坐标平滑推进到当前帧。
        /// 由 <c>Game.Tick</c> 统一驱动，业务无需自行调用；读取结果用
        /// <see cref="TryGetPosition"/>。
        /// </summary>
        /// <param name="dt">帧间隔时间（秒）；&lt;=0 时不推进</param>
        void Tick(float dt);

        /// <summary>
        /// 读取实体**插值后**的当前位置（由 <see cref="Tick"/> 每帧平滑推进）。
        ///
        /// 与 <see cref="OnEntityMove"/> 的分工：
        /// <list type="bullet">
        /// <item><see cref="OnEntityMove"/> 给的是服务端下发的**原始目标坐标**（离散，约 10Hz），
        /// 适合做逻辑判定（追及、命中、AOI 距离）；</item>
        /// <item>本方法给的是**平滑后**的当前位置，适合驱动表现（把实体 Transform 贴上去，
        /// 避免 10Hz 推送造成的瞬移抖动）。</item>
        /// </list>
        ///
        /// 典型用法：实体视图在自己 <c>Update</c> 里轮询一次即可，无需再订阅回调。
        /// <code>
        /// if (Game.Sync.TryGetPosition(entityId, out var x, out var y, out var z))
        ///     transform.position = new Vector3(x, y, z);
        /// </code>
        ///
        /// 实体尚未进入视野、未收到过位置、或已离开视野时返回 false（此时输出参数为 0）。
        /// </summary>
        /// <param name="entityId">实体 ID（与服务端下发的一致；服务端对象号为 uint64，故用 ulong）</param>
        /// <param name="x">输出：插值后 X</param>
        /// <param name="y">输出：插值后 Y</param>
        /// <param name="z">输出：插值后 Z</param>
        /// <returns>该实体当前有可读位置时返回 true</returns>
        bool TryGetPosition(ulong entityId, out float x, out float y, out float z);

        /// <summary>
        /// 绑定消息路由器并注册引擎推送处理器（EMsg.PushPlayerFullSync / EMsg.PushDataSync）。
        /// 由 CloverNet.Init 自动调用，业务侧无需手动绑定。
        /// 只依赖 <see cref="IRouter"/>（注册/注销）——订阅是纯路由行为，不需要 INetwork。
        /// </summary>
        /// <param name="router">消息路由器实例（NetworkManager）</param>
        void Attach(IRouter router);

        /// <summary>
        /// 解除网络管理器绑定并注销推送处理器
        /// </summary>
        void Detach();

        /// <summary>
        /// 注册玩家全量同步回调（EMsg.PushPlayerFullSync 驱动，登录成功后推送一次）。
        /// </summary>
        /// <param name="handler">
        /// 回调参数一：data 按 kind 分桶的映射（kind → (type → 解析后的数据值)）；
        /// 回调参数二：account_data 的映射（type → 解析后的数据值）。
        /// </param>
        void OnFullSync(Action<Dictionary<string, Dictionary<string, object>>, Dictionary<string, object>> handler);

        /// <summary>
        /// 注册数据增量推送回调（EMsg.PushDataSync 驱动；**服务端恒以批量形态下发**，body 为 map[kind]RawValue，
        /// 这里按顶层键逐个展开回调）。
        /// </summary>
        /// <param name="handler">回调参数为 (顶层键 kind, 解析后的数据体 value)；注意该键在视野/移动/属性路径上是**事件名**（enter/leave/move/property），仅未注册类型才是数据类型名</param>
        void OnData(Action<string, object> handler);

        /// <summary>
        /// 注册实体进入场景的回调
        /// </summary>
        /// <param name="handler">回调参数为实体 ID 和初始属性字典（实体号为服务端 uint64，故为 ulong）</param>
        void OnEntityEnter(Action<ulong, Dictionary<string, object>> handler);

        /// <summary>
        /// 注册实体离开场景的回调
        /// </summary>
        /// <param name="handler">回调参数为实体 ID（ulong）</param>
        void OnEntityLeave(Action<ulong> handler);

        /// <summary>
        /// 注册实体移动的回调
        /// </summary>
        /// <param name="handler">回调参数为实体 ID（ulong）和目标坐标（x, y, z）</param>
        void OnEntityMove(Action<ulong, float, float, float> handler);

        /// <summary>
        /// 注册实体属性变更的回调
        /// </summary>
        /// <param name="handler">回调参数为实体 ID（ulong）、属性名和新值</param>
        void OnEntityProperty(Action<ulong, string, object> handler);

        /// <summary>注销全量同步回调（传入注册时的同一委托引用）。</summary>
        void OffFullSync(Action<Dictionary<string, Dictionary<string, object>>, Dictionary<string, object>> handler);

        /// <summary>注销数据增量回调（传入注册时的同一委托引用）。</summary>
        void OffData(Action<string, object> handler);

        /// <summary>注销实体进入回调。</summary>
        void OffEntityEnter(Action<ulong, Dictionary<string, object>> handler);

        /// <summary>注销实体离开回调。</summary>
        void OffEntityLeave(Action<ulong> handler);

        /// <summary>注销实体移动回调。</summary>
        void OffEntityMove(Action<ulong, float, float, float> handler);

        /// <summary>注销实体属性变更回调。</summary>
        void OffEntityProperty(Action<ulong, string, object> handler);

        /// <summary>
        /// 清除所有已注册的回调
        /// </summary>
        void Clear();
    }

    /// <summary>
    /// 服务端场景投影（服务端 mmo.Scene 的客户端对应物，与服务端同义）。
    /// 回答「玩家在服务端哪张地图、哪条分线」，数据来源为 EMsg.PushSceneInfo 推送。
    ///
    /// 与 Game.Scene（ISceneManager）的区别：
    ///   Game.Scene       —— Unity 关卡：加载/卸载哪个画面（客户端本地表现层）
    ///   Game.CloverScene —— 服务端场景：逻辑地图 id + 分线 id（双端同义）
    /// 二者通过 UnityScene 字段衔接；命名约定见 clover-doc/client/concepts/concept-naming。
    /// </summary>
    public interface ICloverScene
    {
        /// <summary>是否已收到服务端场景标识（未进入场景时为 false）</summary>
        bool IsValid { get; }

        /// <summary>
        /// 逻辑地图 id（服务端 Scene.ID()）。类型为 <c>ulong</c> 与服务端 <c>uint64</c> 及
        /// <see cref="IMapData.SceneId"/>（同为 ulong）对齐。
        /// </summary>
        ulong SceneID { get; }

        /// <summary>地图内分线 id（服务端 Instance id）</summary>
        uint InstanceID { get; }

        /// <summary>场景名（服务端 Scene.Name()）</summary>
        string Name { get; }

        /// <summary>
        /// 该服务端场景应加载的 Unity 关卡名（SceneID → Unity Scene 的映射）。
        /// 由业务或配表设置；AutoLoadUnityScene 打开时会在场景变更后自动加载。
        /// </summary>
        string UnityScene { get; set; }

        /// <summary>场景标识变更后是否自动调用 Game.Scene.Load(UnityScene)。默认 false，由业务决定加载时机。</summary>
        bool AutoLoadUnityScene { get; set; }

        /// <summary>
        /// 注册 SceneID → Unity 关卡名 的映射。
        /// 收到 ESceneInfoNotify 时自动根据映射填充 UnityScene 字段。
        /// </summary>
        /// <param name="sceneId">服务端逻辑地图 id（ulong，与服务端 uint64 对齐）</param>
        /// <param name="unitySceneName">对应的 Unity 关卡名</param>
        void RegisterMapping(ulong sceneId, string unitySceneName);

        /// <summary>
        /// 批量注册映射。
        /// </summary>
        /// <param name="mappings">SceneID → Unity 关卡名 的映射表</param>
        void RegisterMappings(IDictionary<ulong, string> mappings);

        /// <summary>
        /// 根据 SceneID 查询已注册的 Unity 关卡名。
        /// </summary>
        /// <param name="sceneId">服务端逻辑地图 id（ulong）</param>
        /// <returns>Unity 关卡名，未注册时返回 null</returns>
        string ResolveUnityScene(ulong sceneId);

        /// <summary>
        /// 绑定网络与路由器并注册 EMsg.PushSceneInfo 处理器（由 CloverNet.Init 调用，业务无需手动绑定）。
        /// </summary>
        /// <param name="net">网络管理器实例（会话 / 线路）</param>
        /// <param name="router">消息路由器实例（注册/注销推送处理器）</param>
        void Attach(INetwork net, IRouter router);

        /// <summary>解除网络绑定并注销处理器</summary>
        void Detach();

        /// <summary>订阅场景标识变更（回调在主线程）</summary>
        void OnChanged(Action<ICloverScene> handler);

        /// <summary>取消订阅场景标识变更</summary>
        void OffChanged(Action<ICloverScene> handler);

        /// <summary>清空当前场景标识（断线/登出时调用），不影响 UnityScene 映射</summary>
        void Clear();
    }

    // ====================================================================
    //  公告/通知推送接口
    // ====================================================================

    /// <summary>
    /// 公告/通知推送接口：消费 EMsg.PushAlert，将服务端公告分发给业务层。
    /// 与服务端 g.Alert(c, notify) 对齐。
    /// </summary>
    public interface IAlert
    {
        /// <summary>
        /// 公告推送回调：参数为公告数据（title, content, level, style, ttl）。
        /// 在**主线程**回调（Net.Tick → Router.Dispatch 同步分发），业务可直接操作 UI。
        /// </summary>
        event Action<EAlertNotify> OnAlert;

        /// <summary>绑定消息路由器并注册推送处理器（由 CloverNet.Init 调用）</summary>
        void Attach(IRouter router);

        /// <summary>解除网络绑定并注销处理器</summary>
        void Detach();
    }

    // ====================================================================
    //  数据 Schema 注册/订阅接口
    // ====================================================================

    /// <summary>
    /// 数据 Schema 声明接口：业务层把「数据类型 → 字段结构」登记给引擎，供本地类型化访问。
    /// 与服务端 RegisterTypeBySchema 对齐。
    ///
    /// **不承担数据订阅**——增量/全量订阅的唯一入口是 <see cref="IWorldSync"/>（`Game.Sync`）：
    ///   Game.Sync.OnData((type, value) =&gt; { ... })          // 增量，按 type 自行分派
    ///   Game.Sync.OnFullSync((data, accountData) =&gt; { ... })  // 全量
    /// 由 <see cref="IWorldSync.OnData"/> 分派，本接口不参与数据分发。
    ///
    /// 使用方式：
    /// <code>
    ///   var schema = new ObjectSchema().String("name", 0).Int("level", 1);
    ///   Game.Schema.RegisterSchema("player", schema);
    ///   var s = Game.Schema.GetSchema("player");
    /// </code>
    /// </summary>
    public interface ISchemaRegistry
    {
        /// <summary>
        /// 注册数据 Schema（类型名 → 字段结构）。
        /// 仅登记结构供本地访问/校验使用；数据到达由 Game.Sync 回调，本接口不参与分发。
        /// </summary>
        /// <param name="type">数据类型标识（与服务端 Schema.TypeName 一致）</param>
        /// <param name="schema">Schema 定义</param>
        void RegisterSchema(string type, ObjectSchema schema);

        /// <summary>
        /// 获取已注册的 Schema（未注册返回 null）。
        /// </summary>
        ObjectSchema GetSchema(string type);
    }

    // ====================================================================
    //  帧同步房间接口
    // ====================================================================

    /// <summary>
    /// 帧同步房间消息号配置。引擎层不含具体业务消息号，由业务注入。
    ///
    /// 字段类型为 <c>uint</c>，与 <c>EMsg</c> / <c>IRouter.OnMsg</c> / <c>INetwork.Call|Send</c>
    /// 的消息号类型保持一致——用 <c>int</c> 会导致每个调用点都要强制转换（曾经的 18 处编译错误）。
    /// 业务侧用整数字面量赋值（<c>Create = 1002001</c>）会自动隐式转换，无需改动。
    /// </summary>
    public class FrameRoomMsgIds
    {
        /// <summary>创建房间 C2S（如 1002001）</summary>
        public uint Create;
        /// <summary>加入房间 C2S（如 1002002）</summary>
        public uint Join;
        /// <summary>离开房间 C2S（如 1002003）</summary>
        public uint Leave;
        /// <summary>发送输入 C2S（如 1002004）</summary>
        public uint Input;
        /// <summary>设置准备 C2S（如 1002005）</summary>
        public uint Ready;
        /// <summary>请求快照 C2S（如 1002006）</summary>
        public uint Snapshot;
        /// <summary>查询房间信息 C2S（如 1002007）</summary>
        public uint Info;
        /// <summary>主动断连 C2S（如 1002008）</summary>
        public uint Disconnect;
        /// <summary>重新连接 C2S（如 1002009）</summary>
        public uint Reconnect;
        /// <summary>追帧恢复 C2S（如 1002010）</summary>
        public uint Recovery;
        /// <summary>接管恢复 C2S（如 1002011）</summary>
        public uint TakeoverRecovery;
        /// <summary>每帧同步推送（如 3002001）</summary>
        public uint FrameSync;
        /// <summary>房间关闭推送（如 3002002）</summary>
        public uint Closed;
        /// <summary>房间接管推送（引擎推送区间，如 4004）</summary>
        public uint Takeover;
    }

    /// <summary>
    /// 帧同步房间管理器接口：管理房间生命周期、帧输入收发、状态查询。
    ///
    /// 引擎层职责：
    ///   - 注册网络消息处理器 + 维护房间状态 + 通过回调通知业务层
    ///   - 提供 Create/Join/Leave/SendInput 等 C2S 操作
    ///
    /// 业务层职责：
    ///   - 通过 Configure() 注入消息号
    ///   - 订阅 OnFrame/OnClosed/OnTakeover 处理帧数据
    ///   - 在 OnFrame 回调中执行 lockstep 模拟
    /// </summary>
    public interface IFrameRoom
    {
        /// <summary>是否已配置消息号</summary>
        bool IsConfigured { get; }

        /// <summary>当前所在房间 ID（未加入房间时为 null）</summary>
        string CurrentRoomId { get; }

        /// <summary>当前已完成帧号（由服务端推送驱动）</summary>
        long CurrentFrame { get; }

        /// <summary>目标帧率</summary>
        int TargetFps { get; }

        /// <summary>房间内玩家 ID 列表</summary>
        IReadOnlyList<string> Players { get; }

        /// <summary>房间是否正在运行（已开始帧推进）</summary>
        bool IsRunning { get; }

        /// <summary>
        /// 每帧同步推送：包含 frame、fps、players 等。
        /// 在主线程回调，业务层在此执行 lockstep 模拟。
        /// </summary>
        event Action<Dictionary<string, object>> OnFrame;

        /// <summary>
        /// 房间关闭：包含 room_id、frame、reason。
        /// 房间关闭后 CurrentRoomId 自动清空。
        /// </summary>
        event Action<string, long, string> OnClosed;

        /// <summary>
        /// 房间隔线接管：服务端通过 EMsg.PushRoomTakeover(4004) 推送 proto.ERoomTakeoverNotify。
        /// 稳定字段 room_id / node_addr / frame / hash 可直接绑定，recovery 为动态 JSON → 动态字典。
        /// 业务层应在此回调中重新加入房间（或走 Reconnect 流程）。
        /// </summary>
        event Action<Dictionary<string, object>> OnTakeover;

        /// <summary>
        /// 配置业务消息号。必须在首次使用前调用一次。
        /// </summary>
        /// <param name="msgIds">消息号配置</param>
        void Configure(FrameRoomMsgIds msgIds);

        /// <summary>
        /// 绑定网络与路由器并注册推送处理器（由 CloverNet.Init 调用，业务无需手动绑定）。
        /// </summary>
        /// <param name="net">网络管理器实例（发消息 / 请求-回包）</param>
        /// <param name="router">消息路由器实例（注册/注销推送处理器）</param>
        void Attach(INetwork net, IRouter router);

        /// <summary>解除网络绑定并注销处理器</summary>
        void Detach();

        // ---- C2S 操作 ----

        /// <summary>创建帧同步房间（异步）</summary>
        /// <param name="roomId">房间 ID</param>
        /// <param name="targetFps">目标帧率（默认 30）</param>
        Task<FrameRoomReply> CreateRoomAsync(string roomId, int targetFps = 30);

        /// <summary>加入帧同步房间（异步），成功后自动设置 CurrentRoomId</summary>
        Task<FrameRoomJoinReply> JoinRoomAsync(string roomId);

        /// <summary>
        /// 设置准备状态（异步）。消息号由业务经 <see cref="FrameRoomMsgIds.Ready"/> 注入。
        /// 引擎的帧同步房间本身没有 ready 概念，是否/如何据此推进开局由业务决定。
        /// </summary>
        /// <param name="ready">true=已准备，false=取消准备</param>
        Task<FrameRoomReply> SetReadyAsync(bool ready);

        /// <summary>离开当前房间（异步），离开后清空房间状态</summary>
        Task<FrameRoomReply> LeaveRoomAsync();

        /// <summary>离开当前房间（同步便捷方法）</summary>
        void LeaveRoom();

        /// <summary>发送帧输入（可靠 TCP），帧号由服务端分配</summary>
        /// <param name="payload">业务自定义输入数据</param>
        void SendInput(string payload);

        /// <summary>发送帧输入，指定帧号（允许客户端预发送，MaxInputLead 由服务端控制）</summary>
        /// <param name="payload">业务自定义输入数据</param>
        /// <param name="frame">目标帧号</param>
        void SendInput(string payload, long frame);

        /// <summary>请求房间快照（异步）</summary>
        Task<FrameRoomReply> SnapshotAsync();

        /// <summary>查询房间信息（异步）</summary>
        Task<FrameRoomReply> InfoAsync();

        /// <summary>主动断开连接：通知服务端标记断线但保留槽位</summary>
        Task DisconnectAsync();

        /// <summary>重新连接房间（异步）：断线后获取完整恢复包</summary>
        Task<FrameRoomReply> ReconnectAsync();

        /// <summary>请求追帧恢复（异步）：获取最近快照 + 增量帧</summary>
        Task<FrameRoomReply> RecoveryAsync();

        /// <summary>接管后请求恢复（异步）</summary>
        Task<FrameRoomReply> TakeoverRecoveryAsync();
    }

    /// <summary>
    /// HTTP 响应结果
    /// </summary>
    public class WebResponse
    {
        /// <summary>
        /// HTTP 状态码
        /// </summary>
        public int StatusCode;

        /// <summary>
        /// 响应正文文本
        /// </summary>
        public string Text;

        /// <summary>
        /// 响应正文原始字节数据
        /// </summary>
        public byte[] Data;

        /// <summary>
        /// 请求是否成功
        /// </summary>
        public bool IsSuccess;
    }

    /// <summary>
    /// HTTP 请求接口，定义常用的 HTTP 操作
    /// </summary>
    public interface IWebRequest
    {
        /// <summary>
        /// 发送 GET 请求。
        /// 回包同时提供文本与原始字节（见 <see cref="WebResponse.Text"/> / <see cref="WebResponse.Data"/>），
        /// 因此不需要单独的「取字节」变体。
        /// </summary>
        /// <param name="url">请求地址</param>
        /// <param name="callback">响应回调，在主线程调用</param>
        void Get(string url, Action<WebResponse> callback);

        /// <summary>
        /// 发送 POST 请求
        /// </summary>
        /// <param name="url">请求地址</param>
        /// <param name="body">请求正文（JSON 字符串）</param>
        /// <param name="callback">响应回调，在主线程调用</param>
        void Post(string url, string body, Action<WebResponse> callback);

        /// <summary>
        /// 释放所有进行中的请求
        /// </summary>
        void Dispose();
    }

    /// <summary>
    /// 数据行基础接口，所有数据表行类型必须实现此接口以提供唯一标识。
    /// </summary>
    public interface IDataRow
    {
        /// <summary>
        /// 获取当前行的唯一标识。
        /// </summary>
        int Id { get; }
    }

    /// <summary>
    /// 数据表操作接口，定义了加载、查询和清理数据表的通用方法。
    /// </summary>
    public interface IDataTable
    {
        /// <summary>
        /// 从指定文件加载数据表，使用提供的解析函数将每行文本转换为数据行对象。
        /// </summary>
        /// <typeparam name="T">数据行类型，必须实现 <see cref="IDataRow"/>。</typeparam>
        /// <param name="fileName">数据文件名（TSV 格式，首行为表头）。</param>
        /// <param name="parser">将制表符分隔的字段数组解析为数据行对象的函数。</param>
        void Load<T>(string fileName, Func<string[], T> parser) where T : class, IDataRow;

        /// <summary>
        /// 根据唯一标识查询指定类型的数据行。
        /// </summary>
        /// <typeparam name="T">数据行类型，必须实现 <see cref="IDataRow"/>。</typeparam>
        /// <param name="id">要查询的行唯一标识。</param>
        /// <returns>匹配的数据行对象，若未找到则返回 null。</returns>
        T Get<T>(int id) where T : class, IDataRow;

        /// <summary>
        /// 获取指定类型数据表中的所有数据行。
        /// </summary>
        /// <typeparam name="T">数据行类型，必须实现 <see cref="IDataRow"/>。</typeparam>
        /// <returns>所有数据行的可枚举集合，若类型未加载则返回空集合。</returns>
        IEnumerable<T> GetAll<T>() where T : class, IDataRow;

        /// <summary>
        /// 清空所有已加载的数据表缓存。
        /// </summary>
        void Clear();
    }

    /// <summary>
    /// 本地化接口，提供多语言文本查询和语言切换能力。
    /// </summary>
    public interface ILocalization
    {
        /// <summary>
        /// 获取当前使用的语言标识。
        /// </summary>
        string Language { get; }

        /// <summary>
        /// 根据键名获取本地化文本，若键不存在则返回键名本身。
        /// </summary>
        /// <param name="key">本地化文本的键名。</param>
        /// <returns>对应的本地化文本，未找到时返回键名。</returns>
        string Get(string key);

        /// <summary>
        /// 切换到指定语言并重新加载语言文件。
        /// </summary>
        /// <param name="lang">目标语言标识（如 "zh"、"en"）。</param>
        void SetLanguage(string lang);

        /// <summary>
        /// 加载指定语言的文本数据，覆盖当前已加载的内容。
        /// </summary>
        /// <param name="lang">语言标识，对应 dataDir 下同名的 TSV 文件。</param>
        void Load(string lang);

        /// <summary>
        /// 注册语言切换事件的监听器，语言变更时将收到通知。
        /// </summary>
        /// <param name="handler">语言切换时的回调，参数为新语言标识。</param>
        void OnLanguageChanged(Action<string> handler);

        /// <summary>
        /// 移除语言切换事件的监听器。
        /// </summary>
        /// <param name="handler">此前注册的回调。</param>
        void OffLanguageChanged(Action<string> handler);
    }

    /// <summary>
    /// 资源管理接口，提供异步资源加载、引用计数释放与预加载能力。
    /// </summary>
    public interface IResourceManager
    {
        /// <summary>
        /// 异步加载指定路径的资源，完成后通过回调返回资源对象。
        /// </summary>
        /// <typeparam name="T">资源类型，必须为 <see cref="UnityEngine.Object"/> 派生类。</typeparam>
        /// <param name="path">相对于资源根目录的路径。</param>
        /// <param name="callback">加载完成回调，加载失败时资源参数为 null。</param>
        void LoadAsset<T>(string path, Action<T> callback) where T : UnityEngine.Object;

        /// <summary>
        /// 异步加载指定路径的资源，并在过程中报告进度。
        /// </summary>
        /// <typeparam name="T">资源类型，必须为 <see cref="UnityEngine.Object"/> 派生类。</typeparam>
        /// <param name="path">相对于资源根目录的路径。</param>
        /// <param name="progress">加载进度回调，参数为 0~1 的进度值；命中缓存时直接回调 1。</param>
        /// <param name="callback">加载完成回调，加载失败时资源参数为 null。</param>
        void LoadAsset<T>(string path, Action<float> progress, Action<T> callback) where T : UnityEngine.Object;

        /// <summary>
        /// 减少指定资源的引用计数；**只降计数，不立即释放**（缓存淘汰由水位 + LRU 决定）。
        /// 加载在途（尚未入缓存）时该调用会被忽略。
        /// </summary>
        /// <param name="path">资源路径，须与加载时传入的路径一致。</param>
        void Release(string path);

        /// <summary>
        /// 卸载所有缓存的资源，并触发 Unity 的未使用资源回收。
        /// </summary>
        void UnloadAll();

        /// <summary>
        /// 批量预加载指定资源列表，全部完成后触发回调。
        /// </summary>
        /// <param name="paths">待预加载的资源路径列表，为空时直接触发回调。</param>
        /// <param name="onDone">全部资源加载完成后的回调。</param>
        void Preload(List<string> paths, Action onDone, Action<float> progress = null);

        /// <summary>
        /// <b>同步</b>取一个"已经驻留在缓存里"的资源；没有驻留就返回 <c>null</c>。
        /// <para>
        /// <b>不触发加载、不阻塞主线程。</b> 想要"立刻拿到"的正确用法是：
        /// 先 <see cref="Preload"/>（或任意一次 <see cref="LoadAsset{T}(string, Action{T})"/>）把它装进来，
        /// 之后就能同步取。引擎刻意**不提供**阻塞式同步加载 —— 那会把主线程卡住。
        /// </para>
        /// <para>
        /// 语义是<b>纯读</b>：<b>不影响引用计数、不改变 LRU 顺序</b>。
        /// 因此取到的对象**不归调用方"持有"**，不要对它调 <see cref="Release"/>（引用计数由加载方负责）。
        /// 已被销毁 / 已卸载的对象按"没有"处理（返回 <c>null</c>），
        /// 不会把 Unity 的「假 null」交出去。
        /// </para>
        /// </summary>
        /// <param name="path">资源路径，须与加载时传入的路径一致。</param>
        T TryGet<T>(string path) where T : UnityEngine.Object;

        /// <summary>
        /// <b>同步</b>回答「这条路径在可加载源里存不存在」—— 只回答，<b>不改变缓存/引用计数</b>。
        /// <para>
        /// <b>三个同步入口的语义差别（别用错）</b>：
        /// <list type="table">
        ///   <item><term><see cref="TryGet{T}(string)"/></term>
        ///     <description>只取<b>已经驻留</b>的资源；不加载、不阻塞。没装进来就是 <c>null</c>。</description></item>
        ///   <item><term><see cref="Exists(string)"/></term>
        ///     <description>只回答「在不在」；<b>不驻留</b>（不占缓存、不动引用计数）。
        ///       「在」不代表能同步拿到对象（拿对象要么 <see cref="TryGet{T}(string)"/>，要么异步
        ///       <see cref="LoadAsset{T}(string, Action{T})"/>）。</description></item>
        ///   <item><term><see cref="LoadAll{T}(string)"/></term>
        ///     <description><b>会真的加载</b>（同步、阻塞主线程），按路径取该处<b>全部</b>资源。</description></item>
        /// </list>
        /// </para>
        /// <para>
        /// 实现可取巧：后端有索引（清单）时查索引即可；没有索引的后端允许降级为<b>一次探测</b>，
        /// 并<b>按路径缓存</b>结果（同一路径不重复探测）。因此本方法**不保证**「源里真没有」与
        /// 「后端探不出来」可区分 —— 只承诺「<c>false</c> ⇒ 引擎按没有处理」。
        /// 空路径恒为 <c>false</c>（不探测）。
        /// </para>
        /// </summary>
        /// <param name="path">相对于资源根目录的路径。</param>
        /// <returns>该路径下存在可加载对象为 true，否则 false。</returns>
        bool Exists(string path);

        /// <summary>
        /// <b>同步</b>批量取一个路径下的<b>全部</b>资源（用于「条带 / 图集子 sprite 按名取不到」的场景：
        /// 逐个帧名 <see cref="LoadAsset{T}(string, Action{T})"/> 会取不到，只有整条取才拿得到）。
        /// <para>
        /// 与 <see cref="TryGet{T}(string)"/> 相反，它<b>会加载</b>（同步、阻塞主线程）；
        /// 也<b>不进</b>缓存/引用计数 —— 取到的对象由调用方自己持有，引擎不做驻留记账
        /// （即：不要对它调 <see cref="Release"/>，也没有 <see cref="CachedBytes"/> 影响）。
        /// </para>
        /// <para>
        /// 后端不支持（例如 AssetBundle 清单未声明「路径 → 包内全部资源」的映射）⇒
        /// <b>返回空数组并 Warn 一次</b>：⛔ 不抛异常、也不静默。空数组一律表示「没取到」，
        /// 调用方按自己的降级分支处理（占位色 / 换默认字体 / 报一条 Warn 等）。
        /// </para>
        /// </summary>
        /// <typeparam name="T">资源类型，必须为 <see cref="UnityEngine.Object"/> 派生类。</typeparam>
        /// <param name="path">相对于资源根目录的路径（**不要**再带 <c>CloverRes.Init</c> 的根前缀）。</param>
        /// <returns>该路径下的全部资源；一个都没有 / 后端不支持时为长度 0 的数组（**不返回 null**）。</returns>
        T[] LoadAll<T>(string path) where T : UnityEngine.Object;

        // ─────────────────────────── 内存水位与采样 ───────────────────────────

        /// <summary>
        /// 当前缓存占用的估算字节数。资源对象大小由后端给出（AssetBundle 用包体大小，
        /// Resources 用纹理/网格的估算值），因此是**用于水位决策的近似值**，不是精确内存占用。
        /// </summary>
        long CachedBytes { get; }

        /// <summary>
        /// 缓存字节水位。超过后按 LRU 释放**引用计数为 0** 的资源；仍在被引用的资源**不会被释放**
        /// （宁可不释放，也不能把正在用的资源卸掉 —— 那会让业务侧拿到 Unity 的「假 null」）。
        /// 设为 0 或负数表示不限制。
        /// </summary>
        long CacheWatermark { get; set; }

        /// <summary>
        /// 每帧驱动：轮询在途加载的进度、推进热更下载。由 Game.Tick 调用，业务无需手动调。
        /// </summary>
        void Tick(float dt);

        // ─────────────────────────── 资源热更 ───────────────────────────
        //
        // 分工：CheckUpdate 只比对不下载；DownloadUpdate 只下载不切换；下载产物落在可写目录，
        // 下次启动（或显式重启 App）时生效。**不做运行中热切换** —— 正在被使用的资源在脚下被替换
        // 会引出难以定位的空引用，代价远大于「提示玩家重启一次」。

        /// <summary>当前生效的资源版本号（未启用热更时为 "0"）。</summary>
        string Version { get; }

        /// <summary>
        /// 热更内容目录（绝对路径）：配表这类**裸文件**下载后落在这里。
        /// 把 <c>CloverData.InitDataTable(dir)</c> 指向它即完成配表热更。
        /// 未热更过时目录可能尚不存在。
        /// </summary>
        string ContentDir { get; }

        /// <summary>是否运行在 AssetBundle 后端（false = 走 Unity 内置 Resources）。</summary>
        bool IsBundleMode { get; }

        /// <summary>当前热更阶段（调试面板展示用）。</summary>
        ResourceUpdateState UpdateState { get; }

        /// <summary>
        /// 拉取远端清单并与本地逐文件比对 hash，**只比对不下载**。
        /// 回调必定被调用一次：成功时 <see cref="ResourceUpdateInfo.Success"/> 为 true，
        /// 失败时 <see cref="ResourceUpdateInfo.Error"/> 给出原因（回调参数不为 null）。
        /// </summary>
        /// <param name="onResult">比对结果回调（主线程）。</param>
        void CheckUpdate(Action<ResourceUpdateInfo> onResult);

        /// <summary>
        /// 下载 <paramref name="info"/> 里的差异文件，支持**断点续传**（HTTP Range + 本地半成品偏移）
        /// 与失败重试。全部完成且 hash 校验通过后才写入本地版本号。
        /// </summary>
        /// <param name="info">CheckUpdate 的结果。</param>
        /// <param name="onProgress">进度回调（主线程，会复用同一个实例）。</param>
        /// <param name="onDone">完成回调：成功为 true；失败为 false 且第二个参数为原因。</param>
        void DownloadUpdate(ResourceUpdateInfo info, Action<ResourceUpdateProgress> onProgress,
            Action<bool, string> onDone);

        /// <summary>
        /// 清除已下载内容与本地版本记录（换服 / 回滚 / 坏包修复用）。
        /// 只清理热更目录，不动工程内首包资源。
        /// </summary>
        void ClearDownloaded();

        /// <summary>
        /// **取消**在途的热更下载与清单检查（保留 <c>.part</c> 半成品，下次更新可续传）。
        /// <para>
        /// 两条消费路径：
        /// ① 业务侧「玩家点取消更新 / 退出更新界面」——直接调它；
        /// ② 引擎拆卸（<c>Game.Shutdown</c>）——自动调用，避免在途请求既不释放也不回调、
        ///    把请求对象与回调引用一起残留到进程回收。
        /// </para>
        /// <para>未启用热更时为空操作（只记一条日志）。可重复调用（幂等）。</para>
        /// </summary>
        void CancelUpdate();
    }
}
