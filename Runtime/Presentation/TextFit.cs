// ─────────────────────────────────────────────────────────────────────────────
// CloverEngine · Runtime/Presentation/TextFit.cs
// 可变长文本的**单行显示截断**（二分找最长可行前缀 + 省略号）—— UI 通用件，下沉到引擎。
//
// 来源（逻辑逐字搬移）：
//   clover-project-cr · client/Assets/Scripts/UI/TextFit.cs:36-128
//     · :39  `Ellipsis = "\u2026"`（U+2026；⛔ 不用三个 ASCII 点 —— CJK 字体下宽度不一致）
//     · :50-84  `Clamp(Text label, string raw)`（装得下就原样写；装不下二分截断 + 保留省略号）
//     · :97-104 `Measure(Text label, string text)`（自建 `TextGenerator` 量单行宽）
//     · :107-113 `ClampSelf(Text label)`（把标签**当前**文本按同规则裁一次）
//     · :115-127 `Path(Text label)`（日志用节点路径）
//
// 为什么沉：
//   uGUI 的 `HorizontalWrapMode` **只有 `Wrap` / `Overflow` 两个取值**，不存在
//   "horizontalOverflow = Truncate" ⇒ 「横向截断」只能自己把**字符串**裁短。这是**所有**用 uGUI 的
//   项目都会撞上的同一件事（引擎建文本的入口 `UIFactory.CreateText` 出于多行文案的需要，
//   刻意设成 `horizontalOverflow = Wrap` + `verticalOverflow = Overflow`：横排会换行、**纵向不设限**），
//   所以下沉进引擎。
//
// ★ 因果（这条必须写下来，否则后人一定会"顺手优化"回去）：
//   为什么**必须自建 `TextGenerator` 量宽**，而不是读 `label.preferredWidth`：
//   uGUI 的 `preferredWidth` 走的是**缓存**的布局用生成器 —— 刚 `label.text = 新值` 之后，
//   同一帧里读到的还是**上一串文本**的宽度（原件本轮实测：3 字串 `rectW=58 / preferredW=44`
//   却被判超宽、截成 1 字 + 省略号，就是读到了旧值）。自建生成器每次现算，判定与裁剪都基于当前串。
//
// ★ 为什么是"显式调用"而不是"把引擎的文本创建点全局改成截断"：
//   一旦全局截断，**多行说明文案**（规则两行、加载提示）也会被裁成一行 —— 那是另一类破坏。
//   截断只对「内容长度由数据决定、且必须单行显示」的标签成立（例：玩家昵称、卡名等来自服务端数据的标签）
//   ⇒ 由各面板在"把数据写进标签"的那一处调一次。
//
// 已知事故 / 坑：
//   · ⛔ **不许用改字号来"糊过去"**：原件明令禁止（改字号会让同一列标签字号不一致，且仍然可能溢出）。
//     装得下就原样写入 —— ⛔ 不无端加省略号。
//   · `label.rectTransform.rect.width <= 1`（布局还没算 / 尚未挂到画布）⇒ 原样写入、不裁：
//     那时**判定依据不存在**，裁只会裁错（原件踩过：布局首帧被裁成 1 字）。
//   · 二分之后仍要**向前退到确实装得下为止**：宽度随长度"单调不减"在换行点上有轻微非单调，
//     由那一步兜住（原件保留此步）。
//   · 判据口径与调用方的断言同源（同一个 `Text.preferredWidth` 语义）⇒ `Clamp` 之后
//     `preferredW <= rectW` 恒成立，可用它做断言。
// ─────────────────────────────────────────────────────────────────────────────

using UnityEngine;
using UnityEngine.UI;

namespace CloverEngine
{
    /// <summary>
    /// 可变长文本的**单行显示截断**（装得下 ⇒ 原样；装不下 ⇒ 二分找最长前缀 + 省略号）。
    ///
    /// <para>
    /// 三个入口：<see cref="Clamp"/>（写入并返回实际文本）、<see cref="ClampSelf"/>
    /// （把标签**当前**文本裁一次）、<see cref="Measure"/>（量不受矩形约束的单行宽，供调用方断言）。
    /// </para>
    /// <para>
    /// ⛔ 不做的事：不换行、不改字号、不裁剪多行文案 —— 那些都会把"超长数据"变成另一类更难查的显示缺陷。
    /// </para>
    /// </summary>
    public static class TextFit
    {
        /// <summary>日志标签。</summary>
        public const string Tag = "TextFit";

