using System;
using System.Runtime.InteropServices;

namespace CloverEngine
{
    /// <summary>
    /// 进程级 msquic 运行时：持有 **API 表 / Registration / Configuration** 三样东西，
    /// 进程内只创建一次、**不主动释放**（进程退出时由 OS 回收）。
    ///
    /// <para>
    /// <b>为什么不按连接创建/释放</b>：msquic 的这些句柄开销大且是引用计数语义，
    /// 每连接创建会在频繁重连（网络抖动）时反复申请释放原生资源；
    /// 而它们本身是线程安全、可在多连接间共享的（ALPN 恒为 <c>clover-quic</c>，
    /// 设置项也是全局一致的），因此"进程级单例"既简单又更稳。
    /// </para>
    ///
    /// <para>
    /// <b>可用性探测</b>：<see cref="TryEnsureReady"/> 是唯一入口。首次调用尝试加载原生库；
    /// 失败时**记住失败原因**并让调用方（<c>TransportCapabilities</c>）据此把 QUIC 线路裁掉，
    /// 同时把原因写进日志 —— 与引擎既有风格一致：能力缺失要**可见**，不静默降级。
    /// </para>
    /// </summary>
    internal static class MsQuicRuntime
    {
        private static readonly object Gate = new object();
        private static bool _initialized;
        private static bool _available;
        private static string _unavailableReason = "not probed";
        private static IntPtr _apiTablePtr;
        private static MsQuicNative.ApiTable _api;
        private static IntPtr _registration;
        private static IntPtr _configuration;

        /// <summary>原生库是否可用（探测结果缓存；未探测时返回 false，由调用方走 <see cref="TryEnsureReady"/>）。</summary>
        public static bool IsAvailable
        {
            get
            {
                lock (Gate)
                {
                    return _available;
                }
            }
        }

        /// <summary>
        /// 读取诊断开关环境变量。WebGL 没有进程环境变量（浏览器沙箱），直接取默认值（关）——
        /// 旧实现无平台分支，WebGL 上这些开关恒失效（且不该假设 GetEnvironmentVariable 总有意义）。
        /// </summary>
        private static bool ReadEnvFlag(string name)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return false;
#else
            return Environment.GetEnvironmentVariable(name) == "1";
#endif
        }

        /// <summary>
        /// 原生调用逐步跟踪（环境变量 <c>CLOVER_QUIC_TRACE=1</c> 打开）。
        ///
        /// <para>
        /// 为什么需要这种"土办法"：msquic 用错时的失败是**进程级断言**（0xC0000420），
        /// 没有托管异常、没有堆栈 —— 只能靠"崩之前最后一条日志"定位是哪个调用。
        /// 默认关闭（正常跑不刷屏）。
        /// </para>
        /// </summary>
        public static readonly bool VerboseTrace = ReadEnvFlag("CLOVER_QUIC_TRACE");

        /// <summary>诊断开关：不释放发送缓冲（判定"崩溃是否由释放时机引起"）。</summary>
        public static readonly bool NoFreeSendBuffers = ReadEnvFlag("CLOVER_QUIC_NOFREE");

        /// <summary>诊断开关：跳过 ConnectionShutdown，直接 ConnectionClose。</summary>
        public static readonly bool SkipConnectionShutdown = ReadEnvFlag("CLOVER_QUIC_SKIP_SHUTDOWN");

        /// <summary>按开关打一条原生调用跟踪。</summary>
        public static void Trace(string what)
        {
            if (VerboseTrace)
                Game.Logger?.Info("Quic", "[trace] " + what);
        }

        /// <summary>不可用原因（一句话，用于日志与调试面板）。</summary>
        public static string UnavailableReason
        {
            get
            {
                lock (Gate)
                {
                    return _unavailableReason;
                }
            }
        }

        /// <summary>ALPN（与服务端 <c>internal/transport/net/quic</c> 一致，见 Tools~/native/README.md）。</summary>
        public const string Alpn = "clover-quic";

        /// <summary>连接级 keep-alive 间隔（毫秒）——Network.framework/msquic 的 QUIC PING，不占用应用流。</summary>
        private const uint KeepAliveIntervalMs = 10_000;

        /// <summary>连接空闲超时（毫秒）。</summary>
        private const uint IdleTimeoutMs = 30_000;

        /// <summary>断开宽限期（毫秒）：网络抖动时不要把连接立刻判死。</summary>
        private const uint DisconnectTimeoutMs = 5_000;

        // 必须保持存活的原生内存（ALPN 字符串与缓冲区），否则 msquic 持有的是野指针。
        private static IntPtr _alpnAnsi;
        private static IntPtr _alpnBufferStruct;

