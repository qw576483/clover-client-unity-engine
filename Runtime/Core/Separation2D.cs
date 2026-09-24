// ─────────────────────────────────────────────────────────────────────────────
// CloverEngine · Runtime/Core/Separation2D.cs
// 角色间水平推开（防"两个角色站进同一格"）—— 纯函数静态工具，下沉到引擎。
//
// 出处：clover-project-cs16 的 client/Assets/Scripts/Module/Map/CsActorSeparation.cs
//   （该文件自己写明：本工程本地碰撞只走 2D 位图 + 竖直射线，**没有任何"另一个角色挡不挡"的
//   判定**，角色预制体上只有服务命中检测用的胶囊、没有 Rigidbody/CharacterController
//   ⇒ 物理引擎根本不参与角色位移解算 ⇒ 两个角色可以站在同一处）。
//   引擎全仓在本次改动前对 `Separation|Overlap\(Actor|PushApart` **0 命中** ⇒ 属引擎缺口，
//   故整体下沉为引擎底座；cs16 的那份实现可退化为对本类的调用。
//
// 与原版（GoldSrc）口径的关系：原版每个玩家实体有一个水平包围盒（`origin ± 16×16 units`
//   = 半宽 0.4064 m，见 HLSDK pm_shared.c 的 player_mins/player_maxs 与 SV_Move 的实体对实体裁剪）
//   ⇒ 两个玩家不能占同一块水平空间，走到一起时是"互相挤住"。本类只复刻这条**几何约束**：
//   两个圆的圆心距 ≥ 两半径之和。
//
// ★ 本类**不是**物理引擎，**不做**也**不许**被拿去当：
//   · 不做路径规划 / 导航（那是 AStar.cs 的事）；
//   · 不做碰撞检测（墙体 / 地面 / 视线由位图与射线各管一层，见项目侧 CsMap.ResolveMove）；
//   · 不模拟速度、质量、摩擦、冲量（没有时间积分，输入输出都是"位置"）；
//   · 不处理竖直分层（不在同一层的角色不该互相推，由调用方先按高度筛完再传进来）；
//   它只回答一件事：**给定一组水平圆，把它们各自挪一点，使两两不再重叠**。
//
// ★ 为什么是"纯数学 + 只用 Vector2"：这段判定必须能被**离线宿主 / EditMode 测试**
//   直接调用并断言（"两个圆同坐标 ⇒ 被推开到不重合"），不该为了跑一次断言就起 Play。
//   本文件除 `UnityEngine.Vector2` 外**不引用任何 Unity API**（不碰 Random / Time / Transform /
//   物理）；日志走引擎既有的 `LogThrottle`（它自身在非 Unity 进程里会自动降级到进程时钟，
//   不抛异常）⇒ 可被直链进裸 .NET 程序做跨端校验（与 MapFormat.cs / GridUtil.cs 同一个理由）。
//
// ★ 确定性（**契约，不是"大概一致"**）：同输入 ⇒ 同输出，逐位相同。具体地：
//   · ⛔ 不调用 `UnityEngine.Random`（也没有种子）；⛔ 不读时钟 / 帧号 / 全局状态；
//   · 遍历顺序 = **数组下标升序**（调用方给定的顺序就是输入的一部分，不是"无序容器"）；
//   · "完全重合"没有几何方向可言，此时用**下标**定一个确定方向（黄金角 × (下标+1)），
//     见 `DeterministicDirection` 的注释（⛔ 不用随机、⛔ 不用当前速度）。
//
// ★ 边界行为（都必须**明确**且**不抛**，逐一有断言覆盖）：
//   · `count <= 0` / 空数组 / `null` 数组 ⇒ 成功返回、什么都不写；
//   · `count == 1` ⇒ 成功返回、位置原样搬进输出（单个圆谈不上"互相重叠"）；
//   · 半径全 0（或负半径 ⇒ 按 0）⇒ 最小间距 0 ⇒ 判定为不重叠 ⇒ 位置**原样**返回；
//   · 完全重叠（同坐标，d ≈ 0）⇒ 按确定性方向推开（不会除零、不会 NaN）；
//   · 非有限坐标（NaN / ±Inf）⇒ 该圆**不动**且不参与推开（不污染别人）⇒ 不抛；
//   · 迭代上限内推不开（几人挤成一堆 / 被墙夹住）⇒ 返回 `false`（**不抛**），调用方按"挤住"处理。
//
// ★ 性能口径：`n` = 同屏参与推开的角色数（个位数 ~ 几十）。
//   复杂度 O(n² × MaxIterations)，**热路径上零分配**（结果写进调用方给的缓冲）。
//   ⛔ 不要每帧对上千个单位调用；那种规模该走空间切分，不属本类职责。
// ─────────────────────────────────────────────────────────────────────────────

