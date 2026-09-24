// ─────────────────────────────────────────────────────────────────────────────
// CloverEngine · Runtime/Presentation/SpriteEntityView.cs
// 2D 精灵实体视图**来源**：把「一个实体」变成「场景里一个 SpriteRenderer 节点」——
// 建节点 / 异步贴图（请求序号守卫）/ 逐帧动画 / 纵深排序 / 销毁回收。
//
// ⛔ ★ 缺陷（本件补的就是它）：引擎的实体视图骨架此前**只有 3D 一条路**
//   （`Runtime/Presentation/Entity.cs` 的 `EntityViewFactory`：3D 模型 + `AnimatorController`），
//   2D 精灵项目拿不到「建 / 绑 / 销」骨架 ⇒ 只能自建第二套实体视图生命周期。
//   ★ 影响范围：所有 2D / 像素风项目。
//   本件把 2D 那条路补进引擎，经 `EntityViewFactory` 的**视图来源接缝**接管
//   （契约 = `IEntityViewSource`，见 `Runtime/Presentation/Entity.cs`），业务不再自建。
//
// ★ 复用（⛔ 不新造轮子）：
//   · 建节点：`SpriteRenderer`（Unity 内置）；
//   · 帧表 → SpriteRenderer：`SpriteFrameAnimator`（Runtime/Presentation/SpriteFrameAnimator.cs，
//     `Play(frames, fps, loop)` / `Advance(dt)`）；
//   · 纵深排序：`SortingLayers`（层级预算 + 深度序 + 同序确定性次级键，
//     Runtime/Presentation/SortingLayers.cs，`DepthOrder(worldY)` / `TiebreakOffset(id)`）；
//   · 节点复用：`IObjectPool`（代码工厂缝 `Register` / `Spawn` / `Despawn`，
//     Runtime/Core/EntityPool.cs:107-142；战斗内 GameObject 一律走池）；
//   · 异步贴图：`Game.Res.LoadAsset<Sprite>(path, cb)`（Runtime/Core/Contracts.cs:1216）；
//   · 占位块：本件自造 1×1 白块（口径照 `Runtime/Resource/FrameBank.cs:592 WhiteSprite()`，
//     PPU = 1 ⇒ 恰好 1 个世界单位）。⛔ **不能直接借 `FrameBank`**：它属 `CloverEngine.Resource`
//     程序集，而 `CloverEngine.Presentation.asmdef` 的引用只有 `CloverEngine.Core`（结构规则 §4.3）。
//
// ★ 与 3D 路径互斥（⛔ 不是叠加）：来源被 `EntityViewFactory` 选中并 `Build` 成功后，
//   工厂不会再建胶囊占位 / 不加载 3D 模型 / 不接 `AnimatorController`。
//   **根节点仍由工厂创建、由工厂销毁**（与 3D 同权）⇒ 本件的 `Release` **只**释放自己的记录
//   （贴图引用 + 池化子节点），⛔ 绝不许销毁根节点（见 `IEntityViewSource` 边界 ①）。
//
// ★ 不按包围盒归一化身高（与 3D 路径刻意不同）：2D 逐帧目录里每帧 `rect` 不同，
//   逐帧按实测包围盒归一化会让「同一角色的不同帧缩放不同」= 换帧抖动。
//   2D 的尺寸口径 = PPU + 导入设置（多帧目录要统一画布锚点，见 `FrameBank.PivotMode.UnifiedCanvasAnchor`）。
//   ⇒ `EntityViewSpec.TargetHeight` 在本件**不参与**（给了只记一条 Info 说明，不静默忽略）。
//
// ★ 驱动：`SpriteFrameAnimator` 没有 MonoBehaviour / 协程，只提供 `Advance(dt)`
//   ⇒ 本件提供 `Tick(dt)` 作为统一推进点，由业务在自己的 Tick 里调一次。
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using UnityEngine;

