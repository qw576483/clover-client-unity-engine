// ─────────────────────────────────────────────────────────────────────────────
// CloverEngine · Runtime/Presentation/SpriteFrameAnimator.cs
// 轻量逐帧动画器：帧表（Sprite[]）→ SpriteRenderer，**由业务 Tick 驱动**。通用底座，下沉到引擎。
//
// 出处：clover-project-super-mario 里至少 5 处**逐字相同**的手写帧推进 ——
//   `_animTimer += dt; if (_animTimer >= 0.11f) { _animTimer = 0f; _frame = (_frame + 1) % _frames.Count;
//    sr.sprite = _frames[_frame]; }`：
//     · Module/Entities/ItemModule.cs:343 金币 0.11s/帧 · :513 星星 0.09 · :603 0.08 · :663 0.06
//     · Module/Entities/FireballModule.cs:225 火球 0.06
//   另有 3 处同型"计时 → 换 Sprite"手写（EnemyModule.cs:237-243 Goomba 翻转 / :483-492 乌龟两帧 /
//   :894-900 食人花两帧，0.18 / 0.16 / 0.22s）—— 节奏常量散落在各个 MonoBehaviour 里，切帧逻辑一式多份。
//   ⇒ 收敛成本类：帧表 + fps 给一次，切帧只有一份实现。
//
// ★ 为什么**不**用 AnimatorController（引擎现成的 `IAnimationManager`）：
//   · `IAnimationManager.CreateAnimator(GameObject, RuntimeAnimatorController)`
//     （Runtime/Core/PresentationContracts.cs:277-285）落地的是一颗 `Animator` + 编辑器里编好的
//     controller 资产（Runtime/Presentation/Animation.cs:14-23：`animator.runtimeAnimatorController = controller`），
//     它回答的是"播哪个 state / 设哪个参数"（`IAnimPlayer.Play(stateName)`）——**要**在编辑器里建 State/Clip；
//   · 平台游戏要的是「帧列表直接切 `SpriteRenderer.sprite`」：每套动画的节奏就是**一个 fps**
//     （0.06~0.22 s/帧，见上），没有状态机、没有混合、没有 Avatar。为 4 张图建 controller + clip
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
//   · 运行中换帧表（重播）⇒ 下标与累计时间**归零**（不会拿旧表的下标去索引新表）；
//   · `target` 允许为 null（离线宿主 / EditMode 测试只推进状态，不写 SpriteRenderer）；
//   · `PlayOnce` 的完成回调抛异常 ⇒ 接住并 Error 留痕，**不打断**调用方的 Tick 链；回调只触发一次；
//   · 单次 `Advance` 推进步数有上限（防"超大 dt / 长时间没 Tick"把一帧卡成死循环）。
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

        /// <summary>当前生效的帧率（<see cref="Play"/> 时归一化后的值；未播放过为 0）。</summary>
        public float Fps { get; private set; }

        /// <summary>当前是否循环播放（<see cref="PlayOnce"/> ⇒ false）。</summary>
        public bool Loop { get; private set; }

        /// <summary>当前帧表长度（0 = 没帧可放）。</summary>
        public int FrameCount => _frames?.Length ?? 0;

        /// <summary>当前帧下标（0 起；空帧表时恒 0）。</summary>
        public int FrameIndex => _index;

        /// <summary>是否正在播放。<see cref="PlayOnce"/> 播到最后一帧后自动变 false（且停在最后一帧）。</summary>
        public bool IsPlaying => _playing;

        /// <summary>
        /// 循环播放。
        /// <para>空帧表 ⇒ 不播 + 降频 Warn；<paramref name="fps"/> &lt;= 0（或 NaN/Inf）⇒ 按 1 fps + 降频 Warn。</para>
        /// <para>重播 / 换表一律**从第 0 帧重新开始**（累计时间归零），并立刻把第 0 帧推到渲染器
        /// （否则首帧前会空窗一个帧周期）。</para>
        /// </summary>
        public void Play(Sprite[] frames, float fps, bool loop = true) => Begin(frames, fps, loop, null);

        /// <summary>
        /// 播放一次（到最后一帧即停，**停在最后一帧**，<see cref="IsPlaying"/> 变 false）。
        /// </summary>
        /// <param name="onComplete">播完回调（可为 null）。**只触发一次**；回调里抛异常会被接住并留下 Error，
        /// 不会打断调用方的 Tick 链。</param>
        public void PlayOnce(Sprite[] frames, float fps, Action onComplete = null) =>
            Begin(frames, fps, false, onComplete);

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
        /// <para>越界下标**收敛**到 [0, 帧数-1] 并降频 Warn。<b>不改播放状态</b>（
        /// 只想摆一帧就停住的话，先 <see cref="Stop"/>）；定位后累计时间归零，
        /// 免得"刚定帧就被下一次 Advance 推走"。</para>
        /// </summary>
        public void SetFrame(int index)
        {
            if (FrameCount == 0)
            {
                LogThrottle.WarnThrottled(Tag, "empty-frames",
                    "SetFrame 时帧表为空（未 Play 过 / 空帧表）⇒ 本次不设置任何帧");
                return;
            }

            var clamped = Mathf.Clamp(index, 0, FrameCount - 1);
            if (clamped != index)
            {
                LogThrottle.WarnThrottled(Tag, "setframe.clamp",
                    $"SetFrame({index}) 越界（当前帧表 {FrameCount} 帧）⇒ 收敛到 {clamped}");
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

            var frames = _frames;
            if (frames == null || frames.Length == 0)
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

                if (!Step(frames)) return; // 已播完（回调也发过了）
            }
        }

        // ── 内部 ─────────────────────────────────────────────────────────────
        private void Begin(Sprite[] frames, float fps, bool loop, Action onComplete)
        {
            _frames = frames;
            _index = 0;      // 换表 / 重播一律从第 0 帧起：旧下标可能越界，也避免"接着上次的帧继续"
            _accum = 0f;
            Loop = loop;
            _onComplete = onComplete;
            Fps = NormalizeFps(fps);
            _frameDuration = 1f / Fps;

            if (frames == null || frames.Length == 0)
            {
                // 非预期分支：空帧表 ⇒ 不抛、不播（保持渲染器现状），但必须留痕（降频，避免每帧刷屏）
                _playing = false;
                LogThrottle.WarnThrottled(Tag, "empty-frames",
                    "Play 收到空帧表（null 或长度 0）⇒ 不播放、不抛异常；" +
                    "通常意味着 Sprite 还没加载完 / 图集路径不对");
                return;
            }

            _playing = true;
            Apply();         // 立刻显示第 0 帧（否则要等一个帧周期才出画面）
        }

        /// <summary>推进一帧。<c>false</c> = 已播完（<see cref="PlayOnce"/> 到末帧，回调已发）。</summary>
        private bool Step(Sprite[] frames)
        {
            _index++;
            if (_index < frames.Length)
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

            _index = frames.Length - 1;  // 停在最后一帧（不清画面）
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

        /// <summary>把当前帧写到渲染器（同帧不重复赋值；无渲染器 / 无帧表 ⇒ 只留状态）。</summary>
        private void Apply()
        {
            var frames = _frames;
            if (frames == null || frames.Length == 0) return;

            if (_index < 0) _index = 0;
            else if (_index >= frames.Length) _index = frames.Length - 1;

            if (_target == null) return;  // 无渲染器（离线宿主 / EditMode 测试）：只推进状态

            var sprite = frames[_index];
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
    }
}
