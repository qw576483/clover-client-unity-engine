using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
#if UNITY_WEBGL || UNITY_ANDROID
using UnityEngine.Networking;
#endif

namespace CloverEngine
{
    /// <summary>
    /// AssetBundle 后端：按清单把「业务加载路径」路由到「包 + 包内资源名」。
    /// <para>
    /// 两个关键取舍（都会在注释里写明原因，避免后来者以为是疏漏）：
    /// </para>
    /// <list type="number">
    ///   <item>
    ///   <b>开包用同步的 <c>AssetBundle.LoadFromFile</c></b>：它只读包头并建立内存映射，**不把整包读进内存**，
    ///   是 Unity 对本地包推荐的 API。代价是首次开包会占用一点主线程时间，但它与包体大小无关，
    ///   而换成异步开包要把「依赖链 + 资源请求」两段异步串起来，复杂度陡增、收益很小。
    ///   </item>
    ///   <item>
    ///   <b>关包用 <c>Unload(false)</c></b>：只释放包自身的序列化数据，**不销毁已加载的 Asset**。
    ///   <c>Unload(true)</c> 能立刻回收内存，但只要引用计数有任何一处不准（业务漏调 Release、
    ///   或同一资源被两条路径缓存），就会把仍在使用的资源destroy 掉，业务侧拿到 Unity 的「假 null」，
    ///   这种 bug 极难定位。因此这里选安全侧：内存由 <see cref="UnloadAll"/> 统一回收。
    ///   </item>
    /// </list>
    /// </summary>
    internal sealed class AssetBundleBackend : IResourceBackend
    {
        /// <summary>已打开的包及其被占用次数。</summary>
        private sealed class LoadedBundle
        {
            public AssetBundle Bundle;
            public int RefCount;
        }

        private readonly string _contentRoot;
        private readonly string _builtinRoot;
        private readonly ResourceManifest _manifest;
        private readonly Dictionary<string, LoadedBundle> _bundles = new(StringComparer.Ordinal);
        private readonly HashSet<string> _missingReported = new(StringComparer.Ordinal);

        /// <summary>已经 Warn 过「不支持 LoadAll」的路径（同一路径只报一次，不刷屏）。</summary>
        private readonly HashSet<string> _loadAllUnsupportedReported = new(StringComparer.Ordinal);

        /// <summary>正在打开中的包名：用于打断清单里的循环依赖（a↔b 会无限递归到栈溢出）。</summary>
        private readonly HashSet<string> _opening = new(StringComparer.Ordinal);
#if (UNITY_ANDROID || UNITY_WEBGL) && !UNITY_EDITOR
        /// <summary>正在异步打开的包 → 合流等待它完成的回调（并发请求同一包时不重复下载）。</summary>
        private readonly Dictionary<string, List<Action<LoadedBundle>>> _openingWaiters = new(StringComparer.Ordinal);
#endif

        /// <summary>构造 AssetBundle 后端。</summary>
        /// <param name="contentRoot">可写内容目录（绝对路径）：热更下载的包所在目录。</param>
        /// <param name="manifest">资源清单（提供 路径→包 映射与依赖表）。</param>
        /// <param name="builtinRoot">
        /// 首包内置目录（绝对路径，可为 null）：内容目录里找不到某包时到这里再找一次。
        /// 用于「首包自带一批资源、热更只补差量」的发布方式。
        /// </param>
        public AssetBundleBackend(string contentRoot, ResourceManifest manifest, string builtinRoot = null)
        {
            _contentRoot = contentRoot;
            _manifest = manifest;
            _builtinRoot = builtinRoot;
        }

        /// <inheritdoc/>
        public string Name => "AssetBundle";

