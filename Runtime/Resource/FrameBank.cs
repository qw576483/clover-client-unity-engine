// ─────────────────────────────────────────────────────────────────────────────
// CloverEngine · Runtime/Resource/FrameBank.cs
// 「整目录抓帧 → 按帧号排序 → 帧号→下标映射 → 统一画布锚点 → 生命周期」的精灵目录件。
//
// 来源：clover-project-cr `client/Assets/Scripts/View/UnitView.cs:945-1346`
//   （该工程里的 `SpriteBank` 静态类；`UnitView` 本体在 `:41-840`，本件只取"目录抓帧 +
//    帧序 + 锚点 + 生命周期"这一段，⛔ 不含任何该工程的单位/玩法语义）。
//
// ═══════════════ 为什么要沉（四条，每条都有事故出处） ═══════════════
//
// ① 帧名 → 帧号解析（`ParseFrameIndex`）——**真机事故**。
//    旧写法取"名字**尾部**的数字串"。Unity 对"一张 PNG 多个子 Sprite"的导入会把 Sprite
//    命名为 `<文件名>_<子序号>` ⇒ 实测名字形如 `frame_000_0`，**尾段 `0` 是子序号**、
//    对它那个 486 帧的目录**恒为 0** ⇒ 486 帧拿到同一个排序键 ⇒ 排序变成空转 ⇒
//    播放顺序退化成"批量加载的偶然顺序"。修法：**从倒数第二段起逐段回退**找纯数字段
//    （`frame_000_0` ⇒ `000` ⇒ 0），只有一段数字时（`frame_003` / `gen_frame_012`）才认最后一段。
//    这条因果链是**资产**：它描述的是"排序键退化后**不报任何错**、只是画面帧序错乱"这类
//    最难查的静默失效，所以在代码里逐字保留。
//
// ② 帧号 → 数组下标映射 + 恒等断言（`FrameNumberMap` / `BuildFrameNumberMap`）。
//    "帧号 ≠ 下标"出现在"一张 PNG 被切成多个子 Sprite"的目录里（导入给的 Sprite 数 > PNG 数）。
//    一旦发生而无人知晓，表现是"取帧取错、动作对不上"——**同样零报错**。
//    ⇒ 建映射时**断言并留痕**（目录名 / 帧数 / 表长 / 是否恒等；非恒等则给出前若干
//    `fN→idx` 作为映射规则证据）。
//
// ③ 统一画布锚点（`UnifyCanvasAnchor`）——**用户报的"贴图抖动"**。
//    多 Sprite 导入给出的 pivot 是**每帧自己那张裁剪框的中心** ⇒ 逐帧换帧时整帧被重新居中。
//    实测该工程一个 486 帧的单位目录里有 **245 个互不相同的锚点**（跨度 0.71×0.61 格）
//    ⇒ 每换一帧人物就在画布上跳一下。修法：取**全目录 `rect` 并集的中心**当锚点，
//    用"相对各自 `rect` 归一化"的 pivot 重建每一帧 ⇒ 每帧按美术自己的画布位置落位，换帧不位移。
//    （`Sprite.Create` 的 `pivot` 参数口径是**相对 `rect` 归一化**、不是相对整张贴图 ——
//     这一条是实测出来的：`pivot = ((anchorX - rect.x) / rect.width, (anchorY - rect.y) / rect.height)`。）
//
// ④ 现造 Sprite 的生命周期（`GeneratedSpritePrefix` + `Clear`）——**"谁造谁 Destroy"**。
//    "拿不到就现造"这条兜底（本类 `LoadViaTexture` / `WhiteSprite`）必须配一条归还路径，
//    否则出图 / 换场景后 `Sprite.Create` 出来的对象没人销毁（静默泄漏，包体越大越明显）。
//    口径：本类只 Destroy **名字带 `GeneratedSpritePrefix` 前缀**的那些；引擎/导入出来的那份
//    归 Unity 资源管，⛔ 本类不碰。
//    ⚠️ 调用方如果 `Game.Res.UnloadAll()` 之后再取帧，拿到的是 Unity 的**"假 null"** ——
//    缓存有效性检查按这一条写（见 `LoadDir`），否则所有精灵会变成"看不见"，零报错。
//
// ═══════════════ 与既有引擎件的边界（⛔ 不是重复实现） ═══════════════
//
// · ⛔ **不是 `SpriteSet` 的重复**（`Runtime/Resource/SpriteSet.cs`）：两者方向相反且互补 ——
//   `SpriteSet` = 「**异步** `Preload` 一批 → 按**名**同步 `TryGet` 单张」，**没有**目录级全量数组、
//   `Get` 不加载、条目可被水位 LRU 淘汰；本类 = 「一次拿到**整目录有序数组** + 按**帧号**定位 +
//   统一锚点」。调用方要的是"第 N 帧动画"（有序数组 + 帧号寻址），`SpriteSet` 给不了；
//   调用方要的是"随用随取单张"（且不介意被淘汰），本类不划算。
//   `SpriteSet` 头部注释也记着同一件事：它**刻意规避**了"批量加载顺序无保证"这个问题（只做按名取），
//   而本类正是去解决它的。
// · ⛔ **不是 `SpriteFrameAnimator` 的重复**（`Runtime/Presentation/SpriteFrameAnimator.cs`）：
//   那个是"帧表 → `SpriteRenderer` 的**推进**器"（`Play` / `Advance` / `FrameIndex`）；
//   本类是"**帧表从哪来 + 怎么排序 + 锚点怎么定 + 谁释放**"。两者正交，可叠加使用。
// · 落点 = `Runtime/Resource/`（资源域）：本件只做"取资源 + 排序 + 重建 Sprite"，不碰渲染器、
//   不碰 MonoBehaviour、不订阅任何事件；符合 `结构规则.md` §二 的"资源加载 / 图集"归属。
//   只依赖 `Core`（`IResourceManager` / `Game.Logger` / `LogThrottle`）⇒ 依赖方向合规。
//
// ═══════════════ 与参考工程的两处**刻意不同**（⛔ 不是照抄） ═══════════════
//   a) 参考实现是 `static class` + 静态缓存 + 自建 `HashSet<string> Warned` 去重 ⇒
//      本件改成**实例 + 可注入 `IResourceManager`**（与 `SpriteSet` 同形，便于离线 / EditMode
//      自检时注入替身），去重改走引擎既有 `LogThrottle`（§已有同类能力不准再起第二套）。
//   b) 参考实现把"兜底 PPU = 100"写死成常量 ⇒ 本件做成构造参数（默认值仍是 Unity 的 100，
//      即不传参时与参考行为一致）。
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using UnityEngine;

