// ─────────────────────────────────────────────────────────────────────────────
// CloverEngine · Runtime/Core/ProjectileRuntime.cs
// 投射物**飞行积分 + 逐格扫掠 + 最近命中** —— 纯计算运行时。
//
// 【本件包含两组能力】
//   · 飞行积分 / 命中半径 / 格坐标 / 连续格→世界坐标 / 轨迹文本
//     （`Step` / `Overlaps` / `Grid` / `WorldOf` / `TrailText`）；
//   · 逐格扫掠 + 最近命中 + 单帧推进次序（`Advance` / `TrySweepTerrain` / `FindNearestHit`）。
//
// 【前提与不变量】
//   · 「沿方向按速度积分 + 一帧内逐格采样防穿墙 + 取最近的重叠目标」是**题材无关的投射物底座**：
//     任何有"子弹 / 箭矢 / 火弹"的 2D 格游戏都适用；
//   · 只吃 `Vector2 / Vector2Int / float` + **回调**，⛔ 不引用任何项目类型、⛔ 不引用
//     `UnityEngine.GameObject`（表现留在项目侧）⇒ 可被离线宿主单编单跑、逐例断言，不必起 Play
//     （与 `GridUtil` / `Separation2D` 同一理由）。
//
// 【注入点（三个）—— "这一格能不能走" / "打到谁了" / "消散·命中要干什么"】
//   ① <see cref="ProjectileBody"/>：把"投射物的状态"抽成结构体（位置 / 方向 / 速度 / 剩余射程 /
//      命中半径 / 累计飞行 / 是否存活），⛔ 不含 View / 伤害 / 技能 id 等题材字段 —— 那些留在项目侧；
//   ② `ProjectileBlockProbe`：该格是否阻挡投射物（地形裁决表在项目侧，**唯一出处**，⛔ 引擎不抄一份）；
//   ③ `ProjectileTargetProbe`：第 index 个候选目标（返回 false = 该项不参与，如已死 / 无效）；
//      "消散 / 命中要干什么"由 <see cref="Advance"/> 返回的 <see cref="ProjectileTick"/> **分类**给调用方
//      —— 因为那三件事（销毁表现节点 / 播命中音效 / 打日志 / 走伤害管线）**全是项目侧副作用**，
//      引擎件只负责"算出该发生哪一种"。
//
// 【不并入本件的三样】（⛔ 别搬进来）
//   · 视图节点 / 渲染器字段：表现层，属项目；
//   · 地形→可否穿越的逐类裁决表：**题材语义**（水格挡不挡弹道取决于调用方的地形模型），
//     引擎若抄一份就会与地形定义**双源**，新增地形值时静默失配；
//   · 命中 / 撞地形的善后（伤害管线 / 音效 / 日志 / 销毁节点）：**副作用**。
//
// 【用法】
//   调用次序固定为：`Advance` → 按 `SteppedPos` 补轨迹点 → 视图 `Sync` →
//   若 `Clipped` 再 `Sync` 一次（截停后的表现点）→ 按 `Outcome` 分派。
//
// 【已知边界与精度限制】
//   · `Advance` 的**前置条件 = `body.Alive == true`**（项目侧在循环开头就把死掉的摘掉）；
//     `Alive == false` 进来 ⇒ 直接返回 `Expired`，不做任何推进（防御，不静默）；
//   · **一帧跨多格**必须靠逐格采样（`sampleStep < 1`）：采样只在"格号变化"时判一次 ⇒
//     `sampleStep` 越小越不会整格跳过，但步数线性上升（默认 <see cref="DefaultSampleStep"/> = 0.25 格）；
//   · `blocked == null`（拿不到地图 / 地图未生成）⇒ **本帧不做地形阻挡**（返回 false）+ 降频 Warn；
//   · **必须先地形、后命中**：一帧跨多格时端点会越过墙，若先判怪物就成"隔墙射杀"（见 `Advance` 注释）；
//   · `TrySweepTerrain` 是**格级采样**，不是逐格的精确 DDA（同输入同输出）。
//   · `StopAt` 只在"进入一个**新**格且该格可穿"时更新 ⇒ 同一格内不重复更新；
//     ⛔ 若改成"每次采样都更新"，表现点会贴到格内更深处、`traveled` 回退量随之变化（数值差异，不易察觉）。
//   · 撞地形时 `traveled` 必须**回退**到截停位置（`Traveled -= Distance(StopAt, 飞过头的位置)`），
//     否则日志里的"飞了 N 格"会算上被截掉的那一段。
//   · `FindNearestHit` 的相等距离（`d >= bestDist`）**不替换** ⇒ 保留"先遇到的那只"，
//     命中对象与候选列表的遍历顺序绑定（列表顺序变了命中对象就变）。
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using UnityEngine;

