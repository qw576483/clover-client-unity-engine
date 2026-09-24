// ─────────────────────────────────────────────────────────────────────────────
// CloverEngine · Runtime/Presentation/TileRenderer.cs
// 等距瓦片**渲染内核**：把「一格一层该怎么摆 / 用哪张图 / 什么色 / 什么排序」收成
// **纯函数（StateOf）+ 无条件写全（Apply）** 两步；题材语义（取哪张图、占位什么色）
// 全部由调用方以参数 + 委托注入。
//
// 为什么必须有它（★ 影响范围：任何"等距格 + 节点池 + 逐格重铺"的 2D 地图）：
//   逐格渲染有两类问题 ——
//     ① 「这一格该画什么」：吃题材数据（瓦片分类、区域、原版逐格覆盖…）⇒ 只能留在项目侧；
//     ② 「这一格画出来长什么样」：只吃「格坐标 + 一张图 + 一个层」⇒ 与题材**无关**。
//   ⛔ 第 ② 类一旦被复制进每个项目，就会复制出「池化复用时不写全字段」这类**静默**缺陷
//   （错色 / 错贴图 / 错排序，不报错，见 `TileRenderState.cs` 头注释）。本件把第 ② 类收成
//   唯一真相：**投影后的对齐/缩放/排序数学 + 无条件写全**都在这里；第 ① 类留在调用方。
//
// ⛔ 与同目录三件**不重叠**（分工，写新代码前先看这段）：
//   · `IsoLayout`（Runtime/Core/IsoLayout.cs）—— 纯投影数学：格 ↔ 世界、屏幕取格、8 向、
//     排序基准 `SortOrder(gx, hy, layerOffset)`。本件**持有一个它**、只做"在格中心上做像素对齐"
//     —— ⛔ 本件不重写逆投影 / 不做屏幕取格 / 不碰方向。
//   · `ITileWorld` / `TileWorld`（同目录）—— **空间事实面**：这一格实不实心 / 托台多高 / 世界到哪。
//     本件**不查可走性、不做碰撞、不读边界** —— 那是"事实"，本件只回答"画成什么样"。
//   · `TileNodePool` + `TileRenderState`（同目录）—— 节点**借还**与一格**渲染状态值**。
//     本件**不持有池**（节点来源由调用方以 `Func<Transform, SpriteRenderer>` 注入，
//     `TileNodePool.Take` 的签名即此、可直接传方法组），只负责**产出**状态值并把状态值
//     **无条件写全**到拿到的节点上（`Build` = 取节点 + `Apply`）。
//   一句话：`TileWorld` 说"能不能站"，`IsoLayout` 说"格在哪"，`TileNodePool` 说"节点从哪来"，
//   `TileRenderState` 说"一格写成什么样"，**本件是唯一把最后两件事接起来的那一层**。
//
// 出处（算法与分支逐条照搬，⛔ 未改语义；原实现的项目语义部分**没有**下沉）：
//   clover-project-diablo2 · client/Assets/Scripts/Module/Map/MapView.cs
//     · 1801-1840  `PlanCell` —— "一格要画什么"的规划（本件只下沉**计划的骨架 + 节点数**）
//     · 1848-1873  `ApplyCellPlan` —— 计划落地（本件 = `ApplyPlan`）
//     · 2401-2411  `ApplyTileState` —— 状态 → 渲染器，**无条件写全**（本件 = `Apply`）
//     · 2418-2463  `GroundState` / `ObjectState` / `FogState` / `LocalScaleFor` / `ColorFor`
//                  （本件 = 一个 `StateOf` + `LocalScaleFor` / `ColorFor`）
//     · 2483-2502  `PlaceOfPx` —— 像素对齐内核（地砖顶边贴格中心上方半格 / 墙底边贴下方半格）
//   ⛔ 未下沉（属题材，留在调用方）：瓦片分类 / 区域 / 素材键 / 逐格覆盖 / 水面不叠 / 实心岩体不画 /
//     各层排序偏移 / 占位配色 / 每单位像素数 —— 全部经参数与委托进来，本件不预设任何一组取值。
//
// <para><b>最小复现（为什么必须有"无条件写全"这一步）</b>：节点池复用节点时，若渲染方写成
//   "记得就重设、漏了就继承上一次"，则第二张图会带着第一张图的贴图 / 颜色 / 排序号出现；
//   再叠加 `enabled` 不复位（遮蔽层节点被业务置 `false`）⇒ 复用到它的一格**静默不可见**。
//   两处都不报错、不打日志。</para>
// <para><b>自证</b>：① `StateOf` 是纯函数（不碰 `SpriteRenderer`、不读全局、不建节点）⇒ 调用方
//   可离线逐格复算并与改前对拍（本项目走 `mapcheck` 的重铺等价断言）；② `Apply` 内**没有**
//   "是不是复用节点"的分支（一个字都没有）⇒ 「逐项相等」是结构性保证而不是"人记得"；
//   ③ `Build` 与 `ApplyPlan` 返回的新建节点数**恒等于** `TileCellPlan.NodeCount`（帧预算按它扣）。</para>
// <para><b>已知边界 / 精度限制</b>：① 本件**不是** MonoBehaviour、无 `Update`、不注册回调 ⇒
//   由调用方驱动（逐格渲染本来就是调用方的循环）；② `StateOf` 的 sprite 为 `null` 时位置**恒等于**
//   格中心（占位菱形与格同心，不做对齐修正）—— 这是原口径；③ 像素对齐用的是"图的**高**"，
//   ⛔ 与图的**宽**无关（等距格图必须按格宽裁切，宽度的对齐由素材侧负责）；
//   ④ `_tilePixelsPerUnit` 是"一张格图在世界单位下的像素高"的解释比例，改它等于改所有已有素材的观感
//   ⇒ 只在换素材规格时改；⑤ 本件**不做**视锥裁剪 / 分块调度 / 贴图异步请求 —— 那些是调用方的策略。</para>
// <para><b>用法 + 首个消费方</b>：
//   <code>
//   var r = new TileRenderer(iso, pixelsPerUnit, tilePixelsPerUnit);
//   var st = r.StateOf(cell, TileLayer.Ground, sprite, placeholderColor, sortOffset, sortBias);
//   var node = r.Build(pool.Take, groundParent, st);      // 取 + 无条件写全
//   </code>
//   首个消费方 = `clover-project-diablo2` 的 `Module/Map/MapView.cs`（`GroundState` / `ObjectState` /
//   `FogState` / `ApplyTileState` / `ApplyCellPlan` 五处将改为薄转发）。业务还要接的线 = 该项目的
//   `mapcheck` 宿主（纯函数对拍 + 节点数断言）。</para>
// ─────────────────────────────────────────────────────────────────────────────

