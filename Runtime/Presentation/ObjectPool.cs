using System;
using System.Collections.Generic;
using UnityEngine;

namespace CloverEngine
{
    /// <summary>
    /// 对象池的内部实现，基于预制体键名维护对象的活跃/非活跃列表，支持对象复用以减少实例化开销。
    /// </summary>
    internal class ObjectPool : IObjectPool
    {
        private readonly Dictionary<string, PoolData> _pools = new();
        private readonly Dictionary<GameObject, string> _reverseMap = new();

        /// <summary>
        /// 代码工厂表（`IObjectPool.Register` 的落点）：key → 造对象的委托。
        /// <para>
        /// **与 `_pools` 分开存**：工厂是"怎么造"的接线，与"造出来多少实例"无关 ——
        /// <see cref="Clear"/> / <see cref="ClearAll"/> 只清池内容，不清这张表
        /// （切场景后同一个 key 仍该用同一个工厂造）。
        /// </para>
        /// </summary>
        private readonly Dictionary<string, Func<GameObject>> _factories = new();

        private Transform _root;
        private const int DefaultCapacity = 64;

        private float _idleExpirySeconds;

        /// <inheritdoc/>
        /// <remarks>默认 0 = 关闭（只按容量裁剪）。回收在 Spawn/Despawn 里惰性执行，不新增 Tick。</remarks>
        public float IdleExpirySeconds
        {
            get => _idleExpirySeconds;
            set => _idleExpirySeconds = value < 0f ? 0f : value;
        }

        /// <inheritdoc/>
        public void TrimIdle()
        {
            foreach (var pool in _pools.Values)
                SweepIdle(pool, Time.unscaledTime);
        }

        /// <summary>记录对象开始闲置的时刻（进 Inactive 时调用）。</summary>
        private static void NoteInactive(PoolData pool, GameObject obj)
        {
            pool.InactiveSince[obj] = Time.unscaledTime;
        }

        /// <summary>对象离开 Inactive（被复用 / 被销毁）时清掉时刻记录，避免字典无界增长。</summary>
        private static void ForgetInactive(PoolData pool, GameObject obj)
        {
            pool.InactiveSince.Remove(obj);
        }

        /// <summary>
        /// 按 <see cref="_idleExpirySeconds"/> 清掉闲置超时的对象（销毁与容量裁剪同路径）。
        /// 阈值为 0 时直接返回 —— 关闭态零开销（既有行为不变）。
        /// </summary>
        private void SweepIdle(PoolData pool, float now)
        {
            if (_idleExpirySeconds <= 0f || pool.Inactive.Count == 0) return;

            // 倒序删：RemoveAt 会让后续元素左移，正序会漏删紧随其后的条目。
            for (var i = pool.Inactive.Count - 1; i >= 0; i--)
            {
                var obj = pool.Inactive[i];
                if (obj == null)
                {
                    pool.Inactive.RemoveAt(i);
                    continue;
                }

                if (!pool.InactiveSince.TryGetValue(obj, out var since))
                {
                    // 无时间戳（老条目 / 阈值刚打开）：以"现在"为准，给他一个完整窗口再回收。
                    pool.InactiveSince[obj] = now;
                    continue;
                }

                if (now - since < _idleExpirySeconds) continue;

                pool.Inactive.RemoveAt(i);
                pool.InactiveSince.Remove(obj);
                _reverseMap.Remove(obj);
                UnityEngine.Object.Destroy(obj);
            }
        }

        /// <summary>
        /// 初始化对象池。常驻根节点**按需重建**（池全清后一起销毁，避免每次 Shutdown→Launch 残留空节点）。
        /// </summary>
        public ObjectPool()
        {
        }

        /// <summary>池化对象的常驻父节点：不存在（首次使用 / 上次 ClearAll 已销毁）时按需创建。</summary>
        private Transform Root
        {
            get
            {
                if (_root == null)
                {
                    var go = new GameObject("[ObjectPool]");
                    UnityEngine.Object.DontDestroyOnLoad(go);
                    _root = go.transform;
                }
                return _root;
            }
        }

