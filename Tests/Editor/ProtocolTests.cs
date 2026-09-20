using System.Text;
using NUnit.Framework;
using UnityEngine;

namespace CloverEngine.Tests
{
    /// <summary>
    /// 协议 DTO 序列化单元测试：JsonUtility（UTF-8 JSON）回环 + 服务端 snake_case 字段名对齐。
    /// </summary>
    public class ProtocolTests
    {
        /// <summary>ELoginReply 序列化回环：success 布尔与凭证字符串完整还原。</summary>
        [Test]
        public void Serializer_LoginReply_RoundTrip()
        {
            var src = new ELoginReply
            {
                owner = "player-1",
                token = "tk-123",
                success = true,
                err = "",
                session_key = "sk-abc",
            };

            var data = Serializer.Serialize(src);
            var dst = Serializer.Deserialize<ELoginReply>(data, 0, data.Length);

            Assert.IsNotNull(dst);
            Assert.AreEqual("player-1", dst.owner);
            Assert.AreEqual("tk-123", dst.token);
            Assert.IsTrue(dst.success);
            Assert.AreEqual("sk-abc", dst.session_key);
        }

        /// <summary>序列化产物为 UTF-8 JSON 文本（可直接按文本校验字段名）。</summary>
        [Test]
        public void Serialize_OutputIsUtf8Json()
        {
            var data = Serializer.Serialize(new EErrorReply { err = "boom" });
            var json = Encoding.UTF8.GetString(data);

            // snake_case 字段名与服务端 Go 结构体 JSON tag 对齐
            StringAssert.Contains("\"err\":\"boom\"", json);
        }

        /// <summary>EResumeSessionReply 反序列化：从 UTF-8 字节片段恢复（模拟回包 body 截取场景）；player_id 为字符串类型。</summary>
        [Test]
        public void Deserialize_ResumeReply_FromOffset()
        {
            var json = JsonUtility.ToJson(new EResumeSessionReply
            {
                player_id = "42",
                success = true,
                err = "",
            });
            var data = Encoding.UTF8.GetBytes(json);

            var dst = Serializer.Deserialize<EResumeSessionReply>(data, 0, data.Length);
            Assert.IsNotNull(dst);
            Assert.AreEqual("42", dst.player_id);
            Assert.IsTrue(dst.success);
        }

        /// <summary>ERankEntry 的 score 为 double（服务端 Go float64 JSON 输出可能带小数）。</summary>
        [Test]
        public void Deserialize_RankEntry_ScoreIsDouble()
        {
            var data = Encoding.UTF8.GetBytes("{\"member\":\"m1\",\"score\":12.5,\"rank\":3}");
            var dst = Serializer.Deserialize<ERankEntry>(data, 0, data.Length);

            Assert.IsNotNull(dst);
            Assert.AreEqual("m1", dst.member);
            Assert.AreEqual(12.5d, dst.score, 0.0001d);
            Assert.AreEqual(3, dst.rank);
        }

        /// <summary>
        /// 大整数边界往返：<c>scene_id</c> 是**无符号**类型（<c>ulong</c>，与服务端 <c>uint64</c> 对齐）。
        /// 两条边界都必须原样还原：
        /// ① 2^63 —— 用有符号 <c>long</c> 会溢出成 MinValue（负数），表现为"进错地图"；
        /// ② &gt; 2^53 —— <c>double</c> 会丢精度，必须按整数解析。
        /// </summary>
        [Test]
        public void Serialize_Deserialize_SceneIdUnsignedRoundTrip()
        {
            // ① 结构体往返：2^63（long 的溢出边界，ulong 下是普通合法值）
            const ulong AboveLongMax = 9223372036854775808UL; // 2^63
            var src = new ESceneInfoNotify { scene_id = AboveLongMax, instance_id = uint.MaxValue, name = "max" };
            var data = Serializer.Serialize(src);
            var dst = Serializer.Deserialize<ESceneInfoNotify>(data, 0, data.Length);
            Assert.IsNotNull(dst);
            Assert.AreEqual(AboveLongMax, dst.scene_id);
            Assert.AreEqual(uint.MaxValue, dst.instance_id);

            // ② 服务端原文：> 2^53 的整数（double 会丢精度，必须按整数解析）
            var bigRaw = Encoding.UTF8.GetBytes("{\"scene_id\":9223372036854775806,\"instance_id\":7,\"name\":\"n\"}");
            var big = Serializer.Deserialize<ESceneInfoNotify>(bigRaw, 0, bigRaw.Length);
            Assert.IsNotNull(big);
            Assert.AreEqual(9223372036854775806UL, big.scene_id);

            // ③ 服务端原文：> long.MaxValue（2^63+1）——证明按**无符号**解析，而不是被截成负数
            var overLong = Encoding.UTF8.GetBytes("{\"scene_id\":9223372036854775809,\"instance_id\":0,\"name\":\"n\"}");
            var over = Serializer.Deserialize<ESceneInfoNotify>(overLong, 0, overLong.Length);
            Assert.IsNotNull(over);
            Assert.AreEqual(9223372036854775809UL, over.scene_id);
        }
    }
}
