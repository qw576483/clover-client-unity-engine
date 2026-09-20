#!/usr/bin/env bash
# =============================================================================
# 交叉编译 Android 版 libmsquic.so（QUIC 原生库）
#
# 为什么需要这个脚本：
#   客户端 QUIC 走 msquic（一套 C 绑定五端复用）。Windows 版二进制随仓库提交
#   （Runtime/Plugins/x86_64/msquic.dll），但 **Android 没有官方预编译**，
#   必须自己从源码交叉编译，否则真机上 QUIC 会被裁剪掉。
#
# 为什么必须用 Git-Bash(msys) 跑（这是本脚本存在的一半理由）：
#   msquic 上游源码 **本身就支持 Android**（submodules/CMakeLists.txt 里有
#   `if(ANDROID)` + `ANDROID_ABI -> android-arm64/android-arm` 的分支，
#   会自动把仓库里的 quictls 子模块交叉编译成静态库），但它假设宿主是 Linux/macOS。
#   在 Windows 上直接跑会死在两处，且报错都很难懂：
#     1) quictls 的 Configure：它用 `which("clang")` 的结果与 `$ANDROID_NDK_ROOT`
#        做**正斜杠前缀匹配**来识别 NDK。原生 Windows perl 拿到的路径是反斜杠，
#        匹配失败后走到 "no NDK aarch64-linux-gcc on $PATH" 这条 die 分支。
#        msys 的 perl 返回 /c/... 形式 ⇒ 匹配成功。
#     2) NDK 的 `aarch64-linux-androidNN-clang` 在 Windows 上是 **.cmd 包装器**，
#        CreateProcess 只认 .exe ⇒ 只有在 msys 的 sh 下执行才解析得到。
#   （顺带：quictls 给 AR 用的是 `llvm-ar`，RANLIB 设为 `:` —— `:` 是 sh 内建，
#     所以少了 sh 也同样跑不起来。）
#
# 用法（Git-Bash）：
#   ./build-android-quic.sh                 # 默认 arm64-v8a
#   ./build-android-quic.sh armeabi-v7a
#   ./build-android-quic.sh x86_64          # x86_64 模拟器
# 环境变量（都可省）：
#   ANDROID_NDK_ROOT  NDK 路径（默认取 Unity 自带 NDK）
#   UNITY_EDITOR_ROOT Unity 编辑器目录（msys 形式；默认 /c/Program Files/Unity/Hub/Editor/6000.6.0f1/Editor，
#                     编辑器换盘/换小版本时覆盖它即可找到自带 NDK）
#   ANDROID_API       min API（默认 26，与工程 AndroidMinSdkVersion 一致）
#   MSQUIC_TAG        版本标签（默认 v2.4.16，与 Windows 版 msquic.dll 的 FileVersion 一致）
#   QUIC_BUILD_ROOT   源码/构建根目录（默认 <仓库根>/_native_build，不进库）
# =============================================================================
set -euo pipefail

ABI="${1:-arm64-v8a}"
case "$ABI" in
    arm64-v8a|armeabi-v7a|x86|x86_64) ;;
    *) echo "不支持的 ABI: $ABI（可选 arm64-v8a / armeabi-v7a / x86 / x86_64）" >&2; exit 2 ;;
esac

API="${ANDROID_API:-26}"
MSQUIC_TAG="${MSQUIC_TAG:-v2.4.16}"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ENGINE_DIR="$(cd "$SCRIPT_DIR/../.." && pwd)"          # 本仓库根（clover-client-unity-engine）
REPO_DIR="$ENGINE_DIR"                                 # 仓库根 == ENGINE_DIR：本包已是独立 git 仓库，
                                                       # 不再是 monorepo 里的一级目录，故不能取上级目录
BUILD_ROOT="${QUIC_BUILD_ROOT:-$REPO_DIR/_native_build}"
SRC_DIR="$BUILD_ROOT/msquic"
OUT_DIR="$BUILD_ROOT/out/$ABI"
DEST_DIR="$ENGINE_DIR/Runtime/Plugins/Android/libs/$ABI"

# Unity 自带 NDK 的位置：默认按标准安装路径推；编辑器装在别的盘 / 换小版本时用 UNITY_EDITOR_ROOT 覆盖
# （这里要 msys 正斜杠形式，例：/<盘符>/Unity/Hub/Editor/<版本>/Editor）
UNITY_EDITOR_ROOT="${UNITY_EDITOR_ROOT:-/c/Program Files/Unity/Hub/Editor/6000.6.0f1/Editor}"
NDK="${ANDROID_NDK_ROOT:-$UNITY_EDITOR_ROOT/Data/PlaybackEngines/AndroidPlayer/NDK}"
[ -d "$NDK" ] || { echo "NDK 不存在: $NDK（可用 ANDROID_NDK_ROOT 指定）" >&2; exit 3; }

