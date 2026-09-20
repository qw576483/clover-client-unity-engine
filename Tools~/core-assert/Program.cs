// 离线断言宿主：链接**真实的**引擎源码（Runtime/Core/{Json,Event,Setting}.cs），
// 断言本轮客户端修复项的行为。用完即删（属 <项目根>/.ai-tmp/test/）。
//
// 运行：
//   dotnet run                                  # 常驻平台：Setting 原子写 + 往返 + 损坏留档
//   dotnet run -p:DefineConstants=UNITY_WEBGL    # WebGL：Setting 退化为内存存储（不写盘）
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using CloverEngine;

internal static class Program
{
    private static int _fail;

    private static void Check(string name, bool ok, string detail = "")
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + name + (detail.Length > 0 ? "  -> " + detail : ""));
        if (!ok) _fail++;
    }

    private static void Main()
    {
        Console.WriteLine("=== Json（循环引用 / 类型保型） ===");
        JsonChecks();

        Console.WriteLine("=== Event（通配订阅 * / **） ===");
        EventChecks();

        Console.WriteLine("=== Setting（原子写 / 往返 / 损坏留档；WebGL 内存存储） ===");
        SettingChecks();

        Console.WriteLine("=== Logger（同日落盘 / 跨天切文件；WebGL 降级） ===");
        LoggerChecks();

        Console.WriteLine(_fail == 0 ? "\nALL PASS" : $"\n{_fail} CHECK(S) FAILED");
        Environment.Exit(_fail == 0 ? 0 : 1);
    }

    private static void JsonChecks()
    {
        // ① 循环引用：必须是「可控的异常」，不能是 StackOverflow（进程直接死）
        var self = new Dictionary<string, object>();
        self["self"] = self;
        var threw = false;
        string kind = "";
        try
        {
            MiniJson.Dump(self);
        }
        catch (Exception ex)
        {
            threw = true;
            kind = ex.GetType().Name;
        }
        Check("循环引用抛异常（不是栈溢出）", threw, kind);

        // ② 浮点保型：整数值的 double 往返回来仍是 double（旧实现会退化成 long）
        var dumped = MiniJson.Dump(1.0d);
        Check("Dump(1.0d) 输出带小数点", dumped == "1.0", dumped);
        Check("Parse(\"1.0\") 是 double", MiniJson.Parse("1.0") is double);
        Check("Parse(Dump(1.0d)) 仍是 double", MiniJson.Parse(dumped) is double, dumped);

        // ③ 整型保型：long 极值不被写成科学计数 / 丢精度
        var max = long.MaxValue;
        Check("long 极值往返保型", MiniJson.Parse(MiniJson.Dump(max)) is long v && v == max);

        // ④ 深度保护：超深嵌套报错而不是栈溢出
        var deep = "";
        for (var i = 0; i < 5000; i++) deep += "[";
        for (var i = 0; i < 5000; i++) deep += "]";
        var deepThrew = false;
        try
        {
            MiniJson.Parse(deep);
        }
        catch (Exception)
        {
            deepThrew = true;
        }
        Check("超深嵌套 parse 抛异常", deepThrew);
    }

    private static void EventChecks()
    {
        var bus = new EventBus();
        var log = new List<string>();
        Action w1 = () => log.Add("Net.*");
        Action w2 = () => log.Add("Net.**");
        Action exact = () => log.Add("exact");

        bus.On("Net.*", w1);
        bus.On("Net.**", w2);
        bus.On("Net.OnConnected", exact);

        log.Clear();
        bus.Emit("Net.OnConnected");
        Check("精确订阅先执行、通配随后", string.Join(",", log) == "exact,Net.*,Net.**", string.Join(",", log));

        log.Clear();
        bus.Emit("Net.A.B");
        Check("`Net.*` 只命中一段（不命中 Net.A.B）", string.Join(",", log) == "Net.**", string.Join(",", log));

        log.Clear();
        bus.Emit("Net.A");
        Check("两段名命中 `Net.*` 与 `Net.**`", string.Join(",", log) == "Net.*,Net.**", string.Join(",", log));

        // 退订语义：用注册时的同名（含通配符）Off 即可注销
        bus.Off("Net.*", w1);
        log.Clear();
        bus.Emit("Net.A");
        Check("Off(\"Net.*\") 注销干净", string.Join(",", log) == "Net.**", string.Join(",", log));

        // 带参通配 + Once
        var got = 0;
        bus.On<int>("Player.**", v => got += v);
        bus.Emit("Player.Gold.Change", 7);
        Check("带参通配收到载荷", got == 7, got.ToString());
    }

    /// <summary>日志文件被写线程持有 ⇒ 必须用 FileShare.ReadWrite 读，否则 IOException。</summary>
    private static string ReadAllTextShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs);
        return sr.ReadToEnd();
    }

    private static void LoggerChecks()
    {
        var dir = Path.Combine(Path.GetTempPath(), "clover-logger-assert-" + Guid.NewGuid().ToString("N"));

#if UNITY_WEBGL
        var w = new Logger(dir);
        w.Info("Assert", "webgl-line");
        Thread.Sleep(300);
        Check("WebGL: 不创建日志目录", !Directory.Exists(dir), dir);
        Check("WebGL: 不落盘任何文件", !Directory.Exists(dir) || Directory.GetFiles(dir).Length == 0);
        if (Directory.Exists(dir)) { try { Directory.Delete(dir, true); } catch { } }
#else
        using (var lg = new Logger(dir))
        {
            lg.Info("Assert", "line-1");
            lg.Info("Assert", "line-2");
            lg.Info("Assert", "line-3");

            // ① 同一天的日志也要落盘（不依赖"日期变化"唤醒写线程）
            var today = DateTime.Now.ToString("yyyy-MM-dd");
            var expect = Path.Combine(dir, today + ".log");
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (!File.Exists(expect) && DateTime.UtcNow < deadline) Thread.Sleep(50);
            Check("同一天日志落盘（不靠日期变化唤醒）", File.Exists(expect), expect);
            if (File.Exists(expect))
            {
                var text = ReadAllTextShared(expect);
                Check("落盘内容完整（3 条都在）",
                    text.Contains("line-1") && text.Contains("line-2") && text.Contains("line-3"));
            }

            // ② 跨天切文件：把私有的 RotateIfNeeded 用"另一天"调一次（旧实现会因日期被调用方提前改写而不切）
            var mi = typeof(Logger).GetMethod("RotateIfNeeded", BindingFlags.Instance | BindingFlags.NonPublic);
            Check("能取到 RotateIfNeeded（实现未改名）", mi != null);
            if (mi != null)
            {
                mi.Invoke(lg, new object[] { "2001-01-01" });
                var rotated = Path.Combine(dir, "2001-01-01.log");
                Check("跨天切出新文件 2001-01-01.log", File.Exists(rotated), rotated);
                // 日期由**写线程自己**取（Logger.cs:294）：强制切走后，下一条日志应被写线程
                // 按"当前日期"再切回当天文件 —— 这正是修复后的语义（旧实现永不切）。
                lg.Info("Assert", "after-rotate");
                Thread.Sleep(400);
                Check("写线程自行取日期 → 下一条落回当天文件", ReadAllTextShared(expect).Contains("after-rotate"));
            }

            // ③ 旧实现的畸形产物：目录里不该出现 `.log` 这种无名文件
            Check("无 `.log` 无名文件（旧缺陷形态）", !File.Exists(Path.Combine(dir, ".log")));
        }

        try { Directory.Delete(dir, true); } catch { }
#endif
    }

    private static void SettingChecks()
    {
        var dir = Path.Combine(Path.GetTempPath(), "clover-setting-assert-" + Guid.NewGuid().ToString("N"));
        var file = Path.Combine(dir, "settings.json");
        var tmp = file + ".tmp";

#if UNITY_WEBGL
        // WebGL：没有可写文件系统 ⇒ 只活在内存里，不创建目录、不落盘
        var w = new Setting(dir);
        w.Set("k", "v");
        w.Save();
        Check("WebGL: Save 后不创建目录", !Directory.Exists(dir), dir);
        Check("WebGL: Save 后不产生文件", !File.Exists(file), file);
        Check("WebGL: 内存内可读回", w.Get<string>("k") == "v", w.Get<string>("k"));
        var w2 = new Setting(dir);
        Check("WebGL: 新实例读回默认值（无盘可读）", w2.Get("k", "def") == "def", w2.Get("k", "def"));
#else
        var s = new Setting(dir);
        s.Set("k", "v");
        s.Save();
        Check("原子写: 目标文件存在", File.Exists(file), file);
        Check("原子写: 临时文件已被替换（无 .tmp 残留）", !File.Exists(tmp), tmp);
        Check("原子写: 内容可解析且含键值", File.ReadAllText(file).Contains("\"k\""), File.ReadAllText(file));

        var s2 = new Setting(dir);
        Check("往返: 新实例读回同一值", s2.Get<string>("k") == "v", s2.Get<string>("k"));

        // 损坏配置：不抛异常，回退默认值并留档 .corrupt（旧实现会把异常抛给 Game.Launch）
        File.WriteAllText(file, "{{{ not json");
        var s3 = new Setting(dir);
        Check("损坏配置: 不抛异常且回退默认值", s3.Get("k", "def") == "def", s3.Get("k", "def"));
        var archived = Directory.GetFiles(dir, "*.corrupt");
        Check("损坏配置: 已留档 .corrupt", archived.Length > 0, string.Join(",", archived));

        // 目录非法（含无效字符）：退化为内存存储，不抛异常
        var bad = new Setting("<>:|?*", "settings.json");
        bad.Set("x", 1);
        bad.Save();
        Check("非法目录: 不抛异常且内存内可读", bad.Get("x", 0) == 1, bad.Get("x", 0).ToString());

        try { Directory.Delete(dir, true); } catch { }
#endif
    }
}
