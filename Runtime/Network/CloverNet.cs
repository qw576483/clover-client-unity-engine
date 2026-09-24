namespace CloverEngine
{
    /// <summary>
    /// 网络模块引导类：负责构造并挂接网络 / 世界同步 / HTTP 子系统，
    /// 维持「Network → Core 单向依赖」的 asmdef 依赖规则（Core 不反向构造模块实现）。
    /// 典型启动顺序：
    ///   Game.Launch(config) → CloverNet.Init(addr, udpAddr) → 业务登录 → Net.SetupSession(...)
    /// </summary>
    public static class CloverNet
    {
        /// <summary>
        /// 初始化网络模块并连接服务器，同时挂接世界同步模块（推送处理器随绑定自动注册）。
        /// 配置 udpAddr 后，登录成功收到 EMsg.UDPBindGrant 令牌时将自动建立常驻裸 UDP 通道。
        /// 需在 Game.Launch 之后调用；重复调用无效。
        /// </summary>
        /// <param name="addr">TCP 服务器地址，格式为 "host:port"；传 null / 空则回退 <see cref="GameConfig.ServerAddr"/></param>
        /// <param name="udpAddr">裸 UDP 共享端点，格式为 "host:port"，传 null 不启用不可靠通道</param>
        /// <param name="plan">
        /// 显式线路计划；非 null 时优先于 <paramref name="addr"/> / <paramref name="udpAddr"/>。
        /// 留空时按 <see cref="GameConfig.ServerAddr"/>（原生家族）+ <see cref="GameConfig.WsAddr"/>
        /// （Web 家族）自动拼计划，再由 <c>TransportPlanner</c> **按平台家族裁剪**：
        /// 原生端保留 QUIC → TCP + 裸 UDP（无 msquic 二进制时自动裁掉 QUIC）；WebGL 当前无可用线路（WebTransport 未实现、WebSocket 需 jslib 桥接）。两者不会同时生效。
        /// </param>
        public static void Init(string addr = null, string udpAddr = null, TransportOptions plan = null)
        {
            if (!Game.IsRunning)
            {
                // 走 Game.Logger：它**永不为 null**（未 Launch 时指向 ConsoleLogger，同样写 Unity Console
                // 且格式与文件日志一致），本诊断也能进日志文件 —— 裸 Debug.LogError 不能。
                Game.Logger?.Error("Network", "CloverNet.Init: game not launched, call Game.Launch first");
                return;
            }
            if (Game.Net != null)
            {
                Game.Logger?.Warn("Network", "network module already initialized");
                return;
            }

            // addr 缺省时回退 GameConfig.ServerAddr —— 否则业务会在 Launch 里设了 ServerAddr
            // 却以为已经连上（那个字段本身不触发连接）。
            // 显式传入线路计划时不做该校验：地址由计划自身携带，允许 addr 为空。
            if (plan == null)
            {
                if (string.IsNullOrEmpty(addr) && Game.Config != null)
                {
                    addr = Game.Config.ServerAddr;
                }

                // 只配了 WsAddr（WebGL 必填项）而 ServerAddr 为空的工程必须放行到线路规划：
                // 旧逻辑见 addr 为空就直接 return，WsAddr 配了也永远不会被线路规划消费
                // （c-bug CloverNet.cs:51）——「连不上」的真实原因反而看不到。
                if (string.IsNullOrEmpty(addr) && string.IsNullOrEmpty(Game.Config?.WsAddr))
                {
                    Game.Logger?.Error("Network",
                        "CloverNet.Init: addr 为空，且 GameConfig 未配置 ServerAddr / WsAddr，无法建立连接");
                    return;
                }
            }

            // 构造顺序：先建 NetworkManager（同时实现 INetwork 与 IRouter），
            // 再挂各协同模块（连接建立后推送即可订阅），最后发起连接。
            var net = new NetworkManager();
            var sync = new WorldSyncManager();
            var cloverScene = new CloverSceneManager();
            var frameRoom = new FrameRoomManager();
            var alert = new AlertManager();
            var schemaRegistry = new SchemaRegistryManager();

            // 业务侧唯一消息入口：Game.OnMsg / Game.OffMsg（Game.Net 不再暴露 OnMsg）
            Game.AttachRouter(net);
            Game.AttachNetwork(net);

            Game.AttachWorldSync(sync);
            sync.Attach(net);
            Game.AttachCloverScene(cloverScene);
            cloverScene.Attach(net, net);   // 参数一 INetwork（会话/线路），参数二 IRouter（消息注册）
            Game.AttachFrameRoom(frameRoom);
            frameRoom.Attach(net, net);     // 同上；此时 Configure 可能尚未调用，Attach 会跳过推送注册，业务 Configure 时自动补注册
            Game.AttachAlert(alert);
            alert.Attach(net);
            Game.AttachSchema(schemaRegistry);   // 纯 Schema 声明表，不参与数据订阅（订阅走 Game.Sync）

            // HTTP 子系统随网络模块一并挂接：账号服登录是必经依赖，HTTP 属引擎基础设施，
            // 不是"选装件"。业务不必再单独调 CloverNet.InitHttp——漏调不会在启动时报错，
            // 只会等玩家点登录时才炸（"HTTP 模块未初始化"），把配置遗漏推迟成线上故障。
            if (Game.Http == null)
            {
                Game.AttachHttp(new WebRequestManager());
            }

            if (plan != null)
                net.Connect(plan);
            else
                net.Connect(addr, udpAddr);

            Game.Logger?.Info("Network", $"network module initialized: lines={net.LinePlan}");
        }

        /// <summary>
        /// 初始化 HTTP 请求模块。**通常不需要业务调用**——<see cref="Init"/> 已随网络模块一并挂接。
        /// 保留本方法用于「只想用 HTTP、暂不连网关」的场景（如纯登录页先换 token）；
        /// 已挂接过则幂等返回。需在 Game.Launch 之后调用。
        /// </summary>
        public static void InitHttp()
        {
            if (!Game.IsRunning)
            {
                // 同上：走 Game.Logger（永不为 null，未启动时也写 Console，且格式与文件日志一致）。
                Game.Logger?.Error("Network", "CloverNet.InitHttp: game not launched, call Game.Launch first");
                return;
            }
            if (Game.Http != null)
            {
                Game.Logger?.Warn("Network", "http module already initialized");
                return;
            }

            Game.AttachHttp(new WebRequestManager());
            Game.Logger?.Info("Network", "http module initialized");
        }
    }
}