namespace CloverEngine
{
    /// <summary>
    /// 2D 精灵实体视图来源（<see cref="IEntityViewSource"/> 的 2D 实现）。
    ///
    /// <para>
    /// <b>用法</b>：
    /// <code>
    /// // ① 纯 2D 项目：注册成默认来源 ⇒ 本项目全部实体走精灵视图
    /// CloverPresentation.EntityView.RegisterSource(
    ///     new SpriteEntityViewSource(layers: myLayers), asDefault: true);
    ///
    /// // ② 每帧 / 每 Tick
    /// spriteSource.Tick(dt);                          // 推进解码好的逐帧动画
    /// spriteSource.ApplyDepth(id, root.transform.position.y);   // 纵深序（SortingLayers）
    ///
    /// // ③ 贴图 / 帧表
    /// //    a) CreateView 的 spec.ModelPath 非空 ⇒ 异步加载**单张贴图**（请求序号守卫）
    /// //    b) spriteSource.LoadFrames(id, framePaths, fps, loop);  // 异步逐帧加载（同一守卫）
    /// //    c) spriteSource.PlayFrames(id, frames, indices, fps);   // 帧表已就绪（谁加载谁 Release）
    ///
    /// // ④ 实体离场
    /// Game.Entity.Destroy(id);   // 或 DestroyGroup / ClearAll —— 视图销毁时自动归还贴图引用与池化节点
    /// </code>
    /// </para>
    ///
    /// <para>
    /// <b>资源生命周期</b>：本件经 `Game.Res.LoadAsset&lt;Sprite&gt;` 加载的每一张（<c>spec.ModelPath</c> 与
    /// <see cref="LoadFrames"/> 的帧路径）都各占一份引用计数，在 <see cref="Release"/> 里归还；
    /// 加载在途时 <see cref="Release"/> 命中不了缓存（契约：忽略），由回调里的**请求序号守卫**补归还
    /// —— 两处配对，漏一处就是引用计数永久悬挂（与 3D 工厂的竞态口径逐字一致，
    /// 见 `Runtime/Presentation/Entity.cs` 的 `CreateView` / `ReleaseView`）。
    /// </para>
    ///
    /// <para><b>主线程专用</b>（与 `LogThrottle` / `ObjectPool` 一致，非线程安全）。</para>
    /// </summary>
    public sealed class SpriteEntityViewSource : IEntityViewSource
    {
        /// <summary>日志 tag（引擎惯例：各能力用类名自报家门）。</summary>
        private const string Tag = "SpriteEntityView";

        /// <summary>
        /// 内部贴图节点的对象池键（代码工厂缝，见 <see cref="IObjectPool.Register"/>）。
        /// 与预制体路径同命名空间，故取带前缀的名字避免撞名。
        /// </summary>
        public const string SpriteNodePoolKey = "clover.entity.sprite";

        /// <summary>自造 1×1 占位贴图 / 精灵的名字前缀（谁造谁 Destroy 的口径与 `FrameBank` 一致）。</summary>
        public const string GeneratedPlaceholderName = "gen_entityview_white1x1";

        /// <summary>每个视图一份记录（不上任何业务类型）。</summary>
        private sealed class Rec
        {
            /// <summary>根节点下的贴图节点（<see cref="SpriteRenderer"/> 所在；可能来自对象池）。</summary>
            public GameObject Node;

            /// <summary>贴图渲染器。</summary>
            public SpriteRenderer Renderer;

            /// <summary>帧动画器（复用 <see cref="SpriteFrameAnimator"/>，一个视图一个）。</summary>
            public SpriteFrameAnimator Anim;

            /// <summary>本视图占用过引用的资源路径（≤ 去重后归还）。</summary>
            public List<string> Paths;

            /// <summary>本视图当前生效的**请求序号**（< 记录里的序号 = 回调作废）。</summary>
            public long RequestSeq;

            /// <summary>是否仍在加载贴图（<see cref="IEntityViewSource.IsLoading"/>）。</summary>
            public bool Loading;

            /// <summary>节点是否来自对象池（决定释放走 <c>Despawn</c> 还是直接 <c>Destroy</c>）。</summary>
            public bool Pooled;

            /// <summary>帧表异步加载已到位的张数（凑满才起播）。</summary>
            public int FrameArrived;

            /// <summary>
            /// 本记录是否已被 <see cref="Release"/> 处理过。
            /// <para>
            /// 它决定「在途加载完成时」该不该由回调补归还引用：本记录已被释放 ⇒ 那次 <c>Release(path)</c>
            /// 多半命中了空缓存（契约：在途时忽略）⇒ **回调必须补一刀**；本记录还活着 ⇒ 归还留给
            /// <see cref="Release"/> 统一做（否则同一份引用被"回调 + Release"扣两次）。
            /// </para>
            /// </summary>
            public bool Released;
        }

