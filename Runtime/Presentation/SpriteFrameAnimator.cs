// ─────────────────────────────────────────────────────────────────────────────
// CloverEngine · Runtime/Presentation/SpriteFrameAnimator.cs
// 轻量逐帧动画器：帧表（Sprite[]）→ SpriteRenderer，**由业务 Tick 驱动**。通用底座。
//
// 为什么要有本件：手写的「计时 → 换帧」在多处重复（同一形状的写法散落各处，节奏常量也在调用方手里）——
//   `_animTimer += dt; if (_animTimer >= 0.11f) { _animTimer = 0f; _frame = (_frame + 1) % _frames.Count;
//    sr.sprite = _frames[_frame]; }`：
//   ⇒ 本件：帧表 + fps 给一次，切帧只有一份实现。
//
// ★ 为什么**不**用 AnimatorController（引擎现成的 `IAnimationManager`）：
//   · `IAnimationManager.CreateAnimator(GameObject, RuntimeAnimatorController)`
//     （Runtime/Core/PresentationContracts.cs:277-285）落地的是一颗 `Animator` + 编辑器里编好的
//     controller 资产（Runtime/Presentation/Animation.cs:14-23：`animator.runtimeAnimatorController = controller`），
//     它回答的是"播哪个 state / 设哪个参数"（`IAnimPlayer.Play(stateName)`）——**要**在编辑器里建 State/Clip；
//   · 平台游戏要的是「帧列表直接切 `SpriteRenderer.sprite`」：每套动画的节奏就是**一个 fps**，
//     没有状态机、没有混合、没有 Avatar。为 4 张图建 controller + clip
//     是纯负担；而帧序列本来就来自图集/条带（`Game.Res.LoadAll<Sprite>(path)`，
//     Runtime/Core/Contracts.cs:1107-1124 —— 逐个帧名 LoadAsset 取不到），天然是"一组 Sprite"。
//   · 所以本类**不引入** Animator / AnimatorController：Sprite 数组进，SpriteRenderer 出。
//
// ★ 不依赖 Addressables（引擎 Addressables 后端**尚未接入**：资源只走 `Game.Res` 的
//   Resources / AssetBundle 后端，另有 `CloverPresentation.PanelProvider` 这个可插拔点，
//   见 Runtime/Presentation/CloverPresentation.cs:60-67）—— 本类**不加载任何资源**，
//   帧表由调用方给（谁加载谁 Release）。
//
// ★ 驱动方式：只提供 `Advance(float dt)` —— ⛔ 不起协程、不加 Update/Tick、不注册任何回调，
//   与引擎既有 Tick 风格一致（`IAnimationManager.Tick(dt)`、各模块 `Tick(dt)`），
//   由业务在自己的 Tick 里调用 ⇒ 暂停 / 倍速 / 逐帧调试都自然可用，且可与逻辑同一时间轴。
//
// 容错契约（每条都有对应 EditMode 测试）：
//   · 空帧表（null / 长度 0）⇒ 不抛、不播，**降频 Warn 一条**（调用方据此查"资源没加载成功"）；
//   · `fps <= 0` / NaN / ±Inf ⇒ 按 1 fps 处理，降频 Warn 一条（不除零、不出 NaN）；
//     ⚠️ 这条只适用于**旧签名** `Play(Sprite[], float, bool)` / `PlayOnce(Sprite[], float, Action)`
//     —— 它们的语义被既有调用方与 EditMode 测试钉着（`InvalidFps_FallsBackToOne`），⛔ 不许改。
//     下面三条新语义只落在**新增重载**上。
//   · 运行中换帧表（重播）⇒ 下标与累计时间**归零**（不会拿旧表的下标去索引新表）；
//   · `target` 允许为 null（离线宿主 / EditMode 测试只推进状态，不写 SpriteRenderer）；
//   · `PlayOnce` 的完成回调抛异常 ⇒ 接住并 Error 留痕，**不打断**调用方的 Tick 链；回调只触发一次；
//   · 单次 `Advance` 推进步数有上限（防"超大 dt / 长时间没 Tick"把一帧卡成死循环）。
//
// ★ 三条语义（**只加在重载上，旧签名语义逐字不变**）：
//   ① **`fps == 0` = 静止帧（停播、停在某帧）**，不是"1 fps 慢慢抖"。
//      （idle 档用 `0f` 帧率 ⇒ 单条静止姿态帧，按 0 播才是对的）。
//      ⇒ `Play(frames, indices, fps, loop)` / `PlayStill(frames, index)` 按这条；旧 `Play(frames, fps)`
//      仍是"非法 fps ⇒ 1 fps + 降频 Warn"（那是既有契约，见上）。
//   ② **帧段子集 / 多区间**：一档动画的帧往往**不是连续区间**（例：`attack` =
//      帧 `182-230 ∪ 247-251`），只喂连续 `Sprite[]` 就得每次换档现切一份数组，且容易把别的动作的帧夹进来。
//      ⇒ 新增 `Play(frames, int[] indices, …)`（下标集合，顺序即播放顺序）+
//      静态 `ExpandRuns(runs, frameCount, out error)`（把 `[起始, 长度, …]` 的段表展开成下标集合）。
//   ③ **换档滞回** `<see cref="SpriteFrameAnimator.SwitchTo"/>`：同档不重播；当前档是"播一次且未播完"时
//      扣住不让位（除非 `force`）。服务端的 `anim` 是**瞬时**的
//      （一次挥砍只在那一 tick 置 attack，下一 tick 就回 walk），若每 tick 都照单全收，
//      表现就是"攻击 1 帧 → 走路从头重来"的单位**抽搐**。
// ─────────────────────────────────────────────────────────────────────────────

