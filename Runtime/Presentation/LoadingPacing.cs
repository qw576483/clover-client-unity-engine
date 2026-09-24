// ─────────────────────────────────────────────────────────────────────────────
// CloverEngine · Runtime/Presentation/LoadingPacing.cs
// 读条屏的**分档 / 节奏纯函数**：档 ↔ completeness、按时间放行到第几档、引擎进度 → 档号。
//
// 边界：
//   收在本件的是「N 档读条图，逐档绑一个真实里程碑；进度不能假（只能用真里程碑前移），但每档要有最短
//   可见时间，让 10 帧动画真的看得见；引擎场景加载进度只占前几档」这三条**纯算术**
//   （不问业务状态、不碰 Unity 对象）；
//   **档数、每档时长、哪一档对应哪个里程碑全留业务侧**（本件只吃参数）。
//
// 口径（调用方要抄的）：
//   ① **进度不许假**：档位只能由真里程碑前移 ⇒ 呈现档位 = `min(真实档位, 放行档位)`；
//      本件**只**给"按时间放行到第几档"（`MaxIndexAt`）与"引擎真进度 → 档号"
//      （`SceneLoadFrameIndex`）两个下限/上限，⛔ 不做任何"随时间自增"。
//   ② `CompletenessOf` 取**区间中点**而非端点：档号是 `(int)((count-1) * c)`，用端点值时浮点误差
//      会把第 2 档（c = 0.22222222 × 9 = 1.9999999）算成第 1 档；中点距两端各半个档宽 ⇒ 浮点安全。
//   ③ `SceneLoadFrameIndex` 里的 `engineCeiling = 0.9` 是引擎场景加载的回调上限
//      （`Runtime/Presentation/Scene.cs` 的 `op.progress >= 0.9f` 门控）：`[0, 0.9]` 只映射到
//      前 `progressShare`（默认 0.5 = 原版 `Show(0.5f)` 的位置）那段门 —— 否则"场景一加载完
//      读条屏就到最后一帧"。
//   ④ 无状态 / 纯函数（可离线逐条断言）；本件不依赖任何项目类型。
//   ⑤ ⚠️ 本公式须与任何自带同一口径的消费方保持同步（`(int)((frameCount-1) * clamp01(c))` + 钳位）。
//
// 用法：
//   var c   = LoadingPacing.CompletenessOf(index, count);                  // 档号 → [0,1]
//   var cap = LoadingPacing.MaxIndexAt(elapsedSeconds, cadenceSeconds, count);   // 按时间放行上限
//   var idx = LoadingPacing.SceneLoadFrameIndex(progress, count, loadedFrame);   // 引擎进度 → 档号
//   var f   = LoadingPacing.FrameIndex(completeness, frameCount);          // [0,1] → 档号（钳位）
// ─────────────────────────────────────────────────────────────────────────────

using System;

namespace CloverEngine
{
    /// <summary>
    /// 读条分档 / 节奏的纯函数集（无状态、可离线断言；口径见文件头）。
    /// </summary>
    public static class LoadingPacing
    {
        /// <summary>
        /// 引擎场景加载的回调进度上限（`Runtime/Presentation/Scene.cs` 的 `op.progress >= 0.9f` 门控）。
        /// </summary>
        public const float SceneProgressCeiling = 0.9f;

        /// <summary>
        /// 引擎场景加载进度在整条读条门里占的比例（0.5 = 映射到原版 `Show(0.5f)` 的位置）。
        /// </summary>
        public const float DefaultProgressShare = 0.5f;

        /// <summary>
        /// `[0,1]` 的 completeness → 档号（**钳位**，<paramref name="frameCount"/> ≤ 0 ⇒ 0）。
        /// <para>公式 = `(int)((frameCount - 1) * clamp01(completeness))`。</para>
        /// </summary>
        public static int FrameIndex(float completeness, int frameCount)
        {
            if (frameCount <= 0) return 0;
            var c = completeness <= 0f ? 0f : (completeness >= 1f ? 1f : completeness);
            var idx = (int)((frameCount - 1) * c);
            return idx < 0 ? 0 : (idx >= frameCount ? frameCount - 1 : idx);
        }

        /// <summary>
        /// 档号 → 交给读条面板的 completeness（原版语义 `[0,1]`）。
        /// <para>取**区间中点** `(index + 0.5) / (count - 1)`（理由见文件头 ②）；档号越界夹到两端；
        /// `count &lt;= 1` ⇒ 1（只有一档时它必然是"满"）。</para>
        /// </summary>
        public static float CompletenessOf(int index, int count)
        {
            if (count <= 1) return 1f;
            if (index < 0) index = 0;
            if (index >= count) return 1f;

            var c = (index + 0.5f) / (count - 1);
            return c > 1f ? 1f : c;
        }

        /// <summary>
        /// **节奏放行**：读条屏已显示 <paramref name="elapsedSeconds"/> 秒时，最多允许开到第几档。
        /// <para>纯函数：`elapsed ≤ 0`（或 NaN）⇒ 0；每 <paramref name="cadenceSeconds"/> 秒放行一档；
        /// 上限 = <paramref name="count"/> - 1。`cadenceSeconds ≤ 0` 或 `count ≤ 0` ⇒ 0（无法放行）。</para>
        /// </summary>
        public static int MaxIndexAt(double elapsedSeconds, float cadenceSeconds, int count)
        {
            if (count <= 0) return 0;
            if (!(cadenceSeconds > 0f)) return 0;
            if (elapsedSeconds <= 0d || double.IsNaN(elapsedSeconds)) return 0;

            var n = (int)(elapsedSeconds / cadenceSeconds);
            return n >= count ? count - 1 : n;
        }

        /// <summary>
        /// 引擎场景加载真进度 → 门里的档号（**不超过** <paramref name="loadedFrame"/>）。
        /// <para>值域依据：引擎在 `allowSceneActivation = false` 期间回调，`op.progress` 上限 =
        /// <see cref="SceneProgressCeiling"/> ⇒ 把 `[0, ceiling]` 线性映射到
        /// `[0, progressShare]`（默认一半的门，= 原版 `Show(0.5f)` 的位置）。</para>
        /// <para>NaN / ≤ 0 ⇒ 0；超过 <paramref name="loadedFrame"/> ⇒ 夹到它；
        /// <paramref name="loadedFrame"/> 自身夹进 `[0, count-1]`。</para>
        /// </summary>
        public static int SceneLoadFrameIndex(float engineProgress, int count, int loadedFrame,
            float engineCeiling = SceneProgressCeiling, float progressShare = DefaultProgressShare)
        {
            if (count <= 0) return 0;

            var top = loadedFrame < 0 ? 0 : (loadedFrame >= count ? count - 1 : loadedFrame);
            if (float.IsNaN(engineProgress)) return 0;

            if (!(engineCeiling > 0f)) return 0;
            var p = engineProgress <= 0f ? 0f : (engineProgress >= engineCeiling ? 1f : engineProgress / engineCeiling);
            if (!(progressShare > 0f)) return 0;

            var completeness = progressShare * p;
            var idx = FrameIndex(completeness, count);
            return idx > top ? top : idx;
        }
    }
}
