using System;
using System.Collections.Generic;

namespace CloverEngine
{
    /// <summary>
    /// 进程内事件总线，同服务端 event.Bus 语义。
    /// 支持 On/Once/Off/Emit，事件名字符串匹配，支持泛型参数传递。
    ///
    /// <para><b>订阅配对语义（实现契约）</b>：同一事件上**同一 handler 重复注册会被忽略并告警**（不再叠加多份），
    /// 因此一次 <c>Off</c> 即注销干净、不残留 —— 旧实现允许叠加多份而 <c>Off</c> 只删首个，
    /// 重复 Init / 忘记配对 Off 时会表现为"回调翻倍 + 订阅泄漏"。</para>
    /// <para><b>分发期间修改即时生效</b>：<c>Emit</c> 回调里调用 <c>Off</c> / <c>OffAll</c>，
    /// 被注销的 handler 本帧不再触发（未被注销者也不会重复触发）。</para>
    ///
    /// <para><b>通配订阅（<c>*</c> / <c>**</c>）</b>：事件名以 <c>.</c> 分段（如 <c>Net.OnConnected</c>），
    /// 订阅名里可用 <c>*</c> 匹配**恰好一段**、<c>**</c> 匹配**一段或多段**（<c>Net.**</c> 命中
    /// <c>Net.OnConnected</c>；<c>Net.*</c> 只命中两段名）。分发顺序：**精确匹配的订阅先执行**，
    /// 通配订阅随后（以保证"订阅具体事件的回调"优先于"泛订阅"）。退订语义与精确订阅一致：
    /// 用注册时**同一个订阅名**（含通配符）调 <c>Off</c> 即注销，一次注销干净。</para>
    /// </summary>
    public interface IEventBus
    {
        /// <summary>
        /// 注册无参数事件监听器
        /// </summary>
        /// <param name="eventName">事件名称</param>
        /// <param name="handler">事件处理回调</param>
        void On(string eventName, Action handler);

        /// <summary>
        /// 注册带一个泛型参数的事件监听器
        /// </summary>
        /// <param name="eventName">事件名称</param>
        /// <param name="handler">事件处理回调</param>
        void On<T>(string eventName, Action<T> handler);

        /// <summary>
        /// 注册带两个泛型参数的事件监听器
        /// </summary>
        /// <param name="eventName">事件名称</param>
        /// <param name="handler">事件处理回调</param>
        void On<T1, T2>(string eventName, Action<T1, T2> handler);

        /// <summary>
        /// 注册一次性无参数事件监听器，触发一次后自动移除
        /// </summary>
        /// <param name="eventName">事件名称</param>
        /// <param name="handler">事件处理回调</param>
        void Once(string eventName, Action handler);

        /// <summary>
        /// 注册一次性带一个泛型参数的事件监听器，触发一次后自动移除
        /// </summary>
        /// <param name="eventName">事件名称</param>
        /// <param name="handler">事件处理回调</param>
        void Once<T>(string eventName, Action<T> handler);

        /// <summary>
        /// 注册一次性带两个泛型参数的事件监听器，触发一次后自动移除
        /// </summary>
        /// <param name="eventName">事件名称</param>
        /// <param name="handler">事件处理回调</param>
        void Once<T1, T2>(string eventName, Action<T1, T2> handler);

        /// <summary>
        /// 按优先级注册无参数监听器：priority 越大越先执行（同优先级内仍是后注册先执行）。
        ///
        /// 为什么需要它：注册顺序此前是控制执行顺序的**唯一**手段，业务想表达"我必须先跑"
        /// 只能靠"后注册"或者干脆在 Update 里轮询等状态 —— 那是把顺序依赖藏起来而不是写出来。
        /// </summary>
        /// <param name="eventName">事件名称</param>
        /// <param name="priority">优先级，大者先执行</param>
        /// <param name="handler">事件处理回调</param>
        void OnPriority(string eventName, int priority, Action handler);