using System;
using UnityEngine;

namespace CloverEngine
{
    /// <summary>
    /// 逐帧动画器：把 <c>Sprite[]</c> 按 fps 逐帧写到 <see cref="SpriteRenderer"/>。
    /// <para>
    /// **无 MonoBehaviour、无协程**：状态推进只经 <see cref="Advance"/>，由业务 Tick 调用
    /// （口径见文件头「驱动方式」）。
    /// </para>
    /// <para>
    /// 一个实例服务"一条 Sprite 序列"（一次 <see cref="Play"/> 绑定一张帧表）；同一渲染器上换动作
    /// 就是再调一次 <see cref="Play"/>（帧表与下标一起重置）。
    /// </para>
    /// </summary>
    public sealed class SpriteFrameAnimator
    {
        private const string Tag = "SpriteFrameAnimator";

        /// <summary>`fps` 非法时的兜底帧率（= 1 帧/秒：慢但可诊断，⛔ 不取 0 以免除零）。</summary>
        private const float MinFps = 1f;

        /// <summary>单次 <see cref="Advance"/> 的推进步数上限（超大 dt 的保护，见 <see cref="Advance"/>）。</summary>
        private const int MaxAdvanceSteps = 240;

        private readonly SpriteRenderer _target;

        private Sprite[] _frames;
        /// <summary>
        /// 当前档位的**帧下标子集**（多区间/乱序均支持），顺序 = 播放顺序；<c>null</c> = 整张帧表按序播。
        /// <para>⛔ 传入的数组按**只读**对待（见 <see cref="Play(Sprite[], int[], float, bool)"/>）：
        /// 本类不克隆它（换档是热路径，每次克隆一份几十元素的数组会持续产生 GC）。</para>
        /// <para>与调用方传入的那个数组**可能不是同一个引用** —— 只有切片里有越界下标时本类才会另建一份
        /// 收敛过的副本（见 <see cref="Begin"/>）。比"是不是同一个档"要用 <see cref="_clipSource"/>。</para>
        /// </summary>
        private int[] _clip;

