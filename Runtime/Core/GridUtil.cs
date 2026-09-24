// ─────────────────────────────────────────────────────────────────────────────
// CloverEngine · Runtime/Core/GridUtil.cs
// 矩形 → 整数格遍历（纯函数 / 无状态 / 不持有任何 Unity 对象）—— 通用底座，下沉到引擎。
//
// 出处：**从项目重复实现收敛而来**，依据 4 个调用点（clover-project-super-mario，
//   client/Assets/Scripts/ 下），四份**逐字相同**：
//     · Module/Player/PlayerActor.cs:1043-1060      Overlap(Rect)      —— 玩家碰撞盒
//     · Module/Entities/EnemyModule.cs:286-303      EnemyGrid.Overlap  —— 敌人共用
//       （该处注释已写明代价："原先它是 Goomba 的私有方法，新增乌龟时复制一份就会出现两套边界处理"）
//     · Module/Entities/ItemModule.cs:355-365       Item.Overlap       —— 道具
//     · Module/Entities/FireballModule.cs:248-257   Overlap(Rect)      —— 火球
//   四份口径完全相同：`xMin = FloorToInt(r.xMin)`、`xMax = FloorToInt(r.xMax - 0.0001f)`，
//   y 外层 / x 内层、**升序**，逐格 yield Vector2Int。
//   ⇒ 本类逐字复刻该口径（连 `- 0.0001f` 的减位都保留）：那 4 处 `foreach (var c in Overlap(r))`
//     可直接换成 `GridUtil.ForEach(r, 已缓存的委托)`。
//
// ★ 为什么是 FloorToInt（而不是 RoundToInt / `(int)` 强转）：
//   · `(int)` 强转对**负数向零截断**（x = -0.5 ⇒ 0）⇒ 坑左侧那半格被算进第 0 格，
//     表现为「站在坑里也能踩到地」，而且**不报错**（PlayerActor.cs:1045 原文同款提醒）；
//   · RoundToInt 会把 x = 0.6 算成第 1 格 ⇒ 格边界被挪到 0.5，与引擎既有格子口径
//     「格 (gx, gy) 覆盖 [gx, gx+1] × [gy, gy+1]」（Runtime/Core/IsoLayout.cs:13-15）不符，
//     还会让"贴边站"取决于小数位（同一格内走一步就换格）。
//   ⇒ 只接受 `Mathf.FloorToInt`：世界坐标 x ∈ [g, g+1) 属于第 g 格。
//
// ★ EdgeEpsilon = 0.0001f 的作用（**不是**含糊的"防浮点误差"）：
//   Rect 的右/上边按**开区间**理解：Rect(x, y, w, h) 覆盖 [xMin, xMax] × [yMin, yMax]。
//   若 xMax 恰好落在格边界上（xMax = 5.0），`FloorToInt(5.0) = 5` 会把"只在一条零宽边上碰到"
//   的第 5 格也算进来 ⇒ 碰撞盒凭空多一列/一行（贴右墙会隔空撞墙、多踩一格）。
//   减 epsilon 后 `FloorToInt(4.9999) = 4` ⇒ 覆盖到的格与几何重叠严格一致。
//   ⚠️ epsilon 必须**大于该坐标量级的 float ULP** 才减得动：float 在 4096 处 ULP ≈ 4.9e-4 > 1e-4，
//   所以默认值只对 |坐标| ≲ 4096 的世界可靠（1 格 = 1 单位的 2D 关卡够用：典型宽度 200~4000）。
//   世界更大时**显式传更大的 epsilon**（本类每个入口都带 epsilon 参数），否则等于没减。
//
// ★ GC：`ForEach` 是热路径入口（每帧、每个碰撞盒都要跑），所以用**回调**而不是迭代器 ——
//   迭代器每次 foreach 都要分配（状态机 + 装箱的枚举器）。但回调自己也有坑：
//   `ForEach(r, (x, y) => {...})` 的 lambda 一旦捕获局部变量，就**每次调用分配一个闭包**；
//   方法组写法 `ForEach(r, OnTile)` 在 Unity 的 C# 9 下**每次转换也分配一个委托**。
//   ⇒ 热路径请把委托**存进字段**（`_onTile ??= OnTile;`）再传进来；`Enumerate` 只给冷路径用，
//     它每次调用都会分配（注释已写在方法上）。
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using UnityEngine;

namespace CloverEngine
{
    /// <summary>
    /// 矩形 → 整数格遍历（纯函数、无状态）。
    /// <para>
    /// 契约（逐字复刻 4 个调用点，见文件头）：
    /// ① 覆盖的格 = 矩形 [xMin,xMax] × [yMin,yMax] 与格 [g, g+1) 有**非零面积**交叠的那些格；
    /// ② 迭代顺序 = y 升序外层、x 升序内层（顺序是契约的一部分：调用方在回调里 <c>break</c> 时，
    ///    换顺序会让"先撞到哪一格"变化）；
    /// ③ 空矩形 / 反向矩形（宽或高为负）⇒ **一次回调都不发**（正常情形，不打日志）；
    /// ④ 非有限坐标（NaN / ±Inf）⇒ 不回调 + 降频 Error（那是数据出错，与"空矩形"不同）。
    /// </para>
    /// <para>非线程安全约定 = 主线程使用（本身不碰任何全局态，只读计算其实可并发）。</para>
    /// </summary>
    public static class GridUtil
    {
        private const string Tag = "GridUtil";

        /// <summary>
        /// 右/上边的默认收边量（= 4 个调用点里的 <c>0.0001f</c>，逐字保留）。
        /// 语义与适用上限见文件头 ★；传 <c>0</c> = 含边界格（闭区间）。
        /// </summary>
        public const float EdgeEpsilon = 0.0001f;

