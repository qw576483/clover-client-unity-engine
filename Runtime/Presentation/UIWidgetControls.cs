// ─────────────────────────────────────────────────────────────────────────────
// CloverEngine · Runtime/Presentation/UIWidgetControls.cs
// uGUI 通用控件工厂：布局助手 + Slider / InputField / Selector / ToggleRow（＋两个句柄类型）——
// 通用横切能力，下沉到引擎。与 `UIWidgets.cs` 的 UIFactory 是同**一个** static partial class。
//
// 出处：clover-project-cs16 的
//   · `client/Assets/Scripts/UI/Flow/CsUiStyle.cs` —— 布局助手 `AnchoredTopLeft` / `AnchoredBottom` /
//     `CreateBottomLabel` / `CreateFullScreen` / `CreateBoxRect` / `CreateLabel` / `SetBarWidth`，
//     控件工厂 `CreateSelector` / `CreateSlider` / `CreateInputField` / `CreateToggleRow`，
//     以及 `Selector` / `ToggleRow` 两个句柄类型；
//   · `client/Assets/Scripts/UI/InGame/CsHudTheme.cs`（:405-545）—— 同一批控件在项目里被**第二遍**
//     自建（`CreateBar` / `CreateInputField`），本片一并收敛到这里。
//   pos / size / fontSize / 锚点 / 轴心 / 层级顺序**逐字照搬**，语义与视觉结果不变。
//
// ⛔ **没有下沉**（那也是能力域划分要求，不是遗漏）：
//   · **配色**：CsUiStyle 的 `#1B1B1B` / `#E8A33D`、CsHudTheme 的 `#FFB000` / `#9CFF9C` … 都是每个
//     项目的主题色，引擎只接受 `Color` / `ColorBlock` / `*Style` 参数，**不给任何默认值**。
//   · **文案**：`"◀"` / `"▶"` 箭头、值占位字、placeholder、按钮标题全部由调用方传入。
//   · **字号档位**：CS 菜单的 22 / 24、HUD 输入框的 20 / 18 都是业务取值，一律经参数传入。
//   · 面板特有的封装（CsUiStyle 的悬停橙按钮 `CreateButton`、CsHudTheme 的 `Place*` 定位族与
//     `CsHudBar` 句柄、`UIFactory.Stretch` 已有的铺满）—— 留在项目侧，做**薄转发**。
//
// 为什么下沉：`结构规则.md` §4.4「已有同类能力不准再起第二套」。同一个 uGUI 控件在项目里被造了
//   三遍（CsUiStyle / CsHudTheme / ConsolePanel 各一份），且三份**互相指对方"有坑/编译不过"**。
//   收敛到引擎后新项目不必再踩一遍下面这三个实测坑：
//     ① **Slider 不能用 `DefaultControls.CreateSlider`**：它的配色塞在内部私有层级里，只能靠名字找
//        节点，而 Unity 明确说过不要依赖该层级；正确做法是手搭轨道 + 填充分 + 手柄分，只依赖
//        `Slider.fillRect` / `Slider.handleRect` 两个**公开**属性（见 CreateSlider）。
//     ② **InputField 的 `placeholder` 声明类型是 `Graphic`**（见 uGUI `InputField.cs` 的
//        `public Graphic placeholder`），直接对它取 `.font` / `.fontSize` / `.text` 会 **CS1061**，
//        必须 `as Text` 再取（见 CreateInputField）。
//     ③ **进度/血量条不能用空 sprite 的 `Image.fillAmount`**：sprite 为空时 `Image` 走
//        `Graphic.OnPopulateMesh` 的实心四边形分支，`fillAmount` 静默失效（看着就是"进度条永不动"），
//        要用**锚点宽度**表达比例（见 SetBarWidth，与 `WorldHpBar` 的世界空间版是同类陷阱的另一种形态）。
//
// 语义约束（改一条 = 语义漂移）：
//   ① 布局字面量（`new Vector2(...)` / 字号位次）逐字照搬项目现版，本文件不做任何"顺手优化"。
//   ② 引擎**不含**任何项目配色 / 文案 / 字号取值：全部来自参数或 `*Style`。
//   ③ `CreateSelector` 的箭头与 `CreateToggleRow` 的值按钮 = **非强调**按钮：先按引擎
//      `CreateButton` 建（其 label 默认 26 号、色 `(0.95,0.96,1)`），再按 `*Style` **覆盖**字号与颜色
//      —— 与项目现版"先建后改"两次赋值的**最终结果**逐字一致（顺序也必须一致，否则会被覆盖回去）。
// ─────────────────────────────────────────────────────────────────────────────
using System;
using UnityEngine;
using UnityEngine.UI;