        private readonly Dictionary<long, Rec> _views = new Dictionary<long, Rec>();

        /// <summary>筛选器（<c>null</c> = 全部认领；注册成默认来源时本筛选器不参与）。</summary>
        private readonly Func<EntityViewSpec, bool> _accepts;

        /// <summary>2D 层级预算 / 深度序 / 次级键（业务给；<c>null</c> = 不写 <c>sortingOrder</c>）。</summary>
        private readonly SortingLayers _layers;

        private readonly IResourceManager _resOverride;
        private readonly IObjectPool _poolOverride;

        /// <summary>全局请求序号（自增；每次「新建加载」取一个新号，晚到的旧回调据此作废）。</summary>
        private long _seq;

        /// <summary>池工厂是否已注册（同一 key 重复注册会覆盖 + 留 Info，故只注册一次）。</summary>
        private bool _poolRegistered;

        /// <summary>自造的 1×1 占位精灵 / 其贴图（最后一个视图释放时一并销毁）。</summary>
        private Sprite _placeholder;
        private Texture2D _placeholderTex;

        /// <summary>占位色（中性灰：与 3D 路径同口径，见 `Runtime/Presentation/Entity.cs` 的 `BuildPlaceholder`）。
        /// 占位物必须一眼看出「这是占位」，不能看着像角色。</summary>
        private static readonly Color DefaultPlaceholderColor = new Color(0.45f, 0.45f, 0.48f);

        /// <param name="accepts">
        /// 规格筛选器：<c>null</c> = 全部认领（纯 2D 项目注册成**默认来源**时就是这样）；
        /// 3D 项目里只让某类实体走精灵视图时，按**自己的资源路径约定**筛选（例：<c>spec =&gt; spec.ModelPath.StartsWith("UI/Icons/")</c>）。
        /// </param>
        /// <param name="layers">
        /// 2D 纵深排序配置（**建议给**）：给了才写 <c>SpriteRenderer.sortingOrder</c> 与同序次级键 z；
        /// 不给 ⇒ 用 Unity 默认顺序（俯视 2D 项目会表现为前后遮挡不对）。
        /// </param>
        /// <param name="res">资源管理器；一般传 <c>null</c>，回落 <c>Game.Res</c>（离线 / EditMode 自检可注入）。</param>
        /// <param name="pool">对象池；一般传 <c>null</c>，回落 <c>Game.Pool</c>。</param>
        public SpriteEntityViewSource(Func<EntityViewSpec, bool> accepts = null, SortingLayers layers = null,
            IResourceManager res = null, IObjectPool pool = null)
        {
            _accepts = accepts;
            _layers = layers;
            _resOverride = res;
            _poolOverride = pool;

            if (_layers == null)
            {
                // 非预期分支（配置缺失）：不写 sortingOrder 不会报错，只会「前后遮挡不对」⇒ 留一条说明。
                LogThrottle.InfoCounted(Tag, "layers.none",
                    "未传 SortingLayers ⇒ 不写 sortingOrder（用 Unity 默认顺序）；" +
                    "俯视 2D 项目应传，否则单位之间的前后遮挡不对", int.MaxValue);
            }
        }

        private IResourceManager Res => _resOverride ?? Game.Res;
        private IObjectPool Pool => _poolOverride ?? Game.Pool;

        /// <summary>当前在管的视图数（调试 / 自检用）。</summary>
        public int Count => _views.Count;

        /// <inheritdoc />
        public bool CanBuild(EntityViewSpec spec) => _accepts == null || _accepts(spec);

