using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.U2D;

namespace CloverEngine
{
    // 契约（ISpriteAtlasManager）在 Runtime/Core/PresentationContracts.cs。

    /// <summary>
    /// 图集管理器实现，通过引用计数管理图集的生命周期
    /// </summary>
    internal class SpriteAtlasManager : ISpriteAtlasManager
    {
        private readonly Dictionary<string, SpriteAtlas> _loaded = new();
        private readonly Dictionary<string, int> _refCounts = new();

        /// <inheritdoc/>
        public void Load(string atlasName, Action<SpriteAtlas> callback)
        {
            if (_loaded.TryGetValue(atlasName, out var atlas))
            {
                _refCounts[atlasName]++;
                Post(() => callback?.Invoke(atlas));
                return;
            }

            // 资源模块未挂载（未 CloverRes.Init / 引擎已 Shutdown）时不能静默短路：
            // `Game.Res?.LoadAsset(...)` 的 `?.` 会吞掉整句 —— callback 永远不被调用、也没有日志，
            // 表现为「图集加载了但什么都没发生」（业务永久等待）。这里补日志 + 以 null 回调收尾。
            var res = Game.Res;
            if (res == null)
            {
                Game.Logger?.Error("Atlas",
                    $"图集加载失败：资源模块未挂载（Game.Res 为 null），atlas={atlasName}");
                Post(() => callback?.Invoke(null));
                return;
            }

            res.LoadAsset<SpriteAtlas>($"Atlas/{atlasName}", loaded =>
            {
                if (loaded != null)
                {
                    // 同一图集的并发请求会各自回调一次：计数必须**累加**而不是覆盖成 1，
                    // 否则一次 Release 就归零卸载，其它仍持有句柄的调用方拿到失效对象。
                    if (!_loaded.ContainsKey(atlasName)) _loaded[atlasName] = loaded;
                    _refCounts.TryGetValue(atlasName, out var count);
                    _refCounts[atlasName] = count + 1;
                }
                Post(() => callback?.Invoke(loaded));
            });
        }

        /// <inheritdoc/>
        public void Release(string atlasName)
        {
            if (!_refCounts.TryGetValue(atlasName, out var count)) return;
            count--;
            if (count <= 0)
            {
                _loaded.Remove(atlasName);
                _refCounts.Remove(atlasName);
                Game.Res?.Release($"Atlas/{atlasName}");
            }
            else
            {
                _refCounts[atlasName] = count;
            }
        }

        /// <inheritdoc/>
        public void GetSprite(string atlasName, string spriteName, Action<Sprite> callback)
        {
            if (_loaded.TryGetValue(atlasName, out var atlas))
            {
                var sprite = atlas.GetSprite(spriteName);
                Post(() => callback?.Invoke(sprite));
            }
            else
            {
                Load(atlasName, loaded =>
                {
                    var sprite = loaded?.GetSprite(spriteName);
                    callback?.Invoke(sprite);
                });
            }
        }

        /// <inheritdoc/>
        public void UnloadAll()
        {
            foreach (var kv in _loaded)
                Game.Res?.Release($"Atlas/{kv.Key}");
            _loaded.Clear();
            _refCounts.Clear();
        }

        /// <summary>
        /// 统一回调派发：有派发器时投主线程队列；**没有派发器时（未 Launch / 编辑器测试）直接内联执行** ——
        /// ⛔ 两条路径必须一致（缓存命中走 Post 会被静默丢弃、加载路径却同步直调，
        /// 表现为"同样的调用有时收不到回调"）。
        /// </summary>
        private static void Post(Action action)
        {
            if (action == null) return;
            var dispatcher = Game.Dispatcher;
            if (dispatcher != null) dispatcher.Post(action);
            else action();
        }
    }
}
