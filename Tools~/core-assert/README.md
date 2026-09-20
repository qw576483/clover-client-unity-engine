# core-assert —— 核心模块「离线编译 + 离线断言」宿主

给 `Runtime/Core/` 里**不依赖 Unity 运行时**的几个文件用：**直接链接真实源码**（不做副本），
在纯 .NET 下编译并跑真断言。改完 `Logger` / `Setting` / `Json` / `Event` 后**不必开 Unity**
就能先确认「能编译 + 行为没坏」，比进编辑器快一个数量级。

> 与 `Tools~/quic-harness/` 的分工：那个查网络/原生互操作（Quic 线路），这个查核心基础件。
> `Tools~/` 目录本身不进 Unity 资源库（Unity 会忽略以 `~` 结尾的目录）。

## 跑法

```bash
cd clover-client-unity-engine/Tools~/core-assert

dotnet run                                  # 常驻平台：落盘 / 原子写 / 往返 / 损坏留档
dotnet run -p:DefineConstants=UNITY_WEBGL    # WebGL：无文件系统 ⇒ 降级为内存存储、不创建目录
```

两种构建**都要跑**：WebGL 分支在常驻平台下是死代码，只跑一种等于漏验一半。
退出码 0 = 全部 PASS；任一 FAIL 会非零退出，可以直接进 CI。

## 覆盖与断言清单

| 文件 | 断言 |
|---|---|
| `Runtime/Core/Json.cs` | 循环引用**抛异常**（不是 StackOverflow）；超深嵌套 parse 报错；`double` 保型（`1.0` 往返仍是 double）；`long` 极值往返不丢精度 |
| `Runtime/Core/Event.cs` | 通配订阅 `Net.*`（恰好一段）/ `Net.**`（一段或多段）命中范围；**精确订阅先于通配执行**；`Off("Net.*")` 注销干净；带参通配收到载荷 |
| `Runtime/Core/Setting.cs` | 写盘后目标文件存在且 **`.tmp` 已原子替换**；新实例往返读回同值；**损坏 JSON 不抛异常**、回退默认值并留档 `.corrupt`；非法目录退化为内存存储（WebGL 构建下：不建目录、不写文件、内存内可读回） |
| `Runtime/Core/Logger.cs` | **同一天的日志也会落盘**（不靠"日期变化"唤醒写线程）；跨天切出 `yyyy-MM-dd.log`；日期由写线程自己取（强制切走后下一条落回当天文件）；目录里不出现 `.log` 这种无名文件（旧缺陷形态） |
| `Runtime/Core/ConsoleLogger.cs` | 作为 `Logger` 的 Console 出口被链接（`ILogger` / `LogLevel` 的真实定义来自 `Logger.cs`，宿主不再重定义） |

## 为什么值得留（而不是"用完就删"）

`Runtime/Core/` 里这几个文件的缺陷**全是静默的**（日志不落盘、设置写坏、JSON 类型悄悄变、
事件订阅收不到），而它们又**不需要 Unity 就能验**。之前这类改动只能"进编辑器点一遍"，
于是长期没人验 —— 本宿主把"能验"变成一条命令。`Stubs.cs` 只补 `UnityEngine.Debug` 与
`CloverEngine.Game.Logger` 两个外部符号，**新增覆盖文件时优先补 stub，不要去复制引擎源码**。

## 注意

- 链接的是 `../../Runtime/Core/*.cs` 的**真实文件**：引擎改了这里立刻反映，不存在"两份代码漂移"。
- 会写临时目录（`%TEMP%/clover-*-assert-*`），跑完自行删除；不碰工程内任何文件。
- `Program.cs` 里那几处 `ReadAllTextShared` 是必须的：日志文件被写线程持有，
  直接 `File.ReadAllText` 会 `IOException`。
