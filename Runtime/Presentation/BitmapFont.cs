// ─────────────────────────────────────────────────────────────────────────────
// CloverEngine · Runtime/Presentation/BitmapFont.cs
// 位图字模的**排版内核**（纯数据 + 纯函数；⛔ 不引用 UnityEngine ⇒ 可离线自检）：
// 字符 → 格位（列 / 行 / 步进）、整串度量、按宽换行、图集 UV 切格、bestFit 缩放、字形回退链。
//
// ★ 通用性依据：
//   这套内核**与素材无关** —— 它只做"整数格位 + 整数步进 + 矩形"的算术，唯一外部输入是
//   「某字符的字形（列 / 行 / 步进）」与「格子 / 图集尺寸」。而引擎侧只有注入点没有本体：
//   `TextHooks.ITextHook` 把引擎建的每个 `Text` 通知业务，却没有"位图字模怎么排"的实现
//   ⇒ 任何"自带位图字模 / 像素风"的项目都得在业务侧重写一遍同样的度量 / 换行 / 切格。
//   本件把本体补齐：**素材加载（.tbl / .dc6 / png / 项目路径 / 简繁表）仍留业务侧**，
//   通过 `IGlyphSource`（格子尺寸 + 图集尺寸 + 字符 → 字形）与回退链注入。
//
// ★ 为什么必须这样（⛔ 别"顺手优化"）：
//   · **步进必须取表里的 `width`**，⛔ 不许用"格子宽"：原版拉丁字模的格子是同宽的，
//     而每个字形的**推进量**不同（i / l 窄，W / M 宽）⇒ 用格宽排列会立刻把字距搞乱。
//     （libd2 `font.zig` L67 原话："How far to advance after drawing it.
//       This is the whole reason the table exists."）
//   · **换行判据是"量到 ≥ 框宽就断"**（不是 `> 框宽`）：判据与参考物 `breakLine`（L188-216）一致，
//     差一个比较符就会让每行比原版多 / 少一个字。
//   · **表里没有的字符：不画、也不推进**（libd2 `font.zig` L130-131 注释 + L170 `orelse continue`）
//     ⇒ 无字形时 `Step` / `Measure` 计 0，**不是**退回格宽。
//     ⚠️ 拉丁表是例外：`Advance` 对**码位范围外**的字符返回格宽（与参考物一致）。
//
// 注意：
//   · 框宽 ≤ 0 ⇒ 不换行（原件 `availPx <= 0` 直接整段一行）；贪心循环里"一个字都塞不下"
//     时**至少取一个字**，否则死循环。
//   · 断在空格的：空格**不带到下一行**；`lastSpace > start` 才算数（行首空格不作断点）。
//   · UV 的 y 必须按 `1-(row+1)*cellH/H` 翻（PNG 行 0 在上、Unity UV (0,0) 在左下）；
//     图集尺寸未知 ⇒ 按 1 兜底（否则除零，且 UV 全变 NaN）。
//   · bestFit 只**整体缩一档**（按宽度），⛔ 不许逐行缩、⛔ 不许改字号表 —— 同一列标签字号必须一致。
//   · ⛔ 不许把本件接到引擎自建 `Text`（`UIFactory`）上：那会把**多行说明文案**也按位图口径排。
//     引擎自建文本走 TTF + `TextHooks` 注入，本件只由业务侧显式调用。
// ─────────────────────────────────────────────────────────────────────────────

using System.Collections.Generic;

namespace CloverEngine
{
    /// <summary>
    /// 一个字模格子：图集里的列 / 行 + 原版步进（px）。
    /// <para>与业务侧"字的图集坐标 + 排版推进量"一一对应；本件只用它做算术，不碰素材。</para>
    /// </summary>
    public struct BitmapGlyph
    {
        /// <summary>图集里的列（格）。</summary>
        public int Col;

        /// <summary>图集里的行（格）。</summary>
        public int Row;

