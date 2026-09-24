// ─────────────────────────────────────────────────────────────────────────────
// CloverEngine · Runtime/Core/OrderedAsyncResult.cs
// 乱序异步结果按下标落位 —— 通用横切能力。
//
// 适用面：并行加载 N 份资源、逐帧确定性回放收集 N 帧结果、N 个子请求的结果汇总
//   —— 这些场景要解决的问题相同。
//
// 为什么需要它：**异步回调的到达顺序 ≠ 业务需要的顺序**。
//   最典型的两个后果：
//     ① 帧序随回调次序抖动 —— N 帧结果按"谁先回来谁先进"拼起来，同一段输入每次跑出来不一样
//        （回放不可复现、对比判据随机红）；
//     ② 结果被"到达即消费" —— 消费方在处理第 1 个结果时看不到第 3 个（业务只能自己再攒一层）。
//   本类把"按下标落位 + 齐了才交付"做成一个可复用的小件：到达顺序随便，交付顺序恒为下标 0..N-1。
//
// 语义约束（改一条 = 语义漂移）：
//   ① `index` 越界 ⇒ **忽略**本次 + 降频 Warn（⛔ 不许抛：异步回调路径上抛异常会打断调用方的循环）；
//   ② 同一 `index` 重复 `Put` ⇒ **覆盖** + 降频 Warn（后到的算新值；静默覆盖会让"结果错"无从查起）；
//   ③ `TryTakeOrdered` 仅在 `IsComplete` 时返回 `true`，且返回**按下标升序**的数组并**清空自己**
//      （可复用：清空后 <see cref="FilledCount"/> 归零，重新 `Put` 一轮即可再交付一次）；
//      未齐时 `ordered = null` 且返回 `false`（⛔ 不返回半成品 —— 半成品正是本类要消灭的东西）；
//   ④ `count <= 0` ⇒ 立即视为 complete，`TryTakeOrdered` 返回**空数组 + true**（零任务也是"齐了"）；
//   ⑤ 非线程安全：约定**主线程使用**（与 <see cref="LogThrottle"/> 一致）；多线程回调请先在各自线程
//      排队、回到主线程再 `Put`。
// ─────────────────────────────────────────────────────────────────────────────

using System;

namespace CloverEngine
{
    /// <summary>
    /// 乱序异步结果按下标落位：回调可以在任意顺序、任意时刻到达，交付时保证是**下标 0..Count-1** 的顺序。
    /// <para>
    /// <b>用途</b>：把"谁先回来谁先进"的到达顺序，归一成业务需要的确定性顺序。典型场景：
    /// 并行加载 N 份资源后按序号组装、逐帧收集 N 帧结果（帧序不能随回调次序抖动）、
    /// N 个子请求的结果汇总后再算总账。
    /// </para>
    /// <para>
    /// <b>用法</b>：
    /// <code>
    /// var pending = new OrderedAsyncResult&lt;byte[]&gt;(3);
    /// StartLoad(i =&gt; pending.Put(i, bytes[i]));           // 完成顺序任意
    /// if (pending.TryTakeOrdered(out var ordered))         // 只有齐了才拿到（且自动清空，可复用）
    ///     Assemble(ordered[0], ordered[1], ordered[2]);    // 顺序恒为下标序
    /// </code>
    /// </para>
    /// <para>
    /// <b>边界</b>：越界 <see cref="Put"/> 被忽略、重复 <see cref="Put"/> 覆盖，两者都留降频 Warn；
    /// <c>count &lt;= 0</c> 时立刻可交付（空数组）。非线程安全，主线程使用。
    /// </para>
    /// </summary>
    public sealed class OrderedAsyncResult<T>
    {
        /// <summary>日志 tag（引擎惯例：各能力用类名自报家门）。</summary>
        private const string Tag = "OrderedAsyncResult";

        /// <summary>降频 key：越界（同一 key 聚合，避免每个坏下标各刷一条）。</summary>
        private const string RangeKey = "OrderedAsyncResult.range";

        /// <summary>降频 key：重复落位（同一 key 聚合）。</summary>
        private const string DuplicateKey = "OrderedAsyncResult.duplicate";

        /// <summary>按**下标**存放的结果（长度 = <see cref="Count"/>，交付前恒不完整）。</summary>
        private readonly T[] _slots;

        /// <summary>每个下标是否已被写过（<typeparamref name="T"/> 允许 null，不能靠"值非 null"判已写）。</summary>
        private readonly bool[] _written;

