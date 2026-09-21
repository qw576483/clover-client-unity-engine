# Clover Unity 客户端引擎 索引

> 一个开箱即用的 Unity C# 客户端引擎（UPM 包），结构参考 GameFramework / ET 等成熟客户端框架的**能力域划分**。
> **结构铁律**见 [`结构规则.md`](结构规则.md)；本文只回答「有什么、在哪、怎么读」。

---

## 0. 怎么读这套文档

| 你想了解 | 去看 |
|---|---|
| 结构铁律：目录归属、依赖方向、命名、契约、评审口令（**最该先读**） | [`结构规则.md`](结构规则.md) |
| 每个能力域 / 模块能干什么 | 本文 §3 |
| 包结构与程序集 | 本文 §1、§2 |
| 网络与会话契约（N1–N13）、通用硬约束（G1–G13） | [`结构规则.md`](结构规则.md) §五 |
| 具体某个模块的 API | 各模块代码注释（`Runtime/**` 不逐模块放 README，实现注释即文档） |
| 服务端能力基线与缺口 | 服务端仓库 `clover-server-engine`（其 `结构规则.md`） |

> **与服务端的关系**：对齐只发生在两个层面 ——
> ① **API 语义**：`OnMsg` / `On` / `Timer.After/Every` / `Fsm.Trigger` 拼写与语义一致；
> ② **网络协议**：帧格式、EMsg 消息号、ObjectID 位布局逐字节一致。
> 除此之外**不镜像服务端目录结构**，客户端按自己的能力域组织。

---

## 1. 目录总览

```text
com.clover.unity-engine/
├── Runtime/
│   ├── Core/         基础域：Game / Event / Timer / Fsm / Dispatcher / Logger / LogThrottle
│   │                 / LogBuffer / Setting / Json / DeviceId / Rng / AStar / IsoLayout / Dir8 / Input，
│   │                 以及**全部跨模块契约**（含协议载体类型）
│   ├── Data/         数据域：CloverData / DataTable / Localization / CloverTable（读打表产物）/ FileSlotStore（一槽一文件）
│   ├── Network/      网络域：Network / WebRequest / WorldSync / CloverAuth / SchemaRegistryManager
│   │                 / Lan（局域网寻服：UDP 旁路发现，子目录 Runtime/Network/Lan/）
│   │                 / Quic（msquic 原生互操作，子目录 Runtime/Network/Quic/）
│   ├── Resource/     资源域：后端抽象 / Resources / AssetBundle / 清单热更与下载器
│   ├── Presentation/ 表现域：Scene / Entity / ObjectPool / Map（逻辑地图）/ UI / UIWidgets
│   │                 / TextHooks（通用件文字接管点）/ SpriteAtlas / Animation / Sound / Input / Camera / Quality
│   └── Plugins/      原生插件落点（已入库 x86_64/msquic.dll 与 Android arm64-v8a / armeabi-v7a / x86_64 的 libmsquic.so；iOS 待补，QUIC 等原生件落这里）
├── Editor/           Editor 横切：Debugger（面板 / GM 控制台 / 网络模拟）
│                               MapBake（地图烘焙：Unity 关卡 → CloverMap 二进制）
│                               EditorStartScene（打开编辑器时打开启动场景）
├── Tests/            Editor（EditMode）+ PlayMode 测试
├── Samples~/         UPM 示例（LoginFlow）
├── Tools~/           工具与说明（`~` 结尾，Unity 不编译）
└── package.json
```

> **以 `~` 结尾的目录 Unity 不扫描**（`Samples~` / `Tools~`）：它们不需要 `.meta`，也不参与包内编译，
> 因此**不放包内运行时代码**。唯一例外是 UPM 样例 `Samples~/LoginFlow/`（`package.json` 的 `samples` 声明，
> 用户导入后即出现在其工程的 `Assets/` 里并参与编译，自带 `CloverEngine.Samples.LoginFlow.asmdef`）。

---

## 2. 模块总览

```
┌──────────────────────────── 业务游戏代码 ────────────────────────────┐
│                              Game 门面                               │
├──────────────┬──────────────┬──────────────┬────────────────────────┤
│   网络域      │   表现域      │   数据域      │   基础域                │
│  Network     │  Scene       │  DataTable   │  Event                 │
│  WorldSync   │  Entity      │  Setting     │  Timer                 │
│  WebRequest  │  Resource    │  Localization│  Fsm                   │
│              │  ObjectPool  │              │  Dispatcher            │
│              │  UI          │              │  Logger                │
│              │  SpriteAtlas │              │  LogThrottle           │
│              │  Animation   │              │  LogBuffer             │
│              │  Sound       │              │                        │
│              │  Input       │              │                        │
│              │  Camera      │              │                        │
│              │  Quality     │              │                        │
├──────────────┴──────────────┴──────────────┴────────────────────────┤
│  Editor 横切：Debugger / MapBake / EditorStartScene（启动场景）     │
└─────────────────────────────────────────────────────────────────────┘
```

依赖规则（以 asmdef 编译期强制）：

```text
CloverEngine.Core          → []            （基础域，不依赖任何人）
CloverEngine.Data          → [Core]
CloverEngine.Network       → [Core]
CloverEngine.Resource      → [Core]
CloverEngine.Presentation  → [Core]
```

即**各模块程序集只引用 `Core`，模块之间互不引用**（`Game` 门面在 `Core`，聚合全部）。
**为什么不能互相引用、以及「必须放进 Core 的类型」判据，见 [`结构规则.md`](结构规则.md) §2.1。**

---

## 3. 能力域速查

### 3.1 网络域

#### Network（连接 / 会话 / 消息路由）

