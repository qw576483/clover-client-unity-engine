// ─────────────────────────────────────────────────────────────────────────────
// CloverEngine · Runtime/Core/IsoLayout.cs
// 等距（isometric）投影的正/逆变换、深度排序、屏幕取格、格间距与 8 方向 —— 通用底座。
//
//   半格尺寸 / 深度排序步长由**构造参数**传入（页面 / 项目各自给一组即可复用），引擎不预设项目常量。
//   ⛔ 层偏移（渲染层的 z 偏移）**不属本件**：它属项目语义，由调用方在返回值上叠加。
//
// 坐标口径：
//   逻辑坐标 = 格子坐标（整数格，`Vector2Int`）；渲染时才做等距投影。
//   格子 (gx, gy) 覆盖逻辑方形 [gx, gx+1] × [gy, gy+1]，**中心** = (gx+0.5, gy+0.5)
//
//   正投影（格 → 世界）：
//     x = ( gx - gy      ) * HalfW
//     y = -( gx + gy + 1 ) * HalfH
//
//   逆投影（世界 → 格）：反解上面的线性方程组，再 **FloorToInt**
//
// ⛔ 逆投影必须 `Mathf.FloorToInt`：C# 的 `(int)` 强转对**负数向零截断**，
//   会导致「图外可走」「格子错半格」—— 且不报错。
// ⛔ 深度排序必须随格子变化，用 `SortOrder(...)`：`(gx + gy) * sortOrderStep + sortOrderBase`。
// ─────────────────────────────────────────────────────────────────────────────

using UnityEngine;

namespace CloverEngine
{
    /// <summary>
    /// 等距投影正/逆变换、深度排序、屏幕取格、格间距与 8 方向。
    /// <para>投影尺度与排序参数由构造传入 ⇒ 一套实现可服务多套「半格宽高 + 排序步长」的页面/项目。
    /// 无参默认值（半格 1.0 / 0.5）属业务口径，引擎不预设。</para>
    /// <para>非线程安全：纯函数、无状态，但约定主线程使用。</para>
    /// </summary>
    public sealed class IsoLayout
    {
        /// <summary>X 轴半格宽（世界单位）。</summary>
        public float HalfW { get; }

        /// <summary>Y 轴半格高（世界单位）。</summary>
        public float HalfH { get; }

        private readonly int _sortOrderStep;
        private readonly int _sortOrderBase;

        /// <summary>
        /// 建一套等距布局参数。
        /// </summary>
        /// <param name="halfW">X 轴半格宽（世界单位）。</param>
        /// <param name="halfH">Y 轴半格高（世界单位）。</param>
        /// <param name="sortOrderStep">每格的排序步长（depth/sortingOrder 随格子变化）。</param>
        /// <param name="sortOrderBase">排序基准值（保证大于背景层）。</param>
        public IsoLayout(float halfW, float halfH, int sortOrderStep, int sortOrderBase)
        {
            HalfW = halfW;
            HalfH = halfH;
            _sortOrderStep = sortOrderStep;
            _sortOrderBase = sortOrderBase;
        }

        // ── 正投影：格 → 世界 ───────────────────────────────────────────────
        /// <summary>格子**中心**的世界坐标（z = 0，2D 平面）。</summary>
        public Vector3 GridToWorld(int gx, int gy)
        {
            var x = (gx - gy) * HalfW;
            var y = -(gx + gy + 1) * HalfH;
            return new Vector3(x, y, 0f);
        }

        /// <summary>格子左上角（格坐标原点）的世界坐标 —— 铺装瓦片时对齐用。</summary>
        public Vector3 GridOriginToWorld(int gx, int gy)
        {
            var x = (gx - gy) * HalfW;
            var y = -(gx + gy) * HalfH;
            return new Vector3(x, y, 0f);
        }

        // ── 逆投影：世界 → 格 ───────────────────────────────────────────────
        /// <summary>世界坐标 → 格子坐标（**FloorToInt**；世界 z 分量被忽略）。</summary>
        public Vector2Int WorldToGrid(Vector3 world)
        {
            var fx = (world.x / HalfW - world.y / HalfH) * 0.5f;
            var fy = (-world.y / HalfH - world.x / HalfW) * 0.5f;
            return new Vector2Int(Mathf.FloorToInt(fx), Mathf.FloorToInt(fy));
        }

