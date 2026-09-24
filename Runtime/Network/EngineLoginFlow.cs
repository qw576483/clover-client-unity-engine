using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace CloverEngine
{
    /// <summary>
    /// 登录流程阶段：业务用它显示「卡在哪一步」（读 <see cref="EngineLoginFlow.Stage"/>，
    /// 或订阅 <see cref="EngineLoginFlow.StageChanged"/>）。
    /// </summary>
    public enum LoginStage
    {
        /// <summary>未启动。</summary>
        Idle = 0,
        /// <summary>等待与网关建立可靠连接。</summary>
        WaitingConnection,
        /// <summary>正在向账号服注册（多为"账号已存在"，失败不致命）。</summary>
        SigningUp,
        /// <summary>正在登录（账号服换 token → EMsg.Login）。</summary>
        LoggingIn,
        /// <summary>正在执行**业务**建角步骤（<see cref="EngineLoginFlow.Options.CreatePlayerAsync"/>）。</summary>
        CreatingPlayer,
        /// <summary>正在执行**业务**进图步骤（<see cref="EngineLoginFlow.Options.EnterMapAsync"/>）。</summary>
        EnteringMap,
        /// <summary>全部就绪（已登录，且业务步骤都成功）。</summary>
        Ready,
        /// <summary>失败（原因见 <see cref="EngineLoginFlow.LastError"/>）。</summary>
        Failed,
    }

    /// <summary>
    /// 引擎登录流程：把「等连接 → 注册 → 账号服换 token → EMsg.Login → SetupSession →（业务建角）
    /// →（业务进图）→ 断线后自动重新登录 / 会话恢复后自动重新进图」这条链路固化成引擎组件。
    ///
    /// 定位：把登录链（账号服 HTTP 换 token + 长连接登录）固化成引擎组件；其中两处判断
    /// 写错的表现都是**静默**的：
    /// <list type="number">
    /// <item><b>重入保护</b>：<c>Start</c> 与"重连成功"两条路径都会触发登录，
    /// 不防重入就会对同一账号登录两次 —— 连接 owner 绑定被覆盖，旧连接变成 unauthenticated 仍在发消息；</item>
    /// <item><b>重登判据</b>：只有「登录过、但**本连接**还没绑定会话」才该重新登录；
    /// 首次连接由业务显式 <see cref="Start"/> 触发，否则会重复登录。</item>
    /// </list>
    ///
    /// 边界（引擎不做的部分）：建角 / 进图是**业务消息号与业务协议**，引擎不认识它们，
    /// 所以以回调形式交给业务（<see cref="Options.CreatePlayerAsync"/> /
    /// <see cref="Options.EnterMapAsync"/>）；业务在回调里用自己的消息号与回包类型调
    /// <c>Game.Net.Call&lt;自己的回包&gt;</c>，只把成败返回给本流程。
    ///
    /// 会话恢复（<c>EMsg.ResumeSession</c>）与重连退避由 <c>NetworkManager</c> 自动完成，
    /// 本流程只负责恢复成功之后**重新进图**（引擎不猜业务进的是哪个图）。
    ///
    /// ★ 与上行闸门（<see cref="INetwork.SendEnabled"/>）的关系：本流程**不动**闸门 ——
    /// 登录链本身就要发送，闸门是留给业务表达"未进图 / 切场景中不发业务上行"的，两者不要互相踩。
    /// </summary>
    public sealed class EngineLoginFlow : IDisposable
    {
        /// <summary>登录流程参数。除账号外全部可选，但强烈建议给业务回调（否则流程只做到"已登录"）。</summary>
        public sealed class Options
        {
            /// <summary>账号（必填）。</summary>
            public string Account;

            /// <summary>密码（必填）。</summary>
            public string Password;

            /// <summary>服务器线路编号（0 = 默认线）。</summary>
            public int Line;

            /// <summary>账号服地址；留空表示"沿用调用方已设置的 <see cref="CloverAuth.AuthAddr"/>"。</summary>
            public string AuthAddr;

            /// <summary>等待连接的超时（秒）。超时即失败，不无限等。</summary>
            public float ConnectTimeoutSeconds = 15f;

            /// <summary>
            /// 业务建角步骤（可选）：返回 true = 成功。在自己的实现里调
            /// <c>Game.Net.Call&lt;业务回包&gt;(业务消息号, 请求体)</c>。
            /// </summary>
            public Func<Task<bool>> CreatePlayerAsync;

            /// <summary>
            /// 业务进图步骤（可选）：返回 true = 成功。同样由业务自己发消息号。
            /// 会话恢复（<c>Net.OnResumed</c>）后会被再次调用 —— 实现必须是**幂等**的
            /// （服务端进图接口通常也是幂等的：已在场景内直接回当前位置）。
            /// </summary>
            public Func<Task<bool>> EnterMapAsync;

            /// <summary>会话恢复成功后是否自动重新进图（默认 true；与 <see cref="EnterMapAsync"/> 同时存在才生效）。</summary>
            public bool AutoEnterMapOnResumed = true;
        }

        private const string Tag = "Login";

        private readonly Options _opt;

        private bool _started;
        private bool _disposed;
        private bool _loginInFlight;   // 重入保护：Start 与"重连后重登"都可能触发
        private Task<bool> _loginTask; // 在途登录任务：并发调用方 await 它拿真实结果（而非把"进行中"误判成失败）
        private bool _loggedIn;        // 登录成功过（跨断线保持，被踢 / 401 时复位）
        private bool _reloginPending;  // 登录态被终止（被踢 / 401）后置位：断线重连成功时仍要自动重登
        private bool _sessionBound;    // **本连接**是否已绑定会话（登录成功 / 恢复成功 = true）
        private bool _inMap;

        /// <summary>
        /// 本进程内「自动恢复」是否已被服务端拒绝过（EMsg.ResumeSession 失败）。
        /// 语义：被拒后 NetworkManager 仍会在每次重连时再提交一次恢复请求，但重登不能再等它
        ///（旧凭证已被判无效）——下一次连接建立后直接走重新登录；直到重新登录成功 /
        /// 恢复成功（新会话上线）才复位。
        /// </summary>
        private bool _resumeRejected;

        /// <summary>401 触发重登的冷却窗口（秒）：401 会被未认证态下的业务上行**高频**触发，
        /// 日志与重登必须一并防抖（防刷屏）。</summary>
        private const double UnauthorizedReloginCooldownSeconds = 3;

        /// <summary>最近一次 401 触发重登的 Stopwatch 时间戳（0 = 从未触发）。</summary>
        private double _lastUnauthorizedReloginTicks;

        /// <summary>恢复后进图前等待会话凭证就绪的上限（秒）。</summary>
        private const float ReenterCredentialWaitSeconds = 5f;

        /// <summary>恢复后进图等待在途登录收尾的上限（秒）。</summary>
        private const float ReenterLoginDrainWaitSeconds = 20f;

        private Action _onConnected;
        private Action _onDisconnected;
        private Action<EResumeSessionReply> _onResumed;
        private Action<string> _onResumeFailed;
        private Action<EErrorReply> _onUnauthorized;
        private Action _onKicked;

        public EngineLoginFlow(Options options)
        {
            _opt = options ?? throw new ArgumentNullException(nameof(options));
            if (string.IsNullOrEmpty(_opt.Account))
                throw new ArgumentException("Options.Account 不能为空（登录必须有账号）", nameof(options));
        }

        /// <summary>当前阶段。</summary>
        public LoginStage Stage { get; private set; } = LoginStage.Idle;

        /// <summary>是否已登录（跨断线保持 true，直到被踢 / 401）。</summary>
        public bool LoggedIn => _loggedIn;

        /// <summary>业务进图是否成功（业务可通过 <see cref="NotifyInMap"/> 主动同步）。</summary>
        public bool InMap => _inMap;

        /// <summary>最近一次失败原因（成功时为 null）。</summary>
        public string LastError { get; private set; }

        /// <summary>阶段变化通知：(阶段, 说明)。说明仅在 <see cref="LoginStage.Failed"/> 时非空。</summary>
        public event Action<LoginStage, string> StageChanged;

        /// <summary>
        /// 启动登录流程（幂等：重复调用只跑一次）。
        /// 通常在 <c>Game.Launch</c> + <c>CloverNet.Init</c> 之后调用一次。
        /// </summary>
        public void Start()
        {
            if (_disposed) return;
            if (_started) return;
            _started = true;
            Subscribe();
            // 观察丢弃的 Task：登录链内部异常必须有痕（StageChanged 已隔离，这里兜底）。
            NetworkManager.ObserveTask(ReloginAsync(), "startup login");
        }

        /// <summary>
        /// 执行一次登录（含重入保护）：Start、以及"断线重连后需要重新登录"都走这里。
        /// 返回是否走到 <see cref="LoginStage.Ready"/>（未走完业务回调时以"已登录"为准）。
        /// </summary>
        public async Task<bool> ReloginAsync()
        {
            if (_disposed) return false;
            if (_loginInFlight && _loginTask != null)
            {
                // 不把「进行中」当「失败」：等待在途登录的真实结果再返回
                // （否则 await 本方法的业务会把正在进行的登录误判为失败）。
                Game.Logger?.Info(Tag, "登录流程已在进行中，等待在途登录完成");
                return await _loginTask;
            }

            _loginInFlight = true;
            _loginTask = LoginCoreAsync();
            try
            {
                return await _loginTask;
            }
            finally
            {
                _loginInFlight = false;
                _loginTask = null;
            }
        }

        private async Task<bool> LoginCoreAsync()
        {
            if (FlowDisposed()) return false;

            SetStage(LoginStage.WaitingConnection);

            if (Game.Net == null)
            {
                Fail("网络模块未挂载（Game.Net 为空）—— 是否漏了 CloverNet.Init？");
                return false;
            }
            if (!await WaitAsync(() => Game.Net != null && Game.Net.IsConnected, _opt.ConnectTimeoutSeconds))
            {
                Fail($"等待连接超时（{_opt.ConnectTimeoutSeconds:0}s）—— 检查 server 地址与网关是否已启动");
                return false;
            }
            if (FlowDisposed()) return false;

            if (!string.IsNullOrEmpty(_opt.AuthAddr))
                CloverAuth.AuthAddr = _opt.AuthAddr;
            if (!CloverAuth.Enabled)
            {
                Fail("账号服未配置（Options.AuthAddr 为空且 CloverAuth.AuthAddr 未设置）—— 登录链路必经账号服");
                return false;
            }

            // 注册：失败不致命（绝大多数情况是"账号已存在"），继续登录。
            SetStage(LoginStage.SigningUp);
            try
            {
                await CloverAuth.SignupAsync(_opt.Account, _opt.Password);
                Game.Logger?.Info(Tag, $"注册成功 account={_opt.Account}");
            }
            catch (Exception e)
            {
                Game.Logger?.Info(Tag, $"注册未成功（多为账号已存在，继续登录）: {e.Message}");
            }

            SetStage(LoginStage.LoggingIn);
            string token;
            try
            {
                token = await CloverAuth.LoginAsync(_opt.Account, _opt.Password);
            }
            catch (Exception e)
            {
                Fail("账号服登录失败: " + e.Message);
                return false;
            }
            if (string.IsNullOrEmpty(token))
            {
                Fail("账号服未返回 token");
                return false;
            }
            if (FlowDisposed()) return false;

            try
            {
                var reply = await Game.Net.Call<ELoginReply>(EMsg.Login, new ELoginRequest { token = token });
                if (reply == null || !reply.success)
                {
                    Fail("游戏服登录失败: " + (reply?.err ?? "空回包"));
                    return false;
                }

                // Dispose 闸门必须在 SetupSession 之前：释放后不得再落会话、置登录态，
                // 也不得继续回调业务建角/进图（否则会出现"悬空完成"）。
                if (FlowDisposed()) return false;

                // 登记会话：进入恢复会话态。
                // ★ 恢复凭证**留空**：登录回包的 session_key 是会话通道加密密钥（AES-256），
                //   不是断线恢复凭证 —— 传非空值会让恢复会话 token mismatch 被踢。
                //   恢复凭证由 EPushPlayerFullSync.session_token 到达后覆盖（NetworkManager 负责），
                //   在此之前引擎不提交恢复请求。
                Game.Net.SetupSession(_opt.Account, null, _opt.Line);
                _loggedIn = true;
                _reloginPending = false;
                _sessionBound = true;
                _resumeRejected = false; // 新会话已建立：恢复到可恢复状态（下次断线继续走自动恢复）
                Game.Logger?.Info(Tag, $"已登录 account={_opt.Account} owner={reply.owner}");
            }
            catch (Exception e)
            {
                Fail("EMsg.Login 失败: " + e.Message);
                return false;
            }

            if (_opt.CreatePlayerAsync != null)
            {
                SetStage(LoginStage.CreatingPlayer);
                if (!await RunBusinessStepAsync(_opt.CreatePlayerAsync, "建角")) return false;
                if (FlowDisposed()) return false;
            }

            if (_opt.EnterMapAsync != null)
            {
                SetStage(LoginStage.EnteringMap);
                if (!await RunBusinessStepAsync(_opt.EnterMapAsync, "进图")) return false;
                if (FlowDisposed()) return false;
                _inMap = true;
            }

            if (FlowDisposed()) return false;
            SetStage(LoginStage.Ready);
            return true;
        }

        /// <summary>
        /// 在途登录的收尾闸门：已 Dispose 则中止后续步骤
        /// （不再写会话 / 不再改 Stage / 不再回调业务建角与进图）。
        /// </summary>
        private bool FlowDisposed()
        {
            if (!_disposed) return false;
            Game.Logger?.Info(Tag, "流程已释放，在途登录中止");
            return true;
        }

        private async Task<bool> RunBusinessStepAsync(Func<Task<bool>> step, string what)
        {
            try
            {
                if (await step()) return true;
                Fail($"{what}失败（业务回调返回 false，详见业务日志）");
                return false;
            }
            catch (Exception e)
            {
                Fail($"{what}异常: {e.Message}");
                return false;
            }
        }

        /// <summary>业务可在自己完成进图 / 离图后同步状态（流程只在 <see cref="Options.EnterMapAsync"/> 成功时自动置位）。</summary>
        public void NotifyInMap(bool inMap)
        {
            _inMap = inMap;
        }

        private void Subscribe()
        {
            _onConnected = () =>
            {
                Game.Logger?.Info(Tag, "网络已连接");
                // ★ 新连接建立时「本连接是否已绑定会话」从零判断（_sessionBound 已在断线时清）：
                //   ① 本次连接会由引擎自动恢复（见 WillEngineAutoResume）→ 等恢复结果：
                //      成功由 OnResumed 置位；失败由 OnResumeFailed 标记，下一次连接建立后重登。
                //      这里不抢跑登录，否则会与自动 ResumeSession 并发（重复登录覆盖会话）。
                //   ② 不走恢复且曾登录过（_loggedIn / _reloginPending）→ 必须重新登录，
                //      否则会停在「已登录」但新连接未绑定会话。
                //   ③ 首次连接（未登录过）→ 由业务显式 Start() 触发，此处不动作。
                if (WillEngineAutoResume())
                {
                    Game.Logger?.Info(Tag, "本连接将由引擎自动恢复会话，跳过重新登录");
                    return;
                }
                if (!_sessionBound && (_loggedIn || _reloginPending))
                    NetworkManager.ObserveTask(ReloginAsync(), "reconnect relogin");
            };

            _onDisconnected = () =>
            {
                _inMap = false;        // 断线即视为离图：业务据此停掉世界相关的上行
                _sessionBound = false; // 旧连接的会话绑定随断线失效：新连接的绑定由 OnResumed / 登录成功重建
                Game.Logger?.Warn(Tag, "网络已断开（引擎会自动重连 / 恢复会话）");
            };

            _onResumed = reply =>
            {
                _sessionBound = true;
                _resumeRejected = false; // 恢复成功 = 新会话已绑定：后续断线继续走自动恢复
                Game.Logger?.Info(Tag, $"会话已恢复(player={reply?.player_id}) → 重新进图");
                if (_opt.AutoEnterMapOnResumed && _opt.EnterMapAsync != null)
                    NetworkManager.ObserveTask(ReenterMapAsync(), "resume re-enter map");
            };

            _onResumeFailed = reason =>
            {
                _sessionBound = false;
                _resumeRejected = true; // 恢复被拒：下一次连接建立后不再等恢复（见 WillEngineAutoResume），直接重登
                Game.Logger?.Warn(Tag, $"会话恢复失败（{reason}）→ 等重连成功后重新登录");
            };

            _onUnauthorized = err =>
            {
                _sessionBound = false;
                _inMap = false;
                _loggedIn = false;      // 被服务端判未认证：不再是"已登录"
                _reloginPending = true; // 但流程仍应登录：本次重登若失败，后续重连成功时继续自动重登
                if (_disposed) return;

                // 401 会在"未认证态"下被业务上行**高频**触发（网关登录门禁逐条回 401）：
                // 日志与重登一并防抖——冷却窗口内只记 Debug，不重复触发（防刷屏）。
                var now = Stopwatch.GetTimestamp();
                var cooling = _lastUnauthorizedReloginTicks != 0
                    && (now - _lastUnauthorizedReloginTicks) < UnauthorizedReloginCooldownSeconds * Stopwatch.Frequency;
                if (_loginInFlight || cooling)
                {
                    Game.Logger?.Debug(Tag, $"401（{err?.err}）：重登已在进行或处于冷却，跳过重复触发");
                    return;
                }

                // 不干等下一次断线/重连：立即触发重新登录（ReloginAsync 自带重入保护，
                // 与在途登录并发时会等其收尾）——否则客户端会停在上行全被拒的未认证态。
                _lastUnauthorizedReloginTicks = now;
                Game.Logger?.Error(Tag, $"被服务端判为未认证(401)：{err?.err} → 触发重新登录", null);
                NetworkManager.ObserveTask(ReloginAsync(), "401 重新登录");
            };

            _onKicked = () =>
            {
                _sessionBound = false;
                _inMap = false;
                _loggedIn = false;      // 被踢 = 登录态终结，需重新登录（注释承诺的语义）
                _reloginPending = true; // 后续重连成功时仍应自动重登（与 401 同一口径）
                Fail("已被踢下线（会话恢复不可用，需重新登录）");
            };

            Game.Event?.On(CloverEvents.Net.OnConnected, _onConnected);
            Game.Event?.On(CloverEvents.Net.OnDisconnected, _onDisconnected);
            Game.Event?.On(CloverEvents.Net.OnResumed, _onResumed);
            Game.Event?.On(CloverEvents.Net.OnResumeFailed, _onResumeFailed);
            Game.Event?.On(CloverEvents.Net.OnUnauthorized, _onUnauthorized);
            Game.Event?.On(CloverEvents.Net.OnKicked, _onKicked);
        }

        private async Task ReenterMapAsync()
        {
            if (_disposed || _opt.EnterMapAsync == null) return;

            // 与在途登录互斥：登录链同样会写 Stage / _inMap，与重登并发时两边状态互相覆盖。
            // 轮询等其收尾，超时仅告警不阻塞。
            if (_loginInFlight)
            {
                Game.Logger?.Info(Tag, "有登录流程在途，等待其收尾后再重新进图");
                if (!await WaitAsync(() => !_loginInFlight, ReenterLoginDrainWaitSeconds))
                    Game.Logger?.Warn(Tag, "等待在途登录收尾超时，继续重新进图");
            }
            if (_disposed) return;

            // 先让出一帧：恢复回包与进图若同帧发出，会话凭证的落库任务可能还排在本帧
            // 派发队列里（盲等固定 300ms 在快机上纯浪费、慢机上也不够）。
            await Task.Yield();
            if (_disposed) return;

            // 再轮询「会话凭证就绪」，条件一满足立即继续（上限 ReenterCredentialWaitSeconds 秒）。
            if (!await WaitAsync(SessionCredentialReady, ReenterCredentialWaitSeconds))
                Game.Logger?.Warn(Tag, "等待恢复后会话凭证就绪超时，继续重新进图");
            if (_disposed) return;

            SetStage(LoginStage.EnteringMap);
            if (await RunBusinessStepAsync(_opt.EnterMapAsync, "恢复后重新进图"))
            {
                if (_disposed) return;
                _inMap = true;
                SetStage(LoginStage.Ready);
            }
        }

        /// <summary>恢复后进图前要等的东西：连接在线且会话凭证字段已落到位。</summary>
        private bool SessionCredentialReady()
        {
            if (Game.Net == null || !Game.Net.IsConnected) return false;
            var session = Game.Net.Session;
            return session != null && !string.IsNullOrEmpty(session.Token);
        }

        /// <summary>
        /// 本次连接建立后，NetworkManager 是否会自动尝试「会话恢复」（EMsg.ResumeSession）。
        /// 判据与 NetworkManager.HandleConnected 的提交条件同口径（会话处于恢复态且 token 非空），
        /// 再叠加本流程两个否决：
        ///   ① _reloginPending：被踢 / 401 后登录态已终结，旧凭证恢复无意义，必须重新登录；
        ///   ② _resumeRejected：本次进程内恢复已被拒过（服务端会踢连接），重连后直接重登。
        /// 会走恢复时不得抢跑重登 —— 否则与自动恢复并发发出两条登录/恢复请求，会话被互相覆盖。
        /// </summary>
        private bool WillEngineAutoResume()
        {
            if (_reloginPending || _resumeRejected)
                return false;

            var net = Game.Net as NetworkManager;
            return net != null && net.SessionResuming && !string.IsNullOrEmpty(net.Session?.Token);
        }

        /// <summary>停止流程并解除事件订阅（之后可再次 <see cref="Start"/>）。</summary>
        public void Stop()
        {
            if (!_started) return;
            _started = false;
            if (_onConnected != null) Game.Event?.Off(CloverEvents.Net.OnConnected, _onConnected);
            if (_onDisconnected != null) Game.Event?.Off(CloverEvents.Net.OnDisconnected, _onDisconnected);
            if (_onResumed != null) Game.Event?.Off(CloverEvents.Net.OnResumed, _onResumed);
            if (_onResumeFailed != null) Game.Event?.Off(CloverEvents.Net.OnResumeFailed, _onResumeFailed);
            if (_onUnauthorized != null) Game.Event?.Off(CloverEvents.Net.OnUnauthorized, _onUnauthorized);
            if (_onKicked != null) Game.Event?.Off(CloverEvents.Net.OnKicked, _onKicked);
            _onConnected = null;
            _onDisconnected = null;
            _onResumed = null;
            _onResumeFailed = null;
            _onUnauthorized = null;
            _onKicked = null;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
        }

        private void SetStage(LoginStage stage, string message = null)
        {
            Stage = stage;
            if (stage != LoginStage.Failed) LastError = null;
            NotifyStageChanged(stage, message);
        }

        private void Fail(string reason)
        {
            LastError = reason;
            Stage = LoginStage.Failed;
            Game.Logger?.Error(Tag, "登录失败: " + reason, null);
            NotifyStageChanged(LoginStage.Failed, reason);
        }

        /// <summary>
        /// 隔离订阅方异常地发布阶段变化：某个订阅方抛异常不得中断登录流程
        /// （与 Router / EventBus 的隔离语义一致）；逐个回调，保证后续订阅者仍收到。
        /// </summary>
        private void NotifyStageChanged(LoginStage stage, string message)
        {
            var handler = StageChanged;
            if (handler == null) return;
            foreach (var d in handler.GetInvocationList())
            {
                try { ((Action<LoginStage, string>)d).Invoke(stage, message); }
                catch (Exception e)
                {
                    Game.Logger?.Error(Tag, $"StageChanged 订阅方异常（stage={stage}）: {e.Message}", e);
                }
            }
        }

        private static async Task<bool> WaitAsync(Func<bool> condition, float timeoutSeconds)
        {
            float waited = 0f;
            var step = 100;   // ms
            while (waited < timeoutSeconds)
            {
                if (condition()) return true;
                await Task.Delay(step);
                waited += step / 1000f;
            }
            return condition();
        }
    }
}
