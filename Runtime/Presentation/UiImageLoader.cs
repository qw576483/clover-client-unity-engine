// ─────────────────────────────────────────────────────────────────────────────
// CloverEngine · Runtime/Presentation/UiImageLoader.cs
// UI 图**异步装载器**（请求序号守卫 + 占位保留 + 同路径去重 + 色调回填）
// ＋ **2D 光照 unlit 校验 / 还原**（纯函数判 shader 名 + 还原 Canvas 默认材质）。
//
// 出处：clover-project-diablo2 `client/Assets/Scripts/UI/UiArt.cs`
//   · `SetSprite` / `SetArtTint`（该文件 `:150-182` 的 `ArtState`、`:298-337` 的加载体）——
//     逐条照搬的语义：**每次请求自增序号、回调里只认"我这次是不是最新的"**（`state.Request != request`
//     ⇒ 丢弃）；加载**失败**保留调用方设的**占位底色**并 Warn（不静默变黑/变透明）；**同路径不重复请求**；
//     色调记在 Image 侧、由"贴图到位的那一刻"统一套用（这样"先设色后回图"与"先回图后设色"两种顺序都对）。
//   · `IsLitShader` / `EnsureUnlit`（该文件 `:914-941`）—— 判 shader 名（**纯函数**）+ 把被挂了
//     受光材质的 Image 换回 Canvas 默认 UI 材质。
//
// 为什么下沉（每个用 uGUI + 异步贴图的项目都会重踩一次）：
//   ① `Game.Res.LoadAsset` 是**异步**的（只有命中引擎缓存才同帧回调）⇒ 对**同一个 Image** 连续发起
//      A、B 两次请求时，**A 的回调可能晚于 B 到达**，画面于是停在 A（旧图）—— 表现成"点了没反应 /
//      快速悬停或逐帧换图时画面来回跳"，且**零报错零日志**；单靠调用方自觉写不出守卫（两处业务各自
//      重踩过：三态半身像靠"进屏预热"绕开、转身过渡逐帧撞上）。
//   ② 项目接了 2D 光照（URP 2D / 自定义 `*Lit*` shader）后，UI 图会被光照当场景精灵压暗，而
//      **不报任何错**（只有看图才发现）⇒ 只能靠一次 shader 名判定的防御性校验兜住。
//
// 用法 + 首个消费方：
//   ① 贴图：`img.color = 占位底色;`（调用方自己给，本件在失败时**原样保留**）
//            `UiImageLoader.SetSprite(img, "UI/Icon/Knight");`  ← 路径由调用方给，引擎不认任何素材名
//            `UiImageLoader.SetTint(img, Color.gray);`          ← 贴图未到时先记下，到位那一刻统一套用
//   ② unlit：`UiImageLoader.EnsureUnlit(img);`（建件/换材质之后调一次即可；纯判据 `IsLitShader` 可离线断言）
//   首个消费方：clover-project-diablo2 `client/Assets/Scripts/UI/UiArt.cs`（`SetSprite` / `EnsureUnlit`
//   转发到本件）—— **接线由项目侧收尾片统一做，本件不碰任何项目文件**。
//
// 边界（⛔ 别当万能药用）：
//   · 引擎**不知道**任何项目的资源路径 / 占位配色 / 字号 / 图集划分 ⇒ 路径与占位色一律由调用方给；
//     本件只在**加载成功**时覆盖 `img.color`（= 色调），失败与在途期间**一个像素都不动**。
//   · "同路径去重"只覆盖**在同一 Image 上**的重复请求；跨 Image 的同路径并发由
//     `IResourceManager.LoadAsset` 内部的 `JoinOrCreatePending` 合并（引擎既有行为，本件不重复造）。
//   · 主线程使用（与 `LogThrottle` 一致，非线程安全）；状态存 `ConditionalWeakTable`，Image 销毁后
//     条目随 GC 消失 ⇒ **不泄漏、也不需要业务记得清理**。
//   · `IsLitShader` 判的是**名字**：项目自写的受光 shader 若名字里不带 `lit`，本件判不出来（判据明写在这里，
//     ⛔ 不猜、不做"shader 属性探测"）。
// ─────────────────────────────────────────────────────────────────────────────

