// ─────────────────────────────────────────────────────────────────────────────
// CloverEngine · Runtime/Presentation/DragDropLayer.cs
// 「拖放层」本体 = 拖影跟随指针 + 目标格高亮（回调式）+ 屏幕点 → 网格坐标 + 落点判定（可否放下可注入）。
//
// 机制（**不含任何项目语义**）：
//   · 拖影节点 + 目标格高亮节点（各建一次、复用之）；
//   · 拖动中：拖影贴在指针上（`rectTransform.position = 鼠标屏幕点`）+
//     指针下的格亮起 / 离开则熄灭（**只在状态变化时报一次**日志）；
//   · 松手：先收表现（藏拖影 / 熄高亮）→ 再判落点 → 再发请求；
//   · 屏幕点 → 网格坐标（屏幕点 → 格下标）；
//   · ★ **落点判定留给调用方**（"装备槽 / 背包内移动 / 丢地面"是业务语义）。
//   屏幕点换算复用的既有件：`Runtime/Core/ScreenPointUtil.cs`（画布模式取相机的唯一口径，
//     含"Overlay 画布世界坐标就是屏幕像素"的口径依据）。
//
// ★ 适用面与分工：
//   「列表/格盘里的东西可以被拖到另一个格上」是**通用** UI 结构："取拖影 → 跟随指针 →
//   屏幕点换算成格 → 高亮目标格 → 松手判落点"前四步在本件；**最后一步的业务规则**
//   （哪些格可放 / 装备 vs 背包 / 堆叠 / 丢地面）由调用方给。
//   ⇒ 业务只提供两样东西：① 网格几何（几列几行 / 格大小 / 格区左上角）
//     ② 两个回调（`TargetChanged` 画高亮 / `CanPlace` 说这个格能不能放）。
//
// ★ 与 `DragGestureRouter` 的分工（⛔ 不要互相替代）：
//   `DragGestureRouter` 回答「这次手势**归谁**」（拖拽 vs 滚动，纯手势仲裁，不含任何坐标）；
//   本件回答「归拖拽之后**发生了什么**」（拖影在哪 / 指着哪个格 / 松手落在哪）。
//   两者串起来 = 一次完整的"从滚动列表里把格子拖到目标槽"。
//
// 边界与失效模式：
//   · 相机必须按**画布模式**取 —— 换算收敛到 `ScreenPointUtil`（Overlay ⇒ null）。把场地相机
//     喂进 Overlay 画布的换算**不抛异常**，只会让命中恒为 false（现象 = "拖不动 / 格不亮"）。
//   · 拖影位置也用 `ScreenPointUtil.TryScreenToWorldInRect`（取拖影父矩形的平面）：那只在
//     **Overlay 画布**下才恰好等于"世界坐标 = 屏幕像素"；非 Overlay 画布下必须投影一次。
//     换算失败 ⇒ 退回屏幕点直填 + 降频 Warn（⛔ 不静默停住 —— 拖影停住会被误判成"拖拽断了"）。
//   · 目标格"变化才回调"：逐帧回调会让业务每帧重设节点（白跑 + 状态日志刷屏）。
//   · 落点判定的**几何部分**（屏幕点 → 格）与**可否放**分开：前者纯计算（离线可断言），
//     后者由业务注入；`CanPlace == null` ⇒ 一律按可放处理 + 降频 Warn（拿不到判定依据必须能查）。
//   · 边界点归属与 uGUI `RectTransformUtility.RectangleContainsScreenPoint` **同一口径**
//     （左闭右开）—— 见 `DragGridMetrics.TryCellAtLocal` 的复核注释。
// ─────────────────────────────────────────────────────────────────────────────

using System;
using UnityEngine;
using UnityEngine.UI;

