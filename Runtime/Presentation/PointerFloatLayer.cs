// ─────────────────────────────────────────────────────────────────────────────
// CloverEngine · Runtime/Presentation/PointerFloatLayer.cs
// 「指针悬停」两个通用件：
//   ① PointerFloatLayer        —— 跟随指针的浮层容器（tooltip 形态：按屏幕点定位、从指针右上展开、
//                                 贴边翻转、跟随鼠标、可复用 / 可池化）；
//   ② PointerHoverRelay        —— 把 uGUI `IPointerEnter/ExitHandler` 转成两个回调的接线件；
//   （① 的定位数学单独放在同文件的 PointerFloatPlacement —— 纯函数，离线可断言。）
//
// 机制口径：
//     · 浮层根建为「左上角为轴心」+ 记下所属 Canvas；
//     · Show / Hide —— 显隐（尺寸由调用方按自己的内容算好）；
//     · 每帧：屏幕点 → 画布局部点 → 贴边翻转；
//     · 指针偏移默认 CursorOffsetX = 22 / CursorOffsetY = -18（画布单位，可覆盖）；
//     · 悬停接线 = `IPointerEnterHandler` / `IPointerExitHandler` → 两个 `Action` 回调（零耦合）。
//
// 为什么是引擎缺口：
//   · 全引擎 `IPointerEnterHandler|IPointerExitHandler|EventTrigger` **0 命中** ⇒ 业务要悬停反馈
//     只能各自 `AddComponent` + 自己实现接口；
//   · `UIWidgets.cs` 的通用件只有 Toast / FloatText / Loading / Confirm / RedDot / Guide ——
//     `FloatTextLayer` 跟随的是**世界坐标的投影**（`WorldToScreenPoint`），**不跟指针**；
//     全引擎对"按指针定位的浮层"没有通用件。
//
// ⛔ 本件只做**容器 / 定位 / 显隐 / 复用**：
//   内容（底板 Image、文本框、字号、配色、换行测量、品质色…）**全部由调用方注入** ——
//   本件不建任何 Text / Image，不含任何色值 / 文案 / 字号常量，不引用任何业务类型
//   （例：物品品质配色仍留在业务侧）。
//
// ★ 为什么两件都放**新文件**，而不是并进 `UIWidgets.cs` / `UIWidgetControls.cs`：
//   ① 同一能力族（指针悬停 → 跟随浮层），一处读得完；
//   ② ⛔ 与 `结构规则.md` §4.4「已有同类能力不准再起第二套」不冲突：这两件是引擎里该能力族的首件。
//
// 定位契约（改一条 = 改所有调用方的观感，见 `PointerFloatPlacement.Resolve`）：
//   · 浮层轴心 pivot = **(0, 1) 左上角** ⇒ 默认从指针的**右下**方向展开；
//   · 尺寸由调用方 `SetSize`（本件不猜内容大小）；
//   · 贴边 = **关于指针镜像**翻到另一侧（不是"把框挪回画布内"）；
//   · 最后夹进画布（浮层比画布还大 / 指针在画布外 ⇒ 钉在边/角上，⛔ 绝不摆到画布外）。
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

namespace CloverEngine
{
    /// <summary>
    /// 浮层定位的**纯函数**（无状态、不碰任何 Unity 运行时对象 ⇒ 可被离线宿主 / EditMode 测试直接断言）。
    /// <para>
    /// 只回答一件事：**左上角轴心的浮层，该摆在画布局部坐标的哪里**。
    /// 不读 <c>Time</c> / <c>Game.Input</c> / 帧号，同输入 ⇒ 同输出（逐位）。
    /// </para>
    /// </summary>
    public static class PointerFloatPlacement
    {
        /// <summary>默认横向偏移：指针**右侧** 22（画布单位）。</summary>
        public const float DefaultCursorOffsetX = 22f;

        /// <summary>默认纵向偏移：指针**下方** 18（画布单位；负 = 向下）。</summary>
        public const float DefaultCursorOffsetY = -18f;

        /// <summary>默认离画布边的留白（4f）。</summary>
        public const float DefaultEdgeMargin = 4f;

        /// <summary>用默认偏移 / 留白解算浮层位置（<paramref name="size"/> 由调用方按自己的内容算好）。</summary>
        /// <param name="pointerLocal">指针在**画布矩形**里的局部点（`ScreenPointUtil.TryScreenToLocalInRect` 的输出）。</param>
        /// <param name="size">浮层尺寸（宽 / 高，画布单位；负值 / 非有限值按 0）。</param>
        /// <param name="canvasRect">画布矩形（`RectTransform.rect`）。</param>
        public static Vector2 Resolve(Vector2 pointerLocal, Vector2 size, Rect canvasRect)
            => Resolve(pointerLocal, size, canvasRect,
                new Vector2(DefaultCursorOffsetX, DefaultCursorOffsetY), DefaultEdgeMargin);

