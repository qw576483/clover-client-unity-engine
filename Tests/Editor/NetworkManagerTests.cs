using System;
using System.Text;
using NUnit.Framework;

namespace CloverEngine.Tests
{
    /// <summary>
    /// NetworkManager 核心网络流程单元测试（EditMode，免真实链路）：
    /// 经 internal 测试构造 NetworkManager(GameConfig) 免 Game.Launch 建立实例，
    /// 经 internal DrainFrame(byte[]) 直接注入完整客户端帧，
    /// 验证 UDP 绑定令牌拦截优先级、回包配对、错误回包、请求超时与未配对推送的路由兜底。
    /// </summary>
    public class NetworkManagerTests
    {
        /// <summary>构造免 Game.Launch 的网络管理器（不配置 UDP 地址，避免测试期建立真实 UDP socket）。</summary>
        private NetworkManager CreateNet()
        {
            return new NetworkManager(new GameConfig
            {
                CallTimeoutSeconds = 5,
                MaxReconnectCount = 5,
            });
        }

        /// <summary>
        /// UDP 绑定令牌帧（EMsg.UDPBindGrant，体为令牌 UTF-8 原文）必须被最高优先级拦截：
        /// 不进入回包配对表、不做路由分发、处理过程不抛异常。
        /// </summary>
        [Test]
        public void DrainFrame_UdpBindGrant_Intercepted()
        {
            var nm = CreateNet();
            var frame = ClientFrame.Encode(0u, EMsg.UDPBindGrant, Encoding.UTF8.GetBytes("token-abc"));

            Assert.DoesNotThrow(() => nm.DrainFrame(frame));
            Assert.AreEqual(0, nm.PendingCallCount);
        }

        /// <summary>
        /// 排队位置帧（EMsg.QueuePosition，网关直发，requestID=0）必须被最高优先级拦截：
        /// 不进入回包配对表、不做业务路由分发，且解析结果落到 IsQueued / QueueAhead / QueueTotal 上。
        /// </summary>
        [Test]
        public void DrainFrame_QueuePosition_SetsQueuedState()
        {
            var nm = CreateNet();
            var body = Encoding.UTF8.GetBytes("{\"ahead\":12,\"total\":13,\"ticket\":7}");
            var frame = ClientFrame.Encode(0u, EMsg.QueuePosition, body);

            Assert.DoesNotThrow(() => nm.DrainFrame(frame));
            Assert.AreEqual(0, nm.PendingCallCount);
            Assert.IsTrue(nm.IsQueued, "收到位置帧后应处于排队态");
            Assert.AreEqual(12, nm.QueueAhead);
            Assert.AreEqual(13, nm.QueueTotal);
            Assert.AreEqual(7L, nm.QueueTicket);

            // 位置刷新：同一帧再来一次（队列前进），状态应被覆盖而不是累加
            var refresh = ClientFrame.Encode(0u, EMsg.QueuePosition,
                Encoding.UTF8.GetBytes("{\"ahead\":3,\"total\":13,\"ticket\":7}"));
            nm.DrainFrame(refresh);
            Assert.AreEqual(3, nm.QueueAhead);
            Assert.AreEqual(7L, nm.QueueTicket);
        }

