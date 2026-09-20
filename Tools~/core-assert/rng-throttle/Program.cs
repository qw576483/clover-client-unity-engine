// 离线断言宿主（Rng / LogThrottle）：链接**真实的**引擎源码，不起 Unity 就能验「能编译 + 语义没坏」。
//
// 运行（在 `Tools~/core-assert/rng-throttle` 目录下，与父宿主同法）：
//   dotnet run
// 退出码 0 = 全部 PASS；任一 FAIL 会非零退出，可以直接进 CI。
//
// 时钟：本宿主是**非 Unity 进程**，故把限频时钟注入成受控变量（`LogThrottle.Clock`）⇒ 限频行为完全可复现；
// 另外专门有一段撤掉注入，验证「Unity 时钟不可用 ⇒ 自动降级到 Stopwatch 且只报一次」那条分支。
using System;
using System.Collections.Generic;
using CloverEngine;
using UnityEngine;   // Vector2Int 来自引擎源码 Rng.cs 的签名（替身在 Stubs.cs）

internal static class Program
{
    private static int _fail;

    /// <summary>捕获 <c>Game.Logger</c> 的输出（替代真实落盘 Logger），用于断言「到底出了几条、什么级别、什么 tag」。</summary>
    private static readonly CaptureLogger Captured = new CaptureLogger();

    /// <summary>受控时钟（秒）。断言通过改写它来推进/冻结时间。</summary>
    private static float _now;

