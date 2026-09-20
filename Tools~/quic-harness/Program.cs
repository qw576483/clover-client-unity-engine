using System;
using System.Text;
using System.Threading;

namespace CloverEngine
{
    /// <summary>
    /// QUIC 原生绑定的**逐步驱动试验台**。
    ///
    /// <para>
    /// 用法：<c>dotnet run --project Tools~/quic-harness</c>
    /// （需要本机网关已启用 QUIC：配了 <c>gateway.tls_cert</c>，监听 <c>127.0.0.1:8003</c>）
    /// </para>
    ///
    /// <para>
    /// <b>为什么要"逐步 + 每步先打标记"</b>：msquic 用错时的失败是**进程级崩溃**
    /// （STATUS_ASSERTION_FAILURE），没有托管异常、没有堆栈 —— 唯一可靠的定位手段就是
    /// "最后打印出来的那个标记 = 崩在下一步"。所以每一步都先 <c>Mark("xxx")</c> 再动手。
    /// </para>
    /// </summary>
    internal static class Program
    {
        private const string GatewayQuic = "127.0.0.1:8003";
        private static int _failures;

        private static void Mark(string what) => Console.WriteLine($"\n===== [STEP] {what} =====");

        private static void Ok(string what) => Console.WriteLine($"  ✓ {what}");

        private static void Bad(string what, string why)
        {
            _failures++;
            Console.WriteLine($"  ✗ {what} :: {why}");
        }

