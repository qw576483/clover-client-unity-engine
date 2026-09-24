// ─────────────────────────────────────────────────────────────────────────────
// CloverEngine · Runtime/Presentation/SnapshotInterpolator.cs
// 「低频权威快照 → 高帧率插值表现」的**渲染时钟 + 插值窗口选择**件（无 MonoBehaviour，由业务 Tick 驱动）。
//
// 来源：clover-project-cr `client/Assets/Scripts/View/BattleViewRoot.cs`
//   · 类注释三（`:32-76`）—— 做法与**三代失败模式**；
//   · 常量与自检面（`:349-604`）—— `HistSlots` / `RenderLagIntervals` / `SteerRate` / 各行自检量；
//   · 实现（`:1622-1842`）—— `RenderClockMs` / `ServerNowMs` / `SelectWindow` / `SetClockRate` /
//     `TickRender`；快照入历史在 `:908-996`（`OnSnapshot`）。
//   该工程服务端 10 Hz、客户端 ~30~60 FPS；本件把"哪几个数量"变成参数（见下方 ⚙ 参数表）。
//
// ═══════════════ 三代失败模式（★ 本件最值钱的部分，三代都要留在注释里） ═══════════════
//
// **第一代：分母写死 + 每帧重置** ⇒ **前跳**。
//   分母写死成一个常量间隔，且**每收到一帧快照就把"当前帧到达时刻"重置** ⇒ 插值比例 `t` 每收一帧
//   从 0 重来一次。快照间隔只要短于那个常量，上一次已经插值到 t=0.6 的位置就被整段丢弃、直接跳到新起点
//   ⇒ **每收一帧往前跳一次**（用户报的"模型抖动的厉害"）。
//
// **第二代：到达驱动换窗口** ⇒ **抖动**（时钟按真实时间走、分母也用真实间隔，这两条已经对了，但……）。
//   换窗口仍然绑在"**包到达**"上：每收一帧就把 `(prev, cur)` 换成最新的一对。于是"换窗口那一刻"由
//   **网络到达时刻**决定，而时钟是按真实时间走的 ⇒ 换的瞬间时钟离窗口末端还差 0~1 帧，
//   窗口末端那一小段位移被**塞进换窗口的那一帧**交付。实测：`t` 最高只到 0.707（= 窗口从来没走完）、
//   单帧速度在 0.78~1.05 之间跳（**cv 0.24**、峰值比 1.23）—— 人眼看到的就是 10 Hz 的"哆嗦"。
//   离线仿真把这条路钉死：关掉速率微调（速率恒 1）但保留"到达驱动换窗口" ⇒ 速度 cv 反而升到 0.37~0.47；
//   **改成按时钟选窗口 ⇒ 速度 cv 0.000、峰值比 1.000、零位移帧 0**。
//
// **本代（本件）：按时钟选窗口 + 有界比例速率修正**。
//   四步（全部在 `Tick` / `RenderClockMs` / `SelectWindow` 里）：
//   <list type="number">
//   <item>渲染时钟 = <c>对齐点 + (真实时间 - 对齐时刻) × 1000 × 速率</c> ——
//     **只按真实时间前进，收到快照时绝不重置**。它渲染的是"服务端时间轴上的哪一毫秒"。
//     ⛔ 不写"每帧累加 `Time.deltaTime`"：那个值被 Unity 夹在 `Time.maximumDeltaTime`（默认 1/3 秒），
//     一次卡顿就让时钟**永久落后**（实测 1915~2108 ms），于是 `t` 恒为 0、单位冻住不动。</item>
//   <item>速率 = <b>有界比例修正</b>（<c>SteerRate</c>）：偏差 ≤ 追帧阈值时在 ±
//     <see cref="SnapshotInterpolatorOptions.LagSteerMaxRate"/> 内**按比例**微调（稳态 ±5%），
//     偏差超过阈值时放开到 <see cref="SnapshotInterpolatorOptions.CatchUpMaxRate"/> 做**有界追帧**。
//     <b>为什么必须常开</b>：位置的导数就是速度 ⇒ 校正**绝不能"瞬跳时钟"**（那会让单位前跳一大截）；
//     但也**不能完全不校正** —— 落后量一旦涨到超过快照历史的覆盖范围，`SelectWindow` 只能夹到最旧一对、
//     `t` 恒为 0，**插值静默失效**（实测 7953 帧里 `t` 只有 3 帧取到中间值，其余非 0 即 1，
//     单位实际是每 100 ms 跳一格）。5% 的速率调制只在那几秒存在（偏差衰减到 0 后速率自动回 1），
//     换来的是插值永不失活。</item>
//   <item>**按渲染时钟**从 <see cref="SnapshotInterpolatorOptions.HistSlots"/> 格快照历史里选
//     "夹住时钟的那一对"（`SelectWindow`），渲染落后**最新快照** <see cref="SnapshotInterpolatorOptions.RenderLagIntervals"/>
//     个间隔：手里始终多握一格快照 ⇒ 到达时刻抖 ±半个间隔也**推不动**正在渲染的窗口；
//     换窗口的时刻由时钟跨过时间戳决定（**不是包到达**）⇒ 边界只在 `t=1` 那一刻跨过，
//     而上一段的 `t=1` 与下一段的 `t=0` 是**同一个坐标** ⇒ 位置连续、速度恒定。</item>
//   <item>比例 <c>t = Clamp01((renderMs - prevMs) / max(1, curMs - prevMs))</c>，位置
//     <c>= Lerp(prevPos, curPos, t)</c>。分母是**两个快照自己的时间戳之差**（真实间隔），
//     ⛔ 不是编译期常量。时钟跑到最新快照之后 ⇒ `t` 夹到 1（**冻在最后一个已知位置，不外推**）。</item>
//   </list>
//   <b>代价（明知）</b>：画面比服务端晚 <see cref="SnapshotInterpolatorOptions.RenderLagIntervals"/> 个间隔。
//   这是"**绝不前跳**"换来的代价，刻意如此：前跳会瞬间把单位推过头（甚至穿过墙）。
//   允许多付的只是**秒级的 ±5% 速率微调**（把落后量自己走回目标），不是位置瞬跳。
//
// **另外两条只夹取、不追帧就会踩的坑（都在 `Tick` 里）**：
//   · **时钟超前上限**（`ClockMaxLeadIntervals`）：快照**停推**（对局结束 / 断线 / 服务端不再发）后
//     "服务端现在"会一路外推、时钟跟着跑飞 —— 实测超前最新快照 **50.8 s**（12902 行里 7891 行
//     `t` 被夹成 1.0）；用 ±5% 的速率把 50 s 拉回来要上千秒 ⇒ 那段时间单位全部冻在最后一帧。
//     ⇒ "没有新数据就不许发明时间"：把时钟夹在 `最新 + ClockMaxLeadIntervals 个间隔`，
//     快照一恢复就立刻松开（不需要靠速率追）。
//   · **"服务端现在"的外推封顶**（`ServerNowExtrapCapIntervals`）：同一个停推场景下，
//     若外推不封顶，`服务端现在` 会一直往前走。封顶 = 2 个间隔（正常的丢包抖动必须能被吸收，
//     ≥2 个间隔的沉默已经不是抖动、是"停推"）。
//
// ═══════════════ ⚙ 参数表（⛔ 全部必须由调用方给；不许把 10Hz / 60FPS 写死在算法里） ═══════════════
//   SnapshotIntervalMs      权威快照的标称间隔（10 Hz ⇒ 100）。**换帧率必须改这个**。
//   ExpectedRenderFps       渲染帧率（默认 60）。只用于**诊断**（见 ctor 的检查），不参与时钟推进。
//   HistSlots               快照历史槽数（默认 6 = 覆盖 5 个间隔）。**必须 > 1 + RenderLagIntervals**，
//                           否则时钟一旦落后就直接掉出历史 ⇒ `t` 恒为 0（插值静默失效）。
//   RenderLagIntervals      渲染落后最新快照几个间隔（= 抖动缓冲深度，默认 2）。
//   LagSteerGain            稳态速率控制的增益（默认 0.05 ⇒ 偏差恰好一个目标量时刚好到满偏）。
//   LagSteerMaxRate         稳态速率允许的偏离（默认 ±0.05）。
//   CatchUpMaxRate          追帧时速率最多跑到多快（默认 1 ⇒ 最高 2× 真实时间）。
//   CatchUpThresholdIntervals 追帧阈值（几个间隔，默认 3）—— 它**只是"放开档位"的界线**，
//                           ⛔ 不是"要不要校正"的开关（300 ms 以内同样在按比例校正）。
//   ServerNowExtrapCapIntervals / ClockMaxLeadIntervals  见上方两条坑（默认 2 / 1）。
//
// ═══════════════ 与既有引擎件的边界 ═══════════════
//   · **不是 `WorldSync` 的重复**：`WorldSync` 是 MMO/AOI 的**逐实体**镜像同步（服务器实体事件 →
//     本地 Entity 增删改 + 每实体插值/外推平滑），它回答"这个实体该怎么平滑到下一格"；
//     本件回答"**这一帧我们该渲染服务端时间轴上的哪一毫秒、该插值哪两个快照之间**"，
//     是**时间轴 / 窗口**层的件，与实体数量、实体类型无关（本件对快照内容完全无知，
//     只持有调用方给的 `payload` 引用）⇒ 两者可叠加：`WorldSync` 管实体，本件管时间。
//   · **不是 `FramePacing` 的重复**：`FramePacingPolicy` 管"要不要开 vSync / 帧率上限"（帧交付的
//     节奏），本件管"这一帧画面该取哪一时刻的状态"（时间轴的推进）。帧节奏不匀会**放大**本件
//     要解决的问题（每帧推进量 = v·dt），但两者职责不重叠。
//   · **不含实体上限 / 不含渲染**：`MaxEntities` 这类上限、以及"把插值结果写到 Transform /
//     SpriteRenderer"都是调用方的事（本件不依赖任何渲染类型，只依赖 `UnityEngine.Time` 作默认时钟）。
//   · 落点 = `Runtime/Presentation/`（表现域）：本件只做"表现侧的时间轴推进"，不跑权威数值、
//     不订阅网络、不改数据（`结构规则.md` §3.5）。只依赖 `Core`（`LogThrottle` / `Game.Logger`）
//     ⇒ 依赖方向合规。默认时钟用 `Time.realtimeSinceStartup`（表现域可用）。
//
// ═══════════════ 时钟注入（★ 便于离线单测） ═══════════════
//   构造时可注入 `Func<float> clockSeconds`（**返回秒**，语义同 `Time.realtimeSinceStartup`）。
//   ⛔ 本件**每帧只采样一次**该时钟，并用同一个样本算 `渲染时钟` 与 `服务端现在` ——
//   参考实现里 `RenderClockMs` / `ServerNowMs` 是两个会各自再读一次真实时间的属性，
//   换成注入时钟后那样写会拿到不同样本（不可复现）⇒ 本件统一成 `...At(real)` 形式（语义等价、可复现）。
//   ⚠️ **真暂停**的项目（`Time.timeScale = 0`）要自己决定时钟语义：默认用 realtime ⇒ 暂停时时钟照走
//   （参考工程的对战不真暂停，故它是安全的）；要"暂停就冻住"，注入一个只在非暂停时推进的时钟即可。
// ─────────────────────────────────────────────────────────────────────────────

