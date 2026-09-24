// ─────────────────────────────────────────────────────────────────────────────
// CloverEngine · Runtime/Presentation/UnitFacingMap.cs
// 「N 档朝向 → 视角号 / 是否镜像」的通用映射 —— 纯逻辑，不持有任何 Unity 对象。
//
// 为什么要有本件：
//   「朝向 → 视角号 + 是否镜像」是**多视角 2D sprite**（16 / 32 向素材）的通用问题，与具体玩法无关
//   （项目专有的帧段数据表不属本件）。本件**只依赖「档位数 + 映射表」**：映射表可换，16 档只是默认。
//
// 已知失效模式（⛔ 别再踩）：
//   · **档位 / 视角搞反不会报错**，只会"人物朝向看着别扭" ⇒ 映射表只能靠断言 + 负控保证，肉眼判不了。
//   · ⛔ 运行时不许把 9 个视角的 clip **取并集**依次播（现象 = "苍蝇海 1 秒 9 次视角、
//     走路原地打转 / 抽搐"）。
//     本件只回答"**该选哪一个视角**"，⛔ 不负责 clip 内容 —— "每档只取一个视角"是调用方（帧段表）的约束。
//   · 朝向极角必须由**插值窗口两端快照之差**求，⛔ 不能用逐帧位移：服务端位置是毫格量化的，
//     站立单位会被 ±2 毫格噪声把方向翻 180°。
//     本件**只做映射**；滞回与噪声门槛是调用方策略，⛔ 引擎不替它定死。
//   · 实测锚点（换素材时要重新核对）：`_1` = 背身（φ = +90°，远离镜头）→ `_5` = 侧身（φ = 0°，朝 +x）
//     → `_9` = 正朝镜头（φ = −90°）。
// ─────────────────────────────────────────────────────────────────────────────

using System;

namespace CloverEngine
{
    /// <summary>
    /// 「朝向档 → 视角号 + 是否水平镜像」的映射表（**纯数据 + 纯函数**，可换表、可参数化档位数）。
    ///
    /// <para>
    /// 用法：<c>var map = new UnitFacingMap(myStepToView);</c> 或直接用 <see cref="Default"/>
    /// （标准 16 档 / 9 视角）。取视角：<see cref="ViewForHeading"/>（由朝向极角）或
    /// <see cref="ViewForStep"/>（由档位号）。
    /// </para>
    /// <para>
    /// 契约：
    /// ① 档位号 <c>step ∈ [0, StepCount)</c>，<see cref="Wrap"/> 负责取模（负数也折回正区间）；
    /// ② <c>step = 0</c> 对应 <c>headingDeg = +90°</c>（远离镜头 / 上场方向），档位**顺时针**递增；
    /// ③ 镜像档 = <c>step ≥ FlipFromStep</c>（默认 <c>StepCount/2 + 1</c>，16 档时为 9 ⇒
    ///    与 <c>step &gt; 8</c> 等价）；
    /// ④ 本件无状态、可多实例、主线程使用（不碰任何全局态）。
    /// </para>
    /// </summary>
    public sealed class UnitFacingMap
    {
        /// <summary>日志标签。</summary>
        public const string Tag = "UnitFacingMap";

        /// <summary>默认档位数（16 档 = 全周 360° ÷ 22.5°）。</summary>
        public const int DefaultStepCount = 16;

        /// <summary>
        /// 档位 0 对应的朝向极角（度）。**约定的原点**：正值 = +y（远离镜头 / 上场方向），
        /// 与原件 `step = round((90° − φ) / 22.5°) mod 16` 里的 `90` 同一个约定。
        /// </summary>
        public const float HeadingOffsetDeg = 90f;

        /// <summary>
        /// 标准 16 档 → 9 视角表（改动必须同步改表并重跑其断言 + 负控）。
        /// </summary>
        public static readonly int[] Standard16StepToView = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 8, 7, 6, 5, 4, 3, 2 };

        private static readonly UnitFacingMap Standard = new UnitFacingMap(Standard16StepToView);

        /// <summary>标准 16 档映射（共用实例，只读使用）。</summary>
        public static UnitFacingMap Default => Standard;

        private readonly int[] _stepToView;

        /// <summary>
        /// 从该档位起为**镜像档**（西半边）。构造时由 <c>flipFromStep &lt;= 0</c> 自动推导为
        /// <c>StepCount / 2 + 1</c>（16 档 ⇒ 9，与原件 <c>step &gt; 8</c> 等价）。
        /// </summary>
        public int FlipFromStep { get; }

