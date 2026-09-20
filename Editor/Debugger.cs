using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace CloverEngine.Editor
{
    /// <summary>
    /// 调试器窗口的基础接口，定义显示、隐藏和切换可见性的能力。
    /// </summary>
    public interface IDebugger
    {
        /// <summary>
        /// 显示调试器窗口。
        /// </summary>
        void Show();

        /// <summary>
        /// 隐藏调试器窗口。
        /// </summary>
        void Hide();

        /// <summary>
        /// 切换调试器窗口的可见状态。
        /// </summary>
        void Toggle();
    }

    /// <summary>
    /// 运行时调试器窗口，显示 FPS、状态机当前状态、网络状态与资源状态。
    /// </summary>
    public class DebuggerWindow : IDebugger
    {
        private bool _visible;
        private float _updateInterval = 0.5f;
        private float _timer;
        private float _fps;
        private int _frameCount;

        /// <summary>
        /// 显示调试器窗口。
        /// </summary>
        public void Show() => _visible = true;

        /// <summary>
        /// 隐藏调试器窗口。
        /// </summary>
        public void Hide() => _visible = false;

        /// <summary>
        /// 切换调试器窗口的可见状态。
        /// </summary>
        public void Toggle() => _visible = !_visible;

        /// <summary>
        /// 绘制调试器界面，包含 FPS、FSM 状态、网络状态（连接/UDP 绑定/会话恢复/待配对 Call/错误回包/RTT）及资源状态。仅在窗口可见时绘制。
        /// </summary>
        public void OnGUI()
        {
            if (!_visible) return;

            GUILayout.BeginArea(new Rect(10, 10, 420, 620));
            GUILayout.BeginVertical("box");

            GUILayout.Label($"FPS: {_fps:F1}");
            GUILayout.Label($"State: {Game.Fsm?.Current}");
            DrawNetworkSection();
            DrawCapabilitySection();
            DrawResourceSection();

            if (GUILayout.Button("Close"))
                Hide();

            GUILayout.EndVertical();
            GUILayout.EndArea();
        }

        /// <summary>
        /// 绘制网络调试区块：连接状态、UDP 绑定状态、会话恢复状态、待配对 Call 数、
        /// 最近错误回包内容与 RTT 统计（最近/平均/最大）。
        /// 通过 NetworkManager 的 internal 访问器读取（Network 程序集已对 Editor 程序集开放 InternalsVisibleTo）。
        /// </summary>
        private static void DrawNetworkSection()
        {
            var net = Game.Net;
            GUILayout.Label($"Network: {(net?.IsConnected == true ? "Connected" : "Disconnected")}");
            if (net == null) return;

            // NetworkManager 提供的扩展调试信息（internal 可见）。
            var nm = net as NetworkManager;

            // UDP 通道绑定状态：登录后网关经 TCP 下发绑定令牌、客户端完成 UDP 绑定后为 true。
            GUILayout.Label($"UDP Bound: {(net.IsUdpBound ? "Yes" : "No")}");
            // 会话恢复标记：断线重连流程中已发送 ResumeSession 且未收到回包时为 true。
            GUILayout.Label($"Resuming: {(nm != null && nm.SessionResuming ? "Yes" : "No")}");
            // 目标服务器地址与 UDP 通道地址。
            GUILayout.Label($"Server: {nm?.ServerAddr ?? "N/A"}  UDP: {nm?.UdpEndpoint ?? "N/A"}");
            // 当前线路与线路计划（主链 → 降级链）：看清「现在跑在哪条线上、还有哪些可退」。
            GUILayout.Label($"Line: {nm?.ActiveLine ?? "N/A"}");
            GUILayout.Label($"Plan: {nm?.LinePlan ?? "N/A"}");
            // 弱网模拟状态（GM `sim` 控制）。
            GUILayout.Label($"Sim: {nm?.Simulator?.Describe() ?? "N/A"}");
            // 待配对 Call 数：已发出但尚未收到回包（也未超时）的请求量。
            GUILayout.Label($"Pending Calls: {nm?.PendingCallCount ?? 0}");

            // 最近一次错误回包（服务端 EErrorReply）及其对应的请求 ID。
            var lastErr = nm?.LastErrorReply;
            if (lastErr != null)
                // code 是机器可读错误码（401 = 未认证等，见 ErrCode）：排查看 err 文本之外还要看它。
                GUILayout.Label($"Last Error: req={nm.LastErrorRequestID} code={lastErr.code} err={lastErr.err}");

            // RTT 统计（毫秒）：最近一次采样 / 指数滑动平均 / 历史最大值。
            GUILayout.Label(
                $"RTT: last={nm?.RttLastMs ?? 0:F1}ms avg={nm?.RttAvgMs ?? 0:F1}ms max={nm?.RttMaxMs ?? 0:F1}ms");
        }

        /// <summary>
        /// 绘制能力区块：传输线路能力表（<see cref="TransportCapabilities.Describe"/>）与局域网寻服说明
        /// （<c>CloverLan.Describe</c>）。
        ///
        /// <para>
        /// **为什么接进面板**：这两段描述过去只写进日志（且 <c>TransportCapabilities.Describe</c> 曾无调用点），
        /// 于是「为什么这条线路没被用 / 这台机器能不能走 QUIC / 本机能不能寻服」只能翻日志才看得到。
        /// 面板上直接给出来，能省掉一轮"猜测 → 翻日志"。
        /// </para>
        /// </summary>
        private static void DrawCapabilitySection()
        {
            GUILayout.Label("— Capability —");
            // 多行文本逐行 Label（GUILayout.Label 不换行，整段塞进去只会显示一行）。
            foreach (var line in TransportCapabilities.Describe().Split('\n'))
                GUILayout.Label(line);
            GUILayout.Label($"Lan: {CloverLan.Describe()}");
        }

        /// <summary>
        /// 绘制资源调试区块：后端、版本、缓存占用与水位、在途加载、已开包数、热更阶段与下载进度。
        /// 热更相关的状态（「现在跑的是哪个版本」「缓存被压到多少」「下载到哪了」）不展示出来时，
        /// 线上问题只能靠猜，因此这里宁可多打几行。
        /// 通过 ResourceManager 的 internal 访问器读取（Resource 程序集已对 Editor 开放 InternalsVisibleTo）。
        /// </summary>
        private static void DrawResourceSection()
        {
            var res = Game.Res;
            GUILayout.Label("— Resource —");
            if (res == null)
            {
                GUILayout.Label("Resource: not initialized");
                return;
            }

            var rm = res as ResourceManager;
            GUILayout.Label($"Backend: {rm?.BackendName ?? "N/A"}  Version: {res.Version}");
            GUILayout.Label(
                $"Cache: {res.CachedBytes / 1024}KB / " +
                $"{(res.CacheWatermark > 0 ? (res.CacheWatermark / 1024) + "KB" : "unlimited")}" +
                $"  entries={rm?.CachedCount ?? 0} inflight={rm?.InflightCount ?? 0}");
            GUILayout.Label($"Open Bundles: {rm?.OpenBundleCount ?? 0}");
            GUILayout.Label($"Update: {res.UpdateState}");

            // 只在真正下载时打进度与速度，避免空闲时刷一行无意义的 0%
            var p = rm?.DownloadProgress;
            if (p != null)
                GUILayout.Label($"Download: {p.Progress:P0} ({p.CompletedFiles}/{p.TotalFiles}) {p.BytesPerSecond / 1024f:F0}KB/s {p.CurrentFile}");
        }

        /// <summary>
        /// 每帧更新 FPS 计算，按固定间隔刷新帧率统计值。
        /// </summary>
        public void Update()
        {
            _frameCount++;
            _timer += Time.unscaledDeltaTime;
            if (_timer >= _updateInterval)
            {
                _fps = _frameCount / _timer;
                _frameCount = 0;
                _timer = 0f;
            }
        }
    }

    /// <summary>
    /// GM 调试命令系统，支持注册和执行字符串命令，用于运行时调试操作。
    /// </summary>
    public static class GMCommand
    {
        /// <summary>
        /// 命令表：比较规则**大小写不敏感**（Ordinal 序，不受 Turkish-I 之类区域设置影响）。
        /// Register 存键与 Execute 查表必须用同一套规则 —— 旧实现 Register 原样存、Execute 用
        /// <c>ToLower()</c> 查（还受当前区域设置影响），任何含大写字母的注册名都查不到。
        /// </summary>
        private static readonly Dictionary<string, Action<string[]>> _commands =
            new(StringComparer.OrdinalIgnoreCase);

        static GMCommand()
        {
            Register("help", _ =>
            {
                Debug.Log("[GM] Available commands: help, reconnect, disconnect, clear, sim");
                Debug.Log("[GM] clear：清空对象池（销毁全部池化对象，未初始化时给明确提示）");
                Debug.Log("[GM] sim <延迟ms> [抖动ms] [丢包率0-1] —— 开启弱网模拟；sim off 关闭；sim 查看状态");
            });

            Register("reconnect", _ =>
            {
                // 网络未初始化时给出明确提示，避免空引用。
                if (Game.Net == null)
                {
                    Debug.LogWarning("[GM] Network not initialized (call Game.Launch first)");
                    return;
                }
                Debug.Log("[GM] Reconnecting...");
                // 走 NetworkManager 的重连入口：清除退避计数后立即重连（会话恢复可用时自动恢复）。
                Game.Net.Reconnect();
            });

            Register("disconnect", _ =>
            {
                Game.Net?.Disconnect();
                Debug.Log("[GM] Disconnected");
            });

            Register("clear", _ =>
            {
                // 对象池清理：销毁池内全部游戏对象并清空池表（与 Game.Shutdown 的 Pool.ClearAll 同一条路径）。
                var pool = Game.Pool;
                if (pool == null)
                {
                    Debug.LogWarning("[GM] clear: 对象池未初始化（CloverPresentation 未挂载 —— 先调 Game.Launch）");
                    return;
                }
                pool.ClearAll();
                Debug.Log("[GM] clear: 对象池已清空（全部池化对象已销毁，池表已复位）");
            });

            // 弱网模拟：延迟 / 抖动 / 丢包。装饰在传输层之外，因此对本轮选中的任何线路（TCP/WS）都生效。
            Register("sim", args =>
            {
                var nm = Game.Net as NetworkManager;
                if (nm?.Simulator == null)
                {
                    Debug.LogWarning("[GM] Network not initialized (call Game.Launch / CloverNet.Init first)");
                    return;
                }

                var sim = nm.Simulator;
                if (args.Length == 0)
                {
                    Debug.Log($"[GM] sim: {sim.Describe()}");
                    Debug.Log("[GM] 用法：sim <延迟ms> [抖动ms] [丢包率0-1]；sim off 关闭");
                    return;
                }

                if (string.Equals(args[0], "off", StringComparison.OrdinalIgnoreCase))
                {
                    sim.Disable();
                    Debug.Log($"[GM] sim off: {sim.Describe()}");
                    return;
                }

                if (string.Equals(args[0], "reset", StringComparison.OrdinalIgnoreCase))
                {
                    sim.ResetCounters();
                    Debug.Log($"[GM] sim counters reset: {sim.Describe()}");
                    return;
                }

                var latency = int.TryParse(args[0], out var l) ? l : 200;
                var jitter = args.Length > 1 && int.TryParse(args[1], out var j) ? j : 0;
                var loss = args.Length > 2 && float.TryParse(args[2], out var r) ? r : 0f;
                sim.Configure(latency, jitter, loss);
                Debug.Log($"[GM] sim on: {sim.Describe()}");
            });
        }

        /// <summary>
        /// 注册一条 GM 调试命令，若已存在同名命令则覆盖。
        /// </summary>
        /// <param name="cmd">命令名称，执行时匹配的第一段字符串（不区分大小写）。</param>
        /// <param name="handler">命令处理回调，接收命令参数数组。</param>
        public static void Register(string cmd, Action<string[]> handler)
        {
            _commands[cmd] = handler;
        }

        /// <summary>
        /// 解析并执行一条 GM 命令字符串。空输入将被忽略，未注册的命令会输出警告日志。
        /// </summary>
        /// <param name="input">完整的命令字符串，以空格分隔，第一段为命令名，后续为参数。</param>
        public static void Execute(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return;
            var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            // 不做 ToLower：比较规则由 _commands 的 OrdinalIgnoreCase 比较器统一负责
            //（ToLower 还受当前区域设置影响，土耳其语环境下 "I" 会转出非 ASCII 字符）。
            var cmd = parts[0];

            if (_commands.TryGetValue(cmd, out var handler))
            {
                var args = new string[parts.Length - 1];
                Array.Copy(parts, 1, args, 0, args.Length);
                try
                {
                    handler.Invoke(args);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[GM] Command error: {ex.Message}");
                }
            }
            else
            {
                Debug.LogWarning($"[GM] Unknown command: {cmd}");
            }
        }
    }

    /// <summary>
    /// GM 控制台窗口：提供一个输入框，把命令字符串交给 <see cref="GMCommand.Execute"/>。
    ///
    /// **存在的必要性**：<c>GMCommand</c> 原先只有 <c>Register</c> / <c>Execute</c> 两个公开方法，
    /// 却**没有任何调用方**——命令注册了却无处执行（典型的「未接线」）。本窗口就是它的入口。
    ///
    /// 用法：进入 Play 模式后从菜单 <c>Clover/GM 控制台</c> 打开，输入 <c>help</c> 查看内置命令。
    /// </summary>
    public class GMConsoleWindow : EditorWindow
    {
        private const string InputControlName = "clover-gm-input";
        private const int MaxHistory = 50;

        private string _input = string.Empty;
        private readonly List<string> _history = new List<string>();
        private Vector2 _scroll;

        [MenuItem("Clover/GM 控制台")]
        private static void Open()
        {
            var w = GetWindow<GMConsoleWindow>("GM 控制台");
            w.minSize = new Vector2(420f, 240f);
            w.Show();
        }

        private void OnGUI()
        {
            // 明确提示引擎状态：网络类命令（reconnect / disconnect）会读 Game.Net。
            EditorGUILayout.HelpBox(
                Game.IsRunning
                    ? "引擎已启动，可直接执行命令。"
                    : "引擎未启动（非 Play 模式或未调 Game.Launch）：网络类命令会提示未初始化。",
                Game.IsRunning ? MessageType.Info : MessageType.Warning);

            using (new EditorGUILayout.HorizontalScope())
            {
                GUI.SetNextControlName(InputControlName);
                _input = EditorGUILayout.TextField(_input);
                if (GUILayout.Button("执行", GUILayout.Width(56f)))
                    ExecuteInput();
            }

            // 回车执行（需先聚焦输入框，避免误吞其它控件的回车）。
            var e = Event.current;
            if (e.type == EventType.KeyDown &&
                (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter) &&
                GUI.GetNameOfFocusedControl() == InputControlName)
            {
                ExecuteInput();
                e.Use();
            }

            EditorGUILayout.LabelField("命令输出见 Console（GM 命令内部用 Debug.Log 输出）", EditorStyles.miniLabel);

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            for (var i = _history.Count - 1; i >= 0; i--)
                EditorGUILayout.LabelField("> " + _history[i]);
            EditorGUILayout.EndScrollView();
        }

        private void ExecuteInput()
        {
            var cmd = _input == null ? string.Empty : _input.Trim();
            if (cmd.Length == 0)
                return;

            _history.Add(cmd);
            if (_history.Count > MaxHistory)
                _history.RemoveAt(0);
            _input = string.Empty;

            GMCommand.Execute(cmd);
            Repaint();
        }
    }

    /// <summary>
    /// 运行时调试面板的挂载器：把 <see cref="DebuggerWindow"/>（FPS / FSM / 网络状态 / RTT）
    /// 装到一个常驻 GameObject 上，并驱动它的 <c>Update</c> 与 <c>OnGUI</c>。
    ///
    /// **存在的必要性**：<c>DebuggerWindow</c> 原先也没有任何挂载点——它的 OnGUI/Update 永远不会被调用。
    /// 由于本程序集仅在 Editor 平台编译（asmdef includePlatforms=Editor），此挂载器只在 Editor 的
    /// Play 模式下可用——这正是调试面板需要的场景（打包后的正式包不带它）。
    ///
    /// 用法：Play 模式下从菜单 <c>Clover/调试面板</c> 开关。
    /// </summary>
    [AddComponentMenu("")] // 不出现在 Add Component 菜单里，只能由下面的菜单项创建
    public class DebuggerOverlay : MonoBehaviour
    {
        private const string RootName = "[Clover Debugger]";

        private readonly DebuggerWindow _window = new DebuggerWindow();

        [MenuItem("Clover/调试面板")]
        private static void Toggle()
        {
            var existing = FindAnyObjectByType<DebuggerOverlay>();
            if (existing != null)
            {
                DestroyImmediate(existing.gameObject);
                return;
            }
            var go = new GameObject(RootName);
            go.hideFlags = HideFlags.DontSave;
            go.AddComponent<DebuggerOverlay>();
        }

        private void Awake()
        {
            _window.Show();
        }

        private void Update()
        {
            _window.Update();
        }

        private void OnGUI()
        {
            _window.OnGUI();
        }
    }
}