        /// <summary>原版步进（= 字模表里的 `width`，px）。无字形时按 0 处理。</summary>
        public int Advance;
    }

    /// <summary>
    /// 图集 UV 矩形（**纯数据**，不依赖 <c>UnityEngine.Rect</c> ⇒ 本件可离线自检）。
    /// 业务侧一行即可转成渲染层用的矩形。
    /// </summary>
    public struct BitmapUvRect
    {
        /// <summary>左下角 x（0..1）。</summary>
        public float X;

        /// <summary>左下角 y（0..1；PNG 行 0 在上 ⇒ 已翻转）。</summary>
        public float Y;

        /// <summary>宽（0..1）。</summary>
        public float Width;

        /// <summary>高（0..1）。</summary>
        public float Height;
    }

    /// <summary>
    /// 字模数据源（由调用方注入）：格子尺寸 + 图集尺寸 + 「字符 → 字形」。
    /// <para>
    /// ⛔ 本件**不预设**字模从哪来（`.tbl` / `.dc6` / png 图集 / 映射表 / 简繁 remap 都行）；
    /// 一个源可以只覆盖一部分字符（<see cref="TryGetGlyph"/> 返回 false 表示"本表没有"）。
    /// </para>
    /// </summary>
    public interface IGlyphSource
    {
        /// <summary>格子的宽（px；用于 UV 切格与"表外字符的默认步进"）。</summary>
        int CellWidth { get; }

        /// <summary>格子的高（px；行高 / 基线口径都按它）。</summary>
        int CellHeight { get; }

        /// <summary>图集的宽（px）。未知 ⇒ 返回值 &lt;= 0（本件按 1 兜底）。</summary>
        int AtlasWidth { get; }

        /// <summary>图集的高（px）。未知 ⇒ 返回值 &lt;= 0（本件按 1 兜底）。</summary>
        int AtlasHeight { get; }

        /// <summary>取某字符的字形；返回 false = **本表没有**（由回退链 / 调用方决定怎么退）。</summary>
        bool TryGetGlyph(char c, out BitmapGlyph glyph);
    }

    /// <summary>
    /// 位图字模的排版内核。全部为**纯函数**（无状态、无 Unity 依赖）⇒ 可用离线宿主逐行比对。
    /// </summary>
    public static class BitmapFont
    {
        /// <summary>
        /// 取某字符的排版步进（px）：有字形 ⇒ 表里的 `Advance`；表中没有 ⇒ **0**（原版行为：不推进）。
        /// </summary>
        public static int Step(IGlyphSource source, char c)
        {
            if (source == null) return 0;
            BitmapGlyph g;
            return source.TryGetGlyph(c, out g) ? g.Advance : 0;
        }

        /// <summary>量一串文本的宽度（px，**不受框宽约束**的单行宽）。</summary>
        public static int Measure(IGlyphSource source, string text)
        {
            if (source == null || string.IsNullOrEmpty(text)) return 0;

            var w = 0;
            for (var i = 0; i < text.Length; i++)
                w += Step(source, text[i]);
            return w;
        }

