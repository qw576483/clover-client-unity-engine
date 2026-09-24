using UnityEngine;

namespace CloverEngine
{
    /// <summary>
    /// 第一人称相机 rig（FPS 机位）：把「鼠标位移 → yaw/pitch」「眼位 → 相机位姿」「水平 FOV → 垂直 fieldOfView」
    /// 三件事收成一件，并留出**后坐力 / 受击摇晃 / 视点晃动**三个表现层的注入缝。
    ///
    /// <para><b>为什么它是"纯逻辑类"而不是 <c>MonoBehaviour</c></b>：
    /// 与 <see cref="LookAccumulator"/> / <see cref="ViewBob"/> / <see cref="CameraMath"/> 同一条口径 ——
    /// 无 Unity 生命周期依赖 ⇒ 可离线断言、可被任意持有者驱动；而 <c>MonoBehaviour</c> 只能靠 <c>LateUpdate</c>
    /// 读 <c>Time.deltaTime</c>，把"帧步长"写死在组件里（暂停 / 时间缩放 / 自动化驱动都会失真）。
    /// 因此本类的帧步长一律走 <see cref="Tick"/> 参数，由业务在自己的 tick 链里驱动
    /// （引擎既有 <c>CameraManager.Tick(dt)</c> 亦由 <c>Game.Tick</c> 以同一口径驱动，见
    /// <c>Runtime/Core/Game.cs:765</c>）。</para>
    ///
    /// <para><b>为什么是独立类而不是 <c>ICameraManager</c> 的一个模式</b>：与
    /// <see cref="CloverThirdPersonCamera"/> 同一理由 —— <c>ICameraManager</c>
    /// （<c>Runtime/Presentation/Camera.cs</c>）是**2D / 俯视**语义（<c>Follow</c> 锁 Z 轴、<c>SetBounds</c> 按正交视口钳位），
    /// 第一人称要的是"相机贴在眼位上、朝向由视角决定"，两者参数与失效模式完全不同。</para>
    ///
    /// <para><b>复用（⛔ 本类不许再抄一份数学）</b>：</para>
    /// <list type="bullet">
    /// <item>yaw/pitch 累加 + pitch 夹取 ⇒ 引擎件 <see cref="LookAccumulator"/>
    ///   （<c>Repeat(yaw+…,360)</c> 环绕、<c>Clamp(pitch+…, ±limit)</c> 夹紧、<c>delta.y</c> 向下为正）；</item>
    /// <item>视线方向 ⇒ <see cref="CameraMath.AimDirection"/>（yaw 0 = +Z、pitch 正 = 抬头）；</item>
    /// <item>水平 → 垂直 FOV ⇒ <see cref="CameraMath.FovYFromFovX"/>（原版 <c>CalcFov</c> 等价式，
    ///   越界回退 90、<c>aspect&lt;=0</c> 原样返回）；</item>
    /// <item>平滑跟随 ⇒ <see cref="CameraMath.Follow(float,float,float,float)"/> /
    ///   <see cref="CameraMath.Follow(Vector3,Vector3,float,float)"/>（一阶指数、帧率无关）。</item>
    /// </list>
    ///
    /// <para><b>边界与失效模式（全部"有明确行为且不抛"）</b>：</para>
    /// <list type="number">
    /// <item><c>dt &lt;= 0</c>（暂停 / 首帧）⇒ 一行也不推进（状态保持），限频 Warn 留痕；</item>
    /// <item>未绑定相机 / 绑定的相机被销毁 / 相机被禁用（<c>isActiveAndEnabled == false</c>）⇒
    ///   本帧**只跳过"下发位姿"这一步**（视角 / 后坐力 / 摇晃状态照常推进，复绑后立刻可用），
    ///   限频 Warn（未绑定 ⇒ WarnOnce）留痕；</item>
    /// <item>灵敏度 <c>&lt;= 0</c> ⇒ 该轴鼠标位移被忽略（视角不动），限频 Warn；</item>
    /// <item>pitch 到端点 ⇒ 夹住不动（<see cref="LookAccumulator"/> 内夹），按**计数口径**留痕
    ///   （首次必打 + 每 50 次一条，不刷屏）；</item>
    /// <item>未设置眼位来源（<see cref="EyeAnchor"/>）⇒ 只更新朝向、机位保持不动，限频 Warn。</item>
    /// </list>
    ///
    /// <para><b>引擎不内置任何手感数值</b>：灵敏度 / 视角限位 / 各时间常数 / 摇晃幅度全部是公开字段，
    /// 由业务从自己的设置与手感表填进来；本类只提供**机制**（累加、夹取、跟随、衰减、下发）。
    /// 换随机器一律走注入的引擎 <see cref="Rng"/>（⛔ 不用全局静态随机器：会把别人的序列打乱，见 <c>Runtime/Core/Rng.cs:13</c>）。</para>
    ///
    /// <para><b>用法（业务侧，典型 CS 类第一人称）</b>：</para>
    /// <code>
    /// var rig = new CloverFirstPersonCamera { EyeAnchor = playerRoot, EyeOffset = new Vector3(0f, 1.62f, 0f) };
    /// rig.SensitivityX = settings.MouseSensitivity * DegPerCount;   // 每 count 多少度（业务换算）
    /// rig.SensitivityY = rig.SensitivityX; rig.SeparateAxes = settings.SeparateAxis; rig.InvertY = settings.InvertY;
    /// rig.PitchLimit  = tuning.PitchLimit;                          // 玩法口径
    /// rig.RecoilRiseTau = tuning.RecoilRiseTau; rig.RecoilFallTau = tuning.RecoilFallTau;
    /// rig.ShakeRng = new Rng(matchSeed);
    /// rig.Bind();                                                   // 用 Game.Camera.Main
    ///
    /// void Update()                          // 业务自己的 tick（可由 Game.Tick 间接驱动）
    /// {
    ///     rig.Tick(Time.deltaTime);           // 帧步长注入，不写死在引擎里
    ///     rig.SetRecoil(sim.RecoilPitch, sim.RecoilYaw);   // 只把**权威值**喂进来，表现由本类跟随
    ///     rig.ViewOffset = bob.Offset; rig.ViewRoll = bob.Roll;   // 引擎件 ViewBob 的输出缝
    /// }
    /// </code>
    ///
    /// <para><b>已知边界</b>：非线程安全（主线程使用）；<see cref="Tick"/> 每帧只许调用一次
    /// （鼠标位移是"读时结算"的增量，一帧调两次会把同一帧的位移按两次算）。</para>
    /// </summary>
    public sealed class CloverFirstPersonCamera
    {
        /// <summary>日志 tag（引擎惯例：各能力用类名 / 模块名自报家门）。</summary>
        private const string Tag = "FpsCamera";