        /// <summary>调用方**原始传入**的切片引用（只用于 <see cref="SwitchTo"/> 的"同档"比较，不参与取帧）。</summary>
        private int[] _clipSource;

        private float _frameDuration = 1f;
        private float _accum;
        private int _index;
        private bool _playing;
        private Action _onComplete;

        /// <summary>
        /// 建一个动画器。<paramref name="target"/> **允许为 null** —— 此时只推进内部状态、
        /// 不写任何 <see cref="SpriteRenderer"/>（离线宿主与 EditMode 测试用；⛔ 不会因此报错刷屏）。
        /// </summary>
        public SpriteFrameAnimator(SpriteRenderer target)
        {
            _target = target;
        }

        /// <summary>目标渲染器（构造时给定的那个，可能为 null）。</summary>
        public SpriteRenderer Target => _target;

        /// <summary>当前帧表（未 <see cref="Play"/> 过 / 空帧表时为 null）。</summary>
        public Sprite[] Frames => _frames;

        /// <summary>
        /// 当前档位的帧下标子集（= 播放顺序）；**未用帧切片时为 <c>null</c>**（= 整表按序播）。
        /// ⛔ 只读视图：调用方不要改它。
        /// </summary>
        public int[] ClipIndices => _clip;

        /// <summary>
        /// 当前**档位**的帧数（用了帧切片时 = 切片长度，否则 = 帧表长度）。0 = 没帧可放。
        /// <para>未用帧切片的调用方看到的值与本次改动前**逐字一致**（= 帧表长度）。</para>
        /// </summary>
        public int ClipLength => _clip != null && _clip.Length > 0
            ? _clip.Length
            : (_frames?.Length ?? 0);

        /// <summary>当前生效的帧率（<see cref="Play"/> 时归一化后的值；未播放过为 0）。「静止帧」档恒为 0。</summary>
        public float Fps { get; private set; }

        /// <summary>当前是否循环播放（<see cref="PlayOnce"/> ⇒ false）。</summary>
        public bool Loop { get; private set; }

        /// <summary>当前**档位**帧数（0 = 没帧可放）。未用帧切片时 = 帧表长度（与改动前一致）。</summary>
        public int FrameCount => ClipLength;

        /// <summary>
        /// 当前档位内的位置下标（0 起；空帧表时恒 0）。
        /// <para>未用帧切片时它**就是**帧数组下标（与改动前一致）；用了切片时它是"档位位置"，
        /// 对应的帧下标是 <c>ClipIndices[FrameIndex]</c>。</para>
        /// </summary>
        public int FrameIndex => _index;

        /// <summary>是否正在播放。<see cref="PlayOnce"/> 播到最后一帧后自动变 false（且停在最后一帧）。</summary>
        public bool IsPlaying => _playing;

        /// <summary>
        /// 循环播放（**旧签名，语义逐字不变**）。
        /// <para>空帧表 ⇒ 不播 + 降频 Warn；<paramref name="fps"/> &lt;= 0（或 NaN/Inf）⇒ 按 1 fps + 降频 Warn。</para>
        /// <para>重播 / 换表一律**从第 0 帧重新开始**（累计时间归零），并立刻把第 0 帧推到渲染器
        /// （否则首帧前会空窗一个帧周期）。</para>
        /// <para>⚠️ "`fps == 0` = 静止帧（停播）"的新语义**不在**这里（旧签名被既有调用方与 EditMode
        /// 测试钉着）⇒ 要静止帧用 <see cref="Play(Sprite[], int[], float, bool)"/>（<c>fps = 0</c>）
        /// 或 <see cref="PlayStill"/>。</para>
        /// </summary>
        public void Play(Sprite[] frames, float fps, bool loop = true) => Begin(frames, null, fps, loop, null, false);