        /// <summary>
        /// 按宽换行（口径 = 原版 libd2 `font.zig` L188-216 `breakLine`）。
        /// <para>
        /// 先把 <c>\n</c> 当**硬换行**拆段，再对每段贪心塞字：走到"量到 ≥ 框宽"就断；
        /// 路过空格就在**最后一个空格**断（空格不带到下一行）；没空格（中文就是这种）就断在
        /// 能塞下的最后一个字；框宽 ≤ 0 或 <paramref name="wrap"/> = false ⇒ 一段一行（不换）。
        /// </para>
        /// </summary>
        /// <param name="source">字模数据源。</param>
        /// <param name="text">原文（可为 null / 空 ⇒ 返回空表）。</param>
        /// <param name="availPx">可用宽度（px）；≤ 0 表示"不换行"。</param>
        /// <param name="wrap">是否允许换行。</param>
        public static List<string> WrapLines(IGlyphSource source, string text, int availPx, bool wrap)
        {
            var lines = new List<string>();
            if (source == null || string.IsNullOrEmpty(text))
                return lines;

            var paragraphs = text.Split('\n');
            for (var p = 0; p < paragraphs.Length; p++)
            {
                var s = paragraphs[p];
                if (!wrap || availPx <= 0 || Measure(source, s) < availPx)
                {
                    lines.Add(s);
                    continue;
                }

                var start = 0;
                while (start < s.Length)
                {
                    var w = 0;
                    var fits = 0;
                    var lastSpace = -1;
                    while (start + fits < s.Length)
                    {
                        if (s[start + fits] == ' ') lastSpace = start + fits;
                        var next = w + Step(source, s[start + fits]);
                        if (next >= availPx)
                            break;
                        w = next;
                        fits++;
                    }

                    if (start + fits >= s.Length)
                    {
                        lines.Add(s.Substring(start));
                        break;
                    }

                    if (lastSpace > start)
                    {
                        lines.Add(s.Substring(start, lastSpace - start));
                        start = lastSpace;
                        while (start < s.Length && s[start] == ' ') start++;   // 断在空格的：空格不带下去
                        continue;
                    }

                    var take = fits > 0 ? fits : 1;                            // 框太窄也至少出一个字
                    lines.Add(s.Substring(start, take));
                    start += take;
                }
            }
            return lines;
        }

        /// <summary>整串行数（口径与 <see cref="WrapLines"/> 完全一致）。</summary>
        public static int CountLines(IGlyphSource source, string text, int availPx, bool wrap)
        {
            return WrapLines(source, text, availPx, wrap).Count;
        }

        /// <summary>
        /// 某字形在图集里的 UV 矩形。
        /// <para>
        /// 口径：图集按**行主序**摆放，格子 = <c>CellWidth×CellHeight</c>，第 i 帧的格子 = (i % Cols, i / Cols)；
        /// PNG 行 0 在**上**，而 Unity 的 UV (0,0) 在**左下** ⇒ y 要按 <c>1-(row+1)*CellH/H</c> 翻。
        /// </para>
        /// </summary>
        public static BitmapUvRect CellUv(IGlyphSource source, BitmapGlyph g)
        {
            var w = source != null && source.AtlasWidth > 0 ? source.AtlasWidth : 1;
            var h = source != null && source.AtlasHeight > 0 ? source.AtlasHeight : 1;
            var cw = source != null ? source.CellWidth : 0;
            var ch = source != null ? source.CellHeight : 0;
            return new BitmapUvRect
            {
                X = g.Col * (float)cw / w,
                Y = 1f - (g.Row + 1) * (float)ch / h,
                Width = (float)cw / w,
                Height = (float)ch / h,
            };
        }

        /// <summary>
        /// 框宽（画布单位）→ 换行用的可用宽度（px）。口径 = 原件
        /// <c>Mathf.Max(1, Mathf.RoundToInt(size.x / scale))</c>（四舍五入取偶，与 <c>Mathf.RoundToInt</c> 一致）。
        /// <para>框宽 ≤ 0 或缩放 ≤ 0 ⇒ 0（= 调用方据此不换行）。</para>
        /// </summary>
        public static int WrapWidthPx(float boxWidth, float scale)
        {
            if (boxWidth <= 0f || scale <= 0f) return 0;
            var v = (int)System.Math.Round((double)(boxWidth / scale), System.MidpointRounding.ToEven);
            return v < 1 ? 1 : v;
        }

