using System;
using System.Collections.Generic;

namespace CloverEngine
{
    /// <summary>
    /// 引擎启动所需的基础配置参数。
    /// 网络相关参数未设置（0 或 null）时，引擎使用内置默认值。
    /// </summary>
    public class GameConfig
    {
        // ───────────── 网络参数默认值（**唯一出处**） ─────────────
        //
        // 这些常量是本组网络参数默认值的唯一来源：GameConfig 自己的字段初始化器用它，
        // NetworkManager 的「未配置 / 0 值」兜底也用它（此前 NetworkManager 里另抄了一份同样的
        // 字面量，改一处漏一处就会让「配置缺省时到底用哪套默认值」两处静默分歧）。
        // ⚠️ 改这里的数值 = **行为变更**（心跳、超时、重连、保活直接影响运行表现），不要顺手调。

        /// <summary>TCP 心跳间隔默认值（毫秒）。</summary>
        public const int DefaultHeartbeatIntervalMs = 15000;

        /// <summary>TCP 连接超时默认值（毫秒）。</summary>
        public const int DefaultConnectTimeoutMs = 5000;

        /// <summary>Call 超时默认值（秒）。</summary>
        public const double DefaultCallTimeoutSeconds = 10;

        /// <summary>断线后自动重连的最大尝试次数默认值。</summary>
        public const int DefaultMaxReconnectCount = 5;

        /// <summary>裸 UDP 通道 NAT 保活周期默认值（秒）。</summary>
        public const double DefaultUdpKeepaliveIntervalSeconds = 10;

        /// <summary>WebSocket 升级路径默认值。</summary>
        public const string DefaultWsPath = "/ws";

        /// <summary>
        /// 服务器连接地址，用于建立网络连接。
        /// </summary>
        public string ServerAddr;

        /// <summary>
        /// 网关 WebSocket 接入地址（服务端 <c>gateway.listen_ws</c>，默认 8001），值为「host:port」。
        /// <para>
        /// **它属于 Web 家族，只在 WebGL 生效**：浏览器没有 BSD socket，只有 WS 可用，
        /// 因此 WebGL 工程**必须配置本项**。
        /// </para>
        /// <para>
        /// 原生平台（Standalone / 移动端）配了也不会用 —— 原生家族是 QUIC → TCP + 裸 UDP，
        /// 不会因为「TCP 连不上」就换到 WebSocket：那不是降级，是换协议家族，
        /// 会把 WS 缺失的「不可靠上行」语义一起换掉且调用方无法察觉
        /// （依据 <c>结构规则.md</c> §五 N10）。
        /// </para>
        /// </summary>
        public string WsAddr;

        /// <summary>
        /// WebSocket 升级路径（服务端 <c>gateway.ws_path</c>），默认 <c>/ws</c>。
        /// </summary>
        public string WsPath = DefaultWsPath;

        /// <summary>
        /// 是否对线路启用 TLS：TCP 走 SslStream 封装、WebSocket 走 <c>wss://</c>。
        /// 引擎**不提供**跳过证书校验的开关（硬约束 §N7），服务端证书必须来自受信任签发链。
        /// <para>
        /// **默认 true（默认加密）**：移动端（Android 9+ / iOS ATS）默认拦截明文流量，
        /// 默认明文等于"默认连不上"；加密是安全侧默认值。
        /// <b>只在服务端确实未启 TLS 时才手动关</b>（<c>gateway.tls_cert</c> 未配置 /
        /// <c>tcp_tls_disabled=true</c>），否则握手会失败。
        /// </para>
        /// </summary>
        public bool UseTls = true;

        /// <summary>
        /// 日志文件的存储目录路径。
        /// </summary>
        public string LogDir;

        /// <summary>
        /// 配置与设置文件的存储目录路径。
        /// </summary>
        public string SettingDir;

        /// <summary>
        /// 资源文件的根目录路径，用于资源加载定位。
        /// </summary>
        public string ResourceRoot;

        /// <summary>
        /// 表格数据文件的存储目录路径，用于数据表初始化。
        /// </summary>
        public string DataDir;

        /// <summary>
        /// TCP 心跳间隔（毫秒）。链路静默超过该时长时由后台发送线程自动补发 ping，
        /// 独立于主线程帧循环（游戏挂起期间心跳不停发，避免被服务端判超时踢除）。
        /// 默认 15000（与 msg-client 行为一致）；0 表示使用引擎默认值。
        /// </summary>
        public int HeartbeatIntervalMs = DefaultHeartbeatIntervalMs;

        /// <summary>
        /// TCP 连接超时（毫秒），含 DNS 解析与握手等待。
        /// 默认 5000；0 表示使用引擎默认值。
        /// </summary>
        public int ConnectTimeoutMs = DefaultConnectTimeoutMs;

