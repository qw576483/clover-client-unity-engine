using UnityEngine;

namespace CloverEngine
{
    /// <summary>
    /// 局域网寻服引导类：初始化并挂接 <see cref="Game.LanBrowser"/>（先例：<c>CloverNet</c>）。
    ///
    /// <para>
    /// 用法顺序（硬要求，见 结构规则.md §五 N13）：
    /// <code>
    /// Game.Launch(config);                       // 1. 起引擎
    /// Game.LanBrowser.Scan();                    // 2. 找主机（OnHostFound / OnScanFinished 出结果）
    /// // 3. 玩家选一台
    /// CloverNet.Init(host.Address, host.UdpAddress);
    /// </code>
    /// <b>寻服必须早于 <c>CloverNet.Init</c></b>：先寻服、选主机，再连；
    /// 引擎不提供运行中切服（<c>CloverNet.Init</c> 幂等、只生效一次）。
    /// </para>
    ///
    /// <para>
    /// 通常<b>不需要业务手动调用</b> <see cref="Init"/>：<see cref="Game.Launch"/> 完成后会自动挂接
    /// （与 <c>CloverPresentation</c> 同构）。手动接管时把它当普通幂等方法调即可。
    /// </para>
    /// </summary>
    public static class CloverLan
    {
        private const string LogTag = "Lan";

        private const string HookKey = "CloverLan";

        /// <summary>
        /// 静态构造里登记启动钩子（契约要求的位置）。
        /// 它保证「业务在 Launch 之前碰过 <see cref="CloverLan"/>（例如读 <see cref="Describe"/>）」时也能自动挂接。
        /// </summary>
        static CloverLan()
        {
            RegisterHook();
        }

        /// <summary>
        /// RuntimeInitializeOnLoadMethod 兜底：<b>静态构造只在类被触碰时才执行</b> ——
        /// 业务若从不引用 <see cref="CloverLan"/>（只调 <c>CloverNet.Init</c>），静态构造根本不会跑，
        /// 「自动挂接」就成了空话。这里在本引擎的固定入口再登记一次
        /// （<see cref="Game.RegisterLaunchHook"/> 按 key 覆盖，重复登记幂等），
        /// 与 <c>CloverPresentation.RegisterAutoMount</c> 同一写法。
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void RegisterAutoMount()
        {
            RegisterHook();
        }

        private static void RegisterHook()
        {
            Game.RegisterLaunchHook(HookKey, () =>
            {
                // 只在真运行时挂接：EditMode 测试里 Game.Launch 也会被调用，
                // 那里不需要（也不该）凭空多出一个扫描器实例。
                if (Application.isPlaying)
                    Init();
            });
        }

        /// <summary>
        /// 初始化并挂接 <see cref="Game.LanBrowser"/>（幂等）。需在 <see cref="Game.Launch"/> 之后调用。
        /// <b>它必须早于 <c>CloverNet.Init</c></b>：先寻服、选主机，再连。
        /// </summary>
        public static void Init()
        {
            if (!Game.IsRunning)
            {
                // 走统一日志门面（此时 Game.Logger 指向 ConsoleLogger，永不 null）：诊断格式与引擎一致，
                // ⛔ 不用裸 Debug.LogError（不进日志文件、与引擎日志格式两张皮）。
                Game.Logger?.Error(LogTag, "引擎尚未启动，请先调用 Game.Launch(config) 再调用 CloverLan.Init()");
                return;
            }

            // 幂等判据用门面（不是静态 bool）：Game.Shutdown 会把 LanBrowser 置空，
            // 静态 bool 会让「Shutdown → Launch」之后再也挂不上。
            if (Game.LanBrowser != null)
            {
                Game.Logger?.Warn(LogTag, "CloverLan.Init 重复调用，忽略（Game.LanBrowser 已挂接）");
                return;
            }

            Game.AttachLanBrowser(new LanBrowser());
            Game.Logger?.Info(LogTag, $"lan module initialized：{Describe()}");
        }

        /// <summary>
        /// 创建局域网**应答端**（"我开的主机要被别人扫到"）——<see cref="ILanBrowser"/> 的对侧。
        ///
        /// <para>
        /// <b>不经 <see cref="Game"/> 门面</b>、<b>不要求 <see cref="Game.Launch"/></b>：
        /// 「我这台是不是主机」是业务决策（进哪个模式 / 开哪张图），不是引擎生命周期的一部分；
        /// 也不用等 <see cref="Init"/> —— 应答与扫描互不影响，早开晚开都行。
        /// </para>
        ///
        /// <code>
        /// var responder = CloverLan.CreateResponder();
        /// var self = LanHostInfo.Create("", 8002, 8003, null, "我的服", 1, 10, "1.0.0", null); // Host 留空 = 自动取本机 IPv4
        /// if (!responder.Start(self))
        ///     Game.Logger.Warn("Lan", $"局域网应答端启动失败：{responder.LastError}");
        /// // ... 主人退出时
        /// responder.Dispose();
        /// </code>
        ///
        /// <para>
        /// ⚠️ 返回值由调用方持有并负责 <see cref="ILanResponder.Dispose"/>（引擎不代管 —— 代管就变成
        /// 门面生命周期的一部分了）。失败不抛异常，原因见 <see cref="ILanResponder.LastError"/>。
        /// </para>
        /// </summary>
        public static ILanResponder CreateResponder()
        {
            return new LanResponder();
        }

        /// <summary>
        /// 平台支持说明（日志 / 调试面板用）。<b>不需 Launch 即可调用</b>（只读平台能力表）。
        /// </summary>
        public static string Describe()
        {
            if (LanCapabilities.IsSupported)
            {
                return $"局域网寻服可用：UDP 查询/应答（默认端口 {LanProtocol.DefaultPort}，" +
                       $"结果上限 {LanProtocol.MaxHosts} 台）";
            }

            return $"局域网寻服不可用：{LanCapabilities.ReasonFor(Application.platform)}";
        }
    }
}