using System;
using UnityEngine;

namespace CloverEngine
{
    /// <summary>
    /// 快照插值器的可调参数（<b>结构体 + 公开字段</b>，与 <c>ViewBobConfig</c> 同形）。默认值见
    /// <see cref="Default"/>（= 参考工程实测收敛的那一组：10 Hz 快照 / 6 格历史 / 落后 2 个间隔 / ±5%）。
    /// <para>⛔ 换快照频率或渲染帧率时**必须**改 <see cref="SnapshotIntervalMs"/> 与
    /// <see cref="ExpectedRenderFps"/>，别指望默认值对任何节奏都好。</para>
    /// </summary>
    public struct SnapshotInterpolatorOptions
    {
        /// <summary>权威快照的标称间隔（毫秒）。10 Hz ⇒ <c>100</c>。</summary>
        public float SnapshotIntervalMs;

        /// <summary>渲染帧率（只用于**诊断**：判"快照是否比渲染帧还密"，不参与时钟推进）。</summary>
        public float ExpectedRenderFps;

        /// <summary>
        /// 快照历史槽数（最旧 … 最新）。历史覆盖 <c>HistSlots - 1</c> 个间隔。
        /// <para>⛔ 它**不是**可有可无的冗余，而是"插值能不能活着"的硬边界：<c>SelectWindow</c> 要求时钟
        /// 落在历史区间内；时钟一旦跑到最旧那一格之前，就只能夹到最旧的一对 ⇒ <c>t</c> 恒为 0
        /// （= 位置按快照周期阶梯跳、插值静默失效）。实测：`最新 - 渲染时钟` 稳定在 **4 个间隔**
        /// 而当时历史只覆盖 3 个间隔 ⇒ 时钟整段跑在窗口之外（7953 帧里 <c>t</c> 只有 3 帧取到中间值）。</para>
        /// </summary>
        public int HistSlots;