namespace CloverEngine
{
    /// <summary>
    /// 单次 <see cref="ProjectileBody.Step"/> 的结果（"这一步之后还活着吗"的显式化）。
    /// </summary>
    public enum ProjectileStepResult
    {
        /// <summary>未推进（`dt &lt;= 0` 或 `speed &lt;= 0`）—— 调用方**不要**补轨迹点。</summary>
        NoAdvance = 0,

        /// <summary>推进了，且仍在射程内（存活）。</summary>
        Advanced = 1,

        /// <summary>推进到**射程耗尽**（本次仍把位置推到终点，之后 `Alive = false`）。</summary>
        RangeExhausted = 2
    }

    /// <summary>一帧推进的结果分类（"消散 / 命中要干什么"由调用方按此分派）。</summary>
    public enum ProjectileOutcome
    {
        /// <summary>仍在飞（什么都没发生）。</summary>
        Flying = 0,

        /// <summary>射程耗尽**自然消散**（没撞地形、没命中目标）。</summary>
        Expired = 1,

        /// <summary>命中目标（<see cref="ProjectileTick.MonsterId"/> 有效）。</summary>
        HitMonster = 2,

        /// <summary>撞上不可穿越地形而消散（<see cref="ProjectileTick.BlockedCell"/> 有效）。</summary>
        HitTerrain = 3
    }

    /// <summary>
    /// 一帧推进的结果。<see cref="Advance"/> 已经把"截停 / 回退射程 / 置死"**写进** body，
    /// 调用方只需按 <see cref="Outcome"/> 分派副作用。
    /// </summary>
    public struct ProjectileTick
    {
        /// <summary>本帧属于哪一类（Flying / Expired / HitMonster / HitTerrain）。</summary>
        public ProjectileOutcome Outcome;

        /// <summary>本帧的 <see cref="ProjectileBody.Step"/> 真的推进了（调用方据此补一个轨迹点）。</summary>
        public bool Stepped;

        /// <summary><see cref="ProjectileBody.Step"/> 之后的连续格坐标（**截停之前**的位置）。
        /// ⛔ 轨迹点必须用这个而不是 `body.Pos`：撞地形时 `body.Pos` 已被截到 <see cref="StopAt"/>。</summary>
        public Vector2 SteppedPos;

        /// <summary>本帧被地形截停（`body.Pos` / `body.Traveled` 已被改写、`Alive = false`）。</summary>
        public bool Clipped;

        /// <summary>第一个阻挡格（仅 <see cref="Clipped"/> 时有意义）。</summary>
        public Vector2Int BlockedCell;

        /// <summary>投射物应停的位置 = 进入阻挡格之前最后一个可穿越的采样点（仅 <see cref="Clipped"/> 时有意义）。</summary>
        public Vector2 StopAt;

        /// <summary>命中的目标 id（仅 <see cref="ProjectileOutcome.HitMonster"/> 时有意义；否则 -1）。</summary>
        public int MonsterId;
    }

    /// <summary>
    /// **地形阻挡探针**：该格是否阻挡投射物。
    /// <para>⛔ 逐类裁决表留在调用方（题材语义，如"水格挡不挡弹道"）—— 引擎只问这一格能不能走。</para>
    /// <para>`null` = 本帧不做地形阻挡（地图未接入 / 未生成），见 <see cref="ProjectileRuntime.Advance"/>。</para>
    /// </summary>
    public delegate bool ProjectileBlockProbe(Vector2Int cell);

