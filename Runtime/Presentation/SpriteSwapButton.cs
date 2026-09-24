// ─────────────────────────────────────────────────────────────────────────────
// CloverEngine · Runtime/Presentation/SpriteSwapButton.cs
// 通用 **sprite-swap 按钮**工厂：常态 / 悬停 / 按下 / 禁用各一张 sprite，尺寸 / 位置 / 文案 / 色调
// 全部由调用方给（⛔ 引擎不含任何项目配色 / 尺寸 / 素材名）。
//
// 为什么要有本件：`UIFactory.CreateButton`（`Runtime/Presentation/UIWidgets.cs:169-187`）**只造纯色按钮**
//   —— 没有"按状态换 sprite"这条路，也没有"一张横排条带切 N 个状态"这条路。
//   其中缺图那条要注意：`SpriteState` 字段留 `null` 时 Unity 的 `Selectable.DoSpriteSwap`
//   会**直接早退**（画面停在上一张图 / 常态），于是"素材没同步好"表现成"悬停按下去没反应"，**零报错**。
//   ⇒ 本件把"缺失**不静默**"钉进实现（见下）。
//
// 缺失 sprite 的处理口径（缺失**必须留痕**，⛔ 不静默）：
//   · **常态缺失**（= 整组没底图）⇒ **保留调用方给的占位底色**，一个字段都不动，
//     并 `LogThrottle.WarnOnce` 点名一次（语义 = "整条缺失：保持纯色块" + 留痕）；
//   · **悬停 / 按下 / 禁用缺失** ⇒ **回落常态帧**（⛔ 不把 `null` 填进 `SpriteState` —— 见上"静默"），
//     且**不告警**：只有常态/按下两态的按钮是常见素材形态，那不算缺陷（口径写在这里，⛔ 不是"悄悄吞掉"）。
//
// 与既有引擎件的边界（⛔ 不是重复实现）：
//   · 骨架仍走 `UIFactory.CreateButton`（⭐ 本件**不改** `UIWidgets.cs`；只调用它）——
//     节点 / 锚点 / Label 子节点 / `Button` 组件都由它建，本件只负责"换皮"。
//   · 条带取帧走 `SpriteStripLoader`（同目录，整条 `LoadAll` 主路 + 逐帧按名兜底 + 就绪回调）——
//     ⛔ 本件不自己写第二套取帧 / 缓存 / 兜底。
//   · unlit 校验走 `UiImageLoader.EnsureUnlit`（⛔ 不自己判 shader 名）。
//   · ⛔ **不是 `UIFactory.CreateButton` 的替代**：那个是"零美术依赖的纯色按钮"（任何工程 launch 后
//     立刻可用）；本件要素材，只在"有原版/美术底图"时用。
//
// 边界：主线程使用（与 `LogThrottle` 一致）；⛔ 本件不加载任何资源（`CreateFromStrip` 把加载交给
// `SpriteStripLoader`）；⛔ 不注册 `Update` / 协程 / 事件，只有调用方主动 `Apply` 或条带就绪时改一次。
// ─────────────────────────────────────────────────────────────────────────────

using System;
using UnityEngine;
using UnityEngine.UI;

namespace CloverEngine
{
    /// <summary>
    /// <b>sprite-swap 按钮</b>工厂：用调用方给的"四态贴图"造一个 uGUI 按钮（或给已有按钮换皮）。
    /// <para>
    /// 骨架由 <see cref="UIFactory.CreateButton"/> 建（底板 <see cref="Image"/> + 居中 <see cref="Text"/> +
    /// <see cref="Button"/>），本件把 <see cref="Selectable.transition"/> 切成
    /// <see cref="Selectable.Transition.SpriteSwap"/> 并填好 <see cref="SpriteState"/>。
    /// </para>
    /// <para>
    /// <b>零项目取值</b>：尺寸 / 位置 / 文案 / 色调 / 字号 / 占位色 / 四态贴图**一律由调用方给**
    /// （⛔ 引擎不含任何素材路径，路径由调用方拼好传进来）。
    /// </para>
    /// <para>
    /// <b>缺图口径</b>（见文件头）：常态缺失 ⇒ 保留占位底 + <see cref="LogThrottle.WarnOnce"/> 一次；
    /// 其余态缺失 ⇒ 回落常态帧（设计内，不告警）。
    /// </para>
    /// </summary>
    public static class SpriteSwapButton
    {
        /// <summary>日志 tag（与 UI 通用件同域，便于按 <c>[SpriteSwap]</c> 检索）。</summary>
        private const string Tag = "SpriteSwap";

        /// <summary><see cref="CreateFromStrip"/> 未注入 loader 时共用的进程内实例（同路径只取一次图）。</summary>
        private static readonly SpriteStripLoader DefaultLoader = new SpriteStripLoader();