        /// <summary>
        /// 请求-响应（Call）超时（秒），超时未收到配对回包则以异常结束任务。
        /// 默认 10；0 表示使用引擎默认值。
        /// </summary>
        public double CallTimeoutSeconds = DefaultCallTimeoutSeconds;

        /// <summary>
        /// 断线后自动重连的最大尝试次数（指数退避：2s/4s/8s/16s/32s…）。
        /// 次数耗尽后发布 Net.OnKicked 事件（需重新登录）。
        /// 默认 5；0 表示使用引擎默认值。
        /// </summary>
        public int MaxReconnectCount = DefaultMaxReconnectCount;

        /// <summary>
        /// 裸 UDP 通道 NAT 保活周期（秒），周期性重发绑定帧维持服务端五元组映射。
        /// 默认 10；0 表示使用引擎默认值。
        /// </summary>
        public double UdpKeepaliveIntervalSeconds = DefaultUdpKeepaliveIntervalSeconds;
    }

    /// <summary>
    /// 引擎核心入口，提供各子系统的静态访问点与生命周期管理。
    /// Core 仅负责核心子系统（日志/派发/事件/定时器/状态机/设置）的构造；
    /// 模块子系统（网络/HTTP/世界同步/数据表/本地化/资源/局域网寻服）由各模块引导类构造并挂接：
    ///   CloverNet.Init(addr, udpAddr)（含 HTTP 子系统）
    ///   CloverLan.Init()（局域网寻服；随 Launch 钩子自动挂接，业务无需调用）
    ///   CloverData.InitDataTable(dir) / CloverData.InitLocalization(dir, lang)
    ///   CloverRes.Init(root)
    /// 该拆分维持 asmdef 依赖方向（Network/Data/Resource → Core 单向），
    /// 详见 客户端待做.md 审查清单「asmdef 依赖方向」。
    /// </summary>
    public static class Game
    {
        /// <summary>
        /// 日志系统，用于输出运行时日志信息。
        /// <para>
        /// **永不为 null**：未 <see cref="Launch"/> 时指向 <c>ConsoleLogger</c>（直接写 Unity Console），
        /// Launch 后换成落盘的 <c>Logger</c>，Shutdown 后回到 Console。
        /// 这样「启动之前」与「测试里没调 Launch」两种情形下的诊断信息**不会**被
        /// <c>Game.Logger?.Xxx</c> 的 <c>?.</c> 静默吞掉 —— 而这两种情形恰恰最需要看到原因。
        /// </para>
        /// </summary>
        public static ILogger Logger { get; private set; } = ConsoleLogger.Instance;

        /// <summary>
        /// 消息派发器，用于将消息分发到目标处理器。
        /// </summary>
        public static IDispatcher Dispatcher { get; private set; }

        /// <summary>
        /// 事件总线，用于发布和订阅全局事件。
        /// </summary>
        public static IEventBus Event { get; private set; }

        /// <summary>
        /// 定时器，用于管理和执行延时与周期性任务。
        /// </summary>
        public static ITimer Timer { get; private set; }

        /// <summary>
        /// 有限状态机，用于管理游戏流程状态的切换。
        /// </summary>
        public static IFsm Fsm { get; private set; }

        /// <summary>
        /// 配置存取接口，用于持久化读写游戏设置。
        /// </summary>
        public static ISetting Setting { get; private set; }

        /// <summary>
        /// 网络管理器，负责维护与服务器的连接通信。
        /// 经 CloverNet.Init 挂接。
        /// </summary>
        public static INetwork Net { get; private set; }

        /// <summary>
        /// HTTP 请求管理器，用于发起网络请求。
        /// 随 CloverNet.Init 挂接（CloverNet.InitHttp 亦可用）。
        /// </summary>
        public static IWebRequest Http { get; private set; }

        /// <summary>
        /// 世界同步管理器，负责客户端与服务端的世界状态同步。
        /// 经 CloverNet.Init 挂接。
        /// </summary>
        public static IWorldSync Sync { get; private set; }

        /// <summary>
        /// 服务端场景投影（mmo.Scene 的客户端对应物）：玩家在服务端哪张地图、哪条分线。
        /// 经 CloverNet.Init 挂接，数据来源为 EMsg.PushSceneInfo。
        /// 注意与 Game.Scene 区分：Game.Scene 是 Unity 关卡（加载哪个画面），
        /// Game.CloverScene 是服务端场景（逻辑地图 + 分线），二者语义不同不可混用。
        /// 命名约定见 clover-doc/client/concepts/concept-naming。
        /// </summary>
        public static ICloverScene CloverScene { get; private set; }

        /// <summary>
        /// 帧同步房间管理器：管理帧同步房间的生命周期（创建/加入/离开/输入收发/帧同步回调）。
        /// 经 CloverNet.Init 挂接；消息号由业务层通过 Configure() 注入。
        /// </summary>
        public static IFrameRoom FrameRoom { get; private set; }