        /// <inheritdoc />
        public bool Build(long objectID, GameObject root, EntityViewSpec spec)
        {
            if (root == null) return false;

            var node = TakeNode(root.transform, out var pooled);
            if (node == null) return false;   // 取不到节点 ⇒ 交回工厂回落 3D 路径（不静默产空对象）

            var sr = node.GetComponent<SpriteRenderer>();
            if (sr == null) sr = node.AddComponent<SpriteRenderer>();

            var color = spec.PlaceholderColor == default ? DefaultPlaceholderColor : spec.PlaceholderColor;
            sr.sprite = PlaceholderSprite();
            sr.color = color;

            var rec = new Rec
            {
                Node = node,
                Renderer = sr,
                Anim = new SpriteFrameAnimator(sr),
                Pooled = pooled,
                Paths = null,
            };
            _views[objectID] = rec;

            if (_layers != null) ApplyDepthTo(rec, objectID, root.transform.position.y);
            else sr.sortingOrder = 0;   // 池里复用的节点会带着**上一个视图**的 sortingOrder ⇒ 显式归零（Unity 默认顺序）

            if (spec.TargetHeight > 0f)
            {
                // 刻意不做包围盒归一化（见文件头）——但要说明「规格里给了却没生效」，不静默忽略。
                LogThrottle.InfoCounted(Tag, "targetHeight.ignored",
                    "2D 精灵视图不按包围盒归一化身高（逐帧 rect 不同 ⇒ 会换帧抖动）；" +
                    "尺寸请由 PPU + 导入设置决定，TargetHeight 在本来源不参与", int.MaxValue);
            }

            if (!string.IsNullOrEmpty(spec.ModelPath))
                LoadSingle(objectID, spec.ModelPath, rec);

            return true;
        }

        /// <inheritdoc />
        public bool IsLoading(long objectID)
        {
            return _views.TryGetValue(objectID, out var rec) && rec.Loading;
        }

        /// <summary>
        /// 2D 视图没有 <see cref="IAnimPlayer"/>（那是 `AnimatorController` 的口径）⇒ 显式接口返回 <c>null</c>；
        /// 取 2D 的帧动画器请用 <see cref="GetAnimator(long)"/>（返回 <see cref="SpriteFrameAnimator"/>）。
        /// </summary>
        IAnimPlayer IEntityViewSource.GetAnimator(long objectID) => null;

        /// <summary>取该实体的帧动画器；不存在返回 <c>null</c>。</summary>
        public SpriteFrameAnimator GetAnimator(long objectID)
        {
            return _views.TryGetValue(objectID, out var rec) ? rec.Anim : null;
        }

        /// <summary>取该实体的贴图节点（根节点下的 <c>Sprite</c> 子节点）；不存在返回 <c>null</c>。</summary>
        public GameObject GetNode(long objectID)
        {
            return _views.TryGetValue(objectID, out var rec) ? rec.Node : null;
        }

        /// <inheritdoc />
        public void Release(long objectID)
        {
            if (!_views.TryGetValue(objectID, out var rec)) return;
            _views.Remove(objectID);   // 先摘表 ⇒ 在途回调的「请求序号守卫」立刻失败（结果被丢弃并归还引用）
            rec.Released = true;       // 在途回调据此知道「这次 Release 多半是空操作 ⇒ 由我来补归还」

            var res = Res;
            if (res != null && rec.Paths != null)
            {
                // 同路径重复登记（同一张图既是首帧又是当前档）只归还一次，否则引用计数会被多扣。
                var seen = new HashSet<string>(StringComparer.Ordinal);
                for (var i = 0; i < rec.Paths.Count; i++)
                {
                    var p = rec.Paths[i];
                    if (!string.IsNullOrEmpty(p) && seen.Add(p)) res.Release(p);
                }
                rec.Paths = null;
            }

            if (rec.Node != null)
            {
                if (rec.Pooled && Pool != null) Pool.Despawn(rec.Node);   // 归还对象池（池会 SetParent 到池根并置非活跃）
                else UnityEngine.Object.Destroy(rec.Node);
                rec.Node = null;
                rec.Renderer = null;
            }

            rec.Anim = null;

            // 谁造谁 Destroy：最后一个视图也没了 ⇒ 销毁自造的 1×1 占位贴图（下一个视图会重新造一张，代价 = 1 像素）
            if (_views.Count == 0) DestroyPlaceholder();
        }

        // ── 贴图 / 帧表 ──────────────────────────────────────────────────────

        /// <summary>
        /// 推进所有视图的逐帧动画（<see cref="SpriteFrameAnimator"/> 只提供 <c>Advance(dt)</c>，
        /// 由业务 Tick 驱动 —— 本方法就是那个统一推进点，业务每帧调一次即可）。
        /// </summary>
        public void Tick(float dt)
        {
            if (_views.Count == 0) return;
            foreach (var kv in _views)
            {
                var anim = kv.Value.Anim;
                if (anim != null && anim.IsPlaying) anim.Advance(dt);
            }
        }

