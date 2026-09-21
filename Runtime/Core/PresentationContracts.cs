using System;
using UnityEngine;
using UnityEngine.U2D;

namespace CloverEngine
{
    // 表现域契约（Core 层）
    //
    // 为什么这些接口放在 Core 而不是 Presentation：
    //   asmdef 依赖是 Presentation → Core 单向，Core 无法反向引用 Presentation。
    //   而 Game 门面在 Core，要给业务暴露 Game.UI / Game.Scene / … 就必须能在 Core 里见到这些类型。
    //   与已挂载的 IEntityManager / IObjectPool / IInputManager（接口在 Core、实现在 Presentation）
    //   保持同一模式。实现类仍在 Runtime/Presentation/ 且为 internal，业务只通过 Game 门面拿实例。

    // ───────────────────────────── UI ─────────────────────────────

    /// <summary>
    /// UI 层级，决定面板挂载的 Canvas 子节点顺序
    /// </summary>
    public enum UILayer
    {
        Background = 0,
        Normal = 1,
        Popup = 2,
        Top = 3,
        System = 4
    }

    /// <summary>
    /// UI 面板接口，业务面板实现此接口后交由 UIManager 管理
    /// </summary>
    public interface IUIPanel
    {
        /// <summary>面板名，默认取类型名</summary>
        string PanelName { get; }
        /// <summary>所属层级</summary>
        UILayer Layer { get; }
        /// <summary>打开时回调</summary>
        void OnOpen(object param);
        /// <summary>关闭时回调</summary>
        void OnClose();
        /// <summary>每帧更新（由 UIManager.Tick 驱动）</summary>
        void OnUpdate(float dt);
        /// <summary>面板根节点</summary>
        GameObject Root { get; }
    }

    /// <summary>
    /// UI 管理器接口：窗口栈、固定层级、互斥与遮罩。打开 / 关闭为同步执行
    /// （预制体经 <c>Resources.Load</c> 或 <c>CloverPresentation.PanelProvider</c> 即时取得）。
    /// </summary>
    public interface IUIManager
    {
        /// <summary>打开面板（泛型指定面板类型；已打开则重新 OnOpen，并在所在层内置顶）</summary>
        void Open<T>(object param = null) where T : class, IUIPanel;
        /// <summary>关闭面板</summary>
        void Close<T>() where T : class, IUIPanel;
        /// <summary>按面板名关闭</summary>
        void Close(string panelName);
        /// <summary>关闭全部已打开面板</summary>
        void CloseAll();
        /// <summary>获取已打开的面板实例，未打开返回 null</summary>
        T Get<T>() where T : class, IUIPanel;
        /// <summary>面板是否已打开</summary>
        bool IsOpen<T>() where T : class, IUIPanel;
        /// <summary>订阅面板打开事件</summary>
        void OnPanelOpened(Action<string> handler);
        /// <summary>订阅面板关闭事件</summary>
        void OnPanelClosed(Action<string> handler);

        // ─────────────── 通用件（Toast / 飘字 / Loading / 确认框 / 红点 / 引导） ───────────────
        //
        // 这些件**不进入窗口栈、不参与 Popup 互斥**：一个 Toast 不应该把正在看的弹窗顶掉。
        // 实现零美术依赖（代码搭 uGUI 节点），新工程 Launch 之后即可直接用，不会因为
        // 「预制体还没做」而静默无反馈；需要美术版时替换 Presentation 侧实现即可，接口不变。

        /// <summary>顶部弹一条短提示，<paramref name="duration"/> 秒后自动淡出。文案为空则忽略。</summary>
        void Toast(string text, float duration = 2f);

        /// <summary>
        /// 在指定世界坐标冒一段飘字（伤害数字 / 获得物品），向上飘并淡出。
        /// <paramref name="color"/> 传 null 用默认金色；目标在相机背面时不显示。
        /// </summary>
        void FloatText(Vector3 worldPos, string text, Color? color = null, float duration = 1.2f);

        /// <summary>
        /// 显示全屏 Loading（半透明遮罩 + 旋转指示 + 文案）。
        /// <para>
        /// **引用计数**：可以嵌套调用，先完成的一方 <see cref="HideLoading"/> 不会把还在等的那层关掉。
        /// 文案为空时沿用上一次的文案。
        /// </para>
        /// </summary>
        void ShowLoading(string text = null);

        /// <summary>收掉一层 Loading（与 <see cref="ShowLoading"/> 配对）；计数归零才真正隐藏。</summary>
        void HideLoading();

