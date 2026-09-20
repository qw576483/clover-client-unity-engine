using System;
using UnityEngine;

namespace CloverEngine
{
    /// <summary>
    /// 引擎统一按键枚举（后端无关）。
    ///
    /// 业务代码只写 GameKey，不写 KeyCode（旧输入）或 Key（新输入）——
    /// 由 CloverEngine 的 Input 模块在运行时把 GameKey 翻译成当前生效输入后端的实际按键。
    /// 这样 Active Input Handling 无论是「Input Manager(Old)」「Input System Package(New)」
    /// 还是「Both」，业务代码都不用改。
    /// </summary>
    public enum GameKey
    {
        None = 0,

        // 字母 A-Z（连续，便于批量映射）
        A, B, C, D, E, F, G, H, I, J, K, L, M,
        N, O, P, Q, R, S, T, U, V, W, X, Y, Z,

        // 主键盘数字 0-9（连续）
        Num0, Num1, Num2, Num3, Num4, Num5, Num6, Num7, Num8, Num9,

        LeftArrow, RightArrow, UpArrow, DownArrow,

        Space, Enter, KeypadEnter, Escape, Backspace, Tab,
        LeftShift, RightShift, LeftCtrl, RightCtrl, LeftAlt, RightAlt,

        F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,

        Semicolon, Comma, Period, Slash, Backslash, Minus, Equals, Plus,

        // 鼠标键（走 GetMouseButton 族，GetKey* 亦可识别）
        MouseLeft, MouseRight, MouseMiddle
    }

    /// <summary>
    /// 单帧输入快照。由 Input 模块每帧刷新，业务侧只读。
    /// <para>
    /// <b>成员保留 public 读写</b>（不改公开签名，避免打断既有代码），
    /// 但 <see cref="IInputManager.State"/> 交出去的**是副本**：业务对它的写入只改副本，
    /// 不会污染引擎内部输入快照。真正的只读语义由那一层保证，见
    /// <see cref="IInputManager.State"/> 的契约注释。
    /// </para>
    /// </summary>
    public class InputState
    {
        public Vector2 MoveDirection;
        public bool Skill1Down;
        public bool Skill2Down;
        public bool Skill3Down;
        public bool Skill4Down;
        public bool JumpDown;
        public bool DodgeDown;
        public bool InteractDown;
        public bool TouchDown;
    }

    /// <summary>
    /// 输入管理器接口。定义在 Core，实现由 Presentation 的 InputManager 提供
    /// （与 IEntityManager / IObjectPool / INetwork 同构，维持 asmdef 单向依赖）。
    ///
    /// 设计要点：
    ///  1) 后端无关——引擎自动探测当前生效的输入后端（旧 Input / 新 InputSystem / 无），
    ///     业务侧永远只调用本接口；
    ///  2) 永不抛异常——后端不可用时返回默认值并给出一次性、可操作的诊断日志，
    ///     绝不把 Unity 的 InvalidOperationException 抛进业务帧循环（否则 Update 中断 = 键鼠全灭）；
    ///  3) 基础设施归引擎——EnsureEventSystem 负责按后端挂对 UI 输入模块，
    ///     从根上消灭「两个 InputModule 并存 → 点击失效」这类问题。
    /// </summary>
    public interface IInputManager
    {
        /// <summary>
        /// 当前帧输入快照。
        /// <para>
        /// <b>契约：返回的是副本，改它不影响引擎</b>（<see cref="InputState"/> 的字段仍是 public 可写，
        /// 但那只是历史签名；实现必须把内部快照拷进一块**复用的**缓冲再交出去，
        /// 因此业务写 <c>Game.Input.State.Xxx = …</c> 不会篡改引擎本帧输入）。
        /// 缓冲复用 = 每次读取**不分配新对象**；同一帧多次读取拿到的是同一实例，别把它存起来跨帧比对。
        /// </para>
        /// </summary>
        InputState State { get; }

        /// <summary>输入后端是否可用（false 表示键鼠完全不可读，日志已给出原因）。</summary>
        bool Available { get; }

