using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text;

namespace CloverEngine
{
    // 资源热更相关的**纯数据契约**（Core 层）。
    //
    // 为什么放 Core：`Game.Res` 的类型 `IResourceManager` 在 Core，
    // 而这些结构是它签名的一部分；且做到「Core 不引用 Resource 程序集」的单向依赖。
    // 与 TransportContracts / PresentationContracts / ProtocolContracts 同一模式。
    //
    // 设计要点：
    //   1. 差异比对的**唯一依据是内容哈希**，不看改时间、不看文件名。发布端只要换内容就必须换 hash。
    //   2. 清单同时承载「文件表」与「资源路径 → 包」映射表，因此客户端不需要额外的打包元数据。
    //   3. 配表这类**裸文件**与 AssetBundle 走同一条清单/下载/校验链路，避免两套热更机制。

    /// <summary>
    /// 清单里的一个文件：一个 AssetBundle 包，或一份裸文件（配表 TSV / JSON / 多语言文本等）。
    /// </summary>
    public sealed class ResourceFileInfo
    {
        /// <summary>
        /// 文件名。Bundle 用包名（如 <c>ui_common</c>）；
        /// 裸文件用相对路径（如 <c>config/item.tsv</c>），落地后按该相对路径读取。
        /// </summary>
        public string Name;

        /// <summary>
        /// 内容哈希（推荐 MD5 小写十六进制）。**差异比对与下载后完整性校验的唯一依据**：
        /// 本地同名文件 hash 与清单一致即视为已是最新，不一致才重新下载。
        /// </summary>
        public string Hash;

        /// <summary>文件字节数。用于展示下载总量、以及断点续传时校验本地半成品的偏移是否合理。</summary>
        public long Size;

        /// <summary>依赖的 Bundle 名列表（裸文件恒为空）。加载某个包前会先递归加载其依赖。</summary>
        public string[] Deps = Array.Empty<string>();

        /// <summary>true = 裸文件（直接落地到可读目录）；false = AssetBundle。</summary>
        public bool IsRaw;

        /// <summary>调试与日志用的单行描述。</summary>
        public override string ToString()
            => $"{Name}({(IsRaw ? "raw" : "bundle")}, {Size}B, {Hash})";
    }

    /// <summary>
    /// 「业务加载路径 → 所在包 / 包内资源名」的映射。
    /// 有了它，业务侧依旧只写 <c>Game.Res.LoadAsset&lt;GameObject&gt;("UI/LoginPanel")</c>，
    /// 不必知道这个资源被怎么分包的。
    /// </summary>
    public sealed class ResourceAssetEntry
    {
        /// <summary>业务加载路径（与传给 LoadAsset 的字符串完全一致）。</summary>
        public string Path;

        /// <summary>所在 Bundle 名。裸文件资源可留空。</summary>
        public string Bundle;

        /// <summary>
        /// Bundle 内的资源名（AssetBundle 里资源的 assetName）。
        /// 留空时取 <see cref="Path"/> 最后一段，例如 <c>UI/LoginPanel</c> → <c>LoginPanel</c>。
        /// </summary>
        public string Asset;

        /// <summary>解析出实际要传给 AssetBundle 的资源名。</summary>
        public string ResolveAssetName()
        {
            if (!string.IsNullOrEmpty(Asset)) return Asset;
            if (string.IsNullOrEmpty(Path)) return null;
            var idx = Path.LastIndexOf('/');
            return idx >= 0 && idx + 1 < Path.Length ? Path.Substring(idx + 1) : Path;
        }

        /// <summary>调试与日志用的单行描述。</summary>
        public override string ToString() => $"{Path} -> {Bundle}#{ResolveAssetName()}";
    }

    /// <summary>
    /// 远端资源清单：版本号 + 文件表 + 资源映射表。
    /// 由发布端生成（例如打包脚本产出 <c>manifest.json</c>），客户端拉取后与本地比对。
    /// </summary>
    public sealed class ResourceManifest
    {
        /// <summary>清单版本号（如 <c>1.0.3</c>）。仅用于展示与本地落盘比对，差异判定仍以文件 hash 为准。</summary>
        public string Version;

