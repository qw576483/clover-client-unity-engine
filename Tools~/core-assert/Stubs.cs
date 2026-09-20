// 离线断言宿主的最小替身：只补被链接的真实引擎源码所需的外部符号。
// 目的：让 `Runtime/Core/{Json,Event,Setting,Logger,ConsoleLogger}.cs` 能在脱离 Unity 的情况下编译并跑真断言。
//
// 注意：LogLevel / ILogger 的真实定义在 `Runtime/Core/Logger.cs`（已链接），
// 这里**不能**再定义一份（重复定义会编译失败）——这正是"链接真实源码、不做副本"的意义。
using System;

namespace UnityEngine
{
    /// <summary>只覆盖引擎这几个文件用到的入口。</summary>
    public static class Debug
    {
        public static void Log(object msg) { }
        public static void LogWarning(object msg) { }
        public static void LogError(object msg) { Console.WriteLine("[Debug.LogError] " + msg); }
    }
}

namespace CloverEngine
{
    /// <summary>门面替身：被链接的源码里只用到 Logger 一个成员。</summary>
    public static class Game
    {
        public static ILogger Logger;
    }
}
