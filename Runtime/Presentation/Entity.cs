using System;
using System.Collections.Generic;
using UnityEngine;

namespace CloverEngine
{
    internal class EntityManager : IEntityManager
    {
        private readonly Dictionary<long, EntityInfo> _entities = new();
        private readonly Dictionary<string, List<long>> _groups = new();

        /// <summary>
        /// 视图工厂注册槽：<see cref="EntityViewFactory"/> 构造时自动注册，实体销毁 / 清空时据此释放
        /// 工厂侧记录（动画播放器 + 模型资源引用计数），否则"Destroy 只销毁 GameObject"会让资源永远不归还。
        /// 引擎内唯一入口，业务无需调用。
        /// </summary>
        private static IEntityViewFactory _viewFactory;

        internal static void RegisterViewFactory(IEntityViewFactory factory) => _viewFactory = factory;

        /// <summary>
        /// 销毁一个实体的视图：先释放工厂侧记录（不存在则静默忽略），再销毁自己登记的 GameObject 并摘表。
        /// </summary>
        private void DestroyView(long objectID)
        {
            _viewFactory?.ReleaseView(objectID);
            if (_views.TryGetValue(objectID, out var view) && view != null)
                UnityEngine.Object.Destroy(view);
            _views.Remove(objectID);
        }

        // 视图单独存放，不放在 EntityInfo 里：
        // EntityInfo 是**只读快照**（见 Core/EntityPool.cs 的注释与 结构规则 §5.3 G8 判据），
        // 若把它当可变载体返回，跨模块拿到后就能绕过 BindView 直接改内部状态。
        private readonly Dictionary<long, GameObject> _views = new();

        public EntityInfo Create(long objectID, int typeID, string group = null)
        {
            if (_entities.TryGetValue(objectID, out var existing))
            {
                if (existing.SceneGroup != null && _groups.TryGetValue(existing.SceneGroup, out var oldIds))
                    oldIds.Remove(objectID);
            }

            var entity = EntityInfo.Create(objectID, typeID, group);

            _entities[objectID] = entity;
            // 重建同 ID 实体：旧视图必须**销毁**（含工厂侧记录 / 资源引用），只清登记会残留无主孤儿视图
            //（与 Destroy / DestroyGroup 行为不一致，场景里留下永远没人回收的旧角色）。
            DestroyView(objectID);

            if (group != null)
            {
                if (!_groups.TryGetValue(group, out var ids))
                {
                    ids = new List<long>();
                    _groups[group] = ids;
                }
                ids.Add(objectID);
            }

            Game.Event?.Emit(CloverEvents.Entity.Created, entity);
            return entity;
        }

        public void Destroy(long objectID)
        {
            if (!_entities.TryGetValue(objectID, out var entity)) return;

            DestroyView(objectID);

            if (entity.SceneGroup != null && _groups.TryGetValue(entity.SceneGroup, out var ids))
                ids.Remove(objectID);

            _entities.Remove(objectID);
            Game.Event?.Emit(CloverEvents.Entity.Destroyed, entity);
        }

        public EntityInfo Get(long objectID)
        {
            return _entities.TryGetValue(objectID, out var entity) ? entity : null;
        }

        public IEnumerable<EntityInfo> GetAll()
        {
            return _entities.Values;
        }

        public IEnumerable<EntityInfo> GetByGroup(string group)
        {
            if (!_groups.TryGetValue(group, out var ids)) yield break;
            foreach (var id in ids)
            {
                if (_entities.TryGetValue(id, out var entity))
                    yield return entity;
            }
        }

        public void BindView(long objectID, GameObject view)
        {
            if (!_entities.TryGetValue(objectID, out var entity))
            {
                // 非预期分支：实体不存在时既登记不了、也不销毁传入对象 —— 调用方以为绑定成功，
                // 实际对象永久无主。留痕 + 就地销毁，让泄漏变成可见的日志。
                Game.Logger?.Warn("Entity",
                    $"BindView 的实体不存在：id={objectID}，传入视图将被销毁（避免无主对象泄漏）");
                if (view != null) UnityEngine.Object.Destroy(view);
                return;
            }

            // 覆盖绑定前先销毁旧视图：重复绑定（重进视野 / 换模型）时旧 GameObject 与工厂记录一起泄漏。
            if (_views.TryGetValue(objectID, out var old) && old != null && old != view)
                UnityEngine.Object.Destroy(old);

            _views[objectID] = view;
            Game.Event?.Emit(CloverEvents.Entity.ViewBound, entity);
        }

