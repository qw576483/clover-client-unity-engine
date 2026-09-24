// ─────────────────────────────────────────────────────────────────────────────
// CloverEngine · Runtime/Presentation/SortingLayers.cs
// 2D `sortingOrder` 的**层级预算表 + 深度序 + 同序确定性次级键**（纯逻辑，不持有任何 Unity 对象）。
//
// 来源（公式与取值逐字搬移；层数 / 预算已参数化）：
//   clover-project-cr · client/Assets/Scripts/View/UnitView.cs
//     · :395-405 `Apply()` 里的深度序：
//         `sortingOrder = SortingOrder.Unit + Mathf.RoundToInt((ArenaTilesH * 0.5f - worldPos.y) * 16f)`
//         （口径：俯视视角下"越靠近屏幕下方（y 越小）越靠前"；取 **16 级/格**，
//           旧值 2 级/格 会让相距半格的两个单位拿到同一个 order ⇒ 谁盖谁由渲染器枚举顺序决定）
//     · :848-884 `SortingOrder` 静态类 = **层级预算表**
//         （底图 0 / 装饰 10 / 塔 50 / 落点指示 200 / 单位 1000±深度 / 血条 2000 / 特效 3000）
//     · :541-580 `DepthTiebreak(int id)` + `DepthTiebreakStep = 1e-4f` + `DepthTiebreakMod = 256`
//         （同 `sortingOrder` 下的**确定性次级键**：一个只由实体 id 决定的微小 z 偏移）
//
// 为什么沉：
//   「层级预算 + 深度序 + 同序次级键」是所有俯视 2D 项目的通用问题：底图、装饰、建筑、指示器、角色、
//   血条、特效各占一段 `sortingOrder`，而角色这段还要按世界 y 细分。原项目把它们散在 `UnitView`
//   与它的嵌套静态类里，别的项目要照抄一遍（连同"忘了留预算"的坑）。
//
// ★ 依据（同序为什么必须有确定性次级键；出自 Unity 官方手册「2D 渲染顺序」）：
//   排序层 → 层内顺序 → 渲染队列 → **距离** → 排序组 → 材质。其中「距离」条目明写：
//   "正交：Unity 使用从相机平面到游戏对象中心的距离。**要控制渲染顺序，请增加或减少游戏对象
//    Transform 组件中的 z 位置。**"；同页又写明"若两个游戏对象上述值都相同，Unity 用内部渲染队列
//    顺序决定先后 —— **此顺序是不固定的，您无法控制**"。
//   `https://docs.unity3d.org.cn/Manual/sprite/sort-sprites/sort-sprites.html`
//   ⇒ 位置**完全重合**的单位（一次 6 只落在同一格）必然拿到同一个 `sortingOrder`，
//     谁盖谁就回到"枚举顺序"⇒**每帧可能不同** ⇒ 两张不同动画帧的贴图在同像素上互相翻盖
//     （原项目实测：`frame=5907 pos=(-5.5,2.5) n=3 order=[1216,1216,1216]`，三只同格）。
//     所以必须有**确定性的次级键** —— 用 z（正交相机下 z 不改投影位置，只决定先后），
//     且只由 id 决定（**逐帧稳定**；任何随时间变化的量都会让次序来回翻，等于没修）。
//
// ★ 为什么次级键用 z 而不是再细分 `sortingOrder`：
//   `sortingOrder` 是 int，再细分就会撞穿层级预算（角色层上界要 **< 血条层**）。
//   z 是**同一 `sortingOrder` 内**的次级排序键，不占 int 预算。
//
// 已知事故 / 坑：
//   · 层级预算**必须逐段检查**（本件 <see cref="ValidateBudget"/>）：角色层的最大 order 一旦 ≥ 血条层，
//     症状是"血条被自己单位的精灵盖住"（很显眼但很难猜是层级算错）；也可能是"站在建筑前面的兵被建筑盖住"。
//   · 深度序的**方向**不能反：`(半场高 − y)` 随 y 减小而增大（越靠屏幕下方 = order 越大 = 后画 = 在前）。
//     写反了不会报错，只会"后面的兵盖住前面的兵"。
//   · `DepthTiebreak` 只由 id 决定 ⇒ 同格堆叠中"新召唤的压在上面"（id 递增），与直觉一致。
//   · 次级键的步长 × 取模基数（默认 1e-4 × 256 = 0.0256）**必须小于 1 个 `sortingOrder` 级**
//     （1/16 格 = 0.0625 格），否则 z 会把本该在前的单位压到后面。
// ─────────────────────────────────────────────────────────────────────────────