        /// <summary>
        /// 确保运行时就绪；返回是否可用。**可从任意线程调用**（内部加锁，只初始化一次）。
        /// </summary>
        /// <param name="reason">不可用原因（可用时为 null）。</param>
        public static bool TryEnsureReady(out string reason)
        {
            lock (Gate)
            {
                if (!_initialized)
                {
                    _initialized = true;
                    try
                    {
                        Initialize();
                        _available = true;
                        _unavailableReason = null;
                        Game.Logger?.Info("Quic", $"msquic 就绪 (v{MsQuicNative.ApiVersion2}, ALPN={Alpn})");
                    }
                    catch (Exception e)
                    {
                        _available = false;
                        _unavailableReason = e.Message;
                        Game.Logger?.Warn("Quic", $"msquic 不可用，QUIC 线路将被裁掉: {e.Message}");
                    }
                }

                reason = _unavailableReason;
                return _available;
            }
        }

        // ------------------------------------------------------------------ 活连接登记（域卸载兜底）
        //
        // 为什么必须有：msquic 的回调是**原生函数指针 → 托管委托**。Unity 在退出 Play 模式 /
        // 重编译时会**卸载脚本域**，此时若还有连接没关干净，msquic 的工作线程会回调到已卸载的
        // 托管代码 —— 结果是**进程级崩溃**（Windows 事件日志里
        // `Faulting module name: msquic.dll`，异常码 0xc0000420 = STATUS_ASSERTION_FAILURE，编辑器直接消失）。
        // 因此：① 每个连接登记在册；② 域卸载/退出前把它们**同步**关干净（msquic 保证 ConnectionClose
        // 之后不再回调）；③ 测试的 TearDown 也必须走这条路（断言失败时后面的清理代码根本不会执行）。
        private static readonly System.Collections.Generic.HashSet<QuicConnection> LiveConnections = new();

        internal static void Register(QuicConnection conn)
        {
            lock (Gate)
            {
                LiveConnections.Add(conn);
            }
        }

        internal static void Unregister(QuicConnection conn)
        {
            lock (Gate)
            {
                LiveConnections.Remove(conn);
            }
        }

        /// <summary>活连接数（诊断/自证用）。</summary>
        public static int LiveConnectionCount
        {
            get
            {
                lock (Gate)
                {
                    return LiveConnections.Count;
                }
            }
        }

        /// <summary>
        /// **同步**关掉所有活连接（域卸载 / 退出前必须调用）。
        /// 语义是"尽力而为但必须同步完成"：每个连接内部会等 SHUTDOWN_COMPLETE（最多 500ms）再强制关句柄。
        /// </summary>
        /// <param name="why">触发原因（写日志用，便于事后定位是哪条路径关的）。</param>
        public static void ShutdownAllConnections(string why)
        {
            QuicConnection[] snapshot;
            lock (Gate)
            {
                if (LiveConnections.Count == 0)
                    return;
                snapshot = new QuicConnection[LiveConnections.Count];
                LiveConnections.CopyTo(snapshot);
            }

            Game.Logger?.Info("Quic", $"关闭全部 QUIC 连接（{snapshot.Length} 条，原因：{why}）");
            foreach (var conn in snapshot)
            {
                try
                {
                    conn?.CloseNow(why);
                }
                catch (Exception e)
                {
                    Game.Logger?.Warn("Quic", $"关闭连接失败（{why}）: {e.Message}");
                }
            }
        }

#if UNITY_EDITOR
        /// <summary>
        /// 编辑器侧的兜底钩子：**域卸载前**与**退出 Play 模式时**把连接关干净。
        /// 没有它，任何"测试断言失败 / 强停 Play / 改脚本触发重编译"都会留下活连接 ⇒ 崩溃。
        /// （Runtime 程序集里用 #if UNITY_EDITOR 引用 UnityEditor 是允许的：出包时这段不参与编译。）
        /// </summary>
        [UnityEditor.InitializeOnLoadMethod]
        private static void HookEditorTeardown()
        {
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += () => ShutdownAllConnections("assembly reload");
            UnityEditor.EditorApplication.playModeStateChanged += state =>
            {
                if (state == UnityEditor.PlayModeStateChange.ExitingPlayMode)
                    ShutdownAllConnections("exit play mode");
            };
        }
#endif

        /// <summary>播放器侧的兜底钩子（真机/Standalone 退出前）。</summary>
#if UNITY_5_3_OR_NEWER
        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void HookRuntimeQuit()
        {
            UnityEngine.Application.quitting += () => ShutdownAllConnections("application quitting");
        }
#endif

        /// <summary>已就绪时返回 API 表与三个句柄；未就绪抛异常（调用方先用 <see cref="TryEnsureReady"/> 判定）。</summary>
        public static void Require(out MsQuicNative.ApiTable api, out IntPtr registration, out IntPtr configuration)
        {
            if (!TryEnsureReady(out var reason))
                throw new InvalidOperationException($"msquic unavailable: {reason}");
            api = _api;
            registration = _registration;
            configuration = _configuration;
        }

