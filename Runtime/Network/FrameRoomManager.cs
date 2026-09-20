using System;
using System.Collections.Generic;

namespace CloverEngine
{
    /// <summary>
    /// 帧同步房间管理器：管理单个帧同步房间的完整生命周期。
    ///
    /// 引擎层职责：
    ///   - 注册 C2S / 推送消息处理器
    ///   - 维护房间状态（ID、帧号、FPS、玩家列表、运行状态）
    ///   - 通过事件发布通知业务层（OnFrame / OnClosed / OnTakeover）
    ///
    /// 业务层职责：
    ///   - 通过 Configure() 注入业务消息号
    ///   - 通过 CreateRoomAsync / JoinRoomAsync / SendInput 等方法发起操作
    ///   - 订阅回调处理帧数据（OnFrame）
    ///
    /// <para>
    /// 【未接线】引擎内部（Runtime/Editor/Tests）当前无调用方：本类为业务侧入口
    /// （SetReadyAsync / SnapshotAsync / RecoveryAsync / TakeoverRecoveryAsync 等 C2S 方法
    /// 注册就绪但引擎内从不触发）——调用与否由业务决定，不要按「无人使用」删除。
    /// </para>
    ///
    /// 使用示例：
    /// <code>
    /// // 1. 配置消息号（业务初始化时调用一次）
    /// Game.FrameRoom.Configure(new FrameRoomMsgIds
    /// {
    ///     Create = 1002001,
    ///     Join = 1002002,
    ///     Leave = 1002003,
    ///     Input = 1002004,
    ///     Disconnect = 1002008,
    ///     Reconnect = 1002009,
    ///     Recovery = 1002010,
    ///     TakeoverRecovery = 1002011,
    ///     FrameSync = 3002001,
    ///     Closed = 3002002,
    ///     Takeover = 4004,
    /// });
    ///
    /// // 2. 注册回调
    /// Game.FrameRoom.OnFrame += OnFrameSync;
    /// Game.FrameRoom.OnClosed += OnRoomClosed;
    /// Game.FrameRoom.OnTakeover += OnRoomTakeover;
    ///
    /// // 3. 加入/创建房间
    /// var reply = await Game.FrameRoom.CreateRoomAsync("battle_01", 30);
    /// var joinReply = await Game.FrameRoom.JoinRoomAsync("battle_01");
    ///
    /// // 4. 每帧发送输入
    /// Game.FrameRoom.SendInput(inputPayload);
    ///
    /// // 5. 清理
    /// Game.FrameRoom.LeaveRoom();
    /// </code>
    /// </summary>
    internal class FrameRoomManager : IFrameRoom
    {
        /// <summary>消息号配置</summary>
        private FrameRoomMsgIds _msgIds;

        /// <summary>网络管理器引用（发消息 / 请求-回包）</summary>
        private INetwork _net;

        /// <summary>消息路由器引用（注册/注销推送处理器）</summary>
        private IRouter _router;

        /// <summary>当前所在房间 ID</summary>
        private string _currentRoomId;

        /// <summary>当前已完成帧号（由服务端推送驱动）</summary>
        private long _currentFrame;

        /// <summary>目标帧率</summary>
        private int _targetFps;

        /// <summary>房间内玩家列表</summary>
        private readonly List<string> _players = new();

        /// <summary>房间是否正在运行（主循环已启动）</summary>
        private bool _running;

        /// <summary>是否已配置消息号</summary>
        private bool _configured;

        /// <summary>推送处理器是否已注册在 _router 上（重复注册会让每条推送回调两次）</summary>
        private bool _handlersRegistered;

        /// <summary>
        /// 每帧推送回调：参数为 FramePush 数据（room_id, frame, fps, players 等）。
        /// 在主线程回调，业务层在此执行逻辑模拟（lockstep）。
        /// </summary>
        public event Action<Dictionary<string, object>> OnFrame;

        /// <summary>
        /// 房间关闭回调：参数为关闭原因（room_id, frame, reason）。
        /// 在主线程回调。
        /// </summary>
        public event Action<string, long, string> OnClosed;

        /// <summary>
        /// 房间隔线接管回调：参数为接管载荷动态字典。
        /// 断线重连/跨节点接管时服务端经 EMsg.PushRoomTakeover(4004) 推送 proto.ERoomTakeoverNotify：
        /// 稳定字段 room_id / node_addr / frame / hash，recovery 为 iframe.RecoveryPack 的动态 JSON。
        /// JsonUtility 无法表达动态结构，故这里统一用 MiniJson 解析为字典，
        /// 字段定义见 <see cref="ERoomTakeoverNotify"/>。业务应在此重新加入房间（或走 Reconnect 流程）。
        /// </summary>
        public event Action<Dictionary<string, object>> OnTakeover;