        public GameObject GetView(long objectID)
        {
            return _views.TryGetValue(objectID, out var view) ? view : null;
        }

        public void DestroyGroup(string group)
        {
            if (!_groups.TryGetValue(group, out var ids)) return;
            var snapshot = new List<long>(ids);
            ids.Clear();
            foreach (var id in snapshot)
            {
                if (_entities.TryGetValue(id, out var entity))
                {
                    DestroyView(id);
                    _entities.Remove(id);
                    Game.Event?.Emit(CloverEvents.Entity.Destroyed, entity);
                }
            }
            _groups.Remove(group);
        }

        public void ClearAll()
        {
            // 先快照实体列表与视图 id：清空后仍要逐实体发 Destroyed 事件（与 Destroy/DestroyGroup 一致），
            // 否则订阅方（血条、小地图、目标锁定…）状态残留，永远清不掉。
            var entities = new List<EntityInfo>(_entities.Values);
            var viewIds = new List<long>(_views.Keys);

            // 工厂侧记录（含没有 BindView 过的视图）一并释放：只清 _views 会让模型资源引用计数悬挂。
            _viewFactory?.ReleaseAll();
            foreach (var id in viewIds)
                DestroyView(id);

            _views.Clear();
            _entities.Clear();
            _groups.Clear();

            foreach (var entity in entities)
                Game.Event?.Emit(CloverEvents.Entity.Destroyed, entity);
        }
    }

    /// <summary>
    /// **实体视图来源**（`EntityViewFactory` 的**视图来源可插拔接缝**）：把「视图规格」变成
    /// 「场景里看得见的视图内容」。
    ///
    /// <para>
    /// ⛔ <b>缺陷（本接缝补的就是它）</b>：此前实体的建 / 绑 / 销骨架**只有 3D 一条路**
    /// （<c>EntityViewFactory</c>：3D 模型 + `AnimatorController`），2D 精灵项目拿不到骨架
    /// ⇒ 只能自建第二套实体视图生命周期。★ 影响范围：所有 2D / 像素风项目。
    /// </para>
    /// <para>
    /// <b>最小复现</b>：不注册任何来源时 <c>CloverPresentation.EntityView.CreateView(id, spec)</c>
    /// 只可能产出胶囊占位 + 3D 模型；要看 2D 精灵实体，业务必须自己
    /// <c>new GameObject</c> + <c>AddComponent&lt;SpriteRenderer&gt;</c> + 自己管贴图 / 帧动画 / 排序 / 回收。
    /// </para>
    /// <para>
    /// <b>修复后自证</b>：注册 <see cref="SpriteEntityViewSource"/>（或任何实现了本接口的类型）后，
    /// 同一句 <c>CreateView</c> 产出的就是来源自己的视图；<b>未注册来源时 3D 路径逐字不变</b>
    /// （工厂里只多一个 <c>source == null</c> 分支）。
    /// </para>
    /// <para>
    /// <b>已知边界</b>：
    /// ① 来源**不拥有根节点** —— 根节点仍由 <c>EntityViewFactory</c> 建、由它（及 `IEntityManager`）销毁；
    ///    <see cref="Release"/> 只许释放来源自己的记录（资源引用 / 池化子节点），⛔ 不许销毁根节点；
    /// ② <see cref="Build"/> 返回 <c>false</c> 时**必须自己撤销已建内容**，否则工厂回落到 3D 路径后
    ///    根节点上会叠两套表现；
    /// ③ 来源接管后，`EntityViewSpec.ModelPath` / `AnimatorPath` 的**资源引用归来源管**
    ///    （工厂不再 Release 它们），⛔ 别两边都 Release（引用计数会被多扣）；
    /// ④ 注册表是**进程级**的（不是工厂实例级）；建议在 `Game.Launch` 之后的接线钩子里注册一次。
    /// </para>
    /// <para>
    /// <b>用法</b>：
    /// <code>
    /// // 2D 项目：整条实体视图链路都走精灵视图
    /// EntityViewFactory.RegisterSource(new SpriteEntityViewSource(layers: myLayers), asDefault: true);
    ///
    /// // 3D 项目里只想让某类实体走 2D（按自己的资源路径约定筛选）
    /// EntityViewFactory.RegisterSource(new SpriteEntityViewSource(spec =&gt; spec.ModelPath.StartsWith("UI/Icons/")));
    /// </code>
    /// </para>
    /// </summary>
    public interface IEntityViewSource
    {
        /// <summary>
        /// 本来源是否负责这条规格。<c>true</c> = 该实体的视图内容全交本来源（3D 路径不参与）。
        /// <para>按注册顺序询问**非默认**来源；都不认领时才用「默认来源」。</para>
        /// </summary>
        bool CanBuild(EntityViewSpec spec);