        /// <summary>当前是否正在显示 Loading。</summary>
        bool IsLoading { get; }

        /// <summary>
        /// 弹一个确认框（标题 / 正文 / 确认 / 取消）。
        /// 同时只显示一个，后到的请求**排队**（丢请求会让玩家点了确认却什么都没发生）。
        /// </summary>
        /// <param name="title">标题，空则显示「提示」。</param>
        /// <param name="message">正文。</param>
        /// <param name="onConfirm">点确认后的回调，可为 null。</param>
        /// <param name="onCancel">点取消（或关闭）后的回调，可为 null。</param>
        /// <param name="confirmText">确认按钮文案，空则「确认」。</param>
        /// <param name="cancelText">取消按钮文案，空则「取消」。</param>
        void Confirm(string title, string message, Action onConfirm, Action onCancel = null,
            string confirmText = null, string cancelText = null);

        /// <summary>
        /// 设置某个红点 key 的显式亮/灭。
        /// key 用 <c>/</c> 分层（<c>mail</c> / <c>mail/reward</c>），父节点自动聚合后代，
        /// 因此点亮 <c>mail/reward</c> 会让 <c>GetRedDot("mail")</c> 同时为 true，无需手工维护父节点。
        /// </summary>
        /// <param name="key">红点 key，不允许为空。</param>
        /// <param name="on">是否亮起。</param>
        void SetRedDot(string key, bool on);

        /// <summary>查询红点 key 当前是否亮起（含后代聚合）。</summary>
        bool GetRedDot(string key);

        /// <summary>订阅红点变化（参数为 key 与变化后的有效值），仅在有效值真正变化时回调。</summary>
        void OnRedDotChanged(Action<string, bool> handler);

        /// <summary>
        /// 显示引导遮罩：挖空 <paramref name="target"/> 所在区域高亮它，其余压暗，并挂一句提示。
        /// 参数为空 / 不传时按「挖空区外点击推进」处理，见 <paramref name="blockTarget"/>。
        /// </summary>
        /// <param name="target">要高亮的目标控件（可为 null，则挖在屏幕中央）。</param>
        /// <param name="tip">提示文案，空则不挂气泡。</param>
        /// <param name="onClick">点击后的回调（两种模式下都会调用）。</param>
        /// <param name="blockTarget">
        /// true（默认）= 点击屏幕任意处触发 <paramref name="onClick"/>，被高亮的控件本身不接收点击（「点这里继续」式）；
        /// false = 只有挖空区外拦点击，被高亮的控件能正常点，业务需自己在目标回调里调 <see cref="HideGuide"/>。
        /// </param>
        void ShowGuide(RectTransform target, string tip = null, Action onClick = null, bool blockTarget = true);

        /// <summary>关闭引导遮罩。</summary>
        void HideGuide();

        /// <summary>每帧驱动已打开面板的 OnUpdate 与各通用件动画；由 Game.Tick 调用</summary>
        void Tick(float dt);
        /// <summary>关闭全部面板并销毁 UI 根节点</summary>
        void Dispose();
    }

    /// <summary>
    /// UI 面板基类：把 IUIPanel 的样板默认值都实现好，业务面板继承它后
    /// **只需 override 自己关心的方法**（通常只有 OnOpen）。
    /// <code>
    /// public class LoginPanel : UIPanel
    /// {
    ///     public override void OnOpen(object param) { /* 刷新界面 */ }
    /// }
    /// </code>
    /// 默认 PanelName 取类型名（即 UIManager 的注册键）；预制体路径按类名
    /// 查 <c>Resources/UI/{类名}</c>（可经 <c>CloverPresentation.PanelProvider</c> 替换来源）。
    /// </summary>
    public abstract class UIPanel : MonoBehaviour, IUIPanel
    {
        /// <summary>
        /// 面板名，默认取类型名。它是 UIManager 里的**注册键**：Open 后的
        /// <c>Close(string)</c> / <c>OnPanelOpened</c> / <c>OnPanelClosed</c> 都用它；
        /// override 后需自行保证同一面板唯一。预制体路径仍按**类名**查
        /// <c>Resources/UI/{类名}</c>（或经 <c>CloverPresentation.PanelProvider</c>）。
        /// </summary>
        public virtual string PanelName => GetType().Name;

        /// <summary>所属层级，默认 Normal。</summary>
        public virtual UILayer Layer => UILayer.Normal;

