using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace CloverEngine
{
    /// <summary>
    /// 局域网寻服协议编解码：**纯函数**（不建 socket、不读引擎状态、不起线程），
    /// 因此可以被单测直接喂字节，也可以被 PlayMode 回环用例当参考实现。
    ///
    /// <para>
    /// 报文格式（文本 + UTF8，UDP 一包一报文，<b>无长度前缀</b>）：
    /// <code>
    /// 查询（客户端 → 广播/单播，默认端口 47777）
    ///   CLOVER-LAN-QUERY/1|&lt;nonce&gt;           nonce = 16 字节随机数的 hex（32 字符）
    /// 应答（主机 → 单播回查询来源地址）
    ///   CLOVER-LAN-REPLY/1|&lt;json&gt;            json = 扁平对象，字段全可选，缺省即默认值
    /// </code>
    /// </para>
    ///
    /// <para>
    /// <b>与既有约定的边界</b>（结构规则.md §五 N13）：
    /// 本类<b>不引用</b> <c>EMsg</c>（不占消息号、N3 手工消息号约定不受影响）、
    /// 不经 <c>Router</c>、不碰 <c>TransportKind</c> / <c>CloverNet</c>。
    /// 魔数用 <c>LAN</c> 段与其它 Clover 协议区分。
    /// </para>
    /// </summary>
    internal static class LanProtocol
    {
        /// <summary>默认查询端口（与网关 <c>listen_tcp</c>/<c>listen_udp</c> 无关，是寻服专用端口）。</summary>
        public const int DefaultPort = 47777;

        /// <summary>结果集上限：超出的新主机被丢弃（打 Warn，不静默）。</summary>
        public const int MaxHosts = 64;

        /// <summary>单包长度上限：超过即丢弃（防刷）。</summary>
        public const int MaxDatagramBytes = 512;

        /// <summary>nonce 随机字节数。</summary>
        public const int NonceBytes = 16;

        /// <summary>nonce 的 hex 字符数（<see cref="NonceBytes"/> × 2）。</summary>
        public const int NonceHexLength = NonceBytes * 2;

        /// <summary>查询报文魔数（首段）。</summary>
        public const string QueryMagic = "CLOVER-LAN-QUERY/1";

        /// <summary>应答报文魔数（首段）。</summary>
        public const string ReplyMagic = "CLOVER-LAN-REPLY/1";

        /// <summary>日志里打印网关/主机串时的截断长度（防超长脏串刷屏）。</summary>
        private const int LogTextMax = 48;

        /// <summary>
        /// 生成一轮扫描的 nonce：16 字节密码学随机数的 hex（32 个小写字符）。
        /// 每轮一个，本轮所有目标共用（复用同一个 nonce）；应答必须回显它，防上一轮的包「串味」。
        /// </summary>
        public static string NewNonce()
        {
            var bytes = new byte[NonceBytes];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(bytes);
            }

            var sb = new StringBuilder(NonceHexLength);
            foreach (var b in bytes)
                sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        /// <summary>构造查询报文（单包，无长度前缀）。</summary>
        /// <param name="nonce">本轮 nonce（见 <see cref="NewNonce"/>；传 null/空按空串拼）</param>
        public static byte[] BuildQuery(string nonce)
        {
            return Encoding.UTF8.GetBytes($"{QueryMagic}|{nonce}");
        }

        /// <summary>
        /// 解析一条应答报文。**任一条校验不过就返回 false**（调用方据此打 Warn 丢弃，不进结果集）。
        ///
        /// <para>校验顺序（先廉价后昂贵，且长度先于任何解析）：</para>
        /// <list type="number">
        /// <item>空包 / 超长（&gt; <see cref="MaxDatagramBytes"/>）丢弃；</item>
        /// <item>首段必须是 <see cref="ReplyMagic"/>（版本不符也在此拦下）；</item>
        /// <item><c>nonce</c> 必须等于本轮 nonce；</item>
        /// <item><c>gateway</c> 必须能解析成 <c>host:port</c>（host 取报文里的，<b>不信任来源 IP</b>
        /// —— 主机可能多网卡，来源 IP 只是它发出应答的网卡）；</item>
        /// <item><c>players</c> / <c>max</c> 非法值钳制（负→0），不算失败。</item>
        /// </list>
        /// </summary>
        /// <param name="datagram">收到的字节</param>
        /// <param name="length">有效长度（&lt;= datagram.Length）</param>
        /// <param name="expectedNonce">本轮 nonce（来自查询包）</param>
        /// <param name="host">成功时输出解析结果（失败为 null）</param>
        /// <param name="error">失败原因（成功为 null）</param>
        /// <param name="warning">成功但值得记一笔的问题（如可选的 udp 字段无法解析）；无则为 null</param>
        /// <returns>通过全部校验返回 true</returns>
        public static bool TryParseReply(byte[] datagram, int length, string expectedNonce,
            out LanHostInfo host, out string error, out string warning)
        {
            host = null;
            error = null;
            warning = null;

            if (datagram == null)
            {
                error = "报文为空引用";
                return false;
            }

            if (length <= 0)
            {
                error = "空包";
                return false;
            }

            if (length > MaxDatagramBytes)
            {
                error = $"超长包（{length} 字节 > 上限 {MaxDatagramBytes}）";
                return false;
            }

            if (length > datagram.Length)
            {
                error = $"声明长度 {length} 超出缓冲区 {datagram.Length}";
                return false;
            }

            string text;
            try
            {
                text = Encoding.UTF8.GetString(datagram, 0, length);
            }
            catch (Exception ex)
            {
                // UTF8 解码在正常实现下不会抛（非法字节走替换字符），这里只做兜底：
                // 任何解析期异常都不能冒泡出收包线程。
                error = $"UTF8 解码失败：{ex.Message}";
                return false;
            }

            var separator = text.IndexOf('|');
            if (separator < 0)
            {
                error = "缺少分隔符 '|'（形如 CLOVER-LAN-REPLY/1|{...}）";
                return false;
            }

            var magic = text.Substring(0, separator);
            if (!string.Equals(magic, ReplyMagic, StringComparison.Ordinal))
            {
                error = $"魔数不符（首段=\"{Truncate(magic)}\"，期望 \"{ReplyMagic}\"）";
                return false;
            }

            if (string.IsNullOrEmpty(expectedNonce))
            {
                error = "本轮无 nonce（未在扫描中）";
                return false;
            }

            IDictionary<string, object> obj;
            try
            {
                obj = MiniJson.Parse(text.Substring(separator + 1)) as IDictionary<string, object>;
            }
            catch (FormatException ex)
            {
                error = $"JSON 解析失败：{ex.Message}";
                return false;
            }
            catch (Exception ex)
            {
                error = $"JSON 解析异常：{ex.Message}";
                return false;
            }

            if (obj == null)
            {
                error = "JSON 体不是对象";
                return false;
            }

            var nonce = MiniJson.GetString(obj, "nonce");
            if (!string.Equals(nonce, expectedNonce, StringComparison.OrdinalIgnoreCase))
            {
                error = $"nonce 不匹配（报文=\"{Truncate(nonce)}\"，本轮=\"{Truncate(expectedNonce)}\"）";
                return false;
            }

            var gateway = MiniJson.GetString(obj, "gateway");
            if (!TryParseEndpoint(gateway, out var gatewayHost, out var gatewayPort))
            {
                error = $"gateway 非法（\"{Truncate(gateway)}\"，需为 host:port 且端口在 1~65535）";
                return false;
            }

            // udp 是可选项：解析不了就按「未提供」处理（不丢整包 —— 丢整包会让一台其实可连的主机消失）。
            var udpPort = 0;
            var udpText = MiniJson.GetString(obj, "udp");
            if (!string.IsNullOrEmpty(udpText))
            {
                if (TryParseEndpoint(udpText, out _, out var parsedUdpPort))
                    udpPort = parsedUdpPort;
                else
                    warning = $"udp 字段无法解析（\"{Truncate(udpText)}\"），按未提供处理";
            }

            host = LanHostInfo.Create(
                gatewayHost,
                gatewayPort,
                udpPort,
                MiniJson.GetString(obj, "auth"),
                MiniJson.GetString(obj, "name"),
                ClampCount(MiniJson.Get(obj, "players")),
                ClampCount(MiniJson.Get(obj, "max")),
                MiniJson.GetString(obj, "version"),
                MiniJson.GetString(obj, "extra"));
            return true;
        }

        /// <summary>
        /// 去重键规范化：trim + 小写。
        /// 用于「同 gateway 视为同一台主机」（大小写/空白不同不算不同主机）。
        /// </summary>
        public static string NormalizeKey(string endpoint)
        {
            return endpoint == null ? string.Empty : endpoint.Trim().ToLowerInvariant();
        }

        /// <summary>
        /// 解析 <c>host:port</c>（也接受 <c>ip</c> 时由调用方走 <see cref="TryParseAddress"/>）。
        /// host 允许是 IPv4 字面量或主机名；IPv6 一律判非法（本能力的广播/子网计算都是 IPv4 语义，
        /// 放行只会产出喂不进 <c>CloverNet.Init</c> 的地址串）。
        /// </summary>
        public static bool TryParseEndpoint(string text, out string host, out int port)
        {
            host = null;
            port = 0;

            if (string.IsNullOrEmpty(text))
                return false;

            var trimmed = text.Trim();
            var separator = trimmed.LastIndexOf(':');
            if (separator <= 0 || separator == trimmed.Length - 1)
                return false;   // 无冒号 / host 为空 / port 为空

            var parsedHost = trimmed.Substring(0, separator).Trim();
            var portText = trimmed.Substring(separator + 1).Trim();
            if (parsedHost.Length == 0 || ContainsWhitespace(parsedHost))
                return false;

            // 无括号 IPv6（如 "fe80::1"）会被 LastIndexOf(':') 切成含冒号的垃圾 host（"fe80:" + "1"），
            // "host:port:extra" 之类多余冒号同理 —— 一律判非法：本能力的广播/子网计算都是 IPv4 语义，
            // 放行只会产出喂不进 CloverNet.Init 的地址串（文档口径：IPv6 一律判非法）。
            if (parsedHost.IndexOf(':') >= 0)
                return false;

            if (!int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out port))
                return false;

            if (port < 1 || port > 65535)
                return false;

            if (IPAddress.TryParse(parsedHost, out var ip))
            {
                if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                    return false;
                parsedHost = ip.ToString();   // IPv4 规范化（去前导零等）
            }

            host = parsedHost;
            return true;
        }

        /// <summary>解析纯 IP 字面量（供 ExtraTargets 里「只给 ip」的形态用）。</summary>
        public static bool TryParseAddress(string text, out IPAddress address)
        {
            address = null;
            if (string.IsNullOrEmpty(text))
                return false;

            return IPAddress.TryParse(text.Trim(), out address)
                   && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;
        }

        /// <summary>
        /// players / max 的钳制：负值与非数字一律按 0（0 对 <c>max</c> 表示「未知」，
        /// 对 <c>players</c> 表示「未报人数」）；数值过大按 int 上界截断。
        /// </summary>
        private static int ClampCount(object value)
        {
            switch (value)
            {
                case null:
                    return 0;
                case long l:
                    return l <= 0 ? 0 : (int)Math.Min(l, int.MaxValue);
                case int i:
                    return i <= 0 ? 0 : i;
                case double d:
                    return double.IsNaN(d) || d <= 0 ? 0 : (int)Math.Min(d, int.MaxValue);
                case string s:
                    return long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
                        ? (int)Math.Min(parsed, int.MaxValue)
                        : 0;
                default:
                    return 0;
            }
        }

        private static bool ContainsWhitespace(string value)
        {
            foreach (var c in value)
            {
                if (char.IsWhiteSpace(c))
                    return true;
            }

            return false;
        }

        /// <summary>日志用截断：脏报文可能带超长串，不能原样进日志。</summary>
        private static string Truncate(string value)
        {
            if (string.IsNullOrEmpty(value))
                return value ?? "<null>";
            return value.Length <= LogTextMax ? value : value.Substring(0, LogTextMax) + "…";
        }
    }
}