        /// <summary>
        /// 按世界 y 写纵深序（复用 <see cref="SortingLayers"/> 的层级预算 + 深度序 + 同序次级键）。
        /// <para>业务在实体位置变化后调（世界尺寸由业务的 <see cref="SortingLayers"/> 实例定义）。</para>
        /// </summary>
        public void ApplyDepth(long objectID, float worldY)
        {
            if (!_views.TryGetValue(objectID, out var rec)) return;
            ApplyDepthTo(rec, objectID, worldY);
        }

        /// <summary>
        /// 直接摆一张**已就绪**的贴图（谁加载谁 Release：本件不接管它的引用计数）。
        /// </summary>
        public void SetSprite(long objectID, Sprite sprite, bool resetColor = true)
        {
            if (!_views.TryGetValue(objectID, out var rec) || rec.Renderer == null) return;
            rec.RequestSeq = ++_seq;      // 让在途的异步加载结果作废（避免它稍后把这张覆盖掉）
            rec.Loading = false;
            rec.Renderer.sprite = sprite;
            if (resetColor) rec.Renderer.color = Color.white;
        }

        /// <summary>
        /// 绑一张**已就绪**的帧表（复用 <see cref="SpriteFrameAnimator.Play(Sprite[], float, bool)"/>）。
        /// <para>帧表由调用方加载 / 归还（口径同 `SpriteFrameAnimator`：⛔ 本件不加载、也不 Release 它）；
        /// 逐帧目录请先抓好帧（引擎 `FrameBank.LoadDir(dir, PivotMode.UnifiedCanvasAnchor)`）。</para>
        /// </summary>
        /// <param name="indices">帧下标切片（可多区间 / 乱序）；<c>null</c> = 整表按序播。</param>
        public void PlayFrames(long objectID, Sprite[] frames, int[] indices, float fps, bool loop = true)
        {
            if (!_views.TryGetValue(objectID, out var rec) || rec.Anim == null) return;
            rec.RequestSeq = ++_seq;      // 在途的异步加载结果作废（这次是明确指令，以它为准）
            rec.Loading = false;
            if (indices == null) rec.Anim.Play(frames, fps, loop);
            else rec.Anim.Play(frames, indices, fps, loop);
            if (rec.Renderer != null) rec.Renderer.color = Color.white;   // 有真帧了 ⇒ 撤掉占位染色
        }

        /// <summary>
        /// **异步**逐张加载帧表（经 <c>Game.Res.LoadAsset&lt;Sprite&gt;</c>），全部有结果后按 <paramref name="fps"/> 起播。
        /// <para>
        /// 加载期间保持占位块（不闪空白）；缺帧被**跳过**（不是填空 sprite —— 那会闪一个纯色块），
        /// 缺失张数留一条降频 Warn。全部缺 ⇒ 保持占位并留 Warn（画面仍可见，便于定位路径写错）。
        /// </para>
        /// <para>
        /// 帧顺序 = <paramref name="paths"/> 的顺序。**本件接管这些路径的引用计数**
        /// （每张 <c>+1</c>，<see cref="Release"/> 时归还；在途时由请求序号守卫补归还）。
        /// </para>
        /// </summary>
        public void LoadFrames(long objectID, IList<string> paths, float fps, bool loop = true)
        {
            if (!_views.TryGetValue(objectID, out var rec)) return;
            if (paths == null || paths.Count == 0)
            {
                LogThrottle.WarnOnce(Tag, "LoadFrames.empty",
                    "LoadFrames 收到空帧表（不播、保持当前画面）；检查帧路径表怎么来的");
                return;
            }

            var res = Res;
            if (res == null)
            {
                // 非预期分支：资源模块未挂（漏了 CloverRes.Init）⇒ 必须留痕，否则只剩占位块且查不到原因。
                Game.Logger?.Error(Tag,
                    $"资源模块不可用（Game.Res 为空），实体 {objectID} 的 {paths.Count} 张帧贴图不会加载", null);
                return;
            }

            var total = paths.Count;
            var seq = ++_seq;                       // ★ 请求序号守卫
            rec.RequestSeq = seq;
            rec.Loading = true;
            rec.FrameArrived = 0;
            Remember(rec, paths);   // 追加登记（不清旧账：旧路径的引用可能还在归还流程里）

            var frames = new Sprite[total];
            var first = paths[0];

            for (var i = 0; i < total; i++)
            {
                var idx = i;
                var path = paths[i];
                res.LoadAsset<Sprite>(path, sprite =>
                {
                    // ★ 竞态：本次请求已被作废（实体已离场 / 视图已重建 / 又下了一条指令）
                    //   ⇒ 丢弃并归还这次加载的引用（与 3D 工厂同一口径：两处配对，漏一处引用计数悬挂）。
                    if (!_views.TryGetValue(objectID, out var cur) || !ReferenceEquals(cur, rec)
                        || cur.RequestSeq != seq)
                    {
                        // 补归还只在「本记录已被释放」时做（见 Rec.Released）。
                        if (sprite != null && rec.Released) res.Release(path);
                        Game.Logger?.Info(Tag,
                            $"帧贴图加载完成时请求已作废（请求 #{seq}），丢弃结果：id={objectID} path={path}");
                        return;
                    }

                    frames[idx] = sprite;
                    cur.FrameArrived++;
                    if (cur.FrameArrived < total) return;

                    cur.Loading = false;
                    var ready = Compact(frames, out var missing);
                    if (missing > 0)
                    {
                        // 非预期分支：部分帧取不到（路径写错 / 导入类型不是 Sprite）⇒ 跳过 + 留痕
                        LogThrottle.WarnOnce(Tag, "frames.missing/" + first,
                            $"{missing}/{total} 帧贴图缺失（已跳过这些帧，其余照播）：id={objectID} 首帧={first}");
                    }
                    if (ready.Length == 0)
                    {
                        Game.Logger?.Warn(Tag,
                            $"全部 {total} 帧都取不到，保留占位块（检查帧路径与导入类型）：id={objectID} 首帧={first}");
                        return;
                    }
                    cur.Anim.Play(ready, fps, loop);
                    if (cur.Renderer != null) cur.Renderer.color = Color.white;
                });
            }
        }

