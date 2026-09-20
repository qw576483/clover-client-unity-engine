// ─────────────────────────────────────────────────────────────────────────────
// CloverEngine · Runtime/Core/Dir8.cs
// 8 方向朝向枚举 —— 网格 / 等距类玩法的通用底座，下沉到引擎。
//
// 出处：Diablo2 项目 `client/Assets/Scripts/Def/Enums.cs` 的 `Diablo2.Def.Dir8`
//   （**顺序与取值逐项照搬**：顺时针，0 = 南）。
//
// ⛔ 为什么引擎自带一个、而不复用业务枚举：业务枚举的注释绑定了该项目的素材帧序
//   （`.dcc` 的 8 个方向帧），引擎不能反向依赖业务。项目侧 `Core/Iso.cs` 门面负责
//   两枚举之间的**逐值映射**（见该文件注释）。
//
// ⛔ **顺序即契约**：`IsoLayout.DirectionTo` / `IsoLayout.DirectionDelta` 的语义按本顺序
//   定义（`S/SW/W/NW/N/E/SE` 顺时针，0 = 南）。任何要与本枚举互通的项目枚举**必须逐项同值**，
//   否则朝向会整体错位（而错位不会报错，只会"人物朝向看着别扭"）。
// ─────────────────────────────────────────────────────────────────────────────

namespace CloverEngine
{
    /// <summary>
    /// 8 方向朝向（**顺时针编号，0 = 南**）。
    /// <para>方向 ↔ 格增量的映射表见 <see cref="IsoLayout.DirectionTo(Vector2Int)"/> 与
    /// <see cref="IsoLayout.DirectionDelta"/>。</para>
    /// </summary>
    public enum Dir8
    {
        S = 0,    // 南（格增量 +gy）
        SW = 1,   // 西南
        W = 2,    // 西
        NW = 3,   // 西北
        N = 4,    // 北（格增量 -gy）
        NE = 5,   // 东北
        E = 6,    // 东
        SE = 7,   // 东南
    }
}