        // ─────────────────────────── 绑定 ───────────────────────────

        /// <summary>当前驱动中的相机；未绑定 = <c>null</c>（也可能是"曾绑定但已被销毁"）。</summary>
        public Camera Camera { get; private set; }

        /// <summary>是否已绑定可用的相机。</summary>
        public bool IsBound => Camera != null;

        /// <summary>眼位来源（角色根节点 / 眼睛节点）。<c>null</c> ⇒ 只更新朝向（见类注释第 6 条边界）。</summary>
        public Transform EyeAnchor { get; set; }

        /// <summary>眼位相对 <see cref="EyeAnchor"/> 的**局部**偏移（站姿眼高 / 蹲姿由业务改它）。</summary>
        public Vector3 EyeOffset = Vector3.zero;

        /// <summary>眼位平滑时间常数（秒）：<c>&gt;0</c> 时对眼位做帧率无关的一阶平滑（站↔蹲、上下台阶不跳变）；
        /// <c>&lt;=0</c>（默认）⇒ 直接吸附。首帧恒吸附（不从原点飞过来）。</summary>
        public float EyeSmoothTau;

        /// <summary>是否绑定过（用于区分"从未绑定"与"绑定的相机被销毁"两种留痕）。</summary>
        private bool _everBound;

        /// <summary>眼位是否已初始化（首帧吸附，避免从 <c>Vector3.zero</c> 平滑过来）。</summary>
        private bool _eyeInited;

        /// <summary>
        /// 用引擎相机管理器（<c>Game.Camera.Main</c>）绑定当前主相机 —— **推荐入口**：
        /// <c>Game.Camera.Main</c> 与跟随 / 震屏作用于同一台相机，且随"相机被销毁 / 被禁用"自动重抓
        /// （见 <c>Runtime/Presentation/Camera.cs:79</c> 的 <c>main.missing</c> 留痕与 <c>Camera.cs:89</c> 的
        /// <c>RefreshCamera</c>，契约在 <c>Runtime/Core/PresentationContracts.cs:472</c> 的 <c>Camera Main</c>）；
        /// 业务直接拿 <c>Camera.main</c> 会绕过门面，可能拿到另一台相机。
        /// </summary>
        /// <returns>绑定成功 = true；<c>Game.Camera</c> 未挂载 / <c>Main</c> 为 null（场景里没有 tag=MainCamera
        /// 且启用的相机）⇒ false 并限频 Warn（<c>Main</c> 自己也会留一条同类日志）。</returns>
        public bool Bind()
        {
            var manager = Game.Camera;
            var cam = manager != null ? manager.Main : null;
            if (cam == null)
            {
                LogThrottle.WarnThrottled(Tag, "fps.bind.nomain",
                    "Bind() 取不到主相机：Game.Camera 未挂载，或场景里没有 tag=MainCamera 且已启用的相机" +
                    "（进关卡前的空窗期会这样，调用方应稍后重试或改用 Bind(camera) 显式绑定）");
                return false;
            }

            return Bind(cam);
        }

