using System;
using UnityEngine.UI;

namespace CloverEngine
{
    /// <summary>
    /// 业务可注册的"文字渲染挂钩"：引擎通用件（Toast / Loading / 确认框 / 飘字 / 引导遮罩）
    /// 创建 <see cref="Text"/> 之后会通知它一次，业务可把自己的渲染方式挂上去。
    /// <para>
    /// <b>为什么需要它</b>：引擎通用件的文本一律走 <see cref="UIFactory.DefaultFont"/> ——
    /// 对"自带位图字模 / 像素风"的项目（例：复刻原版 2D ARPG 的那一类），业务自己的面板能换字模，
    /// 但**引擎自己建的 Text 换不了**，于是引擎自己的提示（中文 Toast、加载中…、确认框按钮）
    /// 成了画面上唯一破格的一处。本挂钩把"引擎刚建好的 Text"交回业务处置，
    /// 引擎**不预设**任何渲染方式（不注册 = 继续用内置字体，行为与没有本挂钩时逐字一致）。
    /// </para>
    /// <para>
    /// <b>生命周期</b>：挂钩是**进程级静态**的，业务在引擎启动（<see cref="Game.Launch"/>）之前注册一次即可。
    /// ⚠️ 引擎的 LoadingLayer 在 <c>UIManager</c> 构造时（= Launch 内）就建好了自己的 Text
    /// ⇒ 晚于 Launch 注册会漏掉它。
    /// </para>
    /// </summary>
    public interface ITextHook
    {
        /// <summary>
        /// 引擎刚创建了一个 <see cref="Text"/>（文案 / 字号 / 对齐 / 颜色 / 溢出策略均已就位，
        /// 调用方随后的布局改动也能读到）。
        /// <para>
        /// 约定：
        /// <list type="bullet">
        ///   <item>在这里把这条 Text 改造成自己的渲染方式（例：保留 Text 作"数据持有者"、
        ///     另挂一个位图字模渲染器画字）——引擎不会在通知之后覆盖 <c>font</c> / <c>text</c> 等字段；</item>
        ///   <item><b>不要抛异常</b>：引擎会吞掉并只 Warn 一次（⛔ 不许因为挂钩出错就让 Toast /
        ///     Loading / 确认框打不开），但那条 Text 是否改造成功就不确定了；</item>
        ///   <item>通知是**同步**发出的，且在引擎建 Text 的那一帧内 ⇒ 挂钩内部**不要**再调
        ///     <see cref="UIFactory.CreateText"/>（会再触发一次通知，递归）。</item>
        /// </list>
        /// </para>
        /// </summary>
        void OnTextCreated(Text text);
    }

    /// <summary>
    /// <see cref="ITextHook"/> 的挂载点：引擎侧**所有**创建 <see cref="Text"/> 的位置
    /// （当前唯一一处 = <see cref="UIFactory.CreateText"/>，它是 Toast / Loading / 确认框 /
    /// 飘字 / 引导 / 按钮文字的唯一出口）在 Text 就位后调用 <see cref="NotifyCreated"/>。
    /// </summary>
    public static class TextHooks
    {
        private static ITextHook _current;
        private static bool _warned;

        /// <summary>
        /// 当前挂钩。<c>null</c>（默认）= 不挂钩 ⇒ 引擎维持历史行为（内置字体，见
        /// <see cref="UIFactory.DefaultFont"/>）。
        /// </summary>
        public static ITextHook Current
        {
            get { return _current; }
            set
            {
                _current = value;
                // 换挂钩（含被清空）即重置"只 Warn 一次"：业务每次注册都该看到它自己的第一条告警，
                // 否则同一进程里换过挂钩之后就再也看不到异常了（静默的隐患）。
                _warned = false;
            }
        }

        /// <summary>
        /// 引擎侧唯一入口：创建 <see cref="Text"/> 之后就位后调用一次。
        /// <para>
        /// 未注册（<see cref="Current"/> == <c>null</c>）⇒ 一次判空即返回：不分配、不打日志、
        /// 不触碰 Text ⇒ **与没有本挂钩时逐字等价**。
        /// </para>
        /// <para>
        /// 挂钩抛异常 ⇒ 吞掉 + **只 Warn 一次**（同一条错误逐 Toast 刷屏没有意义），
        /// 引擎通用件照常打开（它们只依赖 Text 本身，不依赖挂钩做了什么）。
        /// </para>
        /// </summary>
        public static void NotifyCreated(Text text)
        {
            var hook = _current;
            if (hook == null) return;

            try
            {
                hook.OnTextCreated(text);
            }
            catch (Exception ex)
            {
                if (_warned) return;
                _warned = true;
                Game.Logger?.Warn("UI",
                    "文字渲染挂钩 OnTextCreated 抛异常（已吞掉，同一挂钩只报一次）⇒ 该 Text 维持引擎内置字体，"
                    + "通用件继续可用。原因：" + ex.GetType().Name + ": " + ex.Message);
            }
        }
    }
}