namespace CloverEngine
{
    /// <summary>
    /// 精灵目录仓：一次性取某目录**整批**帧（同步）→ 按帧号升序排 → 需要时统一画布锚点 →
    /// 缓存复用；并提供「帧号 → 数组下标」映射与"谁造谁 Destroy"的释放路径。
    /// <para>
    /// <b>用法</b>：
    /// <code>
    /// var bank = new FrameBank();                       // 一般传 null 资源 → 走 Game.Res
    /// var frames = bank.LoadDir("Art/Units/hero_out", FrameBank.PivotMode.UnifiedCanvasAnchor);
    /// var map    = bank.FrameNumberMap("Art/Units/hero_out", FrameBank.PivotMode.UnifiedCanvasAnchor);
    /// var first  = map[182];                            // 帧号 182 → 数组下标（-1 = 该目录没有这一帧）
    /// // 出图 / 换场景时：
    /// bank.Clear();                                     // 只销毁本类现造的 Sprite
    /// </code>
    /// </para>
    /// <para>
    /// <b>同步阻塞</b>：<see cref="LoadDir(string, PivotMode)"/> 走 <see cref="IResourceManager.LoadAll{T}(string)"/>
    /// （**会真的加载**、阻塞主线程，不进缓存/引用计数）。这是"逐帧动画必须一次拿到有序数组"的硬需求
    /// （逐个帧名 `LoadAsset` 取不到，见 `Runtime/Core/Contracts.cs:1107-1124`）⇒ 请在进图前 /
    /// 读条阶段调用，⛔ 不要在战斗热路径里第一次调用。
    /// </para>
    /// <para><b>主线程使用</b>（与 <see cref="LogThrottle"/> 一致，非线程安全）。</para>
    /// </summary>
    public sealed class FrameBank
    {
        /// <summary>日志 tag。</summary>
        private const string Tag = "FrameBank";

        /// <summary>
        /// 现场造出来的 Sprite 的名字前缀 —— <see cref="Clear"/> 靠它区分"本类造的"与"导入/引擎给的"。
        /// </summary>
        public const string GeneratedSpritePrefix = "gen_";

        /// <summary>
        /// 由 `Texture2D` 现场造 `Sprite` 时的**默认** PPU（= Unity 默认值 100 px/单位）。
        /// 做成构造参数（<see cref="FrameBank(IResourceManager, float)"/>）：素材是别的 PPU 时必须显式传，
        /// ⛔ 不许各工程自己再写一个常量（写死会让"导入态 Sprite"与"现造 Sprite"一大一小）。
        /// </summary>
        public const float DefaultPixelsPerUnit = 100f;