namespace CloverEngine
{
    /// <summary>
    /// 非强调按钮（<see cref="Selector"/> 的左右箭头 / <see cref="ToggleRow"/> 的值按钮）的外观。
    /// 引擎不给默认值：配色是业务。
    /// </summary>
    public struct WidgetButtonStyle
    {
        /// <summary>常态 / 选中态底色（uGUI 的 <c>selectedColor</c> 与常态同色）。</summary>
        public Color Background;
        /// <summary>悬停底色。</summary>
        public Color Highlighted;
        /// <summary>按下底色。</summary>
        public Color Pressed;
        /// <summary>禁用底色。</summary>
        public Color Disabled;
        /// <summary>颜色过渡时长（秒）。</summary>
        public float FadeDuration;
        /// <summary>按钮文字色。</summary>
        public Color TextColor;
        /// <summary>按钮文字字号。</summary>
        public int TextFontSize;
    }

    /// <summary>水平滑块（<see cref="UIFactory.CreateSlider"/>）的外观：轨道 / 已填 / 手柄。</summary>
    public struct WidgetSliderStyle
    {
        /// <summary>轨道底色（也是滑块节点的底板色）。</summary>
        public Color TrackColor;
        /// <summary>已填部分颜色。</summary>
        public Color FillColor;
        /// <summary>手柄常态色。</summary>
        public Color HandleColor;
        /// <summary>手柄的交互态颜色（<c>Selectable.colors</c>，由调用方整块给出）。</summary>
        public ColorBlock HandleColors;
        /// <summary>手柄宽度（像素；高度由滑块轨道决定，见 <see cref="UIFactory.CreateSlider"/>）。</summary>
        public float HandleWidth;
    }

    /// <summary>单行输入框（<see cref="UIFactory.CreateInputField"/>）的外观。</summary>
    public struct WidgetInputFieldStyle
    {
        /// <summary>输入框底板色。</summary>
        public Color Background;
        /// <summary>输入框自身的交互态颜色（<c>Selectable.colors</c>，由调用方整块给出）。</summary>
        public ColorBlock Colors;
        /// <summary>文本色。</summary>
        public Color TextColor;
        /// <summary>文本字号。</summary>
        public int TextFontSize;
        /// <summary>占位文案颜色。</summary>
        public Color PlaceholderColor;
        /// <summary>占位文案字号。</summary>
        public int PlaceholderFontSize;
        /// <summary>光标色。</summary>
        public Color CaretColor;
        /// <summary>选区色。</summary>
        public Color SelectionColor;
    }

    /// <summary>
    /// 「◀ 值 ▶」选择行（<see cref="UIFactory.CreateSelector"/>）的外观。
    /// 形态取自 CS 1.6 的选项行：左右箭头夹住当前值。
    /// </summary>
    public struct WidgetSelectorStyle
    {
        /// <summary>整行高度。</summary>
        public float RowHeight;
        /// <summary>左侧标签字号。</summary>
        public int LabelFontSize;
        /// <summary>左侧标签颜色。</summary>
        public Color LabelColor;
        /// <summary>中间值文本字号。</summary>
        public int ValueFontSize;
        /// <summary>中间值文本颜色。</summary>
        public Color ValueColor;
        /// <summary>值文本初值（调用方随后会覆写它）。</summary>
        public string ValueText;
        /// <summary>左箭头文案。</summary>
        public string PrevText;
        /// <summary>右箭头文案。</summary>
        public string NextText;
        /// <summary>两个箭头按钮的外观。</summary>
        public WidgetButtonStyle ArrowButton;
    }

    /// <summary>
    /// 「标签 + 值按钮」开关行（<see cref="UIFactory.CreateToggleRow"/>）的外观。
    /// 形态取自 CS 1.6 的选项行：布尔项是一个点了会变的按钮，不是复选框。
    /// </summary>
    public struct WidgetToggleRowStyle
    {
        /// <summary>整行高度。</summary>
        public float RowHeight;
        /// <summary>左侧标签字号。</summary>
        public int LabelFontSize;
        /// <summary>左侧标签颜色。</summary>
        public Color LabelColor;
        /// <summary>值按钮初值（调用方随后会覆写它）。</summary>
        public string ButtonText;
        /// <summary>值按钮的外观。</summary>
        public WidgetButtonStyle Button;
    }