using UnityEngine;

namespace CloverEngine
{
    /// <summary>
    /// 2D 层级预算 + 深度序 + 同序次级键。
    ///
    /// <para>
    /// 一层一实例（<see cref="FieldHeightTiles"/> / 层数 / 预算都是实例状态 ⇒ 同进程里横版与竖版、
    /// 或两个世界尺寸不同的场景可以各持一份）。
    /// </para>
    /// <para>
    /// 典型用法：<c>var layers = new SortingLayers(fieldHeightTiles: 32f);</c>
    /// → 角色写 <c>renderer.sortingOrder = layers.DepthOrder(worldY);</c>
    /// → 同格堆叠再写 <c>transform.localPosition.z = layers.TiebreakOffset(id);</c>
    /// → 调一次 <see cref="ValidateBudget"/> 确认没撞穿。
    /// </para>
    /// </summary>
    public sealed class SortingLayers
    {
        /// <summary>日志标签。</summary>
        public const string Tag = "SortingLayers";

        /// <summary>默认深度分辨率（级/格）。原项目取 16（1 级 ≈ 1/16 格，已细于任何两个实体的最小可见纵深差）。</summary>
        public const int DefaultDepthLevelsPerTile = 16;

        /// <summary>默认次级键取模基数（决定可区分的同格堆叠上限 = 该值，默认 256 只）。</summary>
        public const int DefaultTiebreakMod = 256;

        /// <summary>默认次级键步长（格）；× <see cref="DefaultTiebreakMod"/> 必须小于 1 个 order 级。</summary>
        public const float DefaultTiebreakStep = 1e-4f;

        // ── 层级预算表（⛔ 每项都可覆盖；默认值 = 通用分层，与原项目 SortingOrder 取值一致） ──

        /// <summary>地面 / 底图层。</summary>
        public int Ground { get; set; } = 0;

        /// <summary>装饰层（草丛 / 栅栏等纯装饰，不参与碰撞）。</summary>
        public int Decoration { get; set; } = 10;

        /// <summary>建筑 / 塔等"结构物"层。</summary>
        public int Structure { get; set; } = 50;

        /// <summary>落点指示 / 地面标记层（在结构之上、角色之下）。</summary>
        public int Indicator { get; set; } = 200;

        /// <summary>角色层**基准**（实际 order = <see cref="DepthOrder"/> = 基准 + 世界 y 深度）。</summary>
        public int Actor { get; set; } = 1000;

        /// <summary>头顶血条 / 名牌等浮层（必须高于角色层的**上界**）。</summary>
        public int Overlay { get; set; } = 2000;

        /// <summary>特效层（必须最高）。</summary>
        public int Effect { get; set; } = 3000;

        // ── 深度序参数 ──

        /// <summary>场地在**纵向**的格数（深度序要用它的半场值；由调用方按自己的世界尺寸给）。</summary>
        public float FieldHeightTiles { get; set; }

        /// <summary>深度分辨率（级/格）。</summary>
        public int DepthLevelsPerTile { get; set; }

        /// <summary>次级键取模基数。</summary>
        public int TiebreakMod { get; set; }

        /// <summary>次级键步长（格）。</summary>
        public float TiebreakStep { get; set; }