namespace CloverEngine
{
    /// <summary>
    /// 网格几何（**纯值，不持有任何 Unity 对象**）——「屏幕点 → 第几列第几行」的唯一真相包。
    /// <para>
    /// 坐标系 = <c>gridRect</c> 的**局部坐标**（`RectTransformUtility` 的 localPoint 口径，
    /// 原点在矩形 pivot 处；画布 y 向上）。因此"格区左上角"通常是 **负的 y**。
    /// </para>
    /// </summary>
    public struct DragGridMetrics
    {
        /// <summary>列数（&gt; 0）。</summary>
        public int Cols;

        /// <summary>行数（&gt; 0）。</summary>
        public int Rows;

        /// <summary>格区**左上角**（局部坐标；画布 y 向上 ⇒ 一般是负值）。</summary>
        public Vector2 Origin;

        /// <summary>单元格尺寸（命中区口径；&gt; 0）。</summary>
        public Vector2 CellSize;

        /// <summary>相邻格左上角的间距；&lt;= 0 的轴 ⇒ 回落 <see cref="CellSize"/>（无缝平铺）。</summary>
        public Vector2 Pitch;

        /// <summary>
        /// 造一份网格几何（`Pitch` 传 0 ⇒ 按 `CellSize` 无缝平铺 —— 绝大多数格盘就是这个形状）。
        /// </summary>
        public static DragGridMetrics Create(int cols, int rows, Vector2 origin, Vector2 cellSize,
            Vector2 pitch = default)
        {
            var m = new DragGridMetrics { Cols = cols, Rows = rows, Origin = origin, CellSize = cellSize, Pitch = pitch };
            if (m.Pitch.x <= 0f) m.Pitch.x = cellSize.x;
            if (m.Pitch.y <= 0f) m.Pitch.y = cellSize.y;
            return m;
        }

        /// <summary>几何是否可用（列数 / 行数 / 格尺寸 / 间距都必须是正数）。</summary>
        public bool IsValid =>
            Cols > 0 && Rows > 0 &&
            CellSize.x > 0f && CellSize.y > 0f &&
            Pitch.x > 0f && Pitch.y > 0f;

        /// <summary>第 (col,row) 格的**中心**（局部坐标；行向下递增）。</summary>
        public Vector2 Center(int col, int row) => new Vector2(
            Origin.x + (col + 0.5f) * Pitch.x,
            Origin.y - (row + 0.5f) * Pitch.y);

        /// <summary>第 (col,row) 格的矩形（局部坐标；<c>Rect</c> 的位置参数是**最小角**）。</summary>
        public Rect CellRect(int col, int row)
        {
            var c = Center(col, row);
            return new Rect(c.x - CellSize.x * 0.5f, c.y - CellSize.y * 0.5f, CellSize.x, CellSize.y);
        }

        /// <summary>行优先下标（<c>row * Cols + col</c>）——与业务侧"第 i 格"的下标口径一致。</summary>
        public int IndexOf(int col, int row) => row * Cols + col;

        /// <summary>
        /// 局部点 → 网格坐标（**纯函数，离线可断言**）。命中返回 <c>true</c>，否则返回 <c>false</c>
        /// 并把 <paramref name="col"/> / <paramref name="row"/> 置 -1。
        ///
        /// <para>
        /// ★ 边界口径 = uGUI 的 <c>Rect.Contains</c>（**左闭右开**：<c>xMin &lt;= x &lt; xMax</c>）——
        /// 与改前"逐格 <c>RectangleContainsScreenPoint</c>"**逐点同结论**：格左上角点算**本格**、
        /// 格右下边界点算**下一格**、格区右/下边界外算**无格**。末尾那次 <see cref="Rect.Contains"/>
        /// 是对 <c>Mathf.FloorToInt</c> 在边界上的浮点舍入做的复核（⛔ 不要删：删了边界点会漂一格）。
        /// </para>
        /// <para>几何非法（<see cref="IsValid"/> 为 false）⇒ 直接返回 <c>false</c>，⛔ 不做"缺省 1 格"的猜测。</para>
        /// </summary>
        public bool TryCellAtLocal(Vector2 local, out int col, out int row)
        {
            col = -1;
            row = -1;
            if (!IsValid) return false;

            col = Mathf.FloorToInt((local.x - Origin.x) / Pitch.x);
            row = Mathf.FloorToInt((Origin.y - local.y) / Pitch.y);
            if (col < 0 || col >= Cols || row < 0 || row >= Rows)
            {
                col = -1;
                row = -1;
                return false;
            }

            if (!CellRect(col, row).Contains(local))
            {
                // 非预期分支：过了下标范围却没过矩形（只可能来自浮点舍入）⇒ 留痕再放弃本次命中
                LogThrottle.WarnThrottled(Tag, "grid.edge.round",
                    $"格边界浮点复核未通过：local={local} col={col} row={row} rect={CellRect(col, row)} "
                    + "⇒ 本次按『无格』处理（若频繁出现请核对 Pitch / CellSize 的取值来源）");
                col = -1;
                row = -1;
                return false;
            }

            return true;
        }

