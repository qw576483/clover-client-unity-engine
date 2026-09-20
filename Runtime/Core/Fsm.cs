using System;
using System.Collections.Generic;

namespace CloverEngine
{
    /// <summary>
    /// 有限状态机接口，定义状态注册、转换和更新的基本操作。
    /// <para>
    /// <b>转换语义（实现契约）</b>：目标状态与当前状态相同时一律忽略（不重跑 OnExit/OnEnter，
    /// 旧实现只给 <c>Transition</c> 加了守卫，<c>Trigger</c>/<c>Force</c> 连自环都会重跑）；
    /// 状态回调（OnExit/OnEnter/OnTick/OnChange）内再发起转换会被收进队列，
    /// 等当前转换收尾后**按序补执行**（不会以旧状态重入、也不会丢意图）。
    /// </para>
    /// </summary>
    public interface IFsm
    {
        /// <summary>
        /// 注册一个状态及其回调函数。同名状态重复注册会**告警**并以后者覆盖（存活期回调被整体替换）。
        /// </summary>
        /// <param name="state">状态名称。</param>
        /// <param name="onEnter">进入状态时触发的回调。</param>
        /// <param name="onTick">每帧更新时触发的回调，参数为时间增量。</param>
        /// <param name="onExit">退出状态时触发的回调。</param>
        void RegisterState(string state, Action onEnter = null, Action<float> onTick = null, Action onExit = null);
        /// <summary>
        /// 执行状态转换，直接切换到指定状态（与当前相同则忽略）。
        /// </summary>
        /// <param name="toState">目标状态名称。</param>
        void Transition(string toState);
        /// <summary>
        /// 添加状态转换规则，将触发器映射到目标状态。
        /// </summary>
        /// <param name="trigger">触发器名称。</param>
        /// <param name="toState">目标状态名称。</param>
        void AddTransition(string trigger, string toState);
        /// <summary>
        /// 通过触发器名称执行状态转换。触发器未注册时**告警**并忽略（旧实现静默 return，拼错触发器名表现为"按了没反应"）。
        /// </summary>
        /// <param name="trigger">触发器名称，需预先通过 AddTransition 注册。</param>
        void Trigger(string trigger);
        /// <summary>
        /// 强制切换到指定状态，无论当前状态如何（与当前相同则忽略，不重跑 OnExit/OnEnter）。
        /// </summary>
        /// <param name="state">目标状态名称。</param>
        void Force(string state);
        /// <summary>
        /// 每帧调用以更新当前状态。
        /// </summary>
        /// <param name="dt">时间增量。</param>
        void Tick(float dt);
        /// <summary>
        /// 注册状态变更监听器。
        /// </summary>
        /// <param name="handler">状态变更回调，参数为原状态和新状态。</param>
        void OnChange(Action<string, string> handler);
        /// <summary>
        /// 移除状态变更监听器。
        /// </summary>
        /// <param name="handler">要移除的回调。</param>
        void OffChange(Action<string, string> handler);
        /// <summary>
        /// 获取当前状态名称。
        /// </summary>
        string Current { get; }
    }

    /// <summary>
    /// 有限状态机的内部实现，管理状态注册、转换和更新。
    /// </summary>
    internal class Fsm : IFsm
    {
        /// <summary>
        /// 一次外部转换调用允许的连锁转换上限：状态回调里再发起转换会被排队补执行，
        /// 若回调链反复自激（如 OnExit 每次都重触发同一路径）则到上限后报错停止 ——
        /// 旧实现无守卫无上限，这种形态直接递归到栈溢出。
        /// </summary>
        private const int MaxChainedSwitches = 8;

        private string _current;
        private readonly Dictionary<string, StateInfo> _states = new();
        private readonly Dictionary<string, string> _transitions = new();
        private readonly List<Action<string, string>> _changeHandlers = new();

        /// <summary>转换执行中标记：状态回调里再发起转换时改为排队（不重入）。</summary>
        private bool _switching;

        /// <summary>回调链里最后一次请求的目标状态；<see cref="_hasPending"/> 为 true 时有效。</summary>
        private string _pendingState;

        private bool _hasPending;

        /// <summary>OnChange 通知的复用缓冲：避免每次状态切换都 new List 复制订阅表。</summary>
        private readonly List<Action<string, string>> _changeBuffer = new();