        /// <summary>
        /// 显式绑定一台相机（自建相机的项目 / 离线宿主用）。
        /// 绑定会把眼位标记为"未初始化" ⇒ 下一次 <see cref="Tick"/> **吸附**到眼位（不从上一台相机的
        /// 位置或世界原点平滑飞过来），并把平滑 FOV 对齐到 <see cref="FovX"/>（避免首帧从 0 平滑上来）。
        /// </summary>
        /// <returns>传入 null ⇒ false + WarnOnce 留痕（只报一次：这是配置错误，不是高频分支）。</returns>
        public bool Bind(Camera cam)
        {
            if (cam == null)
            {
                LogThrottle.WarnOnce(Tag, "fps.bind.null",
                    "Bind(null)：空相机绑定被忽略（第一人称机位不会更新）");
                return false;
            }

            Camera = cam;
            _everBound = true;
            _eyeInited = false;
            _fovXCurrent = FovX;
            Game.Logger?.Info(Tag,
                $"第一人称相机已绑定：{cam.name}（EyeAnchor={(EyeAnchor != null ? EyeAnchor.name : "<未设置>")}，" +
                $"眼位偏移={EyeOffset}，水平FOV={FovX:F1}）；Tick(dt) 由业务逐帧驱动");
            return true;
        }

        /// <summary>解绑（回主菜单 / 交还画面给别的相机时用）：解绑后 <see cref="Tick"/> 不再写任何 Transform。</summary>
        public void Unbind()
        {
            if (Camera == null)
            {
                return;
            }

            Game.Logger?.Info(Tag, $"第一人称相机已解绑（原本驱动 {Camera.name}）");
            Camera = null;
        }

        // ─────────────────────────── 视角 ───────────────────────────

        /// <summary>
        /// yaw/pitch 累加器（引擎件）。业务可以读它的 <c>Yaw</c> / <c>Pitch</c> 出**不含**
        /// 后坐力 / 摇晃的"输入视角"（例如喂给移动方向解算）。
        /// </summary>
        public LookAccumulator Look { get; } = new LookAccumulator();

        /// <summary>
        /// 当前水平朝向（度，0 = +Z）。**含**后坐力 / 摇晃偏移 ⇒ 输入视角本身在 [0, 360)
        /// （<see cref="Look"/>.Yaw），叠加偏移后是任意值（<c>Quaternion.Euler</c> / 三角函数都按周期处理，无需再规范化）。
        /// </summary>
        public float Yaw => Look.Yaw + RecoilYaw + ShakeYaw;

        /// <summary>当前俯仰（度，+ = 抬头；已夹在 ±<see cref="PitchLimit"/> 内）。含后坐力 / 摇晃。</summary>
        public float Pitch => Mathf.Clamp(Look.Pitch + RecoilPitch + ShakePitch, -PitchLimit, PitchLimit);

        /// <summary>水平灵敏度（**度 / count**，不是倍率）：由业务把玩家设置乘上玩法换算系数后填入
        /// （示例口径 = 设置值 × 0.022°/count）。<c>&lt;=0</c> ⇒ 忽略水平位移。</summary>
        public float SensitivityX = 1f;

        /// <summary>垂直灵敏度（度 / count）：仅当 <see cref="SeparateAxes"/> = true 时生效。</summary>
        public float SensitivityY = 1f;

        /// <summary>水平 / 垂直灵敏度**分开**设置（false = 垂直跟随 <see cref="SensitivityX"/>）。</summary>
        public bool SeparateAxes;

        /// <summary>Y 轴反转（勾选后鼠标下移 = 视角向上）。语义与 <see cref="LookAccumulator"/> 的 <c>invertY</c> 一致。</summary>
        public bool InvertY;

        /// <summary>
        /// 俯仰限位（度，按绝对值使用）。**引擎只需保证不翻面**：视线与世界上方向共线（±90°）时朝向分解退化，
        /// 故默认留 1° 余量（±89°）。具体玩法口径（例如 ±80°）由业务覆盖 —— 引擎不替业务定手感值。
        /// </summary>
        public float PitchLimit = 89f;

        /// <summary>把视角**直接对齐**到给定角度（出生 / 接管 / 观战切换用，不做平滑）：yaw 走 360° 规范化，
        /// pitch 按 <see cref="PitchLimit"/> 夹紧。</summary>
        public void SetView(float yawDegrees, float pitchDegrees)
        {
            Look.Reset(yawDegrees, Mathf.Clamp(pitchDegrees, -PitchLimit, PitchLimit));
        }

