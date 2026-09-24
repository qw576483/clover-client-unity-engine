using UnityEngine;

namespace CloverEngine
{
    /// <summary>
    /// 引擎**宿主行为开关**（配合 <see cref="EngineRunner"/> / <c>Game.ConfigureHost</c> 使用）。
    /// <para>
    /// <b>每个字段的默认值 = 引擎现状（"不干预"）</b>：不调用 <c>Game.ConfigureHost</c> 时，
    /// 宿主的行为与本次下沉之前**逐字一致**（一个字节都不变）。要哪一项就显式打开哪一项。
    /// </para>
    /// <para>
    /// 为什么这三件事属引擎：它们是**每个新项目都要在自建 Bootstrap 里重写一遍**的宿主级动作
    /// （后台运行 / 跨场景常驻实例去重 / 监听器归属），而且写错的后果都是静默的
    /// （失焦不推进 ⇒ 看起来像状态机坏了；两个驱动器 ⇒ 什么都快一倍；无监听器 ⇒ 刷屏把真问题淹掉）。
    /// 领域内容（关卡、音效名、相机参数）**不在**这里，业务只给开关。
    /// </para>
    /// </summary>
    public sealed class EngineHostOptions
    {
        /// <summary>
        /// 应用失去焦点后是否继续推进帧循环（对应 <c>Application.runInBackground</c>）。
        /// <para>
        /// <c>null</c>（默认）= <b>不干预</b>：沿用播放器 / 编辑器当前的设置（= 本次下沉前的行为）。
        /// </para>
        /// <para>
        /// 什么时候要开：跑自动化验证 / 无人值守截图时，编辑器窗口一失焦，<c>Time.frameCount</c> 就冻住
        /// （实测两分钟只走 2 帧），现象是"定时器不触发、流程卡死在启动画面"，看起来像状态机坏了，
        /// 而游戏代码一行问题都没有。
        /// </para>
        /// <para>什么时候别开：正式单机游戏不希望切出去还在后台跑（耗电 / 后台还在播声音）。</para>
        /// </summary>
        public bool? RunInBackground;

        /// <summary>
        /// 是否启用**重复驱动器守卫**。<c>false</c>（默认）= 不开启（= 现状：不做任何检查）。
        /// <para>
        /// 开启后，<see cref="EngineRunner"/> 的每个实例在 <c>Awake</c> 里：若已有活着的驱动器，
        /// 就销毁"后到的这一个"自己的 GameObject 并留一条 Warn；否则把自己登记为单例 ——
        /// 于是**场景里预置的驱动器**也能挡住 <c>Game.Launch</c> 再建一个。
        /// </para>
        /// <para>
        /// 为什么值得开：两个驱动器 = <c>Game.Tick</c> 每帧跑两遍（位移 / 计时 / 网络重传全部加倍），
        /// 且 <c>Game.Shutdown</c> 可能被先销毁的那个提前触发；它**不报任何错**，
        /// 只表现为"什么都快了一倍 / 抖 / 刚进场景就被关了"。
        /// </para>
        /// </summary>
        public bool DuplicateInstanceGuard;

        /// <summary>
        /// 是否由宿主**独占** <see cref="AudioListener"/>。<c>false</c>（默认）= 不干预（= 现状）。
        /// <para>
        /// 开启后：宿主 GameObject 上保证有且只有一个 <see cref="AudioListener"/>，
        /// 同时把场景里其它 <see cref="AudioListener"/> <c>enabled = false</c>（每个留一条 Info）。
        /// </para>
        /// <para>
        /// 为什么挂在宿主而不是场景相机：宿主是 <c>DontDestroyOnLoad</c>，而场景相机会随
        /// <c>Game.Scene.Load</c> 被销毁 —— 监听器挂在相机上，一切场景就没有监听器了
        /// （Unity 每帧刷一条 "There are no audio listeners in the scene"，实测把 Editor.log 刷到 74MB，
        /// 并把真正的问题全部淹掉）。判据只看"自己身上有没有"，**不看场景里有没有**：
        /// 场景里那个本来就活不过切场景，拿它当"已存在"的判据正是上面那个坑的成因。
        /// </para>
        /// <para>
        /// ⚠️ <b>代价（要不要开取决于项目）</b>：宿主是原点上的常驻节点，因此**3D 空间音效会按
        /// 宿主位置（原点）算距离衰减**。<c>PlaySFXAt</c> 的远近衰减对玩法有意义的项目**不要开这一项**，
        /// 应把监听器留在跟随机位的相机上（这种情况由业务自己管监听器归属）。
        /// </para>
        /// </summary>
        public bool OwnAudioListener;
    }

    /// <summary>
    /// 引擎驱动器，以 Unity MonoBehaviour 形式驱动引擎主循环与生命周期回调。
    /// <para>
    /// 宿主级可选行为（后台运行 / 重复实例守卫 / AudioListener 归属）见 <see cref="EngineHostOptions"/>，
    /// 经 <c>Game.ConfigureHost</c> 下发；**默认全部不干预**。
    /// </para>
    /// </summary>
    [DefaultExecutionOrder(-10000)]
    internal class EngineRunner : MonoBehaviour
    {
        /// <summary>
        /// 当前驱动器单例，用于判断驱动器是否已创建。
        /// </summary>
        private static EngineRunner _instance;

