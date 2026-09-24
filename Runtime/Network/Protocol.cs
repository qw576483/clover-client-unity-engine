using System;

namespace CloverEngine
{
    // 协议体字段名直接使用 snake_case：Unity JsonUtility 按字段名生成 JSON 键，
    // 必须与服务端 Go 结构体的 json tag（如 player_id / session_token）逐字一致。
    //
    // 类型对齐基准（clover-server-engine 服务端结构体）：
    //   - ERankEntry.Score 为 float64 → 客户端用 double
    //   - ERankEntry.Rank 为 int      → 客户端用 int
    //   - EAlertNotify.TTL 为 int 毫秒
    //   - player_id 线协议为 string
    //   - EPlayerFullSyncNotify.Data / AccountData 为 map[string]json.RawMessage，
    //     JsonUtility 无法表达，需要动态解析时使用 MiniJson（见 WorldSync / Game.Sync）。

    // 注册只走 CloverAuth.SignupAsync（POST {账号服}/auth/signup）：
    // 游戏服不接收注册报文，双端都不需要 ESignupRequest / ESignupReply 这两个协议体。

    /// <summary>
    /// 账号登录请求（EMsg.Login）。
    /// </summary>
    [Serializable]
    public class ELoginRequest
    {
        /// <summary>登录唯一凭证：账号服下发的 JWT（先经 CloverAuth.LoginAsync 换取）</summary>
        public string token;

        /// <summary>
        /// 是否声明「支持会话通道加密」（AES-256-GCM）。
        /// <b>业务不要手填</b>：NetworkManager.Call 在发登录请求时按平台能力自动置位
        /// （见 <see cref="INetwork.IsChannelEncrypted"/>），声明后服务端才在登录回包里下发 session_key。
        /// 与服务端 ELoginRequest.Encrypt 同名字段。
        /// </summary>
        public bool encrypt;
    }

    /// <summary>
    /// 账号登录回包（按 requestID 配对，msgID 恒为 0）。
    /// </summary>
    [Serializable]
    public class ELoginReply
    {
        /// <summary>归属逻辑服 owner 标识</summary>
        public string owner;

        /// <summary>会话令牌</summary>
        public string token;

        /// <summary>是否成功</summary>
        public bool success;

        /// <summary>失败原因，成功时为空</summary>
        public string err;

        /// <summary>
        /// 会话密钥（base64 的 32B AES-256 密钥）：本帧声明了 <see cref="ELoginRequest.encrypt"/>
        /// 时由服务端下发，引擎据此启用整帧通道加密（NetworkManager 自动处理，业务无需读取）。
        /// 未声明时为<b>空</b>，本次会话保持明文。
        /// <b>不要当作断线重连凭证</b>：重连凭证是 <see cref="EPlayerFullSyncNotify.session_token"/>。
        /// </summary>
        public string session_key;
    }

    /// <summary>
    /// 会话恢复请求（EMsg.ResumeSession），断线重连后提交。
    /// </summary>
    [Serializable]
    public class EResumeSessionRequest
    {
        /// <summary>玩家唯一 ID（服务端定义为字符串）</summary>
        public string player_id;

        /// <summary>断线前持有的会话密钥</summary>
        public string session_token;
    }

    /// <summary>
    /// 会话恢复回包（按 requestID 配对，msgID 恒为 0）。
    /// 成功时服务端不重发全量同步，客户端沿用断线前的世界状态；
    /// 失败时服务端随后踢除连接（走重连失败分支）。
    /// </summary>
    [Serializable]
    public class EResumeSessionReply
    {
        /// <summary>玩家唯一 ID</summary>
        public string player_id;

        /// <summary>是否成功</summary>
        public bool success;

        /// <summary>失败原因，成功时为空</summary>
        public string err;

        /// <summary>
        /// 归属对象标识（账号）。服务端在恢复会话时下发，供**网关**把重连后的新连接
        /// 绑回原 owner —— 没有它，新连接过不了网关登录门禁，后续每条业务消息都会被
        /// 拒为 401（表现为「恢复成功但立刻失能」）。客户端一般只需按 401 事件处理，
        /// 不必直接消费本字段。
        /// </summary>
        public string owner;
    }