using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.UI;

namespace CloverEngine
{
    /// <summary>
    /// UI 图**异步装载器** + **unlit 材质校验**（两件都是 uGUI 底座的"静默失败"防治件）。
    /// <para>
    /// 装载侧：<see cref="SetSprite"/> 给同一个 <see cref="Image"/> 上的每次请求发一个**自增序号**，
    /// 回调里只认"我这次是不是最新的" ⇒ <c>Game.Res.LoadAsset</c> 异步回调**乱序**时旧回调被丢弃
    /// （不会盖掉新图），失败保留占位底色并留痕，同路径不重复请求；色调经 <see cref="SetTint"/> 记下、
    /// 在贴图到位那一刻统一套用。
    /// </para>
    /// <para>
    /// 材质侧：<see cref="IsLitShader(string)"/> 是**纯函数**（离线宿主可直接断言）；
    /// <see cref="EnsureUnlit(Image)"/> 是防御性校验 —— 只有发现 Image 被挂了受光材质才把它换回
    /// Canvas 默认 UI 材质（unlit），并留痕（不静默）。
    /// </para>
    /// <para><b>不变量</b>：本件**只在加载成功时**写 <c>img.sprite</c> / <c>img.color</c>；
    /// 在途与失败期间调用方设的占位底色保持不变（"占位可见"优于"静默变黑/变透明"）。</para>
    /// </summary>
    public static class UiImageLoader
    {
        /// <summary>日志 tag（与 UI 通用件同域，便于按 <c>[UiImage]</c> 检索）。</summary>
        private const string Tag = "UiImage";

        /// <summary>
        /// 一个 Image 的贴图请求状态（键 = Image，存 <see cref="ConditionalWeakTable{TKey,TValue}"/>）。
        /// </summary>
        private sealed class ArtState
        {
            /// <summary>最近一次请求的路径（同路径去重的依据）。</summary>
            public string Path;

            /// <summary>贴图到位后要套的色调；默认 <see cref="Color.white"/> = 不做色调乘法（原样显示）。</summary>
            public Color Tint = Color.white;

            /// <summary>本 Image 上已发起的请求序号（每次 <see cref="SetSprite"/> 自增；回调比对它）。</summary>
            public int Request;

            /// <summary>最近一次请求的路径是否**仍在途**（在途 ⇒ 同路径重复调用直接返回）。</summary>
            public bool Pending;

            /// <summary>最近一次请求的路径的贴图是否**已成功落地**（成功 ⇒ 同路径重复调用直接返回）。</summary>
            public bool Applied;
        }

        private static readonly ConditionalWeakTable<Image, ArtState> States =
            new ConditionalWeakTable<Image, ArtState>();

        private static ArtState StateOf(Image img) => States.GetOrCreateValue(img);

        // ── 贴图装载 ───────────────────────────────────────────────────────────

