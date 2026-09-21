# Clover Unity Client Engine

Clover 的 **Unity C# 客户端引擎**，以 UPM 包形式分发（包名 `com.clover.unity-engine`）。

与服务端 [clover-server-engine](https://github.com/qw576483/clover-server-engine) 配套：两端只在**两处**对齐 ——
① API 语义（`OnMsg` / `On` / `Timer.After|Every` / `Fsm.Trigger` 的拼写与语义）；
② 网络协议（帧格式、EMsg 消息号、ObjectID 位布局逐字节一致）。除此之外客户端按自己的能力域组织，不镜像服务端目录。

## 环境要求

| 项 | 要求 |
|---|---|
| Unity | **6000.0（Unity 6）** 及以上 |
| 依赖 | `com.unity.ugui`（Unity 内置包） |

## 安装

**UPM（推荐）**：Unity → Window → Package Manager → `+` → *Add package from git URL*，填：

```
https://github.com/qw576483/clover-client-unity-engine.git
```

**本地**：`+` → *Add package from disk*，选择本目录下的 `package.json`。

## 快速开始

```csharp
using CloverEngine;

var config = new GameConfig
{
    ServerAddr = "127.0.0.1:8002",   // 网关 TCP 口（服务端 gateway.listen_tcp）
    MaxReconnectCount = 5,
    UseTls = true,                   // 与服务端 gateway.tcp_tls_disabled 相反（默认 false 时这里 true）
};

Game.Launch(config);                 // 启动引擎（自动挂载表现域各模块）

// 第 1 参 = 网关 TCP 口；第 2 参 = 网关 UDP 口（gateway.listen_udp），留空 = 不启用不可靠通道
CloverNet.Init("127.0.0.1:8002", "127.0.0.1:8003");
```

完整链路（注册 → 登录 → 会话建立 → 全量同步 → 断线恢复）见包内示例 `Samples~/LoginFlow`。

## 能力域

| 域 | 内容 |
|---|---|
| `Runtime/Core` | Game 门面 / Event / Timer / Fsm / Dispatcher / Logger / Setting / Json，以及全部跨模块契约 |
| `Runtime/Data` | CloverData / DataTable / Localization / CloverTable（读打表产物）/ FileSlotStore |
| `Runtime/Network` | Network / WorldSync / WebRequest / CloverAuth / SchemaRegistry / Lan（局域网寻服） |
| `Runtime/Resource` | 后端抽象 / Resources / AssetBundle / 清单热更与下载器 |
| `Runtime/Presentation` | Scene / Entity / ObjectPool / Map / UI / UIWidgets / SpriteAtlas / Animation / Sound / Input / Camera / Quality |
| `Editor` | Debugger（面板 / GM 控制台 / 网络模拟）、MapBake（Unity 关卡 → CloverMap 二进制） |

各模块程序集**只引用 `Core`**（`CloverEngine.Data` / `.Network` / `.Resource` / `.Presentation` → `[Core]`），模块之间互不引用；`Game` 门面在 `Core`，负责聚合。

## 文档

| 文件 | 内容 |
|---|---|
| [`结构规则.md`](结构规则.md) | 结构铁律：目录归属、依赖方向、命名、契约，以及网络与会话契约（N1–N13）、通用硬约束（G1–G13） |
| [`clover-client-unity-engine-index.md`](clover-client-unity-engine-index.md) | 索引：包结构、能力域、模块总览 |
| [clover-doc](https://github.com/qw576483/clover-doc) | 在线文档（`client/` 一节） |

## 相关仓库

| 仓库 | 说明 |
|---|---|
| [clover-server-engine](https://github.com/qw576483/clover-server-engine) | Go 服务端引擎 |
| [clover-doc](https://github.com/qw576483/clover-doc) | 框架文档 |
| [clover-tools](https://github.com/qw576483/clover-tools) | 打表工具（生成客户端强类型表代码） |

## 许可证

[MIT](LICENSE)