    public static partial class UIFactory
    {
        // ═══════════════════════════ 布局助手 ═══════════════════════════
        //
        // 引擎已有 `Stretch`（铺满父节点）与 `CreateCentered`（居中定尺）；这里补的是通用的
        // `Place`（锚点 + 轴心 + 偏移），以及基于它派生的"左上角 / 底部为原点"定位族与三个便捷建件。
        // 项目 `CsUiStyle.StretchRoot` 与 `CreateFullScreen` 分别等价于引擎既有的 `Stretch` 与
        // `CreatePanel` ⇒ 那两处**不在这里再起第二套**，项目侧直接转发引擎既有的方法。

        /// <summary>
        /// 定位的最通用形式：把矩形钉在父节点的某个锚点 / 轴心（<paramref name="pos"/> 为相对该锚点的偏移）。
        /// <see cref="AnchoredTopLeft"/> / <see cref="AnchoredBottom"/> 与项目侧的 <c>Place*</c> 族都是它的特例。
        /// </summary>
        public static void Place(RectTransform rt, Vector2 anchor, Vector2 pivot, Vector2 pos, Vector2 size)
        {
            if (rt == null) return;
            rt.anchorMin = anchor;
            rt.anchorMax = anchor;
            rt.pivot = pivot;
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
        }

        /// <summary>把矩形固定为"以父节点左上角为原点"的定位方式（<paramref name="pos"/>.y 为负 = 向下）。</summary>
        public static void AnchoredTopLeft(RectTransform rt, Vector2 pos, Vector2 size)
        {
            if (rt == null) return;
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
        }

        /// <summary>
        /// 把矩形固定为"以父节点**底部**为原点"的定位方式（<paramref name="pos"/>.y 为**正** = 向上）。
        ///
        /// <para>用途：署名 / 版权这类"必须贴在底部"的元素。**不要**用 <see cref="AnchoredTopLeft"/> + 一个大负 y
        /// 去放底部元素 —— CanvasScaler 是 <c>match=0.5</c>，真实画布高度随窗口变化（实测 1600x900 时只有约 972），
        /// y 一超过画布高度就**整体掉到屏幕外**（元素 active、文本正确，但一个像素都看不见）。</para>
        /// </summary>
        /// <param name="anchor">决定贴左 / 居中 / 贴右（用 <see cref="TextAnchor"/> 的 Lower* 三种）。</param>
        public static void AnchoredBottom(RectTransform rt, Vector2 pos, Vector2 size,
            TextAnchor anchor = TextAnchor.LowerCenter)
        {
            if (rt == null) return;
            var ax = anchor == TextAnchor.LowerLeft ? 0f : anchor == TextAnchor.LowerRight ? 1f : 0.5f;
            rt.anchorMin = new Vector2(ax, 0f);
            rt.anchorMax = new Vector2(ax, 0f);
            rt.pivot = new Vector2(ax, 0f);
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
        }

        /// <summary>底部居中定位的文本（署名 / 版本号用）。</summary>
        public static Text CreateBottomLabel(string name, Transform parent, string content, int fontSize,
            Vector2 pos, Vector2 size, Color color)
        {
            var text = CreateText(name, parent, content, fontSize, TextAnchor.MiddleCenter, color);
            AnchoredBottom(text.rectTransform, pos, size, TextAnchor.LowerCenter);
            return text;
        }

        /// <summary>左上角定位的纯色块（内容框 / 输入框底 / 分隔条）。</summary>
        public static Image CreateBoxRect(string name, Transform parent, Vector2 pos, Vector2 size, Color color,
            bool raycast = false)
        {
            var img = CreatePanel(name, parent, color, raycast);
            AnchoredTopLeft(img.rectTransform, pos, size);
            return img;
        }

        /// <summary>左上角定位的文本。</summary>
        public static Text CreateLabel(string name, Transform parent, string content, int fontSize, Vector2 pos,
            Vector2 size, TextAnchor anchor, Color color)
        {
            var text = CreateText(name, parent, content, fontSize, anchor, color);
            AnchoredTopLeft(text.rectTransform, pos, size);
            return text;
        }