        /// <summary>日志标签（网格几何没有实例，日志用本常量）。</summary>
        public const string Tag = "DragDropLayer";
    }

    /// <summary>
    /// 「指针下的那一格」——一次命中的结论（几何，**不含**业务语义）。
    /// </summary>
    public struct DragGridHit
    {
        /// <summary>指针下有没有格（<c>false</c> ⇒ 其余字段无意义，<see cref="local"/> 仍填）。</summary>
        public bool valid;

        /// <summary>列 / 行（无效时 -1）。</summary>
        public int col;
        public int row;

        /// <summary>行优先下标（无效时 -1）。</summary>
        public int index;

        /// <summary>指针在 <c>gridRect</c> 的局部坐标（换算成功时填；否则为零向量）。</summary>
        public Vector2 local;

        /// <summary>该格中心（局部坐标）——调用方把高亮块摆到此处即可（⛔ 不必自己再算一遍）。</summary>
        public Vector2 center;

        /// <summary>可读描述（日志 / 自检用）。</summary>
        public override string ToString() => valid
            ? $"格({col},{row})#{index} center={center}"
            : $"无格(local={local})";
    }

    /// <summary>落点判定的结论类别（与具体游戏的"装备槽 / 地面"无关）。</summary>
    public enum DropVerdict
    {
        /// <summary>指针下没有格（拖到格区之外 / 换算失败）⇒ 落点判定交给调用方的业务规则。</summary>
        None = 0,

        /// <summary>落在某个格上，且 <see cref="DragDropLayer.CanPlace"/> 放行。</summary>
        Cell = 1,

        /// <summary>落在某个格上，但 <see cref="DragDropLayer.CanPlace"/> 说这格现在放不下。</summary>
        Rejected = 2,
    }

    /// <summary>一次 <see cref="DragDropLayer.Resolve"/> 的结果（纯数据）。</summary>
    public struct DragDropOutcome
    {
        /// <summary>本次落点的格（<c>hit.valid == false</c> ⇒ 没落在任何格上）。</summary>
        public DragGridHit hit;

        /// <summary>类别。</summary>
        public DropVerdict verdict;

        /// <summary><c>CanPlace</c> 的结论（<c>hit.valid == false</c> 时恒 false）。</summary>
        public bool placeable;

        /// <summary>可读描述（日志 / 自检用）。</summary>
        public override string ToString()
            => $"verdict={verdict} placeable={placeable} hit=[{hit}]";
    }

    /// <summary>
    /// 「拖放层」：拖影跟随 + 目标格高亮回调 + 屏幕点 → 网格坐标 + 落点判定。
    ///
    /// <para>
    /// 纯机制，**无 MonoBehaviour、无协程、不注册回调** —— 由业务的拖拽事件（
    /// <c>IBeginDragHandler</c> / <c>IDragHandler</c> / <c>IEndDragHandler</c> 或自研列表）
    /// 在自己的时机调 <see cref="Begin"/> / <see cref="Move"/> / <see cref="End"/>。
    /// </para>
    /// <para>
    /// ⛔ 本件**不含**任何游戏语义：哪些格可放、装备槽怎么判、堆叠规则、丢到地面怎么处理
    /// —— 一律由调用方（<see cref="CanPlace"/> / <see cref="TargetChanged"/> / 自己的落点纯函数）回答。
    /// </para>
    /// </summary>
    public sealed class DragDropLayer
    {
        /// <summary>日志标签。</summary>
        public const string Tag = "DragDropLayer";

