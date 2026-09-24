// ─────────────────────────────────────────────────────────────────────────────
// CloverEngine · Runtime/Presentation/DragGestureRouter.cs
// 「拖拽 vs 滚动」手势仲裁器 —— 纯逻辑（无 MonoBehaviour、无协程），由业务的拖拽事件驱动。
//
// 口径（阈值 / 让位条件已参数化）：
//   · `DefaultDragThresholdPx = 12f`（判"拖动"而不是"点击"的位移阈值；uGUI 自己先用
//     `EventSystem.pixelDragThreshold`（默认 10）挡一道，本阈值只用来**判方向**）；
//   · 指针拖出滚动列表上沿 = 升格为拖拽；
//   · 让位分支见下「让位规则」；
//   · 「压点击」标记（`ConsumeClickSuppressed`）：★ 收尾时必须清掉（理由见 <see cref="End"/> 的注释）。
//
// ★ 关键前提（决定了本件为什么必须存在；两条都有出处）：
//   ① uGUI 把 `pointerDrag` 判给**最靠前（最深层）**的那个 `IDragHandler`
//      （`PointerInputModule.ProcessDrag`）⇒ 格子上的本件拿到事件后，**必须自己决定**这次手势归谁，
//      否则"纵向拖动 = 滚动列表"和"把格子拖出来"会在同一个 GameObject 上互相抢。
//   ② uGUI 只在 `pointerEvent.pointerPress != pointerEvent.pointerDrag` 时才清 `eligibleForClick`
//      （`PointerInputModule.cs:388-397`）。把 `Button` 与本件挂在**同一个 GameObject** 上时两者相等
//      ⇒ 拖完**仍会**触发 `Button.onClick`（现象 = "拖动换位之后又顺手点了这张卡"）。
//      又因 `ReleaseMouse`（`StandaloneInputModule.cs:206-228`）的顺序是**先 click、后 endDrag**
//      ⇒ 标记只能在**拖动过程中**打上，由业务的点击处理读一次即清（见 <see cref="ConsumeClickSuppressed"/>）。
//
// 让位规则（优先级从高到低，一段手势**只有一个**归属）：
//   ① 指针已拖到**滚动区之外**（调用方给的 <c>escapedRegion</c>）⇒ 归**拖拽**
//      （必须优先于方向判定：列表在下方、目标槽位在上方时，"把格子拖到槽位"的主方向恰恰是**纵向**，
//        只按方向判会把它当滚动（实测：拖向槽位 Δ=(62.8, +251.3) 被判成 scroll，
//        格子一个都没动）。
//   ② 否则**纵向占优**（|dy| &gt; |dx|）⇒ 归滚动；**横向占优** ⇒ 归拖拽。
//   ③ 一旦定为拖拽就**不再改主意**；滚动中的手势若拖出滚动区仍会**升格**为拖拽
//      （真实拖拽手势一定是"先在列表里、再拖出去"）。
//   升格时**必须先给滚动收尾**（调用方转 `ScrollRect.OnEndDrag`），否则它会一直停在"拖动中"，
//   残留的速度 / 惯性会在下一帧继续挪内容。
//
// ★ 为什么是"返回 <see cref="Step"/> 让调用方去转发"，而不是本件自己搬 content：
//   `ScrollRect.OnBeginDrag/OnDrag/OnEndDrag` 是 `public virtual` 的正式事件入口，转发后
//   惯性 / 回弹 / 边界全走它的既有实现（⛔ 不重造轮子）；而且它是在**转发那一刻**才记
//   `m_PointerStartLocalCursor` / `m_StartPosition`，所以从手指中段接管**不会跳一下**。
//   ⇒ 本件只回答"这一拍该干什么"，转发动作留在业务（各项目承载体不同：ScrollRect / 自研列表）。
//
// 已知失效模式：
//   · 压点击标记**必须**在 <see cref="End"/> 里清掉：`ReleaseMouse` 是**先 click 后 endDrag**，
//     所以进入 End 时本次手势的尾巴（如果有）已经派发完；此刻清掉既不影响它，又避免
//     "松手落在别的对象上、那次 click 没来"时把标记留成 true —— 那种情况下，玩家**下一次正常点击**
//     （没越阈值、不产生 beginDrag 的纯点击）会被误判成"拖动的尾巴"而被吞掉。
//   · 阈值小于 `EventSystem.pixelDragThreshold` 也没用（轮不到本件）：别把阈值调到比引擎那道还小。
//   · 每段手势开始（<see cref="Begin"/>）时也要清一次标记：上一段若在别处结束，点击可能没来。
// ─────────────────────────────────────────────────────────────────────────────