        /// <summary>
        /// 把 0~1 的进度写进"按锚点宽度"的进度条。
        /// ⛔ 别改用空 sprite 的 <c>Image.fillAmount</c>：sprite 为空时 <c>Image</c> 走
        /// <c>Graphic.OnPopulateMesh</c> 的实心四边形分支，<c>fillAmount</c> 会**静默失效**。
        /// （世界空间的同类陷阱见 <see cref="WorldHpBar"/>：那边用 <c>localScale</c> 表达比例。）
        /// </summary>
        public static void SetBarWidth(RectTransform fill, float progress01)
        {
            if (fill == null) return;
            var p = Mathf.Clamp01(progress01);
            fill.anchorMin = new Vector2(0f, 0f);
            fill.anchorMax = new Vector2(p, 1f);
            fill.offsetMin = Vector2.zero;
            fill.offsetMax = Vector2.zero;
        }

        // ═══════════════════════════ 控件工厂 ═══════════════════════════

        /// <summary>
        /// 水平滑块：轨道 + 已填 + 手柄（左上角定位，尺寸就是整块控件的尺寸）。
        ///
        /// <para>⛔ <b>自建而不是用 <c>DefaultControls.CreateSlider</c></b>：后者的配色塞在内部层级里，
        /// 只能靠名字去找节点（Unity 明确说过不要依赖它的层级）。这里只依赖
        /// <see cref="Slider.fillRect"/> / <see cref="Slider.handleRect"/> 这两个公开属性。</para>
        /// </summary>
        public static Slider CreateSlider(string name, Transform parent, Vector2 pos, Vector2 size,
            float min, float max, float value, Action<float> onValueChanged, WidgetSliderStyle style)
        {
            var root = CreateBoxRect(name, parent, pos, size, style.TrackColor, true);
            var slider = root.gameObject.AddComponent<Slider>();

            var fillArea = CreateNode("Fill Area", root.rectTransform);
            fillArea.anchorMin = new Vector2(0f, 0.25f);
            fillArea.anchorMax = new Vector2(1f, 0.75f);
            fillArea.offsetMin = new Vector2(2f, 0f);
            fillArea.offsetMax = new Vector2(-2f, 0f);

            var fill = CreatePanel("Fill", fillArea, style.FillColor, false);
            fill.rectTransform.anchorMin = Vector2.zero;
            fill.rectTransform.anchorMax = Vector2.one;
            fill.rectTransform.offsetMin = Vector2.zero;
            fill.rectTransform.offsetMax = Vector2.zero;

            var handleArea = CreateNode("Handle Slide Area", root.rectTransform);
            handleArea.offsetMin = new Vector2(4f, 0f);
            handleArea.offsetMax = new Vector2(-4f, 0f);

            // 手柄：Slider.UpdateVisuals 会自己驱动 anchorMin/anchorMax（X 跟随数值、Y 撑满容器），
            // 这里只需要给宽度（sizeDelta.x），高度让它跟着轨道走。
            var handle = CreatePanel("Handle", handleArea, style.HandleColor, true);
            handle.rectTransform.anchorMin = new Vector2(0f, 0f);
            handle.rectTransform.anchorMax = new Vector2(0f, 1f);
            handle.rectTransform.pivot = new Vector2(0.5f, 0.5f);
            handle.rectTransform.sizeDelta = new Vector2(style.HandleWidth, 0f);

            slider.fillRect = fill.rectTransform;
            slider.handleRect = handle.rectTransform;
            slider.targetGraphic = handle;
            slider.direction = Slider.Direction.LeftToRight;
            slider.wholeNumbers = false;
            slider.minValue = min;
            slider.maxValue = max;
            slider.colors = style.HandleColors;

            slider.value = Mathf.Clamp(value, min, max);

            // 先赋 value 再挂回调：避免回调把"初始化"当成一次用户改动而触发保存。
            if (onValueChanged != null) slider.onValueChanged.AddListener(v => onValueChanged(v));

            return slider;
        }

