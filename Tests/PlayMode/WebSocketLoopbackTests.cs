using System;
using System.Collections;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CloverEngine.Tests
{
    /// <summary>
    /// 真实 WebSocket 链路回环测试（PlayMode）：本机起 WS echo 服务，
    /// 验证 WebSocketConnection 的连接 / 收发与 NetworkManager 经 WS 的请求-回包配对。
    ///
    /// 服务端用 <see cref="MiniWsServer"/>（裸 TCP 自做 RFC6455 握手），**不用 <c>HttpListener</c>** ——
    /// Unity 的 Mono 运行时里 `HttpListener` 不支持 WebSocket 升级，会让客户端握手 400 失败。
    ///
    /// 与 TcpLoopbackTests 的差别正好落在「线格式」上：
    ///   - WS 的一条二进制消息就是**一个客户端帧**，不加 TCP 的 <c>[1B type][4B len]</c> 传输层帧，
    ///     也不加裸 UDP 的 0x55 魔数；本测试的第 1 个用例就是断言这一点；
    ///   - 文本帧按服务端语义丢弃。
    /// </summary>
    public class WebSocketLoopbackTests
    {
        /// <summary>
        /// WebSocket 属 **Web 家族**（架构 §N10），在 Editor（原生平台）上默认会被线路规划裁掉；
        /// 而 WS 的实现只能在 Editor 上验证（WebGL 跑不了 headless 测试），
        /// 因此这里显式放行——仅测试用，见 <c>TransportCapabilities.AllowCrossFamilyForTesting</c>。
        /// </summary>
        [SetUp]
        public void SetUp() => TransportCapabilities.AllowCrossFamilyForTesting = true;

        /// <summary>复位，避免影响同进程内其它用例（它们要验证的正是家族约束）。</summary>
        [TearDown]
        public void TearDown() => TransportCapabilities.AllowCrossFamilyForTesting = false;

        /// <summary>
        /// WebSocketConnection 层回环：连接建立 → Send 发出客户端帧 → 收到的**同一条消息**字节与之一致。
        /// 断言字节完全相等，即证明 WS 线路没有额外加长度前缀或魔数。
        /// </summary>
        [UnityTest]
        public IEnumerator WebSocketConnection_EchoLoopback()
        {
            var server = MiniWsServer.EchoBinary();
            WebSocketConnection conn = null;
            try
            {
                conn = new WebSocketConnection();
                conn.ConnectAsync(server.Url("/ws"));

                var deadline = Time.realtimeSinceStartup + 5f;
                while (conn.State != ConnectionState.Connected && Time.realtimeSinceStartup < deadline)
                    yield return null;
                Assert.AreEqual(ConnectionState.Connected, conn.State, "WS 连接应建立成功");
                Assert.AreEqual(TransportKind.WebSocket, conn.Kind);

                var frame = ClientFrame.Encode(11u, 13u, Encoding.UTF8.GetBytes("ws-ping"));
                conn.Send(frame, 0, frame.Length);

                byte[] received = null;
                deadline = Time.realtimeSinceStartup + 5f;
                while (received == null && Time.realtimeSinceStartup < deadline)
                {
                    conn.Tick();
                    conn.TryTakePacket(out received);
                    if (received == null) yield return null;
                }

                Assert.IsNotNull(received, "echo 回包应到达");
                CollectionAssert.AreEqual(frame, received,
                    "WS 一条消息即一个客户端帧：收到的字节必须与发出的完全一致（无长度前缀、无 0x55 魔数）");
                Assert.IsTrue(ClientFrame.TryDecode(received, out var requestID, out var msgID, out var bodyOffset));
                Assert.AreEqual(11u, requestID);
                Assert.AreEqual(13u, msgID);
                Assert.AreEqual("ws-ping", Encoding.UTF8.GetString(received, bodyOffset, received.Length - bodyOffset));
            }
            finally
            {
                // 清理必须走 finally：任一断言失败也要断开，否则漏下的连接与收包线程污染后续用例。
                conn?.Disconnect();
                server.Dispose();
            }
        }

        /// <summary>
        /// NetworkManager 端到端 Call 回环（线路计划只含 WS）：
        /// 覆盖 线路选择 → 建连 → 编码 → 发送 → 收包 → 配对 → 反序列化 全链路，
        /// 证明「换线路不换语义」——WS 上的 Call 与 TCP 上的行为一致。
        /// </summary>
        [UnityTest]
        public IEnumerator NetworkManager_Call_OverRealWebSocketEcho()
        {
            var server = MiniWsServer.EchoBinary();
            NetworkManager nm = null;
            try
            {
                nm = new NetworkManager(new GameConfig { CallTimeoutSeconds = 10 });
                // 只配 WS 一条线路：验证线路计划本身可用（不依赖 TCP 兜底）
                nm.Connect(TransportOptions.From(null, null, $"127.0.0.1:{server.Port}", "/ws"));

                Assert.AreEqual(TransportKind.WebSocket, nm.ActiveTransport, "应选中 WS 线路");
                StringAssert.Contains("ws:", nm.LinePlan);

                var deadline = Time.realtimeSinceStartup + 5f;
                while (!nm.IsConnected && Time.realtimeSinceStartup < deadline)
                {
                    // 收包与状态推进都在 Tick 里：不泵 Tick 会表现为"连不上/没回包"，那是脚手架缺陷
                    nm.Tick();
                    yield return null;
                }
                Assert.IsTrue(nm.IsConnected, "WS 线路应连接成功");

                // echo 服务原样回显请求帧，requestID 命中配对表即按正常回包处理
                var task = nm.Call<ELoginReply>(EMsg.Login, new ELoginRequest { token = "tok-ws-echo" });
                deadline = Time.realtimeSinceStartup + 5f;
                while (!task.IsCompleted && Time.realtimeSinceStartup < deadline)
                {
                    nm.Tick();
                    yield return null;
                }
                nm.Tick();

                Assert.IsTrue(task.IsCompleted, "Call 应经 WS 回包完成");
                Assert.IsFalse(task.IsFaulted, $"call faulted: {task.Exception}");
                Assert.IsNotNull(task.Result);
            }
            finally
            {
                // 清理必须走 finally：任一断言失败也要断开，否则漏下的连接与收包线程污染后续用例。
                nm?.Disconnect();
                server.Dispose();
            }
        }
    }
}