        /// <summary>
        /// 帧的**锚点模式**（<see cref="LoadDir(string, PivotMode)"/> 的第二参）。
        /// </summary>
        public enum PivotMode
        {
            /// <summary>
            /// 按导入设置原样用。多 Sprite 导入时**每帧的 pivot 是自己那张裁剪框的中心**
            /// ⇒ 逐帧裁剪框不同 ⇒ 换帧时整帧被重新居中（"贴图抖动"）。
            /// 只在"每档只用一帧"的目录上安全（没有换帧位移问题，且保持既有画面逐像素不变）。
            /// </summary>
            AsImported = 0,

            /// <summary>
            /// 全目录共用一个**纹理坐标锚点**（= 该目录所有帧 `rect` 并集的中心）重建每一帧。
            /// <para>
            /// 逐帧动画目录**必须**走这个：素材若是"一个固定画布 + 每帧一张紧裁剪 PNG"，
            /// 导入态 pivot 会让每换一帧人物在画布上重新居中一次（实测某 486 帧目录里
            /// 有 **245 个互不相同的锚点**、跨度 0.71×0.61 格）。
            /// </para>
            /// <para>
            /// 锚点取"并集中心"而不是"并集底边中点"：① 取中心 ⇒ **不引入任何整体位移**
            /// （内容中心对齐逻辑位置是既有约定；取底边中点会把整目录内容整体上移半个画布高）；
            /// ② 只要锚点是"全目录共用的同一个纹理点"，抖动就已消除，中心 / 底边对抖动**等效**。
            /// </para>
            /// <para>
            /// ⚠️ **代价（登记）**：没有逐帧锚点表时只能"整目录共用一个锚点"，
            /// 因此不同帧之间**原本就存在的落脚线差异**（各帧 `rect.y` 的分布）仍按美术原样保留，
            /// 无法像有逐帧锚点表那样逐帧对齐。消除条件 = 拿到逐帧锚点数据。
            /// </para>
            /// </summary>
            UnifiedCanvasAnchor = 1,
        }

        /// <summary>显式注入的资源管理器；<c>null</c> 时回落到 <see cref="Game.Res"/>。</summary>
        private readonly IResourceManager _resource;

        /// <summary>现造 Sprite 用的 PPU（见 <see cref="DefaultPixelsPerUnit"/>）。</summary>
        private readonly float _fallbackPixelsPerUnit;

        /// <summary>缓存键 → 帧数组。</summary>
        private readonly Dictionary<string, Sprite[]> _cache = new Dictionary<string, Sprite[]>(StringComparer.Ordinal);

        /// <summary>缓存键 → 「帧号 → 下标」表。</summary>
        private readonly Dictionary<string, int[]> _frameNoMap = new Dictionary<string, int[]>(StringComparer.Ordinal);

        /// <summary>共享的 1×1 白块（见 <see cref="WhiteSprite"/>）。</summary>
        private Sprite _white;

        /// <summary>
        /// 「只报一次」的代际：<see cref="Clear"/> 递增后，同一目录的告警会**再报一次**
        /// （换关卡 / 重进游戏后重新加载，值得再提醒一次）。
        /// ⛔ 不调 <see cref="LogThrottle.Reset"/>：那会连带清掉别的系统的限频记录，是越界副作用。
        /// </summary>
        private int _generation;

        /// <param name="resource">
        /// 资源管理器；一般传 <c>null</c>，取 <see cref="Game.Res"/>。
        /// 保留注入口是为了离线 / EditMode 自检与替换实现（引擎门面在 Launch 前为空）。
        /// </param>
        /// <param name="fallbackPixelsPerUnit">
        /// 现造 Sprite 的 PPU；<c>&lt;= 0</c> 时按 <see cref="DefaultPixelsPerUnit"/> 处理（不抛）。
        /// </param>
        public FrameBank(IResourceManager resource = null, float fallbackPixelsPerUnit = DefaultPixelsPerUnit)
        {
            _resource = resource;
            _fallbackPixelsPerUnit = fallbackPixelsPerUnit > 0.01f ? fallbackPixelsPerUnit : DefaultPixelsPerUnit;
        }

        /// <summary>已缓存的目录数（诊断 / 自检用）。</summary>
        public int Count { get { return _cache.Count; } }

        private IResourceManager Res { get { return _resource ?? Game.Res; } }

        // ── 抓帧 ─────────────────────────────────────────────────────────────

        /// <summary>
        /// 取某目录下**全部**帧（同步），按帧号升序排；锚点按导入设置原样。
        /// 等价于 <see cref="LoadDir(string, PivotMode)"/> 传 <see cref="PivotMode.AsImported"/>。
        /// </summary>
        /// <returns>帧数组（长度 0 = 该目录没有可用资源；⛔ 不返回 null）。</returns>
        public Sprite[] LoadDir(string path)
        {
            return LoadDir(path, PivotMode.AsImported);
        }