        // ── 内部 ────────────────────────────────────────────────────────────

        /// <summary>异步加载**单张贴图**（<c>spec.ModelPath</c>）：占位块先顶着，真图到位后替换并撤掉染色。</summary>
        private void LoadSingle(long objectID, string path, Rec rec)
        {
            var res = Res;
            if (res == null)
            {
                Game.Logger?.Error(Tag,
                    $"资源模块不可用（Game.Res 为空），实体 {objectID} 的贴图无法加载：{path}", null);
                return;
            }

            var seq = ++_seq;                 // ★ 请求序号守卫
            rec.RequestSeq = seq;
            rec.Loading = true;
            Remember(rec, path);

            res.LoadAsset<Sprite>(path, sprite =>
            {
                if (!_views.TryGetValue(objectID, out var cur) || !ReferenceEquals(cur, rec)
                    || cur.RequestSeq != seq)
                {
                    // 补归还只在「本记录已被释放」时做（见 Rec.Released）：记录还活着 ⇒ 归还留给 Release，
                    // 否则同一份引用被扣两次。
                    if (sprite != null && rec.Released) res.Release(path);
                    Game.Logger?.Info(Tag,
                        $"贴图加载完成时实体已离场 / 请求已作废（请求 #{seq}），丢弃并归还引用：id={objectID} path={path}");
                    return;
                }

                cur.Loading = false;
                if (sprite == null)
                {
                    // 非预期分支：贴图没加载出来（路径写错 / 未导入为 Sprite）⇒ 留痕 + 保持占位块（⛔ 不静默变透明）
                    LogThrottle.WarnOnce(Tag, "sprite.missing/" + path,
                        $"贴图加载失败，保留占位块：id={objectID} path={path}");
                    return;
                }

                cur.Renderer.sprite = sprite;
                cur.Renderer.color = Color.white;   // 真图到位 ⇒ 撤掉占位染色（与 3D「模型到位移除占位」同口径）
            });
        }