    private static void Check(string name, bool ok, string detail = "")
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + name + (detail.Length > 0 ? "  -> " + detail : ""));
        if (!ok) _fail++;
    }

    private static void Main()
    {
        Game.Logger = Captured;
        LogThrottle.Suppress = false;
        LogThrottle.Clock = () => _now;
        _now = 0f;
        LogThrottle.Reset();

        Console.WriteLine("=== Rng（同 seed 同序列 / 非法参数不抛 / 确定性 shuffle） ===");
        RngChecks();

        Console.WriteLine("=== LogThrottle（限频 / 只报一次 / 空 key 不刷屏 / 时钟三级） ===");
        LogThrottleChecks();

        Console.WriteLine(_fail == 0 ? "\nALL PASS" : $"\n{_fail} CHECK(S) FAILED");
        Environment.Exit(_fail == 0 ? 0 : 1);
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Rng
    // ─────────────────────────────────────────────────────────────────────
    private static void RngChecks()
    {
        // ① 同 seed ⇒ 同序列（连取 100 个整数逐一相等）
        var a = new Rng(12345);
        var b = new Rng(12345);
        var same = true;
        var firstDiff = -1;
        for (var i = 0; i < 100; i++)
        {
            var va = a.Next();
            var vb = b.Next();
            if (va == vb) continue;
            same = false;
            if (firstDiff < 0) firstDiff = i;
        }
        Check("同 seed ⇒ 连续 100 个 Next() 全等", same, same ? "" : $"第 {firstDiff} 项起不同");

        // 同 seed ⇒ 同序列（另两个入口：Next(max) 与 NextFloat）
        var c = new Rng(999);
        var d = new Rng(999);
        var same2 = true;
        for (var i = 0; i < 100; i++)
        {
            if (c.Next(1000) != d.Next(1000)) same2 = false;
            if (c.NextFloat() != d.NextFloat()) same2 = false;
        }
        Check("同 seed ⇒ Next(max)/NextFloat 序列也全等", same2);

        // ② 不同 seed ⇒ 不同
        var e = new Rng(12345);
        var f = new Rng(12346);
        var diff = 0;
        for (var i = 0; i < 100; i++)
        {
            if (e.Next() != f.Next()) diff++;
        }
        Check("不同 seed ⇒ 序列不同", diff > 0, $"100 项中 {diff} 项不同");

        // ③ 范围语义
        var g = new Rng(7);
        var inRange = true;
        var inRange2 = true;
        var floatOk = true;
        for (var i = 0; i < 1000; i++)
        {
            var v = g.Next(100);
            if (v < 0 || v >= 100) inRange = false;
            var w = g.Next(5, 10);
            if (w < 5 || w >= 10) inRange2 = false;
            var x = g.NextFloat();
            if (x < 0f || x >= 1f) floatOk = false;
        }
        Check("Next(100) 落在 [0,100)", inRange);
        Check("Next(5,10) 落在 [5,10)", inRange2);
        Check("NextFloat() 落在 [0,1)", floatOk);

        // ④ Chance 边界
        var alwaysFalse = true;
        var alwaysTrue = true;
        var hits = 0;
        for (var i = 0; i < 1000; i++)
        {
            if (g.Chance(0f)) alwaysFalse = false;
            if (!g.Chance(1f)) alwaysTrue = false;
            if (g.Chance(0.5f)) hits++;
        }
        Check("Chance(0) 恒 false", alwaysFalse);
        Check("Chance(1) 恒 true", alwaysTrue);
        Check("Chance(0.5) 命中率落在 1000 次的 [350,650]", hits >= 350 && hits <= 650, hits.ToString());

        // 边界早退不消耗随机流（源码：<=0 / >=1 直接 return，不碰 System.Random）
        var p = new Rng(4242);
        var q = new Rng(4242);
        p.Chance(0f);
        p.Chance(1f);
        p.Chance(-1f);
        p.Chance(2f);
        Check("Chance 边界早退不消耗随机流（后续序列仍与对照一致）", p.Next() == q.Next());

        // ⑤ 非法参数：不抛异常 + 返回安全值 + 限频告警
        LogThrottle.Reset();
        Check("Next(0) ⇒ 0（不抛）", g.Next(0) == 0);
        Check("Next(-7) ⇒ 0（不抛）", g.Next(-7) == 0);
        Check("Next(5,5) ⇒ 5（不抛）", g.Next(5, 5) == 5);
        Check("Next(9,3) ⇒ 9（不抛）", g.Next(9, 3) == 9);
        Check("Range(3f,1f) ⇒ 3f（不抛）", g.Range(3f, 1f) == 3f);
        var bad = g.NextGrid(0, 8);
        Check("NextGrid(0,8) ⇒ (0,0)（不抛）", bad.x == 0 && bad.y == 0);
        Check("Pick(null) ⇒ default（不抛）", g.Pick<string>(null) == null);
        Check("Pick(空表) ⇒ default（不抛）", g.Pick(new List<string>()) == null);
        Check("PickWeighted(null) ⇒ -1（不抛）", g.PickWeighted(null) == -1);
        Check("PickWeighted(权重全 0) ⇒ -1（不抛）", g.PickWeighted(new[] { 0, 0, 0 }) == -1);
        Check("非法参数告警 tag=Rng（与源码一致）", Captured.Any(LogLevel.Warn, "Rng"));
        Check("非法参数告警带现场参数", Captured.Any(LogLevel.Warn, "Rng", "Next(maxExclusive=0)"));

        // 非法参数是**高频路径**：同一 key 必须限频（1000 次调用不多出一条）
        var before = Captured.Count(LogLevel.Warn, "Rng", "Next(maxExclusive=0)");
        for (var i = 0; i < 1000; i++) g.Next(0);
        var after = Captured.Count(LogLevel.Warn, "Rng", "Next(maxExclusive=0)");
        Check("非法参数告警限频不刷屏（再调 1000 次新增 0 条）", after - before == 0, $"新增 {after - before} 条");

        // ⑥ Pick / PickWeighted 正常路径
        var list = new List<string> { "a", "b", "c" };
        var pickOk = true;
        for (var i = 0; i < 1000; i++)
        {
            if (!list.Contains(g.Pick(list))) pickOk = false;
        }
        Check("Pick(list) 必属列表内", pickOk);
        Check("PickWeighted({0,0,5}) 恒取下标 2", g.PickWeighted(new[] { 0, 0, 5 }) == 2);
        var seen = new bool[2];
        for (var i = 0; i < 200; i++) seen[g.PickWeighted(new[] { 1, 1 })] = true;
        Check("PickWeighted({1,1}) 两个下标都会出现", seen[0] && seen[1]);

        // ⑦ Shuffle：确定性 + 原地 + 元素守恒
        var s1 = new List<int>();
        var s2 = new List<int>();
        var s3 = new List<int>();
        for (var i = 0; i < 20; i++) { s1.Add(i); s2.Add(i); s3.Add(i); }
        new Rng(2024).Shuffle(s1);
        new Rng(2024).Shuffle(s2);
        new Rng(2025).Shuffle(s3);
        Check("Shuffle 同 seed ⇒ 同排列", string.Join(",", s1) == string.Join(",", s2));
        Check("Shuffle 不同 seed ⇒ 不同排列", string.Join(",", s1) != string.Join(",", s3));
        var sorted = new List<int>(s1);
        sorted.Sort();
        var multisetOk = true;
        for (var i = 0; i < 20; i++)
        {
            if (sorted[i] != i) multisetOk = false;
        }
        Check("Shuffle 原地重排且元素守恒（不丢不重）", multisetOk, string.Join(",", s1));
        var nullShuffleOk = true;
        try { new Rng(1).Shuffle<int>(null); } catch (Exception) { nullShuffleOk = false; }
        Check("Shuffle(null) 不抛（源码：<=1 个元素直接返回）", nullShuffleOk);

        // ⑧ Derive 确定性
        var root = new Rng(1000);
        Check("DeriveSeed(7) == Seed*31+7", root.DeriveSeed(7) == 1000 * 31 + 7, root.DeriveSeed(7).ToString());
        Check("Derive(7).Seed == DeriveSeed(7)", root.Derive(7).Seed == root.DeriveSeed(7));
        var d1 = root.Derive(7);
        var d2 = root.Derive(7);
        var deriveSame = true;
        for (var i = 0; i < 50; i++)
        {
            if (d1.Next() != d2.Next()) deriveSame = false;
        }
        Check("Derive 同 salt ⇒ 同子序列（确定性）", deriveSame);
        var d3 = root.Derive(8);
        var deriveDiff = false;
        for (var i = 0; i < 50; i++)
        {
            if (d1.Next() != d3.Next()) deriveDiff = true;
        }
        Check("Derive 不同 salt ⇒ 不同子序列", deriveDiff);

        // ⑨ NextGrid 范围 / 偏移 / 确定性
        var gridRng = new Rng(3);
        var gridOk = true;
        var gridOffOk = true;
        for (var i = 0; i < 1000; i++)
        {
            var v = gridRng.NextGrid(10, 8);
            if (v.x < 0 || v.x >= 10 || v.y < 0 || v.y >= 8) gridOk = false;
            var o = gridRng.NextGrid(10, 8, new Vector2Int(5, 5));
            if (o.x < 5 || o.x >= 15 || o.y < 5 || o.y >= 13) gridOffOk = false;
        }
        Check("NextGrid(10,8) 落在 [0,10)×[0,8)", gridOk);
        Check("NextGrid(10,8,from(5,5)) 带偏移落在 [5,15)×[5,13)", gridOffOk);
        var g1 = new Rng(88).NextGrid(50, 50);
        var g2 = new Rng(88).NextGrid(50, 50);
        Check("NextGrid 同 seed 同格（可复现）", g1.x == g2.x && g1.y == g2.y, $"{g1} vs {g2}");

        // ⑩ Index 是 Next(size) 的语义化别名
        Check("Index(size) 语义同 Next(size)", new Rng(555).Index(10) == new Rng(555).Next(10));

        // ⑪ FromTime：唯一允许「不可复现」的入口，且必须留日志
        var t1 = Rng.FromTime();
        var t2 = Rng.FromTime();
        Check("FromTime() 产出非负 seed", t1.Seed >= 0 && t2.Seed >= 0, $"{t1.Seed}/{t2.Seed}");
        Check("FromTime() 打 Info 日志（tag=Rng）", Captured.Any(LogLevel.Info, "Rng", "FromTime seed="));
    }

    // ─────────────────────────────────────────────────────────────────────
    //  LogThrottle
    // ─────────────────────────────────────────────────────────────────────
    private static void LogThrottleChecks()
    {
        Captured.Clear();
        LogThrottle.Suppress = false;
        LogThrottle.Reset();
        _now = 0f;
        LogThrottle.Clock = () => _now;

        Check("注入时钟 ⇒ ClockSource == Injected", LogThrottle.ClockSource == "Injected", LogThrottle.ClockSource);

        // ① 限频：间隔内只出 1 条，跨过间隔再出
        Check("WarnThrottled 首次 ⇒ true", LogThrottle.WarnThrottled("Assert", "k1", "m1", 5f));
        _now = 1f;
        Check("间隔内第 2 次 ⇒ false", !LogThrottle.WarnThrottled("Assert", "k1", "m1", 5f));
        _now = 4.99f;
        Check("间隔边界内（4.99 < 5）⇒ false", !LogThrottle.WarnThrottled("Assert", "k1", "m1", 5f));
        _now = 5f;
        Check("跨过间隔（now-last == 5）⇒ true", LogThrottle.WarnThrottled("Assert", "k1", "m1", 5f));
        Check("限频后确实只输出 2 条", Captured.Count(LogLevel.Warn, "Assert", "m1") == 2,
            Captured.Count(LogLevel.Warn, "Assert", "m1").ToString());
        Check("不同 key 互不影响", LogThrottle.WarnThrottled("Assert", "k2", "m2", 5f));
        Check("ErrorThrottled 走 Error 级别", LogThrottle.ErrorThrottled("Assert", "k3", "e3", 5f) &&
            Captured.Count(LogLevel.Error, "Assert", "e3") == 1);

        // ② ShouldLog(key, +∞) = 只报一次
        Check("ShouldLog(+∞) 首次 ⇒ true", LogThrottle.ShouldLog("once", float.PositiveInfinity));
        Check("ShouldLog(+∞) 第 2 次 ⇒ false", !LogThrottle.ShouldLog("once", float.PositiveInfinity));
        _now += 10000f;
        Check("ShouldLog(+∞) 时间再久 ⇒ false（整进程一次）", !LogThrottle.ShouldLog("once", float.PositiveInfinity));

        // ③ WarnOnce / ErrorOnce：第 2 次返回 false
        Check("WarnOnce 首次 ⇒ true", LogThrottle.WarnOnce("Assert", "w1", "warn-once-1"));
        Check("WarnOnce 第 2 次 ⇒ false", !LogThrottle.WarnOnce("Assert", "w1", "warn-once-1"));
        Check("ErrorOnce 首次 ⇒ true", LogThrottle.ErrorOnce("Assert", "e1", "error-once-1"));
        Check("ErrorOnce 第 2 次 ⇒ false", !LogThrottle.ErrorOnce("Assert", "e1", "error-once-1"));
        Check("WarnOnce 走 Warn 级别且只 1 条", Captured.Count(LogLevel.Warn, "Assert", "warn-once-1") == 1);
        Check("ErrorOnce 走 Error 级别且只 1 条", Captured.Count(LogLevel.Error, "Assert", "error-once-1") == 1);

        // ④ 空 key：不输出 + 只报一次 Warn（⛔ 不刷屏）
        Check("ShouldLog(\"\") ⇒ false", !LogThrottle.ShouldLog(string.Empty));
        Check("ShouldLog(null) ⇒ false", !LogThrottle.ShouldLog(null));
        Check("WarnThrottled(空 key) ⇒ false", !LogThrottle.WarnThrottled("Assert", string.Empty, "空 key 的消息"));
        Check("ErrorOnce(空 key) ⇒ false", !LogThrottle.ErrorOnce("Assert", null, "空 key 的消息"));
        for (var i = 0; i < 1000; i++) LogThrottle.ShouldLog(null);
        Check("空 key 的调用方消息一条都不输出", Captured.Count(LogLevel.Warn, "Assert", "空 key 的消息") == 0);
        Check("空 key 内部告警只报一次（1000 次调用仍 1 条）",
            Captured.Count(LogLevel.Warn, "LogThrottle", "空 key") == 1,
            Captured.Count(LogLevel.Warn, "LogThrottle", "空 key").ToString());

        // ⑤ Suppress
        LogThrottle.Suppress = true;
        Check("Suppress=true ⇒ ShouldLog false", !LogThrottle.ShouldLog("suppress-key"));
        Check("Suppress=true ⇒ WarnThrottled false", !LogThrottle.WarnThrottled("Assert", "suppress-key", "suppress-msg"));
        Check("Suppress=true ⇒ 不产生任何日志", Captured.Count(LogLevel.Warn, "Assert", "suppress-msg") == 0);
        LogThrottle.Suppress = false;

        // ⑥ Reset() 后重新可出（换场景/重进游戏）
        LogThrottle.Reset();
        Check("Reset() 后同一 key 重新可出（不必等间隔）", LogThrottle.ShouldLog("k1"));

        // ⑦ 时钟三级：注入 → Unity → 降级 Stopwatch（本宿主是非 Unity 进程，Time 替身如实抛异常）
        var beforeDowngrade = Captured.Count(LogLevel.Warn, "LogThrottle", "Unity 时钟不可用");
        LogThrottle.Clock = null;
        LogThrottle.Reset();
        Check("Unity 时钟不可用仍能判定（ShouldLog ⇒ true，不抛异常）", LogThrottle.ShouldLog("downgrade"));
        Check("ClockSource == Process（已降级到 Stopwatch）", LogThrottle.ClockSource == "Process", LogThrottle.ClockSource);
        Check("降级事实恰好报 1 条（直发 Game.Logger）",
            Captured.Count(LogLevel.Warn, "LogThrottle", "Unity 时钟不可用") - beforeDowngrade == 1,
            $"新增 {Captured.Count(LogLevel.Warn, "LogThrottle", "Unity 时钟不可用") - beforeDowngrade} 条");
        Check("降级 Warn 带异常类型（现场可定位）", Captured.Any(LogLevel.Warn, "LogThrottle", "SecurityException"));
        Check("降级 Warn 提示注入时钟", Captured.Any(LogLevel.Warn, "LogThrottle", "LogThrottle.Clock"));
        for (var i = 0; i < 200; i++) LogThrottle.ShouldLog("dlg" + i);
        Check("只探测一次：再 200 次调用仍是 1 条降级告警",
            Captured.Count(LogLevel.Warn, "LogThrottle", "Unity 时钟不可用") - beforeDowngrade == 1);
        Check("降级后限频仍生效（进程单调时钟）",
            LogThrottle.ShouldLog("proc-clock") && !LogThrottle.ShouldLog("proc-clock"));
        LogThrottle.Clock = () => _now;
        Check("重新注入 ⇒ ClockSource == Injected（可恢复）", LogThrottle.ClockSource == "Injected", LogThrottle.ClockSource);
    }
}

