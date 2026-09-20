using System;
using System.Collections.Generic;
using System.Text;

namespace CloverEngine
{
    /// <summary>
    /// 客户端传输类型。
    /// 与服务端网关的三条真实接入面一一对应：
    /// TCP（<c>gateway.listen_tcp</c>）、WebSocket（<c>gateway.listen_ws</c> + <c>ws_path</c>）、
    /// 裸 UDP（<c>gateway.listen_udp</c>，与 QUIC 共享端口，靠首字节 0x55 魔数分流）。
    /// </summary>
    public enum TransportKind
    {
        /// <summary>TCP 长连接，传输层帧 <c>[1B type][4B len][payload]</c>，可靠通道保底。</summary>
        Tcp = 0,

        /// <summary>
        /// WebSocket 长连接，一条二进制消息即一个客户端帧（无长度前缀、无魔数）。
        /// 服务端按 BinaryMessage 读取，TextMessage 静默丢弃并告警。
        /// </summary>
        WebSocket = 1,

        /// <summary>裸 UDP 不可靠通道，报文首字节为 0x55 魔数，仅承载不可靠消息与推送。</summary>
        RawUdp = 2,

        /// <summary>
        /// QUIC 长连接（原生 msquic 插件）：可靠数据走客户端主动开的首条双向流（帧前 <c>[4B 大端 len]</c>），
        /// 不可靠数据走 QUIC Datagram（因此 **QUIC 模式下不需要 <c>EMsgBindUDP</c>**）。
        /// 服务端 <c>gateway.listen_udp</c> 与裸 UDP 共享端口，按首字节 0x55 魔数分流。
        /// </summary>
        Quic = 3,

        /// <summary>
        /// WebTransport（WebGL）：建在 HTTP/3（即 QUIC）之上，浏览器侧经 jslib 桥接。
        /// 原生平台不用它（原生家族用 <see cref="Quic"/>）。
        /// </summary>
        WebTransport = 4,
    }

    /// <summary>
    /// 一条具体线路：类型 + 地址（WebSocket 另带升级路径与 TLS 开关）。
    /// 纯数据、无行为，可由配置直接构造；多条线路组成「主链 → 降级链」顺序。
    /// </summary>
    public sealed class TransportLine
    {
        /// <summary>线路类型。</summary>
        public TransportKind Kind { get; }

        /// <summary>「host:port」形式的目标地址。</summary>
        public string Addr { get; }

        /// <summary>WebSocket 升级路径（如 <c>/ws</c>）；其它线路为 null。</summary>
        public string Path { get; }

        /// <summary>是否使用 TLS（WebSocket 走 <c>wss://</c>；TCP 走 SslStream）。</summary>
        public bool UseTls { get; }

        /// <summary>构造一条线路。地址为空视为无效线路，组合时会被跳过。</summary>
        /// <param name="kind">线路类型。</param>
        /// <param name="addr">目标地址，「host:port」形式。</param>
        /// <param name="path">WebSocket 升级路径，其它线路传 null。</param>
        /// <param name="useTls">是否启用 TLS。</param>
        public TransportLine(TransportKind kind, string addr, string path = null, bool useTls = false)
        {
            Kind = kind;
            Addr = addr;
            Path = path;
            UseTls = useTls;
        }

        /// <summary>
        /// 拼出 WebSocket 连接的完整 URL（如 <c>ws://127.0.0.1:8001/ws</c>）。
        /// 非 WebSocket 线路或地址为空时返回空串。
        /// </summary>
        public string BuildWebSocketUrl()
        {
            if (Kind != TransportKind.WebSocket || string.IsNullOrEmpty(Addr))
                return string.Empty;

            var path = string.IsNullOrEmpty(Path) ? "/ws" : Path;
            if (!path.StartsWith("/"))
                path = "/" + path;
            return (UseTls ? "wss://" : "ws://") + Addr + path;
        }

        /// <summary>用于日志与调试面板的简短描述。</summary>
        public string Describe()
        {
            switch (Kind)
            {
                case TransportKind.WebSocket:
                    var path = string.IsNullOrEmpty(Path) ? "/ws" : Path;
                    return "ws:" + Addr + path + (UseTls ? " (tls)" : string.Empty);
                case TransportKind.RawUdp:
                    return "udp:" + Addr;
                case TransportKind.Quic:
                    return "quic:" + Addr;
                case TransportKind.WebTransport:
                    return "wt:" + Addr + (UseTls ? " (tls)" : string.Empty);
                default:
                    return "tcp:" + Addr + (UseTls ? " (tls)" : string.Empty);
            }
        }

        /// <summary>返回 <see cref="Describe"/> 的结果。</summary>
        public override string ToString() => Describe();
    }