        /// <summary>
        /// 造按钮的**非视觉**参数（尺寸 / 位置 / 文案 / 回调 / 标签样式）。
        /// <para>用结构体是为了让 <see cref="Create"/> / <see cref="CreateFromStrip"/> 的签名短到能读；
        /// 所有字段都由调用方填，⛔ 引擎不提供任何默认取值。</para>
        /// </summary>
        public struct Spec
        {
            /// <summary>节点名（为空 ⇒ 降频 Warn 并回落到 <c>"Button"</c>，⛔ 不让 <c>new GameObject</c> 抛）。</summary>
            public string Name;

            /// <summary>按钮尺寸（画布单位）。</summary>
            public Vector2 Size;

            /// <summary>按钮中心相对父节点中心的偏移（画布单位）。</summary>
            public Vector2 Pos;

            /// <summary>按钮文案（⛔ 引擎不翻译、不给默认文案）。</summary>
            public string Label;

            /// <summary>点击回调（可为 <c>null</c>）。</summary>
            public Action OnClick;

            /// <summary>标签颜色；<c>null</c> ⇒ 沿用 <see cref="UIFactory.CreateButton"/> 的内置取值（引擎默认，非项目配色）。</summary>
            public Color? LabelColor;

            /// <summary>标签字号；<c>&lt;= 0</c> ⇒ 沿用 <see cref="UIFactory.CreateButton"/> 的内置取值。</summary>
            public int LabelFontSize;
        }

        /// <summary>
        /// 按钮的一套**皮肤**：四态贴图 + 色调 + 占位底色。
        /// <para>⛔ 引擎不在这里放任何默认素材 / 配色：不给就是 <c>null</c> / <see cref="Color.white"/>。</para>
        /// </summary>
        public sealed class Skin
        {
            /// <summary>常态贴图。为 <c>null</c> ⇒ 视为"整组没底图"（保留占位底，见 <see cref="Apply"/>）。</summary>
            public readonly Sprite Normal;

            /// <summary>悬停（高亮）贴图；<c>null</c> ⇒ 回落常态。</summary>
            public readonly Sprite Highlighted;

            /// <summary>按下贴图；<c>null</c> ⇒ 回落常态。</summary>
            public readonly Sprite Pressed;

            /// <summary>禁用贴图；<c>null</c> ⇒ 回落常态（只灰化文字的做法见 <c>SetInteractable</c>）。</summary>
            public readonly Sprite Disabled;

            /// <summary>贴图到位后套的色调（<see cref="Image.color"/> 的乘法因子）。</summary>
            public readonly Color Tint;

            /// <summary>缺图 / 在途期间保留的占位底色。</summary>
            public readonly Color Placeholder;

            /// <summary>是否按帧原始宽高比显示（<c>true</c> ⇒ 矩形与帧比例不一致时内缩，不拉变形）。</summary>
            public readonly bool PreserveAspect;

            /// <param name="normal">常态贴图（<c>null</c> = 整组没底图 ⇒ 保留占位底）。</param>
            /// <param name="highlighted">悬停贴图；<c>null</c> = 回落常态。</param>
            /// <param name="pressed">按下贴图；<c>null</c> = 回落常态。</param>
            /// <param name="disabled">禁用贴图；<c>null</c> = 回落常态。</param>
            /// <param name="tint">
            /// 色调；不给 = <see cref="Color.white"/>（= **不做色调乘法**，⛔ 不是"引擎挑的配色"）。
            /// </param>
            /// <param name="placeholder">
            /// 占位底色；不给 = <see cref="Color.white"/>（同上，= 不做色调乘法）。
            /// </param>
            /// <param name="preserveAspect">是否保持帧原始宽高比。</param>
            public Skin(Sprite normal, Sprite highlighted = null, Sprite pressed = null, Sprite disabled = null,
                Color? tint = null, Color? placeholder = null, bool preserveAspect = false)
            {
                Normal = normal;
                Highlighted = highlighted;
                Pressed = pressed;
                Disabled = disabled;
                Tint = tint ?? Color.white;
                Placeholder = placeholder ?? Color.white;
                PreserveAspect = preserveAspect;
            }
        }

        /// <summary>
        /// 一条横排条带里，四态各自对应的**帧号**（见 <see cref="CreateFromStrip"/>）。
        /// <para>用 `new StripStates(0, 1, 2)` 这种显式写法；<see cref="Disabled"/> 不传 = <c>-1</c> = 同常态。
        /// ⚠️ 用 `default` / 只写字段会让四个值全是 <c>0</c>（= 四态都用第 0 帧），那不是"没给"，读数时留意。</para>
        /// </summary>
        public struct StripStates
        {
            /// <summary>常态帧号（必给；越界 ⇒ 该按钮保留占位底 + 限频 Warn）。</summary>
            public int Normal;

