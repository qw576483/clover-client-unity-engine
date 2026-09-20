using UnityEngine;

namespace CloverEngine
{
    /// <summary>
    /// 资源模块引导类：构造并挂接资源管理子系统。
    /// 维持「Resource → Core 单向依赖」的 asmdef 规则（Core 不反向构造模块实现）。
    /// </summary>
    /// <remarks>
    /// 两种接入方式：
    /// <code>
    /// // ① 只用 Unity 内置 Resources（默认，零配置）
    /// CloverRes.Init("Clover");
    ///
    /// // ② 启用资源热更（AssetBundle + 断点续传）
    /// CloverRes.Init(new ResourceModuleConfig
    /// {
    ///     ManifestUrl = "https://cdn.example.com/rs/manifest.json",
    ///     CacheWatermark = 256L * 1024 * 1024,
    /// });
    /// </code>
    /// 方式②在**首次安装**（本地还没有任何热更内容）时会先用首包 Resources 把游戏跑起来，
    /// 等 <c>Game.Res.DownloadUpdate</c> 完成后自动切到 AssetBundle；
    /// 之后**版本之间的升级**在下次启动生效（下载完提示玩家重启即可）。
    /// </remarks>
    public static class CloverRes
    {
        /// <summary>
        /// 初始化资源模块（纯 Resources 模式），设置 Resources 根前缀。
        /// 需在 <c>Game.Launch</c> 之后调用；已初始化时只告警并早退。
        /// </summary>
        /// <param name="root">Resources 根前缀（如 <c>"Clover"</c>）；空串表示以 Resources 根为根。</param>
        public static void Init(string root)
        {
            Init(new ResourceModuleConfig { Root = root });
        }

        /// <summary>
        /// 按配置初始化资源模块（可启用热更）。需在 <c>Game.Launch</c> 之后调用。
        /// </summary>
        /// <param name="config">模块配置；为 null 时按默认（纯 Resources）处理。</param>
        public static void Init(ResourceModuleConfig config)
        {
            if (!Game.IsRunning)
            {
                // 这里统一走 Game.Logger（未启动时它指向 ConsoleLogger，**永不为 null**，输出到 Unity Console）：
                // 与引擎其它诊断同一条通道，避免出现只能靠 Unity 原生日志才看得见的"野生"错误。
                Game.Logger?.Error("Resource", "game not launched, call Game.Launch first");
                return;
            }
            if (Game.Res != null)
            {
                Game.Logger?.Warn("Resource", "resource module already initialized");
                return;
            }

            var manager = ResourceManager.Create(config, out var note);
            Game.AttachResource(manager);

            if (!string.IsNullOrEmpty(note))
                Game.Logger?.Warn("Resource", note);

            Game.Logger?.Info("Resource",
                $"resource module initialized: backend={manager.BackendName}, version={manager.Version}, " +
                $"content={manager.ContentDir}, watermark={manager.CacheWatermark / 1024}KB");
        }
    }
}