        /// <summary>
        /// 移除无参数事件监听器
        /// </summary>
        /// <param name="eventName">事件名称</param>
        /// <param name="handler">要移除的事件处理回调</param>
        void Off(string eventName, Action handler);

        /// <summary>
        /// 移除带一个泛型参数的事件监听器
        /// </summary>
        /// <param name="eventName">事件名称</param>
        /// <param name="handler">要移除的事件处理回调</param>
        void Off<T>(string eventName, Action<T> handler);

        /// <summary>
        /// 移除带两个泛型参数的事件监听器
        /// </summary>
        /// <param name="eventName">事件名称</param>
        /// <param name="handler">要移除的事件处理回调</param>
        void Off<T1, T2>(string eventName, Action<T1, T2> handler);

        /// <summary>
        /// 移除指定事件的所有监听器
        /// </summary>
        /// <param name="eventName">事件名称</param>
        void OffAll(string eventName);

        /// <summary>
        /// 移除所有事件的所有监听器
        /// </summary>
        void OffAll();

        /// <summary>
        /// 触发无参数事件
        /// </summary>
        /// <param name="eventName">事件名称</param>
        void Emit(string eventName);

        /// <summary>
        /// 触发带一个泛型参数的事件
        /// </summary>
        /// <param name="eventName">事件名称</param>
        /// <param name="arg1">事件参数</param>
        void Emit<T>(string eventName, T arg1);

        /// <summary>
        /// 触发带两个泛型参数的事件
        /// </summary>
        /// <param name="eventName">事件名称</param>
        /// <param name="arg1">第一个事件参数</param>
        /// <param name="arg2">第二个事件参数</param>
        void Emit<T1, T2>(string eventName, T1 arg1, T2 arg2);
    }

    internal class EventBus : IEventBus
    {
        /// <summary>
        /// 一条监听记录：回调 + 优先级。
        ///
        /// 加优先级的原因：监听器的执行顺序此前**只由注册顺序决定**（后注册先执行），
        /// 业务想说"我这个必须先跑"就没有任何表达手段 —— 只能靠在 Update 里轮询等某个状态，
        /// 或者把初始化代码塞进别人的回调里（本项目 `App.Bootstrap` 就是被逼着轮询等"视图根出现"）。
        /// 有优先级之后，这类顺序依赖可以写出来而不是"碰巧成立"。
        /// </summary>
        private readonly struct HandlerEntry : IEquatable<HandlerEntry>
        {
            public readonly Delegate Handler;
            public readonly int Priority;

            public HandlerEntry(Delegate handler, int priority)
            {
                Handler = handler;
                Priority = priority;
            }

            // 显式实现相等：分发期间要用 List.Contains 做"仍在册"判定（见 StillRegistered）。
            // 不实现 IEquatable 时 List.Contains 会落到反射式 ValueType.Equals，热路径上白白慢一截。
            public bool Equals(HandlerEntry other) => Handler == other.Handler && Priority == other.Priority;

            public override bool Equals(object obj) => obj is HandlerEntry other && Equals(other);

            public override int GetHashCode() => (Handler?.GetHashCode() ?? 0) ^ Priority;
        }

        // 存放顺序是「执行顺序的逆序」（InvokeHandlers 倒序遍历）：
        // priority 小的在前、同 priority 先注册的在前。
        private readonly Dictionary<string, List<HandlerEntry>> _handlers = new();
        private readonly Dictionary<string, List<HandlerEntry>> _onceHandlers = new();

        // 含通配符（* / **）的订阅名，**按注册顺序**记录，用于每次 Emit 时逐个匹配。
        // 用"注册顺序列表"而不是遍历 _handlers.Keys：字典枚举顺序不保证稳定，
        // 通配订阅之间必须有一致的执行顺序（先注册先执行，与精确订阅的兜底顺序同向）。
        private readonly List<string> _wildcardKeys = new();
        private readonly HashSet<string> _wildcardKeySet = new();

