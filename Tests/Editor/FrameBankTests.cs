using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace CloverEngine.Tests
{
    /// <summary>
    /// <see cref="FrameBank.Preload(string, FrameBank.PivotMode)"/> 的契约回归：**预载口径覆盖派生表**
    /// —— 一次调用把「整目录帧数组」与「帧号 → 数组下标」映射表都建好，之后的读取是纯读。
    ///
    /// <para>
    /// 判据（可离线判定，不需要 Game.Res）：<see cref="FrameBank.FrameNumberMap(string, FrameBank.PivotMode)"/>
    /// 只在"本目录还没建过表"时才 <c>new</c> 一张；预载后它必须返回**与预载同实例**的那张表。
    /// 实例不同 ⇒ 读取那一刻又现建了一张，即本用例要挡住的回归。
    /// </para>
    /// </summary>
    public class FrameBankTests
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
        public void Preload_UnknownDir_YieldsEmptyFramesAndEmptyMap()
        {
            var bank = new FrameBank(new FakeResourceManager());

            var pre = bank.Preload("Art/Units/does_not_exist", FrameBank.PivotMode.AsImported);

            Assert.IsNotNull(pre.Frames, "⛔ 不返回 null");
            Assert.IsNotNull(pre.FrameMap, "⛔ 不返回 null");
            Assert.AreEqual(0, pre.Frames.Length);
            Assert.AreEqual(0, pre.FrameMap.Length);
        }

        [Test]
        public void Preload_EmptyPath_YieldsEmptyFramesAndEmptyMap()
        {
            var bank = new FrameBank(new FakeResourceManager());

            var pre = bank.Preload(string.Empty, FrameBank.PivotMode.AsImported);

            Assert.AreEqual(0, pre.Frames.Length);
            Assert.AreEqual(0, pre.FrameMap.Length);
        }

        [Test]
        public void Preload_BuildsFrameNumberMap_SoReadDoesNotRebuildIt()
        {
            var res = new FakeResourceManager();
            var bank = new FrameBank(res);
            res.Put("Art/Units/hero_out", NewFrames("frame_000", "frame_001", "frame_002"));

            var pre = bank.Preload("Art/Units/hero_out", FrameBank.PivotMode.AsImported);

            Assert.AreEqual(3, pre.Frames.Length);
            Assert.AreEqual(3, pre.FrameMap.Length, "表长 = 最大帧号 + 1");
            Assert.AreSame(pre.FrameMap,
                bank.FrameNumberMap("Art/Units/hero_out", FrameBank.PivotMode.AsImported),
                "预载后读取必须命中预载建好的那张表（另一个实例 = 读取那一刻又现建了一张）");
            Assert.AreEqual(1, res.LoadAllCalls.Count, "预载 + 后续读取只加载一次目录资源");
        }

        [Test]
        public void Preload_FramesAreSameCacheEntryAsLoadDir()
        {
            var res = new FakeResourceManager();
            var bank = new FrameBank(res);
            res.Put("Art/Units/hero_out", NewFrames("frame_000", "frame_001"));

            var pre = bank.Preload("Art/Units/hero_out", FrameBank.PivotMode.AsImported);

            Assert.AreSame(pre.Frames, bank.LoadDir("Art/Units/hero_out", FrameBank.PivotMode.AsImported),
                "帧数组是同一个缓存条目（⛔ 不因预载而多加载一份）");
            Assert.AreEqual(1, bank.Count);
        }

        [Test]
        public void Preload_KeepsNonIdentityMapForMultiSpriteNames()
        {
            var res = new FakeResourceManager();
            var bank = new FrameBank(res);
            // 一张 PNG 被切成多个子 Sprite：两个子 Sprite 的帧号相同（`frame_000_0` / `frame_000_1`）
            res.Put("Art/Units/sheet_out", NewFrames("frame_000_0", "frame_000_1"));

            var pre = bank.Preload("Art/Units/sheet_out", FrameBank.PivotMode.AsImported);

            Assert.AreEqual(2, pre.Frames.Length, "两个子 Sprite 都进帧数组");
            Assert.AreEqual(1, pre.FrameMap.Length, "都归属帧号 0 ⇒ 表长 1");
            Assert.AreEqual(0, pre.FrameMap[0], "同帧号取首次出现的下标");
        }

        /// <summary>造一批名字如 `frame_000` 的 Sprite（每个各自一张 4×4 贴图）。</summary>
        private Sprite[] NewFrames(params string[] names)
        {
            var frames = new Sprite[names.Length];
            for (var i = 0; i < names.Length; i++)
            {
                var texture = new Texture2D(4, 4);
                _created.Add(texture);
                var sprite = Sprite.Create(texture, new Rect(0, 0, 4, 4), new Vector2(0.5f, 0.5f));
                sprite.name = names[i];
                _created.Add(sprite);
                frames[i] = sprite;
            }
            return frames;
        }

        /// <summary>
        /// 最小 <see cref="IResourceManager"/> 假实现：只把 <see cref="FrameBank"/> 用到的
        /// <see cref="IResourceManager.LoadAll{T}(string)"/> 做真（记调用次数 —— 判据要证明"只加载一次"），
        /// 其余一律 <see cref="NotSupportedException"/> —— 被 FrameBank 意外调用时直接暴露，
        /// 而不是静默返回默认值。
        /// </summary>
        private sealed class FakeResourceManager : IResourceManager
        {
            private readonly Dictionary<string, UnityEngine.Object[]> _dirs =
                new Dictionary<string, UnityEngine.Object[]>(StringComparer.Ordinal);

            /// <summary>每次 <see cref="LoadAll{T}(string)"/> 的路径（含降级那次 <c>Texture2D</c>）。</summary>
            public readonly List<string> LoadAllCalls = new List<string>();

            /// <summary>把某个目录标记为"有这批资源"。</summary>
            public void Put(string path, UnityEngine.Object[] objects) => _dirs[path] = objects;

            public T[] LoadAll<T>(string path) where T : UnityEngine.Object
            {
                LoadAllCalls.Add(path);
                UnityEngine.Object[] objects;
                if (!_dirs.TryGetValue(path, out objects)) return null;

                var typed = new T[objects.Length];
                for (var i = 0; i < objects.Length; i++) typed[i] = objects[i] as T;
                return typed;
            }

            public void Preload(List<string> paths, Action onDone, Action<float> progress = null)
                => throw new NotSupportedException();

            public T TryGet<T>(string path) where T : UnityEngine.Object => throw new NotSupportedException();

            public void LoadAsset<T>(string path, Action<T> callback) where T : UnityEngine.Object
                => throw new NotSupportedException();

            public void LoadAsset<T>(string path, Action<float> progress, Action<T> callback) where T : UnityEngine.Object
                => throw new NotSupportedException();

            public void Release(string path) => throw new NotSupportedException();

            public void UnloadAll() => throw new NotSupportedException();

            public bool Exists(string path) => throw new NotSupportedException();

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