using System;
using UnityEngine;

namespace CloverEngine
{
    /// <summary>
    /// 等距瓦片的**渲染层**（通用三层）。
    /// <para>⛔ 这里**没有**任何题材语义：层只决定"要不要做地砖式对齐"（<see cref="TileLayer.Ground"/>
    /// 按地砖对齐、其余按墙/物件对齐）与调试可读性；层 → 排序偏移 / 父节点 / 是否启用，全部由调用方给。</para>
    /// </summary>
    public enum TileLayer
    {
        /// <summary>地面层（地砖：图像**顶边**贴在格中心上方半格）。</summary>
        Ground = 0,

        /// <summary>物件层（墙 / 树 / 栅栏等：图像**底边**贴在格中心下方半格，更高的部分向上长）。</summary>
        Object = 1,

        /// <summary>遮蔽层（迷雾 / 遮罩：通常无贴图 + 纯色）。</summary>
        Overlay = 2
    }

    /// <summary>
    /// **一格的渲染计划**：哪几层要画、每层取图用什么键。
    /// <para>计划本身**不含决定**（"该不该画"吃题材数据 ⇒ 由调用方算出来）；本值只提供
    /// **计划的骨架 + 节点数** —— 节点数就是帧预算的扣减量（<see cref="NodeCount"/>）。</para>
    /// <para>⛔ 值语义：`default(TileCellPlan)` = <see cref="None"/>（什么都不画）。</para>
    /// </summary>
    public readonly struct TileCellPlan
    {
        /// <summary>什么都不画（本格连遮蔽层都不画 —— 例如"世界里没有这一格"）。</summary>
        public static readonly TileCellPlan None = new TileCellPlan(false, null, null, false);

        /// <summary>
        /// <c>false</c> ⇒ 本格**完全不进入渲染**：不建任何节点、也不画遮蔽层。
        /// ⛔ 与「画了但没键」是两回事（后者仍要画遮蔽层）。
        /// </summary>
        public readonly bool Draw;

        /// <summary>地面层的取图键（<c>null</c> / 空 = 地面层不画）。</summary>
        public readonly string GroundKey;

        /// <summary>物件层的取图键（<c>null</c> = 没有可用贴图，此时按 <see cref="DrawObject"/> 画纯色占位）。</summary>
        public readonly string ObjectKey;

        /// <summary>物件层是否真的画（<c>true</c> + <see cref="ObjectKey"/> 为 <c>null</c> = 画纯色占位）。</summary>
        public readonly bool DrawObject;

        /// <summary>
        /// ★ 物件层被**刻意压掉**（渲染上等同 <see cref="DrawObject"/> = <c>false</c>）。
        /// <para>语义是「这一格本来有物件键，但叠上去与地面层重复（同一张图），所以不画」——
        /// 调用方用它留痕 / 计数（<see cref="ApplyPlan"/> 不读它，纯统计位）。</para>
        /// ⛔ 不要用它表达"画不出来 / 加载失败"：那是"有键但取不到图 ⇒ 画占位"，<see cref="DrawObject"/> 为 <c>true</c>。</summary>
        public readonly bool ObjectSuppressed;

        /// <summary>建一格计划（唯一的题材无关部分：三层开关 + 两个取图键 + 一个统计位）。</summary>
        public TileCellPlan(bool draw, string groundKey, string objectKey, bool drawObject, bool objectSuppressed = false)
        {
            Draw = draw;
            GroundKey = groundKey;
            ObjectKey = objectKey;
            DrawObject = drawObject;
            ObjectSuppressed = objectSuppressed;
        }

        /// <summary>地面层是否画（键非空即画）。</summary>
        public bool HasGround { get { return !string.IsNullOrEmpty(GroundKey); } }

        /// <summary>本格要建的节点数（0 = 什么都不画；1 = 只画一层；2 = 两层都画）—— 帧预算按它扣。</summary>
        public int NodeCount { get { return (HasGround ? 1 : 0) + (DrawObject ? 1 : 0); } }
    }