        /// <summary>默认拖影节点名（业务自己给工厂时无所谓，用于默认建件）。</summary>
        public const string DefaultGhostName = "DragGhost";

        private readonly Component _context;
        private RectTransform _gridRect;
        private DragGridMetrics _grid;

        private GameObject _ghost;
        private Func<Transform, GameObject> _ghostTake;
        private Action<GameObject> _ghostGive;

        /// <summary>
        /// 建一个拖放层。
        /// </summary>
        /// <param name="context">
        /// 画布下的任一组件（面板自身 / 它的根节点）——**只用于按画布模式取相机**（`ScreenPointUtil`）。
        /// <c>null</c> ⇒ 按 Overlay 口径处理 + 降频 Warn。
        /// </param>
        /// <param name="gridRect">格区所在矩形（局部坐标系的参照物）；<c>null</c> ⇒ 本层无法判格（降频 Warn）。</param>
        /// <param name="grid">网格几何（<see cref="DragGridMetrics"/>）。</param>
        public DragDropLayer(Component context, RectTransform gridRect, DragGridMetrics grid)
        {
            _context = context;
            _gridRect = gridRect;
            _grid = grid;

            if (context == null)
                LogThrottle.WarnThrottled(Tag, "ctor.nocontext",
                    "建 DragDropLayer 时 context 为 null ⇒ 屏幕点换算按 Overlay 画布处理（取不到画布模式）");
            if (gridRect == null)
                LogThrottle.WarnThrottled(Tag, "ctor.nogrid",
                    "建 DragDropLayer 时 gridRect 为 null ⇒ 本层无法把屏幕点换算成格（Resolve 恒返回 None）");
            if (!grid.IsValid)
                LogThrottle.WarnThrottled(Tag, "ctor.badgrid",
                    $"建 DragDropLayer 时网格几何非法（Cols={grid.Cols} Rows={grid.Rows} "
                    + $"CellSize={grid.CellSize} Pitch={grid.Pitch}）⇒ 命中恒为『无格』；"
                    + "请核对列数/行数/格尺寸是否都为正数");

            Target = default;
            LastVerdictName = "none";
        }

        /// <summary>取相机用的上下文（画布下的任一组件）。</summary>
        public Component Context => _context;

        /// <summary>格区矩形（局部坐标的参照物；可在运行时换，例如换页换格区）。</summary>
        public RectTransform GridRect
        {
            get => _gridRect;
            set => _gridRect = value;
        }

        /// <summary>网格几何。</summary>
        public DragGridMetrics Grid
        {
            get => _grid;
            set => _grid = value;
        }

        /// <summary>当前拖影节点（<c>null</c> = 还没取过 / 已归还）。</summary>
        public GameObject Ghost => _ghost;

        /// <summary>
        /// 目标格**变化**时的回调（进入某格 / 离开所有格各回调一次；⛔ 不是逐帧）。
        /// <para>回调里 <c>hit.valid == false</c> ⇒ 请**取消高亮**（本件不画任何东西）。</para>
        /// </summary>
        public Action<DragGridHit> TargetChanged { get; set; }

        /// <summary>
        /// 业务注入的「这个格现在能不能放」(col, row) ⇒ 可放？
        /// <para>
        /// <c>null</c> ⇒ 一律按**可放**处理 + 降频 Warn（拿不到判定依据必须能查，⛔ 不静默拦下也没有静默放行）。
        /// </para>
        /// </summary>
        public Func<int, int, bool> CanPlace { get; set; }