    /// <summary>
    /// 排行榜查询请求（EMsg.RankQuery）。
    /// <para>
    /// 【保留，勿按"无消费点"删除】服务端**有对应实现**：请求体
    /// <c>clover-server-engine/internal/shared/proto/msg.go</c> 的 <c>ERankQueryRequest</c>、
    /// 处理路径 <c>internal/domain/master/rank.go</c> 的 <c>RankQuery</c> handler、
    /// 回包 <c>internal/shared/proto/reply.go</c> 的 <c>ERankQueryReply</c>。
    /// 本类与回包类是**字段对照类**（引擎侧不主动发这条消息，业务按需自建请求）。
    /// </para>
    /// </summary>
    [Serializable]
    public class ERankQueryRequest
    {
        /// <summary>榜单名</summary>
        public string board;

        /// <summary>查询的成员标识（选填）</summary>
        public string member;

        /// <summary>区间起始（1 起，与服务端一致）</summary>
        public int start;

        /// <summary>区间结束（1 起，含）</summary>
        public int stop;
    }

    /// <summary>
    /// 排行榜单条条目。
    /// 服务端 Extra 字段为 json.RawMessage，JsonUtility 解析时自动跳过；
    /// 需要读取时用 MiniJson 对整体 body 做动态解析。
    /// </summary>
    [Serializable]
    public class ERankEntry
    {
        /// <summary>成员标识</summary>
        public string member;

        /// <summary>分数（服务端 float64）</summary>
        public double score;

        /// <summary>名次（1 起，服务端 int）</summary>
        public int rank;
    }

    /// <summary>
    /// 排行榜查询回包（按 requestID 配对，msgID 恒为 0）。
    /// 【保留，勿按"无消费点"删除】服务端对应 <c>internal/shared/proto/reply.go</c> 的 <c>ERankQueryReply</c>。
    /// </summary>
    [Serializable]
    public class ERankQueryReply
    {
        /// <summary>榜单名</summary>
        public string board;

        /// <summary>成员查询结果（member 非空时有效）</summary>
        public ERankEntry member;

        /// <summary>member 是否在榜</summary>
        public bool member_found;

        /// <summary>区间榜单数据</summary>
        public ERankEntry[] range;

        /// <summary>榜单总人数</summary>
        public int total;

        /// <summary>失败原因，成功时为空</summary>
        public string err;
    }

    /// <summary>
    /// 通用错误回包（msgID = EMsg.Error，按 requestID 配对）：逻辑服 handler 返回 error，
    /// 或网关登录门禁拒绝未登录连接时下发。
    /// </summary>
    [Serializable]
    public class EErrorReply
    {
        /// <summary>错误描述（人类可读，仅供展示与日志，不应参与逻辑判断）</summary>
        public string err;

        /// <summary>
        /// 机器可读错误码（0 = 未分类），取值见 <see cref="ErrCode"/>，如 401 = 未认证。
        /// 业务应据码判断；服务端未返回该字段时为 0。
        /// </summary>
        public int code;
    }

    /// <summary>
    /// 玩家角色档案安全视图（EPlayerFullSyncNotify.player）。
    /// </summary>
    [Serializable]
    public class EPlayerSyncView
    {
        /// <summary>玩家唯一 ID</summary>
        public string player_id;

        /// <summary>角色名</summary>
        public string name;

        /// <summary>账号名</summary>
        public string account;

        /// <summary>逻辑服 ID</summary>
        public uint server_id;
    }

    /// <summary>
    /// 账号记录安全视图（EPlayerFullSyncNotify.account_info，不含密码/token）。
    /// </summary>
    [Serializable]
    public class EAccountSyncView
    {
        /// <summary>账号名</summary>
        public string account;

        /// <summary>创建时间（服务端为格式化字符串）</summary>
        public string create_time;

        /// <summary>最近登录时间（服务端为格式化字符串）</summary>
        public string login_time;
    }

