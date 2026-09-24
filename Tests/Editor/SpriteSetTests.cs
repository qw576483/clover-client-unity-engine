using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace CloverEngine.Tests
{
    /// <summary>
    /// <see cref="SpriteSet"/> 的契约回归：批量预加载 → 按名同步取 → 缺失只报一次。
    ///
    /// <para>
    /// 这些用例只钉**可判定的行为**：名字解析（全路径 / 末段名 / 反斜杠规范化）、
    /// 「预加载结束」的判定、取不到时返回 <c>null</c> 而不是抛异常、
    /// 资源管理器缺失时**回调仍然会来**（否则调用方永久等待）。
    /// </para>
    /// <para>
    /// ⛔ 本文件**不**断言"日志只出一条"：那条语义由引擎既有的
    /// <see cref="LogThrottle.WarnOnce"/>/<see cref="LogThrottle.ErrorOnce"/> 保证，
    /// 而其 key 是 SpriteSet 的私有实现细节（含代际号）—— 把私有 key 格式抄进测试会让
    /// 测试与实现细节耦合。日志去重本身在 LogThrottle 侧已有归属，这里不重复钉。
    /// </para>
    /// </summary>
    public class SpriteSetTests
    {
        private readonly List<UnityEngine.Object> _created = new List<UnityEngine.Object>();

        [TearDown]
        public void TearDown()
        {
            // EditMode 下不能靠 GC：显式销毁本用例建出来的 Unity 对象
            foreach (var obj in _created)
                if (obj != null) UnityEngine.Object.DestroyImmediate(obj);

            _created.Clear();
        }

        [Test]
        public void LoadSet_EmptyList_CompletesImmediately()
        {
            var res = new FakeResourceManager();
            var set = new SpriteSet(res);
            var done = 0;

            set.LoadSet(new string[0], () => done++);

            Assert.AreEqual(1, done, "空清单也必须回调（否则调用方永久等待）");
            Assert.IsTrue(set.IsReady);
            Assert.AreEqual(0, set.Count);
            Assert.AreEqual(0, res.PreloadCalls.Count, "空清单不该去调 Preload");
        }

        [Test]
        public void LoadSet_NullList_CompletesInsteadOfThrowing()
        {
            var set = new SpriteSet(new FakeResourceManager());
            var done = 0;

            // 直接调用即可：真抛异常本用例就是红的。
            // ⛔ 刻意不用 Assert.DoesNotThrow —— NUnit 4 里它对 void 委托有 TestDelegate /
            //    AsyncTestDelegate 两个重载，块/表达式 lambda 都会撞成 CS0121 二义性。
            set.LoadSet(null, () => done++);

            Assert.AreEqual(1, done);
            Assert.IsTrue(set.IsReady);
        }

        [Test]
        public void Get_FindsSpriteByShortNameAndFullPath()
        {
            var res = new FakeResourceManager();
            var set = new SpriteSet(res);
            var sprite = NewSprite();
            res.Put("Art/Units/hero_idle", sprite);

            set.LoadSet(new[] { "Art/Units/hero_idle" });

            Assert.AreSame(sprite, set.Get("hero_idle"), "末段名应能取到");
            Assert.AreSame(sprite, set.Get("Art/Units/hero_idle"), "全路径也应能取到");
            Assert.IsTrue(set.IsReady);
        }

        [Test]
        public void LoadSet_NormalizesBackslashesForBothPreloadAndGet()
        {
            var res = new FakeResourceManager();
            var set = new SpriteSet(res);
            var sprite = NewSprite();
            res.Put("Art/Units/hero_idle", sprite);

            set.LoadSet(new[] { @"Art\Units\hero_idle" });

            // 预加载与取值必须用同一串规范化后的路径（否则会出现"加载了却永远取不到"）
            CollectionAssert.AreEqual(new[] { "Art/Units/hero_idle" }, res.LastPreloadPaths);
            Assert.AreSame(sprite, set.Get(@"Art\Units\hero_idle"));
            Assert.AreSame(sprite, set.Get("hero_idle"));
        }

        [Test]
        public void Get_UnknownNameOrNotResident_ReturnsNull_WithoutThrowing()
        {
            var res = new FakeResourceManager();
            var set = new SpriteSet(res);

            set.LoadSet(new[] { "Art/Units/hero_idle", "Art/Units/hero_run" });
            res.Put("Art/Units/hero_idle", NewSprite());     // hero_run 故意不驻留

            Assert.IsNull(set.Get("hero_run"), "清单里有但没驻留 ⇒ null");
            Assert.IsNull(set.Get("does_not_exist"), "不在清单里 ⇒ null");
            Assert.IsNull(set.Get(""), "空名字 ⇒ null");
            Assert.IsNull(set.Get("Art/Units/hero_run"), "用全路径取同样拿不到");
        }

        [Test]
        public void LoadSet_WithoutResourceManager_StillInvokesOnDone()
        {
            if (Game.Res != null)
                Assert.Ignore("本进程已挂接过 IResourceManager，无法覆盖「未挂接」分支");

            var set = new SpriteSet();      // 不注入 ⇒ 回落 Game.Res（此处为 null）
            var done = 0;

            set.LoadSet(new[] { "Art/Units/hero_idle" }, () => done++);

            Assert.AreEqual(1, done, "资源管理器缺失时也必须回调，否则调用方永久等待");
            Assert.IsTrue(set.IsReady);
            Assert.IsNull(set.Get("hero_idle"));
        }

        [Test]
        public void Clear_ResetsReadyAndLookups()
        {
            var res = new FakeResourceManager();
            var set = new SpriteSet(res);
            res.Put("Art/Units/hero_idle", NewSprite());
            set.LoadSet(new[] { "Art/Units/hero_idle" });
            Assert.AreEqual(2, set.Count, "全路径 + 末段名两条");

            set.Clear();

            Assert.IsFalse(set.IsReady, "Clear 之后要重新 LoadSet 才算就绪");
            Assert.AreEqual(0, set.Count);
            Assert.IsNull(set.Get("hero_idle"), "索引已清 ⇒ 取不到");
        }

        [Test]
        public void LoadSet_AppendsMorePathsOnSecondCall()
        {
            var res = new FakeResourceManager();
            var set = new SpriteSet(res);
            var first = NewSprite();
            var second = NewSprite();

            set.LoadSet(new[] { "Art/Units/hero_idle" });
            res.Put("Art/Units/hero_idle", first);
            set.LoadSet(new[] { "Art/Units/hero_run" });
            res.Put("Art/Units/hero_run", second);

            Assert.AreSame(first, set.Get("hero_idle"));
            Assert.AreSame(second, set.Get("hero_run"));
        }

        private Sprite NewSprite()
        {
            var texture = new Texture2D(4, 4);
            _created.Add(texture);

            var sprite = Sprite.Create(texture, new Rect(0, 0, 4, 4), new Vector2(0.5f, 0.5f));
            _created.Add(sprite);
            return sprite;
        }

        /// <summary>
        /// 最小 <see cref="IResourceManager"/> 假实现：只把 SpriteSet 用到的两条语义做真，
        /// 其余（热更 / 内存水位 / 同步加载）一律 <see cref="NotSupportedException"/> ——
        /// 被 SpriteSet 意外调用时会直接暴露出来，而不是静默返回默认值。
        /// </summary>
        private sealed class FakeResourceManager : IResourceManager
        {
            private readonly Dictionary<string, UnityEngine.Object> _objects =
                new Dictionary<string, UnityEngine.Object>(StringComparer.Ordinal);

            public readonly List<string[]> PreloadCalls = new List<string[]>();
            public string[] LastPreloadPaths = Array.Empty<string>();

            /// <summary>把某个路径标记为"已驻留"。</summary>
            public void Put(string path, UnityEngine.Object obj) => _objects[path] = obj;

            public void Preload(List<string> paths, Action onDone, Action<float> progress = null)
            {
                var copy = paths?.ToArray() ?? Array.Empty<string>();
                PreloadCalls.Add(copy);
                LastPreloadPaths = copy;
                onDone?.Invoke();          // 同步完成：用例只判"索引与取值"，不判时序
            }

            public T TryGet<T>(string path) where T : UnityEngine.Object
                => _objects.TryGetValue(path, out var obj) ? obj as T : null;

            public void LoadAsset<T>(string path, Action<T> callback) where T : UnityEngine.Object
                => throw new NotSupportedException();

            public void LoadAsset<T>(string path, Action<float> progress, Action<T> callback) where T : UnityEngine.Object
                => throw new NotSupportedException();

            public void Release(string path) => throw new NotSupportedException();

            public void UnloadAll() => throw new NotSupportedException();

            public bool Exists(string path) => throw new NotSupportedException();

            public T[] LoadAll<T>(string path) where T : UnityEngine.Object => throw new NotSupportedException();

            public long CachedBytes => 0;

            public long CacheWatermark { get; set; }

            public void Tick(float dt) { }

            public string Version => "0";

            public string ContentDir => null;

            public bool IsBundleMode => false;

            public ResourceUpdateState UpdateState => ResourceUpdateState.Idle;

            public void CheckUpdate(Action<ResourceUpdateInfo> onResult) => throw new NotSupportedException();

            public void DownloadUpdate(ResourceUpdateInfo info, Action<ResourceUpdateProgress> onProgress,
                Action<bool, string> onDone) => throw new NotSupportedException();

            public void ClearDownloaded() => throw new NotSupportedException();

            public void CancelUpdate() => throw new NotSupportedException();
        }
    }
}
