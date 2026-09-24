# quic-harness — QUIC 原生绑定的"秒级复现"试验台

> 一个**不进 Unity、不进包**的控制台程序，用来驱动引擎里那份 **msquic P/Invoke 绑定**。
> 存在的唯一理由：**原生互操作出错的失败形式是"进程级崩溃"，在 Unity 里表现为"编辑器直接消失"** ——
> 没有托管堆栈、没有异常、每次复现还要等 3 分钟编辑器启动 + 1 分钟进 Play。
> 搬到控制台里：**11 秒编译、1 秒复现、退出码可读**。

## 为什么必须"链接真实源码"而不是复制一份

`QuicHarness.csproj` 用 `<Compile Include="../../Runtime/Network/Quic/*.cs" />` **通配**链接
`Quic/` 下的全部真实文件（`MsQuicNative` / `MsQuicRuntime` / `QuicConnection` / `QuicStreamFraming`，
新增文件自动纳入，不会漏链），并另链接 `Connection.cs`（`ClientFrame` / `BigEndian` / `ConnectionState`）
与 `EMsg.cs`（消息号常量，冒烟用例不硬编码号）。只用一个 `Stubs.cs` 补上最小的 Unity 替身
（`Game.Logger` / `TransportKind` / 契约接口 / `MonoPInvokeCallback` 空特性）。

⇒ 这里跑过的代码**就是 Unity 里跑的那份**，不存在"两份代码漂移"
（副作用：这几个文件因此被约束成"不依赖 UnityEngine"，对绑定本身也是好事。）

## 怎么跑

```bash
cd Tools~/quic-harness
dotnet build                     # 11 秒
bin/Debug/net8.0/quic-harness.exe        # 全部步骤（含压力步骤）
bin/Debug/net8.0/quic-harness.exe login  # 只跑"连接 → 请求/回包 → 断开"
```

**前置条件**：本机网关已启用 QUIC（配了 `gateway.tls_cert` / `tls_key`，监听 `127.0.0.1:8003`），
且该证书被本机信任（本地开发用 `mkcert -install`）。

### 模式（用于二分定位）

| 模式 | 内容 |
|---|---|
| `plain` | 只连 + 断（**零收发** —— 用来当"对照组"） |
| `send` | 连 + 发 5 帧可靠数据 + 断 |
| `login` | 连 + 发登录帧 + 收回报 + 断 |
| `dgram` | 连 + 发一帧 Datagram + 断 |
| `multi` | 同一连接连做 3 次请求/回包（**验证收包流控没被堵**） |
| `all` | 全部步骤 + 压力：连接中强关、50 帧在途强关、900KB 大帧强关、重连 3 轮 |

环境变量：`CLOVER_QUIC_TRACE=1` 打开**逐调用跟踪**（默认关，正常跑不刷屏）。

## 一个原生崩溃样本（方法论示例）

**现象**：Unity 编辑器被**原生断言**打死 3 次 —— Windows 事件日志
`Faulting module: msquic.dll, version 2.4.16.0`，异常码 **`0xC0000420`**（STATUS_ASSERTION_FAILURE）。

**定位过程**（这就是"逐步 + 每步先打标记 + 每条日志 flush"的原因）：

1. 试验台崩在 `[STEP] 主动 Disconnect` ⇒ 先按**模式**二分：
   `plain`（零收发）**永远不崩**，`send`/`login`/`dgram`（都收过数据）**都崩**；
2. ⇒ 判别项是"**收到过流数据**"，怀疑收包记账；
3. 打印 `totalLen/copied/buffers` ⇒ **数值本身完全正确**（79=79）⇒ 不是算错；
4. 才转向"**这个 API 本就不该在这里调**"：

**口径依据**：`StreamReceiveComplete` **只用于"挂起（PEND）式"收包**。
我们在回调里把数据同步拷进托管内存、返回 `SUCCESS`，就等于"已消费完毕" ——
**再调一次会把 msquic 的收包记账写坏**，代价不是立刻报错，而是**之后关闭连接时原生断言崩溃**。

**正确做法**：同步消费就**不要再调** `StreamReceiveComplete`（`QuicConnection.cs` 的 `StreamEvent.Receive` 分支有长注释）。
该口径下的实测结果：`plain/send/login/dgram/multi/all` **全部退出码 0**，压力步骤全过。

## 两条方法论（来自实测）

1. **进程级崩溃时，stdout 缓冲会整体丢失** —— 你看到的"最后一条日志"可能是假象。
   ⇒ 试验台的日志**每条都 `Flush()`**（`Stubs.cs` 的 `ConsoleHarnessLogger`）。
   （只按"最后一条日志"判会误判崩点：真实崩点可能在它之后更远处。）
2. **每一步先打标记，再动手** —— 崩溃本身没有堆栈，**最后打印出来的标记 = 崩在下一步**。

## 与 Unity 侧测试的分工

| | 试验台（本目录） | `Tests/PlayMode/QuicLoopbackTests.cs` |
|---|---|---|
| 速度 | 秒级 | 分钟级（要起编辑器） |
| 能查什么 | 原生绑定/生命周期/崩溃 | 引擎侧使用链（`CloverNet`/`NetworkManager` 真的走 QUIC） |
| 默认跑吗 | 手动跑 | **`CLOVER_QUIC_E2E=1` 才跑**（会真连网关，默认跳过避免误报） |

两者都在 `Runtime/Plugins/x86_64/msquic.dll` 上跑，**崩溃排查一律先用试验台**，确认干净了再进 Unity 复跑。