        /// <summary>
        /// 取某目录下**全部**帧（同步），按帧号升序排，并按 <paramref name="mode"/> 决定锚点。
        /// <para>
        /// ⚠️ **必须自己排序**：批量加载的返回顺序**没有保证**，直接拿来播会出现"帧序乱跳"；
        /// 帧名里的帧号由 <see cref="ParseFrameIndex(string)"/> 解析（见类注释①的因果链）。
        /// </para>
        /// <para>
        /// **降级链**（两级，任一成功即可）：① `LoadAll&lt;Sprite&gt;`；② 空 ⇒ `LoadAll&lt;Texture2D&gt;`
        /// 现场 `Sprite.Create`。为什么要有第 ② 级：`LoadAll&lt;T&gt;` 的 `T` **必须与导入类型匹配** ——
        /// 若这批 PNG 被导成 `Texture` 而不是 `Sprite`，第 ① 级返回空数组（引擎契约：空数组 = 没取到
        /// 且 Warn），于是整个表现层"什么都不显示但也不报错"。第 ② 级让"导入类型不对"不至于把画面
        /// 变成空白。两级都空 ⇒ 返回空数组 + 留痕（⛔ 不抛）。
        /// </para>
        /// </summary>
        /// <param name="path">资源根下的相对目录。</param>
        /// <param name="mode">锚点模式；逐帧动画目录用 <see cref="PivotMode.UnifiedCanvasAnchor"/>。</param>
        /// <returns>帧数组（长度 0 = 该目录没有可用资源；⛔ 不返回 null）。</returns>
        public Sprite[] LoadDir(string path, PivotMode mode)
        {
            if (string.IsNullOrEmpty(path)) return Array.Empty<Sprite>();

            // ⚠️ 缓存键必须带上 mode：同一路径两种 mode 并存时，只用 path 做键会让第二次不同 mode
            //    的调用拿到第一次的结果（静默用错锚点 —— 比崩溃难查得多）。
            var key = Key(mode, path);

            Sprite[] cached;
            // 缓存有效性检查：`Game.Res.UnloadAll()` 之后拿到的会是 Unity 的**"假 null"**；
            // 直接返回缓存会让所有精灵变成看不见（最难查的一类静默失败）⇒ 失效即重新取。
            if (_cache.TryGetValue(key, out cached) && cached != null
                && (cached.Length == 0 || cached[0] != null))
                return cached;

            var frames = Res != null ? Res.LoadAll<Sprite>(path) : null;
            if (frames == null || frames.Length == 0) frames = LoadViaTexture(path);

            if (frames == null || frames.Length == 0)
            {
                frames = Array.Empty<Sprite>();
                LogThrottle.WarnOnce(Tag, "FrameBank.NoFrames[" + _generation + "]/" + key,
                    "取不到任何帧（Sprite / Texture 两条路都空）：" + path +
                    "（检查目录是否存在、以及这批 PNG 是否被导入为 Sprite；⛔ 本目录本帧起按'空'处理）");
            }
            else
            {
                SortByFrameIndex(frames);
                if (mode == PivotMode.UnifiedCanvasAnchor) frames = UnifyCanvasAnchor(frames, key);
            }

            _cache[key] = frames;
            return frames;
        }

        /// <summary>
        /// 精灵目录已缓存的帧（<see cref="PivotMode.AsImported"/> 模式；调试 / 自检用）。
        /// 没缓存过返回空数组（⛔ 不返回 null，也不触发加载）。
        /// </summary>
        public Sprite[] Cached(string path)
        {
            Sprite[] frames;
            return _cache.TryGetValue(Key(PivotMode.AsImported, path), out frames)
                ? frames : Array.Empty<Sprite>();
        }

        // ── 帧号 → 下标 ──────────────────────────────────────────────────────