        /// <summary>宿主开关；未调用 <see cref="Configure"/> 时 = 每个字段的默认值（全部"不干预"）。</summary>
        private static EngineHostOptions _options = new EngineHostOptions();

        /// <summary>
        /// 本实例是被重复实例守卫销毁的"多余驱动器"（不是引擎宿主）。
        /// <para>
        /// 必须在销毁前置位：这类实例的 <see cref="OnDestroy"/> 若照常调 <c>Game.Shutdown</c>，
        /// 会把**还在另一个驱动器上跑着的引擎一起关掉**（现象 = "一进某个场景游戏就停了"）。
        /// </para>
        /// </summary>
        private bool _duplicateDestroyed;

        /// <summary>
        /// 配置宿主行为（门面入口 <c>Game.ConfigureHost</c>）。不调用 = 全部沿用现状。
        /// <para>
        /// 生效时机：<see cref="EngineHostOptions.RunInBackground"/> 有值时**立即**生效（宿主已存在也有效）；
        /// 另两项是"创建宿主时执行一次"的动作，**必须早于 <c>Game.Launch</c>**（= 早于宿主创建）才生效 ——
        /// 宿主已存在时才调用会记一条 Warn，避免出现"我配了但它没生效"这种静默失效。
        /// </para>
        /// </summary>
        /// <param name="options">宿主开关；null 记 Warn 后按"全部不干预"处理。</param>
        public static void Configure(EngineHostOptions options)
        {
            if (options == null)
            {
                Game.Logger?.Warn("EngineRunner", "ConfigureHost 收到 null，已按「全部不干预」处理");
                return;
            }

            _options = options;

            // RunInBackground 不是"创建时一次"的动作：有值就立刻写，宿主已存在也照样生效。
            if (options.RunInBackground.HasValue)
                Application.runInBackground = options.RunInBackground.Value;

            if (_instance != null && (options.DuplicateInstanceGuard || options.OwnAudioListener))
                Game.Logger?.Warn("EngineRunner",
                    "宿主已创建：DuplicateInstanceGuard / OwnAudioListener 这两项是创建宿主时执行的动作，" +
                    "本次配置对已有宿主不生效 —— 请把 ConfigureHost 放在 Game.Launch 之前");
        }

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
            // AddComponent 会**同步**触发 Awake：守卫开启时由 Awake 把"先到的那个"登记为单例，
            // 这里再把组件引用赋给 _instance（同值）。守卫关闭时 Awake 不碰 _instance，行为与旧实现一致。
            _instance = go.AddComponent<EngineRunner>();
        }

        /// <summary>
        /// 重复实例守卫 + 监听器归属（**两项都只在 <see cref="EngineHostOptions"/> 里显式打开时才做事**）。
        /// <para>
        /// 默认（守卫关闭）= 本方法立即返回：<c>_instance</c> 仍只由 <see cref="Ensure"/> 赋值，
        /// 与本次下沉前逐字一致。
        /// </para>
        /// </summary>
        private void Awake()
        {
            if (_options.DuplicateInstanceGuard)
            {
                if (_instance != null && _instance != this)
                {
                    Game.Logger?.Warn("EngineRunner",
                        $"检测到重复的引擎驱动器（已有 {_instance.name}），已销毁后到的实例：" +
                        "两个驱动器会让 Game.Tick 每帧跑两遍（位移 / 计时 / 网络重传全部加倍）");
                    _duplicateDestroyed = true;   // 见字段说明：它不能触发 Shutdown
                    Destroy(gameObject);
                    return;
                }

                // 先到的那个登记为单例：这样"场景里预置的驱动器"也能挡住 Launch 再 Ensure 一个。
                _instance = this;
            }

            if (_options.OwnAudioListener)
                ApplyAudioListenerOwnership();
        }

        /// <summary>
        /// 宿主独占 AudioListener：保证宿主自己有一个，并把场景里其它监听器禁用。
        /// 每个被禁用的监听器留一条 Info（一次性动作，不会刷屏）。
        /// </summary>
        private void ApplyAudioListenerOwnership()
        {
            if (GetComponent<AudioListener>() == null)
                gameObject.AddComponent<AudioListener>();

            // 用 FindObjectsByType 而不是 FindFirstObjectByType：这里要处理"全部"。
            var listeners = UnityEngine.Object.FindObjectsByType<AudioListener>(FindObjectsSortMode.None);
            foreach (var other in listeners)
            {
                if (other == null || other.gameObject == gameObject) continue;
                other.enabled = false;
                Game.Logger?.Info("EngineRunner",
                    $"场景里已有 AudioListener（{other.gameObject.name}），已禁用以避免重复（宿主独占）");
            }
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
            if (_duplicateDestroyed) return;
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

            // 被重复实例守卫销毁的那一个**不是**引擎宿主：它调用 Shutdown 会把还在跑的引擎一起关掉。
            if (_duplicateDestroyed) return;

            // Game.Shutdown 幂等（未运行时直接返回）：与 OnApplicationQuit 重复到达也安全
            // （编辑器停止播放、宿主被 Destroy 等路径都会走到这里）。
            Game.Shutdown();
        }
    }
}
