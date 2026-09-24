# Clover Unity 客户端引擎 索引

> 一个 Unity C# 客户端引擎（UPM 包），结构参考 GameFramework / ET 等成熟客户端框架的**能力域划分**。
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
│   │                 / LogBuffer / Setting / Json / DeviceId / Rng / AStar / IsoLayout / Dir8 / Input
│   │                 / Separation2D（角色间水平推开：`TryResolve` / `TryResolveOne`）
│   │                 / GridUtil（矩形 → 整数格遍历）/ Screenshot（截图落盘）
│   │                 / JsonWriter（确定性 JSON 原语：固定字段顺序 + 浮点 R 格式 + null 安全；
│   │                              写 `WriteString` / `WriteKey` / `WriteInt` / `WriteLong` / `WriteBool`
│   │                              / `WriteFloat` / `WriteDouble` / `WriteNull`
│   │                              + 最小解析 `ParseValue` / `ParseObject` / `ParseArray` / `ParseString`
│   │                              / `ParseNumber` / `SkipWhitespace`；DTO 字段布局仍留业务侧）
│   │                 / ServiceAutoWire（反射装配：按接口找实现类型并实例化 + 两级类型缓存 +
│   │                                   多实现/找不到的可诊断警告；程序集取自身）
│   │                 / ScreenPointUtil（屏幕点 ↔ 画布矩形 / 世界点换算统一入口；含正交"屏幕 → 地面"的退化口径）
│   │                 / OrderedAsyncResult（乱序异步结果按下标落位）/ EngineRunner + EngineHostOptions（宿主开关），
│   │                 以及**全部跨模块契约**（含协议载体类型）
│   ├── Data/         数据域：CloverData / DataTable / Localization / CloverTable（读打表产物）/ FileSlotStore（一槽一文件）
│   ├── Network/      网络域：Network / WebRequest / WorldSync / CloverAuth / SchemaRegistryManager
│   │                 / Lan（局域网寻服：UDP 旁路发现，子目录 Runtime/Network/Lan/）
│   │                 / Quic（msquic 原生互操作，子目录 Runtime/Network/Quic/）
│   ├── Resource/     资源域：后端抽象 / Resources / AssetBundle / 清单热更与下载器 / SpriteSet（批量异步预加载 → 按名同步取）
│   │                 / FrameBank（整目录抓帧 → 帧号排序 + 帧号→下标映射 + 统一画布锚点 + 谁造谁 Destroy）
│   ├── Presentation/ 表现域：Scene / Entity / ObjectPool / Map（逻辑地图）/ TileWorld（2D 瓦片世界）/ UI / UIWidgets
│   │                 / UIPanelGuards（面板参数取值守卫：OnOpen 载荷探测 + 降级 + 只报一次）
│   │                 / TextHooks（通用件文字接管点）/ SpriteAtlas / Animation / SpriteFrameAnimator（帧表 → SpriteRenderer）
│   │                 / RuntimePanelProvider（运行时面板供给者：零 prefab 资产）/ SnapshotInterpolator（快照插值钟）
│   │                 / TextFit（可变长文本单行截断）/ DragGestureRouter（拖拽 vs 滚动手势仲裁）
│   │                 / TileNodePool + TileRenderState + TileRenderer（SpriteRenderer 瓦片节点池 + 一格渲染状态值 + 等距渲染内核）
│   │                 / UnitFacingMap（N 档朝向 → 视角号 + 镜像）/ SortingLayers（层级预算 + 深度序 + 同序次级键）
│   │                 / SpriteEntityView（2D 精灵实体视图来源：SpriteRenderer 节点 + 异步贴图 + 逐帧动画 + 纵深排序 + 池化回收）
│   │                 / Sound / Input / Camera / Quality / BitmapFont（位图字模排版内核：格位 / 度量 / 换行 / UV / bestFit / 回退）
│   │                 / SpriteSwapButton（sprite-swap 按钮工厂：常态/悬停/按下/禁用四态贴图 + 一张横排条带切 N 态）
│   │                 / SpriteStripLoader（多帧条带 → 定长帧表：整条 LoadAll 主路 + 逐帧按名兜底 + 就绪回调）
│   └── Plugins/      原生插件落点（已入库 x86_64/msquic.dll 与 Android arm64-v8a / armeabi-v7a / x86_64 的 libmsquic.so；iOS 待补，QUIC 等原生件落这里）
├── Editor/           Editor 横切：Debugger（面板 / GM 控制台 / 网络模拟）
│                               MapBake（地图烘焙：Unity 关卡 → CloverMap 二进制）
│                               EditorStartScene（打开编辑器时打开启动场景）
│                               PixelArtImport（像素素材导入规范 / 导入后处理器）
│                               PanelPrefabBuilder（一键生成 Resources/UI 面板壳预制体）
├── Tests/            Editor（EditMode）+ PlayMode 测试
│                     （GridUtil / TileWorld / SpriteFrameAnimator / SpriteSet / OrderedAsyncResult /
│                      EngineHostOptions 有 EditMode 用例，通用件与音效池有 PlayMode 用例；
│                      **本轮新增用例未实跑**，测试通过与否以本机实跑为准，索引不宣称已通过）
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
│  Editor 横切：Debugger / MapBake / EditorStartScene / 像素导入 / 面板壳预制体  │
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
| 应答端 | `CloverLan.CreateResponder()` → `ILanResponder`（**不经 `Game` 门面**，不经 `Game.Launch`）：`Start(LanHostInfo self, LanRespondOptions options = null)` / `Stop()` / `Dispose()`；固定监听 UDP `47777`，收到合法查询后**单播回给来源端点**（⛔ 不广播回包 —— 否则 N 台主机互相扫描会变成广播风暴） |
| 应答端状态 | `IsSupported` / `UnsupportedReason` / `IsRunning` / `ListeningPort` / `LastError` / `Self` / `Describe()`；计数 `QueryCount` / `ReplyCount` / `DroppedCount`（判据用：`QueryCount > 0` = 确实有人在找服）；`OnQuery(来源 ip:port)` 经 `Game.Dispatcher.Post` 收敛到主线程、日志按 1s 限频 |

事件（经 `Game.Event` 发布，与 C# 事件互为等价入口）：`Net.LanHostFound`（参数 `LanHostInfo`）/ `Net.LanScanFinished`。

**与服务器端「服务发现」的区别**：服务端 `internal/app/discovery.go` 是**进程之间**找服务（gateway 找 logic/auth，走 etcd）；
本能力是**客户端**在局域网里找「一台跑着服务端的主机」（走 UDP 广播）。两者不共享协议 / 端口 / 代码，名字也刻意不同。

> 边界：**不做**「客户端当主机 / listen server」—— 应答端只回答「我在这里」，应答里广播的 `gateway` 端口是**调用方给的参数**（不是它自己起的服务）：「列表里看得到、点加入却接不上」= 网关没起来，与本能力无关。不做 NAT 打洞，不做自动重扫与排序评分 UI。

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
| 命名标记点 | `Points`（全部，只读，顺序 = 文件顺序）/ `GetPoints(name)`（该名字下的**全部**坐标，同名多点 = 一组出生点 / 一条路线）/ `TryGetPoint(name, out Vector3)`（取第 0 个）。名字**大小写敏感**（序号比较）；文件里没有该段（旧产物）时返回**空列表**并由加载路径打 Warn 留痕 |
| **不含本地预测解算** | "输入 → 位移 → 贴墙滑动"不在引擎（§3.1「引擎不做本地预测」）：引擎只给空间事实，怎么用是业务手感 |
| 未加载时的行为 | `WalkableAt` 恒返回 true（不阻挡）：地图缺失是导出/配置问题，不该表现成"玩家被锁死" |