        /// <summary>下载根地址（如 <c>https://cdn.example.com/rs/1.0.3</c>），拼上文件名即为文件 URL。</summary>
        public string Root;

        /// <summary>
        /// 是否强制更新（由发布端决定）。引擎只解析并透传到 <see cref="ResourceUpdateInfo.Force"/>
        /// 并在检查结果里告警，**不做强制校验** ——「未更新完不能进游戏」的拦截由业务流程实现。缺省 false。
        /// </summary>
        public bool Force;

        // 两个表是**私有** + 只读视图 + 显式追加入口：外部改表必须走 AddFile / AddAsset，
        // 那样才能就地把惰性索引置空。旧实现是 public List 字段、索引只按 Count 判失效 ——
        // 外部在"数量不变"的情况下替换或重排元素时索引不重建，Find/FindAsset 静默返回陈旧条目。
        private readonly List<ResourceFileInfo> _files = new();
        private readonly List<ResourceAssetEntry> _assets = new();
        private readonly ReadOnlyCollection<ResourceFileInfo> _filesView;
        private readonly ReadOnlyCollection<ResourceAssetEntry> _assetsView;

        /// <summary>构造：建立两个只读视图（视图与内部表同寿命，只建一次）。</summary>
        public ResourceManifest()
        {
            _filesView = _files.AsReadOnly();
            _assetsView = _assets.AsReadOnly();
        }

        /// <summary>
        /// 全部文件（Bundle + 裸文件）。**真只读视图**（<c>ReadOnlyCollection</c>）：
        /// 外部拿不到可写的 <see cref="List{T}"/>，因此不存在"绕过 <see cref="AddFile"/> 就地改内容、
        /// 索引却不知道"的路径。追加请用 <see cref="AddFile"/>。
        /// </summary>
        public IReadOnlyList<ResourceFileInfo> Files => _filesView;

        /// <summary>资源路径映射表（**真只读视图**）。追加请用 <see cref="AddAsset"/>。</summary>
        public IReadOnlyList<ResourceAssetEntry> Assets => _assetsView;

        private Dictionary<string, ResourceFileInfo> _fileIndex;
        private Dictionary<string, ResourceAssetEntry> _assetIndex;

        /// <summary>追加一个文件项（唯一入口）：就地让惰性索引失效。</summary>
        public void AddFile(ResourceFileInfo file)
        {
            if (file == null) return;
            _files.Add(file);
            _fileIndex = null;
        }

        /// <summary>追加一条资源映射（唯一入口）：就地让惰性索引失效。</summary>
        public void AddAsset(ResourceAssetEntry asset)
        {
            if (asset == null) return;
            _assets.Add(asset);
            _assetIndex = null;
        }

        /// <summary>
        /// 显式让惰性索引失效（索引会按需重建）。供任何**绕过 <see cref="AddFile"/> /
        /// <see cref="AddAsset"/> 改动内容**的路径调用（如自定义解析器直接操作列表后）。
        /// </summary>
        public void InvalidateIndex()
        {
            _fileIndex = null;
            _assetIndex = null;
        }

        /// <summary>按文件名查文件信息；不存在返回 null。</summary>
        public ResourceFileInfo Find(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            EnsureIndex();
            return _fileIndex.TryGetValue(name, out var info) ? info : null;
        }

        /// <summary>
        /// 按业务加载路径查映射；不存在返回 null。
        /// **注意**：现有唯一调用方（AssetBundleBackend）**不会**回退到默认规则，而是明确报错并让加载失败 ——
        /// 避免「打包漏了」被本地兜底掩盖成「本地能跑、线上白图」。
        /// </summary>
        public ResourceAssetEntry FindAsset(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            EnsureIndex();
            return _assetIndex.TryGetValue(path, out var entry) ? entry : null;
        }

        /// <summary>清单自检：返回问题描述列表，空列表表示通过。供启动期打日志，避免坏清单一上线就是一串莫名报错。</summary>
        public List<string> Validate()
        {
            var issues = new List<string>();
            if (string.IsNullOrEmpty(Version)) issues.Add("version 为空");
            if (string.IsNullOrEmpty(Root)) issues.Add("root 为空（无法下载）");

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var f in Files)
            {
                if (string.IsNullOrEmpty(f.Name)) { issues.Add("存在 name 为空的文件项"); continue; }
                if (!seen.Add(f.Name)) issues.Add($"文件重复：{f.Name}");
                if (string.IsNullOrEmpty(f.Hash)) issues.Add($"{f.Name} 缺少 hash（无法做差异比对与校验）");
                if (f.Size <= 0) issues.Add($"{f.Name} 的 size 非法：{f.Size}");
            }