    /// <summary>
    /// **目标探针**：第 <c>index</c> 个候选目标。
    /// <para>返回 false = 该项**不参与**本次命中判定（null / 已死 / 越界）。返回 true 时必须填好
    /// <c>id</c> 与 <c>center</c>（**连续格坐标**的中心，与 <see cref="ProjectileBody.Pos"/> 同一口径）。</para>
    /// <para>⛔ 探针**不做**距离判定（那是本类的事）；它只回答"这个候选是什么、还算不算数"。</para>
    /// </summary>
    public delegate bool ProjectileTargetProbe(int index, out int id, out Vector2 center);

    /// <summary>
    /// 投射物飞行状态（**纯数据 + 飞行积分**）：位置 / 方向 / 速度 / 剩余射程 / 命中半径 / 累计飞行 / 存活。
    /// <para>
    /// ⛔ 刻意**不含**：表现节点（View / Renderer）、伤害、技能 id、地形类型、命中记录 ——
    /// 那些是项目语义，留在项目侧（见文件头「不并入本件的三样」）。
    /// </para>
    /// <para>
    /// **确定性**：同输入 ⇒ 同输出，逐位相同；不读时钟 / 帧号 / 全局态。非线程安全：主线程使用。
    /// </para>
    /// </summary>
    public struct ProjectileBody
    {
        /// <summary>连续格坐标（格中心制）。</summary>
        public Vector2 Pos;

        /// <summary>单位方向（格空间）。</summary>
        public Vector2 Dir;

        /// <summary>速度（格/秒）。</summary>
        public float Speed;

        /// <summary>剩余射程（格）。</summary>
        public float RangeLeft;

        /// <summary>命中半径（格）。</summary>
        public float HitRadius;

        /// <summary>累计飞行距离（格）。</summary>
        public float Traveled;

        /// <summary>是否仍在飞行。</summary>
        public bool Alive;

        /// <summary>
        /// 按 <paramref name="dt"/> 推进一步（**不判命中** —— 命中判定需要目标列表，由
        /// <see cref="ProjectileRuntime.Advance"/> 做）。
        /// <para>
        /// 语义：`step = speed * dt`；`step &lt;= 0` ⇒ 原样返回
        /// <see cref="ProjectileStepResult.NoAdvance"/>（位置 / 射程 / 存活都不变）；
        /// `step ≥ RangeLeft` ⇒ 截到剩余射程、`RangeLeft = 0`、`Alive = false`、仍把位置推到终点；
        /// 否则扣减剩余射程。两条推进路径**都**累加 <see cref="Traveled"/>。
        /// </para>
        /// </summary>
        /// <param name="dt">本帧时间（秒）。</param>
        public ProjectileStepResult Step(float dt)
        {
            var step = Speed * dt;
            if (step <= 0f) return ProjectileStepResult.NoAdvance;

            var exhausted = step >= RangeLeft;
            if (exhausted)
            {
                step = RangeLeft;
                RangeLeft = 0f;
            }
            else
            {
                RangeLeft -= step;
            }

            Pos += Dir * step;
            Traveled += step;

            if (exhausted)
            {
                Alive = false;
                return ProjectileStepResult.RangeExhausted;
            }

            return ProjectileStepResult.Advanced;
        }

        /// <summary>该点是否在命中范围内（与目标身体半径一起放宽）。转发 <see cref="ProjectileRuntime.Overlaps"/>。</summary>
        public bool Overlaps(Vector2 targetCenter)
        {
            return ProjectileRuntime.Overlaps(Pos, targetCenter, HitRadius);
        }

        /// <summary>格坐标（**Floor**；用于排序与日志）。转发 <see cref="ProjectileRuntime.GridOf"/>。</summary>
        public Vector2Int Grid
        {
            get { return ProjectileRuntime.GridOf(Pos); }
        }
    }

