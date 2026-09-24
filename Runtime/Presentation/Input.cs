using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.EventSystems;

namespace CloverEngine
{
    /// <summary>
    /// 输入后端抽象。引擎在运行时探测可用后端并选定其一，
    /// 上层 InputManager 只依赖本抽象，因此项目切不切 Active Input Handling 都不影响业务代码。
    /// </summary>
    internal interface IInputBackend
    {
        string Name { get; }
        bool Available { get; }
        string UnavailableReason { get; }
        bool GetKey(GameKey key);
        bool GetKeyDown(GameKey key);
        bool GetKeyUp(GameKey key);
        bool GetMouseButton(int button);
        bool GetMouseButtonDown(int button);
        bool GetMouseButtonUp(int button);
        Vector3 MousePosition { get; }
        Vector2 MouseDelta { get; }
        float GetAxis(string axis, bool raw);
        int TouchCount { get; }
        /// <summary>把键盘/手柄的按键写入高层动作快照。</summary>
        void SampleActions(InputState state);
    }

    // ══════════════════════════════════════════════════════════════════════
    // 后端 1：旧输入（UnityEngine.Input）
    // Active Input Handling 含「Input Manager (Old)」时可用（Old 或 Both）。
    // 注意：若 Player Settings 只勾了 Input System Package (New)，
    //       任何 Input.* 调用都会抛 InvalidOperationException，
    //       本后端在构造期探测一次即可安全判定可用性。
    // ══════════════════════════════════════════════════════════════════════
    internal class LegacyInputBackend : IInputBackend
    {
        public string Name => "Legacy";
        public bool Available { get; private set; }
        public string UnavailableReason { get; private set; }

        private static readonly Dictionary<GameKey, KeyCode> Map = BuildMap();

        private Vector3 _lastMouse;
        private bool _firstFrame = true;
        private Vector2 _cachedDelta;
        private int _deltaFrame = -1;

        public LegacyInputBackend()
        {
            try
            {
                // 最轻量的可用性探针：旧输入被禁用时这一句就会抛异常
                var _ = Input.mousePosition;
                Available = true;
            }
            catch (Exception e)
            {
                Available = false;
                UnavailableReason = e.GetType().Name + ": " + e.Message;
            }
        }

        private static Dictionary<GameKey, KeyCode> BuildMap()
        {
            var m = new Dictionary<GameKey, KeyCode>
            {
                [GameKey.LeftArrow] = KeyCode.LeftArrow,
                [GameKey.RightArrow] = KeyCode.RightArrow,
                [GameKey.UpArrow] = KeyCode.UpArrow,
                [GameKey.DownArrow] = KeyCode.DownArrow,
                [GameKey.Space] = KeyCode.Space,
                [GameKey.Enter] = KeyCode.Return,
                [GameKey.KeypadEnter] = KeyCode.KeypadEnter,
                [GameKey.Escape] = KeyCode.Escape,
                [GameKey.Backspace] = KeyCode.Backspace,
                [GameKey.Tab] = KeyCode.Tab,
                [GameKey.LeftShift] = KeyCode.LeftShift,
                [GameKey.RightShift] = KeyCode.RightShift,
                [GameKey.LeftCtrl] = KeyCode.LeftControl,
                [GameKey.RightCtrl] = KeyCode.RightControl,
                [GameKey.LeftAlt] = KeyCode.LeftAlt,
                [GameKey.RightAlt] = KeyCode.RightAlt,
                [GameKey.Semicolon] = KeyCode.Semicolon,
                [GameKey.Comma] = KeyCode.Comma,
                [GameKey.Period] = KeyCode.Period,
                [GameKey.Slash] = KeyCode.Slash,
                [GameKey.Backslash] = KeyCode.Backslash,
                [GameKey.Minus] = KeyCode.Minus,
                [GameKey.Equals] = KeyCode.Equals,
                [GameKey.Plus] = KeyCode.KeypadPlus,
            };
            for (int i = 0; i <= (int)(GameKey.Z - GameKey.A); i++)
                m[(GameKey)((int)GameKey.A + i)] = (KeyCode)((int)KeyCode.A + i);
            for (int i = 0; i <= (int)(GameKey.Num9 - GameKey.Num0); i++)
                m[(GameKey)((int)GameKey.Num0 + i)] = (KeyCode)((int)KeyCode.Alpha0 + i);
            for (int i = 0; i <= (int)(GameKey.F12 - GameKey.F1); i++)
                m[(GameKey)((int)GameKey.F1 + i)] = (KeyCode)((int)KeyCode.F1 + i);
            return m;
        }

        private static bool TryKeyCode(GameKey key, out KeyCode code)
        {
            return Map.TryGetValue(key, out code);
        }

        // 旧输入被禁用时这些调用会抛异常，必须兜住；但**不能**用闭包包住（GetKey 族是每帧高频路径，
        // 原来的 Guard(() => Input.GetKey(c)) 每次调用都新建闭包与委托，逐帧产生 GC）。
        public bool GetKey(GameKey key)
        {
            if (!Available) return false;
            if (key == GameKey.MouseLeft) return GetMouseButton(0);
            if (key == GameKey.MouseRight) return GetMouseButton(1);
            if (key == GameKey.MouseMiddle) return GetMouseButton(2);
            if (!TryKeyCode(key, out var c)) return false;
            try { return Input.GetKey(c); }
            catch (Exception) { return false; }
        }

        public bool GetKeyDown(GameKey key)
        {
            if (!Available) return false;
            if (key == GameKey.MouseLeft) return GetMouseButtonDown(0);
            if (key == GameKey.MouseRight) return GetMouseButtonDown(1);
            if (key == GameKey.MouseMiddle) return GetMouseButtonDown(2);
            if (!TryKeyCode(key, out var c)) return false;
            try { return Input.GetKeyDown(c); }
            catch (Exception) { return false; }
        }