        /// <summary>
        /// 播放一次（到最后一帧即停，**停在最后一帧**，<see cref="IsPlaying"/> 变 false）。
        /// </summary>
        /// <param name="onComplete">播完回调（可为 null）。**只触发一次**；回调里抛异常会被接住并留下 Error，
        /// 不会打断调用方的 Tick 链。</param>
        public void PlayOnce(Sprite[] frames, float fps, Action onComplete = null) =>
            Begin(frames, null, fps, false, onComplete, false);

        /// <summary>
        /// 循环播放**帧段子集**（可多区间、可乱序；顺序 = <paramref name="indices"/> 的顺序）。
        /// <para>
        /// <b>为什么需要</b>：一档动画的帧常常**不是连续区间**（例：`chr_archer` 的 attack =
        /// 帧 <c>182-230 ∪ 247-251</c>）—— 只喂连续区间会把别的动作的帧夹进来。段表 →
        /// 下标集合用 <see cref="ExpandRuns"/>。
        /// </para>
        /// <para>
        /// <b>本重载的 <c>fps</c> 语义与旧签名不同</b>：<c>fps == 0</c> = **静止帧**（停在切片首帧、不播、
        /// 不告警 —— 那是合法取值，例：原版 idle 就是单条静止姿态帧）；而 <c>NaN / ±Inf / 负数</c>
        /// 仍按 1 fps + 降频 Warn（那些才是写错）。
        /// </para>
        /// <para>
        /// <paramref name="indices"/> 为 <c>null</c> / 长度 0 ⇒ 等价整表按序播（+ 降频 Warn 一次，
        /// 因为"空切片"多半是帧段解出来是空）。
        /// </para>
        /// <para>
        /// ⛔ <b>传入的数组按只读对待</b>：本类**不克隆**（换档是热路径）。调用方请把每个档位的切片
        /// 缓存起来复用（按"目录 × 档位 × 视角"缓存 `<c>_clip</c>`），不要每次换档现造。
        /// </para>
        /// </summary>
        /// <param name="frames">整张帧表（切片的元素下标指向它）。</param>
        /// <param name="indices">帧下标切片；<c>null</c> / 空 = 整表。</param>
        /// <param name="fps">帧率；<c>0</c> = 静止帧（不播）。</param>
        /// <param name="loop">是否循环。</param>
        public void Play(Sprite[] frames, int[] indices, float fps, bool loop = true) =>
            Begin(frames, indices, fps, loop, null, true);

        /// <summary>
        /// 播放一次**帧段子集**（到切片末帧即停、停在末帧）；<c>fps == 0</c> = 静止帧。
        /// 其余语义见 <see cref="Play(Sprite[], int[], float, bool)"/>。
        /// </summary>
        /// <param name="onComplete">播完回调（可为 null，只触发一次，异常被接住）。</param>
        public void PlayOnce(Sprite[] frames, int[] indices, float fps, Action onComplete = null) =>
            Begin(frames, indices, fps, false, onComplete, true);

        /// <summary>
        /// 摆一个**静止帧**（<c>fps = 0</c> 的等价入口，语义最直白）：显示第
        /// <paramref name="index"/> 帧并停住，<see cref="Advance"/> 不再推进。
        /// <para>原版 idle 档是单条静止姿态帧（用 <c>0f</c> 帧率 ⇒ 停在首帧）。</para>
        /// </summary>
        /// <param name="frames">帧表。</param>
        /// <param name="index">要停在第几帧（越界会被夹到 [0, 帧数-1]）。</param>
        public void PlayStill(Sprite[] frames, int index = 0)
        {
            Begin(frames, null, 0f, true, null, true);
            if (ClipLength > 0) SetFrame(index);
        }

