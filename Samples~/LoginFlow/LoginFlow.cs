using System.Collections.Generic;
using CloverEngine;
using UnityEngine;

namespace CloverEngine.Samples
{
    /// <summary>
    /// 登录流程示例（LoginFlow）：演示 CloverEngine 从引擎启动到业务数据同步的完整接入链路。
    ///
    /// <para>
    /// <b>关于目录名 <c>Samples~/</c> 的 <c>~</c></b>（与「<c>~</c> 目录不放运行时代码」的关系）：
    /// Unity 把**名字以 <c>~</c> 结尾的目录**排除在资源导入之外，这正是 UPM「可选样例」的官方机制 ——
    /// <c>package.json</c> 的 <c>samples[].path</c> 指向它，用户在 Package Manager 里点 Import 时
    /// 才把整份样例拷进 <c>Assets/</c>，从此刻起它才被编译。因此 <c>~</c> 不是"放了运行时代码的违规目录"，
    /// 而是"**只有被显式导入后才成为运行时代码**"；这条无法规避（去掉 <c>~</c> 就等于让样例成为包的一部分、
    /// 对所有使用方强制编译）。
    /// 配套要求：本目录带 <c>CloverEngine.Samples.LoginFlow.asmdef</c> —— 导入后它会作为**独立程序集**
    /// 编译，而不是混进业务的 <c>Assembly-CSharp</c>（混进去会在业务侧凭空多出一个 <c>LoginFlow</c> 组件）。
    /// </para>
    ///
    /// 流程总览：
    ///   1. Game.Launch 启动引擎（日志/设置/定时器等基础设施）；
    ///   2. CloverNet.Init 建立 TCP 连接并可选挂载 UDP 共享端点；
    ///   3. 订阅网络生命周期事件（连接/断线/被踢/会话恢复）；
    ///   4. 注册走账号服 HTTP；登录 = ① HTTP 换 token → ② EMsg.Login{token}，
    ///      成功后调用 Net.SetupSession 建立会话；
    ///   5. 订阅 Game.Sync.OnFullSync / OnData 接收玩家全量同步与增量推送；
    ///   6. 断线后由 NetworkManager 按指数退避自动重连，凭证齐备时自动走 ResumeSession 恢复会话。
    ///
    /// 使用方式：将本组件挂载到场景中任意 GameObject，配置服务器地址后进入 Play 模式，
    /// 通过屏幕按钮执行注册/登录。
    /// </summary>
    public class LoginFlow : MonoBehaviour
    {
        /// <summary>TCP 服务器地址，格式 "host:port"（网关 gateway.listen_tcp，默认 8002）。</summary>
        [Header("服务器配置")]
        public string serverAddr = "127.0.0.1:8002";

        /// <summary>
        /// UDP 共享端点地址（可选），格式 "host:port"（网关 gateway.listen_udp，默认 8003）；
        /// 留空表示不启用不可靠通道。
        /// </summary>
        public string udpAddr = "127.0.0.1:8003";

        /// <summary>
        /// 服务器线路是否走 TLS（TCP → SslStream，WS → wss://）。
        /// 必须与服务端 `gateway.tcp_tls_disabled` 相反：服务端为 false（默认）时这里为 true。
        /// 证书走系统信任链校验，引擎**不提供**跳过校验的开关（结构规则 §N7）。
        /// </summary>
        public bool useTls = true;

        /// <summary>测试账号。</summary>
        public string account = "player1";

        /// <summary>测试密码。</summary>
        public string password = "123456";

        /// <summary>
        /// 账号服 HTTP 地址（如 "http://127.0.0.1:8051"）。<b>必填</b>：
        /// 账号服是登录链路的必经依赖，注册 / 登录都必须先经它换取 token。
        /// </summary>
        [Header("账号服")]
        public string authAddr = "http://127.0.0.1:8051";

        /// <summary>登录线路编号（0 为默认线）。</summary>
        public int line;

        /// <summary>登录成功回包，内含 session_key 等会话凭证，供 UI 展示。</summary>
        private ELoginReply _loginReply;