        /// <summary>是否已配置消息号</summary>
        public bool IsConfigured => _configured;

        /// <summary>当前所在房间 ID（未加入房间时为 null）</summary>
        public string CurrentRoomId => _currentRoomId;

        /// <summary>当前已完成帧号</summary>
        public long CurrentFrame => _currentFrame;

        /// <summary>目标帧率</summary>
        public int TargetFps => _targetFps;

        /// <summary>房间内玩家 ID 列表</summary>
        public IReadOnlyList<string> Players => _players;

        /// <summary>房间是否正在运行</summary>
        public bool IsRunning => _running;

        /// <summary>
        /// 配置业务消息号。必须在首次使用前调用一次。
        /// 若 Attach 已在 Configure 之前调用（即 CloverNet.Init 先行），
        /// Configure 会自动补注册推送处理器，业务层无需感知顺序。
        /// </summary>
        /// <param name="msgIds">消息号配置</param>
        public void Configure(FrameRoomMsgIds msgIds)
        {
            // 幂等守卫：Configure 会被业务重复调用（多处在初始化里各调一次）。
            // 直接覆盖并再次 RegisterPushHandlers 会让 OnFrame/OnClosed/OnTakeover
            // 每条被触发两次，而 Detach 只注销一份 —— 回调永远清不干净。
            if (_handlersRegistered)
            {
                Game.Logger?.Warn("FrameRoom",
                    "Configure called again: unregistering the previous push handlers first (避免重复注册)");
                UnregisterPushHandlers();
            }

            _msgIds = msgIds;
            _configured = true;

            // 如果 Attach 已先行调用，补注册推送处理器
            if (_router != null)
            {
                RegisterPushHandlers();
            }
        }

        /// <summary>
        /// 绑定网络与路由器并注册推送处理器（由 CloverNet.Init 调用，业务无需手动绑定）。
        ///
        /// 与 <see cref="Configure"/> <b>顺序无关</b>：两个方法都会检查对方是否已就绪，
        /// 由后到的一方补注册推送处理器（Attach 早于 Configure → Configure 里补；
        /// Configure 早于 Attach → Attach 里补）。未 Configure 时 Attach 只告警不报错。
        /// </summary>
        /// <param name="net">网络管理器实例（发消息 / 请求-回包）</param>
        /// <param name="router">消息路由器实例（注册/注销推送处理器）</param>
        public void Attach(INetwork net, IRouter router)
        {
            if (net == null || router == null)
            {
                Game.Logger?.Error("FrameRoom", "Attach failed: net or router is null");
                return;
            }

            // 与 WorldSyncManager / CloverSceneManager / AlertManager 一致：先 Detach。
            // 重复 Attach（重连后重建模块 / 业务二次初始化）时，旧路由器上的处理器会残留，
            // 且新路由器上再注册一份，推送被处理两次且旧的那份永远注销不掉。
            Detach();

            _net = net;
            _router = router;

            if (!_configured || _msgIds == null)
            {
                Game.Logger?.Warn("FrameRoom", "Attach: message IDs not configured, push handlers deferred until Configure()");
                return;
            }

            RegisterPushHandlers();
        }

        /// <summary>解除网络绑定并注销处理器</summary>
        public void Detach()
        {
            if (_configured && _msgIds != null && _handlersRegistered)
            {
                UnregisterPushHandlers();
            }

            _router = null;
            _net = null;
        }

        // ====================================================================
        //  推送处理器注册/注销（内部）
        // ====================================================================

        private void RegisterPushHandlers()
        {
            if (_router == null || _msgIds == null) return;
            if (_msgIds.FrameSync > 0)
                _router.OnMsg(_msgIds.FrameSync, HandleFrameSync);
            if (_msgIds.Closed > 0)
                _router.OnMsg(_msgIds.Closed, HandleClosed);
            if (_msgIds.Takeover > 0)
                _router.OnMsg(_msgIds.Takeover, HandleTakeover);
            _handlersRegistered = true;
        }

        private void UnregisterPushHandlers()
        {
            if (_router == null || _msgIds == null) return;
            if (_msgIds.FrameSync > 0)
                _router.OffMsg(_msgIds.FrameSync, HandleFrameSync);
            if (_msgIds.Closed > 0)
                _router.OffMsg(_msgIds.Closed, HandleClosed);
            if (_msgIds.Takeover > 0)
                _router.OffMsg(_msgIds.Takeover, HandleTakeover);
            _handlersRegistered = false;
        }

