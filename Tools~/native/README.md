# QUIC 原生插件（msquic）—— 二进制清单与产出方式

> 目录名带 `~` 后缀，Unity 不会导入本目录（UPM 的 dev-only 工具约定），但 git 会跟踪。
> 本文件是「客户端 QUIC 线路」的原生依赖说明。**改这里之前先读完**，否则很容易把某个平台的包打坏。

## 为什么需要插件

服务端 QUIC 已就绪（[`clover-server-engine/internal/transport/net/quic`](https://github.com/qw576483/clover-server-engine/blob/main/internal/transport/net/quic.md)，ALPN `clover-quic`，
流上 `[4B 大端 len][客户端帧]`，不可靠走 Datagram）。客户端缺的只是**原生 QUIC 栈**：
Unity 的 .NET Standard 2.1 档案没有 `System.Net.Quic`（实测 Unity 6000.6 的
`NetStandard/ref/2.1.0`、`Managed`、`MonoBleedingEdge` 三处均无该程序集）。

选择 **msquic** 的理由是**一套 C# 绑定五端复用**：msquic 是同一套 C API，
官方支持 Windows / Linux / macOS / iOS / Android。C# 侧的库名由 `MsQuicNative.LibraryName` 决定 ——
Windows 解析成 `msquic.dll`、Android/Linux 解析成 `libmsquic.so`；
**iOS 是唯一的例外**：静态库必须走 `DllImport("__Internal")`（符号由 Xcode 工程链接期从
`Runtime/Plugins/iOS/libmsquic.a` 解析，写 `"msquic"` 永远解析不到），
`LibraryName` 已在 iOS 构建分支里自动切换 —— **换平台时无需改 C#**，只需要放对二进制。

## 二进制放置位置（必须遵守 Unity 的目录约定）

Unity 只按**固定的特殊目录名**识别平台插件。放错目录会让插件变成「Any Platform」，
从而被塞进 Android / iOS 包 —— 这是最容易踩、且只在出包时才炸的坑。

| 平台 | 放置路径 | 文件 | 状态 |
| --- | --- | --- | --- |
| Windows x64（Editor + Standalone） | `Runtime/Plugins/x86_64/` | `msquic.dll` | ✅ 已就位（Schannel 版，只依赖 `bcrypt`/`ncrypt`） |
| Linux x64 | `Runtime/Plugins/x86_64/` | `libmsquic.so` | ⬜ 未做（`Tools~/native/` 脚本可改造复用） |
| macOS（x64 / arm64） | `Runtime/Plugins/x86_64/` | `libmsquic.dylib` | ⬜ 未做 |
| **Android arm64-v8a** | `Runtime/Plugins/Android/libs/arm64-v8a/` | `libmsquic.so` | ✅ **已产出**（3.58 MB，ELF64/AArch64，见下） |
| **Android armeabi-v7a** | `Runtime/Plugins/Android/libs/armeabi-v7a/` | `libmsquic.so` | ✅ **已产出**（2.72 MB，ELF32/ARM） |
| **Android x86_64** | `Runtime/Plugins/Android/libs/x86_64/` | `libmsquic.so` | ✅ **已产出**（7.1 MB，ELF64/x86-64；x86_64 模拟器用，补齐此前缺的 ABI） |
| iOS | `Runtime/Plugins/iOS/` | `libmsquic.a` | ⬜ 需 mac + Xcode（步骤见文末） |

`.meta` 必须**逐个平台收紧**（`msquic.dll.meta` 是范例：仅 Editor(Windows) 与 Standalone Win64 启用，
Any Platform / Linux64 / OSX / Windows Store 全关）。Android 的 `.so` 同理：只启用 `Android`，
`CPU` 分别写 `ARM64` / `ARMv7`，其余平台一律关闭 —— 否则会漏进别的平台包。

## 产出方式

### PC 平台（可直接取预编译，无需构建环境）

msquic 官方 NuGet 提供各 PC 平台的原生库：

```powershell
dotnet new classlib -o $env:TEMP\msquic-fetch
dotnet add $env:TEMP\msquic-fetch\msquic-fetch.csproj package Microsoft.Native.Quic.MsQuic.Schannel
dotnet add $env:TEMP\msquic-fetch\msquic-fetch.csproj package Microsoft.Native.Quic.MsQuic.OpenSSL

# 按需拷到 Plugins 目录
#   win-x64\msquic.dll      -> Runtime/Plugins/x86_64/msquic.dll
#   linux-x64\libmsquic.so  -> Runtime/Plugins/x86_64/libmsquic.so
#   osx-x64\libmsquic.dylib -> Runtime/Plugins/x86_64/libmsquic.dylib
```

> Windows 用 **Schannel** 变体（无 OpenSSL 依赖）；Linux / macOS / **Android / iOS** 用 **OpenSSL** 变体
> （Android 与 iOS 官方**不发布**预编译，必须自己编）。

### Android（本目录脚本，已跑通）

```bash
# 必须经 Git-Bash 跑（原因见「为什么这么绕」第 1 条）
Tools~/native/build-android-quic.cmd                 # 默认 arm64-v8a
Tools~/native/build-android-quic.cmd armeabi-v7a
Tools~/native/build-android-quic.cmd x86_64          # x86_64 模拟器
CLEAN=1 .../build-android-quic.cmd                                             # 清理后全量重编
```

环境变量（都可省）：`ANDROID_NDK_ROOT`（默认取 Unity 自带 NDK r27c）、`ANDROID_API`（默认 26，
与工程 `AndroidMinSdkVersion` 一致）、`MSQUIC_TAG`（默认 `v2.4.16`，与 Windows 版 dll 的 FileVersion 对齐）、
`QUIC_BUILD_ROOT`（默认 `<仓库根>/_native_build`，不进库）。

脚本末尾**自动自检**（这三项就是"能不能用"的判据）：

```
Class: ELF64 | Machine: AArch64              ← 架构对不对（v7a 应为 ELF32/ARM）
NEEDED: libdl.so, libm.so, libc.so           ← 不得出现 libssl/libcrypto（quictls 必须静态链入）
dyn-syms: MsQuicOpenVersion, MsQuicClose     ← 只应有这两个导出（C# 只 P/Invoke 这两个）
```

### 为什么这么绕（Windows 宿主踩过的 7 个坑，已全部固化进脚本/补丁）

msquic 上游**本身就支持 Android**（`submodules/CMakeLists.txt` 会按 `ANDROID_ABI` 把仓库内的
quictls 子模块交叉编译成静态库），但它假设宿主是 Linux/macOS。Windows 上必须逐条绕过下面这些坑
—— 每条都曾让构建停在看似无关的报错上：

1. **必须用 Git-Bash(msys) 当宿主**：quictls 的 `Configure` 用 `which("clang")` 与
   `$ANDROID_NDK_ROOT` 做正斜杠前缀匹配来识别 NDK，原生 Windows perl 拿到的是反斜杠 → 直接 die；
   NDK 的 `aarch64-linux-androidNN-clang` 是 `.cmd` 包装器（CreateProcess 只认 `.exe`），
   只有 msys 的 `sh` 执行得了；quictls 给 `RANLIB` 用的是 `:`（sh 内建）。
2. **Git 自带 perl 是精简版**：缺 `Locale::Maketext::Simple` / `Params::Check` / `Pod::Usage`
   等**核心**模块（quictls 的依赖链：`OpenSSL::config → IPC::Cmd → Params::Check →
   Locale::Maketext::Simple`）。脚本从 MSYS2 官方仓库取一份完整模块树（7 MB）放进构建区，
   用垫片注入。⚠️ **不要注入 `usr/lib/perl5/core_perl`**（架构相关、含 `Config.pm`，
   版本不匹配会直接报 `Perl lib version ... doesn't match`）。
3. **不能靠 `PERL5LIB` 传路径**：环境变量在 `msys → 原生 make.exe` 这一跳会被改写（实测连冒号都丢）。
   改用 **perl 垫片**（自己设 `PERL5LIB` 后 `exec` 真 perl —— 垫片→perl 是 msys 内部跳转、不改写）
   **+ `PERL5OPT` 兜底**：因为 OpenSSL 生成的 Makefile 里**硬编码了 `PERL=/usr/bin/perl`**，绕过 PATH。
4. **quictls 的 NDK 路径识别要打补丁**（`patch-quictls-android-ndk.py`）：`$ANDROID_NDK_ROOT`
   （经 msys→原生跳转后成 `C:\...`）与 `which()` 结果（`/c/...`）形式不一致，前缀匹配必然失败，
   而它报的却是极具误导性的 `no NDK aarch64-linux-android-gcc on $PATH`。补丁把两边归一化成 msys
   形式，并在 die 消息里带上真实取值（下次不用再猜）。
5. **`make.exe` 必须先复制到无空格路径**：OpenSSL 的 Makefile 会用 `$(MAKE)` 递归调用自己，
   而 NDK 的 make 在 `C:/Program Files/...`（含空格），recipe 未加引号 → sh 收到 `C:/Program`，
   报 `No such file or directory`（Error 127）。
6. **msquic 的 `glob()` 要打补丁**（`patch-msquic-android.py`）：`selfsign_openssl.c` 用了 bionic
   **API 28 才有**的 `glob/globfree`，而工程 minSdk=26。该功能只服务「生成自签证书」这条辅助路径
   （客户端与网关都不用），故给一个「永远找不到」的替身，不为此抬高整包最低 API。
7. **遇到"头文件内容不对"就先 `CLEAN=1`**：OpenSSL 生成头文件的规则是 `$(PERL) ... > $@`，
   **重定向会先截断目标文件**；那次 perl 一旦失败就留下 0 字节头文件，而 make 只看时间戳、不再重做
   → 后续编译报一堆无关错误（实测 `unknown type name 'CRYPTO_RWLOCK'`）。

### iOS（必须 mac + Xcode，Windows 上没有合法途径拿到 iOS SDK）

```bash
brew install cmake
git clone --depth 1 --branch v2.4.16 https://github.com/microsoft/msquic.git
cd msquic && git submodule update --init --depth 1 submodules/openssl3
cmake -S . -B build-ios -G Xcode \
  -DCMAKE_SYSTEM_NAME=iOS -DCMAKE_OSX_ARCHITECTURES=arm64 \
  -DCMAKE_OSX_DEPLOYMENT_TARGET=13.0 \
  -DQUIC_TLS=openssl3 -DQUIC_BUILD_SHARED=OFF -DQUIC_BUILD_TEST=OFF -DQUIC_BUILD_TOOLS=OFF
cmake --build build-ios --config Release --target msquic
# 产出 libmsquic.a -> Runtime/Plugins/iOS/libmsquic.a（并按上面的原则写 .a 的 .meta）
```

（msquic 的 `submodules/CMakeLists.txt` 有 `darwin` 分支，会自己把 quictls 编成 iOS 静态库。）
放好后 C# 侧无需改动：`MsQuicNative.LibraryName` 在 iOS 构建分支里已切到 `__Internal`，
Xcode 链接期从 `libmsquic.a` 解析符号。

> 注：msquic 的 Android/iOS 支持**未经官方自动化验证**（microsoft/msquic#4041），出问题需要自己扛 ——
> 本轮 Android 就是这样踩出来的，上面的清单就是它的"病历"。

## 产物入库策略

- **提交**：`Runtime/Plugins/**` 下的二进制与 `.meta`、本目录的脚本与补丁。
- **不提交**：`<仓库根>/_native_build/`（msquic/quictls 源码、perl 模块树、`out/` 构建产物），
  已在根 `.gitignore` 忽略 —— 体积大且可一键重建。
