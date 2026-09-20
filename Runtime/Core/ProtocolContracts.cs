using System;

namespace CloverEngine
{
    // 本文件收纳「被 Core 契约直接暴露的协议载体（DTO）」。
    //
    // 为什么这些类放在 Core 而不是 Network：Core 的契约（IAlert.OnAlert 的事件参数、
    // IFrameRoom 的返回类型）直接暴露了它们，而 asmdef 依赖是「Network → Core 单向」，
    // Core 不允许引用 Network。若把它们留在 Network，Core 就会「使用未引用程序集的类型」
    // 而无法编译；而给 Core 补上 Network 引用又会让两者互相引用（Unity 不支持循环引用）。
    // 因此：凡被 Core 接口签名 / 事件 / 属性直接暴露的协议载体，必须定义在 Core。
    //
    // 新增 DTO 时照此判断：
    //   - 只被 Network 内部使用的 → 留在 Network/Protocol.cs；
    //   - 出现在 Core 的接口签名 / 事件 / 属性上 → 放这里。

    /// <summary>
    /// 公告 / 警告推送（<c>EMsg.PushAlert</c>）。
    /// 被 <see cref="IAlert.OnAlert"/> 的事件签名使用，故定义在 Core。
    /// </summary>
    [Serializable]
    public class EAlertNotify
    {
        /// <summary>标题</summary>
        public string title;

        /// <summary>正文内容</summary>
        public string content;

        /// <summary>级别（info/warn/error 等）</summary>
        public string level;

        /// <summary>展示样式</summary>
        public string style;

        /// <summary>存活时长（毫秒，对齐服务端 int 毫秒定义）</summary>
        public int ttl;
    }

    /// <summary>
    /// 排队位置通知（<c>EMsg.QueuePosition</c>，网关直发帧）。
    /// 服务端字段见 <c>pkg/shared/proto/msg.go</c> 的 EQueuePositionNotify。
    /// 被 <see cref="INetwork.QueueAhead"/> 等状态暴露，并经 <c>Net.QueuePosition</c> 事件发布，
    /// 故定义在 Core（与 EAlertNotify 同理）。
    /// </summary>
    [Serializable]
    public class EQueuePositionNotify
    {
        /// <summary>前面还有多少人（0 = 已到队首，下一个就放行）</summary>
        public int ahead;

        /// <summary>当前队列总人数（含自己）</summary>
        public int total;

        /// <summary>排队编号（入队序号，单调递增；仅用于展示与排障，不参与排队逻辑）</summary>
        public long ticket;
    }

    /// <summary>
    /// 帧同步房间通用回包，字段与业务侧 def 包（<c>game/def/reply.go</c>，不在本仓库）的 FrameRoomReply 逐字对齐。
    /// 被 <see cref="IFrameRoom"/> 的多个方法作为返回类型使用，故定义在 Core。
    /// </summary>
    [Serializable]
    public class FrameRoomReply
    {
        /// <summary>结果摘要（服务端生成的简要说明）</summary>
        public string summary;

        /// <summary>操作是否成功</summary>
        public bool ok;

        /// <summary>房间 ID</summary>
        public string room_id;

        /// <summary>房间所在节点地址</summary>
        public string node_addr;

        /// <summary>附加消息（移动测试场景下为 MovementBroadcast 的 JSON 字符串）</summary>
        public string message;

        /// <summary>错误描述（成功时为空）</summary>
        public string err;
    }

    /// <summary>
    /// 加入帧同步房间回包（含跨节点接管信息），字段与业务侧 def 包（<c>def/reply.go</c>，不在本仓库）的
    /// FrameRoomJoinReply 对齐。被 <see cref="IFrameRoom.JoinRoomAsync"/> 作为返回类型使用，故定义在 Core。
    /// 注意：服务端还有 takeover_recovery（any，动态 JSON），JsonUtility 无法表达，
    /// 需要时对原始 body 走 MiniJson（ctx.Body）。
    /// </summary>
    [Serializable]
    public class FrameRoomJoinReply
    {
        /// <summary>结果摘要</summary>
        public string summary;

        /// <summary>是否发生了跨节点切换（true 表示原房间在其他节点）</summary>
        public bool switched;

        /// <summary>切换到的目标节点地址（switched=true 时有效；服务端 json 键为 owner_addr）</summary>
        public string owner_addr;

        /// <summary>新节点分配的房间 ID（switched=true 且 owner_addr 非空时有效）</summary>
        public string room_id;

        /// <summary>错误描述（成功时为空）</summary>
        public string err;
    }
}