        // ====================================================================
        //  C2S 操作方法
        // ====================================================================

        /// <summary>
        /// 创建帧同步房间（异步）。
        /// </summary>
        /// <param name="roomId">房间 ID</param>
        /// <param name="targetFps">目标帧率（默认 30）</param>
        /// <returns>服务端回包</returns>
        public async System.Threading.Tasks.Task<FrameRoomReply> CreateRoomAsync(string roomId, int targetFps = 30)
        {
            EnsureConfigured();
            EnsureConnected();
            return await _net.Call<FrameRoomReply>(_msgIds.Create, new FrameRoomCreateRequest
            {
                room_id = roomId,
                target_fps = targetFps,
            });
        }

        /// <summary>
        /// 加入帧同步房间（异步）。
        /// 成功后自动设置 CurrentRoomId，客户端可开始发送输入。
        /// </summary>
        /// <param name="roomId">房间 ID</param>
        /// <returns>服务端回包（含 switched / owner_addr 等接管信息）</returns>
        public async System.Threading.Tasks.Task<FrameRoomJoinReply> JoinRoomAsync(string roomId)
        {
            EnsureConfigured();
            EnsureConnected();
            var reply = await _net.Call<FrameRoomJoinReply>(_msgIds.Join, new FrameRoomRequest
            {
                room_id = roomId,
            });
            if (string.IsNullOrEmpty(reply.err))
            {
                _currentRoomId = roomId;
                Game.Logger?.Info("FrameRoom", $"joined room: {roomId}");
            }
            return reply;
        }

        /// <summary>
        /// 设置准备状态（异步）。消息号由业务经 Configure 注入（<see cref="FrameRoomMsgIds.Ready"/>）。
        /// 引擎的帧同步房间没有 ready 概念，服务端通常只做记录与回显。
        /// </summary>
        /// <param name="ready">true=已准备，false=取消准备</param>
        /// <returns>服务端回包</returns>
        public async System.Threading.Tasks.Task<FrameRoomReply> SetReadyAsync(bool ready)
        {
            EnsureConfigured();
            EnsureConnected();
            if (_msgIds.Ready <= 0)
                throw new InvalidOperationException("Ready message ID not configured");
            return await _net.Call<FrameRoomReply>(_msgIds.Ready, new FrameRoomReadyRequest
            {
                room_id = _currentRoomId,
                ready = ready,
            });
        }

        /// <summary>
        /// 离开当前帧同步房间（异步）。离开后清空房间状态。
        /// </summary>
        /// <returns>服务端回包</returns>
        public async System.Threading.Tasks.Task<FrameRoomReply> LeaveRoomAsync()
        {
            EnsureConfigured();
            EnsureConnected();
            if (string.IsNullOrEmpty(_currentRoomId))
            {
                Game.Logger?.Warn("FrameRoom", "leave ignored: not in a room");
                return new FrameRoomReply { ok = true };
            }
            var req = new FrameRoomRequest { room_id = _currentRoomId };
            var reply = await _net.Call<FrameRoomReply>(_msgIds.Leave, req);
            ClearRoomState();
            return reply;
        }

        /// <summary>
        /// 发送帧输入（可靠，走 TCP）。帧号由服务端分配，客户端只需提交 payload。
        /// 常规锁步帧同步中，输入应在收到 FramePush 后、下一帧推进前提交。
        /// </summary>
        /// <param name="payload">业务自定义输入数据（序列化后的字符串或字节）</param>
        public void SendInput(string payload)
        {
            EnsureConfigured();
            EnsureConnected();
            if (string.IsNullOrEmpty(_currentRoomId))
            {
                Game.Logger?.Warn("FrameRoom", "send input ignored: not in a room");
                return;
            }
            if (_msgIds.Input <= 0)
            {
                Game.Logger?.Warn("FrameRoom", "send input ignored: Input message ID not configured");
                return;
            }
            _net.Send(_msgIds.Input, new FrameRoomInputRequest
            {
                room_id = _currentRoomId,
                payload = payload,
            });
        }

        /// <summary>
        /// 发送帧输入，指定帧号（允许客户端预发送）。
        /// MaxInputLead 由服务端控制，超前帧号会被服务端拒绝。
        /// </summary>
        /// <param name="payload">业务自定义输入数据</param>
        /// <param name="frame">目标帧号</param>
        public void SendInput(string payload, long frame)
        {
            EnsureConfigured();
            EnsureConnected();
            if (string.IsNullOrEmpty(_currentRoomId))
            {
                Game.Logger?.Warn("FrameRoom", "send input ignored: not in a room");
                return;
            }
            if (_msgIds.Input <= 0)
            {
                Game.Logger?.Warn("FrameRoom", "send input ignored: Input message ID not configured");
                return;
            }
            _net.Send(_msgIds.Input, new FrameRoomInputRequest
            {
                room_id = _currentRoomId,
                payload = payload,
                frame = frame,
            });
        }

