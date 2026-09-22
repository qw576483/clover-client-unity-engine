using System;
using System.Runtime.InteropServices;

namespace CloverEngine
{
    /// <summary>
    /// msquic 原生互操作层（**只做 P/Invoke 与结构体映射，不含任何业务逻辑**）。
    ///
    /// 存在理由：Unity 的 .NET Standard 2.1 档案不含 <c>System.Net.Quic</c>
    /// （Unity 6000.6 的 NetStandard/ref/2.1.0、Managed、MonoBleedingEdge 三处均无该程序集），
    /// 服务端 QUIC 又已就绪，客户端只能原生接入。选 msquic 是因为**一套 C 绑定五端复用**
    /// （Windows / Linux / macOS / iOS / Android 同一套 API），见 <c>Tools~/native/README.md</c>。
    ///
    /// <para>
    /// <b>结构与签名来源</b>：逐字段抄自官方头文件 <c>msquic.h</c>（tag <c>v2.4.16</c>，
    /// 与 <c>Runtime/Plugins/x86_64/msquic.dll</c> 的 <c>FileVersion=2.4.16</c> 一致）。
    /// **不要凭记忆改这里的字段顺序/类型**：QUIC_API_TABLE 是函数指针数组，
    /// 少一个字段或顺序错了，后面所有调用都会跳到错误的函数地址（表现为进程崩溃，而不是报错）。
    /// </para>
    ///
    /// <para>
    /// <b>为什么未使用的表项一律声明为 <see cref="IntPtr"/></b>：
    /// 表项共 31 个，本客户端只用其中 12 个。若把 31 个都写成强类型委托，
    /// 就等于要逐个人工核对 31 份签名——错一个就崩。用 IntPtr 只保证**偏移正确**，
    /// 再用 <see cref="Marshal.GetDelegateForFunctionPointer(Type)"/> 转换真正要调的那几个，
    /// 把出错面从 31 处压到 12 处。
    /// </para>
    /// </summary>
    internal static class MsQuicNative
    {
        /// <summary>本插件对应的 msquic API 版本（QUIC_API_VERSION_2）。</summary>
        public const uint ApiVersion2 = 2;

#if UNITY_IOS && !UNITY_EDITOR
        /// <summary>
        /// 平台原生库名。**iOS 静态库必须走 <c>__Internal</c>**（符号由 Xcode 工程在链接期
        /// 从 <c>Runtime/Plugins/iOS/libmsquic.a</c> 解析）——写 "msquic" 在 iOS 上永远解析失败。
        /// </summary>
        public const string LibraryName = "__Internal";
#else
        /// <summary>
        /// 平台原生库名（Unity 从 Runtime/Plugins/&lt;平台&gt;/ 按此名加载）：
        /// Windows → msquic.dll、Android/Linux → libmsquic.so、macOS → libmsquic.dylib。
        /// iOS 构建走上方分支的 <c>__Internal</c>，不用本名。
        /// </summary>
        public const string LibraryName = "msquic";
#endif

#if UNITY_WEBGL && !UNITY_EDITOR
        // WebGL 不支持原生插件（不能 DllImport）：提供 stub，让唯一调用点
        // （MsQuicRuntime.TryEnsureReady）在运行期得到明确异常并转成"QUIC 不可用"留痕，
        // 同时避免把无法解析的 P/Invoke 编进 WebGL 包
        //（asmdef 的 include/excludePlatforms 是整个程序集级别，无法只排除 Quic 目录）。
        public static uint MsQuicOpenVersion(uint version, out IntPtr apiTable)
        {
            apiTable = IntPtr.Zero;
            throw new PlatformNotSupportedException("msquic 原生插件在 WebGL 不可用（浏览器无原生插件支持）");
        }

        public static void MsQuicClose(IntPtr apiTable)
        {
            throw new PlatformNotSupportedException("msquic 原生插件在 WebGL 不可用（浏览器无原生插件支持）");
        }
#else
        /// <summary>
        /// 打开 msquic 并取回 API 函数表。
        /// 注意：**只有这一个函数是原生导出**，其余 API 全部经函数表调用（msquic 的设计）。
        /// </summary>
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern uint MsQuicOpenVersion(uint version, out IntPtr apiTable);

        /// <summary>释放函数表引用（进程内引用计数为 0 时卸载 msquic）。</summary>
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern void MsQuicClose(IntPtr apiTable);
#endif