using System;
using UnityEngine;

namespace CloverEngine
{
    /// <summary>
    /// 角色间**水平**推开（防重叠）的纯函数工具：输入一组「位置 + 半径」，输出推开后的位置。
    /// <para>
    /// **不是物理引擎**（见文件头 ★）：不做路径规划、不做碰撞检测、不做时间积分、不处理竖直分层。
    /// 它只保证一条几何约束 —— 任意两个圆的**圆心距 ≥ 两半径之和**（外加 <see cref="Skin"/> 缝隙）。
    /// </para>
    /// <para>
    /// **确定性契约**：同输入 ⇒ 逐位相同的输出；不依赖 <c>UnityEngine.Random</c> / 时钟 / 帧号，
    /// 遍历顺序就是调用方给的数组下标顺序。
    /// </para>
    /// <para>
    /// **两个入口**（按调用方一次推进几个角色来选）：
    /// ① <see cref="TryResolve"/> —— 一次解开**一整组**（所有人一起挪），用于回合开始 / 传送落点 /
    ///    一次性归一化；② <see cref="TryResolveOne"/> —— 只推**一个**申请位置，其他人视作障碍
    ///    （= cs16 的 <c>CsMatch.StepActorPhysics</c> 形态：每帧只挪当前这个角色）。
    /// 推开结果**不许**直接采用：调用方要再喂回自己的墙体判定钳一次（⛔ 别把人推进墙里）。
    /// </para>
    /// </summary>
    public static class Separation2D
    {
        /// <summary>内部日志 tag（引擎惯例：各能力用类名自报家门，见 Runtime/Core/*.cs）。</summary>
        private const string Tag = "Separation2D";

        /// <summary>
        /// 参与推开的一个**水平圆**：<c>Position</c> 的两分量是水平面的两个轴
        /// （2D 工程 = x/y；3D 工程通常取 x/z，竖直分量由调用方先筛掉）。
        /// </summary>
        /// <remarks>
        /// 与 cs16 的 <c>CsActorSeparation.ActorCircle</c> 的**唯一差异**：没有 <c>Id</c> 字段。
        /// 那边用 <c>Id</c> 算"完全重合"时的散开方向 —— 前提是 <c>Id</c> 是唯一 actor id；
        /// 引擎版**不能**假设调用方一定填了唯一 id（业务常整组填 0 或同阵营同值），
        /// 若两个同 id 的圆取到同一方向，推完仍然重合 ⇒ 引擎版改用**数组下标**做种子
        /// （天然两两不同），既保持确定性又不依赖调用方是否填了 id。
        /// </remarks>
        public struct Circle
        {
            /// <summary>圆心在水平面上的位置。</summary>
            public Vector2 Position;

            /// <summary>水平半径（米 / 世界单位）：两圆心最小间距 = 两者半径之和。</summary>
            public float Radius;

            /// <summary>构造一个参与推开的圆。</summary>
            /// <param name="position">圆心位置（水平面）。</param>
            /// <param name="radius">水平半径；&lt; 0 按 0 处理（见 <see cref="TryResolve"/> 的边界说明）。</param>
            public Circle(Vector2 position, float radius)
            {
                Position = position;
                Radius = radius;
            }
        }

