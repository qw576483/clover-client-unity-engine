// ─────────────────────────────────────────────────────────────────────────────
// CloverEngine · Runtime/Presentation/RuntimePanelProvider.cs
// 「零资产」的运行时面板供给者：按类名反射造面板模板 → 交给 `UIManager` 克隆。
//
// 来源：clover-project-cr `client/Assets/Scripts/UI/PanelFactory.cs:41-216`
//   （该工程里的 `PanelFactory` 静态类，架构契约 D2 方案①）。原注释里记着它成立的**引擎侧依据**：
//   `UIManager.Open<T>` 的取面板链路 = 「拿一个 GameObject（`CloverPresentation.PanelProvider(类名)`，
//   默认实现是 `Resources.Load<GameObject>("UI/" + 类名)`）→ `Instantiate` → `GetComponent<T>()`」
//   （`Runtime/Presentation/UI.cs:131-149`），而 `CloverPresentation.PanelProvider` 的签名是
//   `public static Func<string, GameObject> PanelProvider { get; set; }`
//   （`Runtime/Presentation/CloverPresentation.cs:69`）—— 一个**公开可写**的函数槽。
//   ⇒ 「运行时 new GameObject + AddComponent」这条路径成立，且不需要任何 `.prefab` 资产。
//
// ═══════════════ 为什么落到引擎（四条要点，每条都有事故出处） ═══════════════
//
// ① **反射按类名找面板类型**（而不是写 `switch`）：函数槽只给得到字符串。写 `switch` 的话
//    每加一个面板都要回来改这个文件（它是引擎件、属公共地盘）⇒ 反射让"加面板 = 只加自己的文件"。
//    扫描口径 = 「`MonoBehaviour` + 实现 `IUIPanel` + 非抽象 + 非开放泛型」。
//
// ② **模板必须延后一帧销毁**（`⛔` 不许改成当场 `Object.Destroy`）：
//    `Instantiate` 由 `UIManager` 在**拿到返回值之后同步调用**，所以模板必须活过那一行。
//    当场 `Destroy` 会让对象在 `Instantiate` 之前变成 Unity 的"**假 null**"并抛
//    `MissingReferenceException` —— 症状是"面板偶尔开不出来 + 一条看不懂的异常"，
//    且**只在某些时序下出现**（最难复现的一类）。走 `Timer.After(0f, …)`：回调在**下一个** Tick
//    才触发，那一刻克隆已经完成，场景里不留残留物。
//
// ③ **按"当前总线对象"做幂等安装**：`CloverPresentation.PanelProvider` 是**静态**槽，而工程常
//    关闭域重载（Enter Play Mode Options）⇒ 裸 `bool _installed` 会**跨轮存活**：新一轮 Play 里它
//    仍是 true、`Install()` 直接早退，而静态槽可能已随引擎重建被清掉 ⇒ 之后每次 `Game.UI.Open`
//    都去找 `Resources/UI/{类名}.prefab` 并失败（表现为"面板打不开 / 点了没反应"）——
//    这是实测到的**僵尸会话**的一个成因面（托管侧被整体复位、静态残留）。
//    ⇒ 标识本轮用"**当前总线对象**"（`Game.Launch` 每轮新建 `EventBus`，见 `Runtime/Core/Game.cs`
//    的 Launch 路径），与工程其余订阅方口径一致。
//    ➕ 本件比参考实现多一条**自愈**：同一条总线时若发现静态槽里的委托**不是本实例的那个**
//    （被别的代码清掉 / 覆盖 / 域重载边缘），就重新装一次 —— 而不是直接早退。
//
// ④ **面板必须自己在 `OnOpen` 里建视觉树**（⛔ 不要放 `Awake` / `Start`）：
//    模板对象也会走一遍 `Awake`（`AddComponent` 当场触发），在 `Awake` 里建树会让**模板也建一份**
//    （白做一遍、还多一次资源加载）。这条是用法约定，引擎没法用代码挡住 ⇒ 写在注释里。
//
// ═══════════════ 与既有引擎件的边界（两条路线**并存**，⛔ 谁都不取代谁） ═══════════════
//
// · **prefab 路线（既有，仍是默认）**：`CloverPresentation.PanelProvider == null` 时
//   `UIManager` 按 `Resources/UI/{类名}` 取 prefab；`Editor/PanelPrefabBuilder`（编辑器横切，
//   `clover-client-unity-engine-index.md` §5.6）能按 `IUIPanel` 实现批量生成这些壳 prefab。
//   适合：需要在编辑器里调版面 / 面板里挂引用 / 美术要看到 prefab 的场景。
// · **运行时路线（本件）**：设上 `PanelProvider` ⇒ 零资产、面板完全由代码建。
//   适合：纯代码搭 UI 的工程、不想维护壳 prefab、想在编辑器外（CI / 无 Unity 资产的工程）也能跑。
// · 两条路线靠**同一个函数槽**切换 ⇒ 天然互斥、不会两套同时生效；本件 `Uninstall()` 把槽置回
//   `null`（= 回到 prefab 路线），⛔ 不删 `Editor/PanelPrefabBuilder`、⛔ 不改它的行为。
// · 落点 = `Runtime/Presentation/`（表现域 / UI）：本件只做"造面板对象"，不订阅数据、不碰网络，
//   符合 `结构规则.md` §3.5。只依赖 `Core`（`IUIPanel` / `IEventBus` / `ITimer` / `LogThrottle`）
//   ⇒ 依赖方向合规。
//
// ═══════════════ ⚠️ IL2CPP 裁剪（部署前必读） ═══════════════
//   反射扫程序集在 **IL2CPP** 下会被**代码裁剪（managed code stripping）**打掉：
//   面板类型"没有任何静态引用"，裁剪器认为它们没人用 ⇒ 从包里删掉 ⇒ `GetTypes()` 里没有它们
//   ⇒ 面板**开不出来且完全静默**（引擎只会报一条"找不到面板类型"的 Error，其他一切正常）。
//   ⇒ 用了本件的工程**必须**在 `link.xml`（放 `Assets/` 下任意位置，名字固定）里保留面板类型
//   （或至少保留面板所在程序集），例如：
//   <code>
//   &lt;linker&gt;
//     &lt;assembly fullname="Assembly-CSharp" preserve="all" /&gt;
//   &lt;/linker&gt;
//   </code>
//   另：`ReflectionTypeLoadException` 时本件按"已加载的那部分"继续（并把跳过的原因留痕），
//   ⛔ 不整体放弃（否则一个坏类型会让所有面板都开不出来）。
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace CloverEngine
{
    /// <summary>
    /// 运行时面板供给者：给 <see cref="CloverPresentation.PanelProvider"/> 装一个"按类名反射造面板"的实现，
    /// 使 <c>Game.UI.Open&lt;T&gt;</c> **不再依赖** <c>Resources/UI/{类名}.prefab</c> 资产。
    /// <para>
    /// <b>用法</b>：
    /// <code>
    /// // 一次（例如启动流程里，Game.Launch 之后、第一次 Game.UI.Open 之前）：
    /// _provider = new RuntimePanelProvider(typeof(LoginPanel).Assembly);   // 传面板所在的程序集
    /// _provider.Install(Game.Event);                                      // 幂等；同一条总线只生效一次
    /// // 关服 / 退出流程时（可选）：
    /// _provider.Uninstall();                                              // 槽置回 null ⇒ 回到 prefab 路线
    /// </code>
    /// </para>
    /// <para>
    /// <b>必须在 <c>Game.Launch</c> 之后调用</b>：此前 `Game.Event` 为空 ⇒ `Install` 记一条 Error 并返回
    /// （⛔ 不静默，否则表现为"所有面板都去找 prefab 且找不到"）。
    /// 也必须在**第一次 `Game.UI.Open`** 之前 —— 晚装只影响之后打开的面板，早先那次会因为找不到
    /// prefab 而报 "Panel prefab not found"。
    /// </para>
    /// <para><b>本件不持有面板实例</b>：它只负责"造一个模板交给 `UIManager` 克隆"，面板的生命周期
    /// （打开 / 关闭 / 入栈）仍归 `UIManager`。</para>
    /// </summary>
    public sealed class RuntimePanelProvider
    {
        private const string Tag = "RuntimePanelProvider";

        /// <summary>面板类型所在程序集（按顺序扫描；同名类先到者胜）。</summary>
        private readonly List<Assembly> _assemblies = new List<Assembly>();

        /// <summary>缓存下来的供给者委托（既装进静态槽，也用来核对"槽里还是不是本实例" ⇒ 支持自愈）。</summary>
        private readonly Func<string, GameObject> _provider;

        /// <summary>可注入的定时器（模板延后销毁用）；<c>null</c> 时回落 <see cref="Game.Timer"/>。</summary>
        private readonly ITimer _timer;

        /// <summary>类名 → 面板类型；惰性构建一次。</summary>
        private Dictionary<string, Type> _panelTypes;

        /// <summary>
        /// 已交出但**还没被延后销毁**的模板（正常是空的；仅在 Timer 不可用时兜底，见
        /// <see cref="Uninstall"/>）。
        /// </summary>
        private readonly List<GameObject> _pending = new List<GameObject>();

        /// <summary>装静态槽时那一轮的引擎总线 —— 也就是"**本轮引擎**"的标识（见类注释③）。</summary>
        private IEventBus _installedBus;

        /// <param name="panelAssemblies">
        /// 面板类型所在的程序集（一般传 `typeof(某面板).Assembly`）。
        /// 可以不给（之后用 <see cref="AddAssembly"/> 补），但**必须至少给一个**才能在
        /// <see cref="Install"/> 后开到面板 —— 引擎所在的程序集里没有业务面板。
        /// </param>
        public RuntimePanelProvider(params Assembly[] panelAssemblies)
            : this(null, panelAssemblies)
        {
        }

        /// <param name="timer">定时器（模板延后销毁用）；传 <c>null</c> 取 <see cref="Game.Timer"/>。</param>
        /// <param name="panelAssemblies">面板类型所在的程序集。</param>
        public RuntimePanelProvider(ITimer timer, Assembly[] panelAssemblies)
        {
            _timer = timer;
            _provider = Provide;                    // 缓存方法组转换，供槽内容比对（见 Install）
            if (panelAssemblies != null)
            {
                for (var i = 0; i < panelAssemblies.Length; i++) AddAssembly(panelAssemblies[i]);
            }
        }

        /// <summary>补一个要扫描的程序集（⛔ 在第一次 <see cref="Install"/> 之前加齐，之后加要 <see cref="Invalidate"/>）。</summary>
        public void AddAssembly(Assembly assembly)
        {
            if (assembly == null) return;
            if (_assemblies.Contains(assembly)) return;
            _assemblies.Add(assembly);
        }

        /// <summary>当前是否装着本实例的供给者（且总线仍是安装时那条）。</summary>
        public bool IsInstalled
        {
            get { return _installedBus != null && ReferenceEquals(CloverPresentation.PanelProvider, _provider); }
        }

        /// <summary>安装时那一轮的引擎总线（未安装为 <c>null</c>）。</summary>
        public IEventBus InstalledBus { get { return _installedBus; } }

        /// <summary>已登记的面板类型数；**会触发扫描**（只读诊断用，别放热路径）。</summary>
        public int PanelTypeCount { get { return PanelTypes.Count; } }

        /// <summary>
        /// 已登记的面板类型（只读快照；自检 / 诊断用，⛔ 不要拿它去实例化 —— 实例化走
        /// <see cref="CloverPresentation.PanelProvider"/> 那条链路）。
        /// </summary>
        public IReadOnlyDictionary<string, Type> PanelTypes
        {
            get
            {
                if (_panelTypes == null) _panelTypes = BuildPanelTypeMap();
                return _panelTypes;
            }
        }

        /// <summary>丢掉类型表（用 <see cref="AddAssembly"/> 改了程序集集合之后调，下次访问重建）。</summary>
        public void Invalidate()
        {
            _panelTypes = null;
        }

        /// <summary>
        /// 装上供给者（幂等）。必须在 <c>Game.Launch</c> 之后、**第一次 <c>Game.UI.Open</c>** 之前调用。
        /// </summary>
        /// <param name="bus">当前的引擎总线（一般传 <c>Game.Event</c>）；用它标识"本轮引擎"。</param>
        /// <returns>
        /// <c>true</c> = 本次真的装了（或**自愈重装**了）；<c>false</c> = 同一条总线上已经装好、无需动作；
        /// 传 <c>null</c> 总线时返回 <c>false</c> 并留 Error（说明调用时机早于 `Game.Launch`）。
        /// </returns>
        public bool Install(IEventBus bus)
        {
            if (bus == null)
            {
                // 非预期分支：Install 早于 Game.Launch（本方法的契约就是"必须在其之后"）。
                // 留痕，别静默 —— 否则表现为"所有面板都去找 prefab 且找不到"。
                Game.Logger?.Error(Tag,
                    "引擎总线为空（Install 早于 Game.Launch？），面板供给者未装上；" +
                    "本方法的契约是「必须在 Game.Launch 之后、第一次 Game.UI.Open 之前」", null);
                return false;
            }

            if (ReferenceEquals(bus, _installedBus))
            {
                // 同一条总线：正常情况就是"已经装好了"。但静态槽可能被别的代码清掉 / 换掉
                //（域重载边缘、或别的模块也去写同一个槽）⇒ 自愈重装，⛔ 不直接早退。
                if (ReferenceEquals(CloverPresentation.PanelProvider, _provider)) return false;

                LogThrottle.WarnOnce(Tag, "RuntimePanelProvider.SlotOverwritten[" + GetHashCode() + "]",
                    "静态槽 PanelProvider 已不是本供给者（被清掉 / 被别的模块覆盖？）⇒ 按当前总线自愈重装");
                CloverPresentation.PanelProvider = _provider;
                return true;
            }

            if (_installedBus != null)
            {
                // 换总线 = 引擎重新 Launch（编辑器里每次 Play 都会发生）。只在这一刻报一次，不刷屏。
                Game.Logger?.Info(Tag, "检测到新的事件总线（引擎重新 Launch），重装面板供给者");
            }

            _installedBus = bus;
            CloverPresentation.PanelProvider = _provider;
            Game.Logger?.Info(Tag,
                "面板供给者已替换为运行时构建（零 `Resources/UI/*.prefab` 资产；" +
                "扫描程序集 " + _assemblies.Count + " 个 / 已登记面板类型 " + PanelTypes.Count + " 个）");
            return true;
        }

        /// <summary>
        /// 还原函数槽（= 回到 `Resources/UI/{类名}` 的 prefab 路线）并清掉可能的残留模板。
        /// ⛔ 只影响之后打开的面板（已经开着的那些归 `UIManager`，本件不动）。
        /// </summary>
        public void Uninstall()
        {
            if (_installedBus != null)
            {
                // 只在槽里还是自己时才清（否则会把别人装的供给者一起抹掉）。
                if (ReferenceEquals(CloverPresentation.PanelProvider, _provider))
                    CloverPresentation.PanelProvider = null;
                _installedBus = null;
            }

            for (var i = 0; i < _pending.Count; i++)
            {
                if (_pending[i] != null) UnityEngine.Object.Destroy(_pending[i]);
            }
            _pending.Clear();
        }

        /// <summary>
        /// 引擎回调：按类名造一个"只带 `RectTransform` + 面板组件"的空 GameObject 交给 `UIManager` 克隆。
        /// 找不到类型时返回 <c>null</c> —— 引擎会打它自己的 Error 并**不打开面板**（不静默、不崩）。
        /// </summary>
        private GameObject Provide(string typeName)
        {
            if (string.IsNullOrEmpty(typeName))
            {
                Game.Logger?.Error(Tag, "供给者收到空类名，无法造面板", null);
                return null;
            }

            var type = ResolvePanelType(typeName);
            if (type == null)
            {
                Game.Logger?.Error(Tag,
                    "找不到面板类型 `" + typeName + "`（须是实现 IUIPanel 的 MonoBehaviour 子类，" +
                    "且它所在的程序集已通过构造参数 / AddAssembly 交给本供给者）⇒ 本次不打开面板", null);
                return null;
            }

            // 带 RectTransform：面板根要被挂进 Canvas 的层级节点下，且其内部的 Image/Text 依赖
            // RectTransform 父链做布局。只 new GameObject 的话根节点是普通 Transform，整棵 UI 树会错位。
            var template = new GameObject(typeName, typeof(RectTransform));
            template.AddComponent(type);

            var timer = _timer ?? Game.Timer;
            if (timer != null)
            {
                // 延后一帧销毁（见类注释②：必须活过 UIManager 随后的 Instantiate 那一行）。
                GameObject captured = template;
                timer.After(0f, () =>
                {
                    if (captured != null) UnityEngine.Object.Destroy(captured);
                });
            }
            else
            {
                // 非预期分支：Timer 缺失（理论上只在 Launch 之前）。留痕并把它记进兜底清单，
                // 由 Uninstall 收尾 —— 绝不静默留下一个永远不销毁的对象。
                LogThrottle.WarnOnce(Tag, "RuntimePanelProvider.NoTimer[" + GetHashCode() + "]",
                    "定时器不可用（Game.Timer 为空），面板模板无法延后销毁 ⇒ 记入兜底清单，由 Uninstall 回收");
                _pending.Add(template);
            }

            return template;
        }

        /// <summary>按类名（先精确匹配简单名，再匹配完整名）在已登记的程序集里找面板类型。</summary>
        private Type ResolvePanelType(string typeName)
        {
            var map = PanelTypes;

            Type type;
            if (map.TryGetValue(typeName, out type)) return type;

            // 兜底：调用方传了完整名（`命名空间.类名`）时也认。
            foreach (var kv in map)
            {
                if (kv.Value.FullName == typeName) return kv.Value;
            }
            return null;
        }

        /// <summary>
        /// 扫描已登记的程序集，建立「简单类名 → 类型」。只收能当面板用的类型
        /// （`MonoBehaviour` + 实现 <see cref="IUIPanel"/> + 非抽象 + 非开放泛型）。
        /// <para>同名不同命名空间 ⇒ 简单名只能取到先到的那一个 ⇒ **必须留痕**（否则表现为
        /// "某个面板永远打不开"，而且毫无线索）。</para>
        /// </summary>
        private Dictionary<string, Type> BuildPanelTypeMap()
        {
            var map = new Dictionary<string, Type>(StringComparer.Ordinal);
            if (_assemblies.Count == 0)
            {
                LogThrottle.WarnOnce(Tag, "RuntimePanelProvider.NoAssemblies[" + GetHashCode() + "]",
                    "没有登记任何程序集 ⇒ 面板类型表为空，所有面板都开不出来；" +
                    "构造时传 `typeof(某个面板).Assembly`，或先 AddAssembly");
                return map;
            }

            for (var a = 0; a < _assemblies.Count; a++)
            {
                Type[] types;
                try
                {
                    types = _assemblies[a].GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    // 有类型加载失败时仍尽量把能用的收进来（GetTypes 会整体抛，Types 里保留成功的那部分）。
                    types = ex.Types;
                    Game.Logger?.Warn(Tag,
                        "扫描程序集 `" + _assemblies[a].GetName().Name + "` 时部分类型加载失败（" +
                        ex.Message + "）⇒ 按已加载的部分继续");
                }

                if (types == null) continue;

                for (var i = 0; i < types.Length; i++)
                {
                    var t = types[i];
                    if (t == null || !t.IsClass || t.IsAbstract) continue;
                    if (t.IsGenericTypeDefinition) continue;          // 开放泛型不能被 AddComponent
                    if (!typeof(MonoBehaviour).IsAssignableFrom(t)) continue;
                    if (!typeof(IUIPanel).IsAssignableFrom(t)) continue;

                    Type existing;
                    if (map.TryGetValue(t.Name, out existing))
                    {
                        // 同名不同命名空间：供给者只给得到简单名 ⇒ 后到者不可达，必须留痕。
                        LogThrottle.WarnOnce(Tag, "RuntimePanelProvider.NameCollision/" + t.Name,
                            "面板类名重复 `" + t.Name + "`（" + existing.FullName + " / " + t.FullName +
                            "）：按简单名取的供给者只能取到前者，后者开不出来");
                        continue;
                    }
                    map[t.Name] = t;
                }
            }

            Game.Logger?.Info(Tag, "已登记 " + map.Count + " 个面板类型（供 Game.UI.Open<T> 按类名取）");
            return map;
        }
    }
}