        // ------------------------------------------------------------------ 结构体

        /// <summary>一段连续缓冲区（msquic 的基本数据单元）。</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct QuicBuffer
        {
            public uint Length;
            public IntPtr Buffer;
        }

        /// <summary>Registration 配置；两个字段都可为空/零。</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct RegistrationConfig
        {
            public IntPtr AppName;          // const char*
            public int ExecutionProfile;    // QUIC_EXECUTION_PROFILE，0=LOW_LATENCY（默认）
            public int Padding;             // 显式补齐到 8 字节对齐（C 侧枚举只占 4 字节，结构体尾部对齐到指针宽度）
        }

        /// <summary>证书哈希（当前未使用：保留声明以与 <c>msquic.h</c> 逐字段对应，便于后续排查/使用）。</summary>
        [StructLayout(LayoutKind.Sequential, Size = 20)]
        public struct CertificateHash
        {
        }

        /// <summary>
        /// 凭据配置。<b>客户端用 Type=0（NONE）+ Flags=CLIENT</b>，
        /// 证书校验交给系统信任链（<c>结构规则.md</c> §N7 禁止提供"跳过校验"的开关；
        /// 本地自签联调走 <c>mkcert -install</c> 把本地 CA 装进系统信任链，而不是放弃校验）。
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct CredentialConfig
        {
            public int Type;                // QUIC_CREDENTIAL_TYPE
            public int Flags;               // QUIC_CREDENTIAL_FLAGS
            public IntPtr CertificatePtr;   // 联合体（我们恒为 0）
            public IntPtr Principal;
            public IntPtr Reserved;
            public IntPtr AsyncHandler;
            public int AllowedCipherSuites;
            public IntPtr CaCertificateFile;
        }

        /// <summary>
        /// 连接级/配置级设置。
        ///
        /// <para>
        /// 这是整个绑定里**最容易写错**的结构：C 侧是
        /// <c>union { uint64 IsSetFlags; struct { ... 38 个 1 位字段 ... } }</c>
        /// —— 每一位对应"后面那个字段是否要覆盖默认值"，位序**必须与字段声明顺序一致**。
        /// C# 没有位域，故把 IsSetFlags 写成显式 ulong + 常量位移（见 <see cref="IsSetBit"/>），
        /// 字段则按 C 侧顺序排列（C# 的顺序布局会按自然对齐自动插入 padding，与 C 一致）。
        /// </para>
        ///
        /// <para>本客户端只覆盖 4 项：IdleTimeoutMs / KeepAliveIntervalMs / DisconnectTimeoutMs / DatagramReceiveEnabled
        /// （依据 <c>Tools~/native/README.md</c>：keep-alive 10s、idle 30s；Datagram 要用就得先在设置里打开接收）。</para>
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct Settings
        {
            public ulong IsSetFlags;

            public ulong MaxBytesPerKey;
            public ulong HandshakeIdleTimeoutMs;
            public ulong IdleTimeoutMs;
            public ulong MtuDiscoverySearchCompleteTimeoutUs;
            public uint TlsClientMaxSendBuffer;
            public uint TlsServerMaxSendBuffer;
            public uint StreamRecvWindowDefault;
            public uint StreamRecvBufferDefault;
            public uint ConnFlowControlWindow;
            public uint MaxWorkerQueueDelayUs;
            public uint MaxStatelessOperations;
            public uint InitialWindowPackets;
            public uint SendIdleTimeoutMs;
            public uint InitialRttMs;
            public uint MaxAckDelayMs;
            public uint DisconnectTimeoutMs;
            public uint KeepAliveIntervalMs;
            public ushort CongestionControlAlgorithm;
            public ushort PeerBidiStreamCount;
            public ushort PeerUnidiStreamCount;
            public ushort MaxBindingStatelessOperations;
            public ushort StatelessOperationExpirationMs;
            public ushort MinimumMtu;
            public ushort MaximumMtu;
            /// <summary>位域首字节：SendBufferingEnabled:1 PacingEnabled:1 MigrationEnabled:1 DatagramReceiveEnabled:1 ServerResumptionLevel:2 GreaseQuicBitEnabled:1 EcnEnabled:1</summary>
            public byte Flags1;
            public byte MaxOperationsPerDrain;
            public byte MtuDiscoveryMissingProbeCount;
            public uint DestCidUpdateIdleTimeoutMs;
            /// <summary>第二组位域：HyStartEnabled:1 + Reserved:63</summary>
            public ulong Flags2;
            public uint StreamRecvWindowBidiLocalDefault;
            public uint StreamRecvWindowBidiRemoteDefault;
            public uint StreamRecvWindowUnidiDefault;
        }

