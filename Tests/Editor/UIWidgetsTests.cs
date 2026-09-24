using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CloverEngine.Tests
{
    /// <summary>
    /// UI 通用件的纯逻辑单测（EditMode，不创建 Canvas）：当前覆盖红点注册表的分层聚合与通知语义，
    /// 以及 `WorldHpBar` 的渲染顺序透传（D129b 回归用例）。
    /// Toast / Loading / 确认框 / 引导遮罩需要真实 uGUI 节点与 EventSystem，属 PlayMode / 人工验证范围。
    /// </summary>
    public class UIWidgetsTests
    {
        /// <summary>点亮一个叶子，祖先链应自动聚合亮起，无关分支不受影响。</summary>
        [Test]
        public void RedDot_LeafOn_LightsAncestors()
        {
            var dots = new RedDotRegistry();
            dots.Set("mail/reward", true);

            Assert.IsTrue(dots.Get("mail/reward"), "叶子自身应亮起");
            Assert.IsTrue(dots.Get("mail"), "父节点应自动聚合后代，业务不必手工维护");
            Assert.IsFalse(dots.Get("shop"), "无关分支不应受影响");
            Assert.IsFalse(dots.Get("mail/gift"), "同父下的另一个叶子不应被点亮");
        }

        /// <summary>唯一的后代熄灭后，祖先也应熄灭。</summary>
        [Test]
        public void RedDot_LeafOff_TurnsAncestorsOff()
        {
            var dots = new RedDotRegistry();
            dots.Set("mail/reward", true);
            dots.Set("mail/reward", false);

            Assert.IsFalse(dots.Get("mail/reward"));
            Assert.IsFalse(dots.Get("mail"), "唯一后代熄灭后父节点必须跟着熄灭");
            Assert.AreEqual(0, dots.ExplicitCount);
        }

        /// <summary>还有兄弟亮着时，熄灭其中一个不能把父节点也灭掉。</summary>
        [Test]
        public void RedDot_SiblingOn_KeepsParentOn()
        {
            var dots = new RedDotRegistry();
            dots.Set("mail/reward", true);
            dots.Set("mail/gift", true);
            dots.Set("mail/reward", false);

            Assert.IsTrue(dots.Get("mail"), "还有其它后代亮着，父节点必须保持亮");
            Assert.IsTrue(dots.Get("mail/gift"), "兄弟节点不受影响");
        }

        /// <summary>只在**有效值**变化时回调：重复同值不触发，父节点有效值未变也不触发。</summary>
        [Test]
        public void RedDot_NotifiesOnlyOnEffectiveChange()
        {
            var dots = new RedDotRegistry();
            var events = new List<string>();
            dots.OnChanged((key, on) => events.Add($"{key}={on}"));

            dots.Set("mail/reward", true);
            CollectionAssert.AreEqual(new[] { "mail/reward=True", "mail=True" }, events,
                "点亮叶子应通知叶子与父节点各一次");

            dots.Set("mail/reward", true);
            Assert.AreEqual(2, events.Count, "重复设置同值不应再通知");

            dots.Set("mail/gift", true);
            Assert.AreEqual(3, events.Count, "父节点有效值未变，只应通知新叶子自己");
            Assert.AreEqual("mail/gift=True", events[2]);

            dots.Set("mail/reward", false);
            Assert.AreEqual("mail/reward=False", events[3], "熄灭叶子只通知叶子（父节点仍亮）");
            Assert.AreEqual(4, events.Count);
        }

        /// <summary>
        /// 不变量：父节点恒为「自身显式亮起 或 任一后代亮起」。
        /// 因此**不能**靠「把父节点设成 false」压制还亮着的子节点 —— 这会让 UI 出现
        /// 「子项有红点、父项没有」的困惑状态。要灭整棵子树请逐个熄灭叶子。
        /// </summary>
        [Test]
        public void RedDot_ParentCannotSuppressChildren()
        {
            var dots = new RedDotRegistry();
            dots.Set("mail/reward", true);
            dots.Set("mail", false);

            Assert.IsTrue(dots.Get("mail"), "子节点还亮着，父节点不能被单独熄灭");
            Assert.IsTrue(dots.Get("mail/reward"));
        }

        /// <summary>清空（退出登录 / 切号）应把全部红点熄灭并逐个回调。</summary>
        [Test]
        public void RedDot_Clear_TurnsAllOffAndNotifies()
        {
            var dots = new RedDotRegistry();
            dots.Set("mail/reward", true);
            dots.Set("shop/item", true);

            var events = new List<string>();
            dots.OnChanged((key, on) => events.Add($"{key}={on}"));

            dots.Clear();

            Assert.IsFalse(dots.Get("mail/reward"));
            Assert.IsFalse(dots.Get("mail"));
            Assert.IsFalse(dots.Get("shop/item"));
            Assert.IsFalse(dots.Get("shop"));
            Assert.AreEqual(0, dots.ExplicitCount);
            CollectionAssert.AreEquivalent(
                new[] { "mail/reward=False", "mail=False", "shop/item=False", "shop=False" }, events,
                "显式亮着的 key 与**被聚合的父节点**都应收到一次熄灭通知（只通知叶子会让订阅父 key 的业务红点永久卡住）");
        }

        /// <summary>空 key 是静默无操作，不应抛异常也不应留下状态。</summary>
        [Test]
        public void RedDot_EmptyKey_IsIgnored()
        {
            var dots = new RedDotRegistry();

            Assert.DoesNotThrow(() => dots.Set(null, true));
            Assert.DoesNotThrow(() => dots.Set(string.Empty, true));

            Assert.IsFalse(dots.Get(null));
            Assert.IsFalse(dots.Get(string.Empty));
            Assert.AreEqual(0, dots.ExplicitCount);
        }

        // ───────────────────────── WorldHpBar：渲染顺序（D129b 回归用例） ─────────────────────────
        //
        // 缺陷：WorldHpBar 的两个 Quad 从不写 sortingOrder ⇒ 恒为 0，而业务侧单位精灵常在 1000+
        //       ⇒ 血条被自己单位的精灵盖住（"时有时无、贴脸才看得见"，不报错）。
        // 本用例断言：传入的 sortingOrder 必须**落到两个 Quad 的 MeshRenderer 上**（Bg / Fill）。
        // 修复前该用例无法通过（参数不存在 / 值为 0）。
        // 注：`Create` 内部走 `GameObject.CreatePrimitive` + `Object.Destroy`（编辑器下会打一条
        //     "Destroy may not be called from edit mode"，属测试环境噪声），故忽略日志断言。

        /// <summary>未传 sortingOrder 时必须保持旧行为（0）—— 其它消费方（如 diablo2）不受影响。</summary>
        [Test]
        public void WorldHpBar_DefaultSortingOrder_IsZero()
        {
            var prev = LogAssert.ignoreFailingMessages;
            LogAssert.ignoreFailingMessages = true;
            var host = new GameObject("HpBarTestHost");
            try
            {
                var bar = WorldHpBar.Create(host.transform, tag: "Test.Hp");
                Assert.IsNotNull(bar);
                Assert.AreEqual(WorldHpBar.DefaultSortingOrder, bar.SortingOrder,
                    "默认必须等于 DefaultSortingOrder（刻意保持 0 = 兼容既有消费方）");
                Assert.AreEqual(0, bar.SortingOrder);
                AssertQuadsSortingOrder(bar, 0);
            }
            finally
            {
                Object.DestroyImmediate(host);
                LogAssert.ignoreFailingMessages = prev;
            }
        }

        /// <summary>传入的 sortingOrder 必须真正生效（血条要能压在自己的单位精灵之上）。</summary>
        [Test]
        public void WorldHpBar_ExplicitSortingOrder_ReachesBothQuads()
        {
            var prev = LogAssert.ignoreFailingMessages;
            LogAssert.ignoreFailingMessages = true;
            var host = new GameObject("HpBarTestHost2");
            try
            {
                var bar = WorldHpBar.Create(host.transform, 1f, 0.12f, 2.15f, "Test.Hp", 2000);
                Assert.AreEqual(2000, bar.SortingOrder);
                AssertQuadsSortingOrder(bar, 2000);

                // 幂等复用分支也要更新排序层（改了层级却"没反应"是静默失败）。
                var again = WorldHpBar.Create(host.transform, 1f, 0.12f, 2.15f, "Test.Hp", 4321);
                Assert.AreSame(bar, again, "已有血条必须复用，不再新建");
                Assert.AreEqual(4321, bar.SortingOrder);
                AssertQuadsSortingOrder(bar, 4321);
            }
            finally
            {
                Object.DestroyImmediate(host);
                LogAssert.ignoreFailingMessages = prev;
            }
        }

        private static void AssertQuadsSortingOrder(WorldHpBar bar, int expected)
        {
            var mrs = bar.GetComponentsInChildren<MeshRenderer>(true);
            Assert.AreEqual(2, mrs.Length, "血条必须恰好由 Bg + Fill 两个 Quad 组成");
            foreach (var mr in mrs)
                Assert.AreEqual(expected, mr.sortingOrder,
                    $"Quad `{mr.name}` 的 sortingOrder 必须是 {expected}（否则会被高层级的单位精灵盖住）");
        }
    }
}
