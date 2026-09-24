// ─────────────────────────────────────────────────────────────────────────────
// CloverEngine · Runtime/Core/LogThrottle.cs
// 日志防刷屏：限频 / 只报一次 / 可注入时钟 / Unity 时钟不可用时自动降级 —— 通用横切能力。
//
// ⛔ **不属本件**（项目专属）：
//   · `KnownTags` tag 白名单与 `Normalize(tag)` —— 项目用它保证验收脚本能按 `[tag]` 检索日志，
//     合法 tag 集是每个项目自己的约定，引擎不该替项目判定。
//   · 基础转发 `Info` / `Warn` / `Error` / `Debug` —— 引擎已有 `Game.Logger`（`ILogger`），
//     本类**只**提供「降频闸门 + 时钟」，不做第二套日志门面。
//   · "不降频直发一条 Info"这类方法 —— 它等价于 `Game.Logger.Info(tag, msg)`，再包一层转发
//     方法只是别名（§4.4：已有同类能力不准再起第二套）。
//   · per-tag 薄适配类本身 —— tag 由每个调用方自己传，引擎不该替业务持有 tag。
//
// 计数口径：**第 1 次必打**，之后每 N 次打一条，第 1 次之后行尾补 `（同类第 N 次）`，
//   空 key 归并 `"default"`。本类把它实现为
//   `ShouldLogEvery` / `InfoCounted` / `WarnCounted` / `ErrorCounted`，per-tag 薄适配（转发）留给调用方。
//   计数间隔是**业务侧数值**（默认 50），由调用方通过参数传入，⛔ 不进引擎。
//
// ★ 通用性依据：高频回调（每帧寻路失败 / 素材缺失 / 每帧碰撞失败）裸打日志会把日志文件打爆，
//   限频是任何项目都要的底座（时钟不可用时需有统一的降级口径，也是
//   `patterns/engine-fix.md` §7 的 D 桶判定）。
//
// 语义约束（改一条 = 语义漂移）：
//   ① 时钟三级，**永不抛异常**：已注入 <see cref="Clock"/> ⇒ 用它；否则 `Time.realtimeSinceStartup`
//      （非 Unity 进程调用原生 ECall 会抛，只探测一次）；再否则降级到进程单调时钟（Stopwatch）。
//   ② `ShouldLog(key, +∞)` = 只报一次；空/`null` key ⇒ 不输出 + 只报一次 Warn（⛔ 不刷屏）。
//   ③ 降级那条 Warn **直发 `Game.Logger.Warn`**，⛔ 不许走 `WarnThrottled` / `ShouldLog`
//      （那会从 `Now()` 递归回降频闸门）。
//   ④ 非线程安全：主线程使用。
//   ⑤ **两种口径并存、互不替换**：时间口径（`ShouldLog` / `*Throttled` / `*Once`）与
//      计数口径（`ShouldLogEvery` / `*Counted`）解决的是不同问题（前者防"每帧刷屏"，后者是
//      "偶发但一局出现很多次"的打点抽样）；`Reset()` 把两种记录**一起**清空。
//   ⑥ 计数口径的边界：`everyN <= 1` 视为 1（= 每次都打，⛔ 不除零、不死循环）；
//      空 / `null` key 归并为 `"default"`（**刻意**不同于 `ShouldLog` 的"空 key 恒 false + 只报一次"，
//      ⛔ 不许改 `ShouldLog`）；`Suppress == true` ⇒ 三个 `*Counted` 一律 false。
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;

