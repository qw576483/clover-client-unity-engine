using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace CloverEngine.Tests
{
    /// <summary>
    /// 极简 WebSocket 服务端（**仅测试用**）：只实现 RFC 6455 里本仓库用得到的那一小块 ——
    /// HTTP 升级握手、单帧读写、客户端掩码解掩码、二进制/文本/关闭/心跳四种 opcode。
    ///
    /// <para>
    /// **为什么不用 <c>HttpListener</c>**：Unity 的 Mono 运行时里它**不支持 WebSocket 升级**
    /// （<c>HttpListenerRequest.IsWebSocketRequest</c> 恒为 false，<c>AcceptWebSocketAsync</c> 不可用），
    /// 用它起"假服务端"会对着合法的升级请求回 400，客户端握手失败、<c>State</c> 停在 <c>Disconnected</c>。
    /// 那是**脚手架**的锅而不是 WS 实现的锅 —— 但它会让人误判成实现 bug，所以这里退到裸 TCP 自己握手。
    /// 这样做反而与真实网关更贴近：网关也不是靠 <c>HttpListener</c>，而是自己解 HTTP 升级头。
    /// </para>
    /// <para>
    /// 服务端发出的帧按 RFC 6455 **不加掩码**（掩码是客户端→服务端的义务）；收到的帧必须解掩码。
    /// </para>
    /// </summary>
    internal sealed class MiniWsServer : IDisposable
    {
        private readonly TcpListener _listener;

        /// <summary>已 accept 的客户端（可能仍挂在处理器里等数据）：Dispose 时要连它们一起关闭。</summary>
        private readonly List<TcpClient> _clients = new List<TcpClient>();

        /// <summary>监听端口（构造时由系统分配空闲端口，避免用例之间抢端口）。</summary>
        public int Port { get; }

        private MiniWsServer(Func<NetworkStream, Task> handler)
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = AcceptLoopAsync(handler);
        }

        /// <summary>拼出客户端要连的 ws:// 地址。<paramref name="path"/> 只影响握手请求行，服务端不做路由。</summary>
        public string Url(string path = "/ws") => $"ws://127.0.0.1:{Port}{path}";

        /// <summary>
        /// 回环 echo 服务（网关语义最小集）：**二进制帧原样回写**，文本帧丢弃。
        /// 丢弃文本是刻意的 —— 与网关 readLoop 一致，也让「客户端把帧发成文本」这类错误能被测出来。
        /// </summary>
        public static MiniWsServer EchoBinary()
        {
            return new MiniWsServer(async stream =>
            {
                while (true)
                {
                    var frame = await Ws.ReadFrameAsync(stream).ConfigureAwait(false);
                    if (frame == null) return;               // 对端关闭 / Close 帧
                    if (frame.Payload.Length == 0) continue; // 心跳或空体控制帧
                    if (!frame.Binary) continue;             // 文本丢弃
                    await Ws.WriteFrameAsync(stream, frame.Payload, true).ConfigureAwait(false);
                }
            });
        }

        /// <summary>握手完成后立刻推一帧（服务端主动推送），随后挂住连接不主动断开。</summary>
        public static MiniWsServer PushThenHold(byte[] frame)
        {
            return new MiniWsServer(async stream =>
            {
                await Ws.WriteFrameAsync(stream, frame, true).ConfigureAwait(false);
                await HoldAsync(stream).ConfigureAwait(false);
            });
        }

        /// <summary>只完成握手与保活，不推任何帧：用于验证「建连成功」与「不该连上时就没连上」。</summary>
        public static MiniWsServer HoldOnly() => new MiniWsServer(HoldAsync);

        private static async Task HoldAsync(NetworkStream stream)
        {
            while (true)
            {
                var frame = await Ws.ReadFrameAsync(stream).ConfigureAwait(false);
                if (frame == null) return;
            }
        }

        /// <summary>循环接受连接并逐个跑处理器；监听停止时自然退出。</summary>
        private async Task AcceptLoopAsync(Func<NetworkStream, Task> handler)
        {
            while (true)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                }
                catch
                {
                    return; // 监听器被 Dispose（用例收尾），正常退出
                }

                lock (_clients) _clients.Add(client);

                _ = Task.Run(async () =>
                {
                    try
                    {
                        using (client)
                        using (var stream = client.GetStream())
                        {
                            await Ws.HandshakeAsync(stream).ConfigureAwait(false);
                            await handler(stream).ConfigureAwait(false);
                        }
                    }
                    catch
                    {
                        // 用例收尾时客户端先关，握手/读帧抛异常属预期；测试脚手架不吞业务错误，故无需上报
                    }
                    finally
                    {
                        lock (_clients) _clients.Remove(client);
                    }
                });
            }
        }

        /// <summary>
        /// 停监听并**关闭全部已 accept 的客户端**。
        /// <para>
        /// ★ 必须连客户端一起关：handler（echo / PushThenHold / HoldOnly）会挂住连接继续读，
        /// 只 Stop 监听器的话这些连接的后台线程与 socket 会活过用例收尾、污染后续用例。
        /// </para>
        /// </summary>
        public void Dispose()
        {
            _listener.Stop();
            lock (_clients)
            {
                foreach (var c in _clients)
                {
                    try { c.Close(); }
                    catch { /* 收尾关闭失败不改变用例结论，无需上报 */ }
                }
                _clients.Clear();
            }
        }

        /// <summary>WebSocket 帧编解码（RFC 6455 子集），抽成静态类便于两个测试文件共用。</summary>
        private static class Ws
        {
            /// <summary>RFC 6455 规定的握手魔数。</summary>
            private const string Guid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

            private const byte OpText = 0x1;
            private const byte OpBinary = 0x2;
            private const byte OpClose = 0x8;
            private const byte OpPing = 0x9;
            private const byte OpPong = 0xA;

            /// <summary>读 HTTP 升级请求头并回 101。返回请求路径（供断言用）。</summary>
            public static async Task<string> HandshakeAsync(NetworkStream stream)
            {
                var head = new StringBuilder();
                var one = new byte[1];
                while (true)
                {
                    var n = await stream.ReadAsync(one, 0, 1).ConfigureAwait(false);
                    if (n <= 0) throw new IOException("client closed during handshake");
                    head.Append((char)one[0]);
                    if (head.Length >= 4
                        && head[head.Length - 4] == '\r' && head[head.Length - 3] == '\n'
                        && head[head.Length - 2] == '\r' && head[head.Length - 1] == '\n')
                        break;
                    if (head.Length > 16384) throw new IOException("handshake header too large");
                }

                var path = "/";
                string key = null;
                foreach (var line in head.ToString().Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var colon = line.IndexOf(':');
                    if (colon <= 0)
                    {
                        // 请求行 GET /ws HTTP/1.1
                        var parts = line.Split(' ');
                        if (parts.Length >= 2) path = parts[1];
                        continue;
                    }

                    var name = line.Substring(0, colon).Trim();
                    // Split(':', 2) 语义：Sec-WebSocket-Key 的 base64 尾部可能带 '='，不能按首个 ':' 之后的全部截断
                    if (string.Equals(name, "Sec-WebSocket-Key", StringComparison.OrdinalIgnoreCase))
                        key = line.Substring(colon + 1).Trim();
                }

                if (string.IsNullOrEmpty(key))
                    throw new IOException("missing Sec-WebSocket-Key header");

                string accept;
                using (var sha1 = SHA1.Create())
                    accept = Convert.ToBase64String(sha1.ComputeHash(Encoding.ASCII.GetBytes(key + Guid)));

                var response = "HTTP/1.1 101 Switching Protocols\r\n"
                               + "Upgrade: websocket\r\n"
                               + "Connection: Upgrade\r\n"
                               + "Sec-WebSocket-Accept: " + accept + "\r\n\r\n";
                var bytes = Encoding.ASCII.GetBytes(response);
                await stream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
                return path;
            }

            /// <summary>读到的一帧：<see cref="Payload"/> 为 null 表示对端已关闭或收到 Close 帧。</summary>
            public sealed class Frame
            {
                /// <summary>帧载荷（已解掩码）。</summary>
                public byte[] Payload;

                /// <summary>是否为二进制帧（false 即文本帧）。</summary>
                public bool Binary;
            }

            /// <summary>
            /// 读一帧并解掩码。返回 null = 对端发了 Close 帧或连接已断；
            /// 返回 <c>Payload.Length == 0</c> = 非数据帧（Ping 已自动回 Pong）。
            ///
            /// 这里return一个对象而不是用 <c>out</c> 参数：<c>async</c> 方法不允许带 out（CS1988）。
            /// </summary>
            public static async Task<Frame> ReadFrameAsync(NetworkStream stream)
            {
                var head = new byte[2];
                if (!await ReadExactAsync(stream, head, 2).ConfigureAwait(false)) return null;

                var opcode = (byte)(head[0] & 0x0F);
                var masked = (head[1] & 0x80) != 0;
                long len = head[1] & 0x7F;

                if (len == 126)
                {
                    var ext = new byte[2];
                    if (!await ReadExactAsync(stream, ext, 2).ConfigureAwait(false)) return null;
                    len = (ext[0] << 8) | ext[1];
                }
                else if (len == 127)
                {
                    var ext = new byte[8];
                    if (!await ReadExactAsync(stream, ext, 8).ConfigureAwait(false)) return null;
                    len = 0;
                    for (var i = 0; i < 8; i++) len = (len << 8) | ext[i];
                }

                var mask = new byte[4];
                if (masked && !await ReadExactAsync(stream, mask, 4).ConfigureAwait(false)) return null;

                var payload = new byte[len];
                if (len > 0 && !await ReadExactAsync(stream, payload, (int)len).ConfigureAwait(false)) return null;

                if (masked)
                    for (var i = 0; i < payload.Length; i++) payload[i] ^= mask[i % 4];

                switch (opcode)
                {
                    case OpClose:
                        return null;
                    case OpPing:
                        // 收到 ping 必须回 pong，否则 ClientWebSocket 会在 KeepAlive 超时后主动断开
                        await WriteFrameAsync(stream, payload, false, OpPong).ConfigureAwait(false);
                        return new Frame { Payload = Array.Empty<byte>() };
                    case OpPong:
                        return new Frame { Payload = Array.Empty<byte>() };
                    case OpText:
                    case OpBinary:
                        return new Frame { Payload = payload, Binary = opcode == OpBinary };
                    default:
                        // 分片（continuation）等本测试用不到的帧型：忽略
                        return new Frame { Payload = Array.Empty<byte>() };
                }
            }

            /// <summary>
            /// 写一帧。服务端→客户端不加掩码；<paramref name="opcode"/> 传 0 时按
            /// <paramref name="binary"/> 自动选文本/二进制。
            /// </summary>
            public static async Task WriteFrameAsync(NetworkStream stream, byte[] payload, bool binary, byte opcode = 0)
            {
                var op = opcode != 0 ? opcode : (binary ? OpBinary : OpText);
                var header = new List<byte>(payload.Length + 10) { (byte)(0x80 | op) };

                if (payload.Length < 126)
                {
                    header.Add((byte)payload.Length);
                }
                else if (payload.Length <= 0xFFFF)
                {
                    header.Add(126);
                    header.Add((byte)(payload.Length >> 8));
                    header.Add((byte)(payload.Length & 0xFF));
                }
                else
                {
                    header.Add(127);
                    for (var i = 7; i >= 0; i--) header.Add((byte)(((long)payload.Length >> (8 * i)) & 0xFF));
                }

                var head = header.ToArray();
                await stream.WriteAsync(head, 0, head.Length).ConfigureAwait(false);
                if (payload.Length > 0)
                    await stream.WriteAsync(payload, 0, payload.Length).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
            }

            /// <summary>读满 count 字节；连接关闭/出错返回 false（调用方据此收尾）。</summary>
            private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, int count)
            {
                var read = 0;
                while (read < count)
                {
                    int n;
                    try
                    {
                        n = await stream.ReadAsync(buffer, read, count - read).ConfigureAwait(false);
                    }
                    catch
                    {
                        return false;
                    }

                    if (n <= 0) return false;
                    read += n;
                }

                return true;
            }
        }
    }
}
