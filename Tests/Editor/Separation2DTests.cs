using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CloverEngine.Tests
{
    /// <summary>
    /// <see cref="Separation2D"/>（角色间水平推开，P0 缺口②）的编辑期单元测试。
    /// <para>
    /// <b>判据</b>：每条边界（空集 / 单元素 / 半径全 0 / 完全重合 / 已分离 / 非有限坐标 / 负半径 /
    /// 缓冲不足 / 迭代不收敛）都要有**明确且不抛**的行为，且"同输入 ⇒ 逐位同输出"（确定性）要断言。
    /// </para>
    /// <para>
    /// 用例里的 <see cref="Radius"/> 只是**测试取样值**（0.36 = 角色半径的数量级取样），
    /// ⛔ 不是引擎常量 —— 引擎不规定角色半径。
    /// </para>
    /// </summary>
    public class Separation2DTests
    {
        /// <summary>测试取样半径。仅本文件使用，不构成引擎约定。</summary>
        private const float Radius = 0.36f;

        /// <summary>
        /// 部分用例**故意**走告警/错误分支（缓冲不足 / 半径非法 / 不收敛），按引擎规则这些必须打日志，
        /// 而 Unity Test Runner 默认把日志判为失败 ⇒ 显式放行日志（只放行日志，不放宽任何断言）。
        /// </summary>
        private bool _prevIgnoreFailingMessages;

        [SetUp]
        public void SetUp()
        {
            _prevIgnoreFailingMessages = LogAssert.ignoreFailingMessages;
            LogAssert.ignoreFailingMessages = true;
        }

        [TearDown]
        public void TearDown() => LogAssert.ignoreFailingMessages = _prevIgnoreFailingMessages;

        private static float Dist(Vector2 a, Vector2 b)
        {
            var dx = a.x - b.x;
            var dy = a.y - b.y;
            return (float)System.Math.Sqrt(dx * dx + dy * dy);
        }

        private static Separation2D.Circle C(float x, float y, float r) =>
            new Separation2D.Circle(new Vector2(x, y), r);

        // ── 边界：空 / 单元素 / 缓冲不足 ────────────────────────────────────────

        /// <summary><c>count &lt;= 0</c> 与 <c>null</c> 数组 ⇒ 返回 true 且**一个字节都不写**。</summary>
        [Test]
        public void TryResolve_EmptyInput_ReturnsTrueAndWritesNothing()
        {
            var result = new[] { new Vector2(9f, 9f) };
            var one = new[] { C(1f, 1f, Radius) };

            Assert.IsTrue(Separation2D.TryResolve(null, 0, result), "null 输入应直接成功返回");
            Assert.IsTrue(Separation2D.TryResolve(one, 0, result), "count=0 应直接成功返回");
            Assert.IsTrue(Separation2D.TryResolve(one, -3, result), "负 count 应直接成功返回（不抛）");
            Assert.IsTrue(Separation2D.TryResolve(one, 1, null), "null 输出缓冲应直接成功返回");
            Assert.IsTrue(Separation2D.TryResolve(one, 0, null), "两者都 null 也不许抛");

            Assert.AreEqual(9f, result[0].x, 0f, "退化输入不许写输出缓冲");
            Assert.AreEqual(9f, result[0].y, 0f, "退化输入不许写输出缓冲");
        }

        /// <summary>单元素：谈不上"互相重叠" ⇒ 位置原样搬进输出。</summary>
        [Test]
        public void TryResolve_SingleCircle_CopiesPositionVerbatim()
        {
            var circles = new[] { C(3f, -4f, Radius) };
            var result = new Vector2[1];

            Assert.IsTrue(Separation2D.TryResolve(circles, 1, result));
            Assert.AreEqual(3f, result[0].x, 0f);
            Assert.AreEqual(-4f, result[0].y, 0f);
        }

        /// <summary>输出缓冲长度不足（调用方 bug）：返回 false、留日志、**不半写**、不抛。</summary>
        [Test]
        public void TryResolve_ResultBufferTooSmall_ReturnsFalseWithoutPartialWrite()
        {
            var circles = new[] { C(0f, 0f, Radius), C(0.1f, 0f, Radius) };
            var result = new Vector2[1];

            Assert.IsFalse(Separation2D.TryResolve(circles, 2, result),
                "缓冲装不下 count 个结果 ⇒ 必须返回 false（调用方 bug），不许静默截断");
            Assert.AreEqual(0f, result[0].x, 0f, "失败时不许写了一半");
            Assert.AreEqual(0f, result[0].y, 0f, "失败时不许写了一半");
        }

        // ── 边界：半径全 0 / 负半径 ───────────────────────────────────────────

        /// <summary>半径全 0 ⇒ 最小间距 0 ⇒ 永不重叠 ⇒ 位置**逐位原样**（含同坐标的情形）。</summary>
        [Test]
        public void TryResolve_AllRadiiZero_LeavesPositionsExact()
        {
            var circles = new[] { C(1f, 1f, 0f), C(1f, 1f, 0f), C(-2f, 0.5f, 0f) };
            var result = new Vector2[3];

            Assert.IsTrue(Separation2D.TryResolve(circles, 3, result));
            Assert.AreEqual(1f, result[0].x, 0f);
            Assert.AreEqual(1f, result[0].y, 0f);
            Assert.AreEqual(1f, result[1].x, 0f);
            Assert.AreEqual(1f, result[1].y, 0f);
            Assert.AreEqual(-2f, result[2].x, 0f);
            Assert.AreEqual(0.5f, result[2].y, 0f);
        }

        /// <summary>负半径与 0 半径**等价**（不因非法值改变别人）；单独一个负半径圆也不动。</summary>
        [Test]
        public void TryResolve_NegativeRadius_BehavesExactlyLikeZero()
        {
            var withNegative = new[] { C(1f, 1f, -5f), C(1.2f, 1f, Radius) };
            var withZero = new[] { C(1f, 1f, 0f), C(1.2f, 1f, Radius) };
            var rNeg = new Vector2[2];
            var rZero = new Vector2[2];

            Assert.DoesNotThrow(() => Separation2D.TryResolve(withNegative, 2, rNeg));
            Assert.AreEqual(Separation2D.TryResolve(withZero, 2, rZero),
                            Separation2D.TryResolve(withNegative, 2, rNeg),
                            "负半径必须与 0 半径走同一条路径");

            for (var i = 0; i < 2; i++)
            {
                Assert.AreEqual(rZero[i].x, rNeg[i].x, 0f, "负半径 ≈ 0 半径：结果必须逐位相同");
                Assert.AreEqual(rZero[i].y, rNeg[i].y, 0f, "负半径 ≈ 0 半径：结果必须逐位相同");
            }

            var alone = new[] { C(7f, -3f, -1f) };
            var rAlone = new Vector2[1];
            Assert.IsTrue(Separation2D.TryResolve(alone, 1, rAlone));
            Assert.AreEqual(7f, rAlone[0].x, 0f);
            Assert.AreEqual(-3f, rAlone[0].y, 0f);
        }

        // ── 正常分离 ─────────────────────────────────────────────────────────

        /// <summary>已分离 / 恰好相切（间距 == 半径和）⇒ 位置**逐位不动**（没有"顺手微调"）。</summary>
        [Test]
        public void TryResolve_NotOverlapping_LeavesPositionsUntouched()
        {
            var far = new[] { C(0f, 0f, 0.2f), C(5f, 0f, 0.2f) };
            var rFar = new Vector2[2];
            Assert.IsTrue(Separation2D.TryResolve(far, 2, rFar));
            Assert.AreEqual(0f, rFar[0].x, 0f);
            Assert.AreEqual(5f, rFar[1].x, 0f);

            var tangent = new[] { C(0f, 0f, 0.2f), C(0.4f, 0f, 0.2f) }; // 间距 0.4 == 0.2+0.2
            var rTangent = new Vector2[2];
            Assert.IsTrue(Separation2D.TryResolve(tangent, 2, rTangent));
            Assert.AreEqual(0f, rTangent[0].x, 0f, "恰好相切 = 不重叠 ⇒ 不许推");
            Assert.AreEqual(0.4f, rTangent[1].x, 0f, "恰好相切 = 不重叠 ⇒ 不许推");
        }

        /// <summary>部分重叠的一对：推到圆心距 ≥ 半径和，且两人**各退一半**（中点不动）。</summary>
        [Test]
        public void TryResolve_OverlappingPair_SeparatesByHalvesAroundSameMidpoint()
        {
            var circles = new[] { C(0f, 0f, 0.5f), C(0.4f, 0f, 0.5f) };
            var result = new Vector2[2];

            Assert.IsTrue(Separation2D.TryResolve(circles, 2, result));

            var d = Dist(result[0], result[1]);
            Assert.GreaterOrEqual(d, 1f, "推开后圆心距必须 ≥ 两半径之和（0.5 + 0.5）");

            Assert.Less(result[0].x, 0.4f, "左边那个应被推向 -x");
            Assert.Greater(result[1].x, 0f, "右边那个应被推向 +x");

            var mid = (result[0].x + result[1].x) * 0.5f;
            Assert.AreEqual(0.2f, mid, 1e-4f, "对称推挤 ⇒ 中点应保持不动（0.2）");
            Assert.AreEqual(0f, result[0].y, 1e-6f, "只沿两圆心连线推 ⇒ y 分量不该变");
            Assert.AreEqual(0f, result[1].y, 1e-6f, "只沿两圆心连线推 ⇒ y 分量不该变");
        }

        /// <summary>输入数组不被本方法修改（纯函数 ⇒ 可反复调用得到同一结果）。</summary>
        [Test]
        public void TryResolve_DoesNotMutateInput()
        {
            var circles = new[] { C(0f, 0f, 0.5f), C(0.4f, 0f, 0.5f) };
            var result = new Vector2[2];

            Separation2D.TryResolve(circles, 2, result);

            Assert.AreEqual(0f, circles[0].Position.x, 0f, "输入圆 0 的位置不许被改写");
            Assert.AreEqual(0.4f, circles[1].Position.x, 0f, "输入圆 1 的位置不许被改写");
            Assert.AreEqual(0.5f, circles[0].Radius, 0f, "输入圆的半径不许被改写");
        }

        // ── 完全重合（退化）──────────────────────────────────────────────────

        /// <summary>同坐标一对：必须被推开、方向确定（两次调用逐位相同），且不抛。</summary>
        [Test]
        public void TryResolve_IdenticalPositions_UsesDeterministicDirection()
        {
            var circles = new[] { C(2f, 2f, Radius), C(2f, 2f, Radius) };
            var first = new Vector2[2];
            var second = new Vector2[2];

            Assert.DoesNotThrow(() => Separation2D.TryResolve(circles, 2, first));
            Assert.IsTrue(Separation2D.TryResolve(circles, 2, first), "同坐标一对应能被推开（1 轮内）");
            Assert.IsTrue(Separation2D.TryResolve(circles, 2, second), "同坐标一对应能被推开（1 轮内）");

            Assert.AreEqual(first[0].x, second[0].x, 0f, "确定性：同输入 ⇒ 逐位相同");
            Assert.AreEqual(first[0].y, second[0].y, 0f, "确定性：同输入 ⇒ 逐位相同");
            Assert.AreEqual(first[1].x, second[1].x, 0f, "确定性：同输入 ⇒ 逐位相同");
            Assert.AreEqual(first[1].y, second[1].y, 0f, "确定性：同输入 ⇒ 逐位相同");

            Assert.GreaterOrEqual(Dist(first[0], first[1]), Radius * 2f, "推开后必须不再重合");
            Assert.IsFalse(float.IsNaN(first[0].x) || float.IsNaN(first[1].x),
                "完全重合不许产生 NaN（方向退化时必须换确定性方向，不能除 0）");
        }

        /// <summary>一堆同坐标：不抛、结果有限；若报告已解开，则必须真的两两不重叠。</summary>
        [Test]
        public void TryResolve_Pile_DoesNotThrow_AndIsHonestAboutFailure()
        {
            const int n = 5;
            var circles = new Separation2D.Circle[n];
            for (var i = 0; i < n; i++) circles[i] = C(0f, 0f, 0.3f);

            var result = new Vector2[n];
            var resolved = false;

            Assert.DoesNotThrow(() => resolved = Separation2D.TryResolve(circles, n, result));

            for (var i = 0; i < n; i++)
            {
                Assert.IsFalse(float.IsNaN(result[i].x) || float.IsNaN(result[i].y),
                    $"第 {i} 个结果含 NaN（退化输入不许污染输出）");
            }

            if (resolved)
            {
                for (var i = 0; i < n; i++)
                {
                    for (var j = i + 1; j < n; j++)
                    {
                        Assert.GreaterOrEqual(Dist(result[i], result[j]), 0.6f - 1e-4f,
                            $"报告「已解开」时，第 {i}/{j} 个仍重叠 ⇒ 返回值不诚实");
                    }
                }
            }
        }

        // ── 非有限坐标 ───────────────────────────────────────────────────────

        /// <summary>含 NaN 坐标的圆：原样留着（不猜位置）、不推别人、也不抛。</summary>
        [Test]
        public void TryResolve_NonFinitePosition_IsLeftAloneAndDoesNotCorruptOthers()
        {
            var circles = new[]
            {
                C(float.NaN, 0f, 0.5f),
                C(0f, 0f, 0.5f),
                C(0.2f, 0f, 0.5f)
            };
            var result = new Vector2[3];

            Assert.DoesNotThrow(() => Separation2D.TryResolve(circles, 3, result));
            Assert.IsTrue(float.IsNaN(result[0].x), "非有限坐标应原样留在输出里（不许被改写、也不许猜）");

            // 其余两个必须被解开（NaN 那个不参与配对）
            Assert.GreaterOrEqual(Dist(result[1], result[2]), 1f, "合法的两个仍须被推开");
        }

        // ── 单动者入口 ───────────────────────────────────────────────────────

        /// <summary>把申请位置推到"远离障碍"、距离 = 半径和 + Skin；其余分量不动。</summary>
        [Test]
        public void TryResolveOne_PushesMoverAwayToSumOfRadii()
        {
            var obstacles = new[] { C(0f, 0f, 0.5f) };

            var ok = Separation2D.TryResolveOne(new Vector2(0.2f, 0f), 0.5f, obstacles, 1, out var resolved);

            Assert.IsTrue(ok);
            Assert.GreaterOrEqual(Dist(resolved, new Vector2(0f, 0f)), 1f, "推开后圆心距 ≥ 半径和");
            Assert.Greater(resolved.x, 0.2f, "应沿「远离障碍」的方向推（障碍在原点 ⇒ 往 +x）");
            Assert.AreEqual(0f, resolved.y, 1e-6f, "只沿连线推 ⇒ y 分量不该变");
            Assert.AreEqual(0f, obstacles[0].Position.x, 0f, "障碍不许被挪动");
        }

        /// <summary>退化输入：半径 ≤ 0 / null 障碍 / count ≤ 0 ⇒ 原样返回，不抛。</summary>
        [Test]
        public void TryResolveOne_DegenerateInputs_ReturnInputVerbatim()
        {
            var obstacles = new[] { C(0f, 0f, 0.5f) };
            var p = new Vector2(1.5f, -2.5f);

            Assert.IsTrue(Separation2D.TryResolveOne(p, 0f, obstacles, 1, out var r0));
            Assert.AreEqual(p.x, r0.x, 0f);
            Assert.AreEqual(p.y, r0.y, 0f);

            Assert.IsTrue(Separation2D.TryResolveOne(p, -1f, obstacles, 1, out var rNeg));
            Assert.AreEqual(p.x, rNeg.x, 0f);
            Assert.AreEqual(p.y, rNeg.y, 0f);

            Assert.IsTrue(Separation2D.TryResolveOne(p, 0.5f, null, 3, out var rNull));
            Assert.AreEqual(p.x, rNull.x, 0f);
            Assert.AreEqual(p.y, rNull.y, 0f);

            Assert.IsTrue(Separation2D.TryResolveOne(p, 0.5f, obstacles, 0, out var rZeroCount));
            Assert.AreEqual(p.x, rZeroCount.x, 0f);
            Assert.AreEqual(p.y, rZeroCount.y, 0f);

            Assert.IsTrue(Separation2D.TryResolveOne(new Vector2(float.NaN, 0f), 0.5f, obstacles, 1, out var rNaN));
            Assert.IsTrue(float.IsNaN(rNaN.x), "非有限申请位置应原样返回");
        }

        /// <summary>count 超过数组长度（调用方 bug）：不抛、按数组长度处理、留日志。</summary>
        [Test]
        public void TryResolveOne_CountBeyondArray_DoesNotThrow()
        {
            var obstacles = new[] { C(0f, 0f, 0.5f) };
            var p = new Vector2(0.1f, 0f);

            var ok = Separation2D.TryResolveOne(p, 0.5f, obstacles, 99, out var resolved);

            Assert.IsTrue(ok, "只处理在数组内的障碍 ⇒ 应能解开");
            Assert.GreaterOrEqual(Dist(resolved, new Vector2(0f, 0f)), 1f);
        }

        /// <summary>与障碍完全同坐标：确定方向、可复现、被推开。</summary>
        [Test]
        public void TryResolveOne_IdenticalPosition_IsDeterministic()
        {
            var p = new Vector2(5f, 5f);
            var obstacles = new[] { C(5f, 5f, 0.4f) };

            Assert.DoesNotThrow(() =>
            {
                Separation2D.TryResolveOne(p, 0.4f, obstacles, 1, out var a);
                Separation2D.TryResolveOne(p, 0.4f, obstacles, 1, out var b);

                Assert.AreEqual(a.x, b.x, 0f, "确定性：同输入 ⇒ 逐位相同");
                Assert.AreEqual(a.y, b.y, 0f, "确定性：同输入 ⇒ 逐位相同");
                Assert.GreaterOrEqual(Dist(a, p), 0.8f, "推开后必须不再重合（0.4 + 0.4）");
            });
        }

        /// <summary>已不重叠 ⇒ 原样返回（含"申请位置恰好在相切距离上"）。</summary>
        [Test]
        public void TryResolveOne_NotOverlapping_ReturnsInputVerbatim()
        {
            var obstacles = new[] { C(0f, 0f, 0.4f) };
            var p = new Vector2(0.8f, 0f); // 间距 0.8 == 0.4 + 0.4 ⇒ 相切

            Assert.IsTrue(Separation2D.TryResolveOne(p, 0.4f, obstacles, 1, out var resolved));
            Assert.AreEqual(p.x, resolved.x, 0f);
            Assert.AreEqual(p.y, resolved.y, 0f);
        }
    }
}