        /// <summary>
        /// 「帧号 → 帧数组下标」映射表。
        /// <para>
        /// 返回 <c>int[]</c>：<c>map[n]</c> = 帧号 <c>n</c> 对应的下标，取不到 = <c>-1</c>；
        /// 数组长度 = 该目录**最大帧号 + 1**。同帧号取**首次**出现的下标（稳定）。
        /// </para>
        /// <para>
        /// <b>为什么必须断言并留痕</b>（见类注释②）：帧号 ≠ 下标发生在"一张 PNG 被切成多个子 Sprite"
        /// 的目录里，一旦发生而无人知晓，表现就是"取帧取错帧"这类**零报错**的静默错误。
        /// 恒等 ⇒ 一条 Info；非恒等 ⇒ 一条 Warn + 前若干 `fN→idx` 作为映射规则证据。
        /// </para>
        /// </summary>
        /// <param name="path">与 <see cref="LoadDir(string, PivotMode)"/> 同参。</param>
        /// <param name="mode">锚点模式（同一目录不同 mode 各有各的键，避免拿到别的 mode 的映射）。</param>
        /// <returns>映射表（空路径 ⇒ 空数组；⛔ 不返回 null）。</returns>
        public int[] FrameNumberMap(string path, PivotMode mode)
        {
            if (string.IsNullOrEmpty(path)) return Array.Empty<int>();

            var key = Key(mode, path);
            int[] map;
            if (_frameNoMap.TryGetValue(key, out map) && map != null) return map;

            var frames = LoadDir(path, mode);
            map = BuildFrameNumberMap(frames);
            _frameNoMap[key] = map;

            var identity = map.Length == frames.Length;
            for (var n = 0; identity && n < map.Length; n++)
                if (map[n] != n) identity = false;

            var msg = "帧号→下标映射：dir=" + path + " frames.Length=" + frames.Length +
                      " map.Length=" + map.Length +
                      (identity
                          ? " 恒等（1 PNG = 1 Sprite ⇒ 帧号 == 下标）"
                          : " **非恒等**（下标 ≠ 帧号，取帧必须走本表，⛔ 不要按下标取）：" + DescribeFrameMap(map));

            if (identity)
            {
                // Info 没有 *Once 变体 ⇒ 用计数口径的"只打第 1 次"（everyN = int.MaxValue）。
                if (LogThrottle.ShouldLogEvery("FrameBank.MapIdentity[" + _generation + "]/" + key, int.MaxValue))
                    Game.Logger?.Info(Tag, msg);
            }
            else
            {
                LogThrottle.WarnOnce(Tag, "FrameBank.MapNonIdentity[" + _generation + "]/" + key, msg);
            }

            return map;
        }

        /// <summary>由帧数组建「帧号 → 下标」表；同帧号取**首次**出现的下标（稳定）。</summary>
        private static int[] BuildFrameNumberMap(Sprite[] frames)
        {
            var maxNo = -1;
            for (var i = 0; i < frames.Length; i++)
            {
                var n = ParseFrameIndex(frames[i]);
                if (n > maxNo) maxNo = n;
            }

            var map = new int[maxNo + 1];
            for (var n = 0; n < map.Length; n++) map[n] = -1;          // -1 = 该帧号在此目录不存在
            for (var i = 0; i < frames.Length; i++)
            {
                var n = ParseFrameIndex(frames[i]);
                if (n >= 0 && n < map.Length && map[n] < 0) map[n] = i;
            }
            return map;
        }

        /// <summary>把「下标 ≠ 帧号」的前若干项写成一行（断言不恒等时作为映射规则证据留痕）。</summary>
        private static string DescribeFrameMap(int[] map)
        {
            var sb = new System.Text.StringBuilder();
            var shown = 0;
            for (var n = 0; n < map.Length && shown < 8; n++)
            {
                if (map[n] != n)
                {
                    sb.Append('f').Append(n).Append("→idx").Append(map[n]).Append(' ');
                    shown++;
                }
            }
            if (shown == 0) sb.Append("(前 8 项无差异)");
            return sb.ToString();
        }

        // ── 帧序号解析（纯函数，可离线断言） ────────────────────────────────

        /// <summary>
        /// 帧序号 = 名字里**倒数第二段起往回找的第一个纯数字段**；找不到再认最后一段。
        /// 解析不出时返回 <c>-1</c>（⛔ 不抛）。
        /// <para>
        /// <b>为什么不是"尾部数字串"（旧写法，真机上就是它让帧序失效）</b>（见类注释①）：
        /// Unity 对"一张 PNG 多个子 Sprite"的导入会把 Sprite 命名成 <c>&lt;文件名&gt;_&lt;子序号&gt;</c>，
        /// 实测名字形如 <c>frame_000_0</c> —— 尾部那段数字是**子序号**，对那个 486 帧的目录恒为 `0`
        /// ⇒ 486 帧拿到同一个排序键 ⇒ 排序空转 ⇒ 播放顺序变成"批量加载的偶然顺序"。
        /// 倒过来取倒数第二段（`000`）才是真正的帧序号。
        /// </para>
        /// <para>
        /// 口径示例：<c>frame_000_0→0</c>、<c>frame_485_0→485</c>、<c>frame_7_1→7</c>、
        /// <c>frame_003→3</c>、<c>gen_frame_012→12</c>；没有数字段的名字（<c>White1x1</c>）→ <c>-1</c>。
        /// </para>
        /// <para>
        /// ⚠️ **同一 PNG 内的多个子 Sprite 会拿到相同的键** ⇒ 它们之间的先后由**稳定排序**保持为
        /// 导入器给的顺序。这对"1 PNG = 1 Sprite"的逐帧目录无影响；对"一张 PNG 多子 Sprite"的目录
        /// 则是**刻意保持现状**（这类目录由调用方按映射表取帧，排序结果只用于展示）。
        /// </para>
        /// </summary>
        public static int ParseFrameIndex(string name)
        {
            if (string.IsNullOrEmpty(name)) return -1;

            var segs = name.Split('_');
            for (var i = segs.Length - 2; i >= 0; i--)
            {
                if (segs[i].Length == 0 || !IsAllDigits(segs[i])) continue;
                int value;
                if (int.TryParse(segs[i], out value)) return value;
            }

            // 只有一段数字（如 `frame_003` / `gen_frame_012`）时上面找不到 ⇒ 认最后一段
            if (segs.Length > 0 && IsAllDigits(segs[segs.Length - 1]))
            {
                int value;
                if (int.TryParse(segs[segs.Length - 1], out value)) return value;
            }
            return -1;
        }