        /// <summary>
        /// <see cref="Settings.IsSetFlags"/> 的位定义（**顺序 = C 头里字段顺序**，不可重排）。
        /// </summary>
        public static class IsSetBit
        {
            public const int IdleTimeoutMs = 2;
            public const int DisconnectTimeoutMs = 15;
            public const int KeepAliveIntervalMs = 16;
            public const int DatagramReceiveEnabled = 27;
        }

        /// <summary><see cref="Settings.Flags1"/> 的位：DatagramReceiveEnabled（第 4 位）。</summary>
        public const byte Flags1DatagramReceiveEnabled = 1 << 3;

        // ------------------------------------------------------------------ 事件

        /// <summary>连接事件类型（只列我们消费的，其余留注释防误改）。</summary>
        public static class ConnectionEvent
        {
            public const int Connected = 0;
            public const int ShutdownInitiatedByTransport = 1;
            public const int ShutdownInitiatedByPeer = 2;
            public const int ShutdownComplete = 3;
            public const int LocalAddressChanged = 4;
            public const int PeerAddressChanged = 5;
            public const int PeerStreamStarted = 6;
            public const int StreamsAvailable = 7;
            public const int PeerNeedsStreams = 8;
            public const int IdealProcessorChanged = 9;
            public const int DatagramStateChanged = 10;
            public const int DatagramReceived = 11;
            public const int DatagramSendStateChanged = 12;
            public const int Resumed = 13;
            public const int ResumptionTicketReceived = 14;
            public const int PeerCertificateReceived = 15;
        }

        /// <summary>Datagram 发送状态（QUIC_DATAGRAM_SEND_STATE）；<c>&gt;= LOST_DISCARDED</c> 表示"已终结，可释放缓冲"。</summary>
        public static class DatagramSendState
        {
            public const int Unknown = 0;
            public const int Sent = 1;
            public const int LostSuspect = 2;
            public const int LostDiscarded = 3;
            public const int Acknowledged = 4;
            public const int AcknowledgedSpurious = 5;
            public const int Canceled = 6;

            /// <summary>是否为终结状态（此后 msquic 不再引用我们的缓冲）。</summary>
            public static bool IsFinal(int state) => state >= LostDiscarded;
        }

        /// <summary>流事件类型。</summary>
        public static class StreamEvent
        {
            public const int StartComplete = 0;
            public const int Receive = 1;
            public const int SendComplete = 2;
            public const int PeerSendShutdown = 3;
            public const int PeerSendAborted = 4;
            public const int PeerReceiveAborted = 5;
            public const int SendShutdownComplete = 6;
            public const int ShutdownComplete = 7;
            public const int IdealSendBufferSize = 8;
            public const int PeerAccepted = 9;
            public const int CancelOnLoss = 10;
        }

        /// <summary>
        /// 连接事件。
        ///
        /// 用**显式布局**而不是顺序布局：C 侧是 <c>{ int Type; union {...} }</c>，
        /// 联合体大小随编译选项（preview features）变化 —— 顺序布局一旦算小了，
        /// 读写越界；显式布局只声明"我读的那几个字段在哪个偏移"，多余空间留白，
        /// 无论对方联合体多大都不会越界。
        /// </summary>
        [StructLayout(LayoutKind.Explicit, Size = 8 + 64)]
        public struct ConnectionEventData
        {
            [FieldOffset(0)] public int Type;

            /// <summary>CONNECTED：BOOLEAN SessionResumed; uint8 NegotiatedAlpnLength; const uint8* NegotiatedAlpn;</summary>
            [FieldOffset(8)] public byte ConnectedSessionResumed;
            [FieldOffset(9)] public byte ConnectedAlpnLength;
            [FieldOffset(16)] public IntPtr ConnectedAlpn;