            /// <summary>悬停帧号。</summary>
            public int Highlight;

            /// <summary>按下帧号。</summary>
            public int Pressed;

            /// <summary>禁用帧号；<c>&lt; 0</c> = 同常态。</summary>
            public int Disabled;

            /// <param name="disabled">&lt; 0 = 同常态（默认 <c>-1</c>）。</param>
            public StripStates(int normal, int highlight, int pressed, int disabled = -1)
            {
                Normal = normal;
                Highlight = highlight;
                Pressed = pressed;
                Disabled = disabled;
            }
        }

        // ── 造按钮 ───────────────────────────────────────────────────────────

        /// <summary>
        /// 造一个 sprite-swap 按钮（贴图**已经在手上**时用这条）。
        /// </summary>
        /// <param name="parent">父节点。</param>
        /// <param name="spec">尺寸 / 位置 / 文案 / 回调 / 标签样式。</param>
        /// <param name="skin">四态贴图 + 色调 + 占位底色。</param>
        /// <returns>按钮组件（其 <c>gameObject</c> 就是底板 Image 那个节点）。</returns>
        public static Button Create(Transform parent, Spec spec, Skin skin)
        {
            skin = skin ?? new Skin(null);
            var name = string.IsNullOrEmpty(spec.Name) ? FallbackName() : spec.Name;

            var img = UIFactory.CreateButton(name, parent, spec.Label, spec.Size, spec.Pos, skin.Placeholder,
                spec.OnClick);
            var button = img.GetComponent<Button>();

            if (spec.LabelColor.HasValue || spec.LabelFontSize > 0) ApplyLabelStyle(img, spec);

            Apply(button, skin);
            return button;
        }

        /// <summary>
        /// 造一个 sprite-swap 按钮，贴图**从一条横排条带异步取**（"一张图切 N 个状态"的素材形态）。
        /// <para>
        /// 取到之前按钮显示 <paramref name="placeholder"/> 那套（其 <see cref="Skin.Normal"/> 通常为
        /// <c>null</c> ⇒ 只显示占位底色 + 文案）；条带就绪后自动换上四态贴图。
        /// </para>
        /// <para>
        /// ⚠️ <b>回调可能是同帧同步的</b>（条带已缓存 / 整条取一次就成功）⇒ 返回时按钮可能**已经**是成品皮
        /// （见 <see cref="SpriteStripLoader.RequestStrip"/> 的同步口径）。
        /// </para>
        /// </summary>
        /// <param name="parent">父节点。</param>
        /// <param name="spec">尺寸 / 位置 / 文案 / 回调 / 标签样式。</param>
        /// <param name="stripPath">条带路径（引擎相对路径）。</param>
        /// <param name="frameCount">条带的子 sprite 数。</param>
        /// <param name="states">四态在条带里的帧号（见 <see cref="StripStates"/>）。</param>
        /// <param name="placeholder">取到之前的占位皮肤（可为 <c>null</c> = 纯白占位底 + 默认标签）。</param>
        /// <param name="loader">条带加载器；<c>null</c> ⇒ 用本类共用的进程内实例（同路径只取一次图）。</param>
        public static Button CreateFromStrip(Transform parent, Spec spec, string stripPath, int frameCount,
            StripStates states, Skin placeholder = null, SpriteStripLoader loader = null)
        {
            var button = Create(parent, spec, placeholder);
            var ldr = loader ?? DefaultLoader;

            ldr.RequestStrip(stripPath, frameCount, frames =>
            {
                if (button == null) return;                        // 面板已关闭 / 节点已销毁
                if (frames == null || frames.Length == 0) return;   // 取不到：已由 loader 留痕，保留占位

                var skin = new Skin(
                    PickFrame(frames, states.Normal, stripPath, "常态"),
                    PickFrame(frames, states.Highlight, stripPath, "悬停"),
                    PickFrame(frames, states.Pressed, stripPath, "按下"),
                    PickFrame(frames, states.Disabled, stripPath, "禁用"),
                    placeholder?.Tint, placeholder?.Placeholder, placeholder?.PreserveAspect ?? false);

                Apply(button, skin);
            });

            return button;
        }

        // ── 换皮 ─────────────────────────────────────────────────────────────