        /// <summary>
        /// 公告/通知推送管理器：消费 EMsg.PushAlert，分发服务端公告给业务层。
        /// 经 CloverNet.Init 挂接。
        /// </summary>
        public static IAlert Alert { get; private set; }

        /// <summary>
        /// 数据 Schema 注册/订阅管理器：提供业务层声明 Schema、订阅增量推送的能力。
        /// 经 CloverNet.Init 挂接，内部桥接 WorldSync 的 OnData/OnFullSync。
        /// 注意：与服务端 Game.Data()（三元键存储层句柄）不同义，故客户端门面名用 Schema。
        /// </summary>
        public static ISchemaRegistry Schema { get; private set; }

        /// <summary>局域网寻服：找同网段里可加入的主机。经 CloverLan.Init 挂接（Game.Launch 后自动挂）。</summary>
        public static ILanBrowser LanBrowser { get; private set; }

        /// <summary>
        /// 数据表管理器，用于加载和查询配置数据表。
        /// 经 CloverData.InitDataTable 挂接。
        /// </summary>
        public static IDataTable Table { get; private set; }

        /// <summary>
        /// 本地化管理器，用于多语言文本的查询与切换。
        /// 经 CloverData.InitLocalization 挂接。
        /// </summary>
        public static ILocalization Localization { get; private set; }

        /// <summary>
        /// 资源管理器，负责资源的加载与释放。
        /// 经 CloverRes.Init 挂接。
        /// </summary>
        public static IResourceManager Res { get; private set; }

        // 实体管理器与对象池：接口已下沉到 Core（见 EntityPool.cs），实现由 Presentation 提供。
        public static IEntityManager Entity { get; private set; }

        public static IObjectPool Pool { get; private set; }

        /// <summary>
        /// 逻辑地图：服务端权威地图在客户端的**只读投影**（本地碰撞 / 寻路查询用）。
        /// <para>
        /// 数据与服务端是**同源一份字节**（导出器一次写两份），保证两端空间事实一致 ——
        /// 否则本地能穿墙 / 出图，服务端按地图拒绝，表现成「被拽回来」（橡皮带）。
        /// 格式契约见 <c>clover-server-engine/pkg/domain/mmo/mapdata/README.md</c>。
        /// </para>
        /// <para>
        /// <b>引擎不自动加载地图</b>：哪张图、什么时候加载是业务的事，业务自己调
        /// <c>Game.Map.LoadFromResource("MapData/map-city", ...)</c> 或 <c>Load(bytes)</c>。
        /// </para>
        /// <para>
        /// 注意与 <see cref="Scene"/> 的区分：<c>Game.Scene</c> 是 **Unity 关卡**（加载哪个画面），
        /// <c>Game.Map</c> 是**逻辑地图**（空间事实）；<c>Game.CloverScene</c> 是服务端场景（地图 id + 分线）。
        /// 三者语义不同，不可混用。
        /// </para>
        /// </summary>
        public static IMapData Map { get; private set; }

        // 表现域模块：接口定义在 Core（见 PresentationContracts.cs），实现在 Presentation，
        // 由 CloverPresentation.Init 挂接（随 Game.Launch 自动完成，业务无需手动调用）。
        /// <summary>
        /// UI 管理器：窗口栈 / 层级 / 遮罩。面板预制体放 Resources/UI/{面板类型名}。
        /// </summary>
        public static IUIManager UI { get; private set; }

        /// <summary>
        /// 场景管理器：异步加载/卸载 + 加载门控 + 场景级清理。业务禁止直调 Unity SceneManager。
        /// </summary>
        public static ISceneManager Scene { get; private set; }

        /// <summary>
        /// 图集管理器：图集加载/释放 + Sprite 异步获取（带引用计数）。
        /// </summary>
        public static ISpriteAtlasManager Atlas { get; private set; }

        /// <summary>
        /// 动画管理器：基于 Unity <c>Animator</c> 的播放封装（播放 / 交叉淡入 / 参数 / 完成回调）。
        /// <para>
        /// **不含骨骼动画**：Spine / DragonBones 等第三方骨骼动画由业务自行接入 SDK
        /// （引擎的第三方库策略是「不内置、不封装」），因此这里不是「Animator 与骨骼动画统一播放器」。
        /// </para>
        /// </summary>
        public static IAnimationManager Anim { get; private set; }

        /// <summary>
        /// 声音管理器：BGM / 音效 / 人声，分组音量与静音。
        /// </summary>
        public static ISoundManager Sound { get; private set; }

        /// <summary>
        /// 相机管理器：跟随、震屏、边界约束。
        /// </summary>
        public static ICameraManager Camera { get; private set; }

        /// <summary>
        /// 画质管理器：画质档位、质量配置、帧率监控与自动降级。
        /// <para>
        /// 与 <see cref="DeviceId"/> 无关，勿混用：
        /// 本属性管「这台机器**跑得动多好的画质**」（随时可改），
        /// DeviceId 管「这台机器**是谁**」（稳定标识，用于账号 / 房间寻址）。
        /// </para>
        /// </summary>
        public static IQualityManager Quality { get; private set; }

