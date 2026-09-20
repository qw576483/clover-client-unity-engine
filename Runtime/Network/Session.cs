using System;

namespace CloverEngine
{
    /// <summary>
    /// 会话管理实现，负责维护当前网络会话状态。
    /// 数据载体 SessionInfo 定义于 Core 程序集 Contracts.cs，本文件仅保留实现。
    /// NetworkManager 直接持有本类（无独立接口，避免出现仅一处实现的空壳契约）。
    /// </summary>
    internal class Session
    {
        private SessionInfo _info = new();
        private bool _isResuming;

        /// <summary>
        /// 获取会话信息
        /// </summary>
        public SessionInfo Info => _info;

        /// <summary>
        /// 是否处于恢复会话状态
        /// </summary>
        public bool IsResuming => _isResuming;

        /// <summary>
        /// 设置会话信息（进入恢复会话态，断线后自动携带凭证重连）
        /// </summary>
        /// <param name="account">账号标识</param>
        /// <param name="playerID">玩家唯一 ID</param>
        /// <param name="token">恢复凭证（PushPlayerFullSync 下发的 session_token；可为空，由全量同步补齐）</param>
        /// <param name="line">服务器线路编号</param>
        public void Setup(string account, string playerID, string token, int line)
        {
            _info.Account = account;
            _info.PlayerID = playerID;
            _info.Token = token;
            _info.Line = line;
            _isResuming = true;
        }

        /// <summary>
        /// 清除会话数据
        /// </summary>
        public void Clear()
        {
            _info.Reset();
            _isResuming = false;
        }
    }
}