        /// <summary>
        /// 在 <paramref name="root"/>（工厂已建好的视图根节点）下建出视图内容；返回 <c>false</c> = 没建出，
        /// 工厂会回落 3D 占位路径（见接口注释的边界 ②）。
        /// <para>口径与 <see cref="IEntityViewFactory.CreateView"/> 一致：<b>同步</b>返回时可见内容就绪
        /// （占位块已上）、异步部分（贴图 / 模型）自行补上。</para>
        /// </summary>
        bool Build(long objectID, GameObject root, EntityViewSpec spec);

        /// <summary>释放本来源侧该实体的记录（资源引用 / 池化子节点…）。不存在的 id 静默忽略。</summary>
        void Release(long objectID);

        /// <summary>该实体的视图内容是否仍在加载中（排障 / 进度展示用）。</summary>
        bool IsLoading(long objectID);

        /// <summary>取该实体的动画播放器（3D = <see cref="IAnimPlayer"/>；2D 帧动画没有此口径，返回 null）。</summary>
        IAnimPlayer GetAnimator(long objectID);
    }

    /// <summary>
    /// 实体视图工厂实现（契约见 <see cref="IEntityViewFactory"/>）。
    ///
    /// 一次 CreateView 的完整流程（每一步都要拦住一类静默失败）：
    /// <list type="number">
    /// <item>建根节点（命名 <c>Entity_{id}</c>/<c>Self_{id}</c>）—— 位置先可写，实体立刻能参与表现；</item>
    /// <item>顶一个**中性灰**占位体（不能像角色，否则会被当成敌人）；</item>
    /// <item>异步加载模型；回调里先问"实体还在吗" —— 不在就丢弃实例（**竞态**：加载慢于离场）；</item>
    /// <item>按实测包围盒归一化身高 + 底面对齐节点原点（贴地）；</item>
    /// <item>模型到位后立刻移除占位体；</item>
    /// <item>按配置接动画控制器（不接也能看见角色）。</item>
    /// </list>
    /// 任何一步失败都留日志（模型路径写错、controller 缺失、Shder 取不到…）。
    /// </summary>
    public sealed class EntityViewFactory : IEntityViewFactory
    {
        private sealed class ViewRecord
        {
            public GameObject Root;
            public GameObject Placeholder;
            public IAnimPlayer Anim;
            public string ModelPath;
            public string AnimatorPath;
            public bool Loading;

            /// <summary>
            /// 接管本视图的**视图来源**（没有 = 走 3D 路径）。非 null 时本记录的 ModelPath / AnimatorPath
            /// 已被清空（资源引用归来源管，见 <see cref="IEntityViewSource"/> 边界 ③）。
            /// </summary>
            public IEntityViewSource Source;
        }

        private readonly Dictionary<long, ViewRecord> _views = new Dictionary<long, ViewRecord>();

        // 显式注入的句柄（构造参数，可为 null）；为 null 时「按需」回退到 Game.Res / Game.Anim。
        // 为什么不在这里就把 Game.Res 抓成字段：本工厂由 CloverPresentation.Init 在挂载 Entity 时创建，
        // 而资源模块（CloverRes.Init）的挂接时机并不固定（可能在 Init 之前、也可能之后）——
        // 构造时抓死会永远拿到 null，表现为"占位体在、模型永远不加载"（静默）。
        // 按需解析使两种挂接顺序都成立。
        private readonly IResourceManager _resOverride;
        private readonly IAnimationManager _animOverride;

        private IResourceManager Res => _resOverride ?? Game.Res;
        private IAnimationManager Anim => _animOverride ?? Game.Anim;

