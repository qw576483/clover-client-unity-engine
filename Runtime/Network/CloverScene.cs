using System;
using System.Collections.Generic;

namespace CloverEngine
{
    /// <summary>
    /// 服务端场景投影实现：消费 EMsg.PushSceneInfo，维护「玩家当前所在的服务端场景」。
    ///
    /// 数据流向：服务端 mmo.Scene（权威） → ESceneInfoNotify → 本类 → 业务 / UI。
    /// 本类不含任何权威逻辑，只做服务端下行数据的本地投影，符合「客户端不跑权威逻辑」约束。
    /// 命名与边界约定见 clover-doc/client/concepts/concept-naming。
    /// </summary>
    internal class CloverSceneManager : ICloverScene
    {
        private INetwork _net;
        private IRouter _router;
        private MsgHandler _attachedHandler;
        private readonly List<Action<ICloverScene>> _handlers = new();

        // 场景 id 用 ulong：与服务端 uint64（push.go 的 SceneID）/ IMapData.SceneId 对齐。
        private readonly Dictionary<ulong, string> _mappings = new();

        public bool IsValid { get; private set; }

        public ulong SceneID { get; private set; }

        public uint InstanceID { get; private set; }

        public string Name { get; private set; }

        /// <summary>该服务端场景对应的 Unity 关卡名，由业务/配表设置。</summary>
        public string UnityScene { get; set; }

        /// <summary>场景变更后是否自动加载 Unity 关卡（默认 false，由业务决定时机）。</summary>
        public bool AutoLoadUnityScene { get; set; }

        public void Attach(INetwork net, IRouter router)
        {
            Detach();
            _net = net;
            _router = router;
            _attachedHandler = HandleSceneInfo;
            _router?.OnMsg(EMsg.PushSceneInfo, _attachedHandler);
        }

        public void Detach()
        {
            if (_router == null) return;
            // 只移除自己注册的处理器，不影响其他模块
            if (_attachedHandler != null)
                _router.OffMsg(EMsg.PushSceneInfo, _attachedHandler);
            _router = null;
            _attachedHandler = null;
        }

        // 【本节整体未接线】OnChanged / OffChanged / RegisterMappings / ResolveUnityScene 当前全仓
        // （Runtime/Editor/Tests/Samples~/Tools~）无调用点，属**公开契约**（场景名映射与场景变更通知，
        // 供业务 / 上层流程编排使用）——尚未接线，业务可直接使用，不要按「无人使用」删除。
        public void OnChanged(Action<ICloverScene> handler)
        {
            if (handler != null) _handlers.Add(handler);
        }

        public void OffChanged(Action<ICloverScene> handler)
        {
            if (handler != null) _handlers.Remove(handler);
        }

        public void Clear()
        {
            IsValid = false;
            SceneID = 0;
            InstanceID = 0;
            Name = null;
        }

        public void RegisterMapping(ulong sceneId, string unitySceneName)
        {
            if (sceneId == 0 || string.IsNullOrEmpty(unitySceneName))
            {
                Game.Logger?.Warn("CloverScene", $"RegisterMapping ignored: sceneId={sceneId} unityScene={unitySceneName}");
                return;
            }
            _mappings[sceneId] = unitySceneName;
        }

        public void RegisterMappings(IDictionary<ulong, string> mappings)
        {
            if (mappings == null) return;
            foreach (var kv in mappings)
                RegisterMapping(kv.Key, kv.Value);
        }

        public string ResolveUnityScene(ulong sceneId)
        {
            return _mappings.TryGetValue(sceneId, out var name) ? name : null;
        }

        /// <summary>
        /// 场景标识推送处理器（EMsg.PushSceneInfo）：
        /// 更新场景归属 → 回填会话分线编号 → 通知订阅者 → 按需加载 Unity 关卡。
        /// </summary>
        /// <param name="ctx">消息上下文</param>
        private void HandleSceneInfo(NetCtx ctx)
        {
            ESceneInfoNotify info;
            try
            {
                info = ctx.Bind<ESceneInfoNotify>();
            }
            catch (Exception ex)
            {
                // JsonUtility 对非法 JSON 是**抛异常**而不是返回 null：不包 try/catch 时下面的
                // null 分支永远不可达，异常穿给 Router 只能报通用 Handler error（归因差）。
                Game.Logger?.Warn("CloverScene", $"scene info payload parse failed: {ex.Message}");
                return;
            }
            if (info == null)
            {
                Game.Logger?.Warn("CloverScene", "scene info payload parse failed");
                return;
            }

            IsValid = true;
            SceneID = info.scene_id;
            InstanceID = info.instance_id;
            Name = info.name;

            // 分线编号与服务端 Instance 同义，回填会话字段使其有真实数据来源。
            // Session 本身就是 SessionInfo（没有中间的 Info 层）——原来写的 _net.Session.Info 编译不过。
            if (_net?.Session != null)
                _net.Session.Line = (int)info.instance_id;

            // 自动根据映射表填充 UnityScene（映射表优先于外部手动设置）
            if (_mappings.TryGetValue(SceneID, out var mappedUnityScene))
                UnityScene = mappedUnityScene;

            Game.Logger?.Info("CloverScene", $"scene info received: id={SceneID} instance={InstanceID} name={Name} unityScene={UnityScene ?? "(none)"}");

            // 快照遍历：回调内 OffChanged 会修改列表，直接 foreach 会抛
            // InvalidOperationException 并中断本批后续回调（实现按"回调内可退订"处理）。
            foreach (var handler in _handlers.ToArray())
            {
                try { handler?.Invoke(this); }
                catch (Exception ex) { Game.Logger?.Error("CloverScene", $"scene handler error: {ex.Message}", ex); }
            }

            // 这里把 this 作为事件载荷是**有意为之，且不违反 G8**（判据见 结构规则 §5.3）：
            // CloverSceneManager 是 internal，跨程序集的订阅方只能按公开契约 ICloverScene 使用它，
            // 拿不到任何额外可变状态（也就谈不上"让渡所有权"）；若改成拷贝，反而会让
            // 订阅方与"当前场景"脱钩（契约要的就是"变更后的那个场景对象"）。
            Game.Event?.Emit(CloverEvents.Net.SceneChanged, this);

            if (AutoLoadUnityScene && !string.IsNullOrEmpty(UnityScene))
                Game.Scene?.Load(UnityScene);
        }
    }
}