        /// <summary>当前目标格（由 <see cref="UpdateTarget"/> / <see cref="Begin"/> 维护）。</summary>
        public DragGridHit Target { get; private set; }

        /// <summary>最近一次落点判定的分支名（<c>none</c> / <c>cell</c> / <c>rejected</c>）——自检 / 驱动脚本读它。</summary>
        public string LastVerdictName { get; private set; }

        /// <summary>
        /// 注入拖影的**取件 / 归还**（池化：同一实例复用，⛔ 不每次 `Instantiate`）。
        /// <para>
        /// 典型接法 = 引擎对象池：<c>take: _ => Game.Pool.Spawn("UI/DragGhost", parent)</c>、
        /// <c>give: go => Game.Pool.Despawn(go)</c>；项目已有现成拖影节点（面板构建时建好的）⇒
        /// <c>take: _ => 那个节点</c>、<c>give: go => go.SetActive(false)</c>。
        /// </para>
        /// <para>
        /// 不注入 ⇒ 本层首次 <see cref="ShowGhost"/> 时**自建**一个
        /// <c>new GameObject(DragGhost, RectTransform, Image)</c> 并自己持有（<see cref="ReleaseGhost"/> 销毁）。
        /// </para>
        /// </summary>
        /// <param name="take">取件：给父节点 ⇒ 返回拖影节点；返回 <c>null</c> ⇒ 本次没有拖影（记 Error）。</param>
        /// <param name="give">归还：把节点交回去（可 <c>null</c> ⇒ 只 <c>SetActive(false)</c> 不还池）。</param>
        public void SetGhostPool(Func<Transform, GameObject> take, Action<GameObject> give)
        {
            _ghostTake = take;
            _ghostGive = give;
        }

        /// <summary>
        /// 起拖：确保拖影存在（池化取件）→ 置尺寸 → 贴到指针处 → 记下本次手势的起点。
        /// <para>返回拖影节点（取不到 ⇒ <c>null</c>，调用方据此决定"这次拖拽要不要继续"）。</para>
        /// </summary>
        /// <param name="screen">按下 / 起拖时的指针屏幕坐标。</param>
        /// <param name="ghostSize">拖影尺寸（画布单位）——各项目按物品占格算，本件只搬运。</param>
        public GameObject ShowGhost(Vector2 screen, Vector2 ghostSize)
        {
            if (_ghost == null)
            {
                _ghost = _ghostTake != null ? _ghostTake(_context != null ? _context.transform : null) : null;
                if (_ghost == null && _ghostTake != null)
                    Game.Logger?.Error(Tag,
                        "拖影取件回调返回 null ⇒ 本次拖拽没有拖影（请检查池 key / 工厂是否注册）");
                if (_ghost == null && _ghostTake == null)
                    _ghost = CreateDefaultGhost();
                if (_ghost == null) return null;
            }

            _ghost.SetActive(true);
            var rt = _ghost.transform as RectTransform;
            if (rt == null)
            {
                Game.Logger?.Error(Tag, $"拖影节点 {_ghost.name} 上没有 RectTransform ⇒ 无法定位 / 定尺");
                return null;
            }

            rt.sizeDelta = ghostSize;
            FollowGhost(screen);
            return _ghost;
        }