        /// <summary>
        /// 构造视图工厂。<paramref name="res"/> / <paramref name="anim"/> 留空时「按需」取
        /// <c>Game.Res</c> / <c>Game.Anim</c>（每次使用才解析，故对资源/动画模块的挂接顺序不敏感）。
        /// </summary>
        public EntityViewFactory(IResourceManager res = null, IAnimationManager anim = null)
        {
            _resOverride = res;
            _animOverride = anim;

            // 注册给实体管理器：实体 Destroy / DestroyGroup / ClearAll 时同步释放本工厂的记录
            //（动画播放器 + 模型资源引用），否则"只销毁 GameObject"会让工厂侧引用计数永不归还。
            EntityManager.RegisterViewFactory(this);
        }

        /// <inheritdoc />
        public int Count => _views.Count;

        /// <inheritdoc />
        public GameObject CreateView(long objectID, EntityViewSpec spec)
        {
            if (_views.TryGetValue(objectID, out var existing))
            {
                // 幂等：重进视野这类重复事件不该重建视图。
                if (existing.Root != null) return existing.Root;

                // 旧根已被外部销毁（Unity 的"假 null"，`Root != null` 判不出来）：旧记录的动画与
                // 资源引用必须一并释放，再走重建 —— 否则每重建一次就漏一份引用计数。
                ReleaseView(objectID);
            }

            var name = string.IsNullOrEmpty(spec.NodeName) ? $"Entity_{objectID}" : spec.NodeName;
            var root = new GameObject(name);

            var source = ResolveSource(spec);
            var rec = new ViewRecord
            {
                Root = root,
                ModelPath = spec.ModelPath,
                AnimatorPath = spec.AnimatorPath,
                Source = source,
            };
            _views[objectID] = rec;

            // ★ 视图来源接缝（见 IEntityViewSource）：注册了来源且它认领这条规格 ⇒ 视图内容全交来源，
            //   3D 的「胶囊占位 + 模型异步补 + AnimatorController」一律不参与（两条路径互斥，避免叠两套内容）。
            if (source != null && BuildViaSource(objectID, root, spec, rec, source))
                return root;

            rec.Placeholder = BuildPlaceholder(root.transform, spec);

            if (string.IsNullOrEmpty(spec.ModelPath))
                return root;   // 只用占位体（调试/无模型实体）

            var res = Res;
            if (res == null)
            {
                // 非预期分支：资源模块未挂（漏了 CloverRes.Init）→ 必须留痕，否则只剩占位体且查不到原因。
                Game.Logger?.Error("EntityView",
                    $"资源模块不可用（Game.Res 为空），实体 {objectID} 的模型无法加载：{spec.ModelPath}", null);
                return root;
            }

            rec.Loading = true;
            var path = spec.ModelPath;
            res.LoadAsset<GameObject>(path, model =>
            {
                // ★ 竞态：加载完成时实体可能已经离场 / 视图已被释放 —— 丢弃实例，别往已销毁的节点上挂。
                if (!_views.TryGetValue(objectID, out var cur) || cur != rec || rec.Root == null)
                {
                    // 丢弃路径必须归还刚加载出来的资源引用：ReleaseView 先执行时这次加载还没进缓存，
                    // 它的 Release 命中不了缓存 = 空操作，不在这里补一刀就永久悬挂一份计数。
                    if (model != null) res.Release(path);
                    Game.Logger?.Info("EntityView",
                        $"模型加载完成时实体已离场，丢弃实例并归还资源引用：id={objectID} path={path}");
                    return;
                }
                rec.Loading = false;

                if (model == null)
                {
                    // 非预期分支：模型没加载出来（路径写错 / 未导入）→ 留痕 + 把占位体染成品红
                    Game.Logger?.Warn("EntityView", $"模型加载失败，保留占位体：id={objectID} path={path}");
                    TintPlaceholder(rec.Placeholder, new Color(0.9f, 0.1f, 0.7f));
                    return;
                }

                var inst = UnityEngine.Object.Instantiate(model, rec.Root.transform);
                inst.name = "Model";
                inst.transform.localPosition = Vector3.zero;
                Normalize(inst, spec.TargetHeight);

                // 模型到位立刻移除占位体，否则屏幕上会留一个"灰球"跟着角色跑。
                if (rec.Placeholder != null)
                {
                    UnityEngine.Object.Destroy(rec.Placeholder);
                    rec.Placeholder = null;
                }

                AttachAnimator(objectID, rec, inst, spec);
            });
            return root;
        }