        // ─────────────────────── 后坐力（表现层） ───────────────────────

        /// <summary>
        /// 后坐力**上跳**的跟随时间常数（秒，<c>&lt;=0</c> = 立即到位）。
        /// 权威值在模拟里，本类**只做表现跟随**（上跳用快的常数、回正用慢的常数 ⇒ "抬得快、落得慢"），
        /// ⛔ 不许再累加一份。
        /// </summary>
        public float RecoilRiseTau;

        /// <summary>后坐力**回正**的跟随时间常数（秒，<c>&lt;=0</c> = 立即到位）。</summary>
        public float RecoilFallTau;

        /// <summary>当前后坐力表现偏移（度）：俯仰（+ = 抬头）。</summary>
        public float RecoilPitch { get; private set; }

        /// <summary>当前后坐力表现偏移（度）：水平。</summary>
        public float RecoilYaw { get; private set; }

        private float _recoilPitchTarget;
        private float _recoilYawTarget;

        /// <summary>
        /// 喂入**权威**后坐力（度，模拟 / 武器表算出来的值）—— 本类不做累加、只按
        /// <see cref="RecoilRiseTau"/> / <see cref="RecoilFallTau"/> 平滑跟随（CS 里后坐力就是"准星被抬起"，
        /// <see cref="AimDirection"/> 必须跟着抬，子弹才跟着走）。每帧调用，传入当前权威值。
        /// </summary>
        public void SetRecoil(float pitchDegrees, float yawDegrees)
        {
            _recoilPitchTarget = pitchDegrees;
            _recoilYawTarget = yawDegrees;
        }

        /// <summary>立即清掉后坐力表现（换目标 / 重生 / 观战切换）。</summary>
        public void ClearRecoil()
        {
            _recoilPitchTarget = 0f;
            _recoilYawTarget = 0f;
            RecoilPitch = 0f;
            RecoilYaw = 0f;
        }

        // ─────────────────────── 摇晃（受击 / 爆炸） ───────────────────────

        /// <summary>
        /// 摇晃用的随机器（**必须由业务注入**，例如 <c>new Rng(matchSeed)</c>）：
        /// 注入后摇晃方向确定可复现（离线断言据此判衰减）。
        /// 未注入 ⇒ <see cref="AddShake"/> 被忽略并 WarnOnce 留痕（⛔ 引擎不用全局静态随机器兜底）。
        /// </summary>
        public Rng ShakeRng { get; set; }

        /// <summary>摇晃的横滚比例（<c>0</c> = 不产生横滚，只晃 pitch/yaw）：横滚幅度 = 该值 × 本帧幅度。</summary>
        public float ShakeRollScale;

        /// <summary>当前摇晃幅度（度，随剩余时间线性衰减到 0）。</summary>
        public float ShakeAmplitude { get; private set; }

        /// <summary>本帧摇晃产生的俯仰偏移（度）。</summary>
        public float ShakePitch { get; private set; }

        /// <summary>本帧摇晃产生的水平偏移（度）。</summary>
        public float ShakeYaw { get; private set; }

        /// <summary>本帧摇晃产生的横滚（度）；只有 <see cref="ShakeRollScale"/> ≠ 0 时才非 0。</summary>
        public float ShakeRoll { get; private set; }

        private float _shakeDirPitch;
        private float _shakeDirYaw;
        private float _shakeDirRoll;
        private float _shakeAmplitudeFull;
        private float _shakeDuration;
        private float _shakeLeft;

        /// <summary>
        /// 注入一次摇晃（受击 / 爆炸 / 落地）：三轴各取一个随机方向（<c>Range(-1, 1)</c>）再乘幅度
        /// （幅度由调用方按伤害归一后算出）；随机源是**注入的**
        /// <see cref="ShakeRng"/>（引擎禁用全局静态随机器），幅度 / 时长全部由业务传入（⛔ 引擎不内置）。
        /// </summary>
        /// <param name="amplitudeDegrees">起始幅度（度，<c>&gt;0</c>）。</param>
        /// <param name="durationSeconds">持续时长（秒，<c>&gt;0</c>）：期间幅度**线性**衰减到 0。</param>
        /// <returns>是否真的注入了摇晃（参数非法 / 未注入随机器 ⇒ false，且留痕）。</returns>
        public bool AddShake(float amplitudeDegrees, float durationSeconds)
        {
            if (amplitudeDegrees <= 0f || durationSeconds <= 0f)
            {
                LogThrottle.WarnThrottled(Tag, "fps.shake.badargs",
                    $"AddShake(amplitude={amplitudeDegrees:F3}, duration={durationSeconds:F3}) 参数非正，已忽略" +
                    "（幅度 / 时长由业务传入，引擎不内置默认值）");
                return false;
            }

            var rng = ShakeRng;
            if (rng == null)
            {
                LogThrottle.WarnOnce(Tag, "fps.shake.rng",
                    "未注入 ShakeRng，摇晃被忽略：请给 rig.ShakeRng 赋一个 Rng（引擎禁用全局静态随机器，" +
                    "注入后摇晃方向可复现）");
                return false;
            }

            _shakeDirPitch = rng.Range(-1f, 1f);
            _shakeDirYaw = rng.Range(-1f, 1f);
            _shakeDirRoll = rng.Range(-1f, 1f);
            _shakeAmplitudeFull = amplitudeDegrees;
            _shakeDuration = durationSeconds;
            _shakeLeft = durationSeconds;
            return true;
        }