客户端引擎的底座，与服务端 `event` 内核同语义。

| 能力 | 说明 |
|---|---|
| Connection | 传输抽象：QUIC / TCP / WebSocket / RawUDP；**WebTransport 尚未实现**（见 [`结构规则.md`](结构规则.md) §五 N6 / N10）；服务端已有的传输能力必须在客户端完成对应适配，按平台选择主链和降级链；TCP 连接异步化（`ConnectAsync` + `Connecting` 过渡态），含 5s 连接超时；**帧 / 消息大小上限三条线路统一 10 MiB**（与服务端同值 —— `ClientFrame.MaxBodySize`、`TcpConnection.MaxFramePayload` 软上限 + `HardMaxFramePayload` 硬上限、`WebSocketConnection.MaxMsgPayload` + `HardMaxMsgPayload`、`QuicConnection.MaxFrameSize`；客户端帧体超限在本地 `ClientFrame.Encode` 抛 `ArgumentException` 拦下，线路层软超告警、硬超断线） |
| Session | 登录态：account / playerID（字符串）/ token / 线路 / requestID 分配 / 请求-回包配对 |
| Router | `OnMsg(msgID, handler)` 注册派发，推送（requestID==0）走这里 |
| Call 配对 | `Call<T>` 按 requestID 配对，回包 msgID 恒为 0；统一错误回包 msgID=`EMsg.Error`（body=`EErrorReply{err, code}`）→ 任务以 `CloverCallException` 结束（`Code` 为机器可读错误码，见 `ErrCode`）；`code=401` 时额外发布 `Net.OnUnauthorized`，业务据此回到登录流程；超时以 `TimeoutException` 结束（默认 10s，`GameConfig.CallTimeoutSeconds` 可配） |
| 会话恢复 | 断线重连成功后自动发送 `EMsg.ResumeSession{player_id, session_token}`；失败服务端踢除连接继续重连，次数耗尽抛 `OnKicked` |
| 重连 | session resume（不重登）、次数上限（`MaxReconnectCount` 默认 5）、指数退避（2^n 秒，封顶 60s） |
| 登录流程组件 | `EngineLoginFlow`（可选，业务显式 `Start()`）：把「等连接 → 注册 → 账号服换 token → `EMsg.Login` → `SetupSession` →（业务建角/进图回调）→ 断线自动重登」封装为引擎组件；引擎内**无自动调用方**。可读 `Stage`（`LoginStage`：Idle / WaitingConnection / SigningUp / LoggingIn / CreatingPlayer / EnteringMap / Ready / Failed）或订阅 `StageChanged` 显示「卡在哪一步」；`Options.AutoEnterMapOnResumed`（默认 true）决定会话恢复成功后是否自动重新进图；**重入保护** `_loginInFlight`（并发调用 `ReloginAsync` 会等**在途登录的真实结果**，不把「进行中」误判成失败）；`_resumeRejected` 记录"本进程内恢复已被拒" → 下次连接建立后不再等恢复、直接重登；**401 触发重登带 3s 冷却**（未认证态下业务上行会高频触发 401，日志与重登一并防抖） |
| UDP | 绑定两步：服务端经 TCP 下发 `EMsg.UDPBindGrant`（体为令牌原文）→ 客户端建 UDP socket 后经 UDP 上报 `EMsg.BindUDP`（体为令牌原文）；之后每 10 秒重报绑定帧（兼 NAT 保活），断线清理。`UDPBindGrant` 由引擎最高优先拦截，业务不可占用该消息号 |

**API 对照（“长得很像”的落点）：**

```text
服务端 Go:   g.OnMsg(msgID, func(c event.Ctx) error { ... })
客户端 C#:   Game.OnMsg(MsgDef.Xxx, ctx => { var r = ctx.Bind<XxxNotify>(); ... });   // 业务消息号；引擎推送号（4001–4005）在保留段、不可经 Game.OnMsg 注册

服务端 Go:   g.Reply(c, v)
客户端 C#:   Game.Net.Send(EMsg.Xxx, msg);            // 可靠发送
             await Game.Net.Call<XxxReply>(EMsg.Xxx, msg);  // 请求-回包（requestID 配对）
             Game.Net.SendUnreliable(EMsg.Xxx, msg);  // 非可靠（UDP 优先，回退可靠）
```

**`Ctx` 成员**（服务端 `event.Ctx` 的客户端子集）：`MsgID` / `RequestID` / `TraceID` / `Body` / `Bind<T>()`。

#### LanBrowser（局域网寻服）

找同网段里「谁开了服、能不能进」—— 只做**发现**，不做连接。

| 能力 | 说明 |
|---|---|
| 门面 | `Game.LanBrowser`（`ILanBrowser`）：`Scan(options)` → `OnHostFound` / `OnScanFinished`；`Hosts` 是最近一轮的**只读快照**（上限 64 台，同 `gateway` 去重，重复只覆盖数据不重复触发） |
| 用法三段式 | `Game.Launch` → `Game.LanBrowser.Scan()` → 玩家选一台 → `CloverNet.Init(host.Address, host.UdpAddress)`。**寻服必须早于 `CloverNet.Init`**（先寻服、选主机，再连）；引擎**不提供运行中切服**（`CloverNet.Init` 幂等，见 N1） |
| 协议 | 旁路 UDP：查询 `CLOVER-LAN-QUERY/1\|<nonce>` 广播/单播到默认端口 `47777` → 主机单播回应答 `CLOVER-LAN-REPLY/1\|{json}`。**不占 EMsg 消息号、不进 Router、不走线路族**（N13） |
| 线索与校验 | 每轮一个 32 字符 hex `nonce`，应答必须回显（防上一轮的包串味）；主机地址取报文里的 `gateway`，**不信任来源 IP**（主机可能多网卡）；单包 ≤ 512 字节 |
| 目标集合 | 回环 / 255.255.255.255 / 各网卡子网广播（按掩码算） / `ExtraTargets`；某块网卡枚举失败只跳过它，不中断整轮 |
| 线程模型 | 收包在后台线程（`CloverLan-Udp`）；`State` / `Hosts` 与两个事件一律经 `Game.Dispatcher.Post` 收敛到主线程（与 `NetworkManager.PostOrRun` 同一写法） |
| 扫描收尾 | 窗口由收包线程自判（`ReceiveTimeout=200ms`，**不依赖 `Game.Timer`** —— 它可能没被驱动）；任何提前结束路径（`Stop` / 新一轮 / 平台不支持）都**收尾一次**，调用方不会卡在“扫描中” |
| 平台 | 仅原生平台；WebGL 无 BSD socket ⇒ `IsSupported=false`（读 `LanCapabilities`），`Scan()` 打 Error + 立即 `OnScanFinished`，**禁止静默失败** |

