using System;
using UnityEngine;

namespace CloverEngine
{
    /// <summary>
    /// **帧节奏**（帧率上限 / 垂直同步）的引擎件 —— 只做「把这两个原生设置钉死并读回校验」，
    /// ⛔ **不碰任何画质内容**（阴影 / 分辨率缩放 / LOD / 贴图限制属 <see cref="Quality"/>）。
    ///
    /// <para><b>为什么要有它（下沉记录）</b>：能力下沉。原来全工程只有业务侧
    /// <c>clover-project-diablo2/client/Assets/Scripts/Core/FramePacing.cs</c> 会写
    /// <c>Application.targetFrameRate</c>（引擎里唯一的另一个写点是 <c>Quality.cs:179-181</c>，
    /// 由 <c>Game.Quality.SetLevel</c> 触发）⇒ 业务为了一件通用事自己持有原生调用。
    /// 本件把「读 / 写 / 读回校验 / 失败原因」收进引擎，业务侧只调用、⛔ 不留第二份
    /// <c>Application.targetFrameRate =</c> 写入。</para>
    ///
    /// <para><b>为什么返回 bool + out error 而不是抛异常</b>：离线自检宿主
    /// （<c>tools/probes/hosts/*</c>，非 Unity 进程）里这两个原生 API 会抛
    /// <c>UnityException</c>，而"宿主里失败"是**正常现象**，调用方要能留一条日志继续跑，
    /// 不是崩溃。所以本件把异常收成 <c>false + error</c>，由调用方决定怎么留痕。</para>
    ///
    /// <para><b>口径（调用方要抄的）</b>：帧率上限恒定（与画质档位无关）+ 垂直同步关闭
    /// —— <c>vSyncCount &gt; 0</c> 时 <c>targetFrameRate</c> 会被平台忽略，两个必须一起写。
    /// ⚠️ <c>QualitySettings.SetQualityLevel</c> 会**按档位重置 <c>vSyncCount</c>** ⇒
    /// 每次改画质档位之后都要重新 <see cref="Pin"/>（<c>Quality.cs</c> 的
    /// <c>SetLevel</c> 也会写 <c>vSyncCount = 0</c>，两者方向一致、不冲突）。</para>
    ///
    /// <para><b>★ 为什么"vSync 关、硬性 60fps"会让画面抖 —— 真机 A/B 实测（片 g2-resume）</b>：
    /// 位置是 <c>f(t)</c> 的光滑函数（移动按 <c>dt</c> 积分）⇒ 每帧推进量 = <c>v·dt</c>，
    /// **帧间隔不匀就直接变成画面推进不匀**。实测（`clover-project-diablo2`，`camjitter_drive` 逐帧 TSV，
    /// 同一段 6 折路径、同一把量法 `camjitter_who.py`）：
    /// ① 显示器 **100 Hz**（`Screen.currentResolution.refreshRateRatio`），配置 = 60/0 ⇒
    ///    <c>dt</c> 8.5~62.6 ms（sd 5.0 ms，均值 17.3 ms）；相机（世界滚动）**纵向**每帧推进量
    ///    sd = **2.97 px**（p95 4.6 px、速度 sd 136 px/s ÷ 标称 445 px/s = **31%**），
    ///    且 <c>corr(纵向偏差, dt−均值) = 0.923</c> ⇒ **不匀就是帧时间造成的**（不是代码）；
    /// ② 跟随链路本身**已经干净**（角色相对相机的横向 sd <b>0.19 px</b>）；
    /// ③ 60 fps 落在 100 Hz 面板上每帧占 1.67 个刷新周期 ⇒ 呈现节拍本身也不整（60 与 100 不整除）。
    /// ⇒ 结论：帧节奏必须**与显示器对齐**，而刷新率只有引擎能可靠读到 ⇒ 策略放这里，
    /// 由 <see cref="RecommendVSyncCount(float)"/> 给档位（刷新率可读 ⇒ <c>vSync=1</c>，
    /// 帧交付锁到刷新率 ⇒ 帧间隔恒定）。<b>vSync 开启时平台忽略 targetFrameRate 属预期</b>。</para>
    /// </summary>
    public static class FramePacingPolicy
    {
        /// <summary>
        /// 引擎建议的帧率上限（60）。为什么不是原版逻辑帧 25：逻辑按 <c>dt</c> 积分，帧率只影响采样密度。
        /// <para>⚠️ 它是**刷新率读不到时的兜底上限**；刷新率可读时实际节奏由
        /// <see cref="RecommendVSyncCount(float)"/> 决定（vSync=1 ⇒ 本值被平台忽略，属预期）。</para>
        /// </summary>
        public const int DefaultTargetFrameRate = 60;

        /// <summary>
        /// 引擎建议的垂直同步档位（**兜底值**，0 = 关；只在刷新率读不到时使用）。
        /// <para>刷新率可读时的推荐值见 <see cref="RecommendVSyncCount(float)"/>（= 1）。</para>
        /// </summary>
        public const int DefaultVSyncCount = 0;

