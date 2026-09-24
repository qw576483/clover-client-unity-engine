// ─────────────────────────────────────────────────────────────────────────────
// CloverEngine · Runtime/Presentation/CameraBoundsKit.cs
// 相机边界夹制（**格空间**，⛔ 不是世界 AABB）的纯函数（⛔ 不留第二份实现）。
//
// 为什么必须夹到格空间：
//   把焦点夹进「等距菱形的轴对齐包围盒 AABB − 半屏」是**错的口径**：菱形与它的 AABB 之间
//   那**四个三角区**在世界里根本没有对应格子（= 地图外虚空），而 AABB 夹制把它们当成"图内"
//   ⇒ 机位落在 AABB 内即被放行。实测（80×80 地图、落点 (-19,-11)）屏幕上 31.3% 是虚空，
//   独立像素量法 blackFrac = 0.340 互相印证。
//   口径 = 夹制在**格空间**里做：把「可见格矩形」夹进 [0..W]×[0..H]（连续格坐标口径）。
//
// 口径（连续格坐标：整数 = **格线**；格子 g 覆盖 [g, g+1)，故地图的真实连续范围是 [0,W]×[0,H]）：
//   旋转到菱形自己的两条轴（屏幕矩形在这个坐标系里是**轴对齐**的 ⇒ 两向可独立夹制）：
//        u = gx − gy =  X / isoHalfW          v = gx + gy = −Y / isoHalfH
//        X = u · isoHalfW                     Y = −v · isoHalfH
//   半屏在 (u,v) 框里的半跨：a = halfW / isoHalfW ，b = halfH / isoHalfH
//   ⇒ 可见格矩形 = { |Δu| ≤ a, |Δv| ≤ b }（一个菱形）；它全部落在地图内 ⟺
//        s1 = u + v ∈ [a+b, 2W − (a+b)]      （s1 = 2·gx ∈ [0, 2W]）
//        s2 = v − u ∈ [a+b, 2H − (a+b)]      （s2 = 2·gy ∈ [0, 2H]）
//   ⛔ 上界用 2·W / 2·H（连续格口径），**不是** 2·(W−1)：取 (W−1) 等于把最外一圈格当成图外，
//      白白吃掉一整格可跟随范围（实测 56×40 城镇里 = 少 1 格 ≈ 144px@1080p）。
//
// ── ⚠️ 一条**硬约束**：主角必须留在视口内 ──────────────────────────────────────
//   把菱形整个塞进地图所需要的位移，可能大于"主角还在画面里"允许的位移 ——
//   在**地图角格**上两者**数学上不可兼得**（证明：两条约束相加得 v ≥ a+b，而主角可见要求
//   |Δv| ≤ b，角格处 a+b > 2b 时无解）。此时**让位给主角可见**（地图角落那点虚空由地图
//   边界块的美术去盖，属地图模块的事、⛔ 不是相机该解决的）。
//   判据：被钳制 ⇒ 焦点仍在视口内（离线宿主断言）。
//
// ⛔ **参数化**：`isoHalfW/isoHalfH`（项目侧 = `Iso.HalfW/HalfH`）与 `focusSafeMarginRatio`
//   （项目侧 = `CameraBounds.FocusSafeMarginRatio`）由调用方传入 —— 引擎不持项目常量、不引项目类型。
// ─────────────────────────────────────────────────────────────────────────────

using UnityEngine;

