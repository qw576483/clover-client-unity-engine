// ─────────────────────────────────────────────────────────────────────────────
// CloverEngine · Runtime/Core/PathFollower.cs
// 沿 A* 结果**逐格推进**的路径跟随器 + 朝向 —— 格子 / 等距类玩法的通用底座。
//
// 速度 / 重规划间隔由**构造参数**传入（引擎不预设任何业务数值，与 `IsoLayout` 的半格尺寸同口径）；
//   方向判定走**注入的** `IsoLayout.DirectionTo`。
//
// 适用面：`Advance` / `StepToward` 是**题材无关**的（"沿逐格路径走 + 按格变化更新朝向"），
//   任何寻路都需要一段同语义的跟随器；数值与手感全部由调用方给。
//
// 与 `AStar` 的分工：`AStar.Find` 产出**逐格路径**（`List<Vector2Int>`，含起点与终点），
//   本件只**沿路走** —— ⛔ 本件不寻路、不查可走性、不认识任何地图类型（可走性判定在
//   `AStar` 的 `walkable` 回调里，或调用方自行判定）。
//
// 为什么注入 `IsoLayout` 而不是自己写一张方向表：`IsoLayout.DirectionTo` 是**朝向的唯一真相**
//   —— 复制第二份必然漂移，且漂移**不报错**（只表现为"朝向看着别扭"）。
//   ⛔ `IsoLayout` 只在这里提供方向判定：本件**不碰** `HalfW` / `HalfH`
//   （半格尺寸是项目语义，谁渲染谁决定；本件只处理**格坐标**与格中心）。
//
// 用法：
//   <code>
//   var f = new PathFollower(layout, minMoveSpeed, repathIntervalSeconds);
//   f.SnapTo(grid);                       // 落格（进图 / 刷怪 / 复活）
//   f.SetPath(AStar.Find(...), goal);     // 喂 `AStar` 的结果
//   f.Advance(speedTilesPerSecond, dt);   // 每 tick 一次；随后把 `f.Dir` / `f.Pos` 同步给视图
//   f.StepToward(targetPos, speed, dt);   // 逃跑 / 紧急脱身：**直线**走一步，不做寻路
//   </code>
//
// 边界：
//   · 坐标口径 = **格中心制**：格 (gx,gy) 的中心是 (gx+0.5, gy+0.5)，`Pos` 允许落在两格之间。
//   · `Grid` 必须 `FloorToInt`（⛔ 不用 `(int)` 强转：负数向零截断会让格错半格且不报错）。
//   · `SetPath(null)` / 路径 ≤ 1 点 ⇒ 判为"无路径"（与 `AStar.Find` 起点==终点返回单元素列表对齐）。
//   · `Advance` / `StepToward` 的入参速度低于 `MinMoveSpeed` 时**按 `MinMoveSpeed` 处理**
//     （配表 0 / 负值不至于原地卡死；⛔ 这不会把配表值抬上去 —— 调用方传合法值时一行不生效）。
//   · 朝向只在**格**发生变化时更新（零增量不再调 `IsoLayout.DirectionTo`，否则它会打限频日志）。
//   · `iso` 传 null 时：Error 留痕（非预期分支），朝向更新被跳过，`Pos` / 路径推进仍可用。
//   · ⛔ **不依赖 Unity 运行时对象**（不继承 `MonoBehaviour`、不持有 `GameObject`），
//     可在离线自检宿主里逐帧复算（与 `AStar` / `IsoLayout` 同一约定）。
//   · 非线程安全：主线程使用（同上）。
// ─────────────────────────────────────────────────────────────────────────────

using System.Collections.Generic;
using UnityEngine;

