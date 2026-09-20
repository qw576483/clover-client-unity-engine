using System;
using UnityEngine;

namespace CloverEngine
{
    /// <summary>
    /// 表现域引导类：把实体/对象池 / UI / 场景 / 图集 / 动画 / 声音 / 相机 / 设备 一次性挂接到 <see cref="Game"/> 门面。
    ///
    /// 业务通常**不需要**手动调用：本类在进入 Play 时注册启动钩子，<see cref="Game.Launch"/> 完成后自动
    /// 执行 <see cref="Init"/>，因此 Launch 之后可以直接用：
    /// <code>
    /// Game.UI.Open&lt;LoginPanel&gt;();
    /// Game.Scene.Load("MainCity");
    /// Game.Sound.PlayBGM("bgm_main");
    /// Game.Camera.Follow(player.transform);
    /// Game.Quality.AutoDetect();
    /// </code>
    ///
    /// 需要精细控制时（只想挂一部分 / 手动接管）：
    /// <code>
    /// CloverPresentation.AutoMount = false;   // 关掉自动挂载，必须在 Launch 之前设置
    /// Game.Launch(config);
    /// CloverPresentation.Init();              // 手动挂载（幂等）
    /// </code>
    ///
    /// 与 CloverNet / CloverData / CloverRes / CloverInput 同构，
    /// 维持「Presentation → Core 单向依赖」规则（Core 不反向构造模块实现）。
    /// </summary>
    public static class CloverPresentation
    {
        private const string HookKey = "CloverPresentation";

        /// <summary>
        /// 是否在 <see cref="Game.Launch"/> 后自动挂载表现域模块（默认 true）。
        /// 必须在 Game.Launch 之前设置；运行中修改无效。
        /// </summary>
        public static bool AutoMount { get; set; } = true;

        /// <summary>表现域模块是否已挂载（以 UI 是否已挂接为准）。</summary>
        public static bool Initialized => Game.UI != null;

        /// <summary>
        /// 实体视图工厂：把「实体」变成「场景里看得见的角色」（异步加载模型、加载期占位、
        /// 归一化身高并贴地、可选接动画，以及"加载完成时实体已不存在"的竞态处理）。
        /// 契约见 <see cref="IEntityViewFactory"/>。
        /// <para>
        /// 由 <see cref="Init"/> 在挂载 <see cref="Game.Entity"/> 时**一并创建**，构造时自动注册给实体管理器
        /// （见 <c>EntityManager.RegisterViewFactory</c>）—— 实体的 <c>Destroy</c> / <c>DestroyGroup</c> /
        /// <c>ClearAll</c> 会据此释放工厂侧记录（动画播放器 + 模型资源引用）。业务用法：
        /// <code>
        /// var view = CloverPresentation.EntityView.CreateView(objectID,
        ///     EntityViewSpec.Of("Models/hero", targetHeight: 1.8f));
        /// Game.Entity.BindView(objectID, view);   // 登记视图；其销毁由 Entity 侧统一负责
        /// </code>
        /// </para>
        /// </summary>
        public static IEntityViewFactory EntityView { get; private set; }

        /// <summary>
        /// 面板预制体提供者：传入面板类名，返回预制体（找不到返回 null）。
        /// 为 null 时使用默认实现 —— <c>Resources.Load&lt;GameObject&gt;("UI/{类名}")</c>。
        ///
        /// 接入 Addressables / AssetBundle / 自定义目录时在此替换，例如：
        /// <code>
        /// CloverPresentation.PanelProvider = name =&gt; Addressables.LoadAssetAsync&lt;GameObject&gt;(name).WaitForCompletion();
        /// </code>
        /// 建议在 Game.Launch 之前设置；运行中替换只影响之后打开的面板。
        /// </summary>
        public static Func<string, GameObject> PanelProvider { get; set; }

        /// <summary>
        /// 注册自动挂载钩子。SubsystemRegistration 阶段执行，早于首个场景的 Start，
        /// 因此一定早于业务调用 Game.Launch。
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void RegisterAutoMount()
        {
            Game.RegisterLaunchHook(HookKey, () =>
            {
                if (AutoMount && Application.isPlaying) Init();
            });
        }

        /// <summary>
        /// 挂接全部表现域模块（幂等）。需在 <see cref="Game.Launch"/> 之后调用；
        /// 自动挂载已开启时由 Launch 钩子调用，业务无需重复调用。
        /// </summary>
        public static void Init()
        {
            if (!Game.IsRunning)
            {
                Debug.LogError("[Clover.Presentation] 引擎尚未启动，请先调用 Game.Launch(config) 再调用 CloverPresentation.Init()");
                return;
            }

            // 逻辑地图是**纯数据模块**（不创建任何 GameObject），所以挂在「非运行时早退」之前：
            // EditMode 单测里同样拿得到 Game.Map 做解码与查询验证，不必先拉起 Play 模式。
            if (Game.Map == null) Game.AttachMap(new MapModule());

            // UI / 声音会创建常驻 GameObject（DontDestroyOnLoad），
            // 非运行时（EditMode 单测、编辑器脚本）不允许创建，与 EngineRunner.Ensure 的约定一致。
            if (!Application.isPlaying)
            {
                Game.Logger?.Warn("Presentation", "非运行环境下不挂载表现域模块（UI/声音需要运行期常驻 GameObject）");
                return;
            }

            if (Game.Entity == null)
            {
                Game.AttachEntity(new EntityManager());
                // 实体视图工厂与 EntityManager 配对创建：EntityManager 的工厂注册槽是**静态**的，
                // Shutdown 后重新 Launch → Init 会新建 EntityManager，此时必须同步新建工厂重新注册，
                // 否则注册槽仍指向上一轮的旧工厂实例（实体销毁时释放的是旧工厂的记录）。
                // 构造即完成注册（EntityViewFactory 构造函数内调用 EntityManager.RegisterViewFactory）。
                EntityView = new EntityViewFactory();
            }
            if (Game.Pool == null) Game.AttachPool(new ObjectPool());
            if (Game.UI == null) Game.AttachUI(new UIManager());
            if (Game.Scene == null) Game.AttachScene(new SceneModule());
            if (Game.Atlas == null) Game.AttachAtlas(new SpriteAtlasManager());
            if (Game.Anim == null) Game.AttachAnimation(new AnimationManager());
            if (Game.Sound == null) Game.AttachSound(new SoundManager());
            if (Game.Camera == null) Game.AttachCamera(new CameraManager());
            if (Game.Quality == null) Game.AttachQuality(new QualityManager());

            // EventSystem 归输入模块管理（避免两个 InputModule 并存），已挂接时顺手确保一次。
            // 未挂接也不在此处报错：只有真正 Open 面板时才提示（见 UIManager.Open），避免正确启动顺序被误报。
            Game.Input?.EnsureEventSystem();

            Game.Logger?.Info("Presentation",
                "表现域模块已挂载: Map/Entity(+EntityView)/Pool/UI/Scene/Atlas/Anim/Sound/Camera/Quality");
        }
    }
}
