// ─────────────────────────────────────────────────────────────────────────────
// CloverEngine · Runtime/Presentation/Sound.cs
// 音效**播放闸门**：缺失只报一次 / 单帧起播上限 / 同 clip 并发上限。
//
// 出处：clover-project-cs16 项目侧的「音效发放闸门」——`client/Assets/Scripts/Module/Audio/SfxService.cs`
//   （201 行：探测状态表 + 单帧计数 + 同音效并发滑动窗口 + 缺失告警计数）与
//   `client/Assets/Scripts/Module/Combat/CombatAudio.cs`（97 行，更早的同款）。两份都在补引擎的三个短板：
//     ① `clip == null` 的缺失分支（`PlaySFX` / `PlaySFXAt` / `PlayVoice` 三处 ＋ `PlayBGM` 一处）
//        **每次调用**都裸打一条 Warn（`Game.Logger` 直发，tag `Sound`）⇒ 脚步 / 命中 / 蜂鸣这类每秒多次的
//        高频路径，只要某个音效没落地就把日志刷爆（真问题反而看不见）；
//     ② 全类**没有**任何"单帧 / 同 clip 并发"闸门 ⇒ 一帧打进几十个音效就会把音源池（32）占满，
//        池满之后 `GetAvailableSource` 只能丢弃后面的音效（枪声/脚步互相顶掉）；
//     ③ 没有"缺失只告警一次"的口径（每次调用都报）。
//   另有一处**日志通道**上的不一致（不是上面三个短板之一，但同属"通用能力该有却没有"）：
//   `GetAvailableSource` 的池满告警自带一个 `bool` 只报一次，而引擎（E-core-06 / E-core-14）已有的
//   `LogThrottle.WarnOnce` / `WarnThrottled` 在本文件里**一处都没用上**（本轮一并归零，见语义约束 ⑥）。
//   本片把这三件收敛进引擎（`结构规则.md` §4.4：**已有能力不够用时优先扩展原实现**，⛔ 不准平行再起一套）。
//
// ⛔ **没有下沉**（那些是业务）：
//   · 音效名表与路径命名口径（`sfx/<短名>` 由项目的 `CsAudioTuning` 约定）；
//   · 分组音量值 / `cs.*` 设置键 / 音量应用策略（轮询、master×sfx）；
//   · "开局预热哪些音效"（`Prewarm` 的列表）；
//   · 项目侧"探测一次、Ready 才播"的缓存 —— 见下一段「为什么不需要第三套」。
//
// 为什么下沉：闸门是**任何项目都要的底座**（高频音效缺失要防刷屏、一帧多音效要限流），留在项目侧
//   ⇒ 每个新项目都要再抄一遍（cs16 已经抄了两份，且两份的告警文案 / 阈值口径已经不一致）。
//
// 语义约束（改一条 = 语义漂移；与 `LogThrottle.cs` 的版式一致）：
//   ① **两个闸门默认 `0` = 不限 = 与本次下沉前逐字一致**：不设闸门时，本文件对播放路径的
//      行为（起播次数 / 日志 / 资源引用计数 / 音源池取源顺序）一个字节都没有变化。
//   ② 两个闸门**只管 SFX / Voice 的起播**（`PlaySFX` / `PlaySFXAt` / `PlayVoice`）：
//      BGM / 分组音量 / 淡入淡出 / `TakeSource` 的池逻辑 / `Dispose` 语义**一律不动**。
//   ③ 超限 ⇒ **丢弃该次播放**（⛔ 不排队、⛔ 不打断正在播的音源），并**限频告警**
//      （`LogThrottle.WarnThrottled`，⛔ 不许裸 `Warn`）；丢弃时归还本次加载的资源引用。
//   ④ `MaxConcurrentPerClip` 的口径 = **同一路径当前正在播的音源数**（真并发，**不是**时间窗计数）。
//      路径含分组前缀（`Sound/SFX/…` / `Sound/Voice/…`）⇒ 不同分组互不干扰。
//   ⑤ 缺失（`clip == null`）**整进程只报一次 / 路径**：`LogThrottle.WarnOnce("Sound", "missing:<path>", …)`，
//      message 文案与 `path` 变量保留原样。**四处 `clip == null` 一视同仁**（`PlayBGM` / `PlaySFX` /
//      `PlaySFXAt` / `PlayVoice`）—— BGM 那条**只换了告警频率**（每次 ⇒ 每路径一次），
//      BGM 的播放 / 淡入淡出 / 请求序号判定一行未动。资源加载失败自身的那条 Error 由资源层负责
//      （它在 `ResourceManager.CompletePending`，**不在本文件**；业务侧要"问一句在不在"用
//      `Game.Res.Exists`，那是引擎的按路径缓存，不属本闸门）。
//   ⑥ 本文件**不再有裸 `Game.Logger?.Warn`**：池满告警（`GetAvailableSource`）也改走
//      `LogThrottle.WarnOnce("Sound", "pool.exhausted", …)` —— 但**池逻辑与 `_poolExhaustedWarned`
//      原样保留**（只换发射通道，"只报一次"的原语义逐字不变）。
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace CloverEngine
{
    // 契约（SoundGroup / ISoundManager）已下沉到 Runtime/Core/PresentationContracts.cs。

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
        /// SFX 音源池容量。★ **2026-09-19 由 8 提到 32**（引擎侧改动，登记见
        /// `clover-project-diablo2/tools/ai-skill/constraints.md` 的「本项目依赖的引擎修复」表）。
        /// <para>
        /// 起因：`clover-project-diablo2` 实测「一次命中会同时触发 挥砍 / 命中 / 受击 / 死亡 / 掉落」
        /// 若干音效，8 个源在 AOE / 群体战下被占满 ⇒ 后续音效走 <see cref="GetAvailableSource"/>
        /// 的池满分支被**静默丢弃**（日志 `音效池（8 个音源）已全部占用，本次播放被丢弃`）。
        /// 多挂的 24 个 <see cref="AudioSource"/> 空载不耗 CPU，**无行为副作用**（纯增益）。
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
        // 淡入淡出按**音源**为键：原实现按分组（BGM）为键，切 BGM 时"新曲淡入"与"旧曲淡出"
        // 共用同一个槽位，第二次 StartFade 会把刚启动的淡入协程 Stop 掉（新 BGM 音量停在 ≈0）。
        private readonly Dictionary<AudioSource, Coroutine> _fades = new();
        // 每个音源当前 clip 对应的资源路径：换 clip 时归还旧引用、Dispose 时全部归还
        //（原实现从不 Release，每个播放过的音频引用计数只增不减、永驻资源缓存）。
        private readonly Dictionary<AudioSource, string> _clipPaths = new();
        // 每个音源当前播放的音频分组：原实现靠 clip 名猜分组（含 "Voice" 才算人声），命名不匹配就设错音量。
        private readonly Dictionary<AudioSource, SoundGroup> _srcGroups = new();
        // PlayBGM 的请求序号：异步加载完成时若已有更新的请求（或已 Dispose）则丢弃旧结果（防双 BGM 同播 / 旧曲覆盖新曲）。
        private int _bgmRequestId;
        private bool _poolExhaustedWarned;
        // ── 播放闸门（E-core-17）的记账：**只有需要时才用**，默认值下恒定不参与 ──
        // 单帧起播计数 + 它属于哪一帧。计数只在**真正起播**（异步回调里、取到音源之前）时累加，
        // 所以"加载晚到的音效"算在它真正响的那一帧，而不是它被请求的那一帧。
        private int _framePlays;
        private int _frameOfPlays = -1;
        // 被 App.Pause 真正暂停过的音源。AudioSource.isPlaying 在暂停态返回 false，
        // 恢复时不能靠它反推"哪些原本在播"，必须自己记账。
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
                //   原实现全部挂在 [Sound] 根节点上，PlaySFXAt 设 transform.position 等于
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
            // 这两个事件此前全仓只有 Emit、没有订阅者（死事件）；这里接上引擎内的最小消费者：
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

        // ── 播放闸门（E-core-17）：两个都可配，默认 0 = 不限 = 与下沉前逐字一致 ──────────
        //
        // 为什么要暴露在 ISoundManager 上（而不是只做 SoundManager 的内部字段）：
        // 实现类 internal（G1），业务只拿得到 `Game.Sound`（接口）—— 挂在实现类上等于"业务配不了"，
        // 那闸门就只能永远用默认值（= 没有闸门），等于白做。
        // 取值来源仍是**业务**（例如 cs16 的 `CsAudioTuning.MaxPlaysPerFrame` / `MaxConcurrentPerClip`），
        // 引擎这里只是执行口径（与 `LogThrottle` 的 `everyN` 由业务传入同一分工）。

        /// <inheritdoc/>
        public int MaxPlaysPerFrame { get; set; }

        /// <inheritdoc/>
        public int MaxConcurrentPerClip { get; set; }

        /// <summary>
        /// 播放闸门：本次是否允许**真正起播** <paramref name="path"/>。
        /// <para>
        /// 两个闸门都默认 <c>0</c>（= 不限）：此时本方法**不记账、不判理由**，恒定放行 ——
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
                    // 缺失只报一次/路径（E-core-17）：BGM 也是 `clip == null` 的缺失分支，与下面三处
                    // SFX / Voice 同口径 —— 文件头语义约束 ⑤ 说的就是**所有**缺失只报一次。
                    // 原先这里是**每次 PlayBGM** 都裸打一条 Warn（缺 BGM 的工程每次切曲都刷一条）。
                    // 只换日志发射通道：BGM 的播放 / 淡入淡出 / 请求序号判定一行未动。
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
                    // 缺失只报一次/路径（E-core-17）：原先是**每次调用**一条裸 Warn —— 脚步 / 命中
                    // 这种每秒多次的高频路径一旦某个音效没落地，日志就被它刷爆（真问题反而看不见）。
                    LogThrottle.WarnOnce("Sound", "missing:" + path, $"音效加载失败（clip 为空）：{path}");
                    return;
                }
                // 播放闸门（默认 0 = 不限 ⇒ 与下沉前逐字一致）；超限则归还本次加载的引用并丢弃。
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
                    // 同上：缺失只报一次/路径（E-core-17），⛔ 不再是每次一条裸 Warn。
                    LogThrottle.WarnOnce("Sound", "missing:" + path, $"3D 音效加载失败（clip 为空）：{path}");
                    return;
                }
                // 播放闸门（默认 0 = 不限 ⇒ 与下沉前逐字一致）；超限则归还本次加载的引用并丢弃。
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
                    // 同上：缺失只报一次/路径（E-core-17），⛔ 不再是每次一条裸 Warn。
                    LogThrottle.WarnOnce("Sound", "missing:" + path, $"人声加载失败（clip 为空）：{path}");
                    return;
                }
                // 播放闸门（默认 0 = 不限 ⇒ 与下沉前逐字一致）；超限则归还本次加载的引用并丢弃。
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
            // 甚至到点后把刚重启的音源再 Stop 一次（原实现只停音源、不清 _fades）。
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

            // 归还全部音频的资源引用（原实现只置 clip=null，引用计数只增不减、音频永驻缓存）。
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

                // 用**播放时记录的分组**判定（原实现靠 clip 名是否含 "Voice"/"_voice" 猜分组：
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
                // 否则音量卡在中途、_fades 项也永远清不掉（同 E1 的 Timer unscaled 教训）。
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

            // 池全忙：原实现强制返回 _sfxPool[0]，把正在播放的音效 / 人声**静默打断**。
            // 改为丢弃本次播放并留一次告警（只报一次，避免高频音效刷屏）。
            // E-core-17 只换**日志发射通道**（裸 Warn ⇒ LogThrottle）：取源失败后 return null 的池逻辑
            // 一行未动，`_poolExhaustedWarned` 也**原样保留** —— 于是"只报一次"的语义逐字不变。
            if (!_poolExhaustedWarned)
            {
                _poolExhaustedWarned = true;
                LogThrottle.WarnOnce("Sound", "pool.exhausted",
                    $"音效池（{_sfxPool.Count} 个音源）已全部占用，本次播放被丢弃；高频音效请错峰或提高池容量");
            }
            return null;
        }
    }
}