namespace CloverEngine
{
    /// <summary>
    /// 日志降频闸门（对 <c>Game.Logger</c> 的包装，不改 <c>ILogger</c>）。
    /// <para>
    /// 用于**高频**非预期分支：同一 <c>key</c> 在间隔内只输出第一条；整进程只说一次的事用
    /// <see cref="WarnOnce"/> / <see cref="ErrorOnce"/>。命中间隔时**不产生任何日志**。
    /// </para>
    /// <para>
    /// **两种口径并存，语义不同、不可互相替换**：
    /// ① <b>时间口径</b>（<see cref="ShouldLog"/> / <see cref="WarnThrottled"/> /
    /// <see cref="ErrorThrottled"/> / <see cref="WarnOnce"/> / <see cref="ErrorOnce"/>）—— 治"每帧刷屏"；
    /// ② <b>计数口径</b>（<see cref="ShouldLogEvery"/> / <see cref="InfoCounted"/> /
    /// <see cref="WarnCounted"/> / <see cref="ErrorCounted"/>）—— 治"偶发但一局出现很多次"的打点抽样：
    /// 每 <c>key</c> 独立计数，**第 1 次必打**，之后每 <c>everyN</c> 次打一条，行尾自动补
    /// <c>（同类第 N 次）</c>，空 key 归并为 <c>"default"</c>。
    /// </para>
    /// <para>
    /// **离线宿主（非 Unity 进程）请注入时钟**：<c>LogThrottle.Clock = () =&gt; mySeconds;</c>。
    /// 注入后限频行为完全可复现（确定性）；不注入也不会抛异常 —— 会自动降级到进程单调时钟，
    /// 只是时间原点不可控。排障看 <see cref="ClockSource"/>。
    /// </para>
    /// </summary>
    public static class LogThrottle
    {
        /// <summary>内部告警的 tag（引擎惯例：各能力用类名自报家门，见 Runtime/Core/*.cs）。</summary>
        private const string InternalTag = "LogThrottle";

        /// <summary>全局静默开关（只给压测/自动化用；正常流程不要打开）。</summary>
        public static bool Suppress { get; set; }

        // ── 时钟（单调秒）────────────────────────────────────────────────────
        /// <summary>
        /// 可注入时钟：返回**单调递增的秒**，语义与 <c>UnityEngine.Time.realtimeSinceStartup</c> 一致
        /// （与帧率 / <c>timeScale</c> 无关，用于限频计时）。
        /// <para>默认 <c>null</c> = 用 Unity 的 <c>Time.realtimeSinceStartup</c>。
        /// **离线宿主请注入自己的时钟**，例如
        /// <c>LogThrottle.Clock = () =&gt; (float)sw.Elapsed.TotalSeconds;</c> —— 注入后限频行为确定可复现；
        /// 不注入也不会抛异常（见 <see cref="Now"/> 的自动降级）。</para>
        /// </summary>
        public static Func<float> Clock { get; set; }

        /// <summary>
        /// 当前生效的时钟来源：<c>"Injected"</c>（已注入 <see cref="Clock"/>）/ <c>"Unity"</c>
        /// （默认，尚未探测）/ <c>"Process"</c>（Unity 时钟不可用，已降级到 <c>Stopwatch</c>）。
        /// **供自检与排障用**，业务不要依赖它。
        /// </summary>
        public static string ClockSource =>
            Clock != null ? "Injected" : (_unityClockUnavailable ? "Process" : "Unity");

        private static bool _unityClockUnavailable;
        private static Func<float> _processClock;

        // ── 限频表 ───────────────────────────────────────────────────────────
        private static readonly Dictionary<string, float> LastEmitAt =
            new Dictionary<string, float>(StringComparer.Ordinal);

        /// <summary>空 key 告警是否已出过（空 key 无法降频 ⇒ 只说一次，避免自刷屏）。</summary>
        private static bool _emptyKeyWarned;

        /// <summary>
        /// 限频闸门：返回 true 表示「现在可以打」。命中闸门时不产生任何日志。
        /// <para><paramref name="key"/> 为空 / <c>null</c> ⇒ 恒 false（无法降频就不输出），
        /// 并**只报一次** Warn 把问题暴露出来。</para>
        /// </summary>
        public static bool ShouldLog(string key, float intervalSeconds = 5f)
        {
            if (Suppress) return false;

            if (string.IsNullOrEmpty(key))
            {
                // 没给 key 就无法降频：按不允许处理，并把问题暴露出来（只报一次，避免自刷屏）
                if (!_emptyKeyWarned)
                {
                    _emptyKeyWarned = true;
                    Game.Logger?.Warn(InternalTag, "LogThrottle.ShouldLog 收到空 key，已按不输出处理");
                }
                return false;
            }

            var now = Now();
            if (LastEmitAt.TryGetValue(key, out var last))
            {
                if (float.IsPositiveInfinity(intervalSeconds)) return false;
                if (now - last < intervalSeconds) return false;
            }

            LastEmitAt[key] = now;
            return true;
        }