        /// <summary>
        /// 解算浮层的 <c>anchoredPosition</c>。契约：浮层 **pivot = (0,1) 左上角**、尺寸 = <paramref name="size"/>。
        /// <para>三步，顺序固定：</para>
        /// <list type="number">
        /// <item>指针 + 偏移（默认 = 右下展开）；</item>
        /// <item>贴边**关于指针镜像**翻到另一侧：横向 <c>x = 指针.x - 偏移.x - 宽</c>，
        ///       纵向 <c>y = 指针.y - 偏移.y + 高</c>（偏移取反 = 同一道缝隙换到另一侧）；</item>
        /// <item>兜底夹进画布（<c>[xMin+margin, xMax-margin-宽]</c> / <c>[yMin+margin+高, yMax-margin]</c>）——
        ///       浮层比画布还大 / 指针在画布外时，夹取区间会退化成单点（左对齐 / 顶对齐），**确定性且不抖**。</item>
        /// </list>
        /// </summary>
        /// <param name="pointerLocal">指针画布局部点（须是有限值；本件不对指针做兜底，因为坐标来源自带 bool 失败态）。</param>
        /// <param name="size">浮层尺寸（画布单位）。</param>
        /// <param name="canvasRect">画布矩形。</param>
        /// <param name="cursorOffset">指针偏移（默认 <see cref="DefaultCursorOffsetX"/> / <see cref="DefaultCursorOffsetY"/>）。</param>
        /// <param name="edgeMargin">离画布边留白（负值 / 非有限值按 0）。</param>
        public static Vector2 Resolve(Vector2 pointerLocal, Vector2 size, Rect canvasRect,
            Vector2 cursorOffset, float edgeMargin)
        {
            var w = Sanitize(size.x);
            var h = Sanitize(size.y);
            var margin = Sanitize(edgeMargin);

            // ① 默认位置：指针 + 偏移（pivot 左上 ⇒ 这个点就是框的左上角）
            var x = pointerLocal.x + cursorOffset.x;
            var y = pointerLocal.y + cursorOffset.y;

            // ② 贴边翻转：**关于指针镜像**（左右 / 上下各判一次，可同时翻转 = 指针在角上）
            if (x + w > canvasRect.xMax - margin) x = pointerLocal.x - cursorOffset.x - w;
            if (y - h < canvasRect.yMin + margin) y = pointerLocal.y - cursorOffset.y + h;

            // ③ 兜底夹取：夹取区间恒满足 min ≤ max（浮层超过画布时区间退化成单点），⛔ 不会 NaN / 不会左右横跳
            var minX = canvasRect.xMin + margin;
            var maxX = Mathf.Max(minX, canvasRect.xMax - margin - w);
            x = Mathf.Clamp(x, minX, maxX);

            var maxY = canvasRect.yMax - margin;
            var minY = Mathf.Min(maxY, canvasRect.yMin + margin + h);
            y = Mathf.Clamp(y, minY, maxY);

            return new Vector2(x, y);
        }

        /// <summary>尺寸 / 留白的兜底：负值与非有限值一律按 0（否则会把节点摆到 NaN）。</summary>
        private static float Sanitize(float v)
            => float.IsNaN(v) || float.IsInfinity(v) || v < 0f ? 0f : v;
    }

    /// <summary>
    /// 跟随指针的浮层容器（tooltip 形态）：**只管容器 / 定位 / 显隐 / 复用**，内容由调用方注入。
    /// <para>
    /// 生命周期（非 MonoBehaviour，由持有者驱动）：
    /// <c>Create</c>（建节点 + 记 Canvas）→ 每次显示 <c>SetSize</c> + <c>Show</c> →
    /// 每帧 <c>Tick</c> → <c>Hide</c> / <c>Release</c>（归还池）/ <c>Destroy</c>。
    /// </para>
    /// <para>
    /// 调用方把内容挂到 <see cref="Content"/>（一个 pivot = 左上角、尺寸由 <see cref="SetSize"/> 控制的
    /// 空 RectTransform）下：底板 Image、Text 等全部自建（本件不建任何内容节点）。
    /// </para>
    /// <para>
    /// <b>池化口径</b>：<see cref="Acquire"/> / <see cref="Release"/> 复用**整棵节点树**（省掉"每次悬停
    /// 新建 / 销毁一棵 uGUI 树"的分配）。池是静态的、上限 <see cref="PoolCapacity"/>；
    /// 池项在 <c>Acquire</c> 时若已被销毁（父节点被 <c>Destroy</c> 会连带销毁子节点）则**直接丢弃**，
    /// 不会把 null 发给调用方。⛔ 只想"一个面板一个浮层、反复 Show/Hide"时用 <see cref="Create"/> 即可
    /// （那本身就是复用），不必走池。
    /// </para>
    /// </summary>
    public sealed class PointerFloatLayer
    {
        /// <summary>限频日志 / 单次日志的标签。</summary>
        public const string Tag = "PointerFloat";