# --- 关键：把三个目录塞进 PATH（缺一不可）------------------------------------
#   ① NDK llvm bin：which("clang")/which("llvm-ar") 要能命中，且路径必须是
#      /c/... 正斜杠形式（msys 风格），否则 quictls 的 android 检测会失败；
#   ② NDK prebuilt bin：NDK 自带的 make.exe；
#   ③ Git usr/bin：msys 的 perl/sed/awk/sh（sh 是 .cmd 包装器能否执行的前提）。
NDK_BIN="$NDK/toolchains/llvm/prebuilt/windows-x86_64/bin"
NDK_MAKE_SRC="$NDK/prebuilt/windows-x86_64/bin/make.exe"
# make.exe 必须先复制到**无空格路径**再用：
# OpenSSL 的 Makefile 会用 $(MAKE) 递归调用自己（build_libs -> $(MAKE) depend 等），
# 而 make 把 MAKE 变量设成"自己被调用时的路径"。NDK 的 make.exe 位于
# "C:/Program Files/..."（带空格），recipe 里没有加引号，于是 sh 把 `C:/Program`
# 当成命令 → "No such file or directory" / Error 127（本轮实测踩到）。
# 复制成无空格路径后，子 make 的路径就不含空格了。DLL 依赖由 PATH 里的 NDK bin 提供。
mkdir -p "$BUILD_ROOT/bin"
NDK_MAKE="$BUILD_ROOT/bin/make.exe"
[ -f "$NDK_MAKE" ] || cp "$NDK_MAKE_SRC" "$NDK_MAKE"
GIT_USR='/c/Program Files/Git/usr/bin'
export ANDROID_NDK_ROOT="$NDK"
export ANDROID_NDK="$NDK"
export PATH="$NDK_BIN:$NDK/prebuilt/windows-x86_64/bin:$GIT_USR:$PATH"

# Windows 控制台默认 GBK，python 打印非 ASCII（如 ✓）会抛 UnicodeEncodeError 并返回非 0，
# 从而把构建脚本的 `|| exit` 带崩（本轮踩过：补丁其实已写入，却被当成失败）。
export PYTHONIOENCODING=utf-8

echo "=== Android QUIC 交叉编译 ==="
echo "  ABI      : $ABI"
echo "  minAPI   : $API"
echo "  NDK      : $NDK"
echo "  msquic   : $MSQUIC_TAG"
echo "  源码/构建: $BUILD_ROOT"

# --- 补给的 perl 模块 --------------------------------------------------------
# 为什么必须补：Git for Windows 自带的 perl 是**精简版**，缺了若干核心模块
# （实测缺 Locale::Maketext / Locale::Maketext::Simple / Params::Check / Pod::Usage）。
# 而 quictls 的 Configure 依赖链路是
#     OpenSSL::config → IPC::Cmd → Params::Check → Locale::Maketext::Simple
# 缺一个就直接 `Can't locate Locale/Maketext/Simple.pm` 而失败（而且是 build 中途才炸）。
# 处理：从 MSYS2 官方仓库取一份**完整模块树**（7MB，只含纯 Perl 模块，无 XS），
# 用 PERL5LIB 注入 —— 不动系统 perl，删掉该目录即可回到原状。
# 注：模块来自 perl 5.38，宿主 perl 是 5.42；这里用到的都是**纯 Perl** 模块，跨小版本兼容；
# 不引入任何 XS（.so）模块，因此不会出现二进制不兼容。
PERL_LIB_ROOT="$BUILD_ROOT/msys2-perl"
MSYS2_PERL_PKG="${MSYS2_PERL_PKG:-https://repo.msys2.org/msys/x86_64/perl-5.38.2-3-x86_64.pkg.tar.zst}"
if [ ! -f "$PERL_LIB_ROOT/usr/share/perl5/core_perl/Locale/Maketext/Simple.pm" ]; then
    echo "--- 补给 perl 核心模块（Git 自带 perl 缺 Locale::Maketext::Simple 等）"
    mkdir -p "$PERL_LIB_ROOT"
    curl -fsSL -o "$PERL_LIB_ROOT/perl.pkg.tar.zst" "$MSYS2_PERL_PKG"
    tar -xf "$PERL_LIB_ROOT/perl.pkg.tar.zst" -C "$PERL_LIB_ROOT"
    rm -f "$PERL_LIB_ROOT/perl.pkg.tar.zst"