        /// <summary>
        /// 限频警告：同一 <paramref name="key"/> 在 <paramref name="intervalSeconds"/> 秒内只输出第一条。
        /// 用于**高频回调**里的非预期分支（每帧都可能触发的素材缺失 / 寻路失败等）。
        /// </summary>
        /// <returns>本次是否真的输出了日志。</returns>
        public static bool WarnThrottled(string tag, string key, string message, float intervalSeconds = 5f)
        {
            if (!ShouldLog(key, intervalSeconds)) return false;
            Game.Logger?.Warn(tag, message);
            return true;
        }

        /// <summary>限频错误：语义同 <see cref="WarnThrottled"/>，级别为 Error。</summary>
        /// <returns>本次是否真的输出了日志。</returns>
        public static bool ErrorThrottled(string tag, string key, string message, float intervalSeconds = 5f)
        {
            if (!ShouldLog(key, intervalSeconds)) return false;
            Game.Logger?.Error(tag, message);
            return true;
        }

        /// <summary>
        /// 只报一次（Warn 级）：同一 <paramref name="key"/> **整个进程生命周期内**只输出一条。
        /// 用于「初始化失败」「降级到占位资源」这类说一遍就够的事。
        /// </summary>
        /// <returns>本次是否真的输出了日志。</returns>
        public static bool WarnOnce(string tag, string key, string message)
        {
            if (!ShouldLog(key, float.PositiveInfinity)) return false;
            Game.Logger?.Warn(tag, message);
            return true;
        }

        /// <summary>只报一次（Error 级），语义同 <see cref="WarnOnce"/>。</summary>
        /// <returns>本次是否真的输出了日志。</returns>
        public static bool ErrorOnce(string tag, string key, string message)
        {
            if (!ShouldLog(key, float.PositiveInfinity)) return false;
            Game.Logger?.Error(tag, message);
            return true;
        }

        // ── 计数闸门（S1 计数口径：每 key 计数，第 1 次 + 之后每 N 次）─────────────────
        /// <summary><c>everyN</c> 缺省值 = 50（业务可另传）。</summary>
        private const int DefaultEveryN = 50;

        /// <summary>计数口径里空 / <c>null</c> key 的归并名（⛔ 与 <see cref="ShouldLog"/> 的空 key 语义刻意不同）。</summary>
        private const string DefaultKey = "default";

        /// <summary>计数表：key → 已出现次数（**只由主线程访问**）。</summary>
        private static readonly Dictionary<string, int> Counters =
            new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>
        /// **计数闸门**：每 <paramref name="key"/> **独立计数** —— <c>count == 1</c> 恒 <c>true</c>；
        /// 之后 <c>count % everyN == 0</c> 才 <c>true</c>。命中闸门时**不产生任何日志**，
        /// 需要连日志一起出时用 <see cref="InfoCounted"/> / <see cref="WarnCounted"/> / <see cref="ErrorCounted"/>。
        /// <para>边界：<paramref name="everyN"/> &lt;= 1 视为 1（= 每次都打，⛔ 不除零 / 不死循环）；
        /// <paramref name="key"/> 为空 / <c>null</c> ⇒ 归并为 <c>"default"</c>（与
        /// <see cref="ShouldLog"/> 的"空 key 恒 false + 只报一次"**刻意不同**，那条**不许**改）；
        /// <see cref="Suppress"/> 为 <c>true</c> ⇒ 恒 <c>false</c>（且不计入计数表）。</para>
        /// <para>**非线程安全，主线程使用**（与时间口径一致）。</para>
        /// </summary>
        public static bool ShouldLogEvery(string key, int everyN = DefaultEveryN)
        {
            if (Suppress) return false;

            if (everyN <= 1) everyN = 1;
            if (string.IsNullOrEmpty(key)) key = DefaultKey;

            Counters.TryGetValue(key, out var count);
            count++;
            Counters[key] = count;

            return count == 1 || count % everyN == 0;
        }

