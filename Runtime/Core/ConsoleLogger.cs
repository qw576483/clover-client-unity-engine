using System;
using System.Globalization;
using System.Threading;
using UnityEngine;

namespace CloverEngine
{
    /// <summary>
    /// 控制台兜底日志实现：把日志直接写进 Unity Console（<see cref="Debug"/>）。
    /// <para>
    /// <c>Game.Logger</c> **永不为 null**：未 Launch 时指向本类（写 Console，测试里直接可见），
    /// Launch 后（或 Shutdown 后）同样回到本类。因此 <c>Game.Logger?.Xxx</c> 里的 <c>?.</c>
    /// 已经没有存在必要，但保留它无害（行为不变）。
    /// </para>
    /// <para>
    /// 只做 Console 输出，不落盘、不起后台线程 —— 它的职责就是「最小可用的观察窗口」。
    /// 需要文件日志时由 <c>Game.Launch</c> 换上 <c>Logger</c>（落盘 + 后台写线程）。
    /// </para>
    /// <para>
    /// <b>输出格式</b>：与 <see cref="Logger"/> 的文件行<b>完全一致</b> ——
    /// <c>[yyyy-MM-dd HH:mm:ss.fff] [级别] [tag] 消息</c>（见 <see cref="FormatLine"/>）。
    /// </para>
    /// </summary>
    internal sealed class ConsoleLogger : ILogger
    {
        /// <summary>Console 输出失败的降频上报阈值：第 1 次必报，之后每 N 次报一次。</summary>
        private const int FailureReportEvery = 100;

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>共享实例：无状态，不需要每处各建一个。</summary>
        public static readonly ConsoleLogger Instance = new();

        private LogLevel _level = LogLevel.Debug;

        /// <summary>Unity Console 输出失败累计次数（Debug API 自身抛异常时递增）。</summary>
        private int _consoleFailures;

        /// <summary>日志级别；本实现只有 Console 输出，级别仅用于过滤。</summary>
        public LogLevel Level
        {
            get => _level;
            set => _level = value;
        }

        /// <inheritdoc/>
        public void Debug(string tag, string msg) => Write(LogLevel.Debug, tag, msg, null);

        /// <inheritdoc/>
        public void Info(string tag, string msg) => Write(LogLevel.Info, tag, msg, null);

        /// <inheritdoc/>
        public void Warn(string tag, string msg) => Write(LogLevel.Warn, tag, msg, null);

        /// <inheritdoc/>
        public void Error(string tag, string msg, Exception ex = null) => Write(LogLevel.Error, tag, msg, ex);

        /// <inheritdoc/>
        public void Fatal(string tag, string msg, Exception ex = null) => Write(LogLevel.Fatal, tag, msg, ex);

        /// <summary>
        /// 统一的日志行格式：文件日志（<see cref="Logger"/>）与 Console 日志共用，
        /// 保证两条通道可以按时间逐行对齐。<c>[时间] [级别] [tag] 消息</c>，
        /// 有异常时追加一行异常原文。
        /// </summary>
        internal static string FormatLine(DateTime now, LogLevel level, string tag, string msg, Exception ex)
        {
            var line = $"[{now.ToString("yyyy-MM-dd HH:mm:ss.fff", Inv)}] [{level}] [{tag ?? "?"}] {msg}";
            if (ex != null)
                line += Environment.NewLine + ex;
            return line;
        }

        /// <summary>
        /// 把<b>已格式化好的一行</b>按级别写进 Unity Console。
        /// 供 <see cref="Logger"/> 复用（它的文件行与 Console 行必须是同一格式），
        /// 以及 WebGL 下 <see cref="Logger"/> 的同步输出路径使用。本方法不做级别过滤。
        /// </summary>
        internal void WriteLine(LogLevel level, string line)
        {
            // 注意必须写全 UnityEngine.Debug：本类自己有一个名为 Debug 的方法，
            // 简单名 Debug 会被它遮蔽，`Debug.Log(...)` 会编译不过。
            try
            {
                switch (level)
                {
                    case LogLevel.Warn:
                        UnityEngine.Debug.LogWarning(line);
                        break;
                    case LogLevel.Error:
                    case LogLevel.Fatal:
                        UnityEngine.Debug.LogError(line);
                        break;
                    default:
                        UnityEngine.Debug.Log(line);
                        break;
                }
            }
            catch (Exception ex)
            {
                // Unity 的 Debug API 在极少数场景（如编辑器退出中、域重载）会抛；这里刻意不向上传播。
                // 但"静默空 catch"会让这类故障彻底无痕，因此至少计数 + 降频降级输出到 stderr。
                var n = Interlocked.Increment(ref _consoleFailures);
                if (n == 1 || n % FailureReportEvery == 0)
                {
                    try
                    {
                        System.Console.Error.WriteLine(
                            $"[ConsoleLogger] Unity Console 输出失败（第 {n} 次）：{ex.Message}");
                    }
                    catch
                    {
                        // 最后一跳也失败（无 stderr / 宿主已卸载）：确实无处可写，放弃
                    }
                }
            }
        }

        private void Write(LogLevel level, string tag, string msg, Exception ex)
        {
            if (level < _level) return;
            WriteLine(level, FormatLine(DateTime.Now, level, tag, msg, ex));
        }
    }
}
