#!/usr/bin/env python3
# =============================================================================
# 给 quictls（OpenSSL 3.1.4+quic）的 NDK 识别打补丁，使它在 **Windows(msys) 宿主**
# 上也能工作。由 build-android-quic.sh 自动调用（幂等：打过就跳过）。
#
# 为什么需要：
#   quictls 的 `Configurations/15-android.conf` 用**字符串前缀**判断 clang 是否来自 NDK：
#       if (which("clang") =~ m|^$ndk/.*/prebuilt/([^/]+)/|)
#   其中 $ndk = canonpath($ENV{ANDROID_NDK_ROOT})。
#   在 Windows 上这两个值的"形式"经常不一致：
#       · 环境变量经过 msys -> 原生 make.exe 这一跳后会变成 `C:\Program Files\...`
#       · 而 PATH 查找（which）返回的是 msys 形式 `/c/Program Files/...`
#   于是匹配失败，被误判成"没有 NDK clang"，最终报一句极具误导性的
#       no NDK aarch64-linux-android-gcc on $PATH
#   （Linux/macOS 宿主上两者天然一致，所以上游从未暴露这个问题。）
#
# 做法：把两边都归一化成 msys 形式（正斜杠 + 小写盘符 `/c/...`）再做匹配；
#       并在 die 消息里带上两个原始值，避免下次再猜。
# =============================================================================
import io
import sys

MARK = "CLOVER_NDK_PATH_NORM"

NORM_SUB = '''    # ---- %s --------------------------------------------------
    # 把路径归一化成 msys 形式：反斜杠 -> 正斜杠，`C:/x` -> `/c/x`。
    # 这样 $ANDROID_NDK_ROOT 与 which(...) 的结果就能可靠比较。
    sub clover_norm_path {
        my ($p) = @_;
        return '' unless defined $p;
        $p =~ s{\\\\}{/}g;
        $p =~ s{^([a-zA-Z]):/}{"/" . lc($1) . "/"}e;
        return $p;
    }

''' % MARK

REPLACEMENTS = [
    # 1) sub 定义前插入归一化函数
    ("sub android_ndk {", NORM_SUB + "sub android_ndk {", 1),
    # 2) $ndk 统一成 msys 形式
    ("$ndk = canonpath($ndk);",
     "$ndk = canonpath($ndk);\n            $ndk = clover_norm_path($ndk);", 1),
    # 3) 三处 which(...) 比较：先归一化再匹配
    ('if (which("clang") =~ m|^$ndk/.*/prebuilt/([^/]+)/|) {',
     'if (clover_norm_path(which("clang")) =~ m{^$ndk/.*/prebuilt/([^/]+)/}) {', 1),
    ('if (which("llvm-ar") =~ m|^$ndk/.*/prebuilt/([^/]+)/|) {',
     'if (clover_norm_path(which("llvm-ar")) =~ m{^$ndk/.*/prebuilt/([^/]+)/}) {', 1),
    ('if (which("$triarch-gcc") !~ m|^$ndk/.*/prebuilt/([^/]+)/|) {',
     'if (clover_norm_path(which("$triarch-gcc")) !~ m{^$ndk/.*/prebuilt/([^/]+)/}) {', 1),
    # 4) die 消息带上真实取值（下次不用再猜）
    ('die "no NDK $triarch-gcc on \\$PATH";',
     'die "no NDK $triarch-gcc on \\$PATH "\n'
     '                        . "(ANDROID_NDK_ROOT=$ndk, which(clang)=" . which("clang") . ")";', 1),
]


def main() -> int:
    if len(sys.argv) != 2:
        print("用法: patch-quictls-android-ndk.py <15-android.conf>", file=sys.stderr)
        return 2

    path = sys.argv[1]
    src = io.open(path, encoding="utf-8").read()

    if MARK in src:
        print("  补丁已存在，跳过")
        return 0

    for old, new, _ in REPLACEMENTS:
        if src.count(old) < 1:
            print("  ✗ 补丁失败：找不到目标片段 %r" % old[:60], file=sys.stderr)
            return 3
        src = src.replace(old, new, 1)

    io.open(path, "w", encoding="utf-8", newline="\n").write(src)
    print("  ✓ 已打补丁：%s（统一 NDK 路径形式）" % path)
    return 0


if __name__ == "__main__":
    sys.exit(main())
