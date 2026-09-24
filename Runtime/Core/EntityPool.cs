using System;
using System.Collections.Generic;
using UnityEngine;

namespace CloverEngine
{
    /// <summary>
    /// 实体信息：**跨模块只读的数据快照**（结构规则 §5.2 G8，判据见 §5.3）。
    ///
    /// <para>
    /// G8 要求"引用类型**跨程序集**传递不得让渡可变状态：或返回**拷贝快照**，或返回**只读契约 / 不可变对象**"，其**实质**是：
    /// 不让外部拿到引用后修改模块内部的可变状态、或绕过契约方法改状态。
    /// 这里**不做每次读取都拷贝**，而是用「只读契约」达到同一目的 ——
    /// 实体读取是每帧高频路径，逐次拷贝会产生无谓的 GC 压力：
    /// </para>
    /// <list type="bullet">
    /// <item>所有数据只读，实例只能由本类工厂 <see cref="Create"/> 生成；</item>
    /// <item>视图绑定 / 销毁等修改一律走 <see cref="IEntityManager"/> 的契约方法
    /// （<c>BindView</c> / <c>Destroy</c> / <c>ClearAll</c> …），视图本身用
    /// <see cref="IEntityManager.GetView"/> 读，不放在本类型里。</item>
    /// </list>
    /// <para>因此外部即使持有实例也改不坏内部状态 ⇒ 满足 G8。</para>
    /// </summary>
    public sealed class EntityInfo
    {
        /// <summary>实体对象 ID（服务端分配，客户端只读）。</summary>
        public long ObjectID { get; private set; }

        /// <summary>实体类型 ID（配表模型/类型）。</summary>
        public int TypeID { get; private set; }

        /// <summary>所属场景组（用于整组销毁；可为 null）。</summary>
        public string SceneGroup { get; private set; }

        private EntityInfo()
        {
        }

        /// <summary>
        /// 生成一份实体快照。<b>由 <see cref="IEntityManager"/> 的实现调用</b>；
        /// 只读契约下外部即使创建也影响不到管理器内部状态，故公开无风险。
        /// </summary>
        public static EntityInfo Create(long objectID, int typeID, string group)
        {
            return new EntityInfo
            {
                ObjectID = objectID,
                TypeID = typeID,
                SceneGroup = group,
            };
        }
    }

    /// <summary>
    /// 实体管理器接口。定义在 Core，实现由 Presentation 的 EntityManager 提供。
    /// </summary>
    public interface IEntityManager
    {
        /// <summary>创建（或重建）实体并返回其只读快照。</summary>
        EntityInfo Create(long objectID, int typeID, string group = null);

        /// <summary>销毁实体。视图的销毁由实现负责，外部无需也无法直接改。</summary>
        void Destroy(long objectID);

        /// <summary>按 ID 取实体快照；不存在返回 null。</summary>
        EntityInfo Get(long objectID);

        /// <summary>取全部实体快照。</summary>
        IEnumerable<EntityInfo> GetAll();

        /// <summary>
        /// 取某场景组的实体快照。
        /// <para>
        /// 【未接线】引擎内部（Runtime/Editor/Tests）当前无调用点，保留为公开契约供业务使用
        /// （按组遍历是本接口的既定能力）——不要按"无人使用"删除。
        /// </para>
        /// </summary>
        IEnumerable<EntityInfo> GetByGroup(string group);

        /// <summary>绑定实体视图（<b>修改实体状态的唯一契约入口之一</b>）。</summary>
        void BindView(long objectID, GameObject view);

        /// <summary>
        /// 取实体当前绑定的视图；未绑定或实体不存在返回 null。
        /// 视图只给读不给改（与 <see cref="EntityInfo"/> 的只读契约一致）。
        /// </summary>
        GameObject GetView(long objectID);

        /// <summary>销毁整组的实体与视图。</summary>
        void DestroyGroup(string group);

        /// <summary>清空所有实体与视图。</summary>
        void ClearAll();
    }