            foreach (var a in Assets)
            {
                if (string.IsNullOrEmpty(a.Path)) { issues.Add("存在 path 为空的资源映射"); continue; }
                if (!string.IsNullOrEmpty(a.Bundle) && Find(a.Bundle) == null)
                    issues.Add($"资源 {a.Path} 指向不存在的包：{a.Bundle}");
            }

            foreach (var f in Files)
            {
                foreach (var dep in f.Deps)
                {
                    if (Find(dep) == null) issues.Add($"{f.Name} 依赖不存在的包：{dep}");
                }
            }

            return issues;
        }

        /// <summary>
        /// 惰性构建查表索引。**失效判据只有"索引为 null"这一条** —— 由 <see cref="AddFile"/> /
        /// <see cref="AddAsset"/> / <see cref="InvalidateIndex"/> 负责置空。
        /// 不再拿"列表数量是否变化"当判据：数量不变而内容被替换/重排时它判不出来，
        /// 会让 Find / FindAsset 静默返回陈旧条目（那比多算一次 O(n) 昂贵得多）。
        /// </summary>
        private void EnsureIndex()
        {
            if (_fileIndex == null)
            {
                _fileIndex = new Dictionary<string, ResourceFileInfo>(_files.Count, StringComparer.Ordinal);
                foreach (var f in _files)
                {
                    if (f == null || string.IsNullOrEmpty(f.Name)) continue;
                    _fileIndex[f.Name] = f;
                }
            }

            if (_assetIndex == null)
            {
                _assetIndex = new Dictionary<string, ResourceAssetEntry>(_assets.Count, StringComparer.Ordinal);
                foreach (var a in _assets)
                {
                    if (a == null || string.IsNullOrEmpty(a.Path)) continue;
                    _assetIndex[a.Path] = a;
                }
            }
        }

        /// <summary>解析清单 JSON。语法非法时抛 <see cref="FormatException"/>；结构缺失按「缺字段」处理而非抛异常。</summary>
        public static ResourceManifest Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                throw new FormatException("resource manifest is empty");

            var root = MiniJson.Parse(json) as Dictionary<string, object>
                       ?? throw new FormatException("resource manifest must be a JSON object");

            var manifest = new ResourceManifest
            {
                Version = Str(root, "version"),
                Root = Str(root, "root"),
                Force = Flag(root, "force"),
            };

            if (root.TryGetValue("files", out var filesRaw) && filesRaw is List<object> files)
            {
                foreach (var item in files)
                {
                    if (!(item is Dictionary<string, object> d)) continue;
                    manifest.AddFile(new ResourceFileInfo
                    {
                        Name = Str(d, "name"),
                        Hash = Str(d, "hash"),
                        Size = Num(d, "size"),
                        IsRaw = Flag(d, "raw"),
                        Deps = StrArray(d, "deps"),
                    });
                }
            }

            if (root.TryGetValue("assets", out var assetsRaw) && assetsRaw is List<object> assets)
            {
                foreach (var item in assets)
                {
                    if (!(item is Dictionary<string, object> d)) continue;
                    manifest.AddAsset(new ResourceAssetEntry
                    {
                        Path = Str(d, "path"),
                        Bundle = Str(d, "bundle"),
                        Asset = Str(d, "asset"),
                    });
                }
            }