        /// <summary>
        /// 获取当前状态名称。
        /// </summary>
        public string Current => _current;

        /// <summary>
        /// 注册一个状态及其回调函数。
        /// </summary>
        /// <param name="state">状态名称。</param>
        /// <param name="onEnter">进入状态时触发的回调。</param>
        /// <param name="onTick">每帧更新时触发的回调，参数为时间增量。</param>
        /// <param name="onExit">退出状态时触发的回调。</param>
        public void RegisterState(string state, Action onEnter = null, Action<float> onTick = null, Action onExit = null)
        {
            if (_states.ContainsKey(state))
            {
                // 覆盖不留痕的旧行为：运行中重复注册会静默丢掉前一组回调，排障时无从发现。
                Game.Logger?.Warn("Fsm",
                    $"state '{state}' re-registered: previous OnEnter/OnTick/OnExit are replaced");
            }
            _states[state] = new StateInfo
            {
                OnEnter = onEnter,
                OnTick = onTick,
                OnExit = onExit
            };
        }

        /// <summary>
        /// 添加状态转换规则，将触发器映射到目标状态。
        /// </summary>
        /// <param name="trigger">触发器名称。</param>
        /// <param name="toState">目标状态名称。</param>
        public void AddTransition(string trigger, string toState)
        {
            _transitions[trigger] = toState;
        }

        /// <summary>
        /// 执行状态转换，直接切换到指定状态（若目标状态不同）。
        /// </summary>
        /// <param name="toState">目标状态名称。</param>
        public void Transition(string toState)
        {
            if (toState == _current) return;
            SwitchTo(toState);
        }

        /// <summary>
        /// 通过触发器名称执行状态转换，若触发器已注册则切换状态。
        /// <para>触发器未注册时<b>告警</b>并忽略：旧实现静默 return，业务把触发器名拼错时的表现是"按了没反应"，无从定位。</para>
        /// </summary>
        /// <param name="trigger">触发器名称。</param>
        public void Trigger(string trigger)
        {
            if (!_transitions.TryGetValue(trigger, out var toState))
            {
                Game.Logger?.Warn("Fsm",
                    $"unregistered trigger '{trigger}' ignored; registered: {string.Join(", ", _transitions.Keys)}");
                return;
            }
            SwitchTo(toState);
        }

        /// <summary>
        /// 强制切换到指定状态，无论当前状态如何（与当前相同则忽略，见 <see cref="SwitchTo"/>）。
        /// </summary>
        /// <param name="state">目标状态名称。</param>
        public void Force(string state)
        {
            SwitchTo(state);
        }