        /// <inheritdoc/>
        public bool Ready(out string error)
        {
            if (_manifest == null)
            {
                error = "资源清单未加载（Bundle 后端无法把加载路径路由到包）";
                return false;
            }
            var hasContent = !string.IsNullOrEmpty(_contentRoot) && Directory.Exists(_contentRoot);
            // 首包目录可能位于**包内**（Android 的 APK / WebGL 的 URL）：包内文件不走真实文件系统，
            // Directory.Exists 恒为 false，这里按「已配置即视为存在」处理，加载时走 UnityWebRequest 分支。
            var hasBuiltin = !string.IsNullOrEmpty(_builtinRoot) &&
                             (Directory.Exists(_builtinRoot) || IsPackageInternalDir(_builtinRoot));
            if (!hasContent && !hasBuiltin)
            {
                error = $"内容目录不存在：{_contentRoot}（首包内置目录也不存在：{_builtinRoot ?? "(未配置)"}）";
                return false;
            }

            error = null;
            return true;
        }

        /// <inheritdoc/>
        public AsyncOperation BeginLoad(string path, Type type, Action<UnityEngine.Object> onDone)
        {
            var entry = _manifest.FindAsset(path);
            if (entry == null || string.IsNullOrEmpty(entry.Bundle))
            {
                // 清单里查不到：说明这个资源没有被打进任何包。**不静默回退到 Resources** ——
                // 那会让「打包漏了」表现为「本地能跑、线上白图」，是最难查的一类问题。
                if (_missingReported.Add(path))
                    Game.Logger?.Error("Resource",
                        $"manifest has no bundle for '{path}'：该资源未被打包（请检查打包清单，勿依赖本地 Resources 兜底）");
                onDone?.Invoke(null);
                return null;
            }

            if (NeedsWebRequestOpen(entry.Bundle))
            {
                // 包内包（Android 的 APK / WebGL）：本地同步开包不可用，只能走 UnityWebRequest 异步开。
                // 本方法不提供进度句柄（返回 null，调用方按「无进度」处理），完成由 onDone 通知。
                EnsureBundleViaWebRequest(entry.Bundle, loaded =>
                {
                    if (loaded == null || loaded.Bundle == null)
                    {
                        onDone?.Invoke(null);
                        return;
                    }
                    StartAssetRequest(loaded, entry, type, onDone);
                });
                return null;
            }

            var opened = EnsureBundle(entry.Bundle);
            if (opened == null || opened.Bundle == null)
            {
                onDone?.Invoke(null);
                return null;
            }

            return StartAssetRequest(opened, entry, type, onDone);
        }

        /// <summary>在已打开的包上发起资源异步加载（含「同帧已完成」守卫与失败清理）。</summary>
        private AsyncOperation StartAssetRequest(LoadedBundle loaded, ResourceAssetEntry entry, Type type,
            Action<UnityEngine.Object> onDone)
        {
            var assetName = entry.ResolveAssetName();
            var req = type != null
                ? loaded.Bundle.LoadAssetAsync(assetName, type)
                : loaded.Bundle.LoadAssetAsync(assetName);

            if (req == null)
            {
                Game.Logger?.Error("Resource", $"LoadAssetAsync returned null: {entry} (in {entry.Bundle})");
                ReleaseBundle(entry.Bundle);
                onDone?.Invoke(null);
                return null;
            }

            // ★ 同 ResourcesBackend（实测修复）：若 LoadAssetAsync 在调用的同一帧已完成，
            //   completed 事件不会再触发 —— 那会让上游 pending 永远卡在 _inflight（该路径此后一直加载不出来）。
            if (req.isDone)
            {
                CompleteAssetRequest(loaded, entry, req, onDone);
                return req;
            }

            req.completed += op => CompleteAssetRequest(loaded, entry, op as AssetBundleRequest, onDone);
            return req;
        }