        /// <summary>
        /// 单行文本输入框（按 <paramref name="anchor"/> / <paramref name="pivot"/> 定位，同 <see cref="Place"/>）。
        ///
        /// <para>这里用 uGUI 自带的 <c>DefaultControls.CreateInputField</c>：输入框的
        /// textComponent / placeholder / 光标 / 选区那一套内部连线较多，重写一遍风险大于收益；
        /// 建好之后只用公开属性改配色与字号。</para>
        ///
        /// <para>⛔ <b>本方法存在的直接原因（实测坑）</b>：uGUI 里 <c>InputField.placeholder</c> 的
        /// 声明类型是 <c>Graphic</c>（包源码 <c>.../Runtime/UGUI/UI/Core/InputField.cs</c> 的
        /// <c>public Graphic placeholder</c>），对它直接取 <c>.font</c> / <c>.fontSize</c> / <c>.text</c>
        /// 会报 <b>CS1061</b>。唯一正确写法是先 <c>as Text</c> 再取（下面的 placeholder 段）。
        /// 项目侧曾把这记成「CsUiStyle.CreateInputField 编译不过」（CsHudTheme.cs:473 / ConsolePanel.cs:80）
        /// —— 根因就是这一行缺失；本方法已按正确写法下沉，该缺陷不复存在。</para>
        /// </summary>
        public static InputField CreateInputField(string name, Transform parent, Vector2 anchor, Vector2 pivot,
            Vector2 pos, Vector2 size, string placeholderText, int characterLimit, WidgetInputFieldStyle style)
        {
            var go = DefaultControls.CreateInputField(new DefaultControls.Resources());
            go.name = name;
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            // 定位用通用 Place（不是 AnchoredTopLeft）：菜单侧传 (0,1)/(0,1) 即"左上角为原点"，
            // HUD 侧传自己的锚点对 —— 两条路径的四个字段赋值逐字相同，视觉不变。
            Place(rt, anchor, pivot, pos, size);

            var input = go.GetComponent<InputField>();
            if (input == null)
            {
                Game.Logger?.Error("UI", $"DefaultControls 未产出 InputField（{name}）");
                return null;
            }

            var bg = go.GetComponent<Image>();
            if (bg != null) bg.color = style.Background;

            input.colors = style.Colors;

            input.lineType = InputField.LineType.SingleLine;
            input.characterLimit = characterLimit;
            input.caretColor = style.CaretColor;
            input.customCaretColor = true;
            input.selectionColor = style.SelectionColor;

            var font = DefaultFont();
            if (input.textComponent != null)
            {
                input.textComponent.font = font;
                input.textComponent.fontSize = style.TextFontSize;
                input.textComponent.color = style.TextColor;
                input.textComponent.alignment = TextAnchor.MiddleLeft;
                input.textComponent.supportRichText = false;
                input.textComponent.horizontalOverflow = HorizontalWrapMode.Overflow;
                input.textComponent.verticalOverflow = VerticalWrapMode.Truncate;
            }
            else
            {
                Game.Logger?.Warn("UI", $"输入框 {name} 没有 Text 子节点，文本不会显示");
            }

            // uGUI 的 placeholder 声明类型是 Graphic，font/fontSize/text 只在 Text 上 → 必须 as Text 再取。
            var placeholder = input.placeholder as Text;
            if (placeholder != null)
            {
                placeholder.font = font;
                placeholder.fontSize = style.PlaceholderFontSize;
                placeholder.text = placeholderText;
                placeholder.color = style.PlaceholderColor;
            }
            else if (input.placeholder != null)
            {
                Game.Logger?.Warn("UI",
                    $"输入框 {name} 的 placeholder 不是 Text（{input.placeholder.GetType().Name}），占位文案不会显示");
            }

            return input;
        }

        /// <summary>
        /// 「◀ 值 ▶」选择行 —— CS 1.6 的 bot 难度 / 选项就是这个形态（左右箭头夹住当前值）。
        /// 返回的 <see cref="Selector"/> 持有值文本，切档时由调用方改它的 <c>text</c>。
        /// </summary>
        public static Selector CreateSelector(string name, Transform parent, string label, Vector2 pos,
            float labelWidth, float arrowWidth, float valueWidth, Action onPrev, Action onNext,
            WidgetSelectorStyle style)
        {
            var holder = CreateNode(name, parent);
            AnchoredTopLeft(holder, pos, new Vector2(labelWidth + arrowWidth * 2f + valueWidth, style.RowHeight));

            CreateLabel("Label", holder, label, style.LabelFontSize, Vector2.zero,
                new Vector2(labelWidth, style.RowHeight), TextAnchor.MiddleLeft, style.LabelColor);

            var prev = CreateNonAccentButton("Prev", holder, style.PrevText, new Vector2(labelWidth, 0f),
                new Vector2(arrowWidth, style.RowHeight), onPrev, style.ArrowButton);
            var value = CreateLabel("Value", holder, style.ValueText, style.ValueFontSize,
                new Vector2(labelWidth + arrowWidth, 0f),
                new Vector2(valueWidth, style.RowHeight), TextAnchor.MiddleCenter, style.ValueColor);
            var next = CreateNonAccentButton("Next", holder, style.NextText,
                new Vector2(labelWidth + arrowWidth + valueWidth, 0f),
                new Vector2(arrowWidth, style.RowHeight), onNext, style.ArrowButton);

            return new Selector { Value = value, Prev = prev, Next = next };
        }