事件（经 `Game.Event` 发布，与 C# 事件互为等价入口）：`Net.LanHostFound`（参数 `LanHostInfo`）/ `Net.LanScanFinished`。

**与服务器端「服务发现」的区别**：服务端 `internal/app/discovery.go` 是**进程之间**找服务（gateway 找 logic/auth，走 etcd）；
本能力是**客户端**在局域网里找「一台跑着服务端的主机」（走 UDP 广播）。两者不共享协议 / 端口 / 代码，名字也刻意不同。

> 边界：不做「客户端当主机 / listen server」，不做 NAT 打洞，不做自动重扫与排序评分 UI。

#### WebRequest（HTTP 短连接）

版本检查、公告、CDN 清单、日志/埋点上报等一切 HTTP(S) 请求的统一入口：GET/POST（**超时 / 重试 / 并发上限未实现**）。**不走游戏长连接，与 Network 互不干扰**。

#### WorldSync（世界镜像同步）

服务器 MMO/AOI 数据的客户端投影，业务拿到的是“已经在本地的世界”。

| 能力 | 说明 |
|---|---|
| 实体镜像 | 服务器 AOI 进出/移动/属性事件 → 本地 Entity 增删改 |
| 插值平滑 | 服务器离散位置 → 本地插值/外推平滑移动（X/Y/Z 三轴），**所有远端实体默认开启** |
| 移动预测 | **引擎不做本地预测**：只有「服务端权威 + 客户端插值」这一条路径，没有可切换的预测档位、没有回滚、没有死推。玩法确需本地预测时由业务自行实现（输入即时生效 + 服务端回执校正） |
| 快照对齐 | 登录/重连 resume 后，服务器快照**整体覆盖**本地，禁止增量硬拼 |
| 表现事件 | buff/技能/受击/飘字只做表现回放，不算数值 |
| 帧同步管道 | 房间帧同步：上行发输入，下行收帧广播按帧推进（预测回滚留给项目自研） |

### 3.2 表现域

#### Map（逻辑地图）

服务端权威地图在客户端的**只读投影**：本地碰撞与寻路查询用，数据是**服务端加载的同一份字节**。

| 能力 | 约束 |
|---|---|
| 解码 CloverMap 二进制（元数据 + 可行走位图） | 客户端**只解位图**：碰撞体与出生点是服务端的事，这里只校验段长度以保证文件完整 |
| `WalkableAt(x, z)` 空间查询 | 算法与服务端 `mapdata.WalkableAt` 逐位一致（同一份位图、同一套 floor 取整口径）—— 否则本地能穿墙、服务端拒绝 ⇒ 橡皮带 |
| 加载方式 | `Load(bytes)` 或 `LoadFromResource("MapData/xxx")`（走 `Game.Res`，不直调 Resources 原生 API） |
| **不含本地预测解算** | "输入 → 位移 → 贴墙滑动"不在引擎（§3.1「引擎不做本地预测」）：引擎只给空间事实，怎么用是业务手感 |
| 未加载时的行为 | `WalkableAt` 恒返回 true（不阻挡）：地图缺失是导出/配置问题，不该表现成"玩家被锁死" |

