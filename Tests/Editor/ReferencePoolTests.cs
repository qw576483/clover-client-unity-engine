using System.Collections.Generic;
using NUnit.Framework;

namespace CloverEngine.Tests
{
    /// <summary>
    /// <see cref="ReferencePool"/>（纯 C# 对象复用）的回归用例：复用同一实例、复位回调、
    /// 按类型分桶、清理与 null 归还。
    /// </summary>
    public class ReferencePoolTests
    {
        /// <summary>被测对象：实现 <see cref="IReferencePoolable"/>，记录回调并自带一份集合状态。</summary>
        private class Item : IReferencePoolable
        {
            public int Acquires;
            public int Releases;
            public readonly List<int> Data = new();

            public void OnAcquire()
            {
                Acquires++;
                Data.Clear();
            }

            public void OnRelease()
            {
                Releases++;
                Data.Clear();
            }
        }

        private class Other
        {
        }

        // 静态池是全局的：用例之间必须互不干扰
        [SetUp]
        public void SetUp() => ReferencePool.ClearAll();

        [TearDown]
        public void TearDown() => ReferencePool.ClearAll();

        [Test]
        public void Acquire_OnEmptyPool_CreatesNewInstance()
        {
            var a = ReferencePool.Acquire<Item>();
            Assert.IsNotNull(a);
            Assert.AreEqual(0, ReferencePool.Count<Item>(), "Acquire 会从池里取出，池应保持空");
        }

        [Test]
        public void Release_ThenAcquire_ReusesSameInstance()
        {
            var a = ReferencePool.Acquire<Item>();
            ReferencePool.Release(a);

            var b = ReferencePool.Acquire<Item>();
            Assert.AreSame(a, b, "归还后应复用同一实例（这正是引用池的目的）");
        }

        [Test]
        public void Poolable_Callbacks_InvokedAndStateResetOnAcquire()
        {
            var a = ReferencePool.Acquire<Item>();
            a.Data.Add(1);
            a.Data.Add(2);
            ReferencePool.Release(a);
            Assert.AreEqual(1, a.Releases, "归还时应回调 OnRelease");
            Assert.AreEqual(0, a.Data.Count, "OnRelease 应清掉外部引用/集合");

            var b = ReferencePool.Acquire<Item>();
            Assert.AreSame(a, b);
            // 同一实例：第 1 次 Acquire 记 1，归还后被再次取出再记 1 ⇒ 共 2
            Assert.AreEqual(2, b.Acquires, "每次取出都应回调 OnAcquire");
            Assert.AreEqual(0, b.Data.Count, "复用实例不得带上一次的状态");
        }

        [Test]
        public void Pools_AreSeparatedByType()
        {
            ReferencePool.Release(ReferencePool.Acquire<Item>());
            ReferencePool.Release(ReferencePool.Acquire<Other>());

            Assert.AreEqual(1, ReferencePool.Count<Item>());
            Assert.AreEqual(1, ReferencePool.Count<Other>());
        }

        [Test]
        public void Clear_Type_DropsPooledInstances()
        {
            ReferencePool.Release(ReferencePool.Acquire<Item>());
            Assert.AreEqual(1, ReferencePool.Count<Item>());

            ReferencePool.Clear<Item>();
            Assert.AreEqual(0, ReferencePool.Count<Item>());
        }

        [Test]
        public void Release_Null_IsIgnored()
        {
            Assert.DoesNotThrow(() => ReferencePool.Release<Item>(null));
            Assert.AreEqual(0, ReferencePool.Count<Item>());
        }
    }
}