        /// <inheritdoc/>
        /// <remarks>
        /// 重复注册 = 覆盖（后注册的生效）+ Info 留痕；传 null 工厂 = 注销该 key + Info 留痕。
        /// 「传 null 当注销」是**刻意**的：把 null 存进表里会让 <see cref="Spawn"/> 在运行时
        /// 抛 NullReferenceException（一个远离注册点的崩溃），不如在这里给出明确语义。
        /// </remarks>
        public void Register(string key, Func<GameObject> factory)
        {
            if (string.IsNullOrEmpty(key))
            {
                // 空 key 与任何 Spawn 的 key 都对不上：注册了也永远不会命中，明确报错比静默丢弃好排查。
                Game.Logger?.Error("ObjectPool", "Register 收到空 key，已忽略（空 key 无法与 Spawn 的 key 匹配）");
                return;
            }

            if (factory == null)
            {
                if (_factories.Remove(key))
                    Game.Logger?.Info("ObjectPool", $"池 {key} 的代码工厂已注销，该 key 回落 Resources 预制体路径");
                else
                    Game.Logger?.Warn("ObjectPool", $"注销 {key} 的代码工厂失败：该 key 没有注册过工厂（已忽略）");
                return;
            }

            if (_factories.ContainsKey(key))
                Game.Logger?.Info("ObjectPool", $"池 {key} 的代码工厂被重复注册，已覆盖为新的工厂");

            _factories[key] = factory;
        }

        /// <summary>
        /// 从对象池中获取或实例化一个游戏对象，优先复用非活跃对象。
        /// 预制体缺失时返回 null（并已记 Error 日志），不返回"空壳对象"。
        /// </summary>
        /// <param name="key">预制体资源键名，对应 Resources 目录下的预制体路径。</param>
        /// <param name="parent">可选的父级 Transform，为 null 时保持默认父级。</param>
        /// <returns>激活的游戏对象实例；预制体加载失败时为 null。</returns>
        public GameObject Spawn(string key, Transform parent = null, string group = null)
        {
            if (!_pools.TryGetValue(key, out var pool))
            {
                pool = new PoolData { PrefabKey = key, Group = group, MaxCapacity = DefaultCapacity };
                _pools[key] = pool;
            }
            else if (!string.IsNullOrEmpty(group) && pool.Group != group)
            {
                // 已有池补填 / 变更分组（⛔ 只认首次创建时的 group 会让后续按场景名 ClearGroup 漏清这个池）。
                Game.Logger?.Info("ObjectPool", $"池 {key} 的分组由 {pool.Group ?? "(null)"} 变更为 {group}");
                pool.Group = group;
            }

            // 空闲过期回收：把闲置超时的对象清掉（IdleExpirySeconds = 0 时零开销）
            SweepIdle(pool, Time.unscaledTime);

            while (pool.Inactive.Count > 0)
            {
                var idx = pool.Inactive.Count - 1;
                var obj = pool.Inactive[idx];
                pool.Inactive.RemoveAt(idx);
                if (obj == null)
                {
                    // 条目指向已被外部销毁的对象（业务自己 Destroy 了池化对象）：丢弃并继续取下一个，
                    // 否则 SetActive 直接抛 MissingReferenceException。
                    Game.Logger?.Warn("ObjectPool", $"池 {key} 中的对象已被外部销毁，已丢弃该条目");
                    continue;
                }

                ForgetInactive(pool, obj);
                pool.Active.Add(obj);
                obj.SetActive(true);
                // worldPositionStays=false：与新建路径（Instantiate(prefab, parent)）的位置/缩放语义一致，
                // 否则父级带缩放时对象会被重新缩放成不一致的样子。
                if (parent != null) obj.transform.SetParent(parent, false);
                return obj;
            }

            var instance = CreateInstance(key, parent);
            if (instance == null) return null;   // 预制体缺失：不登记假实例（原来会返回空 GameObject，掩盖失败）
            if (!pool.HasBaseScale)
            {
                pool.BaseScale = instance.transform.localScale;   // 记下预制体原始缩放，归还时恢复（别把美术缩放吃掉）
                pool.HasBaseScale = true;
            }
            pool.Active.Add(instance);
            _reverseMap[instance] = key;
            return instance;
        }