using UnityEngine;

namespace CloverEngine
{
    /// <summary>
    /// 「拖拽 vs 滚动」手势仲裁器（**纯逻辑**）。
    ///
    /// <para>
    /// 生命周期：<see cref="Begin"/>（按下）→ 若干次 <see cref="Move"/>（移动）→ <see cref="End"/>（抬起）。
    /// 每次 <see cref="Move"/> 返回一个 <see cref="Step"/>，调用方据此把事件转发给
    /// <c>ScrollRect</c> 或走自己的拖拽逻辑。
    /// </para>
    /// <para>
    /// 本类**不引用** `ScrollRect` / `EventSystem` / `PointerEventData` —— 调用方负责把
    /// "指针有没有拖出滚动区"（<c>escapedRegion</c>）算好传进来（那是几何，属调用方的画布结构）。
    /// </para>
    /// </summary>
    public sealed class DragGestureRouter
    {
        /// <summary>默认位移阈值（像素）。uGUI 自己先用 `EventSystem.pixelDragThreshold`（默认 10）挡一道，这道只判方向。</summary>
        public const float DefaultDragThresholdPx = 12f;

        /// <summary>手势归属。</summary>
        public enum Gesture
        {
            /// <summary>还没超过阈值，意图未定。</summary>
            Undecided,

            /// <summary>本次手势归滚动列表。</summary>
            Scroll,

            /// <summary>本次手势归内容拖拽。</summary>
            Content,
        }

        /// <summary>本拍该做什么（调用方按此转发）。</summary>
        public enum Step
        {
            /// <summary>什么也不做（还没过阈值 / 手势已结束）。</summary>
            None,

            /// <summary>接管滚动：调用方转 <c>ScrollRect.OnBeginDrag</c>。</summary>
            BeginScroll,

            /// <summary>继续滚动：转 <c>ScrollRect.OnDrag</c>。</summary>
            MoveScroll,

            /// <summary>滚动收尾：转 <c>ScrollRect.OnEndDrag</c>。</summary>
            EndScroll,

            /// <summary>开始拖拽内容：调用方起拖拽表现（幽灵体 / 高亮…）。</summary>
            BeginContent,

            /// <summary>拖拽内容中：上报指针位移。</summary>
            MoveContent,

            /// <summary>拖拽结束：调用方做落位判定。</summary>
            EndContent,

            /// <summary>
            /// 由滚动**升格**为拖拽：调用方**先**转 <c>ScrollRect.OnEndDrag</c>（给滚动收尾），
            /// **再**走 <see cref="BeginContent"/> 的动作 + 当作 <see cref="MoveContent"/> 上报一次。
            /// </summary>
            EscalateToContent,
        }

        /// <summary>
        /// 判"这是一次拖动而不是一次点击"的位移阈值（像素）。
        /// <para>⛔ 参数化：默认 <see cref="DefaultDragThresholdPx"/>，各项目按自己的 DPI / 画布缩放调。</para>
        /// </summary>
        public float DragThresholdPx { get; set; }

        /// <summary>
        /// 本手势是否允许"纵向占优 ⇒ 归滚动"。<c>false</c> ⇒ 所有越阈值手势都归拖拽
        /// （例如已经选中的槽位那一行不允许滚动）。
        /// </summary>
        public bool AllowScroll { get; set; }

        /// <summary>当前手势归属（<see cref="Begin"/> 后为 <see cref="Gesture.Undecided"/>）。</summary>
        public Gesture Current { get; private set; }

        /// <summary>最近一次手势实际走的分支名（自检 / 驱动脚本读它，避免"只看日志"）。</summary>
        public string LastGestureName { get; private set; }

        private Vector2 _start;
        private bool _clickSuppressed;