        /// <summary>
        /// 异步把 <paramref name="path"/> 处的 <see cref="Sprite"/> 贴到 <paramref name="img"/> 上。
        /// <para>
        /// <b>★ 请求守卫（本方法存在的首要理由）</b>：每次调用自增本 Image 的请求序号，回调里比对
        /// 「我这次是不是最新的」，**过期的直接丢弃**（连 Warn 都不打 —— 那是设计内行为）。
        /// 根因见文件头「为什么下沉 ①」。
        /// </para>
        /// <para>
        /// <b>失败保留占位</b>：回调拿到 <c>null</c> ⇒ 只打一条**限频** Warn（点名路径 + 当前底色），
        /// <c>img.sprite</c> / <c>img.color</c> **都不动** ⇒ 调用方设的纯色占位继续可见，不静默变黑/变透明。
        /// </para>
        /// <para>
        /// <b>同路径去重</b>：同一 Image 上「该路径仍在途」或「该路径已成功」⇒ 直接返回（不重复发起、
        /// 不重复回调）。⚠️ **失败过**的路径不在此列 —— 调用方再调一次会重试，
        /// ⛔ 不许把"素材当时没同步好"永久钉成占位。
        /// </para>
        /// <para>
        /// <b>语义</b>：同一个 Image 上「**后发起的请求胜出**」。所有调用点都是"贴当前该显示的那张图"，
        /// 与"回调恰好按序到达"时的行为逐字相同；只在乱序时把错态修成最新态。
        /// </para>
        /// </summary>
        /// <param name="img">目标 Image（为 <c>null</c> 或已销毁 ⇒ 直接返回）。</param>
        /// <param name="path">资源路径（由调用方给；空 / <c>null</c> ⇒ 直接返回）。</param>
        /// <param name="tint">贴图到位后要套的色调；不传 = 沿用 <see cref="SetTint"/> 记下的（默认白）。</param>
        public static void SetSprite(Image img, string path, Color? tint = null)
        {
            if (img == null || string.IsNullOrEmpty(path)) return;

            var state = StateOf(img);
            if (tint.HasValue) state.Tint = tint.Value;

            // 同路径去重（在途 / 已成功）；失败过的路径不拦 ⇒ 再调一次就是重试。
            if (state.Path == path && (state.Pending || state.Applied)) return;

            if (Game.Res == null)
            {
                // 非预期分支：资源模块没初始化（CloverRes.Init 未调用）⇒ 保留占位并只报一次。
                LogThrottle.WarnOnce(Tag, "res.null",
                    $"Game.Res 未初始化（CloverRes.Init 未调用）⇒ 贴图 {path} 无法加载，" +
                    $"Image {img.name} 保留占位底色 {img.color}");
                return;
            }

            state.Path = path;
            state.Pending = true;
            state.Applied = false;
            var request = ++state.Request;   // ★ 请求守卫：本次请求的序号（回调里比对）

            Game.Res.LoadAsset<Sprite>(path, sp =>
            {
                if (img == null) return;                  // 面板已关闭销毁
                if (state.Request != request) return;     // ★ 已被更新的请求取代 ⇒ 丢弃（设计内行为，不告警）
                state.Pending = false;

                if (sp == null)
                {
                    // 非预期分支：素材缺失 / 路径写错 ⇒ 保留纯色占位 + 点名路径（限频：避免逐帧刷屏）。
                    LogThrottle.WarnThrottled(Tag, "sprite.missing:" + path,
                        $"贴图缺失：{path}（Image {img.name} 保留占位底色 {img.color}，按占位显示）", 30f);
                    return;
                }

                state.Applied = true;
                img.sprite = sp;
                img.color = state.Tint;   // 色调在"到位那一刻"套用 ⇒ 先设色后回图 / 先回图后设色 两种顺序都对
                EnsureUnlit(img);         // 防御 2D 光照把 UI 图压暗（见 EnsureUnlit）
            });
        }

        /// <summary>
        /// 记录 / 立即应用一个 Image 的色调（选中与未选中、空槽底、禁用灰化…）。
        /// 贴图**已在** ⇒ 立即写入 <c>img.color</c>；**未到** ⇒ 记下，由 <see cref="SetSprite"/> 的回调
        /// 在贴图到位那一刻套用（不丢状态、也不会把"已经设好的颜色"冲掉）。
        /// </summary>
        public static void SetTint(Image img, Color tint)
        {
            if (img == null) return;
            StateOf(img).Tint = tint;
            if (img.sprite != null) img.color = tint;   // 贴图已在 ⇒ 立即生效
        }

        /// <summary>最近一次请求的路径（没有则 <c>null</c>）。供调用方 / 离线宿主断言用。</summary>
        public static string RequestedPath(Image img) => img == null ? null : StateOf(img).Path;