        /// <summary>默认省略号（U+2026）。⛔ 不用三个 ASCII 点：CJK 字体下宽度不一致。</summary>
        public const string Ellipsis = "\u2026";

        /// <summary>
        /// 截断后写入 <paramref name="label"/>，返回**实际写进去的字符串**。
        /// <paramref name="label"/> 为 null 时原样返回（调用方不必判空）。
        /// </summary>
        /// <param name="label">目标标签（矩形宽 = 可用宽度）。</param>
        /// <param name="raw">数据源给的原文。</param>
        public static string Clamp(Text label, string raw) => Clamp(label, raw, Ellipsis);

        /// <summary>
        /// 同 <see cref="Clamp(Text,string)"/>，但省略号可换（有些字体没有 U+2026 的字形，
        /// 或某些语言习惯用 ASCII 点）—— 空的 <paramref name="ellipsis"/> 按 <see cref="Ellipsis"/> 处理。
        /// </summary>
        /// <param name="label">目标标签（矩形宽 = 可用宽度）。</param>
        /// <param name="raw">数据源给的原文。</param>
        /// <param name="ellipsis">省略号后缀（可换，⛔ 不许为空串 —— 会得到"被砍掉的文本"而无提示）。</param>
        public static string Clamp(Text label, string raw, string ellipsis)
        {
            var s = raw ?? string.Empty;
            if (label == null) return s;
            var suffix = string.IsNullOrEmpty(ellipsis) ? Ellipsis : ellipsis;

            var limit = label.rectTransform.rect.width;
            if (limit <= 1f || s.Length == 0)
            {
                // 矩形宽度未知（布局还没算）或空串：原样写入，不做无法判定的裁剪。
                label.text = s;
                return s;
            }

            label.text = s;
            var natural = Measure(label, s);
            if (natural <= limit) return s; // 装得下 ⇒ 原样（⛔ 不加省略号）

            // 二分：找最大的 k 使 (前 k 字 + 省略号) 装得下。二分对"宽度随长度单调不减"成立，
            // 结尾再向前退到**确实能装下**为止（换行点造成的轻微非单调由这一步兜住）。
            var lo = 0;
            var hi = s.Length;
            while (lo < hi)
            {
                var mid = (lo + hi + 1) / 2;
                if (Measure(label, s.Substring(0, mid) + suffix) <= limit) lo = mid; else hi = mid - 1;
            }
            while (lo > 0 && Measure(label, s.Substring(0, lo) + suffix) > limit) lo--;

            var kept = s.Substring(0, lo) + suffix;
            label.text = kept;
            Game.Logger?.Info(Tag,
                $"文本超宽已截断：标签={Path(label)} 原文={s.Length} 字 → 显示={lo} 字+省略号 " +
                $"(rectW={limit:0.0} 单行宽={natural:0.0})");
            return kept;
        }

        /// <summary>
        /// 量一串文本在**不受矩形宽度约束**时的单行像素宽（口径 = uGUI 的 `Text.preferredWidth`：
        /// `TextGenerator.GetPreferredWidth(text, GetGenerationSettings(Vector2.zero)) / pixelsPerUnit`）。
        ///
        /// <para>
        /// <b>⛔ 必须自建 <see cref="TextGenerator"/>，不许读 <c>label.preferredWidth</c></b>：
        /// 后者走 uGUI **缓存**的布局生成器 —— 刚赋值完 `label.text` 的同一帧读到的还是上一串的宽度
        /// （见文件头「因果」的实测读数）。自建生成器每次现算。
        /// </para>
        /// </summary>
        public static float Measure(Text label, string text)
        {
            if (label == null) return 0f;
            var gen = new TextGenerator();
            var settings = label.GetGenerationSettings(Vector2.zero);
            var ppu = label.pixelsPerUnit <= 0f ? 1f : label.pixelsPerUnit;
            return gen.GetPreferredWidth(text ?? string.Empty, settings) / ppu;
        }

        /// <summary>把标签**当前**文本按上面的规则裁一次（用于已在别处赋过值的标签）。</summary>
        public static void ClampSelf(Text label)
        {
            if (label == null) return;
            var raw = label.text;
            var kept = Clamp(label, raw);
            if (kept != raw) label.text = kept;
        }

        /// <summary>节点路径（日志用；最多上溯 16 层，防异常层级导致死循环）。</summary>
        private static string Path(Text label)
        {
            var t = label.transform;
            var p = t.name;
            var parent = t.parent;
            var guard = 0;
            while (parent != null && guard++ < 16)
            {
                p = parent.name + "/" + p;
                parent = parent.parent;
            }
            return p;
        }
    }
}