    /// <summary>
    /// **一层的渲染参数**（题材侧算好传进来）：排序偏移 / 附加偏置 / 占位色。
    /// <para>对齐方式**不在**这里 —— 它由 <see cref="TileLayer"/> 决定（地面层地砖式对齐，其余按墙/物件对齐）。</para>
    /// </summary>
    public readonly struct TileLayerParams
    {
        /// <summary>该层的基础排序偏移（传给 <see cref="IsoLayout.SortOrder(int,int,int)"/> 的 `layerOffset`）。</summary>
        public readonly int SortOffset;

        /// <summary>该层的附加偏置（例如"地面层整体下移一个排序步长"）；无偏置给 0。</summary>
        public readonly int SortBias;

        /// <summary>该层**没有贴图**时的占位色（有贴图时恒被写成白色 —— 原版像素不能被染色）。</summary>
        public readonly Color PlaceholderColor;

        /// <summary>建一层参数。</summary>
        public TileLayerParams(int sortOffset, int sortBias, Color placeholderColor)
        {
            SortOffset = sortOffset;
            SortBias = sortBias;
            PlaceholderColor = placeholderColor;
        }
    }

    /// <summary>
    /// 等距瓦片渲染内核（纯逻辑类，**不是** MonoBehaviour）：见文件头「分工」一节。
    /// <para>线程/生命周期：主线程使用；不持有池、不建节点（除 <see cref="Build"/> 通过注入的委托取节点）、
    /// 不订阅任何回调 —— 调用方给什么就算什么。</para>
    /// </summary>
    public sealed class TileRenderer
    {
        private const string Tag = "TileRenderer";

        /// <summary>无贴图节点的名字（有贴图画 <see cref="NodeName"/>）。仅用于场景里看得懂，无渲染影响。</summary>
        private const string NodeName = "Tile";

        /// <summary>占位节点名（无贴图）。</summary>
        private const string PlaceholderNodeName = "Tile_placeholder";

        /// <summary>本内核使用的等距投影（格 ↔ 世界 / 排序基准）；**只读**，不做逆投影。</summary>
        public IsoLayout Iso { get; }

        /// <summary>契约「每世界单位多少像素」（`Sprite` 导入设置的 PPU）。</summary>
        public float PixelsPerUnit { get; }

        /// <summary>一张等距格图按多少像素高解释（素材规格；决定节点的缩放与像素对齐）。</summary>
        public float TilePixelsPerUnit { get; }

