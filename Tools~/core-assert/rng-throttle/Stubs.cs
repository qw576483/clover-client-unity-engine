// 离线断言宿主的最小替身：只补被链接的真实引擎源码所需的外部符号。
// 目的：让 `Runtime/Core/{Rng,LogThrottle}.cs`（+ 取 ILogger 定义的 Logger.cs）在脱离 Unity 的情况下
// 编译并跑真断言。
//
// 注意（与父宿主同一条纪律）：LogLevel / ILogger / Logger 的真实定义在 `Runtime/Core/Logger.cs`（已链接），
// 这里**不能**再定义一份（重复定义会编译失败）—— 这正是「链接真实源码、不做副本」的意义。
//
// ⛔ 替身只用于编译与「非 Unity 进程」这一事实的复现，**不改任何被链接源码的语义**。
using System;
using System.Security;

namespace UnityEngine
{
    /// <summary>只覆盖引擎这几个文件用到的入口。</summary>
    public static class Debug
    {
        public static void Log(object msg) { }
        public static void LogWarning(object msg) { }
        public static void LogError(object msg) { Console.WriteLine("[Debug.LogError] " + msg); }
    }

    /// <summary>
    /// 桌面 .NET 上没有 Unity 的 ECall 实现 —— 真实 Unity 的 <c>Time.realtimeSinceStartup</c> 在
    /// 非 Unity 进程里（离线宿主就是这种进程）调用即抛 <c>SecurityException</c>
    /// （"ECall methods must be packaged into a system module"）。
    /// 本替身**如实复现这个行为**，因此它同时是「时钟降级到 Stopwatch」那条分支的触发条件。
    /// </summary>
    public static class Time
    {
        public static float realtimeSinceStartup =>
            throw new SecurityException("ECall methods must be packaged into a system module");
    }

    /// <summary>Rng 只用到 <c>zero</c> / 构造 / <c>x</c> / <c>y</c>。</summary>
    public struct Vector2Int
    {
        public int x;
        public int y;

        public Vector2Int(int x, int y)
        {
            this.x = x;
            this.y = y;
        }

        public static Vector2Int zero => new Vector2Int(0, 0);

        public override string ToString() => "(" + x + ", " + y + ")";
    }
}

namespace CloverEngine
{
    /// <summary>门面替身：被链接的源码里只用到 Logger 一个成员（真实实现在 Runtime/Core/Game.cs）。</summary>
    public static class Game
    {
        public static ILogger Logger;
    }
}