        /// <summary>立即结束摇晃（重生 / 换关）。</summary>
        public void ClearShake()
        {
            _shakeLeft = 0f;
            _shakeAmplitudeFull = 0f;
            ShakeAmplitude = 0f;
            ShakePitch = 0f;
            ShakeYaw = 0f;
            ShakeRoll = 0f;
        }

        // ─────────────────────── 视点晃动的注入缝 ───────────────────────

        /// <summary>
        /// 业务每帧塞进来的**相机局部空间**视点偏移（典型 = 引擎件 <see cref="ViewBob"/>.Offset，x = 右、y = 上）：
        /// 偏移先转进视线空间再加到眼位，这样"左右晃"不会让机位方向算错。
        /// <para>⚠️ 偏移**不影响** <see cref="AimDirection"/> 与 <see cref="EyePosition"/> ——
        /// 子弹不跟着脚步摆（射线由调用方用不含 bob 的位姿发出）。</para>
        /// </summary>
        public Vector3 ViewOffset;

        /// <summary>业务每帧塞进来的视点横滚（度，典型 = <see cref="ViewBob"/>.Roll）；与本帧摇晃横滚相加。</summary>
        public float ViewRoll;

        // ─────────────────────── 眼位 / FOV / 输出 ───────────────────────

        /// <summary>
        /// **水平** FOV（度，原版口径）：Unity 的 <c>Camera.fieldOfView</c> 是**垂直** FOV，
        /// 每帧按当前宽高比经 <see cref="CameraMath.FovYFromFovX"/> 换算下发。开镜时业务改这个值即可。
        /// </summary>
        public float FovX = 90f;

        /// <summary>水平 FOV 的平滑时间常数（秒）：开镜 / 收镜过渡用；<c>&lt;=0</c>（默认）⇒ 立即到位。</summary>
        public float FovSmoothTau;

        /// <summary>本帧实际下发给 Unity 的**垂直** FOV（度）；未绑定时为 0。</summary>
        public float VerticalFieldOfView { get; private set; }

        /// <summary>本帧平滑后的水平 FOV（度）。</summary>
        public float FovXCurrent { get; private set; }

        private float _fovXCurrent;

        /// <summary>本帧射线起点（眼位，**不含** <see cref="ViewOffset"/> 与摇晃）。</summary>
        public Vector3 EyePosition { get; private set; }

        /// <summary>
        /// 本帧视线方向（含后坐力 / 摇晃，与相机朝向一致）：
        /// <c>(cos(pitch)·sin(yaw), sin(pitch), cos(pitch)·cos(yaw))</c>（<see cref="CameraMath.AimDirection"/>）。
        /// </summary>
        public Vector3 AimDirection { get; private set; } = Vector3.forward;

        // ─────────────────────── 输入开关 / 光标锁 ───────────────────────

        /// <summary>本 rig 是否读鼠标（false ⇒ 只不读输入，机位仍会跟随）。默认 true。</summary>
        public bool ControlEnabled { get; private set; } = true;

        /// <summary>当前这把"输入锁"是否为**本 rig**加的（只解自己加的锁，避免把别人的锁解掉）。</summary>
        public bool InputLockedByRig { get; private set; }

        /// <summary>
        /// 开关本 rig 的**鼠标读取**（弹窗 / 死亡 / 过场时关掉）。语义对照
        /// <see cref="CloverThirdPersonCamera.SetControlEnabled"/>：**只**影响输入读取，不影响机位跟随。
        /// <para>⚠️ 它**不**去动引擎的全局输入锁 —— <c>IInputManager.Lock()</c> 会让**所有**读取返回默认值
        /// （含移动 / 技能 / 交互，见 <c>Runtime/Presentation/Input.cs:780-801</c>），那是业务级的暂停语义，
        /// 相机 rig 擅自锁它会把角色一起冻住。要锁光标 / 输入请显式调 <see cref="LockInput"/> /
        /// <see cref="UnlockInput"/>。</para>
        /// </summary>
        public void SetControlEnabled(bool on)
        {
            if (ControlEnabled == on)
            {
                return;
            }

            ControlEnabled = on;
            Game.Logger?.Info(Tag, on
                ? "第一人称相机恢复响应鼠标（机位一直由 Tick 驱动）"
                : "第一人称相机暂停响应鼠标（机位仍由 Tick 驱动）");
        }

