using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Threading;

namespace CloverEngine
{
    /// <summary>
    /// 分级日志，文件 + Console 双写。
    ///
    /// 文件路径：<c>{logDir}/YYYY-MM-DD.log</c>，跨天自动切换文件（logDir 由构造参数决定）。
    /// **文件**写入在独立后台线程完成（ConcurrentQueue 入队 + 后台循环每批 flush 一次），不阻塞调用方；
    /// **每来一条日志就唤醒写线程**（不是在"日期变化"时才唤醒），因此同一天的日志也会及时落盘，
    /// 不会出现"攒一天、进程一崩全丢"的情况。
    /// **Console** 输出（UnityEngine.Debug.Log*）是在调用线程上同步执行的，因此整体<b>不是</b>「零阻塞」。
    /// 输出为纯文本，不含 ANSI 转义序列——Unity Console 用富文本着色，不需要颜色码。
    ///
    /// <para>文件行与 Console 行共用同一套格式（<see cref="ConsoleLogger.FormatLine"/>）：
    /// <c>[yyyy-MM-dd HH:mm:ss.fff] [级别] [tag] 消息</c>；Error/Fatal 走 <c>Debug.LogError</c>，
    /// Warn 走 <c>Debug.LogWarning</c>，与 <see cref="ConsoleLogger"/> 完全一致。</para>
    ///
    /// <para><b>WebGL 限制（重要）</b>：<c>UNITY_WEBGL</c> 下既没有线程（<c>Thread.Start</c> 会抛），
    /// 也没有可写的本地文件系统。因此该平台下本类<b>不建目录、不建文件、不起后台线程</b>，
    /// 日志退化为「调用线程上同步写 Console」（等价 <see cref="ConsoleLogger"/>），
    /// 进程结束后不留任何日志文件。需要留存时请由业务把日志桥接到服务端。</para>
    /// </summary>
    public enum LogLevel
    {
        /// <summary>
        /// 调试级别日志
        /// </summary>
        Debug = 0,
        /// <summary>
        /// 普通信息级别日志
        /// </summary>
        Info = 1,
        /// <summary>
        /// 警告级别日志
        /// </summary>
        Warn = 2,
        /// <summary>
        /// 错误级别日志
        /// </summary>
        Error = 3,
        /// <summary>
        /// 致命错误级别日志
        /// </summary>
        Fatal = 4
    }

    public interface ILogger
    {
        /// <summary>
        /// 获取或设置日志级别，低于此级别的日志将被忽略
        /// </summary>
        LogLevel Level { get; set; }

        /// <summary>
        /// 输出调试级别日志
        /// </summary>
        /// <param name="tag">日志标签，用于分类</param>
        /// <param name="msg">日志消息内容</param>
        void Debug(string tag, string msg);

        /// <summary>
        /// 输出信息级别日志
        /// </summary>
        /// <param name="tag">日志标签，用于分类</param>
        /// <param name="msg">日志消息内容</param>
        void Info(string tag, string msg);

        /// <summary>
        /// 输出警告级别日志
        /// </summary>
        /// <param name="tag">日志标签，用于分类</param>
        /// <param name="msg">日志消息内容</param>
        void Warn(string tag, string msg);

        /// <summary>
        /// 输出错误级别日志
        /// </summary>
        /// <param name="tag">日志标签，用于分类</param>
        /// <param name="msg">日志消息内容</param>
        /// <param name="ex">可选的异常对象，为null时不输出异常堆栈</param>
        void Error(string tag, string msg, Exception ex = null);

        /// <summary>
        /// 输出致命错误级别日志
        /// </summary>
        /// <param name="tag">日志标签，用于分类</param>
        /// <param name="msg">日志消息内容</param>
        /// <param name="ex">可选的异常对象，为null时不输出异常堆栈</param>
        void Fatal(string tag, string msg, Exception ex = null);
    }

    internal class Logger : ILogger, IDisposable
    {
        /// <summary>logDir 为 null / 空串 / 纯空白时的兜底目录（相对当前工作目录）。</summary>
        private const string DefaultLogDir = "logs";

        /// <summary>
        /// 写线程的空闲等待上限：既是"没新日志也定期把已写内容 flush 到磁盘"的节拍，
        /// 也是 Dispose 之外的兜底唤醒（写线程不会被永久挂住）。
        /// </summary>
        private const int IdleWaitMs = 1000;

        /// <summary>写盘失败 / 日志丢弃的降频上报阈值：第 1 次必报，之后每 N 次报一次。</summary>
        private const int FailureReportEvery = 100;

