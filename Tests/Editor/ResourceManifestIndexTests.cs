using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace CloverEngine.Tests
{
    /// <summary>
    /// <see cref="ResourceManifest"/> 惰性索引的失效语义回归。
    ///
    /// <para>
    /// 契约是：
    /// ① 两个表**只读**（拿到手也改不动，不存在"绕过入口就地改"的路径）；
    /// ② 追加一律走 <c>AddFile</c> / <c>AddAsset</c>，它们就地让索引失效；
    /// ③ 另有 <c>InvalidateIndex()</c> 供自定义解析路径显式失效。
    /// </para>
    /// </summary>
    public class ResourceManifestIndexTests
    {
        [Test]
        public void Files_View_IsReadOnly()
        {
            var m = new ResourceManifest();
            m.AddFile(new ResourceFileInfo { Name = "a.bundle", Hash = "h1", Size = 1 });

            // 拿到手也必须是只读集合：能改的话就可能绕过 AddFile，索引便无从失效
            Assert.IsTrue(m.Files is IList<ResourceFileInfo>);
            Assert.Throws<NotSupportedException>(
                () => ((IList<ResourceFileInfo>)m.Files).Add(new ResourceFileInfo { Name = "x" }));
        }

        [Test]
        public void Assets_View_IsReadOnly()
        {
            var m = new ResourceManifest();
            m.AddAsset(new ResourceAssetEntry { Path = "UI/A", Bundle = "ui_common" });

            Assert.Throws<NotSupportedException>(
                () => ((IList<ResourceAssetEntry>)m.Assets).Add(new ResourceAssetEntry { Path = "UI/B" }));
        }

        [Test]
        public void Find_SeesFilesAddedAfterFirstLookup()
        {
            var m = new ResourceManifest();
            m.AddFile(new ResourceFileInfo { Name = "a.bundle", Hash = "h1", Size = 1 });
            Assert.IsNotNull(m.Find("a.bundle"));

            // 先查过一次（索引已建）之后再追加：新条目必须立刻可查
            m.AddFile(new ResourceFileInfo { Name = "b.bundle", Hash = "h2", Size = 1 });
            Assert.IsNotNull(m.Find("b.bundle"));
        }

        [Test]
        public void Find_LastWriteWinsForSameName()
        {
            var m = new ResourceManifest();
            m.AddFile(new ResourceFileInfo { Name = "a.bundle", Hash = "h1", Size = 1 });
            Assert.AreEqual("h1", m.Find("a.bundle").Hash);

            // 同名再追加（索引必须重建，否则返回的是陈旧条目）
            m.AddFile(new ResourceFileInfo { Name = "a.bundle", Hash = "h2", Size = 2 });
            Assert.AreEqual("h2", m.Find("a.bundle").Hash);
        }

        [Test]
        public void FindAsset_AfterInvalidateIndex_ReflectsNewEntries()
        {
            var m = new ResourceManifest();
            m.AddAsset(new ResourceAssetEntry { Path = "UI/A", Bundle = "ui_common" });
            Assert.IsNotNull(m.FindAsset("UI/A"));

            m.InvalidateIndex();
            m.AddAsset(new ResourceAssetEntry { Path = "UI/B", Bundle = "ui_common" });

            Assert.IsNotNull(m.FindAsset("UI/A"));
            Assert.IsNotNull(m.FindAsset("UI/B"));
        }

        [Test]
        public void Find_MissingAndEmptyNames_ReturnNull()
        {
            var m = new ResourceManifest();
            Assert.IsNull(m.Find(null));
            Assert.IsNull(m.Find(""));
            Assert.IsNull(m.FindAsset("nope"));
        }
    }
}