namespace CloverEngine
{
    /// <summary>
    /// 相机边界夹制的纯函数（**格空间**，⛔ 不是世界 AABB）：无副作用、不碰 `Game` / 场景，
    /// 离线宿主可直接链本件调生产实现（⛔ 不在探针里镜像一份"看起来一样"的公式 —— 镜像 =
    /// 改了生产也不变红的假闸门）。
    /// </summary>
    public static class CameraBoundsKit
    {
        /// <summary>
        /// 把**机位**夹到「可见**格**矩形 ⊆ 地图」，并保证**焦点仍在视口安全边距内**。
        ///
        /// <para><b>夹的是机位、⛔ 不是焦点</b>：焦点只作为「机位最多能挪多远」的参照。
        /// 把两者当同一个量的话，"藏虚空"的位移会被当成"焦点被夹"⇒ 玩家被顶到画面角落
        /// （实测 p50 598.6px / max 1065.8px 偏离屏幕中心）。</para>
        /// </summary>
        /// <param name="camera">**机位**世界坐标（被夹的那个量）。</param>
        /// <param name="focus">**焦点**（玩家）世界坐标 —— 只用于限定位移上限，本身不被改写。</param>
        /// <param name="mapWidth">地图宽（格）。</param>
        /// <param name="mapHeight">地图高（格）。</param>
        /// <param name="halfW">半屏宽（世界单位 = 正交尺寸 × aspect）。</param>
        /// <param name="halfH">半屏高（世界单位 = 正交尺寸）。</param>
        /// <param name="isoHalfW">等距半格宽（世界单位）。</param>
        /// <param name="isoHalfH">等距半格高（世界单位）。</param>
        /// <param name="focusSafeMarginRatio">「主角可见」的安全边距（占半屏的比例，见类注释硬约束段）。</param>
        /// <returns>夹制后的机位世界坐标（xy）。</returns>
        public static Vector2 ClampCameraGrid(Vector2 camera, Vector2 focus, int mapWidth, int mapHeight,
            float halfW, float halfH, float isoHalfW, float isoHalfH, float focusSafeMarginRatio)
        {
            if (mapWidth <= 0 || mapHeight <= 0 || halfW <= 0f || halfH <= 0f || isoHalfW <= 0f || isoHalfH <= 0f)
                return camera;

            var a = halfW / isoHalfW;                 // 半屏在 u 方向的半跨（格）
            var b = halfH / isoHalfH;                 // 半屏在 v 方向的半跨（格）
            var margin = a + b;                       // 菱形顶点到中心在两个方向上的合计跨度

            var u = camera.x / isoHalfW;              // **机位**在菱形轴系里的坐标
            var v = -camera.y / isoHalfH;

            var s1 = ClampSpan(u + v, margin, 2f * mapWidth - margin);          // = 2·gx
            var s2 = ClampSpan(v - u, margin, 2f * mapHeight - margin);         // = 2·gy

            var u2 = (s1 - s2) * 0.5f;
            var v2 = (s1 + s2) * 0.5f;

            // ★ 主角可见预算：位移是相对**焦点**（玩家）量的，⛔ 不是相对机位自己
            //   （相对机位自己量的话，机位已经在边缘上 ⇒ 预算被自己吃掉，玩家一路被顶到画面角上）。
            var uf = focus.x / isoHalfW;
            var vf = -focus.y / isoHalfH;
            var au = a * focusSafeMarginRatio;
            var bv = b * focusSafeMarginRatio;
            var uc = Mathf.Clamp(u2, uf - au, uf + au);
            var vc = Mathf.Clamp(v2, vf - bv, vf + bv);
            if (Mathf.Abs(uc - u2) > 1e-4f || Mathf.Abs(vc - v2) > 1e-4f)
            {
                // 非预期分支（地图角格处两条约束不可兼得）⇒ **必须留痕**；只报一次，避免每帧刷屏。
                LogThrottle.WarnOnce("Camera", "cameraBounds.visibilityWon",
                    $"地图角格处「零虚空」与「主角可见（安全边距 {focusSafeMarginRatio:0.##}）」" +
                    $"不可兼得 ⇒ 本次让位给主角可见（机位 ({uc * isoHalfW:0.##},{-vc * isoHalfH:0.##})，" +
                    $"零虚空解为 ({u2 * isoHalfW:0.##},{-v2 * isoHalfH:0.##})，焦点 ({focus.x:0.##},{focus.y:0.##})）");
                u2 = uc;
                v2 = vc;
            }

            return new Vector2(u2 * isoHalfW, -v2 * isoHalfH);
        }

        /// <summary>
        /// 兼容口径：**机位与焦点同一处**（= 相机想停在玩家身上的理想情形）。
        /// 只用单点的调用方（离线宿主的用例）走这个入口，语义 = <see cref="ClampCameraGrid"/> 传同一个点。
        /// </summary>
        public static Vector2 ClampFocusGrid(Vector2 focus, int mapWidth, int mapHeight,
            float halfW, float halfH, float isoHalfW, float isoHalfH, float focusSafeMarginRatio)
        {
            return ClampCameraGrid(focus, focus, mapWidth, mapHeight, halfW, halfH,
                isoHalfW, isoHalfH, focusSafeMarginRatio);
        }

        /// <summary>
        /// 把 <c>[lo, hi]</c> 这段可行区间夹出来；**区间为空**（视野比地图还大）⇒ 居中（原版语义：
        /// 地图小于一屏时相机停在地图中心，⛔ 不是贴某一边）。
        /// </summary>
        public static float ClampSpan(float s, float lo, float hi)
        {
            if (hi <= lo) return (lo + hi) * 0.5f;
            return Mathf.Clamp(s, lo, hi);
        }
    }
}
