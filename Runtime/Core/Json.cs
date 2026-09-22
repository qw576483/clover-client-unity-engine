using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace CloverEngine
{
    /// <summary>
    /// 轻量 JSON 解析/序列化工具，零第三方依赖，纯静态线程安全。
    /// 用于 JsonUtility 无法覆盖的场景：Dictionary / List、顶层非对象、嵌套动态 JSON
    /// （如 EPushDataSync 批量形态的顶层 map、body 内嵌对象、服务端 json.RawMessage）。
    /// 解析产物映射：object → Dictionary&lt;string, object&gt; / List&lt;object&gt; / string /
    /// long（整型）/ ulong（超出 long 范围但仍放得下 uint64 的整型，如服务端 uint64 对象号）/
    /// double（浮点）/ bool / null。整数字面量若连 ulong 都放不下，**按原文保留为 string**
    /// （宁可类型不匹配，也不静默丢精度；见 <c>ParseNumber</c>）。
    /// </summary>
    public static class MiniJson
    {
        /// <summary>解析递归深度上限，防御恶意深嵌套导致栈溢出</summary>
        private const int MaxDepth = 128;

        /// <summary>
        /// 解析 JSON 文本为动态对象树。
        /// </summary>
        /// <param name="json">UTF-8 JSON 文本</param>
        /// <returns>Dictionary/List/基本值；空或纯空白文本返回 null</returns>
        /// <exception cref="FormatException">JSON 语法非法或嵌套超限</exception>
        public static object Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return null;
            var parser = new Parser(json);
            var value = parser.ParseValue(0);
            parser.SkipWhitespace();
            if (!parser.IsEnd)
                throw new FormatException($"trailing characters at {parser.Position}");
            return value;
        }

        /// <summary>
        /// 解析 JSON 字节片段（UTF-8）为动态对象树。
        /// </summary>
        public static object Parse(byte[] data, int offset, int length)
        {
            return Parse(Encoding.UTF8.GetString(data, offset, length));
        }

        /// <summary>
        /// 将对象树序列化为 JSON 文本。
        /// 支持 Dictionary&lt;string, object&gt; / List / string / bool / 数值 / null / 枚举 / 时间；
        /// float/double 非有限值输出 null（对齐 JSON 规范）。
        /// <para>加固（对齐 Parse 侧的 <see cref="MaxDepth"/>）：</para>
        /// <list type="bullet">
        /// <item><b>循环引用 + 深度保护</b>：递归时携带「当前路径」的引用集合，自引用对象直接报
        /// <see cref="FormatException"/>，深度超过 <see cref="MaxDepth"/> 同样报错 ——
        /// 旧实现没有这两层保护，对象图成环会一路递归到 StackOverflowException（不可捕获、进程直接崩）。</item>
        /// <item><b>float 不再先提升成 double</b>：按 float 自己的最短可往返格式输出，
        /// 不会再把 1.1f 打印成 1.100000023841858（那是 float→double 的精确值）。</item>
        /// <item><b>浮点保型</b>：整数值的 double（如 1.0）输出为 <c>1.0</c> 而不是 <c>1</c>，
        /// 这样 Parse 回来仍是 double（否则会被 ParseNumber 读成 long，类型信息丢失）。</item>
        /// <item><b>枚举 / DateTime / DateTimeOffset / TimeSpan 加引号</b>：它们走
        /// <c>Convert.ToString</c> 时会输出不带引号的裸文本（如 <c>2024-01-01 00:00:00</c>），
        /// 那是非法 JSON；现在统一按字符串输出（TimeSpan 不实现 IConvertible，原本还会落到
        /// default 分支抛 ArgumentException）。</item>
        /// <item><b>键序稳定</b>：对象键按 <see cref="StringComparer.Ordinal"/> 排序后输出，
        /// 同一份数据每次序列化结果一致（旧实现直接 foreach 字典，顺序取决于字典内部布局，
        /// 增删/rehash 后会变，造成 diff 噪音与内容哈希不稳定）。</item>
        /// </list>
        /// </summary>
        public static string Dump(object value)
        {
            var sb = new StringBuilder();
            DumpValue(sb, value, 0, null);
            return sb.ToString();
        }

        private static void DumpValue(StringBuilder sb, object value, int depth, HashSet<object> path)
        {
            // 与 Parse 侧对齐的深度上限：超过即报错，绝不让它递归到栈溢出
            if (depth > MaxDepth)
                throw new FormatException($"json nested too deep (>{MaxDepth})");

            switch (value)
            {
                case null:
                    sb.Append("null");
                    break;
                case bool b:
                    sb.Append(b ? "true" : "false");
                    break;
                case string s:
                    DumpString(sb, s);
                    break;
                case char c:
                    DumpString(sb, c.ToString());
                    break;
                case float f:
                    AppendSingle(sb, f);
                    break;
                case double d:
                    AppendDouble(sb, d);
                    break;
                case decimal m:
                    sb.Append(EnsureFraction(m.ToString(CultureInfo.InvariantCulture)));
                    break;
                // 下面几个都实现了（或没实现）IConvertible，但 Convert.ToString 会产出不带引号的裸文本，
                // 那是非法 JSON：必须排在 `case IConvertible` 之前并显式加引号输出。
                case DateTime dt:
                    DumpString(sb, dt.ToString("o", CultureInfo.InvariantCulture));
                    break;
                case DateTimeOffset dto:
                    DumpString(sb, dto.ToString("o", CultureInfo.InvariantCulture));
                    break;
                case TimeSpan ts:
                    // TimeSpan 不实现 IConvertible，若不在这里接住会落到 default 分支抛 ArgumentException
                    DumpString(sb, ts.ToString("c", CultureInfo.InvariantCulture));
                    break;
                case Enum e:
                    DumpString(sb, e.ToString());
                    break;
                case IConvertible:
                    // int/long/short/byte 等，统一不变文化输出
                    sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                    break;
                case IDictionary<string, object> dict:
                    DumpObject(sb, dict, depth, path);
                    break;
                // 其它字典（Dictionary<string,int> / Hashtable / SortedList…）只实现**非泛型** IDictionary：
                // 不加这一支会落到下面的 IEnumerable 分支被当数组遍历，元素是 KeyValuePair/DictionaryEntry
                // → 再到 default 抛 ArgumentException，整个 Dump 失败（表现为"Set 一个 Dictionary<string,int> 就落不了盘"）。
                case System.Collections.IDictionary dictionary:
                    DumpDictionary(sb, dictionary, depth, path);
                    break;
                case System.Collections.IEnumerable enumerable:
                    DumpArray(sb, enumerable, depth, path);
                    break;
                default:
                    throw new ArgumentException($"unsupported type: {value.GetType()}");
            }
        }

        private static void AppendDouble(StringBuilder sb, double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d))
            {
                sb.Append("null");
                return;
            }
            sb.Append(EnsureFraction(d.ToString("R", CultureInfo.InvariantCulture)));
        }

        private static void AppendSingle(StringBuilder sb, float f)
        {
            if (float.IsNaN(f) || float.IsInfinity(f))
            {
                sb.Append("null");
                return;
            }
            // 必须按 float 自己的最短可往返格式输出：
            // 旧实现先提升为 double 再打印，1.1f 会变成 1.100000023841858（float→double 的精确值）。
            sb.Append(EnsureFraction(f.ToString("R", CultureInfo.InvariantCulture)));
        }

        /// <summary>
        /// 给"看起来是整数"的浮点文本补上 <c>.0</c>：
        /// double 1.0 的 "R" 输出是 "1"，Parse 回来会变 long；补 ".0" 后 Parse 走 double 分支，往返保型。
        /// 已含 '.' / 'e' / 'E' 的文本（3.5、1E+20）本身就是浮点形态，原样返回。
        /// </summary>
        private static string EnsureFraction(string text)
        {
            if (text.IndexOf('.') >= 0 || text.IndexOf('e') >= 0 || text.IndexOf('E') >= 0)
                return text;
            return text + ".0";
        }

        private static void DumpObject(StringBuilder sb, IDictionary<string, object> dict, int depth, HashSet<object> path)
        {
            path = path ?? new HashSet<object>(ReferenceComparer.Instance);
            // 循环引用保护：同一个容器对象若已在当前递归路径上，再进去就是无限递归（StackOverflow）
            if (!path.Add(dict))
                throw new FormatException("circular reference detected while dumping json object");

            try
            {
                sb.Append('{');
                var first = true;
                foreach (var kv in SortedEntries(dict))
                {
                    if (!first)
                        sb.Append(',');
                    first = false;
                    DumpString(sb, kv.Key);
                    sb.Append(':');
                    DumpValue(sb, kv.Value, depth + 1, path);
                }
                sb.Append('}');
            }
            finally
            {
                // 只移除自己：同一容器在不同分支上重复出现（菱形引用）是合法的，不该被判成环
                path.Remove(dict);
            }
        }

        /// <summary>
        /// 序列化任意 <see cref="System.Collections.IDictionary"/>（键统一按不变文化转成字符串，
        /// 键序同样按 Ordinal 排序）—— 与 <see cref="DumpObject"/> 的输出形态完全一致：
        /// 同一份数据无论用 <c>Dictionary&lt;string,object&gt;</c> 还是别的字典装，序列化结果都相同。
        /// </summary>
        private static void DumpDictionary(StringBuilder sb, System.Collections.IDictionary dict, int depth, HashSet<object> path)
        {
            path = path ?? new HashSet<object>(ReferenceComparer.Instance);
            if (!path.Add(dict))
                throw new FormatException("circular reference detected while dumping json object");

            try
            {
                var entries = new List<KeyValuePair<string, object>>(dict.Count);
                foreach (System.Collections.DictionaryEntry entry in dict)
                {
                    // 键必须是字符串（JSON 对象键）：非字符串键按不变文化转写；null 键退化为空串
                    // （Hashtable 允许 null 键，直接 DumpString(null) 会 NRE）。
                    var key = Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? string.Empty;
                    entries.Add(new KeyValuePair<string, object>(key, entry.Value));
                }
                entries.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));

                sb.Append('{');
                var first = true;
                foreach (var kv in entries)
                {
                    if (!first)
                        sb.Append(',');
                    first = false;
                    DumpString(sb, kv.Key);
                    sb.Append(':');
                    DumpValue(sb, kv.Value, depth + 1, path);
                }
                sb.Append('}');
            }
            finally
            {
                path.Remove(dict);
            }
        }

        private static void DumpArray(StringBuilder sb, System.Collections.IEnumerable enumerable, int depth, HashSet<object> path)
        {
            path = path ?? new HashSet<object>(ReferenceComparer.Instance);
            if (!path.Add(enumerable))
                throw new FormatException("circular reference detected while dumping json array");

            try
            {
                sb.Append('[');
                var first = true;
                foreach (var item in enumerable)
                {
                    if (!first)
                        sb.Append(',');
                    first = false;
                    DumpValue(sb, item, depth + 1, path);
                }
                sb.Append(']');
            }
            finally
            {
                path.Remove(enumerable);
            }
        }

        /// <summary>按键的 Ordinal 序排序，保证输出的键序稳定（与字典内部布局无关）。</summary>
        private static List<KeyValuePair<string, object>> SortedEntries(IDictionary<string, object> dict)
        {
            var entries = new List<KeyValuePair<string, object>>(dict.Count);
            foreach (var kv in dict)
                entries.Add(kv);
            entries.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
            return entries;
        }

        /// <summary>按引用判等的比较器：循环引用检测必须按对象身份判等，不能走 Equals 重写。</summary>
        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceComparer Instance = new();

            // 显式实现：避免与静态的 object.Equals(object, object) 撞名触发隐藏警告
            bool IEqualityComparer<object>.Equals(object x, object y) => ReferenceEquals(x, y);

            int IEqualityComparer<object>.GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }

        private static void DumpString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        /// <summary>
        /// 从对象树中按字符串键取值（不存在返回 null）。
        /// </summary>
        public static object Get(IDictionary<string, object> dict, string key)
        {
            return dict != null && dict.TryGetValue(key, out var v) ? v : null;
        }

        /// <summary>
        /// 从对象树中按字符串键取字符串值（非字符串或不存在返回 null）。
        /// </summary>
        public static string GetString(IDictionary<string, object> dict, string key)
        {
            return Get(dict, key) as string;
        }

        private sealed class Parser
        {
            private readonly string _json;
            private int _pos;

            public Parser(string json)
            {
                _json = json;
            }

            public bool IsEnd => _pos >= _json.Length;

            public int Position => _pos;

            public void SkipWhitespace()
            {
                while (_pos < _json.Length && char.IsWhiteSpace(_json[_pos]))
                    _pos++;
            }

            private char Peek()
            {
                if (IsEnd)
                    throw new FormatException("unexpected end of json");
                return _json[_pos];
            }

            private char Next()
            {
                var c = Peek();
                _pos++;
                return c;
            }

            private void Expect(char c)
            {
                if (Next() != c)
                    throw new FormatException($"expected '{c}' at {_pos - 1}");
            }

            public object ParseValue(int depth)
            {
                if (depth > MaxDepth)
                    throw new FormatException("json nested too deep");
                SkipWhitespace();
                switch (Peek())
                {
                    case '{':
                        return ParseObject(depth);
                    case '[':
                        return ParseArray(depth);
                    case '"':
                        return ParseString();
                    case 't':
                        ExpectLiteral("true");
                        return true;
                    case 'f':
                        ExpectLiteral("false");
                        return false;
                    case 'n':
                        ExpectLiteral("null");
                        return null;
                    default:
                        return ParseNumber();
                }
            }

            private void ExpectLiteral(string literal)
            {
                foreach (var c in literal)
                {
                    if (Next() != c)
                        throw new FormatException($"invalid literal near {_pos}");
                }
            }

            private object ParseObject(int depth)
            {
                Expect('{');
                var dict = new Dictionary<string, object>();
                SkipWhitespace();
                if (Peek() == '}')
                {
                    _pos++;
                    return dict;
                }
                while (true)
                {
                    SkipWhitespace();
                    var key = ParseString();
                    SkipWhitespace();
                    Expect(':');
                    dict[key] = ParseValue(depth + 1);
                    SkipWhitespace();
                    var c = Next();
                    if (c == '}')
                        return dict;
                    if (c != ',')
                        throw new FormatException($"expected ',' or '}}' at {_pos - 1}");
                }
            }

            private object ParseArray(int depth)
            {
                Expect('[');
                var list = new List<object>();
                SkipWhitespace();
                if (Peek() == ']')
                {
                    _pos++;
                    return list;
                }
                while (true)
                {
                    list.Add(ParseValue(depth + 1));
                    SkipWhitespace();
                    var c = Next();
                    if (c == ']')
                        return list;
                    if (c != ',')
                        throw new FormatException($"expected ',' or ']' at {_pos - 1}");
                }
            }

            private string ParseString()
            {
                Expect('"');
                var sb = new StringBuilder();
                while (true)
                {
                    var c = Next();
                    if (c == '"')
                        return sb.ToString();
                    if (c == '\\')
                    {
                        var esc = Next();
                        switch (esc)
                        {
                            case '"': sb.Append('"'); break;
                            case '\\': sb.Append('\\'); break;
                            case '/': sb.Append('/'); break;
                            case 'b': sb.Append('\b'); break;
                            case 'f': sb.Append('\f'); break;
                            case 'n': sb.Append('\n'); break;
                            case 'r': sb.Append('\r'); break;
                            case 't': sb.Append('\t'); break;
                            case 'u':
                                sb.Append(ParseUnicodeEscape());
                                break;
                            default:
                                throw new FormatException($"invalid escape '\\{esc}' at {_pos - 1}");
                        }
                    }
                    else
                    {
                        sb.Append(c);
                    }
                }
            }

            private string ParseUnicodeEscape()
            {
                if (_pos + 4 > _json.Length)
                    throw new FormatException("invalid \\u escape");
                var code = Convert.ToInt32(_json.Substring(_pos, 4), 16);
                _pos += 4;
                
                // 修复bug：正确处理代理对（surrogate pair）
                // 检查是否是高代理项（U+D800~U+DBFF），如果是，需要继续解析低代理项
                if (code >= 0xD800 && code <= 0xDBFF)
                {
                    // 高代理项，需要继续解析低代理项
                    if (_pos + 1 < _json.Length && _json[_pos] == '\\' && _json[_pos + 1] == 'u')
                    {
                        _pos += 2; // 跳过 '\\u'
                        if (_pos + 4 > _json.Length)
                            throw new FormatException("invalid surrogate pair: missing low surrogate");
                        var lowCode = Convert.ToInt32(_json.Substring(_pos, 4), 16);
                        _pos += 4;
                        
                        if (lowCode >= 0xDC00 && lowCode <= 0xDFFF)
                        {
                            // 有效代理对，组合成完整的Unicode字符
                            var highSurrogate = (char)code;
                            var lowSurrogate = (char)lowCode;
                            return new string(new[] { highSurrogate, lowSurrogate });
                        }
                        else
                        {
                            throw new FormatException($"invalid low surrogate: U+{lowCode:X4}");
                        }
                    }
                    else
                    {
                        throw new FormatException("invalid surrogate pair: missing low surrogate");
                    }
                }
                else if (code >= 0xDC00 && code <= 0xDFFF)
                {
                    // 低代理项，不允许单独出现
                    throw new FormatException($"unexpected low surrogate: U+{code:X4}");
                }
                
                return ((char)code).ToString();
            }

            private object ParseNumber()
            {
                var start = _pos;
                while (_pos < _json.Length && "-+.eE0123456789".IndexOf(_json[_pos]) >= 0)
                    _pos++;
                var text = _json.Substring(start, _pos - start);
                if (text.Length == 0)
                    throw new FormatException($"invalid number at {start}");
                if (text.IndexOf('.') >= 0 || text.IndexOf('e') >= 0 || text.IndexOf('E') >= 0)
                    return double.Parse(text, CultureInfo.InvariantCulture);
                if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
                    return l;

                // 超出 long 范围：**不许静默退化为 double** —— double 只有 53 位有效尾数，
                // 接近 2^63 的大整数（服务端 uint64 对象号 / 雪花 ID）会被就近取整，
                // Parse 出来的值跟原文不是同一个数，且不报错（表现为"实体号对不上，却查无异常"）。
                // 顺序：long → ulong（uint64 放得下）→ 原文字符串（宁可是字符串，也不丢精度）。
                if (ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ul))
                    return ul;

                Game.Logger?.Warn("Json",
                    $"数字超出 ulong 范围，按原文保留为字符串以避免精度丢失：{text}");
                return text;
            }
        }
    }
}