        /// <summary>面板根节点，默认即本组件所在 GameObject。</summary>
        public GameObject Root => gameObject;

        /// <summary>打开时回调，默认空实现。</summary>
        public virtual void OnOpen(object param) { }

        /// <summary>关闭时回调，默认空实现。</summary>
        public virtual void OnClose() { }

        /// <summary>每帧回调，默认空实现。</summary>
        public virtual void OnUpdate(float dt) { }
    }

    // ─────────────────────────── Scene ───────────────────────────

    /// <summary>
    /// 场景管理器接口：异步加载/卸载、进度回调、加载门控与场景级清理
    /// </summary>
    public interface ISceneManager
    {
        /// <summary>当前场景名</summary>
        string CurrentScene { get; }
        /// <summary>异步加载场景，progress 为 0~1 进度，onDone 在完成后回调</summary>
        void Load(string sceneName, Action<float> progress = null, Action onDone = null);
        /// <summary>异步卸载场景</summary>
        void Unload(string sceneName, Action onDone = null);
        /// <summary>订阅场景加载完成事件</summary>
        void OnSceneLoaded(Action<string> handler);
        /// <summary>订阅场景卸载完成事件</summary>
        void OnSceneUnloaded(Action<string> handler);
    }

    // ───────────────────────── SpriteAtlas ───────────────────────

    /// <summary>
    /// 图集管理器接口，提供图集的加载、释放和精灵获取功能
    /// </summary>
    public interface ISpriteAtlasManager
    {
        /// <summary>
        /// 异步加载指定图集，已加载时增加引用计数并直接回调
        /// </summary>
        /// <param name="atlasName">图集资源名称</param>
        /// <param name="callback">加载完成后的回调，若加载失败则参数为 null</param>
        void Load(string atlasName, Action<SpriteAtlas> callback);

        /// <summary>
        /// 释放指定图集的引用，引用计数归零时自动卸载资源
        /// </summary>
        /// <param name="atlasName">图集资源名称</param>
        void Release(string atlasName);

        /// <summary>
        /// 从指定图集中获取精灵，若图集未加载则先自动加载再获取。
        /// <para>
        /// 注意：自动加载会**增加一次引用计数**，且不会自动归还 —— 长期持有的调用方应自行
        /// <see cref="Release"/>（引用计数只增不减会让图集常驻内存）。
        /// </para>
        /// </summary>
        /// <param name="atlasName">图集资源名称</param>
        /// <param name="spriteName">精灵名称</param>
        /// <param name="callback">获取完成后的回调，若精灵不存在则参数为 null</param>
        void GetSprite(string atlasName, string spriteName, Action<Sprite> callback);

        /// <summary>
        /// 卸载所有已缓存的图集并清除引用计数
        /// </summary>
        void UnloadAll();
    }

    // ───────────────────────── Animation ─────────────────────────

    /// <summary>
    /// 动画播放器接口（基于 Unity Animator）。
    /// <para>
    /// 需要动画事件时，在业务侧自行挂 <c>AnimationEvent</c> 接收脚本：本接口只负责状态播放、
    /// 参数设置与播放完成回调。第三方骨骼动画（Spine / DragonBones）由业务自行接入 SDK，
    /// 引擎不提供封装。
    /// </para>
    /// </summary>
    public interface IAnimPlayer
    {
        /// <summary>播放指定状态，normalizedTime 为归一化起始时间</summary>
        void Play(string stateName, float normalizedTime = 0f);
        /// <summary>在 duration 秒内过渡到指定状态</summary>
        void CrossFade(string stateName, float duration = 0.25f);
        /// <summary>设置布尔参数</summary>
        void SetBool(string name, bool value);
        /// <summary>设置浮点参数</summary>
        void SetFloat(string name, float value);
        /// <summary>设置整型参数</summary>
        void SetInteger(string name, int value);
        /// <summary>触发一次性触发器</summary>
        void SetTrigger(string name);
        /// <summary>订阅播放完成回调</summary>
        void OnComplete(Action callback);
    }

    /// <summary>
    /// 动画管理器接口：创建/销毁播放器，并统一 Tick
    /// </summary>
    public interface IAnimationManager
    {
        /// <summary>为 GameObject 创建 Animator 播放器</summary>
        IAnimPlayer CreateAnimator(GameObject go, RuntimeAnimatorController controller);
        /// <summary>销毁播放器</summary>
        void Destroy(IAnimPlayer player);
        /// <summary>每帧驱动全部播放器</summary>
        void Tick(float dt);
    }