        /// <summary>最近一次请求是否仍在途（供断言 / 调试用）。</summary>
        public static bool IsPending(Image img) => img != null && StateOf(img).Pending;

        /// <summary>最近一次请求的贴图是否已成功落地（失败与在途都为 <c>false</c>；供断言用）。</summary>
        public static bool IsLoaded(Image img) => img != null && StateOf(img).Applied;

        // ── 2D 光照 / unlit 校验 ────────────────────────────────────────────────

        /// <summary>
        /// 判断某 shader **名**是否会让 UI 图受 2D 光照影响（⇒ 必须换掉）。
        /// <para>
        /// **纯函数**（离线宿主可直接断言，无 Unity 依赖）：含 <c>unlit</c>
        /// （<c>Sprites/Default</c> / <c>Unlit/Color</c> / <c>Sprite-Unlit-Default</c>）⇒ **不受光**，返回 false；
        /// 否则出现 <c>lit</c>（<c>Universal Render Pipeline/2D/Sprite-Lit-Default</c> 等）⇒ 受光，返回 true。
        /// </para>
        /// <para>顺序是刻意的：**必须先判 <c>unlit</c>** —— <c>unlit</c> 这个单词里也含 <c>lit</c>，
        /// 反了就 <c>Sprites/Default</c> 会被误判成受光。</para>
        /// </summary>
        public static bool IsLitShader(string shaderName)
        {
            if (string.IsNullOrEmpty(shaderName)) return false;
            var n = shaderName.ToLowerInvariant();
            if (n.Contains("unlit")) return false;   // 先判 unlit：`unlit` 里也含 `lit`
            return n.Contains("lit");
        }

        /// <summary>同 <see cref="IsLitShader(string)"/>，入参为 <see cref="Shader"/>（为 <c>null</c> ⇒ false）。</summary>
        public static bool IsLitShader(Shader shader) => shader != null && IsLitShader(shader.name);

        /// <summary>同 <see cref="IsLitShader(string)"/>，入参为 <see cref="Material"/>（为 <c>null</c> ⇒ false）。</summary>
        public static bool IsLitShader(Material material) => material != null && IsLitShader(material.shader);

        /// <summary>
        /// 确保这个 <see cref="Image"/> 走**不受 2D 光照影响**的材质。
        /// <para>
        /// 引擎 Canvas 是 <c>RenderMode.ScreenSpaceOverlay</c>（<c>Runtime/Presentation/UI.cs</c>），
        /// <c>Image.material == null</c> 时走 Canvas 默认的 <c>UI/Default</c>（unlit）——
        /// 这是**最常见路径，本方法一个字段都不碰**。只有发现 Image 被挂了受光材质才动手：
        /// 换回 Canvas 默认材质（<c>material = null</c>）并留痕 —— ⛔ 不静默
        /// （受光材质不会报错，只会让 UI 图随 2D 光照变暗，只有看图才发现）。
        /// </para>
        /// </summary>
        public static void EnsureUnlit(Image img) => EnsureUnlit((Graphic)img);

        /// <summary>
        /// 同 <see cref="EnsureUnlit(Image)"/>，入参放宽到 <see cref="Graphic"/>（<c>Text</c> / <c>RawImage</c>
        /// 等同样会被 2D 光照压暗）。
        /// </summary>
        public static void EnsureUnlit(Graphic graphic)
        {
            if (graphic == null) return;

            var mat = graphic.material;
            if (mat == null) return;                          // 最常见路径：Canvas 默认材质（unlit），无需处理
            if (!IsLitShader(mat)) return;

            // 非预期分支（受光材质会静默压暗 UI）：换回 UI 默认材质 + 留痕。限频：一次事故里往往整屏都是。
            LogThrottle.WarnThrottled(Tag, "material.lit",
                $"{graphic.GetType().Name} {graphic.name} 的材质 {mat.shader?.name} 受 2D 光照影响（会压暗 UI）" +
                " ⇒ 已改回 Canvas 默认 UI 材质（unlit）", 5f);
            graphic.material = null;
        }
    }
}