        /// <summary>当前生效的后端名："Legacy" / "InputSystem" / "None"。</summary>
        string BackendName { get; }

        /// <summary>是否已被业务侧锁定（锁定时所有读取返回默认值，State 全清零）。</summary>
        bool IsLocked { get; }

        /// <summary>
        /// 当前是否有触摸输入。
        /// <para>
        /// 【未接线】引擎内部（Runtime/Editor/Tests）当前无消费点，属**公开契约**（触屏统一接口）——
        /// 尚未接线，业务可直接使用，不要按"无人使用"删除。
        /// </para>
        /// </summary>
        bool HasTouch { get; }

        /// <summary>锁定输入（过场动画、结算弹窗等场景使用）。</summary>
        void Lock();

        /// <summary>解除锁定。</summary>
        void Unlock();

        // ───────────── 原始查询（后端无关） ─────────────

        /// <summary>按键是否处于按住状态。</summary>
        bool GetKey(GameKey key);

        /// <summary>按键本帧是否按下。</summary>
        bool GetKeyDown(GameKey key);

        /// <summary>按键本帧是否抬起。</summary>
        bool GetKeyUp(GameKey key);

        /// <summary>鼠标键是否按住（0=左 1=右 2=中）。</summary>
        bool GetMouseButton(int button);

        /// <summary>鼠标键本帧是否按下。</summary>
        bool GetMouseButtonDown(int button);

        /// <summary>鼠标键本帧是否抬起。</summary>
        bool GetMouseButtonUp(int button);

        /// <summary>鼠标屏幕坐标（与 Input.mousePosition 同值，左下角为原点）。</summary>
        Vector3 MousePosition { get; }

        /// <summary>本帧鼠标位移（像素）。</summary>
        Vector2 MouseDelta { get; }

        /// <summary>
        /// 读轴值。legacy 后端直接读 InputManager.asset 的轴定义；
        /// "Horizontal" / "Vertical" 在新后端下回退为 A/D、W/S 键盘。
        /// 轴不存在时返回 0 而不是抛异常。
        /// </summary>
        float GetAxis(string axis, bool raw = false);

        // ───────────── 高层动作（回调式） ─────────────
        //
        // 【本节整体未接线】引擎内部（Runtime/Editor/Tests）当前无 On*/Off* 的调用点，属**公开契约**
        // （业务可直接用回调式高层动作，免去每帧读 State 的手写派发）—— 尚未接线，业务可直接使用，
        // 不要按"无人使用"删除。State 快照（<see cref="State"/>）同样保留，二者可并存。

        /// <summary>注册移动回调（每帧移动向量非零时触发）。</summary>
        void OnMove(Action<Vector2> handler);

        /// <summary>注销移动回调。</summary>
        void OffMove(Action<Vector2> handler);

        /// <summary>注册技能回调（skillIndex 从 1 开始）。</summary>
        void OnSkill(int skillIndex, Action handler);

        /// <summary>注销技能回调。</summary>
        void OffSkill(int skillIndex, Action handler);

        /// <summary>注册跳跃回调。</summary>
        void OnJump(Action handler);

        /// <summary>注销跳跃回调。</summary>
        void OffJump(Action handler);

        /// <summary>注册闪避回调。</summary>
        void OnDodge(Action handler);

        /// <summary>注销闪避回调。</summary>
        void OffDodge(Action handler);

        /// <summary>注册交互回调。</summary>
        void OnInteract(Action handler);

        /// <summary>注销交互回调。</summary>
        void OffInteract(Action handler);

        // ───────────── 基础设施 ─────────────

        /// <summary>
        /// 确保场景中存在 EventSystem，且只挂载与当前输入后端匹配的 UI 输入模块。
        /// 幂等，可重复调用。
        /// </summary>
        void EnsureEventSystem();

        /// <summary>引擎每帧驱动（由 Game.Tick 调用，业务侧不要手动调）。</summary>
        void Tick();
    }
}