        /// <summary>
        /// 建一个渲染内核。
        /// </summary>
        /// <param name="iso">等距投影（**必须非 null**；<c>null</c> ⇒ Error 留痕后抛 <see cref="ArgumentNullException"/>
        /// —— 没有投影就没有"格中心"，后续每个坐标都是错的，早抛出胜过静默画歪）。</param>
        /// <param name="pixelsPerUnit">契约 PPU（`GameConst.PixelsPerUnit` 那类）。</param>
        /// <param name="tilePixelsPerUnit">格图像素高（素材规格，决定缩放 = 契约PPU / 本值）。
        /// 非有限或 <c>&lt;= 0</c> ⇒ Error 留痕并按 <c>1</c> 处理（= 一格一世界单位、不做缩放）。</param>
        public TileRenderer(IsoLayout iso, float pixelsPerUnit, float tilePixelsPerUnit)
        {
            if (iso == null)
            {
                // 非预期分支：没有投影 ⇒ 后续每个格中心都是错的（且不报错）⇒ 当场打断
                LogThrottle.ErrorOnce(Tag, "ctor.null-iso",
                    "TileRenderer: IsoLayout 为 null（没有投影就没有格中心）⇒ 抛 ArgumentNullException");
                throw new ArgumentNullException(nameof(iso));
            }

            if (!IsUsableMetric(pixelsPerUnit) || !IsUsableMetric(tilePixelsPerUnit))
            {
                // 非预期分支：度量非法（0 / 负数 / NaN / Inf）⇒ 会让缩放与像素对齐整体失效且不报错
                LogThrottle.ErrorOnce(Tag, "ctor.bad-metric",
                    $"TileRenderer: 像素度量非法（pixelsPerUnit={pixelsPerUnit}, tilePixelsPerUnit={tilePixelsPerUnit}）" +
                    "⇒ 两者都按 1 处理（= 一格一世界单位、不做缩放；检查调用方是否读到了空配置）");
                pixelsPerUnit = 1f;
                tilePixelsPerUnit = 1f;
            }

            Iso = iso;
            PixelsPerUnit = pixelsPerUnit;
            TilePixelsPerUnit = tilePixelsPerUnit;
        }

        // ── 三个像素 / 颜色纯内核 ────────────────────────────────────────────

        /// <summary>
        /// 节点缩放：有贴图 ⇒ <c>契约PPU / 格图像素高</c>（把"素材按别的 PPU 解释"的差额补回来）；
        /// 占位 ⇒ <c>1</c>。
        /// </summary>
        public Vector3 LocalScaleFor(bool hasSprite)
        {
            return hasSprite
                ? Vector3.one * (PixelsPerUnit / TilePixelsPerUnit)
                : Vector3.one;
        }

        /// <summary>节点颜色：有贴图 ⇒ 白（像素不能被染色）；占位 ⇒ 调用方给的占位色。</summary>
        public static Color ColorFor(bool hasSprite, Color placeholderColor)
        {
            return hasSprite ? Color.white : placeholderColor;
        }

        /// <summary>取一张图在**像素**下的高（<c>null</c> ⇒ 0 = 占位菱形，不做对齐修正）。</summary>
        public static int HeightPxOf(Sprite sprite)
        {
            return sprite != null ? Mathf.RoundToInt(sprite.rect.height) : 0;
        }

        /// <summary>
        /// 像素对齐内核（★ 唯一真相，<see cref="StateOf"/> 与业务离线复算都走它）：
        /// <list type="bullet">
        ///   <item><b>地砖</b>（<paramref name="isFloor"/> = true）：图像**顶边**贴在格中心上方半格
        ///     ⇒ 图像的"顶部一格高菱形"正好铺满本格；</item>
        ///   <item><b>墙 / 物件</b>：图像**底边**贴在格中心下方半格（= 本格菱形的前角）⇒ 更高的部分向上长；</item>
        ///   <item><paramref name="spriteHeightPx"/> <c>&lt;= 0</c>：原样返回格中心（占位菱形本来就与格同心）。</item>
        /// </list>
        /// <para>⛔ 只吃"图像高（px）"，**不碰 `Sprite`** ⇒ 离线宿主能逐格复算（不需要真的建出贴图）。</para>
        /// </summary>
        /// <param name="cellCenter">格中心的世界坐标（<see cref="IsoLayout.GridToWorld"/>）。</param>
        /// <param name="spriteHeightPx">图像高（px）；0 = 占位菱形。</param>
        /// <param name="isFloor">true = 地砖（顶边贴格中心上方半格）；false = 墙 / 物件（底边贴下方半格）。</param>
        public Vector3 PlaceOfPx(Vector3 cellCenter, int spriteHeightPx, bool isFloor)
        {
            if (spriteHeightPx <= 0) return cellCenter;      // 占位菱形本来就与格同心

            if (spriteHeightPx < 0)
            {
                // 非预期分支：负的图高（`sprite.rect` 被写坏？）⇒ 走占位分支更安全，但必须留痕
                LogThrottle.WarnThrottled(Tag, "place.negative-height",
                    $"PlaceOfPx 收到负的图像高（{spriteHeightPx} px）⇒ 按 0 处理（与格同心）；检查素材 rect");
                return cellCenter;
            }

            var h = spriteHeightPx / TilePixelsPerUnit;      // 图像在世界单位下的高
            var dy = isFloor
                ? Iso.HalfH - h * 0.5f                       // 顶边在 +HalfH ⇒ 中心下移
                : h * 0.5f - Iso.HalfH;                      // 底边在 -HalfH ⇒ 中心上移
            return new Vector3(cellCenter.x, cellCenter.y + dy, cellCenter.z);
        }