        /// <summary>
        /// 注册无参数事件监听器
        /// </summary>
        /// <param name="eventName">事件名称</param>
        /// <param name="handler">事件处理回调</param>
        public void On(string eventName, Action handler) => AddHandler(eventName, handler, 0);

        /// <summary>
        /// 注册带一个泛型参数的事件监听器
        /// </summary>
        /// <param name="eventName">事件名称</param>
        /// <param name="handler">事件处理回调</param>
        public void On<T>(string eventName, Action<T> handler) => AddHandler(eventName, handler, 0);

        /// <summary>
        /// 注册带两个泛型参数的事件监听器
        /// </summary>
        /// <param name="eventName">事件名称</param>
        /// <param name="handler">事件处理回调</param>
        public void On<T1, T2>(string eventName, Action<T1, T2> handler) => AddHandler(eventName, handler, 0);

        /// <summary>
        /// 按优先级注册无参数监听器：priority 越大越先执行。
        ///
        /// 与 <see cref="On(string, Action)"/> 的关系：`On` 等价于 priority = 0；
        /// **同一优先级内仍是"后注册先执行"**（既有语义不变，不会因为引入优先级而改变现有代码的行为）。
        /// </summary>
        /// <param name="eventName">事件名称</param>
        /// <param name="priority">优先级，大者先执行</param>
        /// <param name="handler">事件处理回调</param>
        public void OnPriority(string eventName, int priority, Action handler)
            => AddHandler(eventName, handler, priority);

        /// <summary>
        /// 注册一次性无参数事件监听器，触发一次后自动移除
        /// </summary>
        /// <param name="eventName">事件名称</param>
        /// <param name="handler">事件处理回调</param>
        public void Once(string eventName, Action handler) => AddOnceHandler(eventName, handler);

        /// <summary>
        /// 注册一次性带一个泛型参数的事件监听器，触发一次后自动移除
        /// </summary>
        /// <param name="eventName">事件名称</param>
        /// <param name="handler">事件处理回调</param>
        public void Once<T>(string eventName, Action<T> handler) => AddOnceHandler(eventName, handler);

        /// <summary>
        /// 注册一次性带两个泛型参数的事件监听器，触发一次后自动移除
        /// </summary>
        /// <param name="eventName">事件名称</param>
        /// <param name="handler">事件处理回调</param>
        public void Once<T1, T2>(string eventName, Action<T1, T2> handler) => AddOnceHandler(eventName, handler);

        /// <summary>
        /// 移除无参数事件监听器
        /// </summary>
        /// <param name="eventName">事件名称</param>
        /// <param name="handler">要移除的事件处理回调</param>
        public void Off(string eventName, Action handler) => RemoveHandler(eventName, handler);

        /// <summary>
        /// 移除带一个泛型参数的事件监听器
        /// </summary>
        /// <param name="eventName">事件名称</param>
        /// <param name="handler">要移除的事件处理回调</param>
        public void Off<T>(string eventName, Action<T> handler) => RemoveHandler(eventName, handler);

        /// <summary>
        /// 移除带两个泛型参数的事件监听器
        /// </summary>
        /// <param name="eventName">事件名称</param>
        /// <param name="handler">要移除的事件处理回调</param>
        public void Off<T1, T2>(string eventName, Action<T1, T2> handler) => RemoveHandler(eventName, handler);

        /// <summary>
        /// 移除指定事件的所有监听器。
        /// <para>
        /// 分发（<see cref="Emit(string)"/>）过程中调用也**即时生效**：本帧剩余 handler 不再触发 ——
        /// 旧实现只从字典删条目、不动正在遍历的列表，本帧剩余 handler 仍会全部触发。
        /// </para>
        /// </summary>
        /// <param name="eventName">事件名称</param>
        public void OffAll(string eventName)
        {
            _handlers.Remove(eventName);
            _onceHandlers.Remove(eventName);
            UntrackWildcard(eventName);
        }