    /// <summary>
    /// 投射物**飞行 / 扫掠 / 命中**的纯计算入口（全部纯函数、无状态，见文件头）。
    /// </summary>
    public static class ProjectileRuntime
    {
        /// <summary>内部日志 tag（引擎惯例）。</summary>
        private const string Tag = "ProjectileRuntime";

        /// <summary>
        /// 逐格采样的默认步长（格）。**必须 &lt; 1**：采样只在"格号变化"时判一次，步长越大越可能整格跳过
        /// （高速投射物一帧跨多格）⇒ 取 1/4 格，保证任意线段都不会跳过一整格。
        /// </summary>
        public const float DefaultSampleStep = 0.25f;

        /// <summary>
        /// 逐格步进扫 `from → to` 这段位移：返回**第一个阻挡格**，并用 <paramref name="stopAt"/> 给出
        /// 投射物应停的位置（= 进入该格之前最后一个可穿越的采样点；线段起点本身就在阻挡格里时 = <paramref name="from"/>）。
        /// <para>为什么扫整段而不是只判端点：一帧跨多格（高速投射物 / 卡帧后的 dt）时只判端点会穿墙。</para>
        /// <para><paramref name="blocked"/> == null（地图未接入 / 未生成）⇒ 本帧**不做**地形阻挡并留一次 Warn。</para>
        /// </summary>
        /// <param name="from">本帧起点（连续格坐标）。</param>
        /// <param name="to">本帧终点（连续格坐标，= 步进之后的位置）。</param>
        /// <param name="blocked">该格是否阻挡投射物（null = 不做地形阻挡）。</param>
        /// <param name="blockedCell">第一个阻挡格（返回 true 时有效）。</param>
        /// <param name="stopAt">应停位置（返回 false 时 = <paramref name="from"/>）。</param>
        /// <param name="sampleStep">采样步长（格，&lt; 1；`<= 0` 按 <see cref="DefaultSampleStep"/>）。</param>
        /// <returns>true = 撞到地形。</returns>
        public static bool TrySweepTerrain(Vector2 from, Vector2 to, ProjectileBlockProbe blocked,
            out Vector2Int blockedCell, out Vector2 stopAt, float sampleStep = DefaultSampleStep)
        {
            blockedCell = default(Vector2Int);
            stopAt = from;

            var d = to - from;
            var dist = d.magnitude;
            if (dist <= 0f) return false;                       // 没位移：无中间格可判

            if (blocked == null)
            {
                // 非预期分支（地图未接入 / 未生成）：本帧不做地形阻挡并留痕一次（不静默）
                LogThrottle.WarnOnce(Tag, "sweep.no-probe",
                    "TrySweepTerrain: 地形探针为 null（地图未接入 / 未生成？）⇒ 本帧不做地形阻挡（投射物会穿墙；离线自检宿主属正常）");
                return false;
            }

            if (sampleStep <= 0f || float.IsNaN(sampleStep) || float.IsInfinity(sampleStep))
            {
                sampleStep = DefaultSampleStep;
                LogThrottle.WarnOnce(Tag, "sweep.bad-sample-step",
                    $"TrySweepTerrain: sampleStep 非法 ⇒ 按默认 {DefaultSampleStep} 格处理（步长必须 > 0 且 < 1）");
            }

            var steps = Mathf.CeilToInt(dist / sampleStep);
            var lastCell = new Vector2Int(int.MinValue, int.MinValue);
            for (var i = 1; i <= steps; i++)
            {
                var pt = from + d * ((float)i / steps);
                var g = new Vector2Int(Mathf.FloorToInt(pt.x), Mathf.FloorToInt(pt.y));
                if (g == lastCell) continue;                    // 同一格只判一次
                lastCell = g;

                if (blocked(g))
                {
                    blockedCell = g;
                    return true;
                }

                stopAt = pt;                                    // 该格可穿 ⇒ 记录"最后安全位置"
            }

            return false;
        }