        /// <summary>该格 + 层偏移的排序值（薄转发 <see cref="IsoLayout.SortOrder(int,int,int)"/>，避免调用方自己拼）。</summary>
        public int SortOrder(Vector2Int cell, int layerOffset)
        {
            return Iso.SortOrder(cell.x, cell.y, layerOffset);
        }

        // ── 一格一层：状态值（纯函数） ───────────────────────────────────────

        /// <summary>
        /// ★ 一格的**渲染状态**（纯函数：不建节点、不碰 <see cref="SpriteRenderer"/>、不读全局）。
        /// <para>三层的差别只剩"对齐方式"（由 <paramref name="layer"/> 决定）与调用方给的
        /// 占位色 / 排序参数 ⇒ `GroundState` / `ObjectState` / `FogState` 三种调用合成本方法一种。</para>
        /// </summary>
        /// <param name="cell">格坐标。</param>
        /// <param name="layer">渲染层（决定对齐：<see cref="TileLayer.Ground"/> = 地砖式）。</param>
        /// <param name="sprite">贴图；<c>null</c> = 用 <paramref name="placeholderColor"/> 的占位菱形。</param>
        /// <param name="placeholderColor">**无贴图**时的占位色（有贴图时被 <see cref="ColorFor"/> 变成白）。</param>
        /// <param name="sortOffset">该层基础排序偏移（`layerOffset`）。</param>
        /// <param name="sortBias">该层附加偏置（无偏置给 0，例如地面层整体下移一个排序步长）。</param>
        public TileRenderState StateOf(Vector2Int cell, TileLayer layer, Sprite sprite, Color placeholderColor,
                                       int sortOffset, int sortBias = 0)
        {
            var hasSprite = sprite != null;
            var center = Iso.GridToWorld(cell.x, cell.y);
            return new TileRenderState(
                sprite,
                ColorFor(hasSprite, placeholderColor),
                LocalScaleFor(hasSprite),
                PlaceOfPx(center, HeightPxOf(sprite), layer == TileLayer.Ground),
                Iso.SortOrder(cell.x, cell.y, sortOffset) + sortBias);
        }

        // ── 落地：无条件写全 ────────────────────────────────────────────────

        /// <summary>
        /// ★ 把一格瓦片的**全部**渲染字段**无条件**写到节点上（池化前后"逐项相等"就靠这里）：
        /// <list type="number">
        ///   <item>`transform.SetParent(parent, false)` —— **追加到末尾** ⇒ 块内格序与全新建时一致；</item>
        ///   <item>`sprite`；③ `color`；④ `transform.localScale`；⑤ `transform.position`（世界坐标，含 z）；</item>
        ///   <item>`sortingOrder`；</item>
        ///   <item>`enabled = true` —— ⛔ **池化必须复位它**：遮蔽层节点会被业务置 `false`，
        ///     若不复位，复用到它的一格会**静默不可见**。</item>
        /// </list>
        /// <para>⛔ 本方法里**不许**出现"是不是复用节点"的分支（一个字都不许）—— 一旦有分支，
        /// 「逐项相等」就不再是结构性保证。</para>
        /// <para><paramref name="sr"/> 为 <c>null</c> ⇒ 什么都不做（空节点没什么可写的，⛔ 不抛：
        /// 这条路径在逐格热循环里，抛出去会打断整图重铺）。</para>
        /// </summary>
        public static void Apply(SpriteRenderer sr, Transform parent, TileRenderState st)
        {
            if (sr == null) return;

            sr.name = st.Sprite != null ? NodeName : PlaceholderNodeName;
            sr.transform.SetParent(parent, false);
            sr.sprite = st.Sprite;
            sr.color = st.Color;
            sr.transform.localScale = st.LocalScale;
            sr.transform.position = st.Position;
            sr.sortingOrder = st.SortingOrder;
            sr.enabled = true;
        }