        /// <summary>单次遍历的格数告警阈值：超过它几乎一定是"世界坐标当格坐标用"（见 <see cref="ForEach"/>）。</summary>
        private const long HugeTileCount = 1000000L;

        /// <summary>
        /// 把矩形折成**格范围**（闭区间，含两端）。返回 <c>false</c> = 矩形不覆盖任何格
        /// （空矩形 / 反向矩形 / 非有限坐标），此时四个出参均为 0。
        /// </summary>
        /// <param name="r">世界坐标矩形（<c>Rect.x/y</c> 为左下角，与 2D 平台一致）。</param>
        /// <param name="xMin">最左格 tx（含）。</param>
        /// <param name="xMax">最右格 tx（含）。</param>
        /// <param name="yMin">最下格 ty（含）。</param>
        /// <param name="yMax">最上格 ty（含）。</param>
        /// <param name="epsilon">
        /// 右/上边的收边量（<see cref="EdgeEpsilon"/>）：矩形**不**把"只在边界上碰到"的格算进来。
        /// 传 0 = 闭区间（含边界格）；传得比矩形宽还大 ⇒ 变成空范围（返回 false）。
        /// </param>
        public static bool TryGetTileRange(Rect r, out int xMin, out int xMax, out int yMin, out int yMax,
            float epsilon = EdgeEpsilon)
        {
            xMin = 0;
            xMax = 0;
            yMin = 0;
            yMax = 0;

            if (!IsFinite(r.xMin) || !IsFinite(r.xMax) || !IsFinite(r.yMin) || !IsFinite(r.yMax))
            {
                // 非预期分支：NaN/±Inf（未初始化坐标、除以 0 的结果）⇒ FloorToInt 结果不可预测，必须留痕
                LogThrottle.ErrorThrottled(Tag, "range.non-finite",
                    $"矩形含非有限坐标（x[{r.xMin},{r.xMax}] y[{r.yMin},{r.yMax}]）⇒ 本次不遍历任何格；" +
                    "NaN/Inf 通常来自未初始化的坐标或一次除以 0");
                return false;
            }

            xMin = Mathf.FloorToInt(r.xMin);
            xMax = Mathf.FloorToInt(r.xMax - epsilon);
            yMin = Mathf.FloorToInt(r.yMin);
            yMax = Mathf.FloorToInt(r.yMax - epsilon);

            // 空矩形（宽/高为 0 或被 epsilon 收没了）与反向矩形（宽/高为负）都落在这里：静默、不打日志。
            return xMin <= xMax && yMin <= yMax;
        }

        /// <summary>
        /// 遍历矩形覆盖到的所有格。**热路径入口：委托已缓存时不产生任何分配。**
        /// </summary>
        /// <param name="r">世界坐标矩形。</param>
        /// <param name="action">逐格回调，参数顺序 (tx, ty)（与 <c>Vector2Int</c> 的 <c>x</c> / <c>y</c> 同序）。</param>
        /// <param name="epsilon">右/上边收边量，见 <see cref="TryGetTileRange"/>。</param>
        public static void ForEach(Rect r, Action<int, int> action, float epsilon = EdgeEpsilon)
        {
            if (action == null)
            {
                // 非预期分支：漏传回调。说一次就够（每帧调一次会把它变成刷屏源）
                LogThrottle.ErrorOnce(Tag, "foreach.null-action", "ForEach 的 action 为 null ⇒ 本次不遍历");
                return;
            }

            if (!TryGetTileRange(r, out var x0, out var x1, out var y0, out var y1, epsilon)) return;

            var count = ((long)x1 - x0 + 1) * ((long)y1 - y0 + 1);
            if (count > HugeTileCount)
            {
                // 非预期分支：格数离谱 = 世界坐标被当格坐标用（或 Rect 传了世界尺寸）。
                // ⛔ 不截断（截断 = 静默丢格，碰撞会漏判）——只留痕，本轮仍全量遍历。
                LogThrottle.ErrorThrottled(Tag, "foreach.huge-rect",
                    $"矩形覆盖 {count} 格（x[{x0},{x1}] y[{y0},{y1}]），超过 {HugeTileCount}；" +
                    "通常是世界坐标被当成格坐标（或 Rect 传了世界尺寸）—— 本次仍全量遍历");
            }

            for (var y = y0; y <= y1; y++)
            for (var x = x0; x <= x1; x++)
                action(x, y);
        }

        /// <summary>
        /// 遍历矩形覆盖到的所有格（迭代器版本）。
        /// <para>
        /// ⚠️ **每次调用都会分配**：C# 迭代器状态机对象 + <c>IEnumerable&lt;Vector2Int&gt;</c> 的
        /// 装箱枚举器（<c>foreach</c> 结束后等 GC 回收）。⇒ 只用于冷路径（构建关卡、工具、
        /// EditMode 测试）；**每帧的碰撞查询请用 <see cref="ForEach"/>**。
        /// </para>
        /// </summary>
        public static IEnumerable<Vector2Int> Enumerate(Rect r, float epsilon = EdgeEpsilon)
        {
            // 注意：迭代器体是**惰性**的 ⇒ TryGetTileRange 里的降频日志在第一次 MoveNext 时才发。
            if (!TryGetTileRange(r, out var x0, out var x1, out var y0, out var y1, epsilon)) yield break;

            for (var y = y0; y <= y1; y++)
            for (var x = x0; x <= x1; x++)
                yield return new Vector2Int(x, y);
        }

        /// <summary>有限性判定（<c>float.IsFinite</c> 在 Unity 目标框架上不保证可用，自己判两条）。</summary>
        private static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
    }
}