    /// <summary>
    /// 玩家全量同步推送（EMsg.PushPlayerFullSync），登录成功后服务端主动下发。
    /// Data（kind → type → JSON）与 AccountData（type → JSON）为动态 map，
    /// JsonUtility 不支持，需要消费这两块时通过 Game.Sync（WorldSync）或 MiniJson 解析。
    /// </summary>
    [Serializable]
    public class EPlayerFullSyncNotify
    {
        /// <summary>玩家唯一 ID</summary>
        public string player_id;

        /// <summary>账号名</summary>
        public string account;

        /// <summary>会话密钥，客户端须保存供断线重连（仅驻留内存）</summary>
        public string session_token;

        /// <summary>角色档案视图（脱敏）</summary>
        public EPlayerSyncView player;

        /// <summary>账号安全视图</summary>
        public EAccountSyncView account_info;
    }

    // EAlertNotify 已移至 Core/ProtocolContracts.cs：它被 Core 的 IAlert.OnAlert 事件签名直接
    // 暴露，留在 Network 会让 Core「使用未引用程序集的类型」而无法编译（详见该文件头注释）。

    // 数据增量同步推送（EMsg.PushDataSync）没有独立协议体类，原因如下：
    //   - 服务端只发批量形态：body 为顶层 map[type]json.RawMessage（多个未注册类型合并下发），
    //     value 即该 type 的数据体（全量 / 字段级 diff），没有 {type, body} 包装；
    //   - 已注册类型走各自的业务消息号推送，不经 EMsg.PushDataSync。
    // 该 body 无法用 JsonUtility 表达，统一由 WorldSyncManager（Game.Sync）经 MiniJson
    // 解析后按 type 分发。与服务端 internal/transport/event/sync.go 的实现逐字对应。

    /// <summary>
    /// 场景标识推送（EMsg.PushSceneInfo），玩家进入/切换场景时由服务端场景下发。
    /// 与服务端 mmo.Scene 语义一致：scene_id 是逻辑地图，instance_id 是地图内分线。
    /// 客户端由 Game.CloverScene 消费，与 Game.Scene（Unity 关卡）分属两层，不可混用。
    /// </summary>
    [Serializable]
    public class ESceneInfoNotify
    {
        /// <summary>
        /// 逻辑地图 id（服务端 Scene.ID()）。类型为 <c>ulong</c> 与服务端 <c>uint64</c> 对齐
        /// （<c>clover-server-engine/pkg/shared/proto/push.go</c> 的 <c>SceneID uint64</c>）：
        /// 用有符号 long 时 scene_id ≥ 2^63 会解析成负数。
        /// 客户端消费方是 <see cref="ICloverScene.SceneID"/>（同为 ulong）与 <c>Game.Map.SceneId</c>。
        /// </summary>
        public ulong scene_id;

        /// <summary>地图内分线 id（服务端 Instance id）</summary>
        public uint instance_id;

        /// <summary>场景名（服务端 Scene.Name()）</summary>
        public string name;
    }

    /// <summary>
    /// 房间接管恢复推送（EMsg.PushRoomTakeover=4004），对应服务端 proto.ERoomTakeoverNotify。
    /// 断线重连/跨节点接管完成后下发：通知新 owner 地址、恢复基准帧与哈希。
    /// recovery 为 iframe.RecoveryPack 的动态 JSON，JsonUtility 无法表达——
    /// 引擎 FrameRoomManager 用 MiniJson 解析后经 Game.FrameRoom.OnTakeover 回调动态字典。
    /// 本类只用于字段对照，业务实际消费走 OnTakeover。
    /// </summary>
    [Serializable]
    public class ERoomTakeoverNotify
    {
        /// <summary>房间 ID</summary>
        public string room_id;

        /// <summary>新 owner 节点地址</summary>
        public string node_addr;

        /// <summary>恢复基准帧（RecoveredUntil）</summary>
        public long frame;

        /// <summary>基准帧状态哈希</summary>
        public ulong hash;
    }

    // ====================================================================
    //  帧同步房间协议体
    // ====================================================================