        private static int Main(string[] args)
        {
            // 模式（用于二分定位崩溃）：plain=只连+断；dgram=连+发Datagram+断；login=连+请求回包+断；all=全部
            var mode = args.Length > 0 ? args[0] : "all";
            Console.WriteLine($"QUIC 试验台启动（模式={mode}，原生库: msquic.dll）");

            if (mode != "all")
                return RunSingleMode(mode);

            // ---- 0. 原生库加载 + API 表 + Registration/Configuration/凭据 ----
            Mark("MsQuicRuntime.TryEnsureReady（加载 dll + 打开 API 表 + 建 Configuration）");
            if (!MsQuicRuntime.TryEnsureReady(out var reason))
            {
                Bad("msquic 就绪", reason);
                Console.WriteLine("结论：原生库/绑定不可用，后面的用例无意义。");
                return 2;
            }
            MsQuicRuntime.Require(out var api, out _, out _);
            Console.WriteLine($"  API 表: ConnectionOpen=0x{api.ConnectionOpen.ToInt64():X} StreamSend=0x{api.StreamSend.ToInt64():X} DatagramSend=0x{api.DatagramSend.ToInt64():X}");
            Ok("原生绑定就绪");

            // ---- 1. 连接 + 握手 ----
            Mark("连接网关 QUIC 端口并等握手");
            var conn = ConnectOrFail(GatewayQuic, out var err);
            if (conn == null)
            {
                Bad("QUIC 握手", err);
                Console.WriteLine("结论：连不上（网关没开 QUIC / 证书不受信）。后面的用例跳过。");
                return 3;
            }
            Ok($"已连接，Datagram 支持 = {conn.SupportsUnreliable}");

            // ---- 2. 请求/回包往返（真实业务路径）----
            // 消息号一律引用 EMsg 真实常量（试验台链接了 Runtime/Network/EMsg.cs）：
            // 此前硬编码 4110/4003 与 EMsg 脱钩，消息号调整后试验台仍发旧号却照样判通过，会掩盖真实回归。
            Mark("发登录帧（非法 token）并等回包");
            var reply = RequestReply(conn, 42, EMsg.Login, "{\"token\":\"invalid-token-for-harness\"}", 15000);
            if (reply == null)
                Bad("请求/回包往返", "15s 内没收到回包（服务端应回 ELoginReply{success:false} 或 EMsg.Error）");
            else if (ClientFrame.TryDecode(reply, out var rid, out var mid, out _))
                Ok($"收到回包 len={reply.Length} requestID={rid} msgID={mid}（期望 requestID=42）");
            else
                Bad("回包解码", $"帧头不足 8 字节: {reply.Length}");

            // ---- 3. Datagram（不可靠通道）----
            Mark("发一帧 Datagram（不可靠通道冒烟）");
            if (conn.SupportsUnreliable)
            {
                var dgram = ClientFrame.Encode(0, EMsg.PushDataSync, Encoding.UTF8.GetBytes("{}"));
                conn.SendUnreliable(dgram, 0, dgram.Length);
                Thread.Sleep(300);
                Ok($"已发 Datagram，连接状态 = {conn.State}");
            }
            else
            {
                Console.WriteLine("  （对端未协商 Datagram，跳过）");
            }

            // ---- 4. 主动断开（用户路径）----
            Mark("主动 Disconnect");
            conn.Disconnect();
            Thread.Sleep(600);
            Ok($"Disconnect 后状态 = {conn.State}，活连接数 = {MsQuicRuntime.LiveConnectionCount}");

            // ---- 5. 域卸载兜底：ShutdownAllConnections ----
            Mark("ShutdownAllConnections（模拟 Unity 域卸载/退出 Play 时的兜底关闭）");
            MsQuicRuntime.ShutdownAllConnections("harness step 5");
            Ok($"活连接数 = {MsQuicRuntime.LiveConnectionCount}");

            // ---- 6. **连接中**就强制关闭（Unity 里"连到一半退出 Play"就是这样）----
            Mark("ConnectAsync 到死端口后立刻 CloseNow（连接中强制关闭）");
            var c6 = new QuicConnection { ConnectTimeoutMs = 2000 };
            try
            {
                c6.ConnectAsync("127.0.0.1:9"); // 9 = discard，必然连不通
                Thread.Sleep(200);
                c6.CloseNow("harness step 6");
                Ok($"已强制关闭，状态 = {c6.State}");
            }
            catch (Exception e)
            {
                Bad("连接中强制关闭", e.Message);
            }

            // ---- 7. 发送在途时强制关闭 ----
            Mark("连接后狂发 50 帧再立刻 CloseNow（发送在途时强制关闭）");
            var c7 = ConnectOrFail(GatewayQuic, out var err7);
            if (c7 == null)
            {
                Bad("步骤 7 连接", err7);
            }
            else
            {
                var f = ClientFrame.Encode(7, EMsg.Login, Encoding.UTF8.GetBytes("{\"token\":\"x\"}"));
                for (var i = 0; i < 50; i++)
                    c7.Send(f, 0, f.Length);
                c7.CloseNow("harness step 7");
                Ok($"已强制关闭，状态 = {c7.State}");
            }

            // ---- 8. 大帧在途时强制关闭 ----
            Mark("发 900KB 大帧后立刻 CloseNow（大缓冲在途）");
            var c8 = ConnectOrFail(GatewayQuic, out var err8);
            if (c8 == null)
            {
                Bad("步骤 8 连接", err8);
            }
            else
            {
                var big = ClientFrame.Encode(8, EMsg.Login, new byte[900 * 1024]);
                c8.Send(big, 0, big.Length);
                Thread.Sleep(20);
                c8.CloseNow("harness step 8");
                Ok($"已强制关闭，状态 = {c8.State}");
            }

            // ---- 9. 连上→发→断开，重复 3 轮（重连路径）----
            Mark("连接→发送→断开 重复 3 轮");
            for (var i = 0; i < 3; i++)
            {
                var c9 = ConnectOrFail(GatewayQuic, out var err9);
                if (c9 == null)
                {
                    Bad($"第 {i + 1} 轮连接", err9);
                    break;
                }
                var f = ClientFrame.Encode((uint)(100 + i), EMsg.Login, Encoding.UTF8.GetBytes("{\"token\":\"y\"}"));
                c9.Send(f, 0, f.Length);
                Thread.Sleep(100);
                c9.Disconnect();
                Thread.Sleep(200);
            }
            Ok("重连 3 轮完成");

            // ---- 收尾：把一切都关掉（这一步是"域卸载"等价物）----
            Mark("最终 ShutdownAllConnections");
            MsQuicRuntime.ShutdownAllConnections("harness end");
            Ok($"活连接数 = {MsQuicRuntime.LiveConnectionCount}");

            Console.WriteLine($"\n===== 结果：{(_failures == 0 ? "ALL STEPS OK（无崩溃、无失败）" : _failures + " 步失败")} =====");
            return _failures == 0 ? 0 : 1;
        }