namespace CloverEngine
{
    /// <summary>
    /// 路径跟随器：持有**连续格坐标** / 朝向 / 逐格路径，按速度推进（纯逻辑，无 MonoBehaviour）。
    /// <para>分工见文件头：只沿 `AStar` 的结果走，⛔ 自己不寻路、不判可走性。</para>
    /// </summary>
    public sealed class PathFollower
    {
        /// <summary>方向判定的唯一真相（只用于 `DirectionTo`；本件不读它的半格尺寸）。</summary>
        private readonly IsoLayout _iso;

        /// <summary>
        /// 速度下限（格 / 秒）：<see cref="Advance"/> / <see cref="StepToward"/> 的入参低于它时按它处理。
        /// </summary>
        public float MinMoveSpeed { get; }

        /// <summary>
        /// 每次 <see cref="SetPath"/> 后写入 <see cref="RepathTimer"/> 的冷却秒数。
        /// </summary>
        public float RepathIntervalSeconds { get; }

        // ── 位置 ─────────────────────────────────────────────────────────────
        /// <summary>连续格坐标（格中心制，见文件头）。</summary>
        public Vector2 Pos;

        /// <summary>当前朝向（8 方向；按**格**变化更新）。</summary>
        public Dir8 Dir = Dir8.S;

        // ── 路径 ─────────────────────────────────────────────────────────────
        /// <summary>剩余路径（格）；null = 无路径。第 0 个元素是**起点格**，不参与"到达"。</summary>
        public List<Vector2Int> Path;

        /// <summary>下一个要到达的路点下标（<see cref="SetPath"/> 后从 1 起）。</summary>
        public int PathIndex;

        /// <summary>上次寻路的目标格（供调用方做"目标格变化 &gt; N 才重算"的节流）。</summary>
        public Vector2Int PathTarget;

        /// <summary><see cref="PathTarget"/> 是否有效。</summary>
        public bool HasPathTarget;

        /// <summary>距离下次允许重寻路的秒数（由 <see cref="RepathIntervalSeconds"/> 起算，调用方自行递减）。</summary>
        public float RepathTimer;

        /// <summary>
        /// 建一个跟随器。
        /// </summary>
        /// <param name="iso">方向判定来源（引擎等距布局）。传 null ⇒ Error 留痕，朝向不更新。</param>
        /// <param name="minMoveSpeed">速度下限（格 / 秒），见 <see cref="MinMoveSpeed"/>；≤ 0 ⇒ 按 0 处理。</param>
        /// <param name="repathIntervalSeconds">重寻路冷却（秒），见 <see cref="RepathIntervalSeconds"/>；≤ 0 ⇒ 按 0 处理。</param>
        public PathFollower(IsoLayout iso, float minMoveSpeed, float repathIntervalSeconds)
        {
            if (iso == null)
            {
                // 非预期分支：调用方漏传。不抛（热路径上抛出去会打断整只怪的 tick），
                // 但必须留痕 —— 否则表现为"怪会走、朝向永远不动"，无从定位。
                Game.Logger?.Error("PathFollower",
                    "iso 为 null：朝向更新将被跳过（Pos / 路径推进仍可用）。请传一个 IsoLayout");
            }
            _iso = iso;
            MinMoveSpeed = minMoveSpeed > 0f ? minMoveSpeed : 0f;
            RepathIntervalSeconds = repathIntervalSeconds > 0f ? repathIntervalSeconds : 0f;
        }

        /// <summary>当前格（连续坐标向下取整；**必须 Floor**，见文件头边界）。</summary>
        public Vector2Int Grid
        {
            get { return new Vector2Int(Mathf.FloorToInt(Pos.x), Mathf.FloorToInt(Pos.y)); }
        }

        /// <summary>格中心（连续坐标）。</summary>
        public static Vector2 Center(Vector2Int g)
        {
            return new Vector2(g.x + 0.5f, g.y + 0.5f);
        }

        /// <summary>当前路径是否还剩余路点。</summary>
        public bool HasRemainingPath
        {
            get { return Path != null && PathIndex < Path.Count; }
        }