        /// <summary>按指针更新拖影位置（复用 <see cref="ScreenPointUtil"/>；换算失败退回屏幕点直填 + 降频 Warn）。</summary>
        /// <returns>本次是否真的挪了拖影（没有拖影 ⇒ <c>false</c>）。</returns>
        public bool FollowGhost(Vector2 screen)
        {
            if (_ghost == null) return false;

            var rt = _ghost.transform as RectTransform;
            if (rt == null)
            {
                LogThrottle.WarnThrottled(Tag, "ghost.norect",
                    $"拖影 {_ghost.name} 上没有 RectTransform ⇒ 本次不跟指针");
                return false;
            }

            // 拖影贴在指针上：换算用**拖影父矩形的平面**（Overlay 画布下世界坐标就是屏幕像素；非 Overlay
            // 必须投一次，见 ScreenPointUtil 文件头）。⛔ 别在这里自己写 RectTransformUtility 调用。
            var parent = rt.parent as RectTransform;
            if (parent != null && ScreenPointUtil.TryScreenToWorldInRect(parent, screen, _context, out var world))
            {
                rt.position = world;
                return true;
            }

            // 非预期分支：父矩形缺失 / 换算失败（ScreenPointUtil 内部已降频 Warn）——
            // 退回"世界坐标 = 屏幕点"（只在 Overlay 画布下正确，所以必须留痕）。
            LogThrottle.WarnThrottled(Tag, "ghost.followfallback",
                $"拖影父矩形缺失或换算失败（父={(parent != null ? parent.name : "(null)")}，screen={screen}）"
                + " ⇒ 本次按『世界坐标 = 屏幕点』直填（仅 Overlay 画布成立）");
            rt.position = new Vector3(screen.x, screen.y, rt.position.z);
            return true;
        }

        /// <summary>
        /// 收表现：藏拖影 + 目标格回调一次"无格"（= 取消高亮）+ 清目标。
        /// <para>⛔ 落点判定**不在这里**（那是 <see cref="Resolve"/>）；松手链路必须是"先收表现、再判落点"。</para>
        /// </summary>
        public void HideGhost()
        {
            if (_ghost != null) _ghost.SetActive(false);
            ClearTarget();
        }

        /// <summary>把拖影归还（有 <c>give</c> 回调就交回，否则销毁自建件）并断开引用。</summary>
        public void ReleaseGhost()
        {
            var go = _ghost;
            _ghost = null;
            if (go == null) return;

            if (_ghostGive != null)
            {
                _ghostGive(go);
                return;
            }

            // 没给归还回调：自建件由本层销毁（⛔ 不把自建件留在场景里，那会变成"下次拖拽多一个节点"）
            UnityEngine.Object.Destroy(go);
        }

        /// <summary>
        /// 屏幕点 → 网格坐标（**复用 <see cref="ScreenPointUtil"/>**：相机按画布模式取）。
        /// </summary>
        /// <param name="screen">指针屏幕坐标。</param>
        /// <param name="hit">命中结论；未命中时 <c>hit.valid == false</c>（<c>local</c> 若换算成功仍填）。</param>
        /// <returns>指针是否落在某个格上。</returns>
        public bool TryHit(Vector2 screen, out DragGridHit hit)
        {
            hit = default;

            if (_gridRect == null)
            {
                LogThrottle.WarnThrottled(Tag, "hit.nogrid",
                    "TryHit: gridRect 为 null（面板还没建 / 已销毁）⇒ 本次不判格");
                return false;
            }

            if (!ScreenPointUtil.TryScreenToLocalInRect(_gridRect, screen, _context, out var local))
            {
                // 失败原因（rect 为空等）已在 ScreenPointUtil 内部降频 Warn
                return false;
            }

            hit.local = local;
            if (!_grid.TryCellAtLocal(local, out var col, out var row)) return false;

            hit.valid = true;
            hit.col = col;
            hit.row = row;
            hit.index = _grid.IndexOf(col, row);
            hit.center = _grid.Center(col, row);
            return true;
        }

        /// <summary>
        /// 按指针刷新目标格；**只有状态真的变了**才回调 <see cref="TargetChanged"/>。
        /// </summary>
        /// <returns>本次是否发生了变化（调用方不需要据此做事，仅供自检）。</returns>
        public bool UpdateTarget(Vector2 screen)
        {
            TryHit(screen, out var hit);

            var changed = Target.valid != hit.valid || Target.index != hit.index;
            Target = hit;
            if (changed) TargetChanged?.Invoke(hit);
            return changed;
        }

