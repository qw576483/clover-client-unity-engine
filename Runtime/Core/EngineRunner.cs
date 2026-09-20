using UnityEngine;

namespace CloverEngine
{
    /// <summary>
    /// 引擎驱动器，以 Unity MonoBehaviour 形式驱动引擎主循环与生命周期回调。
    /// </summary>
    [DefaultExecutionOrder(-10000)]
    internal class EngineRunner : MonoBehaviour
    {
        /// <summary>
        /// 当前驱动器单例，用于判断驱动器是否已创建。
        /// </summary>
        private static EngineRunner _instance;

        /// <summary>
        /// 确保引擎驱动器存在，不存在时创建常驻 GameObject 并挂载，避免随场景切换被销毁。
        /// 仅在 Play 模式下生效：EditMode（如单元测试）中调用会直接跳过，防止在编辑器环境误建常驻对象。
        /// </summary>
        public static void Ensure()
        {
            if (_instance != null) return;
            // 非运行时（EditMode 测试/编辑器脚本）不允许创建运行期 GameObject，也不允许 DontDestroyOnLoad。
            if (!Application.isPlaying) return;
            var go = new GameObject("[CloverEngine]");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<EngineRunner>();
        }

        /// <summary>
        /// 每帧回调，以帧间隔时长驱动引擎主循环。
        /// <para>
        /// <b>关于 <see cref="Time.maximumDeltaTime"/> 钳制（默认 0.333s）</b>：<c>dt</c> 取自
        /// <see cref="Time.deltaTime"/>，长时间后台 / 断点挂起返回后的那一帧，dt 被钳到 ≤0.333s ——
        /// 换言之"挂起的墙钟时间被吞掉"，Timer/Net/Sync 只看到一帧步长。
        /// 这是**刻意保留**的 Unity 主循环语义（dt 是"本帧步长"而不是"墙钟差"）：
        /// 若按真实墙钟补 dt，跨过长时间挂起后定时器会瞬发一批回调、同步/插值会大跳。
        /// 需要"画面冻结但仍要计时"的场合（暂停菜单 / 结算屏）用 <c>AfterUnscaled</c>/<c>EveryUnscaled</c>
        /// —— 它们按 <see cref="Time.unscaledDeltaTime"/> 推进、不吃 <c>timeScale=0</c>，
        /// 但同样以帧为节拍，极端低帧率下不等于墙钟。
        /// </para>
        /// </summary>
        private void Update()
        {
            Game.Tick(Time.deltaTime);
        }

        /// <summary>
        /// 应用挂起或恢复回调，向事件总线广播对应的暂停/恢复事件。
        /// </summary>
        /// <param name="paused">是否处于挂起状态。</param>
        private void OnApplicationPause(bool paused)
        {
            if (paused)
                Game.Event?.Emit(CloverEvents.App.Pause);
            else
                Game.Event?.Emit(CloverEvents.App.Resume);
        }

        /// <summary>
        /// 应用退出回调，执行引擎关闭流程（与 <see cref="OnDestroy"/> 双向幂等：
        /// 谁先到谁生效，另一端只是空操作）。
        /// </summary>
        private void OnApplicationQuit()
        {
            Game.Shutdown();
        }

        /// <summary>
        /// 宿主对象被销毁时的兜底关闭路径（非退出场景：对象被其它代码 Destroy、非常规卸载等）。
        /// <para>
        /// 旧实现只有 <see cref="OnApplicationQuit"/>：宿主被其它途径销毁时既不 Shutdown、
        /// 也不清静态 <c>_instance</c> —— 引擎继续挂在旧实例上且驱动器已消失（无人驱动 Tick），
        /// 之后再 Ensure 又会造出第二个驱动器，前后状态错乱。
        /// </para>
        /// </summary>
        private void OnDestroy()
        {
            // 只清自己那份引用（防御：极端情况下静态字段可能已指向新实例）
            if (_instance == this)
                _instance = null;
            // Game.Shutdown 幂等（未运行时直接返回）：与 OnApplicationQuit 重复到达也安全
            // （编辑器停止播放、宿主被 Destroy 等路径都会走到这里）。
            Game.Shutdown();
        }
    }
}