        /// <summary>落到某格中心（进图 / 刷怪 / 复活用），并清空路径与路径目标。</summary>
        public void SnapTo(Vector2Int g)
        {
            Pos = Center(g);
            Path = null;
            PathIndex = 0;
            HasPathTarget = false;
        }

        /// <summary>设为路径（跳过起点格；`AStar.Find` 的返回值含起点）。</summary>
        public void SetPath(List<Vector2Int> path, Vector2Int target)
        {
            PathTarget = target;
            HasPathTarget = true;
            RepathTimer = RepathIntervalSeconds;

            if (path == null || path.Count <= 1)
            {
                Path = null;
                PathIndex = 0;
                return;
            }

            Path = path;
            PathIndex = 1;      // 第 0 个是当前格，不用"到达"
        }

        /// <summary>丢弃当前路径（**不动** `PathTarget` / `HasPathTarget`）。</summary>
        public void ClearPath()
        {
            Path = null;
            PathIndex = 0;
        }

        /// <summary>
        /// 沿路径推进 <paramref name="tilesPerSecond"/> × <paramref name="dt"/> 格。
        /// </summary>
        /// <param name="tilesPerSecond">速度（格 / 秒）；低于 <see cref="MinMoveSpeed"/> 时按它处理。</param>
        /// <param name="dt">时间增量（秒）。</param>
        /// <returns>true = 路径已走完（或本来就没有路径）。</returns>
        public bool Advance(float tilesPerSecond, float dt)
        {
            if (!HasRemainingPath) return true;

            var speed = tilesPerSecond < MinMoveSpeed ? MinMoveSpeed : tilesPerSecond;
            var step = speed * dt;
            if (step <= 0f) return false;

            var fromGrid = Grid;

            while (step > 0f && PathIndex < Path.Count)
            {
                var wp = Center(Path[PathIndex]);
                var d = wp - Pos;
                var len = d.magnitude;

                if (len <= 1e-4f)
                {
                    PathIndex++;
                    continue;
                }

                if (len <= step)
                {
                    Pos = wp;
                    step -= len;
                    PathIndex++;
                }
                else
                {
                    Pos = Pos + d * (step / len);
                    step = 0f;
                }
            }

            UpdateDir(fromGrid);

            if (PathIndex >= Path.Count)
            {
                Path = null;
                PathIndex = 0;
                return true;
            }
            return false;
        }

        /// <summary>
        /// 朝某点**直线**走一步（逃跑 / 紧急脱身用；不做寻路）。
        /// </summary>
        /// <param name="target">目标点（连续格坐标）。</param>
        /// <param name="tilesPerSecond">速度（格 / 秒）；低于 <see cref="MinMoveSpeed"/> 时按它处理。</param>
        /// <param name="dt">时间增量（秒）。</param>
        /// <returns>true = 已到达（距离 &lt; 0.05 格）。</returns>
        public bool StepToward(Vector2 target, float tilesPerSecond, float dt)
        {
            var speed = tilesPerSecond < MinMoveSpeed ? MinMoveSpeed : tilesPerSecond;
            var step = speed * dt;
            var d = target - Pos;
            var len = d.magnitude;
            if (len <= 0.05f) return true;

            var fromGrid = Grid;
            Pos = len <= step ? target : Pos + d * (step / len);
            UpdateDir(fromGrid);
            return false;
        }

        /// <summary>
        /// 按"格"变化更新朝向（**零增量不调 `IsoLayout.DirectionTo`**：它会打限频日志，
        /// 而"这一步没跨格"是**正常**情形 —— 每帧都报会把日志刷成噪声）。
        /// </summary>
        private void UpdateDir(Vector2Int fromGrid)
        {
            var to = Grid;
            if (to.x == fromGrid.x && to.y == fromGrid.y) return;
            if (_iso == null) return;      // 构造期已 Error 留痕，不重复刷
            Dir = _iso.DirectionTo(fromGrid, to);
        }
    }
}