        /// <summary>
        /// 会话通道加密（AES-256-GCM）端到端：
        /// ① 明文登录回包带 session_key → 引擎启用通道加密；
        /// ② 之后的下行帧是密文（<c>[12B nonce][ciphertext+tag]</c>）→ 解密后交给业务处理器；
        /// ③ 密文被篡改 → 丢弃、不触发处理器。
        /// 本用例守「双端线格式一致」：格式 / nonce / tag 任一处改单边都会红。
        /// </summary>
        [Test]
        public void DrainFrame_EncryptedChannel_RoundTrip()
        {
            if (!SessionCrypto.IsSupported)
                Assert.Ignore("当前平台无 AES-GCM —— 引擎在此时不声明 encrypt，服务端保持明文");

            var nm = CreateNet();
            var key = new byte[SessionCrypto.KeySize];
            for (var i = 0; i < key.Length; i++) key[i] = (byte)(i + 1);

            // ① 登录回包（明文）携带密钥：走配对路径完成协商
            var loginTask = nm.Call<ELoginReply>(EMsg.Login, new ELoginRequest { token = "tok" });
            var loginBody = Serializer.Serialize(new ELoginReply
            {
                success = true,
                owner = "p1",
                session_key = Convert.ToBase64String(key),
            });
            nm.DrainFrame(ClientFrame.Encode(1u, 0u, loginBody));

            Assert.IsTrue(loginTask.IsCompleted, "登录回包应完成配对");
            Assert.IsTrue(nm.IsChannelEncrypted, "带 session_key 的回包应启用通道加密");

            // ② 密文推送：应解密并分发到注册的处理器
            var handled = 0;
            EAlertNotify received = null;
            nm.OnMsg(EMsg.PushAlert, ctx =>
            {
                handled++;
                received = ctx.Bind<EAlertNotify>();
            });

            var alertFrame = ClientFrame.Encode(0u, EMsg.PushAlert,
                Serializer.Serialize(new EAlertNotify { title = "encrypted", content = "hi" }));
            var encrypted = SessionCrypto.Encrypt(key, alertFrame);
            Assert.IsNotNull(encrypted, "加密失败（IsSupported 为真时不该发生）");

            nm.DrainFrame(encrypted);
            Assert.AreEqual(1, handled, "密文帧应解密后分发");
            Assert.IsNotNull(received);
            Assert.AreEqual("encrypted", received.title);

            // ③ 篡改密文：GCM 认证失败 → 丢弃，不产生第二次分发
            var tampered = (byte[])encrypted.Clone();
            tampered[tampered.Length - 1] ^= 0xFF;
            nm.DrainFrame(tampered);
            Assert.AreEqual(1, handled, "被篡改的密文必须丢弃");
        }

        /// <summary>
        /// 回包配对：requestID 命中配对表时，回包 body 按 Call 泛型反序列化并完成任务；
        /// 未连接时 Send 静默丢弃不影响配对（Call 不做连接前置拦截的设计约束）。
        /// </summary>
        [Test]
        public void DrainFrame_ReplyPairsPendingCall()
        {
            var nm = CreateNet();
            nm.SetupSession("acc", "key", 0);

            var task = nm.Call<ELoginReply>(EMsg.Login, new ELoginRequest { token = "tok-xyz" });
            // SessionInfo.AllocateRequestID 从 1 起分配，配对表中恰好一项未决请求
            Assert.AreEqual(1, nm.PendingCallCount);

            // 正常回包约定 msgID 恒为 0
            var body = Serializer.Serialize(new ELoginReply { success = true, owner = "p1", session_key = "sk" });
            nm.DrainFrame(ClientFrame.Encode(1u, 0u, body));

            Assert.IsTrue(task.IsCompleted);
            Assert.IsFalse(task.IsFaulted);
            Assert.IsTrue(task.Result.success);
            Assert.AreEqual("p1", task.Result.owner);
            Assert.AreEqual("sk", task.Result.session_key);
            Assert.AreEqual(0, nm.PendingCallCount);
        }

        /// <summary>
        /// 错误回包（msgID = EMsg.Error，body = EErrorReply{err}）：
        /// 请求以 CloverCallException 结束（ServerError 为服务端错误描述），
        /// 且最近错误回包信息被记录（供调试面板展示）。
        /// </summary>
        [Test]
        public void DrainFrame_ErrorReply_FailsCallWithCloverException()
        {
            var nm = CreateNet();
            nm.SetupSession("acc", "key", 0);

            var task = nm.Call<ELoginReply>(EMsg.Login, null);
            var body = Serializer.Serialize(new EErrorReply { err = "boom" });
            nm.DrainFrame(ClientFrame.Encode(1u, EMsg.Error, body));

            Assert.IsTrue(task.IsFaulted);
            var ag = Assert.Throws<AggregateException>(() => task.Wait(0));
            var inner = ag.GetBaseException() as CloverCallException;
            Assert.IsNotNull(inner);
            Assert.AreEqual("boom", inner.ServerError);
            Assert.AreEqual(1u, inner.RequestID);

            Assert.AreEqual("boom", nm.LastErrorReply.err);
            Assert.AreEqual(1u, nm.LastErrorRequestID);
            Assert.AreEqual(0, nm.PendingCallCount);
        }