        /// <inheritdoc />
        public GameObject GetRoot(long objectID)
        {
            return _views.TryGetValue(objectID, out var rec) ? rec.Root : null;
        }

        /// <inheritdoc />
        public bool IsLoading(long objectID)
        {
            return _views.TryGetValue(objectID, out var rec)
                && (rec.Loading || (rec.Source != null && rec.Source.IsLoading(objectID)));
        }

        /// <inheritdoc />
        public IAnimPlayer GetAnimator(long objectID)
        {
            if (!_views.TryGetValue(objectID, out var rec)) return null;
            return rec.Anim ?? rec.Source?.GetAnimator(objectID);
        }

        /// <inheritdoc />
        public void ReleaseView(long objectID)
        {
            if (!_views.TryGetValue(objectID, out var rec)) return;
            _views.Remove(objectID);   // 先摘表：加载回调里的"实体还在吗"检查因此能立刻失败

            // ★ 来源接管过的视图：先让来源释放自己的记录（贴图引用 / 池化子节点），再销毁根节点。
            //   顺序不能反 —— 池化子节点必须在根节点被 Destroy **之前**归还，否则池里留下"已销毁对象"
            //   的引用（下次 Spawn 时要靠告警丢弃，且永远不会被复用）。
            if (rec.Source != null)
            {
                var src = rec.Source;
                rec.Source = null;   // 先清引用：来源 Release 里抛异常也不会让后续路径重复调用
                try
                {
                    src.Release(objectID);
                }
                catch (Exception e)
                {
                    // 非预期分支：来源释放抛异常 ⇒ 留痕但不阻断（根节点仍要销毁，否则实体没了视图还在）
                    Game.Logger?.Warn("EntityView",
                        $"视图来源 {src.GetType().Name} 释放实体 {objectID} 时抛异常（根节点仍会销毁）：" +
                        $"{e.GetType().Name}: {e.Message}");
                }
            }

            var anim = Anim;
            if (rec.Anim != null && anim != null)
            {
                anim.Destroy(rec.Anim);
                rec.Anim = null;
            }
            if (rec.Root != null)
                UnityEngine.Object.Destroy(rec.Root);

            // 归还资源引用（计数归零后才真正卸载）。路径为空说明没加载过。
            // 注意：加载在途时这次 Release 命中不了缓存（等于空操作），由加载完成回调里的
            // "实体已离场"竞态分支补释放 —— 两处配对，漏一处就是引用计数永久悬挂。
            var res = Res;
            if (!string.IsNullOrEmpty(rec.ModelPath))
                res?.Release(rec.ModelPath);
            if (!string.IsNullOrEmpty(rec.AnimatorPath))
                res?.Release(rec.AnimatorPath);
        }

        /// <inheritdoc />
        public void ReleaseAll()
        {
            if (_views.Count == 0) return;
            var ids = new List<long>(_views.Keys);
            foreach (var id in ids)
                ReleaseView(id);
        }

        // ─────────────────── 视图来源注册表（可插拔接缝，契约见 IEntityViewSource） ───────────────────

        /// <summary>非默认来源：按注册顺序问 <see cref="IEntityViewSource.CanBuild"/>，先认领者接管。</summary>
        private static readonly List<IEntityViewSource> Sources = new List<IEntityViewSource>();

        /// <summary>
        /// 默认来源：没有任何非默认来源认领该规格时用它。
        /// 「整条实体视图链路都走同一套视图」（例如纯 2D 项目走精灵视图）就注册成默认。
        /// </summary>
        private static IEntityViewSource DefaultSource;