        /// <summary>
        /// **由显示器刷新率决定垂直同步档位**（纯函数，离线可断言；A/B 实测依据见类注释 ★）：
        /// <list type="bullet">
        /// <item>刷新率可读（&gt; 0）⇒ <b>1</b>：帧交付锁到刷新率 ⇒ 帧间隔恒定（100 Hz 面板 ⇒ 10.0 ms），
        /// 世界滚动与角色位移的每帧推进量因此恒定；</item>
        /// <item>刷新率读不到（&lt;= 0：无头 / 离线宿主 / 平台不提供）⇒ <see cref="DefaultVSyncCount"/>（0）
        /// ⇒ 退回"帧率上限"口径（<see cref="DefaultTargetFrameRate"/>）。</item>
        /// </list>
        /// </summary>
        public static int RecommendVSyncCount(float refreshHz)
            => refreshHz > 0f ? 1 : DefaultVSyncCount;

        /// <summary>
        /// 读当前显示器的刷新率（Hz）。原生 API 不可用（离线宿主 / 无头）或平台返回 0 时返回 <c>0</c>
        /// 并**不抛异常**（调用方据此走兜底口径）。
        /// </summary>
        public static float TryReadRefreshHz()
        {
            try { return Screen.currentResolution.refreshRateRatio.value; }
            catch (Exception) { return 0f; }
        }

        /// <summary>
        /// 引擎推荐的帧节奏（一次读全）：<paramref name="refreshHz"/> = 当前刷新率（读不到为 0）、
        /// <paramref name="vSyncCount"/> = <see cref="RecommendVSyncCount(float)"/>、
        /// <paramref name="targetFrameRate"/> = <see cref="DefaultTargetFrameRate"/>（vSync=1 时被平台忽略）。
        /// </summary>
        /// <returns>刷新率是否可读（<c>false</c> ⇒ 调用方应把返回的档位当兜底口径）。</returns>
        public static bool Recommend(out int targetFrameRate, out int vSyncCount, out float refreshHz)
        {
            refreshHz = TryReadRefreshHz();
            vSyncCount = RecommendVSyncCount(refreshHz);
            targetFrameRate = DefaultTargetFrameRate;
            return refreshHz > 0f;
        }

        /// <summary>
        /// 把帧节奏钉死：写 <c>Application.targetFrameRate</c> 与 <c>QualitySettings.vSyncCount</c>，
        /// 随后**读回校验**（原生 API 可能被平台改写）。
        /// <para>返回 <c>true</c> = 写入且读回一致；<c>false</c> = 抛异常（<paramref name="error"/> 为原因）
        /// 或读回值不等于目标（此时 <paramref name="readBackFps"/> / <paramref name="readBackVSync"/>
        /// 给出**实际**读回值，调用方据此判"未生效"）。</para>
        /// </summary>
        public static bool Pin(int targetFrameRate, int vSyncCount,
            out int readBackFps, out int readBackVSync, out string error)
        {
            readBackFps = 0;
            readBackVSync = 0;
            error = null;
            try
            {
                Application.targetFrameRate = targetFrameRate;
                QualitySettings.vSyncCount = vSyncCount;

                readBackFps = Application.targetFrameRate;
                readBackVSync = QualitySettings.vSyncCount;
                return readBackFps == targetFrameRate && readBackVSync == vSyncCount;
            }
            catch (Exception e)
            {
                error = e.GetType().Name + ": " + e.Message;
                return false;
            }
        }

        /// <summary>
        /// 只读当前帧节奏（不改任何值）。原生 API 不可用（离线宿主）时返回 false 并给出原因。
        /// 用途：调用方要在 <see cref="Pin"/> 前后对比"是否真的被改过"。
        /// </summary>
        public static bool TryRead(out int fps, out int vSyncCount, out string error)
        {
            fps = 0;
            vSyncCount = 0;
            error = null;
            try
            {
                fps = Application.targetFrameRate;
                vSyncCount = QualitySettings.vSyncCount;
                return true;
            }
            catch (Exception e)
            {
                error = e.GetType().Name + ": " + e.Message;
                return false;
            }
        }

        /// <summary>
        /// 生效口径的**单行文本**（不含日志级别 / 不含业务文案 —— 业务在自己那层加前缀与自己的口径）。
        /// </summary>
        /// <param name="targetFrameRate">目标帧率上限。</param>
        /// <param name="vSyncCount">目标垂直同步档位。</param>
        /// <param name="reason">为什么重钉（"启动" / "改画质档位 2" / "自检宿主"…）。</param>
        /// <param name="beforeFps">改前读到的帧率上限。</param>
        /// <param name="beforeVSync">改前读到的垂直同步档位。</param>
        public static string Describe(int targetFrameRate, int vSyncCount, string reason,
            int beforeFps, int beforeVSync)
        {
            return $"帧节奏已钉死：targetFrameRate={targetFrameRate} vSyncCount={vSyncCount}" +
                   $"（原因={reason}；改前 targetFrameRate={beforeFps} vSyncCount={beforeVSync}）" +
                   "—— 口径 = 帧率上限恒定、与画质档位无关；" +
                   "vSyncCount>0 时平台会忽略 targetFrameRate ⇒ 两个必须一起写；" +
                   "QualitySettings.SetQualityLevel 会按档位重置 vSyncCount ⇒ 每次改画质档位后必须重新 Pin。";
        }
    }
}