        /// <summary>
        /// 建一份层级配置。
        /// </summary>
        /// <param name="fieldHeightTiles">场地纵向格数（**必给**：世界尺寸属业务，⛔ 引擎不替它定死）。&lt;=0 ⇒ 按 1 处理 + 降频 Warn。</param>
        /// <param name="depthLevelsPerTile">深度分辨率（级/格）；&lt;=0 ⇒ <see cref="DefaultDepthLevelsPerTile"/>。</param>
        /// <param name="tiebreakMod">次级键取模基数；&lt;=0 ⇒ <see cref="DefaultTiebreakMod"/>。</param>
        /// <param name="tiebreakStep">次级键步长；&lt;=0 ⇒ <see cref="DefaultTiebreakStep"/>。</param>
        public SortingLayers(float fieldHeightTiles,
            int depthLevelsPerTile = DefaultDepthLevelsPerTile,
            int tiebreakMod = DefaultTiebreakMod,
            float tiebreakStep = DefaultTiebreakStep)
        {
            if (!(fieldHeightTiles > 0f) || float.IsInfinity(fieldHeightTiles))
            {
                // 非预期分支：场地高度缺失 / 写错 ⇒ 用 1 格兜底并留痕（否则深度序整片退化成同一个 order）
                LogThrottle.WarnThrottled(Tag, "fieldHeight.invalid",
                    $"场地纵向格数非法（{fieldHeightTiles}）⇒ 按 1 格处理；检查调用方的场地尺寸常量");
                fieldHeightTiles = 1f;
            }

            FieldHeightTiles = fieldHeightTiles;
            DepthLevelsPerTile = depthLevelsPerTile > 0 ? depthLevelsPerTile : DefaultDepthLevelsPerTile;
            TiebreakMod = tiebreakMod > 0 ? tiebreakMod : DefaultTiebreakMod;
            TiebreakStep = tiebreakStep > 0f ? tiebreakStep : DefaultTiebreakStep;
        }

        /// <summary>
        /// 角色层在给定世界 y 处的 `sortingOrder`。
        /// <para>
        /// <c>= Actor + round((FieldHeightTiles/2 − worldY) × DepthLevelsPerTile)</c>
        /// —— y 越小（越靠屏幕下方）order 越大（后画 ⇒ 挡在前面）。
        /// </para>
        /// </summary>
        /// <param name="worldY">世界坐标 y（格）。</param>
        public int DepthOrder(float worldY) =>
            Actor + Mathf.RoundToInt((FieldHeightTiles * 0.5f - worldY) * DepthLevelsPerTile);

        /// <summary>角色层可能取到的最小 order（= 场地最上端）。由 <see cref="DepthOrder"/> 在同端点上求，⛔ 不另写一份公式。</summary>
        public int ActorOrderMin => Mathf.Min(DepthOrder(FieldHeightTiles * 0.5f), DepthOrder(-FieldHeightTiles * 0.5f));

        /// <summary>角色层可能取到的最大 order（= 场地最下端）。</summary>
        public int ActorOrderMax => Mathf.Max(DepthOrder(FieldHeightTiles * 0.5f), DepthOrder(-FieldHeightTiles * 0.5f));

        /// <summary>
        /// 同 `sortingOrder` 下的**确定性次级键**：只由实体 id 决定的微小 z 偏移（格）。
        /// <para>
        /// 依据与取值口径见文件头（Unity 官方：同 `sortingOrder` 的渲染次序不固定）。
        /// ⛔ 只允许用**逐帧不变**的量（id）；用帧号 / lerp 进度会让次序来回翻，等于没修。
        /// </para>
        /// </summary>
        /// <param name="id">实体 id（一次对局内不变）。</param>
        public float TiebreakOffset(int id)
        {
            var k = id % TiebreakMod;
            if (k < 0) k += TiebreakMod;
            return k * TiebreakStep;
        }

        /// <summary>
        /// 预算是否自洽：<c>ActorOrderMin &gt; Structure</c>（角色不压在建筑下）
        /// 且 <c>ActorOrderMax &lt; Overlay</c>（血条不被角色盖）且 <c>Overlay &lt; Effect</c>。
        /// </summary>
        public bool BudgetValid =>
            ActorOrderMin > Structure && ActorOrderMax < Overlay && Overlay < Effect;

        /// <summary>
        /// 检查层级预算，不自洽时降频 Warn 并返回 <c>false</c>（建好配置后调一次即可）。
        /// </summary>
        public bool ValidateBudget()
        {
            if (BudgetValid) return true;

            LogThrottle.WarnThrottled(Tag, "budget.overflow",
                $"层级预算不自洽：角色层 order ∈ [{ActorOrderMin}, {ActorOrderMax}]，" +
                $"结构层={Structure} / 浮层={Overlay} / 特效层={Effect}；" +
                "要求 角色min > 结构 且 角色max < 浮层 < 特效层" +
                "（不满足的症状：血条被自己单位的精灵盖住 / 兵被建筑盖住）");
            return false;
        }
    }
}