        /// <summary>
        /// 「标签 + 值按钮」的开关行（On / Off）：点值按钮就地切换。
        /// CS 1.6 的选项里布尔项就是一个点了会变的按钮，不是复选框。
        /// </summary>
        public static ToggleRow CreateToggleRow(string name, Transform parent, string label, Vector2 pos,
            float labelWidth, float buttonWidth, WidgetToggleRowStyle style)
        {
            var holder = CreateNode(name, parent);
            AnchoredTopLeft(holder, pos, new Vector2(labelWidth + buttonWidth, style.RowHeight));

            CreateLabel("Label", holder, label, style.LabelFontSize, Vector2.zero,
                new Vector2(labelWidth, style.RowHeight), TextAnchor.MiddleLeft, style.LabelColor);

            var button = CreateNonAccentButton("Value", holder, style.ButtonText, new Vector2(labelWidth, 0f),
                new Vector2(buttonWidth, style.RowHeight), null, style.Button);
            var value = button != null ? button.GetComponentInChildren<Text>() : null;

            if (value == null)
                Game.Logger?.Warn("UI", $"开关行 {name} 没有取到值文本，On/Off 不会显示");

            return new ToggleRow { Button = button, Value = value };
        }

        /// <summary>
        /// 非强调按钮：按 <see cref="CreateButton"/> 建（label 居中、字号 26、色 (0.95,0.96,1)），
        /// 再按 <paramref name="style"/> 覆盖颜色与字号。
        /// ⛔ 两步顺序不能倒：覆盖必须发生在 <see cref="CreateButton"/> 之后，否则会被它的默认值盖回去。
        /// </summary>
        private static Button CreateNonAccentButton(string name, Transform parent, string label, Vector2 pos,
            Vector2 size, Action onClick, WidgetButtonStyle style)
        {
            var img = CreateButton(name, parent, label, size, Vector2.zero, style.Background, onClick);
            AnchoredTopLeft(img.rectTransform, pos, size);

            var btn = img.GetComponent<Button>();
            if (btn != null)
            {
                var colors = btn.colors;
                colors.normalColor = style.Background;
                colors.highlightedColor = style.Highlighted;
                colors.pressedColor = style.Pressed;
                colors.selectedColor = style.Background;
                colors.disabledColor = style.Disabled;
                colors.colorMultiplier = 1f;
                colors.fadeDuration = style.FadeDuration;
                btn.colors = colors;
            }

            var labelText = img.GetComponentInChildren<Text>();
            if (labelText != null)
            {
                labelText.fontSize = style.TextFontSize;
                labelText.color = style.TextColor;
            }

            return btn;
        }
    }

    /// <summary>「标签 + 值按钮」开关行的句柄（可序列化，能存进预制体）。</summary>
    [Serializable]
    public sealed class ToggleRow
    {
        public Button Button;
        public Text Value;

        public void SetText(string text)
        {
            if (Value != null) Value.text = text;
        }
    }

    /// <summary>
    /// 「◀ 值 ▶」选择行的句柄。
    ///
    /// <para>
    /// 标了 <see cref="SerializableAttribute"/> 才能作为嵌套字段存进面板预制体
    /// （<c>[SerializeField] private Selector _bots;</c>）—— 面板的控件引用必须能序列化，
    /// 否则实例化出来全是 null。
    /// </para>
    /// </summary>
    [Serializable]
    public sealed class Selector
    {
        public Text Value;
        public Button Prev;
        public Button Next;

        public void SetText(string text)
        {
            if (Value != null) Value.text = text;
        }
    }
}
