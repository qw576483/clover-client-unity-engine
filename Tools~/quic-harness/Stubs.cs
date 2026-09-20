using System;

// Unity 里 [MonoPInvokeCallback] 来自 UnityEngine.dll 的 AOT 命名空间；
// 控制台试验台没有 Unity，这里补一个同名的空特性，让引擎源码能**原样**编译（见 Program.cs 顶部说明）。
namespace AOT
{
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class MonoPInvokeCallbackAttribute : Attribute
    {
        public MonoPInvokeCallbackAttribute(Type type) { }
    }
}

namespace CloverEngine
{
    // ============================================================================
    // 控制台试验台所需的**最小 Unity 替身**。
    //
    // 目的只有一个：让引擎里的 QUIC 原生源码（MsQuicNative / MsQuicRuntime /
    // QuicConnection / QuicStreamFraming）能**原样**在 .NET 控制台里编译运行，
    // 而不是复制一份出来（复制出来的那份迟早与真实代码漂移）。
    //
    // 这里只补"被引用到的最小面"：类型名与方法签名与 Unity 侧一致，
    // 行为上不做任何事（日志打到 stdout）。
    // ============================================================================

    /// <summary>线路类型（引擎里在 TransportContracts.cs；这里只留试验台用到的值）。</summary>
    public enum TransportKind
    {
        Tcp = 0,
        WebSocket = 1,
        RawUdp = 2,
        Quic = 3,
        // 与原枚举保持一致：WebTransport = 4 已删除（客户端从未实现，属死枚举值）。
    }

    // 注：ConnectionState / BigEndian / ClientFrame 不再在此声明 —— QuicHarness.csproj 直接链接了
    // 引擎真实源码（Connection.cs），这三样由它提供。旧版在 Stubs 里复制 ClientFrame = 两份代码漂移
    // （帧头布局一改，试验台仍按旧格式跑通），已删除副本。

    /// <summary>可靠通道契约（引擎里在 Transport.cs；试验台只用 QuicConnection 实现它）。</summary>
    public interface ITransportConnection
    {
        TransportKind Kind { get; }
        ConnectionState State { get; }
        event Action Connected;
        event Action Disconnected;
        void ConnectAsync(string addr);
        void Disconnect();
        void Send(byte[] data, int offset, int count);
        void Tick();
        bool TryTakePacket(out byte[] data);
    }

    /// <summary>自带不可靠通道的契约（引擎里在 Transport.cs）。</summary>
    public interface IUnreliableTransport
    {
        bool SupportsUnreliable { get; }
        void SendUnreliable(byte[] data, int offset, int count);
    }

    /// <summary>引擎日志接口的最小替身（引擎里是 Logger；这里直接写 stdout）。</summary>
    public interface IHarnessLogger
    {
        void Info(string tag, string msg);
        void Warn(string tag, string msg);
        void Error(string tag, string msg, Exception ex = null);
    }

    internal sealed class ConsoleHarnessLogger : IHarnessLogger
    {
        // ⚠️ 每条都 Flush：进程被原生断言打死时，**stdout 缓冲会整体丢失**，
        // 于是"最后一条日志"看起来像是崩溃点，其实是假象（本轮真踩过：
        // 因为没 flush，我一度以为崩在 ConnectionShutdown 之后，实际崩点更靠后）。
        private static void Line(string level, string tag, string msg)
        {
            Console.WriteLine($"[{level}][{tag}] {msg}");
            Console.Out.Flush();
        }

        public void Info(string tag, string msg) => Line("INFO ", tag, msg);
        public void Warn(string tag, string msg) => Line("WARN ", tag, msg);
        public void Error(string tag, string msg, Exception ex = null) =>
            Line("ERROR", tag, $"{msg}{(ex == null ? "" : " :: " + ex.GetType().Name + ": " + ex.Message)}");
    }

    /// <summary>
    /// 引擎门面的最小替身：QUIC 源码里只用到了 <c>Game.Logger</c>。
    /// 注意引擎里 `Game.Logger` **永不为 null**（默认 ConsoleLogger），这里保持同一语义。
    /// </summary>
    public static class Game
    {
        public static IHarnessLogger Logger { get; } = new ConsoleHarnessLogger();
    }

}
