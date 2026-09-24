// ─────────────────────────────────────────────────────────────────────────────
// CloverEngine · Runtime/Core/ClientConfig.cs
// 「带默认值的配置段」的**加载链**：多来源回退 + 容错解析 + 热改 Reload。
//
// 定位：把「外部给的一段文本」按顺序变成「一个带默认值的配置对象」——
//   「按顺序试 N 个配置来源 → 任一来源读不出/解析不了就退到下一个 → 全坏就用内置默认值
//   → 改完文件能 Reload」这条链就在本件里。
//   字段类型（泛型 T）、具体来源（Unity 资源 / 磁盘文件 / 常量）、具体默认值全留调用方
//   （注入委托）；⛔ 本件不预设任何一组取值。
//
// ⛔ 与 `Setting`（`Runtime/Core/Setting.cs`）/ `FileSlotStore`（`Runtime/Data/FileSlotStore.cs`）
//    的分工（**别当第二套 Setting 用**，`结构规则.md` §3.2）：
//   · `Setting`          = **可写的单文件 KV 存储**（`Set`/`Get`/`Save`，引擎负责原子写盘 + 损坏留档）；
//   · `FileSlotStore`    = **一槽一文件**的文本存储（存档 / 回放 / 关卡草稿，可枚举）；
//   · 本件（本文件）     = **只读的配置来源链**：把「外部给的一段文本」按顺序变成「一个带默认值的
//     配置对象」。⛔ 本件**不写盘、不认识键、不做类型转换** —— "值从哪来"由调用方注入。
//   ⇒ 判据：要「存下来 / 改一改」用 `Setting`；要「读一份配置、坏了就回默认」用本件。
//
// 语义约束（改一条 = 语义漂移）：
//   ① **每项默认值只有一处**：默认值由调用方给的 `createDefault` 提供（通常是 `new T()`，字段
//      初始化器即唯一出处）。本件不做任何字段级默认值，⛔ 不在引擎里第二遍写 `0.7f` 之类。
//   ② **绝不抛异常**：来源读取抛异常 / 解析抛异常 ⇒ 记一条 Warn 后**跳到下一个来源**；全部来源
//      都不可用 ⇒ `Source` = `DefaultSourceName`、`Value` = `createDefault()`（+ 一条 Warn）。
//   ③ 来源返回 `null` / 空串 / 纯空白 = 「本来源没有内容」⇒ **静默跳过**（不是异常，不打日志）。
//   ④ 命中来源后过一遍可选的 `normalize`（业务在这里做逐字段兜底 / 裁剪 / 越界告警）；它抛异常
//      同样只 Warn 并改用下一个来源（一个来源的规范化失败 ⇒ 该来源整体不可用）。
//   ⑤ `Reload()` **重跑整条链**（换掉缓存值 + 更新 `Source` / `LoadCount`），供"改完配置不重启"用；
//      `Source` 在首次加载前是 `NotLoadedSourceName`，**且读取它不会触发加载**。
//   ⑥ 非线程安全：主线程使用（与 `Setting` / `FileSlotStore` 一致）。
//   ⑦ 本件不依赖 UnityEngine（只用 `System`）⇒ 离线自检宿主（非 Unity 进程）可直接链进工程跑。
//
// 用法：
//   var loader = new ConfigSectionLoader<MyRoot>("Cfg",
//       new[]
//       {
//           new ConfigSource("Resources/Configs/config", () => AssetText()),
//           new ConfigSource("文件:" + path,              () => ReadFile(path), path),
//       },
//       parse: (json, from) => Parse(json, from),   // 返回 null = 本来源解析失败（解析器自己留痕）
//       createDefault: () => new MyRoot(),
//       normalize: (root, from) => Clamp(root, from));
//   var cfg = loader.Value;      // 惰性：首次访问触发一次加载
//   loader.Reload();             // 热改（重跑整条链）
//   var log = loader.Source;     // "Resources/…" / "文件:…" / "默认值" / "(未加载)"
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;