        /// <summary>
        /// 迭代轮数上限：每个"需求位移"都是**瞬时**完成（一次推到位），所以正常 1~2 轮就收敛。
        /// 给 8 轮是为了让"人群中心 / 半包围"这类情形也能收敛；到上限仍有重叠 ⇒ 返回 <c>false</c>
        /// （调用方按"挤住"处理），⛔ 不是抛异常、⛔ 也不是无限循环。
        /// </summary>
        public const int MaxIterations = 8;

        /// <summary>
        /// 推开后额外留的缝隙（米）：只推到"刚好相切"会让两个圆在浮点误差上反复判重叠
        /// ⇒ 每帧各推一丝 ⇒ 位置抖动。留 1 mm 余量（与 cs16 侧同值）。
        /// </summary>
        public const float Skin = 0.001f;

        /// <summary>
        /// 判定"完全重合"的距离下限：圆心距小于它就没有可信的几何方向，
        /// 改用 <see cref="DeterministicDirection"/> 给的确定方向（同时避免 <c>dx / d</c> 除零）。
        /// </summary>
        private const float MinDirectionDistance = 1e-5f;

        /// <summary>
        /// 黄金角（弧度，≈ 137.5°）：用下标 × 它定"完全重合"时的散开方向，
        /// 相邻下标的方向差**不是** 0、π 的整数倍 ⇒ 一组同坐标的圆会被散到不同方向。
        /// </summary>
        private const double GoldenAngle = 2.399963229728653;

        /// <summary>
        /// 把 <paramref name="circles"/> 的前 <paramref name="count"/> 个圆各自推开，使两两不重叠，
        /// 结果**写进** <paramref name="result"/>（前 <paramref name="count"/> 个元素）。
        /// <para>
        /// **不改输入**（<paramref name="circles"/> 只读）⇒ 同一输入可反复调用得到同一结果（确定性契约）。
        /// 输出缓冲由调用方提供 ⇒ 热路径零分配。
        /// </para>
        /// <para>
        /// <b>返回值</b>：<c>true</c> = 已推出（或本来就无重叠 / 空集 / 单元素）；
        /// <c>false</c> = 迭代上限内仍有重叠（挤成一堆）**或**缓冲长度不够（调用方 bug）。
        /// 无论哪种情况**都不抛异常**，失败原因都会留日志（<c>LogThrottle.*Once</c>，不刷屏）。
        /// </para>
        /// <para>
        /// <b>边界</b>：<c>circles == null || result == null || count &lt;= 0</c> ⇒ 直接返回 <c>true</c>、
        /// 一个字节都不写；<c>count == 1</c> ⇒ 位置原样搬过去；半径 ≤ 0（含负数、NaN、±Inf）
        /// ⇒ 该圆按 0 处理（自身**不产生**最小间距，不主动推别人；但它仍会被半径更大的圆推开 ——
        /// "点落在圆里"当然要推出去，否则那个点永远埋在别人身体里）；坐标非有限 ⇒ 该圆不动也不推别人。
        /// </para>
        /// </summary>
        /// <param name="circles">输入圆数组（只读；半径与坐标为水平面口径）。</param>
        /// <param name="count">参与推开的元素个数（&lt;= 数组长度；超长按调用方 bug 处理）。</param>
        /// <param name="result">输出缓冲（长度须 ≥ <paramref name="count"/>）。</param>
        public static bool TryResolve(Circle[] circles, int count, Vector2[] result)
        {
            if (circles == null || result == null || count <= 0) return true;

            if (count > circles.Length || count > result.Length)
            {
                // 非预期分支（调用方 bug）：必须留痕，且只说一次 —— 越界访问才是真正的坑。
                LogThrottle.ErrorOnce(Tag, "resolve-buffer-too-small",
                    $"TryResolve: count={count} 超过输入/输出缓冲长度（circles={circles.Length}, " +
                    $"result={result.Length}）⇒ 本次不做推开。调用方应传入 count 以内的长度");
                return false;
            }

            // 先原样拷贝：即使解析失败，输出也是"合法的输入位置"，不会留半成品。
            for (var i = 0; i < count; i++) result[i] = circles[i].Position;

            if (count == 1) return true; // 单个圆谈不上"互相重叠"

            for (var iter = 0; iter < MaxIterations; iter++)
            {
                var overlapped = false;
                for (var i = 0; i < count; i++)
                {
                    var ri = NormalizedRadius(circles[i].Radius);
                    if (ri <= 0f) continue;             // 零/负半径：不参与推开（最小间距 0 ⇒ 永不重叠）
                    if (!IsFinite(result[i])) continue; // 坐标非有限：原样留着，也不推别人

                    // 只遍历 j > i？不行 —— j 也必须遍历全部：result[j] 可能已被本轮前面的 i 改过，
                    // 要按**最新**位置判（Gauss-Seidel）。用 i != j 覆盖所有有序对即可（对称推，不会重复推）。
                    for (var j = 0; j < count; j++)
                    {
                        if (j == i) continue;

                        var rj = NormalizedRadius(circles[j].Radius);
                        if (rj <= 0f) continue;
                        if (!IsFinite(result[j])) continue;

                        var minSep = ri + rj;                  // 两圆心最小间距 = 两半径之和
                        var dx = result[i].x - result[j].x;
                        var dy = result[i].y - result[j].y;
                        var d2 = dx * dx + dy * dy;
                        if (d2 >= minSep * minSep) continue;   // 不重叠（含 minSep == 0 的全部情形）

                        overlapped = true;
                        var d = (float)Math.Sqrt(d2);
                        if (d < MinDirectionDistance)
                        {
                            // 完全重合：没有"推开方向"可言 ⇒ 用下标的确定性方向。
                            DeterministicDirection(i, out dx, out dy);
                        }
                        else
                        {
                            dx /= d;
                            dy /= d;
                        }

                        // 两人**各退一半**（质量相等）：只推一方会让"被推的人"在下一帧反过来推你 ⇒ 拉锯抖动。
                        var half = (minSep - d + Skin) * 0.5f;
                        result[i] = new Vector2(result[i].x + dx * half, result[i].y + dy * half);
                        result[j] = new Vector2(result[j].x - dx * half, result[j].y - dy * half);
                    }
                }

                if (!overlapped) return true;
            }

            // 非预期分支（挤成一堆 / 被墙夹住）：留一条日志就够，⛔ 不要每帧刷屏。
            LogThrottle.WarnOnce(Tag, "resolve-not-converged",
                $"TryResolve: {MaxIterations} 轮内仍有圆重叠（count={count}）⇒ 返回 false，" +
                "调用方应按\"挤住\"处理（原地不动 / 交给寻路绕开），而不是把结果当已解开");
            return false;
        }

