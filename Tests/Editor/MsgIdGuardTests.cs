using System;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CloverEngine.Tests
{
    /// <summary>
    /// 引擎保留消息号守卫测试（对应 `结构规则.md` §五 N3 的消息号约定）：
    /// 业务经 `Game.OnMsg` 注册 `msgID &lt;= EMsg.InternalMsgMax` 时必须被拒绝（打日志 + 不注册），
    /// 而引擎自己走 `_router.OnMsg` 的注册不受影响（本测试同时钉住这两条边界）。
    /// </summary>
    public class MsgIdGuardTests
    {
        private static readonly byte[] EmptyBody = Array.Empty<byte>();

        [TearDown]
        public void TearDown()
        {
            // 全局静态 router：测试后清掉，避免影响其它用例（Game.OnMsg 在 router 为 null 时会打 Error 日志且不注册）
            Game.AttachRouter(null);
        }

        /// <summary>
        /// 跨程序集常量一致性：Core 的 `Game.InternalMsgMax` 与 Network 的 `EMsg.InternalMsgMax`
        /// 必须是同一数值。
        ///
        /// 为什么会有两份：依赖方向是 `Network → Core`，Core 的 `Game.OnMsg` 看不到 `EMsg`
        /// （一开始写成 `EMsg.InternalMsgMax` 直接编译不过：CS0103）。两份常量若漂移，
        /// 守卫的边界就与实际协议区间不一致 —— 本条断言把这种漂移变成"测试红"。
        /// </summary>
        [Test]
        public void InternalMsgMax_CoreAndNetwork_Agree()
        {
            Assert.AreEqual(EMsg.InternalMsgMax, Game.InternalMsgMax,
                "Core 的 Game.InternalMsgMax 必须与 Network 的 EMsg.InternalMsgMax 保持一致");
        }

        /// <summary>
        /// 保留段（<= 10000）：注册被拒绝 ⇒ 派发时业务 handler 一次都不会被调用。
        /// 取 4003（EMsg.PushDataSync）——它是引擎自己真正在用的推送号，最能说明"业务不能碰"。
        /// </summary>
        [Test]
        public void OnMsg_ReservedRange_RegistrationRejected()
        {
            var router = new Router();
            Game.AttachRouter(router);

            int calls = 0;
            // 守卫**必须打日志**（拒绝要可见，不能静默）：先用 LogAssert 钉住这条 Error，
            // 否则 Unity 测试框架会把"未处理的 Error 日志"判为失败（这正是它该做的）。
            LogAssert.Expect(LogType.Error, new Regex("拒绝注册引擎保留段消息"));
            Game.OnMsg(EMsg.PushDataSync, _ => calls++);

            router.Dispatch(EMsg.PushDataSync, 0u, "", EmptyBody, 0, 0);

            Assert.AreEqual(0, calls, "保留段消息号的业务注册必须被拒绝（否则会与引擎内部监听冲突）");
        }

        /// <summary>边界值 10000（InternalMsgMax 本身）同样属于保留段，必须拒绝。</summary>
        [Test]
        public void OnMsg_InternalMsgMaxItself_Rejected()
        {
            var router = new Router();
            Game.AttachRouter(router);

            int calls = 0;
            LogAssert.Expect(LogType.Error, new Regex("拒绝注册引擎保留段消息"));
            Game.OnMsg(EMsg.InternalMsgMax, _ => calls++);
            router.Dispatch(EMsg.InternalMsgMax, 0u, "", EmptyBody, 0, 0);

            Assert.AreEqual(0, calls);
        }

        /// <summary>业务段（InternalMsgMax + 1 起）：注册与派发都正常。</summary>
        [Test]
        public void OnMsg_BusinessRange_Accepted()
        {
            var router = new Router();
            Game.AttachRouter(router);

            int calls = 0;
            const uint businessMsgId = EMsg.InternalMsgMax + 1;
            Game.OnMsg(businessMsgId, _ => calls++);

            router.Dispatch(businessMsgId, 0u, "", EmptyBody, 0, 0);

            Assert.AreEqual(1, calls, "业务消息号（>= 10001）必须能正常注册并收到派发");
        }

        /// <summary>
        /// 引擎内部注册路径（`_router.OnMsg` 直连）不受守卫影响：
        /// 4005（PushSceneInfo）经 Router 直接注册后必须能收到派发 —— 这条保证守卫不会误伤引擎自身。
        /// </summary>
        [Test]
        public void RouterOnMsg_ReservedRange_StillWorksForEngine()
        {
            var router = new Router();
            Game.AttachRouter(router);

            int calls = 0;
            router.OnMsg(EMsg.PushSceneInfo, _ => calls++);

            router.Dispatch(EMsg.PushSceneInfo, 0u, "", EmptyBody, 0, 0);

            Assert.AreEqual(1, calls, "引擎内部（_router.OnMsg）注册保留段消息必须仍然可用");
        }
    }
}