        /// <summary>
        /// **锁定引擎输入**（光标锁钩子）：开菜单 / 暂停 / 结算遮罩时调；回到游戏画面时调
        /// <see cref="UnlockInput"/>。
        /// <para><b>为什么这个钩子在这里</b>：引擎的 <c>IInputManager.Lock</c> / <c>Unlock</c>
        /// （<c>Runtime/Presentation/Input.cs:746-757</c>）在本件之前**没有任何调用方** ——
        /// 而"第一人称开菜单要停键鼠、回画面要恢复"正是它存在的场景，故由本 rig 接上，
        /// 并跟踪"这把锁是不是自己加的"（只解自己的锁）。</para>
        /// <para>⚠️ <b>语义提醒（别接反）</b>：<c>Lock</c> 是**全局输入闸门** —— 锁定期间所有读取返回默认值
        /// （含移动 / 技能 / **视角**，见 <c>Runtime/Presentation/Input.cs:780-801</c>），所以它对应的是
        /// "暂停 / 交给 UI"，**不是** FPS 的"锁住光标但继续转视角"。后者在本引擎里由业务自己设
        /// <c>UnityEngine.Cursor.lockState / visible</c> 实现（引擎输入门面不碰鼠标可见性，也不替业务接线）。</para>
        /// </summary>
        /// <returns>是否真的加锁（引擎未启动 / 已锁 / 本 rig 已持有 ⇒ false）。</returns>
        public bool LockInput()
        {
            var input = Game.Input;
            if (input == null)
            {
                LogThrottle.WarnOnce(Tag, "fps.lock.noinput",
                    "LockInput()：Game.Input 未挂载（引擎未 Launch 或输入模块未挂接），忽略");
                return false;
            }

            if (InputLockedByRig || input.IsLocked)
            {
                return false;
            }

            input.Lock();
            InputLockedByRig = true;
            Game.Logger?.Info(Tag, "已锁定输入（光标 / 键鼠交给本 rig 的持有者，解锁请调 UnlockInput()）");
            return true;
        }

        /// <summary>解锁引擎输入（回到游戏画面 / 重新操作时调）。只解**本 rig 加的**那把锁（⛔ 不解别人的）。</summary>
        /// <returns>是否真的解锁。</returns>
        public bool UnlockInput()
        {
            var input = Game.Input;
            if (input == null)
            {
                LogThrottle.WarnOnce(Tag, "fps.lock.noinput",
                    "UnlockInput()：Game.Input 未挂载，忽略");
                return false;
            }

            if (!InputLockedByRig)
            {
                // 非预期分支：没加锁却来解 —— 可能是"锁被别人解了 / 解了两次"（不解别人的锁）。
                LogThrottle.WarnThrottled(Tag, "fps.unlock.notheld",
                    "UnlockInput()：本 rig 当前没有持有输入锁（可能已解过，或锁是别人加的），已忽略" +
                    "（避免把别人的锁解掉）");
                return false;
            }

            input.Unlock();
            InputLockedByRig = false;
            Game.Logger?.Info(Tag, "已解锁输入（键鼠交还；解锁时引擎会重置鼠标位移基准，避免视角突跳）");
            return true;
        }

        // ─────────────────────────── 每帧驱动 ───────────────────────────

        /// <summary>
        /// 推进一帧：读鼠标 → 累加视角 → 跟随后坐力 → 衰减摇晃 → 算眼位与朝向 → 写相机 Transform 与
        /// <c>fieldOfView</c>。**帧步长由调用方注入**（不读 <c>Time.deltaTime</c>）。
        /// </summary>
        /// <param name="dt">帧间隔（秒）；<c>&lt;=0</c>（暂停 / 首帧）⇒ 不推进任何状态。</param>
        public void Tick(float dt)
        {
            if (dt <= 0f)
            {
                // 暂停 / 首帧（以及 timeScale=0 的帧）都会走到这里：不推进任何状态，限频留痕。
                LogThrottle.WarnThrottled(Tag, "fps.dt",
                    $"Tick(dt={dt:F4})：dt<=0（暂停 / 首帧），本帧不推进视角与机位");
                return;
            }

            ReadLookInput();

            // pitch 到端点：夹取发生在 LookAccumulator 内（防翻面），这里只按**计数口径**留痕
            // （首次必打 + 每 50 次一条）—— 顶着限位看天/看地是正常操作，不能按"一次一条"报。
            if (Mathf.Abs(Look.Pitch) >= PitchLimit - 0.0001f)
            {
                LogThrottle.InfoCounted(Tag, "fps.pitch.limit",
                    $"俯仰已到端点（Pitch={Look.Pitch:F2}°，限位 ±{PitchLimit:F2}°），已夹住不再增大");
            }

            TickRecoil(dt);
            TickShake(dt);
            TickFov(dt);

            var rot = Quaternion.Euler(-Pitch, Yaw, ViewRoll + ShakeRoll);
            AimDirection = CameraMath.AimDirection(Yaw, Pitch);
            EyePosition = ResolveEyePosition(dt);

            ApplyToCamera(rot);
        }