        /// <summary>
        /// 只推**一个**申请位置：把它挪到与 <paramref name="others"/> 里每个圆都不重叠
        /// （<paramref name="others"/> 视作**不动的障碍**，不参与被推）。
        /// <para>
        /// 这是每帧角色推进的形态（= cs16 的 <c>CsMatch.StepActorPhysics</c>：一次只挪当前这个角色）。
        /// 与 <see cref="TryResolve"/> 的差异：那里所有人一起挪（各退一半），这里只挪申请方（退全部）。
        /// </para>
        /// <para>
        /// <b>返回值 / 边界</b>：语义与 <see cref="TryResolve"/> 一致 —— <c>true</c> = 已推出
        /// （含 <paramref name="radius"/> ≤ 0、<paramref name="others"/> 为空、申请位置非有限等退化情形）；
        /// <c>false</c> = 迭代上限内仍有重叠。任何情形**都不抛异常**，失败留降频日志。
        /// </para>
        /// </summary>
        /// <param name="position">申请位置（水平面）。</param>
        /// <param name="radius">申请方的水平半径；≤ 0 ⇒ 直接原样返回 <c>true</c>。</param>
        /// <param name="others">障碍圆数组（只读；其半径 ≤ 0 的项被跳过）。</param>
        /// <param name="count">障碍圆的个数（超过数组长度按调用方 bug 处理）。</param>
        /// <param name="result">推开后的位置（未推开时等于 <paramref name="position"/>）。</param>
        public static bool TryResolveOne(Vector2 position, float radius, Circle[] others, int count,
                                         out Vector2 result)
        {
            result = position;

            var r = NormalizedRadius(radius);
            if (r <= 0f) return true;              // 零半径：最小间距 0 ⇒ 永不重叠
            if (!IsFinite(position)) return true;  // 非有限申请位置：原样返回，不猜
            if (others == null || count <= 0) return true;

            if (count > others.Length)
            {
                LogThrottle.ErrorOnce(Tag, "resolve-one-count-out-of-range",
                    $"TryResolveOne: count={count} 超过 others 长度 {others.Length} ⇒ 只处理前 " +
                    $"{others.Length} 个。调用方应传入数组长度以内的 count");
                count = others.Length;
            }

            for (var iter = 0; iter < MaxIterations; iter++)
            {
                var overlapped = false;
                for (var i = 0; i < count; i++)
                {
                    var ri = NormalizedRadius(others[i].Radius);
                    if (ri <= 0f) continue;
                    var p = others[i].Position;
                    if (!IsFinite(p)) continue;

                    var minSep = r + ri;
                    var dx = result.x - p.x;
                    var dy = result.y - p.y;
                    var d2 = dx * dx + dy * dy;
                    if (d2 >= minSep * minSep) continue;

                    overlapped = true;
                    var d = (float)Math.Sqrt(d2);
                    if (d < MinDirectionDistance)
                    {
                        // 与障碍完全同坐标：按下标（而非随机/速度）取确定方向，⛔ 保证可复现。
                        DeterministicDirection(i, out dx, out dy);
                    }
                    else
                    {
                        dx /= d;
                        dy /= d;
                    }

                    var need = minSep - d + Skin; // 申请方退全部（障碍不动）
                    result = new Vector2(result.x + dx * need, result.y + dy * need);
                }

                if (!overlapped) return true;
            }

            LogThrottle.WarnOnce(Tag, "resolve-one-not-converged",
                $"TryResolveOne: {MaxIterations} 轮内仍被 {count} 个障碍之一压住 ⇒ 返回 false，" +
                "调用方应把该角色当\"挤住\"处理（本次不位移）");
            return false;
        }