        /// <summary>Dispose 等待写线程退出的超时（毫秒）。</summary>
        private const int DisposeJoinTimeoutMs = 1000;

        private LogLevel _level = LogLevel.Debug;

#if !UNITY_WEBGL
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private readonly string _logDir;
        private readonly ConcurrentQueue<string> _queue = new();
        private readonly Thread _writerThread;
        private readonly ManualResetEventSlim _waitHandle = new(false);
        private volatile bool _running;
        private volatile bool _disposed;
        /// <summary>写线程未能在 Dispose 超时内退出：此后 Dispose 侧不再碰文件句柄与等待句柄。</summary>
        private volatile bool _abandoned;

        // 下面几个字段【只由写线程访问】：日期判定与文件句柄都不跨线程共享。
        // 日期归属写线程自己，跨天必然重建（若由调用方提前改写日期，写线程会判定"日期没变、无需切文件"）。
        private string _currentDate;
        /// <summary>已经报过错的日期：同一天重复打开失败只报一次，避免刷屏。</summary>
        private string _rotateFailedDate;
        private StreamWriter _writer;
        /// <summary>累计丢弃的行数（写不进去就被丢弃，不无限堆积在无上限队列里）。</summary>
        private long _dropped;
        /// <summary>累计写失败次数，用于降频上报。</summary>
        private long _failures;
#endif

        /// <summary>
        /// 获取或设置日志级别，低于此级别的日志将被忽略
        /// </summary>
        public LogLevel Level
        {
            get => _level;
            set => _level = value;
        }

        /// <summary>
        /// 初始化日志系统，创建指定目录并启动后台写入线程。
        /// <para>
        /// 本构造<b>不抛异常</b>：<c>Game.Launch</c> 是无条件 <c>new Logger(...)</c> 的，
        /// 传 null / 空串 / 非法路径时旧的 <c>Directory.CreateDirectory("")</c> 会抛
        /// ArgumentException 直接把启动打崩。现在的处理是：空串/空白 → 回退默认目录；
        /// 目录创建失败 → 记一条 Error 并退化为「只写 Console」（不落盘）。
        /// </para>
        /// <para>WebGL 下不创建任何目录、不启动线程（见类注释）。</para>
        /// </summary>
        /// <param name="logDir">日志文件存储目录，目录不存在时会自动创建</param>
        public Logger(string logDir)
        {
#if UNITY_WEBGL
            // WebGL：无线程、无本地文件系统，构造不做任何 IO；日志只走 Console。
            // logDir 参数在此不被使用（保留签名以便 Game.Launch 统一传参）。
#else
            var dir = logDir;
            if (string.IsNullOrWhiteSpace(dir))
            {
                dir = DefaultLogDir;
                ConsoleLogger.Instance.WriteLine(LogLevel.Warn,
                    $"[{TimeStamp(DateTime.Now)}] [Warn] [Logger] logDir 为空，回退到默认目录 '{DefaultLogDir}'");
            }
            _logDir = dir;

            try
            {
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
            }
            catch (Exception ex)
            {
                // 目录建不出来（权限 / 非法字符 / 路径过长）也不许把异常抛回调用方：
                // 记一条 Error，后续由写线程在打开文件时按降频继续报错。
                ConsoleLogger.Instance.WriteLine(LogLevel.Error,
                    $"[{TimeStamp(DateTime.Now)}] [Error] [Logger] 日志目录不可用 '{dir}'：{ex.Message}；本次运行不落盘");
            }

            try
            {
                _writerThread = new Thread(WriteLoop)
                {
                    IsBackground = true,
                    Name = "Logger"
                };
                _running = true;
                _writerThread.Start();
            }
            catch (Exception ex)
            {
                _writerThread = null;
                _running = false;
                ConsoleLogger.Instance.WriteLine(LogLevel.Error,
                    $"[{TimeStamp(DateTime.Now)}] [Error] [Logger] 日志写线程启动失败：{ex.Message}；日志改为同步落盘");
            }
#endif
        }

        /// <summary>
        /// 输出调试级别日志
        /// </summary>
        /// <param name="tag">日志标签，用于分类</param>
        /// <param name="msg">日志消息内容</param>
        public void Debug(string tag, string msg) => Log(LogLevel.Debug, tag, msg);

        /// <summary>
        /// 输出信息级别日志
        /// </summary>
        /// <param name="tag">日志标签，用于分类</param>
        /// <param name="msg">日志消息内容</param>
        public void Info(string tag, string msg) => Log(LogLevel.Info, tag, msg);