    // ─────────────────────────── Sound ───────────────────────────

    /// <summary>
    /// 音频分组，区分背景音乐、音效和人声的独立音量控制
    /// </summary>
    public enum SoundGroup
    {
        BGM = 0,
        SFX = 1,
        Voice = 2
    }

    /// <summary>
    /// 声音管理器接口，提供 BGM、音效、人声的播放控制与音量管理
    /// </summary>
    public interface ISoundManager
    {
        /// <summary>
        /// 播放背景音乐，双音源交替实现切换过渡
        /// </summary>
        /// <param name="clipName">音频资源名称</param>
        /// <param name="fadeTime">淡入时长（秒）</param>
        void PlayBGM(string clipName, float fadeTime = 0.5f);

        /// <summary>
        /// 停止当前背景音乐
        /// </summary>
        /// <param name="fadeTime">淡出时长（秒）</param>
        void StopBGM(float fadeTime = 0.5f);

        /// <summary>
        /// 以 2D 模式播放音效
        /// </summary>
        /// <param name="clipName">音频资源名称</param>
        void PlaySFX(string clipName);

        /// <summary>
        /// 在指定世界坐标以 3D 空间模式播放音效
        /// </summary>
        /// <param name="clipName">音频资源名称</param>
        /// <param name="position">播放位置的世界坐标</param>
        void PlaySFXAt(string clipName, Vector3 position);

        /// <summary>
        /// 以 2D 模式播放人声
        /// </summary>
        /// <param name="clipName">音频资源名称</param>
        void PlayVoice(string clipName);

        // ─────────────── 播放闸门（单帧上限 / 同 clip 并发上限） ───────────────
        //
        // 为什么在契约上：实现类 `SoundManager` 是 internal（G1），业务只拿得到 `Game.Sound`
        // （本接口）—— 只挂在实现类上等于"业务配不了"，闸门就永远是默认值（= 没闸门）。
        // 取值来源仍是各项目自己的调参常量（引擎不含任何项目数值）。

        /// <summary>
        /// 单帧最多**真正起播**几次音效 / 人声；<c>0</c> = 不限（**默认 0**）。
        /// <para>
        /// 超限的请求**直接丢弃**（⛔ 不排队、⛔ 不打断正在播的音源），并经
        /// <c>LogThrottle.WarnThrottled</c> 限频告警。只影响 <see cref="PlaySFX"/> /
        /// <see cref="PlaySFXAt"/> / <see cref="PlayVoice"/>；BGM 不受影响。
        /// </para>
        /// <para>
        /// ⚠️ 计数口径 = **真正起播**的那一帧（异步加载晚到的音效算在它响的那一帧），不是"被请求"的那一帧。
        /// 默认 <c>0</c> 时**与不设闸门逐字一致** —— 改默认值会悄悄改掉所有项目的表现，⛔ 不许改。
        /// </para>
        /// </summary>
        int MaxPlaysPerFrame { get; set; }

        /// <summary>
        /// 同一路径**同时播放**的上限；<c>0</c> = 不限（**默认 0**）。
        /// <para>
        /// 口径 = 该路径**当前正在播的音源数**（真并发，**不是**"时间窗内起播了几次"）。
        /// 路径含分组前缀（<c>Sound/SFX/…</c> / <c>Sound/Voice/…</c>）⇒ 不同分组互不干扰，BGM 不参与。
        /// 超限同样丢弃 + 限频告警（见 <see cref="MaxPlaysPerFrame"/>）。
        /// </para>
        /// </summary>
        int MaxConcurrentPerClip { get; set; }

        /// <summary>
        /// 停止所有正在播放的声音，包括 BGM、音效和人声
        /// </summary>
        void StopAll();

        /// <summary>
        /// 设置指定音频分组的音量，数值会被限制在 0~1 范围内
        /// </summary>
        /// <param name="group">音频分组</param>
        /// <param name="volume">音量值，范围 0~1</param>
        void SetVolume(SoundGroup group, float volume);

        /// <summary>
        /// 获取指定音频分组的音量
        /// </summary>
        /// <param name="group">音频分组</param>
        /// <returns>当前音量值，范围 0~1，未设置时返回 1</returns>
        float GetVolume(SoundGroup group);

        /// <summary>
        /// 设置指定音频分组的静音状态
        /// </summary>
        /// <param name="group">音频分组</param>
        /// <param name="mute">是否静音</param>
        void SetMute(SoundGroup group, bool mute);

