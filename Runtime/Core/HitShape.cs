// ─────────────────────────────────────────────────────────────────────────────
// CloverEngine · Runtime/Core/HitShape.cs
// 命中判定几何：正面扇形 + 矩形走廊 + 线段通畅 —— 纯函数静态工具，下沉到引擎。
//
// 【出处】clover-project-diablo2 的
//   client/Assets/Scripts/Module/Combat/MeleeShape.cs:48-152（整类逐行照搬，算法与边界一字未改）。
//   该文件自称「攻击判定形状的唯一口径」，⛔ 项目侧改动后仍保留那句话：本类现在是**唯一实现**，
//   diablo2 的 MeleeShape 退化为薄转发（只留题材调参常量）。
//
// 【为什么下沉（通用性判据）】
//   ① 三件都是**题材无关的纯几何**：任意 2D 格游戏都可能要「正面锥 / 矩形走廊 / 线段不穿墙」；
//   ② 只吃 float / Vector2Int，不碰 MonoBehaviour / 场景 / 地图实现（地形用**回调注入**）⇒
//      可被离线宿主 / EditMode 直接调用并断言，不必起 Play（与 GridUtil / Separation2D 同一理由）；
//   ③ 引擎全仓在本次改动前对 `InFrontCone|InMeleeRect|LineClear` **0 命中** ⇒ 属**引擎缺口**，
//      故整体下沉为引擎底座（能力层路由见 skill `patterns/engine-fix.md`：通用底座 = C 桶之外的底座类）。
//   ⛔ 本类**不预设** 60° / 1.2 格这类数值 —— 那是**题材调参**（diablo2 的 8 向朝向量化误差推导），
//      由调用方传 `cosMin` / `reach` / `halfWidth` 进来。
//
// 【用法 + 首个消费方】
//   diablo2 `Module/Combat/MeleeShape.cs`：`ToUnit` / `InFrontCone` / `InMeleeRect` / `LineClear`
//   四个入口全部转发到本类（调用点见该文件的 `MeleeShape` 类体）。
//   调用顺序固定为：① 朝向格增量由调用方用**引擎权威表** `IsoLayout.DirectionDelta(dir)` 取好 →
//   ② `ToUnit(dx, dy, out fx, out fy)`（false = 零向量 ⇒ 调用方拒绝本次攻击并留痕）→
//   ③ `InFrontCone` **且** `InMeleeRect` 同时成立才算"在攻击形状内" →
//   ④ `LineClear(walkable, from, to)` 是**独立的一关**（近战与远程都要过）。
//
// 【已知边界与精度限制】
//   · `InFrontCone` 的零偏移（与攻击者同格，dx=dy=0）**恒返回 true** —— 见下「踩过的坑」；
//   · `InMeleeRect` 的 `reach` / `halfWidth` 是**格**单位，零偏移天然满足（沿轴 0 ∈ [0, reach]、垂距 0）；
//   · `LineClear` 是 **Bresenham 格级**口径（除两端点外每格都要 walkable），**不是**原版
//     `Missiles.txt` 的 sub-tile 单位外接框求交 —— 本类服务的是"格坐标口径"的游戏；
//   · `LineClear` 的格步数超过 `maxSteps` ⇒ 返回 `false`（按"不通"处理并留痕）——
//     那是**防御异常入参**（`BadLine` 导致死循环时能退出），不是正常路径；
//   · `walkable == null`（地图未接入）⇒ **放行**（返回 true），⛔ 不把"拿不到地图"变成"打不到"，
//     由调用方留痕。
//
// 【修复或移植时踩过的坑】（照搬原文件的记录，⛔ 别再把它们改回去）
//   · **同格被角度锥拒掉**：原版近战触及是**距离 / 外接框的整数口径**，不是角度口径 ——
//     ① `<根>/原版资源/d2lod1.10txt-1.10f/data/global/excel/Weapons.txt` 第 20 列 `rangeadder`
//        （近战武器追加触及：短剑 / 手斧 = 空(=0)，战杖 `War Staff` = 1）⇒ 触及 = 1 + rangeadder **格**；
//     ② 同目录 `MonStats2.txt` 第 8 列 `MeleeRng`（骷髅 `skeleton1` = 0）⇒ 同样是**格数**。
//     两处都表明"够不够得着"是**沿距离比较**（`0 ≤ reach` 恒真，同格时外接框必然重叠）⇒
//     **同格必命中**。角度锥是**本项目新增的量化近似**，它不该在"距离 0"这个本该恒真的点上把攻击拒掉：
//     diablo2 实测 40 次真实左键**全被拒**（`report-audioverify2.md` §2.3）。
//     ⛔ 只补这一个退化点（`ZeroOffsetEpsilon`），**不把扇形放宽成圆形**。
//   · **零向量的处理**：`ToUnit` 对 (0,0) 返回 false 并清零出参（调用方拒绝攻击）——
//     若这里返回 `(0,0)` 并当成合法朝向，扇形会把「背后的目标」判成命中（点积 0 ≥ cosMin 取决于
//     cosMin 符号），这种错误**不报错、只表现为打击判定诡异**。
//   · **`(int)` 强转的坑同族**（见 GridUtil 文件头）：格坐标一律 `Mathf.FloorToInt`，负格才不会向零截断。
// ─────────────────────────────────────────────────────────────────────────────