    /// <summary>
    /// 对象池接口，提供基于预制体键名的对象复用、预加载和清理能力。
    /// 定义在 Core，实现由 Presentation 的 ObjectPool 提供。
    /// </summary>
    public interface IObjectPool
    {
        /// <summary>
        /// 从对象池中获取或实例化一个游戏对象。
        /// </summary>
        GameObject Spawn(string key, Transform parent = null, string group = null);

        /// <summary>
        /// 为某个 <paramref name="key"/> 注册一个**代码工厂**：<see cref="Spawn"/> / <see cref="Preload"/>
        /// 时**优先用工厂造对象**，未注册的 key 才回落既有的 <c>Resources.Load&lt;GameObject&gt;(key)</c> 预制体路径。
        /// <para>
        /// <b>为什么要有它</b>：G5 要求"战斗内 GameObject 一律走对象池"，但**代码造出来的对象**
        /// （子弹 / 碎片 / 敌人 / 地块这类由逻辑 new 出来的）此前无法入池 —— 池只会去 Resources 找预制体，
        /// 找不到就报错返回 null。于是这些对象只能绕过池裸 <c>Instantiate</c>，既违反 G5、又丢掉了复用。
        /// 注册工厂后，"怎么造"由业务决定，"什么时候复用 / 什么时候销毁"仍由池统一负责。
        /// </para>
        /// <para>
        /// <b>语义</b>：
        /// ① 工厂只在**池里没有可复用对象**时被调用（复用路径完全不变）；
        /// ② 同一 key **重复注册 = 覆盖**（后注册的生效），并留一条 Info 日志；
        ///    <paramref name="factory"/> 传 <c>null</c> = **注销**该 key（恢复 Resources 回落）并留痕；
        /// ③ 工厂返回 <c>null</c> ⇒ 本次 Spawn 失败、返回 null、记 Error —— ⛔ **不静默产出空对象**
        ///    （与预制体缺失同口径：不做"空壳对象掩盖失败"）；
        /// ④ <see cref="Clear"/> / <see cref="ClearAll"/> 只销毁**对象池内容**，**不注销工厂**
        ///    （工厂是代码接线，不是池内容；切场景后同一 key 仍该用同一个工厂造）；
        /// ⑤ 工厂造出的对象与预制体实例**同权**：走同一套复用 / 归还 / 分组 / 空闲过期 / 反向映射。
        /// </para>
        /// <para>
        /// <b>调用时机</b>：注册是纯字典写入，可在任何时刻调用；建议在 <c>Game.Launch</c> 之后、
        /// 首次 <see cref="Spawn"/> 之前一次注册完（例如 Launch 钩子里）。
        /// </para>
        /// </summary>
        /// <param name="key">池键名，与 <see cref="Spawn"/> 的 key 同一命名空间（建议不与预制体路径重名）。</param>
        /// <param name="factory">
        /// 造对象的委托：每次调用都须返回一个**新实例**（池不复用工厂的返回值）。
        /// 返回 null 视为"造不出"，会被记为 Error（见语义 ③）。
        /// </param>
        void Register(string key, Func<GameObject> factory);

        /// <summary>
        /// 将游戏对象回收到对象池中，若不属于已知池则直接销毁。
        /// </summary>
        void Despawn(GameObject obj);

        /// <summary>
        /// 预先实例化指定数量的游戏对象到池中，以减少运行时分配开销。
        /// </summary>
        void Preload(string key, int count, string group = null);

        /// <summary>
        /// 清除指定键名的整个对象池，销毁所有活跃和非活跃的游戏对象。
        /// </summary>
        void Clear(string key);

        /// <summary>
        /// 清除所有对象池，销毁全部游戏对象。
        /// </summary>
        void ClearAll();

        /// <summary>
        /// 清除指定场景组的所有对象池，销毁该组内所有活跃和非活跃的游戏对象。
        /// </summary>
        void ClearGroup(string group);

        /// <summary>
        /// 获取指定键名池中当前处于活跃状态的对象数量。
        /// <para>
        /// 【未接线】引擎内部（Runtime/Editor/Tests）当前无调用点，保留为公开契约供业务/调试面板使用
        /// （实现已就绪：<c>Presentation/ObjectPool.cs</c>）——不要按"无人使用"删除。
        /// </para>
        /// </summary>
        int GetActiveCount(string key);

