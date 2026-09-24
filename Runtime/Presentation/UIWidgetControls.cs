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
using System.Collections.Generic;
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
        /// 去放底部元素 —— CanvasScaler 是 <c>match=0.5</c>，真实画布高度随窗口变化（1600x900 时只有约 972），
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
        /// <para>⛔ <b>本方法存在的直接原因</b>：uGUI 里 <c>InputField.placeholder</c> 的
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

        // ═══════════════════════ 竖向滚动列表 ═══════════════════════
        //
        // 出处（形状与坑逐条来自参考实现，⛔ 不含任何项目专属数值）：
        //   `clr-project-cr` 的 `UI/Panels/DeckEditPanel.cs` 的 `BuildGrid()` —— 它为了"可按住拖动的卡池"
        //   自建了一套 `ScrollRect + RectMask2D + content + 行`；同一个交付单元此前只做过分页
        //   （`RoomListPanel`）⇒ 同一个工程里出现了**两份取法**。本件把它收敛成引擎的一个建件。
        //
        // ★ 必须写进注释的四个坑（都是"看得见现象、查不到原因"的类型）：
        //   ① **`viewport` / `content` 两个字段必须显式赋值**。`ScrollRect` 在二者任一为空时**直接 return**
        //      （不报错、不警告）⇒ 表现是"节点都在、拖不动"，最难归因的一种。
        //   ② **视口必须有可命中的图形**：`RectMask2D` **不是** `Graphic`，只有它时射线打不到视口本身 ——
        //      "按在两行之间的空隙上拖动"命不中任何东西 ⇒ 拖不动（按在行上则能拖，因为事件从子节点冒泡上来）。
        //      本件给视口默认挂一个**全透明** `Image`（`raycastTarget = true`）解决（这正是 uGUI 自带
        //      ScrollView 的形状）；要背景色就传 `viewportColor`。
        //   ③ **content 的高度必须自己算**（行高之和 + 间距之和）：不设时会按"行撑满父节点"解释，
        //      滚动范围恒为 0（拖不动）或底部最后一行被永久裁掉。
        //   ④ **`verticalNormalizedPosition`：1 = 顶部、0 = 底部**（与直觉相反）。想在切页签时"复位到顶部"
        //      要用 1 —— 参见 <see cref="VerticalList.ScrollToTop"/>。
        //
        // ⛔ 引擎不含任何项目的滚动手感数值（`movementType` / `elasticity` / `decelerationRate` /
        //    `scrollSensitivity` 一律不预设）：把 <see cref="VerticalList.Scroll"/> 暴露给调用方，
        //    手感由项目定（参考实现自己登记了"这两个值与原版的真实手感参数无出处"）。
        // ⛔ 也不用 `Mask`：它要求同一个节点上有 `Graphic` 且行为受 `showMaskGraphic` 影响；
        //    `RectMask2D` 只需矩形即可裁剪，且不依赖 sprite。

        /// <summary>
        /// 创建一个**竖向滚动列表**（视口 + 列表根 + 行宿主）。
        /// <para>
        /// 结构：<c>name</c>（= 视口，带 <see cref="ScrollRect"/> 与可选的 <see cref="RectMask2D"/>）
        /// → <c>{name}.Content</c>（列表根，行挂在这下面）。
        /// 行由 <see cref="VerticalList.CreateItem"/> 造（锚点 = 顶部撑满宽、轴心 = 顶边），
        /// 位置与 content 高度由 <see cref="VerticalList.Reflow"/> 统一算。
        /// </para>
        /// <para>
        /// 定位沿用 <see cref="Place"/> 的四元组（与 <see cref="CreateInputField"/> 同风格）：
        /// <paramref name="anchor"/> / <paramref name="pivot"/> / <paramref name="pos"/> / <paramref name="viewportSize"/>。
        /// </para>
        /// </summary>
        /// <param name="name">节点名（视口名）。</param>
        /// <param name="parent">宿主节点。</param>
        /// <param name="anchor">视口锚点（同 <see cref="Place"/>）。</param>
        /// <param name="pivot">视口轴心（同 <see cref="Place"/>）。</param>
        /// <param name="pos">视口位置（相对锚点的偏移）。</param>
        /// <param name="viewportSize">可视区尺寸（= 视口尺寸，滚动就是在这个框里发生）。</param>
        /// <param name="itemHeight">行高（画布单位）。</param>
        /// <param name="spacing">行间距（画布单位；可为 0）。</param>
        /// <param name="masked">是否挂 <see cref="RectMask2D"/> 裁掉出框的行。默认 true。</param>
        /// <param name="viewportColor">视口底色；默认全透明（只为可命中，见上文坑 ②）。</param>
        public static VerticalList CreateVerticalList(string name, Transform parent, Vector2 anchor, Vector2 pivot,
            Vector2 pos, Vector2 viewportSize, float itemHeight, float spacing, bool masked = true,
            Color viewportColor = default(Color))
        {
            // 视口底色用 Image 而不是 CreateNode：见上文坑 ② —— 没有 Graphic 就没有可命中的图元。
            var viewport = CreatePanel(name, parent, viewportColor, true);
            Place(viewport.rectTransform, anchor, pivot, pos, viewportSize);

            var scroll = viewport.gameObject.AddComponent<ScrollRect>();
            if (masked)
            {
                // 遮罩挂在**视口自己**身上：`RectMask2D` 裁的是自己的子节点，而子节点只有 content
                // ⇒ 效果 = 视口裁剪，不必再多套一层空节点（参考实现的做法）。
                viewport.gameObject.AddComponent<RectMask2D>();
            }

            var content = CreateNode(name + ".Content", viewport.rectTransform);
            // content 锚点 = 顶部**横向撑满**、轴心 = 顶边中点：宽度随视口变（行宽度也随视口变），
            // 高度由 Reflow 显式写死。⛔ 不要给行用 Stretch（铺满）—— 那会让行对 content 高度贡献 0。
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.anchoredPosition = Vector2.zero;
            content.sizeDelta = new Vector2(0f, 0f);

            scroll.viewport = viewport.rectTransform;
            scroll.content = content;
            scroll.horizontal = false;
            scroll.vertical = true;

            return new VerticalList
            {
                Root = viewport.rectTransform,
                Viewport = viewport.rectTransform,
                Content = content,
                Scroll = scroll,
                ItemHeight = itemHeight,
                Spacing = spacing,
            };
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

    /// <summary>
    /// **竖向滚动列表**的句柄（由 <see cref="UIFactory.CreateVerticalList"/> 产出）。
    /// <para>
    /// 职责边界：本类只管"列表的骨架"——视口 / 列表根 / 行的落位 / content 高度 / 滚到某一行；
    /// **行里画什么**（图标、文案、按钮、拖拽）是调用方的事：<see cref="CreateItem"/> 返回的就是一个
    /// 空的、已按位摆好的矩形节点，往里面挂子节点即可。
    /// </para>
    /// <para>
    /// 行不做虚拟化（行数少时没必要）：<see cref="ClearItems"/> + 重新 <see cref="CreateItem"/> 是最直的用法；
    /// 行数多时由调用方自己缓存返回的 <see cref="RectTransform"/>（= "可复用的 item 宿主"）。
    /// </para>
    /// </summary>
    public sealed class VerticalList
    {
        /// <summary>列表容器（= <see cref="Viewport"/>；<see cref="ScrollRect"/> 挂在这个节点上）。</summary>
        public RectTransform Root;

        /// <summary>视口（带遮罩时裁剪发生在这里；<see cref="ScrollRect.viewport"/> 指向它）。</summary>
        public RectTransform Viewport;

        /// <summary>列表根：所有行都是它的子节点（<see cref="ScrollRect.content"/>）。</summary>
        public RectTransform Content;

        /// <summary>
        /// uGUI 滚动件本体。手感参数（<c>movementType</c> / <c>elasticity</c> / <c>decelerationRate</c> /
        /// <c>inertia</c> / <c>scrollSensitivity</c>）**引擎刻意不预设** —— 那属项目的表现取值，由调用方设。
        /// </summary>
        public ScrollRect Scroll;

        /// <summary>行高（画布单位）—— 建列表时给的默认行高。</summary>
        public float ItemHeight;

        /// <summary>行间距（画布单位）。</summary>
        public float Spacing;

        /// <summary>一行所占的步进（行高 + 间距）。</summary>
        public float RowStep => ItemHeight + Spacing;

        /// <summary>当前行数（= 列表根的子节点数）。</summary>
        public int ItemCount => Content != null ? Content.childCount : 0;

        /// <summary>各行的高度（与行下标一一对应，供 <see cref="Reflow"/> 累加落位）。</summary>
        private readonly List<float> _rowHeights = new List<float>();

        /// <summary>
        /// 造一个**行宿主**：锚点 = 顶部横向撑满、轴心 = 顶边中点，尺寸 = (自动宽, <paramref name="height"/>)。
        /// 它是个空节点，调用方往里挂自己的控件即可；返回它便于后续取用（缓存 = 复用）。
        /// </summary>
        /// <param name="name">行名（默认 <c>Item{下标}</c>）。</param>
        /// <param name="height">行高；<c>&lt;= 0</c> ⇒ 用 <see cref="ItemHeight"/>。</param>
        public RectTransform CreateItem(string name = null, float height = -1f)
        {
            var h = height > 0f ? height : ItemHeight;
            var index = ItemCount;
            var item = UIFactory.CreateNode(
                string.IsNullOrEmpty(name) ? "Item" + index : name, Content);
            item.anchorMin = new Vector2(0f, 1f);
            item.anchorMax = new Vector2(1f, 1f);
            item.pivot = new Vector2(0.5f, 1f);
            item.sizeDelta = new Vector2(0f, h);

            _rowHeights.Add(h);
            Reflow();
            return item;
        }

        /// <summary>取第 <paramref name="index"/> 行（越界返回 null）。</summary>
        public RectTransform GetItem(int index)
        {
            if (Content == null || index < 0 || index >= Content.childCount) return null;
            return Content.GetChild(index) as RectTransform;
        }

        /// <summary>
        /// 重新按"行高 + 间距"排布所有行，并把 content 高度写成总高。
        /// <para>
        /// ⛔ 不写 content 高度的话：滚动范围会算错（拖不动 / 底部行被永久裁掉），见建件注释的坑 ③。
        /// </para>
        /// </summary>
        public void Reflow()
        {
            if (Content == null) return;

            var count = Content.childCount;
            if (_rowHeights.Count > count) _rowHeights.RemoveRange(count, _rowHeights.Count - count);

            var y = 0f;
            for (var i = 0; i < count; i++)
            {
                // 行是外部可能直接建/删的（Content 是公开字段）：临时缺高度时按 ItemHeight 兜底，
                // 免得"某一行高度 0 导致下面全部叠在一起"。
                var h = i < _rowHeights.Count ? _rowHeights[i] : ItemHeight;
                if (i >= _rowHeights.Count) _rowHeights.Add(h);

                if (Content.GetChild(i) is RectTransform rt)
                {
                    rt.anchorMin = new Vector2(0f, 1f);
                    rt.anchorMax = new Vector2(1f, 1f);
                    rt.pivot = new Vector2(0.5f, 1f);
                    rt.sizeDelta = new Vector2(rt.sizeDelta.x, h);
                    rt.anchoredPosition = new Vector2(0f, -y);
                }

                y += h + Spacing;
            }

            var total = count > 0 ? y - Spacing : 0f;   // 最后一个行后面不跟间距
            Content.sizeDelta = new Vector2(Content.sizeDelta.x, Mathf.Max(0f, total));
        }

        /// <summary>销毁全部行并复位（下一次 <see cref="CreateItem"/> 从第 0 行重新开始）。</summary>
        public void ClearItems()
        {
            if (Content != null)
            {
                for (var i = Content.childCount - 1; i >= 0; i--)
                    UnityEngine.Object.Destroy(Content.GetChild(i).gameObject);
            }
            _rowHeights.Clear();
            if (Content != null) Content.sizeDelta = new Vector2(Content.sizeDelta.x, 0f);
        }

        /// <summary>
        /// 滚到顶部。⚠️ 是 <c>1f</c> 不是 <c>0f</c>（<c>verticalNormalizedPosition</c>：1 = 顶、0 = 底）。
        /// </summary>
        public void ScrollToTop()
        {
            if (Scroll != null) Scroll.verticalNormalizedPosition = 1f;
        }

        /// <summary>滚到底部。</summary>
        public void ScrollToBottom()
        {
            if (Scroll != null) Scroll.verticalNormalizedPosition = 0f;
        }

        /// <summary>
        /// 把第 <paramref name="index"/> 行滚进视野（顶对齐，<paramref name="padding"/> 为再往下的额外偏移）。
        /// <para>content 高度不大于视口时（= 根本没得滚）不动 —— 否则归一化位置会算出 NaN / 越界的值。</para>
        /// </summary>
        public void ScrollToIndex(int index, float padding = 0f)
        {
            if (Scroll == null || Content == null || Viewport == null) return;

            var scrollable = Content.rect.height - Viewport.rect.height;
            if (scrollable <= 0f) return;

            var offset = 0f;
            var count = Mathf.Min(index, _rowHeights.Count);
            for (var i = 0; i < count; i++) offset += _rowHeights[i] + Spacing;
            offset += Mathf.Max(0f, padding);

            Scroll.verticalNormalizedPosition = Mathf.Clamp01(1f - offset / scrollable);
        }
    }
}