        private static int ParseFrameIndex(Sprite s)
        {
            return s == null ? -1 : ParseFrameIndex(s.name);
        }

        private static bool IsAllDigits(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            for (var i = 0; i < s.Length; i++) if (s[i] < '0' || s[i] > '9') return false;
            return true;
        }

        /// <summary>
        /// 按帧号**升序**就地排序（<b>稳定</b>：同键保持导入器给的相对顺序）。
        /// <para>
        /// 插入排序：帧数最多约千级、且**几乎已经有序**（批量加载通常按名序）⇒ 不值得引入
        /// `Array.Sort` + 比较器闭包（每次加载都要分配）。⚠️ 键**先算一遍存数组**：
        /// 参考实现里每次比较都重新解析名字（`O(n²)` 次字符串切分），这里只是把同一件事做省一点，
        /// **排序结果与参考实现逐项相同**（同键比较用 `>` ⇒ 遇相等即停 ⇒ 稳定）。
        /// </para>
        /// </summary>
        private static void SortByFrameIndex(Sprite[] frames)
        {
            var keys = new int[frames.Length];
            for (var i = 0; i < frames.Length; i++) keys[i] = ParseFrameIndex(frames[i]);

            for (var i = 1; i < frames.Length; i++)
            {
                var current = frames[i];
                var currentKey = keys[i];
                var j = i - 1;
                while (j >= 0 && keys[j] > currentKey)
                {
                    frames[j + 1] = frames[j];
                    keys[j + 1] = keys[j];
                    j--;
                }
                frames[j + 1] = current;
                keys[j + 1] = currentKey;
            }
        }

        // ── 统一画布锚点 ─────────────────────────────────────────────────────