        /// <summary>
        /// **换档**（带滞回）—— 把 <see cref="Play(Sprite[], int[], float, bool)"/> 与滞回规则合成一步。
        /// <para>三条规则：</para>
        /// <list type="number">
        /// <item>请求档 == 当前档（帧表引用 / 切片引用 / fps / loop 全同）⇒ **什么都不做**（⛔ 绝不从第 0 帧重播）；</item>
        /// <item>当前档是"**播一次且还没播完**"（<see cref="Loop"/> = false 且 <see cref="IsPlaying"/> = true）
        ///   ⇒ **扣住不放**（返回 false），除非 <paramref name="force"/> = true；</item>
        /// <item>其余情况正常换档（换档当帧立刻贴新档首帧，累计时间归零）。</item>
        /// </list>
        /// <para>
        /// 为什么要它：服务端的动作字段常常是**瞬时**的（一次挥砍只在那一 tick 置 attack、下一 tick 就回 walk）。
        /// 调用方若每 tick 照单全收地 <see cref="Play"/>，表现就是"攻击 1 帧 → 走路从第 0 帧重来"＝ 原地抽搐。
        /// </para>
        /// <para>
        /// ⛔ 本方法**不猜"死亡/不可打断"这类业务语义**：要无条件换档（例如死亡必须立刻播）就传
        /// <paramref name="force"/> = true，判据留在业务侧（引擎不含项目档位枚举）。
        /// </para>
        /// </summary>
        /// <returns>是否真的换了档（false = 被滞回规则挡下 / 参数与当前档相同）。</returns>
        public bool SwitchTo(Sprite[] frames, int[] indices, float fps, bool loop = true, bool force = false)
        {
            // 「同档」= 帧表引用 + 切片引用 + 生效帧率 + 循环策略全同（按引用比，不比内容 ——
            // 比内容要逐元素扫，且换档是热路径）。调用方把每个档位的切片缓存起来复用即可。
            var requestedClip = indices != null && indices.Length > 0 ? indices : null;
            if (ReferenceEquals(_frames, frames) && ReferenceEquals(_clipSource, requestedClip) &&
                Fps == NormalizeFpsForCompare(fps) && Loop == loop)
            {
                return false;   // 规则 ①：同档不重播
            }

            // 规则 ②：当前档"播一次且未播完" ⇒ 扣住（除非 force）。判据用公开状态，不引入第二份记账。
            if (!force && !Loop && _playing && _frames != null) return false;

            Begin(frames, indices, fps, loop, null, true);
            return true;
        }

        /// <summary>
        /// 把**帧段表**展开成帧下标数组。<paramref name="runs"/> 的形状 = <c>[起始帧号, 长度, 起始帧号, 长度, …]</c>
        /// （扁平段表，便于两端对照）。
        /// <para>
        /// 严格校验（任一不合法即返回 <c>null</c> 并写出原因，⛔ 不做"静默裁剪"——
        /// 帧段表算错会导致"播到不属于本动作的帧"，那种现象极难归因）：
        /// 段表为 null / 空、长度为奇数、某段长度 &lt;= 0、某段超出 <paramref name="frameCount"/>。
        /// </para>
        /// </summary>
        /// <param name="runs">扁平段表；<c>null</c> / 空 ⇒ 返回 <c>null</c>。</param>
        /// <param name="frameCount">帧表总帧数（越界判据）。</param>
        /// <param name="error">失败原因；成功时为 null。</param>
        public static int[] ExpandRuns(int[] runs, int frameCount, out string error)
        {
            error = null;
            if (runs == null || runs.Length == 0)
            {
                error = "帧段表为空（null 或长度 0）";
                return null;
            }

            if (runs.Length % 2 != 0)
            {
                error = $"帧段表长度 {runs.Length} 为奇数（应为 [起始, 长度] 成对出现）";
                return null;
            }

            var total = 0;
            for (var i = 1; i < runs.Length; i += 2)
            {
                if (runs[i] <= 0)
                {
                    error = $"第 {i / 2} 段长度 {runs[i]} 非法（必须 > 0）";
                    return null;
                }

                if (runs[i - 1] < 0 || runs[i - 1] + runs[i] > frameCount)
                {
                    error = $"第 {i / 2} 段 [{runs[i - 1]}, {runs[i - 1] + runs[i]}) 超出帧表范围（共 {frameCount} 帧）";
                    return null;
                }

                total += runs[i];
            }

            var indices = new int[total];
            var at = 0;
            for (var i = 0; i < runs.Length; i += 2)
            {
                for (var k = 0; k < runs[i + 1]; k++) indices[at++] = runs[i] + k;
            }

            return indices;
        }

