using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace CloverEngine
{
    /// <summary>
    /// 确定性 JSON **原语**（写 + 最小解析），零第三方依赖、纯静态、无状态、线程安全。
    ///
    /// <para><b>为什么不用 / 不扩 <see cref="MiniJson"/></b>（判据）：</para>
    /// <list type="bullet">
    /// <item><see cref="MiniJson"/> 是**动态对象树**工具（<c>object</c> ⇄ JSON），服务的是
    /// "不知道结构、拿到什么写什么"的场景（如 <c>EPushDataSync</c> 顶层 map、内嵌动态 body）。</item>
    /// <item>它是**整体序列化**：<c>Dump(object)</c> 的键序按 Ordinal **重排**，输出顺序与调用方
    /// 声明的字段顺序无关 ⇒ 拿它写 DTO 会让"字段顺序固定"这条确定性契约落空（存档 diff 噪音、
    /// 内容哈希不稳定）。</item>
    /// <item>它不给**逐字段增量写**的原语（没有 <c>WriteKey</c> / 无逗号状态管理），也没有
    /// "long 优先、浮点才 double"的 DTO 取值形态 —— 那是 <c>TryParse</c> 侧 <c>GetInt/GetLong/GetFloat</c>
    /// 依赖的解析口径。</item>
    /// <item>因此本类**不是** MiniJson 的替代品，而是它的**下层**：DTO 的字段布局（字段顺序、哪些字段、
    /// 缺字段回什么默认值）仍然**留在业务侧**（如 <c>SaveJson</c>），本类只负责"给定顺序、逐字段、
    /// 逐字节确定地写出来"以及"把文本切成可预测的 <c>Dictionary/List/long/double/string/bool/null</c>"。</item>
    /// </list>
    ///
    /// <para><b>确定性口径</b>（三条，业务侧据此可断言 <c>Write(Parse(json)) == json</c>）：</para>
    /// <list type="number">
    /// <item><b>字段顺序 = 调用顺序</b>：写侧不排序、不重排，<see cref="WriteKey"/> 只做
    /// 「逗号 + 带引号的键 + 冒号」，顺序完全由调用方决定。</item>
    /// <item><b>浮点用 R 格式 + 不变文化</b>：<see cref="WriteFloat"/> / <see cref="WriteDouble"/> 走
    /// <c>"R"</c>（最短可往返），整数 <see cref="WriteInt"/> / <see cref="WriteLong"/> 走
    /// <see cref="CultureInfo.InvariantCulture"/> —— 换 region 不改变字节。</item>
    /// <item><b>null 安全</b>：<see cref="WriteString"/> 收到 <c>null</c> 写裸 <c>null</c>（⛔ 不写 <c>""</c>），
    /// 与解析侧"<c>null</c> → <c>null</c>"成对；非有限浮点同样写 <c>null</c>（对齐 JSON 规范，
    /// ⛔ 不产出 <c>NaN</c> / <c>Infinity</c> 这类非法字面量）。</item>
    /// </list>
    ///
    /// <para><b>解析产物映射</b>（<see cref="ParseValue"/>）：对象 → <c>Dictionary&lt;string, object&gt;</c>、
    /// 数组 → <c>List&lt;object&gt;</c>、整数 → <c>long</c>、浮点 → <c>double</c>、字符串 → <c>string</c>、
    /// <c>true/false</c> → <c>bool</c>、<c>null</c> → <c>null</c>。
    /// ⚠️ 整数**只认 long**（超出即退 <c>double</c>）—— 这是 DTO 取值侧的既有口径（缺字段 / 类型不符回默认值，
    /// 而不是抛异常）；需要 uint64 精度的大整数（雪花 ID）请用 <see cref="MiniJson.Parse"/>。</para>
    ///
    /// <para><b>确定性边界</b>：本类只做"文本 ⇄ 基本类型"，⛔ 不认识任何业务类型，⛔ 不缓存、⛔ 不排序、
    /// ⛔ 不读时钟 / 随机数 —— 同一份输入永远得到同一份输出。</para>
    /// </summary>
    public static class JsonWriter
    {
        /// <summary>解析递归深度上限，防御恶意深嵌套导致栈溢出（与 <see cref="MiniJson"/> 同值）。</summary>
        public const int MaxDepth = 128;

        // ═════════════════════════════════════════════════════════════════════
        // 写（逐字段、增量、顺序即字节序）
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 写一个 JSON 字符串字面量（<c>null</c> 安全）。
        /// <paramref name="v"/> 为 <c>null</c> ⇒ 写裸 <c>null</c>（⛔ 不写 <c>""</c>，
        /// 解析回来仍是 <c>null</c>，往返保型）；否则写双引号包裹的转义文本。
        /// </summary>
        public static void WriteString(StringBuilder sb, string v)
        {
            if (v == null)
            {
                sb.Append("null");
                return;
            }

            sb.Append('"');
            for (var i = 0; i < v.Length; i++)
            {
                var c = v[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < ' ')
                        {
                            sb.Append("\\u");
                            sb.Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            sb.Append('"');
        }

        /// <summary>
        /// 写对象的一个成员名（逗号 + 带引号的键 + 冒号）；值由调用方随后用本类的写原语接上。
        /// <paramref name="comma"/> = <c>false</c> 表示这是对象第一个成员（不写前导逗号）。
        /// 用「调用方自报逗号」而不是内部状态，是为了保持纯静态、零分配、可离线断言。
        /// </summary>
        public static void WriteKey(StringBuilder sb, string key, bool comma)
        {
            if (comma) sb.Append(',');
            WriteString(sb, key);
            sb.Append(':');
        }

        /// <summary>写一个整数（不变文化，⛔ 不受本机 region 影响）。</summary>
        public static void WriteInt(StringBuilder sb, int v)
        {
            sb.Append(v.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>写一个长整数（不变文化）。</summary>
        public static void WriteLong(StringBuilder sb, long v)
        {
            sb.Append(v.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>写一个布尔字面量（<c>true</c> / <c>false</c>）。</summary>
        public static void WriteBool(StringBuilder sb, bool v)
        {
            sb.Append(v ? "true" : "false");
        }

        /// <summary>写一个浮点数：<c>"R"</c> 格式（最短可往返）；<c>NaN</c> / <c>±Infinity</c> ⇒ 写 <c>null</c>。</summary>
        public static void WriteFloat(StringBuilder sb, float v)
        {
            if (float.IsNaN(v) || float.IsInfinity(v))
            {
                sb.Append("null");
                return;
            }
            sb.Append(v.ToString("R", CultureInfo.InvariantCulture));
        }

        /// <summary>写一个双精度数：<c>"R"</c> 格式；<c>NaN</c> / <c>±Infinity</c> ⇒ 写 <c>null</c>。</summary>
        public static void WriteDouble(StringBuilder sb, double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v))
            {
                sb.Append("null");
                return;
            }
            sb.Append(v.ToString("R", CultureInfo.InvariantCulture));
        }

        /// <summary>写 JSON 空值。</summary>
        public static void WriteNull(StringBuilder sb)
        {
            sb.Append("null");
        }

        // ═════════════════════════════════════════════════════════════════════
        // 最小解析原语（<c>ref int pos</c> 游标推进；调用方负责处理"解析后有多余字符"）
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>跳过 JSON 空白（空格 / 制表 / 换行 / 回车）。</summary>
        public static void SkipWhitespace(string s, ref int pos)
        {
            while (pos < s.Length)
            {
                var c = s[pos];
                if (c == ' ' || c == '\t' || c == '\n' || c == '\r') { pos++; continue; }
                break;
            }
        }

        /// <summary>
        /// 从 <paramref name="pos"/> 处解析一个 JSON 值并推进游标。
        /// 产物映射见类型注释；语法非法抛 <see cref="FormatException"/>。
        /// </summary>
        public static object ParseValue(string s, ref int pos)
        {
            return ParseValue(s, ref pos, 0);
        }

        /// <summary>从 <paramref name="pos"/>（指向 <c>{</c>）解析一个 JSON 对象。</summary>
        public static Dictionary<string, object> ParseObject(string s, ref int pos)
        {
            return ParseObject(s, ref pos, 0);
        }

        /// <summary>从 <paramref name="pos"/>（指向 <c>[</c>）解析一个 JSON 数组。</summary>
        public static List<object> ParseArray(string s, ref int pos)
        {
            return ParseArray(s, ref pos, 0);
        }

        /// <summary>从 <paramref name="pos"/>（指向开引号）解析一个 JSON 字符串（含转义）。</summary>
        public static string ParseString(string s, ref int pos)
        {
            pos++;                                   // 开引号
            var sb = new StringBuilder();
            while (true)
            {
                if (pos >= s.Length) throw new FormatException("unterminated json string");
                var c = s[pos++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }

                if (pos >= s.Length) throw new FormatException("unterminated json escape");
                var e = s[pos++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u':
                        if (pos + 4 > s.Length) throw new FormatException("incomplete \\u escape");
                        var hex = s.Substring(pos, 4);
                        pos += 4;
                        sb.Append((char)int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        break;
                    default:
                        throw new FormatException($"unknown escape \\{e}");
                }
            }
        }

        /// <summary>
        /// 从 <paramref name="pos"/> 处解析一个 JSON 数字并推进游标。
        /// 含 <c>.</c> / <c>e</c> / <c>E</c> ⇒ <c>double</c>；否则先 <c>long</c>，long 放不下才退 <c>double</c>。
        /// </summary>
        public static object ParseNumber(string s, ref int pos)
        {
            var start = pos;
            var isFloat = false;
            if (pos < s.Length && (s[pos] == '-' || s[pos] == '+')) pos++;
            while (pos < s.Length)
            {
                var c = s[pos];
                if (c >= '0' && c <= '9') { pos++; continue; }
                if (c == '.' || c == 'e' || c == 'E' || c == '+' || c == '-')
                {
                    isFloat = isFloat || c == '.' || c == 'e' || c == 'E';
                    pos++;
                    continue;
                }
                break;
            }
            if (pos == start) throw new FormatException("expected a value");

            var text = s.Substring(start, pos - start);
            if (isFloat)
            {
                double d;
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                    throw new FormatException($"invalid float \"{text}\"");
                return d;
            }

            long l;
            if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out l))
            {
                double d;
                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
                throw new FormatException($"invalid integer \"{text}\"");
            }
            return l;
        }

        // ── 私有：带深度的递归实现（公开签名不带 depth，深度只在内部传递）──────────

        private static object ParseValue(string s, ref int pos, int depth)
        {
            if (depth > MaxDepth)
                throw new FormatException($"json nested too deep (>{MaxDepth})");
            SkipWhitespace(s, ref pos);
            if (pos >= s.Length) throw new FormatException("unexpected end of json (expected a value)");

            var c = s[pos];
            switch (c)
            {
                case '{': return ParseObject(s, ref pos, depth);
                case '[': return ParseArray(s, ref pos, depth);
                case '"': return ParseString(s, ref pos);
                case 't':
                    Expect(s, ref pos, "true");
                    return true;
                case 'f':
                    Expect(s, ref pos, "false");
                    return false;
                case 'n':
                    Expect(s, ref pos, "null");
                    return null;
                default:
                    return ParseNumber(s, ref pos);
            }
        }

        private static Dictionary<string, object> ParseObject(string s, ref int pos, int depth)
        {
            var res = new Dictionary<string, object>();
            pos++;                                   // '{'
            SkipWhitespace(s, ref pos);
            if (pos < s.Length && s[pos] == '}') { pos++; return res; }

            while (true)
            {
                SkipWhitespace(s, ref pos);
                if (pos >= s.Length || s[pos] != '"')
                    throw new FormatException($"expected an object key (string) at {pos}");
                var key = ParseString(s, ref pos);
                SkipWhitespace(s, ref pos);
                if (pos >= s.Length || s[pos] != ':')
                    throw new FormatException($"expected ':' at {pos}");
                pos++;
                res[key] = ParseValue(s, ref pos, depth + 1);
                SkipWhitespace(s, ref pos);
                if (pos >= s.Length) throw new FormatException("unterminated json object (expected '}')");
                if (s[pos] == ',') { pos++; continue; }
                if (s[pos] == '}') { pos++; return res; }
                throw new FormatException($"expected ',' or '}}' at {pos}");
            }
        }

        private static List<object> ParseArray(string s, ref int pos, int depth)
        {
            var res = new List<object>();
            pos++;                                   // '['
            SkipWhitespace(s, ref pos);
            if (pos < s.Length && s[pos] == ']') { pos++; return res; }

            while (true)
            {
                res.Add(ParseValue(s, ref pos, depth + 1));
                SkipWhitespace(s, ref pos);
                if (pos >= s.Length) throw new FormatException("unterminated json array (expected ']')");
                if (s[pos] == ',') { pos++; continue; }
                if (s[pos] == ']') { pos++; return res; }
                throw new FormatException($"expected ',' or ']' at {pos}");
            }
        }

        private static void Expect(string s, ref int pos, string token)
        {
            if (pos + token.Length > s.Length || string.CompareOrdinal(s, pos, token, 0, token.Length) != 0)
                throw new FormatException($"expected \"{token}\" at {pos}");
            pos += token.Length;
        }
    }
}