        /// <summary>资源加载结束：资源名对不上时回收本次包占用，并把结果交给上层。</summary>
        private void CompleteAssetRequest(LoadedBundle loaded, ResourceAssetEntry entry, AssetBundleRequest req,
            Action<UnityEngine.Object> onDone)
        {
            var asset = req?.asset;
            if (asset == null)
            {
                // 包开成功了但资源名对不上：多半是清单里的 asset 字段与实际打包名不一致
                Game.Logger?.Error("Resource",
                    $"asset not found in bundle: {entry}（清单的 asset 名需与打包时一致）");
                ReleaseBundle(entry.Bundle);
            }
            onDone?.Invoke(asset);
        }

        /// <inheritdoc/>
        public void EndLoad(string path)
        {
            var entry = _manifest.FindAsset(path);
            if (entry == null || string.IsNullOrEmpty(entry.Bundle))
            {
                // 缓存条目按清单路由到包，正常不会走到这里；走到说明上层 BeginLoad/EndLoad 已配对错位
                Game.Logger?.Warn("Resource", $"EndLoad 找不到清单映射（Begin/End 配对错位？）：{path}");
                return;
            }
            ReleaseBundle(entry.Bundle);
        }

        /// <inheritdoc/>
        public void UnloadAll()
        {
            foreach (var pair in _bundles)
            {
                if (pair.Value.Bundle != null)
                    pair.Value.Bundle.Unload(false);
            }
            _bundles.Clear();
            // 包已放掉，再让引擎回收「确实没人引用」的资源。
            // 全量扫描推迟到下一帧（ResourceUnload）：同步执行会在 Shutdown / 切场景帧上叠出可见卡顿。
            ResourceUnload.Request();
        }

        /// <inheritdoc/>
        public int EstimateBytes(string path, UnityEngine.Object asset)
        {
            // 契约（IResourceManager.CachedBytes）：AssetBundle 模式按**包体大小**计 ——
            // 包是最小卸载单元，丢一个包就是丢它的全部资源。清单拿不到包信息时才退回单资源估算。
            var entry = _manifest.FindAsset(path);
            var info = entry != null && !string.IsNullOrEmpty(entry.Bundle) ? _manifest.Find(entry.Bundle) : null;
            if (info != null && info.Size > 0)
                return (int)Math.Min(info.Size, int.MaxValue);
            return ResourceSize.Estimate(asset);
        }

        /// <inheritdoc/>
        /// <remarks>
        /// **查清单索引**：清单就是「业务路径 → 包 + 包内资源名」的映射表（含惰性字典索引），
        /// 所以这里**不读盘、不开包、不加载**，纯查表。这正是「有索引走索引」那条路径。
        /// <para>
        /// 边界：清单里有映射 ≠ 包文件真的在磁盘上（热更没下完 / 包损坏）—— 本方法只回答
        /// 「打包清单声明了这条路径」，能不能真加载出来由 <see cref="BeginLoad"/> 那条路负责报错。
        /// </para>
        /// </remarks>
        public bool Exists(string path, Type type)
        {
            if (_manifest == null || string.IsNullOrEmpty(path)) return false;
            var entry = _manifest.FindAsset(path);
            return entry != null && !string.IsNullOrEmpty(entry.Bundle);
        }

        /// <inheritdoc/>
        /// <remarks>
        /// <b>本后端不支持 LoadAll</b>，按契约降级为「空数组 + 只 Warn 一次」。原因（不是偷懒）：
        /// 清单的映射粒度是「一个业务路径 → 包内**一个**资源名」；条带 / 图集的子 sprite 在包里是
        /// **同一个资源对象的子资源**（`AssetBundle` 只能先取到父资源再枚举），清单里没有
        /// 「一个路径 → 包内全部资源」的声明 ⇒ 引擎**不能**凭空猜出该开哪个包、该枚举哪些资源。
        /// <para>
        /// ⛔ 也**不**回退到 Unity 内置 <c>Resources</c> 去猜：那会让「打包漏了」表现为
        /// 「本地能跑、线上白图」（与 <see cref="BeginLoad"/> 对未打包资源的处置同一口径）。
        /// 业务侧要拿结构：让打包端把条带声明进清单，或改用 <see cref="BeginLoad"/> 按完整资源名加载。
        /// </para>
        /// </remarks>
        public T[] LoadAll<T>(string path) where T : UnityEngine.Object
        {
            if (_loadAllUnsupportedReported.Add(path ?? string.Empty))
            {
                Game.Logger?.Warn("Resource",
                    $"AssetBundle 后端不支持 LoadAll<{typeof(T).Name}>({path})：清单没有「路径 → 包内全部资源」"
                    + "的映射，返回空数组；请改用 LoadAsset<T>(完整资源名) 或让打包端声明条带父资源");
            }
            return Array.Empty<T>();
        }

