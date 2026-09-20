using System;
using System.Collections.Generic;

namespace CloverEngine
{
    /// <summary>
    /// QUIC 流上的分帧（**纯函数，无 IO、无原生调用**）：与服务端
    /// <c>internal/transport/net/quic/conn.go</c> 的 <c>[4B 大端长度][客户端帧]</c> 严格对应。
    ///
    /// <para>
    /// 为什么要单独抽出来：帧格式写错时**没有任何编译期提示**，服务端只会把长度读成巨大值然后断开
    /// （实测踩过：服务端日志 <c>quic read: frame too large: 1351873247</c>，排查花了整整一轮）。
    /// 抽成纯函数后就能用单测把"逐字节形状"钉住，而不是靠"连上试一次"。
    /// </para>
    /// </summary>
    internal static class QuicStreamFraming
    {
        /// <summary>长度前缀字节数（服务端 <c>quicFrameLenSize</c>）。</summary>
        public const int HeaderLen = 4;

        /// <summary>
        /// 保活帧长度：<c>requestID(4) + msgID(4)</c>，无 body。
        /// 保活必须是"合法的空客户端帧"，而不是空长度 ——
        /// 服务端读循环按"先读长度、再读整帧"工作，长度为 0 也能过，但帧内容为空会让上层解析出错。
        /// </summary>
        public const int KeepAliveBodyLen = 8;

        /// <summary>
        /// 编码流上一帧：<c>[4B 大端长度][客户端帧]</c>。
        /// </summary>
        /// <param name="payload">客户端帧字节（<c>[4B requestID][4B msgID][body]</c>）；空数组表示生成保活帧。</param>
        /// <param name="keepAliveMsgId">保活帧的 msgID（仅当 <paramref name="payload"/> 为空时使用）。</param>
        public static byte[] Encode(byte[] payload, uint keepAliveMsgId = 0)
        {
            var bodyLen = payload != null && payload.Length > 0 ? payload.Length : KeepAliveBodyLen;
            var frame = new byte[HeaderLen + bodyLen];
            BigEndian.WriteUInt32(frame, 0, (uint)bodyLen);
            if (payload != null && payload.Length > 0)
            {
                Buffer.BlockCopy(payload, 0, frame, HeaderLen, payload.Length);
            }
            else
            {
                BigEndian.WriteUInt32(frame, HeaderLen, 0);            // requestID = 0（无回包）
                BigEndian.WriteUInt32(frame, HeaderLen + 4, keepAliveMsgId);
            }

            return frame;
        }

        /// <summary>
        /// 从现有缓冲区编码流上一帧（<c>[4B 大端长度][客户端帧]</c>）：
        /// 发送热路径用 —— 数据直接从源缓冲布局进帧体，避免"先拷 payload、再拷整帧"的二次分配。
        /// </summary>
        /// <param name="payload">客户端帧字节的源缓冲。</param>
        /// <param name="offset">起始偏移。</param>
        /// <param name="count">字节数（&gt; 0；0 长度的"合法空客户端帧"请走 <see cref="Encode"/>）。</param>
        public static byte[] EncodeFrom(byte[] payload, int offset, int count)
        {
            var frame = new byte[HeaderLen + count];
            BigEndian.WriteUInt32(frame, 0, (uint)count);
            if (count > 0)
                Buffer.BlockCopy(payload, offset, frame, HeaderLen, count);
            return frame;
        }

        /// <summary>
        /// 从重组缓冲里切出**完整**的客户端帧（可能一次切出多帧），剩余不完整的字节留在缓冲里等下一批。
        /// </summary>
        /// <param name="buffer">重组缓冲（原地消费掉已切出的部分）。</param>
        /// <param name="frames">切出的完整帧（追加到该列表）。</param>
        /// <param name="badLength">遇到不合法的长度前缀时置为 true（调用方应按链路失败处理并留痕）。</param>
        /// <param name="maxFrameSize">单帧上限（超过即视为流已错位）。</param>
        public static void Extract(List<byte> buffer, List<byte[]> frames, out bool badLength, int maxFrameSize)
        {
            badLength = false;
            while (buffer.Count >= HeaderLen)
            {
                var len = (buffer[0] << 24) | (buffer[1] << 16) | (buffer[2] << 8) | buffer[3];
                if (len < 0 || len > maxFrameSize)
                {
                    badLength = true;
                    return;
                }

                if (buffer.Count < HeaderLen + len)
                    return; // 还没收全

                if (len > 0)
                {
                    var frame = new byte[len];
                    buffer.CopyTo(HeaderLen, frame, 0, len);
                    frames.Add(frame);
                }

                buffer.RemoveRange(0, HeaderLen + len);
            }
        }
    }
}