        /// <summary>
        /// 找被投射物擦到的目标（取**最近的一只**；距离相等时保留**先遇到**的那只）。
        /// <para>⛔ 只做"距离 ≤ 命中半径"的几何判定 —— "哪些候选算数"由 <paramref name="target"/> 探针回答。</para>
        /// </summary>
        /// <param name="count">候选个数（&lt;= 0 或探针为 null ⇒ 返回 -1）。</param>
        /// <param name="target">目标探针（见 <see cref="ProjectileTargetProbe"/>）。</param>
        /// <param name="pos">投射物当前位置（连续格坐标）。</param>
        /// <param name="hitRadius">命中半径（格）。</param>
        /// <returns>命中的目标 id；-1 = 没命中任何目标。</returns>
        public static int FindNearestHit(int count, ProjectileTargetProbe target, Vector2 pos, float hitRadius)
        {
            if (target == null || count <= 0) return -1;

            var bestId = -1;
            var bestDist = float.MaxValue;
            for (var i = 0; i < count; i++)
            {
                if (!target(i, out var id, out var center)) continue;   // 该候选不参与（已死 / 无效 / 越界）

                var d = Vector2.Distance(pos, center);
                if (d > hitRadius) continue;
                if (d >= bestDist) continue;                            // 相等不替换 = 保留先遇到的那只

                bestDist = d;
                bestId = id;
            }

            return bestId;
        }

        /// <summary>
        /// 推进一枚投射物**一帧**：飞行积分 → **地形逐格扫掠** → 命中判定 → 分类。
        /// <para>
        /// ★ 次序**不可调换**：一帧跨多格（高速 / 卡帧后的 dt）时端点会越过墙，
        /// 若先判怪物就会判成"命中墙后那只怪"= 隔墙射杀。故**先地形、后命中**；且撞地形截停后
        /// **还要再判一次命中** —— 贴墙站着的怪要能被打到（否则这次判定会被墙"吃掉"）。
        /// </para>
        /// <para>
        /// 副作用**全部**写在 <paramref name="body"/> 上：截停（<c>Pos</c>）、回退射程（<c>Traveled</c>）、
        /// 置死（<c>Alive</c>）。⛔ 表现 / 音效 / 伤害 / 日志一律由调用方按返回的
        /// <see cref="ProjectileTick.Outcome"/> 分派。
        /// </para>
        /// </summary>
        /// <param name="body">投射物状态（按 ref 推进；前置条件 `Alive == true`）。</param>
        /// <param name="dt">本帧时间（秒）。</param>
        /// <param name="blocked">地形阻挡探针（null = 本帧不做地形阻挡）。</param>
        /// <param name="target">目标探针（null = 不做命中判定）。</param>
        /// <param name="targetCount">候选目标个数。</param>
        public static ProjectileTick Advance(ref ProjectileBody body, float dt, ProjectileBlockProbe blocked,
            ProjectileTargetProbe target, int targetCount)
        {
            var t = new ProjectileTick
            {
                Outcome = ProjectileOutcome.Flying,
                MonsterId = -1,
                SteppedPos = body.Pos,
                StopAt = body.Pos
            };

            if (!body.Alive)
            {
                // 非预期分支：调用方应先把死掉的摘掉（project 侧 TickProjectiles 的循环开头做这件事）
                LogThrottle.WarnOnce(Tag, "advance.dead-body",
                    "Advance: 传入的投射物已经不存活 ⇒ 本次不推进（调用方应先摘掉它）");
                t.Outcome = ProjectileOutcome.Expired;
                return t;
            }

            var from = body.Pos;
            var stepResult = body.Step(dt);
            t.Stepped = stepResult != ProjectileStepResult.NoAdvance;
            t.SteppedPos = body.Pos;

            // ── 地形碰撞（★ 必须排在命中判定之前，见 summary）────────────────────
            if (TrySweepTerrain(from, body.Pos, blocked, out var blockedCell, out var stopAt))
            {
                var flown = body.Pos;
                body.Pos = stopAt;                                   // 截停在进入阻挡格之前（表现点）
                body.Traveled = Mathf.Max(0f, body.Traveled - Vector2.Distance(stopAt, flown));
                body.Alive = false;

                t.Clipped = true;
                t.BlockedCell = blockedCell;
                t.StopAt = stopAt;

                // 贴墙站着的怪先算命中（否则这次判定会被墙"吃掉"）
                var wallHitId = FindNearestHit(targetCount, target, body.Pos, body.HitRadius);
                if (wallHitId >= 0)
                {
                    t.Outcome = ProjectileOutcome.HitMonster;
                    t.MonsterId = wallHitId;
                    return t;
                }

                t.Outcome = ProjectileOutcome.HitTerrain;
                return t;
            }

            var hitId = FindNearestHit(targetCount, target, body.Pos, body.HitRadius);
            if (hitId >= 0)
            {
                body.Alive = false;                                  // 命中即结束飞行
                t.Outcome = ProjectileOutcome.HitMonster;
                t.MonsterId = hitId;
                return t;
            }

            if (!body.Alive)
            {
                t.Outcome = ProjectileOutcome.Expired;               // 射程耗尽自然消散
                return t;
            }

            return t;
        }