        /// <summary>已打开的包数量（调试面板展示用）。</summary>
        public int OpenBundleCount => _bundles.Count;

        /// <summary>
        /// 确保目标包（含其依赖）已打开，并把引用计数 +1。
        /// 依赖先于自身打开：Unity 要求依赖包在被依赖者之前加载，否则资源里的引用会解析失败。
        /// </summary>
        private LoadedBundle EnsureBundle(string name)
        {
            if (_bundles.TryGetValue(name, out var existing) && existing.Bundle != null)
            {
                existing.RefCount++;
                return existing;
            }

            if (_opening.Contains(name))
            {
                // 清单成环（a 依赖 b、b 又依赖 a）：不拦会无限递归到栈溢出
                Game.Logger?.Error("Resource", $"清单存在循环依赖，已中断打开：{name}");
                return null;
            }

            var info = _manifest.Find(name);
            if (info == null)
            {
                Game.Logger?.Error("Resource", $"bundle not in manifest: {name}");
                return null;
            }

            if (info.IsRaw)
            {
                Game.Logger?.Error("Resource", $"'{name}' 是裸文件，不能作为 AssetBundle 打开（清单 raw 标记有误）");
                return null;
            }

            _opening.Add(name);
            try
            {
                // 依赖先开且**任一失败即整体失败**：继续开自身会让父包内的跨包引用解析不到
                // （表现为材质变紫 / 丢贴图），且不重试不上报，问题被推迟到美术表现上。
                var openedDeps = OpenDependencies(name, info);
                if (openedDeps == null)
                {
                    Game.Logger?.Error("Resource", $"依赖包未能全部打开，放弃打开 {name}");
                    return null;
                }

                var bundle = LoadBundleFile(name);
                if (bundle == null)
                {
                    // 自身失败：刚为本次打开占用的依赖引用必须配平释放，否则永久悬挂、无法卸载
                    ReleaseDependencies(openedDeps);
                    return null;
                }

                var loaded = new LoadedBundle { Bundle = bundle, RefCount = 1 };
                _bundles[name] = loaded;
                return loaded;
            }
            finally
            {
                _opening.Remove(name);
            }
        }

        /// <summary>
        /// 打开依赖列表并把它们各占用 +1；**任一依赖失败即释放已打开的部分并返回 null**（调用方据此整体放弃）。
        /// </summary>
        private List<string> OpenDependencies(string name, ResourceFileInfo info)
        {
            var deps = info.Deps;
            if (deps == null || deps.Length == 0) return new List<string>();

            var opened = new List<string>(deps.Length);
            foreach (var dep in deps)
            {
                if (string.IsNullOrEmpty(dep)) continue;
                if (EnsureBundle(dep) == null)
                {
                    Game.Logger?.Error("Resource", $"依赖包打开失败：{dep}（被 {name} 依赖）");
                    ReleaseDependencies(opened);
                    return null;
                }
                opened.Add(dep);
            }
            return opened;
        }

        /// <summary>按配平释放一批依赖占用。</summary>
        private void ReleaseDependencies(List<string> deps)
        {
            if (deps == null) return;
            foreach (var dep in deps)
                ReleaseBundle(dep);
        }

