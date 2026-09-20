using UnityEngine;

namespace CloverEngine
{
    /// <summary>
    /// 输入模块引导类：构造并挂接输入管理器，同时把 UI 输入基础设施（EventSystem）一次性建好。
    ///
    /// 与 CloverNet / CloverData / CloverRes 同构，维持「Presentation → Core 单向依赖」规则
    /// （Core 不反向构造模块实现）。
    ///
    /// 典型启动顺序：
    ///   Game.Launch(config) → CloverInput.Init() → CloverNet.Init(addr) → 构建业务 UI
    ///
    /// 为什么输入要由引擎接管：
    ///   1) 后端无关：Active Input Handling 三种取值（Old / New / Both）下都能用同一套 API，
    ///      业务代码不再直接碰 UnityEngine.Input，从根上避免「一次改设置 = 键鼠全灭」；
    ///   2) 永不抛异常：后端不可用时只给一次性、可操作的日志，不会让 UnityEngine.Input 的
    ///      InvalidOperationException 在 Update 里打断整帧逻辑（那会导致键盘也不响应）；
    ///   3) 基础设施归引擎：EventSystem + 正确的 InputModule 由引擎保证唯一且匹配后端，
    ///      消灭「两个 InputModule 并存 → 按钮点不了」。
    /// </summary>
    public static class CloverInput
    {
        /// <summary>
        /// 初始化输入模块并挂接到 Game.Input。需在 Game.Launch 之后调用；重复调用无效。
        /// </summary>
        public static void Init()
        {
            if (!Game.IsRunning)
            {
                // 统一走 Game.Logger（未 Launch 时指向 ConsoleLogger，永不为 null）：注释自称此时可走 Logger，
                // 恰说明本可落盘 / 过滤 —— 裸 Debug.LogError 不进日志文件，线上查不到 Init 调用顺序错误。
                Game.Logger?.Error("Input", "引擎尚未启动，请先调用 Game.Launch(config) 再调用 CloverInput.Init()", null);
                return;
            }

            // 与 CloverPresentation.Init 同款守卫：EditMode / 编辑器脚本调用会创建常驻 EventSystem 并
            // DontDestroyOnLoad，污染编辑器场景 —— 非运行时直接跳过。
            if (!Application.isPlaying)
            {
                Game.Logger?.Warn("Input", "非运行环境下不初始化输入模块（EventSystem 需要运行期常驻 GameObject）");
                return;
            }

            if (Game.Input != null) return;

            var manager = new InputManager();
            Game.AttachInput(manager);
            manager.EnsureEventSystem();

            Game.Logger?.Info("Input", $"输入模块已挂载: backend={manager.BackendName}, available={manager.Available}");
        }

        /// <summary>
        /// 确保 EventSystem 与当前后端匹配（业务侧改动 UI 根节点后可再调一次，幂等）。
        /// </summary>
        public static void EnsureEventSystem()
        {
            Game.Input?.EnsureEventSystem();
        }

        /// <summary>输入模块是否已就绪（已挂载且后端可用）。</summary>
        public static bool Ready => Game.Input != null && Game.Input.Available;
    }
}