        /// <summary>
        /// 把一目录的帧统一到**同一个纹理坐标锚点**上重建（见 <see cref="PivotMode.UnifiedCanvasAnchor"/>）。
        /// <para>
        /// 前提（不满足就**降级保原样 + 留痕**，⛔ 不静默）：**所有帧的画布尺寸一致**。
        /// </para>
        /// <para>
        /// ⚠️ 判据是"**画布尺寸一致**"，⛔ **不是**"所有帧来自同一张贴图"：逐帧素材常是"一帧一张 PNG"
        /// （每张只被切成 1 个子 Sprite），而"一张 PNG 多个子 Sprite"是另一类目录。两者的**坐标系是同一个**：
        /// 每张 PNG 都是**同一尺寸的完整画布**，`rect` 就是"内容在这块画布上的位置"。
        /// （参考工程一开始写成"必须同一张贴图"，跑起来对**每一个**逐帧目录都打降级告警 —— 这条错法
        /// 也是资产，写在这里免得后人重踩。）
        /// </para>
        /// <para>
        /// 锚点 = 全目录 `rect` 并集的中心（画布像素坐标）。
        /// <b>换算式</b>（`Sprite.Create` 的 `pivot` 是"**相对 `rect` 归一化**"，不是相对整张贴图 ——
        /// 这条是实测出来的，⛔ 不靠回忆）：
        /// <c>pivot = ((anchorX - rect.x) / rect.width, (anchorY - rect.y) / rect.height)</c>。
        /// </para>
        /// <para>
        /// 重建出来的 Sprite 名字带 <see cref="GeneratedSpritePrefix"/> 前缀 ⇒ <see cref="Clear"/>
        /// 能销毁它们（导入出来的那份归 Unity 资源管，本类 ⛔ 不 Destroy）。
        /// </para>
        /// <para>
        /// ⚠️ 用**每帧自己的**贴图重建，⛔ 不用首帧那张：逐帧素材是一帧一张 PNG，
        /// 用首帧贴图会把别的帧画成第一帧的图。
        /// </para>
        /// <para>
        /// 实例方法（而非静态）不是为了状态 —— 是为了让"降级只报一次"按**本实例的代际**记账；
        /// 判据探针可 `new FrameBank(null)` 后直接调用（它不碰资源管理器）。
        /// </para>
        /// </summary>
        public Sprite[] UnifyCanvasAnchor(Sprite[] frames, string cacheKey)
        {
            if (frames == null || frames.Length == 0) return frames ?? Array.Empty<Sprite>();

            var tex0 = frames[0] != null ? frames[0].texture : null;
            if (tex0 == null)
            {
                WarnAnchorDegrade(cacheKey, "首帧没有贴图（Texture2D 为空）");
                return frames;
            }

            // 画布尺寸必须一致（各帧可以是各自的贴图，但都必须是同一尺寸的完整画布）。
            var canvasW = tex0.width;
            var canvasH = tex0.height;
            for (var i = 1; i < frames.Length; i++)
            {
                var s = frames[i];
                if (s == null || s.texture == null) continue;
                if (s.texture.width != canvasW || s.texture.height != canvasH)
                {
                    WarnAnchorDegrade(cacheKey,
                        "同目录的帧画布尺寸不一致（首帧 " + canvasW + "x" + canvasH + "，第 " + i + " 帧 " +
                        s.texture.width + "x" + s.texture.height + "）—— 无法定义共用锚点");
                    return frames;
                }
            }

            var minX = float.MaxValue; var minY = float.MaxValue;
            var maxX = float.MinValue; var maxY = float.MinValue;
            for (var i = 0; i < frames.Length; i++)
            {
                var s = frames[i];
                if (s == null) continue;
                var r = s.rect;
                if (r.width <= 0.01f || r.height <= 0.01f) continue;
                if (r.xMin < minX) minX = r.xMin;
                if (r.yMin < minY) minY = r.yMin;
                if (r.xMax > maxX) maxX = r.xMax;
                if (r.yMax > maxY) maxY = r.yMax;
            }
            if (minX > maxX || minY > maxY)
            {
                WarnAnchorDegrade(cacheKey, "所有帧的 rect 都无效（宽高 <= 0）");
                return frames;
            }

            var anchorX = (minX + maxX) * 0.5f;
            var anchorY = (minY + maxY) * 0.5f;
            var ppu = frames[0].pixelsPerUnit > 0.01f ? frames[0].pixelsPerUnit : _fallbackPixelsPerUnit;

            var rebuilt = new Sprite[frames.Length];
            var failed = 0;
            for (var i = 0; i < frames.Length; i++)
            {
                var s = frames[i];
                if (s == null) { failed++; continue; }
                var r = s.rect;
                if (r.width <= 0.01f || r.height <= 0.01f) { rebuilt[i] = s; failed++; continue; }

                var pivot = new Vector2((anchorX - r.x) / r.width, (anchorY - r.y) / r.height);
                var fppu = s.pixelsPerUnit > 0.01f ? s.pixelsPerUnit : ppu;
                var made = Sprite.Create(s.texture, r, pivot, fppu);
                made.name = GeneratedSpritePrefix + s.name;
                rebuilt[i] = made;
            }

            if (failed > 0)
            {
                LogThrottle.WarnOnce(Tag, "FrameBank.AnchorPartial[" + _generation + "]/" + cacheKey,
                    failed + " 帧无法重建（rect 无效 / 元素为 null），这些帧保留导入态 —— " +
                    "它们与其余帧的锚点不一致（换帧时会位移）：" + cacheKey);
            }

            Game.Logger?.Info(Tag,
                "统一锚点：" + cacheKey + " 帧=" + frames.Length +
                " 并集=(" + minX.ToString("F0") + "," + minY.ToString("F0") + ")-(" +
                maxX.ToString("F0") + "," + maxY.ToString("F0") + ") " +
                "锚点(纹理像素)=(" + anchorX.ToString("F1") + "," + anchorY.ToString("F1") + ") " +
                "ppu=" + ppu.ToString("F0") + "（逐帧 pivot 已换算成相对各自 rect 的归一化值）");

            return rebuilt;
        }

        /// <summary>降级留痕（每个目录 + 每个代际只报一次）：拿不到贴图 / 画布尺寸不一致 ⇒ 保留导入态 Sprite。</summary>
        private void WarnAnchorDegrade(string cacheKey, string why)
        {
            LogThrottle.WarnOnce(Tag, "FrameBank.AnchorDegrade[" + _generation + "]/" + cacheKey,
                "无法统一锚点（" + why + "）⇒ 保留导入态 Sprite" +
                "（逐帧 pivot = 各自裁剪框中心 ⇒ 换帧会位移）：" + cacheKey);
        }