        /// <summary>
        /// 创建等待 <paramref name="count"/> 个结果的小件。
        /// </summary>
        /// <param name="count">
        /// 期待的结果个数（下标 0..count-1）。<c>&lt;= 0</c> ⇒ 立即视为 complete
        /// （<see cref="TryTakeOrdered"/> 会返回空数组 + <c>true</c>），并留一次降频 Warn 说明是退化用法。
        /// </param>
        public OrderedAsyncResult(int count)
        {
            Count = count > 0 ? count : 0;
            _slots = Count == 0 ? Array.Empty<T>() : new T[Count];
            _written = Count == 0 ? Array.Empty<bool>() : new bool[Count];

            // count <= 0：零任务也算"齐了"（否则调用方会永远等一个不会到来的交付）。
            IsComplete = Count == 0;
            if (Count == 0)
            {
                // 非预期分支留痕（降频，只报一次）：正常用法 count >= 1；传 0/负数通常是上游数量算错。
                LogThrottle.WarnOnce(Tag, "OrderedAsyncResult.zeroCount",
                    $"构造时 count={count}（<= 0），已按 0 处理并立即视为 complete：" +
                    "TryTakeOrdered 会返回空数组 + true；若上游数量不该为 0，请检查它是否算错");
            }
        }

        /// <summary>期待的结果个数（= 构造时传入的正数，<c>&lt;= 0</c> 时为 0）。</summary>
        public int Count { get; }

        /// <summary>
        /// 已落位的下标个数（重复 <see cref="Put"/> 同一 index 不重复计数）。
        /// 对外只读（<c>private set</c> 只供本类内部的落位 / 清空改写）。
        /// </summary>
        public int FilledCount { get; private set; }

        /// <summary>是否已收齐（<c>true</c> ⇒ <see cref="TryTakeOrdered"/> 可交付）。对外只读。</summary>
        public bool IsComplete { get; private set; }

        /// <summary>
        /// 把一个结果落到指定下标（顺序任意）。
        /// <para>
        /// 边界：<paramref name="index"/> 越界 ⇒ **忽略** + 降频 Warn（不抛，异步回调路径上抛异常会打断调用方）；
        /// 同一 <paramref name="index"/> 重复调用 ⇒ **覆盖**旧值 + 降频 Warn（后到的算新值）。
        /// </para>
        /// </summary>
        public void Put(int index, T value)
        {
            if (index < 0 || index >= Count)
            {
                // 越界：忽略 + 降频留痕（⛔ 不许抛）。忽略而不是扩容：Count 是业务给出的契约，
                // 悄悄增长会让"齐了才交付"的语义失效（永远差一个不存在的下标可就交付不了）。
                LogThrottle.WarnThrottled(Tag, RangeKey,
                    $"Put 的 index={index} 越界（Count={Count}），本次结果已忽略；" +
                    "越界通常是下标算错（用了 1 起编号 / 遍历了比任务数更多的项）");
                return;
            }

            if (_written[index])
            {
                // 重复落位：覆盖 + 降频留痕（静默覆盖会让"结果不对"无从查起）。
                LogThrottle.WarnThrottled(Tag, DuplicateKey,
                    $"Put 的 index={index} 之前已落位过，本次已覆盖旧值（Count={Count}）；" +
                    "重复回调通常是重试 / 重入未去重");
            }
            else
            {
                _written[index] = true;
                FilledCount++;
            }

            _slots[index] = value;

            if (FilledCount >= Count && !IsComplete)
            {
                IsComplete = true;
                Game.Logger?.Info(Tag, $"{Count} 个结果已收齐，可交付（TryTakeOrdered 会按下标序返回并清空）");
            }
        }

        /// <summary>
        /// 取走结果（仅在 <see cref="IsComplete"/> 时成功）。
        /// <para>
        /// 成功 ⇒ <paramref name="ordered"/> = 长度 <see cref="Count"/> 的数组、**按下标升序**，
        /// 并**清空自己**（可复用：清空后 <see cref="FilledCount"/> 归零、<see cref="IsComplete"/> 回到初始态，
        /// 再 <see cref="Put"/> 一轮即可交付下一次）。
        /// </para>
        /// <para>未收齐 ⇒ <paramref name="ordered"/> = <c>null</c> 且返回 <c>false</c>（⛔ 不交付半成品）。</para>
        /// <para><see cref="Count"/> 为 0 ⇒ 返回**空数组** + <c>true</c>（每次调用都成立）。</para>
        /// </summary>
        /// <returns>本次是否交付了完整结果。</returns>
        public bool TryTakeOrdered(out T[] ordered)
        {
            if (!IsComplete)
            {
                // 不返回半成品：半成品正是本类要消灭的东西（调用方拿到 3 缺 1 的数组更难发现）。
                ordered = null;
                return false;
            }

            var result = new T[Count];
            if (Count > 0)
            {
                Array.Copy(_slots, result, Count);

                // 清空（可复用）：槽位 / 已写标记 / 计数一起复位，否则第二轮会把旧值当成已落位。
                Array.Clear(_slots, 0, Count);
                Array.Clear(_written, 0, Count);
                FilledCount = 0;
                IsComplete = false;
            }

            ordered = result;
            return true;
        }
    }
}