        /// <summary>
        /// 停止播放（**保留当前帧的画面**，不把 sprite 清掉）。<see cref="Advance"/> 之后不再推进；
        /// 未触发的完成回调一并作废（避免"停了还回调"）。
        /// </summary>
        public void Stop()
        {
            _playing = false;
            _onComplete = null;
            _accum = 0f;
        }

        /// <summary>
        /// 直接定位到第 <paramref name="index"/> 帧并立刻推给渲染器（帧表为空 ⇒ 空操作 + 降频 Warn）。
        /// <para>越界下标**收敛**到 [0, 档位帧数-1] 并降频 Warn。<b>不改播放状态</b>（
        /// 只想摆一帧就停住的话，用 <see cref="PlayStill"/> 或先 <see cref="Stop"/>）；
        /// 定位后累计时间归零，免得"刚定帧就被下一次 Advance 推走"。</para>
        /// <para>用了帧切片时 <paramref name="index"/> 是**档位位置**（不是帧数组下标）。</para>
        /// </summary>
        public void SetFrame(int index)
        {
            if (ClipLength == 0)
            {
                LogThrottle.WarnThrottled(Tag, "empty-frames",
                    "SetFrame 时帧表为空（未 Play 过 / 空帧表）⇒ 本次不设置任何帧");
                return;
            }

            var clamped = Mathf.Clamp(index, 0, ClipLength - 1);
            if (clamped != index)
            {
                LogThrottle.WarnThrottled(Tag, "setframe.clamp",
                    $"SetFrame({index}) 越界（当前档位 {ClipLength} 帧）⇒ 收敛到 {clamped}");
            }

            _index = clamped;
            _accum = 0f;
            Apply();
        }

        /// <summary>
        /// 推进 <paramref name="dt"/> 秒（**唯一的时间入口**，由业务 Tick 调）。
        /// <para><paramref name="dt"/> &lt;= 0 ⇒ 不推进（0 是合法的"暂停帧"，负值按 0 处理并降频 Warn）；
        /// 未在播放 ⇒ 直接返回。</para>
        /// <para>单次推进超过 <see cref="MaxAdvanceSteps"/> 帧（= dt 异常大 / 太久没 Tick）⇒
        /// 丢弃剩余累计时间并降频 Warn，⛔ 不把一帧卡成死循环。</para>
        /// </summary>
        public void Advance(float dt)
        {
            if (!_playing) return;

            if (dt <= 0f)
            {
                if (dt < 0f)
                {
                    // 非预期分支：负 dt（时钟回退 / 参数写反）⇒ 按 0 处理，不倒退播放
                    LogThrottle.WarnThrottled(Tag, "advance.negative-dt",
                        $"Advance 收到负 dt（{dt}）⇒ 按 0 处理（动画不倒退）");
                }
                return;
            }

            if (ClipLength == 0)
            {
                // 保险：Play 已经挡住空帧表；真到这里说明状态被外部改坏 ⇒ 停下并留痕
                _playing = false;
                LogThrottle.WarnThrottled(Tag, "empty-frames", "Advance 时帧表为空 ⇒ 停止播放");
                return;
            }

            _accum += dt;
            var steps = 0;
            while (_accum >= _frameDuration)
            {
                _accum -= _frameDuration;

                if (++steps > MaxAdvanceSteps)
                {
                    _accum = 0f;
                    LogThrottle.WarnThrottled(Tag, "advance.steps",
                        $"单次 Advance(dt={dt:F3}s) 需要推进超过 {MaxAdvanceSteps} 帧（fps={Fps:F2}）" +
                        "⇒ 丢弃剩余累计时间；dt 异常大或长时间没 Tick 时会走到这里");
                    return;
                }

                if (!Step()) return; // 已播完（回调也发过了）
            }
        }