        /// <summary>
        /// 设备唯一标识（跨会话稳定），用于匿名登录、房间寻址等需要「这台设备是谁」的场景。
        /// <para>
        /// 取值优先级：平台原生 ID → 本地持久化的随机码（首次生成后写入
        /// <see cref="Setting"/>）→ 本次会话临时码。
        /// 平台原生 ID 常不可用或不可靠（WebGL 返回固定值、模拟器镜像可能撞车、
        /// 换硬件/恢复出厂会变），因此本属性**始终**给出一个可用值，业务无需自己做兜底。
        /// </para>
        /// <para>
        /// 注意：清空本地数据 / 重装会丢失持久化码，此时会生成新标识 —— 对匿名身份而言可接受。
        /// </para>
        /// </summary>
        public static IDeviceIdProvider DeviceId { get; private set; }

        /// <summary>
        /// 输入管理器，负责键鼠/手柄/触摸的后端无关读取。
        /// 经 CloverInput.Init 挂接。
        /// </summary>
        public static IInputManager Input { get; private set; }

        /// <summary>
        /// 引擎是否处于运行状态。
        /// </summary>
        public static bool IsRunning { get; private set; }

        /// <summary>
        /// 当前引擎启动配置（Launch 时快照），网络模块据此读取心跳/超时/重连参数。
        /// </summary>
        public static GameConfig Config { get; private set; }

        private static ILogger _logger;

        /// <summary>
        /// 启动引擎，初始化核心子系统。若引擎已运行则忽略本次调用（记 Warn，走 Game.Logger 而非裸 Debug）。
        /// 运行环境下同时创建引擎驱动器（MonoBehaviour）自动驱动每帧 Tick。
        /// <para>
        /// 构造阶段任一步失败：已建资源全部回滚、静态门面复位到"未启动"（可安全重试 Launch），
        /// 错误记入日志后<b>继续向外抛出</b> —— 调用方必须知道引擎没有起来。
        /// </para>
        /// </summary>
        /// <param name="config">引擎启动配置参数（不可为 null）。</param>
        public static void Launch(GameConfig config)
        {
            if (IsRunning)
            {
                Logger?.Warn("Game", "Already running, Launch ignored");
                return;
            }

            if (config == null)
            {
                // 此时尚未建立落盘 Logger：用当前兜底 Logger（ConsoleLogger）把原因说清楚再抛出，
                // 旧实现直接 NRE 在 config.LogDir 上，异常无任何上下文。
                Logger?.Error("Game", "Launch failed: config is null (GameConfig is required)");
                throw new ArgumentNullException(nameof(config), "Game.Launch: config must not be null");
            }

            Config = config;

            // 构造阶段整体护航：任一步失败都回滚到"未启动"的干净状态。
            // 旧实现无 try/catch：中断后残留 Logger 后台线程 + 半赋值的门面 + IsRunning=false，
            // 重试 Launch 还会再建一份；反向的半拆卸（IsRunning=true 残留）则由 Shutdown 侧兜底。
            try
            {
                _logger = new Logger(config.LogDir ?? "logs");
                Logger = _logger;

                Dispatcher = new Dispatcher();
                Event = new EventBus();
                Timer = new Timer();
                Fsm = new Fsm();
                Setting = new Setting(config.SettingDir ?? "setting");
                // 设备码在 Setting 之后构造：持久化兜底要写进 Setting。
                // 它必须早于任何联网/登录（匿名身份依赖它），故放在 Launch 主流程而非懒加载。
                DeviceId = new DeviceIdProvider();

                InitFsm();

                // 创建常驻驱动器，保证无需业务侧手动调用 Tick（编辑器测试等非运行环境跳过）
                EngineRunner.Ensure();
            }
            catch (Exception ex)
            {
                AbortLaunch();
                Logger?.Error("Game", $"Launch failed: {ex.Message}", ex);
                throw;
            }

            IsRunning = true;

            // 执行上层模块注册的启动钩子（Presentation 会借此自动挂接 UI/Scene/声音/相机/设备等），
            // 因此业务在 Launch 之后即可直接使用 Game.UI / Game.Scene / …，无需额外初始化调用。
            RunLaunchHooks();

            Logger.Info("Game", "Engine launched");
        }

        /// <summary>
        /// Launch 失败时的回滚：把已建资源与静态门面复位到"未启动"。
        /// <para>回滚本身绝不放任异常逃逸（会掩盖原始失败原因）；每一步单独兜底并留痕。</para>
        /// </summary>
        private static void AbortLaunch()
        {
            try { Timer?.StopAll(); } catch (Exception ex) { LogRollback("Timer.StopAll", ex); }
            try { Event?.OffAll(); } catch (Exception ex) { LogRollback("Event.OffAll", ex); }
            try { (_logger as Logger)?.Dispose(); } catch (Exception ex) { LogRollback("Logger.Dispose", ex); }

            _logger = null;
            Logger = ConsoleLogger.Instance;

            Dispatcher = null;
            Event = null;
            Timer = null;
            Fsm = null;
            Setting = null;
            DeviceId = null;

            IsRunning = false;
        }