        /// <summary>
        /// 将游戏对象回收到对象池中，禁用并重置父级；若不属于已知池则直接销毁。
        /// </summary>
        /// <param name="obj">要回收的游戏对象。</param>
        public void Despawn(GameObject obj)
        {
            if (obj == null) return;

            if (_reverseMap.TryGetValue(obj, out var key) && _pools.TryGetValue(key, out var pool))
            {
                if (pool.Inactive.Contains(obj))
                {
                    // 重复归还：对象已在 Inactive 里，再走下面的销毁分支会让列表里的引用变成"已销毁对象"，
                    // 下次 Spawn 取到失效引用。重复归还按无操作处理并留痕。
                    Game.Logger?.Warn("ObjectPool", $"对象被重复归还，已忽略：key={key} name={obj.name}");
                    return;
                }

                if (pool.Active.Remove(obj))
                {
                    obj.SetActive(false);
                    obj.transform.SetParent(Root, false);
                    // 归零变换（位置 / 旋转 / 缩放）—— 只 SetParent 会把脏变换串给下一个使用者；
                    // 缩放恢复成预制体的基准值，而不是硬写 Vector3.one。
                    obj.transform.localPosition = Vector3.zero;
                    obj.transform.localRotation = Quaternion.identity;
                    obj.transform.localScale = pool.HasBaseScale ? pool.BaseScale : Vector3.one;
                    pool.Inactive.Add(obj);
                    NoteInactive(pool, obj); // 记录闲置起点（空闲过期回收用）
                    TrimPool(pool);
                    // 空闲过期回收：归还也是池"在用"的时刻，顺手清掉已超时的老空闲对象
                    SweepIdle(pool, Time.unscaledTime);
                    return;
                }
            }

            // 不属于任何池（或登记状态异常）：按契约直接销毁。
            Game.Logger?.Info("ObjectPool", $"归还的对象不属于任何池，直接销毁：{obj.name}");
            _reverseMap.Remove(obj);
            UnityEngine.Object.Destroy(obj);
        }

        /// <summary>
        /// 预先实例化指定数量的游戏对象到池中并置为非活跃状态，以减少运行时分配开销。
        /// </summary>
        /// <param name="key">预制体资源键名。</param>
        /// <param name="count">要预加载的实例数量。</param>
        public void Preload(string key, int count, string group = null)
        {
            if (!_pools.TryGetValue(key, out var pool))
            {
                pool = new PoolData { PrefabKey = key, Group = group, MaxCapacity = DefaultCapacity };
                _pools[key] = pool;
            }
            else if (!string.IsNullOrEmpty(group) && pool.Group != group)
            {
                // 同 Spawn：已有池也要能补填 / 变更分组，否则 ClearGroup 漏清。
                Game.Logger?.Info("ObjectPool", $"池 {key} 的分组由 {pool.Group ?? "(null)"} 变更为 {group}");
                pool.Group = group;
            }

            for (var i = 0; i < count; i++)
            {
                var obj = CreateInstance(key, null);
                if (obj == null) return;   // 预制体缺失（错误已在 CreateInstance 记过），不再重复刷日志
                if (!pool.HasBaseScale)
                {
                    pool.BaseScale = obj.transform.localScale;
                    pool.HasBaseScale = true;
                }
                obj.SetActive(false);
                obj.transform.SetParent(Root, false);
                pool.Inactive.Add(obj);
                NoteInactive(pool, obj);
                _reverseMap[obj] = key;
            }
        }

        /// <summary>
        /// 清除指定键名的整个对象池，销毁所有活跃和非活跃的游戏对象。
        /// </summary>
        /// <param name="key">预制体资源键名。</param>
        public void Clear(string key)
        {
            if (!_pools.TryGetValue(key, out var pool)) return;

            foreach (var obj in pool.Active)
            {
                if (obj != null)
                {
                    _reverseMap.Remove(obj);
                    UnityEngine.Object.Destroy(obj);
                }
            }
            foreach (var obj in pool.Inactive)
            {
                if (obj != null)
                {
                    _reverseMap.Remove(obj);
                    UnityEngine.Object.Destroy(obj);
                }
            }

            pool.Active.Clear();
            pool.Inactive.Clear();
            pool.InactiveSince.Clear();
            _pools.Remove(key);
        }

        /// <summary>
        /// 清除所有对象池，销毁全部游戏对象并清空内部数据。
        /// </summary>
        public void ClearAll()
        {
            foreach (var kv in _pools)
            {
                foreach (var obj in kv.Value.Active)
                    if (obj != null) UnityEngine.Object.Destroy(obj);
                foreach (var obj in kv.Value.Inactive)
                    if (obj != null) UnityEngine.Object.Destroy(obj);
            }
            _pools.Clear();
            _reverseMap.Clear();

            // 池全清后连常驻根节点一起销毁：否则每次 Shutdown→Launch 都残留一个空的 [ObjectPool] 根节点
            //（IObjectPool 无 Dispose，销毁时机只能挂在这里）；下次使用按需重建（见 Root 属性）。
            if (_root != null)
            {
                UnityEngine.Object.Destroy(_root.gameObject);
                _root = null;
            }
        }