        /// <summary>
        /// 渲染落后"最新快照"几个间隔（= 抖动缓冲深度）。取 2：渲染的那一对是 <c>[s(k-2), s(k-1)]</c>、
        /// 而手里已经握着 <c>s(k)</c> ⇒ 窗口末端离最新快照还有**整整一个间隔**的余量。
        /// 取 1 时窗口末端就是最新快照 ⇒ 每收一帧就换一对、换的那一刻时钟离窗口末端还差 0~1 帧
        /// ⇒ 那一帧要交付"剩下的任意份额" ⇒ 实测速度 cv 0.24~0.28。
        /// </summary>
        public float RenderLagIntervals;

        /// <summary>稳态速率控制的增益（<c>rate = 1 + (落后量 - 目标) / 目标 × Gain</c>）。</summary>
        public float LagSteerGain;

        /// <summary>稳态速率允许的偏离（±）。人眼对"整体快/慢 5%"没有感觉，但足以把时钟平滑拉回目标。</summary>
        public float LagSteerMaxRate;

        /// <summary>追帧时时钟最多跑多快（1 = 最高 2× 真实时间）。</summary>
        public float CatchUpMaxRate;

        /// <summary>追帧阈值（几个间隔）：只是一条"放开档位"的界线，⛔ 不是"要不要校正"的开关。</summary>
        public float CatchUpThresholdIntervals;

        /// <summary>"服务端现在"允许比最新快照最多超前几个间隔（停推时不许发明时间）。</summary>
        public float ServerNowExtrapCapIntervals;

        /// <summary>渲染时钟允许比最新快照最多超前几个间隔。</summary>
        public float ClockMaxLeadIntervals;

        /// <summary>目标落后量（毫秒）= <see cref="RenderLagIntervals"/> × <see cref="SnapshotIntervalMs"/>。</summary>
        public float TargetLagMs { get { return RenderLagIntervals * SnapshotIntervalMs; } }

        /// <summary>追帧阈值（毫秒）。</summary>
        public float CatchUpThresholdMs { get { return CatchUpThresholdIntervals * SnapshotIntervalMs; } }

        /// <summary>"服务端现在"的外推上限（毫秒）。</summary>
        public float ServerNowExtrapCapMs { get { return ServerNowExtrapCapIntervals * SnapshotIntervalMs; } }

        /// <summary>渲染时钟的超前上限（毫秒）。</summary>
        public float ClockMaxLeadMs { get { return ClockMaxLeadIntervals * SnapshotIntervalMs; } }

        /// <summary>历史覆盖时长（毫秒）= <c>(HistSlots - 1)</c> × <see cref="SnapshotIntervalMs"/>。</summary>
        public float HistoryCoverageMs { get { return Mathf.Max(0, HistSlots - 1) * SnapshotIntervalMs; } }

        /// <summary>参考工程实测收敛的那一组默认值（10 Hz 快照 / 6 格历史 / 落后 2 个间隔 / ±5% 速率）。</summary>
        public static SnapshotInterpolatorOptions Default()
        {
            return new SnapshotInterpolatorOptions
            {
                SnapshotIntervalMs = 100f,            // 10 Hz
                ExpectedRenderFps = 60f,
                HistSlots = 6,                        // 覆盖 5 个间隔，比目标落后量多 3 个间隔的余量
                RenderLagIntervals = 2f,
                LagSteerGain = 0.05f,                 // = LagSteerMaxRate ⇒ 偏差一个目标量时刚好满偏
                LagSteerMaxRate = 0.05f,
                CatchUpMaxRate = 1f,
                CatchUpThresholdIntervals = 3f,
                ServerNowExtrapCapIntervals = 2f,
                ClockMaxLeadIntervals = 1f,
            };
        }
    }

    /// <summary>
    /// 快照插值器：把低频权威快照的**时间轴**推进到渲染时刻，并给出"该插值哪一对快照、比例是多少"。
    /// <para>
    /// <b>用法</b>（三步，全部由业务在自己的 Tick 里驱动）：
    /// <code>
    /// var interp = new SnapshotInterpolator(SnapshotInterpolatorOptions.Default());
    /// // 收到快照（引擎不替你订阅；本件对快照内容一无所知）：
    /// interp.Push(snapshot.server_ms, snapshot);        // payload 由调用方自定（可传 null）
    /// // 每帧渲染：
    /// var t = interp.Tick();                            // 0..1（未就绪时返回 1）
    /// if (interp.HasWindow)
    /// {
    ///     var prev = (MySnapshot)interp.WindowStartPayload;
    ///     var cur  = (MySnapshot)interp.WindowEndPayload;
    ///     // 用 prev / cur / t 自己 Lerp 你的实体字段（位置 / 血量 / 朝向…）
    /// }
    /// </code>
    /// </para>
    /// <para>
    /// <b>为什么给 `payload` 而不是内建实体容器</b>：本件只管**时间**。调用方的快照类型（有什么字段、
    /// 几张表、怎么插值）是业务自由，本件不替它建容器、也不做拷贝（`payload` 是**引用**，
    /// 与参考实现"入历史只存引用、每帧多一次分配都不要"的口径一致）。
    /// </para>
    /// <para><b>主线程使用</b>（与 <see cref="LogThrottle"/> 一致，非线程安全）。</para>
    /// </summary>
    public sealed class SnapshotInterpolator
    {
        /// <summary>日志 tag。</summary>
        private const string Tag = "SnapshotInterpolator";

