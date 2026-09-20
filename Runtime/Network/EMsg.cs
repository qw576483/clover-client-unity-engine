namespace CloverEngine
{
    /// <summary>
    /// 引擎级内置消息号常量，与 clover-server-engine/pkg/shared/proto 全局区间约定对齐。
    /// 业务消息号必须 >= InternalMsgMax + 1（即 >= 10001），由业务**手工维护**常量定义，
    /// **不提供代码生成 / 自动生号**；两端同名同值，改动需双端同时改。
    /// </summary>
    public static class EMsg
    {
        // 号位 1 曾用于 Signup（注册）：注册已完全移到账号服 HTTP
        // （CloverAuth.SignupAsync → POST {账号服}/auth/signup），游戏服不接收注册报文。
        // 号位作废但**保留不复用**——号位是双端协议契约，回收会让旧客户端发出语义不同的报文。

        /// <summary>账号登录</summary>
        public const uint Login = 2;

        /// <summary>断线重连后用 session_token 恢复会话</summary>
        public const uint ResumeSession = 3;

        /// <summary>排行榜查询</summary>
        public const uint RankQuery = 4;

        /// <summary>TCP/WS 玩家凭令牌上报常驻裸 UDP 端点（体为 EMsg.UDPBindGrant 下发的令牌）</summary>
        public const uint BindUDP = 5;

        /// <summary>网关下发的不可靠通道绑定令牌（登录后先收到本帧，再上报 EMsg.BindUDP）</summary>
        public const uint UDPBindGrant = 6;

        /// <summary>
        /// 排队位置通知（网关直发）：服务器限流/满载时连入等候队列，本帧携带「前面还有 N 人」。
        /// 入队时下发一次，队列前进后按固定间隔刷新（位置没变不重发）；
        /// 未启用排队（服务端 gateway.queue_cap=0）时不会收到本帧——超限直接断连。
        /// body 为 EQueuePositionNotify（JSON），对应 "Net.QueuePosition" 事件与
        /// <c>IsQueued</c> / <c>QueueAhead</c> / <c>QueueTotal</c> 状态。
        /// </summary>
        public const uint QueuePosition = 7;

        /// <summary>登录成功后玩家全量数据推送</summary>
        public const uint PushPlayerFullSync = 4001;

        /// <summary>公告/警告推送</summary>
        public const uint PushAlert = 4002;

        /// <summary>数据增量同步推送</summary>
        public const uint PushDataSync = 4003;

        /// <summary>房间接管推送（断线重连场景）</summary>
        public const uint PushRoomTakeover = 4004;

        /// <summary>场景标识推送：玩家进入/切换场景时下发所在服务端场景（scene_id + instance_id）</summary>
        public const uint PushSceneInfo = 4005;

        /// <summary>通用错误回包（body 为 EErrorReply{err}），回包 msgID 使用该特殊值</summary>
        public const uint Error = 0xFFFFFFFF;

        /// <summary>引擎消息号上界，业务消息号必须大于该值</summary>
        public const uint InternalMsgMax = 10000;
    }
}