        /// <summary>
        /// 请求房间快照（异步）：某一时刻的完整状态。
        /// </summary>
        /// <returns>服务端回包</returns>
        public async System.Threading.Tasks.Task<FrameRoomReply> SnapshotAsync()
        {
            EnsureConfigured();
            EnsureConnected();
            if (_msgIds.Snapshot <= 0)
                throw new InvalidOperationException("Snapshot message ID not configured");
            return await _net.Call<FrameRoomReply>(_msgIds.Snapshot, new FrameRoomRequest { room_id = _currentRoomId });
        }

        /// <summary>
        /// 查询房间信息（异步）：帧号、玩家数、等待状态等。
        /// </summary>
        /// <returns>服务端回包</returns>
        public async System.Threading.Tasks.Task<FrameRoomReply> InfoAsync()
        {
            EnsureConfigured();
            EnsureConnected();
            if (_msgIds.Info <= 0)
                throw new InvalidOperationException("Info message ID not configured");
            return await _net.Call<FrameRoomReply>(_msgIds.Info, new FrameRoomRequest { room_id = _currentRoomId });
        }

        /// <summary>
        /// 主动断开连接（异步）：通知服务端标记玩家断线但保留槽位。
        /// 通常在切场景或长时间挂起时调用，而非正常离开。
        /// </summary>
        public async System.Threading.Tasks.Task DisconnectAsync()
        {
            EnsureConfigured();
            EnsureConnected();
            if (_msgIds.Disconnect <= 0) return;
            if (string.IsNullOrEmpty(_currentRoomId)) return;
            await _net.Call<FrameRoomReply>(_msgIds.Disconnect, new FrameRoomRequest { room_id = _currentRoomId });
        }

        /// <summary>
        /// 重新连接房间（异步）：断线后调用，获取完整恢复包。
        /// </summary>
        /// <returns>服务端回包</returns>
        public async System.Threading.Tasks.Task<FrameRoomReply> ReconnectAsync()
        {
            EnsureConfigured();
            EnsureConnected();
            if (_msgIds.Reconnect <= 0)
                throw new InvalidOperationException("Reconnect message ID not configured");
            return await _net.Call<FrameRoomReply>(_msgIds.Reconnect, new FrameRoomRequest { room_id = _currentRoomId });
        }

        /// <summary>
        /// 请求追帧恢复（异步）：获取最近快照 + 增量帧。
        /// </summary>
        /// <returns>服务端回包</returns>
        public async System.Threading.Tasks.Task<FrameRoomReply> RecoveryAsync()
        {
            EnsureConfigured();
            EnsureConnected();
            if (_msgIds.Recovery <= 0)
                throw new InvalidOperationException("Recovery message ID not configured");
            return await _net.Call<FrameRoomReply>(_msgIds.Recovery, new FrameRoomRequest { room_id = _currentRoomId });
        }

        /// <summary>
        /// 接管后请求恢复（异步）：跨节点接管后请求帧数据恢复。
        /// </summary>
        /// <returns>服务端回包</returns>
        public async System.Threading.Tasks.Task<FrameRoomReply> TakeoverRecoveryAsync()
        {
            EnsureConfigured();
            EnsureConnected();
            if (_msgIds.TakeoverRecovery <= 0)
                throw new InvalidOperationException("TakeoverRecovery message ID not configured");
            return await _net.Call<FrameRoomReply>(_msgIds.TakeoverRecovery, new FrameRoomRequest { room_id = _currentRoomId });
        }

        // ====================================================================
        //  快捷退出（同步，异步的便捷封装）
        // ====================================================================

        /// <summary>
        /// 离开当前房间（同步便捷方法）。适用于不需要等待回包的场景。
        /// </summary>
        public void LeaveRoom()
        {
            if (string.IsNullOrEmpty(_currentRoomId)) return;
            if (_msgIds == null || _msgIds.Leave <= 0 || _net == null)
            {
                ClearRoomState();
                return;
            }

            // 观察丢弃的 Task：未连接时 LeaveRoomAsync 经 EnsureConnected 抛 InvalidOperationException，
            // 不观察会被静默吞掉、不留日志（表现为「离房失败但无迹可寻」）。
            NetworkManager.ObserveTask(LeaveRoomAsync(), "leave room");
        }

        // ====================================================================
        //  推送处理器
        // ====================================================================