        /// <summary>
        /// 请求超时：配置 CallTimeoutSeconds = 0.1 后不注入回包，
        /// 请求应在超时定时器触发后以 TimeoutException 结束并移出配对表。
        /// </summary>
        [Test]
        public void Call_NoReply_TimesOut()
        {
            var nm = new NetworkManager(new GameConfig
            {
                CallTimeoutSeconds = 0.1,
                MaxReconnectCount = 5,
            });
            nm.SetupSession("acc", "key", 0);

            var task = nm.Call<ELoginReply>(EMsg.Login, null);
            Assert.AreEqual(1, nm.PendingCallCount);

            // 超时定时器在后台线程触发，等待足够时长（远大于 0.1s）
            var ag = Assert.Throws<AggregateException>(() => task.Wait(3000));
            Assert.IsInstanceOf<TimeoutException>(ag.GetBaseException());
            Assert.AreEqual(0, nm.PendingCallCount);
        }

        /// <summary>
        /// 未配对推送（requestID = 0）应兜底路由到 OnMsg 注册的处理器，
        /// 上下文携带原始 msgID / requestID 供业务侧使用。
        /// </summary>
        [Test]
        public void DrainFrame_UnpairedPush_RoutedToHandler()
        {
            var nm = CreateNet();
            var routedMsgID = 0u;
            var routedReqID = 0u;
            nm.OnMsg(EMsg.PushAlert, ctx =>
            {
                routedMsgID = ctx.MsgID;
                routedReqID = ctx.RequestID;
            });

            var body = Serializer.Serialize(new EAlertNotify { title = "t", content = "c" });
            nm.DrainFrame(ClientFrame.Encode(0u, EMsg.PushAlert, body));

            Assert.AreEqual((uint)EMsg.PushAlert, routedMsgID);
            Assert.AreEqual(0u, routedReqID);
        }

        /// <summary>短帧（长度不足 8 字节帧头）注入应被安全丢弃，不影响网络管理器后续状态。</summary>
        [Test]
        public void DrainFrame_ShortFrame_Discarded()
        {
            var nm = CreateNet();

            Assert.DoesNotThrow(() => nm.DrainFrame(new byte[] { 1, 2, 3 }));
            Assert.AreEqual(0, nm.PendingCallCount);
        }

        /// <summary>
        /// 全量同步推送（EMsg.PushPlayerFullSync）：NetworkManager 内部处理器先补齐会话凭证
        /// （player_id / session_token），再分发到业务侧 OnMsg 处理器。
        /// </summary>
        [Test]
        public void DrainFrame_PlayerFullSync_EnrichesSessionThenRoutes()
        {
            var nm = CreateNet();
            var routed = false;
            nm.OnMsg(EMsg.PushPlayerFullSync, _ => routed = true);

            var body = Encoding.UTF8.GetBytes(
                "{\"player_id\":\"42\",\"account\":\"acc\",\"session_token\":\"sk2\",\"line\":1}");
            nm.DrainFrame(ClientFrame.Encode(0u, EMsg.PushPlayerFullSync, body));

            Assert.IsTrue(routed);
            Assert.AreEqual("42", nm.Session.PlayerID);
            Assert.AreEqual("sk2", nm.Session.Token);
            // 分线编号（Session.Line）由 EMsg.PushSceneInfo 推送填充；
            // 全量同步推送即使带 line 字段也不解析（见 NetworkManager.HandlePlayerFullSync）。
            Assert.AreEqual(0, nm.Session.Line);
        }
    }
}