        /// <summary>最近一条状态信息，用于 OnGUI 展示。</summary>
        private string _status = "未连接";

        /// <summary>
        /// 组件已销毁（OnDestroy 置位）。async void 的 await 续体在组件销毁后仍会回到主线程执行，
        /// 而 OnDestroy 已调 Game.Shutdown() 把 Game.Net 置空 —— 续体必须据此提前返回，
        /// 否则访问 Game.Net 抛 NRE（原实现被 catch 吞成「登录异常」文案，写入已无意义）。
        /// </summary>
        private bool _destroyed;

        /// <summary>
        /// 启动引擎并初始化网络与事件订阅（进入 Play 模式后自动执行一次）。
        /// </summary>
        private void Start()
        {
            // 1. 启动引擎基础设施（EngineRunner 主循环由 Launch 内部确保创建，仅 Play 模式生效）。
            Game.Launch(new GameConfig
            {
                ServerAddr = serverAddr,
                CallTimeoutSeconds = 10,
                MaxReconnectCount = 5,
                UseTls = useTls,
            });

            // 账号服（必填）：注册 / 登录统一先走 HTTP 换取 token，再 EMsg.Login{token}。
            // 未配置属配置错误 —— 明确失败并终止初始化，不静默继续。
            CloverAuth.AuthAddr = authAddr;
            if (!CloverAuth.Enabled)
            {
                Game.Logger?.Error("LoginFlow", "未配置账号服地址（authAddr 为空），注册 / 登录不可用");
                return;
            }

            // 2. 初始化网络：建立 TCP 连接；UDP 端点可选，登录成功后由网关下发绑定令牌自动绑定。
            CloverNet.Init(serverAddr, string.IsNullOrEmpty(udpAddr) ? null : udpAddr);

            // HTTP 模块由 CloverNet.Init 一并挂接，无需单独初始化。

            // 3. 订阅网络生命周期事件（NetworkManager 保证回调均在主线程）。
            // ★ 事件名一律用 CloverEvents.Net 常量，不手写字符串（Contracts.cs 的约定）：
            //   常量可被 IDE 索引、改名可编译期抓全，也不会与业务自建的同名字符串事件相撞。
            Game.Event.On(CloverEvents.Net.OnConnected, () => _status = "已连接");
            Game.Event.On(CloverEvents.Net.OnDisconnected, () => _status = "连接断开（恢复中自动重连）");
            Game.Event.On(CloverEvents.Net.OnConnectFailed, () => _status = "连接失败");
            Game.Event.On(CloverEvents.Net.OnKicked, () => _status = "被踢下线（会话失效）");
            Game.Event.On<EResumeSessionReply>(CloverEvents.Net.OnResumed,
                reply => _status = $"会话已恢复: {reply.player_id}");
            Game.Event.On<string>(CloverEvents.Net.OnResumeFailed,
                reason => _status = $"会话恢复失败: {reason}");

            // 4. 订阅业务数据同步（全量 + 增量）。
            Game.Sync.OnFullSync(OnFullSync);
            Game.Sync.OnData(OnDataSync);
        }

        /// <summary>
        /// 组件销毁时关闭引擎，释放连接、UDP 通道与后台线程资源。
        /// 先置销毁标记：在途的 async void 续体（注册/登录）据它提前返回，不再触碰已拆卸的引擎。
        /// </summary>
        private void OnDestroy()
        {
            _destroyed = true;
            Game.Shutdown();
        }

        /// <summary>
        /// 绘制操作按钮、状态信息与网络指标。
        /// </summary>
        private void OnGUI()
        {
            GUILayout.BeginArea(new Rect(20, 20, 380, 240), GUI.skin.box);
            GUILayout.Label($"[LoginFlow] {_status}");
            GUILayout.Label($"Account: {account}  Line: {line}");
            GUILayout.Label($"Connected: {Game.Net != null && Game.Net.IsConnected}  " +
                            $"UdpBound: {Game.Net != null && Game.Net.IsUdpBound}");

            GUILayout.Space(8);
            if (GUILayout.Button("Signup 注册"))
                SignupAsync();
            if (GUILayout.Button("Login 登录"))
                LoginAsync();

            GUILayout.Space(8);
            GUILayout.Label(_loginReply != null && _loginReply.success
                ? $"Logged in: player={_loginReply.owner} session=***"
                : "Not logged in");

            GUILayout.EndArea();
        }