        /// <summary>
        /// 获取指定键名池中当前处于非活跃（已回收）状态的对象数量。
        /// <para>【未接线】同 <see cref="GetActiveCount"/>：实现已就绪、引擎内无消费点，保留为公开契约。</para>
        /// </summary>
        int GetInactiveCount(string key);

        /// <summary>
        /// **空闲过期回收**阈值（秒）：&gt;0 时，某对象在池中闲置（非活跃）超过该时长后，
        /// 会在下一次 <see cref="Spawn"/> / <see cref="Despawn"/> 时被销毁回收（销毁与容量裁剪同路径）。
        /// <para>
        /// <b>默认 0 = 关闭</b>（保持既有行为不变：只按 <c>MaxCapacity</c> 裁剪，不按时长回收）。
        /// 需要它的场景：一次战斗/一张图里产生大量同类临时对象、之后长时间不再需要，
        /// 只靠容量上限会在"峰值那一下"把上限顶高、且此后一直占着这批实例的内存。
        /// </para>
        /// <para>负数按 0 处理。回收在**主线程**的池操作里惰性执行，不引入额外 Tick。</para>
        /// </summary>
        float IdleExpirySeconds { get; set; }

        /// <summary>
        /// 立即按 <see cref="IdleExpirySeconds"/> 清理一次全部池的空闲对象（调试 / 切场景前手动收紧内存）。
        /// 阈值为 0（关闭）时为无操作。
        /// </summary>
        void TrimIdle();
    }

    /// <summary>
    /// 实体视图规格：一个实体该用哪个模型 / 动画 / 多高。
    ///
    /// 为什么"视图长什么样"要由业务以数据形式给引擎，而不是让引擎猜：
    /// 模型路径、身高、用哪套动画全部是**内容**（配表/资源归属业务），
    /// 引擎只负责"把它变成场景里一个能看见、尺寸对、贴地的角色"这套**机械流程**。
    /// </summary>
    public struct EntityViewSpec
    {
        /// <summary>视图根节点名；留空时用 <c>Entity_{objectID}</c>（自己与他人用统一命名便于排障）。</summary>
        public string NodeName;

        /// <summary>模型资源路径（走 <c>Game.Res</c>，不含扩展名）；留空 = 只用占位体，异步不加载。</summary>
        public string ModelPath;

        /// <summary>动画控制器路径（<see cref="UnityEngine.RuntimeAnimatorController"/>）；留空 = 不接动画。</summary>
        public string AnimatorPath;

        /// <summary>
        /// 目标身高（米）：&gt;0 时按模型实测包围盒**归一化**并**贴地**（底面对齐节点原点）；
        /// &lt;=0 = 用模型原始尺寸（美术已按统一单位出图时用这个）。
        ///
        /// 为什么默认建议给：不同素材包单位不一致（同一批 CC0 角色里，有的 1 单位=1 米、
        /// 有的整体只有 0.002 —— 不比一次就会出现"一个角色像蚂蚁、另一个顶破天"）。
        /// </summary>
        public float TargetHeight;

        /// <summary>占位体颜色；默认由实现取中性灰。</summary>
        public Color PlaceholderColor;

        /// <summary>占位体缩放（模型加载完成前先顶上，避免"进图半天看不到自己"）。&lt;=0 用默认。</summary>
        public float PlaceholderScale;

        /// <summary>
        /// 创建一个只给必要字段、其余走默认值的规格。
        /// <para>
        /// 与 <see cref="IEntityViewFactory"/> 配套使用（引擎内无调用点，但属公开便捷构造器，
        /// 供业务构造 spec）。接线方式：
        /// <code>
        /// var view = CloverPresentation.EntityView.CreateView(objectID,
        ///     EntityViewSpec.Of("Models/hero", targetHeight: 1.8f));
        /// </code>
        /// 不要按"无人使用"删除。
        /// </para>
        /// </summary>
        public static EntityViewSpec Of(string modelPath, string animatorPath = null, float targetHeight = 0f)
        {
            return new EntityViewSpec
            {
                ModelPath = modelPath,
                AnimatorPath = animatorPath,
                TargetHeight = targetHeight,
            };
        }
    }