        /// <summary>
        /// 移除所有事件的所有监听器（分发过程中调用同样即时生效，语义同 <see cref="OffAll(string)"/>）。
        /// </summary>
        public void OffAll()
        {
            _handlers.Clear();
            _onceHandlers.Clear();
            _wildcardKeys.Clear();
            _wildcardKeySet.Clear();
        }

        /// <summary>
        /// 触发无参数事件
        /// </summary>
        /// <param name="eventName">事件名称</param>
        public void Emit(string eventName) => InvokeHandlers(eventName);

        /// <summary>
        /// 触发带一个泛型参数的事件
        /// </summary>
        /// <param name="eventName">事件名称</param>
        /// <param name="arg1">事件参数</param>
        public void Emit<T>(string eventName, T arg1) => InvokeHandlers(eventName, arg1);

        /// <summary>
        /// 触发带两个泛型参数的事件
        /// </summary>
        /// <param name="eventName">事件名称</param>
        /// <param name="arg1">第一个事件参数</param>
        /// <param name="arg2">第二个事件参数</param>
        public void Emit<T1, T2>(string eventName, T1 arg1, T2 arg2) => InvokeHandlers(eventName, arg1, arg2);

        /// <summary>含通配符的订阅名登记进匹配表（幂等）。</summary>
        private void TrackWildcard(string eventName)
        {
            if (eventName == null) return;
            if (eventName.IndexOf('*') < 0) return;
            if (_wildcardKeySet.Add(eventName))
                _wildcardKeys.Add(eventName);
        }

        /// <summary>把订阅名从通配匹配表摘除（精确订阅名无副作用）。</summary>
        private void UntrackWildcard(string eventName)
        {
            if (_wildcardKeySet.Remove(eventName))
                _wildcardKeys.Remove(eventName);
        }

        private void AddHandler(string eventName, Delegate handler, int priority)
        {
            TrackWildcard(eventName);
            if (!_handlers.TryGetValue(eventName, out var list))
            {
                list = new List<HandlerEntry>();
                _handlers[eventName] = list;
            }
            else if (ContainsHandler(list, handler))
            {
                // 同一 handler 重复注册是"静默订阅泄漏"的常见源头：注册两次而 Off 一次仍残留一份，
                // 表现为回调翻倍、引用释放不掉。这里忽略重复注册并告警 —— 与 Off 的"一次注销干净"配对。
                Game.Logger?.Warn("Event",
                    $"duplicate handler ignored for event '{eventName}': same handler already registered " +
                    "(one Off() now removes it completely)");
                return;
            }
            Insert(list, handler, priority);
        }

        private void AddOnceHandler(string eventName, Delegate handler)
        {
            TrackWildcard(eventName);
            if (!_onceHandlers.TryGetValue(eventName, out var list))
            {
                list = new List<HandlerEntry>();
                _onceHandlers[eventName] = list;
            }
            else if (ContainsHandler(list, handler))
            {
                Game.Logger?.Warn("Event",
                    $"duplicate once-handler ignored for event '{eventName}': same handler already registered");
                return;
            }
            Insert(list, handler, 0);
        }