        /// <summary>该点是否在命中范围内（与目标身体半径一起放宽）：`Distance(pos, center) &lt;= hitRadius`。</summary>
        /// <param name="pos">投射物位置（连续格坐标）。</param>
        /// <param name="targetCenter">目标中心（连续格坐标）。</param>
        /// <param name="hitRadius">命中半径（格）。</param>
        public static bool Overlaps(Vector2 pos, Vector2 targetCenter, float hitRadius)
        {
            return Vector2.Distance(pos, targetCenter) <= hitRadius;
        }

        /// <summary>连续格坐标 → 格坐标（**`Mathf.FloorToInt`**）。
        /// ⛔ 不用 `(int)` 强转：它对负数向零截断 ⇒ 负格（图左上方向）会错半格，且**不报错**。</summary>
        /// <param name="pos">连续格坐标（格中心制）。</param>
        public static Vector2Int GridOf(Vector2 pos)
        {
            return new Vector2Int(Mathf.FloorToInt(pos.x), Mathf.FloorToInt(pos.y));
        }

        /// <summary>
        /// 连续格坐标 → 世界坐标。与 <see cref="IsoLayout.GridToWorld"/> 的正投影**同一口径**
        /// （`x=(px-py)*halfW`、`y=-(px+py+1)*halfH`），只是允许小数输入：
        /// 当 `p == (gx+0.5, gy+0.5)` 时结果与 `GridToWorld(gx, gy)` 完全相同。
        /// </summary>
        /// <param name="p">连续格坐标。</param>
        /// <param name="halfW">X 轴半格宽（世界单位，由调用方给：`IsoLayout.HalfW`）。</param>
        /// <param name="halfH">Y 轴半格高（世界单位，由调用方给：`IsoLayout.HalfH`）。</param>
        public static Vector3 ContinuousGridToWorld(Vector2 p, float halfW, float halfH)
        {
            return new Vector3((p.x - p.y) * halfW, -(p.x + p.y + 1f) * halfH, 0f);
        }

        /// <summary>
        /// 轨迹的**可读形式**（自证打印）：`(x1.00,y1.00) → (x2.00,y2.00) …`（最多取前 <paramref name="max"/> 个点；
        /// 超长时补 `…（共 N 点，末点 (x,y)）`）。空轨迹返回 `(空)`。
        /// <para>格式固定（判据脚本会比对这两段文本）。</para>
        /// </summary>
        /// <param name="trail">轨迹点（连续格坐标）。</param>
        /// <param name="max">最多打印前几个点（&lt;= 0 按 8 处理）。</param>
        public static string FormatTrail(IList<Vector2> trail, int max = 8)
        {
            if (trail == null || trail.Count == 0) return "(空)";
            if (max <= 0) max = 8;

            var n = trail.Count;
            var parts = new List<string>();
            for (var i = 0; i < n && i < max; i++) parts.Add($"({trail[i].x:0.00},{trail[i].y:0.00})");
            var head = string.Join(" → ", parts);
            return n > max
                ? head + $" → …（共 {n} 点，末点 ({trail[n - 1].x:0.00},{trail[n - 1].y:0.00})）"
                : head;
        }
    }
}
