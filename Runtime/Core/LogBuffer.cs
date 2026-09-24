// ─────────────────────────────────────────────────────────────────────────────
// CloverEngine · Runtime/Core/LogBuffer.cs
// 运行时日志环形缓冲：**最近 N 行的只读窗口** —— 补 `ILogger` 只写不读的缺口，通用横切能力。
//
//   语义：环形覆盖 / 行数版本号 / 线程安全入队 + 主线程搬运 / `Drain` 返回新增行数 /
//   控制台本地回显（`Push`）/ 启动时 `BeforeSceneLoad` 自动挂钩。
//
// ⛔ **不属本件**（项目专属）：
//   · 「面板怎么画」—— 显示几行、怎么截断、输入框与命令解析全属表现 / 业务。
//   · 行级过滤 / tag 白名单 / 检索约定 —— 每个项目的日志检索脚本不同，引擎不替项目判定。
//
// ★ 通用性依据：`ILogger`（`Runtime/Core/Logger.cs:52-95`）只有 `Debug / Info / Warn / Error / Fatal`，
//   **没有读取 / 订阅接口** —— 本件补上这条读取通道（否则实现「游戏内控制台看最近日志」
//   需自己挂 `Application.logMessageReceivedThreaded` 再写一遍环形缓冲）。
//   （与 `LogThrottle` 同批的 D 桶能力，见 skill `patterns/engine-fix.md` §7）。
//
// 语义约束（改一条 = 语义漂移）：
//   ① **不是第二套 `ILogger`**：本类只"收行 + 存最近 N 行"，不写行、不是日志门面，
//      `Logger.cs` / `ILogger` **一行未动**；要正常打日志请用 `Game.Logger`。
//   ② 线程：`Application.logMessageReceivedThreaded` 在**任意线程**触发 ⇒ 入队只走
//      `ConcurrentQueue`（回调里不碰主线程状态）；`Version` / `Lines` / `Drain` / `Push` / `Clear`
//      **只保证主线程读 / 调**，与引擎 `Dispatcher` 的口径一致。
//   ③ 环形覆盖：超过 `Capacity` 丢最旧；`capacity <= 0` 视为 `DefaultCapacity`（400）。
//   ④ `Install` 幂等（重复调不重复挂）；`Uninstall` 可重复调、之后再 `Install` 要能重新工作；
//      `Uninstall` 只解挂、**不清内容**（清内容用 `Clear`，两者语义不同）。
//   ⑤ `Push(null / 空)` ⇒ 忽略（⛔ 不许把空行塞进面板）。
//   ⑥ G8（结构规则 §5.3）：`Lines` 交出的是**只读契约**（`IReadOnlyList<string>`），业务不得向下转型去改它。
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using UnityEngine;

namespace CloverEngine
{
    /// <summary>
    /// 运行时日志环形缓冲：保存**最近 N 行**日志文本，供"游戏内控制台"这类 UI 读。
    /// <para>
    /// **为什么存在**：<see cref="ILogger"/> 只有"写"（<see cref="Logger"/> 最终把每行交给
    /// <c>UnityEngine.Debug.Log/LogWarning/LogError</c>），**没有读取 / 订阅接口**。
    /// 本类挂 Unity 的公开回调 <c>Application.logMessageReceivedThreaded</c> 收整行文本
    /// （形如 <c>[2026-01-01 12:00:00.000] [Info] [Match] ...</c>）—— 这是引擎**公开 API**，不是私有反射。
    /// </para>
    /// <para>
    /// **用法**：默认在 <c>BeforeSceneLoad</c> 自动 <see cref="Install"/> 一次（进程一起来就收行，
    /// 不依赖面板是否打开）；不想收的宿主可 <see cref="Uninstall"/>（或反复
    /// <see cref="Uninstall"/> / <see cref="Install"/> 控制开关）。UI 侧每帧调 <see cref="Drain"/>
    /// 搬运一次，并用 <see cref="Version"/> 判断"要不要重画"。
    /// </para>
    /// <para>
    /// **线程**：入队线程安全（<see cref="ConcurrentQueue{T}"/>）；<see cref="Version"/> /
    /// <see cref="Lines"/> / <see cref="Drain"/> / <see cref="Push"/> / <see cref="Clear"/>
    /// 只在主线程调用（与引擎 <c>Dispatcher</c> 一致）。
    /// </para>
    /// </summary>
    public static class LogBuffer
    {
        /// <summary>默认保留行数（够翻一回合的战斗日志）。</summary>
        public const int DefaultCapacity = 400;

        /// <summary>内部告警的 tag（引擎惯例：各能力用类名自报家门，见 Runtime/Core/*.cs）。</summary>
        private const string InternalTag = "LogBuffer";

        /// <summary>非主线程回调只入这里（回调里不碰 <c>_lines</c> / <c>Version</c>）。</summary>
        private static readonly ConcurrentQueue<string> Pending = new ConcurrentQueue<string>();

        /// <summary>最近 N 行（**只由主线程访问**）。</summary>
        private static readonly List<string> Buffered = new List<string>(DefaultCapacity);

        private static int _capacity = DefaultCapacity;
        private static bool _installed;
        /// <summary>
        /// 自上次 <see cref="Drain"/> 起**经 <see cref="Push"/> 本地回显**的行数（主线程独占，无需原子操作）——
        /// 日志行那条路的新增数由 <see cref="Drain"/> 直接从队列取走的行数得到。
        /// </summary>
        private static int _pushedSinceDrain;