        /// <summary>
        /// 把一套皮肤套到**已有**按钮上（运行中换图 / 状态刷新用；<see cref="Create"/> 也走这里）。
        /// <para>
        /// 口径：<see cref="Skin.Normal"/> 为 <c>null</c> ⇒ **什么都不改**（保留调用方已设的占位底色与
        /// 现有 sprite）+ 一条 <see cref="LogThrottle.WarnOnce"/>；其余态为 <c>null</c> ⇒ 回落常态帧
        /// （⛔ 不把 <c>null</c> 填进 <see cref="SpriteState"/>，那会让 Unity 静默早退）。
        /// </para>
        /// </summary>
        /// <param name="button">目标按钮；<c>null</c> / 已销毁 ⇒ 直接返回。</param>
        /// <param name="skin">要套的皮肤；<c>null</c> ⇒ 直接返回（不是"清成默认"，那会静默改画面）。</param>
        public static void Apply(Button button, Skin skin)
        {
            if (button == null || skin == null) return;

            var img = button.targetGraphic as Image ?? button.GetComponent<Image>();
            if (img == null)
            {
                // 非预期分支：按钮没有 Image 底板 ⇒ 无 sprite 可换（只报一次，别每个按钮刷一条）
                LogThrottle.WarnOnce(Tag, "no-graphic",
                    "按钮 " + button.name + " 没有 Image 底板 ⇒ sprite-swap 无法应用（检查节点是不是被换成了别的 Graphic）");
                return;
            }

            if (skin.Normal == null)
            {
                // 非预期分支（整组没底图）：保留占位底 + 只报一次，⛔ 不动任何字段
                LogThrottle.WarnOnce(Tag, "missing-normal",
                    "按钮 " + button.name + " 的常态贴图缺失 ⇒ 保留占位底色 " + img.color +
                    " 与现有 sprite（⛔ 不做任何替换；检查素材是否同步 / 路径是否正确）");
                return;
            }

            img.sprite = skin.Normal;
            img.color = skin.Tint;                       // 未给 = white = 不做乘法（不是配色）
            img.preserveAspect = skin.PreserveAspect;

            var highlighted = skin.Highlighted != null ? skin.Highlighted : skin.Normal;
            var pressed = skin.Pressed != null ? skin.Pressed : skin.Normal;
            var disabled = skin.Disabled != null ? skin.Disabled : skin.Normal;

            button.transition = Selectable.Transition.SpriteSwap;
            button.spriteState = new SpriteState
            {
                highlightedSprite = highlighted,
                pressedSprite = pressed,
                selectedSprite = highlighted,           // 键盘 / 手柄选中与鼠标悬停同表现
                disabledSprite = disabled,
            };

            UiImageLoader.EnsureUnlit(img);             // 防御 2D 光照把底图压暗（⛔ 不自己判 shader 名）
        }

        // ── 内部 ─────────────────────────────────────────────────────────────

        /// <summary>把 <see cref="Spec"/> 的标签样式套到 <c>Label</c> 子节点上（取值全来自调用方）。</summary>
        private static void ApplyLabelStyle(Image img, Spec spec)
        {
            var text = img.transform.Find("Label")?.GetComponent<Text>();
            if (text == null)
            {
                // 非预期分支：UIFactory 的按钮约定"Label 子节点"；找不到说明节点结构被改过
                LogThrottle.WarnOnce(Tag, "label.missing",
                    "按钮 " + img.name + " 找不到 Label 子节点 ⇒ 标签颜色 / 字号未应用（检查 UIFactory.CreateButton 的节点结构）");
                return;
            }

            if (spec.LabelColor.HasValue) text.color = spec.LabelColor.Value;
            if (spec.LabelFontSize > 0) text.fontSize = spec.LabelFontSize;
        }

        /// <summary>
        /// 取条带里第 <paramref name="index"/> 帧。
        /// <para><paramref name="index"/> &lt; 0 ⇒ 返回 <c>null</c>（= "该态同常态"，设计内，不告警）；
        /// 越界（条带帧数比调用方报的少）⇒ 限频 Warn 后同样返回 <c>null</c>。</para>
        /// </summary>
        private static Sprite PickFrame(Sprite[] frames, int index, string stripPath, string stateName)
        {
            if (index < 0) return null;
            if (frames != null && index < frames.Length) return frames[index];

            // 非预期分支：帧号越界（帧数常量与素材不同步）⇒ 该态回落常态，点名 + 限频
            LogThrottle.WarnThrottled(Tag, "frame-out-of-range:" + stripPath,
                "条带 " + stripPath + " 的「" + stateName + "」帧号 " + index + " 越界（取到 " +
                (frames?.Length ?? 0) + " 帧）⇒ 该态回落常态（检查帧数常量与素材导入设置）", 10f);
            return null;
        }

        /// <summary>节点名兜底（<see cref="Spec.Name"/> 为空时用；⛔ 不是给按钮起"业务名"）。</summary>
        private static string FallbackName()
        {
            LogThrottle.WarnOnce(Tag, "spec.empty-name", "Spec.Name 为空 ⇒ 节点名回落为 \"Button\"（不影响显示）");
            return "Button";
        }
    }
}
