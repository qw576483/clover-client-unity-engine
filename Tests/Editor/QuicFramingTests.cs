using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;

namespace CloverEngine.Tests
{
    /// <summary>
    /// QUIC 流分帧的逐字节用例（对应 <c>结构规则.md</c> §五 N2 的线格式部分）。
    ///
    /// <para>
    /// 这些断言存在的理由很具体：帧格式错**不会**编译报错、也**不会**在客户端报错 ——
    /// 服务端只会把长度读成巨大值然后断开（实测踩过：`quic read: frame too large: 1351873247`）。
    /// 有了它，线格式一旦被改错，本地立刻红，而不是等连服务端时猜。
    /// </para>
    /// </summary>
    public class QuicFramingTests
    {
        private const int MaxFrame = 1 << 20;

        [Test]
        public void Encode_NormalFrame_BigEndianLengthPrefixThenPayload()
        {
            var payload = new byte[] { 0xAA, 0xBB, 0xCC };
            var frame = QuicStreamFraming.Encode(payload);

            Assert.AreEqual(7, frame.Length, "4 字节长度前缀 + 3 字节数据");
            CollectionAssert.AreEqual(new byte[] { 0x00, 0x00, 0x00, 0x03 }, new[] { frame[0], frame[1], frame[2], frame[3] },
                "长度前缀必须是大端（服务端用 binary.BigEndian.Uint32 读）");
            CollectionAssert.AreEqual(payload, new[] { frame[4], frame[5], frame[6] });
        }

        [Test]
        public void Encode_KeepAlive_IsWellFormedEmptyClientFrame()
        {
            // ★ 保活消息号必须**引用生产常量**（QuicConnection.KeepAliveMsgId），不能再写字面量：
            //   旧实现硬编码 4001（恰为 EMsg.PushPlayerFullSync）并断言 0x00000FA1，而生产实际用 0 ——
            //   测试钉住的值与实际发送的保活帧毫无关系，真实保活消息号被改错也不会变红。
            //   （该常量是 private const，在不改 Runtime 的约束下只能反射读取；取不到说明生产代码被重构，直接红。）
            var field = typeof(QuicConnection).GetField("KeepAliveMsgId",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(field, "生产应保留 QuicConnection.KeepAliveMsgId（保活消息号的唯一出处）");
            var keepAliveMsgId = (uint)field.GetRawConstantValue();
            Assert.AreEqual(0u, keepAliveMsgId,
                "保活消息号必须是 0：0 不是任何业务/引擎消息号，服务端 dispatch 查不到 handler 会直接忽略，" +
                "但流上有字节 —— 这正是保活需要的「应用层活跃度」");

            // 用**生产值**编码保活帧：requestID=0 + msgID（大端），长度前缀 = 8
            var frame = QuicStreamFraming.Encode(Array.Empty<byte>(), keepAliveMsgId);

            Assert.AreEqual(12, frame.Length);
            Assert.AreEqual(8, frame[3], "保活帧 body 长度固定 8（只有客户端帧头、没有 body）");
            CollectionAssert.AreEqual(new byte[] { 0, 0, 0, 0 }, new[] { frame[4], frame[5], frame[6], frame[7] },
                "保活帧 requestID 必须为 0（无回包）");
            var expectedMsgId = new[]
            {
                (byte)(keepAliveMsgId >> 24), (byte)(keepAliveMsgId >> 16),
                (byte)(keepAliveMsgId >> 8), (byte)keepAliveMsgId,
            };
            CollectionAssert.AreEqual(expectedMsgId, new[] { frame[8], frame[9], frame[10], frame[11] },
                "保活帧 msgID 必须按大端编码，且取自生产常量 QuicConnection.KeepAliveMsgId");
        }

        [Test]
        public void Extract_SingleCompleteFrame()
        {
            var payload = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 };
            var buffer = new List<byte>(QuicStreamFraming.Encode(payload));
            var frames = new List<byte[]>();

            QuicStreamFraming.Extract(buffer, frames, out var bad, MaxFrame);

            Assert.IsFalse(bad);
            Assert.AreEqual(1, frames.Count);
            CollectionAssert.AreEqual(payload, frames[0]);
            Assert.AreEqual(0, buffer.Count, "切完的字节必须从缓冲里移除");
        }

        [Test]
        public void Extract_MultipleFramesInOneBatch()
        {
            var buffer = new List<byte>();
            buffer.AddRange(QuicStreamFraming.Encode(new byte[] { 0x11 }));
            buffer.AddRange(QuicStreamFraming.Encode(new byte[] { 0x22, 0x33 }));
            var frames = new List<byte[]>();

            QuicStreamFraming.Extract(buffer, frames, out var bad, MaxFrame);

            Assert.IsFalse(bad);
            Assert.AreEqual(2, frames.Count);
            CollectionAssert.AreEqual(new byte[] { 0x11 }, frames[0]);
            CollectionAssert.AreEqual(new byte[] { 0x22, 0x33 }, frames[1]);
        }

        [Test]
        public void Extract_PartialFrame_KeepsRemainderForNextBatch()
        {
            var full = QuicStreamFraming.Encode(new byte[] { 0x01, 0x02, 0x03, 0x04 });
            var buffer = new List<byte>();
            buffer.AddRange(new ArraySegment<byte>(full, 0, 3)); // 只到了长度前缀的一半多
            var frames = new List<byte[]>();

            QuicStreamFraming.Extract(buffer, frames, out var bad, MaxFrame);
            Assert.IsFalse(bad);
            Assert.AreEqual(0, frames.Count, "还没收全就不该产出帧");
            Assert.AreEqual(3, buffer.Count, "已收到的字节必须留在缓冲里");

            buffer.AddRange(new ArraySegment<byte>(full, 3, full.Length - 3)); // 补齐
            QuicStreamFraming.Extract(buffer, frames, out bad, MaxFrame);
            Assert.IsFalse(bad);
            Assert.AreEqual(1, frames.Count);
            CollectionAssert.AreEqual(new byte[] { 0x01, 0x02, 0x03, 0x04 }, frames[0]);
        }

        [Test]
        public void Extract_IllegalLength_ReportsDesync()
        {
            // 长度 0xFFFFFFFF（远超上限）：流已错位，必须报错让上层断链，而不是继续读出垃圾
            var buffer = new List<byte> { 0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x00 };
            var frames = new List<byte[]>();

            QuicStreamFraming.Extract(buffer, frames, out var bad, MaxFrame);

            Assert.IsTrue(bad);
            Assert.AreEqual(0, frames.Count);
        }
    }
}