        /// <summary>
        /// 落点判定：指针处有没有格 + 那格能不能放（由 <see cref="CanPlace"/> 回答）。
        /// <para>
        /// ★ **只判几何 + 可放性**，不含任何游戏语义；"装备槽 / 背包内移动 / 丢地面"仍由
        /// 调用方自己的纯函数判。
        /// </para>
        /// </summary>
        public DragDropOutcome Resolve(Vector2 screen)
        {
            TryHit(screen, out var hit);
            var outcome = new DragDropOutcome { hit = hit, verdict = DropVerdict.None, placeable = false };

            if (!hit.valid)
            {
                LastVerdictName = "none";
                return outcome;
            }

            var ok = true;
            if (CanPlace != null)
            {
                ok = CanPlace(hit.col, hit.row);
            }
            else
            {
                // 非预期分支：没接线（调用方忘了注入）⇒ 放行但必须留痕，否则"放不下"永远查不出原因
                LogThrottle.WarnThrottled(Tag, "resolve.nocanplace",
                    "Resolve: 未注入 CanPlace ⇒ 任何格都按『可放』处理（请接线，否则落点判定缺一半）");
            }

            outcome.placeable = ok;
            outcome.verdict = ok ? DropVerdict.Cell : DropVerdict.Rejected;
            LastVerdictName = ok ? "cell" : "rejected";
            return outcome;
        }

        // ═════════════════════════════════════════════════════════════════════
        // 一段手势的便捷封装（按下 → 移动 → 抬起）；调用方也可以只用上面的原子方法
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>按下 / 起拖：起拖影 + 首次刷新目标格（若起拖点就在某格上，回调立即来一次）。</summary>
        public DragGridHit Begin(Vector2 screen, Vector2 ghostSize)
        {
            ShowGhost(screen, ghostSize);
            UpdateTarget(screen);
            return Target;
        }

        /// <summary>移动：拖影跟指针 + 刷新目标格（变化才回调高亮）。</summary>
        public bool Move(Vector2 screen)
        {
            FollowGhost(screen);
            return UpdateTarget(screen);
        }

        /// <summary>
        /// 抬起：**先收表现**（藏拖影 / 取消高亮）→ **再判落点**（⛔ 顺序不能反：反了会让
        /// "松手那一刻的目标格"被清掉，落点判定拿到空目标）。
        /// </summary>
        public DragDropOutcome End(Vector2 screen)
        {
            var outcome = Resolve(screen);
            HideGhost();
            return outcome;
        }

        // ═════════════════════════════════════════════════════════════════════
        // 内部
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>清目标格（状态真的从"有格"变"无格"时才回调一次）。</summary>
        private void ClearTarget()
        {
            if (!Target.valid) return;
            Target = default;
            LastVerdictName = "none";
            TargetChanged?.Invoke(Target);
        }

        /// <summary>
        /// 自建默认拖影（只在本层没拿到业务工厂时用一次）：纯色半透明块 + <c>raycastTarget = false</c>
        /// （拖影不能吃射线，否则指针下的格子收不到事件）。
        /// </summary>
        private GameObject CreateDefaultGhost()
        {
            var go = new GameObject(DefaultGhostName, typeof(RectTransform), typeof(Image));
            var img = go.GetComponent<Image>();
            if (img != null)
            {
                // 半透明白：原版"拖影 = 物品的半透明副本"，这里没有项目素材 ⇒ 只给底色 + Warn
                img.color = new Color(1f, 1f, 1f, 0.65f);
                img.raycastTarget = false;
            }

            var rt = (RectTransform)go.transform;
            var parent = _context != null ? _context.transform : null;
            if (parent != null) rt.SetParent(parent, false);
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);

            Game.Logger?.Warn(Tag,
                "没有注入拖影取件回调 ⇒ 本层自建了一个纯色拖影（⛔ 这是兜底，不是交付形态："
                + "请用 SetGhostPool 接项目自己的拖影素材）");
            return go;
        }
    }
}
