using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace CloverEngine
{
    /// <summary>资源文件哈希工具：清单里的 hash 与下载后校验共用同一套算法（MD5 小写十六进制）。</summary>
    internal static class ResourceHash
    {
        /// <summary>计算文件 MD5（小写十六进制）。文件不存在或读取失败返回 null。</summary>
        public static string FileMd5Hex(string path)
        {
            var hash = FileMd5HexQuiet(path, out var error);
            if (error != null)
                Game.Logger?.Warn("Resource", $"hash failed for {path}: {error}");
            return hash;
        }

        /// <summary>
        /// 计算文件 MD5（小写十六进制）；失败时返回 null 并通过 <paramref name="error"/> 给出原因。
        /// **不写日志** —— 供后台线程调用（那里不能触碰 Unity API），失败原因由调用方带回主线程再打。
        /// </summary>
        public static string FileMd5HexQuiet(string path, out string error)
        {
            error = null;
            try
            {
                using (var md5 = MD5.Create())
                using (var stream = File.OpenRead(path))
                {
                    return ToHex(md5.ComputeHash(stream));
                }
            }
            catch (Exception e)
            {
                error = e.Message;
                return null;
            }
        }

        /// <summary>
        /// 异步计算文件 MD5（小写十六进制），失败 / 文件不存在时回调收到 null。
        /// **算在后台线程、回主线程回调**（经 <c>Game.Dispatcher.Post</c>）：整包几十 MB 的同步哈希
        /// 会长时间卡住主线程帧，而下载校验与差异比对都要用到它；失败日志在回调侧（主线程）打，
        /// 避免在后台线程触碰 Unity API。WebGL 无线程能力，退回同步计算（该平台包体小，且本就在单线程播放器循环里）。
        /// </summary>
        public static void FileMd5HexAsync(string path, Action<string> onDone)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            onDone?.Invoke(FileMd5Hex(path));
#else
            System.Threading.Tasks.Task.Run(() =>
            {
                string hash = null;
                string error = null;
                try
                {
                    using (var md5 = MD5.Create())
                    using (var stream = File.OpenRead(path))
                    {
                        hash = ToHex(md5.ComputeHash(stream));
                    }
                }
                catch (Exception e)
                {
                    error = e.Message;
                }

                void Deliver()
                {
                    if (error != null)
                        Game.Logger?.Warn("Resource", $"hash failed for {path}: {error}");
                    onDone?.Invoke(hash);
                }

                var dispatcher = Game.Dispatcher;
                if (dispatcher != null)
                    dispatcher.Post(Deliver);
                else
                    Deliver();
            });
#endif
        }

#if UNITY_WEBGL
        /// <summary>
        /// 计算内存字节的 MD5（小写十六进制），仅 WebGL 使用：该平台不落盘，
        /// 落地前的 hash 校验只能在内存内容上做（没有文件可读）。失败返回 null。
        /// </summary>
        public static string BytesMd5Hex(byte[] bytes)
        {
            if (bytes == null) return null;
            try
            {
                using (var md5 = MD5.Create())
                    return ToHex(md5.ComputeHash(bytes));
            }
            catch (Exception e)
            {
                Game.Logger?.Warn("Resource", $"内存 hash 计算失败: {e.Message}");
                return null;
            }
        }
#endif

        /// <summary>比较两个哈希是否相同（忽略大小写与首尾空白）；任一为空视为不相同。</summary>
        public static bool Same(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            return string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private static string ToHex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes)
                sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }

    /// <summary>
    /// 断点续传下载器：把清单里的差异文件拉进内容目录。
    /// <para>
    /// 断点续传的三道保险（缺任何一道都会**静默产出坏文件**，这是这类下载器最容易翻车的地方）：
    /// </para>
    /// <list type="number">
    ///   <item>半成品先写 <c>*.part</c>，**校验通过才改名**成最终文件，因此内容目录里不会出现半个包；</item>
    ///   <item>续传用 <c>Range: bytes=N-</c>；若服务端**不支持 Range 而返回 200**，那是从头重发的完整内容，
    ///   此时必须**丢掉已有半成品重新写**，否则会得到「前半段 + 完整内容」的拼接文件；</item>
    ///   <item>落地前按清单 hash 校验，不符即删除重来（受重试上限约束）。</item>
    /// </list>
    /// <para>
    /// <b>WebGL 例外（见 <c>BeginOne</c> / <c>CompleteFile</c> 的 <c>#if UNITY_WEBGL</c> 分支）</b>：
    /// 该平台没有可持久化的本地文件系统 —— System.IO 落在 Emscripten 的**内存**虚拟 FS 上，
    /// 页面刷新即丢，也拿不到 IndexedDB 同步 —— 所以上面这三道保险在本平台**整体不适用**：
    /// 引擎改为"只下到内存"（<c>DownloadHandlerBuffer</c> 收内容 → 内存里校验 hash → 只记进度不落盘），
    /// 并留一条 Warn 说明本平台不做持久化。其它平台逐行走原逻辑，行为不变。
    /// </para>
    /// <para>
    /// 所有回调都在 <see cref="Tick"/> 内触发，即**主线程**，调用方无需再切线程。
    /// 下载泵不另起线程：<c>UnityWebRequest</c> 本身就是异步的，泵在主线程即可；
    /// 唯一的后台工作是整包 MD5（<c>ResourceHash.FileMd5HexAsync</c>，算完经 Dispatcher 回主线程），
    /// 因此不需要处理「回调到达时对象已被销毁」这类并发问题。
    /// </para>
    /// </summary>
    internal sealed class ResourceDownloader
    {
        /// <summary>请求长时间无字节推进即视为停滞，中止后交给重试（见 <see cref="CheckStall"/>）。</summary>
        private const double StallTimeoutSeconds = 30.0;

        /// <summary>一个下载任务（含重试计数：**计数必须挂在任务上**，挂在进行中的请求上会在重排队时丢失，
        /// 导致失败文件被无限重排——这是最典型的死循环写法）。</summary>
        private sealed class DownloadTask
        {
            public ResourceFileInfo Info;
            public string Url;
            public string FinalPath;
            public string TempPath;
            public int Attempts;

            /// <summary>重排后的最早可发起时刻（退避用；0 = 立刻可发起）。</summary>
            public long NotBeforeTicks;
        }

        /// <summary>一个进行中的下载。</summary>
        private sealed class ActiveDownload
        {
            public DownloadTask Task;
            public UnityWebRequest Request;
            public FileStream Stream;
            public long StartOffset;

            /// <summary>停滞看门狗：上次观测到的字节数与对应时刻。</summary>
            public long LastBytes;
            public long LastActivityTicks;

            /// <summary>已因停滞中止过一次（Abort 后等待请求真正结束，避免重复 Abort）。</summary>
            public bool StallAborted;

            /// <summary>速度采样：上次计入采样窗口的字节数。</summary>
            public long LastSampleBytes;
        }

        /// <summary>
        /// 边下边写：把网络分片直接追加进文件，而不是全量缓存在内存里
        /// （大包动辄几十 MB，全量在内存会直接顶爆峰值内存）。
        /// </summary>
        private sealed class FileAppendHandler : DownloadHandlerScript
        {
            private readonly FileStream _stream;

            public FileAppendHandler(FileStream stream)
            {
                _stream = stream;
            }

            protected override bool ReceiveData(byte[] data, int dataLength)
            {
                if (data == null || dataLength <= 0) return true;
                try
                {
                    _stream.Write(data, 0, dataLength);
                    return true;
                }
                catch (Exception e)
                {
                    // 返回 false 会中止本次请求并把 result 置为失败，交给重试逻辑处理
                    Game.Logger?.Error("Resource", $"写入下载文件失败: {e.Message}", e);
                    return false;
                }
            }

            protected override void CompleteContent()
            {
                try
                {
                    _stream.Flush();
                }
                catch (Exception e)
                {
                    Game.Logger?.Warn("Resource", $"flush 失败: {e.Message}");
                }
            }
        }

        private readonly List<DownloadTask> _queue = new();
        private readonly List<ActiveDownload> _active = new();
        private readonly List<ActiveDownload> _dying = new();
        private readonly ResourceUpdateProgress _progress = new();

        private string _contentDir;
        private int _maxConcurrent = 4;
        private int _maxRetries = 3;

#if UNITY_WEBGL
        /// <summary>
        /// WebGL 专用：本次会话已下载完成的内容（键 = 任务的 <c>TempPath</c>）。
        /// 该平台没有可持久化的本地文件系统 —— System.IO 落在 Emscripten 的**内存**虚拟 FS 上，
        /// 页面刷新即丢，也拿不到 IndexedDB 同步 —— 所以不走 <c>*.part</c> / 改名落地那套，
        /// 内容只留在内存里供本次加载使用（见 <see cref="BeginOne"/> 的 WebGL 分支）。
        /// </summary>
        private readonly Dictionary<string, byte[]> _memoryFiles = new();
#endif

        private long _totalBytes;
        private long _finishedBytes;
        private int _finishedFiles;
        private bool _running;
        private string _error;

        /// <summary>正在后台算 MD5 的半成品数：算作「未结束」，不能让 Tick 提前收尾。</summary>
        private int _hashing;

        /// <summary>下载会话号：取消 / 重新开始后旧会话的后台回调一律作废（防止串话到新一轮下载）。</summary>
        private int _session;

        /// <summary>进度回调异常只报一次（每帧回调一次，重复打会刷屏）。</summary>
        private bool _progressWarned;

        private Action<ResourceUpdateProgress> _onProgress;
        private Action<bool, string> _onDone;

        // 速度采样（0.5s 滑动窗口，避免瞬时抖动让显示数字乱跳）
        private long _sampleTicks;
        private long _sampleBytes;
        private float _speed;

        /// <summary>是否正在下载。</summary>
        public bool IsRunning => _running;

        /// <summary>当前进度对象（**同一实例被复用**，回调里不要长期持有它）。</summary>
        public ResourceUpdateProgress Progress => _progress;

        /// <summary>
        /// 开始下载。已在下载中时返回 false（不重复启动，也不会调用回调）。
        /// </summary>
        /// <param name="root">下载根地址（清单的 root，末尾带不带斜杠都可）。</param>
        /// <param name="files">待下载文件列表；为空时立即以成功回调结束。</param>
        /// <param name="contentDir">内容目录（绝对路径）：半成品与最终文件都放这里。
        /// <b>WebGL 下不落盘</b>，该参数只用于计算文件名与内存缓存的键。</param>
        /// <param name="maxConcurrent">并发下载数（建议 2~6）。</param>
        /// <param name="maxRetries">单文件最大重试次数。</param>
        /// <param name="onProgress">进度回调（主线程）。</param>
        /// <param name="onDone">完成回调（主线程）：成功 true；失败 false 且第二参为原因。</param>
        public bool Start(string root, List<ResourceFileInfo> files, string contentDir,
            int maxConcurrent, int maxRetries,
            Action<ResourceUpdateProgress> onProgress, Action<bool, string> onDone)
        {
            if (_running)
            {
                Game.Logger?.Warn("Resource", "downloader already running, ignore Start");
                return false;
            }
            if (files == null || files.Count == 0)
            {
                onDone?.Invoke(true, null);
                return false;
            }

            _contentDir = contentDir;
            _maxConcurrent = Math.Max(1, maxConcurrent);
            _maxRetries = Math.Max(0, maxRetries);
            _onProgress = onProgress;
            _onDone = onDone;
            _error = null;
            _finishedBytes = 0;
            _finishedFiles = 0;
            _totalBytes = 0;
            _queue.Clear();
            _active.Clear();
            _hashing = 0;
            _progressWarned = false;
            _session++; // 新一轮会话：上一轮残留的后台哈希回调据此作废

#if UNITY_WEBGL
            // WebGL 不落盘：内容只在内存里，没有内容目录要建（建了也只是内存虚拟 FS 里的一个空目录）。
            _memoryFiles.Clear();
#else
            try
            {
                Directory.CreateDirectory(_contentDir);
            }
            catch (Exception e)
            {
                onDone?.Invoke(false, $"创建内容目录失败：{_contentDir}: {e.Message}");
                return false;
            }
#endif

            var trimmedRoot = (root ?? string.Empty).TrimEnd('/');
            var invalidName = (string)null;
            var invalidCount = 0;
            foreach (var info in files)
            {
                if (info == null || string.IsNullOrEmpty(info.Name)) continue;

                // 清单文件名不可信：拒绝绝对路径 / 含「..」段的名字（zip slip：否则会写到内容目录之外）；
                // URL 逐段转义（包名含空格、#、? 或中文时直拼会让地址被截断或非法）。
                if (!ResourcePaths.TryCombine(_contentDir, info.Name, out var finalPath))
                {
                    invalidName ??= info.Name;
                    invalidCount++;
                    continue;
                }

                _totalBytes += Math.Max(0, info.Size);
                _queue.Add(new DownloadTask
                {
                    Info = info,
                    Url = trimmedRoot + "/" + ResourcePaths.EscapeUrlPath(info.Name),
                    FinalPath = finalPath,
                    TempPath = finalPath + ".part",
                });
            }

            if (invalidCount > 0)
            {
                // 清单里出现越界文件名 = 清单不可信：整次更新拒绝落地（而不是"能下多少下多少"），
                // 否则提交出去的清单会指向一个永远不会下载成功的文件。
                var error = $"清单含非法文件名（{invalidName} 等 {invalidCount} 个），已拒绝本次下载";
                Game.Logger?.Error("Resource", error);
                onDone?.Invoke(false, error);
                return false;
            }

            if (_queue.Count == 0)
            {
                onDone?.Invoke(true, null);
                return false;
            }

            _progress.TotalFiles = _queue.Count;
            _progress.TotalBytes = _totalBytes;
            _progress.CompletedFiles = 0;
            _progress.DownloadedBytes = 0;
            _progress.CurrentFile = null;
            _progress.BytesPerSecond = 0f;

            _sampleTicks = Stopwatch.GetTimestamp();
            _sampleBytes = 0;
            _speed = 0f;
            _running = true;

            Game.Logger?.Info("Resource",
                $"download start: {_queue.Count} file(s), {_totalBytes / 1024}KB, concurrent={_maxConcurrent}");
            return true;
        }

        /// <summary>
        /// 取消下载：中止在途请求（Abort 后等其结束再释放，见 <see cref="AbortActive"/>），
        /// **保留 *.part**（下次可续传）。不触发完成回调。
        /// </summary>
        public void Cancel()
        {
            if (!_running) return;
            _running = false;
            _session++; // 在途的后台哈希回调据此作废

            foreach (var active in _active)
                AbortActive(active);
            _active.Clear();
            _queue.Clear();
            _onDone = null;
            _onProgress = null;
            Game.Logger?.Info("Resource", "download cancelled（半成品已保留，下次可续传）");
        }

        /// <summary>每帧泵：推进在途下载、补足并发、算速度并回调。由 <c>Game.Tick → Res.Tick</c> 驱动。</summary>
        public void Tick()
        {
            // 0) 收尾已中止的请求：请求真正结束后才能 Dispose（在途不允许释放）
            DrainDying();

            if (!_running) return;

            // 1) 收掉已结束的请求（倒序遍历以便原地删除；FinishOne 内部可能整体失败清空列表，故每轮都重判下标）
            for (var i = _active.Count - 1; i >= 0; i--)
            {
                if (i >= _active.Count) continue;
                var active = _active[i];
                if (!active.Request.isDone)
                {
                    CheckStall(active);
                    continue;
                }

                _active.RemoveAt(i);
                FinishOne(active);
                if (_error != null) break;
            }

            // 2) 补足并发（重排任务带退避：到时间的才发起）
            while (_error == null && _active.Count < _maxConcurrent && _queue.Count > 0)
            {
                var idx = -1;
                var now = Stopwatch.GetTimestamp();
                for (var i = 0; i < _queue.Count; i++)
                {
                    if (_queue[i].NotBeforeTicks <= now)
                    {
                        idx = i;
                        break;
                    }
                }
                if (idx < 0) break;

                var next = _queue[idx];
                _queue.RemoveAt(idx);
                BeginOne(next);
            }

            // 3) 汇总进度（正在下载的请求本帧新到的字节也计入速度采样）
            UpdateSpeed();

            long downloaded = _finishedBytes;
            string current = null;
            foreach (var active in _active)
            {
                SampleActive(active);
                downloaded += active.StartOffset + (long)active.Request.downloadedBytes;
                current = active.Task.Info.Name;
            }

            _progress.DownloadedBytes = downloaded;
            _progress.CompletedFiles = _finishedFiles;
            _progress.CurrentFile = current;
            _progress.BytesPerSecond = _speed;
            SafeReportProgress();

            // 4) 收尾（失败也走这里，保证回调必达一次）；后台还有哈希在算时先不收（那批文件还没落地）
            if (_active.Count == 0 && _queue.Count == 0 && (_hashing == 0 || _error != null))
            {
                _running = false;
                var done = _onDone;
                var error = _error;
                _onDone = null;
                _onProgress = null;

                if (error == null)
                    Game.Logger?.Info("Resource", $"download done: {_finishedFiles} file(s)");
                else
                    Game.Logger?.Error("Resource", $"download failed: {error}");

                try
                {
                    done?.Invoke(error == null, error);
                }
                catch (Exception e)
                {
                    // 完成回调抛异常不能穿出 Tick：_onDone 已清空，穿出去会让下载器与调用方状态不一致
                    Game.Logger?.Error("Resource", $"下载完成回调异常：{e.Message}", e);
                }
            }
        }

        /// <summary>启动一个文件的下载（含续传判定）。</summary>
        private void BeginOne(DownloadTask task)
        {
#if UNITY_WEBGL
            // WebGL 没有可持久化的本地文件系统：下面的 FileStream / Directory.CreateDirectory / File.Move
            // 只会写到 Emscripten 的**内存**虚拟 FS（页面刷新即丢，也拿不到 IndexedDB 同步）——
            // *.part 续传、改名落地、落盘后哈希这"三道保险"全部失去意义，还会给出一份"看起来下好了"的假状态。
            // 因此本平台**不走落盘路径**：直接下到内存、在内存里校验并计入完成。
            // 其它平台逐行走原逻辑（本分支不进下面的任何代码）。
            Game.Logger?.Warn("Resource",
                "WebGL 平台不做本地持久化：本次下载只在内存中完成（*.part 续传 / 落地改名均不适用，页面刷新需重下）");
            BeginOneInMemory(task);
#else
            var info = task.Info;

            try
            {
                var dir = Path.GetDirectoryName(task.FinalPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            }
            catch (Exception e)
            {
                Fail($"创建子目录失败：{task.FinalPath}: {e.Message}");
                return;
            }

            // 半成品长度即续传起点；超过目标大小说明是坏的，直接丢掉重下
            long offset = 0;
            try
            {
                if (File.Exists(task.TempPath))
                {
                    var length = new FileInfo(task.TempPath).Length;
                    if (length > info.Size)
                    {
                        File.Delete(task.TempPath);
                    }
                    else
                    {
                        offset = length;
                    }
                }
            }
            catch (Exception e)
            {
                Game.Logger?.Warn("Resource", $"读取半成品失败，将从头下载：{task.TempPath}: {e.Message}");
                offset = 0;
            }

            // 半成品已经完整：不必再下，直接进入校验
            if (offset > 0 && offset == info.Size)
            {
                CompleteFile(task);
                return;
            }

            FileStream stream;
            try
            {
                stream = offset > 0
                    ? new FileStream(task.TempPath, FileMode.Append, FileAccess.Write, FileShare.Read)
                    : new FileStream(task.TempPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            }
            catch (Exception e)
            {
                Fail($"打开下载文件失败：{task.TempPath}: {e.Message}");
                return;
            }

            var req = new UnityWebRequest(task.Url, UnityWebRequest.kHttpVerbGET)
            {
                downloadHandler = new FileAppendHandler(stream),
                // 不设总超时：大包在弱网下很容易超过默认值，不应被总时限悄悄掐断。
                // 服务端半开 / 挂死由 Tick 里的**停滞看门狗**（CheckStall，字节长时间不推进即中止重试）兜底。
                timeout = 0,
            };
            if (offset > 0)
                req.SetRequestHeader("Range", $"bytes={offset}-");

            var active = new ActiveDownload
            {
                Task = task,
                Request = req,
                Stream = stream,
                StartOffset = offset,
                LastActivityTicks = Stopwatch.GetTimestamp(),
            };

            _active.Add(active);

            try
            {
                req.SendWebRequest();
            }
            catch (Exception e)
            {
                _active.Remove(active);
                CloseActive(active, deleteTemp: false);
                Fail($"发起下载失败：{task.Url}: {e.Message}");
            }
#endif
        }

#if UNITY_WEBGL
        /// <summary>
        /// WebGL 专用的"只下到内存"路径：不建目录、不开 FileStream、不带 Range（无 *.part 可续），
        /// 用 <see cref="DownloadHandlerBuffer"/> 把内容收在内存；长度校验、MD5 校验与"回调恰好一次"
        /// 仍走统一入口（<see cref="FinishOne"/> / <see cref="CompleteFile"/>），与其它平台同一套收尾逻辑。
        /// </summary>
        private void BeginOneInMemory(DownloadTask task)
        {
            var req = new UnityWebRequest(task.Url, UnityWebRequest.kHttpVerbGET)
            {
                downloadHandler = new DownloadHandlerBuffer(),
                // 不设总超时：与落盘路径一致，服务端半开 / 挂死由 Tick 里的停滞看门狗兜底。
                timeout = 0,
            };

            var active = new ActiveDownload
            {
                Task = task,
                Request = req,
                Stream = null,          // 本平台无文件句柄（CloseActive 对 null 安全）
                StartOffset = 0,        // 无续传起点
                LastActivityTicks = Stopwatch.GetTimestamp(),
            };

            _active.Add(active);

            try
            {
                req.SendWebRequest();
            }
            catch (Exception e)
            {
                _active.Remove(active);
                CloseActive(active, deleteTemp: false);
                Fail($"发起下载失败：{task.Url}: {e.Message}");
            }
        }
#endif

        /// <summary>处理一个已结束的下载：服务端行为异常则重来，正常则校验落地。</summary>
        private void FinishOne(ActiveDownload active)
        {
            var task = active.Task;
            var info = task.Info;
            var code = active.Request.responseCode;
            var downloaded = (long)active.Request.downloadedBytes;

            // 收掉最后一次 Tick 之后新到的字节（本请求已结束，之后不会再采样）
            SampleActive(active);

#if UNITY_WEBGL
            // WebGL 不落盘：内容必须在下面 CloseActive 释放请求**之前**从 download handler 取出来，
            // 否则 CompleteFile 就没有可校验的内容了（本平台的 hash 校验在内存里做）。
            var memoryBytes = active.Request.downloadHandler?.data;
#endif

            // 服务端忽略了 Range 而回 200（整包重发）：半成品 + 完整内容会拼成坏文件，必须从头写
            if (active.StartOffset > 0 && code == 200)
            {
                Game.Logger?.Warn("Resource",
                    $"服务端不支持断点续传（Range 被忽略，返回 200），改为整包重下：{info.Name}");
                CloseActive(active, deleteTemp: true);
                Requeue(task);
                return;
            }

            if (active.Request.result != UnityWebRequest.Result.Success && code != 416)
            {
                var reason = $"{active.Request.result} (HTTP {code}) {active.Request.error}";
                CloseActive(active, deleteTemp: false);
                Game.Logger?.Warn("Resource", $"下载失败：{info.Name}: {reason}");
                Requeue(task);
                return;
            }

            // 416：请求的区间不存在。只有「本地半成品长度正好等于目标长度」才是我们想要的语义
            if (code == 416)
            {
                CloseActive(active, deleteTemp: false);
                if (active.StartOffset == info.Size)
                {
                    CompleteFile(task);
                    return;
                }
                Requeue(task);
                return;
            }

            CloseActive(active, deleteTemp: false);

            var total = active.StartOffset + downloaded;
            if (total != info.Size)
            {
                Game.Logger?.Warn("Resource",
                    $"下载长度不符：{info.Name} 得到 {total}B，清单要求 {info.Size}B（半成品保留，下次续传）");
                Requeue(task);
                return;
            }

#if UNITY_WEBGL
            _memoryFiles[task.TempPath] = memoryBytes;
#endif
            CompleteFile(task);
        }

        /// <summary>
        /// 校验并落地一个文件；校验失败则删除重来。
        /// MD5 在**后台线程**计算（几十 MB 的整包同步哈希会明显卡帧），算完经 Dispatcher 回主线程继续；
        /// 期间用 <c>_hashing</c> 计数，Tick 不会把这批文件当成「已结束」提前收尾。
        /// <para>WebGL 无文件可落地：走 <c>CompleteFileInMemory</c>（校验内存内容，不落盘）。</para>
        /// </summary>
        private void CompleteFile(DownloadTask task)
        {
#if UNITY_WEBGL
            // 内存校验是同步的（该平台无线程），但同样用 _hashing 标记"这批文件还没结束"，
            // 与其它平台共用 Tick 的收尾门控（同步算完即归零，不会让收尾迟到）。
            _hashing++;
            CompleteFileInMemory(task);
            _hashing--;
#else
            _hashing++;
            var session = _session;
            ResourceHash.FileMd5HexAsync(task.TempPath, actual =>
            {
                // 期间取消 / 重新开始 / 整体失败：本轮结果作废（半成品保留，交给下次续传或校验）
                if (session != _session) return;
                _hashing--;
                if (!_running || _error != null) return;

                if (!ResourceHash.Same(actual, task.Info.Hash))
                {
                    Game.Logger?.Warn("Resource",
                        $"hash 校验失败：{task.Info.Name}（期望 {task.Info.Hash}，实际 {actual}）—— 删除后重下");
                    TryDelete(task.TempPath);
                    Requeue(task);
                    return;
                }

                try
                {
                    TryDelete(task.FinalPath);
                    File.Move(task.TempPath, task.FinalPath);
                }
                catch (Exception e)
                {
                    Fail($"落地文件失败：{task.FinalPath}: {e.Message}");
                    return;
                }

                _finishedFiles++;
                _finishedBytes += task.Info.Size;
            });
#endif
        }

#if UNITY_WEBGL
        /// <summary>
        /// WebGL 的内存版落地：不做改名落盘，直接按**内存内容**算 MD5 校验（无文件可读），
        /// 不符即丢弃重下。与落盘版一样**只在校验通过后**才计入完成，
        /// 因此失败时不会产生"看似完成"的假状态（Tick 的收尾门控照旧）。
        /// </summary>
        private void CompleteFileInMemory(DownloadTask task)
        {
            if (!_running || _error != null) return;

            if (!_memoryFiles.TryGetValue(task.TempPath, out var bytes) || bytes == null)
            {
                Game.Logger?.Warn("Resource", $"内存中没有本次下载的内容，改为重下：{task.Info.Name}");
                Requeue(task);
                return;
            }

            var actual = ResourceHash.BytesMd5Hex(bytes);
            if (!ResourceHash.Same(actual, task.Info.Hash))
            {
                Game.Logger?.Warn("Resource",
                    $"hash 校验失败（内存）：{task.Info.Name}（期望 {task.Info.Hash}，实际 {actual}）—— 丢弃后重下");
                _memoryFiles.Remove(task.TempPath);
                Requeue(task);
                return;
            }

            // 校验通过：内容留在 _memoryFiles 里供本次加载使用（本平台不做持久化）。
            _finishedFiles++;
            _finishedBytes += task.Info.Size;
        }
#endif

        /// <summary>
        /// 把任务放回队尾重试，**带指数退避**（0.5s 起、上限 8s）：服务端持续 5xx 或弱网时，
        /// 避免在数帧内连打 MaxRetries 次（放大服务端压力且进度抖动）。超过上限则整体失败。
        /// </summary>
        private void Requeue(DownloadTask task)
        {
            task.Attempts++;
            if (task.Attempts > _maxRetries)
            {
                Fail($"重试 {_maxRetries} 次后仍失败：{task.Info.Name}");
                return;
            }

            var delay = RetryDelaySeconds(task.Attempts);
            task.NotBeforeTicks = Stopwatch.GetTimestamp() + (long)(delay * Stopwatch.Frequency);
            _queue.Add(task);
            Game.Logger?.Info("Resource",
                $"重新排队下载：{task.Info.Name}（第 {task.Attempts} 次重试，{delay:F1}s 后）");
        }

        /// <summary>重试退避：0.5s / 1s / 2s / 4s / …，上限 8s。</summary>
        private static double RetryDelaySeconds(int attempts)
        {
            var delay = 0.5 * (1 << Math.Min(Math.Max(attempts - 1, 0), 4));
            return Math.Min(delay, 8.0);
        }

        private void CloseActive(ActiveDownload active, bool deleteTemp)
        {
            try
            {
                active.Request?.Dispose();
            }
            catch (Exception e)
            {
                Game.Logger?.Warn("Resource", $"请求释放失败: {e.Message}");
            }

            try
            {
                active.Stream?.Flush();
                active.Stream?.Dispose();
            }
            catch (Exception e)
            {
                Game.Logger?.Warn("Resource", $"文件句柄释放失败: {e.Message}");
            }

            if (deleteTemp)
                TryDelete(active.Task.TempPath);
        }

        /// <summary>
        /// 中止一个在途请求：**先 Abort、等它真正结束再 Dispose**（Unity 要求在途请求不能释放，
        /// 直接 Dispose 会报错并可能残留句柄）。已结束的请求直接收尾。
        /// </summary>
        private void AbortActive(ActiveDownload active)
        {
            var req = active.Request;
            if (req == null || req.isDone)
            {
                CloseActive(active, deleteTemp: false);
                return;
            }

            try
            {
                req.Abort();
            }
            catch (Exception e)
            {
                Game.Logger?.Warn("Resource", $"中止请求失败: {e.Message}");
            }
            _dying.Add(active);
        }

        /// <summary>收尾已中止的请求：等请求结束再释放（由 Tick 每帧驱动，取消/关停后仍会继续收干净）。</summary>
        private void DrainDying()
        {
            for (var i = _dying.Count - 1; i >= 0; i--)
            {
                var active = _dying[i];
                if (active.Request != null && !active.Request.isDone) continue;

                _dying.RemoveAt(i);
                CloseActive(active, deleteTemp: false);
            }
        }

        /// <summary>
        /// 停滞看门狗：UnityWebRequest 设了 timeout=0（大文件不应被总时限掐断），
        /// 但服务端半开 / 挂死时 downloadedBytes 会永远不涨 —— 不能无限等，
        /// 超过 <see cref="StallTimeoutSeconds"/> 无字节推进就中止本次请求（下一次 Tick 走统一的重排入口）。
        /// </summary>
        private void CheckStall(ActiveDownload active)
        {
            if (active.StallAborted) return;

            var now = Stopwatch.GetTimestamp();
            var bytes = (long)active.Request.downloadedBytes;
            if (bytes != active.LastBytes)
            {
                active.LastBytes = bytes;
                active.LastActivityTicks = now;
                return;
            }

            var idle = (now - active.LastActivityTicks) / (double)Stopwatch.Frequency;
            if (idle < StallTimeoutSeconds) return;

            active.StallAborted = true;
            Game.Logger?.Warn("Resource",
                $"下载 {StallTimeoutSeconds:F0}s 无字节推进，中止本次请求（将按重试策略重排）：{active.Task.Info.Name}");
            try
            {
                active.Request.Abort();
            }
            catch (Exception e)
            {
                Game.Logger?.Warn("Resource", $"Abort 失败: {e.Message}");
            }
        }

        /// <summary>速度采样：把该请求**本帧新到**的字节计入采样窗口（大文件下载期间速度才不会恒为 0）。</summary>
        private void SampleActive(ActiveDownload active)
        {
            if (active.Request == null) return;
            var bytes = (long)active.Request.downloadedBytes;
            if (bytes <= active.LastSampleBytes) return;
            _sampleBytes += bytes - active.LastSampleBytes;
            active.LastSampleBytes = bytes;
        }

        /// <summary>进度回调隔离：业务回调抛异常不能穿出 Tick（会打断整帧并让下载泵停在半状态）。</summary>
        private void SafeReportProgress()
        {
            var callback = _onProgress;
            if (callback == null) return;
            try
            {
                callback(_progress);
            }
            catch (Exception e)
            {
                // 进度回调每帧都会调，重复打日志会刷屏：一次会话只报第一条
                if (!_progressWarned)
                {
                    _progressWarned = true;
                    Game.Logger?.Error("Resource", $"进度回调异常（本次下载内后续同类不再重复）：{e.Message}", e);
                }
            }
        }

        private void UpdateSpeed()
        {
            var now = Stopwatch.GetTimestamp();
            var elapsed = (now - _sampleTicks) / (double)Stopwatch.Frequency;
            if (elapsed < 0.5) return;

            _speed = (float)(_sampleBytes / elapsed);
            _sampleTicks = now;
            _sampleBytes = 0;
        }

        /// <summary>
        /// 标记整体失败：清空队列、中止在途请求，然后**由 Tick 的收尾步骤统一回调**。
        /// 不在这里直接回调，是为了让「回调恰好一次」只有一处出口 —— 失败点分散时最容易漏调，
        /// 而漏调的后果是调用方永久等待（表现为卡在更新界面）。
        /// </summary>
        private void Fail(string error)
        {
            if (_error != null) return;
            _error = error;

            _queue.Clear();
            foreach (var active in _active)
                AbortActive(active);
            _active.Clear();
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception e)
            {
                Game.Logger?.Warn("Resource", $"删除失败 {path}: {e.Message}");
            }
        }
    }
}
