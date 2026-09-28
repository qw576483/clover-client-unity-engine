using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace CloverEngine.Tests
{
    /// <summary>
    /// <see cref="ResourceManager"/> 的「无类型加载 vs 具体类型加载」回归用例（EditMode，注入假后端 ⇒ 完全离线，
    /// 不依赖真实资源与真实链路）。
    /// <para>
    /// <b>复现的缺陷</b>：<c>Preload(paths, onDone)</c> 用 <c>LoadAsset&lt;UnityEngine.Object&gt;</c> 发起加载，
    /// 而 Unity 对贴图路径按**主资源**解析（Sprite 导入的 `.png` ⇒ <c>Texture2D</c>）⇒ 同一批路径之后
    /// <c>LoadAsset&lt;Sprite&gt;</c> 要么在途 join 后拿到 <c>null</c>、要么命中「与缓存类型不符」分支拿 <c>null</c>。
    /// </para>
    /// <para>
    /// 假后端复刻这一条真实语义：<c>BeginLoad</c> 收到 <c>typeof(UnityEngine.Object)</c> 回 <c>Texture2D</c>、
    /// 收到 <c>typeof(Sprite)</c> 回 <c>Sprite</c>；完成时机由用例显式驱动（<see cref="FakeBackend.CompleteAll"/>），
    /// 因此「在途」与「已完成」两条路径都能确定性复现。
    /// </para>
    /// </summary>
    public class ResourceManagerPreloadTests
    {
        private const string Path = "Sprites/Cards/art_000";

        /// <summary>按 List 顺序记录每次真实加载请求。</summary>
        private sealed class FakeBackend : IResourceBackend
        {
            public readonly List<string> RequestedPaths = new();
            public readonly List<Type> RequestedTypes = new();
            public int EndLoadCalls;

            private readonly List<Action> _pending = new();

            public string Name => "Fake";
            public bool Ready(out string error) { error = null; return true; }

            public AsyncOperation BeginLoad(string path, Type type, Action<UnityEngine.Object> onDone)
            {
                RequestedPaths.Add(path);
                RequestedTypes.Add(type);
                _pending.Add(() => onDone(AssetOf(type)));
                return null;   // 假后端不提供进度句柄（契约允许：调用方按「无进度」处理）
            }

            public void EndLoad(string path) => EndLoadCalls++;
            public void UnloadAll() { }
            public int EstimateBytes(string path, UnityEngine.Object asset) => 4096;
            public bool Exists(string path, Type type) => true;
            public T[] LoadAll<T>(string path) where T : UnityEngine.Object => Array.Empty<T>();

            /// <summary>让全部在途请求完成（模拟后端回调到达）。</summary>
            public void CompleteAll()
            {
                var batch = _pending.ToArray();
                _pending.Clear();
                foreach (var complete in batch) complete();
            }

            /// <summary>主资源（无类型加载拿到的那个）与具体类型资源是两个不同对象 —— 与 Unity 的真实语义一致。</summary>
            private static UnityEngine.Object AssetOf(Type type)
            {
                var tex = new Texture2D(4, 4);
                return type == typeof(Sprite)
                    ? Sprite.Create(tex, new Rect(0f, 0f, 4f, 4f), new Vector2(0.5f, 0.5f))
                    : tex;
            }
        }

        /// <summary>
        /// 用假后端造一个 <see cref="ResourceManager"/>。
        /// 走私有构造（不引入仅供测试的生产入口）：资源域内部类型已对测试程序集开放（<c>InternalsVisibleTo</c>）。
        /// </summary>
        private static ResourceManager CreateManager(FakeBackend backend)
        {
            var ctor = typeof(ResourceManager).GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic, null,
                new[] { typeof(IResourceBackend), typeof(ResourceUpdater), typeof(string), typeof(string), typeof(long) },
                null);
            Assert.IsNotNull(ctor, "ResourceManager 的私有构造应保留（本用例用它注入假后端）");
            return (ResourceManager)ctor.Invoke(new object[] { backend, null, "test-content", string.Empty, 0L });
        }

        [Test]
        public void Preload_InFlight_TypedLoadGetsSprite()
        {
            var backend = new FakeBackend();
            var manager = CreateManager(backend);

            manager.Preload(new List<string> { Path }, null);          // 无类型加载在途
            Assert.IsTrue(backend.RequestedTypes.Contains(typeof(UnityEngine.Object)), "预热应按无类型发起加载");

            Sprite got = null;
            var callbacks = 0;
            manager.LoadAsset<Sprite>(Path, s => { got = s; callbacks++; });
            Assert.IsTrue(backend.RequestedTypes.Contains(typeof(Sprite)),
                "在途的无类型请求满足不了 Sprite ⇒ 必须按 Sprite 重启一次真实加载");

            backend.CompleteAll();                                     // 两次请求（被放弃的那次 + 重启的那次）都回调

            Assert.AreEqual(1, callbacks, "回调必须恰好一次（被放弃的那次不得二次分发）");
            Assert.IsNotNull(got, "Preload 在途期间发起的 LoadAsset<Sprite> 必须拿到 Sprite");
            Assert.AreEqual(1, backend.EndLoadCalls, "被放弃的那次 BeginLoad 应补一次 EndLoad（后端占用配对）");
        }

        [Test]
        public void Preload_Completed_TypedLoadGetsSprite()
        {
            var backend = new FakeBackend();
            var manager = CreateManager(backend);

            var preloaded = false;
            manager.Preload(new List<string> { Path }, () => preloaded = true);
            backend.CompleteAll();
            Assert.IsTrue(preloaded, "预热应已完成");

            // 缓存里此刻是无类型加载留下的主资源（Texture2D）——
            // 按 Sprite 取必须把它当未命中并重载，而不是走「与缓存类型不符」返回 null。
            Sprite got = null;
            manager.LoadAsset<Sprite>(Path, s => got = s);
            backend.CompleteAll();

            Assert.IsNotNull(got, "预热完成后再按 Sprite 取，必须拿到 Sprite");
            Assert.AreEqual(typeof(Sprite), backend.RequestedTypes.Last(), "重载必须按 Sprite 发起");
        }

        [Test]
        public void TypedLoad_ThenPreload_DoesNotReloadOrDowngrade()
        {
            var backend = new FakeBackend();
            var manager = CreateManager(backend);

            Sprite got = null;
            manager.LoadAsset<Sprite>(Path, s => got = s);
            backend.CompleteAll();
            Assert.IsNotNull(got);

            // 对照组：先按 Sprite 加载好，再对同一路径预热（无类型）——
            // Sprite 本身也是 UnityEngine.Object ⇒ 命中缓存、不重载、不被降级成主资源。
            var preloaded = false;
            manager.Preload(new List<string> { Path }, () => preloaded = true);

            Assert.IsTrue(preloaded, "无类型请求应直接命中缓存（Sprite 是 UnityEngine.Object）");
            Assert.AreEqual(1, backend.RequestedPaths.Count(p => p == Path), "不得为同一路径再发起一次真实加载");
            Assert.AreEqual(0, backend.EndLoadCalls, "缓存命中不得产生 EndLoad");

            Sprite again = null;
            manager.LoadAsset<Sprite>(Path, s => again = s);
            Assert.IsNotNull(again, "缓存里的 Sprite 仍应按 Sprite 命中");
        }
    }
}
