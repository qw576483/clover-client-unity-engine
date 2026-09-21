using System;
using System.IO;
using UnityEngine;

namespace CloverEngine
{
    /// <summary>
    /// 资源加载后端契约：把「资源从哪来」从资源管理器里剥出来。
    /// <para>
    /// 存在理由：资源管理器负责的是**缓存 / 引用计数 / 水位淘汰 / 热更编排**，
    /// 而「从 Resources 读」还是「从 AssetBundle 读」是另一维度的变化。
    /// 两者混在一个类里时（改造前就是这样），要加一种后端就得动缓存的代码 —— 那是最不该被牵连的部分。
    /// </para>
    /// <para>
    /// 约束：<b>调用与回调都必须发生在主线程</b>。Unity 的资源 API 不线程安全；
    /// 且加载完成由 <c>AsyncOperation.completed</c> 触发，它本身就在主线程。
    /// 因此实现里**不要**把回调丢到线程池或 Dispatcher 队列 —— 那会让「同帧完成」变成「下一帧才完成」，
    /// 还会给资源管理器引入不必要的重入时序问题。
    /// </para>
    /// <para>
    /// 另注意：<see cref="BeginLoad"/> 在路径非法等场景下会**同步回调** <c>onDone(null)</c>。
    /// 因此调用方必须在调用它**之前**就把自己的状态登记好，否则会漏处理一次即时完成。
    /// </para>
    /// </summary>
    internal interface IResourceBackend
    {
        /// <summary>后端名（日志与调试面板用）。</summary>
        string Name { get; }

        /// <summary>
        /// 启动期自检：内容与元数据是否就绪。返回 false 时 <paramref name="error"/> 给出原因。
        /// 资源管理器在自检失败时会**明确报错并回退**，而不是留到第一次加载才报 null。
        /// </summary>
        bool Ready(out string error);

        /// <summary>
        /// 发起一次资源加载。
        /// </summary>
        /// <param name="path">业务加载路径。</param>
        /// <param name="type">期望的资源类型。</param>
        /// <param name="onDone">资源可用回调（可能同步触发，也可能稍后触发）；失败时参数为 null。</param>
        /// <returns>
        /// 可用于轮询进度的操作句柄；无法提供进度时返回 null（调用方按「立即完成」处理）。
        /// </returns>
        AsyncOperation BeginLoad(string path, Type type, Action<UnityEngine.Object> onDone);

        /// <summary>释放一次加载占用（后端内部的包/文件引用计数）。与 <see cref="BeginLoad"/> 一一对应。</summary>
        void EndLoad(string path);

        /// <summary>卸载后端持有的全部内容（切场景清理 / Shutdown）。</summary>
        void UnloadAll();

        /// <summary>估算某资源占用的字节数（水位决策用）。无法估算时返回一个保守的小值而非 0。</summary>
        int EstimateBytes(string path, UnityEngine.Object asset);

        /// <summary>
        /// **同步**回答「这条路径在可加载源里存不存在」。<b>不是一次加载</b>：
        /// 不占上层缓存、不动引用计数（上层 <c>ResourceManager.Exists</c> 会按路径缓存结果）。
        /// <para>
        /// 有索引的后端查索引（不读盘）；没有索引的后端允许降级为**一次同步探测**，
        /// 此时**可能**把对象读进 Unity 自己的内存（Unity 的 <c>Resources</c> 没有「只问不取」的 API）
        /// —— 这是**可接受的降级**，但必须在实现里写明，别让后人以为它是零成本。
        /// </para>
        /// </summary>
        /// <param name="path">业务加载路径。</param>
        /// <param name="type">期望类型；传 <c>typeof(UnityEngine.Object)</c> 表示「任意类型都算存在」。</param>
        bool Exists(string path, Type type);

        /// <summary>
        /// **同步**取一个路径下的全部资源（条带 / 图集的子 sprite 只有整条取才拿得到）。
        /// <para>
        /// 与 <see cref="BeginLoad"/> 不同，它**会加载**且**同步返回**；也与上层缓存无关
        /// （不进缓存、不动引用计数，调用方自己持有对象）。
        /// </para>
        /// <para>
        /// 后端做不到（例如 AssetBundle 清单里没有「路径 → 包内全部资源」的映射）⇒
        /// <b>返回空数组 + Warn 一次</b>；⛔ 不许抛异常、不许静默。
        /// </para>
        /// </summary>
        T[] LoadAll<T>(string path) where T : UnityEngine.Object;
    }

    /// <summary>
    /// 清单文件名的路径安全工具：清单来自远端，其中的文件名**不可信**。
    /// 直接 <c>Path.Combine(root, name)</c> 时，形如 <c>../../x</c> 或绝对路径的条目
    /// 会让下载/删除/读取越出内容目录（zip slip），因此所有「清单名 → 本地路径」的拼接都必须过这里。
    /// </summary>
    internal static class ResourcePaths
    {
        /// <summary>
        /// 把清单里的相对文件名安全地拼到根目录下。绝对路径、含「..」段、含冒号的名字一律拒绝（返回 false）。
        /// </summary>
        public static bool TryCombine(string root, string name, out string path)
        {
            path = null;
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(name)) return false;
            if (Path.IsPathRooted(name)) return false;
            if (name.IndexOf(':') >= 0) return false; // Windows 盘符 / URI scheme 一律拒绝