        // ── 兜底与共享件 ─────────────────────────────────────────────────────

        /// <summary>
        /// 「拿不到 `Sprite` 就取 `Texture` 现场造」的兜底（第 ② 级降级，见 <see cref="LoadDir(string, PivotMode)"/>）。
        /// 造出来的 Sprite 名字带 <see cref="GeneratedSpritePrefix"/> 前缀，由 <see cref="Clear"/> 负责销毁。
        /// </summary>
        private Sprite[] LoadViaTexture(string path)
        {
            var textures = Res != null ? Res.LoadAll<Texture2D>(path) : null;
            if (textures == null || textures.Length == 0) return Array.Empty<Sprite>();

            var sprites = new Sprite[textures.Length];
            for (var i = 0; i < textures.Length; i++)
            {
                var t = textures[i];
                if (t == null) continue;
                var s = Sprite.Create(t, new Rect(0f, 0f, t.width, t.height),
                    new Vector2(0.5f, 0.5f), _fallbackPixelsPerUnit);
                s.name = GeneratedSpritePrefix + t.name;
                sprites[i] = s;
            }

            LogThrottle.WarnOnce(Tag, "FrameBank.TextureFallback[" + _generation + "]/" + path,
                "目录被导成 Texture（不是 Sprite），已现场造 " + sprites.Length +
                " 个 Sprite（PPU=" + _fallbackPixelsPerUnit.ToString("F0") + "）：" + path +
                "（根因是素材导入类型不是 Sprite，⛔ 不是资源没加载出来）");
            return sprites;
        }

        /// <summary>
        /// 共享的 1×1 白色精灵（<c>pixelsPerUnit = 1</c> ⇒ 恰好 1 个世界单位）。
        /// 世界空间纯色底块 / 占位块用它，避免每个视图各造一张贴图（贴图多了会各自打断合批）。
        /// <para>惰性创建一次；由 <see cref="Clear"/> 销毁。</para>
        /// </summary>
        public Sprite WhiteSprite()
        {
            if (_white != null) return _white;

            var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            tex.name = "White1x1";
            tex.SetPixel(0, 0, Color.white);
            tex.Apply(false, false);
            // 贴图与精灵都由本类造 ⇒ 名字带生成前缀（Clear 里按前缀/显式两路都能认出来）。
            _white = Sprite.Create(tex, new Rect(0f, 0f, 1f, 1f), new Vector2(0.5f, 0.5f), 1f);
            _white.name = GeneratedSpritePrefix + "white1x1";
            return _white;
        }

        // ── 生命周期 ─────────────────────────────────────────────────────────

        /// <summary>
        /// 清缓存 + 销毁**本类现造**的 Sprite（"谁造谁 Destroy"）。
        /// <para>
        /// 只销毁名字带 <see cref="GeneratedSpritePrefix"/> 的那些：引擎 / Unity 导入的那份**不归本类释放**
        /// （它由资源模块的引用计数 / 水位管）。⛔ 不要在这里 `Res.UnloadAll()` —— 那是资源模块的职责。
        /// </para>
        /// <para>
        /// 出图 / 换场景 / 内存吃紧时调用；调用后同名目录会重新加载，且「只报一次」的告警**重新武装**。
        /// </para>
        /// </summary>
        public void Clear()
        {
            foreach (var pair in _cache)
            {
                var frames = pair.Value;
                if (frames == null || frames.Length == 0) continue;
                for (var i = 0; i < frames.Length; i++)
                {
                    var s = frames[i];
                    // ⚠️ 必须写全 `UnityEngine.Object.Destroy`：本类**不是** MonoBehaviour，
                    //    没有 `MonoBehaviour.Destroy` 这个实例成员 —— 只写 `Destroy(s)` 会 CS0103
                    //    （参考工程记着这条：真机编译才报，离线 Roslyn 自检当时没抓到）。
                    if (s != null && !string.IsNullOrEmpty(s.name) && s.name.StartsWith(GeneratedSpritePrefix))
                        UnityEngine.Object.Destroy(s);
                }
            }
            _cache.Clear();
            _frameNoMap.Clear();

            if (_white != null)
            {
                var tex = _white.texture;
                UnityEngine.Object.Destroy(_white);
                if (tex != null) UnityEngine.Object.Destroy(tex);   // 白块贴图也是本类造的，一起归还
                _white = null;
            }

            _generation++;                                          // 重新武装「只报一次」
        }

        /// <summary>缓存键 = 锚点模式 + 目录（两个 mode 各存一份，避免静默用错锚点）。</summary>
        private static string Key(PivotMode mode, string path)
        {
            return ((int)mode).ToString() + "|" + path;
        }
    }
}