            return manifest;
        }

        /// <summary>序列化为 JSON（用于把远端清单缓存到本地，下次启动可先离线比对）。</summary>
        public string ToJson()
        {
            var sb = new StringBuilder();
            sb.Append("{\"version\":");
            AppendString(sb, Version);
            sb.Append(",\"root\":");
            AppendString(sb, Root);
            if (Force) sb.Append(",\"force\":true");

            sb.Append(",\"files\":[");
            for (var i = 0; i < Files.Count; i++)
            {
                var f = Files[i];
                if (i > 0) sb.Append(',');
                sb.Append("{\"name\":");
                AppendString(sb, f.Name);
                sb.Append(",\"hash\":");
                AppendString(sb, f.Hash);
                sb.Append(",\"size\":").Append(f.Size);
                if (f.IsRaw) sb.Append(",\"raw\":true");
                if (f.Deps != null && f.Deps.Length > 0)
                {
                    sb.Append(",\"deps\":[");
                    for (var j = 0; j < f.Deps.Length; j++)
                    {
                        if (j > 0) sb.Append(',');
                        AppendString(sb, f.Deps[j]);
                    }
                    sb.Append(']');
                }
                sb.Append('}');
            }
            sb.Append(']');

            sb.Append(",\"assets\":[");
            for (var i = 0; i < Assets.Count; i++)
            {
                var a = Assets[i];
                if (i > 0) sb.Append(',');
                sb.Append("{\"path\":");
                AppendString(sb, a.Path);
                sb.Append(",\"bundle\":");
                AppendString(sb, a.Bundle);
                if (!string.IsNullOrEmpty(a.Asset))
                {
                    sb.Append(",\"asset\":");
                    AppendString(sb, a.Asset);
                }
                sb.Append('}');
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private static void AppendString(StringBuilder sb, string value)
        {
            sb.Append(MiniJson.Dump(value ?? string.Empty));
        }

        private static string Str(Dictionary<string, object> d, string key)
            => d.TryGetValue(key, out var v) && v is string s ? s : null;

        private static long Num(Dictionary<string, object> d, string key)
        {
            if (!d.TryGetValue(key, out var v)) return 0;
            switch (v)
            {
                case long l: return l;
                case double db: return (long)db;
                case int i: return i;
                default: return 0;
            }
        }

        private static bool Flag(Dictionary<string, object> d, string key)
            => d.TryGetValue(key, out var v) && v is bool b && b;

        private static string[] StrArray(Dictionary<string, object> d, string key)
        {
            if (!d.TryGetValue(key, out var v) || !(v is List<object> list) || list.Count == 0)
                return Array.Empty<string>();

            var result = new string[list.Count];
            var n = 0;
            foreach (var item in list)
            {
                if (item is string s && !string.IsNullOrEmpty(s))
                    result[n++] = s;
            }
            if (n == result.Length) return result;

            var trimmed = new string[n];
            Array.Copy(result, trimmed, n);
            return trimmed;
        }
    }

    /// <summary>
    /// 一次「检查更新」的结果。既承载「要不要更新」，也承载「更新什么」。
    /// </summary>
    public sealed class ResourceUpdateInfo
    {
        /// <summary>远端清单；检查失败时为 null。</summary>
        public ResourceManifest Remote;

        /// <summary>本地当前版本号（未落地过任何版本时为 "0"）。</summary>
        public string LocalVersion;

        /// <summary>需要下载的文件（本地缺失或 hash 不一致）。</summary>
        public List<ResourceFileInfo> FilesToDownload = new();

        /// <summary>需要删除的本地残留文件（远端已不存在的包）。</summary>
        public List<string> FilesToDelete = new();

        /// <summary>待下载总字节数。</summary>
        public long TotalBytes;

        /// <summary>是否有更新（<see cref="FilesToDownload"/> 非空）。</summary>
        public bool HasUpdate => FilesToDownload.Count > 0;

        /// <summary>远端版本是否要求强制更新（清单里的 <c>force</c> 字段，缺省 false）。</summary>
        public bool Force;

        /// <summary>失败原因；成功时为 null。</summary>
        public string Error;

        /// <summary>是否检查成功。</summary>
        public bool Success => Error == null && Remote != null;

        /// <summary>调试与日志用的单行描述。</summary>
        public string Describe()
        {
            if (!Success) return $"check failed: {Error}";
            if (!HasUpdate) return $"up to date ({LocalVersion})";
            return $"update {LocalVersion} -> {Remote.Version}: {FilesToDownload.Count} file(s), {TotalBytes} bytes";
        }
    }

    /// <summary>
    /// 下载进度。一个实例会在整个下载过程里被复用（每次回调原地更新字段），
    /// 避免每帧给回调分配新对象 —— 与 UI 侧「每帧一次 GC 就是异味」的取向一致。
    /// </summary>
    public sealed class ResourceUpdateProgress
    {
        /// <summary>已完成文件数。</summary>
        public int CompletedFiles;

        /// <summary>待下载文件总数。</summary>
        public int TotalFiles;

        /// <summary>已完成字节数（含断点续传时已落地的部分）。</summary>
        public long DownloadedBytes;

        /// <summary>待下载总字节数。</summary>
        public long TotalBytes;

        /// <summary>总进度 0~1；总长为 0 时视为 1。</summary>
        public float Progress => TotalBytes <= 0 ? 1f : Math.Min(1f, (float)DownloadedBytes / TotalBytes);

        /// <summary>平滑后的下载速度（字节/秒）。</summary>
        public float BytesPerSecond;

        /// <summary>当前正在下载的文件名。</summary>
        public string CurrentFile;

        /// <summary>调试与日志用的单行描述。</summary>
        public override string ToString()
            => $"{Progress:P0} ({CompletedFiles}/{TotalFiles}) {BytesPerSecond / 1024f:F0}KB/s {CurrentFile}";
    }

    /// <summary>资源热更的当前阶段（调试面板展示用）。</summary>
    public enum ResourceUpdateState
    {
        /// <summary>尚未检查。</summary>
        Idle = 0,
        /// <summary>正在拉取并比对清单。</summary>
        Checking = 1,
        /// <summary>正在下载差异文件。</summary>
        Downloading = 2,
        /// <summary>下载完成且校验通过，内容已就绪。</summary>
        Ready = 3,
        /// <summary>上次操作失败。</summary>
        Failed = 4,
    }

    /// <summary>
    /// 资源模块配置。由引导类 <c>CloverRes.Init(ResourceModuleConfig)</c>（在 Resource 程序集）消费；
    /// 本类型放 Core 是因为 Resource 依赖 Core 而非反向。
    /// </summary>
    /// <remarks>
    /// 两种模式，靠 <see cref="ManifestUrl"/> 是否为空区分：
    /// <list type="bullet">
    ///   <item><b>不配热更</b>（ManifestUrl 为空）：走 Unity 内置 Resources，行为与改造前完全一致；</item>
    ///   <item><b>配热更</b>：从 <see cref="ManifestUrl"/> 拉清单，差异下载到 <see cref="ContentDir"/>，
    ///   之后所有加载走 AssetBundle 后端。</item>
    /// </list>
    /// 因此**接入热更是显式的**：不配就是老行为，不会有「悄悄换了后端」这种意外。
    /// </remarks>
    public sealed class ResourceModuleConfig
    {
        /// <summary>
        /// Resources 根前缀（如 <c>"Clover"</c> 表示 <c>Resources/Clover/...</c>）。
        /// 只在非 Bundle 模式下生效；留空表示以 Resources 根为根。
        /// </summary>
        public string Root;

        /// <summary>远端清单地址（完整 URL）。为空 = 不启用热更（走 Resources）。</summary>
        public string ManifestUrl;

        /// <summary>
        /// 热更内容落地目录。为空时用 <c>Application.persistentDataPath/clover-res</c>。
        /// 必须是**可写目录**：StreamingAssets 在移动端是只读的。
        /// </summary>
        public string ContentDir;

        /// <summary>
        /// 首包内置内容目录（可选）。AssetBundle 模式下，可写内容目录找不到某包时会到这里再找一次，
        /// 用于「首包自带一批资源、热更只补差量」的发布方式。
        /// **注意**：目录位于包内时（如 Android 的 StreamingAssets、WebGL 的服务路径）不能用
        /// <c>File.Exists</c> / 同步开包判定，这几类平台会自动改走 UnityWebRequest 异步读取（见 AssetBundleBackend）。
        /// </summary>
        public string BuiltinContentDir;

        /// <summary>缓存字节水位，超过后按 LRU 释放未引用资源；<c>0</c> 或负数表示不限制。默认 256MB。</summary>
        public long CacheWatermark = 256L * 1024 * 1024;

        /// <summary>并发下载数，默认 4。</summary>
        public int MaxConcurrentDownloads = 4;

        /// <summary>单文件最大重试次数，默认 3。</summary>
        public int MaxRetries = 3;
    }
}