        /// <summary>
        /// 建一张映射表。
        /// </summary>
        /// <param name="stepToView">
        /// 档位 → 视角号（下标 = 档位号）。长度即 <see cref="StepCount"/>。
        /// ⛔ 按**只读**对待（本类不克隆；换表是冷路径，但热路径会直接索引它）。
        /// </param>
        /// <param name="flipFromStep">
        /// 镜像起始档位；<c>&lt;= 0</c> ⇒ 自动取 <c>StepCount / 2 + 1</c>。
        /// </param>
        public UnitFacingMap(int[] stepToView, int flipFromStep = 0)
        {
            if (stepToView == null || stepToView.Length == 0)
                throw new ArgumentException("映射表为空（null 或长度 0）：至少要有 1 档", nameof(stepToView));

            _stepToView = stepToView;

            var auto = stepToView.Length / 2 + 1;
            FlipFromStep = flipFromStep > 0 ? flipFromStep : auto;
            if (FlipFromStep > stepToView.Length)
            {
                // 非预期分支：显式给了越界的镜像起点 ⇒ 收敛到自动值并留痕（否则整表都不会镜像）
                LogThrottle.WarnThrottled(Tag, "flipFromStep.range",
                    $"flipFromStep={flipFromStep} 超出档位数 {stepToView.Length} ⇒ 按 {auto} 处理");
                FlipFromStep = auto;
            }
        }

        /// <summary>档位数（= 映射表长度）。</summary>
        public int StepCount => _stepToView.Length;

        /// <summary>取映射表里的视角号（下标按 <see cref="Wrap"/> 取模）；<c>&lt;= 0</c> 返回 0。</summary>
        public int ViewForStep(int step)
        {
            var v = _stepToView[Wrap(step)];
            return v > 0 ? v : 0;
        }

        /// <summary>该档位是否要水平镜像（西半边 = 同一视角 + 镜像）。</summary>
        public bool StepFlip(int step) => Wrap(step) >= FlipFromStep;

        /// <summary>把任意档位号折回 <c>[0, StepCount)</c>（负数 / 越界都收敛）。</summary>
        public int Wrap(int step)
        {
            var n = StepCount;
            var v = step % n;
            return v < 0 ? v + n : v;
        }

        /// <summary>
        /// 朝向极角（度）→ 档位号（<c>[0, StepCount)</c>）。
        /// <para>
        /// <c>step = round((HeadingOffsetDeg − headingDeg) / (360° / StepCount))</c>，再取模。
        /// 取整用 <c>Math.Round</c> 的默认口径（**中点取偶**），与原件的 <c>Math.Round</c> 一致 ——
        /// ⛔ 不用"四舍五入远离零"：那会在恰好落在档边界的朝向上差一档（表现为偶发"朝向跳一格"）。
        /// </para>
        /// </summary>
        /// <param name="headingDeg">朝向极角（度）。非有限值 ⇒ 按 0° 处理 + 降频 Warn。</param>
        public int StepForHeading(float headingDeg)
        {
            if (float.IsNaN(headingDeg) || float.IsInfinity(headingDeg))
            {
                // 非预期分支：非有限极角（多半是调用方把零向量喂进了 Atan2，或位移算成了 NaN）
                LogThrottle.WarnThrottled(Tag, "heading.notfinite",
                    $"朝向极角非有限值（{headingDeg}）⇒ 按 0° 处理；检查调用方的窗口位移向量（是否全零 / NaN）");
                headingDeg = 0f;
            }

            // 用 double 中间量：与原件 `(90.0 - φ) / 22.5` 同为双精度，避免 16 档边界上的单精度误差。
            var stepSize = 360.0 / StepCount;
            var step = (int)Math.Round((HeadingOffsetDeg - headingDeg) / stepSize) % StepCount;
            if (step < 0) step += StepCount;
            return step;
        }

        /// <summary>
        /// 朝向极角（度）→ 视角号 + 是否镜像（一次取全，热路径推荐这个，省一次 <see cref="StepForHeading"/> 的取模）。
        /// </summary>
        /// <param name="headingDeg">朝向极角（度）。</param>
        /// <param name="view">输出：视角号（≥ 1）。</param>
        /// <param name="flip">输出：是否水平镜像。</param>
        public void ViewForHeading(float headingDeg, out int view, out bool flip)
        {
            var step = StepForHeading(headingDeg);
            view = ViewForStep(step);
            flip = StepFlip(step);
        }
    }
}
