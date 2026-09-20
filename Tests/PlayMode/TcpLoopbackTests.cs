using System;
using System.Collections;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CloverEngine.Tests
{
    /// <summary>
    /// 真实 TCP 链路回环测试（PlayMode）：本机 TcpListener 起 echo 服务，
    /// 验证 TcpConnection 非阻塞连接 / 帧收发 / 完整帧收包队列，
    /// 以及 NetworkManager.Call 端到端请求-回包配对（服务端最小语义：原样回显请求帧）。
    /// </summary>
    public class TcpLoopbackTests
    {
        /// <summary>
        /// 启动一个单连接 echo 服务：接受一个 TCP 客户端，将收到的字节原样回写。
        /// </summary>
        /// <param name="listener">已 Start 的监听器</param>
        /// <returns>服务端处理任务（测试结束后由停止监听自然退出）</returns>
        private static Task StartEchoServer(TcpListener listener)
        {
            return Task.Run(async () =>
            {
                var client = await listener.AcceptTcpClientAsync();
                using (var stream = client.GetStream())
                {
                    var buf = new byte[4096];
                    while (true)
                    {
                        int n;
                        try
                        {
                            n = await stream.ReadAsync(buf, 0, buf.Length);
                        }
                        catch
                        {
                            break;
                        }
                        if (n <= 0) break;
                        await stream.WriteAsync(buf, 0, n);
                    }
                }
                client.Close();
            });
        }

        /// <summary>
        /// TcpConnection 层回环：非阻塞连接建立 → Send 发送客户端帧 → 收包队列取出完整回显帧。
        /// </summary>
        [UnityTest]
        public IEnumerator TcpConnection_EchoLoopback()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var serverTask = StartEchoServer(listener);
            TcpConnection conn = null;
            try
            {
                conn = new TcpConnection();
                conn.ConnectAsync($"127.0.0.1:{port}");

                // 等待非阻塞连接建立（后台线程完成握手，轮询状态）
                var deadline = Time.realtimeSinceStartup + 5f;
                while (conn.State != ConnectionState.Connected && Time.realtimeSinceStartup < deadline)
                    yield return null;
                Assert.AreEqual(ConnectionState.Connected, conn.State, "connection should be established");

                // 发送完整客户端帧，等待 echo 回显进入收包队列
                var frame = ClientFrame.Encode(9u, 7u, Encoding.UTF8.GetBytes("ping"));
                conn.Send(frame, 0, frame.Length);

                byte[] received = null;
                deadline = Time.realtimeSinceStartup + 5f;
                while (received == null && Time.realtimeSinceStartup < deadline)
                {
                    conn.Tick();
                    conn.TryTakePacket(out received);
                    if (received == null) yield return null;
                }

                Assert.IsNotNull(received, "echo reply should arrive");
                Assert.IsTrue(ClientFrame.TryDecode(received, out var requestID, out var msgID, out var bodyOffset));
                Assert.AreEqual(9u, requestID);
                Assert.AreEqual(7u, msgID);
                Assert.AreEqual("ping", Encoding.UTF8.GetString(received, bodyOffset, received.Length - bodyOffset));
            }
            finally
            {
                // 清理必须走 finally：任一断言失败也要断开，否则漏下的连接后台线程与 socket 污染后续用例。
                conn?.Disconnect();
                listener.Stop();
            }
        }

        /// <summary>
        /// NetworkManager 端到端 Call 回环：真实 TCP 上发送请求，echo 服务回显请求帧
        /// （requestID 命中配对表即按正常回包处理），验证 连接 → 编码 → 发送 →
        /// 收包 → 配对 → 反序列化 全链路；超时/事件均经 Game 静态桩安全兜底。
        /// </summary>
        [UnityTest]
        public IEnumerator NetworkManager_Call_OverRealTcpEcho()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var serverTask = StartEchoServer(listener);
            NetworkManager nm = null;
            try
            {
                nm = new NetworkManager(new GameConfig { CallTimeoutSeconds = 10 });
                nm.Connect($"127.0.0.1:{port}");

                var deadline = Time.realtimeSinceStartup + 5f;
                while (!nm.IsConnected && Time.realtimeSinceStartup < deadline)
                    yield return null;
                Assert.IsTrue(nm.IsConnected, "network manager should be connected");

                // echo 服务原样回显请求帧（requestID=1 命中配对表），body 反序列化为 ELoginReply
                var task = nm.Call<ELoginReply>(EMsg.Login, new ELoginRequest { token = "tok-echo" });
                deadline = Time.realtimeSinceStartup + 5f;
                while (!task.IsCompleted && Time.realtimeSinceStartup < deadline)
                {
                    nm.Tick();
                    yield return null;
                }
                nm.Tick();

                Assert.IsTrue(task.IsCompleted, "call should complete via echo reply");
                Assert.IsFalse(task.IsFaulted, $"call faulted: {task.Exception}");
                Assert.IsNotNull(task.Result);
            }
            finally
            {
                // 清理必须走 finally：任一断言失败也要断开，否则漏下的连接后台线程与 socket 污染后续用例。
                nm?.Disconnect();
                listener.Stop();
            }
        }
    }
}
