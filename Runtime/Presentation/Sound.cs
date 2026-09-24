// ─────────────────────────────────────────────────────────────────────────────
// CloverEngine · Runtime/Presentation/Sound.cs
// 音效**播放闸门**：缺失只报一次 / 单帧起播上限 / 同 clip 并发上限 / 同路径最小重播间隔。
//
// ⛔ **下列内容不在本件**（那些是业务）：
//   · 音效名表与路径命名口径（`sfx/<短名>` 由业务约定）；
//   · 分组音量值 / 设置键 / 音量应用策略（轮询、master×sfx）；
//   · "开局预热哪些音效"（`Prewarm` 的列表）；
//   · 项目侧"探测一次、Ready 才播"的缓存。
//
// 语义约束（改一条 = 语义漂移；与 `LogThrottle.cs` 的版式一致）：
//   ① **三个闸门默认 `0` = 不限**：不设闸门时，本文件对播放路径的
//      行为（起播次数 / 日志 / 资源引用计数 / 音源池取源顺序）保持不变。
//      第 3 个（同一路径最小重播间隔）见 <see cref="SoundRepeatGate"/>
//      （可注入时钟 / 空键放行 / 时间源不可用即惰性）。
//   ② 三个闸门**只管 SFX / Voice 的起播**（`PlaySFX` / `PlaySFXAt` / `PlayVoice`）：
//      BGM / 分组音量 / 淡入淡出 / `TakeSource` 的池逻辑 / `Dispose` 语义**一律不动**。
//   ③ 超限 ⇒ **丢弃该次播放**（⛔ 不排队、⛔ 不打断正在播的音源），并**限频告警**
//      （`LogThrottle.WarnThrottled`，⛔ 不许裸 `Warn`）；丢弃时归还本次加载的资源引用。
//   ④ `MaxConcurrentPerClip` 的口径 = **同一路径当前正在播的音源数**（真并发，**不是**时间窗计数）。
//      路径含分组前缀（`Sound/SFX/…` / `Sound/Voice/…`）⇒ 不同分组互不干扰。
//   ⑤ 缺失（`clip == null`）**整进程只报一次 / 路径**：`LogThrottle.WarnOnce("Sound", "missing:<path>", …)`，
//      message 文案与 `path` 变量保留原样。**四处 `clip == null` 一视同仁**（`PlayBGM` / `PlaySFX` /
//      `PlaySFXAt` / `PlayVoice`）：BGM 那条的告警频率同为每路径一次；BGM 的播放 / 淡入淡出 /
//      请求序号判定不受影响。资源加载失败自身的那条 Error 由资源层负责
//      （它在 `ResourceManager.CompletePending`，**不在本文件**；业务侧要"问一句在不在"用
//      `Game.Res.Exists`，那是引擎的按路径缓存，不属本闸门）。
//   ⑥ 本文件所有告警一律走 `LogThrottle`（池满分支见 `GetAvailableSource` 的
//      `LogThrottle.WarnOnce("Sound", "pool.exhausted", …)`）；池逻辑与 `_poolExhaustedWarned`
//      不变（"只报一次"语义不变）。
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace CloverEngine
{
    // 契约（SoundGroup / ISoundManager）见 Runtime/Core/PresentationContracts.cs。

    /// <summary>
    /// 协程宿主：GameObject 本身不能启动/停止协程（那是 MonoBehaviour 的能力），
    /// SoundManager 挂一个它作为载体来驱动音量淡入淡出协程。
    /// </summary>
    internal class SoundCoroutineHost : MonoBehaviour { }

    /// <summary>
    /// 声音管理器实现，基于固定音源池管理 BGM 与音效播放
    /// </summary>
    internal class SoundManager : ISoundManager
    {
        /// <summary>
        /// SFX 音源池容量。
        /// <para>
        /// 取值依据：「一次命中会同时触发 挥砍 / 命中 / 受击 / 死亡 / 掉落」
        /// 若干音效，池不足时在 AOE / 群体战下会被占满 ⇒ 后续音效走 <see cref="GetAvailableSource"/>
        /// 的池满分支被**静默丢弃**。
        /// <see cref="AudioSource"/> 空载不耗 CPU，**无行为副作用**（纯增益）。
        /// </para>
        /// </summary>
        private const int SfxPoolSize = 32;

        private readonly AudioSource[] _bgmSources = new AudioSource[2];
        private int _currentBGM;
        private readonly List<AudioSource> _sfxPool = new();
        private readonly Dictionary<SoundGroup, float> _volumes = new();
        private readonly Dictionary<SoundGroup, bool> _mutes = new();
        private readonly GameObject _root;
        private readonly MonoBehaviour _host;
        // 淡入淡出按**音源**为键：⛔ 不能按分组（BGM）为键 —— 切 BGM 时"新曲淡入"与"旧曲淡出"
        // 会共用同一个槽位，第二次 StartFade 会把刚启动的淡入协程 Stop 掉（新 BGM 音量停在 ≈0）。
        private readonly Dictionary<AudioSource, Coroutine> _fades = new();
        // 每个音源当前 clip 对应的资源路径：换 clip 时归还旧引用、Dispose 时全部归还。
        private readonly Dictionary<AudioSource, string> _clipPaths = new();
        // 每个音源当前播放的音频分组（播放时记录；⛔ 不靠 clip 名猜分组：命名不匹配就设错音量）。
        private readonly Dictionary<AudioSource, SoundGroup> _srcGroups = new();
        // PlayBGM 的请求序号：异步加载完成时若已有更新的请求（或已 Dispose）则丢弃旧结果（防双 BGM 同播 / 旧曲覆盖新曲）。
        private int _bgmRequestId;
        private bool _poolExhaustedWarned;
        // ── 播放闸门的记账：**只有需要时才用**，默认值下恒定不参与 ──
        // 单帧起播计数 + 它属于哪一帧。计数只在**真正起播**（异步回调里、取到音源之前）时累加，
        // 所以"加载晚到的音效"算在它真正响的那一帧，而不是它被请求的那一帧。
        private int _framePlays;
        private int _frameOfPlays = -1;
        // 被 App.Pause 真正暂停过的音源。AudioSource.isPlaying 在暂停态返回 false，
        // 恢复时不能靠它反推"哪些正在播"，必须自己记账。
        private readonly List<AudioSource> _appPausedSources = new();

        /// <summary>
        /// 创建声音管理器，初始化 BGM 双音源、32 个音效池音源，并将所有分组音量设为 1
        /// </summary>
        public SoundManager()
        {
            _root = new GameObject("[Sound]");
            UnityEngine.Object.DontDestroyOnLoad(_root);
            _host = _root.AddComponent<SoundCoroutineHost>();

            for (var i = 0; i < 2; i++)
            {
                _bgmSources[i] = _root.AddComponent<AudioSource>();
                _bgmSources[i].loop = true;
                _bgmSources[i].playOnAwake = false;
            }

            for (var i = 0; i < SfxPoolSize; i++)
            {
                // ★ 每个 SFX 音源挂**独立子节点**：3D 音效要按各自的位置播放；
                //   若共用 [Sound] 根节点，PlaySFXAt 设 transform.position 等于
                //   把整个根（含 BGM 与其它音效）搬走，多次 3D 音效互相抢位。
                var go = new GameObject("SFX" + i);
                go.transform.SetParent(_root.transform, false);
                var src = go.AddComponent<AudioSource>();
                src.playOnAwake = false;
                _sfxPool.Add(src);
            }

            foreach (SoundGroup g in Enum.GetValues(typeof(SoundGroup)))
            {
                _volumes[g] = 1f;
                _mutes[g] = false;
            }

            // 订阅应用级暂停 / 恢复（发布方 EngineRunner.OnApplicationPause，事件名见 CloverEvents.App）。
            // 切后台时把正在播的音源暂停、回前台恢复，避免"退到后台还在响 / 回来时状态错乱"。
            // Game.Event 为 null 时静默跳过（EditMode 里直接 new SoundManager() 的情形）。
            Game.Event?.On(CloverEvents.App.Pause, OnAppPause);
            Game.Event?.On(CloverEvents.App.Resume, OnAppResume);
        }

        /// <summary>
        /// <see cref="CloverEvents.App.Pause"/> 的消费者：暂停本管理器当前在播的音源并记账。
        /// 已在暂停态的不会被重复暂停（isPlaying 为 false 时跳过）。
        /// </summary>
        private void OnAppPause()
        {
            _appPausedSources.Clear();

            foreach (var src in _bgmSources)
            {
                if (src == null || !src.isPlaying) continue;
                src.Pause();
                _appPausedSources.Add(src);
            }

            foreach (var src in _sfxPool)
            {
                if (src == null || !src.isPlaying) continue;
                src.Pause();
                _appPausedSources.Add(src);
            }
        }

        /// <summary><see cref="CloverEvents.App.Resume"/> 的消费者：恢复由 <see cref="OnAppPause"/> 暂停的音源。</summary>
        private void OnAppResume()
        {
            foreach (var src in _appPausedSources)
            {
                // Dispose 已销毁根节点时这些组件会变成"假 null"：跳过而不是对已销毁对象调用。
                if (src == null) continue;
                src.UnPause();
            }
            _appPausedSources.Clear();
        }

        // ── 播放闸门：三个都可配，默认 0 = 不限 ──────────
        //
        // 为什么要暴露在 ISoundManager 上（而不是只做 SoundManager 的内部字段）：
        // 实现类 internal（G1），业务只拿得到 `Game.Sound`（接口）—— 挂在实现类上等于"业务配不了"，
        // 那闸门就只能永远用默认值（= 没有闸门），等于白做。
        // 取值来源仍是**业务**（`MaxPlaysPerFrame` / `MaxConcurrentPerClip` 由业务配），
        // 引擎这里只是执行口径（与 `LogThrottle` 的 `everyN` 由业务传入同一分工）。

        /// <inheritdoc/>
        public int MaxPlaysPerFrame { get; set; }

        /// <inheritdoc/>
        public int MaxConcurrentPerClip { get; set; }

        /// <summary>
        /// 播放闸门：本次是否允许**真正起播** <paramref name="path"/>。
        /// <para>
        /// 三个闸门都默认 <c>0</c>（= 不限）：此时本方法**不记账、不判理由**，恒定放行 ——
        /// 这是"默认值不改变任何既有表现"的落点（见文件头语义约束 ①）。
        /// </para>
        /// <para>
        /// 超限 ⇒ 返回 <c>false</c>（调用方归还本次加载的资源引用后丢弃这次播放）并**限频告警**：
        /// ⛔ 不排队（排队会在池空出来时"补播"一串迟到的声音）、⛔ 不打断正在播的音源。
        /// </para>
        /// </summary>
        private bool AllowPlay(string path)
        {
            var perFrame = MaxPlaysPerFrame;
            if (perFrame > 0)
            {
                var frame = Time.frameCount;
                if (frame != _frameOfPlays)
                {
                    _frameOfPlays = frame;
                    _framePlays = 0;
                }

                if (_framePlays >= perFrame)
                {
                    // key 固定（不含帧号）：否则限频表的 key 会随帧号无限增长，且每帧都是"新 key" ⇒ 等于没限频。
                    LogThrottle.WarnThrottled("Sound", "playcap.frame",
                        $"单帧音效起播已达上限（MaxPlaysPerFrame = {perFrame}），本次播放被丢弃：{path}" +
                        "（同一帧里挤进来的音效请错峰发放，或调高该上限）");
                    return false;
                }
            }

            var perClip = MaxConcurrentPerClip;
            if (perClip > 0 && CountPlaying(path) >= perClip)
            {
                // key 按路径分：某个音效触发过密时，别的音效的告警不被它吃掉。
                LogThrottle.WarnThrottled("Sound", "playcap.clip:" + path,
                    $"同一音效同时播放已达上限（MaxConcurrentPerClip = {perClip}），本次播放被丢弃：{path}");
                return false;
            }

            // 第 3 维：同一路径的最小重播间隔（时间窗）—— 默认 0 = 不限 ⇒ 不记账、恒定放行。
            // 判据与"被丢弃的那次不刷新计时"的推导见 SoundRepeatGate。
            var minRepeat = SoundRepeatGate.MinRepeatSecondsPerClip;
            if (minRepeat > 0f && SoundRepeatGate.ShouldDrop(path, minRepeat, out var sinceRepeat))
            {
                // key 按路径分（同 perClip 口径）：某个音效触发过密时，别的音效的告警不被它吃掉。
                LogThrottle.WarnThrottled("Sound", "playcap.repeat:" + path,
                    $"同一音效重播间隔不足（MinRepeatSecondsPerClip = {minRepeat}s），本次播放被丢弃：{path}" +
                    $"（距上次允许起播 {sinceRepeat:F3}s）");
                return false;
            }

            if (perFrame > 0) _framePlays++;
            return true;
        }

        /// <summary>
        /// 当前正在播 <paramref name="path"/> 的音源数 —— <see cref="MaxConcurrentPerClip"/> 的判据
        /// （真并发，不是时间窗计数）。只扫 SFX 池：BGM 双音源不参与闸门（BGM 行为一律不动）。
        /// </summary>
        private int CountPlaying(string path)
        {
            var n = 0;
            foreach (var src in _sfxPool)
            {
                if (src == null || !src.isPlaying) continue;
                if (_clipPaths.TryGetValue(src, out var srcPath) && srcPath == path) n++;
            }
            return n;
        }

        /// <inheritdoc/>
        public void PlayBGM(string clipName, float fadeTime = 0.5f)
        {
            var requestId = ++_bgmRequestId;
            var path = $"Sound/BGM/{clipName}";
            Game.Res?.LoadAsset<AudioClip>(path, clip =>
            {
                if (clip == null)
                {
                    // 缺失只报一次/路径：BGM 也是 `clip == null` 的缺失分支，与下面三处
                    // SFX / Voice 同口径 —— 文件头语义约束 ⑤ 说的就是**所有**缺失只报一次。
                    LogThrottle.WarnOnce("Sound", "missing:" + path, $"BGM 加载失败（clip 为空）：{path}");
                    return;
                }
                // 加载竞态：期间又发起了新的 PlayBGM（或已 Dispose）→ 丢弃本次结果并归还引用，
                // 否则两路 BGM 同播 / 旧曲把新曲覆盖掉。
                if (requestId != _bgmRequestId || _root == null)
                {
                    Game.Res?.Release(path);
                    return;
                }

                var prev = _bgmSources[_currentBGM];
                var next = _currentBGM == 0 ? 1 : 0;

                if (_bgmSources[next].isPlaying)
                    _bgmSources[next].Stop();

                AssignClip(_bgmSources[next], path, clip, SoundGroup.BGM);
                _bgmSources[next].volume = _mutes[SoundGroup.BGM] ? 0f : _volumes[SoundGroup.BGM];
                _bgmSources[next].Play();

                _currentBGM = next;

                if (fadeTime > 0f)
                {
                    // 淡入目标音量必须考虑静音：否则静音状态下切 BGM 会把音量拉回未静音值，静音设置失效。
                    var targetVolume = _mutes[SoundGroup.BGM] ? 0f : _volumes[SoundGroup.BGM];
                    StartFade(_bgmSources[next], 0f, targetVolume, fadeTime);
                    if (prev.isPlaying)
                        StartFade(prev, prev.volume, 0f, fadeTime, true);
                }
                else if (prev.isPlaying)
                {
                    // 无淡出时必须立即停掉旧 BGM：否则两路同时播放（音量叠加）。
                    prev.Stop();
                }
            });
        }

        /// <inheritdoc/>
        public void StopBGM(float fadeTime = 0.5f)
        {
            var src = _bgmSources[_currentBGM];
            if (!src.isPlaying) return;

            if (fadeTime <= 0f)
            {
                src.Stop();
                return;
            }

            StartFade(src, src.volume, 0f, fadeTime, true);
        }

        /// <inheritdoc/>
        public void PlaySFX(string clipName)
        {
            var path = $"Sound/SFX/{clipName}";
            Game.Res?.LoadAsset<AudioClip>(path, clip =>
            {
                if (clip == null)
                {
                    // 缺失只报一次/路径（脚步 / 命中这类每秒多次的高频路径，逐次报会把日志刷爆）。
                    LogThrottle.WarnOnce("Sound", "missing:" + path, $"音效加载失败（clip 为空）：{path}");
                    return;
                }
                // 播放闸门（默认 0 = 不限）；超限则归还本次加载的引用并丢弃。
                if (!AllowPlay(path))
                {
                    Game.Res?.Release(path);
                    return;
                }
                var src = TakeSource(path, clip, SoundGroup.SFX);
                if (src == null) return;
                src.spatialBlend = 0f;
                src.Play();
            });
        }

        /// <inheritdoc/>
        public void PlaySFXAt(string clipName, Vector3 position)
        {
            var path = $"Sound/SFX/{clipName}";
            Game.Res?.LoadAsset<AudioClip>(path, clip =>
            {
                if (clip == null)
                {
                    // 同上：缺失只报一次/路径。
                    LogThrottle.WarnOnce("Sound", "missing:" + path, $"3D 音效加载失败（clip 为空）：{path}");
                    return;
                }
                // 播放闸门（默认 0 = 不限）；超限则归还本次加载的引用并丢弃。
                if (!AllowPlay(path))
                {
                    Game.Res?.Release(path);
                    return;
                }
                var src = TakeSource(path, clip, SoundGroup.SFX);
                if (src == null) return;
                src.transform.position = position;
                src.spatialBlend = 1f;
                src.Play();
            });
        }

        /// <inheritdoc/>
        public void PlayVoice(string clipName)
        {
            var path = $"Sound/Voice/{clipName}";
            Game.Res?.LoadAsset<AudioClip>(path, clip =>
            {
                if (clip == null)
                {
                    // 同上：缺失只报一次/路径。
                    LogThrottle.WarnOnce("Sound", "missing:" + path, $"人声加载失败（clip 为空）：{path}");
                    return;
                }
                // 播放闸门（默认 0 = 不限）；超限则归还本次加载的引用并丢弃。
                if (!AllowPlay(path))
                {
                    Game.Res?.Release(path);
                    return;
                }
                var src = TakeSource(path, clip, SoundGroup.Voice);
                if (src == null) return;
                src.spatialBlend = 0f;
                src.Play();
            });
        }

        /// <summary>
        /// 异步回调统一入口：取一个空闲音源并完成 clip 指派。
        /// 负责三件事：Dispose 后到达的回调归还资源引用后丢弃（否则对已销毁组件赋值抛 MissingReference）；
        /// 池满时归还引用并丢弃；换 clip 时归还旧 clip 的资源引用。
        /// </summary>
        private AudioSource TakeSource(string path, AudioClip clip, SoundGroup group)
        {
            if (_root == null)
            {
                // Dispose 已销毁根节点：回调可能仍会到达 —— 不碰已销毁组件，先归还本次加载的引用。
                Game.Res?.Release(path);
                return null;
            }

            var src = GetAvailableSource();
            if (src == null)
            {
                Game.Res?.Release(path);
                return null;
            }

            AssignClip(src, path, clip, group);
            src.volume = _mutes[group] ? 0f : _volumes[group];
            return src;
        }

        /// <summary>给音源指派 clip 并记账（换 clip 先归还旧路径的引用；记录分组，供分组音量使用）。</summary>
        private void AssignClip(AudioSource src, string path, AudioClip clip, SoundGroup group)
        {
            if (_clipPaths.TryGetValue(src, out var oldPath))
                Game.Res?.Release(oldPath);

            src.clip = clip;
            _clipPaths[src] = path;
            _srcGroups[src] = group;
        }

        /// <inheritdoc/>
        public void StopAll()
        {
            // 先停掉在途淡入淡出协程：否则被停音源的协程继续推进并写 volume，
            // 甚至到点后把刚重启的音源再 Stop 一次。
            foreach (var kv in _fades)
                if (kv.Value != null && _host != null) _host.StopCoroutine(kv.Value);
            _fades.Clear();

            foreach (var src in _bgmSources)
                if (src.isPlaying) src.Stop();
            foreach (var src in _sfxPool)
                if (src.isPlaying) src.Stop();
        }

        /// <inheritdoc/>
        public void SetVolume(SoundGroup group, float volume)
        {
            _volumes[group] = Mathf.Clamp01(volume);
            ApplyGroupVolume(group);
        }

        /// <inheritdoc/>
        public float GetVolume(SoundGroup group)
        {
            return _volumes.TryGetValue(group, out var v) ? v : 1f;
        }

        /// <inheritdoc/>
        public void SetMute(SoundGroup group, bool mute)
        {
            if (_mutes.TryGetValue(group, out var current) && current == mute)
                return;

            _mutes[group] = mute;
            ApplyGroupVolume(group);
        }

        /// <inheritdoc/>
        /// <remarks>
        /// 读的就是 <see cref="SetMute"/> 写的那张 <c>_mutes</c> 表本身（**同源状态**，不另存一份）：
        /// 本类里改这张表的地方只有 <see cref="SetMute"/> 与构造函数的初值两处，
        /// 因此这里读到的值必然等于"下一次播放实际会用的音量是否为 0"。
        /// 未登记的分组按未静音处理（与 <see cref="GetVolume"/> 未登记返回 1f 同口径）。
        /// </remarks>
        public bool IsMuted(SoundGroup group)
        {
            return _mutes.TryGetValue(group, out var mute) && mute;
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            // 先退订应用级事件：不退订的话，Shutdown 之后事件总线若还活着（或被重新 Emit），
            // 回调会落到已销毁的音源上（本类已被拆卸，记账表也已清空）。
            Game.Event?.Off(CloverEvents.App.Pause, OnAppPause);
            Game.Event?.Off(CloverEvents.App.Resume, OnAppResume);
            _appPausedSources.Clear();

            foreach (var kv in _fades)
                if (kv.Value != null) _host?.StopCoroutine(kv.Value);
            _fades.Clear();

            StopAll();

            // 归还全部音频的资源引用（只置 clip=null 会让引用计数只增不减、音频永驻缓存）。
            foreach (var kv in _clipPaths)
                Game.Res?.Release(kv.Value);
            _clipPaths.Clear();
            _srcGroups.Clear();

            foreach (var src in _bgmSources)
                src.clip = null;
            foreach (var src in _sfxPool)
                src.clip = null;

            if (_root != null)
                UnityEngine.Object.Destroy(_root);
        }

        private void ApplyGroupVolume(SoundGroup group)
        {
            var target = _mutes[group] ? 0f : _volumes[group];

            if (group == SoundGroup.BGM)
            {
                foreach (var src in _bgmSources)
                    if (src.isPlaying) src.volume = target;
                return;
            }

            foreach (var src in _sfxPool)
            {
                if (!src.isPlaying) continue;

                // 用**播放时记录的分组**判定（⛔ 不靠 clip 名是否含 "Voice"/"_voice" 猜分组：
                // 命名不含该词的语音会被当音效处理，音量 / 静音设置对不上）。
                if (!_srcGroups.TryGetValue(src, out var srcGroup) || srcGroup != group) continue;

                src.volume = target;
            }
        }

        private void StartFade(AudioSource source, float from, float to, float duration, bool stopOnEnd = false)
        {
            if (_host == null || source == null) return;

            // 同一音源同一时刻只跑一个淡变；不同音源（新 BGM 淡入 / 旧 BGM 淡出）互不干扰。
            if (_fades.TryGetValue(source, out var running) && running != null)
                _host.StopCoroutine(running);

            _fades[source] = _host.StartCoroutine(FadeCoroutine(source, from, to, duration, stopOnEnd));
        }

        private IEnumerator FadeCoroutine(AudioSource source, float from, float to, float duration, bool stopOnEnd)
        {
            var elapsed = 0f;
            while (elapsed < duration)
            {
                if (source == null) yield break;

                // 用 unscaledDeltaTime：timeScale=0（暂停 / 结算屏）时淡入淡出仍要走完，
                // 否则音量卡在中途、_fades 项也永远清不掉。
                elapsed += Time.unscaledDeltaTime;
                var t = Mathf.Clamp01(elapsed / duration);
                source.volume = Mathf.Lerp(from, to, t);
                yield return null;
            }

            if (source != null)
                source.volume = to;

            if (stopOnEnd && source != null && source.isPlaying)
                source.Stop();

            _fades.Remove(source);
        }

        private AudioSource GetAvailableSource()
        {
            foreach (var src in _sfxPool)
            {
                if (!src.isPlaying) return src;
            }

            // 池全忙：丢弃本次播放并留一次告警（只报一次，避免高频音效刷屏）。
            // ⛔ 不许改为强制返回 _sfxPool[0] —— 那会把正在播放的音效 / 人声**静默打断**。
            if (!_poolExhaustedWarned)
            {
                _poolExhaustedWarned = true;
                LogThrottle.WarnOnce("Sound", "pool.exhausted",
                    $"音效池（{_sfxPool.Count} 个音源）已全部占用，本次播放被丢弃；高频音效请错峰或提高池容量");
            }
            return null;
        }
    }

    /// <summary>
    /// 音效播放闸门的**第 3 维**：同一路径的**最小重播间隔（时间窗）** —— 与
    /// <see cref="SoundManager.MaxPlaysPerFrame"/>（单帧起播上限）、
    /// <see cref="SoundManager.MaxConcurrentPerClip"/>（同路径真并发上限）互补。
    /// <para>
    /// ⛔ <b>为什么需要这一维</b>（★ 影响所有项目）：只有"单帧上限"与"同时播放上限"时，
    /// 某个键被高频请求（实测 portal 约 52 次/秒）会让 32 个音源在 ~0.6s 内被它占满 ⇒
    /// 同一时刻的 hit / monster_attack 在 <see cref="SoundManager.GetAvailableSource"/> 的池满分支
    /// 被**静默丢弃**。
    /// </para>
    /// <para><b>取值行为</b>：保持 <see cref="MinRepeatSecondsPerClip"/> = <c>0</c>（不限）时，
    /// 对同一 clip 连发请求超过 32 次 ⇒ 日志出现 <c>音效池（32 个音源）已全部占用，本次播放被丢弃</c>，
    /// 后续其它音效全被丢弃；置 <c>0.1f</c> 后，同一键 100ms 内的第 2 / 3 次起播被丢弃
    /// （<see cref="DropCount"/> 累加），其它键照常放行；默认 <c>0</c> ⇒ 不记账、恒定放行
    /// （与另外两个闸门同口径）。</para>
    /// <para><b>已知边界 / 精度限制</b>：时间源不可用（时钟为 <c>null</c> 且 Unity 时钟抛异常 —— 即非
    /// Unity 宿主，同 <see cref="LogThrottle"/> 的时钟降级口径；或取值 <c>&lt;= 0</c>）⇒ **闸门惰性（不丢弃）**，
    /// 宁可漏节流也不许吞掉正常音效；时钟回退（<c>now &lt; last</c>）按"间隔不足"丢弃。
    /// ⛔ <b>不许</b>在热路径裸读 <c>Time.realtimeSinceStartup</c> / <c>Time.unscaledTime</c>
    /// 而不接异常 —— 非 Unity 宿主会崩。非线程安全，主线程使用。</para>
    /// <para><b>用法</b>：业务置 <c>CloverEngine.SoundRepeatGate.MinRepeatSecondsPerClip</c>
    /// （默认 <c>0</c> = 不限；契约上不去 <see cref="ISoundManager"/> 是因为闸门状态是**全进程共享的静态计时表**，
    /// 与另外两个"实例可配"的闸门分工不同 —— 见 `结构规则.md` §4.4）。引擎内消费点 =
    /// <see cref="SoundManager.AllowPlay"/>（本文件）；离线自检请注入
    /// <see cref="Clock"/>（<c>() =&gt; 秒</c>）以获得确定性计时。</para>
    /// </summary>
    public static class SoundRepeatGate
    {
        /// <summary>
        /// 同一路径的最小重播间隔（秒）。<c>0</c>（默认）= **不限** —— 此时
        /// <see cref="ShouldDrop"/> 不记账、恒定返回 <c>false</c>。取值由**业务**下发
        /// （引擎不含任何项目数值，与另外两个闸门同一分工）。
        /// </summary>
        public static float MinRepeatSecondsPerClip { get; set; }

        /// <summary>
        /// 可注入时钟（返回**秒**，语义同 <c>UnityEngine.Time.unscaledTime</c>：不受 <c>timeScale</c>
        /// 影响 ⇒ 暂停时也不误判）。默认 <c>null</c> = 用 Unity <c>Time.unscaledTime</c>；
        /// **非 Unity 宿主调用会抛异常** ⇒ 本类接住并按"时间源不可用"处理（见 <see cref="ShouldDrop"/>），
        /// 因此离线宿主不注入也不会崩，只是闸门惰性。离线自检请注入自己的时钟。
        /// </summary>
        public static Func<float> Clock { get; set; }

        /// <summary>累计被本闸门丢弃的次数（自检 / 排障用；生产只读）。</summary>
        public static int DropCount { get; private set; }

        /// <summary>每个键最近一次**被允许起播**的时刻（只由主线程访问）。</summary>
        private static readonly Dictionary<string, float> LastPlayAt =
            new Dictionary<string, float>(StringComparer.Ordinal);

        /// <summary>
        /// 是否丢弃本次起播：距同一 <paramref name="key"/> 上次**被允许起播**的间隔
        /// <c>&lt; intervalSeconds</c> ⇒ <c>true</c>。语义逐条：
        /// <paramref name="intervalSeconds"/> <c>&lt;= 0</c>（= 不限）/ 空 <c>key</c> / 时间源不可用 ⇒ <c>false</c>（放行）；
        /// 只有"被允许"的那一次刷新计时 ⇒ 被丢弃的请求不会把窗口越推越远（不会造成"永久静音"）。
        /// </summary>
        /// <param name="sinceSeconds">返回 <c>true</c> 时 = 距上次被允许起播的间隔（供日志 / 断言核数）。</param>
        public static bool ShouldDrop(string key, float intervalSeconds, out float sinceSeconds)
        {
            sinceSeconds = 0f;
            if (intervalSeconds <= 0f) return false;         // 0 = 不限（默认）⇒ 不记账、恒定放行
            if (string.IsNullOrEmpty(key)) return false;

            float now;
            try
            {
                var clock = Clock;
                now = clock != null ? clock() : Time.unscaledTime;
            }
            catch (Exception)
            {
                // 非 Unity 宿主 / 时钟不可读 ⇒ 放行（时间源不可用口径，见类注释）
                return false;
            }

            if (now <= 0f) return false;                     // 时间源不可用 ⇒ 闸门惰性

            float last;
            if (!LastPlayAt.TryGetValue(key, out last))
            {
                LastPlayAt[key] = now;
                return false;                                // 该键首次请求 ⇒ 放行
            }

            var since = now - last;
            if (since >= intervalSeconds)
            {
                LastPlayAt[key] = now;
                return false;                                // 已超过最小间隔 ⇒ 放行并刷新计时
            }

            sinceSeconds = since;
            DropCount++;
            return true;                                     // 间隔不足 ⇒ 丢弃（⛔ 不刷新计时）
        }

        /// <summary>
        /// 清空计时与丢弃计数，并把 <see cref="Clock"/> 恢复为默认（<c>null</c>）。
        /// **仅供离线自检宿主**（同一进程里跑多个用例）。
        /// </summary>
        public static void Reset()
        {
            LastPlayAt.Clear();
            DropCount = 0;
            Clock = null;
        }
    }
}