        /// <summary>取一个贴图节点：优先对象池的代码工厂缝（战斗内 GameObject 一律走池），池不可用才直接 new。</summary>
        private GameObject TakeNode(Transform parent, out bool pooled)
        {
            var pool = Pool;
            if (pool != null)
            {
                if (!_poolRegistered)
                {
                    pool.Register(SpriteNodePoolKey, CreateSpriteNode);
                    _poolRegistered = true;
                }

                var go = pool.Spawn(SpriteNodePoolKey, parent);
                if (go != null)
                {
                    // 池中复用的节点名字可能是旧的（Spawn 不改名）⇒ 统一成节点名，便于排障
                    go.name = "Sprite";
                    pooled = true;
                    return go;
                }

                // 非预期分支：池里有 key 但造不出对象（工厂被注销 / 池异常）⇒ 留痕并回落直接 new
                LogThrottle.WarnOnce(Tag, "pool.spawn.failed",
                    $"对象池取节点失败（key={SpriteNodePoolKey}）⇒ 本次回落直接 new；检查是否有别的代码注销了该 key");
            }

            pooled = false;
            var node = new GameObject("Sprite");
            node.transform.SetParent(parent, false);
            return node;
        }

        /// <summary>池工厂：造一个只带 <see cref="SpriteRenderer"/> 的节点（⛔ 不含任何素材 / 配色）。</summary>
        private static GameObject CreateSpriteNode()
        {
            var go = new GameObject("Sprite");
            go.AddComponent<SpriteRenderer>();
            return go;
        }

        /// <summary>
        /// 登记本视图占用过引用的资源路径（<see cref="Release"/> 时归还）。
        /// <para>刻意**追加**而不是替换：重复的 <see cref="LoadFrames"/> / <see cref="SetSprite"/> 交错时，
        /// 被替换掉的那份清单里的引用就再也没人归还了。去重放在归还那一步做。</para>
        /// </summary>
        private static void Remember(Rec rec, string path)
        {
            if (rec == null || string.IsNullOrEmpty(path)) return;
            rec.Paths ??= new List<string>();
            rec.Paths.Add(path);
        }

        private static void Remember(Rec rec, IList<string> paths)
        {
            if (rec == null || paths == null) return;
            for (var i = 0; i < paths.Count; i++) Remember(rec, paths[i]);
        }

        private void ApplyDepthTo(Rec rec, long objectID, float worldY)
        {
            if (rec?.Renderer == null) return;
            if (_layers == null) return;   // 构造时已留过一条说明

            rec.Renderer.sortingOrder = _layers.DepthOrder(worldY);

            // 同 `sortingOrder` 下的确定性次级键（Unity 官方：同序的先后「不固定、无法控制」）：
            // 只由实体 id 决定 ⇒ 逐帧稳定。截断成 int 只影响「谁盖谁」的决胜，不影响逐帧一致。
            var p = rec.Node.transform.localPosition;
            rec.Node.transform.localPosition =
                new Vector3(p.x, p.y, _layers.TiebreakOffset(unchecked((int)objectID)));
        }

        /// <summary>去掉缺失帧（<c>null</c>）后的紧凑帧表；全部到位时**原样返回**（不白造数组）。</summary>
        private static Sprite[] Compact(Sprite[] frames, out int missing)
        {
            var n = 0;
            for (var i = 0; i < frames.Length; i++) if (frames[i] != null) n++;
            missing = frames.Length - n;
            if (missing == 0) return frames;

            var ready = new Sprite[n];
            var k = 0;
            for (var i = 0; i < frames.Length; i++)
                if (frames[i] != null) ready[k++] = frames[i];
            return ready;
        }

        /// <summary>惰性造 1×1 白块（PPU = 1 ⇒ 恰好 1 个世界单位；口径照 `FrameBank.WhiteSprite()`）。</summary>
        private Sprite PlaceholderSprite()
        {
            if (_placeholder != null) return _placeholder;

            var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            tex.name = GeneratedPlaceholderName;
            tex.SetPixel(0, 0, Color.white);
            tex.Apply(false, false);

            // 贴图与精灵都由本件造 ⇒ 名字带生成前缀，排障时一眼能认出「不是美术资源」
            _placeholder = Sprite.Create(tex, new Rect(0f, 0f, 1f, 1f), new Vector2(0.5f, 0.5f), 1f);
            _placeholder.name = GeneratedPlaceholderName;
            _placeholderTex = tex;
            return _placeholder;
        }

        /// <summary>销毁自造的占位精灵与其贴图（谁造谁 Destroy）。</summary>
        private void DestroyPlaceholder()
        {
            if (_placeholder != null)
            {
                UnityEngine.Object.Destroy(_placeholder);
                _placeholder = null;
            }
            if (_placeholderTex != null)
            {
                UnityEngine.Object.Destroy(_placeholderTex);
                _placeholderTex = null;
            }
        }
    }
}