        /// <summary>
        /// 注册一个视图来源（**进程级**，注册一次即可；建议在 <c>Game.Launch</c> 之后的接线钩子里调）。
        /// <para>
        /// <paramref name="asDefault"/> = <c>true</c> ⇒ 记为默认来源（不参与 <see cref="IEntityViewSource.CanBuild"/>
        /// 筛选，只在没人认领时兜底），用于"本项目所有实体都用这套视图"；<c>false</c> ⇒ 按注册顺序参与筛选。
        /// </para>
        /// <para>重复注册同一个实例 = 无操作；传 <c>null</c> = 无操作（不抛、不留痕）。</para>
        /// </summary>
        public static void RegisterSource(IEntityViewSource source, bool asDefault = false)
        {
            if (source == null) return;

            if (asDefault)
            {
                if (!ReferenceEquals(DefaultSource, source))
                    Game.Logger?.Info("EntityView", $"默认视图来源已注册：{source.GetType().Name}");
                DefaultSource = source;
                return;
            }

            if (Sources.Contains(source)) return;
            Sources.Add(source);
            Game.Logger?.Info("EntityView",
                $"视图来源已注册：{source.GetType().Name}（非默认来源共 {Sources.Count} 个）");
        }

        /// <summary>注销视图来源（传 <c>null</c> 无操作）。注销默认来源后回落 3D 路径。</summary>
        public static void UnregisterSource(IEntityViewSource source)
        {
            if (source == null) return;
            if (ReferenceEquals(DefaultSource, source)) DefaultSource = null;
            Sources.Remove(source);
        }

        /// <summary>清空全部来源注册（切服 / 引擎拆卸用，避免静态表跨局持有业务对象）。</summary>
        public static void ClearSources()
        {
            Sources.Clear();
            DefaultSource = null;
        }

        /// <summary>
        /// 该规格该用哪个来源：先按注册顺序问非默认来源（<see cref="IEntityViewSource.CanBuild"/>），
        /// 都不认领再用默认来源；都没有 = <c>null</c> ⇒ 走既有 3D 路径（行为逐字不变）。
        /// </summary>
        private static IEntityViewSource ResolveSource(EntityViewSpec spec)
        {
            for (var i = 0; i < Sources.Count; i++)
            {
                try
                {
                    if (Sources[i].CanBuild(spec)) return Sources[i];
                }
                catch (Exception e)
                {
                    // 非预期分支：筛选器抛异常 ⇒ 留痕并按「不认领」处理（不能让一条规格把整条创建链打断）
                    Game.Logger?.Warn("EntityView",
                        $"视图来源 {Sources[i].GetType().Name}.CanBuild 抛异常，按「不认领」处理：" +
                        $"{e.GetType().Name}: {e.Message}");
                }
            }
            return DefaultSource;
        }

        /// <summary>
        /// 让来源建视图内容：<c>true</c> = 来源接管（3D 占位 / 模型 / 动画全不参与）；
        /// <c>false</c> / 抛异常 = 回落 3D 路径（来源必须自己撤销已建内容，见 <see cref="IEntityViewSource"/> 边界 ②）。
        /// </summary>
        private static bool BuildViaSource(long objectID, GameObject root, EntityViewSpec spec,
            ViewRecord rec, IEntityViewSource source)
        {
            var built = false;
            try
            {
                built = source.Build(objectID, root, spec);
            }
            catch (Exception e)
            {
                Game.Logger?.Error("EntityView",
                    $"视图来源 {source.GetType().Name} 建视图抛异常（按「没建出」处理，回落 3D 路径）：" +
                    $"id={objectID}", e);
            }

            if (built)
            {
                // 来源接管资源生命周期 ⇒ 清掉 3D 记录里的资源路径，ReleaseView 不再对它们 Release
                //（否则同一份资源被「来源 + 工厂」各 Release 一次，引用计数被多扣 ⇒ 缓存提前淘汰）。
                rec.ModelPath = null;
                rec.AnimatorPath = null;
                return true;
            }

            try
            {
                source.Release(objectID);   // 撤回来源侧可能已登记的记录（贴图引用 / 池化子节点）
            }
            catch (Exception e)
            {
                Game.Logger?.Warn("EntityView",
                    $"视图来源 {source.GetType().Name} 回撤已建内容时抛异常：id={objectID} " +
                    $"{e.GetType().Name}: {e.Message}");
            }
            rec.Source = null;
            Game.Logger?.Warn("EntityView",
                $"视图来源 {source.GetType().Name} 未建出视图（id={objectID}）⇒ 回落 3D 占位路径");
            return false;
        }

        private static GameObject BuildPlaceholder(Transform parent, EntityViewSpec spec)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            go.name = "Placeholder";
            go.transform.SetParent(parent, false);