        /// <summary>
        /// bestFit（对应 uGUI 的 <c>resizeTextForBestFit</c>）：内容按 <paramref name="scale"/> 缩放后的宽度
        /// 超过框宽 ⇒ **整体缩一档**到刚好装下；不小于 <paramref name="minScale"/>（下限）。
        /// <para>装得下 ⇒ 原样返回 <paramref name="scale"/>（⛔ 不放大、⛔ 不逐行缩）。</para>
        /// <para>
        /// ⚠️ 框宽 ≤ 0 时本函数**仍按原件口径**缩到 <paramref name="minScale"/>（原件是
        /// <c>need > size.x</c> 直接进分支）⇒ 调用方应在框宽 &gt; 0 时才调用它。
        /// </para>
        /// </summary>
        /// <param name="scaledWidth">已按 <paramref name="scale"/> 缩放后的内容宽度（px / 画布单位同口径）。</param>
        /// <param name="scale">当前缩放（= 目标字高 / 字模格高）。</param>
        /// <param name="boxWidth">框宽（画布单位）。</param>
        /// <param name="minScale">缩放下限（≤ 0 表示无下限）。</param>
        public static float BestFitScale(float scaledWidth, float scale, float boxWidth, float minScale)
        {
            if (scale <= 0f || scaledWidth <= 0f || scaledWidth <= boxWidth)
                return scale;

            var fit = scale * boxWidth / scaledWidth;
            return fit < minScale ? minScale : fit;
        }

        /// <summary>
        /// 字形回退链：依次尝试 <paramref name="chain"/> 上的各候选源，**第一个命中的胜出**。
        /// <para>
        /// 用途 = 「某字符不在当前字模表时怎么退」（例：原版字模直查 → 简 / 繁 remap 表 → 都没有则放弃）。
        /// ⛔ 不做无限回退：链由调用方按优先级给出，且链条数固定。
        /// </para>
        /// </summary>
        /// <param name="chain">候选源（按优先级；可含 null，会被跳过）。</param>
        /// <param name="c">字符。</param>
        /// <param name="glyph">命中的字形；全链失败 ⇒ <c>default</c>。</param>
        /// <param name="index">命中来自第几个源（全失败 ⇒ -1）。</param>
        public static bool TryResolve(IGlyphSource[] chain, char c, out BitmapGlyph glyph, out int index)
        {
            glyph = default(BitmapGlyph);
            index = -1;
            if (chain == null) return false;

            for (var i = 0; i < chain.Length; i++)
            {
                var src = chain[i];
                if (src == null) continue;
                BitmapGlyph g;
                if (!src.TryGetGlyph(c, out g)) continue;
                glyph = g;
                index = i;
                return true;
            }
            return false;
        }

        /// <summary>同 <see cref="TryResolve"/>，但不关心来自哪个源。</summary>
        public static bool TryResolve(IGlyphSource[] chain, char c, out BitmapGlyph glyph)
        {
            int ignored;
            return TryResolve(chain, c, out glyph, out ignored);
        }

        /// <summary>
        /// 把回退链包成**一个** <see cref="IGlyphSource"/>（图集尺寸 / 格子尺寸取链上第一个非空源），
        /// 便于直接喂给 <see cref="Measure"/> / <see cref="WrapLines"/>（它们只认单源）。
        /// </summary>
        public static IGlyphSource Chain(IGlyphSource[] sources)
        {
            return new GlyphSourceChain(sources);
        }

        private sealed class GlyphSourceChain : IGlyphSource
        {
            private readonly IGlyphSource[] _sources;
            private readonly IGlyphSource _metrics;

            internal GlyphSourceChain(IGlyphSource[] sources)
            {
                _sources = sources ?? new IGlyphSource[0];
                for (var i = 0; i < _sources.Length; i++)
                {
                    if (_sources[i] != null) { _metrics = _sources[i]; break; }
                }
            }

            public int CellWidth { get { return _metrics != null ? _metrics.CellWidth : 0; } }

            public int CellHeight { get { return _metrics != null ? _metrics.CellHeight : 0; } }

            public int AtlasWidth { get { return _metrics != null ? _metrics.AtlasWidth : 0; } }

            public int AtlasHeight { get { return _metrics != null ? _metrics.AtlasHeight : 0; } }

            public bool TryGetGlyph(char c, out BitmapGlyph glyph)
            {
                return TryResolve(_sources, c, out glyph);
            }
        }
    }
}
