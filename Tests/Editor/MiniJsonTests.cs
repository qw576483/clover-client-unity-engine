using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace CloverEngine.Tests
{
    /// <summary>
    /// MiniJson 动态 JSON 解析/序列化单元测试。
    /// 解析产物约定：object → Dictionary&lt;string, object&gt; / List&lt;object&gt; / string /
    /// long（整型）/ double（浮点）/ bool / null。
    /// </summary>
    public class MiniJsonTests
    {
        /// <summary>基本类型解析：整型为 long、浮点为 double、bool/null/string 按约定映射。</summary>
        [Test]
        public void Parse_Primitives()
        {
            var root = MiniJson.Parse("{\"a\":1,\"f\":1.5,\"t\":true,\"n\":null,\"s\":\"x\"}")
                as Dictionary<string, object>;

            Assert.IsNotNull(root);
            Assert.AreEqual(1L, MiniJson.Get(root, "a"));
            Assert.AreEqual(1.5d, MiniJson.Get(root, "f"));
            Assert.AreEqual(true, MiniJson.Get(root, "t"));
            Assert.IsNull(MiniJson.Get(root, "n"));
            Assert.AreEqual("x", MiniJson.GetString(root, "s"));
        }

        /// <summary>嵌套对象与数组解析：数组为 List&lt;object&gt;，元素可为混合类型。</summary>
        [Test]
        public void Parse_NestedObjectAndArray()
        {
            var root = MiniJson.Parse("{\"list\":[1,\"a\",{\"k\":2}]}")
                as Dictionary<string, object>;

            Assert.IsNotNull(root);
            var list = MiniJson.Get(root, "list") as List<object>;
            Assert.IsNotNull(list);
            Assert.AreEqual(3, list.Count);
            Assert.AreEqual(1L, list[0]);
            Assert.AreEqual("a", list[1]);

            var nested = list[2] as Dictionary<string, object>;
            Assert.IsNotNull(nested);
            Assert.AreEqual(2L, MiniJson.Get(nested, "k"));
        }

        /// <summary>数型区分：无小数点/指数为 long，含 . e E 为 double（科学计数 1e2 → 100.0）。</summary>
        [Test]
        public void Parse_NumberTyping()
        {
            var root = MiniJson.Parse("{\"i\":-3,\"e\":1e2}") as Dictionary<string, object>;

            Assert.IsNotNull(root);
            Assert.IsInstanceOf<long>(MiniJson.Get(root, "i"));
            Assert.AreEqual(-3L, MiniJson.Get(root, "i"));
            Assert.IsInstanceOf<double>(MiniJson.Get(root, "e"));
            Assert.AreEqual(100.0d, MiniJson.Get(root, "e"));
        }

        /// <summary>字符串转义还原：引号/反斜杠/斜杠/控制字符。</summary>
        [Test]
        public void Parse_StringEscapes()
        {
            var s = (string)MiniJson.Parse("\"a\\\"b\\\\c\\/d\\n\\t\"");
            Assert.AreEqual("a\"b\\c/d\n\t", s);
        }

        /// <summary>Unicode 转义（\\uXXXX）应还原为对应字符。</summary>
        [Test]
        public void Parse_UnicodeEscape()
        {
            var s = (string)MiniJson.Parse("\"\\u4e2d\"");
            Assert.AreEqual("中", s);
        }

        /// <summary>空文本与纯空白文本解析为 null（WorldSync 空体容错依赖此行为）。</summary>
        [Test]
        public void Parse_Empty_ReturnsNull()
        {
            Assert.IsNull(MiniJson.Parse(""));
            Assert.IsNull(MiniJson.Parse("   "));
        }

        /// <summary>顶层非对象值（字符串/布尔/null/数字）直接返回对应标量。</summary>
        [Test]
        public void Parse_TopLevelScalars()
        {
            Assert.AreEqual("hi", MiniJson.Parse("\"hi\""));
            Assert.AreEqual(true, MiniJson.Parse("true"));
            Assert.IsNull(MiniJson.Parse("null"));
            Assert.AreEqual(7L, MiniJson.Parse("7"));
        }

        /// <summary>尾部多余字符属于非法 JSON，必须抛 FormatException。</summary>
        [Test]
        public void Parse_TrailingCharacters_Throws()
        {
            Assert.Throws<FormatException>(() => MiniJson.Parse("{} x"));
        }

        /// <summary>语法错误必须抛 FormatException（未闭合对象）。</summary>
        [Test]
        public void Parse_Malformed_Throws()
        {
            Assert.Throws<FormatException>(() => MiniJson.Parse("{\"a\":"));
        }

        /// <summary>字节片段重载：按 offset/length 截取解析，忽略片段外的字节。</summary>
        [Test]
        public void Parse_Bytes_OffsetLength()
        {
            var payload = System.Text.Encoding.UTF8.GetBytes("{\"k\":9}");
            // 前后各填充 3 字节噪声，验证片段截取正确
            var buf = new byte[payload.Length + 6];
            System.Buffer.BlockCopy(payload, 0, buf, 3, payload.Length);

            var root = MiniJson.Parse(buf, 3, payload.Length) as Dictionary<string, object>;
            Assert.IsNotNull(root);
            Assert.AreEqual(9L, MiniJson.Get(root, "k"));
        }

        /// <summary>Dump→Parse 回环：对象/数组/嵌套结构完整还原。</summary>
        [Test]
        public void Dump_Parse_RoundTrip()
        {
            var src = new Dictionary<string, object>
            {
                { "i", 42L },
                { "d", 3.5d },
                { "b", true },
                { "s", "文本\"引号" },
                { "list", new List<object> { 1L, "x", null } },
                { "obj", new Dictionary<string, object> { { "deep", 1L } } },
            };

            var json = MiniJson.Dump(src);
            var back = MiniJson.Parse(json) as Dictionary<string, object>;

            Assert.IsNotNull(back);
            Assert.AreEqual(42L, MiniJson.Get(back, "i"));
            Assert.AreEqual(3.5d, MiniJson.Get(back, "d"));
            Assert.AreEqual(true, MiniJson.Get(back, "b"));
            Assert.AreEqual("文本\"引号", MiniJson.GetString(back, "s"));

            var list = MiniJson.Get(back, "list") as List<object>;
            Assert.IsNotNull(list);
            Assert.AreEqual(3, list.Count);
            Assert.IsNull(list[2]);

            var obj = MiniJson.Get(back, "obj") as Dictionary<string, object>;
            Assert.IsNotNull(obj);
            Assert.AreEqual(1L, MiniJson.Get(obj, "deep"));
        }

        /// <summary>非有限 double（NaN/Infinity）序列化输出 null（对齐 JSON 规范）。</summary>
        [Test]
        public void Dump_NonFiniteDouble_IsNull()
        {
            Assert.AreEqual("null", MiniJson.Dump(double.NaN));
            Assert.AreEqual("null", MiniJson.Dump(double.PositiveInfinity));
        }
    }
}