            /// <summary>SHUTDOWN_INITIATED_BY_TRANSPORT：QUIC_STATUS Status</summary>
            [FieldOffset(8)] public uint TransportShutdownStatus;
            /// <summary>SHUTDOWN_INITIATED_BY_PEER：QUIC_UINT62 ErrorCode</summary>
            [FieldOffset(8)] public ulong PeerShutdownErrorCode;
            /// <summary>SHUTDOWN_COMPLETE：位域首字节（HandshakeCompleted:1 …）</summary>
            [FieldOffset(8)] public byte ShutdownCompleteFlags;

            /// <summary>DATAGRAM_STATE_CHANGED：BOOLEAN SendEnabled; uint16 MaxSendLength</summary>
            [FieldOffset(8)] public byte DatagramSendEnabled;
            [FieldOffset(10)] public ushort DatagramMaxSendLength;

            /// <summary>DATAGRAM_SEND_STATE_CHANGED：void* ClientContext; QUIC_DATAGRAM_SEND_STATE State</summary>
            [FieldOffset(8)] public IntPtr DatagramSendContext;
            [FieldOffset(16)] public int DatagramSendState;

            /// <summary>DATAGRAM_RECEIVED：const QUIC_BUFFER* Buffer; QUIC_RECEIVE_FLAGS Flags</summary>
            [FieldOffset(8)] public IntPtr DatagramBufferPtr;
            [FieldOffset(16)] public int DatagramReceiveFlags;
        }

        /// <summary>
        /// 流事件（同上：显式布局）。
        /// 关键成员是 RECEIVE —— **回调期间数据由 msquic 持有**，
        /// 必须先拷贝出来再调 <c>StreamReceiveComplete</c>，否则数据被回收/复用。
        /// </summary>
        [StructLayout(LayoutKind.Explicit, Size = 8 + 64)]
        public struct StreamEventData
        {
            [FieldOffset(0)] public int Type;

            /// <summary>START_COMPLETE：QUIC_STATUS Status; QUIC_UINT62 ID; BOOLEAN PeerAccepted:1</summary>
            [FieldOffset(8)] public uint StartStatus;
            [FieldOffset(16)] public ulong StartId;

            /// <summary>RECEIVE：uint64 AbsoluteOffset; uint64 TotalBufferLength; const QUIC_BUFFER* Buffers; uint32 BufferCount; QUIC_RECEIVE_FLAGS Flags</summary>
            [FieldOffset(8)] public ulong ReceiveAbsoluteOffset;
            [FieldOffset(16)] public ulong ReceiveTotalLength;
            [FieldOffset(24)] public IntPtr ReceiveBuffersPtr;
            [FieldOffset(32)] public uint ReceiveBufferCount;
            [FieldOffset(36)] public int ReceiveFlags;

            /// <summary>SEND_COMPLETE：BOOLEAN Canceled; void* ClientContext</summary>
            [FieldOffset(8)] public byte SendCanceled;
            [FieldOffset(16)] public IntPtr SendClientContext;

            /// <summary>SHUTDOWN_COMPLETE：BOOLEAN ConnectionShutdown; …bits…; QUIC_UINT62 ConnectionErrorCode; QUIC_STATUS ConnectionCloseStatus</summary>
            [FieldOffset(8)] public byte StreamShutdownConnectionShutdown;
            [FieldOffset(16)] public ulong StreamShutdownErrorCode;
            [FieldOffset(24)] public uint StreamShutdownStatus;
        }

        // ------------------------------------------------------------------ 委托签名

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate uint ConnectionCallback(IntPtr connection, IntPtr context, ref ConnectionEventData evt);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate uint StreamCallback(IntPtr stream, IntPtr context, ref StreamEventData evt);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate uint RegistrationOpenFn(ref RegistrationConfig config, out IntPtr registration);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void RegistrationCloseFn(IntPtr registration);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate uint ConfigurationOpenFn(IntPtr registration, ref QuicBuffer alpnBuffers, uint alpnBufferCount,
            IntPtr settings, uint settingsSize, IntPtr context, out IntPtr configuration);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void ConfigurationCloseFn(IntPtr configuration);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate uint ConfigurationLoadCredentialFn(IntPtr configuration, ref CredentialConfig credConfig);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate uint ConnectionOpenFn(IntPtr registration, ConnectionCallback handler, IntPtr context, out IntPtr connection);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void ConnectionCloseFn(IntPtr connection);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void ConnectionShutdownFn(IntPtr connection, uint flags, ulong errorCode);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate uint ConnectionStartFn(IntPtr connection, IntPtr configuration, ushort family, IntPtr serverName, ushort serverPort);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate uint StreamOpenFn(IntPtr connection, uint flags, StreamCallback handler, IntPtr context, out IntPtr stream);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void StreamCloseFn(IntPtr stream);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate uint StreamStartFn(IntPtr stream, uint flags);