        // ── 内部 ─────────────────────────────────────────────────────────────

        /// <summary>
        /// "完全重合"时用的**确定性**方向：黄金角 × (下标 + 1)。
        /// <para>
        /// 为什么是下标：几何上无可信方向（速度在静止时是零向量、随机数每帧都变 ⇒ 抖动），
        /// 而下标是输入的固有属性 ⇒ 同输入同方向。为什么是黄金角：相邻下标方向差
        /// <c>2.3999…</c> 弧度，既不为 0 也不是 π 的整数倍 ⇒ 一组同坐标的圆会被散到**不同**方向，
        /// 不像"下标 × 90°"那样四个一轮就被卡住。
        /// </para>
        /// </summary>
        private static void DeterministicDirection(int index, out float dx, out float dy)
        {
            var angle = (index + 1) * GoldenAngle;
            dx = (float)Math.Cos(angle);
            dy = (float)Math.Sin(angle);
        }

        /// <summary>
        /// 半径归一：负数 / NaN / ±Inf 一律按 0 处理（= 自身不产生最小间距），并**降频留痕** ——
        /// 静默吃掉非法半径会让"怎么推都不动"变成无从定位的现象。
        /// </summary>
        private static float NormalizedRadius(float radius)
        {
            if (radius < 0f || float.IsNaN(radius) || float.IsInfinity(radius))
            {
                LogThrottle.WarnOnce(Tag, "invalid-radius",
                    $"非法半径 {radius} 已按 0 处理（该圆不参与推开）；" +
                    "半径应是 >= 0 的有限数（水平半径，单位与世界坐标一致）");
                return 0f;
            }

            return radius;
        }

        /// <summary>有限性判定（⛔ 不用 <c>float.IsFinite</c>：Unity 的 API 兼容级别未必有）。</summary>
        private static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

        /// <summary>两个分量都有限才算有限（任一分量非有限 ⇒ 距离计算会变成 NaN ⇒ 整点不可用）。</summary>
        private static bool IsFinite(Vector2 v) => IsFinite(v.x) && IsFinite(v.y);
    }
}