fi
# ⚠️ 两个必须遵守的约束（都踩过）：
#   1) 只给 `usr/share/perl5/*`（纯 Perl 模块），**绝不能**给 `usr/lib/perl5/core_perl`
#      —— 那里是架构相关模块（含 Config.pm），版本钉死在 5.38，会让宿主 perl 报
#      "Perl lib version (5.38.2) doesn't match executable 'perl' version (5.42.2)"。
#   2) **不要用 PERL5LIB 传路径**：环境变量在 msys -> 原生 make.exe 这一跳会被改写
#      （实测变成 `C \Work\...`，连冒号都丢了），perl 收到的是一串不存在的目录，
#      于是 OpenSSL 的 Configure 依然死在
#      `Can't locate Locale/Maketext/Simple.pm`。
#      改用**垫片 + 命令行 -I**：参数不会被改写，且对所有 `perl xxx` 调用生效。
PERL_SHIM_DIR="$BUILD_ROOT/bin"
mkdir -p "$PERL_SHIM_DIR"
cat > "$PERL_SHIM_DIR/perl" <<EOF
#!/bin/sh
# 除了 -I 参数，还要把搜索路径写进 **环境变量 PERL5LIB**：
# OpenSSL 的 Configure 会用 \$^X（真实 perl 路径）把它自己刚生成的 configdata.pm
# 再执行一次，那次调用不经过本垫片 —— 只有环境变量能带过去（实测：只给 -I 时
# 依然死在 "Can't locate Pod/Usage.pm at configdata.pm line 19802"）。
# 这里用环境变量不会踩到之前 PERL5LIB 被改写的坑：垫片 -> perl 是 msys 内部跳转，
# 不发生路径改写；被改写的只是「msys -> 原生 make.exe」那一跳，而这条路由垫片自己设置。
# （注意：本 heredoc 未加引号，**注释里不能出现反引号**，否则会被 bash 当命令替换执行。）
PERL5LIB="$PERL_LIB_ROOT/usr/share/perl5/core_perl:$PERL_LIB_ROOT/usr/share/perl5/vendor_perl\${PERL5LIB:+:\$PERL5LIB}"
export PERL5LIB
exec /usr/bin/perl -I$PERL_LIB_ROOT/usr/share/perl5/core_perl -I$PERL_LIB_ROOT/usr/share/perl5/vendor_perl "\$@"
EOF
chmod +x "$PERL_SHIM_DIR/perl"
export PATH="$PERL_SHIM_DIR:$PATH"   # 必须放在 PATH 前面，压过系统 perl

# 还要用 PERL5OPT 兜底：OpenSSL 生成的 Makefile 里 **硬编码了 `PERL=/usr/bin/perl`**
# （见 <build>/submodules/openssl3/Makefile 的 PERL= 行），于是像
#   $(PERL) "-I." -Mconfigdata util/dofile.pl ... > include/openssl/asn1.h
# 这类生成头文件的规则会**绕过 PATH 垫片**，再次缺模块。
# PERL5OPT 对环境里"任何一个" perl 都生效；值以 `-I` 开头，不会被 msys 当路径改写
# （之前 PERL5LIB 被改写是因为它的值以 `/` 开头）。
export PERL5OPT="-I$PERL_LIB_ROOT/usr/share/perl5/core_perl -I$PERL_LIB_ROOT/usr/share/perl5/vendor_perl${PERL5OPT:+ $PERL5OPT}"
perl -e 'require Locale::Maketext::Simple; require Params::Check; require Pod::Usage; require IPC::Cmd; print "  perl 模块自检 OK（经垫片）\n"' \
    || { echo "perl 模块补给失败（quictls Configure 会失败）" >&2; exit 6; }

# --- ① 准备源码（含 quictls 子模块；只需 openssl3，googletest 等用不到）------
if [ ! -d "$SRC_DIR/.git" ]; then
    echo "--- 克隆 msquic $MSQUIC_TAG"
    git clone --depth 1 --branch "$MSQUIC_TAG" https://github.com/microsoft/msquic.git "$SRC_DIR"
fi
echo "--- 同步 quictls 子模块（提供 OpenSSL 3.1.4+quic）"
git -C "$SRC_DIR" submodule update --init --depth 1 submodules/openssl3

# --- 给 quictls 打 NDK 识别补丁（只在 Windows/msys 宿主下需要）----------------
# 见 patch-quictls-android-ndk.py 顶部注释：不补的话会在 Configure 里报
# `no NDK aarch64-linux-android-gcc on $PATH`（误导性极强，实际是路径形式不一致）。
QUICTLS_CONF="$SRC_DIR/submodules/openssl3/Configurations/15-android.conf"
echo "--- quictls NDK 路径识别补丁"
python "$SCRIPT_DIR/patch-quictls-android-ndk.py" "$QUICTLS_CONF" || exit 7