        /// <summary>连续的格坐标（不取整，供插值/插值动画用）。</summary>
        public Vector2 WorldToGridContinuous(Vector3 world)
        {
            var fx = (world.x / HalfW - world.y / HalfH) * 0.5f;
            var fy = (-world.y / HalfH - world.x / HalfW) * 0.5f;
            return new Vector2(fx, fy);
        }

        // ── 屏幕 → 世界/格 ──────────────────────────────────────────────────
        /// <summary>
        /// 屏幕坐标 → **地面（z = 0）**的世界坐标。
        /// ⛔ 必须显式给「相机到地面的距离」，否则点击位置整体偏移；
        /// 正交相机下距离 = `-camera.transform.position.z`。
        /// </summary>
        /// <param name="cam">主相机（**必须正交**）；为 null 时返回零向量并限频告警。</param>
        /// <param name="screenPos">屏幕坐标（`Game.Input.MousePosition`）。</param>
        public Vector3 ScreenToWorldOnGround(Camera cam, Vector3 screenPos)
        {
            if (cam == null)
            {
                LogThrottle.WarnThrottled("Iso", "screen2world.nocam",
                    "ScreenToWorldOnGround: 相机为 null（Camera.main 没找到？），返回 (0,0,0)");
                return Vector3.zero;
            }

            var depth = -cam.transform.position.z;
            if (Mathf.Approximately(depth, 0f))
            {
                LogThrottle.WarnThrottled("Iso", "screen2world.zerodepth",
                    $"ScreenToWorldOnGround: 相机 z={cam.transform.position.z} 导致到地面距离为 0，按 10 处理");
                depth = 10f;
            }

            var p = cam.ScreenToWorldPoint(new Vector3(screenPos.x, screenPos.y, depth));
            p.z = 0f;
            return p;
        }

        /// <summary>屏幕坐标 → 格子坐标（等距逆投影，负数用 Floor）。</summary>
        public Vector2Int ScreenToGrid(Camera cam, Vector3 screenPos)
        {
            return WorldToGrid(ScreenToWorldOnGround(cam, screenPos));
        }

        // ── 深度排序 ────────────────────────────────────────────────────────
        /// <summary>
        /// 该格子的排序基准：`(gx + gy) * sortOrderStep + sortOrderBase`。
        /// 同一 `gx+gy` 的格按 gy 依次递增，保证「后方物件盖住前方角色」。
        /// </summary>
        public int SortOrder(int gx, int gy)
        {
            return (gx + gy) * _sortOrderStep + _sortOrderBase;
        }

        /// <summary>该格子 + 层偏移的排序值（层偏移由调用方给，引擎不预设层语义）。</summary>
        public int SortOrder(int gx, int gy, int layerOffset) => SortOrder(gx, gy) + layerOffset;

        // ── 距离与方向 ──────────────────────────────────────────────────────
        /// <summary>格间**八向步数**（Chebyshev 距离）—— 8 邻接寻路/射程判定用它。</summary>
        public int GridDistance(Vector2Int a, Vector2Int b)
        {
            return Mathf.Max(Mathf.Abs(a.x - b.x), Mathf.Abs(a.y - b.y));
        }

        /// <summary>格间欧氏距离（世界单位，格边长 = 1）—— 手感类距离（拾取/对话）用它。</summary>
        public float GridDistanceEuclidean(Vector2Int a, Vector2Int b)
        {
            float dx = a.x - b.x;
            float dy = a.y - b.y;
            return Mathf.Sqrt(dx * dx + dy * dy);
        }

        /// <summary>格间欧氏距离（含小数坐标，移动插值中判定用）。</summary>
        public float GridDistanceEuclidean(Vector2 a, Vector2 b)
        {
            return Vector2.Distance(a, b);
        }

        /// <summary>是否为 8 邻接（含同格）。</summary>
        public bool IsAdjacent(Vector2Int a, Vector2Int b) => GridDistance(a, b) <= 1;

