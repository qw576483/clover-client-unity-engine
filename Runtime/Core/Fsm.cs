using System;
using System.Collections.Generic;

namespace CloverEngine
{
    /// <summary>
    /// 有限状态机接口，定义状态注册、转换和更新的基本操作。
    /// <para>
    /// <b>转换语义（实现契约）</b>：目标状态与当前状态相同时一律忽略（不重跑 OnExit/OnEnter）；
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
        /// 通过触发器名称执行状态转换。触发器未注册时**告警**并忽略。
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
        /// 把状态机恢复到"未初始化"：清空已注册状态、触发器映射与当前状态（**不销毁实例**）。
        /// <para>
        /// 用于每回合 / 每次复用（对象池取回）时把上一轮的状态表与当前状态清干净，再重新
        /// <see cref="RegisterState"/> + <see cref="Force"/>。实现契约（<see cref="Fsm.Reset"/> 逐条说明）：
        /// 调用后 <see cref="Current"/> 必须为空；可重复调用（第二次为空操作）；
        /// <b>不</b>触发任何 OnExit / OnEnter / <see cref="OnChange"/> 回调（重置不是一次状态转换）；
        /// 订阅表是否保留由实现声明（<see cref="Fsm"/> 的选择是**保留**，见 <see cref="Fsm.Reset"/>）。
        /// </para>
        /// </summary>
        void Reset();
        /// <summary>
        /// 获取当前状态名称。
        /// </summary>
        string Current { get; }
    }

    /// <summary>
    /// 有限状态机的实现，管理状态注册、转换和更新。
    /// <para>
    /// <b>对外可实例化</b>（经 <see cref="Game.NewFsm"/>；本类由 <c>internal</c> 提升为 <c>public</c>）——
    /// <b>为什么业务需要一个自己的实例</b>：引擎里只有一份<b>应用级单例</b>
    /// <see cref="Game.Fsm"/>（<c>Game.Launch</c> 时建立、<c>Game.InitFsm</c> 注册的是
    /// 游戏流程状态 Launching/Logging/MainCity/Battle…，且 <c>Game.Tick</c> 只驱动它一份）。
    /// 业务要用状态机描述"每个 Bot / 每个单位各自一棵"的局部流程时，若把状态注册到那一份上，
    /// 多个实体就会<b>共用同一个 <see cref="Current"/></b>、互相覆盖（同名状态重复注册还会
    /// 触发"存活期回调被整体替换"的告警）⇒ 结构上不成立。
    /// 语义只有一份实现：业务直接用 <c>Game.NewFsm()</c> 拿自己那份实例，⛔ 不要自己再抄一份
    /// （自环守卫 / 回调内再转换排队 / 连锁上限 8 / 异常隔离都必须走本类）。
    /// 本类由 <c>internal</c> 提升为 <c>public</c> 就是为了让跨程序集也能实例化。
    /// </para>
    /// <para>
    /// 与 <see cref="Game.Fsm"/> 的关系：<b>完全独立</b> —— 新实例有自己的
    /// <c>_states</c>/<c>_transitions</c>/<c>_changeHandlers</c>/<c>_current</c>，
    /// 不共享任何静态态；<c>Game.Tick</c> <b>不会</b>驱动新实例，谁创建谁负责按帧调用
    /// <see cref="Tick"/>（与 <see cref="Game.Fsm"/> 由 <c>Game.Tick</c> 驱动不同）。
    /// </para>
    /// </summary>
    public class Fsm : IFsm
    {
        /// <summary>
        /// 一次外部转换调用允许的连锁转换上限：状态回调里再发起转换会被排队补执行，
        /// 若回调链反复自激（如 OnExit 每次都重触发同一路径）则到上限后报错停止。
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
                // 静默覆盖会丢掉前一组回调且不留痕，排障时无从发现 ⇒ 必须告警。
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
        /// <para>触发器未注册时<b>告警</b>并忽略（含已注册触发器名列表），便于定位拼写错误。</para>
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
        /// 恢复到"未初始化"（每回合重开 / 从对象池取回复用时调用）：清空状态表、触发器表与当前状态，
        /// 清完与"刚 new 出来"的状态机一致（<see cref="Current"/> 为 <c>null</c>、<see cref="Tick"/> 空转）。
        /// <para>
        /// <b>清什么</b>：<c>_states</c>（已注册状态及其存活期回调）、<c>_transitions</c>（触发器映射）、
        /// <see cref="Current"/>、以及"转换执行中 / 待补执行目标"这组内部标记 —— 后者必须一起清：
        /// 若在回调链执行中重置而留下 <c>_switching = true</c>，之后的转换会被误当"重入"塞进队列永不执行。
        /// </para>
        /// <para>
        /// <b>不清什么</b>：<see cref="OnChange"/> 的订阅表（<c>_changeHandlers</c>）—— 订阅是"调用方与实例
        /// 之间"的关系，不是状态机内容；静默解绑会让"重置后通知不再到达"变成无从定位的现象
        /// （表现是"重置一次以后 OnChange 就再也不触发了"）。要解绑请显式 <see cref="OffChange"/>。
        /// </para>
        /// <para>
        /// <b>为什么不销毁实例</b>：实例常被对象池 / 每回合复用，销毁重建会让外部持有的引用（订阅、
        /// 字段引用）全部作废，并引入 ABA 陷阱（新实例与旧引用"看似相同实则不同"）——
        /// 本方法刻意只做"清内容"，同一引用继续可用。
        /// </para>
        /// <para>
        /// <b>边界</b>：可重复调用（第二次起为空操作）；刚 <c>new</c> 出来时调用安全；
        /// <b>不</b>触发任何 OnExit / OnEnter / OnChange 回调（重置不是一次状态转换，⛔ 不要指望它"通知离开"）；
        /// 不抛异常、不打日志（正常操作；非预期分支才需留痕）。重置后再 <see cref="Transition"/> 到旧状态名
        /// 会走"状态未注册"的 Error 分支（因为状态表确实空了），这是刻意保留的可见行为。
        /// </para>
        /// </summary>
        public void Reset()
        {
            _states.Clear();
            _transitions.Clear();
            _current = null;
            _switching = false;
            _hasPending = false;
            _pendingState = null;
        }

        /// <summary>
        /// 执行状态转换（Transition / Trigger / Force 与链式补执行的唯一入口）。
        /// <list type="bullet">
        /// <item><b>自转换守卫</b>：目标与当前相同直接忽略（Transition / Trigger / Force 一致，
        /// 避免自环重跑 OnExit+OnEnter、回调内再触发即成死循环）。</item>
        /// <item><b>重入保护</b>：转换执行中状态回调再次发起转换时只把目标排队（取最后一次），
        /// 当前转换收尾后按序补执行（不以旧状态重入）。</item>
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