        /// <summary>
        /// 输出警告级别日志
        /// </summary>
        /// <param name="tag">日志标签，用于分类</param>
        /// <param name="msg">日志消息内容</param>
        public void Warn(string tag, string msg) => Log(LogLevel.Warn, tag, msg);

        /// <summary>
        /// 输出错误级别日志
        /// </summary>
        /// <param name="tag">日志标签，用于分类</param>
        /// <param name="msg">日志消息内容</param>
        /// <param name="ex">可选的异常对象，为null时不输出异常堆栈</param>
        public void Error(string tag, string msg, Exception ex = null) => Log(LogLevel.Error, tag, msg, ex);

        /// <summary>
        /// 输出致命错误级别日志
        /// </summary>
        /// <param name="tag">日志标签，用于分类</param>
        /// <param name="msg">日志消息内容</param>
        /// <param name="ex">可选的异常对象，为null时不输出异常堆栈</param>
        public void Fatal(string tag, string msg, Exception ex = null) => Log(LogLevel.Fatal, tag, msg, ex);

        private void Log(LogLevel level, string tag, string msg, Exception ex = null)
        {
            if (level < _level) return;

            // 只取一次时间：同时用于时间戳与（写线程侧的）日期判定，避免跨天边界上两处对不上。
            var now = DateTime.Now;
            var line = ConsoleLogger.FormatLine(now, level, tag, msg, ex);

#if !UNITY_WEBGL
            // 已 Dispose（或写线程已被放弃）时不再入队：队列无人消费会无上限增长，只保留 Console 输出。
            if (!_disposed && !_abandoned)
            {
                _queue.Enqueue(line);
                // 每条日志都唤醒写线程：只靠"日期变化"唤醒会让同一天的日志全部积压在队列里不落盘。
                SignalWriter();
            }
#endif

            // Console 输出统一交给 ConsoleLogger：Error/Fatal 走 Debug.LogError，且内部自带 try/catch，
            // 编辑器退出、域重载等场景下 Debug API 自身抛异常也不会把异常抛回业务调用方。
            ConsoleLogger.Instance.WriteLine(level, line);
        }

#if !UNITY_WEBGL

        private void SignalWriter()
        {
            try
            {
                _waitHandle.Set();
            }
            catch (ObjectDisposedException)
            {
                // 与 Dispose 竞态：忽略即可，队列内容会在下个节拍被处理
            }
        }

        private void WriteLoop()
        {
            try
            {
                while (_running)
                {
                    WaitForWork();

                    // 日期由写线程自己取：不跨线程共享，跨天必然触发 RotateIfNeeded 重建文件
                    RotateIfNeeded(DateOf(DateTime.Now));
                    DrainQueue();
                }
            }
            catch (Exception ex)
            {
                // 写线程任何未预期异常都不许让它"静默消失"（否则日志永久停摆且无人知晓）
                ConsoleLogger.Instance.WriteLine(LogLevel.Error,
                    $"[{TimeStamp(DateTime.Now)}] [Error] [Logger] 日志写线程异常终止：{ex}");
            }

            FlushRemaining();
        }

        private void WaitForWork()
        {
            try
            {
                _waitHandle.Wait(IdleWaitMs);
                _waitHandle.Reset();
            }
            catch (ObjectDisposedException)
            {
                // 句柄已被释放：当作"有事要做"直接返回
            }
            catch (Exception)
            {
                Thread.Sleep(IdleWaitMs);
            }
        }

        /// <summary>把队列里当前所有行写进文件，<b>整批只 flush 一次</b>（AutoFlush=false，不该每行都 flush）。</summary>
        private void DrainQueue()
        {
            var wrote = false;
            while (_queue.TryDequeue(out var line))
            {
                if (_writer == null)
                {
                    // 文件打不开（目录不可写 / 磁盘满）：丢弃而不是让队列无上限膨胀
                    _dropped++;
                    NoteFailure(null);
                    continue;
                }

                try
                {
                    _writer.WriteLine(line);
                    wrote = true;
                }
                catch (Exception ex)
                {
                    _dropped++;
                    NoteFailure(ex);
                    // 出错后立刻丢弃句柄：下一轮重新打开（可能已恢复），也避免往坏流里继续写
                    CloseWriter();
                }
            }

            if (wrote)
            {
                try
                {
                    _writer?.Flush();
                }
                catch (Exception ex)
                {
                    NoteFailure(ex);
                    CloseWriter();
                }
            }
        }

