@echo off
rem ===========================================================================
rem Android 版 libmsquic.so 构建入口（Windows）
rem
rem 必须经 Git-Bash 跑，原因见 build-android-quic.sh 顶部注释：
rem   quictls 的 android 检测依赖 msys 风格路径，NDK 的 clang 是 .cmd 包装器
rem   （只有 sh 能执行），RANLIB 用的是 sh 内建 `:`。
rem
rem 用法：
rem   build-android-quic.cmd            （默认 arm64-v8a）
rem   build-android-quic.cmd armeabi-v7a
rem   build-android-quic.cmd x86_64     （x86_64 模拟器）
rem ===========================================================================
setlocal
set ABI=%1
if "%ABI%"=="" set ABI=arm64-v8a

set BASH="C:\Program Files\Git\bin\bash.exe"
set SCRIPT=%~dp0build-android-quic.sh

rem 交给 msys 的路径要用 /c/... 形式
set SCRIPT_MSYS=%SCRIPT:\=/%
set SCRIPT_MSYS=/c%SCRIPT_MSYS:~2%

%BASH% -lc "%SCRIPT_MSYS% %ABI%"
exit /b %ERRORLEVEL%
