using System;
using System.Collections;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CloverEngine.Tests
{
    /// <summary>
    /// 局域网寻服的**真实 UDP**用例（PlayMode）：本机起应答器 → 浏览器真发包 → 真收包 → 真收尾。
    ///
    /// <para>
    /// 为什么值得单独跑一遍真 socket：进程内的纯逻辑用例（<c>Tests/Editor/LanProtocolTests.cs</c>）
    /// 覆盖不到「收包线程 + 窗口自判收尾 + 主线程收敛」这三件事，而选服界面卡在"扫描中"、
    /// 事件在后台线程触发导致 UI 崩，恰恰都出在这一段。
    /// </para>
    ///
    /// <para>
    /// 应答器用 <see cref="UdpClient"/> 绑 <c>127.0.0.1:&lt;临时端口&gt;</c>，收到查询后回一条合法应答；
    /// 端口冲突（绑不上）→ <c>Assert.Ignore</c>（环境问题不伪装成失败）。
    /// </para>
    ///
    /// <para>
    /// 三条纪律（见 skill <c>reference/unity-cli.md</c> §4.1）：等待助手**只求值一次谓词**；
    /// 等待用**真实时间**；收尾把 socket / 线程 / 扫描全部收干净
    /// （本用例不建 GameObject，因此没有 <c>DestroyImmediate</c> 的对象，等价动作是关 socket + 停线程 + Stop 扫描）。
    /// </para>
    /// </summary>
    public class LanBrowserLoopbackTests
    {
        private const string ResponderName = "回环测试服";
        private const int ResponderGatewayPort = 8002;
        private const int ResponderUdpPort = 8003;
        private const int ScanWindowMs = 800;

        private LanBrowser _browser;
        private UdpClient _responder;
        private Thread _responderThread;
        private volatile bool _responderStop;
        private volatile bool _scanFinished;
        private volatile int _foundCount;
        private volatile int _queryCount;
        private volatile LanHostInfo _firstHost;

        /// <summary>
        /// 无论成败都必须走到这里：断言失败会抛异常，方法体末尾的清理就没了 ——
        /// 于是收包线程会活过本用例（这与 QUIC 用例里"活连接活到域卸载"是同一类事故）。
        ///
        /// <para>
        /// <b>必须用同步的 <c>[TearDown]</c>，不能改成 <c>[UnityTearDown]</c>（协程）</b>：
        /// 实测协程版 teardown 会被推迟到**下一个用例已经开始之后**才执行，那时字段
        /// <see cref="_browser"/> 已被下一个用例换成它自己的实例 ⇒ 上一个用例的清理会把
        /// 下一个用例正在跑的扫描 <c>Stop()</c> 掉：日志诡异地落在下一个用例的报告块里，
        /// 断言读到半路状态（"刚 Scan 完就断言 Idle，实际是 Scanning"）。
        /// </para>
        /// </summary>
        [TearDown]
        public void Cleanup()
        {
            _responderStop = true;

            try
            {
                _responder?.Close();
            }
            catch (Exception ex)
            {
                Game.Logger?.Warn("LanTest", $"关闭应答器失败：{ex.Message}");
            }

            if (_responderThread != null)
            {
                // 关 socket 会让阻塞中的 Receive 立刻返回，正常几百毫秒内就退出。
                if (!_responderThread.Join(1000))
                {
                    Game.Logger?.Warn("LanTest", "应答器线程未在 1s 内退出（测试结束不因此阻塞）");
                }

                _responderThread = null;
            }

            _browser?.Stop();
            _browser = null;
        }

        /// <summary>
        /// 每个用例**开始时**复位探针字段。
        ///
        /// <para>
        /// 不能省：<c>[UnityTest]</c> 的生命周期与普通 NUnit 用例不同，夹具实例可能被复用，
        /// 字段残留会让 <see cref="WaitFor"/> 一进来就吃到上一个用例的 <c>true</c> 而**直接跳过等待**，
        /// 表现为"Scan 完立刻断言 Idle，但那时还在 Scanning"的假失败。
        /// </para>
        /// </summary>
        private void ResetProbe()
        {
            _scanFinished = false;
            _foundCount = 0;
            _queryCount = 0;
            _firstHost = null;
            _responderStop = false;
        }

        /// <summary>
        /// 正常路径：一台主机应答 → OnHostFound 一次、字段全对、窗口到点自动收尾、收尾后回到 Idle。
        /// </summary>
        [UnityTest]
        [Timeout(30000)]
        public IEnumerator Scan_LoopbackResponder_DiscoversHostAndFinishes()
        {
            if (!LanCapabilities.IsSupported)
            {
                Assert.Ignore($"本平台不支持局域网寻服：{LanCapabilities.ReasonFor(Application.platform)}");
            }

            ResetProbe();

            var port = StartResponder();

            var browser = new LanBrowser();
            _browser = browser;
            browser.OnHostFound += host =>
            {
                _foundCount++;
                if (_firstHost == null)
                    _firstHost = host;
            };
            browser.OnScanFinished += () => _scanFinished = true;

            var startedAt = Time.realtimeSinceStartup;
            browser.Scan(new LanScanOptions
            {
                Port = port,
                IncludeBroadcast = false,
                IncludeSubnetBroadcast = false,
                IncludeLoopback = true,
                DurationMs = ScanWindowMs,
            });

            Assert.AreEqual(LanBrowserState.Scanning, browser.State, "Scan 后应立即进入扫描态");
            Assert.AreEqual(1, browser.Targets.Count, "关掉广播/子网广播后应只剩回环目标 127.0.0.1");

            yield return WaitFor(() => _scanFinished, 5f, $"OnScanFinished（窗口 {ScanWindowMs}ms，应答器端口 {port}）");
            var elapsed = Time.realtimeSinceStartup - startedAt;

            Assert.Less(elapsed, 3f,
                $"窗口 {ScanWindowMs}ms 应在 1s 出头收尾，实际 {elapsed:F2}s —— 收尾卡住会让选服界面一直转圈");
            Assert.AreEqual(LanBrowserState.Idle, browser.State, "收尾后必须回到 Idle");

            Assert.AreEqual(1, _foundCount,
                $"应恰好发现 1 台主机（应答器端口 {port}，本轮收到查询 {_queryCount} 个）");
            Assert.IsNotNull(_firstHost, "OnHostFound 必须带出主机信息");

            Assert.AreEqual($"127.0.0.1:{ResponderGatewayPort}", _firstHost.Address, "Address 必须可直接喂 CloverNet.Init");
            Assert.AreEqual("127.0.0.1", _firstHost.Host);
            Assert.AreEqual(ResponderGatewayPort, _firstHost.GatewayPort);
            Assert.AreEqual(ResponderUdpPort, _firstHost.UdpPort);
            Assert.AreEqual($"127.0.0.1:{ResponderUdpPort}", _firstHost.UdpAddress);
            Assert.AreEqual("http://127.0.0.1:8051", _firstHost.AuthAddr);
            Assert.AreEqual(ResponderName, _firstHost.Name);
            Assert.AreEqual(3, _firstHost.Players);
            Assert.AreEqual(8, _firstHost.MaxPlayers);
            Assert.AreEqual("1.0.0", _firstHost.Version);
            Assert.AreEqual("loopback", _firstHost.Extra);
            Assert.AreEqual(1, browser.Hosts.Count, "同一台主机只应有一条结果");
        }

        /// <summary>
        /// 空手而归也必须收尾：窗口内没有任何应答（目标端口无人监听）→ 0 台主机，但
        /// <see cref="ILanBrowser.OnScanFinished"/> 一定触发、State 一定回 Idle。
        /// </summary>
        [UnityTest]
        [Timeout(30000)]
        public IEnumerator Scan_NoResponder_StillFinishes()
        {
            if (!LanCapabilities.IsSupported)
            {
                Assert.Ignore($"本平台不支持局域网寻服：{LanCapabilities.ReasonFor(Application.platform)}");
            }

            ResetProbe();

            var silentPort = ReservePort();

            var browser = new LanBrowser();
            _browser = browser;
            browser.OnHostFound += _ => _foundCount++;
            browser.OnScanFinished += () => _scanFinished = true;

            browser.Scan(new LanScanOptions
            {
                Port = silentPort,
                IncludeBroadcast = false,
                IncludeSubnetBroadcast = false,
                IncludeLoopback = true,
                DurationMs = ScanWindowMs,
            });

            yield return WaitFor(() => _scanFinished, 5f, $"OnScanFinished（无应答，端口 {silentPort}）");

            Assert.AreEqual(LanBrowserState.Idle, browser.State);
            Assert.AreEqual(0, _foundCount, "无人应答时不该凭空产出主机");
            Assert.AreEqual(0, browser.Hosts.Count);

            // 收尾后再 Stop 是幂等空操作（调用方在收尾回调里顺手 Stop 是常见写法）
            browser.Stop();
            Assert.AreEqual(0, _foundCount);
        }

        // ====================================================================
        //  辅助
        // ====================================================================

        /// <summary>起本机应答器并返回它监听的端口；绑不上按环境问题忽略（不伪装成失败）。</summary>
        private int StartResponder()
        {
            try
            {
                _responder = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            }
            catch (Exception ex)
            {
                Assert.Ignore($"本机应答器绑定失败（端口冲突/权限）：{ex.Message}");
            }

            // 让 Receive 能周期性醒来检查停止标志（否则 TearDown 里要等满一个默认无限超时）。
            _responder.Client.ReceiveTimeout = 200;

            var port = ((IPEndPoint)_responder.Client.LocalEndPoint).Port;
            _responderThread = new Thread(ResponderLoop) { IsBackground = true, Name = "LanTestResponder" };
            _responderThread.Start();
            return port;
        }

        /// <summary>占一个临时端口再立刻放掉：用来构造「这个端口上没人监听」（UDP 无连接，不需要真占住）。</summary>
        private static int ReservePort()
        {
            using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            return ((IPEndPoint)probe.Client.LocalEndPoint).Port;
        }

        /// <summary>应答器：收到查询 → 回显 nonce 回一条合法应答（挂后台线程，测试主线程不受影响）。</summary>
        private void ResponderLoop()
        {
            var from = new IPEndPoint(IPAddress.Any, 0);
            var watch = Stopwatch.StartNew();

            while (!_responderStop && watch.ElapsedMilliseconds < 15000)
            {
                try
                {
                    var query = _responder.Receive(ref from);
                    _queryCount++;

                    var text = Encoding.UTF8.GetString(query);
                    if (!text.StartsWith(LanProtocol.QueryMagic, StringComparison.Ordinal))
                    {
                        Game.Logger?.Warn("LanTest",
                            $"收到非查询报文，忽略：{text.Substring(0, Math.Min(32, text.Length))}");
                        continue;
                    }

                    var nonce = text.Substring(LanProtocol.QueryMagic.Length + 1);
                    var reply = Encoding.UTF8.GetBytes(BuildReply(nonce));
                    _responder.Send(reply, reply.Length, from);
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut
                                                || ex.SocketErrorCode == SocketError.ConnectionReset
                                                || ex.SocketErrorCode == SocketError.Interrupted)
                {
                    // 超时/ICMP 回执：正常现象，回循环头检查停止标志。
                }
                catch (ObjectDisposedException)
                {
                    break;   // TearDown 关了应答器
                }
                catch (Exception ex)
                {
                    // 绝不能把异常丢出线程；记一条再退出（否则应答器静默失效，用例会以"没发现主机"报错）。
                    Game.Logger?.Warn("LanTest", $"应答器异常，停止应答：{ex.Message}");
                    break;
                }
            }
        }

        /// <summary>构造一条合法应答（字段覆盖 LanHostInfo 的全部可选/必填项）。</summary>
        private static string BuildReply(string nonce)
        {
            return $"{LanProtocol.ReplyMagic}|{{" +
                   $"\"nonce\":\"{nonce}\"," +
                   $"\"name\":\"{ResponderName}\"," +
                   $"\"gateway\":\"127.0.0.1:{ResponderGatewayPort}\"," +
                   $"\"udp\":\"127.0.0.1:{ResponderUdpPort}\"," +
                   "\"auth\":\"http://127.0.0.1:8051\"," +
                   "\"players\":3,\"max\":8,\"version\":\"1.0.0\",\"extra\":\"loopback\"}";
        }

        /// <summary>
        /// 等待助手：**谓词只求值一次**并拿它断言。
        /// 循环里求值成功后又在 Assert 里求值一次，是"数据早收到了却报超时"的经典成因
        /// （见 skill <c>reference/unity-cli.md</c> §4.1）。
        /// </summary>
        private static IEnumerator WaitFor(Func<bool> condition, float timeoutSeconds, string what)
        {
            var deadline = Time.realtimeSinceStartup + timeoutSeconds;
            var ok = condition();

            while (!ok && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
                ok = condition();
            }

            Assert.IsTrue(ok, $"等待超时（{timeoutSeconds}s）：{what}");
        }
    }
}