        // ── 内部 ─────────────────────────────────────────────────────────────
        /// <summary>
        /// 播放入口的唯一实现。
        /// </summary>
        /// <param name="indices">帧下标切片；<c>null</c> / 空 = 整表。</param>
        /// <param name="stillOnZeroFps">
        /// <c>true</c> ⇒ <c>fps == 0</c> 按「静止帧」处理（停播、停在首帧、**不打日志**）；
        /// <c>false</c> ⇒ 旧签名语义（<c>fps &lt;= 0</c> 一律按 1 fps + 降频 Warn）。
        /// <para>两条语义刻意并存：旧签名被既有调用方与 EditMode 测试（`InvalidFps_FallsBackToOne`）钉住，
        /// ⛔ 不许改；新重载才带"0 = 静止帧"。</para>
        /// </param>
        private void Begin(Sprite[] frames, int[] indices, float fps, bool loop, Action onComplete,
            bool stillOnZeroFps)
        {
            _frames = frames;
            _clipSource = indices != null && indices.Length > 0 ? indices : null;
            _clip = SanitizeClip(_clipSource, frames);
            _index = 0;      // 换表 / 重播一律从第 0 帧起：旧下标可能越界，也避免"接着上次的帧继续"
            _accum = 0f;
            Loop = loop;
            _onComplete = onComplete;

            // ★ 静止帧（fps == 0）必须**先判**：它是合法取值（原版 idle = 单条静止姿态帧），
            //   ⛔ 不能落到下面的 NormalizeFps —— 那会把 0 变成 1 fps（表现成"该静止的档在慢慢抖"）
            //   并打一条"fps 非正"的**误报**日志。这里也不打日志：整档静止是正常素材形态。
            var still = stillOnZeroFps && fps == 0f;
            if (still)
            {
                Fps = 0f;
                _frameDuration = float.PositiveInfinity;
                _playing = false;
            }
            else
            {
                Fps = NormalizeFps(fps);
                _frameDuration = 1f / Fps;
                _playing = ClipLength > 0;
            }

            if (ClipLength == 0)
            {
                // 非预期分支：空帧表 ⇒ 不抛、不播（保持渲染器现状），但必须留痕（降频，避免每帧刷屏）。
                // Fps 保持上面算出的值 —— 与本次改动前逐字一致。
                _playing = false;
                LogThrottle.WarnThrottled(Tag, "empty-frames",
                    "Play 收到空帧表（null 或长度 0）⇒ 不播放、不抛异常；" +
                    "通常意味着 Sprite 还没加载完 / 图集路径不对");
                return;
            }

            Apply();         // 立刻显示第 0 帧（否则要等一个帧周期才出画面）
        }

        /// <summary>
        /// 切片守卫：有越界 / 负下标时**另建一份收敛过的副本**（并降频 Warning）；全都合法时**原样返回**
        /// 调用方的数组（零分配 —— 换档是热路径）。
        /// </summary>
        private static int[] SanitizeClip(int[] indices, Sprite[] frames)
        {
            if (indices == null) return null;
            if (frames == null || frames.Length == 0) return null;   // 空帧表：交给 Begin 的空表分支
            if (indices.Length == 0) return null;

            var bad = false;
            foreach (var i in indices)
            {
                if (i >= 0 && i < frames.Length) continue;
                bad = true;
                break;
            }

            if (!bad) return indices;

            var fixedIndices = new int[indices.Length];
            for (var i = 0; i < indices.Length; i++)
                fixedIndices[i] = Mathf.Clamp(indices[i], 0, frames.Length - 1);

            LogThrottle.WarnThrottled(Tag, "clip.out-of-range",
                $"帧切片里有越界下标（帧表 {frames.Length} 帧）⇒ 本档已把越界项收敛到端点；" +
                "通常是帧段表与实到帧数不一致（素材换了一批 / 切片算错）");
            return fixedIndices;
        }