        private static void LogRollback(string step, Exception ex)
        {
            Logger?.Warn("Game", $"launch rollback: {step} failed: {ex.Message}");
        }

        /// <summary>
        /// 挂接网络管理器实例（由 CloverNet.Init 调用，业务侧无需手动调用）。
        /// </summary>
        /// <param name="net">网络管理器实现。</param>
        public static void AttachNetwork(INetwork net)
        {
            Net = net;
        }

        /// <summary>
        /// 挂接世界同步管理器实例（由 CloverNet.Init 调用，业务侧无需手动调用）。
        /// </summary>
        /// <param name="sync">世界同步管理器实现。</param>
        public static void AttachWorldSync(IWorldSync sync)
        {
            Sync = sync;
        }

        /// <summary>
        /// 挂接服务端场景投影（由 CloverNet.Init 调用，业务侧无需手动调用）。
        /// </summary>
        /// <param name="cloverScene">服务端场景投影实现。</param>
        public static void AttachCloverScene(ICloverScene cloverScene)
        {
            CloverScene = cloverScene;
        }

        /// <summary>
        /// 挂接帧同步房间管理器（由 CloverNet.Init 调用，业务侧无需手动调用）。
        /// </summary>
        /// <param name="frameRoom">帧同步房间管理器实现。</param>
        public static void AttachFrameRoom(IFrameRoom frameRoom)
        {
            FrameRoom = frameRoom;
        }

        /// <summary>
        /// 挂接公告推送管理器（由 CloverNet.Init 调用，业务侧无需手动调用）。
        /// </summary>
        public static void AttachAlert(IAlert alert)
        {
            Alert = alert;
        }

        /// <summary>
        /// 挂接数据 Schema 注册/订阅管理器（由 CloverNet.Init 调用，业务侧无需手动调用）。
        /// </summary>
        public static void AttachSchema(ISchemaRegistry schema)
        {
            Schema = schema;
        }

        /// <summary>
        /// 挂接局域网寻服实现（由 CloverLan.Init 调用，业务侧无需手动调用）。
        /// </summary>
        /// <param name="browser">局域网寻服实现。</param>
        public static void AttachLanBrowser(ILanBrowser browser)
        {
            LanBrowser = browser;
        }

        // ====================================================================
        //  消息路由（业务唯一入口，命名与服务端 g.OnMsg 一致）
        // ====================================================================

        private static IRouter _router;

        /// <summary>
        /// 引擎保留消息号上界：业务消息号必须 **大于** 该值（即 &gt;= 10001）。
        ///
        /// 与 Network 程序集的 `EMsg.InternalMsgMax` 是同一个全局约定。
        /// **为什么这里要再写一份**：依赖方向是 `Network → Core`（Core 是基石、不引用任何模块），
        /// 所以 Core 里的 `Game.OnMsg` 看不到 `EMsg`。两份常量由
        /// `Tests/Editor/MsgIdGuardTests.InternalMsgMax_CoreAndNetwork_Agree` 断言相等 ——
        /// 只改一处、漏改另一处会被测试立刻抓住，而不是等到线上"消息永远不来"。
        /// </summary>
        public const uint InternalMsgMax = 10000;

        /// <summary>
        /// 挂接消息路由器（由 CloverNet.Init 调用，业务侧无需手动调用）。
        /// </summary>
        /// <param name="router">路由器实现（NetworkManager）。</param>
        public static void AttachRouter(IRouter router)
        {
            _router = router;
        }

        /// <summary>
        /// 注册消息处理器（**业务唯一入口**，与服务端 g.OnMsg 同名）。
        /// Game.Net 上不再暴露 OnMsg，避免「两个入口」。
        /// </summary>
        /// <param name="msgID">消息 ID</param>
        /// <param name="handler">消息处理器</param>
        public static void OnMsg(uint msgID, MsgHandler handler)
        {
            // ★ 引擎保留段守卫：禁止业务占用 msgID <= InternalMsgMax。
            //
            // 为什么**只挡业务入口、不挡 Router.OnMsg**：引擎自己必须注册保留段消息 ——
            // `EMsg.PushPlayerFullSync=4001` / `PushAlert=4002` / `PushDataSync=4003` /
            // `PushRoomTakeover=4004` / `PushSceneInfo=4005` 全在 [1,10000] 内，
            // 走的是 `_router.OnMsg` 直连。若在 Router 上拦，会把引擎自己的监听一起打断。
            //
            // 为什么"打日志 + 拒绝"而不是抛异常：与服务端 `Logic.OnMsg` 对保留段直接 panic 对齐语义，
            // 但客户端崩掉比写错常量的代价更大；**绝不能静默忽略** —— 静默会让业务以为注册成功，
            // 然后表现为"消息永远不来"，是最难查的一类问题。
            if (msgID <= InternalMsgMax)
            {
                Logger?.Error("Game",
                    $"拒绝注册引擎保留段消息 msgID={msgID}（引擎占用 [1,{InternalMsgMax}]）。" +
                    $"业务消息号必须 >= {InternalMsgMax + 1}");
                return;
            }

            if (_router == null)
            {
                // CloverNet.Init 之前注册：旧实现 _router?.OnMsg 静默丢弃，业务以为注册成功，
                // 最终表现为"消息永远不来"；这里必须报错（与上面保留段拒绝的行为一致）。
                Logger?.Error("Game",
                    $"OnMsg({msgID}) rejected: router not attached yet - call CloverNet.Init(...) before registering message handlers");
                return;
            }
            _router.OnMsg(msgID, handler);
        }