        /// <summary>从本地真实文件系统打开一个包（可写内容目录 / 首包目录）。</summary>
        private AssetBundle LoadBundleFile(string name)
        {
            var file = ResolveBundleFile(name);
            if (string.IsNullOrEmpty(file))
            {
                Game.Logger?.Error("Resource", $"清单文件名不合法（含越界路径段，已拒绝）：{name}");
                return null;
            }

            AssetBundle bundle;
            try
            {
                bundle = AssetBundle.LoadFromFile(file);
            }
            catch (Exception e)
            {
                Game.Logger?.Error("Resource", $"open bundle failed: {file}: {e.Message}", e);
                return null;
            }

            if (bundle == null)
            {
                Game.Logger?.Error("Resource",
                    $"open bundle failed: {file}（文件缺失或与当前 Unity 版本不兼容；请检查热更是否下载完整）");
                return null;
            }
            return bundle;
        }

        /// <summary>
        /// 定位包文件：先看可写内容目录（热更下载的位置），再看首包内置目录。
        /// 两处都没有时返回可写目录下的路径 —— 让报错指向「本该下载到这里」，
        /// 而不是指向一个只读的首包目录，避免误导排查方向。
        /// 清单文件名不可信（可能含 <c>..</c> / 绝对路径）：拼接统一走 <see cref="ResourcePaths"/>，非法名返回 null。
        /// </summary>
        private string ResolveBundleFile(string name)
        {
            var root = !string.IsNullOrEmpty(_contentRoot) ? _contentRoot : _builtinRoot;
            if (string.IsNullOrEmpty(root)) return null;
            if (!ResourcePaths.TryCombine(root, name, out var fallback)) return null;

            if (!string.IsNullOrEmpty(_contentRoot) &&
                ResourcePaths.TryCombine(_contentRoot, name, out var downloaded) && File.Exists(downloaded))
                return downloaded;

            if (!string.IsNullOrEmpty(_builtinRoot) &&
                ResourcePaths.TryCombine(_builtinRoot, name, out var builtin) && File.Exists(builtin))
                return builtin;

            return fallback;
        }

        /// <summary>
        /// 释放一次包占用；计数归零则关包，并**级联释放它带来的依赖占用**
        /// （依赖的计数是在 <see cref="EnsureBundle"/> 里为「被依赖」而 +1 的，必须配平）。
        /// </summary>
        private void ReleaseBundle(string name)
        {
            if (!_bundles.TryGetValue(name, out var loaded))
            {
                // 释放一个并未打开的包 = 引用计数失衡，留痕（运行期换后端 / UnloadAll 之后最容易出现）
                Game.Logger?.Warn("Resource", $"ReleaseBundle 找不到已打开的包（引用失衡？）：{name}");
                return;
            }

            loaded.RefCount--;
            if (loaded.RefCount > 0) return;

            if (loaded.Bundle != null)
                loaded.Bundle.Unload(false);
            _bundles.Remove(name);

            var info = _manifest.Find(name);
            if (info?.Deps == null) return;
            foreach (var dep in info.Deps)
            {
                if (!string.IsNullOrEmpty(dep))
                    ReleaseBundle(dep);
            }
        }

        // ─────────────────── 包内包（Android 的 APK / WebGL）的异步开包 ───────────────────

        /// <summary>
        /// 该包是否只能经 UnityWebRequest 打开：包内位置不走真实文件系统，
        /// <c>AssetBundle.LoadFromFile</c> 不可用（WebGL 全部包；Android 仅在包内、且可写目录没有本地拷贝时）。
        /// </summary>
        private bool NeedsWebRequestOpen(string name)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return true;
#elif UNITY_ANDROID && !UNITY_EDITOR
            if (!string.IsNullOrEmpty(_contentRoot) && File.Exists(Path.Combine(_contentRoot, name))) return false;
            return IsPackageInternalDir(_builtinRoot);
#else
            return false;
#endif
        }