        private readonly SnapshotInterpolatorOptions _options;

        /// <summary>注入的时钟（秒）；<c>null</c> ⇒ <c>Time.realtimeSinceStartup</c>。</summary>
        private readonly Func<float> _clock;

        /// <summary>本实例的日志键前缀（多实例时「只报一次」互不干扰）。</summary>
        private readonly string _logKey;

        // ── 快照历史（环形，按服务端时间戳单调） ──────────────────────────────

        /// <summary>各历史槽的服务端时间戳（毫秒），下标 0 最旧、<c>HistSlots-1</c> 最新。</summary>
        private readonly float[] _msHist;

        /// <summary>各历史槽的调用方载荷，与 <see cref="_msHist"/> 一一对应（本件只存引用，不读内容）。</summary>
        private readonly object[] _payloadHist;

        /// <summary>已填充的历史槽数（≤ <c>HistSlots</c>；首帧为 1）。</summary>
        private int _histLen;

        // ── 时间轴（全部以服务端时间戳为单位，毫秒） ──────────────────────────

        /// <summary>**被渲染的那一对**的起始时间戳。</summary>
        private float _prevMs;

        /// <summary>**被渲染的那一对**的结束时间戳。</summary>
        private float _currMs;

        /// <summary>**被渲染的那一对**的载荷（由 <see cref="SelectWindow"/> 按渲染时钟选出）。</summary>
        private object _prevPayload;
        private object _curPayload;

        /// <summary>**最新收到的快照**的时间戳。渲染落后它是 1~2 个间隔（抖动缓冲）。</summary>
        private float _newestMs;

        /// <summary>渲染时钟（毫秒）：**它渲染的是"服务端时间轴上的哪一毫秒"**。<c>NaN</c> = 还没对齐过。</summary>
        private float _renderMs = float.NaN;

        /// <summary>时钟对齐点：该真实时刻（<see cref="_clockBaseReal"/>，秒）对应的服务端时间轴毫秒值。</summary>
        private float _clockBaseMs;

        /// <summary>时钟对齐点：注入时钟的读数（秒）。</summary>
        private float _clockBaseReal;

        /// <summary>**最新快照的到达时刻**（秒）—— 把阶梯状的时间戳外推成连续时间轴。</summary>
        private float _arrivalReal;

        /// <summary>时钟相对真实时间的**速率**（常态 1；只在"落后太多"时被抬高做平滑追帧）。</summary>
        private float _clockRate = 1f;

        private bool _hasPrev;
        private int _snapshotCount;
        private bool _catchingUp;
        private bool _clockLeadCapped;
        private int _droppedOutOfOrder;

        /// <param name="options">
        /// 参数；传 <c>null</c> 取 <see cref="SnapshotInterpolatorOptions.Default"/>。
        /// 非正 / 过小的值会被**归一化**（并留一条 Warn，⛔ 不静默、也⛔ 不抛）。
        /// </param>
        /// <param name="clockSeconds">
        /// 时钟注入点：**返回秒**，语义同 <c>Time.realtimeSinceStartup</c>（单调增、与 `timeScale` 无关）。
        /// 传 <c>null</c> 用引擎默认。离线单测 / 回放灌数据时注入它即可完全复现。
        /// </param>
        public SnapshotInterpolator(SnapshotInterpolatorOptions? options = null, Func<float> clockSeconds = null)
        {
            _logKey = Tag + "[" + GetHashCode() + "]";
            _clock = clockSeconds ?? DefaultClock;

            var o = options ?? SnapshotInterpolatorOptions.Default();
            var d = SnapshotInterpolatorOptions.Default();
            var fixes = string.Empty;

            if (!(o.SnapshotIntervalMs > 0.01f))
            {
                o.SnapshotIntervalMs = d.SnapshotIntervalMs;
                fixes += "SnapshotIntervalMs ";
            }
            if (!(o.ExpectedRenderFps > 0.01f))
            {
                o.ExpectedRenderFps = d.ExpectedRenderFps;
                fixes += "ExpectedRenderFps ";
            }
            if (o.HistSlots < 3)
            {
                o.HistSlots = d.HistSlots;
                fixes += "HistSlots ";
            }
            if (!(o.RenderLagIntervals >= 1f))
            {
                o.RenderLagIntervals = d.RenderLagIntervals;
                fixes += "RenderLagIntervals ";
            }
            if (!(o.LagSteerGain > 0f)) { o.LagSteerGain = d.LagSteerGain; fixes += "LagSteerGain "; }
            if (!(o.LagSteerMaxRate > 0f)) { o.LagSteerMaxRate = d.LagSteerMaxRate; fixes += "LagSteerMaxRate "; }
            if (!(o.CatchUpMaxRate > 0f)) { o.CatchUpMaxRate = d.CatchUpMaxRate; fixes += "CatchUpMaxRate "; }
            if (!(o.CatchUpThresholdIntervals > 0f))
            {
                o.CatchUpThresholdIntervals = d.CatchUpThresholdIntervals;
                fixes += "CatchUpThresholdIntervals ";
            }
            if (!(o.ServerNowExtrapCapIntervals > 0f))
            {
                o.ServerNowExtrapCapIntervals = d.ServerNowExtrapCapIntervals;
                fixes += "ServerNowExtrapCapIntervals ";
            }
            if (!(o.ClockMaxLeadIntervals > 0f))
            {
                o.ClockMaxLeadIntervals = d.ClockMaxLeadIntervals;
                fixes += "ClockMaxLeadIntervals ";
            }

            _options = o;
            _msHist = new float[o.HistSlots];
            _payloadHist = new object[o.HistSlots];

            if (fixes.Length > 0)
            {
                LogThrottle.WarnOnce(Tag, _logKey + "/options.normalized",
                    "快照插值参数非法（非正 / 过小）⇒ 已按默认值归一：" + fixes.Trim() +
                    "（检查调用方传进来的那组参数；⛔ 默认值是按 10 Hz 快照调的）");
            }

            // 诊断①：历史覆盖必须留出缓冲余量，否则时钟一落后就掉出历史 ⇒ t 恒为 0（插值静默失效）。
            if (o.HistoryCoverageMs <= o.TargetLagMs)
            {
                LogThrottle.WarnOnce(Tag, _logKey + "/options.coverage",
                    "快照历史覆盖（" + o.HistoryCoverageMs.ToString("F0") + "ms = " +
                    (o.HistSlots - 1) + " 个间隔）不大于目标落后量（" + o.TargetLagMs.ToString("F0") +
                    "ms）⇒ 时钟一旦有偏差就会掉出历史、插值被夹成阶梯（t≡0/1）；" +
                    "把 HistSlots 加到大于 1 + RenderLagIntervals 并留几个间隔余量");
            }

            // 诊断②：快照比渲染帧还密 ⇒ 一个间隔内没有渲染帧，插值看不出效果（是参数配错，不是算法问题）。
            if (o.ExpectedRenderFps > 0.01f && o.SnapshotIntervalMs < 1000f / o.ExpectedRenderFps)
            {
                LogThrottle.WarnOnce(Tag, _logKey + "/options.density",
                    "快照间隔（" + o.SnapshotIntervalMs.ToString("F0") + "ms）比渲染帧间隔（" +
                    (1000f / o.ExpectedRenderFps).ToString("F1") + "ms @ " + o.ExpectedRenderFps.ToString("F0") +
                    "FPS）还短 ⇒ 单帧交付里插值无意义；检查 SnapshotIntervalMs / ExpectedRenderFps 是否配错");
            }

            Reset();
        }