        /// <summary>保留行数；由 <see cref="Install"/> 确定（<c>&lt;= 0</c> 视为 <see cref="DefaultCapacity"/>）。</summary>
        public static int Capacity => _capacity;

        /// <summary>行数版本号：每次内容变化自增 —— 调用方用它判断"有没有新行"，避免每帧重拼字符串。</summary>
        public static int Version { get; private set; }

        /// <summary>已缓冲的行（只读契约，**只允许主线程读**；G8：不把可变表交出去）。</summary>
        public static IReadOnlyList<string> Lines => Buffered;

        /// <summary>当前是否已挂钩（<c>Application.logMessageReceivedThreaded</c>）。</summary>
        public static bool Installed => _installed;

        /// <summary>
        /// 进程启动就挂钩子（<c>BeforeSceneLoad</c>）—— 否则打开控制台时里面永远是空的。
        /// 找不到 / 不想自动装的宿主可显式 <see cref="Uninstall"/>。
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void HookFromRuntime() => Install();

        /// <summary>
        /// 挂钩子并设定保留行数。**幂等**：已挂钩时重复调用只是重设 <see cref="Capacity"/>
        /// （不会重复挂）。挂钩失败（<c>Application</c> 不可用等）⇒ **不抛**，记一条 Warn 并把
        /// <see cref="Installed"/> 复位为 false（调用方可重试）。
        /// </summary>
        /// <param name="capacity">保留行数；<c>&lt;= 0</c> 视为 <see cref="DefaultCapacity"/>。</param>
        public static void Install(int capacity = DefaultCapacity)
        {
            _capacity = capacity > 0 ? capacity : DefaultCapacity;
            if (_installed) return;

            try
            {
                Application.logMessageReceivedThreaded += OnLogMessage;
                _installed = true;
            }
            catch (Exception ex)
            {
                // 非预期分支：挂钩失败不该让整个游戏起不来，但必须留痕（控制台会看不到日志）
                _installed = false;
                Game.Logger?.Warn(InternalTag,
                    $"订阅 Application.logMessageReceivedThreaded 失败，日志缓冲将收不到新行：{ex.Message}");
            }
        }

        /// <summary>解挂（可重复调用）。**只解挂、不清内容** —— 清内容用 <see cref="Clear"/>。</summary>
        public static void Uninstall()
        {
            if (!_installed) return;
            _installed = false;
            try
            {
                Application.logMessageReceivedThreaded -= OnLogMessage;
            }
            catch (Exception ex)
            {
                Game.Logger?.Warn(InternalTag, $"解挂 Application.logMessageReceivedThreaded 失败：{ex.Message}");
            }
        }

        /// <summary>
        /// 主线程：把队列搬进环形缓冲，返回**自上次 <see cref="Drain"/> 起新增的行数**
        /// （含经 <see cref="Push"/> 本地回显的行），并把该计数归零。没有新行时不做任何事
        /// （<see cref="Version"/> 不变 ⇒ UI 无需重画）。
        /// </summary>
        public static int Drain()
        {
            var moved = 0;
            while (Pending.TryDequeue(out var line))
            {
                Buffered.Add(line);
                moved++;
            }

            var added = moved + _pushedSinceDrain;
            _pushedSinceDrain = 0;
            if (added == 0) return 0;

            Trim();
            Version++;
            return added;
        }

        /// <summary>
        /// 本地回显：控制台里输入的那一行（命令 / 错误提示）也进同一份缓冲，读起来才连贯。
        /// 只允许主线程调；<paramref name="text"/> 为空 / <c>null</c> ⇒ 忽略。
        /// </summary>
        public static void Push(string text)
        {
            if (string.IsNullOrEmpty(text)) return;

            Buffered.Add($"[{DateTime.Now:HH:mm:ss}]  > {text}");
            Trim();
            Version++;
            _pushedSinceDrain++;
        }

        /// <summary>清空（回主菜单 / 新比赛时调，避免上一局的日志和新一局混在一起）。只允许主线程调。</summary>
        public static void Clear()
        {
            while (Pending.TryDequeue(out _)) { }
            Buffered.Clear();
            _pushedSinceDrain = 0;
            Version++;
        }

        // ── 内部 ─────────────────────────────────────────────────────────────

        /// <summary>环形覆盖：只留最后 <see cref="Capacity"/> 行（丢最旧）。主线程调。</summary>
        private static void Trim()
        {
            if (Buffered.Count > _capacity) Buffered.RemoveRange(0, Buffered.Count - _capacity);
        }

        /// <summary>
        /// **任意线程**回调：只把整行文本塞进队列（不碰 <see cref="Buffered"/> / <see cref="Version"/> /
        /// <c>_pushedSinceDrain</c>），真正的搬运发生在主线程的 <see cref="Drain"/>。
        /// </summary>
        private static void OnLogMessage(string condition, string stackTrace, LogType type)
        {
            if (string.IsNullOrEmpty(condition)) return;

            Pending.Enqueue($"[{DateTime.Now:HH:mm:ss}] {LevelTag(type)} {condition}");
        }

        /// <summary>Unity 日志级别 → 缓冲行里的短级别标签（`ERR` / `AST` / `WRN` / `EXC` / `LOG`）。</summary>
        private static string LevelTag(LogType type)
        {
            switch (type)
            {
                case LogType.Error: return "ERR";
                case LogType.Assert: return "AST";
                case LogType.Warning: return "WRN";
                case LogType.Exception: return "EXC";
                default: return "LOG";
            }
        }
    }
}