        /// <summary>
        /// 计数口径的 Info：命中（第 1 次 / 每 <paramref name="everyN"/> 次）则输出且返回 <c>true</c>，
        /// 行尾自动补 <c>（同类第 N 次）</c>（<c>count == 1</c> 时不补 —— 首行保持干净可检索）。
        /// 参数与边界语义同 <see cref="ShouldLogEvery"/>。
        /// </summary>
        /// <returns>本次是否真的输出了日志。</returns>
        public static bool InfoCounted(string tag, string key, string message, int everyN = DefaultEveryN)
        {
            if (!ShouldLogEvery(key, everyN)) return false;
            Game.Logger?.Info(tag, CountedLine(key, message));
            return true;
        }

        /// <summary>计数口径的 Warn：语义同 <see cref="InfoCounted"/>，级别为 Warn。</summary>
        /// <returns>本次是否真的输出了日志。</returns>
        public static bool WarnCounted(string tag, string key, string message, int everyN = DefaultEveryN)
        {
            if (!ShouldLogEvery(key, everyN)) return false;
            Game.Logger?.Warn(tag, CountedLine(key, message));
            return true;
        }

        /// <summary>计数口径的 Error：语义同 <see cref="InfoCounted"/>，级别为 Error。</summary>
        /// <returns>本次是否真的输出了日志。</returns>
        public static bool ErrorCounted(string tag, string key, string message, int everyN = DefaultEveryN)
        {
            if (!ShouldLogEvery(key, everyN)) return false;
            Game.Logger?.Error(tag, CountedLine(key, message));
            return true;
        }

        /// <summary>
        /// 行尾计数后缀：<c>（同类第 {count} 次）</c>（**全角括号**，与项目现值逐字一致 ——
        /// 换成半角会让按 `（同类第` 检索日志的脚本对不上）；<c>count == 1</c> 时不加后缀。
        /// </summary>
        private static string CountedLine(string key, string message)
        {
            var k = string.IsNullOrEmpty(key) ? DefaultKey : key;
            return Counters.TryGetValue(k, out var count) && count > 1
                ? message + $"（同类第 {count} 次）"
                : message;
        }

        /// <summary>
        /// 清空**所有**限频记录（换场景 / 重进游戏时调用）：时间口径的 <c>LastEmitAt</c> 与
        /// 计数口径的计数表**一并清空** —— 语义 = "清空所有限频记录"，清完后每个 key 的
        /// 「首次必打」重新生效（避免「上次进图报过就不再报」）。
        /// </summary>
        public static void Reset()
        {
            LastEmitAt.Clear();
            Counters.Clear();
        }

        // ── 内部 ─────────────────────────────────────────────────────────────
        /// <summary>
        /// 取当前单调秒（降频闸门唯一入口）。三级取值，**保证永不抛异常**：
        /// ① 已注入 <see cref="Clock"/> ⇒ 用它（离线宿主走这条，行为可复现）；
        /// ② 否则用 Unity `Time.realtimeSinceStartup`（原生 ECall）—— **非 Unity 进程会抛**
        ///    ⇒ `try/catch` 接住并进入 ③（只探测一次）；
        /// ③ 进程单调时钟（`Stopwatch.Elapsed`），并把降级事实报一次（用 `Game.Logger` **直发**，
        ///    不走 `ShouldLog`，否则会递归回到本方法）。
        /// </summary>
        private static float Now()
        {
            var injected = Clock;
            if (injected != null) return injected();

            if (!_unityClockUnavailable)
            {
                try
                {
                    return UnityEngine.Time.realtimeSinceStartup;
                }
                catch (Exception ex)
                {
                    // 非预期分支（离线宿主）：必须留日志，且**只留一条**（否则每次限频调用都刷屏）。
                    _unityClockUnavailable = true;
                    _processClock = CreateProcessClock();
                    Game.Logger?.Warn(InternalTag,
                        $"Unity 时钟不可用（Time.realtimeSinceStartup 抛 {ex.GetType().Name}）⇒ LogThrottle 的限频入口" +
                        "已降级到进程单调时钟（Stopwatch）；离线宿主请注入 LogThrottle.Clock 以获得确定性计时");
                }
            }

            return _processClock != null ? _processClock() : 0f;
        }

        /// <summary>进程单调时钟：语义同 `realtimeSinceStartup`（秒，自本方法调用时刻起算，不受帧率/timeScale 影响）。</summary>
        private static Func<float> CreateProcessClock()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            return () => (float)sw.Elapsed.TotalSeconds;
        }
    }
}
