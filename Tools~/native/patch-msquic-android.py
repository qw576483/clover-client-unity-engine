#!/usr/bin/env python3
# =============================================================================
# 让 msquic 能在 Android 上编译通过（由 build-android-quic.sh 自动调用，幂等）。
#
# 问题：`src/platform/selfsign_openssl.c` 用了 `glob()` / `globfree()`，
#       而 **Android bionic 直到 API 28 才提供这两个函数**，本工程 minSdk=26，
#       于是编译报 `call to undeclared function 'glob'`。
#
# 判断：这两个调用只服务于「生成自签证书时找临时目录」这个**辅助功能**
#       （`CxPlatGetSelfSignedCertPath`，客户端与网关都不用它），
#       为它把整包最低 API 抬到 28 不值得（会丢掉 Android 8/9 设备）。
#
# 做法：在 Android 上给一个「永远找不到」的替身，让代码走它原有的
#       “找不到目录就 mkdtemp 新建”分支 —— 行为不变，且不抬高最低 API。
# =============================================================================
import io
import sys

MARK = "CLOVER_ANDROID_GLOB_SHIM"

SHIM = '''
#ifdef __ANDROID__
//
// %s
// Android bionic 的 glob()/globfree() 自 API 28 才有，而本工程 minSdk=26。
// 这里用 glob 仅是为「自签证书」找临时目录（客户端/网关都不使用该辅助功能），
// 故提供「永远找不到」的替身：让后续走原有的 mkdtemp 分支，不抬高最低 API 版本。
//
static int clover_android_glob_unavailable(const char* pattern, int flags, void* errfunc, glob_t* pglob)
{
    (void)pattern; (void)flags; (void)errfunc;
    if (pglob != NULL) {
        pglob->gl_pathc = 0;
        pglob->gl_pathv = NULL;
    }
    return -1; // 非 0 = 没有匹配（与 glob() 的失败语义一致）
}
#define glob(p, f, e, g) clover_android_glob_unavailable((p), (f), (e), (g))
#define globfree(g)      do { (void)(g); } while (0)
#endif
''' % MARK

ANCHOR = "#include <glob.h>"


def main() -> int:
    if len(sys.argv) != 2:
        print("用法: patch-msquic-android.py <selfsign_openssl.c>", file=sys.stderr)
        return 2

    path = sys.argv[1]
    src = io.open(path, encoding="utf-8").read()

    if MARK in src:
        print("  补丁已存在，跳过")
        return 0

    if src.count(ANCHOR) != 1:
        print("  ✗ 补丁失败：找不到唯一锚点 %r（共 %d 处）" % (ANCHOR, src.count(ANCHOR)), file=sys.stderr)
        return 3

    src = src.replace(ANCHOR, ANCHOR + SHIM, 1)
    io.open(path, "w", encoding="utf-8", newline="\n").write(src)
    print("  ✓ 已打补丁：%s（Android 端 glob 替身）" % path)
    return 0


if __name__ == "__main__":
    sys.exit(main())