        /// <summary>
        /// 释放音频管理器资源
        /// </summary>
        void Dispose();
    }

    // ─────────────────────────── Camera ──────────────────────────

    /// <summary>
    /// 相机管理器接口，提供跟随、震屏和边界约束功能
    /// </summary>
    public interface ICameraManager
    {
        /// <summary>
        /// 让主相机平滑跟随指定目标
        /// </summary>
        /// <param name="target">跟随目标</param>
        /// <param name="smoothTime">平滑跟随时长（秒），值越小跟随越紧</param>
        void Follow(Transform target, float smoothTime = 0.1f);

        /// <summary>
        /// 停止跟随当前目标
        /// </summary>
        void Unfollow();

        /// <summary>
        /// 触发相机震动效果，强度随时间线性衰减
        /// </summary>
        /// <param name="duration">震动持续时间（秒）</param>
        /// <param name="intensity">震动最大偏移强度</param>
        void Shake(float duration, float intensity);

        /// <summary>
        /// 设置相机移动边界，相机位置将被限制在该范围内
        /// </summary>
        /// <param name="bounds">允许相机覆盖的区域边界</param>
        void SetBounds(Bounds bounds);

        /// <summary>驱动每帧相机更新，依次处理跟随、边界约束和震动偏移</summary>
        /// <param name="dt">帧间隔时间</param>
        void Tick(float dt);
    }

    // ────────────────────────── Quality ──────────────────────────
    //
    // 命名说明：这一族管的是**画质与性能档位**（分辨率缩放 / 阴影 / LOD / 帧率），
    // 历史上叫 Device*（Game.Device / DeviceLevel / IDeviceManager），容易与
    // 「设备唯一标识」（Game.DeviceId）混淆——两者毫无关系：
    //   - 本族：这台机器**跑得动多好的画质**（可随时改，不涉及身份）
    //   - DeviceId：这台机器**是谁**（稳定标识，用于账号 / 房间寻址）
    // 故统一更名为 Quality*。

    /// <summary>
    /// 画质档位，用于选择对应的质量配置预设
    /// </summary>
    public enum QualityTier
    {
        Low = 0,
        Medium = 1,
        High = 2
    }

    /// <summary>
    /// 质量配置，定义分辨率缩放、阴影、LOD、粒子密度等同屏表现参数
    /// </summary>
    public class QualityConfig
    {
        /// <summary>渲染分辨率缩放比例，范围 0~1</summary>
        public float ResolutionScale = 1f;
        /// <summary>是否启用阴影</summary>
        public bool ShadowEnabled;
        /// <summary>LOD 细节等级，数值越小细节越多</summary>
        public int LODLevel;
        /// <summary>粒子密度系数，范围 0~1</summary>
        public float ParticleDensity = 1f;
        /// <summary>同屏同类对象的最大数量</summary>
        public int MaxSameScreenCount = 20;
        /// <summary>目标帧率</summary>
        public int TargetFrameRate = 60;
    }

    /// <summary>
    /// 画质管理器接口：画质档位设置、自动检测与等级变更通知。
    /// </summary>
    public interface IQualityManager
    {
        /// <summary>当前画质档位</summary>
        QualityTier Level { get; }

        /// <summary>当前生效的质量配置</summary>
        QualityConfig Config { get; }

        /// <summary>
        /// 是否处于性能节流状态。
        /// <para>
        /// 信号来源：Unity 的 <c>SystemInfo</c> 不提供电池温度，因此这里以
        /// **当前平均 FPS 低于该档位目标帧率的 70%** 作为「正被性能压制」的判据，
        /// 而非硬件热节流；已处于最低档时为 false。
        /// </para>
        /// </summary>
        bool IsThrottling { get; }

        /// <summary>当前实际帧率</summary>
        float CurrentFPS { get; }

        /// <summary>设置画质档位，应用对应预设配置并通知变更</summary>
        /// <param name="level">目标画质档位</param>
        void SetLevel(QualityTier level);

        /// <summary>根据设备内存和显存自动检测并设置画质档位</summary>
        void AutoDetect();

        /// <summary>每帧更新，用于检测节流与帧率变化</summary>
        /// <param name="dt">帧间隔时间</param>
        void Tick(float dt);

        /// <summary>订阅画质档位变更事件</summary>
        /// <param name="handler">等级变更时的处理函数，参数为新的等级</param>
        void OnLevelChanged(Action<QualityTier> handler);