        /// <summary>StreamShutdown（当前未调用：保留声明以与 <c>msquic.h</c> 对应；流的终止走 StreamClose）。</summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate uint StreamShutdownFn(IntPtr stream, uint flags, ulong errorCode);

        /// <summary>
        /// StreamSend。**缓冲描述符按指针传**（不是 <c>ref</c>）：msquic 的发送是异步的，
        /// 它保存的是我们给的 <c>QUIC_BUFFER*</c>，直到 SEND_COMPLETE 才会读。
        /// 传 <c>ref</c>（= 指向托管栈帧的指针）会在方法返回后失效，msquic 读到的是垃圾 ——
        /// 表现为"服务端把长度读成天文数字然后断开"（真踩过）。故调用方必须传**原生堆**上的描述符。
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate uint StreamSendFn(IntPtr stream, IntPtr buffers, uint bufferCount, uint flags, IntPtr clientContext);

        /// <summary>StreamReceiveComplete（当前未调用：本客户端是**同步消费**收包，刻意不调它 ——
        /// 见 <c>QuicConnection</c> 的 Receive 分支长注释；声明保留以与 <c>msquic.h</c> 对应）。</summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void StreamReceiveCompleteFn(IntPtr stream, ulong bufferLength);

        /// <summary>DatagramSend。缓冲描述符同样按指针传（理由见 <see cref="StreamSendFn"/>）。</summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate uint DatagramSendFn(IntPtr connection, IntPtr buffers, uint bufferCount, uint flags, IntPtr clientSendContext);

        // ------------------------------------------------------------------ 函数表

        /// <summary>
        /// QUIC_API_TABLE（v2，共 31 项，顺序与头文件 <c>msquic.h</c> 的 <c>typedef struct QUIC_API_TABLE</c> 严格一致）。
        /// 前 12 项本客户端要用（已给出强类型访问器），其余保持 IntPtr 仅用于占位对齐。
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct ApiTable
        {
            // 0-2 上下文与回调
            public IntPtr SetContext;
            public IntPtr GetContext;
            public IntPtr SetCallbackHandler;
            // 3-4 参数
            public IntPtr SetParam;
            public IntPtr GetParam;
            // 5-7 Registration
            public IntPtr RegistrationOpen;
            public IntPtr RegistrationClose;
            public IntPtr RegistrationShutdown;
            // 8-10 Configuration
            public IntPtr ConfigurationOpen;
            public IntPtr ConfigurationClose;
            public IntPtr ConfigurationLoadCredential;
            // 11-14 Listener
            public IntPtr ListenerOpen;
            public IntPtr ListenerClose;
            public IntPtr ListenerStart;
            public IntPtr ListenerStop;
            // 15-20 Connection
            public IntPtr ConnectionOpen;
            public IntPtr ConnectionClose;
            public IntPtr ConnectionShutdown;
            public IntPtr ConnectionStart;
            public IntPtr ConnectionSetConfiguration;
            public IntPtr ConnectionSendResumptionTicket;
            // 21-27 Stream
            public IntPtr StreamOpen;
            public IntPtr StreamClose;
            public IntPtr StreamStart;
            public IntPtr StreamShutdown;
            public IntPtr StreamSend;
            public IntPtr StreamReceiveComplete;
            public IntPtr StreamReceiveSetEnabled;
            // 28 Datagram
            public IntPtr DatagramSend;
            // 29-30 证书/恢复票据校验完成（v2.2 起）
            public IntPtr ConnectionResumptionTicketValidationComplete;
            public IntPtr ConnectionCertificateValidationComplete;

            /// <summary>把某个表项转成强类型委托。</summary>
            public T Fn<T>(IntPtr slot) where T : Delegate
            {
                if (slot == IntPtr.Zero)
                    throw new InvalidOperationException($"msquic API table slot is null: {typeof(T).Name}");
                return Marshal.GetDelegateForFunctionPointer<T>(slot);
            }
        }
    }
}