        public bool GetKeyUp(GameKey key)
        {
            if (!Available) return false;
            if (key == GameKey.MouseLeft) return GetMouseButtonUp(0);
            if (key == GameKey.MouseRight) return GetMouseButtonUp(1);
            if (key == GameKey.MouseMiddle) return GetMouseButtonUp(2);
            if (!TryKeyCode(key, out var c)) return false;
            try { return Input.GetKeyUp(c); }
            catch (Exception) { return false; }
        }

        public bool GetMouseButton(int button)
        {
            if (!Available) return false;
            try { return Input.GetMouseButton(button); }
            catch (Exception) { return false; }
        }

        public bool GetMouseButtonDown(int button)
        {
            if (!Available) return false;
            try { return Input.GetMouseButtonDown(button); }
            catch (Exception) { return false; }
        }

        public bool GetMouseButtonUp(int button)
        {
            if (!Available) return false;
            try { return Input.GetMouseButtonUp(button); }
            catch (Exception) { return false; }
        }

        public Vector3 MousePosition
        {
            get
            {
                if (!Available) return Vector3.zero;
                try { return Input.mousePosition; }
                catch (Exception) { return Vector3.zero; }
            }
        }

        public Vector2 MouseDelta
        {
            get
            {
                // 每帧只结算一次：原实现每次读取都刷新 _lastMouse，同一帧第二次读取恒为 (0,0)，
                // 多个读取方（相机 / 业务）会互相吃掉位移。
                if (_deltaFrame == Time.frameCount) return _cachedDelta;

                var cur = MousePosition;
                _cachedDelta = _firstFrame ? Vector2.zero : new Vector2(cur.x - _lastMouse.x, cur.y - _lastMouse.y);
                _lastMouse = cur;
                _firstFrame = false;
                _deltaFrame = Time.frameCount;
                return _cachedDelta;
            }
        }

        /// <summary>
        /// 重置位移基准（失焦恢复、输入解锁、暂停恢复时调用）：下次读取的位移从 0 重新起算，
        /// 避免把"失焦 / 锁定期间累计的位移"当成一帧的位移（表现为视角 / 准星突跳）。
        /// </summary>
        public void ResetMouseBaseline()
        {
            _lastMouse = MousePosition;
            _firstFrame = true;
            _cachedDelta = Vector2.zero;
            _deltaFrame = -1;
        }

        public float GetAxis(string axis, bool raw)
        {
            if (!Available || string.IsNullOrEmpty(axis)) return 0f;
            // 轴未在 InputManager.asset 中定义时 Input.GetAxis* 会抛 ArgumentException，
            // 这里兜住，避免一个不存在的轴名把整帧 Update 打断。
            try { return raw ? Input.GetAxisRaw(axis) : Input.GetAxis(axis); }
            catch (Exception) { return 0f; }
        }

        public int TouchCount
        {
            get
            {
                if (!Available) return 0;
                try { return Input.touchCount; }
                catch (Exception) { return 0; }
            }
        }

        /// <summary>旧输入的手柄按键（JoystickButton0-6）不占用 GameKey 空间，直接按 KeyCode 读。</summary>
        private static bool Pad(int index)
        {
            try { return Input.GetKeyDown((KeyCode)((int)KeyCode.JoystickButton0 + index)); }
            catch (Exception) { return false; }
        }

