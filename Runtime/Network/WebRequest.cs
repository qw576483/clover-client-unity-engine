using System;
using System.Collections.Generic;
using UnityEngine.Networking;

namespace CloverEngine
{
    /// <summary>
    /// HTTP 请求管理实现，负责发起请求并统一回收资源。
    /// 契约类型（IWebRequest / WebResponse）定义于 Core 程序集 Contracts.cs；
    /// 模块随 CloverNet.Init 一并挂接（CloverNet.InitHttp 保留供特例手动调用）。
    /// </summary>
    internal class WebRequestManager : IWebRequest
    {
        /// <summary>
        /// 请求总超时（秒）。UnityWebRequest 默认 0 = **永不超时**：账号服连接卡住时
        /// 登录 Task 会永久停在"登录中"（c-bug WebRequest.cs:36）。
        /// 取与引擎请求-响应超时（GameConfig.CallTimeoutSeconds 默认 10s）同量级的 10s——
        /// 账号服登录等短请求卡住 10s 已应失败，让业务走重试/报错而不是无限等待。
        /// </summary>
        private const int RequestTimeoutSeconds = 10;

        private readonly List<UnityWebRequest> _activeRequests = new();

        /// <summary>是否已释放（Dispose 后完成回调不再回调业务，只回收请求资源）。</summary>
        private bool _disposed;

        /// <summary>
        /// 发送 GET 请求
        /// </summary>
        /// <param name="url">请求地址</param>
        /// <param name="callback">响应回调，在主线程调用</param>
        public void Get(string url, Action<WebResponse> callback)
        {
            var req = UnityWebRequest.Get(url);
            req.timeout = RequestTimeoutSeconds;
            Send(req, callback);
        }

        /// <summary>
        /// 发送 POST 请求，正文以 JSON 格式提交
        /// </summary>
        /// <param name="url">请求地址</param>
        /// <param name="body">请求正文（JSON 字符串）</param>
        /// <param name="callback">响应回调，在主线程调用</param>
        public void Post(string url, string body, Action<WebResponse> callback)
        {
            var req = new UnityWebRequest(url, "POST");
            try
            {
                req.timeout = RequestTimeoutSeconds;
                var bodyRaw = System.Text.Encoding.UTF8.GetBytes(body);
                req.uploadHandler = new UploadHandlerRaw(bodyRaw);
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/json");
            }
            catch (Exception e)
            {
                // 构造期抛异常（如 body 为 null 时 GetBytes 抛 ArgumentNullException）：
                // 请求尚未入 _activeRequests，必须就地回收防泄漏；原因留日志后原样上抛。
                req.Dispose();
                Game.Logger?.Error("WebRequest", $"POST {url} body encode failed: {e.Message}", e);
                throw;
            }
            Send(req, callback);
        }

        /// <summary>
        /// 释放所有进行中的请求。
        /// 未完成的 UnityWebRequest **不能直接 Dispose**（Unity 要求完成后才可释放）：
        /// 先 Abort 促使其尽快收尾，真正的 Dispose 交给各自的完成回调
        /// （Dispose 之后到达的回调不再回调业务，只回收资源）。
        /// </summary>
        public void Dispose()
        {
            _disposed = true;
            foreach (var req in _activeRequests)
            {
                if (req == null || req.isDone) continue;
                req.Abort();
            }
        }

        /// <summary>
        /// 发送请求并在完成时构造统一响应对象，经主线程派发器回调业务侧
        /// </summary>
        /// <param name="req">Unity 网络请求对象</param>
        /// <param name="callback">业务响应回调</param>
        private void Send(UnityWebRequest req, Action<WebResponse> callback)
        {
            UnityWebRequestAsyncOperation op;
            try
            {
                op = req.SendWebRequest();
            }
            catch (Exception e)
            {
                // SendWebRequest 抛出（URL 非法等）：请求未被跟踪，就地回收防泄漏，日志后原样上抛。
                req.Dispose();
                Game.Logger?.Error("WebRequest", $"send failed: {req.url}: {e.Message}", e);
                throw;
            }

            _activeRequests.Add(req);
            op.completed += _ =>
            {
                _activeRequests.Remove(req);

                if (_disposed)
                {
                    // 模块已释放（Shutdown 途中）：不再回调业务，只回收请求资源
                    // （Dispose() 已对该请求 Abort，完成后的 Dispose 是合法操作）。
                    req.Dispose();
                    return;
                }

                var resp = new WebResponse
                {
                    StatusCode = (int)req.responseCode,
                    Text = req.downloadHandler?.text,
                    Data = req.downloadHandler?.data,
                    IsSuccess = req.result == UnityWebRequest.Result.Success
                };

                req.Dispose();

                // 无派发器（编辑器测试环境）时内联执行，而不是把回调整个丢掉；
                // 引擎已停（Shutdown 之后）时同理——派发队列不会再被排空，投递等于丢弃。
                var cb = callback;
                PostOrRun(() => cb?.Invoke(resp));
            };
        }

        /// <summary>
        /// 主线程切换：有派发器且引擎在运行 → 投递；否则内联同步执行
        /// （与 NetworkManager.PostOrRun 同款兜底，避免"派发器为 null 时回调被丢"）。
        /// </summary>
        /// <param name="action">待执行动作</param>
        private static void PostOrRun(Action action)
        {
            var dispatcher = Game.Dispatcher;
            if (dispatcher == null)
            {
                action();
                return;
            }

            if (!Game.IsRunning)
            {
                Game.Logger?.Warn("WebRequest", "engine not running — callback executed inline");
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    Game.Logger?.Error("WebRequest", $"inline callback error: {e.Message}", e);
                }
                return;
            }

            dispatcher.Post(action);
        }
    }
}
