using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace CloverEngine
{
    /// <summary>
    /// 资源热更编排：拉清单 → 与本地比对 → 差异下载 → 校验落地 → 写本地版本。
    /// <para>
    /// 为什么比对要「信任本地清单」而不是「每次全量算 hash」：
    /// 一个包动辄几十 MB，每次启动把所有本地文件重算一遍 MD5 会让启动卡到没法用。
    /// 因此本地保留一份**上次校验通过的清单**（<c>manifest.json</c>）：
    /// 远端与本地清单里 hash 相同的文件直接跳过，只有「变了」或「本地文件不在了」才动磁盘。
    /// 怀疑本地被损坏时，走 <see cref="ClearDownloaded"/> 硬修复，而不是每次都付全量校验的代价。
    /// </para>
    /// <para>
    /// 目录约定（<c>ContentDir</c> 下）：
    /// <c>manifest.json</c> = 本地已生效清单（**版本号的唯一来源**，<see cref="LocalVersion"/> 取自它）；
    /// 其余为各 AssetBundle / 裸文件；下载中的半成品是 <c>&lt;名&gt;.part</c>。
    /// </para>
    /// </summary>
    internal sealed class ResourceUpdater
    {
        private const string ManifestFileName = "manifest.json";

        private readonly ResourceModuleConfig _config;
        private readonly string _contentDir;
        private readonly ResourceDownloader _downloader = new();

        private UnityWebRequest _manifestRequest;
        private Action<ResourceUpdateInfo> _checkCallback;

        /// <summary>已被取消、等待结束的清单请求（Unity 要求请求结束后才能 Dispose，由 Tick 收尾）。</summary>
        private UnityWebRequest _cancelledRequest;

        /// <summary>清单请求或差异比对（含后台哈希）在途：期间拒绝重复 Check。</summary>
        private bool _checkInFlight;

        /// <summary>构造编排器。</summary>
        /// <param name="config">模块配置。</param>
        /// <param name="contentDir">内容目录（绝对路径，已解析过默认值）。</param>
        /// <param name="localManifest">本地已生效清单（可能为 null，表示从未更新过）。</param>
        public ResourceUpdater(ResourceModuleConfig config, string contentDir, ResourceManifest localManifest)
        {
            _config = config;
            _contentDir = contentDir;
            LocalManifest = localManifest;
        }

        /// <summary>本地已生效清单；从未更新过时为 null。</summary>
        public ResourceManifest LocalManifest { get; private set; }

        /// <summary>当前阶段。</summary>
        public ResourceUpdateState State { get; private set; } = ResourceUpdateState.Idle;

        /// <summary>是否正在下载。</summary>
        public bool IsDownloading => _downloader.IsRunning;

        /// <summary>下载进度（实例复用）。</summary>
        public ResourceUpdateProgress Progress => _downloader.Progress;

        /// <summary>本地版本号；未更新过时为 "0"。</summary>
        public string LocalVersion
        {
            get
            {
                if (LocalManifest != null && !string.IsNullOrEmpty(LocalManifest.Version))
                    return LocalManifest.Version;
                return "0";
            }
        }

        /// <summary>从内容目录读取已落地的清单。读不到 / 解析失败时返回 null。</summary>
        public static ResourceManifest LoadLocalManifest(string contentDir)
        {
            if (string.IsNullOrEmpty(contentDir)) return null;

            var path = Path.Combine(contentDir, ManifestFileName);
            if (!File.Exists(path)) return null;

            try
            {
                var json = File.ReadAllText(path, Encoding.UTF8);
                return ResourceManifest.Parse(json);
            }
            catch (Exception e)
            {
                // 损坏的本地清单不该让游戏起不来：当作「没有本地版本」处理，下次会重新拉全量。
                Game.Logger?.Warn("Resource", $"本地清单损坏，将按首次安装处理：{e.Message}");
                return null;
            }
        }

        /// <summary>内容目录（绝对路径）。裸文件（配表等）的读取方据此定位。</summary>
        public string ContentDir => _contentDir;

        /// <summary>每帧泵：推进清单请求与下载器。由 <c>Game.Tick → Res.Tick</c> 驱动。</summary>
        public void Tick()
        {
            DrainCancelledRequest();
            PumpManifestRequest();
            _downloader.Tick();
        }

        /// <summary>拉取远端清单并比对。回调必定触发一次（失败时 <c>Error</c> 有值）。</summary>
        public void Check(Action<ResourceUpdateInfo> onResult)
        {
            if (string.IsNullOrEmpty(_config.ManifestUrl))
            {
                onResult?.Invoke(new ResourceUpdateInfo
                {
                    LocalVersion = LocalVersion,
                    Error = "未配置 ManifestUrl（不启用热更）",
                });
                return;
            }

            if (_checkInFlight)
            {
                onResult?.Invoke(new ResourceUpdateInfo
                {
                    LocalVersion = LocalVersion,
                    Error = "上一次检查尚未结束",
                });
                return;
            }

            _checkInFlight = true;
            State = ResourceUpdateState.Checking;
            _checkCallback = onResult;

            var url = _config.ManifestUrl;
            var req = new UnityWebRequest(url, UnityWebRequest.kHttpVerbGET)
            {
                downloadHandler = new DownloadHandlerBuffer(),
                timeout = 15,
            };
            _manifestRequest = req;

            try
            {
                req.SendWebRequest();
            }
            catch (Exception e)
            {
                // 发送失败：请求对象也要释放（它已经创建），回调引用与在途标记一起清掉，避免残留
                _manifestRequest = null;
                _checkCallback = null;
                _checkInFlight = false;
                req.Dispose();
                State = ResourceUpdateState.Failed;
                Game.Logger?.Error("Resource", $"发起清单请求失败：{e.Message}", e);
                onResult?.Invoke(new ResourceUpdateInfo
                {
                    LocalVersion = LocalVersion,
                    Error = $"发起清单请求失败：{e.Message}",
                });
            }
        }

        /// <summary>下载差异文件；全部校验通过后写入本地清单（版本号随清单），最后才清理远端已移除的旧文件。</summary>
        public void Download(ResourceUpdateInfo info, Action<ResourceUpdateProgress> onProgress,
            Action<bool, string> onDone)
        {
            if (info == null || !info.Success)
            {
                onDone?.Invoke(true, null);
                return;
            }

            if (_downloader.IsRunning)
            {
                // 已在下载中：不能静默（调用方会永久等回调），也不能给出成功或把状态改回 Ready（那都是假状态）
                Game.Logger?.Warn("Resource", "已有下载任务在运行，忽略重复 Download");
                onDone?.Invoke(false, "已有下载任务在运行");
                return;
            }

            State = ResourceUpdateState.Downloading;

            if (!info.HasUpdate)
            {
                // 远端只有删除、没有新增文件：没有下载步骤，但「提交清单 + 清理旧文件」仍要执行，
                // 否则旧文件永不清理、本地版本号永不前进（下次启动还会重复走一遍检查）。
                CommitAndCleanup(info, onDone);
                return;
            }

            var started = _downloader.Start(
                info.Remote.Root,
                info.FilesToDownload,
                _contentDir,
                _config.MaxConcurrentDownloads,
                _config.MaxRetries,
                onProgress,
                (ok, error) =>
                {
                    if (!ok)
                    {
                        // 下载失败时**不删旧文件、不提交清单**：本地保持已安装版本的完整状态，
                        // 避免「清单已指向新版本、文件却没下完」的半更新残局（运行中与下次启动都会白图/缺包）。
                        State = ResourceUpdateState.Failed;
                        onDone?.Invoke(false, error);
                        return;
                    }

                    CommitAndCleanup(info, onDone);
                });

            if (!started && State == ResourceUpdateState.Downloading)
            {
                // 兜底：Start 未启动且未回调（契约上不应发生）——不能静默，否则调用方永久等待
                State = ResourceUpdateState.Failed;
                Game.Logger?.Error("Resource", "下载器未能启动（未回调），下载中止");
                onDone?.Invoke(false, "下载器未能启动");
            }
        }

        /// <summary>提交新清单并清理旧文件（「下载成功」与「无文件可下」两条路径共用）。</summary>
        private void CommitAndCleanup(ResourceUpdateInfo info, Action<bool, string> onDone)
        {
            // 只有**全部文件校验通过**才写清单：写早了会让下次启动以为已有内容，
            // 而实际是半成品，表现为「莫名少资源」。
            if (!CommitManifest(info.Remote, out var commitError))
            {
                State = ResourceUpdateState.Failed;
                onDone?.Invoke(false, commitError);
                return;
            }

            // 新版本已落地生效，此时才删远端已移除的旧文件与孤儿半成品：
            // 删除放到成功后，下载中途失败不会破坏仍在使用中的版本。
            DeleteStale(info);

            State = ResourceUpdateState.Ready;
            onDone?.Invoke(true, null);
        }

        /// <summary>
        /// 取消下载 / 在途的清单检查（保留半成品）。
        /// 在途清单请求也要一起收掉：否则引擎拆卸时它既不释放也不回调，请求对象与回调引用一起残留。
        ///
        /// <para>
        /// <b>调用点</b>：业务可见入口是公开面 <see cref="IResourceManager.CancelUpdate"/>（本方法的转发），
        /// 由业务在「玩家点取消更新」时调用；引擎侧的自动调用点是 <c>Game.Shutdown</c> 的
        /// <c>Res.CancelUpdate</c> 拆卸步骤。半成品按 <c>.part</c> 保留，下次更新可续传。
        /// </para>
        /// </summary>
        public void CancelDownload()
        {
            _downloader.Cancel();

            var req = _manifestRequest;
            if (req != null)
            {
                _manifestRequest = null;
                _checkCallback = null;
                _checkInFlight = false;

                if (req.isDone)
                {
                    req.Dispose();
                }
                else
                {
                    // 请求在途：Unity 要求请求结束后才能 Dispose —— 先 Abort，由 Tick 收尾
                    try
                    {
                        req.Abort();
                    }
                    catch (Exception e)
                    {
                        Game.Logger?.Warn("Resource", $"中止清单请求失败: {e.Message}");
                    }
                    _cancelledRequest = req;
                }
                Game.Logger?.Info("Resource", "已取消在途的清单检查");
            }

            if (State == ResourceUpdateState.Downloading || State == ResourceUpdateState.Checking)
                State = ResourceUpdateState.Idle;
        }

        /// <summary>收尾被取消的清单请求：等它真正结束再释放。</summary>
        private void DrainCancelledRequest()
        {
            var req = _cancelledRequest;
            if (req == null || !req.isDone) return;
            _cancelledRequest = null;
            req.Dispose();
        }

        /// <summary>
        /// 下载成功但后端切换失败时的状态回退：更新未在本进程生效，
        /// 状态不能停在 Ready（业务会据此误判「热更已生效」）。
        /// </summary>
        public void RollbackStateAfterApplyFailure()
        {
            if (State == ResourceUpdateState.Ready)
                State = ResourceUpdateState.Failed;
        }

        /// <summary>清除热更内容与本地版本记录（换服 / 回滚 / 坏包硬修复）。</summary>
        public void ClearDownloaded(out string error)
        {
            error = null;
            if (_downloader.IsRunning)
            {
                error = "正在下载中，无法清理（请先取消下载）";
                return;
            }

            try
            {
                if (Directory.Exists(_contentDir))
                    Directory.Delete(_contentDir, true);
                Directory.CreateDirectory(_contentDir);
                LocalManifest = null;
                State = ResourceUpdateState.Idle;
                Game.Logger?.Info("Resource", $"已清除热更内容：{_contentDir}");
            }
            catch (Exception e)
            {
                error = $"清除失败：{_contentDir}: {e.Message}";
                Game.Logger?.Error("Resource", error, e);
            }
        }

        private void PumpManifestRequest()
        {
            var req = _manifestRequest;
            if (req == null || !req.isDone) return;

            _manifestRequest = null;
            var callback = _checkCallback;
            _checkCallback = null;

            var info = BuildResultFromResponse(req);
            req.Dispose();

            if (!info.Success || info.Remote == null)
            {
                FinishCheck(info, callback);
                return;
            }

            // 差异比对（含本地文件重算 MD5）是纯 IO/CPU 工作，且可能逐个哈希几十 MB 的文件：
            // 放后台线程算、回主线程交付结果 —— 同步做会长时间冻结主线程。
            DiffLocal(info.Remote, info, () => FinishCheck(info, callback));
        }

        /// <summary>检查流程的统一出口：更新状态、打日志、把结果交给业务（回调恰好一次）。</summary>
        private void FinishCheck(ResourceUpdateInfo info, Action<ResourceUpdateInfo> callback)
        {
            _checkInFlight = false;
            State = info.Success
                ? (info.HasUpdate ? ResourceUpdateState.Idle : ResourceUpdateState.Ready)
                : ResourceUpdateState.Failed;

            if (info.Success && info.Force)
                Game.Logger?.Warn("Resource",
                    $"远端清单要求强制更新（force=true，版本 {info.Remote.Version}）：" +
                    "下载完成前应拦截进游戏（拦截点由业务流程实现，引擎不做强制校验）");

            Game.Logger?.Info("Resource", $"check update: {info.Describe()}");
            try
            {
                callback?.Invoke(info);
            }
            catch (Exception e)
            {
                Game.Logger?.Error("Resource", $"检查更新回调异常：{e.Message}", e);
            }
        }

        private ResourceUpdateInfo BuildResultFromResponse(UnityWebRequest req)
        {
            var info = new ResourceUpdateInfo { LocalVersion = LocalVersion };

            if (req.result != UnityWebRequest.Result.Success)
            {
                info.Error = $"拉取清单失败：{req.result} (HTTP {req.responseCode}) {req.error}";
                return info;
            }

            ResourceManifest remote;
            try
            {
                remote = ResourceManifest.Parse(req.downloadHandler.text);
            }
            catch (Exception e)
            {
                info.Error = $"清单解析失败：{e.Message}";
                return info;
            }

            var issues = remote.Validate();
            if (issues.Count > 0)
            {
                // 清单本身有问题时**明确失败**，不要「能下多少下多少」：
                // 那会把发布端的错误变成客户端上一堆零散的加载失败，定位成本高得多。
                info.Error = "清单自检未通过：" + string.Join("；", issues);
                return info;
            }

            // 版本回退（降级 / 回滚）提示：比对仍按文件 hash 进行，但要让业务在日志里看得见
            if (CompareVersions(remote.Version, LocalVersion) < 0)
                Game.Logger?.Warn("Resource",
                    $"远端清单版本 {remote.Version} 低于本地 {LocalVersion}（疑似回退/降级），仍按文件 hash 差异更新");

            info.Remote = remote;
            info.Force = remote.Force;
            return info;
        }

        /// <summary>版本号比较（逐段数字，缺段按 0 处理）；无法比较时返回 0。</summary>
        private static int CompareVersions(string a, string b)
        {
            if (string.Equals(a, b, StringComparison.Ordinal)) return 0;
            if (string.IsNullOrEmpty(a)) return string.IsNullOrEmpty(b) ? 0 : -1;
            if (string.IsNullOrEmpty(b)) return 1;

            var pa = a.Split('.');
            var pb = b.Split('.');
            var n = Math.Max(pa.Length, pb.Length);
            for (var i = 0; i < n; i++)
            {
                var sa = i < pa.Length ? pa[i] : "0";
                var sb = i < pb.Length ? pb[i] : "0";
                if (int.TryParse(sa, out var va) && int.TryParse(sb, out var vb))
                {
                    if (va != vb) return va < vb ? -1 : 1;
                    continue;
                }

                var cmp = string.CompareOrdinal(sa, sb);
                if (cmp != 0) return cmp < 0 ? -1 : 1;
            }
            return 0;
        }

        /// <summary>
        /// 与本地比对，算出「要下什么」「要删什么」。判定顺序：本地清单说 hash 相同 → 本地文件也在 → 跳过；
        /// 否则需要实际算一次 hash 才能定论。
        /// <para>
        /// 逐个哈希会长时间占用主线程，因此**整个比对放后台线程**执行，完成后回主线程回调；
        /// 日志在主线程补打（后台线程不触碰 Unity API）。
        /// </para>
        /// </summary>
        private void DiffLocal(ResourceManifest remote, ResourceUpdateInfo info, Action onDone)
        {
            // 本地清单是**共享对象**（后端与主线程也会读它的惰性索引）：先在主线程把需要的名字表取好，
            // 后台线程只用快照 —— 否则会和主线程并发触碰同一份索引（Dictionary 并发读写）。
            var localFiles = LocalManifest?.Files;
            Dictionary<string, ResourceFileInfo> localByName = null;
            if (localFiles != null)
            {
                localByName = new Dictionary<string, ResourceFileInfo>(localFiles.Count, StringComparer.Ordinal);
                foreach (var f in localFiles)
                {
                    if (f != null && !string.IsNullOrEmpty(f.Name))
                        localByName[f.Name] = f;
                }
            }

#if UNITY_WEBGL && !UNITY_EDITOR
            // WebGL 无线程能力：退回同步比对（该平台包体小，且本就在单线程播放器循环里）
            var problemsSync = new List<string>();
            DiffLocalCore(remote, info, localByName, localFiles, problemsSync);
            LogProblems(problemsSync);
            onDone();
#else
            System.Threading.Tasks.Task.Run(() =>
            {
                var problems = new List<string>();
                try
                {
                    DiffLocalCore(remote, info, localByName, localFiles, problems);
                }
                catch (Exception e)
                {
                    problems.Add($"差异比对异常：{e.Message}");
                }

                void Deliver()
                {
                    LogProblems(problems);
                    onDone();
                }

                var dispatcher = Game.Dispatcher;
                if (dispatcher != null)
                    dispatcher.Post(Deliver);
                else
                    Deliver();
            });
#endif
        }

        private static void LogProblems(List<string> problems)
        {
            if (problems == null) return;
            foreach (var problem in problems)
                Game.Logger?.Warn("Resource", problem);
        }

        /// <summary>
        /// 比对核心逻辑（可能运行在后台线程：**这里不许调用任何 Unity API**，问题先收集、回主线程再打日志；
        /// 本地清单只用主线程取好的 <paramref name="localByName"/> / <paramref name="localFiles"/> 快照）。
        /// </summary>
        private void DiffLocalCore(ResourceManifest remote, ResourceUpdateInfo info,
            Dictionary<string, ResourceFileInfo> localByName, IReadOnlyList<ResourceFileInfo> localFiles, List<string> problems)
        {
            foreach (var file in remote.Files)
            {
                if (file == null || string.IsNullOrEmpty(file.Name)) continue;

                // 清单文件名不可信：含「..」/ 绝对路径的名字不能去读（会越出内容目录）。
                // 同时标记为待下载 —— 下载器会在 Start 处统一拒绝，使整个更新以明确原因失败（而不是静默跳过、提交出坏清单）。
                if (!ResourcePaths.TryCombine(_contentDir, file.Name, out var localPath))
                {
                    problems.Add($"文件名不合法（含越界路径段），更新将失败：{file.Name}");
                    info.FilesToDownload.Add(file);
                    continue;
                }

                if (!File.Exists(localPath))
                {
                    info.FilesToDownload.Add(file);
                    info.TotalBytes += Math.Max(0, file.Size);
                    continue;
                }

                if (localByName != null && localByName.TryGetValue(file.Name, out var known) &&
                    ResourceHash.Same(known.Hash, file.Hash))
                    continue; // 上次已校验通过且远端没变

                // 本地有文件、但本地清单没记录（或记录不一致）：只能实际算一次 hash 才能定论
                var actual = ResourceHash.FileMd5HexQuiet(localPath, out var hashError);
                if (hashError != null)
                    problems.Add($"hash failed for {localPath}: {hashError}（该文件将按需重下）");

                if (hashError == null && ResourceHash.Same(actual, file.Hash))
                    continue;

                info.FilesToDownload.Add(file);
                info.TotalBytes += Math.Max(0, file.Size);
            }

            if (localFiles == null) return;

            foreach (var local in localFiles)
            {
                if (local == null || string.IsNullOrEmpty(local.Name)) continue;
                if (remote.Find(local.Name) != null) continue;
                info.FilesToDelete.Add(local.Name);
            }
        }

        /// <summary>
        /// 清理「新版本已经不需要」的本地文件：远端已移除的最终文件及其半成品，
        /// 外加内容目录里所有**基名已不在远端清单**的 <c>*.part</c>（取消 / 改名留下的孤儿 ——
        /// 永远续不上、只会占空间）。只在下载成功且清单位已提交后调用（见 <see cref="CommitAndCleanup"/>）。
        /// </summary>
        private void DeleteStale(ResourceUpdateInfo info)
        {
            var names = info.FilesToDelete;
            if (names != null)
            {
                foreach (var name in names)
                {
                    // 清单文件名不可信：含「..」/ 绝对路径的名字一律跳过（删它只会越出内容目录）
                    if (!ResourcePaths.TryCombine(_contentDir, name, out var path))
                    {
                        Game.Logger?.Warn("Resource", $"文件名不合法（含越界路径段），已跳过删除：{name}");
                        continue;
                    }

                    TryDelete(path, name);
                    TryDelete(path + ".part", name + ".part");
                }
            }

            var remote = info.Remote;
            if (remote == null || !Directory.Exists(_contentDir)) return;

            var known = new HashSet<string>(StringComparer.Ordinal);
            foreach (var f in remote.Files)
            {
                if (f != null && !string.IsNullOrEmpty(f.Name)) known.Add(f.Name);
            }

            try
            {
                foreach (var part in Directory.GetFiles(_contentDir, "*.part", SearchOption.AllDirectories))
                {
                    var relative = part.Substring(_contentDir.Length).TrimStart('/', '\\').Replace('\\', '/');
                    if (!relative.EndsWith(".part", StringComparison.Ordinal)) continue;
                    var baseName = relative.Substring(0, relative.Length - ".part".Length);
                    if (known.Contains(baseName)) continue; // 还在远端清单里：是有效续传半成品，保留

                    TryDelete(part, relative);
                }
            }
            catch (Exception e)
            {
                Game.Logger?.Warn("Resource", $"清理半成品失败（仅占空间，不影响运行）：{e.Message}");
            }
        }

        private static void TryDelete(string path, string label)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    Game.Logger?.Info("Resource", $"删除本地残留：{label}（远端已移除或为无主半成品）");
                }
            }
            catch (Exception e)
            {
                Game.Logger?.Warn("Resource", $"删除失败（仅占空间，不影响运行）：{path}: {e.Message}");
            }
        }

        private bool CommitManifest(ResourceManifest remote, out string error)
        {
            error = null;
            var path = Path.Combine(_contentDir, ManifestFileName);
            var tempPath = path + ".tmp";
            try
            {
                Directory.CreateDirectory(_contentDir);
                // 清单提交必须尽量原子：直接覆写时中途崩溃会留下截断的 json，下次启动会当作「从未更新」
                // 重新全量下载。先写临时文件、再改名替换，旧的完整清单在改名成功前始终可用。
                File.WriteAllText(tempPath, remote.ToJson(), Encoding.UTF8);
                if (File.Exists(path)) File.Delete(path);
                File.Move(tempPath, path);
                LocalManifest = remote;
                return true;
            }
            catch (Exception e)
            {
                error = $"写入本地清单失败：{e.Message}";
                Game.Logger?.Error("Resource", error, e);
                return false;
            }
        }
    }
}