        /// <summary>
        /// 注册流程：注册走账号服 HTTP（游戏服不再挂注册 handler），成功后自动执行登录。
        /// async void 仅用于 Unity 顶层事件回调；异常（账号服不可达 / 账号已存在等）统一在此捕获展示。
        /// </summary>
        private async void SignupAsync()
        {
            try
            {
                if (_destroyed) return;
                await CloverAuth.SignupAsync(account, password);
                // 组件在等待期间被销毁（OnDestroy 已 Game.Shutdown()）：续体不再启动登录
                if (_destroyed) return;
                _status = $"注册成功: {account}";
                LoginAsync();
            }
            catch (System.Exception ex)
            {
                if (!_destroyed) _status = $"注册异常: {ex.Message}";
            }
        }

        /// <summary>
        /// 登录流程：唯一两步链路 —— ① HTTP 向账号服换取 token；② EMsg.Login{token}。
        /// 成功后调用 Net.SetupSession 建立会话
        /// （凭证存入 Session 后，断线重连将自动携带 player_id/session_token 走恢复协议）。
        /// </summary>
        private async void LoginAsync()
        {
            try
            {
                if (_destroyed) return;
                var token = await CloverAuth.LoginAsync(account, password);
                // 组件在等待期间被销毁：Game.Shutdown() 已把 Game.Net 置空，续体不能再访问（NRE）
                if (_destroyed) return;
                // ELoginRequest.encrypt 由引擎按平台能力自动置位（支持 AES-GCM 时协商通道加密），
                // 业务无需手填；是否协商成功可读 Game.Net.IsChannelEncrypted。
                var reply = await Game.Net.Call<ELoginReply>(EMsg.Login, new ELoginRequest { token = token });
                // 回包等待期间同样可能被销毁：落状态与写会话都不应在引擎拆卸后进行
                if (_destroyed) return;
                _loginReply = reply;
                if (reply.success)
                {
                    _status = $"登录成功: {reply.owner}";
                    // 建立会话：记录 account/line，进入断线恢复态。
                    //
                    // ★ 恢复凭证**传空**（不是 reply.session_key）：登录回包的 session_key 在服务端启用
                    //   通道加密时是 **AES 通道密钥**，把它当断线重连凭证交给 SetupSession 会让恢复会话
                    //   token mismatch 被踢。恢复凭证由 EPushPlayerFullSync.session_token 到达后覆盖。
                    Game.Net.SetupSession(account, null, line);
                }
                else
                {
                    _status = $"登录失败: {reply.err}";
                }
            }
            catch (System.Exception ex)
            {
                if (!_destroyed) _status = $"登录异常: {ex.Message}";
            }
        }

        /// <summary>
        /// 玩家全量同步回调：登录成功/会话恢复后由服务端推送一次完整数据视图。
        /// </summary>
        /// <param name="data">按 kind 分桶的数据映射（kind → (type → 解析后的值)）。</param>
        /// <param name="accountData">账号级数据映射（type → 解析后的值）。</param>
        private void OnFullSync(Dictionary<string, Dictionary<string, object>> data,
            Dictionary<string, object> accountData)
        {
            _status = $"全量同步完成: {data.Count} 类数据";
            foreach (var kv in data)
                Debug.Log($"[LoginFlow] full sync kind={kv.Key} entries={kv.Value.Count}");
            if (accountData.Count > 0)
                Debug.Log($"[LoginFlow] account data entries={accountData.Count}");
        }

        /// <summary>
        /// 增量数据推送回调：服务端单条与批量形态已由 WorldSync 统一展开为逐条回调。
        /// </summary>
        /// <param name="type">数据类型标识。</param>
        /// <param name="value">解析后的数据值（MiniJson 动态对象）。</param>
        private void OnDataSync(string type, object value)
        {
            Debug.Log($"[LoginFlow] data sync: type={type}");
        }
    }
}