        /// <summary>推进一帧。<c>false</c> = 已播完（<see cref="PlayOnce"/> 到末帧，回调已发）。</summary>
        private bool Step()
        {
            _index++;
            if (_index < ClipLength)
            {
                Apply();
                return true;
            }

            if (Loop)
            {
                _index = 0;
                Apply();
                return true;
            }

            _index = ClipLength - 1;  // 停在最后一帧（不清画面）
            _playing = false;
            Apply();
            FireComplete();
            return false;
        }

        private void FireComplete()
        {
            var cb = _onComplete;
            _onComplete = null;          // 先摘回调再调：回调里重播/停止都不会二次触发
            if (cb == null) return;

            try
            {
                cb();
            }
            catch (Exception ex)
            {
                // 非预期分支：回调异常不许打断调用方的 Tick 链
                Game.Logger?.Error(Tag, $"PlayOnce 的完成回调抛出异常：{ex.Message}", ex);
            }
        }

        /// <summary>
        /// 把当前帧写到渲染器（同帧不重复赋值；无渲染器 / 无帧表 ⇒ 只留状态）。
        /// <para>用了帧切片时：<see cref="_index"/> 是**档位位置**，先经切片映射成帧数组下标再取帧。</para>
        /// </summary>
        private void Apply()
        {
            var frames = _frames;
            var length = ClipLength;
            if (frames == null || frames.Length == 0 || length == 0) return;

            if (_index < 0) _index = 0;
            else if (_index >= length) _index = length - 1;

            if (_target == null) return;  // 无渲染器（离线宿主 / EditMode 测试）：只推进状态

            var frameIndex = _clip != null ? _clip[_index] : _index;
            if (frameIndex < 0 || frameIndex >= frames.Length)
            {
                // 保险：切片越界（Begin 的 SanitizeClip 已挡，这里防"帧表被换成更短的一张"）
                LogThrottle.WarnThrottled(Tag, "clip.index-out-of-range",
                    $"帧切片下标 {frameIndex} 超出当前帧表（{frames.Length} 帧）⇒ 本帧不写渲染器；" +
                    "多半是换了帧表却没换切片");
                return;
            }

            var sprite = frames[frameIndex];
            if (ReferenceEquals(_target.sprite, sprite)) return;
            _target.sprite = sprite;
        }

        /// <summary>
        /// fps 归一化：&gt; 0 且有限 ⇒ 原样；否则按 <see cref="MinFps"/> 并降频留痕
        /// （NaN / +Inf 一并落入这里，避免 1/fps = NaN 污染累计时间）。
        /// </summary>
        private static float NormalizeFps(float fps)
        {
            if (fps > 0f && !float.IsInfinity(fps)) return fps;

            LogThrottle.WarnThrottled(Tag, "fps.invalid",
                $"Play 收到非正 / 非有限 fps（{fps}）⇒ 按 {MinFps} fps 处理（检查动画节奏常量）");
            return MinFps;
        }

        /// <summary>
        /// 比较用归一化（**不写日志**）：与"新重载"口径一致 —— <c>0</c> 保留为 <c>0</c>（= 静止帧），
        /// 其余非法值按 <see cref="MinFps"/>。
        /// <para>为什么不直接调 <see cref="NormalizeFps"/>：那是"要播放了"的路径，对非法值要留痕；
        /// 而"比一下是不是同一个档"不该产生告警（否则每次换档判定都会打一条无关的日志）。</para>
        /// </summary>
        private static float NormalizeFpsForCompare(float fps)
        {
            if (fps == 0f) return 0f;
            if (fps > 0f && !float.IsInfinity(fps)) return fps;
            return MinFps;
        }
    }
}
