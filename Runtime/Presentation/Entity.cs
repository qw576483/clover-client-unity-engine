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
    /// 实体视图工厂实现（契约见 <see cref="IEntityViewFactory"/>）。
    ///
    /// 一次 CreateView 的完整流程（每一步都对应一个已踩过的静默失败）：
    /// <list type="number">
    /// <item>建根节点（命名 <c>Entity_{id}</c>/<c>Self_{id}</c>）—— 位置先可写，实体立刻能参与表现；</item>
    /// <item>顶一个**中性灰**占位体（不能像角色：用过亮橙色，被当成敌人）；</item>
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

            var rec = new ViewRecord
            {
                Root = root,
                ModelPath = spec.ModelPath,
                AnimatorPath = spec.AnimatorPath,
            };
            _views[objectID] = rec;

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
            return _views.TryGetValue(objectID, out var rec) && rec.Loading;
        }

        /// <inheritdoc />
        public IAnimPlayer GetAnimator(long objectID)
        {
            return _views.TryGetValue(objectID, out var rec) ? rec.Anim : null;
        }

        /// <inheritdoc />
        public void ReleaseView(long objectID)
        {
            if (!_views.TryGetValue(objectID, out var rec)) return;
            _views.Remove(objectID);   // 先摘表：加载回调里的"实体还在吗"检查因此能立刻失败

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
