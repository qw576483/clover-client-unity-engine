namespace CloverEngine
{
    /// <summary>
    /// 引擎级错误码，与 clover-server-engine 的 pkg/shared/proto.ErrCode* 是同一套数字契约。
    /// 出现在 <see cref="EErrorReply.code"/> 与 <see cref="CloverCallException.Code"/> 上。
    ///
    /// 设计意图：让客户端能对「失败」做统一处理（如 401 → 回到登录流程），
    /// 而不是匹配错误文案——文案会变，码不会。业务自定义错误码请从 1000 起，
    /// 避免与引擎段（0 与 4xx/5xx）冲突。
    /// </summary>
    public static class ErrCode
    {
        /// <summary>
        /// 无错误码（缺省值）。引擎内部未分类的 error（非 BizError）会落在此值，
        /// 此时业务只能回退到「按 err 文案展示」，不要做逻辑判断。
        /// </summary>
        public const int None = 0;

        /// <summary>请求不合法（参数缺失 / 格式错误 / 状态不允许）</summary>
        public const int BadRequest = 400;

        /// <summary>
        /// 未认证：未登录、会话失效，或网关登录门禁拒绝。
        /// 业务收到后应回到登录流程（引擎会同时发布 Net.OnUnauthorized 事件）。
        /// </summary>
        public const int Unauthenticated = 401;

        /// <summary>已认证但无权限（如操作不属于自己的房间）</summary>
        public const int Forbidden = 403;

        /// <summary>目标不存在（房间已解散、玩家不在线等）</summary>
        public const int NotFound = 404;

        /// <summary>频率超限</summary>
        public const int TooManyRequests = 429;

        /// <summary>服务端内部错误</summary>
        public const int Internal = 500;
    }
}