namespace CloverEngine
{
    /// <summary>
    /// 一条配置来源：**显示名** + 读文本的委托（可选一个另作日志/解析标识的标签）。
    /// <para>
    /// 约定：<see cref="ReadText"/> 返回 <c>null</c> / 空串 / 纯空白 = **本来源没有内容**
    /// （引擎静默跳过，不算错误）；**抛异常** = 读取失败（引擎记 Warn 后改试下一个来源，⛔ 不向上抛）。
    /// </para>
    /// </summary>
    public sealed class ConfigSource
    {
        /// <summary>来源显示名（命中它时会成为 <see cref="ConfigSectionLoader{T}.Source"/> 的取值）。</summary>
        public string Name { get; }

        /// <summary>解析器 / 日志里用来标识本来源的文本（为空 ⇒ 用 <see cref="Name"/>）。</summary>
        public string LogLabel { get; }

        /// <summary>读取本来源文本（见类型注释的约定）。</summary>
        public Func<string> ReadText { get; }

        /// <summary>
        /// 建一条来源。
        /// </summary>
        /// <param name="name">显示名（进 <c>Source</c>，也是跳过日志里的标识）。</param>
        /// <param name="readText">读取委托（可为 null ⇒ 视为"没有内容"）。</param>
        /// <param name="logLabel">解析/日志标签（可为 null ⇒ 用 <paramref name="name"/>）。</param>
        public ConfigSource(string name, Func<string> readText, string logLabel = null)
        {
            Name = name;
            ReadText = readText;
            LogLabel = logLabel;
        }
    }

    /// <summary>
    /// 「带默认值的配置段」的加载链：按顺序试 <see cref="ConfigSource"/>，容错解析，
    /// 全失败回 <c>createDefault()</c>；<see cref="Reload"/> 支持热改。语义与边界见文件头。
    /// </summary>
    /// <typeparam name="T">配置段 / 配置根的类型（引用类型；字段默认值由它自己的初始化器给出）。</typeparam>
    public sealed class ConfigSectionLoader<T> where T : class
    {
        /// <summary>所有来源都不可用时的 <see cref="Source"/> 取值。</summary>
        public const string DefaultSourceName = "默认值";

        /// <summary>首次加载之前的 <see cref="Source"/> 取值（读取 <see cref="Source"/> 不触发加载）。</summary>
        public const string NotLoadedSourceName = "(未加载)";

        private readonly string _tag;
        private readonly List<ConfigSource> _sources;
        private readonly Func<string, string, T> _parse;
        private readonly Func<T> _createDefault;
        private readonly Action<T, string> _normalize;

        private bool _loaded;
        private T _value;

        /// <summary>当前命中的来源显示名 / <see cref="DefaultSourceName"/> / <see cref="NotLoadedSourceName"/>。</summary>
        public string Source { get; private set; }

        /// <summary>已执行过的加载次数（首次 <see cref="Value"/> 访问 1 次，每次 <see cref="Reload"/> +1）。</summary>
        public int LoadCount { get; private set; }

        /// <summary>是否已加载过（false ⇒ <see cref="Source"/> 还是 <see cref="NotLoadedSourceName"/>）。</summary>
        public bool Loaded => _loaded;

        /// <summary>
        /// 建一条加载链。
        /// </summary>
        /// <param name="tag">日志标签（业务侧惯例用自己的入口名，如 <c>"Cfg"</c>）。</param>
        /// <param name="sources">来源（**按顺序**试；null / 空 ⇒ 直接走默认值）。</param>
        /// <param name="parse">解析委托：(来源文本, 来源日志标签) → 配置对象；**返回 null = 本来源解析失败**
        /// （解析器自己负责留痕，引擎只补一条来源级跳过日志）。抛异常按"解析失败"处理。</param>
        /// <param name="createDefault">内置默认值的唯一出处（通常是 <c>() =&gt; new T()</c>）。</param>
        /// <param name="normalize">命中来源后的可选规范化钩子：`(配置对象, 来源日志标签) => …`
        /// （逐字段兜底 / 裁剪 / 越界告警；第二参就是 <see cref="ConfigSource.LogLabel"/>，便于点名是哪份配置越界）。</param>
        public ConfigSectionLoader(string tag, IEnumerable<ConfigSource> sources,
            Func<string, string, T> parse, Func<T> createDefault, Action<T, string> normalize = null)
        {
            _tag = string.IsNullOrEmpty(tag) ? "Config" : tag;
            _sources = new List<ConfigSource>();
            if (sources != null)
            {
                foreach (var s in sources)
                {
                    if (s != null) _sources.Add(s);
                }
            }

            _parse = parse;
            _createDefault = createDefault;
            _normalize = normalize;
            Source = NotLoadedSourceName;
        }