    /// <summary>
    /// 实体视图工厂：把「实体」变成「场景里看得见的角色」——异步加载模型、加载期间占位、
    /// 归一化身高并贴地、可选接动画、以及**加载完成时实体已经不存在**的竞态处理。
    ///
    /// <para>
    /// <b>接线方式</b>：实现为 <c>Presentation/Entity.cs</c> 的 <c>EntityViewFactory</c>，
    /// 由 <c>CloverPresentation.Init</c> 在挂载 <see cref="IEntityManager"/> 时一并创建，
    /// 并经 <c>CloverPresentation.EntityView</c> 暴露给业务；构造时自动注册给实体管理器，
    /// 故实体销毁 / 清空会同步释放工厂侧记录。业务用法：
    /// <code>
    /// var view = CloverPresentation.EntityView.CreateView(objectID,
    ///     EntityViewSpec.Of("Models/hero", targetHeight: 1.8f));
    /// Game.Entity.BindView(objectID, view);    // 视图的销毁由 Entity 侧统一负责
    /// </code>
    /// 保留为公开契约 —— 不要按"无人使用"删除。
    /// </para>
    ///
    /// 为什么这属于引擎：<see cref="IEntityManager"/> 只做 id ↔ GameObject 记账
    /// （<c>BindView</c>/<c>GetView</c>），而"异步加载 + 占位 + 竞态 + 归一化"是**每帧都在跑、
    /// 每个项目都要写一遍**的机械流程；写错的后果全是静默的：
    /// <list type="bullet">
    /// <item>不处理竞态 → 加载回来的模型挂到已销毁的节点上，或残留一个"幽灵角色"；</item>
    /// <item>不归一化 → 不同素材包的角色尺寸差几个数量级；</item>
    /// <item>不贴地 → 角色悬空或半截入地；</item>
    /// <item>不占位 → 网络先到、资源后到，玩家先看到"人不见了"。</item>
    /// </list>
    /// 玩法相关的部分（谁该显形、位置怎么插值、速度怎么驱动动画）**仍归业务**。
    /// </summary>
    public interface IEntityViewFactory
    {
        /// <summary>
        /// 为实体创建视图：**立即**返回可用的根节点（占位体已就位），模型异步补上。
        /// 同一 objectID 重复调用返回已有视图（幂等，便于"重进视野"这类重复事件）。
        /// </summary>
        GameObject CreateView(long objectID, EntityViewSpec spec);

        /// <summary>取视图根节点；不存在返回 null。</summary>
        GameObject GetRoot(long objectID);

        /// <summary>该实体的模型是否仍在加载中（排障/进度展示用）。</summary>
        bool IsLoading(long objectID);

        /// <summary>取该实体的动画播放器；未接动画或仍在加载时为 null。</summary>
        IAnimPlayer GetAnimator(long objectID);

        /// <summary>销毁单个视图（实体离开视野/场景时调用）。不存在的 id 静默忽略。</summary>
        void ReleaseView(long objectID);

        /// <summary>销毁全部视图（切场景、断线重登时调用）。</summary>
        void ReleaseAll();

        /// <summary>当前视图数量（调试面板用）。</summary>
        int Count { get; }
    }

    /// <summary>
    /// 可被引用池管理的对象（**可选**实现）：池在取出/归还时回调，让对象自己复位状态。
    ///
    /// <para>
    /// 为什么复位要有统一入口：复用对象最容易踩的坑是"忘了清干净上一次的字段"，
    /// 而症状（上一局的伤害数字串到这一局）离根因非常远。把复位点固定在池的两个动作上，
    /// 就不会出现"某个路径复用了但没复位"。
    /// </para>
    /// </summary>
    public interface IReferencePoolable
    {
        /// <summary>对象从池中取出、交给使用方之前调用（复位为可用初态）。</summary>
        void OnAcquire();

        /// <summary>对象被归还入池之前调用（释放外部引用 / 清空集合，避免池持有间接引用）。</summary>
        void OnRelease();
    }