            foreach (var seg in name.Split('/', '\\'))
            {
                if (seg == "..") return false; // 任何一段是「..」都会越出内容目录
            }

            path = Path.Combine(root, name);
            return true;
        }

        /// <summary>
        /// 把清单里的文件名转义为 URL 的路径部分（逐段转义，保留 '/' 分隔）：
        /// 包名含空格、<c>#</c>、<c>?</c> 或中文时，直拼会让请求地址被截断或非法。
        /// </summary>
        public static string EscapeUrlPath(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;

            var parts = name.Split('/');
            for (var i = 0; i < parts.Length; i++)
                parts[i] = Uri.EscapeDataString(parts[i]);
            return string.Join("/", parts);
        }
    }

    /// <summary>
    /// 「回收未使用资源」（<c>Resources.UnloadUnusedAssets</c>）的统一触发点。
    /// 它是一次全量对象与资源扫描，直接在关包/切场景/关引擎的关键帧里同步执行会叠出可见卡顿，
    /// 因此**推迟到下一帧**执行（经 <c>Game.Dispatcher</c>），并把同一帧内的多次请求合并为一次；
    /// 无 Dispatcher（引擎未启动 / 测试宿主）时退回同步执行，保证语义不变。
    /// </summary>
    internal static class ResourceUnload
    {
        private static IDispatcher _queuedOn;

        public static void Request()
        {
            var dispatcher = Game.Dispatcher;
            if (dispatcher == null)
            {
                Resources.UnloadUnusedAssets();
                return;
            }

            // 同一派发器上只排一次；回调里复位。
            // 若回调因关引擎没跑到（队列不再排空），下次 Launch 会换新派发器，判定自然失效、可重新排队。
            if (ReferenceEquals(_queuedOn, dispatcher)) return;
            _queuedOn = dispatcher;
            dispatcher.Post(() =>
            {
                _queuedOn = null;
                Resources.UnloadUnusedAssets();
            });
        }
    }

    /// <summary>
    /// 内存占用的统一估算。**这是估算值，不是精确统计** —— 用途只有一个：
    /// 让「缓存该不该淘汰」有一个跟资源真实体量挂钩的判据（而不是只看条数）。
    /// Unity 的 <c>Profiler.GetRuntimeMemorySizeLong</c> 在非 Development Build 里会返回 0，
    /// 因此这里按类型自己算，保证任何构建模式下都有可用数值。
    /// </summary>
    internal static class ResourceSize
    {
        /// <summary>无法归类的资源的保守估值（预制体、ScriptableObject 等）。</summary>
        private const int FallbackBytes = 16 * 1024;

        /// <summary>估算单个资源对象的字节数。</summary>
        public static int Estimate(UnityEngine.Object asset)
        {
            switch (asset)
            {
                case null:
                    return 0;
                case Texture2D tex:
                    return Math.Max(1, tex.width * tex.height * 4);
                case RenderTexture rt:
                    return Math.Max(1, rt.width * rt.height * 4);
                case Sprite sprite:
                    return sprite.texture != null ? Estimate(sprite.texture) : 4 * 1024;
                case AudioClip clip:
                    // 16bit 单/多声道：采样点数 × 声道数 × 2 字节
                    return (int)Math.Max(1, clip.samples * clip.channels * 2L);
                case Mesh mesh:
                    return Math.Max(1, mesh.vertexCount * 32);
                case TextAsset text:
                    return Math.Max(1, text.bytes != null ? text.bytes.Length : text.text.Length * 2);
                default:
                    return FallbackBytes;
            }
        }
    }

    /// <summary>
    /// Unity 内置 <c>Resources</c> 后端：引擎默认后端，零配置、零外部产物。
    /// <para>
    /// 保留它的意义不只是兼容：它是**首包兜底** —— 即使 AssetBundle 后端因为清单缺失、
    /// 下载目录损坏而不可用，Resources 仍能让工程跑起来（开发期尤其重要）。
    /// </para>
    /// </summary>
    internal sealed class ResourcesBackend : IResourceBackend
    {
        private readonly string _root;

        /// <summary>构造 Resources 后端。</summary>
        /// <param name="root">Resources 根前缀（可用空串表示直接以 Resources 根为根）。</param>
        public ResourcesBackend(string root)
        {
            _root = root ?? string.Empty;
        }

        /// <inheritdoc/>
        public string Name => "Resources";

        /// <inheritdoc/>
        public bool Ready(out string error)
        {
            // Resources 不需要任何外部元数据：目录不存在只是「什么都加载不到」，
            // 不是配置错误，因此这里恒通过。
            error = null;
            return true;
        }

        /// <summary>
        /// 业务加载路径 → Unity <c>Resources</c> 全路径（拼上根前缀）。
        /// 拼法与改造前 <see cref="BeginLoad"/> 里那一行**逐字一致**（含空路径的情形）⇒ 行为零变化；
        /// 两个同步入口与它共用，避免两处拼法漂移。
        /// </summary>
        private string Full(string path)
        {
            return string.IsNullOrEmpty(_root) ? path : _root.TrimEnd('/') + "/" + path;
        }

        /// <inheritdoc/>
        public AsyncOperation BeginLoad(string path, Type type, Action<UnityEngine.Object> onDone)
        {
            var full = Full(path);
            var req = Resources.LoadAsync(full, type);
            if (req == null)
            {
                // 路径非法（如包含 ".."、绝对路径）时 Unity 直接返回 null。
                // 必须在这里就回调，否则上层的进度轮询会永远等一个不存在的请求。
                Game.Logger?.Error("Resource", $"invalid resources path: {full}");
                onDone?.Invoke(null);
                return null;
            }

            // ★ 实测修复：若该资源在本帧已经被同步 Resources.Load 取过，
            //   Resources.LoadAsync 会「立刻完成」，此时 completed 事件**不会再触发**
            //   ⇒ 上游 ResourceManager 的 pending 会永远卡在 _inflight，该路径此后一直加载不出来
            //   （表现为：一次进图先取了占位/同步资源，之后异步加载再也不回调 —— 画面永远是占位色块）。
            //   实测复现与原始输出见项目侧 `client/_dev/p_a11_probe2.cs` / `a11_probe2.txt`。
            if (req.isDone)
            {
                Game.Logger?.Info("Resource", $"already done, callback inline: {full}");
                onDone?.Invoke(req.asset);
                return req;
            }

            req.completed += op => onDone?.Invoke((op as ResourceRequest)?.asset);
            return req;
        }

        /// <inheritdoc/>
        public void EndLoad(string path)
        {
            // Resources 没有「包」这一层，加载占用由 Unity 自己按资源粒度管理，
            // 我们只需在缓存淘汰时不再持有引用即可。
        }

        /// <inheritdoc/>
        public void UnloadAll()
        {
            // 只回收「引擎认为无人引用」的资源：本类已把缓存字典条目清掉，
            // 但业务侧若仍持有对象，UnloadUnusedAssets 不会动它 —— 这是我们要的安全语义。
            // 全量扫描推迟到下一帧（ResourceUnload）：直接同步执行会在关包/切场景帧上叠出可见卡顿。
            ResourceUnload.Request();
        }

        /// <inheritdoc/>
        public int EstimateBytes(string path, UnityEngine.Object asset) => ResourceSize.Estimate(asset);

        /// <inheritdoc/>
        /// <remarks>
        /// <b>降级说明（本后端没有索引）</b>：Unity 的 <c>Resources</c> **没有**「只问不取」的 API，
        /// 因此这里只能**探测**：<c>Resources.Load</c> 一次主资源 + <c>Resources.LoadAll</c> 一次子资源。
        /// 代价与旧写法相同（业务侧以前就是 <c>Resources.Load&lt;Sprite&gt;(path) != null</c> 这么问的），
        /// 且上层（<c>ResourceManager.Exists</c>）**按路径缓存**结果 ⇒ 同一路径一辈子只探这一次。
        /// <para>
        /// 为什么必须补 <c>LoadAll</c> 这一半：条带 / 图集的**子 sprite 按路径取不到**
        /// （<c>Load(path)</c> 返回 null），只有整条 <c>LoadAll</c> 才拿得到 ⇒ 只探主资源会误报「不存在」。
        /// </para>
        /// </remarks>
        public bool Exists(string path, Type type)
        {
            var full = Full(path);
            if (string.IsNullOrEmpty(full)) return false;   // 空路径恒「不存在」，不探测

            if (type == null) type = typeof(UnityEngine.Object);
            try
            {
                if (Resources.Load(full, type) != null) return true;
                var all = Resources.LoadAll(full, type);
                return all != null && all.Length > 0;
            }
            catch (Exception e)
            {
                // 路径非法（含 ".." / 绝对路径）等：按「不存在」回答并留痕，不向上抛（契约：Exists 只回答）
                Game.Logger?.Warn("Resource",
                    $"Exists 探测失败（按不存在处理）：{full}: {e.GetType().Name}: {e.Message}");
                return false;
            }
        }

        /// <inheritdoc/>
        public T[] LoadAll<T>(string path) where T : UnityEngine.Object
        {
            var full = Full(path);
            if (string.IsNullOrEmpty(full)) return Array.Empty<T>();

            try
            {
                var all = Resources.LoadAll<T>(full);
                return all ?? Array.Empty<T>();
            }
            catch (Exception e)
            {
                // 不抛（契约：取不到就是空数组）；异常也不静默 —— 点名路径与类型
                Game.Logger?.Warn("Resource",
                    $"LoadAll<{typeof(T).Name}> 异常（返回空数组）：{full}: {e.GetType().Name}: {e.Message}");
                return Array.Empty<T>();
            }
        }
    }
}
