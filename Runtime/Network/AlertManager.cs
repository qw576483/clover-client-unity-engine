using System;

namespace CloverEngine
{
    /// <summary>
    /// 公告/通知推送管理器：消费 EMsg.PushAlert，将服务端公告分发给业务层订阅者。
    ///
    /// 与服务端对齐：
    ///   - 服务端 g.Alert(c, EAlertNotify{title,content,level,style,ttl}) → EMsg.PushAlert (4002)
    ///   - 客户端 AlertManager 注册推送处理器 → 解析 EAlertNotify → 触发 OnAlert 事件
    ///
    /// 回调线程：**主线程**（Net.Tick → Router.Dispatch 内同步分发），可直接操作 UI。
    ///
    /// 使用方式：
    /// <code>
    ///   // 订阅公告
    ///   Game.Alert.OnAlert += HandleAlert;
    ///
    ///   // 取消订阅
    ///   Game.Alert.OnAlert -= HandleAlert;
    /// </code>
    /// </summary>
    internal class AlertManager : IAlert
    {
        private IRouter _router;
        private MsgHandler _attachedHandler;

        /// <summary>
        /// 公告推送回调：参数为公告数据（title, content, level, style, ttl）。
        /// 主线程回调，可直接操作 UI。
        /// </summary>
        public event Action<EAlertNotify> OnAlert;

        public void Attach(IRouter router)
        {
            Detach();
            _router = router;
            _attachedHandler = HandleAlertPush;
            _router?.OnMsg(EMsg.PushAlert, _attachedHandler);
        }

        public void Detach()
        {
            if (_router == null) return;
            if (_attachedHandler != null)
                _router.OffMsg(EMsg.PushAlert, _attachedHandler);
            _router = null;
            _attachedHandler = null;
        }

        private void HandleAlertPush(NetCtx ctx)
        {
            EAlertNotify alert;
            try
            {
                alert = ctx.Bind<EAlertNotify>();
            }
            catch (Exception ex)
            {
                // JsonUtility 对非法 JSON 是**抛异常**而不是返回 null：不包 try/catch 时下面的
                // null 分支永远不可达，异常穿给 Router 只能报通用 Handler error（归因差）。
                Game.Logger?.Warn("Alert", $"alert payload parse failed: {ex.Message}");
                return;
            }
            if (alert == null)
            {
                Game.Logger?.Warn("Alert", "alert payload parse failed");
                return;
            }

            Game.Logger?.Info("Alert", $"alert received: level={alert.level ?? "info"} title={alert.title ?? ""}");

            try { OnAlert?.Invoke(alert); }
            catch (Exception ex)
            {
                Game.Logger?.Error("Alert", $"OnAlert callback error: {ex.Message}", ex);
            }

            Game.Event?.Emit(CloverEvents.Net.Alert, alert);
        }
    }
}