        /// <summary>
        /// 格增量 → 8 方向朝向（**格空间增量 → 屏幕上的朝向**；与 <see cref="Dir8"/> 的顺时针编号一致）。
        /// <para>**权威表**（由本文件的 <see cref="GridToWorld"/> 直接反算）：</para>
        /// <code>
        ///   ( 0, +1) → SW   世界位移 (−HalfW, −HalfH) ⇒ 屏幕左下
        ///   ( 0, -1) → NE   屏幕右上
        ///   (+1,  0) → SE   屏幕右下
        ///   (-1,  0) → NW   屏幕左上
        ///   (+1, +1) → S    世界位移 (0, −2·HalfH)   ⇒ 屏幕正下
        ///   (-1, -1) → N    屏幕正上
        ///   (+1, -1) → E    屏幕正右
        ///   (-1, +1) → W    屏幕正左
        /// </code>
        /// <para>推导：`GridToWorld` 是 `x = (gx − gy)·HalfW`、`y = −(gx + gy + 1)·HalfH`
        /// ⇒ `Δworld = ( (Δgx − Δgy)·HalfW , −(Δgx + Δgy)·HalfH )`。
        /// 所以 `(0, +1)` 是**屏幕左下 = SW**，`(+1, +1)` 才是**屏幕正下 = S**。</para>
        /// <para>自洽判据：`DirectionDelta(DirectionTo(delta))` 必须与 `delta` 的**符号方向一致**
        /// —— ⛔ 整档偏转会让人物朝向"看着不对"：格增量算出的朝向比真实屏幕朝向**少一档**，
        /// 再叠加素材（`.dcc` 8 向图）就是整体错位，且**不报错、不打日志**。</para>
        /// <para>增量取符号后比较，故 (3, 7) 与 (1, 1) 结果相同。</para>
        /// </summary>
        public Dir8 DirectionTo(Vector2Int delta)
        {
            var sx = System.Math.Sign(delta.x);
            var sy = System.Math.Sign(delta.y);

            switch (sx)
            {
                case 0:                       // Δgx == 0 ⇒ 世界位移在 x 上为 −Δgy（正 Δgy 往左）
                    if (sy > 0) return Dir8.SW;
                    if (sy < 0) return Dir8.NE;
                    LogThrottle.WarnThrottled("Iso", "dir.zero", "DirectionTo 收到零增量，按 Dir8.S 处理");
                    return Dir8.S;

                case 1:                       // Δgx > 0 ⇒ 世界 x 增大（屏幕向右）
                    if (sy > 0) return Dir8.S;    // ( +1, +1 ) → 正下
                    if (sy < 0) return Dir8.E;    // ( +1, -1 ) → 正右
                    return Dir8.SE;               // ( +1,  0 ) → 右下

                default:                      // Δgx < 0 ⇒ 世界 x 减小（屏幕向左）
                    if (sy > 0) return Dir8.W;    // ( -1, +1 ) → 正左
                    if (sy < 0) return Dir8.N;    // ( -1, -1 ) → 正上
                    return Dir8.NW;               // ( -1,  0 ) → 左上
            }
        }

        /// <summary>从 <paramref name="from"/> 指向 <paramref name="to"/> 的 8 方向（同格按 S）。</summary>
        public Dir8 DirectionTo(Vector2Int from, Vector2Int to)
        {
            return DirectionTo(new Vector2Int(to.x - from.x, to.y - from.y));
        }

        /// <summary>
        /// 朝向 → 格增量（<see cref="DirectionTo(Vector2Int)"/> 的**逆**，用于「朝前移动一格 / 反向击退」）。
        /// <para>逐条是 <see cref="DirectionTo(Vector2Int)"/> 那张表的反查（**必须整表一起改**，
        /// 只改一边会让 `DirectionTo(DirectionDelta(d)) != d`，朝向自相矛盾且不报错）：</para>
        /// <code>
        ///   S  → ( +1, +1)    SW → ( 0, +1)    W  → (−1, +1)    NW → (−1,  0)
        ///   N  → (−1, −1)    NE → ( 0, −1)    E  → ( +1, −1)    SE → ( +1,  0)
        /// </code>
        /// <para>自洽判据（最小复现）：8 个格增量逐个断言
        /// `DirectionDelta(DirectionTo(delta))` 与 `delta` 的**符号方向一致**。</para>
        /// </summary>
        public Vector2Int DirectionDelta(Dir8 dir)
        {
            switch (dir)
            {
                case Dir8.S: return new Vector2Int(1, 1);
                case Dir8.SW: return new Vector2Int(0, 1);
                case Dir8.W: return new Vector2Int(-1, 1);
                case Dir8.NW: return new Vector2Int(-1, 0);
                case Dir8.N: return new Vector2Int(-1, -1);
                case Dir8.NE: return new Vector2Int(0, -1);
                case Dir8.E: return new Vector2Int(1, -1);
                case Dir8.SE: return new Vector2Int(1, 0);
                default:
                    LogThrottle.WarnThrottled("Iso", "dirdelta.bad",
                        $"DirectionDelta 收到未登记的 Dir8={dir}，返回 (0,0)");
                    return Vector2Int.zero;
            }
        }
    }
}