using System;
using UnityEngine;

namespace CloverEngine
{
    /// <summary>
    /// 命中判定几何：**正面扇形 + 矩形走廊 + 线段通畅**（全部纯函数、无状态）。
    /// <para>
    /// 契约（与 diablo2 `MeleeShape` 逐字一致，⛔ 改语义 = 改玩法判定）：
    /// ① <see cref="InFrontCone"/> —— 目标偏移与朝向单位向量的夹角余弦 ≥ <c>cosMin</c>；
    ///    **零偏移（同格）恒 true**（零距离上"夹角"无定义，而原版近战触及是距离/外接框口径）；
    /// ② <see cref="InMeleeRect"/> —— 以朝向为轴的矩形：沿轴投影 ∈ [0, reach] 且 |垂距| ≤ halfWidth；
    /// ③ <see cref="LineClear"/> —— Bresenham 线上**除两端点外**每格都必须可走。
    /// ①② 同时成立才算"在攻击形状内"；③ 是否独立的一关（近战与远程都要过）。
    /// </para>
    /// <para>
    /// **确定性**：同输入 ⇒ 同输出，逐位相同；不读时钟 / 帧号 / 全局态，不调 <c>UnityEngine.Random</c>。
    /// **依赖**：除 <c>UnityEngine.Vector2Int</c> / <c>Mathf</c> 这两个数学原语外**不引用任何 Unity API**
    /// （不碰 Transform / 物理 / 场景），日志走引擎既有的 <see cref="LogThrottle"/>（非 Unity 进程里自动降级）。
    /// </para>
    /// <para>非线程安全：约定主线程使用（本身无状态，只读计算其实可并发）。</para>
    /// </summary>
    public static class HitShape
    {
        /// <summary>内部日志 tag（引擎惯例：各能力用类名自报家门）。</summary>
        private const string Tag = "HitShape";

        /// <summary>
        /// 线段遍历的格步数上限（防御：`BadLine` 参数导致死循环时能退出并返回 false）。
        /// 与 diablo2 原实现同值；正常"一屏内的两点"远小于它。
        /// </summary>
        public const int MaxLineSteps = 1024;

        /// <summary>
        /// 判定"零偏移（同格）"的距离下限：低于它就认为目标与攻击者同格。
        /// 与 diablo2 原实现同值（`1e-6f`）—— 它只吸收浮点噪声，不是"小步长也算同格"。
        /// </summary>
        private const float ZeroOffsetEpsilon = 1e-6f;

        /// <summary>
        /// 朝向的**格增量** → **单位向量**（格坐标下的向量，不是屏幕方向）。
        /// <para>
        /// ⛔ 本类**不自己写 `Dir8` 映射表**：格增量一律由调用方用**引擎权威表**
        /// <see cref="IsoLayout.DirectionDelta"/> 取好再传进来 —— 这样本类对地图 / 投影零依赖，
        /// 可被最小自检宿主单独编译驱动。
        /// </para>
        /// </summary>
        /// <param name="dx">朝向的格增量 x（来自 `IsoLayout.DirectionDelta`）。</param>
        /// <param name="dy">朝向的格增量 y。</param>
        /// <param name="fx">单位向量 x（不可解时为 0）。</param>
        /// <param name="fy">单位向量 y（不可解时为 0）。</param>
        /// <returns>false = 朝向向量不可解（0 向量；调用方据此**拒绝**本次攻击并留痕）。</returns>
        public static bool ToUnit(int dx, int dy, out float fx, out float fy)
        {
            fx = dx;
            fy = dy;
            var len = Mathf.Sqrt(fx * fx + fy * fy);
            if (len <= 0f)
            {
                fx = 0f;
                fy = 0f;
                return false;
            }

            fx /= len;
            fy /= len;
            return true;
        }

        /// <summary>
        /// **正面扇形**：目标偏移 (dx,dy) 与朝向单位向量 (fx,fy) 的夹角余弦 ≥ <paramref name="cosMin"/>。
        /// <para>
        /// ★ **零偏移（与攻击者同格，dx=dy=0）⇒ 返回 true（命中）** —— 零距离上"夹角"无定义，
        /// 而**原版的近战触及是距离 / 外接框口径**（`Weapons.txt` `rangeadder` / `MonStats2.txt`
        /// `MeleeRng`，见文件头「踩过的坑」）：`0 ≤ reach` 恒真 ⇒ 同格必命中。
        /// ⛔ 只补这一个退化点；扇形本身与"正侧方 90° 不命中"的口径一字未动。
        /// </para>
        /// </summary>
        /// <param name="fx">攻击者朝向单位向量 x。</param>
        /// <param name="fy">攻击者朝向单位向量 y。</param>
        /// <param name="dx">目标相对攻击者的格偏移 x。</param>
        /// <param name="dy">目标相对攻击者的格偏移 y。</param>
        /// <param name="cosMin">扇形半角的余弦阈值（调用方给；如半角 60° ⇒ 0.5）。</param>
        public static bool InFrontCone(float fx, float fy, float dx, float dy, float cosMin)
        {
            var len = Mathf.Sqrt(dx * dx + dy * dy);
            if (len <= ZeroOffsetEpsilon) return true;      // 同格 ⇒ 命中（原版口径：距离 0 ≤ 任何 reach）
            return (dx * fx + dy * fy) / len >= cosMin;
        }