        /// <summary>目录是否位于「包内」不可用 System.IO 访问的位置（Android 的 APK / WebGL 的 URL）。</summary>
        private static bool IsPackageInternalDir(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return false;
#if (UNITY_ANDROID || UNITY_WEBGL) && !UNITY_EDITOR
            var streamingAssets = Application.streamingAssetsPath;
            return !string.IsNullOrEmpty(streamingAssets) &&
                   dir.StartsWith(streamingAssets, StringComparison.Ordinal);
#else
            return false;
#endif
        }

        /// <summary>
        /// 经 UnityWebRequest 异步打开「包内包」：依赖先开（同样走异步路径），任一失败即整体失败并回收已打开的依赖。
        /// <para>
        /// 同一包已有其它请求在打开时**合流等待**（不重复下载）；<c>chain</c> 是祖先链，专用于识别清单成环
        /// （真成环时在链上命中，立刻失败，不会互相等待成死锁）。
        /// </para>
        /// </summary>
        private void EnsureBundleViaWebRequest(string name, Action<LoadedBundle> onReady)
        {
            EnsureBundleViaWebRequest(name, null, onReady);
        }

        private void EnsureBundleViaWebRequest(string name, List<string> chain, Action<LoadedBundle> onReady)
        {
#if !((UNITY_ANDROID || UNITY_WEBGL) && !UNITY_EDITOR)
            // 桌面 / 编辑器平台：包内包路径不适用（NeedsWebRequestOpen 恒 false），防御性拒绝。
            Game.Logger?.Error("Resource", $"该平台不支持经 UnityWebRequest 打开包：{name}");
            onReady?.Invoke(null);
#else
            if (_bundles.TryGetValue(name, out var existing) && existing.Bundle != null)
            {
                existing.RefCount++;
                onReady?.Invoke(existing);
                return;
            }

            if (chain != null && chain.Contains(name))
            {
                // 真成环（a 依赖 b、b 又依赖 a）：不拦会互相等待 / 无限递归
                Game.Logger?.Error("Resource", $"清单存在循环依赖，已中断打开：{name}");
                onReady?.Invoke(null);
                return;
            }

            if (_opening.Contains(name))
            {
                // 并发请求已在打开同一个包：合流等待（完成时按等待者数量补引用计数）
                if (!_openingWaiters.TryGetValue(name, out var waiters))
                    _openingWaiters[name] = waiters = new List<Action<LoadedBundle>>();
                waiters.Add(onReady);
                return;
            }

            var info = _manifest.Find(name);
            if (info == null)
            {
                Game.Logger?.Error("Resource", $"bundle not in manifest: {name}");
                onReady?.Invoke(null);
                return;
            }

            if (info.IsRaw)
            {
                Game.Logger?.Error("Resource", $"'{name}' 是裸文件，不能作为 AssetBundle 打开（清单 raw 标记有误）");
                onReady?.Invoke(null);
                return;
            }

            _opening.Add(name);
            var innerChain = new List<string>(chain?.Count + 1 ?? 1);
            if (chain != null) innerChain.AddRange(chain);
            innerChain.Add(name);

            var openedDeps = new List<string>();
            OpenDependenciesViaWebRequest(name, info.Deps, 0, openedDeps, innerChain, depsOk =>
            {
                if (!depsOk)
                {
                    _opening.Remove(name);
                    Game.Logger?.Error("Resource", $"依赖包未能全部打开，放弃打开 {name}");
                    onReady?.Invoke(null);
                    CompleteWebOpen(name, null);
                    return;
                }

                FetchBundleFromWeb(name, bundle =>
                {
                    _opening.Remove(name);
                    if (bundle == null)
                    {
                        ReleaseDependencies(openedDeps);
                        onReady?.Invoke(null);
                        CompleteWebOpen(name, null);
                        return;
                    }

                    var loaded = new LoadedBundle { Bundle = bundle, RefCount = 1 };
                    _bundles[name] = loaded;
                    onReady?.Invoke(loaded);
                    CompleteWebOpen(name, loaded);
                });
            });
#endif
        }

#if (UNITY_ANDROID || UNITY_WEBGL) && !UNITY_EDITOR
        /// <summary>包内包的读取地址：首包目录（位于 StreamingAssets 时）或 StreamingAssets 根 + 包名。</summary>
        private string WebRequestUrl(string name)
        {
            var root = IsPackageInternalDir(_builtinRoot) ? _builtinRoot : Application.streamingAssetsPath;
            return (root ?? string.Empty).TrimEnd('/') + "/" + ResourcePaths.EscapeUrlPath(name);
        }