        /// <summary>
        /// 把本帧位姿下发给相机。相机不可用（未绑定 / 被销毁 / 被禁用）⇒ **只跳过下发**并留痕：
        /// 上面的视角 / 后坐力 / 摇晃状态照常推进，复绑后立刻可用（"相机没了"是表现层故障，不是逻辑故障）。
        /// </summary>
        private void ApplyToCamera(Quaternion rotation)
        {
            var cam = Camera;
            if (cam == null)
            {
                if (_everBound)
                {
                    // 已绑定过、现在拿不到（被销毁）：Unity 的 == null 会覆盖"已销毁"这一情形。
                    LogThrottle.WarnThrottled(Tag, "fps.camera.gone",
                        "绑定的相机已被销毁 / 置空，本帧不下发位姿；切场景后请重新 Bind()");
                }
                else
                {
                    LogThrottle.WarnOnce(Tag, "fps.camera.unbound",
                        "尚未绑定相机：第一人称机位一帧都没写过 —— 调 Bind()（走 Game.Camera.Main）或 Bind(camera)");
                }

                return;
            }

            if (!cam.isActiveAndEnabled)
            {
                // 相机被禁用时写 Transform 是"看不见的驱动"（表现为画面不动却不报错）⇒ 明确跳过 + 留痕。
                LogThrottle.WarnThrottled(Tag, "fps.camera.disabled",
                    $"绑定的相机 {cam.name} 已禁用（isActiveAndEnabled=false），本帧不下发位姿");
                return;
            }

            // Unity 的 fieldOfView 是**垂直** FOV，而 FovX 是原版口径的**水平** FOV ⇒ 每帧按当前宽高比换算
            // （CameraMath.FovYFromFovX 的退化分支：aspect<=0 原样返回水平值）。
            VerticalFieldOfView = cam.aspect > 0f
                ? CameraMath.FovYFromFovX(FovXCurrent, cam.aspect)
                : FovXCurrent;

            // ViewOffset 是**相机局部空间**偏移：先转进视线空间再加到眼位。
            cam.transform.SetPositionAndRotation(EyePosition + rotation * ViewOffset, rotation);
            cam.fieldOfView = VerticalFieldOfView;
        }

        /// <summary>
        /// 读鼠标位移并累加视角。走引擎 <c>Game.Input</c>（后端无关：旧输入 / 新输入都能用；直接读
        /// <c>UnityEngine.Input</c> 的写法在只勾了新后端的工程里会抛异常，见 <c>Runtime/Presentation/Input.cs:56</c>）。
        /// 输入被锁 / 引擎未启动 / 本 rig 已停用 ⇒ **只跳过输入**，机位仍由 <see cref="Tick"/> 驱动。
        /// </summary>
        private void ReadLookInput()
        {
            if (!ControlEnabled)
            {
                return;
            }

            var input = Game.Input;
            if (input == null || input.IsLocked)
            {
                return;
            }

            var delta = input.MouseDelta;
            if (delta.x == 0f && delta.y == 0f)
            {
                return;
            }

            ApplyLookDelta(delta.x, delta.y);
        }