        private static void Initialize()
        {
            // 1) 打开 API 表（唯一的原生导出函数）
            var status = MsQuicNative.MsQuicOpenVersion(MsQuicNative.ApiVersion2, out _apiTablePtr);
            ThrowIfFailed(status, "MsQuicOpenVersion");
            if (_apiTablePtr == IntPtr.Zero)
                throw new InvalidOperationException("MsQuicOpenVersion 返回了空函数表");

            _api = Marshal.PtrToStructure<MsQuicNative.ApiTable>(_apiTablePtr);

            // 2) Registration（AppName 仅作诊断显示；执行档用 LOW_LATENCY 默认值）
            var appName = Marshal.StringToHGlobalAnsi("CloverUnity");
            try
            {
                var regConfig = new MsQuicNative.RegistrationConfig { AppName = appName, ExecutionProfile = 0 };
                var openRegistration = _api.Fn<MsQuicNative.RegistrationOpenFn>(_api.RegistrationOpen);
                ThrowIfFailed(openRegistration(ref regConfig, out _registration), "RegistrationOpen");
            }
            finally
            {
                Marshal.FreeHGlobal(appName);
            }

            // 3) Configuration：ALPN = clover-quic + 连接级设置
            _alpnAnsi = Marshal.StringToHGlobalAnsi(Alpn);
            _alpnBufferStruct = Marshal.AllocHGlobal(Marshal.SizeOf<MsQuicNative.QuicBuffer>());
            var alpnBuffer = new MsQuicNative.QuicBuffer { Length = (uint)Alpn.Length, Buffer = _alpnAnsi };
            Marshal.StructureToPtr(alpnBuffer, _alpnBufferStruct, false);

            var settings = new MsQuicNative.Settings
            {
                IdleTimeoutMs = IdleTimeoutMs,
                KeepAliveIntervalMs = KeepAliveIntervalMs,
                DisconnectTimeoutMs = DisconnectTimeoutMs,
                Flags1 = MsQuicNative.Flags1DatagramReceiveEnabled                 // 允许接收 Datagram（不可靠通道）
            };
            settings.IsSetFlags =
                (1UL << MsQuicNative.IsSetBit.IdleTimeoutMs) |
                (1UL << MsQuicNative.IsSetBit.KeepAliveIntervalMs) |
                (1UL << MsQuicNative.IsSetBit.DisconnectTimeoutMs) |
                (1UL << MsQuicNative.IsSetBit.DatagramReceiveEnabled);

            var settingsSize = (uint)Marshal.SizeOf<MsQuicNative.Settings>();
            var settingsPtr = Marshal.AllocHGlobal((int)settingsSize);
            try
            {
                Marshal.StructureToPtr(settings, settingsPtr, false);
                var openConfiguration = _api.Fn<MsQuicNative.ConfigurationOpenFn>(_api.ConfigurationOpen);
                ThrowIfFailed(
                    openConfiguration(_registration, ref alpnBuffer, 1, settingsPtr, settingsSize, IntPtr.Zero, out _configuration),
                    "ConfigurationOpen");
            }
            finally
            {
                Marshal.FreeHGlobal(settingsPtr);
            }

            // 4) 客户端凭据：Type=NONE + CLIENT。
            //    §N7 硬约束：**不得**设置 NO_CERTIFICATE_VALIDATION —— 校验走系统信任链。
            //    本地自签联调的正确做法是 `mkcert -install` 把本地 CA 装进系统信任链（见 Tools~/native/README.md）。
            var cred = new MsQuicNative.CredentialConfig
            {
                Type = 0,                                                            // QUIC_CREDENTIAL_TYPE_NONE
                Flags = 0x00000001                                                   // QUIC_CREDENTIAL_FLAG_CLIENT
            };
            var loadCredential = _api.Fn<MsQuicNative.ConfigurationLoadCredentialFn>(_api.ConfigurationLoadCredential);
            ThrowIfFailed(loadCredential(_configuration, ref cred), "ConfigurationLoadCredential");
        }

        /// <summary>
        /// QUIC_STATUS 判定：Windows（Schannel 版）语义是 HRESULT，<c>&gt;= 0</c> 视为成功
        /// （含 <c>QUIC_STATUS_PENDING</c> 等正值"待完成"状态）。**不能用 <c>== 0</c> 判成功**，
        /// 否则会把 PENDING 之类的正常返回值当失败。
        /// </summary>
        private static void ThrowIfFailed(uint status, string what)
        {
            if ((int)status >= 0)
                return;
            throw new InvalidOperationException($"{what} failed: 0x{status:X8}");
        }
    }
}