        /// <summary>
        /// 每帧调用以更新当前状态，触发 OnTick 回调。
        /// <para>回调异常在此隔离（与 Event / Timer / Dispatcher 一致）：单个状态回调抛异常
        /// 不应中断整个 <c>Game.Tick</c>，否则本帧 Net/Sync/Res/UI 全部不执行。</para>
        /// </summary>
        /// <param name="dt">时间增量。</param>
        public void Tick(float dt)
        {
            if (_current == null || !_states.TryGetValue(_current, out var info)) return;
            try
            {
                info.OnTick?.Invoke(dt);
            }
            catch (Exception ex)
            {
                Game.Logger?.Error("Fsm", $"OnTick error [{_current}]: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 注册状态变更监听器，当状态切换时触发回调。
        /// <para>同一 handler 重复注册会被忽略并告警（与 Event 的订阅配对语义一致）：注册两次、注销一次不再残留。</para>
        /// </summary>
        /// <param name="handler">状态变更回调，参数为原状态和新状态。</param>
        public void OnChange(Action<string, string> handler)
        {
            if (_changeHandlers.Contains(handler))
            {
                Game.Logger?.Warn("Fsm",
                    "duplicate OnChange handler ignored: same handler already registered (one OffChange now removes it)");
                return;
            }
            _changeHandlers.Add(handler);
        }

        /// <summary>
        /// 移除状态变更监听器（删除该 handler 的全部注册，注销一次即干净）。
        /// </summary>
        /// <param name="handler">要移除的回调。</param>
        public void OffChange(Action<string, string> handler)
        {
            // 倒序删：RemoveAt 会让后面元素左移，正序会漏删紧随其后的匹配项。
            for (var i = _changeHandlers.Count - 1; i >= 0; i--)
            {
                if (_changeHandlers[i] == handler)
                {
                    _changeHandlers.RemoveAt(i);
                }
            }
        }

        /// <summary>
        /// 执行状态转换（Transition / Trigger / Force 与链式补执行的唯一入口）。
        /// <list type="bullet">
        /// <item><b>自转换守卫</b>：目标与当前相同直接忽略 —— 旧实现只有 Transition 有守卫，
        /// Trigger/Force 遇自环（<c>Game.InitFsm</c> 注册的 Disconnected→Disconnected）会重跑
        /// OnExit+OnEnter，回调内再触发即成死循环。</item>
        /// <item><b>重入保护</b>：转换执行中状态回调再次发起转换时只把目标排队（取最后一次），
        /// 当前转换收尾后按序补执行 —— 旧实现以旧状态重入：OnExit 执行两次、内层新状态被外层覆盖。</item>
        /// </list>
        /// </summary>
        private void SwitchTo(string toState)
        {
            if (toState != null && !_states.ContainsKey(toState))
            {
                Game.Logger?.Error("Fsm", $"State not registered: {toState}");
                return;
            }

            if (toState == _current) return; // 自转换：不重跑 OnExit/OnEnter

            if (_switching)
            {
                // 状态回调内再发起转换：排队（最后意图为准），不重入
                _pendingState = toState;
                _hasPending = true;
                return;
            }

            _switching = true;
            try
            {
                var target = toState;
                var chained = 0;
                while (true)
                {
                    ApplySwitch(target);

                    if (!_hasPending) break;

                    _hasPending = false;
                    chained++;
                    if (chained > MaxChainedSwitches)
                    {
                        Game.Logger?.Error("Fsm",
                            $"transition chain aborted after {MaxChainedSwitches} chained switches " +
                            "(state callbacks keep requesting new targets - likely a self-driving loop); " +
                            $"pending target '{_pendingState}' dropped");
                        break;
                    }
                    target = _pendingState;
                }
            }
            finally
            {
                _switching = false;
                _hasPending = false;
                _pendingState = null;
            }
        }

        /// <summary>单次状态切换：先改 <see cref="Current"/> 再回调（OnExit → OnEnter → OnChange）。</summary>
        private void ApplySwitch(string toState)
        {
            var from = _current;
            if (toState == from) return; // 链式补执行可能请求同状态：同样不重跑回调

            // 先改状态、再回调：任何回调（或回调链里读 Current 的逻辑）见到的都是转换后的值；
            // 回调内再发起转换由 SwitchTo 的 _switching 守卫收进队列，不会以旧状态重入。
            _current = toState;

            InvokeStateCallback(from, onExit: true);
            InvokeStateCallback(toState, onExit: false);

            // 通知变更订阅者（复用缓冲，避免每次切换都 new List 复制订阅表）。
            // 遍历的是快照：回调里 OnChange/OffChange 改订阅表不会破坏遍历、也不会抛 InvalidOperationException。
            _changeBuffer.Clear();
            _changeBuffer.AddRange(_changeHandlers);
            for (var i = 0; i < _changeBuffer.Count; i++)
            {
                try
                {
                    _changeBuffer[i]?.Invoke(from, toState);
                }
                catch (Exception ex)
                {
                    Game.Logger?.Error("Fsm", $"OnChange handler error: {ex.Message}", ex);
                }
            }
        }

        /// <summary>调用某状态的 OnExit（<paramref name="onExit"/>=true）或 OnEnter，异常隔离。</summary>
        private void InvokeStateCallback(string state, bool onExit)
        {
            if (state == null || !_states.TryGetValue(state, out var info)) return;
            try
            {
                if (onExit)
                    info.OnExit?.Invoke();
                else
                    info.OnEnter?.Invoke();
            }
            catch (Exception ex)
            {
                Game.Logger?.Error("Fsm",
                    $"{(onExit ? "OnExit" : "OnEnter")} error [{state}]: {ex.Message}", ex);
            }
        }

        private class StateInfo
        {
            public Action OnEnter;
            public Action<float> OnTick;
            public Action OnExit;
        }
    }
}