> 格式契约（逐字节）见 [`clover-server-engine/pkg/domain/mmo/mapdata/README.md`](https://github.com/qw576483/clover-server-engine/blob/main/pkg/domain/mmo/mapdata/README.md)；
> 导出端是 `Editor/MapBake`。三处实现（写 / 客户端读 / 服务端读）必须同步改。

#### Scene（场景管理）

| 能力 | 约束 |
|---|---|
| 异步加载 / 卸载场景，进度回调 | 业务**禁止直调** `SceneManager.LoadScene`，必须走本模块 |
| 加载门控 | `allowSceneActivation` 封装：进度 ≥ 0.9 时放行；**与资源预加载无耦合**（预加载需业务自行在 Load 前完成） |
| 场景流程 | **与流程状态机无内置联动**：切场景由业务在流程状态回调里调 `Game.Scene.Load`（登录场景→主城→战斗…） |
| 场景级清理 | 场景卸载时自动批量回收：该场景的 Entity、View、对象池实例；加载进度定时器按唯一 id 停止，**scope = 场景名** 的定时器随场景批量回收（`StopScope(sceneName)`） |
| 场景环境 | **未提供**统一挂载点：环境光 / 雾等由业务自行设置（引擎不做 `RenderSettings` / 雾封装） |

#### Entity（实体管理）

| 能力 | 说明 |
|---|---|
| Entity | 只读快照：ObjectID（位布局同服务端）+ TypeID + 所属场景（SceneGroup）；**无属性集**（属性走 WorldSync 的实体事件） |
| EntityManager | 创建/销毁/按 ID 查询/按类型遍历 |
| View 绑定 | `IEntityManager.BindView` / `GetView`（同步绑定与读取）；**异步 View 工厂 `CloverPresentation.EntityView`（`IEntityViewFactory`）已接线**（随 `CloverPresentation.Init` 创建并自动注册）：`CloverPresentation.EntityView.CreateView(objectID, EntityViewSpec.Of(...))` 后 `Game.Entity.BindView(objectID, view)` 登记，销毁由 Entity 侧统一负责 |
| 分组 | 按场景/玩法分组，随场景批量销毁 |

#### Resource（资源与热更）

| 能力 | 约束 |
|---|---|
| 异步加载 / 释放，引用计数 | 业务**禁止直调** Resources / AB / Addressables 原生 API |
| 版本热更 | 资源 + 配置热更；下载器支持断点续传、边下边玩（当前**直用 `UnityWebRequest`**，未经 `Game.Http` 统一入口） |
| 预加载 | 场景切换前按清单预加载（**与 Scene 加载门控无耦合**，需业务自行保证时序） |
| 内存水位 | 超水位按 LRU 释放未引用资源 |
| 同步取值 | `Game.Res.TryGet<T>(path)`：取**已驻留**资源（不触发加载、不阻塞、纯读）；配合 `Preload` 供"必须立刻拿到"的场景 |

#### ObjectPool（对象池）

| 池 | 能力 | 约束 |
|---|---|---|
| GameObject 池 | Spawn/Despawn（按 prefab key）、预热、容量上限（Trim 裁剪）、**空闲过期回收**（`IdleExpirySeconds`，默认 0 = 关闭）、按场景分组清理 | 战斗内特效/子弹/飘字/角色**一律走池**，禁止裸 `Instantiate` |
| 引用池 | `ReferencePool`：**纯 C# 对象复用**（`Acquire<T>()` / `Release(T)` / `Count<T>()` / `Clear<T>()` / `ClearAll()`；对象可实现 `IReferencePoolable`，在取/还时自动复位）。实现在 `Runtime/Core/EntityPool.cs` | 只复用**托管对象**（输入帧 / 事件参数 / 临时集合）；`GameObject` 一律走上面的 GameObject 池 |

#### UI

| 能力 | 说明 |
|---|---|
| UIManager | 窗口栈、5 层固定层级（Background / Normal / Popup / Top / System）、打开/关闭、Popup 互斥与遮罩；面板继承 `UIPanel` 基类即可，预制体默认放 `Resources/UI/{类名}`（可用 `CloverPresentation.PanelProvider` 换成 Addressables / AB） |
| 数据绑定 | UI 只订阅数据变更事件刷新，**不直连网络、不改数据** |
| 通用件 | Toast / 飘字 / Loading / 红点 / 确认框 / 引导遮罩高亮件 / 世界血条 `WorldHpBar`（业务自挂在实体视图根下，引擎未接门面入口）（见 `Runtime/Presentation/UIWidgets.cs`） |
| 控件工厂 | `UIFactory` 的通用 uGUI 控件（业务"用代码搭 UI"一律用它，别自己再写一套）：水平滑块 `CreateSlider` · 单行输入框 `CreateInputField` · 「◀ 值 ▶」选择行 `CreateSelector` · 开关行 `CreateToggleRow`（句柄类型 `Selector` / `ToggleRow` 也在 `Presentation` 程序集）· 进度条比例 `SetBarWidth`（**锚点宽度**口径，不用空 sprite 的 `fillAmount`）· 布局助手 `Place` / `AnchoredTopLeft` / `AnchoredBottom` / `CreateLabel` / `CreateBoxRect` / `CreateBottomLabel`；**配色 / 文案 / 字号 / 回调一律由参数传入**，引擎不含任何项目取值（见 `Runtime/Presentation/UIWidgetControls.cs`） |
| 适配 | Canvas + CanvasScaler；通用件（Toast / Loading / 确认框）内置安全区适配（`SafeAreaFitter`，internal），**业务面板需自行用 `Screen.safeArea` 处理**（控件工厂只负责摆放，不碰安全区——这条边界不变） |

#### SpriteAtlas（图片）

| 能力 | 约束 |
|---|---|
| 图集管理 | 图集加载/释放、Sprite 异步获取、引用计数；UI 图集与场景图集分离 |
| 大图 | 背景/立绘等非图集大图独立加载、LRU 释放 |
| 内存 | 贴图内存纳入 Resource 水位统一管理 |

#### Animation（动画）

| 能力 | 约束 |
|---|---|
| Animator 封装 | 字符串参数方法（SetBool / SetFloat / SetInteger / SetTrigger）、状态切换、归一化时间播放；**参数常量未集中定义**（由业务自行维护常量） |
| 骨骼动画 | **不在引擎范围**：Spine / DragonBones 由业务自行接入 SDK，引擎不做封装 |
| 动画事件 | 完成回调已实现；帧事件在业务侧挂 Unity `AnimationEvent` 接收脚本处理 |
| 技能时间轴 | **引擎不提供**：没有时间轴/帧事件表 API，业务用 `Timer` + 状态机自己编排 |

#### Sound（音效）

| 能力 | 说明 |
|---|---|
| 分组 | BGM / SFX / Voice 三组独立音量，BGM 切换淡入淡出 |
| AudioSource 池 | 固定挂常驻根（**不随实体回收**），`PlaySFXAt` 只设位置 |
| 播放闸门 | ① **缺失只报一次**：四处 `clip == null`（`PlayBGM` / `PlaySFX` / `PlaySFXAt` / `PlayVoice`）全走 `LogThrottle.WarnOnce("Sound", "missing:<path>")`，⛔ 不再是"每次调用一条裸 Warn"（高频缺失路径曾把日志刷爆）；② 两个可配闸门（挂在 `ISoundManager` 上，默认 `0` = **不限**）：`MaxPlaysPerFrame`（单帧最多真正起播几次）/ `MaxConcurrentPerClip`（同一路径**同时播放**的音源数上限，真并发口径）；超限**丢弃该次播放**（⛔ 不排队、⛔ 不打断正在播的音源）+ `LogThrottle.WarnThrottled` 限频告警。只作用于 `PlaySFX` / `PlaySFXAt` / `PlayVoice`，**BGM / 分组音量 / 淡入淡出 / `TakeSource` 池逻辑 / `Dispose` 一律不受影响**；阈值由业务下发（如 cs16 的 `CsAudioTuning`）。另：`Sound.cs` 内**已无裸 `Game.Logger?.Warn`** —— `GetAvailableSource` 的池满告警也走 `LogThrottle.WarnOnce("Sound","pool.exhausted")`，池逻辑与 `_poolExhaustedWarned` 原样保留（只换发射通道） |
| 设置持久化 | **未实现**（音量/静音仅在内存，未写入 Setting） |

#### Input（输入）

| 能力 | 说明 |
|---|---|
| 输入抽象 | 键鼠 / 触屏 / 手柄统一接口，业务面对“操作指令”而非具体设备 |
| 虚拟摇杆 / 技能键位 | **未实现**（Input 只有 MoveDirection 与按键布尔） |
| 手势 | **未实现**（无点按 / 长按 / 滑动 / 双指缩放识别） |
| 与帧同步衔接 | 帧同步模式下，操作指令即上行载荷，由 WorldSync 帧管道发送 |
| 输入锁 | `Lock/Unlock` API 已有但**无调用方**——UI 打开时不会自动屏蔽世界输入；`OnMove/OnSkill/OnJump/OnDodge/OnInteract` 等高层动作回调引擎内也**无调用方**（业务按需订阅） |

#### Camera

跟随 / 震屏 / 边界约束（`Unfollow` / `SetBounds` 暂无调用方）；**锁定目标、震屏接动画时间轴未实现**。小模块，可选引用。另提供独立组件 `CloverThirdPersonCamera`（3D 第三人称环绕机位 / 遮挡避障 / 贴脸隐藏角色），业务自行挂到相机上，不经 `Game.Camera`。另有相机侧三个**纯件**（能力下沉；同样**不经 `Game.Camera` 门面**，业务自持 / 自传配置）：`ViewBob`（第一人称视点晃动 + 落地沉降，纯逻辑类 + `ViewBobConfig` 七个数值）、`CameraMath`（`FovYFromFovX` 水平→垂直 FOV / `AimDirection` yaw,pitch→视线方向 / `Follow` 指数平滑跟随）、`LookAccumulator`（鼠标位移 → yaw/pitch 累加）。

#### Quality / DeviceId（画质与设备标识）

> 该域拆成两个模块：`Quality`（画质档位 / 预设 / 帧率监控与自动降档，门面 `Game.Quality`）与
> `DeviceId`（设备标识，三层兜底，门面 `Game.DeviceId`）。**门面上没有 `Game.Device`**，两者语义互不相干。

| 能力 | 说明 |
|---|---|
| 机型分级 | 按**内存 / 显存**（`SystemInfo.systemMemorySize` / `graphicsMemorySize`）把设备分到 高 / 中 / 低 档（**不读 CPU**） |
| 画质档位 | 每档一组参数：分辨率缩放、阴影、LOD、粒子密度、同屏人数上限、帧率目标 |
| 自动降质 | 掉帧监控（**发热监控未实现**：`CheckBatteryTemperature` 为空实现），持续掉帧自动降档 |
| 设置持久化 | 玩家手动画质选择写入 Setting |

### 3.3 数据域

| 模块 | 能力 | 约束 |
|---|---|---|
| DataTable | 策划 TSV → 由**打表工具**生成的强类型 C# 表（生成器在 [`clover-tools/table`](https://github.com/qw576483/clover-tools/blob/main/table/README.md)，不在客户端引擎内），启动加载、按 ID 查询 | 生成物禁止手改；不用反射，IL2CPP 安全 |
| Setting | 本地存档：单文件 JSON（**无设备级 / 账号级分级、无加密**）。写盘**原子替换**：先写 `settings.json.tmp` → `File.Replace`（`Setting.cs:248-258`），写一半失败只会留下 `.tmp`、不破坏既有文件；`Set` 拒绝不可 JSON 序列化的值（否则一个坏值进字典后整份配置再也落不了盘）；Load 遇损坏 json 先另存 `.corrupt` 留档、再回退默认并标脏（下次 Save 覆盖）；**目录为 null / 空串 / 非法时不再抛异常**（旧实现 `Directory.CreateDirectory("")` 会把 `Game.Launch` 打崩），退化为内存存储并 Warn（`:121-144`）；**WebGL 不建目录、不读、不写**，设置只在内存、进程结束即丢（`:111-119`）。**实现落在 `Runtime/Core/Setting.cs`** —— 能力上属数据域，但契约与门面在 `Core`，故**程序集归属 `Core`** | 替代裸 PlayerPrefs |
| Localization | 多语言文本，语言切换事件 | 文案 key 与配表同套生成（**图片多语言未实现**，需要时再加接口） |
| CloverTable | **读自家打表工具的产物**：`LoadAll(streamingAssetsDir, dataDir)`（成功 `null` / 失败**可定位错误串**）+ `Get<T>(tableName, int\|string key)`（反射填 public 字段、按 (表,类型,列) 缓存）+ `Dir` / `ResolveDir` / `RequiredTables` | ⚠️ 引擎旧入口 `CloverData.InitDataTable` 要求行类实现 `IDataRow`、**读不了打表产物** ⇒ 工程侧一律用 `CloverTable`；打表生成的 `Tables.Default.*` 强类型壳是"便捷访问层"，可继续用 |
| FileSlotStore | 「键 → 文本」的**槽位**存储，**一槽一文件**：原子写（`.tmp` → `File.Replace`）+ 损坏留档（`.corrupt` 副本）+ `List()` 枚举（**字典序**）+ `LastCorruptPath` | 与 `Setting` **互补**：`Setting` = 单文件 KV（在 `Core`），本类 = 一槽一文件（在 `Data`）。`List()` **不是插入序** ⇒ 要"创建先后"自己维护索引键（如 `clover-project-diablo2` 的 `char/index`）；`.json` 槽会校验内容可解析 |
| 服务器数据缓存 | 服务器下行同步数据（背包 / 任务 / 面板）的本地缓存。**实现在 `Network/` 的 `WorldSync` + `SchemaRegistryManager`**，不属 `Data/` | 写入唯一入口是服务器同步消息；UI 经事件订阅读取 |

### 3.4 基础域

| 模块 | 能力 | 与服务端对齐点 |
|---|---|---|
| Event | 进程内事件总线：`On(name, h)` / `On<T>(name, h)` / `OnPriority` / `Once` / `Off` / `OffAll` / `Emit(...)`——**无 `OnEvent`**；**支持 `*` / `**` 通配订阅**（`*` 匹配一段、`**` 匹配一段或多段；精确匹配的订阅先执行；退订用注册时同一个名字） | 同服务端 `event.Bus` 语义（通配是客户端额外能力） |
| Timer | `After` / `Every` / `AfterName` / `EveryName` / `StopNamed` / `StopScope` / `StopAll`（含 `*Unscaled` 变体） | **API 与服务端 `timer.Scheduler` 完全同名**；实现为主线程 `List<TimerEntry>` 全表线性遍历（非最小堆），Update 驱动 |
| Fsm | `RegisterState` / `Transition` / `Trigger` / `Force` / `Tick(dt)` / `OnChange` | **API 与服务端 `fsm.Machine` 完全同名**；游戏流程用它（**房间帧同步走 `Game.FrameRoom`，不使用 Fsm**） |
| Dispatcher | 主线程分发器：`Post(action)`，引擎每帧排空 | 后台线程回调进入主线程的**唯一通道** |
| Logger | 分级日志，文件 + Console 双写 | 文件 `logs/YYYY-MM-DD.log`（根目录无子目录），文件去 ANSI 颜色码 |
| LogThrottle | 日志降频闸门，**两种口径并存、不可互相替换**：① **时间口径** `ShouldLog(key, intervalSeconds)` / `WarnThrottled` / `ErrorThrottled` / `WarnOnce` / `ErrorOnce`（同 key 在间隔内只出第一条；空 key 恒 false + 只报一次 Warn）；② **计数口径** `ShouldLogEvery(key, everyN)` / `InfoCounted` / `WarnCounted` / `ErrorCounted`（每 key 独立计数，**第 1 次必打**、之后每 N 次一条，行尾补 `（同类第 N 次）`；空 key 归并 `"default"`；`everyN <= 1` 视为 1）。`Reset()` **一并清空两种记录**；可注入 `Clock`（`ClockSource` = Injected / Unity / Process，时钟三级永不抛） | 无对应（客户端防刷屏；高频回调里的非预期分支必须走它） |
| LogBuffer | 运行时日志**环形缓冲**：最近 N 行**只读窗口**（`Lines` + `Version`）+ 线程安全入队（挂 `Application.logMessageReceivedThreaded`）+ `Install` / `Uninstall` / `Drain` / `Push` / `Clear`（`DefaultCapacity` = 400，超过丢最旧）；补 `ILogger` **只写不读**的缺口 | 无对应（**不是第二套 logger**：只收行 / 不写行，`ILogger` 一行未动） |

---

## 4. Game 门面（启动与流程）

```text
Game.Launch(config)   // 初始化核心子系统 + 执行启动钩子（不联网 / 不登录 / 不进场景；配表、资源、网络需另调 CloverData.InitDataTable / CloverRes.Init / CloverNet.Init）
Game.Net / Game.Http  // 网络域入口
Game.Sync             // 世界镜像入口
Game.LanBrowser       // 局域网寻服入口（CloverLan.Init 挂接；寻服 → 选主机 → 再 CloverNet.Init）
Game.Scene / Game.Entity / Game.Map / Game.Res / Game.Pool
Game.UI / Game.Atlas / Game.Anim / Game.Sound / Game.Input / Game.Camera
Game.Event / Game.Timer / Game.Fsm / Game.Table / Game.Setting
LogThrottle / LogBuffer  // 静态类，直接 CloverEngine.LogThrottle.X / CloverEngine.LogBuffer.X 用，**不经 Game 门面**
                         // （日志降频闸门：时间口径 + 计数口径 / 运行时日志环形缓冲：最近 N 行只读窗口）
```

- **流程状态机**：启动 → 热更检查 → 登录 → 主城 → 战斗 … 用 `Fsm` 实现；**与 Scene 模块无内置联动**，切场景由业务在状态回调里调 `Game.Scene.Load`。
- 引擎自持隐藏 `MonoBehaviour` 宿主驱动 Update，业务无需挂脚本。
- **每帧驱动**：`Game.Tick` 统一驱动 `Input / Dispatcher / Timer / Fsm / Net / Sync / Res / UI / Anim / Camera / Quality`（`Runtime/Core/Game.cs`）。
- 生命周期事件（经 `Game.Event` 发布，事件名带 `Net.` 前缀，回调均在主线程）：
  `Net.OnConnected`（连接建立）/ `Net.OnDisconnected`（断开，恢复可用时自动重连）/
  `Net.OnConnectFailed`（从未连上：首次连接失败或地址非法）/
  `Net.OnKicked`（重连次数耗尽或无恢复凭证）/ `Net.OnResumed`（会话已恢复，参数为回包结构）/
  `Net.OnResumeFailed`（恢复被拒，参数为原因）/ `Net.PlayerFullSync`（全量同步推送，参数为解析后的字典）/
  `Net.LanHostFound`（局域网发现一台主机，参数为 `LanHostInfo`）/ `Net.LanScanFinished`（一轮局域网扫描结束）。

---

## 5. Editor 横切

### 5.1 Debugger

- 运行时统计面板：FPS / FSM 状态 / 网络状态（含 RTT）/ 资源状态。**无**「上下行流量 / 在线实体数 / 对象池命中率 / 画质档」。**仅此用途，客户端不做服务端式 Metrics 指标体系**。
- **网络区块**（读取 `NetworkManager` internal 访问器）：连接状态 / UDP 绑定状态（`IsUdpBound`）/
  会话恢复标记 / 目标地址 / **Line · Plan · Sim 三行** / 待配对 Call 数 / 最近错误回包（`EErrorReply`：`err` + **`code`** + 对应 requestID）/
  RTT 统计（最近 / EMA 平均 / 历史最大）。
- **网络区块补充**：服务端排队态（`IsQueued` / `QueueAhead` / `QueueTotal`）与通道加密态
  （`IsChannelEncrypted`）由引擎自动维护、可直接读；**调试面板当前未展示这两项**。
- **资源区块**：后端名 / 版本 / 缓存占用与水位 / 在途加载 / 已开包数 / 热更阶段与下载进度。
- **GM 指令**（5 条）：`help` / `reconnect` / `disconnect` / `clear` / `sim <延迟ms> [抖动ms] [丢包率0-1]`；
  `reconnect` 走 `Net.Reconnect()`（清退避计数立即重连）。
- **两个菜单挂载入口**（此前 `GMCommand` 与 `DebuggerWindow` 都只有实现、**无任何调用方**，「注册/写好」却无处触发）：
  `Clover/GM 控制台` → `GMConsoleWindow`（`Editor/Debugger.cs:336`，输入框把命令串交给 `GMCommand.Execute`，
  Play 模式下用；引擎未启动时面板顶部提示网络类命令会失败）；`Clover/调试面板` → `DebuggerOverlay`
  （`:412`，把 `DebuggerWindow` 装到常驻隐藏 GameObject 并驱动其 `Update`/`OnGUI`，再点一次即关闭）。
  两者都只在 Editor（Play 模式）可用，打包不含。
- 仅 Editor 程序集 + Development Build 可编译进包。

### 5.2 MapBake（地图烘焙）

`Clover/地图烘焙/` —— 把 Unity 关卡烘焙成 CloverMap 二进制（服务端与客户端同源一份）。

| 入口 | 位置 | 说明 |
|---|---|---|
| `Clover/地图烘焙/打开烘焙窗口` | `Editor/MapBake/MapBaker.cs`（菜单声明；窗口实现在 `MapBakeWindow.cs`） | 选场景、定 scene_id / 格边长 / 原点 / 尺寸 / 烘焙规则 / 输出目录；烘焙 + 回读自检。**面板与菜单走帧驱动导出**（`MapBaker.ExportInteractive`）：逐格烘焙切成每帧一块 + **可取消**进度条，不冻住编辑器（旧实现同步跑，大图下点一次冻住数分钟且无反馈）；「按场景包围盒推算」与烘焙的失败都显**错误态**红框（`MapBakeWindow.cs:70,167`） |
| `Clover/地图烘焙/导出当前场景（用已保存参数）` | `Editor/MapBake/MapBaker.cs` | 用上次面板保存的参数（存 EditorPrefs）直接导出 |
| `-executeMethod CloverEngine.Editor.MapBaker.Export` | 同上 | 命令行 / CI 入口，复用同一份配置；失败以非零退出码结束 |

**取样柱以地面为基准**：`ProbeBottomY` / `ProbeTopY` 是**相对地面顶面 `GroundTopY` 的高度**（实际世界 Y = `GroundTopY + Probe*`，且须 `ProbeBottomY > 0`；`MapBakeOptions.cs:50-62,93-99`、`MapBaker.cs:327-333`）—— 旧实现按**绝对 Y** 解释（默认 0.2~2.2），地面 Y≠0 的关卡（如地面在 10m）取样柱整根埋在地下、逐格都不与障碍相交，会烘出「全可走」空地图**且无告警**。
同批参数语义修正还有：出生点回退位置以 `GroundTopY` 定高，并把「格」按 `CellSize` 换算成米（`MapBaker.cs:459-465`；旧实现漏乘 `CellSize`，格边长 ≠1 时第二个角落到地图中间或图外，玩家一出生就被本地碰撞锁死）。

**为什么必须在 Unity 进程里做**：场景真身在 `.unity` / Prefab / Terrain / MeshCollider 里，
只有 Unity 能正确解析（地形高度、Mesh 碰撞、Prefab 变体、静态合批）；脱离 Unity 写资产解析器 = 重造 Unity。
所以形态是 Editor 工具（面板 + 菜单 + CLI），**不是独立 exe**。产物的两个读取端：
服务端 `pkg/domain/mmo/mapdata`、客户端 `Game.Map`。

**参数是业务的**：烘焙规则（什么算障碍 / 地面高度 / 出生点标记前缀）由项目在面板上给，
引擎只提供链路与中性默认值 —— 引擎里不写任何一款游戏的场景路径或关卡布局。

### 5.3 EditorStartScene（打开编辑器时打开启动场景）

`Clover/编辑器启动场景/` —— 打开编辑器（每次会话首次）把**启动场景**打开，保证点 Play 走完整启动链路。

| 项 | 内容 |
|---|---|
| 启动场景怎么定 | `EditorBuildSettings.scenes` 里**第一条 enabled** 的场景（各工程本来就声明了：cs16 / super-mario / diablo2 = `Assets/Scenes/Boot.unity`，cr = `Assets/Scenes/Main.unity`）。引擎**不写死任何场景路径**，工程侧零配置 |
| 菜单 | `Clover/编辑器启动场景/启用（打开编辑器时自动切到启动场景）`（勾选项，存 EditorPrefs、**按工程路径隔离**，默认开）；`Clover/编辑器启动场景/立即打开启动场景` |
| 实现 | `Editor/EditorStartScene.cs`（`[InitializeOnLoadMethod]` + `delayCall`；`SessionState` 保证**每次编辑器会话只切一次**，域重载不会把人拽回启动场景） |

**为什么必须这样**：编辑器按 Play 用的是**当前打开的场景**，不是 Build Settings 首项。开在关卡场景上点 Play 会整条跳过启动链路（`Game.Launch` / 流程装配都在启动场景里），现象是「点 Play 直接进关卡、缺初始化」**且不报错**。

**三条不打扰原则**（任一命中就只记日志、不动场景）：① 批处理（CI / `-executeMethod`）下不切；② 已经打开着启动场景（含多场景叠加）不切；③ 当前场景**有未保存改动**不切 —— `OpenScene` 会弹原生模态保存框，Editor 脚本里禁止弹模态。首次导入 / 编译未结束时最多等约 10 秒再试，超了本次会话放弃。

### 5.4 配表代码生成（打表工具，不在引擎内）

客户端引擎的编辑器程序集只有 **Debugger、MapBake 与 EditorStartScene** 三个横切。配表代码生成由仓库的**打表工具**（[`clover-tools/table`](https://github.com/qw576483/clover-tools/blob/main/table/README.md)）负责：
源表 → tsv + 强类型 C# 代码；生成物禁止手改。

明确**不做代码生成**的部分：`EMsg` 消息号、`Protocol` DTO、动画参数常量、多语言 key ——
全部**手工维护**，多处（两端）必须保持一致；**不提供 Generator，也不提供自动生号**。

---

## 6. 引擎边界（明确不做）

| 能力 | 归属 | 引擎提供的衔接 |
|---|---|---|
| 渠道 SDK（登录 / 支付 / 分享 / 广告） | 发行接入层（独立工程，引擎不做） | Game 启动流程留 SDK 初始化钩子；渠道登录口 `CloverAuth.ChannelLoginAsync` |
| Crash 收集（Bugly 等） | 第三方 SDK | Logger 可挂外部上报出口 |
| 数据埋点 | 业务层 | 经 Event 总线订阅，业务自行上报 |
| 新手引导 | 业务层 | UI 提供遮罩/高亮组件 |
| 语音 / 视频 | 第三方 SDK | 无 |
| 战斗回放 | 后续期（依赖帧同步管道） | WorldSync 帧数据可录制 |
| 反作弊 / 加固 | 发行环节 | 无 |

---

## 7. 分期实施

| 期 | 内容 | 验收 |
|---|---|---|
| P0 服务端对齐 + 最小闭环 | Core（Game/Logger/Event/Timer/Fsm/Dispatcher）+ Network（TCP/WS/QUIC/WebTransport/RawUDP）+ DataTable + 断线恢复 + 真实联调 | 自动连接登录、传输层与服务端真实能力对齐、OnMsg 收推、Call 回包、重连恢复、Timer/Fsm 主线程触发、API 形态与服务端对齐、平台线路选择与降级链可运行 |
| P1 会话韧性 + 资源底座 | session resume、UDP 绑定三约束、WebRequest、Resource、ObjectPool、Setting、全传输线路测试、平台化降级测试 | 拔网恢复不重登且快照覆盖；资源异步加载/池化可用；QUIC/WebSocket/WebTransport/RawUDP/TCP 的平台路径可验证 |
| P2 表现全家桶 | Scene / Entity+View / UI（含通用件）/ SpriteAtlas / Animation（仅封装 Unity `Animator`）/ Sound / Input / Camera / Quality / WorldSync | 场景带预加载切换；服务器 AOI 事件驱动实体平滑移动；UI 数据绑定；摇杆驱动角色。**引擎不做本地预测**（见 §3.1），因此**没有**「预测档位可切换」这一项 |
| P3 扩展能力 | Debugger 深度网络模拟、房间帧同步管道、Localization、更多平台性能调优 | 弱网 / 多线路下符合 N4~N9；帧上行下行通畅；平台适配稳定 |

---

## 8. 待决策项

| 问题 | 选项 | 当前选择 |
|---|---|---|
| 序列化 | JSON / Protobuf / MemoryPack | 跟服务端 JSON 线格式；后续可换，Router API 不动 |
| 脚本热更 | 不热更 / HybridCLR / Lua | 默认不热更（仅资源 + 配置热更） |
| 资源后端 | Resources / AssetBundle / Addressables | 已实现 Resources + AssetBundle；Addressables 未接入（按同一 `IResourceBackend` 落位） |