        /// <summary>
        /// 跨天切文件。文件名<b>始终</b>是 <c>YYYY-MM-DD.log</c>，按本次写入的日期（<paramref name="today"/>）重建。
        /// </summary>
        private void RotateIfNeeded(string today)
        {
            if (_writer != null && _currentDate == today) return;

            CloseWriter();

            try
            {
                var path = Path.Combine(_logDir, today + ".log");
                _writer = new StreamWriter(path, append: true) { AutoFlush = false };
                _currentDate = today;
                _rotateFailedDate = null;
            }
            catch (Exception ex)
            {
                _currentDate = null;
                // 同一天只报一次：否则每条日志都会刷一条"打不开文件"，把 Console 冲垮
                if (_rotateFailedDate != today)
                {
                    _rotateFailedDate = today;
                    ConsoleLogger.Instance.WriteLine(LogLevel.Error,
                        $"[{TimeStamp(DateTime.Now)}] [Error] [Logger] 打开日志文件失败 '{Path.Combine(_logDir, today + ".log")}'：{ex.Message}");
                }
            }
        }

        private void CloseWriter()
        {
            var writer = _writer;
            _writer = null;
            _currentDate = null;
            if (writer == null) return;
            try
            {
                writer.Flush();
                writer.Dispose();
            }
            catch (Exception ex)
            {
                // 关闭失败（磁盘满 / 流已损坏）同样要留痕，不能空 catch 吞掉
                NoteFailure(ex);
            }
        }

        private void FlushRemaining()
        {
            try
            {
                RotateIfNeeded(DateOf(DateTime.Now));
                DrainQueue();
            }
            catch (Exception ex)
            {
                NoteFailure(ex);
            }
            finally
            {
                CloseWriter();
            }
        }

        /// <summary>写失败的降频上报：第 1 次必报，之后每 FailureReportEvery 次报一次。</summary>
        private void NoteFailure(Exception ex)
        {
            var n = ++_failures;
            if (n != 1 && n % FailureReportEvery != 0) return;

            var reason = ex == null
                ? "日志文件不可用（未能打开或已被关闭）"
                : $"写日志文件失败：{ex.Message}";
            ConsoleLogger.Instance.WriteLine(LogLevel.Error,
                $"[{TimeStamp(DateTime.Now)}] [Error] [Logger] {reason}（累计失败 {n} 次，丢弃 {_dropped} 行）");
        }

        private static string DateOf(DateTime now) => now.ToString("yyyy-MM-dd", Inv);

        private static string TimeStamp(DateTime now) => now.ToString("yyyy-MM-dd HH:mm:ss.fff", Inv);

#endif

        /// <summary>
        /// 释放日志系统资源，停止后台写入线程并刷新剩余日志。
        /// <para>
        /// 安全收尾：先置 <c>_running=false</c> 并唤醒线程，再 Join。
        /// <b>Join 超时后绝不 Dispose 写线程正在用的对象</b>（否则写线程随即抛 ObjectDisposedException、
        /// 最后一批日志丢失）—— 此时置 <c>_abandoned</c>，由写线程自己在收尾里关闭文件。
        /// </para>
        /// </summary>
        public void Dispose()
        {
#if UNITY_WEBGL
            // WebGL 下没有线程、没有文件句柄，无需收尾
            return;
#else
            if (_disposed) return;
            _disposed = true;
            _running = false;
            SignalWriter();

            if (_writerThread == null)
            {
                // 线程没起来（启动失败）：同步把剩余日志刷出去，至少不丢
                FlushRemaining();
                return;
            }

            bool joined;
            try
            {
                joined = _writerThread.Join(DisposeJoinTimeoutMs);
            }
            catch (Exception)
            {
                joined = false;
            }

            if (!joined)
            {
                _abandoned = true;
                ConsoleLogger.Instance.WriteLine(LogLevel.Warn,
                    $"[{TimeStamp(DateTime.Now)}] [Warn] [Logger] Dispose 等待写线程超时（{DisposeJoinTimeoutMs}ms）；" +
                    "文件句柄交由写线程自行关闭，避免 ObjectDisposedException");
                return;
            }

            // 线程已确认退出：此时才安全释放等待句柄。
            // _writer 由写线程的 FlushRemaining 负责关闭，这里不再重复 Dispose。
            try
            {
                _waitHandle.Dispose();
            }
            catch (Exception)
            {
                // 释放失败无副作用，忽略
            }
#endif
        }
    }
}