        private void HandleFrameSync(NetCtx ctx)
        {
            if (ctx.Body == null || ctx.BodyLength <= 0) return;

            Dictionary<string, object> data;
            try
            {
                data = MiniJson.Parse(ctx.Body, ctx.BodyOffset, ctx.BodyLength) as Dictionary<string, object>;
            }
            catch (Exception e)
            {
                Game.Logger?.Error("FrameRoom", $"frame sync parse failed: {e.Message}");
                return;
            }
            if (data == null) return;

            // 更新本地状态
            if (data.TryGetValue("frame", out var frameObj))
                _currentFrame = Convert.ToInt64(frameObj);
            if (data.TryGetValue("fps", out var fpsObj))
                _targetFps = Convert.ToInt32(fpsObj);
            if (data.TryGetValue("player_count", out var pcObj))
                _running = true; // 有帧推送说明房间在运行

            if (data.TryGetValue("players", out var playersObj) && playersObj is Dictionary<string, object> playersDict)
            {
                _players.Clear();
                foreach (var kv in playersDict)
                    _players.Add(kv.Key);
            }

            Game.Logger?.Debug("FrameRoom", $"frame={_currentFrame} players={_players.Count}");

            try { OnFrame?.Invoke(data); }
            catch (Exception e)
            {
                Game.Logger?.Error("FrameRoom", $"OnFrame callback error: {e.Message}");
            }
        }

        private void HandleClosed(NetCtx ctx)
        {
            if (ctx.Body == null || ctx.BodyLength <= 0) return;

            Dictionary<string, object> data;
            try
            {
                data = MiniJson.Parse(ctx.Body, ctx.BodyOffset, ctx.BodyLength) as Dictionary<string, object>;
            }
            catch (Exception e)
            {
                Game.Logger?.Error("FrameRoom", $"closed parse failed: {e.Message}");
                return;
            }
            if (data == null) return;

            var roomId = data.TryGetValue("room_id", out var rid) ? rid?.ToString() : "";
            var frame = data.TryGetValue("frame", out var fObj) ? Convert.ToInt64(fObj) : 0;
            var reason = data.TryGetValue("reason", out var rObj) ? rObj?.ToString() : "";

            Game.Logger?.Info("FrameRoom", $"room closed: {roomId} frame={frame} reason={reason}");

            // 只清「关的就是当前房间」的状态：旧房间或重发的延迟关闭推送（room_id 不匹配）
            // 若照样清空，会把仍在进行的当前房间误清（后续 SendInput / 查询全部失效）。
            // 推送缺 room_id 时无法比对，按原行为视为当前房间（服务端关闭推送恒带 room_id）。
            var current = _currentRoomId;
            if (string.IsNullOrEmpty(roomId) || string.IsNullOrEmpty(current)
                || string.Equals(roomId, current, StringComparison.Ordinal))
            {
                ClearRoomState();
            }
            else
            {
                Game.Logger?.Warn("FrameRoom",
                    $"stale room-closed push ignored: closed='{roomId}', current='{current}'");
            }

            try { OnClosed?.Invoke(roomId, frame, reason); }
            catch (Exception e)
            {
                Game.Logger?.Error("FrameRoom", $"OnClosed callback error: {e.Message}");
            }
        }

        private void HandleTakeover(NetCtx ctx)
        {
            if (ctx.Body == null || ctx.BodyLength <= 0) return;

            Dictionary<string, object> data;
            try
            {
                data = MiniJson.Parse(ctx.Body, ctx.BodyOffset, ctx.BodyLength) as Dictionary<string, object>;
            }
            catch (Exception e)
            {
                Game.Logger?.Error("FrameRoom", $"takeover parse failed: {e.Message}");
                return;
            }
            if (data == null) return;

            Game.Logger?.Info("FrameRoom", $"room takeover received");

            try { OnTakeover?.Invoke(data); }
            catch (Exception e)
            {
                Game.Logger?.Error("FrameRoom", $"OnTakeover callback error: {e.Message}");
            }
        }

        // ====================================================================
        //  内部方法
        // ====================================================================

        private void ClearRoomState()
        {
            _currentRoomId = null;
            _currentFrame = 0;
            _targetFps = 0;
            _players.Clear();
            _running = false;
        }

        private void EnsureConfigured()
        {
            if (!_configured || _msgIds == null)
                throw new InvalidOperationException("FrameRoom not configured. Call Configure(msgIds) first.");
        }

        private void EnsureConnected()
        {
            if (_net == null || !_net.IsConnected)
                throw new InvalidOperationException("Network not connected.");
        }
    }
}
