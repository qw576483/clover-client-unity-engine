using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace CloverEngine
{
    /// <summary>
    /// 定时器，API 与服务端 timer.Scheduler 完全同名。
    /// 实现为主线程 `List<TimerEntry>` 全表线性遍历（非最小堆），Update 驱动；支持命名定时器和作用域批量停止。
    /// </summary>
    public interface ITimer
    {
        /// <summary>
        /// 延迟执行一次回调
        /// </summary>
        /// <param name="delay">延迟时间（秒）</param>
        /// <param name="callback">回调函数</param>
        /// <returns>定时器唯一标识符，可用于停止</returns>
        long After(float delay, Action callback);

        /// <summary>
        /// 延迟执行一次回调，属于指定作用域
        /// </summary>
        /// <param name="delay">延迟时间（秒）</param>
        /// <param name="callback">回调函数</param>
        /// <param name="scope">作用域名称，可通过StopScope批量停止</param>
        /// <returns>定时器唯一标识符，可用于停止</returns>
        long After(float delay, Action callback, string scope);

        /// <summary>
        /// 循环执行回调
        /// </summary>
        /// <param name="interval">循环间隔时间（秒）</param>
        /// <param name="callback">回调函数</param>
        /// <returns>定时器唯一标识符，可用于停止</returns>
        long Every(float interval, Action callback);

        /// <summary>
        /// 循环执行回调，属于指定作用域
        /// </summary>
        /// <param name="interval">循环间隔时间（秒）</param>
        /// <param name="callback">回调函数</param>
        /// <param name="scope">作用域名称，可通过StopScope批量停止</param>
        /// <returns>定时器唯一标识符，可用于停止</returns>
        long Every(float interval, Action callback, string scope);

        /// <summary>
        /// 延迟执行一次回调，按【不受 timeScale 影响】的真实时间计时。
        /// <para>
        /// 为什么必须有它：<c>Tick</c> 是靠引擎每帧传进来的 <c>dt</c> 推进的，而 <c>dt</c> 来自
        /// <c>Time.deltaTime</c> —— <b>一旦 <c>Time.timeScale = 0</c>（暂停菜单 / 结算屏 / GameOver），
        /// <c>dt</c> 恒为 0，任何定时器都永远不再触发</b>。
        /// 实测踩过：GameOver 屏上"停 4 秒后回标题"用的是普通 <c>After</c>，
        /// 结果那张屏<b>永久卡死</b>（复现：一个"1 秒"定时器等了 62 秒，
        /// 直到 <c>timeScale</c> 被恢复才补执行）。
        /// </para>
        /// <para>
        /// 凡是"画面已经被冻结、但还需要继续走时间"的场合（暂停菜单自动返回、
        /// 结算屏倒计时、GameOver 回标题、UI 动效延时），一律用这个方法。
        /// </para>
        /// </summary>
        long AfterUnscaled(float delay, Action callback);

        /// <summary>循环执行回调，按不受 timeScale 影响的真实时间计时（理由见 <see cref="AfterUnscaled"/>）。</summary>
        long EveryUnscaled(float interval, Action callback);

        /// <summary>
        /// 命名延迟执行一次回调，同名时先停止旧定时器
        /// </summary>
        /// <param name="name">定时器名称，用于唯一标识</param>
        /// <param name="delay">延迟时间（秒）</param>
        /// <param name="callback">回调函数</param>
        /// <returns>定时器唯一标识符</returns>
        long AfterName(string name, float delay, Action callback);

        /// <summary>
        /// 命名循环执行回调，同名时先停止旧定时器
        /// </summary>
        /// <param name="name">定时器名称，用于唯一标识</param>
        /// <param name="interval">循环间隔时间（秒）</param>
        /// <param name="callback">回调函数</param>
        /// <returns>定时器唯一标识符</returns>
        long EveryName(string name, float interval, Action callback);

        /// <summary>
        /// 停止指定ID的定时器
        /// </summary>
        /// <param name="id">定时器标识符</param>
        void Stop(long id);

        /// <summary>
        /// 停止指定名称的所有定时器
        /// </summary>
        /// <param name="name">定时器名称</param>
        void StopNamed(string name);

        /// <summary>
        /// 停止指定作用域的所有定时器
        /// </summary>
        /// <param name="scope">作用域名称</param>
        void StopScope(string scope);

        /// <summary>
        /// 停止所有定时器
        /// </summary>
        void StopAll();

        /// <summary>
        /// 推进定时器，每帧由Update调用
        /// </summary>
        /// <param name="dt">帧间隔时间（秒）</param>
        void Tick(float dt);
    }

    internal class Timer : ITimer
    {
        // id 从 1 开始发放：0 保留为「无效 / 未持有」哨兵。
        // 调用方普遍用 0 表示"没有定时器"（例：SceneModule._progressTimerId 初值与重置值都是 0），
        // 若 id 从 0 起发，Stop(0) 的墓碑会命中"下一个恰好拿到 id 0 的定时器"（见 Stop 的注释）。
        private long _nextId = 1;
        private readonly List<TimerEntry> _entries = new();

        /// <summary>
        /// 待移除的定时器 id（标记式停止 + 延迟清理）。
        /// <para>
        /// 用 <see cref="HashSet{T}"/> 而非 List：旧实现用 <c>List.Contains</c> 线性扫描，
        /// 每帧 O(条目数 × 待移除数)；停止判定是热路径，必须 O(1)。
        /// </para>
        /// <para>
        /// 为什么是"标记"而不是就地 <c>RemoveAt</c>：<see cref="Tick"/> 用倒序索引遍历，
        /// 回调内停止定时器若立刻改列表结构会让下标错位（越界或重复触发）。
        /// 停止后条目在本帧的遍历中被跳过并物理移除 —— 即"本帧立即不再触发"。
        /// </para>
        /// </summary>
        private readonly HashSet<long> _toRemove = new();
        private readonly Dictionary<string, List<long>> _namedTimers = new();
        private readonly Dictionary<string, List<long>> _scopedTimers = new();

        /// <summary>
        /// 延迟执行一次回调
        /// </summary>
        /// <param name="delay">延迟时间（秒）</param>
        /// <param name="callback">回调函数</param>
        /// <returns>定时器唯一标识符，可用于停止</returns>
        public long After(float delay, Action callback) => Schedule(delay, 0, callback, null, null);

        /// <summary>
        /// 延迟执行一次回调，属于指定作用域
        /// </summary>
        /// <param name="delay">延迟时间（秒）</param>
        /// <param name="callback">回调函数</param>
        /// <param name="scope">作用域名称，可通过StopScope批量停止</param>
        /// <returns>定时器唯一标识符，可用于停止</returns>
        public long After(float delay, Action callback, string scope) => Schedule(delay, 0, callback, null, scope);

        /// <summary>
        /// 循环执行回调
        /// </summary>
        /// <param name="interval">循环间隔时间（秒），必须 &gt; 0</param>
        /// <param name="callback">回调函数</param>
        /// <returns>定时器唯一标识符，可用于停止；interval 非法时返回 -1 且不创建</returns>
        public long Every(float interval, Action callback)
        {
            if (!ValidateEveryInterval(interval, nameof(Every))) return -1;
            return Schedule(interval, interval, callback, null, null);
        }

        /// <summary>
        /// 循环执行回调，属于指定作用域
        /// </summary>
        /// <param name="interval">循环间隔时间（秒），必须 &gt; 0</param>
        /// <param name="callback">回调函数</param>
        /// <param name="scope">作用域名称，可通过StopScope批量停止</param>
        /// <returns>定时器唯一标识符，可用于停止；interval 非法时返回 -1 且不创建</returns>
        public long Every(float interval, Action callback, string scope)
        {
            if (!ValidateEveryInterval(interval, nameof(Every))) return -1;
            return Schedule(interval, interval, callback, null, scope);
        }

        /// <inheritdoc/>
        public long AfterUnscaled(float delay, Action callback) => Schedule(delay, 0, callback, null, null, true);

        /// <inheritdoc/>
        public long EveryUnscaled(float interval, Action callback)
        {
            if (!ValidateEveryInterval(interval, nameof(EveryUnscaled))) return -1;
            return Schedule(interval, interval, callback, null, null, true);
        }

        /// <summary>
        /// 命名延迟执行一次回调，同名时先停止旧定时器
        /// </summary>
        /// <param name="name">定时器名称，用于唯一标识</param>
        /// <param name="delay">延迟时间（秒）</param>
        /// <param name="callback">回调函数</param>
        /// <returns>定时器唯一标识符</returns>
        public long AfterName(string name, float delay, Action callback)
        {
            StopNamed(name);
            return Schedule(delay, 0, callback, name, null);
        }

        /// <summary>
        /// 命名循环执行回调，同名时先停止旧定时器
        /// </summary>
        /// <param name="name">定时器名称，用于唯一标识</param>
        /// <param name="interval">循环间隔时间（秒），必须 &gt; 0</param>
        /// <param name="callback">回调函数</param>
        /// <returns>定时器唯一标识符；interval 非法时返回 -1（不创建、也不停掉同名旧定时器）</returns>
        public long EveryName(string name, float interval, Action callback)
        {
            // 先校验再顶替：非法参数完全不动现有状态（否则旧定时器被停掉、新的又没建）。
            if (!ValidateEveryInterval(interval, nameof(EveryName))) return -1;
            StopNamed(name);
            return Schedule(interval, interval, callback, name, null);
        }

        /// <summary>
        /// 停止指定ID的定时器。id 不存在时静默忽略（"停一个已触发完成的定时器"是常见正常路径）。
        /// <para>
        /// 停止是**即时生效**的：条目被标记后，本帧 <see cref="Tick"/> 的遍历轮到它时会直接跳过并移除；
        /// 若在回调内调用，即使目标的遍历下标位于当前回调之后（更小索引）也不会再触发 ——
        /// 旧实现把 id 记到下一帧才处理，回调内停掉的定时器本帧仍会被触发一次。
        /// </para>
        /// </summary>
        /// <param name="id">定时器标识符</param>
        public void Stop(long id)
        {
            // 0 / 负数 = 「无效 / 未持有」哨兵，绝不能进墓碑集合。
            // 实测（2026-09-19，客户端 E 编号见 修复记录.md）：SceneModule 用 0 初始化 _progressTimerId 且无条件 Stop(它)，
            // 而旧实现 id 从 0 起发 ⇒ Stop(0) 把 0 写进墓碑，紧接着新建的进度轮询 timer 正好拿到 id 0
            // ⇒ 它在第一次 Tick 就被墓碑移除、一次都没轮询 ⇒ progress 停在 0.9、allowSceneActivation
            // 永不置 true ⇒ 场景永不激活 ⇒ 全新 Play 会话第一次 Game.Scene.Load 必死（表现为黑屏）。
            if (id <= 0) return;
            _toRemove.Add(id);
        }

        /// <summary>
        /// 停止指定名称的所有定时器（即时生效，语义同 <see cref="Stop"/>）。
        /// </summary>
        /// <param name="name">定时器名称</param>
        public void StopNamed(string name)
        {
            if (_namedTimers.TryGetValue(name, out var ids))
            {
                foreach (var id in ids)
                    _toRemove.Add(id);
                // 字典条目立即清除：名字 → id 列表只是"停止入口"的索引，
                // 条目的物理移除由 Tick 统一完成（条目移除时也会把自己从这两个字典里摘掉）。
                _namedTimers.Remove(name);
            }
        }

        /// <summary>
        /// 停止指定作用域的所有定时器（即时生效，语义同 <see cref="Stop"/>）。
        /// </summary>
        /// <param name="scope">作用域名称</param>
        public void StopScope(string scope)
        {
            if (_scopedTimers.TryGetValue(scope, out var ids))
            {
                foreach (var id in ids)
                    _toRemove.Add(id);
                _scopedTimers.Remove(scope);
            }
        }

        /// <summary>
        /// 停止所有定时器（即时生效，语义同 <see cref="Stop"/>）。
        /// <para>
        /// **不许直接 <c>_entries.Clear()</c>**：若在定时器回调内调用，外层 <see cref="Tick"/>
        /// 的倒序索引循环会对已清空的列表取下标（ArgumentOutOfRangeException）并中断整批 tick。
        /// 这里只做标记，物理清理由 Tick 的遍历完成。
        /// </para>
        /// </summary>
        public void StopAll()
        {
            foreach (var e in _entries)
                _toRemove.Add(e.Id);
            _namedTimers.Clear();
            _scopedTimers.Clear();
        }

        /// <summary>
        /// 推进定时器，每帧由Update调用
        /// </summary>
        /// <param name="dt">帧间隔时间（秒）。
        /// <b>Unscaled 条目刻意忽略该参数</b>：它们的用途就是 <c>timeScale=0</c>（暂停菜单 / 结算屏）时仍走真实时间，
        /// 只能按 <c>Time.unscaledDeltaTime</c> 推进；若改听传入 dt，调用方传的缩放时间会把它们一起冻住，
        /// <see cref="AfterUnscaled"/> 的承诺即不成立。缩放条目的时间源仍由调用方决定（Game.Tick 传 Time.deltaTime）。</param>
        public void Tick(float dt)
        {
            for (var i = _entries.Count - 1; i >= 0; i--)
            {
                var e = _entries[i];

                // 已被 Stop/StopNamed/StopScope/StopAll 标记（含本帧更早的回调内标记）：
                // 本帧不再触发，跳过并移除 —— 停止即时生效，且回调内停止不会错位外层倒序索引。
                if (_toRemove.Contains(e.Id))
                {
                    RemoveEntryAt(i);
                    continue;
                }

                // 不受 timeScale 影响的定时器用真实帧间隔推进（刻意忽略传入 dt，理由见方法注释）。
                e.Elapsed += e.Unscaled ? UnscaledDeltaTime() : dt;

                if (e.Elapsed < e.Delay) continue;

                try
                {
                    e.Callback?.Invoke();
                }
                catch (Exception ex)
                {
                    Game.Logger?.Error("Timer", $"Timer callback error: {ex.Message}", ex);
                }

                // 回调内停止了自己（或 StopAll）：本帧不再按间隔重排，直接移除。
                if (_toRemove.Contains(e.Id))
                {
                    RemoveEntryAt(i);
                    continue;
                }

                if (e.Interval > 0)
                {
                    e.Elapsed -= e.Delay;
                    e.Delay = e.Interval;
                }
                else
                {
                    RemoveEntryAt(i);
                }
            }

            // 回调内可能停掉了"已经遍历过"的条目（索引更大者不会再被本轮循环访问）：
            // 补删一遍保证停止最终生效；顺带清掉指向不存在条目的无效 id（如已触发完成的定时器被再次 Stop）。
            if (_toRemove.Count > 0)
            {
                for (var i = _entries.Count - 1; i >= 0; i--)
                {
                    if (_toRemove.Contains(_entries[i].Id))
                        RemoveEntryAt(i);
                }
                _toRemove.Clear();
            }
        }

        /// <summary>
        /// 读取真实帧间隔。单独成一个方法并禁止内联：把对 Unity 原生成员（<c>Time.unscaledDeltaTime</c>
        /// 是 ECall）的访问隔离出 <see cref="Tick(float)"/> 主循环 ——
        /// Tick 的 JIT 不需要解析这条原生调用，非 Unscaled 路径在任何托管宿主里都能跑。
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static float UnscaledDeltaTime() => UnityEngine.Time.unscaledDeltaTime;

        /// <summary>物理移除 <paramref name="index"/> 处的条目，并把它从命名/作用域索引中摘掉。</summary>
        private void RemoveEntryAt(int index)
        {
            Unregister(_entries[index]);
            _entries.RemoveAt(index);
        }

        /// <summary>
        /// 从命名/作用域索引里摘掉该条目的 id（条目被移除时必调）。
        /// <para>
        /// 旧实现只在 StopNamed/StopScope 时清字典：一次性命名/作用域定时器触发（自然完成）后
        /// 对应 id 永久滞留 —— 反复用不同 name/scope 会让字典无限增长。
        /// </para>
        /// </summary>
        private void Unregister(TimerEntry e)
        {
            if (e.Name != null && _namedTimers.TryGetValue(e.Name, out var named))
            {
                named.Remove(e.Id);
                if (named.Count == 0)
                    _namedTimers.Remove(e.Name);
            }

            if (e.Scope != null && _scopedTimers.TryGetValue(e.Scope, out var scoped))
            {
                scoped.Remove(e.Id);
                if (scoped.Count == 0)
                    _scopedTimers.Remove(e.Scope);
            }
        }

        /// <summary>
        /// 校验循环定时器的 interval。<c>&lt;= 0</c>（含 NaN）非法：旧实现会把它当一次性定时器
        /// 触发一次即删，与"循环执行"的承诺相反且无任何日志。
        /// 这里**拒绝创建并告警**，不静默改语义；调用方拿到 -1 即"没建"（多半是参数写错）。
        /// </summary>
        private static bool ValidateEveryInterval(float interval, string api)
        {
            if (interval > 0f) return true;
            Game.Logger?.Warn("Timer",
                $"{api}(interval={interval}) 非法：循环间隔必须 > 0（旧行为是静默退化成一次性定时器，已改为拒绝创建）。");
            return false;
        }

        private long Schedule(float delay, float interval, Action callback, string name, string scope,
                              bool unscaled = false)
        {
            var id = _nextId++;
            var entry = new TimerEntry
            {
                Id = id,
                Delay = delay,
                Interval = interval,
                Callback = callback,
                Name = name,
                Scope = scope,
                Elapsed = 0,
                Unscaled = unscaled
            };
            _entries.Add(entry);

            if (name != null)
            {
                if (!_namedTimers.TryGetValue(name, out var ids))
                {
                    ids = new List<long>();
                    _namedTimers[name] = ids;
                }
                ids.Add(id);
            }

            if (scope != null)
            {
                if (!_scopedTimers.TryGetValue(scope, out var ids))
                {
                    ids = new List<long>();
                    _scopedTimers[scope] = ids;
                }
                ids.Add(id);
            }

            return id;
        }

        private class TimerEntry
        {
            public long Id;
            public float Delay;
            public float Interval;
            public Action Callback;
            public string Name;
            public string Scope;
            public float Elapsed;
            /// <summary>true = 按 Time.unscaledDeltaTime 推进（不受 Time.timeScale 影响）。</summary>
            public bool Unscaled;
            }
    }
}