        /// <summary>
        /// 注销指定消息号的全部处理器。
        /// </summary>
        /// <param name="msgID">消息 ID</param>
        public static void OffMsg(uint msgID)
        {
            _router?.OffMsg(msgID);
        }

        /// <summary>
        /// 注销指定消息号的特定处理器（传回注册时的同一 handler 引用）。
        /// </summary>
        /// <param name="msgID">消息 ID</param>
        /// <param name="handler">要移除的处理器</param>
        public static void OffMsg(uint msgID, MsgHandler handler)
        {
            _router?.OffMsg(msgID, handler);
        }

        /// <summary>
        /// 挂接 HTTP 请求管理器实例（由 CloverNet.Init 调用，业务侧无需手动调用）。
        /// </summary>
        /// <param name="http">HTTP 请求管理器实现。</param>
        public static void AttachHttp(IWebRequest http)
        {
            Http = http;
        }

        /// <summary>
        /// 挂接数据表管理器实例（由 CloverData.InitDataTable 调用，业务侧无需手动调用）。
        /// </summary>
        /// <param name="table">数据表管理器实现。</param>
        public static void AttachDataTable(IDataTable table)
        {
            Table = table;
        }

        /// <summary>
        /// 挂接本地化管理器实例（由 CloverData.InitLocalization 调用，业务侧无需手动调用）。
        /// </summary>
        /// <param name="localization">本地化管理器实现。</param>
        public static void AttachLocalization(ILocalization localization)
        {
            Localization = localization;
        }

        /// <summary>
        /// 挂接资源管理器实例（由 CloverRes.Init 调用，业务侧无需手动调用）。
        /// </summary>
        /// <param name="res">资源管理器实现。</param>
        public static void AttachResource(IResourceManager res)
        {
            Res = res;
        }

        public static void AttachEntity(IEntityManager entity)
        {
            Entity = entity;
        }

        public static void AttachPool(IObjectPool pool)
        {
            Pool = pool;
        }

        /// <summary>挂接逻辑地图实例（由 CloverPresentation.Init 调用，业务侧无需手动调用）。</summary>
        public static void AttachMap(IMapData map) { Map = map; }

        /// <summary>
        /// 挂接输入管理器实例（由 CloverInput.Init 调用，业务侧无需手动调用）。
        /// </summary>
        /// <param name="input">输入管理器实现。</param>
        public static void AttachInput(IInputManager input)
        {
            Input = input;
        }

        /// <summary>挂接 UI 管理器实例（由 CloverPresentation.Init 调用，业务侧无需手动调用）。</summary>
        public static void AttachUI(IUIManager ui) { UI = ui; }

        /// <summary>挂接场景管理器实例（由 CloverPresentation.Init 调用，业务侧无需手动调用）。</summary>
        public static void AttachScene(ISceneManager scene) { Scene = scene; }

        /// <summary>挂接图集管理器实例（由 CloverPresentation.Init 调用，业务侧无需手动调用）。</summary>
        public static void AttachAtlas(ISpriteAtlasManager atlas) { Atlas = atlas; }

        /// <summary>挂接动画管理器实例（由 CloverPresentation.Init 调用，业务侧无需手动调用）。</summary>
        public static void AttachAnimation(IAnimationManager anim) { Anim = anim; }

        /// <summary>挂接声音管理器实例（由 CloverPresentation.Init 调用，业务侧无需手动调用）。</summary>
        public static void AttachSound(ISoundManager sound) { Sound = sound; }

        /// <summary>挂接相机管理器实例（由 CloverPresentation.Init 调用，业务侧无需手动调用）。</summary>
        public static void AttachCamera(ICameraManager camera) { Camera = camera; }

        /// <summary>挂接画质管理器实例（由 CloverPresentation.Init 调用，业务侧无需手动调用）。</summary>
        public static void AttachQuality(IQualityManager quality) { Quality = quality; }