        /// <summary>
        /// 取一个节点并把一格状态**写全**（= <c>takeNode(parent)</c> + <see cref="Apply"/>），返回该节点。
        /// <para><paramref name="takeNode"/> 是**注入的节点来源**（引擎是 <c>TileNodePool.Take</c>，
        /// 直接传方法组即可；业务 / 测试宿主可传自己的实现）。为 <c>null</c> ⇒ 留痕一次并返回 <c>null</c>。</para>
        /// </summary>
        public SpriteRenderer Build(Func<Transform, SpriteRenderer> takeNode, Transform parent, TileRenderState st)
        {
            if (takeNode == null)
            {
                // 非预期分支：没有节点来源 ⇒ 什么都建不出来，但**不能**在热循环里抛
                LogThrottle.ErrorOnce(Tag, "build.null-take",
                    "TileRenderer.Build: takeNode 为 null（没有节点来源）⇒ 本次不建节点；检查调用方是否忘了传池");
                return null;
            }

            var sr = takeNode(parent);
            Apply(sr, parent, st);
            return sr;
        }

        /// <summary>
        /// ★ 把一格的**计划**落地（= 逐层 <see cref="Build"/>），返回本格**新建的节点数**
        /// （恒等于 <see cref="TileCellPlan.NodeCount"/>；<c>Draw == false</c> ⇒ 0）。
        /// <para>⛔ 遮蔽层（迷雾 / 遮罩）**不在这里**：它由"这一格是否已探索"驱动，不属于渲染计划
        /// ⇒ 调用方另调 <see cref="StateOf"/> + <see cref="Build"/>（口径见文件头的「出处」一节）。</para>
        /// <para>顺序固定为「地面 → 物件」（兄弟序 = 格序 ⇒ 同 `sortingOrder` 时的平局结果确定）。</para>
        /// </summary>
        /// <param name="plan">本格计划。</param>
        /// <param name="cell">格坐标。</param>
        /// <param name="spriteOf">取图委托（键 → 贴图；取不到返回 <c>null</c> = 用占位）。</param>
        /// <param name="takeNode">节点来源委托（见 <see cref="Build"/>）。</param>
        /// <param name="groundParent">地面层父节点。</param>
        /// <param name="objectParent">物件层父节点。</param>
        /// <param name="groundLayer">地面层参数。</param>
        /// <param name="objectLayer">物件层参数。</param>
        public int ApplyPlan(TileCellPlan plan, Vector2Int cell,
                             Func<string, Sprite> spriteOf,
                             Func<Transform, SpriteRenderer> takeNode,
                             Transform groundParent, Transform objectParent,
                             TileLayerParams groundLayer, TileLayerParams objectLayer)
        {
            if (!plan.Draw) return 0;

            if (spriteOf == null)
            {
                // 非预期分支：没有取图委托 ⇒ 整图会变成占位色块（看着像"素材全丢了"）⇒ 必须留痕
                LogThrottle.ErrorOnce(Tag, "plan.null-lookup",
                    "TileRenderer.ApplyPlan: spriteOf 为 null（没有取图入口）⇒ 本格全部按占位色画；检查调用方是否忘了传");
            }

            var n = 0;

            if (plan.HasGround)
            {
                var groundSprite = spriteOf != null ? spriteOf(plan.GroundKey) : null;
                Build(takeNode, groundParent,
                    StateOf(cell, TileLayer.Ground, groundSprite, groundLayer.PlaceholderColor,
                            groundLayer.SortOffset, groundLayer.SortBias));
                n++;
            }

            if (plan.DrawObject)
            {
                var objectSprite = plan.ObjectKey != null && spriteOf != null ? spriteOf(plan.ObjectKey) : null;
                Build(takeNode, objectParent,
                    StateOf(cell, TileLayer.Object, objectSprite, objectLayer.PlaceholderColor,
                            objectLayer.SortOffset, objectLayer.SortBias));
                n++;
            }

            return n;
        }

        /// <summary>度量是否可用（有限且为正）。</summary>
        private static bool IsUsableMetric(float v)
        {
            return !float.IsNaN(v) && !float.IsInfinity(v) && v > 0f;
        }
    }
}