        /// <summary>本件的参数（只读，归一化之后的那一组）。</summary>
        public SnapshotInterpolatorOptions Options { get { return _options; } }

        // ── 自检面（判据用；语义与参考工程逐条对齐） ─────────────────────────

        /// <summary>已收到的快照数（含被丢弃的乱序帧？**不含** —— 只数入历史的那些）。</summary>
        public int SnapshotCount { get { return _snapshotCount; } }

        /// <summary>已填充的历史槽数（0 .. <c>HistSlots</c>）。</summary>
        public int HistoryLength { get { return _histLen; } }

        /// <summary>是否已经有一对可插值的窗口（<c>history &gt;= 2</c> 且已对齐过时钟）。</summary>
        public bool HasWindow { get { return _hasPrev && !float.IsNaN(_renderMs); } }

        /// <summary>渲染时钟当前值（毫秒）；<c>NaN</c> = 还没对齐过（首个快照到达时对齐一次）。</summary>
        public float RenderClockMs { get { return _renderMs; } }

        /// <summary>**上一次 <see cref="Tick"/> 算出的插值比例**（`NaN` = 还没就绪）。</summary>
        public float InterpRatio { get { return _lastT; } }

        /// <summary>
        /// 渲染落后**正在渲染的那一对的末端**多少毫秒（<c>currMs - renderMs</c>）。
        /// 判据：正常运行应在 <c>[0, 一个快照间隔]</c> 内波动，且**永远 &gt;= 0**
        /// （负值 = 渲染跑到服务端前面 = 外推，本件刻意不允许）。
        /// </summary>
        public float RenderLagMs { get { return _currMs - _renderMs; } }

        /// <summary>
        /// 渲染落后的**总**落后量 = 最新快照时间戳 - 渲染时钟。稳态应落在
        /// <c>[一个间隔, RenderLagIntervals 个间隔]</c> —— 多出来的那一个间隔就是**抖动缓冲**。
        /// </summary>
        public float BufferLagMs { get { return _newestMs - _renderMs; } }

        /// <summary>**正在渲染的那一对**的起始时间戳（毫秒；0 = 还没有一对）。</summary>
        public float WindowStartMs { get { return _prevMs; } }

        /// <summary>**正在渲染的那一对**的结束时间戳（毫秒；两者之差应 ≡ 服务端间隔）。</summary>
        public float WindowEndMs { get { return _currMs; } }

        /// <summary>正在渲染的那一对的起始载荷（由调用方在 <see cref="Push"/> 时给的引用）。</summary>
        public object WindowStartPayload { get { return _prevPayload; } }

        /// <summary>正在渲染的那一对的结束载荷。</summary>
        public object WindowEndPayload { get { return _curPayload; } }

        /// <summary>最新收到的快照的时间戳（毫秒）。</summary>
        public float NewestSnapshotMs { get { return _newestMs; } }

        /// <summary>渲染时钟当前速率：常态 <c>1.0</c>，追帧中 &gt; 1.0（≤ <c>1 + CatchUpMaxRate</c>）。</summary>
        public float ClockRate { get { return _clockRate; } }

        /// <summary>是否正在追帧（灾难级偏差）。</summary>
        public bool CatchingUp { get { return _catchingUp; } }

        /// <summary>渲染时钟当前是否**掉出了**快照历史（= 插值已被夹成阶梯，属静默失效，已留痕）。</summary>
        public bool OutOfWindow { get { return _outOfWindow; } }

        /// <summary>被丢弃的帧数（时间戳没前进 / 乱序 ⇒ 整帧丢弃、不入历史）。</summary>
        public int DroppedOutOfOrderCount { get { return _droppedOutOfOrder; } }