        /// <summary>池上限（超过则 <see cref="Release"/> 直接销毁节点，避免池无限长）。</summary>
        public const int PoolCapacity = 4;

        private static readonly Stack<PointerFloatLayer> Pool = new Stack<PointerFloatLayer>();

        private readonly RectTransform _content;
        private RectTransform _canvas;
        private Camera _canvasCamera;
        private Vector2 _size;
        private bool _visible;
        private bool _warnedNoInput;

        private PointerFloatLayer(RectTransform content)
        {
            _content = content;
        }

        /// <summary>内容宿主（pivot = 左上角）：调用方把底板 / 文本挂到这里。</summary>
        public RectTransform Content => _content;

        /// <summary>所在画布矩形（取不到时为 <c>null</c> ⇒ 位置不更新，见 <see cref="Tick"/>）。</summary>
        public RectTransform Canvas => _canvas;

        /// <summary>是否处于显示态（由 <see cref="Show"/> / <see cref="Hide"/> 维护）。</summary>
        public bool IsVisible => _visible;

        /// <summary>当前浮层尺寸（由 <see cref="SetSize"/> 写入）。</summary>
        public Vector2 Size => _size;

        /// <summary>
        /// 在 <paramref name="parent"/> 下建一个浮层（默认隐藏 —— 调用方首次 <see cref="Show"/> 才可见）。
        /// </summary>
        /// <param name="parent">宿主（通常是面板自己的根节点；<c>null</c> ⇒ 挂到场景根，通常取不到画布）。</param>
        /// <param name="name">节点名（默认 <c>PointerFloat</c>；调用方按自己的层次命名习惯改）。</param>
        /// <param name="canvas">画布矩形；<c>null</c> ⇒ 从 <paramref name="parent"/> 向上找（取不到时降频 Warn）。</param>
        public static PointerFloatLayer Create(Transform parent, string name = "PointerFloat", RectTransform canvas = null)
        {
            if (string.IsNullOrEmpty(name)) name = "PointerFloat";

            var content = UIFactory.CreateCentered(name, parent, Vector2.zero, Vector2.zero);
            // 轴心 = 左上角：这是定位契约的一部分（`PointerFloatPlacement.Resolve` 就是按它算的）。
            // 锚点保持 (0.5,0.5) ⇒ `anchoredPosition` 就是"画布局部坐标"本身，与画布尺寸 / 缩放无关。
            content.pivot = new Vector2(0f, 1f);

            var layer = new PointerFloatLayer(content);
            layer.Bind(canvas != null ? canvas : ResolveCanvas(parent));
            content.gameObject.SetActive(false);
            return layer;
        }

        /// <summary>
        /// 从池里取一个浮层（没有可复用的就等价于 <see cref="Create"/>）。
        /// <para>取出的浮层：已挂到 <paramref name="parent"/> 下、已重新绑定 <paramref name="canvas"/>、已隐藏、
        /// 池里被销毁过的项会被丢弃。</para>
        /// </summary>
        public static PointerFloatLayer Acquire(Transform parent, string name = "PointerFloat", RectTransform canvas = null)
        {
            while (Pool.Count > 0)
            {
                var pooled = Pool.Pop();
                if (pooled._content == null) continue;   // Unity：父节点被销毁 ⇒ 子节点已销毁，弃用此项

                pooled._content.name = string.IsNullOrEmpty(name) ? "PointerFloat" : name;
                pooled._content.SetParent(parent, false);
                pooled.Bind(canvas != null ? canvas : ResolveCanvas(parent));
                pooled._visible = false;
                pooled._size = Vector2.zero;
                pooled._content.sizeDelta = Vector2.zero;
                pooled._content.gameObject.SetActive(false);
                return pooled;
            }

            return Create(parent, name, canvas);
        }

        /// <summary>归还到池（隐藏 + 保留节点树，供下一次 <see cref="Acquire"/> 复用）；池满 / 节点已销毁 ⇒ 直接销毁。</summary>
        public void Release()
        {
            Hide();
            if (_content == null) return;

            if (Pool.Count >= PoolCapacity)
            {
                Destroy();
                return;
            }

            Pool.Push(this);
        }

        /// <summary>设置内容尺寸（同时写进 <see cref="Content"/> 的 <c>sizeDelta</c>）；负值 / 非有限值按 0。</summary>
        public void SetSize(Vector2 size)
        {
            _size = new Vector2(
                float.IsNaN(size.x) || float.IsInfinity(size.x) || size.x < 0f ? 0f : size.x,
                float.IsNaN(size.y) || float.IsInfinity(size.y) || size.y < 0f ? 0f : size.y);

            if (_content != null) _content.sizeDelta = _size;
        }