        /// <summary>
        /// **矩形走廊**：以朝向 (fx,fy) 为轴、长 <paramref name="reach"/>、半宽 <paramref name="halfWidth"/>。
        /// <para>`along = (dx,dy)·朝向`（必须 ∈ [0, reach]）；`perp = (dx,dy)·朝向的左法线`（|perp| ≤ halfWidth）。</para>
        /// <para>零偏移天然满足（沿轴 0 ∈ [0, reach]、垂距 0 ≤ 半宽）。正后方（`along &lt; 0`）恒不命中。</para>
        /// </summary>
        /// <param name="fx">攻击者朝向单位向量 x。</param>
        /// <param name="fy">攻击者朝向单位向量 y。</param>
        /// <param name="dx">目标相对攻击者的格偏移 x。</param>
        /// <param name="dy">目标相对攻击者的格偏移 y。</param>
        /// <param name="reach">走廊长度（格，含）。</param>
        /// <param name="halfWidth">走廊半宽（格）。</param>
        public static bool InMeleeRect(float fx, float fy, float dx, float dy, float reach, float halfWidth)
        {
            var along = dx * fx + dy * fy;
            if (along < 0f || along > reach) return false;
            var perp = dx * (-fy) + dy * fx;
            return Mathf.Abs(perp) <= halfWidth;
        }

        /// <summary>
        /// **线段通畅**：从 <paramref name="from"/> 到 <paramref name="to"/> 的 Bresenham 线上，
        /// **除两端点外**的每一格都必须 <paramref name="walkable"/>。
        /// <para>
        /// 用途：攻击（近战 / 远程）不得穿过墙 / 水 / 地图外。⛔ <paramref name="walkable"/> == null
        /// （地图未接入）⇒ **放行**（返回 true）+ 降频 Warn，不把"拿不到地图"变成"打不到"。
        /// </para>
        /// <para>
        /// `<c>from == to</c>` ⇒ 无中间格 ⇒ 恒返回 true。格步数用尽（&gt; <paramref name="maxSteps"/>）
        /// ⇒ 返回 false（按"不通"处理）+ 降频 Warn —— 那是入参异常，不是正常路径。
        /// </para>
        /// </summary>
        /// <param name="walkable">该格是否可走（null = 不做地形阻挡）。</param>
        /// <param name="from">起点格（**本身不判可走**）。</param>
        /// <param name="to">终点格（**本身不判可走**）。</param>
        /// <param name="maxSteps">格步数上限（默认 <see cref="MaxLineSteps"/>）。</param>
        public static bool LineClear(Func<Vector2Int, bool> walkable, Vector2Int from, Vector2Int to,
            int maxSteps = MaxLineSteps)
        {
            if (walkable == null)
            {
                // 非预期分支的**降级**：地图未接入。放行（不把"拿不到地图"变成"打不到"）并留痕一次。
                LogThrottle.WarnOnce(Tag, "lineclear.no-walkable",
                    "LineClear: walkable 为 null（地图未接入？）⇒ 本次不做线段阻挡（放行）");
                return true;
            }

            if (maxSteps <= 0)
            {
                maxSteps = MaxLineSteps;
                LogThrottle.WarnOnce(Tag, "lineclear.bad-maxsteps",
                    $"LineClear: maxSteps <= 0（非法入参）⇒ 按默认 {MaxLineSteps} 处理");
            }

            var x = from.x;
            var y = from.y;
            var x1 = to.x;
            var y1 = to.y;
            var dx = Mathf.Abs(x1 - x);
            var dy = Mathf.Abs(y1 - y);
            var sx = x < x1 ? 1 : -1;
            var sy = y < y1 ? 1 : -1;
            var err = dx - dy;
            var guard = 0;

            while (guard++ < maxSteps)
            {
                if (x == x1 && y == y1) return true;

                var e2 = 2 * err;
                if (e2 > -dy) { err -= dy; x += sx; }
                if (e2 < dx) { err += dx; y += sy; }

                if (x == x1 && y == y1) return true;          // 到达终点：两端点不判可走
                if (!walkable(new Vector2Int(x, y))) return false;
            }

            // 非预期分支（入参异常 / 起点终点离谱地远）：按"不通"处理并留痕（⛔ 不静默）
            LogThrottle.WarnOnce(Tag, "lineclear.steps-exhausted",
                $"LineClear: {maxSteps} 步内没走到终点（from={from} to={to}）⇒ 按'线段不通'处理；" +
                "通常是 BadLine 入参导致（半径 / 坐标算错）");
            return false;                                       // 步数爆掉 = 入参异常
        }
    }
}