        /// <summary>
        /// 注册引擎启动钩子：Game.Launch 完成核心子系统初始化后自动执行。
        /// 供上层模块（如 Presentation）把自己挂接到门面，避免 Core 反向依赖上层程序集。
        /// 同一 key 重复注册会覆盖（幂等），因此可安全配合 RuntimeInitializeOnLoadMethod 使用。
        /// </summary>
        /// <param name="key">注册方唯一标识，重复注册时用于覆盖。</param>
        /// <param name="hook">启动时执行的回调。</param>
        public static void RegisterLaunchHook(string key, Action hook)
        {
            if (string.IsNullOrEmpty(key) || hook == null) return;
            _launchHooks[key] = hook;
        }

        private static readonly Dictionary<string, Action> _launchHooks = new();

        /// <summary>
        /// 执行已注册的启动钩子。单个钩子抛异常不影响其余钩子，仅记录错误日志。
        /// <para>
        /// 先对 <c>Values</c> 做快照再遍历：钩子内再调 <see cref="RegisterLaunchHook"/> 会修改字典，
        /// 直接 foreach 会抛 InvalidOperationException —— 旧实现这样使 Launch 半途中断
        /// （而此时 <c>IsRunning</c> 已为 true，后续 Launch 全被 "Already running" 早退）。
        /// </para>
        /// </summary>
        private static void RunLaunchHooks()
        {
            var hooks = new List<Action>(_launchHooks.Values);
            foreach (var hook in hooks)
            {
                try { hook(); }
                catch (Exception ex)
                {
                    Logger?.Error("Game", $"Launch hook failed: {ex.Message}", ex);
                }
            }
        }

        /// <summary>
        /// 引擎每帧驱动入口，按顺序刷新输入、消息派发、定时器、状态机及网络同步。
        /// 引擎未运行时调用无效。
        /// <para>
        /// 每个子系统单独隔离：任一子系统 Tick 抛异常只记日志、不中断本帧后续 ——
        /// 旧实现无任何隔离，<c>Fsm.OnTick</c> 一类异常会让 Net/Sync/Res/UI/Anim/Camera/Quality 全部不执行。
        /// </para>
        /// </summary>
        /// <param name="dt">自上一帧经过的时间（秒）。</param>
        public static void Tick(float dt)
        {
            if (!IsRunning) return;

            try { Input?.Tick(); } catch (Exception ex) { ReportTickFault("Input", ex); }
            try { Dispatcher?.Flush(); } catch (Exception ex) { ReportTickFault("Dispatcher", ex); }
            try { Timer?.Tick(dt); } catch (Exception ex) { ReportTickFault("Timer", ex); }
            try { Fsm?.Tick(dt); } catch (Exception ex) { ReportTickFault("Fsm", ex); }
            try { Net?.Tick(); } catch (Exception ex) { ReportTickFault("Net", ex); }
            try { Sync?.Tick(dt); } catch (Exception ex) { ReportTickFault("Sync", ex); }
            // 资源在表现域之前驱动：在途加载的进度与热更下载都在这里推进，
            // 本帧加载完的资源能赶上同一个 Tick 里的表现域刷新。
            try { Res?.Tick(dt); } catch (Exception ex) { ReportTickFault("Res", ex); }
            try { UI?.Tick(dt); } catch (Exception ex) { ReportTickFault("UI", ex); }
            try { Anim?.Tick(dt); } catch (Exception ex) { ReportTickFault("Anim", ex); }
            try { Camera?.Tick(dt); } catch (Exception ex) { ReportTickFault("Camera", ex); }
            try { Quality?.Tick(dt); } catch (Exception ex) { ReportTickFault("Quality", ex); }
        }

        /// <summary>Tick 各步骤的连续失败计数（防刷屏：每步首次必报，此后每 100 次报一次）。</summary>
        private static readonly Dictionary<string, int> _tickFaults = new();