# --- msquic 的 Android 编译补丁 ----------------------------------------------
# 见 patch-msquic-android.py：selfsign_openssl.c 用了 API 28 才有的 glob()，
# 而工程 minSdk=26；该功能（自签证书）客户端用不到，故给个替身而不抬高最低 API。
echo "--- msquic Android 补丁（glob）"
python "$SCRIPT_DIR/patch-msquic-android.py" "$SRC_DIR/src/platform/selfsign_openssl.c" || exit 8

# --- ② 配置 ----------------------------------------------------------------
# 可选清理：`CLEAN=1 ./build-android-quic.sh`。
# 为什么需要：OpenSSL 生成头文件的规则形如 `$(PERL) ... > $@`，**shell 重定向会先把目标文件截断**，
# 若那次 perl 因故失败（例如缺 Pod::Usage），就会留下 **0 字节的头文件**；而 make 只看时间戳，
# 认为它已生成、不会重做 —— 后续编译于是报一堆看似毫不相干的错
# （实测量到的是 `unknown type name 'CRYPTO_RWLOCK'`、`type specifier missing`）。
# 遇到这类"头文件内容不对"的报错，先 CLEAN=1 重来。
if [ "${CLEAN:-0}" = "1" ]; then
    echo "--- CLEAN=1：清理构建目录 $OUT_DIR"
    rm -rf "$OUT_DIR"
fi

# 用 Windows 形式路径交给 cmake（msys 会改写含 ':' 的参数，交给 cmake 前先转好），
# 但 PATH / ANDROID_NDK_ROOT 保持 msys 形式 —— 那是给 quictls 的 perl 看的。
win() { cygpath -m "$1"; }
JOBS="${NUMBER_OF_PROCESSORS:-4}"

echo "--- cmake 配置"
cmake -S "$(win "$SRC_DIR")" -B "$(win "$OUT_DIR")" \
      -G "Unix Makefiles" \
      -DCMAKE_MAKE_PROGRAM="$(win "$NDK_MAKE")" \
      -DCMAKE_TOOLCHAIN_FILE="$(win "$NDK/build/cmake/android.toolchain.cmake")" \
      -DANDROID_ABI="$ABI" \
      -DANDROID_PLATFORM="android-$API" \
      -DCMAKE_BUILD_TYPE=Release \
      -DQUIC_TLS=openssl3 \
      -DQUIC_BUILD_SHARED=ON \
      -DQUIC_BUILD_TEST=OFF \
      -DQUIC_BUILD_TOOLS=OFF \
      -DQUIC_ENABLE_LOGGING=OFF

# --- ③ 编译（会先编 quictls 静态库 libssl.a/libcrypto.a，再编 libmsquic.so）-
echo "--- 编译（-j$JOBS，首次含 quictls，耗时可观）"
cmake --build "$(win "$OUT_DIR")" --target msquic -- -j"$JOBS"

# --- ④ 收集产物 -------------------------------------------------------------
SO="$(find "$OUT_DIR" -name 'libmsquic.so*' -type f ! -name '*.so.*' | head -n 1 || true)"
if [ -z "$SO" ]; then
    SO="$(find "$OUT_DIR" -name 'libmsquic.so.*' -type f | sort | head -n 1 || true)"
fi
[ -n "$SO" ] || { echo "没找到 libmsquic.so（构建可能失败）" >&2; exit 4; }

mkdir -p "$DEST_DIR"
cp -f "$SO" "$DEST_DIR/libmsquic.so"

# --- ⑤ 自检：ELF 架构 + 动态依赖（不得依赖 libssl/libcrypto：应是静态链入）---
echo "=== 自检 ==="
READELF="$NDK_BIN/llvm-readelf.exe"
"$READELF" -h "$DEST_DIR/libmsquic.so" | grep -E 'Class|Machine|Type' | sed 's/^/  /'
echo "  --- 动态依赖："
"$READELF" -d "$DEST_DIR/libmsquic.so" | grep -E 'NEEDED' | sed 's/^/  /'
if "$READELF" -d "$DEST_DIR/libmsquic.so" | grep -qE 'NEEDED.*(libssl|libcrypto)'; then
    echo "  ✗ 依赖了 libssl/libcrypto 动态库（应为静态链接）" >&2
    exit 5
fi
echo "  ✓ 无 libssl/libcrypto 动态依赖"
echo "  --- 导出符号（只应有 MsQuicOpenVersion / MsQuicClose）："
"$READELF" --dyn-syms "$DEST_DIR/libmsquic.so" | grep -E 'MsQuic' | sed 's/^/  /'

ls -l "$DEST_DIR/libmsquic.so"
echo "=== 完成：$ABI -> Runtime/Plugins/Android/libs/$ABI/libmsquic.so ==="
