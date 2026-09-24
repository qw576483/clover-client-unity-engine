using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace CloverEngine
{
    /// <summary>
    /// 资源管理器：缓存 / 引用计数 / 水位淘汰 / 热更编排，加载动作委派给 <see cref="IResourceBackend"/>。
    /// 契约类型（<see cref="IResourceManager"/>）定义于 Core；模块初始化经 <c>CloverRes.Init</c> 完成。
    /// </summary>
    /// <remarks>
    /// 三处行为约定：
    /// <list type="number">
    ///   <item>
    ///   <b>Release 不立刻从缓存移除</b>：引用计数归零只表示「业务不再用」，
    ///   缓存是否释放交给**字节水位 + LRU** 决定。否则「加载→立刻释放→再加载」会反复走磁盘/解包，
    ///   而缓存本来就是为了避免这件事。显式释放仍有 <see cref="UnloadAll"/>。
    ///   </item>
    ///   <item>
    ///   <b>淘汰只针对未引用条目</b>：水位压不下去时**宁可超标也不强拆正在被引用的资源** ——
    ///   强拆会把业务正在用的对象从缓存摘掉并连带 Unload，业务侧随后拿到 Unity 的「假 null」。
    ///   </item>
    ///   <item>
    ///   <b>同路径并发加载合并</b>：多个调用者同时加载同一路径时只发起一次真实加载，
    ///   回调按注册顺序各收到一次。
    ///   </item>
    /// </list>
    /// </remarks>
    internal sealed class ResourceManager : IResourceManager
    {
        /// <summary>缓存条目。</summary>
        private sealed class Entry
        {
            public UnityEngine.Object Asset;
            public int RefCount;
            public int Bytes;

            /// <summary>
            /// 加载该条目时使用的后端。淘汰时按它配对 <c>EndLoad</c>：
            /// 运行中切换过后端（ApplyDownloadedContent）后，旧条目不能用新后端去释放，
            /// 否则会按新清单错误递减/卸载并不属于它的包。
            /// </summary>
            public IResourceBackend Backend;
        }

        /// <summary>在途加载：同一路径的多个调用者共享一次真实加载。</summary>
        private sealed class PendingLoad
        {
            public string Path;
            public readonly List<Action<UnityEngine.Object>> Callbacks = new();
            public readonly List<Action<float>> ProgressCallbacks = new();
            public AsyncOperation Operation;

            /// <summary>本次加载使用的后端（与 <see cref="Entry.Backend"/> 配对，防切换后端后释放错位）。</summary>
            public IResourceBackend Backend;

            /// <summary>是否已向后端发起过加载（区分「操作句柄为 null」与「尚未发起」）。</summary>
            public bool IsStarted;

            /// <summary>加载在途期间收到的 Release 次数：完成时从引用计数里扣掉，避免计数只增不减。</summary>
            public int Releases;

            public float LastReportedProgress = -1f;
        }

        // 缓存键是**业务加载路径**（逻辑键，不落磁盘），大小写不敏感：
        // 打包端（可能来自 Linux/macOS）与运行端对同一路径的大小写写法不一致时，
        // 仍应命中同一条目 —— 否则同一资源会被当两条重复加载、FindAsset 也会误报"未打包"。
        private readonly Dictionary<string, Entry> _cache = new(StringComparer.OrdinalIgnoreCase);
        private readonly LinkedList<string> _lru = new();
        private readonly Dictionary<string, LinkedListNode<string>> _lruNodes = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, PendingLoad> _inflight = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<PendingLoad> _activeLoads = new();

        // Exists 的按路径结果缓存（大小写口径与 _cache 一致：同一路径的不同大小写写法算一条）。
        // 只在切后端时清空（见 Exists 的备注）—— 它是「源里有没有」的答案，与加载/卸载无关。
        private readonly Dictionary<string, bool> _existsCache = new(StringComparer.OrdinalIgnoreCase);

        private IResourceBackend _backend;
        private readonly ResourceUpdater _updater;
        private readonly string _contentDir;
        private readonly string _builtinDir;
        private long _cachedBytes;
        private long _watermark;
        private bool _watermarkWarned;

        /// <summary>按配置构造资源管理器（含后端选择与热更编排）。</summary>
        /// <param name="config">模块配置；为 null 时按默认（纯 Resources）处理。</param>
        /// <param name="error">构造过程中的告警/错误（可能为 null）。</param>
        public static ResourceManager Create(ResourceModuleConfig config, out string error)
        {
            config ??= new ResourceModuleConfig();
            error = null;

            var contentDir = ResolveContentDir(config);
            var builtinDir = config.BuiltinContentDir;

            // 不配清单地址 = 不启用热更：走 Resources 后端
            if (string.IsNullOrEmpty(config.ManifestUrl))
                return new ResourceManager(new ResourcesBackend(config.Root), null, contentDir, builtinDir,
                    config.CacheWatermark);

            var localManifest = ResourceUpdater.LoadLocalManifest(contentDir);
            var updater = new ResourceUpdater(config, contentDir, localManifest);

            IResourceBackend backend;
            if (localManifest == null)
            {
                // 首次安装：还没下载过任何内容。这里**先用首包 Resources 兜底把游戏跑起来**，
                // 更新流程走完后会切换到 Bundle 后端（见 ApplyDownloadedContent）。
                // 直接报错会让「第一次进游戏」变成死路。
                backend = new ResourcesBackend(config.Root);
                error = "尚未下载过热更内容，暂用首包 Resources 兜底；更新完成后自动切换到 AssetBundle";
            }
            else
            {
                var bundleBackend = new AssetBundleBackend(contentDir, localManifest, builtinDir);
                if (bundleBackend.Ready(out var bundleError))
                {
                    backend = bundleBackend;
                }
                else
                {
                    backend = new ResourcesBackend(config.Root);
                    error = $"AssetBundle 后端不可用（{bundleError}），已回退首包 Resources";
                }
            }

            return new ResourceManager(backend, updater, contentDir, builtinDir, config.CacheWatermark);
        }

        private ResourceManager(IResourceBackend backend, ResourceUpdater updater, string contentDir,
            string builtinDir, long watermark)
        {
            _backend = backend;
            _updater = updater;
            _contentDir = contentDir;
            _builtinDir = builtinDir;
            _watermark = watermark;
        }

        /// <summary>解析内容目录：配置为空时落到 persistentDataPath 下的固定子目录。</summary>
        private static string ResolveContentDir(ResourceModuleConfig config)
        {
            if (!string.IsNullOrEmpty(config.ContentDir)) return config.ContentDir;

            try
            {
                return Path.Combine(Application.persistentDataPath, "clover-res");
            }
            catch (Exception e)
            {
                // persistentDataPath 在极早期（如静态构造阶段）可能不可用
                Game.Logger?.Warn("Resource", $"persistentDataPath 不可用（{e.Message}），热更内容目录退到临时目录");
                return Path.Combine(Path.GetTempPath(), "clover-res");
            }
        }

        // ─────────────────────────── 加载 ───────────────────────────

        /// <inheritdoc/>
        public void LoadAsset<T>(string path, Action<T> callback) where T : UnityEngine.Object
        {
            LoadAsset(path, null, callback);
        }

        /// <inheritdoc/>
        public void LoadAsset<T>(string path, Action<float> progress, Action<T> callback) where T : UnityEngine.Object
        {
            if (string.IsNullOrEmpty(path))
            {
                Game.Logger?.Error("Resource", "LoadAsset 收到空路径");
                callback?.Invoke(null);
                return;
            }

            if (_cache.TryGetValue(path, out var cached) && cached.Asset != null)
            {
                if (cached.Asset is T typed)
                {
                    cached.RefCount++;
                    Touch(path);
                    progress?.Invoke(1f);
                    callback?.Invoke(typed);
                    return;
                }

                // 同一路径被以不同类型加载：缓存里是另一个类型的实例。
                // 不递增引用计数（调用方拿不到对象）、也不静默返回 null —— 明确报错指向调用方写错了类型，
                // 否则「RefCount 涨了、资源却没到手」会一路累积成无法回收的引用。
                Game.Logger?.Error("Resource",
                    $"LoadAsset<{typeof(T).Name}> 与缓存类型不符：{path} 缓存中是 {cached.Asset.GetType().Name}" +
                    "（同一路径只能用同一类型加载）");
                callback?.Invoke(null);
                return;
            }

            // 缓存里存着「已被销毁」的对象（Unity 的假 null）：先清掉再重新加载，
            // 否则会一直把假 null 交给业务。
            if (_cache.ContainsKey(path))
                Evict(path);

            var pending = JoinOrCreatePending(path);
            if (progress != null) pending.ProgressCallbacks.Add(progress);
            pending.Callbacks.Add(obj => callback?.Invoke(obj as T));

            if (pending.Operation == null && !pending.IsStarted)
            {
                pending.IsStarted = true;
                pending.Backend = _backend;
                try
                {
                    pending.Operation = pending.Backend.BeginLoad(path, typeof(T), obj => CompletePending(pending, obj));
                }
                catch (Exception e)
                {
                    Game.Logger?.Error("Resource", $"加载失败：{path}: {e.Message}", e);
                    CompletePending(pending, null);
                }
            }
        }

        /// <inheritdoc/>
        public T TryGet<T>(string path) where T : UnityEngine.Object
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (!_cache.TryGetValue(path, out var entry)) return null;

            // 用 Unity 的 == null 判断：被销毁的对象会算出「假 null」，
            // 那种情况必须当作"没有" —— 否则业务拿到手一用就是 MissingReference。
            if (entry.Asset == null) return null;

            // 故意【不】RefCount++ 也【不】Touch：
            // TryGet 是"看一眼缓存里有没有"，不是"我又加载了一次"。
            // 若在这里加引用，业务每帧 TryGet 一次就会把引用计数顶上去，那个资源再也释放不掉。
            return entry.Asset as T;
        }

        /// <inheritdoc/>
        /// <remarks>
        /// **按路径缓存**（契约要求）：本事只在第一次问某个路径时落到后端（Resources 后端就是一次探测），
        /// 之后一辈子命中本字典 ⇒ 高频调用（例如每帧问一次图标）不会反复读盘。
        /// <para>
        /// 缓存失效点：**切换后端**（<see cref="ApplyDownloadedContent"/> —— 首包 Resources → 热更 Bundle）
        /// 时必须清空，否则「新后端里有、首包里没有」的路径会被旧的 <c>false</c> 永久蒙住。
        /// 只降不升的东西（源里没有就是没有）不需要其它失效点。
        /// </para>
        /// </remarks>
        public bool Exists(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;                 // 空路径恒「不存在」（不探测、不缓存）
            if (_existsCache.TryGetValue(path, out var cached)) return cached;

            var result = _backend != null && _backend.Exists(path, typeof(UnityEngine.Object));
            _existsCache[path] = result;
            return result;
        }

        /// <inheritdoc/>
        public T[] LoadAll<T>(string path) where T : UnityEngine.Object
        {
            if (string.IsNullOrEmpty(path))
            {
                // 空路径：不静默（否则「取不到」会被当成「本来就没有」），但也不抛 —— 按契约返回空数组
                Game.Logger?.Warn("Resource", $"LoadAll<{typeof(T).Name}> 收到空路径 ⇒ 返回空数组");
                return Array.Empty<T>();
            }

            if (_backend == null)
            {
                Game.Logger?.Warn("Resource", $"LoadAll<{typeof(T).Name}>：后端未就绪 ⇒ 返回空数组（{path}）");
                return Array.Empty<T>();
            }

            try
            {
                return _backend.LoadAll<T>(path) ?? Array.Empty<T>();
            }
            catch (Exception e)
            {
                // 后端的实现异常不能穿到业务（契约：取不到＝空数组）；但必须留痕，不静默
                Game.Logger?.Error("Resource",
                    $"LoadAll<{typeof(T).Name}> 失败（返回空数组）：{path}: {e.Message}", e);
                return Array.Empty<T>();
            }
        }

        private PendingLoad JoinOrCreatePending(string path)
        {
            if (_inflight.TryGetValue(path, out var existing)) return existing;

            var pending = new PendingLoad { Path = path };
            _inflight[path] = pending;
            _activeLoads.Add(pending);
            return pending;
        }

        /// <summary>
        /// 一次真实加载结束：入缓存、把结果分发给全部等待者。
        /// 注意本方法**可能被同步重入**（后端在路径非法时直接回调），因此 <see cref="PendingLoad"/>
        /// 必须在调用后端之前就登记进 <c>_inflight</c>。
        /// </summary>
        private void CompletePending(PendingLoad pending, UnityEngine.Object asset)
        {
            _inflight.Remove(pending.Path);
            _activeLoads.Remove(pending);

            if (asset != null)
            {
                var owner = pending.Backend ?? _backend;
                var bytes = owner.EstimateBytes(pending.Path, asset);
                // 引用计数 = 等待者数量 − 在途期间已 Release 的数量（每次 LoadAsset 对应一次 Release）。
                // 恒置 1 会让先 Release 的一方把计数降到 0，条目被水位/LRU 提前淘汰，
                // 其它仍持句柄的调用者随之失效。
                var refCount = pending.Callbacks.Count - pending.Releases;
                if (refCount < 0) refCount = 0;
                _cache[pending.Path] = new Entry { Asset = asset, RefCount = refCount, Bytes = bytes, Backend = owner };
                _cachedBytes += bytes;
                Touch(pending.Path);
                EnforceWatermark();
            }
            else
            {
                Game.Logger?.Error("Resource", $"加载失败：{pending.Path}");
            }

            foreach (var cb in pending.Callbacks)
            {
                try
                {
                    cb?.Invoke(asset);
                }
                catch (Exception e)
                {
                    Game.Logger?.Error("Resource", $"加载回调异常（{pending.Path}）：{e.Message}", e);
                }
            }

            // 进度只在**成功**时报 1：失败时同样报 100% 会让业务把"加载失败"当成"加载完成"。
            // 失败信号由上面完成回调的 null 参数给出。
            if (asset == null) return;
            foreach (var pc in pending.ProgressCallbacks)
            {
                try
                {
                    pc?.Invoke(1f);
                }
                catch (Exception e)
                {
                    Game.Logger?.Warn("Resource", $"进度回调异常（{pending.Path}）：{e.Message}");
                }
            }
        }

        // ─────────────────────────── 引用计数 / 水位 ───────────────────────────

        /// <inheritdoc/>
        public void Release(string path)
        {
            if (string.IsNullOrEmpty(path)) return;

            if (_cache.TryGetValue(path, out var entry))
            {
                // 只降计数，不移除：是否真正释放交给水位 + LRU（见类注释的第 1 条）
                if (entry.RefCount > 0) entry.RefCount--;
                return;
            }

            if (_inflight.TryGetValue(path, out var pending))
            {
                // 加载在途（尚未入缓存）：登记这次释放，完成时从引用计数里扣掉。
                // 直接忽略会让「先 Release 再等回调」的调用者永远无法归还引用 —— 条目完成时计数恒 ≥ 1，
                // 水位/LRU 再也回收不掉。
                pending.Releases++;
                return;
            }

            // 既不在缓存、也不在途：加载从未发生或早已被淘汰 —— 属于引用配对失衡，留痕便于排查
            Game.Logger?.Warn("Resource", $"Release 找不到对应加载：{path}（是否重复 Release / 路径不一致）");
        }

        /// <inheritdoc/>
        public void UnloadAll()
        {
            // 只清「无人引用」的条目；有引用的留着，强行清会让业务手里的对象变成假 null
            var keys = new List<string>(_cache.Keys);
            foreach (var key in keys)
            {
                if (_cache.TryGetValue(key, out var entry) && entry.RefCount <= 0)
                    Evict(key);
            }

            // 仍有被引用条目或在途加载时**跳过后端整体卸载**：后端 UnloadAll 会 Unload 全部包并清空包表，
            // 把还在用的资源所在包一起拆掉，重开同名包后旧条目的淘汰还会对新实例错误递减引用计数。
            // 安全侧优先：宁可这次不释放（与水位淘汰「宁可持续超标」同一取舍），并明确告警。
            var referenced = 0;
            foreach (var entry in _cache.Values)
            {
                if (entry.RefCount > 0) referenced++;
            }
            if (referenced > 0 || _inflight.Count > 0)
            {
                Game.Logger?.Warn("Resource",
                    $"UnloadAll 跳过后端卸载：仍有 {referenced} 个被引用条目 / {_inflight.Count} 个在途加载；" +
                    "请检查业务是否漏调 Game.Res.Release");
                return;
            }

            _backend.UnloadAll();
        }

        /// <inheritdoc/>
        public long CachedBytes => _cachedBytes;

        /// <inheritdoc/>
        public long CacheWatermark
        {
            get => _watermark;
            set
            {
                _watermark = value;
                _watermarkWarned = false;
                EnforceWatermark();
            }
        }

        /// <summary>缓存条目数（调试面板用）。</summary>
        public int CachedCount => _cache.Count;

        /// <summary>在途加载数（调试面板用）。</summary>
        public int InflightCount => _inflight.Count;

        /// <summary>当前后端名（日志与调试面板用）。</summary>
        public string BackendName => _backend?.Name ?? "(none)";

        /// <summary>已打开的 AssetBundle 包数（非 Bundle 后端恒为 0，调试面板用）。</summary>
        public int OpenBundleCount => (_backend as AssetBundleBackend)?.OpenBundleCount ?? 0;

        /// <summary>下载进度；未在下载时为 null（调试面板用）。</summary>
        public ResourceUpdateProgress DownloadProgress => _updater?.IsDownloading == true ? _updater.Progress : null;

        private void Touch(string key)
        {
            if (_lruNodes.TryGetValue(key, out var node))
            {
                // 复用节点：LinkedList 没有「移动节点」的 API，Remove + AddFirst(同一节点) 即可，
                // 且不会像「Remove + AddFirst(key)」那样每次命中都新建一个 LinkedListNode（高频命中下持续产生 GC）。
                _lru.Remove(node);
                _lru.AddFirst(node);
                return;
            }

            _lruNodes[key] = _lru.AddFirst(key);
        }

        private void Evict(string key)
        {
            // 按「加载该条目时用的后端」配对释放：运行中切过后端也不能用新后端去释放旧条目
            var backend = _backend;
            if (_cache.TryGetValue(key, out var entry))
            {
                backend = entry.Backend ?? _backend;
                _cachedBytes -= entry.Bytes;
                if (_cachedBytes < 0) _cachedBytes = 0;
                _cache.Remove(key);
            }

            if (_lruNodes.TryGetValue(key, out var node))
            {
                _lru.Remove(node);
                _lruNodes.Remove(key);
            }

            backend.EndLoad(key);

            // 淘汰只释放了「包 / 加载占用」，资源对象本身要等引擎扫描才会真正回收 ——
            // 水位的意义是控**真实内存**，因此每次淘汰都请求一次回收（同帧内合并为一次、推迟到下一帧，见 ResourceUnload）。
            ResourceUnload.Request();
        }

        /// <summary>
        /// 把缓存压回水位以下：从 LRU 尾部挑**引用计数为 0** 的条目淘汰。
        /// 全部条目都被引用时停止（并只告警一次）—— 宁可持续超标，也不拆业务正在用的资源。
        /// </summary>
        private void EnforceWatermark()
        {
            if (_watermark <= 0 || _cachedBytes <= _watermark) return;

            // 从 LRU 尾部**单次扫描**挑牺牲者（批量淘汰不退化成 O(n²)）。
            // 淘汰过程不回调业务代码，引用计数不会中途变化，因此一次遍历的判定始终有效。
            var node = _lru.Last;
            while (node != null && _cachedBytes > _watermark)
            {
                var prev = node.Previous;
                if (_cache.TryGetValue(node.Value, out var entry) && entry.RefCount <= 0)
                    Evict(node.Value);
                node = prev;
            }

            if (_cachedBytes > _watermark)
            {
                // 未压下去 = 剩下的是已用完的候选仍未达标（全部条目都在被引用）
                if (!_watermarkWarned)
                {
                    _watermarkWarned = true;
                    Game.Logger?.Warn("Resource",
                        $"缓存 {_cachedBytes / 1024}KB 已超水位 {_watermark / 1024}KB，但所有条目都在被引用，暂不释放；" +
                        "请检查业务是否漏调 Game.Res.Release");
                }
                return;
            }

            _watermarkWarned = false;
        }

        // ─────────────────────────── 预加载 ───────────────────────────

        /// <inheritdoc/>
        public void Preload(List<string> paths, Action onDone, Action<float> progress = null)
        {
            if (paths == null || paths.Count == 0)
            {
                progress?.Invoke(1f);
                onDone?.Invoke();
                return;
            }

            var total = paths.Count;
            var remaining = total;
            foreach (var path in paths)
            {
                LoadAsset<UnityEngine.Object>(path, obj =>
                {
                    // 预热只负责把内容顶进缓存，**不长期持有引用**：Release 自己那一次 +1。
                    // 否则被预热条目 RefCount 恒 ≥ 1，永不满足淘汰条件，水位/LRU 永远回收不了。
                    if (obj != null) Release(path);
                    remaining--;
                    progress?.Invoke((float)(total - remaining) / total);
                    if (remaining <= 0)
                        onDone?.Invoke();
                });
            }
        }

        // ─────────────────────────── 每帧驱动 ───────────────────────────

        /// <inheritdoc/>
        public void Tick(float dt)
        {
            // 1) 在途加载的进度：Unity 的 AsyncOperation 只能轮询，因此进度回调由 Tick 驱动
            for (var i = _activeLoads.Count - 1; i >= 0; i--)
            {
                if (i >= _activeLoads.Count) break;
                var pending = _activeLoads[i];
                var op = pending.Operation;
                if (op == null || pending.ProgressCallbacks.Count == 0) continue;

                var p = op.progress;
                if (Mathf.Approximately(p, pending.LastReportedProgress)) continue;
                pending.LastReportedProgress = p;

                // 倒序遍历 + 每轮重判下标：回调里可能对同一在途路径再 LoadAsset（改动该列表），
                // 正序 foreach 会抛 InvalidOperationException 并穿出 Tick、打断整帧。
                for (var j = pending.ProgressCallbacks.Count - 1; j >= 0; j--)
                {
                    if (j >= pending.ProgressCallbacks.Count) continue;
                    var pc = pending.ProgressCallbacks[j];
                    try
                    {
                        pc?.Invoke(p);
                    }
                    catch (Exception e)
                    {
                        Game.Logger?.Warn("Resource", $"进度回调异常（{pending.Path}）：{e.Message}");
                    }
                }
            }

            // 2) 热更下载泵
            _updater?.Tick();
        }

        // ─────────────────────────── 热更 ───────────────────────────

        /// <inheritdoc/>
        public string Version => _updater?.LocalVersion ?? "0";

        /// <inheritdoc/>
        public bool IsBundleMode => _backend is AssetBundleBackend;

        /// <inheritdoc/>
        public ResourceUpdateState UpdateState => _updater?.State ?? ResourceUpdateState.Idle;

        /// <inheritdoc/>
        public string ContentDir => _contentDir;

        /// <inheritdoc/>
        public void CheckUpdate(Action<ResourceUpdateInfo> onResult)
        {
            if (_updater == null)
            {
                onResult?.Invoke(new ResourceUpdateInfo
                {
                    LocalVersion = Version,
                    Error = "未启用热更（CloverRes.Init 未配置 ManifestUrl）",
                });
                return;
            }

            _updater.Check(onResult);
        }

        /// <inheritdoc/>
        public void DownloadUpdate(ResourceUpdateInfo info, Action<ResourceUpdateProgress> onProgress,
            Action<bool, string> onDone)
        {
            if (_updater == null)
            {
                onDone?.Invoke(false, "未启用热更（CloverRes.Init 未配置 ManifestUrl）");
                return;
            }

            _updater.Download(info, onProgress, (ok, error) =>
            {
                if (ok && !ApplyDownloadedContent(out var applyError))
                {
                    // 下载已落盘、校验已通过，但本进程切不到 Bundle 后端：
                    // 不能把整体判定为成功（否则业务认为热更已生效），UpdateState 也要从 Ready 回退。
                    _updater.RollbackStateAfterApplyFailure();
                    onDone?.Invoke(false, applyError);
                    return;
                }

                onDone?.Invoke(ok, error);
            });
        }

        /// <inheritdoc/>
        public void ClearDownloaded()
        {
            if (_updater == null)
            {
                Game.Logger?.Warn("Resource", "未启用热更，无需清理");
                return;
            }

            _updater.ClearDownloaded(out var error);
            if (error != null)
                Game.Logger?.Error("Resource", error);
        }

        /// <inheritdoc/>
        public void CancelUpdate()
        {
            if (_updater == null)
            {
                // 未启用热更 = 没有在途请求：留痕但按"无需动作"处理（不当作错误）
                Game.Logger?.Warn("Resource", "未启用热更，无需取消");
                return;
            }

            _updater.CancelDownload();
        }

        /// <summary>
        /// 下载完成后把后端从「首包 Resources 兜底」切到 AssetBundle。
        /// <para>
        /// 只在**当前还不是 AssetBundle 后端**时切换，也就是「首次安装」这条路径；
        /// 版本之间的升级不在运行中切换 —— 正在被使用的资源在脚下被换掉会引出难查的空引用，
        /// 因此新版本在下次启动生效（下载完成时业务侧提示玩家重启即可）。
        /// </para>
        /// </summary>
        private bool ApplyDownloadedContent(out string error)
        {
            error = null;
            var manifest = _updater?.LocalManifest;
            if (manifest == null)
            {
                error = "下载完成但本地清单缺失，无法切换到 AssetBundle 后端";
                Game.Logger?.Error("Resource", error);
                return false;
            }
            if (_backend is AssetBundleBackend) return true;

            var backend = new AssetBundleBackend(_contentDir, manifest, _builtinDir);
            if (!backend.Ready(out var readyError))
            {
                error = $"下载已完成但 Bundle 后端不可用（{readyError}）";
                Game.Logger?.Error("Resource", error + "，继续用首包 Resources");
                return false;
            }

            // 已缓存的条目留着：它们是有效对象，且仍被业务引用；缓存以路径为键，
            // 之后同路径命中缓存、新路径走新后端，语义一致。
            // 旧条目的淘汰按 Entry.Backend 配对释放（见 Evict），不会用新后端错误递减包引用。
            _backend = backend;

            // Exists 的答案缓存必须跟着后端一起换：首包 Resources 里没有的路径，热更包里可能有
            // （反之亦然）。不清空的话，「新后端里有、首包里没有」的路径会被旧的 false 永久蒙住。
            _existsCache.Clear();
            Game.Logger?.Info("Resource", $"已切换到 AssetBundle 后端（版本 {manifest.Version}）");
            return true;
        }
    }
}