        /// <summary>订阅性能节流状态变更事件</summary>
        /// <param name="handler">节流状态变更时的处理函数，参数为是否节流</param>
        void OnThrottling(Action<bool> handler);
    }

    // ──────────────────────────── Map ────────────────────────────
    //
    // 逻辑地图：服务端权威地图在客户端的**只读投影**。
    //
    // 为什么引擎必须提供它：客户端本地碰撞要和服务端跑同一套空间事实（哪格能走），否则
    // 本地能穿墙 / 出图 → 服务端按地图拒绝 → 两端位置越差越大 → 表现成「被拽回来」（橡皮带）。
    // 数据源就是服务端加载的**同一份字节**（格式契约见
    // clover-server-engine/pkg/domain/mmo/mapdata/README.md）。
    //
    // ★ 边界：本接口只回答**空间事实**（这一格能不能走、地图多大）。
    //   「输入 → 位移 → 贴着墙滑」那套**本地预测解算不在引擎**（见 clover-client-unity-engine-index.md §3.1），
    //   业务拿这里的查询结果自己写即可。

    /// <summary>
    /// 逻辑地图接口：解码后的地图元数据 + 可行走性查询。
    /// <para>
    /// **未加载时 <see cref="WalkableAt"/> 恒返回 true**（不阻挡）：地图缺失是配置/导出问题，
    /// 不该表现成"玩家被锁死在原地" —— 那会把一个可诊断的问题变成一个不可诊断的现象。
    /// 加载失败的原因见 <see cref="Status"/> 与引擎日志。
    /// </para>
    /// </summary>
    public interface IMapData
    {
        /// <summary>是否已成功加载地图数据。</summary>
        bool Loaded { get; }

        /// <summary>逻辑地图 id（与服务端 mmo 场景 id、<c>Game.CloverScene.SceneID</c> 对齐）。</summary>
        ulong SceneId { get; }

        /// <summary>地图名。</summary>
        string Name { get; }

        /// <summary>数据格式版本。</summary>
        int Version { get; }

        /// <summary>格边长（米）。</summary>
        float CellSize { get; }

        /// <summary>位图原点：格子 (0,0) 的角（世界坐标）。</summary>
        Vector3 Origin { get; }

        /// <summary>位图宽度（格，东西向）。</summary>
        int Width { get; }

        /// <summary>位图深度（格，南北向）。</summary>
        int Depth { get; }

        /// <summary>总格数（<see cref="Width"/> × <see cref="Depth"/>）。</summary>
        int CellCount { get; }

        /// <summary>可走格数。</summary>
        int WalkableCount { get; }

        /// <summary>阻挡格数。</summary>
        int BlockedCount { get; }

        /// <summary>碰撞体数量（客户端不解码碰撞体，只记录数量供诊断）。</summary>
        int ColliderCount { get; }

        /// <summary>当前状态的一行人类可读描述（未加载 / 已加载 / 数据非法）。</summary>
        string Status { get; }

        /// <summary>
        /// 解码并加载地图数据。失败时返回 false 并给出**与服务端同语义**的错误串
        /// （便于两端日志对照排查），此时地图保持未加载状态。
        /// </summary>
        /// <param name="data">CloverMap 二进制字节（通常是 <c>TextAsset.bytes</c>）。</param>
        /// <param name="error">失败原因；成功时为 null。</param>
        bool Load(byte[] data, out string error);

        /// <summary>
        /// 经资源模块异步加载地图数据（走 <c>Game.Res</c>，不直调 Resources 原生 API）。
        /// 路径与 <c>Resources.Load</c> 的写法一致、**不带扩展名**，文件须是 <c>.bytes</c>
        /// （如 <c>"MapData/map-city"</c> 对应 <c>Resources/MapData/map-city.bytes</c>）。
        /// </summary>
        /// <param name="path">Resources 下的资源路径（不含扩展名）。</param>
        /// <param name="onDone">完成回调，参数为是否加载成功；失败原因见引擎日志与 <see cref="Status"/>。</param>
        void LoadFromResource(string path, Action<bool> onDone = null);

        /// <summary>清空已加载的地图数据，回到"未加载"状态。</summary>
        void Clear();

        /// <summary>
        /// 世界坐标 (x, z) 是否可走（越界 = 不可走；未加载 = 可走）。
        /// 算法与服务端 <c>mapdata.WalkableAt</c> 逐位一致（同一份位图、同一套 floor 取整口径）。
        /// </summary>
        bool WalkableAt(float x, float z);
    }
}