            // ★ 必须去掉 Collider：客户端不需要角色物理（位置由服务端权威 + 本地解算决定），
            //   留着会让相机探针打到自己的占位体（镜头被拉近）。
            var col = go.GetComponent<Collider>();
            if (col != null) UnityEngine.Object.Destroy(col);

            var s = spec.PlaceholderScale > 0f ? spec.PlaceholderScale : 0.65f;
            go.transform.localScale = new Vector3(s, s * 1.3f, s);

            // 中性灰：占位物必须一眼看出"这是占位"，不能看着像角色（用过亮橙色，被当成敌人）。
            var color = spec.PlaceholderColor == default ? new Color(0.45f, 0.45f, 0.48f) : spec.PlaceholderColor;
            TintPlaceholder(go, color);
            return go;
        }

        /// <summary>
        /// 改占位体颜色：用 <see cref="MaterialPropertyBlock"/> 而不是 <c>renderer.material</c>
        /// —— 后者每次访问都会新建一个材质实例，逐个实体累积成泄漏。
        /// </summary>
        private static void TintPlaceholder(GameObject go, Color color)
        {
            if (go == null) return;
            var mr = go.GetComponent<MeshRenderer>();
            if (mr == null) return;
            var mpb = new MaterialPropertyBlock();
            mpb.SetColor("_Color", color);
            mr.SetPropertyBlock(mpb);
        }

        /// <summary>
        /// 归一化：按**实测包围盒**把身高缩放到目标值，并让底面对齐节点原点（贴地）。
        /// 目标身高 &lt;=0 时只做贴地（美术已按统一单位出图时用）。
        /// </summary>
        private static void Normalize(GameObject inst, float targetHeight)
        {
            var b = MeasureBounds(inst);
            if (targetHeight > 0.0001f && b.size.y > 0.0001f)
            {
                inst.transform.localScale *= targetHeight / b.size.y;
                b = MeasureBounds(inst);
            }
            inst.transform.position += new Vector3(0f, -b.min.y, 0f);
        }

        private static Bounds MeasureBounds(GameObject go)
        {
            var rs = go.GetComponentsInChildren<Renderer>();
            if (rs.Length == 0)
            {
                // 非预期分支：模型里一个 Renderer 都没有（空 Prefab / 只有骨骼）→ 留痕，
                // 否则接下来的贴地/归一化会拿一个"假包围盒"（原点 1×1×1）算，尺寸看似对其实瞎猜。
                Game.Logger?.Warn("EntityView", $"模型无任何 Renderer，尺寸按占位默认值处理：{go.name}");
                return new Bounds(go.transform.position, Vector3.one);
            }
            var b = rs[0].bounds;
            for (int i = 1; i < rs.Length; i++)
                b.Encapsulate(rs[i].bounds);
            return b;
        }

        private void AttachAnimator(long objectID, ViewRecord rec, GameObject modelInstance, EntityViewSpec spec)
        {
            var res = Res;
            var anim = Anim;
            if (anim == null || res == null || string.IsNullOrEmpty(spec.AnimatorPath)) return;

            var ctrlPath = spec.AnimatorPath;
            res.LoadAsset<RuntimeAnimatorController>(ctrlPath, ctrl =>
            {
                // 与模型同样的竞态检查：这一步也可能晚于"实体离场"（第二条异步链）。
                // 丢弃分支必须归还 controller 的资源引用（否则每次离场都悬挂一份计数、常驻内存）。
                if (!_views.TryGetValue(objectID, out var cur) || !ReferenceEquals(cur, rec)
                    || cur.Root == null || modelInstance == null)
                {
                    if (ctrl != null)
                    {
                        res.Release(ctrlPath);
                        Game.Logger?.Info("EntityView",
                            $"动画 controller 加载完成时实体已离场，归还资源引用：id={objectID} path={ctrlPath}");
                    }
                    return;
                }

                if (ctrl == null)
                {
                    // 非预期分支：controller 缺失（没生成 / 路径变）→ 留痕但不阻塞（模型仍可见）
                    Game.Logger?.Warn("EntityView", $"动画 controller 缺失，角色将静止：{ctrlPath}");
                    return;
                }
                cur.Anim = anim.CreateAnimator(modelInstance, ctrl);
            });
        }
    }
}