        private bool _outOfWindow;
        private float _lastT = float.NaN;

        // ── 时钟 ─────────────────────────────────────────────────────────────

        private static float DefaultClock() { return Time.realtimeSinceStartup; }

        /// <summary>渲染时钟在真实时刻 <paramref name="real"/> 的取值（毫秒）。</summary>
        private float RenderClockMsAt(float real)
        {
            return _clockBaseMs + (real - _clockBaseReal) * 1000f * _clockRate;
        }

        /// <summary>
        /// **连续**的"服务端现在"（毫秒）：把最新快照的时间戳按"它到达后过了多少真实时间"外推，
        /// 但外推量**有上限**（<see cref="SnapshotInterpolatorOptions.ServerNowExtrapCapMs"/>）。
        /// <para>用途只剩"灾难级偏差的恢复判据"：时钟的**稳态推进不引用它**，于是到达时刻的抖动
        /// （网络抖动 / 编辑器卡顿）不会通过它调制渲染速度 —— 那正是"速度脉动"的传播路径。</para>
        /// </summary>
        private float ServerNowMsAt(float real)
        {
            var extrap = (real - _arrivalReal) * 1000f;
            return _newestMs + Mathf.Min(extrap, _options.ServerNowExtrapCapMs);
        }

        /// <summary>改时钟速率并**重新锚定**对齐点（保证改速率那一刻时钟值连续 —— 不产生跳变）。</summary>
        private void SetClockRate(float rate, float realNow)
        {
            _clockBaseMs = RenderClockMsAt(realNow);
            _clockBaseReal = realNow;
            _clockRate = rate;
        }

        // ── 速率控制律（纯函数，便于离线断言） ───────────────────────────────

        /// <summary>
        /// 速率控制律（**纯函数**，离线可断言）：输入"时钟相对目标的偏差"（<c>目标值 - 时钟现值</c>，
        /// 正 = 时钟落后了要加速），输出时钟该跑多快。
        /// <list type="bullet">
        /// <item>偏差 ≤ <see cref="SnapshotInterpolatorOptions.CatchUpThresholdMs"/>：
        ///   在 ±<see cref="SnapshotInterpolatorOptions.LagSteerMaxRate"/> 之内**按比例**微调（人眼无感），
        ///   偏差越小修正越小、归零则速率回 1；</item>
        /// <item>偏差超过阈值：放开到 <see cref="SnapshotInterpolatorOptions.CatchUpMaxRate"/>
        ///   （落后太多时追赶、超前时放慢 —— 超前时只放一半，避免把时钟拉过头）。</item>
        /// </list>
        /// ⛔ 调用方（= <see cref="Tick"/>）必须**每帧都调**：不能写成
        /// <c>|err| &gt; 阈值 ? SteerRate(err) : 1f</c> —— 那个门控等于"阈值以内一律不校正"，
        /// 而落后量一旦被成批到达的快照推过阈值就**再也回不来**（时钟会一直跑在历史之外 ⇒ 插值静默失效）。
        /// </summary>
        public static float SteerRate(float lagErrorMs, SnapshotInterpolatorOptions options)
        {
            var threshold = options.CatchUpThresholdMs;
            if (Mathf.Abs(lagErrorMs) > threshold)
                return lagErrorMs > 0f ? 1f + options.CatchUpMaxRate : 1f - options.CatchUpMaxRate * 0.5f;

            return Mathf.Clamp(1f + lagErrorMs / Mathf.Max(1f, options.TargetLagMs) * options.LagSteerGain,
                1f - options.LagSteerMaxRate, 1f + options.LagSteerMaxRate);
        }

        /// <summary>本实例参数下的控制律（等价于 <see cref="SteerRate(float, SnapshotInterpolatorOptions)"/>）。</summary>
        public float SteerRate(float lagErrorMs)
        {
            return SteerRate(lagErrorMs, _options);
        }

        // ── 入快照 ───────────────────────────────────────────────────────────