    /// <summary>
    /// 引用池：**纯 C# 对象**的复用池（无 GameObject、无 Unity 依赖）。
    ///
    /// <para>
    /// 与 <see cref="IObjectPool"/> 的分工：<see cref="IObjectPool"/> 管 <c>GameObject</c>
    /// （Instantiate/Destroy 昂贵、必须复用），本类管**普通托管对象**
    /// （每帧产生的输入帧、事件参数、临时 List 等 —— 它们的问题不是"实例化贵"，而是
    /// "每帧 new 一堆短命对象把 GC 顶起来"）。两者是**互补**而不是替代关系。
    /// </para>
    ///
    /// <para>
    /// 用法（类型必须有无参构造函数）：
    /// <code>
    /// var msg = ReferencePool.Acquire&lt;InputFrame&gt;();   // 池空则 new
    /// ... 使用 ...
    /// ReferencePool.Release(msg);                     // 归还，供下次 Acquire 复用
    /// </code>
    /// 对象实现 <see cref="IReferencePoolable"/> 时，取出/归还会自动回调
    /// <see cref="IReferencePoolable.OnAcquire"/> / <see cref="IReferencePoolable.OnRelease"/>。
    /// </para>
    ///
    /// <para>
    /// 线程模型：**主线程专用**。内部加锁只是为了"误从后台线程归还"时不破坏栈结构；
    /// 不要把它当跨线程池用（跨线程复用同一实例本身就不安全）。
    /// </para>
    /// </summary>
    public static class ReferencePool
    {
        // 按类型分桶存放空闲实例。用 object 装箱是为了支持"任意引用类型共用一个静态池"，
        // 取出时再按 T 转回 —— 池里每个桶的实例类型恒为键类型，故转换不会失败。
        private static readonly Dictionary<Type, Stack<object>> Pools = new();
        private static readonly object Gate = new();

        /// <summary>
        /// 取一个 <typeparamref name="T"/> 实例：池中有空闲则复用，否则新建。
        /// </summary>
        public static T Acquire<T>() where T : class, new()
        {
            T item;
            lock (Gate)
            {
                if (Pools.TryGetValue(typeof(T), out var stack) && stack.Count > 0)
                {
                    item = (T)stack.Pop();
                }
                else
                {
                    item = new T();
                }
            }

            (item as IReferencePoolable)?.OnAcquire();
            return item;
        }

        /// <summary>
        /// 归还一个 <typeparamref name="T"/> 实例。传 null 时静默忽略（与 <c>Despawn(null)</c> 同口径）。
        /// 同一实例被重复归还时会被忽略并告警 —— 重复归还会让池里出现两个指向同一对象的条目，
        /// 之后两次 Acquire 拿到同一个实例（极难定位的"两个用者共享状态"）。
        /// </summary>
        public static void Release<T>(T item) where T : class, new()
        {
            if (item == null) return;

            (item as IReferencePoolable)?.OnRelease();

            lock (Gate)
            {
                if (!Pools.TryGetValue(typeof(T), out var stack))
                {
                    stack = new Stack<object>();
                    Pools[typeof(T)] = stack;
                }
                else if (stack.Contains(item))
                {
                    Game.Logger?.Warn("ReferencePool",
                        $"duplicate release ignored for {typeof(T).Name}: instance already in pool");
                    return;
                }
                stack.Push(item);
            }
        }

        /// <summary>当前 <typeparamref name="T"/> 池中的空闲实例数（调试 / 测试用）。</summary>
        public static int Count<T>() where T : class, new()
        {
            lock (Gate)
            {
                return Pools.TryGetValue(typeof(T), out var stack) ? stack.Count : 0;
            }
        }

        /// <summary>清空 <typeparamref name="T"/> 的池（丢弃其中的空闲实例，交给 GC）。</summary>
        public static void Clear<T>() where T : class, new()
        {
            lock (Gate)
            {
                Pools.Remove(typeof(T));
            }
        }

        /// <summary>清空全部类型的池（切场景 / 引擎拆卸时调用，避免静态池跨局持有对象）。</summary>
        public static void ClearAll()
        {
            lock (Gate)
            {
                Pools.Clear();
            }
        }
    }
}
