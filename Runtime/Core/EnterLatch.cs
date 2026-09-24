// ─────────────────────────────────────────────────────────────────────────────
// CloverEngine · Runtime/Core/EnterLatch.cs
// 「进入触发一次、持续逗留不重复、离开重新武装」的通用**状态跃迁闩锁**（纯值类型，可直接离线断言）。
//
// 口径要点：① 区与载荷点均由调用方以泛型给出（⛔ 引擎不认任何题材语义）；
//   ② "是否已触发过"由 `HasLastTrigger` 布尔位表达（泛型里没有"某类型的空值常量"可用）。
//
// 适用面：**任何"踩到某区域就触发一次"**（出口 / 接缝 / 陷阱 / 传送门 / 治疗泉 / 拾取区）都用
//   这一条判据；同一份口径会被"生产路径"与"离线断言"两处用到。
//   ⛔ "记住上一格"的口径在**沿出口列 / 接缝逐格挪动**时会**每格各发一次** ⇒ 必须是状态跃迁闩锁。
//
// 语义（= 状态跃迁闩锁，**不是**"记住上一格"）：
//   ① `inside == true` 且尚未发过 ⇒ 发一次（并记下本次触发点）；
//   ② 仍 `inside`（**同一格或沿区域逐格挪动**）⇒ 不再发；
//   ③ `inside == false` ⇒ 重新武装，下次再进入可再发（⛔ 不许把角色卡在区域里出不来）。
//
// 用法：
//   `var latch = new EnterLatch<Vector2Int>();`（持在持有者字段里，**不要每帧 new**）
//   `if (latch.ShouldEmit(onExit, grid)) { /* 发过门请求 */ }`
//   `latch.Reset()` 在"进图落位 / 传送 / 复活 / 复位"后调 —— 把玩家搬到别处后区域要能再触发一次。
//
// 边界（⛔ 防止当万能药用）：
//   · **只判"要不要发"**，不判"发去哪"、不判"什么算区域"（`inside` 由调用方按唯一口径算好再传）；
//   · 不线程安全（主线程使用），不持有任何 Unity 对象；
//   · 纯函数式：同样的入参序列 ⇒ 同样的出参序列（状态只有本值类型的三个字段）。
// ─────────────────────────────────────────────────────────────────────────────

namespace CloverEngine
{
    /// <summary>
    /// 进入闩锁：进入某个"区域"时触发一次，持续逗留不重复触发，离开后重新武装。
    /// <para>纯值类型（无 Unity 依赖、无分配）⇒ 可离线逐帧断言；<typeparamref name="T"/> =
    /// 触发点载荷（如 <c>Vector2Int</c> 格坐标 / 实体 id / 空 `struct`）。</para>
    /// <para>⛔ 它**不是**"记住上一格"：沿区域逐格挪动不会重复触发（见文件头 ③）。</para>
    /// </summary>
    /// <typeparam name="T">触发点载荷（只用于日志 / 排障读取，不参与判定）。</typeparam>
    public struct EnterLatch<T> where T : struct
    {
        /// <summary>是否已在"本轮进入"里发过（0 = 已重新武装，1 = 已发过）。</summary>
        private byte _fired;

        /// <summary>最近一次触发时的载荷（由 <see cref="ShouldEmit(bool, T)"/> 写入）。</summary>
        private T _last;

        /// <summary>是否触发过（区分"从未触发"与"触发过、载荷恰好是默认值"）。</summary>
        private bool _hasLast;

        /// <summary>是否处于"已发过、尚未重新武装"状态。</summary>
        public bool Fired => _fired != 0;

        /// <summary>是否触发过（<see cref="Reset"/> 后为 false）。</summary>
        public bool HasLastTrigger => _hasLast;

        /// <summary>最近一次触发时记下的载荷（未触发过时 = <c>default(T)</c>，用 <see cref="HasLastTrigger"/> 判定）。</summary>
        public T LastTriggerAt => _last;

        /// <summary>
        /// 是否应当发出"进入"事件（不记录触发点，载荷恒为 <c>default(T)</c>）。
        /// </summary>
        /// <param name="inside">本帧是否处在区域内（调用方按唯一口径算好再传进来）。</param>
        public bool ShouldEmit(bool inside) => ShouldEmit(inside, default);

        /// <summary>
        /// 是否应当发出"进入"事件。
        /// <para>① <paramref name="inside"/> 为 false ⇒ 重新武装并返回 false；
        /// ② 本轮已发过 ⇒ 返回 false（同一区域 / 沿区域逐格挪动都只发一次）；
        /// ③ 否则发一次并记下 <paramref name="at"/>。</para>
        /// </summary>
        /// <param name="inside">本帧是否处在区域内。</param>
        /// <param name="at">本次触发点（只供日志 / 排障读；不参与判定）。</param>
        public bool ShouldEmit(bool inside, T at)
        {
            if (!inside)
            {
                // 离开区域 ⇒ 重新武装（下一次再进入可再发一次）
                _fired = 0;
                return false;
            }

            if (_fired != 0) return false;      // 已在本次"进入"里发过

            _fired = 1;
            _last = at;
            _hasLast = true;
            return true;
        }

        /// <summary>重新武装（进图落位 / 传送 / 复活 / 复位：把角色搬到别处后，区域要能再触发一次）。</summary>
        public void Reset()
        {
            _fired = 0;
            _last = default;
            _hasLast = false;
        }
    }
}