> 格式契约（逐字节）见 [`clover-server-engine/pkg/domain/mmo/mapdata/README.md`](https://github.com/qw576483/clover-server-engine/blob/main/pkg/domain/mmo/mapdata/README.md)；
> 导出端是 `Editor/MapBake`。三处实现（写 / 客户端读 / 服务端读）必须同步改。

#### TileWorld（2D 瓦片世界）

2D 平台 / 瓦片关卡的**空间事实面**（实心格位图 + 移动托台的**小数顶高** + 世界边界）；**门面上没有入口**，业务自持实例（与 `Map` 是两套语义，见下）。

| 能力 | 约束 |
|---|---|
| 事实查询 | `ITileWorld.IsSolid(tx, ty)`（**越界恒 false**、不抛、不打日志）/ `IsSolidAt(float x, float y)`（世界 → 格**必须** `Mathf.FloorToInt`，`(int)` 强转对负数向零截断 ⇒ "站在坑里也能踩到地"）/ `TryGetCarrierTop(tx, ty, out float topY)`（托台小数顶高；查不到返回 `false` —— 那是正常查询、不是错误分支）/ `MinX` / `MaxX` / `GroundTopY` / `HasBounds` / `SolidCount` / `CarrierCount` |
| 构建期写入 | `SetSolid(tx, ty, solid = true)` / `Clear()`（只清**格数据**、**不动**边界与地面顶高）/ `SetCarrierTop` / `ClearCarrierTop` / `SetBounds(minX, maxX, groundTopY)`（非有限值忽略 + Error；`minX > maxX` 归一化后交换 + 降频 Warn） |
| **只回答空间事实，不做位移解算** | 口径与 `Map.cs:14-15` **逐字一致**：「输入 → 位移 → 贴墙滑动」的解算**不在引擎** —— ⛔ 本接口**没有** `Move()` / `Step()` / `Resolve()` 之类方法（用多大半径、几点采样、撞墙是停还是滑都是玩法手感，加了两个项目就会互相打架） |
| 为什么另起一个接口（不塞进 `IMapData`） | 服务器 `CloverMap V1` 是 **3D MMO 单层平地**（`WalkableAt(x, z)`，一张图一层、高度由地面标量决定）；2D 平台要的是「同一 x 上**多层**格 + 每格可有小数顶高 + 托台会动」⇒ 两套语义不硬塞 |
| 存储 | 稀疏两个哈希表（`HashSet<long>` 实心 + `Dictionary<long,float>` 托台），键 = `((long)tx << 32) \| (uint)ty`（**双射**，每键 8 字节）；⛔ 不用 32 位异或键 —— 后者在 `\|tx\| ≥ 2^15` 时键碰撞 ⇒ 误判实心（"明明没砖却撞上了"）且不报错（`Runtime/Presentation/TileWorld.cs`；出处见该文件头注释） |

#### TileNodePool + TileRenderState（瓦片节点池 + 渲染状态值）

等距 / 瓦片地图的**逐格渲染**那一对通用件（`Runtime/Presentation/{TileNodePool,TileRenderState}.cs`）：节点反复重铺时**不销毁、只复用**，且复用与新建给出**逐项相同**的画面。

| 能力 | 约束 |
|---|---|
| `TileNodePool`（`SpriteRenderer` 节点池） | `TileNodePool(Transform root)` → `Take(parent)`（**取出即 `SetActive(true)`**）/ `Return(sr)`（**失活 + 挂回池根，不销毁**）/ `Clear()`（真销毁**池内空闲**节点，不动已取出的）；计数 `CreatedCount` / `ReusedCount`（**只增不减**，进程内累计自证量）、`FreeCount`（"池里现在几个"看它）；静态纯函数 `SplitDemand(freeCount, demand, out fromFree, out create)`（离线算池化收益，不建 `GameObject`）。⛔ **瓦片节点（`SpriteRenderer`）的唯一创建点 = `Take` 的冷分支**；池根由**调用方**给（挂在业务自己的节点树里 ⇒ 随场景销毁，不跨场景泄漏）。 |
| `TileRenderState`（一格渲染状态值） | `readonly struct`，5 个 `public readonly` 字段 / 11 个标量：`Sprite` / `Color` / `LocalScale` / `Position` / `SortingOrder`（对应 `SpriteRenderer.sprite/color`、`transform.localScale/position`、`sortingOrder`）；`SameAs(other)` 逐项比较（`Sprite` 按**引用**；⛔ 不走 `Vector3.Equals` 的 epsilon 语义）。⛔ 只放渲染状态，**业务字段不许进来**（否则池化路径会继承上一次的残留状态）。 |
| **为什么不是扩展 `ObjectPool`** | `ObjectPool` 是 **GameObject 级**池：key 寻址 + 预制体/代码工厂 + `DontDestroyOnLoad` 池根 + 归还时归零变换 —— 回答"某个预制体/工厂的实例借还"。本件只池化一种东西（`GameObject`+`SpriteRenderer`，无 key/无预制体），**故意不归零变换**（位置/缩放/贴图/颜色/排序号由调用方每格用 `TileRenderState` **无条件写全**），池根随场景销毁。两者语义不同、**可以并存**（硬塞进去会带来 key 维度、变换归零、池根策略三处打架）。 |
| 首个消费方 + 自证 | `clover-project-diablo2` 的 `Module/Map/MapView.cs`（`EnsurePool` / `NewTile` / `ReturnTiles` / `Clear`；`GroundState`/`ObjectState`/`FogState` 三个纯函数产出状态值）；同名两件在该项目已改为**薄转发**。自证 = 改前/改后纯函数对拍 + `mapcheck` §17/§20（唯一创建点、`Take`/`Return` 的 `SetActive` 严格配对、池化收益算术）。 |

#### TileRenderer（等距瓦片渲染内核）

把「一格一层画出来长什么样」收成**纯函数 + 无条件写全**两步（`Runtime/Presentation/TileRenderer.cs`）：**投影后的对齐 / 缩放 / 排序数学**与**写全渲染字段**是题材无关的，唯一真相放这里；「这一格该画什么」（瓦片分类 / 区域 / 素材键 / 逐格覆盖）吃题材数据，**留在调用方**。

| 能力 | 约束 |
|---|---|
| 构造与度量 | `TileRenderer(IsoLayout iso, float pixelsPerUnit, float tilePixelsPerUnit)`：`iso == null` ⇒ Error 留痕 + 抛 `ArgumentNullException`（没有投影就没有格中心，早抛胜过**静默画歪**）；两个像素度量任一非有限 / `<= 0` ⇒ Error 留痕并按 `1` 处理。只做**正投影后的对齐**，⛔ 不重写逆投影 / 不做屏幕取格（那是 `IsoLayout` 的事）。 |
| 三个纯内核 | `LocalScaleFor(bool hasSprite)`（有图 ⇒ `契约PPU / 格图像素高`、占位 ⇒ 1）/ `static ColorFor(bool hasSprite, Color placeholder)`（有图 ⇒ 白，像素不被染色）/ `PlaceOfPx(Vector3 cellCenter, int spriteHeightPx, bool isFloor)`（**只吃图像高(px)**、⛔ 不碰 `Sprite` ⇒ 离线宿主可逐格复算；地砖 ⇒ 顶边贴格中心上方半格、墙/物件 ⇒ 底边贴下方半格；`<= 0` ⇒ 原样返回格中心；`< 0` ⇒ 降频 Warn + 按 0）。 |
| 一格一层的状态值 | `StateOf(Vector2Int cell, TileLayer layer, Sprite sprite, Color placeholderColor, int sortOffset, int sortBias = 0)` —— **纯函数**（不建节点、不碰 `SpriteRenderer`、不读全局），返回 `TileRenderState`。对齐方式由 `TileLayer` 决定（`Ground` = 地砖式，其余 = 墙 / 物件式）；地面 / 物件 / 遮蔽三层是同一个方法的三次调用。 |
| 落地（**无条件写全**） | `static Apply(SpriteRenderer sr, Transform parent, TileRenderState st)`：`name` / `SetParent(parent, false)`（追加到末尾 ⇒ 块内格序与全新建一致）/ `sprite` / `color` / `localScale` / `position` / `sortingOrder` / `enabled = true`（⛔ **必须复位**：遮蔽层节点会被业务置 `false`，不复位 ⇒ 复用到它的一格**静默不可见**）。⛔ 方法体内**没有**"是不是复用节点"的分支 —— 有分支，「逐项相等」就不再是结构性保证。`sr == null` ⇒ 空操作（⛔ 不抛：逐格热循环里抛出去会打断整图重铺）。 |
| 节点来源（**注入**） | `Build(Func<Transform, SpriteRenderer> takeNode, Transform parent, TileRenderState st)` = 取 + 写全；`TileNodePool.Take` 的签名即该委托、可直接传方法组 ⇒ 本件**不持有池**、⛔ 不建第二套池。`takeNode == null` ⇒ Error 留痕 + 返回 `null`。 |
| 计划骨架 + 节点数 | `TileCellPlan`（`Draw` / `GroundKey` / `ObjectKey` / `DrawObject` / `ObjectSuppressed`；`HasGround` / `NodeCount`；`default` = `None` = 什么都不画）；`TileLayerParams`（`SortOffset` / `SortBias` / `PlaceholderColor`）；`ApplyPlan(plan, cell, spriteOf, takeNode, groundParent, objectParent, groundLayer, objectLayer)` 顺序固定「地面 → 物件」、返回节点数（**恒等于** `NodeCount`，帧预算按它扣）。⛔ 遮蔽层不在这里（由"是否已探索"驱动，调用方另调 `StateOf` + `Build`）。 |
| **与三件的分工** | `IsoLayout` = 格在哪（投影数学）；`TileWorld` = 能不能站（空间事实）；`TileNodePool` / `TileRenderState` = 节点从哪来 / 一格写成什么样；**本件 = 唯一把后两件接起来的那一层**（对齐 / 缩放 / 排序 + 写全）。⛔ 本件不查可走性、不做碰撞、不裁视锥、不请求贴图。 |
| 出处 + 首个消费方 | `clover-project-diablo2` 的 `Module/Map/MapView.cs:1801-1873, 2401-2502`（`PlanCell` / `ApplyCellPlan` / `ApplyTileState` / `GroundState` / `ObjectState` / `FogState` / `LocalScaleFor` / `ColorFor` / `PlaceOfPx`）；题材语义（瓦片分类 / 区域 / 素材键 / 逐格覆盖 / 水面不叠 / 实心岩体不画 / 各层排序偏移 / 占位配色 / 每单位像素数）**全部经参数与委托进来**，引擎不预设任何一组取值。自证 = `StateOf` 纯函数对拍 + `Apply` 内无分支 + `ApplyPlan` 返回恒等 `NodeCount`（业务侧 `mapcheck`）。 |

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
| **视图来源可插拔** | `EntityViewFactory.RegisterSource(IEntityViewSource, asDefault)`（+ `UnregisterSource` / `ClearSources`）：把「视图规格 → 视图内容」这一层开放给业务 —— 2D 精灵项目注册 `SpriteEntityViewSource` 即可经同一门面拿到视图，⛔ 不再自建第二套实体视图生命周期；**未注册来源时 3D 路径逐字不变**（见下节 `SpriteEntityView`） |
| 分组 | 按场景/玩法分组，随场景批量销毁 |

#### Resource（资源与热更）

| 能力 | 约束 |
|---|---|
| 异步加载 / 释放，引用计数 | 业务**禁止直调** Resources / AB / Addressables 原生 API |
| 版本热更 | 资源 + 配置热更；下载器支持断点续传、边下边玩（当前**直用 `UnityWebRequest`**，未经 `Game.Http` 统一入口） |
| 预加载 | 场景切换前按清单预加载（**与 Scene 加载门控无耦合**，需业务自行保证时序） |
| 内存水位 | 超水位按 LRU 释放未引用资源 |
| 同步取值 | `Game.Res.TryGet<T>(path)`：取**已驻留**资源（不触发加载、不阻塞、纯读）；配合 `Preload` 供"必须立刻拿到"的场景 |
| 精灵集合 `SpriteSet` | 批量异步预加载 → 按名同步取：`LoadSet(paths, onDone)`（逐条走 `Preload`，重复路径由引擎合并）→ `Get(name)`（**纯读缓存**，不触发加载 / 不阻塞主线程）→ 缺失返回 `null` 并**按名字只报一次**（`Get` 每帧都会被调，逐帧刷屏会打爆日志；`Clear()` 后同名会再报一次）；另有 `IsReady`（只表示"流程结束"，**不表示**每张都拿到）/ `Count` / `Clear`。**不持有引用计数**（`Preload` 自己把预热那次 `+1` 还掉）⇒ 条目在水位压力下可被 LRU 淘汰、淘汰后 `Get` 返 `null`；需长期常驻请自行 `Game.Res.LoadAsset` 持有引用（`Runtime/Resource/SpriteSet.cs`；出处与取舍见该文件头注释） |
| 精灵目录仓 `FrameBank` | **整目录**抓帧 → 帧序 → 锚点 → 生命周期（与 `SpriteSet` **互补、方向相反**：`SpriteSet` 按名取单张、本件一次拿有序数组）：`LoadDir(path, PivotMode)`（同步 `LoadAll<Sprite>`；空则降级 `LoadAll<Texture2D>` 现场 `Sprite.Create`；**按帧号升序稳定排序**）· `ParseFrameIndex(name)`（**静态纯函数**：从倒数第二段起回退找纯数字段 —— 取"尾部数字串"对 `frame_000_0` 这类名字恒返回 0 ⇒ 排序空转，见文件头事故①）· `FrameNumberMap(path, mode)`（帧号→下标 + **恒等断言留痕**：非恒等 ⇒ Warn + 前 8 项 `fN→idx`）· `UnifyCanvasAnchor(frames, key)`（全目录 `rect` 并集中心当锚点、按 `pivot = ((anchorX-rect.x)/rect.width, …)` 重建；**实测某 486 帧目录 245 个不同锚点 = 换帧位移**）· `WhiteSprite()` · `Clear()`（**只销毁**名字带 `GeneratedSpritePrefix` 的现造 Sprite，⛔ 不动导入态）· `Cached` / `Count`；去重走 `LogThrottle`；`Mode` 参与缓存键（防静默用错锚点）（`Runtime/Resource/FrameBank.cs`） |

#### ObjectPool（对象池）

| 池 | 能力 | 约束 |
|---|---|---|
| GameObject 池 | Spawn/Despawn（按 prefab key）、预热、容量上限（Trim 裁剪）、**空闲过期回收**（`IdleExpirySeconds`，默认 0 = 关闭）、按场景分组清理；**代码工厂** `IObjectPool.Register(string key, Func<GameObject> factory)`：`Spawn` / `Preload` 时**工厂优先**，未注册的 key 才回落既有的 `Resources.Load<GameObject>(key)` 预制体路径（用途：代码造的对象也能入池）—— 工厂返回 `null` ⇒ 本次 Spawn 失败 + Error，⛔ 不静默产出空对象；请在首次 `Spawn` 之前一次注册完（例如 Launch 钩子里），建议 key 不与预制体路径重名 | 战斗内特效/子弹/飘字/角色**一律走池**，禁止裸 `Instantiate` |
| 引用池 | `ReferencePool`：**纯 C# 对象复用**（`Acquire<T>()` / `Release(T)` / `Count<T>()` / `Clear<T>()` / `ClearAll()`；对象可实现 `IReferencePoolable`，在取/还时自动复位）。实现在 `Runtime/Core/EntityPool.cs` | 只复用**托管对象**（输入帧 / 事件参数 / 临时集合）；`GameObject` 一律走上面的 GameObject 池 |

#### UI

| 能力 | 说明 |
|---|---|
| UIManager | 窗口栈、5 层固定层级（Background / Normal / Popup / Top / System）、打开/关闭、Popup 互斥与遮罩；面板继承 `UIPanel` 基类即可，预制体默认放 `Resources/UI/{类名}`（可用 `CloverPresentation.PanelProvider` 换成 Addressables / AB） |
| 数据绑定 | UI 只订阅数据变更事件刷新，**不直连网络、不改数据** |
| 通用件 | Toast / 飘字 / Loading / 红点 / 确认框 / 引导遮罩高亮件 / 世界血条 `WorldHpBar`（业务自挂在实体视图根下，引擎未接门面入口）（见 `Runtime/Presentation/UIWidgets.cs`）。**飘字签名**（契约 `Runtime/Core/PresentationContracts.cs`）：`IUIManager.FloatText(worldPos, text, color = null, duration = 1.2f, riseWorld = 0f, fade = true)` —— `riseWorld = 0`（默认）沿用本次下沉前的"屏幕升距 70 画布单位"（与相机距离无关，旧调用方一个像素都不变），`> 0` 才按**世界单位**沿世界 +Y 投影（投影不出结果时回落屏幕升距 + 限频留痕）；`fade = false` = 全程不透明、到点直接隐藏（复用节点时已复位 alpha） |
| 控件工厂 | `UIFactory` 的通用 uGUI 控件（业务"用代码搭 UI"一律用它，别自己再写一套）：水平滑块 `CreateSlider` · 单行输入框 `CreateInputField` · 「◀ 值 ▶」选择行 `CreateSelector` · 开关行 `CreateToggleRow`（句柄类型 `Selector` / `ToggleRow` 也在 `Presentation` 程序集）· 进度条比例 `SetBarWidth`（**锚点宽度**口径，不用空 sprite 的 `fillAmount`）· 布局助手 `Place` / `AnchoredTopLeft` / `AnchoredBottom` / `CreateLabel` / `CreateBoxRect` / `CreateBottomLabel` · 引擎署名行 `CreateCreditLabel(parent, font = null, fontSize = 14, bottomOffset = 16f, text = "by clover-engine")`（钉死底部锚点 `anchor/pivot = (0.5, 0)` —— 用左上角锚点 + 大负 y 放底部元素会在 `CanvasScaler` 真实画布高度变小时整块掉到屏幕外；`font == null` 用引擎内置字体并**降频 Warn**：像素 / 点阵字体常只有大写字形，小写会被静默渲染成全大写 `BY CLOVER-ENGINE`，见 `Runtime/Presentation/UIWidgets.cs`）；**配色 / 文案 / 字号 / 回调一律由参数传入**，引擎不含任何项目取值（见 `Runtime/Presentation/UIWidgetControls.cs`） |
| 图片异步装载 `UiImageLoader` | uGUI **异步贴图**的底座（业务别再自己写一套请求守卫）：`SetSprite(Image img, string path, Color? tint = null)` —— **请求序号守卫**（同一 Image 只有"最新一次请求"的回调会落地：`Game.Res.LoadAsset` 异步 ⇒ 连续换图时旧回调可能晚到并盖掉新图）、**失败保留占位**（回调 `null` ⇒ 只打限频 Warn，`sprite`/`color` 一个都不动，占位继续可见）、**同路径去重**（在途或已成功 ⇒ 不重复发起；**失败过的不拦** ⇒ 再调一次即重试）、`SetTint(img, color)`（贴图已在 ⇒ 立即生效；未到 ⇒ 记下、到位那一刻统一套用）；另有 `RequestedPath` / `IsPending` / `IsLoaded` 供断言。**⛔ 引擎不含任何项目素材路径 / 占位配色 / 字号**（路径与占位色由调用方给，本件只在**成功**时覆盖 `img.color`）；状态存 `ConditionalWeakTable` ⇒ Image 销毁即随 GC 释放（见 `Runtime/Presentation/UiImageLoader.cs`） |
| 2D 光照 unlit 校验 | 同文件的 `IsLitShader(string/Shader/Material)`（**纯函数**判 shader 名：先判 `unlit` 再判 `lit` —— 顺序反了 `Sprites/Default` 会被误判受光；可离线断言）＋ `EnsureUnlit(Image/Graphic)`（防御性校验：`material == null` 的常见路径**一个字段都不碰**，只有发现被挂了受光材质才换回 Canvas 默认 UI 材质 `material = null` 并**限频留痕**——受光材质不报错，只随 2D 光照把 UI 图压暗，只有看图才发现） |
| 条带加载 `SpriteStripLoader` | 「多帧条带 → 定长帧表」加载器：`RequestStrip(stripPath, frameCount, onReady)`（**同一路径只加载一次**，就绪前重复调用只登记回调、就绪后统一触发；**已就绪 ⇒ 同帧同步回调**，调用方要能接受）· `Cached` / `IsReady` / `Count` · `Clear()`（**只丢引用，⛔ 不 Destroy 任何 Sprite** —— `LoadAll` 取到的对象归调用方）。取帧次序**固定**：① 整条 `LoadAll`（主路，同步阻塞）→ ② 逐帧按名 `LoadAsset`（兜底）—— 因为"一张 PNG 切多个子 Sprite"的导入设置下**逐帧按名取不到**且会让资源模块白刷 Error。纯函数 `Slice(all, stripPath, frameCount)`（按"子 sprite 名 = `{文件名}_{帧号}`"切帧，离线可断言）· `FramePath` / `FileNameOf`；帧号解析**复用 `FrameBank.ParseFrameIndex`**（⛔ 不另写一份，它能处理 `frame_000_0` 这类"尾段是子序号"的名字）；缺帧 `WarnOnce` 点名路径 + 缺口数（⛔ 不静默）（`Runtime/Presentation/SpriteStripLoader.cs`） |
| sprite-swap 按钮 `SpriteSwapButton` | 四态贴图（常态 / 悬停 / 按下 / 禁用）的 uGUI 按钮工厂（引擎既有 `UIFactory.CreateButton` **只造纯色按钮**，没有"按状态换 sprite"这条路）：`Create(parent, Spec, Skin)`（骨架仍走 `UIFactory.CreateButton`，⭐ 本件不改它）· `CreateFromStrip(parent, Spec, stripPath, frameCount, StripStates, placeholder, loader)`（**一张横排条带切 N 个状态**，取帧交给 `SpriteStripLoader`）· `Apply(Button, Skin)`（运行中换皮）。尺寸 / 位置 / 文案 / 色调 / 字号 / 占位色 / 四态贴图**一律参数化**（⛔ 引擎不含任何项目配色 / 素材路径 / 默认文案）。缺图口径：**常态缺失 ⇒ 保留调用方给的占位底色 + 一个字段都不动 + `LogThrottle.WarnOnce` 一次**；悬停 / 按下 / 禁用缺失 ⇒ **回落常态帧**（⛔ 不把 `null` 填进 `SpriteState` —— Unity 的 `Selectable.DoSpriteSwap` 遇 `null` 会**静默早退**，表现成"悬停 / 按下没反应"且零报错）；`selectedSprite` 与悬停同帧（键鼠与手柄表现一致）；unlit 走 `UiImageLoader.EnsureUnlit`（⛔ 不自己判 shader 名）（`Runtime/Presentation/SpriteSwapButton.cs`） |
| 适配 | Canvas + CanvasScaler；通用件（Toast / Loading / 确认框）内置安全区适配（`SafeAreaFitter`，internal），**业务面板需自行用 `Screen.safeArea` 处理**（控件工厂只负责摆放，不碰安全区——这条边界不变） |
| 参数取值守卫 `UIPanelGuards` | `OnOpen(param)` 载荷的**类型探测 + 缺失留痕 + 降级取值**（⛔ **永不抛异常**）：`TryGet<T>(param, out T)`（**纯探测，不产生任何日志**）/ `Require<T>(param, panelName, out T)`（失败 ⇒ 返回 `default`，面板按空数据打开；有 `tag` 重载）/ `RequireValue<T>(param, panelName, fallback)`（值类型载荷 ⇒ 失败返回兜底值）。留痕走 `LogThrottle.WarnOnce`（内部即 `Game.Logger`）⇒ 同一 `(tag, 面板, 原因)` 整个进程**只报一次**（面板重开 / 每帧取参不刷屏）；**⛔ 引擎不含任何面板类名 / 项目 tag**（tag 与 panelName 由调用方给，缺省 tag = `"UI"`）；泛型**不加 `class` 约束** ⇒ 值类型（如 `int skillId`）也走本件（`Runtime/Presentation/UIPanelGuards.cs`） |
| 运行时面板供给者 `RuntimePanelProvider` | **零 prefab 资产**的 `CloverPresentation.PanelProvider` 实现（与 `Editor/PanelPrefabBuilder` 的 prefab 路线**并存**，靠同一个函数槽切换）：`new RuntimePanelProvider(typeof(某面板).Assembly)` → `Install(Game.Event)`（**幂等**；按"**当前总线对象**"标识本轮，对抗关域重载后的僵尸会话；同总线但槽被换掉时**自愈重装**）→ `Provide` 反射扫「`MonoBehaviour` + `IUIPanel` + 非抽象 + 非开放泛型」→ `new GameObject(类名, typeof(RectTransform)) + AddComponent(类型)` 造模板交给 `UIManager` 克隆；**模板延后一帧销毁**（`Timer.After(0f,…)`，⛔ 当场 `Destroy` 会让对象在 `Instantiate` 前变假 null 抛异常）· `Uninstall()` 把槽置回 `null`（= 回 prefab 路线）· `AddAssembly` / `Invalidate` / `PanelTypes` / `IsInstalled` / `InstalledBus`。⚠️ **IL2CPP 裁剪**：面板类型无静态引用，**必须**用 `link.xml` 保留（否则面板开不出来且**完全静默**）；约定「面板在 `OnOpen` 里建视觉树，⛔ 不放 `Awake`/`Start`」（模板也会走一遍 `Awake`）（`Runtime/Presentation/RuntimePanelProvider.cs`） |

#### WorldOverlayWidgets（世界 / 屏幕叠加层四件）

「世界点 → 屏幕 / 画布」的**叠加层通用件**（`Runtime/Presentation/WorldOverlayWidgets.cs`）—— 与 `UIWidgets.cs` 同一先例（同一条口径的通用件合在一个文件，⛔ 不是新起的组织方式）：`UIWidgets` 已把 Toast / 飘字 / Loading / 确认框 / 红点 / 引导遮罩 / `WorldHpBar` 七件合在一起，本件是同一类东西的续集。四件共用同一条「世界点 → `UIFactory.UICamera()` 投影 → `ScreenPointUtil` → 画布局部点」口径。

| 能力 | 约束 |
|---|---|
| `WorldProjectedLabelLayer`（世界投影标签层） | **按 id 池化复用、持久显示**：构造 `(RectTransform canvas, Transform parent, LabelFactory factory, ...)`（`LabelFactory = IOverlayLabelView LabelFactory(int id, Transform parent)`）→ `Apply(IList<WorldLabelItem> items)` 返回本次可见数（一次性写全文案 / 颜色 / 位置 / 显隐）· `TryGetView(id, out view)` · `VisibleCount` / `PooledCount` / `Clear()` / `Dispose()`。数据载体 `WorldLabelItem{Id, World, Text, Color}`（`Id < 0` ⇒ 本次跳过）；文字渲染由调用方实现 `IOverlayLabelView` 注入（位图字模 / uGUI `Text` / 图片拼字都行） |
| `ScreenTargetBar`（屏幕顶部目标条） | `ScreenTargetBarSpec{NodeName, Size, Pos, Background, Fill, TitleSize, TitlePos, TitleNodeName, FillNodeName}` + `TitleFactory` 注入 → `Create(...)` · `Show(title, value, maxValue, titleColor)` · `Hide()` · `Dispose()` · `Title` / `Value` / `MaxValue` / `Fill01` / `IsVisible`。⚠️ 进度填充走 `Slider.fillRect` 的**锚点宽度**，⛔ **不是** `Image.fillAmount` —— 无 sprite 的 Filled Image 会退化成整块矩形（扣血看不出来）**且零报错** |
| `WorldNameplate`（世界内名牌） | `WorldNameplateSpec{NodeName, BackColor, LabelNodeName, Pivot}` + `LabelFactory` + `SizeMeasure(string)` 注入 → `Create(...)` · `Show(world, text, color)` · `Hide()` · `Dispose()` · `Text` / `IsVisible`（`Show` 返回投影是否成功） |
| `CenterAnnounceLayer`（居中公告条：淡入 / 停留 / 淡出） | `CenterAnnounceSpec{NodeName, Size, Pos, LabelNodeName, FadeIn, Hold, FadeOut}` + `LabelFactory` → `Create(...)` · `Show(text)` · `Tick(dt)`（由业务驱动）· `Hide()` · `Dispose()` · 纯函数 `AlphaAt(elapsed, fadeIn, hold, fadeOut)` · `TotalSeconds` / `IsShowing` / `Elapsed` / `Text` / `Alpha`。渐隐用 `CanvasGroup.alpha`，⛔ 不逐帧 `SetText`（字模标签每次重排都会销毁重建字形节点 ⇒ 每帧 GC） |
| `SoftwareCursorLayer`（软件多态光标） | `SoftwareCursorSpec{CanvasNodeName, SortingOrder, ReferenceResolution, MatchWidthOrHeight, ImageNodeName, ImageSize, ImagePivot}` + `ImageFactory` → `Create(...)` · `Follow(screenPos)` · `SetVisible(bool)` · `SetSystemCursorHidden(bool)` · `Dispose()` · `Canvas` / `Image` / `IsVisible` / `SystemCursorHidden`。⚠️ **只有调用方说"贴图到位"**（`SetVisible(true)`）才隐藏系统光标 —— 否则会出现"连指针都看不见"，比系统默认箭头更糟 |
| 共用口径与 ⛔ 边界 | 世界点投影失败（相机缺失 / 点在相机背面 / 换算失败）⇒ **把该件藏起来**，⛔ 绝不画在错位置；池化复用必须**每次写全** 文案 / 颜色 / 位置 / 显隐（漏写一项 = 复用出上一帧的残留）；屏幕 / 世界点换算**一律复用 `ScreenPointUtil`**（⛔ 本文件不直接调 `RectTransformUtility`）；⛔ 本文件**零项目专有** —— 不出现任何业务类名 / 文案 / 配色 / 字号 / 图标 / 素材路径，`nodeName` 这类只为可断言性存在、默认值中性 |

#### TextFit（可变长文本单行截断）

「**内容长度由数据决定、且必须单行显示**」的标签（昵称 / 卡名等）的**单行截断**（装得下 ⇒ 原样写入；装不下 ⇒ 二分找最长前缀 + 省略号）。uGUI 的 `HorizontalWrapMode` **只有 `Wrap` / `Overflow` 两个取值**，不存在 `Truncate` ⇒ 横向截断只能自己把**字符串**裁短；而引擎建文本的入口 `UIFactory.CreateText` 出于多行文案的需要，刻意设成 `horizontalOverflow = Wrap` + `verticalOverflow = Overflow`（纵向不设限 ⇒ 超长串换行后会画到矩形外、**盖住整块面板**）⇒ 截断做成**显式调用**（面板在"把数据写进标签"那一处调一次），⛔ **不做全局截断**（那会把多行说明文案也裁成一行，是另一类破坏）。

| 能力 | 说明 |
|---|---|
| 入口 | `TextFit.Clamp(Text label, string raw)` / `Clamp(label, raw, ellipsis)`（**省略号可换**，空串回落 U+2026）· `ClampSelf(Text label)`（把标签**当前**文本按同规则裁一次）· `Measure(Text label, string text)`（不受矩形约束的单行像素宽，供调用方断言）。`label == null` ⇒ 原样返回（调用方不必判空）；`rect.width <= 1`（布局还没算）或空串 ⇒ **原样写入、不裁**（判定依据不存在，裁只会裁错） |
| ⛔ 为什么不读 `label.preferredWidth` | uGUI 的 `preferredWidth` 走**缓存**的布局用生成器：刚 `label.text = 新值` 之后，同一帧读到的还是**上一串**文本的宽度（实测：3 字串 `rectW=58 / preferredW=44` 被判超宽、截成 1 字 + 省略号）⇒ 必须自建 `TextGenerator` 现算（`Measure` = `GetPreferredWidth(text, GetGenerationSettings(Vector2.zero)) / pixelsPerUnit`） |
| 口径 | 二分找最大的 k 使「前 k 字 + 省略号」装得下；结尾再**向前退到确实装得下**为止（换行点造成的轻微非单调由这步兜住）。判据与调用方断言**同源** ⇒ `Clamp` 之后 `preferredW <= rectW` **恒成立**。⛔ **不许改字号糊过去**（会让同列标签字号不一致且仍可能溢出）、⛔ 装得下**不许**无端加省略号（`Runtime/Presentation/TextFit.cs`） |

#### BitmapFont（位图字模排版内核）

自带位图字模 / 像素风项目的**排版内核**（字符 → 格位、整串度量、按宽换行、图集 UV 切格、bestFit 缩放、字形回退链）。与 `TextHooks`（注入点）配套：`TextHooks` 把"引擎刚建的 `Text`"交回业务，本件把"位图字模怎么排"补齐 —— **素材与路径仍留业务侧**，通过 `IGlyphSource`（格子尺寸 + 图集尺寸 + 字符 → 字形）注入。⛔ 本件**不引用 UnityEngine**（纯函数，可离线自检），也**不接**引擎自建 `Text`（那会把多行说明文案按位图口径排）。

| 能力 | 说明 |
|---|---|
| 数据源 | `IGlyphSource.CellWidth / CellHeight / AtlasWidth / AtlasHeight / TryGetGlyph(char, out BitmapGlyph)`；`BitmapGlyph{Col,Row,Advance}`、`BitmapUvRect{X,Y,Width,Height}` 为**纯数据**（业务侧一行转成自己的矩形类型） |
| 入口 | `BitmapFont.Step / Measure / WrapLines / CountLines / CellUv / WrapWidthPx / BestFitScale / TryResolve(chain) / Chain(sources)` |
| 口径 | 步进**必须取字模表里的 `width`**（⛔ 不用格宽：i/l 窄、W/M 宽）；换行判据 = "量到 **≥** 框宽就断"、路过空格在最后一个空格断（空格不带下一行）、没空格断在最后一个能塞下的字、框太窄**至少出一个字**；表里没有的字符**不画也不推进**（步进计 0）⇒ 与 libd2 `font.zig` `breakLine` / `orelse continue` 逐条对齐 |
| UV 切格 | 行主序格位 → UV，PNG 行 0 在上 ⇒ y 按 `1-(row+1)*CellH/H` 翻；图集尺寸未知按 1 兜底 |
| bestFit | 内容按缩放后的宽度超框 ⇒ **整体缩一档**到刚好装下（不小于下限）；装得下 ⇒ 原样（⛔ 不放大、⛔ 不逐行缩） |
| 回退链 | `TryResolve` 依次尝试候选源，第一个命中的胜出（例：原版字模直查 → 简 / 繁 remap 再查）；都失败 = 该字不画（调用方自己决定要不要报日志） |
| 等价自检 | 纯函数 ⇒ 可离线比对：本项目侧把这五块从 `UI/D2Text.cs` 换成本件后，另跑离线宿主对同批输入（中英混排 / 空串 / 超长不换行词 / 缺字 / 简繁回退 / bestFit 缩短）逐字段比对**改前实现**，1651/1651 项相等（`Runtime/Presentation/BitmapFont.cs`） |

#### DragGestureRouter（拖拽 vs 滚动手势仲裁）

「**一个滚动列表里还有可拖出的格子**」这种 UI 结构的手势**仲裁器**（纯逻辑，无 `MonoBehaviour`；调用方把"指针有没有拖出滚动区"算好传进来）。uGUI 把 `pointerDrag` 判给**最靠前（最深层）**的那个 `IDragHandler`（`PointerInputModule.ProcessDrag`）⇒ 拿到事件的组件必须**自己决定**这次手势归谁，否则"纵向拖动 = 滚动列表"与"把格子拖出来"会在同一个 GameObject 上互相抢。

让位规则（优先级从高到低，一段手势**只有一个**归属）：① 指针已拖出**滚动区**（`escapedRegion`）⇒ 归**拖拽**（**必须优先于方向判定**：列表在下方、目标槽位在上方时，"把格子拖到槽位"的主方向恰恰是**纵向** —— 实测拖向槽位 Δ=(62.8, +251.3) 被判成 `scroll`，格子一个都没动）；② 否则**纵向占优**（`|dy| > |dx|`）⇒ 归滚动，横向占优 ⇒ 归拖拽；③ 定为拖拽后**不再改主意**；滚动中的手势若拖出滚动区会**升格**为拖拽（升格前必须先给滚动收尾，否则 `ScrollRect` 会停在"拖动中"，残留惯性在下一帧继续挪 content）。

| 能力 | 说明 |
|---|---|
| 入口 | `Begin(Vector2 pointer)`（按下，并清上一段可能残留的压点击标记）→ `Move(Vector2 pointer, bool escapedScrollRegion)` → `End()`；每次调用返回一个 `Step`（`None` / `BeginScroll` / `MoveScroll` / `EndScroll` / `BeginContent` / `MoveContent` / `EndContent` / `EscalateToContent`），调用方按它把事件转发给 `ScrollRect.OnBeginDrag/OnDrag/OnEndDrag` 或走自己的拖拽逻辑（转发而非自己搬 content：`ScrollRect` 那三个方法是 `public virtual` 的正式入口，惯性 / 回弹 / 边界全走它的既有实现，且它在**转发那一刻**才记起点 ⇒ 从手指中段接管不会跳）。另 `Current`（`Undecided` / `Scroll` / `Content`）· `LastGestureName`（自检 / 驱动脚本读它，避免"只看日志"） |
| 参数化 | `DragThresholdPx`（默认 `12`：uGUI 自己先用 `EventSystem.pixelDragThreshold`（默认 10）挡一道，本阈值只用来**判方向**）· `AllowScroll`（已选中的槽位那一行置 `false`，不参与滚动） |
| 压掉尾巴点击 | 拖完**仍会**触发一次 `Button.onClick`：uGUI 只在 `pointerPress != pointerDrag` 时才清 `eligibleForClick`，而 `Button` 与挂在同一 GameObject 上的本件**两者相等**；又因 `ReleaseMouse` 的顺序是**先 click、后 endDrag** ⇒ 标记只能在拖动过程中打上（`Move` 里），由业务的点击处理**第一行**调 `ConsumeClickSuppressed()` 读一次即清；`End()` 里**一并清掉**（否则"松手落在别的对象上、那次 click 没来"时标记留 `true`，会吞掉下一次**正常点击**）（`Runtime/Presentation/DragGestureRouter.cs`） |

#### PointerFloatLayer + PointerHoverRelay（跟随指针的浮层 + 悬停接线）

「指针悬停 → 跟随指针的浮层（tooltip 形态）」两个通用件（`Runtime/Presentation/PointerFloatLayer.cs`）：① `PointerFloatLayer` = 跟随指针的浮层**容器**（按屏幕点定位、从指针右上展开、贴边翻转、跟随鼠标、可复用 / 可池化）；② `PointerHoverRelay` = 把 uGUI `IPointerEnter/ExitHandler` 转成两个回调的接线件；定位数学单独放在同文件的 `PointerFloatPlacement`（纯函数，离线可断言）。下沉前实测：全引擎 `IPointerEnterHandler|IPointerExitHandler|EventTrigger` **0 命中**，而 `UIWidgets` 的 `FloatTextLayer` 跟随的是**世界坐标的投影**、**不跟指针** ⇒ 属引擎缺口。

| 能力 | 说明 |
|---|---|
| 定位纯函数 `PointerFloatPlacement.Resolve(pointerLocal, size, canvasRect)` / `Resolve(..., cursorOffsetX, cursorOffsetY, edgeMargin)` | 常量 `DefaultCursorOffsetX = 22f` / `DefaultCursorOffsetY = -18f` / `DefaultEdgeMargin = 4f`。契约（改一条 = 改所有调用方的观感）：浮层轴心 pivot = **(0, 1) 左上角** ⇒ 默认从指针的**右下**展开；贴边 = **关于指针镜像**翻到另一侧（⛔ 不是"把框挪回画布内"）；最后夹进画布（浮层比画布还大 / 指针在画布外 ⇒ 钉在边角上，⛔ 绝不摆到画布外） |
| `PointerFloatLayer` | `Create(parent, name = "PointerFloat", canvas = null)` / `Acquire(...)`（走池，`PoolCapacity = 4`）/ `Release()` · `SetSize(Vector2)`（**尺寸由调用方给**，本件不猜内容大小）· `Show()` / `Hide()` / `Tick()`（每帧：屏幕点 → 画布局部点 → 贴边翻转）· `Destroy()` · `Content` / `Canvas` / `IsVisible` / `Size`；`Tag = "PointerFloat"` |
| `PointerHoverRelay : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler` | `Action OnEnter` / `OnExit` —— 业务 `AddComponent<PointerHoverRelay>()` 后赋两个回调即可（⛔ 别再各写一个实现接口的按钮脚本） |
| ⛔ 不负责 | 只做**容器 / 定位 / 显隐 / 复用**：内容（底板 `Image`、文本框、字号、配色、换行测量、品质色…）**全部由调用方注入** —— 本件不建任何 `Text` / `Image`，不含任何色值 / 文案 / 字号常量，不引用任何业务类型 |

#### DragDropLayer（拖放层本体）

「列表 / 格盘里的东西可以被拖到另一个格上」的**通用结构**（`Runtime/Presentation/DragDropLayer.cs`）：把「取拖影 → 跟随指针 → 屏幕点换算成格 → 高亮目标格 → 松手判落点」收敛成一处；**只有最后一步的业务规则**（哪些格可放 / 装备 vs 背包 / 堆叠 / 丢地面）留项目侧。

| 能力 | 说明 |
|---|---|
| `DragGridMetrics`（网格几何，纯值类型） | `Create(cols, rows, origin, cellSize, ...)` · `IsValid` · `Center(col, row)` · `CellRect(col, row)` · `IndexOf(col, row)`（= `row * Cols + col`）· `TryCellAtLocal(local, out col, out row)`。边界点归属与 uGUI `RectTransformUtility.RectangleContainsScreenPoint` **同一口径**（左闭右开）。屏幕点换算**复用** `Runtime/Core/ScreenPointUtil.cs`（画布模式取相机的**唯一口径**：Overlay ⇒ `null`）—— ⛔ 相机必须按画布模式取：把场地相机喂进 Overlay 画布的换算**不抛异常**，只会让命中恒为 `false`（现象 = "拖不动 / 格不亮"）。拖影位置也用 `ScreenPointUtil.TryScreenToWorldInRect`（取拖影父矩形的平面），那只在 **Overlay 画布**下才恰好等于"世界坐标 = 屏幕像素" |
| `DragDropLayer` | `new DragDropLayer(Component context, RectTransform gridRect, DragGridMetrics grid)` → `Begin(screen, ghostSize)` / `Move(screen)` / `End(screen)`（一次完整拖放三段）· `ShowGhost(screen, ghostSize)` / `FollowGhost(screen)` / `HideGhost()` / `ReleaseGhost()` · `TryHit(screen, out hit)` · `UpdateTarget(screen)` · `Resolve(screen)` → `DragDropOutcome{hit, verdict, placeable}` · `SetGhostPool(take, give)`（拖影走池）· `TargetChanged`（**只在状态变化时回调** —— 逐帧回调会让业务每帧重设节点、白跑 + 状态日志刷屏）/ `CanPlace(col, row)`（注入"这个格能不能放"）· `Target` / `LastVerdictName` / `Ghost` / `Grid` / `GridRect` / `Context`；常量 `Tag = "DragDropLayer"` / `DefaultGhostName = "DragGhost"`；枚举 `DropVerdict`、结构 `DragGridHit{valid, col, row, index, local, center}` |
| 容错与 ⛔ 边界 | 拖影位置换算失败 ⇒ 退回屏幕点直填 + 限频 Warn（⛔ **不静默停住** —— 拖影停住会被误判成"拖拽断了"）；`CanPlace == null` ⇒ 一律按可放处理 + 限频 Warn（"未接线"必须能查）；⛔ **本件不判"手势归谁"** —— 那是 `DragGestureRouter`（纯手势仲裁，不含任何坐标）；本件回答的是"归拖拽之后**发生了什么**"（拖影在哪 / 指着哪个格 / 松手落在哪）。两者串起来 = 一次完整的"从滚动列表里把格子拖到目标槽" |

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
| 帧动画 `SpriteFrameAnimator` | 帧表（`Sprite[]`）+ fps → `SpriteRenderer` 的轻量逐帧动画器：`Play(frames, fps, loop = true)` / `PlayOnce(frames, fps, onComplete)` / `Stop()` / `SetFrame(index)` / `Advance(dt)`（**由业务在自己的 Tick 里驱动**：⛔ 无 MonoBehaviour、无协程、不注册回调）/ `FrameIndex` / `IsPlaying`。**不用 `AnimatorController`**：那要求编辑器里编好 State/Clip、回答的是"播哪个 state"（见上一行），而帧序列本来就来自图集（`Game.Res.LoadAll<Sprite>`），天然是"一组 Sprite"；也**不依赖 Addressables**（本类不加载任何资源，帧表由调用方给、谁加载谁 Release）。容错：空帧表 / `fps <= 0` / 运行中换帧表 / `target == null` / 完成回调抛异常 / 单次 `Advance` 步数上限，都有明确口径（降频 Warn，不抛）（`Runtime/Presentation/SpriteFrameAnimator.cs`） |
| 骨骼动画 | **不在引擎范围**：Spine / DragonBones 由业务自行接入 SDK，引擎不做封装 |
| 动画事件 | 完成回调已实现；帧事件在业务侧挂 Unity `AnimationEvent` 接收脚本处理 |
| 技能时间轴 | **引擎不提供**：没有时间轴/帧事件表 API，业务用 `Timer` + 状态机自己编排 |

#### UnitFacingMap（朝向 → 视角映射）

「**N 档朝向 → 视角号 + 是否镜像**」的通用映射（纯逻辑，不持有任何 Unity 对象）—— 多视角 2D sprite（16 / 32 向素材）都会撞上的同一件事。原件里它只是那张**项目专有帧段数据表**上的四个静态成员 ⇒ 别的项目要用就得整张数据表一起抄；本件把它抽成只依赖「档位数 + 映射表」的独立件。

| 能力 | 说明 |
|---|---|
| 入口 | `ViewForStep(step)` · `StepFlip(step)` · `Wrap(step)` · `StepForHeading(headingDeg)` · `ViewForHeading(headingDeg, out view, out flip)`（热路径推荐：一次取全，省一次取模） |
| 参数化 | `new UnitFacingMap(int[] stepToView, int flipFromStep = 0)` —— **档位数 = 映射表长度**（16 档只是默认，见 `Default` / `Standard16StepToView`）；`flipFromStep <= 0` ⇒ 自动取 `StepCount / 2 + 1`（16 档 ⇒ 9，与原件 `step > 8` 逐字等价）；越界的显式镜像起点 ⇒ 收敛到自动值 + 降频 Warn |
| 约定（**换素材必须重新核对**） | `step = 0` ↔ `headingDeg = +90°`（远离镜头 / 上场方向），档位**顺时针**递增；`step = round((90° − φ) / (360° / StepCount))`，取整用 `Math.Round` 默认口径（**中点取偶**，与原件一致 —— 换成"四舍五入远离零"会在恰好落档边界上差一档）；非有限极角 ⇒ 按 0° + 降频 Warn。实测锚点：`_1` = 背身（φ=+90°）→ `_5` = 侧身（φ=0°，朝 +x）→ `_9` = 正朝镜头（φ=−90°） |
| ⛔ 不负责 | **滞回与噪声门槛**（原件取 0.25 档 / 0.01 格）属**调用方策略**，⛔ 引擎不替业务定死；也**不管 clip 内容** —— "每档只取一个视角"是帧段表的约束（旧表取 9 视角的**并集**曾表现为"苍蝇海 1 秒 9 次视角 / 走路原地打转抽搐"）。另：朝向极角应由**插值窗口两端快照之差**求，⛔ 不能用逐帧位移（服务端毫格量化会让站立单位的方向翻 180°）（`Runtime/Presentation/UnitFacingMap.cs`） |

#### SortingLayers（层级预算 + 深度序 + 同序次级键）

2D `sortingOrder` 的**层级预算表 + 深度序 + 同序确定性次级键**（纯逻辑，一层一实例，不持有任何 Unity 对象）—— 俯视 2D 项目的通用问题：底图 / 装饰 / 建筑 / 指示器 / 角色 / 浮层 / 特效各占一段，而角色这段还要按世界 y 细分。

| 能力 | 说明 |
|---|---|
| 层级预算表（**每项可覆盖**） | `Ground`(0) · `Decoration`(10) · `Structure`(50) · `Indicator`(200) · `Actor`(1000) · `Overlay`(2000) · `Effect`(3000)；`ValidateBudget()` 机械检查 **`ActorOrderMin > Structure` 且 `ActorOrderMax < Overlay < Effect`**，不自洽 ⇒ 降频 Warn + 返回 `false`（不满足的症状：**血条被自己单位的精灵盖住** / 兵被建筑盖住） |
| 深度序 | `DepthOrder(worldY)` = `Actor + round((FieldHeightTiles / 2 − worldY) × DepthLevelsPerTile)` —— y 越小（越靠屏幕下方）order 越大（后画 ⇒ 挡在前面）。`ActorOrderMin` / `ActorOrderMax` = `DepthOrder` 在**场地两端点**上的取值（⛔ 不另写第二份公式）。**方向写反不会报错**，只表现为"后面的兵盖住前面的兵" |
| 同序次级键 | `TiebreakOffset(id)`：**只由实体 id 决定**的微小 z 偏移（默认步长 `1e-4` × 取模 `256` ⇒ 上限 `0.0256` 格，**必须小于 1 个 order 级** = 1/16 格 = 0.0625 格）。**依据 = Unity 官方手册「2D 渲染顺序」**（排序层 → 层内顺序 → 渲染队列 → **距离** → 排序组 → 材质）：「距离」条目明写"正交：…**要控制渲染顺序，请增加或减少游戏对象 Transform 组件中的 z 位置**"，同页又写明"若两个游戏对象上述值都相同，Unity 用内部渲染队列顺序决定先后 —— **此顺序是不固定的，您无法控制**" ⇒ 位置**完全重合**的单位（一次 6 只落在同一格）必然同序、谁盖谁**每帧可能不同**（实测 `pos=(-5.5,2.5) n=3 order=[1216,1216,1216]` 三只同格）⇒ 必须有确定性次级键。⛔ 只许用**逐帧不变**的量（id）；用帧号 / lerp 进度会让次序来回翻 = 等于没修。用 z 而非再细分 `sortingOrder`：int 再细分会撞穿上面的预算 |
| 参数化 | `new SortingLayers(float fieldHeightTiles, int depthLevelsPerTile = 16, int tiebreakMod = 256, float tiebreakStep = 1e-4f)` —— **世界尺寸必给**（⛔ 引擎不替业务定死世界尺寸；缺失 / 非法 ⇒ 按 1 格 + 降频 Warn），层数 / 分辨率 / 次级键步长全可覆盖（`Runtime/Presentation/SortingLayers.cs`） |

#### SpriteEntityView（2D 精灵实体视图来源）

引擎的实体视图骨架此前**只有 3D 一条路**（`EntityViewFactory`：3D 模型 + `AnimatorController`）⇒ 2D 精灵项目拿不到「建 / 绑 / 销」骨架，只能自建第二套实体视图生命周期（实测参考：`clover-project-diablo2` 的 `Module/View/ViewModule.cs:1296 EnsureRoot` / `:1444 CreateEntityNode` / `:1716 DestroyView` / `:1694 RefreshAllFrames`）。本件把 2D 那条路补进引擎，经**视图来源接缝**接管（`Runtime/Presentation/SpriteEntityView.cs`）。

| 能力 | 说明 |
|---|---|
| 注册（接缝） | `EntityViewFactory.RegisterSource(IEntityViewSource source, bool asDefault = false)`（+ `UnregisterSource` / `ClearSources`）：非默认来源按注册顺序问 `CanBuild(spec)`，先认领者接管；都没认领才用默认来源；⛔ 未注册来源时 3D 路径**逐字不变**。契约 = `IEntityViewSource`（`CanBuild` / `Build` / `Release` / `IsLoading` / `GetAnimator`，定义在 `Runtime/Presentation/Entity.cs`） |
| 建节点 | 根节点仍由工厂创建 / 销毁；本来源只在根下建一个 **`Sprite`** 子节点（`SpriteRenderer`），节点走**对象池**代码工厂缝（`IObjectPool.Register` / `Spawn` / `Despawn`，池键 `SpriteEntityViewSource.SpriteNodePoolKey` = `clover.entity.sprite`）；占位 = 自造 1×1 白块 + `EntityViewSpec.PlaceholderColor`（默认中性灰，与 3D 路径同口径） |
| 异步贴图 | `spec.ModelPath` = **单张贴图**路径，经 `Game.Res.LoadAsset<Sprite>` 异步来，带**请求序号守卫**（每条记录持一个自增序号，晚到的旧回调丢弃结果；记录已被释放 ⇒ 由回调补 `Release`，与 3D 工厂竞态口径逐字一致）。`SetSprite` 直接摆一张已就绪的图 |
| 逐帧动画 | `LoadFrames(objectID, paths, fps, loop)`：**异步**逐张加载 + 同一序号守卫，凑齐后起播（缺帧**跳过**且留痕，⛔ 不填空 sprite）；`PlayFrames(objectID, frames, indices, fps, loop)`：帧表已就绪（谁加载谁 Release）；播放器复用 `SpriteFrameAnimator`，`Tick(dt)` 是统一推进点 |
| 纵深排序 | `ApplyDepth(objectID, worldY)` 复用 `SortingLayers`：`sortingOrder = DepthOrder(worldY)` + 同序次级键 `TiebreakOffset((int)objectID)` 写节点 z；未传 `SortingLayers` ⇒ `sortingOrder` 归零（用 Unity 默认顺序）并留一条说明 |
| 销毁回收 | `Release(objectID)`：归还本视图占用的贴图引用（去重）→ 节点 `Despawn` 回对象池 → 最后一个视图释放时销毁自造占位贴图。⛔ 来源**不销毁根节点**（工厂 / `IEntityManager` 负责；顺序 = 先 `Release` 再销毁根，否则池里会留"已销毁对象"的引用） |
| ⛔ 不做 | 不按包围盒归一化身高（逐帧 `rect` 不同 ⇒ 会让同一角色换帧时缩放不同）；`EntityViewSpec.TargetHeight` 在本来源不参与（给了只留一条 Info）。尺寸口径 = PPU + 导入设置（多帧目录用 `FrameBank.PivotMode.UnifiedCanvasAnchor`）。⛔ 不含任何素材名 / 配色 / 项目类型 |

#### SnapshotInterpolator（快照插值钟）

「低频权威快照 → 高帧率插值表现」的**时间轴**件（无 MonoBehaviour，由业务 Tick 驱动；**不是 `WorldSync` 的重复** —— `WorldSync` 管逐实体镜像平滑，本件管"这一帧渲染服务端时间轴上的哪一毫秒、插值哪两个快照之间"，对快照内容完全无知）。

| 能力 | 说明 |
|---|---|
| 入快照 | `Push(float serverMs, object payload = null)`：**只入历史，⛔ 绝不重置渲染时钟、⛔ 绝不换正在渲染的窗口**（"到达驱动换窗口"正是抖动的根因）；时间戳 ≤0 ⇒ 退化为按标称周期推定 + 留痕；时间戳没前进 / 乱序 ⇒ **整帧丢弃**（历史必须按时间戳单调）+ 留痕 + 计入 `DroppedOutOfOrderCount` |
| 每帧 | `Tick()` → 推进渲染时钟（**绝对真实时间 × 速率**，⛔ 不写"每帧累加 `Time.deltaTime`"—— 被 `Time.maximumDeltaTime` 夹住会永久落后）→ 有界比例速率修正（`SteerRate`）→ **按时钟**从历史选"夹住时钟的那一对" → 返回插值比例 `t` |
| 三代失败模式 | ①**分母写死 + 每帧重置** ⇒ 每收一帧前跳一次；②**到达驱动换窗口** ⇒ 速度 cv 0.24~0.28（10 Hz"哆嗦"）；③**本代**：按时钟选窗口 + 有界比例速率修正 ⇒ 离线仿真速度 cv 0.000、峰值比 1.000。三条都逐字写在文件头 |
| 参数化（⛔ 不写死帧率） | `SnapshotInterpolatorOptions`：`SnapshotIntervalMs`（10 Hz⇒100）· `ExpectedRenderFps` · `HistSlots`（6）· `RenderLagIntervals`（2）· `LagSteerGain` / `LagSteerMaxRate`（0.05）· `CatchUpMaxRate`（1）· `CatchUpThresholdIntervals`（3）· `ServerNowExtrapCapIntervals`（2）· `ClockMaxLeadIntervals`（1）+ 派生量与 `HistoryCoverageMs`；非法 / 过小值**归一化 + 一条 Warn**，历史覆盖不足缓冲余量、快照比渲染帧还密各有**诊断留痕** |
| 自检面 | `RenderClockMs` · `InterpRatio` · `RenderLagMs` · `BufferLagMs` · `WindowStartMs` / `WindowEndMs` / `WindowStartPayload` / `WindowEndPayload` · `NewestSnapshotMs` · `ClockRate` · `CatchingUp` · `HasWindow` · `HistoryLength` · `SnapshotCount` · `OutOfWindow` · `DroppedOutOfOrderCount` · `Reset()` |
| 时钟注入 | 构造器 `Func<float> clockSeconds`（返回**秒**，语义同 `Time.realtimeSinceStartup`）；**每帧只采样一次**（`…At(real)` 形式，可复现）；真暂停项目自行注入"暂停即冻结"的时钟（`Runtime/Presentation/SnapshotInterpolator.cs`） |

#### Sound（音效）

| 能力 | 说明 |
|---|---|
| 分组 | BGM / SFX / Voice 三组独立音量，BGM 切换淡入淡出 |
| 分组静音查询 | `ISoundManager.IsMuted(SoundGroup group)`：读的就是 `SetMute` 写的那张 `_mutes` 表**本身**（**同源状态**，不另存一份）—— 因此读到的值必然等于"下一次播放实际会用的音量是否为 0"；**未设置过的分组返回 `false`**（与 `GetVolume` 未设置时返回 1 同口径：都回答"引擎当前实际生效的值"）（契约 `Runtime/Core/PresentationContracts.cs`，实现 `Runtime/Presentation/Sound.cs`） |
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

跟随 / 震屏 / 边界约束（`Unfollow` / `SetBounds` 暂无调用方）；**锁定目标、震屏接动画时间轴未实现**。小模块，可选引用。另提供独立组件 `CloverThirdPersonCamera`（3D 第三人称环绕机位 / 遮挡避障 / 贴脸隐藏角色），业务自行挂到相机上，不经 `Game.Camera`。另有相机侧四个**纯件**（能力下沉；同样**不经 `Game.Camera` 门面**，业务自持 / 自传配置）：`CloverFirstPersonCamera`（第一人称 rig，纯逻辑类：`Bind` / `Bind(camera)` / `Unbind` / `Tick(dt)` / `ApplyLookDelta(dx, dy)` / `SetRecoil` / `AddShake`，`Yaw` / `Pitch` 为 Look + 后坐力 + 摇晃的合量）、`ViewBob`（第一人称视点晃动 + 落地沉降，纯逻辑类 + `ViewBobConfig` 七个数值）、`CameraMath`（`FovYFromFovX` 水平→垂直 FOV / `AimDirection` yaw,pitch→视线方向 / `Follow` 指数平滑跟随）、`LookAccumulator`（鼠标位移 → yaw/pitch 累加）。另 `ICameraManager.Main`（`Camera Main { get; }`，`Runtime/Core/PresentationContracts.cs:471`）：返回**本 rig 当前驱动的相机**（未就绪返回 null + 降频留痕，调用方**必须判 null**）—— 供业务替代 `Camera.main` 绕门面（后者做一次带 tag 的静态查找，可能拿到另一台相机，症状是"跟随 / 震屏作用于 A、业务算屏幕坐标用的是 B"，UI 与 3D 对不上而两处代码各自看都对）。

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
| Fsm | `RegisterState` / `Transition` / `Trigger` / `Force` / `Tick(dt)` / `OnChange` / `OffChange` / `Reset()`；另 `Game.NewFsm()` 取**独立实例**（每个 Bot / 单位一棵，`Tick` 由持有者驱动） | **API 与服务端 `fsm.Machine` 完全同名**；游戏流程用它（**房间帧同步走 `Game.FrameRoom`，不使用 Fsm**）。`Reset()` 清状态表 / 触发器表 / `Current`（**不销毁实例**、**不触发**任何 OnExit / OnEnter / OnChange、**保留** `OnChange` 订阅表），用于每回合重开 / 对象池复用；重置后再 `Transition` 到旧状态名会走"状态未注册"Error（须先重新 `RegisterState`） |
| Dispatcher | 主线程分发器：`Post(action)`，引擎每帧排空 | 后台线程回调进入主线程的**唯一通道** |
| Logger | 分级日志，文件 + Console 双写 | 文件 `logs/YYYY-MM-DD.log`（根目录无子目录），文件去 ANSI 颜色码 |
| LogThrottle | 日志降频闸门，**两种口径并存、不可互相替换**：① **时间口径** `ShouldLog(key, intervalSeconds)` / `WarnThrottled` / `ErrorThrottled` / `WarnOnce` / `ErrorOnce`（同 key 在间隔内只出第一条；空 key 恒 false + 只报一次 Warn）；② **计数口径** `ShouldLogEvery(key, everyN)` / `InfoCounted` / `WarnCounted` / `ErrorCounted`（每 key 独立计数，**第 1 次必打**、之后每 N 次一条，行尾补 `（同类第 N 次）`；空 key 归并 `"default"`；`everyN <= 1` 视为 1）。`Reset()` **一并清空两种记录**；可注入 `Clock`（`ClockSource` = Injected / Unity / Process，时钟三级永不抛） | 无对应（客户端防刷屏；高频回调里的非预期分支必须走它） |
| LogBuffer | 运行时日志**环形缓冲**：最近 N 行**只读窗口**（`Lines` + `Version`）+ 线程安全入队（挂 `Application.logMessageReceivedThreaded`）+ `Install` / `Uninstall` / `Drain` / `Push` / `Clear`（`DefaultCapacity` = 400，超过丢最旧）；补 `ILogger` **只写不读**的缺口 | 无对应（**不是第二套 logger**：只收行 / 不写行，`ILogger` 一行未动） |
| GridUtil | 矩形 → 整数格遍历（**纯函数 / 无状态**）：`EdgeEpsilon`（= `0.0001f`，右 / 上边收边量，传 0 = 含边界格）/ `TryGetTileRange(Rect, out xMin, out xMax, out yMin, out yMax, epsilon)`（只算格范围，返回 `false` = 不覆盖任何格）/ `ForEach(Rect, Action<int,int>, epsilon)` —— **热路径入口，委托已缓存时不产生任何分配**（lambda 捕获局部变量或方法组转换都会每次分配一个委托 ⇒ 热路径请把委托存进字段）/ `Enumerate(Rect, epsilon)` 迭代器版 —— **每次调用都会分配**，只给冷路径（构建关卡 / 工具 / EditMode 测试）。口径：`FloorToInt`（⛔ 不用 `(int)` 强转，负数向零截断会让坑边半格算进第 0 格）、y 升序外层 / x 升序内层、空 / 反向矩形静默不回调、非有限坐标不回调 + 降频 Error、超大格数（> 1e6）只留痕**不截断**（`Runtime/Core/GridUtil.cs`；逐字收敛项目里 4 份重复实现） | 无对应（客户端通用底座） |
| Separation2D | 角色间**水平**推开（防"两个角色站进同一格"）的**纯函数 / 无状态**工具：`TryResolve(circles, count, result)`（一次解开一整组、各退一半）/ `TryResolveOne(position, radius, others, count, out result)`（只推一个申请位置，其余视作障碍）；`Circle{Vector2 Position, float Radius}`、`MaxIterations = 8`、`Skin = 0.001f` | 无对应（客户端通用底座）。**确定性契约**：同输入 ⇒ 逐位相同（不调 `UnityEngine.Random`、不读时钟 / 帧号，遍历顺序 = 数组下标序；完全重合时按下标取确定方向）；**不是物理引擎**：不做寻路 / 不做碰撞检测 / 不做时间积分 / 不处理竖直分层，推开结果须再由调用方的墙体判定钳一次 |
| Screenshot | `Screenshot.CaptureToFile(string path, int superSize = 1)`：**立即**读像素并写 PNG（父目录不存在自动递归创建）。**调用方负责在帧末调用**（Play 模式 `yield return new WaitForEndOfFrame()` 之后；Editor 菜单 / 自动化脚本直接调）—— 引擎刻意不替调用方排帧末（那会让引擎持有一次业务生命周期，`Game.Shutdown` 时留下悬挂协程）。**永不抛**：空路径 / 屏幕尺寸非法 / 编码失败 / 读写异常一律返回 `false` + `Error` 留痕（静默失败 = 调用方以为存了、磁盘上没有）；`superSize < 1` 按 1 处理并留痕；`superSize > 1` 是**读屏后最近邻放大**（不是渲染层超采样）（`Runtime/Core/Screenshot.cs`） | 无对应 |
| OrderedAsyncResult\<T\> | 乱序异步结果**按下标落位**（交付顺序恒为下标 0..Count-1）：`Count` / `FilledCount` / `IsComplete` / `Put(index, value)`（越界**忽略**、同一 index 重复**覆盖**，两者均降频 Warn，⛔ 不抛 —— 异步回调路径上抛异常会打断调用方的循环）/ `TryTakeOrdered(out T[] ordered)`（**仅收齐时**返回 `true`，结果按下标升序且**清空自己**可复用；未齐时 `ordered = null` + `false`，⛔ 不交付半成品）；`count <= 0` ⇒ 立即视为 complete、`TryTakeOrdered` 返回空数组 + `true`。用途：并行加载 N 份资源 / 逐帧收集 N 帧结果（帧序不能随回调次序抖动）/ N 个子请求汇总。主线程使用（`Runtime/Core/OrderedAsyncResult.cs`） | 无对应 |
| ScreenPointUtil | 指针 / 屏幕点 ↔ **画布矩形 / 世界点** 换算的统一入口（收敛原先**三份逐字重复**的 `UiPointConvertCamera`）：`CameraForCanvas(canvas)` / `CameraForUi(context)`（**按画布模式取相机**：`ScreenSpaceOverlay ⇒ null`，`ScreenSpaceCamera` / `WorldSpace` 才用画布自己的 `worldCamera`）· `TryScreenToWorldInRect(rect, screen, cam \| context, out world)` · `TryScreenToLocalInRect(…, out local)` · `ContainsScreenPoint(rect, screen, cam \| context)` · `TryScreenToGround(cam, screen, out world, fallbackDepth = 10f)`。所有 `Try*` **不抛异常**（拖拽链路上抛出去会打断 uGUI 事件派发），失败返回 `false` 并把 `out` 置零；`rect == null` ⇒ `false` + 降频 Warn（⛔ 不返回"成功 + 零向量"，那会把元素摆到原点）。**根因（有实机读数）**：Overlay 画布的世界坐标**就是屏幕像素**，而 `RectTransformUtility` 收到**非空**相机时会把屏幕点当成"相机视锥里的一个方向"再投到画布平面 ⇒ 相差一次投影、命中判定**恒为 false**（实测 `RectangleContainsScreenPoint(rect,(214,221),mainCam)=False`、传 `null` 时为 `True` ⇒ 命中测试返回 **-1** ⇒ 按下**根本不进入拖拽**）。⛔ 与"屏幕 → **格**"是两回事：那里要的**正是**相机投影，走 `UIFactory.UICamera` + `IsoLayout.ScreenToWorldOnGround` / `ScreenToGrid`。`TryScreenToGround` 的口径：`depth = -cam.transform.position.z`（正交下到地面的距离，少了它点击位置整体偏移），`Mathf.Approximately(depth, 0)` ⇒ **退化为固定值** `fallbackDepth`（否则 `ScreenToWorldPoint` 恒返回同一点，表现为"点击位置不随鼠标移动"），非正交降频 Warn 后照算。⚠️ **与 `IsoLayout.ScreenToWorldOnGround` 是同一条"正交 → 地面"规则的两份实现**（本件 fallback **参数化**、只出世界点；那份写死 `10`、顺带出**格**坐标）—— **保持两份、不收敛**；将来若要收敛，**前提是先给 `IsoLayout` 补 static 入口**（否则调用方要为用不到的方法填 4 个构造参）。落 `Core` 的理由：UI / View / 输入三处共用，而模块之间禁止互相引用（[`结构规则.md`](结构规则.md) §2.1）；`Core` 已直接用 `RectTransform`（`Runtime/Core/PresentationContracts.cs:198`）且已引用 `UnityEngine.UIModule`（`Runtime/Core/ScreenPointUtil.cs`） | 无对应（客户端通用底座） |
| JsonWriter | **确定性 JSON 原语**（纯静态 / 无状态 / 线程安全，`Runtime/Core/JsonWriter.cs`）。**写**：`WriteString`（**null 安全**：`null` ⇒ 裸 `null`，⛔ 不写 `""`，与解析侧成对往返）/ `WriteKey(sb, key, comma)`（逗号 + 带引号键 + 冒号，逗号由调用方自报 ⇒ 零状态、可离线断言）/ `WriteInt` / `WriteLong`（不变文化，换 region 不变字节）/ `WriteBool` / `WriteFloat` / `WriteDouble`（`"R"` 最短可往返；`NaN` / `±Infinity` ⇒ 写 `null`，⛔ 不产出非法字面量）/ `WriteNull`。**解析**：`SkipWhitespace` / `ParseValue` / `ParseObject`（→ `Dictionary<string,object>`）/ `ParseArray`（→ `List<object>`）/ `ParseString` / `ParseNumber`（含 `.`/`e`/`E` ⇒ `double`；否则先 `long`，long 放不下才退 `double`），递归深度上限 `MaxDepth = 128`。**确定性三口径**：① 字段顺序 = 调用顺序（⛔ 不排序、不重排）；② 浮点 `R` + 不变文化；③ null 安全。**与 `MiniJson` 的关系**：`MiniJson` 是**动态对象树**工具（`Dump` 按 Ordinal **重排**键序、不给逐字段增量写原语），本类是它的**下层** —— DTO 的字段布局（顺序 / 缺字段默认值）仍**留在业务侧**（如 `SaveJson`）；需要 uint64 精度的大整数（雪花 ID）仍走 `MiniJson.Parse` | 无对应（客户端通用底座） |
| ServiceAutoWire | **反射装配（服务定位）**（`Runtime/Core/ServiceAutoWire.cs`）：`TryResolve<T>(Assembly, out T)` / `TryResolve<T>(Assembly, out T, out string error)` —— 在给定程序集里找**非抽象 / 非接口**的 `T` 实现并 `Activator.CreateInstance`；`FindImplementations(contract, assembly)` 供诊断。**确定性**：候选按 `Type.FullName` 的 Ordinal 序排序（`Assembly.GetTypes()` 返回顺序不保证稳定）；**多实现 = 可诊断不静默**（Warn 列全候选 + 取确定性首个，⛔ 不抛）；**逐个降级**（某候选实例化失败 ⇒ Warn 后试下一个，全失败才 `false` + `error` 带最后一个异常）；`GetTypes()` 抛 `ReflectionTypeLoadException` 时用能加载的那部分 + 留痕（⛔ 不整体放弃）。**两级缓存**（程序集 → 类型数组、`(程序集, 契约)` → 已排序候选），⛔ 不缓存**实例**（单例与否由业务决定）；`ClearCache()` 供热重载复位。装配失败**不抛** —— 不该让游戏起不来，但要留可查日志 | 无对应（客户端通用底座） |
| HitShape | **命中判定几何**（`Runtime/Core/HitShape.cs`，纯函数静态类）：`ToUnit(dx, dy, out fx, out fy)`（格增量 → 单位向量；返回 `false` = 零向量 ⇒ 调用方拒绝本次攻击并留痕）/ `InFrontCone(fx, fy, dx, dy, cosMin)`（正面扇形）/ `InMeleeRect(fx, fy, dx, dy, reach, halfWidth)`（矩形走廊，单位 = **格**）/ `LineClear(walkable, from, to, maxSteps)`（Bresenham **格级**通畅：除两端点外每格都要可走）；常量 `MaxLineSteps = 1024`。地形一律**回调注入**（⛔ 引擎不认任何地形枚举）；`60°` / `1.2 格` 之类全是**题材调参**，由调用方传 `cosMin` / `reach` / `halfWidth` 进来。三条已知边界：① 零偏移（同格 `dx=dy=0`）在扇形里**恒 true**（原版近战触及是"距离 / 外接框"的整数口径，⛔ 不是角度口径，被角度锥拒掉才是错的）；② `walkable == null`（地图未接入）⇒ **放行**返回 `true` 并留痕（⛔ 不把"拿不到地图"变成"打不到"）；③ 格步数超 `maxSteps` ⇒ 按"不通"处理（防御异常入参，`BadLine` 死循环时能退出）。首个消费方 = diablo2 `Module/Combat/MeleeShape.cs`（已退化薄转发，只留题材常量） | 无对应（客户端通用底座） |
| ProjectileRuntime | **投射物飞行积分 + 逐格扫掠 + 最近命中**（`Runtime/Core/ProjectileRuntime.cs`）：`ProjectileBody`（`Pos` / `Dir` / `Speed` / `RangeLeft` / `HitRadius` / `Traveled` / `Alive` + `Step(dt)` / `Overlaps(center)` / `Grid`）· `Advance(ref body, dt, blocked, count, target, maxSteps)` → `ProjectileTick{Outcome, Stepped, SteppedPos, Clipped, BlockedCell, StopAt, MonsterId}` · `TrySweepTerrain(from, to, blocked, out hit)`（一帧内逐格采样防穿墙）· `FindNearestHit(count, target, pos, hitRadius)` · `Overlaps` / `GridOf` / `ContinuousGridToWorld` / `FormatTrail`；`DefaultSampleStep = 0.25f`。三个注入点 = "这一格挡不挡弹道"（`ProjectileBlockProbe`）/ "第 index 个候选目标是谁"（`ProjectileTargetProbe`，返回 `false` = 该项不参与）/ 消散·命中**要干什么**（由 `ProjectileOutcome` 分类后调用方自己派发副作用）。⛔ 不含 `GameObject` / 伤害管线 / 音效 / `TileKind` 逐类裁决表（地形语义留在项目侧，引擎抄一份就**双源**） | 无对应（客户端通用底座） |
| GridGraph | **格子图通用算法底座**（`Runtime/Core/GridGraph.cs`，纯逻辑 / 无状态 / 不持有 Unity 对象）：8 邻接 BFS `FloodFill` / `CountUnreachableTargets` / `FillUnreachablePockets` · 边界环封 `SealBorderRing` · 可走格索引 `WalkableIndex`（`Rebuild` / `Count` / `Cells` / `Pick(index)`，**O(1) 均匀抽样**；填充顺序 = x 外层升序、y 内层升序，**顺序即契约** ⇒ 同 seed 的抽样序列才可复现）· 批写 `Fill` / `FillRect` / `LineH` / `LineV` / `FillDisk` / `PaintDiskWalkable` / `SetOnWalkable` · 工具 `Neighbors8` / `InBounds` / `IsDiagonalStep`。委托签名与 `CloverEngine.AStar` **同一个**（`Func<Vector2Int,bool> isWalkable`、同一套越界口径）⇒ 业务同一个方法组可同时喂两处，不会出现"BFS 说通、A* 走不过去"。两条**调用方契约**：`isWalkable` 对**图外必须返回 false**；`width` / `height` 是真实格数且 `visited` 由调用方按 `[width,height]` 分配（违反 ① 的症状 = BFS 从地图外绕过去，自检通过但实际走不通）。对角要求**两侧格都可走**（与 `AStar.Neighbors` 逐行一致，⛔ 不许"顺手优化"）。⛔ 不寻路、不做位移解算、不认识任何地形枚举 | 无对应（客户端通用底座） |
| GridBitSet | **「格集合 ↔ base64 位图」编解码**（`Runtime/Core/GridBitSet.cs`，纯函数 / 无状态 / 不依赖 UnityEngine ⇒ 离线宿主可直接链）：`Encode(indices, w, h)` / `Encode(..., maxCells)` → base64（尺寸非法 / 超上限 ⇒ **空串**）· `Decode(cells, w, h, into)` / `(..., maxCells)` → **本次并入的格数**（坏串 ⇒ `0` 且 `into` 不变）· `IndexOf(x, y, w, h)`（越界 ⇒ `-1`）· `ToCell(index, w, h, out x, out y)`（越界 ⇒ `false` 且 `(0,0)`）；`MaxCells = 1<<20`（防坏档里的超大 `w*h` 吃掉几百 MB）。语义（**逐字节**是硬要求）：位图 = `(w*h+7)/8` 字节、行优先 `i = y*w+x` 对应第 `i>>3` 字节的第 `i&7` 位（低位在前）⇒ 同集合恒得同串；越界索引**一律丢弃**（⛔ 不抛、⛔ 不把负索引折成别的格）；短串容错 ⇒ 后面的格视为未探索（旧档最坏退化成"少记几格"）。用途：小地图已探索格 / 迷雾 / 掩码落盘。首个消费方 = diablo2 `Def/ExploredCodec.cs` | 无对应（客户端通用底座） |
| ConfigSectionLoader（`Runtime/Core/ClientConfig.cs`） | **「带默认值的配置段」加载链**（`ConfigSectionLoader<T> where T : class` + `ConfigSource{Name, LogLabel, ReadText}`）：`new ConfigSectionLoader(tag, sources, parse, createDefault, normalize = null)` → `Value`（首次读触发加载）/ `Load()` / `Reload()`（**重跑整条链**，供"改完配置不重启"）/ `Source`（首个命中来源名；首次加载前 = `NotLoadedSourceName`，**且读它不会触发加载**）/ `LoadCount` / `Loaded`；常量 `DefaultSourceName = "默认值"`。语义：① **每项默认值只有一处**（调用方 `createDefault` 的字段初始化器是唯一出处，⛔ 引擎不第二遍写 `0.7f` 这类）；② **绝不抛异常** —— 来源读取 / 解析 / `normalize` 抛异常都只 Warn 后**跳到下一个来源**，全部来源不可用 ⇒ `Source = DefaultSourceName`、`Value = createDefault()` + 一条 Warn；③ 来源返回 `null` / 空串 / 纯空白 = "本来源没有内容" ⇒ **静默跳过**（不是异常、不打日志）。⛔ 本件**只读**：不写盘、不认识键、不做类型转换 —— 要"存下来 / 改一改"用 `Setting`，要"一槽一文件"用 `FileSlotStore`（三者分工见 [`结构规则.md`](结构规则.md) §3.2） | 无对应（客户端通用底座） |
| EnterLatch\<T\> | **「进入触发一次」的通用状态跃迁闩锁**（`Runtime/Core/EnterLatch.cs`，纯值类型 `where T : struct`）：`ShouldEmit(bool inside)` / `ShouldEmit(bool inside, T at)` —— ① `inside == true` 且尚未发过 ⇒ 发一次并记下触发点；② 仍 `inside`（**同一格或沿区域逐格挪动**）⇒ 不再发；③ `inside == false` ⇒ 重新武装（⛔ 不许把角色卡在区域里出不来）；另 `Fired` / `HasLastTrigger` / `LastTriggerAt` / `Reset()`（进图落位 / 传送 / 复活后调）。⛔ **不是"记住上一格"** —— 旧口径沿出口列 / 接缝逐格挪动会**每格各发一次**（同一族缺陷，改一处漏一处）；也⛔ 只判"要不要发"，不判"发去哪"、不判"什么算区域"（`inside` 由调用方按唯一口径算好再传）。持在持有者字段里，**⛔ 不要每帧 `new`**；纯函数式 ⇒ 同样的入参序列恒得同样的出参序列。首个消费方 = diablo2 `Module/Map/ExitLatch`（薄封装） | 无对应（客户端通用底座） |
| StableHash | **「程序化生成结果」的自证设施两半**（`Runtime/Core/StableHash.cs`，静态纯函数）：① FNV-1a 64 —— 常量 `OffsetBasis = 14695981039346656037UL` / `Prime = 1099511628211UL`；`Combine(hash, ulong / int / bool)` · `Fnv1a64(byte[], offset, count)` / `Fnv1a64(string)` · `Hex(hash)`（`X16`）· `HashGrid(w, h, read, seed)` / `HashGridHex(...)`（**先混 width / height，再按 y 外层升序、x 内层升序逐格混入 `byte` 地形码**）；② 字符画 dump —— `ToAscii(w, h, charOf, legend, maxRows)`（行首为三位零填充的 y 加一条竖线、首行图例、`maxRows > 0` 只输出顶部若干行，地图北端在上）。委托 = `GridCellCodeReader(int x, int y)` / `GridCellCharReader(int x, int y)`，**签名是 (x, y)**（与 `GridUtil` / `Vector2Int` 同序，⛔ 不是 (row, col)；越界由调用方自己给安全值）。用途：同 seed **离线可断言**的最小证据面（一行哈希 + 一张字符画）—— prime 写错或逐格顺序不一致都**不报错**，只表现为"两条日志看起来都对、却永远对不上"。`ToAscii` 每次调用分配一个 `StringBuilder`（诊断路径，⛔ 不进热循环） | 无对应（客户端通用底座） |
| PathFollower | **沿 A* 结果逐格推进 + 朝向**（`Runtime/Core/PathFollower.cs`，`sealed class`，无 MonoBehaviour）：`new PathFollower(IsoLayout iso, float minMoveSpeed, float repathIntervalSeconds)` → `SnapTo(grid)`（落格：进图 / 刷怪 / 复活）/ `SetPath(path, target)`（喂 `AStar.Find` 的结果；`null` 或路径 ≤ 1 点 ⇒ 判"无路径"，与"起点 == 终点返回单元素列表"对齐）/ `ClearPath()` / `HasRemainingPath` / `Advance(tilesPerSecond, dt)`（每 tick 一次）/ `StepToward(targetPos, speed, dt)`（逃跑 / 紧急脱身：**直线**走一步，**不做寻路**）/ `Grid` / 静态 `Center(Vector2Int)`；字段 `Pos` / `Dir`（`Dir8`，默认 `S`）/ `Path` / `PathIndex` / `PathTarget` / `HasPathTarget` / `RepathTimer`；只读 `MinMoveSpeed` / `RepathIntervalSeconds`。坐标口径 = **格中心制**（格 (gx,gy) 的中心是 (gx+0.5, gy+0.5)，`Pos` 允许落在两格之间）；`Grid` **必须 `FloorToInt`**（⛔ 不用 `(int)` 强转：负数向零截断会让格错半格且**不报错**）；入参速度低于 `MinMoveSpeed` 时**按 `MinMoveSpeed` 处理**。朝向的唯一真相 = **注入的** `IsoLayout.DirectionTo`（⛔ 不自己抄一张方向表 —— 复制第二份必然再次漂移，且漂移不报错，只表现为"朝向看着别扭"）；⛔ 不碰 `HalfW` / `HalfH`。与 `AStar` 分工：`AStar` 产出逐格路径，本件只**沿路走**（不寻路、不查可走性）。首个消费方 = diablo2 `Module/Monster/MonsterRuntime.cs`（已退化薄转发） | 无对应（客户端通用底座） |

---

## 4. Game 门面（启动与流程）

```text
Game.Launch(config)   // 初始化核心子系统 + 执行启动钩子（不联网 / 不登录 / 不进场景；配表、资源、网络需另调 CloverData.InitDataTable / CloverRes.Init / CloverNet.Init）
Game.ConfigureHost(opts) // 宿主开关（EngineHostOptions）：RunInBackground / DuplicateInstanceGuard / OwnAudioListener；**默认全「不干预」**（不调用 = 与既有行为逐字一致）
Game.Net / Game.Http  // 网络域入口
Game.Sync             // 世界镜像入口
Game.LanBrowser       // 局域网寻服（发现端）入口（CloverLan.Init 挂接；寻服 → 选主机 → 再 CloverNet.Init）
                      // 应答端**不经门面**：CloverLan.CreateResponder() → ILanResponder（Start / Stop / Dispose）
Game.NewFsm()         // 取一棵**独立**状态机（每个 Bot / 单位一棵；Game.Tick 只驱动 Game.Fsm 那一份）
Game.Scene / Game.Entity / Game.Map / Game.Res / Game.Pool
Game.UI / Game.Atlas / Game.Anim / Game.Sound / Game.Input / Game.Camera
Game.Event / Game.Timer / Game.Fsm / Game.Table / Game.Setting
LogThrottle / LogBuffer  // 静态类，直接 CloverEngine.LogThrottle.X / CloverEngine.LogBuffer.X 用，**不经 Game 门面**
                         // （日志降频闸门：时间口径 + 计数口径 / 运行时日志环形缓冲：最近 N 行只读窗口）
```

- **流程状态机**：启动 → 热更检查 → 登录 → 主城 → 战斗 … 用 `Fsm` 实现；**与 Scene 模块无内置联动**，切场景由业务在状态回调里调 `Game.Scene.Load`。
- 引擎自持隐藏 `MonoBehaviour` 宿主驱动 Update，业务无需挂脚本。
- **每帧驱动**：`Game.Tick` 统一驱动 `Input / Dispatcher / Timer / Fsm / Net / Sync / Res / UI / Anim / Camera / Quality`（`Runtime/Core/Game.cs`）。
- **宿主开关**：`Game.ConfigureHost(EngineHostOptions)`（`Runtime/Core/EngineRunner.cs`）—— 三项都是"每个新项目都要在自建 Bootstrap 里重写一遍"的宿主级动作，写错的后果都**静默**：
  `RunInBackground`（`bool?`；`null` = 不干预。跑自动化验证 / 无人值守截图时必须开：编辑器窗口一失焦 `Time.frameCount` 就冻住（实测两分钟走 2 帧），现象是"定时器不触发、流程卡在启动画面"，看起来像状态机坏了）；
  `DuplicateInstanceGuard`（两个驱动器 = `Game.Tick` 每帧跑两遍，位移 / 计时 / 网络重传全部加倍，且 `Game.Shutdown` 可能被先销毁的那个提前触发 —— **不报任何错**，只表现为"什么都快了一倍 / 抖 / 刚进场景就被关了"）；
  `OwnAudioListener`（监听器保证在 `DontDestroyOnLoad` 宿主上；挂场景相机会随切场景被销毁，Unity 每帧刷一条 "There are no audio listeners in the scene"，实测把 Editor.log 刷到 74MB；⚠️ **代价**：宿主在原点 ⇒ **3D 空间音效按原点算距离衰减**，`PlaySFXAt` 的远近衰减对玩法有意义的项目**别开这一项**）。
  生效时机：`RunInBackground` 有值即**立刻**生效（宿主已存在也有效）；另两项是"创建宿主时执行一次"的动作，**必须早于 `Game.Launch`**，宿主已存在时才调用会记一条 Warn（不静默失效）。
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

**命名标记点段（格式 bit2）**：`MapBakeOptions.MarkerRootName` 指定一个**根对象**，其下每个子物体 → 一个标记点（**对象名 = 标记名**、世界坐标 = 点位、Y 取**真实高度**；同名多点允许，按文件顺序）。**留空 = 不导出该段**（既不写段、也不置 `FlagMarkers` 位 ⇒ 产物与旧版逐字节一致）；配了根对象名却在场景根对象里找不到 ⇒ 只打 Warn、产出**不含**标记段（客户端按名取点会全部落空）。段布局（小端，**追加在文件末尾**）：`u32 count → repeat{ u32 name_len, byte[name_len] name, f32 x,y,z }`；单条定长部分 = `CloverMapFormat.MarkerStride`（16 字节）。读取端：客户端 `Game.Map.Points` / `GetPoints(name)` / `TryGetPoint`，服务端 `pkg/domain/mmo/mapdata`（`readMarkers`，只校验完整性、暂不消费）—— 写 / 客户端读 / 服务端读三处实现必须同步改。

**层过滤（多层地图）**：`MapBakeOptions.LayerFilterEnabled` / `LayerMin` / `LayerMax` / `LayerMinY` / `LayerMaxY`（**默认关**；关着时 `ShouldBakeCollider` 恒返回 true ⇒ 与旧版逐字节一致）。开关打开时判据 = Unity Layer 落在 `[LayerMin, LayerMax]`（闭区间）**且**碰撞体垂直跨度与高度带 `[LayerMinY, LayerMaxY]` **相交**（`LayerMaxY <= LayerMinY` = 不设上界）—— 用"相交"而非"完全包含"，是为了让跨层的大墙 / 立柱**仍然算障碍**，只有整层落在带外的楼层（典型：上层楼板）被排除。真·逐格高度场（`FlagHeightField`）列 V2；在它落地前，多层地图靠**逐层各烘一份单层位图**（一份数据一个楼层）。

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

客户端引擎的编辑器程序集横切共 **5 个**：**Debugger（§5.1）、MapBake（§5.2）、EditorStartScene（§5.3）、PixelArtImport（§5.5）、PanelPrefabBuilder（§5.6）**。
配表代码生成由仓库的**打表工具**（[`clover-tools/table`](https://github.com/qw576483/clover-tools/blob/main/table/README.md)）负责：
源表 → tsv + 强类型 C# 代码；生成物禁止手改。

明确**不做代码生成**的部分：`EMsg` 消息号、`Protocol` DTO、动画参数常量、多语言 key ——
全部**手工维护**，多处（两端）必须保持一致；**不提供 Generator，也不提供自动生号**。

### 5.5 PixelArtImport（像素素材导入规范）

`Clover/像素素材导入/` —— 像素素材的导入规范：PPU / 滤波 = Point / 不压缩 / 无 mipmap / 按"目录 → 轴心"规则表。
**默认不生效**：开关与配置资产路径存 EditorPrefs、**按工程路径隔离**、默认关；必须用**配置资产**显式开启。

| 入口（`Clover/像素素材导入/`） | 位置 | 说明 |
|---|---|---|
| `启用（按配置资产处理导入的纹理）` | `Editor/PixelArtImportPostprocessor.cs`（常量 `MenuEnabled`，勾选项） | 总开关。**关着 / 没选过配置资产 / 资产已失效** ⇒ `ActiveSettings` 返回 null、后处理器什么都不做（"默认不生效"的落地口径）；配置资产取不到时每会话 Warning 一次，不让"开关看着是开的却毫无效果"静默 |
| `创建或选择配置资产…` | 同上（`MenuPickSettings`） | 创建 / 选中 `PixelArtImportSettings` 资产并登记为当前配置 |
| `打印配置摘要与自检` | 同上（`MenuReport`） | 只读，不改任何东西 |
| `用当前配置重导选中纹理` | 同上（`MenuReimportSelection`） | 导入钩子**不会回溯**已导入的素材 ⇒ 改规则后用这条重导 |

配置资产类型 `PixelArtImportSettings`（`Editor/PixelArtImportSettings.cs`）：规则表按"目录前缀 → 轴心"匹配，规则表为空时不处理任何纹理。

### 5.6 PanelPrefabBuilder（面板壳预制体）

`Clover/面板壳预制体/` —— 一键生成面板壳预制体（只有"一个挂好面板组件、**铺满父层**的 RectTransform 根节点"，内容由面板自己在 `OnOpen/Awake` 里搭）。目录固定 `Assets/Resources/UI`（这正是 `UIManager` 默认的查法：按 key `"UI/{类名}"` 取，见 `Runtime/Presentation/UI.cs:134`，换别的目录默认就查不到）。重复执行安全（已存在的同名预制体会被覆盖重建）。

| 入口（`Clover/面板壳预制体/`） | 说明 |
|---|---|
| `扫描 IUIPanel 实现并全部生成` | 扫全部 `IUIPanel` 实现，逐个生成 `Resources/UI/{类名}.prefab`；失败的列成清单 |
| `打印待生成清单（只读）` | 只列清单、不写盘 |
| `只为选中的面板脚本生成` | 只处理 Project 里选中的脚本 |

⛔ **未改 `CloverPresentation.PanelProvider` 的默认行为**（默认来源仍是 `Resources/UI/{类名}`）。
⚠️ **两条面板供给路线并存**（靠同一个函数槽切换，⛔ 谁都不取代谁）：① 本行的 **prefab 路线**（默认 / 需要编辑器里调版面时用）；② `RuntimePanelProvider` 的**运行时路线**（零资产、纯代码建面板，见 §3.2 UI 表末行）。
存盘**三关校验**（`m_Script` 引用非 0）：存盘前查 `MonoScript`、存盘后回读资产查组件、再读预制体文件文本查 `m_Script: {fileID: 0}` —— 脚本尚未导入时 `SaveAsPrefabAsset` 会把引用写成 0，预制体存在、**编译不报错、运行时组件为 null**（表现是 `Component X not found on prefab`）。

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

---

## 本批补登（2026-09-24 收尾，主 agent 直接落盘）

> 以下 5 件在上一轮登记时遗漏（其余同批新件已由 `final-docs` 登记）。签名均回读源码逐字核实。

| 能力 | 所在文件:行 | 公开签名 / 入口 | 一句话用途 | 已知缺口 / 限制 |
|---|---|---|---|---|
| `ChunkedTilePlanner`（大地图分块规划） | `Runtime/Presentation/ChunkedTilePlanner.cs:60` | `BeginFrame()` / `TryAccept(int nodeCost)` / `ChunkRangeOf(...)` / `EnumerateChunks(...)` / `FloorDiv(int)` / `RequestRebuild()` / `ConsumeRebuild()` / `FrontBuffer` · `BackBuffer` · `MaxNodesPerFrame`（0=不限） | 可见块范围 → 每帧节点预算准入 → 双缓冲换帧（**纯逻辑**） | ⛔ 不碰渲染、不建节点、不查素材；负坐标必须走 `FloorDiv`（C# `/` 向 0 截断）。项目侧 `MapView.cs` **尚未接线** |
| `TilemapGenUtil`（程序化瓦片） | `Runtime/Presentation/TilemapGenUtil.cs` | `Dir4` / `Dir4Mask` / `EdgeRequirement` + 拼块与生成树入口 | 块级随机 DFS 生成树 + 环路；按四边开口/镜像拼 tileset + Stamp 叠加 | 块库 / 组码 / 原版规则表**全部由调用方注入**；项目侧 `MapGenCave` / `MapGenWilderness` **尚未接线** |
| `LoadingPacing`（读条分档节奏） | `Runtime/Presentation/LoadingPacing.cs` | `SceneProgressCeiling`(0.9) / `DefaultProgressShare`(0.5) + 进度↔档位与放行判据 | 把"场景加载进度 + 档位"编排成稳定读条节奏 | ⛔ 帧数 / 档位取值属业务（引擎只给机制） |
| `CameraBoundsKit`（格空间相机夹制） | `Runtime/Presentation/CameraBoundsKit.cs` | `ClampCameraGrid(...)` / `ClampFocusGrid(...)` / `ClampSpan(...)` | 可见格矩形 ⊆ 地图 + 焦点安全边距（**格空间**口径） | ⛔ 与 `Camera.cs` 的世界 AABB 钳位口径不同；依赖等距参数（`IsoLayout`） |
| `SceneScaffold`（Editor 横切） | `Editor/SceneScaffold.cs` | `Create(scenePath, SceneScaffoldOptions, out error)` / `ApplyBuildSettings(IList<string>)` / `SetPlayModeStartScene(string)` / `FindTypeByName` / `FindTypeBySimpleName` / `ExecuteFromCommandLine()`（`-executeMethod`）/ 菜单 `Clover/场景脚手架/…` | 生成最小可运行场景 + 写 Build Settings + 设 Play 起始场景 + 反射类型解析 + 启动自愈 | ⛔ 不含任何业务取值；URP `Light2D` 走**反射可选接入**（Editor 程序集不硬引用 URP）。项目侧 `ProjectBuilder.cs` **尚未接线** |

### 项目侧接线状态（2026-09-24 主 agent 收尾，逐项如实）

| 引擎件 | 项目侧接线 | 说明 |
|---|---|---|
| `ChunkedTilePlanner` | ✅ `Module/Map/MapView.cs` | 块枚举经 `AppendChunkCoords(xOuter:true)`（**顺序逐字保留** ⇒ 画面逐像素不变）；`FrameAccepts` **保留**（它还需"格数"维度，引擎件只建模节点维度） |
| `TileRenderer` | ✅ `Module/Map/MapView.cs` | `LocalScaleFor` / `ColorFor` / `HeightPxOf` / `PlaceOfPx` 改薄转发；度量口径同源（PPU 64 / 格图 80 / 半格高同源） |
| `SceneScaffold` | ✅ `client/Assets/Editor/ProjectBuilder.cs` | 场景生成 / BuildSettings / Play 起始场景三处全部转发；项目取值经 `SceneScaffoldOptions` 传入 |
| 切图规则（`PixelArtSlicingRule` + `IPixelArtSlicer`） | ✅ `client/Assets/Editor/D2PixelArtSlicer.cs` | 引擎出规则与矩形、**工程出适配器**（U2D provider 属包程序集，引擎不引）；未注册切图器时**明确告警** |
| `TilemapGenUtil` | ⛔ **未接线** | 理由：切换它要重写 `MapGenCave` 的块级 DFS 与 `MapGenWilderness` 的开口拼块（**生成结果 = 画面与地图基线判据**）；本机无 Unity 实例、无法逐图复核 ⇒ 盲切风险高于收益。引擎件本身已就位、文档已登记，可随时按需切 |
| `SpriteEntityView` | ⛔ **未注册** | 理由：本项目**不经 `Game.Entity` 建视图**（`ViewModule` 自建，二者数据来源与生命周期不同）。注册一份没人走的来源 = 死代码 ⇒ 不注册；将来若切到门面实体，调 `EntityViewFactory.RegisterSource(new SpriteEntityViewSource(layers, spec), asDefault)` 即可 |
| `SpriteAnimator`（项目） | ⛔ **不合并** | 与 `SpriteFrameAnimator` **语义不同**（后者要"已加载的 Sprite[]"并直写渲染器，前者是"帧键时间轴"、支持帧未到位仍在计时）⇒ 项目文件头已写明分工，强行合并是退化 |