        /// <summary>显示，并**立刻**摆到指针处（避免第一帧从旧位置飞过来 —— 项目侧原行为）。</summary>
        public void Show()
        {
            if (_content == null) return;

            _content.gameObject.SetActive(true);
            _visible = true;
            Tick();
        }

        /// <summary>隐藏（节点保留，供下一次 <see cref="Show"/> 复用）。</summary>
        public void Hide()
        {
            _visible = false;
            if (_content != null) _content.gameObject.SetActive(false);
        }

        /// <summary>
        /// 按**当前指针**（<c>Game.Input.MousePosition</c>，引擎自己的输入门面）摆好浮层；由持有者每帧驱动。
        /// <para>不可见 / 取不到画布 / 输入未挂载 ⇒ 直接返回（各留一条限频日志，⛔ 不静默挪框）。</para>
        /// </summary>
        public void Tick()
        {
            if (!_visible || _content == null) return;

            if (_canvas == null)
            {
                LogThrottle.WarnThrottled(Tag, "canvas.missing",
                    "取不到所属 Canvas ⇒ 浮层停在最后位置（不跟随指针）；" +
                    "多半是宿主节点还没挂到画布下（或父节点传了 null）");
                return;
            }

            var input = Game.Input;
            if (input == null)
            {
                if (!_warnedNoInput)
                {
                    _warnedNoInput = true;
                    LogThrottle.WarnOnce(Tag, "input.missing",
                        "Game.Input 未挂载（CloverInput.Init 未调用）⇒ 浮层不跟随指针（停在最后位置）");
                }

                return;
            }

            var screen = input.MousePosition;
            if (!ScreenPointUtil.TryScreenToLocalInRect(_canvas, new Vector2(screen.x, screen.y),
                    _canvasCamera, out var local))
            {
                LogThrottle.WarnThrottled(Tag, "convert.fail",
                    "屏幕点 → 画布局部点换算失败 ⇒ 浮层位置不更新（本次不挪框，避免摆到原点）");
                return;
            }

            _content.anchoredPosition = PointerFloatPlacement.Resolve(local, _size, _canvas.rect);
        }

        /// <summary>销毁节点（不再复用）。</summary>
        public void Destroy()
        {
            _visible = false;
            if (_content != null) UnityEngine.Object.Destroy(_content.gameObject);
        }

        /// <summary>绑定画布：同时记下"按画布模式该用的换算相机"（Overlay ⇒ null，见 `ScreenPointUtil` 文件头）。</summary>
        private void Bind(RectTransform canvas)
        {
            _canvas = canvas;
            _canvasCamera = canvas != null ? ScreenPointUtil.CameraForCanvas(canvas.GetComponent<Canvas>()) : null;
        }

        /// <summary>从宿主向上找画布（项目侧原口径：`GetComponentInParent&lt;Canvas&gt;()`）。</summary>
        private static RectTransform ResolveCanvas(Transform parent)
        {
            if (parent == null) return null;
            var canvas = parent.GetComponentInParent<Canvas>();
            return canvas != null ? canvas.transform as RectTransform : null;
        }
    }

    /// <summary>
    /// 悬停事件接线件：把 uGUI 的 <see cref="IPointerEnterHandler"/> / <see cref="IPointerExitHandler"/>
    /// 转成两个回调。出处 `UI/HoverTarget.cs:24-43`（逐字同语义）。
    /// <para>
    /// 只做转发、不做任何判定：回调为 <c>null</c> ⇒ 什么都不做（"没接线"不是异常）。
    /// 用法：<c>go.AddComponent&lt;P&gt;()</c> 后给 <see cref="OnEnter"/> / <see cref="OnExit"/> 赋值。
    /// </para>
    /// <para>
    /// ⛔ 不是 <c>sealed</c>：项目侧保留自己的 <c>HoverTarget</c> 作为空子类，
    /// 以便既有调用点（<c>AddComponent&lt;HoverTarget&gt;()</c>）一字不改。
    /// </para>
    /// </summary>
    public class PointerHoverRelay : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        /// <summary>指针进入时调用（未给 = <c>null</c> ⇒ 什么都不做）。</summary>
        public Action OnEnter;

        /// <summary>指针离开时调用（未给 = <c>null</c> ⇒ 什么都不做）。</summary>
        public Action OnExit;

        /// <inheritdoc/>
        public void OnPointerEnter(PointerEventData eventData)
        {
            if (OnEnter != null) OnEnter();
        }

        /// <inheritdoc/>
        public void OnPointerExit(PointerEventData eventData)
        {
            if (OnExit != null) OnExit();
        }
    }
}