        /// <summary>
        /// 建一个仲裁器。
        /// </summary>
        /// <param name="dragThresholdPx">位移阈值（像素）；&lt;= 0 ⇒ 用 <see cref="DefaultDragThresholdPx"/>。</param>
        /// <param name="allowScroll">是否允许"纵向占优 ⇒ 归滚动"。</param>
        public DragGestureRouter(float dragThresholdPx = DefaultDragThresholdPx, bool allowScroll = true)
        {
            DragThresholdPx = dragThresholdPx > 0f ? dragThresholdPx : DefaultDragThresholdPx;
            AllowScroll = allowScroll;
            Current = Gesture.Undecided;
            LastGestureName = "undecided";
        }

        /// <summary>本次手势是否已经"真的拖动过"（用来压掉随后那次 <c>Button.onClick</c>）。</summary>
        public bool ClickSuppressed => _clickSuppressed;

        /// <summary>
        /// 按下：开一段新手势，清掉上一段可能残留的"压点击"标记（上一段若在别处结束，点击可能没来）。
        /// </summary>
        /// <param name="pointer">按下时的指针屏幕坐标。</param>
        public void Begin(Vector2 pointer)
        {
            Current = Gesture.Undecided;
            _start = pointer;
            _clickSuppressed = false;
            LastGestureName = "undecided";
        }

        /// <summary>
        /// 指针移动一拍。返回调用方该做的事。
        /// </summary>
        /// <param name="pointer">当前指针屏幕坐标。</param>
        /// <param name="escapedScrollRegion">
        /// 指针是否已**离开滚动区**（拖出列表上沿等）。由调用方算（几何属调用方的画布结构），
        /// ⛔ 不要在滚动区内的判定上偷懒传 false —— 那正是"格子拖不出来"的成因。
        /// </param>
        public Step Move(Vector2 pointer, bool escapedScrollRegion)
        {
            // ① 已定 = 拖拽 ⇒ 只上报位移（本次手势不再改主意）。
            if (Current == Gesture.Content) return Step.MoveContent;

            // ② 正在滚动的这次手势里，指针又拖出了滚动区 ⇒ **升格**为拖拽。
            if (Current == Gesture.Scroll)
            {
                if (AllowScroll && escapedScrollRegion)
                {
                    Current = Gesture.Content;
                    LastGestureName = "content";
                    return Step.EscalateToContent;
                }
                return Step.MoveScroll;
            }

            // ③ 意图未定 ⇒ 过了阈值才判：先看"是不是已经拖出滚动区"，再看主方向。
            var delta = pointer - _start;
            if (delta.magnitude < DragThresholdPx) return Step.None; // 还没过阈值：交给点击

            // 从这一刻起，本次手势的尾巴（pointerUp 之后那次 click）必须被压掉。
            _clickSuppressed = true;

            if (AllowScroll && !escapedScrollRegion && Mathf.Abs(delta.y) > Mathf.Abs(delta.x))
            {
                Current = Gesture.Scroll;
                LastGestureName = "scroll";
                return Step.BeginScroll; // 此刻接管：ScrollRect 以当前指针 / 当前位置为起点，不会跳
            }

            Current = Gesture.Content;
            LastGestureName = "content";
            return Step.BeginContent;
        }

        /// <summary>
        /// 抬起：结束本段手势，返回收尾动作（<see cref="Step.EndScroll"/> / <see cref="Step.EndContent"/> /
        /// <see cref="Step.None"/>）。
        /// <para>
        /// ★ 这里**一并清掉"压点击"标记**，理由见文件头「已知失效模式」——`ReleaseMouse` 是
        /// **先 click 后 endDrag**，此刻清既不影响本次尾巴，又能避免残留标记吞掉下一次正常点击。
        /// </para>
        /// </summary>
        public Step End()
        {
            var g = Current;
            Current = Gesture.Undecided;

            _clickSuppressed = false;

            if (g == Gesture.Scroll) return Step.EndScroll;
            if (g == Gesture.Content) return Step.EndContent;
            return Step.None;
        }

        /// <summary>
        /// 读一次并清掉"压点击"标记：业务的点击处理**第一行**调它，
        /// 返回 <c>true</c> 表示"这次点击是拖动的尾巴，忽略"。
        /// </summary>
        public bool ConsumeClickSuppressed()
        {
            if (!_clickSuppressed) return false;
            _clickSuppressed = false;
            return true;
        }
    }
}
