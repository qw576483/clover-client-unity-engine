using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace CloverEngine
{
    /// <summary>
    /// 局域网寻服的 UDP 收发层：一个 socket + 一条收包线程（`CloverLan-Udp`）。
    ///
    /// <para>
    /// 职责边界（结构规则.md §五 N13）：**只做收发**，不解析、不判状态、不发事件 ——
    /// 收到的字节原样交给回调（<c>LanBrowser.Accept</c>），窗口是否结束由本类按
    /// <see cref="ReceiveTimeoutMs"/> 轮询自判后回调 <c>onFinished</c>。
    /// </para>
    ///
    /// <para>
    /// <b>为什么窗口收尾不依赖 <see cref="Game.Timer"/></b>：寻服发生在 <c>Game.Launch</c> 之后、
    /// 连接之前（甚至可能在没有 Launch 的编辑器测试里用），Timer 可能没被驱动；
    /// 而收包线程本来就要阻塞等待，让它自己按耗时决定收尾，既不引入第二个线程，
    /// 也不会有「Timer 没跑 ⇒ 永远停在 Scanning」这种静默卡死。
    /// </para>
    ///
    /// <para>
    /// 收包线程的纪律：只收 → 回调 → 继续；**任何异常都不许冒出线程**
    /// （异常逃逸会杀进程，且线程里没有谁的 catch 能兜住）。
    /// </para>
    /// </summary>
    internal sealed class LanSocket
    {
        /// <summary>
        /// 单次收包阻塞上限（毫秒）：到点返回，让收包线程能周期性检查「窗口是否已结束 / 是否被 Stop」。
        /// <c>SocketException.TimedOut</c> 是**正常**返回路径，不算错误。
        /// </summary>
        public const int ReceiveTimeoutMs = 200;

        /// <summary>连续 socket 错误达到该次数就提前收尾（防病态 socket 把窗口耗在空转上）。</summary>
        private const int MaxSocketErrors = 64;

        /// <summary>接收缓冲：比协议上限多 1 字节 —— 多出来的那一字节正是「超长包」的判据。</summary>
        private const int ReceiveBufferBytes = LanProtocol.MaxDatagramBytes + 1;

        private readonly Socket _socket;
        private volatile bool _stop;
        private int _errorCount;
        private int _receivedCount;

        /// <summary>"超长数据报"告警只打一次（防高频脏包刷屏）。</summary>
        private int _oversizeLogged;

        private LanSocket(Socket socket)
        {
            _socket = socket;
        }

        /// <summary>
        /// 创建并绑定 UDP socket（绑 <c>0.0.0.0:0</c>，即临时端口：应答单播回到本端口）。
        /// 创建失败返回 null 并给出原因 —— 调用方必须据此打 Error 并收尾，**不许静默**。
        /// </summary>
        /// <param name="error">失败原因（成功为 null）</param>
        public static LanSocket TryCreate(out string error)
        {
            error = null;
            Socket socket = null;
            try
            {
                socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
                {
                    ReceiveTimeout = ReceiveTimeoutMs,
                    // 255.255.255.255 与 x.x.x.255 都要求它；默认值在不同运行时下不一致，显式设置。
                    EnableBroadcast = true,
                };
                socket.Bind(new IPEndPoint(IPAddress.Any, 0));
                return new LanSocket(socket);
            }
            catch (Exception ex)
            {
                // Bind 失败时释放刚创建的 socket：否则句柄要等 GC 才回收（旧实现直接 return null）
                try
                {
                    socket?.Close();
                }
                catch (Exception closeEx)
                {
                    Game.Logger?.Warn("Lan", $"关闭绑定失败的 UDP socket 出错：{closeEx.Message}");
                }

                error = ex.Message;
                return null;
            }
        }

        /// <summary>
        /// 向目标发一份报文。失败打 Warn（含目标）并返回 false ——
        /// 单个目标发不出去不该让整轮不发（比如某网卡的子网广播地址被系统拒了）。
        /// </summary>
        /// <param name="payload">报文</param>
        /// <param name="target">目标地址</param>
        public bool Send(byte[] payload, IPEndPoint target)
        {
            if (_stop)
            {
                Game.Logger?.Warn("Lan", $"发送失败：socket 已停止（目标 {target}）");
                return false;
            }

            try
            {
                var sent = _socket.SendTo(payload, target);
                if (sent != payload.Length)
                {
                    Game.Logger?.Warn("Lan", $"发送不完整（{sent}/{payload.Length} 字节，目标 {target}）");
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                Game.Logger?.Warn("Lan", $"发送查询失败（目标 {target}）：{ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 启动收包线程：收 → 回调 → 继续，直到窗口耗尽、被 <see cref="Stop"/> 或连续出错过多。
        /// 无论以哪条路径结束，都会先关 socket 再回调 <paramref name="onFinished"/>（收尾只这一次）。
        /// </summary>
        /// <param name="durationMs">扫描窗口（毫秒）</param>
        /// <param name="onDatagram">收到一份报文（参数：字节、来源 IP）</param>
        /// <param name="onFinished">窗口结束（含提前结束）时回调一次</param>
        public void Start(int durationMs, Action<byte[], string> onDatagram, Action onFinished)
        {
            // 线程不需要在字段里留引用（Stop 明确不 Join，见下方注释）：用完即走的局部变量
            var thread = new Thread(() => Loop(durationMs, onDatagram, onFinished))
            {
                IsBackground = true,   // 不阻止进程/编辑器退出：寻服随时可以被丢掉
                Name = "CloverLan-Udp",
            };
            thread.Start();
        }

        /// <summary>
        /// 停止收包并关闭 socket（幂等）。从主线程调用时，正在阻塞的收包会立刻以异常返回，
        /// 收包线程随即走收尾路径并退出；<b>不等线程 join</b>（避免把主线程卡在收包超时上）。
        /// </summary>
        public void Stop()
        {
            if (_stop)
                return;

            _stop = true;
            CloseSocket();
        }

        private void Loop(int durationMs, Action<byte[], string> onDatagram, Action onFinished)
        {
            var watch = Stopwatch.StartNew();
            var buffer = new byte[ReceiveBufferBytes];

            try
            {
                while (!_stop && watch.ElapsedMilliseconds < durationMs)
                {
                    EndPoint remote = new IPEndPoint(IPAddress.Any, 0);

                    int received;
                    try
                    {
                        received = _socket.ReceiveFrom(buffer, ref remote);
                    }
                    catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
                    {
                        continue;   // 超时是正常节奏：回循环头判窗口/停止标志
                    }
                    catch (SocketException ex) when (ex.SocketErrorCode == SocketError.MessageSize)
                    {
                        // 超长数据报：Windows 上 ReceiveFrom 不截断，直接以 MessageSize 返回
                        //（缓冲设计的多 1 字节判据只覆盖"能被截断进缓冲"的运行时；这里按设计
                        // 干净拒收：忽略并留痕，**不计入 socket 错误累计**，避免脏包把本轮提前收尾）。
                        if (_stop)
                            break;
                        if (Interlocked.Exchange(ref _oversizeLogged, 1) == 0)
                            Game.Logger?.Warn("Lan",
                                $"收到超长数据报（超过 {ReceiveBufferBytes - 1} 字节），已忽略（后续同类只计数）");
                        continue;
                    }
                    catch (SocketException ex)
                    {
                        if (_stop)
                            break;

                        // Windows 上对已关闭端口发过 UDP 后，再收会报 ConnectionReset（ICMP 回执），
                        // 这是**正常现象**不是故障；但其它错误码必须可见 —— 只报第一次，防刷屏。
                        if (_errorCount++ == 0)
                        {
                            Game.Logger?.Warn("Lan",
                                $"收包异常（第 1 次，后续同类只计数）：{ex.SocketErrorCode} {ex.Message}");
                        }

                        if (_errorCount >= MaxSocketErrors)
                        {
                            Game.Logger?.Warn("Lan", $"收包异常累计 {_errorCount} 次，提前收尾本轮扫描");
                            break;
                        }

                        continue;
                    }
                    catch (ObjectDisposedException)
                    {
                        break;   // Stop 关了 socket
                    }
                    catch (Exception ex)
                    {
                        if (_stop)
                            break;
                        Game.Logger?.Warn("Lan", $"收包线程异常（继续本轮）：{ex.Message}");
                        continue;
                    }

                    if (received <= 0)
                        continue;

                    _receivedCount++;

                    // 交给上层解析：异常绝不能冒出线程（会杀进程）。
                    try
                    {
                        var datagram = new byte[received];
                        Buffer.BlockCopy(buffer, 0, datagram, 0, received);
                        onDatagram?.Invoke(datagram, (remote as IPEndPoint)?.Address?.ToString());
                    }
                    catch (Exception ex)
                    {
                        Game.Logger?.Warn("Lan", $"处理应答报文失败：{ex.Message}");
                    }
                }
            }
            finally
            {
                CloseSocket();
                Game.Logger?.Info("Lan",
                    $"收包线程结束：收到 {_receivedCount} 个报文，socket 异常 {_errorCount} 次");

                try
                {
                    onFinished?.Invoke();
                }
                catch (Exception ex)
                {
                    Game.Logger?.Warn("Lan", $"扫描收尾回调异常：{ex.Message}");
                }
            }
        }

        private void CloseSocket()
        {
            try
            {
                // _socket 是 readonly 且由 TryCreate 保证非空（无需空判断）
                _socket.Close();
            }
            catch (Exception ex)
            {
                // 关不掉不影响后续（socket 会被 GC 回收），但必须留痕。
                Game.Logger?.Warn("Lan", $"关闭 UDP socket 失败：{ex.Message}");
            }
        }
    }
}