        /// <summary>串行打开依赖（异步路径版，chain 透传用于识别成环）；任一失败释放已打开的部分并回调 false。</summary>
        private void OpenDependenciesViaWebRequest(string name, string[] deps, int index, List<string> opened,
            List<string> chain, Action<bool> onDone)
        {
            if (deps == null || index >= deps.Length)
            {
                onDone(true);
                return;
            }

            var dep = deps[index];
            if (string.IsNullOrEmpty(dep))
            {
                OpenDependenciesViaWebRequest(name, deps, index + 1, opened, chain, onDone);
                return;
            }

            EnsureBundleViaWebRequest(dep, chain, loaded =>
            {
                if (loaded == null)
                {
                    Game.Logger?.Error("Resource", $"依赖包打开失败：{dep}（被 {name} 依赖）");
                    ReleaseDependencies(opened);
                    onDone(false);
                    return;
                }
                opened.Add(dep);
                OpenDependenciesViaWebRequest(name, deps, index + 1, opened, chain, onDone);
            });
        }

        /// <summary>结束一次异步开包：把结果分发给该包的全部合流等待者（成功时每个等待者补一次包引用占用）。</summary>
        private void CompleteWebOpen(string name, LoadedBundle loaded)
        {
            if (!_openingWaiters.TryGetValue(name, out var waiters)) return;
            _openingWaiters.Remove(name);

            var ok = loaded != null && loaded.Bundle != null;
            foreach (var waiter in waiters)
            {
                if (ok) loaded.RefCount++;
                try
                {
                    waiter?.Invoke(ok ? loaded : null);
                }
                catch (Exception e)
                {
                    Game.Logger?.Error("Resource", $"开包合流回调异常（{name}）：{e.Message}", e);
                }
            }
        }

        /// <summary>下载并打开一个包内包；结束（含失败）时释放请求对象。</summary>
        private void FetchBundleFromWeb(string name, Action<AssetBundle> onFetched)
        {
            var url = WebRequestUrl(name);
            UnityWebRequest req;
            try
            {
                req = UnityWebRequestAssetBundle.GetAssetBundle(url);
            }
            catch (Exception e)
            {
                Game.Logger?.Error("Resource", $"open bundle (web) failed: {url}: {e.Message}", e);
                onFetched?.Invoke(null);
                return;
            }

            req.SendWebRequest().completed += _ =>
            {
                AssetBundle bundle = null;
                var result = req.result;
                var code = req.responseCode;
                var error = req.error;
                try
                {
                    if (result == UnityWebRequest.Result.Success)
                        bundle = DownloadHandlerAssetBundle.GetContent(req);
                }
                catch (Exception e)
                {
                    Game.Logger?.Error("Resource", $"open bundle (web) failed: {url}: {e.Message}", e);
                }
                finally
                {
                    // 请求已结束才释放（Unity 要求）；取出的 AssetBundle 生命周期独立于请求对象。
                    req.Dispose();
                }

                if (bundle == null)
                    Game.Logger?.Error("Resource",
                        $"open bundle (web) failed: {url}（{result}，HTTP {code} {error}；" +
                        "请检查首包是否包含该包、URL 是否可达）");
                onFetched?.Invoke(bundle);
            };
        }
#endif
    }
}
