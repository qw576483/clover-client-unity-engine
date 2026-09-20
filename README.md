# clover-client-unity-engine

Clover 的 **Unity C# 客户端引擎**，以 UPM 包形式分发（包名 `com.clover.unity-engine`）。

对标服务端 [clover-server-engine](https://github.com/qw576483/clover-server-engine)：两端只在**两处**对齐 ——
① API 语义（`OnMsg` / `On` / `Timer.After|Every` / `Fsm.Trigger` 的拼写与语义）；
② 网络协议（帧格式、EMsg 消息号、ObjectID 位布局逐字节一致）。**除此之外不镜像服务端目录结构**，客户端按自己的能力域组织。

## 环境要求

| 项 | 要求 |
|---|---|
| Unity | **6000.0（Unity 6）** 及以上 |
| 依赖 | `com.unity.ugui`（Unity 内置包） |

## 安装

**UPM（推荐）**：Package Manager → `+` → *Add package from git URL*，填：

```
https://github.com/qw576483/clover-client-unity-engine.git
```

**本地**：*Add package from disk*，选择本目录下的 `package.json`。

## 目录结构

```text
clover-client-unity-engine/
├── Runtime/
│   ├── Core/         基础域：Game / Event / Timer / Fsm / Dispatcher / Logger / LogThrottle
│   │                 / LogBuffer / Setting / Json / DeviceId，以及全部跨模块契约（含协议载体类型）
│   ├── Data/         数据域：CloverData / DataTable / Localization / CloverTable（读打表产物）/ FileSlotStore
│   ├── Network/      网络域：Network / WebRequest / WorldSync / CloverAuth / SchemaRegistry
│   │                 / Lan（局域网寻服：UDP 旁路发现）
│   ├── Resource/     资源域：后端抽象 / Resources / AssetBundle / 清单热更与下载器
│   ├── Presentation/ 表现域：Scene / Entity / ObjectPool / Map（逻辑地图）/ UI / UIWidgets
│   │                 / TextHooks / SpriteAtlas / Animation / Sound / Input / Camera / Quality
│   └── Plugins/      原生插件落点（已入库 msquic：x86_64/msquic.dll 与 Android arm64-v8a / armeabi-v7a / x86_64）
├── Editor/           Editor 横切：Debugger（面板 / GM 控制台 / 网络模拟）、MapBake（Unity 关卡 → CloverMap 二进制）
├── Tests/            EditMode + PlayMode 测试
├── Samples~/         UPM 示例（LoginFlow）
├── Tools~/           工具与说明（`~` 结尾，Unity 不编译）：core-assert、quic-harness、native
└── package.json
```

> 以 `~` 结尾的目录（`Samples~` / `Tools~`）Unity 完全不扫描，**不能**放运行时代码。

## 依赖规则（asmdef 编译期强制）

```text
CloverEngine.Core          → []            （基础域，不依赖任何人）
CloverEngine.Data          → [Core]
CloverEngine.Network       → [Core]
CloverEngine.Resource      → [Core]
CloverEngine.Presentation  → [Core]
```

各模块程序集**只引用 `Core`，模块之间互不引用**；`Game` 门面在 `Core`，负责聚合。

## 示例

`Samples~/LoginFlow`：注册 → 登录 → 会话建立 → 全量同步 → 断线恢复的完整接入流程。

## 文档

| 文件 | 内容 |
|---|---|
| [`结构规则.md`](结构规则.md) | **结构铁律**：目录归属、依赖方向、命名、契约、评审口令，以及网络与会话契约（N1–N13）、通用硬约束（G1–G13）——**最该先读** |
| [`clover-client-unity-engine-index.md`](clover-client-unity-engine-index.md) | 索引：有什么、在哪、怎么读（能力域、模块总览、包结构） |
| [`修复记录.md`](修复记录.md) | E 编号体系的缺陷修复记录（与服务端的 S 编号是两套独立体系） |

## 原生库

`Runtime/Plugins/` 下的 msquic 二进制已入库；重新构建原生库（msquic / quictls）见 `Tools~/native/README.md`，构建中间产物不入库。