        /// <summary>
        /// 推入一帧权威快照（**到达只入历史；⛔ 绝不重置渲染时钟、⛔ 绝不换正在渲染的窗口**）。
        /// <para>
        /// 非预期分支（两条，都**只报一次**、都**不静默**）：
        /// ① <paramref name="serverMs"/> &lt;= 0（协议字段缺失）=&gt; 时间轴退化为"按标称周期推定"；
        /// ② 时间戳没有前进 / 乱序 =&gt; **整帧丢弃**（⛔ 不入历史 —— 历史必须**按时间戳单调**，
        ///    否则 `SelectWindow` 的"找夹住时钟的一对"会选错）。
        /// </para>
        /// </summary>
        /// <param name="serverMs">该快照的服务端时间戳（毫秒，`server_ms` 字段）。</param>
        /// <param name="payload">调用方自己的快照对象（本件只存引用，⛔ 不读、⛔ 不拷贝；可为 null）。</param>
        /// <returns><c>true</c> = 本帧入了历史；<c>false</c> = 被丢弃（原因已留痕）。</returns>
        public bool Push(float serverMs, object payload = null)
        {
            var real = _clock();
            var first = _snapshotCount == 0;

            if (serverMs <= 0f)
            {
                // 非预期分支（协议字段缺失 / 为 0）：退化时间轴 = 按标称周期推定，并留痕一次。
                // 退化的代价是节奏退化为固定标称间隔，但画面依然不跳（时钟照旧只按真实时间走）。
                LogThrottle.WarnOnce(Tag, _logKey + "/push.noTimestamp",
                    "快照 server_ms=" + serverMs + "（<=0，字段缺失或未填）⇒ 插值时间轴退化为按标称周期 " +
                    _options.SnapshotIntervalMs.ToString("F0") + "ms 推定；" +
                    "请检查服务端是否在快照里填了 server_ms（只报一次）");
                serverMs = Mathf.RoundToInt(_newestMs) + _options.SnapshotIntervalMs;
            }
            else if (!first && serverMs <= _newestMs)
            {
                _droppedOutOfOrder++;
                LogThrottle.WarnOnce(Tag, _logKey + "/push.outOfOrder",
                    "快照时间戳没有前进（最新=" + _newestMs.ToString("F0") + " 本帧=" + serverMs +
                    "）⇒ 丢弃本帧（⛔ 不入插值历史）；若持续出现说明服务端 server_ms 不是单调递增，" +
                    "或快照乱序（只报一次）");
                return false;
            }

            // ── 入历史（环形；**到达只入队，绝不改正在渲染的那一对**） ──
            for (var i = 0; i < _options.HistSlots - 1; i++)
            {
                var tmp = _payloadHist[i];
                _payloadHist[i] = _payloadHist[i + 1];
                _payloadHist[i + 1] = tmp;
                _msHist[i] = _msHist[i + 1];
            }

            _payloadHist[_options.HistSlots - 1] = payload;
            _msHist[_options.HistSlots - 1] = serverMs;
            if (_histLen < _options.HistSlots) _histLen++;
            _snapshotCount++;

            // ── 服务端时间戳 → 时间轴（★ 快照到达只做这一件事，⛔ 绝不碰渲染时钟） ──
            _newestMs = serverMs;
            _arrivalReal = real;

            if (first)
            {
                // 渲染时钟**只在首帧对齐一次**：对齐到"服务端现在 - 目标落后量"，
                // 于是从第一帧起就落在**目标落后量前**那一对快照之间，不会先贴到最新再被推回去。
                _clockBaseMs = _newestMs - _options.TargetLagMs;
                _clockBaseReal = real;
                _clockRate = 1f;
                _catchingUp = false;
                _renderMs = _clockBaseMs;
                Game.Logger?.Info(Tag,
                    "渲染时钟已对齐：server_ms=" + _newestMs.ToString("F0") + " - 目标落后 " +
                    _options.TargetLagMs.ToString("F0") + "ms（" + _options.RenderLagIntervals.ToString("F0") +
                    "×" + _options.SnapshotIntervalMs.ToString("F0") + "ms = 抖动缓冲）= 时钟起于 " +
                    _clockBaseMs.ToString("F0") + "（只在首帧对齐一次；此后快照到达只入历史，" +
                    "⛔ 不重置时钟、⛔ 不换正在渲染的窗口）");
            }

            return true;
        }

        /// <summary>
        /// 每帧调一次：推进渲染时钟 → 有界比例速率修正 → 选插值窗口 → 返回插值比例 `t`。
        /// <para>未就绪（还没到首帧 / 历史不足 2 格）时返回 <c>1f</c>（"冻在最后一个已知位置"语义）。</para>
        /// </summary>
        /// <returns>插值比例 <c>t</c> ∈ [0,1]（调用方用它 Lerp <see cref="WindowStartPayload"/> 与
        /// <see cref="WindowEndPayload"/> 的字段）。</returns>
        public float Tick()
        {
            if (_snapshotCount == 0)
            {
                _lastT = float.NaN;
                _hasPrev = false;
                _outOfWindow = false;
                return 1f;
            }

            var real = _clock();

            // ① 时钟：绝对真实时间 × 速率（⛔ 不因快照到达而重置）。
            var clock = RenderClockMsAt(real);

            // ② 速率：**有界比例修正**（稳态 ±5%，灾难级偏差放开到 CatchUpMaxRate）。
            //    ⛔ 不写成 `|err| > 阈值 ? SteerRate(err) : 1f` —— 那个门控 = "阈值以内一律不校正"，
            //    而落后量一旦被成批到达的快照推过阈值就**再也回不来**（时钟一直跑在历史之外
            //    ⇒ SelectWindow 夹到最旧一对 ⇒ t 恒为 0 ⇒ 插值静默失效）。
            //    为什么走速率而不是"直接把时钟瞬跳到目标"：位置的导数就是速度，瞬跳 = 单位前跳一大截。
            var err = (ServerNowMsAt(real) - _options.TargetLagMs) - clock;
            var rate = SteerRate(err);
            if (!Mathf.Approximately(rate, _clockRate)) SetClockRate(rate, real);
            clock = RenderClockMsAt(real);

            if (Mathf.Abs(err) > _options.CatchUpThresholdMs && !_catchingUp)
            {
                _catchingUp = true;
                Game.Logger?.Warn(Tag,
                    "渲染时钟偏离目标 " + err.ToString("F0") + "ms（目标落后 " +
                    _options.TargetLagMs.ToString("F0") + "ms）⇒ 启动有界追帧（速率 " + rate.ToString("F2") +
                    "×）；常见成因：长卡顿 / 断网重连 / 服务端停推" +
                    "（本机卡顿会把 Time.deltaTime 夹在 Time.maximumDeltaTime）");
            }
            else if (Mathf.Abs(err) <= _options.CatchUpThresholdMs && _catchingUp)
            {
                _catchingUp = false;
                Game.Logger?.Info(Tag,
                    "渲染时钟已回到目标附近（偏离 " + err.ToString("F0") + "ms）⇒ 速率回到 " +
                    rate.ToString("F2") + "×");
            }

            // ②′ 时钟**超前上限**（"没有新数据就不许发明时间"）。
            //    为什么必须有：快照**停推**（对局结束 / 断线 / 服务端不再推）之后时钟会跟着
            //    "服务端现在"一路往前走，而把 50 s 的偏差用 ±5% 的速率拉回来要上千秒
            //    ⇒ 这段时间单位全部冻在最后一帧。⛔ 这个夹取**不产生画面跳变**：夹取前后 clock
            //    都在最新快照之后 ⇒ SelectWindow 两边都把 t 夹成 1 ⇒ 位置完全相同（本来就冻着）。
            //    ✅ 而且恢复是**立刻**的：上限挂在最新快照上，快照一恢复上限跟着松开（不靠速率追）。
            if (_histLen >= 1)
            {
                var leadCap = _newestMs + _options.ClockMaxLeadMs;
                if (clock > leadCap)
                {
                    LogThrottle.WarnOnce(Tag, _logKey + "/clock.leadCapped",
                        "渲染时钟超前最新快照 " + (clock - _newestMs).ToString("F0") + "ms（上限 " +
                        _options.ClockMaxLeadMs.ToString("F0") + "ms = " + _options.ClockMaxLeadIntervals.ToString("F0") +
                        " 个间隔）⇒ 就地锚回并保持；成因：快照停推（对局结束 / 断线 / 服务端不再推）。" +
                        "⛔ 不是把时钟永久钉死：上限跟着最新快照走，快照一恢复立即松开（只报一次）");
                    _clockLeadCapped = true;
                    // 就地锚回（锚在**绝对**值 leadCap 上，⛔ 不是 SetClockRate —— 那个会锚在
                    // 未夹取的时钟值上，等于没夹）。速率不动（仍是比例修正给的那个）。
                    _clockBaseMs = leadCap;
                    _clockBaseReal = real;
                    clock = leadCap;
                }
                else if (clock < _newestMs - _options.ClockMaxLeadMs && _clockLeadCapped)
                {
                    _clockLeadCapped = false;   // 回到正常范围 ⇒ 复位告警（下次停推还能再报一次）
                }
            }
            _renderMs = clock;

            // ③④ 按时钟选窗口 + 算 t：窗口边界只在 t=1 那一刻跨过 ⇒ 位置连续、速度恒定。
            var t = SelectWindow(_renderMs);
            _hasPrev = _histLen >= 2;

            // ③′ 脱窗不变量（**必须留痕**）：SelectWindow 在时钟早于最旧快照时会夹到最旧一对、
            //     使 t 恒为 0 —— 这一刻**插值已经死了**，但画面上只是"动得一顿一顿"，不会报任何错。
            //     这是"最隐蔽的静默失效"，所以显式记一次（只报一次，⛔ 不刷屏）。
            if (_histLen >= 2)
            {
                var oldest = _msHist[_options.HistSlots - _histLen];
                var newest = _msHist[_options.HistSlots - 1];
                _outOfWindow = _renderMs < oldest || _renderMs > newest;
                if (_outOfWindow)
                {
                    LogThrottle.WarnOnce(Tag, _logKey + "/window.out",
                        "渲染时钟 " + _renderMs.ToString("F0") + "ms 掉出快照历史 [" + oldest.ToString("F0") +
                        ", " + newest.ToString("F0") + "]ms（落后最新 " + (newest - _renderMs).ToString("F0") +
                        "ms，历史覆盖 " + _options.HistoryCoverageMs.ToString("F0") + "ms）⇒ 插值被夹成阶梯" +
                        "（t≡0/1）；速率 " + _clockRate.ToString("F3") + "× 会把它拉回目标 " +
                        _options.TargetLagMs.ToString("F0") + "ms（只报一次）");
                }
                else
                {
                    _outOfWindow = false;
                }
            }
            else
            {
                _outOfWindow = false;
            }

            _lastT = t;
            return t;
        }

