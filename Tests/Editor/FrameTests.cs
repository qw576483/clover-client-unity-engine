using NUnit.Framework;

namespace CloverEngine.Tests
{
    /// <summary>
    /// BigEndian 大端读写与 ClientFrame 客户端帧编解码单元测试。
    /// 线协议约定：[4B requestID][4B msgID][JSON body]，整数全部大端（对齐服务端 Go binary.BigEndian）。
    /// </summary>
    public class FrameTests
    {
        /// <summary>WriteUInt32 后 ReadUInt32 回环应完整还原原值。</summary>
        [Test]
        public void BigEndian_WriteRead_RoundTrip()
        {
            var buf = new byte[8];
            BigEndian.WriteUInt32(buf, 0, 0x01020304u);
            BigEndian.WriteUInt32(buf, 4, 0xDEADBEEFu);

            Assert.AreEqual(0x01020304u, BigEndian.ReadUInt32(buf, 0));
            Assert.AreEqual(0xDEADBEEFu, BigEndian.ReadUInt32(buf, 4));
        }

        /// <summary>WriteUInt32 写出的字节必须高位在前（大端序），小端平台也不得翻转。</summary>
        [Test]
        public void BigEndian_Write_MostSignificantByteFirst()
        {
            var buf = new byte[4];
            BigEndian.WriteUInt32(buf, 0, 0x01020304u);

            Assert.AreEqual(0x01, buf[0]);
            Assert.AreEqual(0x02, buf[1]);
            Assert.AreEqual(0x03, buf[2]);
            Assert.AreEqual(0x04, buf[3]);
        }

        /// <summary>Encode→TryDecode 回环：帧头字段与 body 内容完整还原，bodyOffset 指向帧头之后。</summary>
        [Test]
        public void ClientFrame_EncodeDecode_RoundTrip()
        {
            var body = new byte[] { 0x11, 0x22, 0x33 };
            var frame = ClientFrame.Encode(7u, 42u, body);

            Assert.AreEqual(ClientFrame.HeaderLen + body.Length, frame.Length);

            var ok = ClientFrame.TryDecode(frame, out var requestID, out var msgID, out var bodyOffset);
            Assert.IsTrue(ok);
            Assert.AreEqual(7u, requestID);
            Assert.AreEqual(42u, msgID);
            Assert.AreEqual(ClientFrame.HeaderLen, bodyOffset);

            // body 内容逐字节还原
            Assert.AreEqual(0x11, frame[bodyOffset]);
            Assert.AreEqual(0x22, frame[bodyOffset + 1]);
            Assert.AreEqual(0x33, frame[bodyOffset + 2]);
        }

        /// <summary>空 body（null）编码帧长恰为帧头长度，TryDecode 正常解析且 bodyOffset 指向帧尾。</summary>
        [Test]
        public void ClientFrame_EmptyBody_HeaderOnly()
        {
            var frame = ClientFrame.Encode(0u, 1u, null);

            Assert.AreEqual(ClientFrame.HeaderLen, frame.Length);

            var ok = ClientFrame.TryDecode(frame, out var requestID, out var msgID, out var bodyOffset);
            Assert.IsTrue(ok);
            Assert.AreEqual(0u, requestID);
            Assert.AreEqual(1u, msgID);
            Assert.AreEqual(ClientFrame.HeaderLen, bodyOffset);
        }

        /// <summary>长度不足帧头（8 字节）的短帧必须被拒绝，防止越界读取。</summary>
        [Test]
        public void ClientFrame_TryDecode_RejectsShortFrame()
        {
            Assert.IsFalse(ClientFrame.TryDecode(new byte[0], out _, out _, out _));
            Assert.IsFalse(ClientFrame.TryDecode(new byte[] { 1, 2, 3 }, out _, out _, out _));
            Assert.IsFalse(ClientFrame.TryDecode(new byte[7], out _, out _, out _));
        }

        /// <summary>null 帧必须被拒绝（返回 false 而非抛异常）。</summary>
        [Test]
        public void ClientFrame_TryDecode_RejectsNull()
        {
            Assert.IsFalse(ClientFrame.TryDecode(null, out _, out _, out _));
        }
    }
}
