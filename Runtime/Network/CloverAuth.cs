using System;
using System.Threading.Tasks;
using UnityEngine;

namespace CloverEngine
{
    /// <summary>
    /// 账号服（auth 服）客户端：把「注册 / 登录」从游戏长连接搬到 HTTP。
    ///
    /// 登录体系外置后客户端流程变成两步：
    ///   1) HTTPS 调账号服换取 JWT（本类负责）
    ///   2) 连游戏网关发 EMsg.Login{token}（由业务调用，见下方示例）
    ///
    /// 典型用法：
    /// <code>
    /// // 启动时配置账号服地址（必填：未配置属配置错误，调用本类方法会抛异常）。
    /// // 地址应来自客户端配置文件（config.json 的 server.auth_addr），不要写死在代码里。
    /// CloverAuth.AuthAddr = Cfg.Server.auth_addr;
    ///
    /// // 登录：先换 token，再交给游戏服
    /// string token = await CloverAuth.LoginAsync(account, password);
    /// await Game.Net.Call&lt;ELoginReply&gt;(EMsg.Login, new ELoginRequest { token = token });
    /// </code>
    ///
    /// <see cref="AuthAddr"/> 为<strong>必填配置</strong>：未配置时 <see cref="Enabled"/> 为 false，
    /// 调用 <see cref="LoginAsync"/> / <see cref="SignupAsync"/> 会抛 <see cref="InvalidOperationException"/>。
    /// 登录只此一条路径——游戏服只认 token，不存在「账号密码直接发游戏服」的老流程。
    ///
    /// 登录走 HTTP 而非长连接的原因：登录是低频请求-响应操作，且账号服天然是
    /// HTTP 服务（需与渠道 SDK 服务端、运营后台交互），与游戏消息不该共用一条通道。
    /// </summary>
    public static class CloverAuth
    {
        /// <summary>
        /// 账号服 HTTP 地址（如 "http://127.0.0.1:8051"）。<strong>必填配置</strong>：
        /// 为空即未配置（属配置错误），此时调用 <see cref="LoginAsync"/> / <see cref="SignupAsync"/>
        /// 会抛 <see cref="InvalidOperationException"/>。
        /// </summary>
        public static string AuthAddr { get; set; }

        /// <summary>是否已配置账号服。</summary>
        public static bool Enabled => !string.IsNullOrEmpty(AuthAddr);

        /// <summary>登录账号服，返回 JWT。失败抛出异常（消息为服务端 err 文案）。</summary>
        public static Task<string> LoginAsync(string account, string password)
            => PostAsync("/auth/login", account, password);

        /// <summary>
        /// 注册。账号服注册成功即签发 token（注册即登录），返回值可直接用于 EMsg.Login。
        /// 失败抛出异常（账户已存在等）。
        /// </summary>
        public static Task<string> SignupAsync(string account, string password)
            => PostAsync("/auth/signup", account, password);

        /// <summary>
        /// 渠道登录（微信 / QQ / Steam / 自建账号中心），返回 JWT，用法与 <see cref="LoginAsync"/> 一致。
        /// 失败抛出异常（票据无效 / 渠道未接入等）。
        ///
        /// 需要服务端已注册渠道校验器：未注册时账号服返回 HTTP 501，异常消息为
        /// 「账号服未接入该渠道…」——据此可区分「服务端没接渠道」与「票据真的错了(401)」。
        /// </summary>
        /// <param name="channel">渠道标识（如 "wechat"），须与服务端注册的一致。</param>
        /// <param name="ticket">渠道票据（如微信登录 code），由渠道 SDK 在客户端取得。</param>
        public static Task<string> ChannelLoginAsync(string channel, string ticket)
            => PostBodyAsync("/auth/login", JsonUtility.ToJson(new ChannelReq { channel = channel, ticket = ticket }));

        // PostAsync 统一的「HTTP → 解析 → 取 token」流程。
        //
        // 用 TaskCompletionSource 而不是 async void：调用方需要 await 到结果才能继续
        // （拿 token 换游戏会话），异常也必须能沿 await 链路抛出。
        private static Task<string> PostAsync(string path, string account, string password)
            => PostBodyAsync(path, JsonUtility.ToJson(new CredReq { account = account, password = password }));