        /// <summary>
        /// 单模式运行：只做一件事，然后断开。用于**二分定位**"到底是哪一步让 msquic 断言"。
        /// 每一步都先打标记（进程级崩溃时只能靠"最后一条标记"定位）。
        /// </summary>
        private static int RunSingleMode(string mode)
        {
            Mark($"mode={mode}: TryEnsureReady");
            if (!MsQuicRuntime.TryEnsureReady(out var reason))
            {
                Bad("msquic 就绪", reason);
                return 2;
            }

            Mark($"mode={mode}: 连接");
            var conn = ConnectOrFail(GatewayQuic, out var err);
            if (conn == null)
            {
                Bad("连接", err);
                return 3;
            }
            Ok($"已连接 (datagram={conn.SupportsUnreliable})");

            if (mode == "dgram")
            {
                Mark("mode=dgram: 发 Datagram");
                var d = ClientFrame.Encode(0, EMsg.PushDataSync, Encoding.UTF8.GetBytes("{}"));
                conn.SendUnreliable(d, 0, d.Length);
                Thread.Sleep(500);
                Ok("Datagram 已发");
            }
            else if (mode == "login")
            {
                Mark("mode=login: 发登录帧并等回包");
                var reply = RequestReply(conn, 77, EMsg.Login, "{\"token\":\"t\"}", 10000);
                Ok(reply == null ? "未收到回包（仍继续，测的是断开路径）" : $"收到回包 len={reply.Length}");
            }
            else if (mode == "multi")
            {
                // 连做 3 次请求/回包：验证"不再调 StreamReceiveComplete"没有把 msquic 的收包流控堵死
                // （若流控被堵，第二次之后就收不到数据了）。
                Mark("mode=multi: 同一连接连做 3 次请求/回包");
                for (var i = 0; i < 3; i++)
                {
                    var reply = RequestReply(conn, (uint)(200 + i), EMsg.Login, "{\"token\":\"m\"}", 8000);
                    if (reply == null)
                        Bad($"第 {i + 1} 次往返", "没收到回包（收包流控可能被堵住）");
                    else
                        Ok($"第 {i + 1} 次往返成功 len={reply.Length}");
                }
            }
            else if (mode == "send")
            {
                Mark("mode=send: 发 5 帧可靠数据");
                var f = ClientFrame.Encode(5, EMsg.Login, Encoding.UTF8.GetBytes("{\"token\":\"t\"}"));
                for (var i = 0; i < 5; i++)
                    conn.Send(f, 0, f.Length);
                Thread.Sleep(500);
                Ok("可靠帧已发");
            }

            Mark($"mode={mode}: 主动 Disconnect");
            conn.Disconnect();
            Thread.Sleep(800);
            Ok($"Disconnect 后状态={conn.State}");

            Mark($"mode={mode}: ShutdownAllConnections");
            MsQuicRuntime.ShutdownAllConnections($"harness mode {mode}");
            Ok($"活连接数={MsQuicRuntime.LiveConnectionCount}");

            Console.WriteLine($"\n===== mode={mode} 完成：{(_failures == 0 ? "无崩溃" : _failures + " 步失败")} =====");
            return _failures == 0 ? 0 : 1;
        }

        /// <summary>连接并等握手；失败返回 null 并给出原因。</summary>
        private static QuicConnection ConnectOrFail(string addr, out string error)
        {
            error = null;
            var conn = new QuicConnection { ConnectTimeoutMs = 3000 };
            var connected = false;
            var disconnected = false;
            conn.Connected += () => connected = true;
            conn.Disconnected += () => disconnected = true;

            try
            {
                conn.ConnectAsync(addr);
            }
            catch (Exception e)
            {
                error = e.Message;
                return null;
            }

            var deadline = DateTime.UtcNow.AddSeconds(6);
            while (!connected && !disconnected && DateTime.UtcNow < deadline)
                Thread.Sleep(50);

            if (!connected)
            {
                error = disconnected ? "链路在握手期就断开（看上面的 Quic 日志）" : "6s 内未连上";
                return null;
            }

            return conn;
        }

        /// <summary>发一帧并等回包（按 requestID 认领）。</summary>
        private static byte[] RequestReply(QuicConnection conn, uint requestId, uint msgId, string json, int timeoutMs)
        {
            var frame = ClientFrame.Encode(requestId, msgId, Encoding.UTF8.GetBytes(json));
            conn.Send(frame, 0, frame.Length);

            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                if (conn.TryTakePacket(out var packet) && packet != null && packet.Length >= 8)
                {
                    if (ClientFrame.TryDecode(packet, out var rid, out _, out _) && rid == requestId)
                        return packet;
                }
                Thread.Sleep(20);
            }

            return null;
        }
    }
}