    /// <summary>
    /// 线路选择配置：可靠通道按 <see cref="ReliableOrder"/> 顺序尝试（首个为主链，其余为降级链），
    /// 不可靠通道由 <see cref="UdpAddr"/> 单独指定。
    /// 被裁剪（平台不支持）与全部失败的判定由网络模块负责，本类只承载选择结果。
    /// </summary>
    public sealed class TransportOptions
    {
        /// <summary>可靠通道尝试顺序；可能为空（配置缺失），此时连接会明确报错而不是静默不发包。</summary>
        public IReadOnlyList<TransportLine> ReliableOrder { get; }

        /// <summary>裸 UDP 共享端点；null 表示不启用不可靠通道。</summary>
        public string UdpAddr { get; }

        /// <summary>按给定顺序构造；地址为空的线路会被丢弃。</summary>
        /// <param name="reliableOrder">可靠线路顺序，可为 null。</param>
        /// <param name="udpAddr">裸 UDP 端点，可为 null。</param>
        public TransportOptions(IEnumerable<TransportLine> reliableOrder, string udpAddr = null)
        {
            var list = new List<TransportLine>();
            if (reliableOrder != null)
            {
                foreach (var line in reliableOrder)
                {
                    if (line != null && !string.IsNullOrEmpty(line.Addr))
                        list.Add(line);
                }
            }

            ReliableOrder = list;
            UdpAddr = string.IsNullOrEmpty(udpAddr) ? null : udpAddr;
        }

        /// <summary>
        /// 按地址拼出默认顺序：TCP（原生家族）+ WebSocket（Web 家族）。
        /// <para>
        /// **两者不会同时生效**：运行平台只属于一个家族（依据 <c>结构规则.md</c> §五 N10）——
        /// 原生端只保留 TCP，WebGL 只保留 WebSocket，由
        /// <c>TransportPlanner.ApplyPlatformRules</c> 按平台裁剪，被裁掉的那条会记进日志。
        /// 因此「TCP 连不上就退到 WS」这种情况**不会发生**：那不是降级，是换协议家族。
        /// </para>
        /// <para>
        /// 典型用法：原生工程只传 <paramref name="tcpAddr"/>（+ <paramref name="udpAddr"/>）；
        /// WebGL 工程只传 <paramref name="wsAddr"/>。两个都传是允许的（同一份配置跑多端），
        /// 但任一时刻只有属于当前平台的那条会生效。
        /// </para>
        /// </summary>
        /// <param name="tcpAddr">网关 TCP 口地址。</param>
        /// <param name="udpAddr">裸 UDP 端点。</param>
        /// <param name="wsAddr">网关 WebSocket 口地址。</param>
        /// <param name="wsPath">WebSocket 升级路径，默认 <c>/ws</c>。</param>
        /// <param name="useTls">是否启用 TLS。</param>
        public static TransportOptions From(string tcpAddr, string udpAddr = null, string wsAddr = null,
            string wsPath = "/ws", bool useTls = false, string quicAddr = null)
        {
            var lines = new List<TransportLine>();
            // 原生家族顺序：QUIC → TCP（QUIC 与裸 UDP 共用服务端的 listen_udp 端点，
            // 因此 quicAddr 通常就是 udpAddr —— 显式传参而不是在这里推断，便于测试与单机特例）。
            if (!string.IsNullOrEmpty(quicAddr))
                lines.Add(new TransportLine(TransportKind.Quic, quicAddr, null, useTls));
            if (!string.IsNullOrEmpty(tcpAddr))
                lines.Add(new TransportLine(TransportKind.Tcp, tcpAddr, null, useTls));
            if (!string.IsNullOrEmpty(wsAddr))
                lines.Add(new TransportLine(TransportKind.WebSocket, wsAddr, wsPath, useTls));
            return new TransportOptions(lines, udpAddr);
        }

        /// <summary>用于日志与调试面板的线路全貌描述，如 <c>tcp:127.0.0.1:8002 -&gt; ws:127.0.0.1:8001/ws | udp:127.0.0.1:8003</c>。</summary>
        public string Describe()
        {
            var sb = new StringBuilder();
            for (var i = 0; i < ReliableOrder.Count; i++)
            {
                if (i > 0)
                    sb.Append(" -> ");
                sb.Append(ReliableOrder[i].Describe());
            }

            if (ReliableOrder.Count == 0)
                sb.Append("(无可靠线路)");
            if (UdpAddr != null)
                sb.Append(" | udp:").Append(UdpAddr);
            return sb.ToString();
        }

        /// <summary>返回 <see cref="Describe"/> 的结果。</summary>
        public override string ToString() => Describe();
    }
}