        // PostBodyAsync 三个入口（注册 / 密码登录 / 渠道登录）共用的发送与解析逻辑。
        private static Task<string> PostBodyAsync(string path, string body)
        {
            var tcs = new TaskCompletionSource<string>();

            if (!Enabled)
            {
                tcs.SetException(new InvalidOperationException(
                    "未配置账号服地址（CloverAuth.AuthAddr 为空）"));
                return tcs.Task;
            }
            if (Game.Http == null)
            {
                tcs.SetException(new InvalidOperationException(
                    "HTTP 模块未初始化（需先调用 CloverNet.Init）"));
                return tcs.Task;
            }

            Game.Http.Post(AuthAddr + path, body, resp =>
            {
                if (resp == null)
                {
                    Game.Logger?.Warn("Auth", "账号服无响应（回调响应为空）");
                    tcs.SetException(new Exception("账号服无响应"));
                    return;
                }

                AuthResult result = null;
                if (!string.IsNullOrEmpty(resp.Text))
                {
                    try
                    {
                        result = JsonUtility.FromJson<AuthResult>(resp.Text);
                    }
                    catch
                    {
                        // 落到下面的"解析失败"分支统一归因，避免把 JsonUtility 的
                        // 内部异常信息暴露给业务（它对排查没帮助）。
                    }
                }

                if (result != null)
                {
                    if (!result.success || string.IsNullOrEmpty(result.token))
                    {
                        // 服务端业务失败（含渠道 501 / 未认证 401 等带 JSON err 的失败响应）：
                        // 以服务端 err 文案为准，并留一条日志。
                        var msg = string.IsNullOrEmpty(result.err)
                            ? $"账号服返回失败（HTTP {resp.StatusCode}）"
                            : result.err;
                        Game.Logger?.Warn("Auth", $"账号服请求被拒（HTTP {resp.StatusCode}）: {msg}");
                        tcs.SetException(new Exception(msg));
                        return;
                    }

                    // body 声明成功仍要判传输层结果（原实现从不检查 resp.IsSuccess / HTTP 2xx）：
                    // "body 成功 + HTTP 层失败"属异常不一致，以 HTTP 层为准。
                    if (!resp.IsSuccess)
                    {
                        Game.Logger?.Warn("Auth",
                            $"账号服响应不一致：HTTP {resp.StatusCode}（IsSuccess=false）但 body 声明成功，按失败处理");
                        tcs.SetException(new Exception($"账号服请求失败（HTTP {resp.StatusCode}）"));
                        return;
                    }

                    // token 临期提醒（exp 为 Unix 秒；0 = 账号服未返回）。引擎不做自动刷新，
                    // 但临期/过期必须有一条日志可查——否则只能等 401 / 恢复失败才发现 token 失效。
                    WarnIfTokenExpiring(result.exp);

                    Game.Logger?.Info("Auth", $"账号服登录成功 owner={result.owner}");
                    tcs.SetResult(result.token);
                    return;
                }

                // body 为空 / 不可解析：按传输层结果归因。StatusCode=0 是连接失败或超时，
                // 不是"响应无法解析"；该分支原先既误报归因又不留日志，两条一并修掉。
                if (!resp.IsSuccess)
                {
                    Game.Logger?.Warn("Auth",
                        $"账号服请求失败：HTTP {resp.StatusCode}（IsSuccess=false, bodyLen={resp.Text?.Length ?? 0}）");
                    tcs.SetException(new Exception(resp.StatusCode > 0
                        ? $"账号服请求失败（HTTP {resp.StatusCode}）"
                        : "账号服请求失败：连接失败或超时（未收到响应）"));
                    return;
                }

                Game.Logger?.Warn("Auth",
                    $"账号服响应无法解析：HTTP {resp.StatusCode}, bodyLen={resp.Text?.Length ?? 0}");
                tcs.SetException(new Exception($"账号服响应无法解析（HTTP {resp.StatusCode}）: {resp.Text}"));
            });
            return tcs.Task;
        }

        /// <summary>token 临期告警窗口（秒）：剩余时间低于该值时打 Warn。仅告警，引擎不做自动刷新。</summary>
        private const long TokenExpiryWarnSeconds = 300;

        /// <summary>
        /// token 临期检查（<see cref="AuthResult.exp"/> 为 Unix 秒；0 = 账号服未返回，跳过）。
        /// 引擎不提供刷新机制，但该字段绝不能零读取、零日志——临期/已过期必须可查，
        /// 而不是等上行被 401 拒绝才发现。
        /// </summary>
        /// <param name="exp">token 过期时间戳（Unix 秒）</param>
        private static void WarnIfTokenExpiring(long exp)
        {
            if (exp <= 0) return;
            var remain = exp - DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (remain > TokenExpiryWarnSeconds) return;

            Game.Logger?.Warn("Auth", remain > 0
                ? $"账号服 token 将在 {remain}s 后过期（exp={exp}）：引擎无自动刷新，请尽快重新登录"
                : $"账号服 token 已过期 {-remain}s（exp={exp}）：引擎无自动刷新，请重新登录");
        }

        /// <summary>注册 / 账号密码登录请求体（字段名与服务端 authCredReq 对齐）。</summary>
        [Serializable]
        private class CredReq
        {
            public string account;
            public string password;
        }

        /// <summary>渠道登录请求体（与账号密码登录共用 /auth/login，服务端按有无 channel 分派）。</summary>
        [Serializable]
        private class ChannelReq
        {
            public string channel;
            public string ticket;
        }

        /// <summary>账号服响应体（字段名与服务端 authTokenResp 对齐）。</summary>
        [Serializable]
        private class AuthResult
        {
            /// <summary>已认证对象标识（账号服侧通常是账号名）。</summary>
            public string owner;
            /// <summary>JWT，交给游戏服 EMsg.Login{token} 使用。</summary>
            public string token;
            /// <summary>是否成功。</summary>
            public bool success;
            /// <summary>失败原因（人类可读，仅供展示）。</summary>
            public string err;
            /// <summary>token 过期时间（Unix 秒）；0 表示账号服未返回。</summary>
            public long exp;
        }
    }
}