        /// <summary>列表里是否已有该 handler（按委托相等性：同一目标 + 同一方法）。</summary>
        private static bool ContainsHandler(List<HandlerEntry> list, Delegate handler)
        {
            for (var i = 0; i < list.Count; i++)
            {
                if (list[i].Handler == handler)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 插入到「执行顺序的逆序」位置上（见 <see cref="_handlers"/> 的说明）：
        /// 新项（同优先级里最后注册的）要排在所有 priority ≤ 自己的项之后，
        /// 即插到第一个 priority 更大的项之前。
        /// </summary>
        private static void Insert(List<HandlerEntry> list, Delegate handler, int priority)
        {
            var idx = 0;
            while (idx < list.Count && list[idx].Priority <= priority)
            {
                idx++;
            }
            list.Insert(idx, new HandlerEntry(handler, priority));
        }

        private void RemoveHandler(string eventName, Delegate handler)
        {
            RemoveFrom(_handlers, eventName, handler);
            RemoveFrom(_onceHandlers, eventName, handler);
        }

        /// <summary>
        /// 从注册表删除该 handler 的**全部**注册条目（覆盖普通与 once 两张表）。
        /// <para>
        /// 旧实现只删首个匹配：与"On 允许重复注册"组合后，注册两次、Off 一次仍残留一份，
        /// 造成重复回调与订阅泄漏。现在 On 已对同一 handler 去重（至多一份），这里再全删一层
        /// 保证"无论历史/边缘情况下有几份，一次 Off 都注销干净"。
        /// </para>
        /// </summary>
        private static void RemoveFrom(Dictionary<string, List<HandlerEntry>> map, string eventName, Delegate handler)
        {
            if (!map.TryGetValue(eventName, out var list))
            {
                return;
            }
            // 倒序删：RemoveAt 会让后面的元素左移，正序会漏删紧随其后的匹配项。
            for (var i = list.Count - 1; i >= 0; i--)
            {
                if (list[i].Handler == handler)
                {
                    list.RemoveAt(i);
                }
            }
        }

        /// <summary>
        /// 分发一次事件：**先精确匹配、再通配匹配**。
        /// <para>
        /// 顺序是刻意的：业务订阅了具体事件（如 <c>Net.OnConnected</c>）时，
        /// 它的回调应先于"泛订阅"（如 <c>Net.**</c>）执行，避免泛订阅把共享状态改掉后
        /// 具体订阅看到的是已变化的现场。两组内部各自沿用既有的优先级 / 注册序语义。
        /// </para>
        /// </summary>
        private void InvokeHandlers(string eventName, params object[] args)
        {
            // 1) 精确匹配（既有行为，保持完全不变）
            InvokeList(eventName, args);

            // 2) 通配匹配：逐个注册过的通配名去匹配本次事件名
            if (_wildcardKeys.Count == 0) return;
            // 快照：回调里可能再 On/Off 通配订阅，直接遍历原表会抛 InvalidOperationException
            var patterns = _wildcardKeys.ToArray();
            foreach (var pattern in patterns)
            {
                // 事件名本身恰好等于该通配名时，已由上面的精确匹配分发过，不重复触发
                if (string.Equals(pattern, eventName, StringComparison.Ordinal)) continue;
                if (!WildcardMatches(pattern, eventName)) continue;
                InvokeList(pattern, args);
            }
        }

        /// <summary>
        /// 通配匹配：事件名与订阅名均以 <c>.</c> 分段；<c>*</c> 匹配**恰好一段**，
        /// <c>**</c> 匹配**一段或多段**（<c>Net.**</c> 命中 <c>Net.OnConnected</c>，
        /// <c>Net.*</c> 只命中两段名）。段比较大小写敏感，与精确匹配的字典口径一致。
        /// </summary>
        private static bool WildcardMatches(string pattern, string name)
        {
            if (string.IsNullOrEmpty(pattern) || string.IsNullOrEmpty(name)) return false;
            return MatchSegments(pattern.Split('.'), 0, name.Split('.'), 0);
        }

        private static bool MatchSegments(string[] p, int pi, string[] n, int ni)
        {
            while (pi < p.Length)
            {
                var seg = p[pi];
                if (seg == "**")
                {
                    // ** 消耗 1..(剩余段数) 段，逐一回溯
                    for (var take = 1; ni + take <= n.Length; take++)
                    {
                        if (MatchSegments(p, pi + 1, n, ni + take)) return true;
                    }
                    return false;
                }

                if (ni >= n.Length) return false;
                if (seg != "*" && !string.Equals(seg, n[ni], StringComparison.Ordinal)) return false;
                pi++;
                ni++;
            }
            return ni == n.Length;
        }

        /// <summary>
        /// 按**已注册的订阅名**（可能是通配名）分发其订阅表：普通订阅 + 一次性订阅。
        /// </summary>
        private void InvokeList(string eventName, object[] args)
        {
            if (_handlers.TryGetValue(eventName, out var list) && list.Count > 0)
            {
                // 快照 + "仍在册"检查（旧实现直接对原表做倒序索引遍历）：
                //   ① 快照固定本轮条目序列 —— 已执行过的不会因列表左移被重复触发，也不会取越界下标；
                //   ② 每个条目执行前检查它是否仍挂在当前注册表里 —— 回调内 Off / OffAll 即时生效，
                //      被注销者本帧不再触发；OffAll 把整表摘出字典后，剩余条目全部跳过。
                // 表按「执行顺序的逆序」存放（见 _handlers 的说明），所以倒着走 ——
                // 效果 = 优先级大者先执行、同优先级后注册者先执行。
                var snapshot = new List<HandlerEntry>(list);
                for (var i = snapshot.Count - 1; i >= 0; i--)
                {
                    var entry = snapshot[i];
                    if (!StillRegistered(eventName, list, entry))
                    {
                        continue;
                    }
                    try
                    {
                        entry.Handler?.DynamicInvoke(args);
                    }
                    catch (Exception ex)
                    {
                        Game.Logger?.Error("Event", $"Handler error [{eventName}]: {ex.Message}", ex);
                    }
                }
            }

            if (_onceHandlers.TryGetValue(eventName, out var onceList) && onceList.Count > 0)
            {
                // 一次性订阅与普通订阅用同一套顺序语义：表按「执行逆序」存放，倒着走
                // （旧实现快照后按注册序正序触发，与 On 的"后注册先执行"恰好相反）。
                // 逐条消费（而不是整表 Clear）：分发中 Off 掉某条 once 时同样即时生效。
                var snapshot = new List<HandlerEntry>(onceList);
                for (var i = snapshot.Count - 1; i >= 0; i--)
                {
                    // OffAll 已摘除该事件表（或表被重建）：剩余 once 不再触发
                    if (!_onceHandlers.TryGetValue(eventName, out var current) || !ReferenceEquals(current, onceList))
                    {
                        continue;
                    }
                    // Remove 兼作两个判定：条目仍在表里才触发；不在（已被分发中 Off 消费）则跳过
                    if (!onceList.Remove(snapshot[i]))
                    {
                        continue;
                    }
                    try
                    {
                        snapshot[i].Handler?.DynamicInvoke(args);
                    }
                    catch (Exception ex)
                    {
                        Game.Logger?.Error("Event", $"Once handler error [{eventName}]: {ex.Message}", ex);
                    }
                }
            }
        }

        /// <summary>
        /// 分发期间的"仍在册"判定：条目必须仍挂在**当前注册表**里才算数。
        /// 用于让 Off / OffAll 在分发过程中即时生效：
        /// <list type="bullet">
        /// <item>OffAll 把整表摘出字典（或 On 重建了新表）⇒ 直接判 false，剩余条目全部不再触发；</item>
        /// <item>单条 Off 从表里删掉了该条目 ⇒ 由 <see cref="List{T}.Contains"/> 判 false。</item>
        /// </list>
        /// 事件 handler 数量小（个位数），这里的线性 Contains 是常数级开销。
        /// </summary>
        private bool StillRegistered(string eventName, List<HandlerEntry> list, HandlerEntry entry)
        {
            if (!_handlers.TryGetValue(eventName, out var current) || !ReferenceEquals(current, list))
            {
                return false;
            }
            return list.Contains(entry);
        }
    }
}