    /// <summary>
    /// 帧同步房间通用请求体（仅含 room_id）。
    /// 用于 Leave / Snapshot / Info / Disconnect / Reconnect / Recovery / TakeoverRecovery。
    /// </summary>
    [Serializable]
    public class FrameRoomRequest
    {
        /// <summary>房间 ID</summary>
        public string room_id;
    }

    /// <summary>
    /// 设置准备状态请求（MsgFrameRoomReady，号由业务注入）。
    /// 字段与服务端 game/def/msg.go 的 FrameRoomReadyReq 逐字对齐。
    /// </summary>
    [Serializable]
    public class FrameRoomReadyRequest
    {
        /// <summary>房间 ID</summary>
        public string room_id;

        /// <summary>true=已准备，false=取消准备</summary>
        public bool ready;
    }

    /// <summary>
    /// 创建帧同步房间请求。
    /// </summary>
    [Serializable]
    public class FrameRoomCreateRequest
    {
        /// <summary>房间 ID</summary>
        public string room_id;

        /// <summary>目标帧率（默认 30）</summary>
        public int target_fps;
    }

    /// <summary>
    /// 发送帧输入请求。
    /// 帧号可选：不填由服务端按提交顺序分配，填了由服务端校验 MaxInputLead。
    /// </summary>
    [Serializable]
    public class FrameRoomInputRequest
    {
        /// <summary>房间 ID</summary>
        public string room_id;

        /// <summary>业务自定义输入 payload（JSON 字符串）</summary>
        public string payload;

        /// <summary>目标帧号（可选，0 表示由服务端分配）</summary>
        public long frame;
    }

    // FrameRoomReply / FrameRoomJoinReply 已移至 Core/ProtocolContracts.cs：
    // 它们被 Core 的 IFrameRoom 作为返回类型直接暴露，留在 Network 会让 Core
    //「使用未引用程序集的类型」而无法编译（详见该文件头注释）。

    /// <summary>
    /// 帧同步推送数据（业务层 Map → Map，引擎层不反序列化具体字段）。
    /// 服务端格式示例：{ "room_id": "battle_01", "frame": 42, "fps": 30, "player_count": 2, "players": {...} }
    /// 由 FrameRoomManager 通过 MiniJson 解析为 Dictionary&lt;string, object&gt;。
    /// <para>
    /// 【保留，勿按"无消费点"删除】服务端**有对应实现**：每帧广播结构
    /// <c>clover-server-engine/pkg/domain/room/frame/types.go</c> 的 <c>FramePush</c>
    /// （<c>internal/domain/room/frame/types.go</c> 以别名导出）、下发路径 <c>room.go</c> 的广播。
    /// 本类为**字段对照类**（引擎推送实际走 MiniJson 动态解析，见 FrameRoomManager）。
    /// </para>
    /// </summary>
    [Serializable]
    public class EFrameSyncNotify
    {
        /// <summary>房间 ID</summary>
        public string room_id;

        /// <summary>当前帧号</summary>
        public long frame;

        /// <summary>目标帧率</summary>
        public int fps;

        /// <summary>房间内玩家数</summary>
        public int player_count;
    }

    /// <summary>
    /// 房间关闭推送数据。
    /// 服务端格式：{ "room_id": "battle_01", "frame": 1200, "reason": "all_disconnected" }
    /// <para>
    /// 【保留，勿按"无消费点"删除】服务端**有对应实现**：
    /// <c>clover-server-engine/pkg/domain/room/frame/types.go</c> 的 <c>RoomClosedPush</c>
    /// （<c>internal/domain/room/frame/room.go</c> 构造并经广播路径下发）。本类为**字段对照类**。
    /// </para>
    /// </summary>
    [Serializable]
    public class EFrameRoomClosedNotify
    {
        /// <summary>房间 ID</summary>
        public string room_id;

        /// <summary>关闭时的帧号</summary>
        public long frame;

        /// <summary>关闭原因（如 all_disconnected、host_left）</summary>
        public string reason;
    }
}