        public void ClearGroup(string group)
        {
            var keysToRemove = new List<string>();
            foreach (var kv in _pools)
            {
                if (kv.Value.Group == group)
                {
                    foreach (var obj in kv.Value.Active)
                    {
                        if (obj != null)
                        {
                            _reverseMap.Remove(obj);
                            UnityEngine.Object.Destroy(obj);
                        }
                    }
                    foreach (var obj in kv.Value.Inactive)
                    {
                        if (obj != null)
                        {
                            _reverseMap.Remove(obj);
                            UnityEngine.Object.Destroy(obj);
                        }
                    }
                    kv.Value.InactiveSince.Clear();
                    keysToRemove.Add(kv.Key);
                }
            }

            foreach (var key in keysToRemove)
            {
                _pools.Remove(key);
            }
        }

        /// <summary>
        /// 获取指定键名池中当前处于活跃状态的对象数量。
        /// </summary>
        /// <param name="key">预制体资源键名。</param>
        /// <returns>活跃对象数量，若池不存在则返回 0。</returns>
        public int GetActiveCount(string key)
        {
            return _pools.TryGetValue(key, out var pool) ? pool.Active.Count : 0;
        }

        /// <summary>
        /// 获取指定键名池中当前处于非活跃（已回收）状态的对象数量。
        /// </summary>
        /// <param name="key">预制体资源键名。</param>
        /// <returns>非活跃对象数量，若池不存在则返回 0。</returns>
        public int GetInactiveCount(string key)
        {
            return _pools.TryGetValue(key, out var pool) ? pool.Inactive.Count : 0;
        }

        /// <summary>
        /// 造一个新实例：**注册过工厂的 key 一律走工厂**（代码造的对象也能入池，G5），
        /// 未注册的 key 才回落 <c>Resources.Load</c> 预制体路径。
        /// <para>
        /// 工厂路径与预制体路径的执行顺序一致：先造、再挂父、再改名；两者都遵守"造不出就返回 null
        /// 并记 Error"（⛔ 不返回空壳对象 —— 那会让"预制体缺失/工厂写坏"变成静默的表现缺失）。
        /// </para>
        /// </summary>
        private GameObject CreateInstance(string key, Transform parent)
        {
            if (_factories.TryGetValue(key, out var factory))
            {
                if (factory == null)
                {
                    // 表里存着 null（正常注册路径进不来，只有外部反射/序列化等异常途径）：
                    // 明确报错并返回 null，不静默换来源（换来源会让"为什么这批对象长得不一样"无法定位）。
                    Game.Logger?.Error("ObjectPool",
                        $"池 {key} 的代码工厂为 null，本次 Spawn 失败（该 key 已被注册过工厂，不会再回落 Resources）");
                    return null;
                }

                var made = factory();
                if (made == null)
                {
                    // 工厂返回 null = 造不出：与"预制体找不到"同口径，报错并放弃本次 Spawn。
                    Game.Logger?.Error("ObjectPool",
                        $"池 {key} 的代码工厂返回 null，本次 Spawn 失败：工厂必须返回一个已实例化的 GameObject");
                    return null;
                }

                // worldPositionStays=false：与预制体路径（Instantiate(prefab, parent)）的挂父语义一致。
                if (parent != null) made.transform.SetParent(parent, false);
                made.name = key;   // 与预制体路径同名：池内对象按 key 命名，排障时能一眼看出它属于哪个池
                return made;
            }

            var prefab = Resources.Load<GameObject>(key);
            if (prefab == null)
            {
                // 不造空对象（返回 new GameObject(key) 并登记进 Active / _reverseMap，
                // 会让后续 Spawn 把这个"无组件的有效实例"发给业务，失败被静默掩盖）。
                Game.Logger?.Error("ObjectPool", $"Prefab not found: {key}");
                return null;
            }
            var go = UnityEngine.Object.Instantiate(prefab, parent);
            go.name = key;
            return go;
        }

        private void TrimPool(PoolData pool)
        {
            while (pool.Inactive.Count > pool.MaxCapacity)
            {
                var obj = pool.Inactive[0];
                pool.Inactive.RemoveAt(0);
                ForgetInactive(pool, obj);
                if (obj != null)
                {
                    _reverseMap.Remove(obj);
                    UnityEngine.Object.Destroy(obj);
                }
            }
        }

        private class PoolData
        {
            public string PrefabKey;
            public string Group;
            public int MaxCapacity = DefaultCapacity;
            public readonly List<GameObject> Active = new();
            public readonly List<GameObject> Inactive = new();

            /// <summary>Inactive 中各对象开始闲置的时刻（Time.unscaledTime），空闲过期回收用。</summary>
            public readonly Dictionary<GameObject, float> InactiveSince = new();

            /// <summary>预制体的原始局部缩放（首个实例落地时记录）：归还时恢复成它，而不是硬写 Vector3.one。</summary>
            public Vector3 BaseScale = Vector3.one;
            public bool HasBaseScale;
        }
    }
}