        private static void ReportTickFault(string step, Exception ex)
        {
            _tickFaults.TryGetValue(step, out var n);
            n++;
            _tickFaults[step] = n;
            if (n == 1 || n % 100 == 0)
            {
                Logger?.Error("Game", $"Tick step '{step}' failed (count={n}): {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 关闭引擎，断开网络连接并释放各子系统占用的资源。引擎未运行时调用无效（可重复调用，幂等）。
        /// <para>
        /// 拆卸的每一步单独隔离：任一模块抛异常都只记日志并继续拆其余模块，
        /// 收尾统一复位全部门面与 <see cref="IsRunning"/> —— 旧实现无隔离且 <c>IsRunning = false</c>
        /// 排在最后，任一模块抛异常即残留 "IsRunning=true 的半拆卸状态"，此后 Launch 全被早退、引擎再也起不来。
        /// </para>
        /// </summary>
        public static void Shutdown()
        {
            if (!IsRunning) return;

            Logger.Info("Game", "Engine shutting down");

            TeardownStep("Alert.Detach", () => Alert?.Detach());
            TeardownStep("LanBrowser.Stop", () => LanBrowser?.Stop());
            TeardownStep("FrameRoom.Detach", () => FrameRoom?.Detach());
            TeardownStep("CloverScene.Detach", () => CloverScene?.Detach());
            TeardownStep("Sync.Detach", () => Sync?.Detach());
            TeardownStep("Net.Disconnect", () => Net?.Disconnect());
            TeardownStep("Http.Dispose", () => Http?.Dispose());
            TeardownStep("Timer.StopAll", () => Timer?.StopAll());
            TeardownStep("Event.OffAll", () => Event?.OffAll());

            // 表现域清理：先释放图集引用（内部会回调 Res.Release），
            // 再销毁声音/UI 自建的常驻 GameObject，最后统一卸载资源。
            TeardownStep("Atlas.UnloadAll", () => Atlas?.UnloadAll());
            TeardownStep("Sound.Dispose", () => Sound?.Dispose());
            TeardownStep("UI.Dispose", () => UI?.Dispose());

            // 先取消在途的热更下载/清单请求，再卸载：不取消的话请求对象与回调引用会残留到进程回收
            //（既不释放也不回调），而 UnloadAll 也拿一个"还在下载"的状态没办法。
            TeardownStep("Res.CancelUpdate", () => Res?.CancelUpdate());
            TeardownStep("Res.UnloadAll", () => Res?.UnloadAll());
            TeardownStep("Pool.ClearAll", () => Pool?.ClearAll());

            // 设置落盘：不显式 Save 的话，最后一次 Set 的配置只留在内存里（标记为脏），
            // 进程退出即静默丢失 —— 旧实现从不 Save。
            TeardownStep("Setting.Save", () => Setting?.Save());

            TeardownStep("Logger.Dispose", () => (_logger as Logger)?.Dispose());
            _logger = null;
            // 回到 Console 兜底：Shutdown 之后仍会有诊断输出（重连收尾、资源卸载告警、断开回调异常等），
            // 让它们继续可见，而不是因为 Logger 变成 null 就凭空消失。
            Logger = ConsoleLogger.Instance;

            // 模块子系统同样必须置空。它们的 Init 都带「已初始化则早退」守卫
            // （CloverNet.Init / CloverData.InitDataTable / InitLocalization / CloverRes.Init），
            // 不置空会让 Shutdown → Launch 的再次初始化**全部静默失效**：
            // 网络、HTTP、配表、本地化、资源一个都挂不上，且只留一条 "already initialized" 警告。
            Net = null;
            Http = null;
            Sync = null;
            Table = null;
            Localization = null;
            Res = null;

            Entity = null;
            Pool = null;
            Map = null;
            Alert = null;
            LanBrowser = null;
            Schema = null;
            CloverScene = null;
            _router = null;
            FrameRoom = null;
            Input = null;
            UI = null;
            Scene = null;
            Atlas = null;
            Anim = null;
            Sound = null;
            Camera = null;
            Quality = null;

            // Core 门面同样必须置空（旧实现漏了这一段）：不置空的话 Shutdown 之后这些门面仍指向
            // 旧实例 —— 业务再 Post 只会入永不 flush 的死队列，事件/定时器/状态机订阅悄悄失效。
            Dispatcher = null;
            Event = null;
            Timer = null;
            Fsm = null;
            Setting = null;
            DeviceId = null;

            IsRunning = false;
        }

        /// <summary>Shutdown 拆卸步骤的异常隔离：单步失败只记日志、继续拆其余模块。</summary>
        private static void TeardownStep(string step, Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                try
                {
                    Logger?.Warn("Game", $"Shutdown step '{step}' failed: {ex.Message}");
                }
                catch
                {
                    // 日志通道本身也失败（如编辑器退出时 Debug API 抛异常）：放弃上报。
                    // 绝不能让这里再抛 —— 否则整个 Shutdown 中断，恰好落回"半拆卸"的老问题。
                }
            }
        }

        /// <summary>
        /// 注册引擎内置流程状态与状态迁移（Launching → Logging → MainCity → Battle…）。
        /// </summary>
        private static void InitFsm()
        {
            Fsm.RegisterState("Launching");
            Fsm.RegisterState("CheckingUpdate");
            Fsm.RegisterState("Logging");
            Fsm.RegisterState("MainCity");
            Fsm.RegisterState("Battle");
            Fsm.RegisterState("Disconnected");

            Fsm.AddTransition("UpdateDone", "Logging");
            Fsm.AddTransition("LoginSuccess", "MainCity");
            Fsm.AddTransition("EnterBattle", "Battle");
            Fsm.AddTransition("BattleEnd", "MainCity");
            Fsm.AddTransition("Disconnected", "Disconnected");
            Fsm.AddTransition("Reconnected", "MainCity");

            Fsm.Force("Launching");
        }
    }
}