/// <summary>
/// 捕获日志的替身 logger（实现真实的 <see cref="ILogger"/>）：断言「出了几条 / 什么级别 / 什么 tag / 消息里有什么」。
/// </summary>
internal sealed class CaptureLogger : ILogger
{
    internal sealed class Entry
    {
        public LogLevel Level;
        public string Tag;
        public string Message;
    }

    private readonly List<Entry> _entries = new List<Entry>();

    public LogLevel Level { get; set; } = LogLevel.Debug;

    public void Debug(string tag, string msg) => Add(LogLevel.Debug, tag, msg);
    public void Info(string tag, string msg) => Add(LogLevel.Info, tag, msg);
    public void Warn(string tag, string msg) => Add(LogLevel.Warn, tag, msg);
    public void Error(string tag, string msg, Exception ex = null) => Add(LogLevel.Error, tag, msg);
    public void Fatal(string tag, string msg, Exception ex = null) => Add(LogLevel.Fatal, tag, msg);

    public void Clear() => _entries.Clear();

    public bool Any(LogLevel level, string tag, string contains = null) => Count(level, tag, contains) > 0;

    public int Count(LogLevel level, string tag, string contains = null)
    {
        var n = 0;
        for (var i = 0; i < _entries.Count; i++)
        {
            var e = _entries[i];
            if (e.Level != level) continue;
            if (e.Tag != tag) continue;
            if (contains != null && (e.Message == null || e.Message.IndexOf(contains, StringComparison.Ordinal) < 0)) continue;
            n++;
        }
        return n;
    }

    private void Add(LogLevel level, string tag, string msg)
    {
        // 级别过滤与真实 Logger 一致（本宿主的断言全部用 Warn/Error/Info，不会落到 Debug 分支）
        if (level < Level) return;
        _entries.Add(new Entry { Level = level, Tag = tag, Message = msg });
    }
}
