// ─────────────────────────────────────────────────────────────────────────────
// CloverEngine · Runtime/Resource/SpriteSet.cs
// 「批量异步预加载 → 按名同步取 → 缺失只报一次」的精灵组合件。
//
// `Game.Res.LoadAsset` 只有异步版（契约见 Runtime/Core/Contracts.cs:1028-1043）
//   ⇒ 想"随用随取"就必须先把整套攒进缓存，这就是"先 Preload、后同步取"的由来。
//   取不到（Preload 清单漏了某个动作）必须报、且只报一次（Get 每帧都会被调用）：
//   静默返回 null 的表现是**角色整段隐形，且零报错零日志**。
//
// 重复预加载同一路径由引擎合并：`IResourceManager.LoadAsset` 内部
//   `JoinOrCreatePending` 把并发请求并进同一次加载（Runtime/Resource/ResourceManager.cs:205）。
//
// 引擎 Addressables 后端的现状：**引擎当前没有 Addressables 后端**，只有
//   「Unity 内置 Resources」与「AssetBundle 热更」两个后端（后端切换见 ResourceModuleConfig
//   的 ManifestUrl 语义，Runtime/Core/ResourceContracts.cs:476-484）；
//   可插拔的 Provider 只在 UI 侧存在（CloverPresentation.PanelProvider）。
//   本件只依赖 IResourceManager 的三条既有语义（Preload / TryGet / Release 由 Preload 自理），
//   因此将来新增后端时行为随该后端语义走，本件不需要改。
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using UnityEngine;

namespace CloverEngine
{
    /// <summary>
    /// 精灵集合：一次性批量预加载，之后按名同步取。
    /// <para>
    /// <b>用法</b>：
    /// <code>
    /// var sprites = new SpriteSet();
    /// sprites.LoadSet(new[] { "Art/Units/hero_idle", "Art/Units/hero_run" }, () => { /* 可以开始用了 */ });
    /// // 之后每帧：sprites.Get("hero_idle") 或 sprites.Get("Art/Units/hero_idle")
    /// </code>
    /// </para>
    /// <para>
    /// <b>不阻塞主线程</b>：加载走 <see cref="IResourceManager.Preload"/>（异步）；<see cref="Get"/>
    /// 是**纯读缓存**（<see cref="IResourceManager.TryGet{T}(string)"/>）—— 不触发加载、不等待，
    /// 没驻留就返回 <c>null</c> 并**只报一次**错。
    /// </para>
    /// <para>
    /// <b>不持有引用计数</b>：<c>Preload</c> 自己会把预热那次 <c>+1</c> 还掉
    ///（ResourceManager.cs:536-538），本件不额外 Release；因此条目在水位压力下可被 LRU 淘汰，
    /// 淘汰后 <see cref="Get"/> 会返回 <c>null</c>（会报一次）。需要长期常驻请自行
    /// <c>Game.Res.LoadAsset</c> 持有引用。
    /// </para>
    /// <para><b>非线程安全</b>：主线程使用（与 <see cref="LogThrottle"/> 一致）。</para>
    /// </summary>
    public sealed class SpriteSet
    {
        /// <summary>日志 tag（引擎惯例：各能力用类名自报家门，见 Runtime/Core/*.cs）。</summary>
        private const string Tag = "SpriteSet";

        /// <summary>名字 → 资源路径。全路径本身也是一个可用的名字。</summary>
        private readonly Dictionary<string, string> _pathsByName = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>显式注入的资源管理器；<c>null</c> 时回落到 <see cref="Game.Res"/>。</summary>
        private readonly IResourceManager _resource;

        private int _inFlight;
        private bool _loadRequested;

        /// <summary>
        /// 「只报一次」的代际：<see cref="Clear"/> 递增后，同名缺失会**再报一次**
        /// （换关卡/重进游戏后重新加载，值得再提醒一次）。
        /// ⛔ 不调 <see cref="LogThrottle.Reset"/>：那会连带清掉别的系统的限频记录，是越界副作用。
        /// </summary>
        private int _generation;

        /// <param name="resource">
        /// 资源管理器；一般传 <c>null</c>，取 <see cref="Game.Res"/>。
        /// 保留注入口是为了离线/EditMode 自检与替换实现（引擎门面在 Launch 前为空）。
        /// </param>
        public SpriteSet(IResourceManager resource = null) => _resource = resource;

        /// <summary>
        /// 预加载是否**已结束**（含"清单为空"与"资源管理器未挂接"两种立即结束的情形）。
        /// <para>它只表示"流程结束"，<b>不表示</b>"每一张都拿到" —— 缺哪张由 <see cref="Get"/> 报。</para>
        /// </summary>
        public bool IsReady => _loadRequested && _inFlight == 0;

        /// <summary>已登记的名字条数（含全路径与短名；诊断与自检用）。</summary>
        public int Count => _pathsByName.Count;

        private IResourceManager Res => _resource ?? Game.Res;