        /// <summary>
        /// 把一次**原始鼠标位移**按当前灵敏度 / 反转 / 限位累加进视角 —— 设备无关的公开缝：
        /// <list type="bullet">
        /// <item><see cref="Tick"/> 从 <c>Game.Input</c> 读到位移后走这里（唯一实现，⛔ 不复制第二份）；</item>
        /// <item>业务可据此做**回放 / 演示 / 观战接管**（把录制下来的位移原样喂进来）；</item>
        /// <item>离线断言也走这里（不需要真的接上输入后端）。</item>
        /// </list>
        /// <para>分轴灵敏度：<see cref="LookAccumulator"/>.Add 用**同一个** <c>degreesPerCount</c> 驱动两轴，
        /// 而"另一轴传 0 位移"时它的 <c>Repeat</c> / <c>Clamp</c> 加的是 0（状态不变）
        /// ⇒ 两次调用等效于分轴灵敏度，且 yaw/pitch 的夹紧口径仍只有 <see cref="LookAccumulator"/> 一处。</para>
        /// </summary>
        /// <param name="deltaX">本帧鼠标水平位移（count / 像素）。</param>
        /// <param name="deltaY">本帧鼠标垂直位移（count / 像素，屏幕坐标：向下为正）。</param>
        public void ApplyLookDelta(float deltaX, float deltaY)
        {
            var sensX = SensitivityX;
            var sensY = SeparateAxes ? SensitivityY : SensitivityX;

            if (sensX > 0f)
            {
                Look.Add(deltaX, 0f, sensX, false, PitchLimit);
            }
            else if (deltaX != 0f)
            {
                LogThrottle.WarnThrottled(Tag, "fps.sens.x",
                    $"水平灵敏度 <=0（{sensX:F4} 度/count）：水平位移已忽略（视角不转）");
            }

            if (sensY > 0f)
            {
                Look.Add(0f, deltaY, sensY, InvertY, PitchLimit);
            }
            else if (deltaY != 0f)
            {
                LogThrottle.WarnThrottled(Tag, "fps.sens.y",
                    $"垂直灵敏度 <=0（{sensY:F4} 度/count）：垂直位移已忽略（俯仰不动）");
            }
        }

        /// <summary>
        /// 后坐力的**表现跟随**：只跟随 <see cref="SetRecoil"/> 喂进来的权威值，不累加。
        /// 挑时间常数的判据（<c>|目标| &gt; |当前|</c> = 正在上跳 ⇒ 用上升常数，否则用回正常数）
        /// 按 <see cref="CameraMath.Follow(float,float,float,float)"/> 的类注释就地内联 ——
        /// 引擎只保留一条跟随实现，⛔ 不另起第二套。
        /// </summary>
        private void TickRecoil(float dt)
        {
            var targetPitch = _recoilPitchTarget;
            var targetYaw = _recoilYawTarget;

            RecoilPitch = CameraMath.Follow(RecoilPitch, targetPitch, dt,
                Mathf.Abs(targetPitch) > Mathf.Abs(RecoilPitch) ? RecoilRiseTau : RecoilFallTau);
            RecoilYaw = CameraMath.Follow(RecoilYaw, targetYaw, dt,
                Mathf.Abs(targetYaw) > Mathf.Abs(RecoilYaw) ? RecoilRiseTau : RecoilFallTau);
        }

        /// <summary>摇晃衰减：幅度随剩余时间**线性**归零（<see cref="AddShake"/> 的 out 时长到了就彻底停）。</summary>
        private void TickShake(float dt)
        {
            if (_shakeLeft <= 0f)
            {
                ShakeAmplitude = 0f;
                ShakePitch = 0f;
                ShakeYaw = 0f;
                ShakeRoll = 0f;
                return;
            }

            _shakeLeft -= dt;
            var k = _shakeLeft > 0f ? _shakeLeft / _shakeDuration : 0f;
            ShakeAmplitude = _shakeAmplitudeFull * k;
            ShakePitch = _shakeDirPitch * ShakeAmplitude;
            ShakeYaw = _shakeDirYaw * ShakeAmplitude;
            ShakeRoll = _shakeDirRoll * ShakeAmplitude * ShakeRollScale;
        }

        /// <summary>水平 FOV 的平滑（开镜 / 收镜过渡）；<see cref="FovSmoothTau"/> <c>&lt;=0</c> ⇒ 直接吸附。</summary>
        private void TickFov(float dt)
        {
            _fovXCurrent = FovSmoothTau > 0f
                ? CameraMath.Follow(_fovXCurrent, FovX, dt, FovSmoothTau)
                : FovX;
            FovXCurrent = _fovXCurrent;
        }

        /// <summary>
        /// 本帧眼位 = <see cref="EyeAnchor"/>.TransformPoint(<see cref="EyeOffset"/>)，按
        /// <see cref="EyeSmoothTau"/> 做帧率无关平滑（首帧吸附）。未设置眼位来源 ⇒ 机位保持不动
        /// （**不能**退回 Vector3.zero：那会把相机丢到世界原点）。
        /// </summary>
        private Vector3 ResolveEyePosition(float dt)
        {
            var anchor = EyeAnchor;
            if (anchor == null)
            {
                LogThrottle.WarnThrottled(Tag, "fps.eye.missing",
                    "未设置 EyeAnchor（眼位来源），本帧机位保持不动、只更新朝向");
                return Camera != null ? Camera.transform.position : Vector3.zero;
            }

            var want = anchor.TransformPoint(EyeOffset);
            if (!_eyeInited)
            {
                _eyeInited = true;
                EyePosition = want;
                return want;
            }

            return EyeSmoothTau > 0f
                ? CameraMath.Follow(EyePosition, want, dt, EyeSmoothTau)
                : want;
        }
    }
}
