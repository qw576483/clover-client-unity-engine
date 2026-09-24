// ─────────────────────────────────────────────────────────────────────────────
// CloverEngine · Runtime/Presentation/TileRenderState.cs
// 一格瓦片的**渲染状态**（不可变值类型）。通用底座。
//
// 为什么必须有这个类型（★ 影响范围：任何"节点池 + 逐格渲染"的 2D 地图）：
//   ⛔ 对象池复用节点时，「复用出来的节点」与「新建的节点」必须给出**逐项相同**的画面 ——
//   只要渲染字段是"记得就重设、漏了就继承上一次"的散装写法，池化路径就会开始**静默继承
//   上一格的残留状态**（看不见的错色 / 错贴图 / 错排序，且不报错）。
//   把一格的渲染状态收成**一个值**、再由渲染方**无条件写全**，这件事就从"人记得"变成
//   **结构性保证**（另见 `TileNodePool` 的类注释）。
//
// 字段与 `SpriteRenderer` 的对应（断言口径 = 这 5 项逐项相等 / 共 11 个标量）：
//   Sprite        → SpriteRenderer.sprite
//   Color         → SpriteRenderer.color          （r/g/b/a 4 个标量）
//   LocalScale    → transform.localScale          （x/y/z 3 个标量）
//   Position      → transform.position（世界坐标） （x/y/z 3 个标量）
//   SortingOrder  → SpriteRenderer.sortingOrder
//   ⚠️ 第 6 项 `SpriteRenderer.enabled` **不在本值里**：它是"被逐格业务改写的开关"
//      （如迷雾格会被置 false），由渲染方在写全 5 项时**一并复位为 true**。
//
// <para><b>不写全字段的后果</b>：整图重铺（Town 56×40 ≈ 2000+ 节点）时若"复用节点不写全字段"，
//   第二张图会带着第一张图的贴图/颜色/排序号出现（实测形态见本仓 `TileNodePool.cs` 头注释）。</para>
// <para><b>自证</b>：`SameAs` 是本值的**纯**比较（不依赖 `Vector3.Equals` 的 epsilon 语义 —— 那会让
//   "差一点点"判成相等）。离线用例表覆盖边界：状态全同 / 只差一个字段 / position.z 只差 1e-6。</para>
// <para><b>已知边界 / 精度限制</b>：① 值语义 ⇒ 默认构造（`default(TileRenderState)`）会得到一个
//   "全 0 状态"（sprite=null / color 全 0 / scale 0 / pos 0 / order 0），它**不是**合法的一格画面
//   —— 调用方必须走 5 参构造，⛔ 不许用 `default` 当"空状态"占位；② `SameAs` 是**逐位比较**
//   （`==`），对 NaN 恒 false（NaN != NaN）⇒ 喂 NaN 的坐标会被判"每次都不同"，那是上游算错；
//   ③ 本类型**只放渲染状态** —— ⛔ 业务字段（tile kind / 块号 / 是否已探索…）不许塞进来，
//   一旦塞进来，池化路径就会开始"继承上一次的残留业务状态"。</para>
// <para><b>用法</b>：调用方构造一次本值（一格的纯函数），把它交给渲染方；
//   渲染方**无条件写全** 5 个字段（+ `enabled`）。</para>
// ─────────────────────────────────────────────────────────────────────────────

using UnityEngine;

namespace CloverEngine
{
    /// <summary>
    /// 一格瓦片的渲染状态（见文件头；5 个字段 / 11 个标量）。
    /// <para>不可变（全 <c>readonly</c>）：构造后没有任何写入口 ⇒ 传引用也不会被下游改脏。</para>
    /// </summary>
    public readonly struct TileRenderState
    {
        /// <summary>贴图（null = 占位菱形 / 无贴图态）。</summary>
        public readonly Sprite Sprite;

        /// <summary>节点颜色（有贴图 = 白；占位 = 业务自己的占位色）。</summary>
        public readonly Color Color;

        /// <summary>节点局部缩放。</summary>
        public readonly Vector3 LocalScale;

        /// <summary>节点世界坐标（含业务自己的对齐修正）。</summary>
        public readonly Vector3 Position;

        /// <summary>深度排序值（同 `sortingOrder` 时按兄弟序平局，故写入顺序也有意义）。</summary>
        public readonly int SortingOrder;

        /// <summary>构造（唯一入口；5 个字段必须全部显式给 —— ⛔ 不要用 `default` 当空状态）。</summary>
        public TileRenderState(Sprite sprite, Color color, Vector3 localScale, Vector3 position, int sortingOrder)
        {
            Sprite = sprite;
            Color = color;
            LocalScale = localScale;
            Position = position;
            SortingOrder = sortingOrder;
        }

        /// <summary>
        /// 5 个渲染字段**逐项相等**（`Sprite` 按引用比较；不使用 `Vector3.Equals` / `Color.Equals`
        /// 的 epsilon 语义 —— 池化自证的判据是"逐位相同"，不是"差不多"）。
        /// </summary>
        public bool SameAs(TileRenderState other)
        {
            return ReferenceEquals(Sprite, other.Sprite)
                   && Color.r == other.Color.r && Color.g == other.Color.g
                   && Color.b == other.Color.b && Color.a == other.Color.a
                   && LocalScale.x == other.LocalScale.x && LocalScale.y == other.LocalScale.y
                   && LocalScale.z == other.LocalScale.z
                   && Position.x == other.Position.x && Position.y == other.Position.y
                   && Position.z == other.Position.z
                   && SortingOrder == other.SortingOrder;
        }

        /// <summary>单行描述（日志 / 断言输出用）。</summary>
        public override string ToString()
        {
            return $"sprite={(Sprite != null ? Sprite.name : "(none)")} color=({Color.r:0.###},{Color.g:0.###}," +
                   $"{Color.b:0.###},{Color.a:0.###}) scale=({LocalScale.x:0.####},{LocalScale.y:0.####}) " +
                   $"pos=({Position.x:0.####},{Position.y:0.####},{Position.z:0.####}) order={SortingOrder}";
        }
    }
}