        public void SampleActions(InputState s)
        {
            if (!Available) return;
            s.Skill1Down = GetKeyDown(GameKey.J) || Pad(3);
            s.Skill2Down = GetKeyDown(GameKey.K) || Pad(4);
            s.Skill3Down = GetKeyDown(GameKey.L) || Pad(5);
            s.Skill4Down = GetKeyDown(GameKey.Semicolon) || Pad(6);
            s.JumpDown = GetKeyDown(GameKey.Space) || Pad(0);
            s.DodgeDown = GetKeyDown(GameKey.LeftShift) || Pad(1);
            s.InteractDown = GetKeyDown(GameKey.F) || Pad(2);
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // 后端 2：新输入（UnityEngine.InputSystem）
    // 仅当 Active Input Handling 为「Input System Package (New)」或「Both」且装了包时可用。
    // 引擎 asmdef 不硬引用 Unity.InputSystem（否则没装包的项目直接编译失败），
    // 因此这里全部走反射，且保证任何一步失败都不抛到上层。
    // ══════════════════════════════════════════════════════════════════════
    internal class InputSystemBackend : IInputBackend
    {
        public string Name => "InputSystem";
        public bool Available { get; private set; }
        public string UnavailableReason { get; private set; }

        private Type _keyboardType, _mouseType, _gamepadType, _keyEnumType;
        private PropertyInfo _kbCurrent, _mouseCurrent, _gamepadCurrent, _kbIndexer, _tsCurrent;
        // 只缓存「GameKey → InputSystem.Key 枚举值 + 索引器参数数组」的解析结果，不缓存 control 实例：
        // control 属于具体设备，键盘热插拔后旧实例会失效。
        private sealed class KeyBinding
        {
            /// <summary>索引器参数数组只建一次（GetValue 每帧调用，new[] 会逐帧产生垃圾）。</summary>
            public object[] Index;
        }

        private readonly Dictionary<GameKey, KeyBinding> _keyEnumCache = new();
        private readonly HashSet<GameKey> _keyMiss = new();
        private static readonly Dictionary<(Type, string), PropertyInfo> PropCache = new();
        private static readonly Dictionary<Type, MethodInfo> ReadValueCache = new();

        public InputSystemBackend()
        {
            try
            {
                _keyboardType = Type.GetType("UnityEngine.InputSystem.Keyboard, Unity.InputSystem");
                _mouseType = Type.GetType("UnityEngine.InputSystem.Mouse, Unity.InputSystem");
                _gamepadType = Type.GetType("UnityEngine.InputSystem.Gamepad, Unity.InputSystem");
                // Key 当前是命名空间级枚举；再挂一个「嵌套」写法兜底，防将来版本挪位置
                _keyEnumType = Type.GetType("UnityEngine.InputSystem.Key, Unity.InputSystem")
                               ?? Type.GetType("UnityEngine.InputSystem.Keyboard+Key, Unity.InputSystem");

                if (_keyboardType == null || _mouseType == null || _keyEnumType == null)
                {
                    Available = false;
                    UnavailableReason = "未找到 Unity.InputSystem 程序集（com.unity.inputsystem 未安装）";
                    return;
                }

                _kbCurrent = _keyboardType.GetProperty("current", BindingFlags.Public | BindingFlags.Static);
                _mouseCurrent = _mouseType.GetProperty("current", BindingFlags.Public | BindingFlags.Static);
                if (_gamepadType != null)
                    _gamepadCurrent = _gamepadType.GetProperty("current", BindingFlags.Public | BindingFlags.Static);
                var touchscreenType = Type.GetType("UnityEngine.InputSystem.Touchscreen, Unity.InputSystem");
                if (touchscreenType != null)
                    _tsCurrent = touchscreenType.GetProperty("current", BindingFlags.Public | BindingFlags.Static);
                _kbIndexer = _keyboardType.GetProperty("Item", new[] { _keyEnumType });

                if (_kbIndexer == null || _mouseCurrent == null || _kbCurrent == null)
                {
                    Available = false;
                    UnavailableReason = "InputSystem 反射绑定失败（Keyboard/Mouse 结构不匹配）";
                    return;
                }

                // 功能探针：至少真能拿到一个设备（键盘 / 鼠标 / 手柄 / 触屏），且能解析按键枚举，才认为可用。
                // 只验证“类型存在”不够——那会出现「日志说可用、按键却全无反应」的死局；
                // 但**不能**要求 Keyboard.current 非空：纯触屏 / 只接手柄的设备上，那会把整个新后端判死，
                // 连它本来支持的手柄按键一起失效（旧后端又未必可用）。
                var hasDevice = Current(_kbCurrent) != null || Current(_mouseCurrent) != null
                                || Current(_gamepadCurrent) != null || Current(_tsCurrent) != null;
                if (!hasDevice)
                {
                    Available = false;
                    UnavailableReason = "InputSystem 已启用但未发现键盘/鼠标/手柄/触屏设备（或新后端未激活）";
                    return;
                }
                if (ResolveKeyEnum(GameKey.Space) == null)
                {
                    Available = false;
                    UnavailableReason = "InputSystem.Key 枚举名解析失败（com.unity.inputsystem 版本差异）";
                    return;
                }

                Available = true;
            }
            catch (Exception e)
            {
                Available = false;
                UnavailableReason = e.GetType().Name + ": " + e.Message;
            }
        }

        private static PropertyInfo Prop(object target, string name)
        {
            if (target == null) return null;
            var t = target.GetType();
            // 用 (Type, name) 复合键直接查缓存：原实现每次调用先拼 "FullName.name" 字符串，
            // 命中缓存也照样分配新串（Bool / GetValue 是每帧高频路径）。
            var cacheKey = (t, name);
            if (PropCache.TryGetValue(cacheKey, out var cached)) return cached;
            var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
            PropCache[cacheKey] = p;
            return p;
        }

        private static bool Bool(object target, string name)
        {
            var p = Prop(target, name);
            if (p == null) return false;
            try { return p.GetValue(target) is bool b && b; }
            catch (Exception) { return false; }
        }

        private static object Current(PropertyInfo currentProp)
        {
            if (currentProp == null) return null;
            try { return currentProp.GetValue(null); }
            catch (Exception) { return null; }
        }

        private object KeyControl(GameKey key)
        {
            if (!_keyEnumCache.TryGetValue(key, out var binding))
            {
                if (_keyMiss.Contains(key)) return null;
                var enumValue = ResolveKeyEnum(key);
                if (enumValue == null)
                {
                    _keyMiss.Add(key);   // 该 GameKey 在当前包版本里不存在，无需再试
                    return null;
                }
                binding = new KeyBinding { Index = new[] { enumValue } };
                _keyEnumCache[key] = binding;
            }

            var kb = Current(_kbCurrent);
            if (kb == null) return null;   // 键盘未接入：不写缓存，等接入后再解析
            try { return _kbIndexer.GetValue(kb, binding.Index); }
            catch (Exception) { return null; }
        }

        /// <summary>把 GameKey 解析为 InputSystem.Key 枚举值（枚举名跨版本有差异，逐个候选尝试）。</summary>
        private object ResolveKeyEnum(GameKey key)
        {
            if (_keyEnumType == null) return null;
            var candidates = KeyName(key);
            if (candidates == null) return null;
            foreach (var candidate in candidates)
            {
                try { return Enum.Parse(_keyEnumType, candidate, true); }
                catch (Exception) { }
            }
            return null;
        }

        /// <summary>GameKey → InputSystem Key 枚举名候选（不同版本命名有差异，逐个尝试）。</summary>
        private static string[] KeyName(GameKey key)
        {
            if (key >= GameKey.A && key <= GameKey.Z)
                return new[] { ((char)('A' + (int)(key - GameKey.A))).ToString() };

            if (key >= GameKey.Num0 && key <= GameKey.Num9)
            {
                var n = (int)(key - GameKey.Num0);
                // Input System 1.4 起 Alpha0 更名为 Digit0，老版本只有 Alpha0
                return new[] { "Digit" + n, "Alpha" + n };
            }

            if (key >= GameKey.F1 && key <= GameKey.F12)
                return new[] { "F" + (1 + (int)(key - GameKey.F1)) };

            switch (key)
            {
                case GameKey.LeftArrow: return new[] { "LeftArrow" };
                case GameKey.RightArrow: return new[] { "RightArrow" };
                case GameKey.UpArrow: return new[] { "UpArrow" };
                case GameKey.DownArrow: return new[] { "DownArrow" };
                case GameKey.Space: return new[] { "Space" };
                case GameKey.Enter: return new[] { "Enter" };
                case GameKey.KeypadEnter: return new[] { "NumpadEnter", "KeypadEnter" };
                case GameKey.Escape: return new[] { "Escape" };
                case GameKey.Backspace: return new[] { "Backspace" };
                case GameKey.Tab: return new[] { "Tab" };
                case GameKey.LeftShift: return new[] { "LeftShift" };
                case GameKey.RightShift: return new[] { "RightShift" };
                case GameKey.LeftCtrl: return new[] { "LeftCtrl" };
                case GameKey.RightCtrl: return new[] { "RightCtrl" };
                case GameKey.LeftAlt: return new[] { "LeftAlt" };
                case GameKey.RightAlt: return new[] { "RightAlt" };
                case GameKey.Semicolon: return new[] { "Semicolon" };
                case GameKey.Comma: return new[] { "Comma" };
                case GameKey.Period: return new[] { "Period" };
                case GameKey.Slash: return new[] { "Slash" };
                case GameKey.Backslash: return new[] { "Backslash" };
                case GameKey.Minus: return new[] { "Minus" };
                case GameKey.Equals: return new[] { "Equals" };
                case GameKey.Plus: return new[] { "NumpadPlus", "Plus" };
                default: return null;
            }
        }

        private static Vector2 ReadVector2(object control)
        {
            if (control == null) return Vector2.zero;
            try
            {
                var type = control.GetType();
                if (!ReadValueCache.TryGetValue(type, out var m))
                {
                    // MethodInfo 缓存一次：MouseDelta / MousePosition 每帧调用，逐次反射查找开销高。
                    m = type.GetMethod("ReadValue", Type.EmptyTypes);
                    ReadValueCache[type] = m;
                }
                var v = m?.Invoke(control, null);
                return v is Vector2 vec ? vec : Vector2.zero;
            }
            catch (Exception) { return Vector2.zero; }
        }

        private static string MouseButtonName(int button)
            => button == 0 ? "leftButton" : button == 1 ? "rightButton" : "middleButton";

        private bool MouseButton(int button)
        {
            var mouse = Current(_mouseCurrent);
            if (mouse == null) return false;
            var btn = Prop(mouse, MouseButtonName(button))?.GetValue(mouse);
            return Bool(btn, "isPressed");
        }

        public bool GetKey(GameKey key)
        {
            if (!Available) return false;
            if (key == GameKey.MouseLeft) return GetMouseButton(0);
            if (key == GameKey.MouseRight) return GetMouseButton(1);
            if (key == GameKey.MouseMiddle) return GetMouseButton(2);
            return Bool(KeyControl(key), "isPressed");
        }

        public bool GetKeyDown(GameKey key)
        {
            if (!Available) return false;
            if (key == GameKey.MouseLeft) return GetMouseButtonDown(0);
            if (key == GameKey.MouseRight) return GetMouseButtonDown(1);
            if (key == GameKey.MouseMiddle) return GetMouseButtonDown(2);
            return Bool(KeyControl(key), "wasPressedThisFrame");
        }

        public bool GetKeyUp(GameKey key)
        {
            if (!Available) return false;
            if (key == GameKey.MouseLeft) return GetMouseButtonUp(0);
            if (key == GameKey.MouseRight) return GetMouseButtonUp(1);
            if (key == GameKey.MouseMiddle) return GetMouseButtonUp(2);
            return Bool(KeyControl(key), "wasReleasedThisFrame");
        }

        public bool GetMouseButton(int button) => Available && MouseButton(button);

        public bool GetMouseButtonDown(int button) => MouseButtonFrame(button, "wasPressedThisFrame");

        public bool GetMouseButtonUp(int button) => MouseButtonFrame(button, "wasReleasedThisFrame");

        private bool MouseButtonFrame(int button, string frameProp)
        {
            if (!Available) return false;
            var mouse = Current(_mouseCurrent);
            if (mouse == null) return false;
            var btn = Prop(mouse, MouseButtonName(button))?.GetValue(mouse);
            return Bool(btn, frameProp);
        }

        public Vector3 MousePosition
        {
            get
            {
                var mouse = Current(_mouseCurrent);
                if (mouse == null) return Vector3.zero;
                var pos = Prop(mouse, "position")?.GetValue(mouse);
                var v = ReadVector2(pos);
                return new Vector3(v.x, v.y, 0f);
            }
        }

        public Vector2 MouseDelta
        {
            get
            {
                var mouse = Current(_mouseCurrent);
                if (mouse == null) return Vector2.zero;
                return ReadVector2(Prop(mouse, "delta")?.GetValue(mouse));
            }
        }

        public float GetAxis(string axis, bool raw)
        {
            if (!Available || string.IsNullOrEmpty(axis)) return 0f;
            if (string.Equals(axis, "Horizontal", StringComparison.OrdinalIgnoreCase))
                return Mathf.Clamp(
                    (GetKey(GameKey.D) ? 1f : 0f) - (GetKey(GameKey.A) ? 1f : 0f) + StickAxis(true), -1f, 1f);
            if (string.Equals(axis, "Vertical", StringComparison.OrdinalIgnoreCase))
                return Mathf.Clamp(
                    (GetKey(GameKey.W) ? 1f : 0f) - (GetKey(GameKey.S) ? 1f : 0f) + StickAxis(false), -1f, 1f);
            if (string.Equals(axis, "Mouse ScrollWheel", StringComparison.OrdinalIgnoreCase))
                return ReadScrollWheel();
            return 0f;
        }

        /// <summary>手柄左摇杆分量（平滑值，无手柄返回 0；键盘硬 ±1 与它相加后整体钳位）。</summary>
        private float StickAxis(bool horizontal)
        {
            try
            {
                var pad = Current(_gamepadCurrent);
                if (pad == null) return 0f;
                var stick = ReadVector2(Prop(pad, "leftStick")?.GetValue(pad));
                return horizontal ? stick.x : stick.y;
            }
            catch (Exception) { return 0f; }
        }

        /// <summary>
        /// 滚轮轴：把 InputSystem 的像素级 scroll 值换算成旧输入 "Mouse ScrollWheel" 的量纲
        /// （旧后端一格滚轮 ≈ 0.1）。原实现只认 Horizontal/Vertical，该轴恒 0 —— 滚轮缩放整条静默失效。
        /// </summary>
        private float ReadScrollWheel()
        {
            try
            {
                var mouse = Current(_mouseCurrent);
                if (mouse == null) return 0f;
                var v = ReadVector2(Prop(mouse, "scroll")?.GetValue(mouse));

                // 一格滚轮的像素值随平台 / 驱动不同（Windows 为 120），统一按 120 换算，
                // 与旧后端"一格 ≈ 0.1"对齐，保证按轴值缩放的逻辑（如第三人称相机）两端一致。
                const float scrollPixelsPerNotch = 120f;
                const float wheelUnitsPerNotch = 0.1f;
                return v.y / scrollPixelsPerNotch * wheelUnitsPerNotch;
            }
            catch (Exception) { return 0f; }
        }

        /// <summary>
        /// 触屏点数：经反射读 <c>Touchscreen.current.activeTouches</c>（老版本退化为 primaryTouch.press）。
        /// 原实现恒返回 0，而 InputManager 优先选新后端 ⇒ HasTouch / TouchDown 恒 false，移动端触摸静默失效。
        /// </summary>
        public int TouchCount
        {
            get
            {
                if (!Available || _tsCurrent == null) return 0;
                try
                {
                    var ts = Current(_tsCurrent);
                    if (ts == null) return 0;

                    var active = Prop(ts, "activeTouches")?.GetValue(ts);
                    if (active != null)
                    {
                        var count = Prop(active, "Count")?.GetValue(active);
                        return count is int n ? n : 0;
                    }

                    // 老版本 inputsystem 没有 activeTouches：退化为"主触点是否按下"。
                    var primary = Prop(ts, "primaryTouch")?.GetValue(ts);
                    if (primary == null) return 0;
                    return Bool(Prop(primary, "press")?.GetValue(primary), "isPressed") ? 1 : 0;
                }
                catch (Exception) { return 0; }
            }
        }

        public void SampleActions(InputState s)
        {
            if (!Available) return;
            s.Skill1Down = GetKeyDown(GameKey.J) || Pad("buttonNorth");
            s.Skill2Down = GetKeyDown(GameKey.K) || Pad("leftShoulder");
            s.Skill3Down = GetKeyDown(GameKey.L) || Pad("rightShoulder");
            s.Skill4Down = GetKeyDown(GameKey.Semicolon) || Pad("rightTrigger");
            s.JumpDown = GetKeyDown(GameKey.Space) || Pad("buttonSouth");
            s.DodgeDown = GetKeyDown(GameKey.LeftShift) || Pad("buttonEast");
            s.InteractDown = GetKeyDown(GameKey.F) || Pad("buttonWest");
        }

        private bool Pad(string buttonName)
        {
            var pad = Current(_gamepadCurrent);
            if (pad == null) return false;
            return Bool(Prop(pad, buttonName)?.GetValue(pad), "wasPressedThisFrame");
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // 后端 3：不可用（两个后端都探测失败）——所有读取返回默认值，绝不抛异常
    // ══════════════════════════════════════════════════════════════════════
    internal class NullInputBackend : IInputBackend
    {
        public string Name => "None";
        public bool Available => false;
        public string UnavailableReason { get; }

        public NullInputBackend(string reason) => UnavailableReason = reason;

        public bool GetKey(GameKey key) => false;
        public bool GetKeyDown(GameKey key) => false;
        public bool GetKeyUp(GameKey key) => false;
        public bool GetMouseButton(int button) => false;
        public bool GetMouseButtonDown(int button) => false;
        public bool GetMouseButtonUp(int button) => false;
        public Vector3 MousePosition => Vector3.zero;
        public Vector2 MouseDelta => Vector2.zero;
        public float GetAxis(string axis, bool raw) => 0f;
        public int TouchCount => 0;
        public void SampleActions(InputState state) { }
    }

    /// <summary>
    /// 输入管理实现：持有选中后端，向业务暴露后端无关的稳定 API。
    /// 由 CloverInput.Init 构造并挂接到 Game.Input，随后由 Game.Tick 每帧驱动。
    /// </summary>
    internal class InputManager : IInputManager
    {
        private readonly InputState _state = new();
        // 对外的快照缓冲（**复用**，不每帧 new）：State 每次读取把 _state 拷进它再交出去，
        // 业务改它只改副本，引擎内部 _state 不受影响（见 IInputManager.State 的契约注释）。
        private readonly InputState _snapshot = new();
        private readonly IInputBackend _backend;
        private readonly List<Action<Vector2>> _moveHandlers = new();
        private readonly Dictionary<int, List<Action>> _skillHandlers = new();
        private readonly List<Action> _jumpHandlers = new();
        private readonly List<Action> _dodgeHandlers = new();
        private readonly List<Action> _interactHandlers = new();

        /// <summary>
        /// 当前帧输入快照：**副本**（契约见 <see cref="IInputManager.State"/>）。
        /// 拷贝进复用的 <see cref="_snapshot"/> 再返回，既不外泄内部实例、也不产生每帧分配。
        /// </summary>
        public InputState State
        {
            get
            {
                _snapshot.MoveDirection = _state.MoveDirection;
                _snapshot.Skill1Down = _state.Skill1Down;
                _snapshot.Skill2Down = _state.Skill2Down;
                _snapshot.Skill3Down = _state.Skill3Down;
                _snapshot.Skill4Down = _state.Skill4Down;
                _snapshot.JumpDown = _state.JumpDown;
                _snapshot.DodgeDown = _state.DodgeDown;
                _snapshot.InteractDown = _state.InteractDown;
                _snapshot.TouchDown = _state.TouchDown;
                return _snapshot;
            }
        }
        public bool Available => _backend.Available;
        public string BackendName => _backend.Name;
        public bool IsLocked { get; private set; }
        // 契约：锁定时"所有读取"返回默认值 —— HasTouch 也必须受锁约束。
        public bool HasTouch => !IsLocked && _backend.TouchCount > 0;

        // 刻意**不**受 IsLocked 约束：它回答"UI 是否吃掉指针"这一状态事实，不是输入读取
        // （见 IInputManager.PointerOverUi 的说明）。后端无关：uGUI 命中与旧/新输入后端都无关。
        public bool PointerOverUi => InputInfrastructure.PointerOverUi();

        // 失焦 / 回前台检测（见 Tick）：失焦恢复、解锁时重置鼠标位移基准，
        // 否则回前台第一帧的位移是"失焦期间累计量"，视角 / 准星会突跳。
        private bool _wasFocused = true;

        public InputManager()
        {
            var legacy = new LegacyInputBackend();
            var modern = new InputSystemBackend();

            // 优先新输入系统（InputSystem）；探测不到才退回旧输入（Legacy）。
            if (modern.Available)
                _backend = modern;
            else if (legacy.Available)
                _backend = legacy;
            else
                _backend = new NullInputBackend("Legacy: " + legacy.UnavailableReason
                                                + " | InputSystem: " + modern.UnavailableReason);

            LogDiagnostics(_backend, modern, legacy.Available);
        }

        /// <summary>一次性诊断日志：说清「现在跑的是哪套后端」以及后端不可用时怎么修。</summary>
        private static void LogDiagnostics(IInputBackend chosen, InputSystemBackend modern, bool legacyOk)
        {
            // 统一走 Game.Logger（未 Launch 时指向 ConsoleLogger，永不为 null）：诊断要能进日志文件，
            // 否则线上查"键鼠全灭"没有依据（裸 Debug.Log 不进日志文件）。
            if (!chosen.Available)
            {
                Game.Logger?.Error("Input",
                    "输入后端不可用，键鼠将完全无响应。原因：" + chosen.UnavailableReason + "\n" +
                    "修复：Edit → Project Settings → Player → Other Settings → Active Input Handling 设为 " +
                    "\"Both\" 或 \"Input System Package (New)\"，然后【必须完全重启 Unity 编辑器】" +
                    "（该设置只在编辑器启动时读取一次，改完不重启不生效）。", null);
                return;
            }

            if (chosen.Name == "Legacy")
            {
                Game.Logger?.Info("Input",
                    $"输入后端=Legacy（新输入不可用：{modern.UnavailableReason}；旧输入可用={legacyOk}）。输入就绪。");
                return;
            }

            Game.Logger?.Info("Input", $"输入后端={chosen.Name}。输入就绪。");
        }

        public void Lock()
        {
            IsLocked = true;
            ClearState();
        }

        public void Unlock()
        {
            IsLocked = false;
            // 锁定期间后端没有结算过位移：解锁瞬间先重置基准，避免首帧把锁定期的累计量喷出去。
            ResetBackendMouseBaseline();
        }

        /// <summary>把鼠标位移基准重置到"当前"（失焦恢复 / 解锁时用；仅 Legacy 后端有内部基准）。</summary>
        private void ResetBackendMouseBaseline()
        {
            if (_backend is LegacyInputBackend legacy) legacy.ResetMouseBaseline();
        }

        private void ClearState()
        {
            _state.MoveDirection = Vector2.zero;
            _state.Skill1Down = false;
            _state.Skill2Down = false;
            _state.Skill3Down = false;
            _state.Skill4Down = false;
            _state.JumpDown = false;
            _state.DodgeDown = false;
            _state.InteractDown = false;
            _state.TouchDown = false;
        }

        // ───────────── 原始查询 ─────────────

        public bool GetKey(GameKey key) => !IsLocked && _backend.GetKey(key);
        public bool GetKeyDown(GameKey key) => !IsLocked && _backend.GetKeyDown(key);
        public bool GetKeyUp(GameKey key) => !IsLocked && _backend.GetKeyUp(key);
        public bool GetMouseButton(int button) => !IsLocked && _backend.GetMouseButton(button);
        public bool GetMouseButtonDown(int button) => !IsLocked && _backend.GetMouseButtonDown(button);
        public bool GetMouseButtonUp(int button) => !IsLocked && _backend.GetMouseButtonUp(button);
        // 契约："锁定时所有读取返回默认值"——MousePosition 原来漏掉了锁检查（GetKey/GetAxis/MouseDelta 都受锁）。
        public Vector3 MousePosition => IsLocked ? Vector3.zero : _backend.MousePosition;

        public Vector2 MouseDelta
        {
            get
            {
                if (!IsLocked) return _backend.MouseDelta;
                // 锁定时对外恒 0，但仍读一次后端：Legacy 后端的位移是"读时结算"，
                // 完全不读会让内部基准停在锁定前，解锁首帧出现位移突跳（配合 Unlock 的重置双保险）。
                _ = _backend.MouseDelta;
                return Vector2.zero;
            }
        }

        public float GetAxis(string axis, bool raw = false) => IsLocked ? 0f : _backend.GetAxis(axis, raw);

        // ───────────── 高层动作注册 ─────────────

        public void OnMove(Action<Vector2> handler) { if (handler != null) _moveHandlers.Add(handler); }
        public void OffMove(Action<Vector2> handler) => _moveHandlers.Remove(handler);

        public void OnSkill(int skillIndex, Action handler)
        {
            if (handler == null) return;
            if (!_skillHandlers.TryGetValue(skillIndex, out var list))
            {
                list = new List<Action>();
                _skillHandlers[skillIndex] = list;
            }
            list.Add(handler);
        }

        public void OffSkill(int skillIndex, Action handler)
        {
            if (_skillHandlers.TryGetValue(skillIndex, out var list)) list.Remove(handler);
        }

        public void OnJump(Action handler) { if (handler != null) _jumpHandlers.Add(handler); }
        public void OffJump(Action handler) => _jumpHandlers.Remove(handler);
        public void OnDodge(Action handler) { if (handler != null) _dodgeHandlers.Add(handler); }
        public void OffDodge(Action handler) => _dodgeHandlers.Remove(handler);
        public void OnInteract(Action handler) { if (handler != null) _interactHandlers.Add(handler); }
        public void OffInteract(Action handler) => _interactHandlers.Remove(handler);

        // ───────────── 基础设施 ─────────────

        /// <summary>引擎侧统一维护 EventSystem，避免业务各自 new 出重复 InputModule。</summary>
        public void EnsureEventSystem() => InputInfrastructure.EnsureEventSystem(_backend.Name == "Legacy");

        // ───────────── 每帧驱动 ─────────────

        public void Tick()
        {
            // 失焦 / 回前台检测：回前台首帧重置鼠标位移基准并清掉上一帧的按下态，
            // 否则失焦期间累计的位移会在回前台第一帧被当成一帧的位移（视角 / 准星突跳）。
            var focused = Application.isFocused;
            if (focused != _wasFocused)
            {
                _wasFocused = focused;
                if (focused)
                {
                    ResetBackendMouseBaseline();
                    ClearState();
                }
            }

            if (IsLocked)
            {
                ClearState();
                return;
            }

            _state.TouchDown = _backend.TouchCount > 0;
            _state.MoveDirection = SampleMove();
            _backend.SampleActions(_state);

            if (_state.MoveDirection.sqrMagnitude > 0.01f) Fire(_moveHandlers, _state.MoveDirection);
            if (_state.Skill1Down) FireSkill(1);
            if (_state.Skill2Down) FireSkill(2);
            if (_state.Skill3Down) FireSkill(3);
            if (_state.Skill4Down) FireSkill(4);
            if (_state.JumpDown) Fire(_jumpHandlers);
            if (_state.DodgeDown) Fire(_dodgeHandlers);
            if (_state.InteractDown) Fire(_interactHandlers);
        }

        private Vector2 SampleMove()
        {
            var dir = new Vector2(_backend.GetAxis("Horizontal", true), _backend.GetAxis("Vertical", true));
            if (dir.sqrMagnitude > 1f) dir.Normalize();
            return dir;
        }

        // 以下 Fire* 都先**快照**再遍历：回调里增删监听器（On/Off）会改动原 List，
        // 直接按实时 Count 遍历会漏触发或同帧重复触发。
        private void Fire(List<Action> handlers)
        {
            var snapshot = handlers.ToArray();
            for (int i = 0; i < snapshot.Length; i++)
            {
                try { snapshot[i]?.Invoke(); }
                catch (Exception ex) { Game.Logger?.Error("Input", "动作回调异常: " + ex.Message, ex); }
            }
        }

        private void Fire(List<Action<Vector2>> handlers, Vector2 v)
        {
            var snapshot = handlers.ToArray();
            for (int i = 0; i < snapshot.Length; i++)
            {
                try { snapshot[i]?.Invoke(v); }
                catch (Exception ex) { Game.Logger?.Error("Input", "移动回调异常: " + ex.Message, ex); }
            }
        }

        private void FireSkill(int index)
        {
            if (!_skillHandlers.TryGetValue(index, out var list) || list.Count == 0) return;
            var snapshot = list.ToArray();
            for (int i = 0; i < snapshot.Length; i++)
            {
                try { snapshot[i]?.Invoke(); }
                catch (Exception ex) { Game.Logger?.Error("Input", "技能回调异常: " + ex.Message, ex); }
            }
        }
    }

    /// <summary>
    /// 引擎侧 UI 输入基础设施：保证场景里恰好有一个 EventSystem，
    /// 且只挂载与当前输入后端匹配的 InputModule。
    /// </summary>
    internal static class InputInfrastructure
    {
        private const string LegacyModule = "StandaloneInputModule";
        private const string ModernModule = "InputSystemUIInputModule";

        /// <summary>指针命中判定失败只报一次（非预期分支留痕，避免逐帧刷屏）。</summary>
        private static bool _pointerOverUiFailedLogged;

        /// <summary>
        /// 指针是否压在 UI 上：<c>EventSystem.IsPointerOverGameObject()</c>（无参重载 = 鼠标左键指针，
        /// 与 uGUI 的 <c>PointerInputModule</c> 同口径；触摸要按 fingerId 判，不在本探针范围内）。
        ///
        /// <para><b>降级（永不抛异常）</b>：<c>EventSystem.current == null</c>（场景里还没有 EventSystem，
        /// 如引擎 <see cref="EnsureEventSystem"/> 之前 / 离线宿主）⇒ <c>false</c>；判定调用抛异常 ⇒
        /// <c>false</c> + 一条 Warn（只报一次）。两种情况都是"指针不在 UI 上"的安全值。</para>
        ///
        /// <para>为什么不再让业务各自反射：反射拿不到 uGUI 类型时是**静默**恒 false，且口径会分叉；
        /// 引擎 asmdef 本就硬引用 uGUI（<see cref="EnsureEventSystem"/> 用 <c>EventSystem</c> /
        /// <c>StandaloneInputModule</c>），故这里直接用类型、不需要反射。</para>
        /// </summary>
        public static bool PointerOverUi()
        {
            var es = EventSystem.current;
            if (es == null) return false;   // 还没有 EventSystem / 离线宿主：安全值
            try
            {
                return es.IsPointerOverGameObject();
            }
            catch (Exception e)
            {
                if (!_pointerOverUiFailedLogged)
                {
                    _pointerOverUiFailedLogged = true;
                    Game.Logger?.Warn("Input", $"IsPointerOverGameObject() 抛异常"
                        + $"（{e.GetType().Name}: {e.Message}）⇒ 本局按「指针不在 UI 上」处理（只报一次）");
                }
                return false;
            }
        }

        // uGUI 集成模块位于 Unity.InputSystem（包内 versionDefines：装了 com.unity.ugui 时定义
        // UNITY_INPUT_SYSTEM_ENABLE_UI）。个别版本可能落在 Unity.InputSystem.ForUI，故两个都试。
        private static readonly string[] ModernModuleTypeNames =
        {
            "UnityEngine.InputSystem.UI.InputSystemUIInputModule, Unity.InputSystem",
            "UnityEngine.InputSystem.UI.InputSystemUIInputModule, Unity.InputSystem.ForUI",
        };

        private static Type FindModernModuleType()
        {
            for (int i = 0; i < ModernModuleTypeNames.Length; i++)
            {
                var t = Type.GetType(ModernModuleTypeNames[i]);
                if (t != null) return t;
            }
            return null;
        }

        public static void EnsureEventSystem(bool preferLegacy)
        {
            var es = EventSystem.current;
            // 默认的 FindAnyObjectByType 会跳过**未激活**对象：EventSystem 挂在未激活节点上时会漏判并再建一个
            //（场景里出现两个 EventSystem）。必须带 FindObjectsInactive.Include。
            if (es == null)
                es = UnityEngine.Object.FindAnyObjectByType<EventSystem>(FindObjectsInactive.Include);

            bool created = false;
            if (es == null)
            {
                var go = new GameObject("EventSystem");
                UnityEngine.Object.DontDestroyOnLoad(go);
                es = go.AddComponent<EventSystem>();
                created = true;
            }

            var want = preferLegacy ? LegacyModule : ModernModule;
            bool hasWanted = false;

            foreach (var module in es.GetComponents<BaseInputModule>())
            {
                var name = module.GetType().Name;
                if (name == want) { hasWanted = true; continue; }

                // 只清"另一个后端"的模块（StandaloneInputModule / InputSystemUIInputModule）：
                // 业务自定的输入模块（TouchInputModule / 自定义模块）必须保留 —— 原实现按"类型名 != 目标"一律 Destroy。
                if (name != LegacyModule && name != ModernModule) continue;

                // 两个 InputModule 并存会让点击/输入互相吃掉；Destroy 要等帧末才生效，
                // 先 enabled=false 让旧模块本帧立即停止处理，否则本帧新旧并存正是要消灭的现象。
                module.enabled = false;
                UnityEngine.Object.Destroy(module);
                Game.Logger?.Info("Input", $"移除不匹配的输入模块 {name}（保留 {want}）");
            }

            if (hasWanted)
            {
                LogReady(created, want);
                return;
            }

            if (preferLegacy)
            {
                es.gameObject.AddComponent<StandaloneInputModule>();
            }
            else
            {
                var t = FindModernModuleType();
                if (t != null)
                {
                    es.gameObject.AddComponent(t);
                }
                else
                {
                    // 新后端可用但拿不到新模块：退回旧模块并明确告警。
                    // 注意：若 Active Input Handling 为「只新」，旧模块会失效 → UI 点击全灭，
                    // 所以这条告警必须当回事，不能当成无害的兜底。
                    Game.Logger?.Warn("Input",
                        "未找到 InputSystemUIInputModule，回退 StandaloneInputModule。" +
                        "若 Active Input Handling 为「只新」，UI 点击将失效：" +
                        "请确认 com.unity.inputsystem 已安装且版本正常。");
                    es.gameObject.AddComponent<StandaloneInputModule>();
                }
            }

            LogReady(created, want);
        }

        private static void LogReady(bool created, string module)
        {
            Game.Logger?.Info("Input", $"EventSystem {(created ? "已创建（常驻）" : "已存在")}，InputModule={module}");
        }
    }
}