        /// <summary>
        /// **按渲染时钟选插值窗口**：在历史里找一对"夹住时钟"的快照 ⇒ 写窗口时间戳 / 载荷，并返回比例 `t`。
        /// <para>
        /// 为什么必须"按时钟选"而不是"每收到一帧就换一对"：后者把换窗口的时刻绑在**包到达时刻**上，
        /// 而时钟是按真实时间走的 ⇒ 换窗口那一刻时钟离窗口末端还差 0~1 帧（实测 t 最大只到 0.707，
        /// 而窗口末端才是 t=1）⇒ 那一帧要交付"剩下的任意份额"（0~100% 的一个间隔位移）⇒ 速度脉动。
        /// 按时钟换窗口时，窗口边界**只在 t=1 那一刻**跨过，而 t=1 与下一段的 t=0 是同一个坐标
        /// ⇒ 位置连续、速度严格恒定。
        /// </para>
        /// 越界（时钟早于最旧 / 晚于最新）时夹到最边上一对，⛔ 绝不外推。
        /// </summary>
        private float SelectWindow(float clock)
        {
            var slots = _options.HistSlots;
            var lo = slots - _histLen;
            var pi = slots - 1;
            var ci = slots - 1;
            if (_histLen >= 2)
            {
                pi = lo;
                ci = lo + 1;
                for (var i = lo; i < slots - 1; i++)
                {
                    if (clock <= _msHist[i + 1]) { pi = i; ci = i + 1; break; }
                    pi = i;
                    ci = i + 1;
                }
            }

            _prevMs = _msHist[pi];
            _currMs = _msHist[ci];
            _prevPayload = _payloadHist[pi];
            _curPayload = _payloadHist[ci];

            var span = Mathf.Max(1f, _currMs - _prevMs);
            return _histLen >= 2 ? Mathf.Clamp01((clock - _prevMs) / span) : 1f;
        }

        /// <summary>
        /// 复位到"还没收到任何快照"的状态（**换对局 / 重连 / 出图**时调）。
        /// <para>时钟对齐点、历史、窗口、自检计数全清；「只报一次」的告警**不重置**
        /// （它们按实例记账，跨对局保留 —— 否则每局都会把那几条刷一遍）。</para>
        /// </summary>
        public void Reset()
        {
            var now = _clock();

            for (var i = 0; i < _msHist.Length; i++)
            {
                _msHist[i] = 0f;
                _payloadHist[i] = null;
            }
            _histLen = 0;

            _prevMs = 0f;
            _currMs = 0f;
            _prevPayload = null;
            _curPayload = null;
            _newestMs = 0f;

            _renderMs = float.NaN;
            _clockBaseMs = 0f;
            _clockBaseReal = now;
            _arrivalReal = now;
            _clockRate = 1f;

            _hasPrev = false;
            _snapshotCount = 0;
            _catchingUp = false;
            _outOfWindow = false;
            _clockLeadCapped = false;
            _droppedOutOfOrder = 0;
            _lastT = float.NaN;
        }
    }
}