        /// <summary>配置对象（**惰性**：首次访问触发一次加载；之后返回缓存值，直到 <see cref="Reload"/>）。</summary>
        public T Value
        {
            get
            {
                if (!_loaded) Load();
                return _value;
            }
        }

        /// <summary>
        /// 重跑整条来源链（热改）。等价于「丢掉缓存再 <see cref="Load"/> 一次」，返回新值。
        /// </summary>
        public T Reload()
        {
            Load();
            return _value;
        }

        /// <summary>
        /// 执行一次加载：按顺序试来源 → 命中即返回；全不可用 ⇒ 默认值 + Warn。**不抛异常**（见文件头 ②）。
        /// <para>调用方通常只用 <see cref="Value"/>（惰性）与 <see cref="Reload"/>；直接调本方法只多一层显式。</para>
        /// </summary>
        public T Load()
        {
            LoadCount++;

            for (var i = 0; i < _sources.Count; i++)
            {
                var source = _sources[i];

                string text;
                try
                {
                    text = source.ReadText != null ? source.ReadText() : null;
                }
                catch (Exception e)
                {
                    // 非预期分支（权限 / 资源后端未就绪 / 平台不支持）：留痕后改试下一个来源
                    Warn($"来源 '{source.Name}' 读取异常：{e.GetType().Name}: {e.Message}（改试下一个来源）");
                    continue;
                }

                // ③ 没有内容：静默跳过（不是异常）
                if (string.IsNullOrWhiteSpace(text)) continue;

                var from = string.IsNullOrEmpty(source.LogLabel) ? source.Name : source.LogLabel;

                T parsed;
                try
                {
                    parsed = _parse != null ? _parse(text, from) : null;
                }
                catch (Exception e)
                {
                    Warn($"来源 '{source.Name}' 解析异常：{e.GetType().Name}: {e.Message}（改试下一个来源）");
                    continue;
                }

                if (parsed == null)
                {
                    // 解析器返回 null = 本来源的文本不可用（它自己已留痕）⇒ 改试下一个来源
                    Warn($"来源 '{source.Name}' 解析失败（解析器返回 null）（改试下一个来源）");
                    continue;
                }

                if (_normalize != null)
                {
                    try
                    {
                        _normalize(parsed, from);
                    }
                    catch (Exception e)
                    {
                        // 规范化失败 ⇒ 该来源整体不可用（拿它继续跑等于把坏值交给业务）
                        Warn($"来源 '{source.Name}' 规范化异常：{e.GetType().Name}: {e.Message}（改试下一个来源）");
                        continue;
                    }
                }

                _value = parsed;
                _loaded = true;
                Source = source.Name;
                return _value;
            }

            Warn($"所有来源均不可用（共 {_sources.Count} 个），使用内置默认值");
            _value = _createDefault != null ? _createDefault() : null;
            _loaded = true;
            Source = DefaultSourceName;
            return _value;
        }

        /// <summary>
        /// 上报通道：引擎惯例（`Setting` / `FileSlotStore` 同款）—— 走 <c>Game.Logger</c>；
        /// 它为 null（引擎尚未构造）时静默丢弃，⛔ 不回落 `UnityEngine.Debug`（Core 不依赖 Unity）。
        /// </summary>
        private void Warn(string msg)
        {
            Game.Logger?.Warn(_tag, msg);
        }
    }
}