        /// <summary>
        /// 批量预加载一组资源路径；全部完成后回调 <paramref name="onDone"/>（**必定被调用一次**，
        /// 含清单为空与资源管理器缺失的情形 —— 否则调用方会永久等待）。
        /// <para>
        /// 名字解析：每条路径登记两个名字 —— ① 路径原样（如 <c>Art/Units/hero_idle</c>）；
        /// ② 末段去扩展名（如 <c>hero_idle</c>）。短名冲突时保留先注册的那条并报一次 Warn
        ///（此时请改用全路径取）。反斜杠一律规范成正斜杠，预加载与取值用同一串，避免路径两侧不一致。
        /// </para>
        /// <para>可多次调用**追加**清单（同名以最后一次为准）；要整体重建请先 <see cref="Clear"/>。</para>
        /// </summary>
        public void LoadSet(IEnumerable<string> paths, Action onDone = null)
        {
            if (paths == null)
            {
                LogThrottle.ErrorOnce(Tag, $"SpriteSet.NullPaths[{_generation}]",
                    "LoadSet 收到 null（按空清单处理）；若要清空请用 Clear()");
                _loadRequested = true;
                onDone?.Invoke();
                return;
            }

            var list = new List<string>();
            foreach (var raw in paths)
            {
                var path = Normalize(raw);
                if (string.IsNullOrEmpty(path)) continue;

                list.Add(path);
                _pathsByName[path] = path;                      // ① 全路径也是名字

                var shortName = ShortName(path);                // ② 末段去扩展名
                if (string.IsNullOrEmpty(shortName) || shortName == path) continue;

                if (_pathsByName.TryGetValue(shortName, out var existing) && existing != path)
                {
                    // 非预期分支（同名不同目录）：必须留痕，否则"按名取"会悄悄拿到另一张图
                    LogThrottle.WarnOnce(Tag, $"SpriteSet.NameCollision[{_generation}]/{shortName}",
                        $"短名 `{shortName}` 同时对应 `{existing}` 与 `{path}`：按名取将拿到先注册的那个，请改用全路径");
                    continue;
                }

                _pathsByName[shortName] = path;
            }

            _loadRequested = true;

            if (list.Count == 0)
            {
                onDone?.Invoke();
                return;
            }

            var res = Res;
            if (res == null)
            {
                // 引擎门面尚未挂接（CloverRes.Init 未跑）：不能静默，也不能让调用方永久等待
                LogThrottle.ErrorOnce(Tag, $"SpriteSet.NoResourceManager[{_generation}]",
                    $"IResourceManager 未挂接（Game.Res 为空），{list.Count} 条资源不会加载；" +
                    "请先 CloverRes.Init / Game.AttachResource");
                onDone?.Invoke();
                return;
            }

            _inFlight++;
            try
            {
                res.Preload(list, () =>
                {
                    _inFlight--;
                    onDone?.Invoke();
                });
            }
            catch (Exception ex)
            {
                _inFlight--;
                LogThrottle.ErrorOnce(Tag, $"SpriteSet.PreloadThrew[{_generation}]",
                    $"Preload 抛出 {ex.GetType().Name}: {ex.Message}（按加载失败处理）");
                onDone?.Invoke();
            }
        }

        /// <summary>
        /// 按名同步取一张已驻留的精灵。**不加载、不阻塞**。
        /// <para>取不到（名字不在清单 / 尚未驻留 / 已被水位淘汰）返回 <c>null</c>，
        /// 并按名字**只报一次** Error（它会每帧被调用，逐帧刷屏会把日志打爆）。</para>
        /// </summary>
        public Sprite Get(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                LogThrottle.ErrorOnce(Tag, $"SpriteSet.EmptyName[{_generation}]",
                    "Get 收到空名字，返回 null（调用方应传 LoadSet 里的路径或末段名）");
                return null;
            }

            var key = Normalize(name);
            if (!_pathsByName.TryGetValue(key, out var path))
            {
                LogThrottle.ErrorOnce(Tag, $"SpriteSet.NotInSet[{_generation}]/{key}",
                    $"精灵不在 LoadSet 清单里：{key}（清单 {_pathsByName.Count} 条）—— 这一帧会隐形");
                return null;
            }

            var sprite = Res?.TryGet<Sprite>(path);
            if (sprite == null)
            {
                LogThrottle.ErrorOnce(Tag, $"SpriteSet.NotResident[{_generation}]/{path}",
                    $"精灵未驻留：{path}（加载失败或已被缓存水位淘汰）—— 这一帧会隐形");
            }

            return sprite;
        }

        /// <summary>
        /// 清空名字索引并让 <see cref="IsReady"/> 复位；下次 <see cref="LoadSet"/> 是全新一批。
        /// <para>只清本件的索引：引擎缓存的引用计数不由本件持有，淘汰交给水位/LRU
        ///（见类型注释「不持有引用计数」）。</para>
        /// </summary>
        public void Clear()
        {
            _pathsByName.Clear();
            _loadRequested = false;
            _generation++;                                          // 重新武装「只报一次」
        }

        // ── 内部 ─────────────────────────────────────────────────────────────

        /// <summary>路径规范化：反斜杠转正斜杠（Windows 下手写的路径与 AssetDatabase 串不一致会静默取不到）。</summary>
        private static string Normalize(string path)
            => string.IsNullOrEmpty(path) ? null : path.Replace('\\', '/').Trim();

        /// <summary>末段去扩展名：<c>Art/Units/hero_idle.png</c> ⇒ <c>hero_idle</c>。</summary>
        private static string ShortName(string normalizedPath)
        {
            var slash = normalizedPath.LastIndexOf('/');
            var name = slash >= 0 ? normalizedPath.Substring(slash + 1) : normalizedPath;

            var dot = name.LastIndexOf('.');
            return dot > 0 ? name.Substring(0, dot) : name;
        }
    }
}
