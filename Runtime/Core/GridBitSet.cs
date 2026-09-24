// ─────────────────────────────────────────────────────────────────────────────
// CloverEngine · Runtime/Core/GridBitSet.cs
// 「格集合 ↔ base64 位图」的编解码：**逐格幂等 + 越界容错 + 坏串不抛**（能力下沉）。
//
// 为什么要有它（下沉记录）：
//   全工程只有业务侧 `clover-project-diablo2/client/Assets/Scripts/Def/ExploredCodec.cs`
//   在做「把一份体积大的格集合（小地图已探索格）压成 base64 位图落盘、读档再解回来」这件事。
//   这段编解码**只跟 `w/h/格索引` 有关**（纯 `System` / `System.Text`，零 Unity、零业务语义），
//   属通用底座：任何"稀疏布尔格 / 已访问区域 / 迷雾 / 掩码"都能用同一份实现
//   （`patterns/engine-fix.md` §4.7 的 D 桶）。
//   本件把编解码收进引擎；业务侧保留自己的 DTO（区域 id 等**语义字段**）与语义命名，转调本件。
//
// 语义约束（改一条 = 语义漂移；**与旧实现逐字节一致**是硬要求）：
//   ① 位图 = `(w*h + 7) / 8` 字节，**行优先**：格索引 `i = y * w + x` 对应第 `i >> 3` 字节的
//      第 `i & 7` 位（低位在前）；编码后 `Convert.ToBase64String` ⇒ 同集合恒得同串（确定性）。
//   ② **越界索引一律丢弃**（`< 0` / `>= w*h`）：⛔ 不抛、⛔ 不静默放大（不把负索引折成别的格）。
//   ③ 尺寸不合法（`w <= 0` / `h <= 0`）或超出上限 ⇒ `Encode` 返回空串、`Decode` 返回 0（**不抛**）。
//   ④ `Decode` **只并入、不清空**目标列表，返回本次并入的格数；`base64` 非法（旧档 / 手改）
//      ⇒ 返回 0 且不抛（调用方拿到"空集合"，读档流程照常成功）。
//   ⑤ 短串容错：字节数不够时，后面的格一律视为未探索（`break`，不抛）—— 旧档 / 截断串最坏
//      退化成"少记几格"。
//   ⑥ 上限 `MaxCells`（默认 1<<20 格）防"坏档里的超大 w*h 吃掉几百 MB"；需要别的上限的调用方
//      用带 `maxCells` 参数的重载显式传入。
//   ⑦ 无状态 / 纯函数：同样的输入恒得同样的输出（可直接当"存→读→再存"幂等断言用）。
//   ⑧ 本件不依赖 UnityEngine ⇒ 离线自检宿主（非 Unity 进程）可直接链进工程跑。
//
// 用法（首个消费方 = `clover-project-diablo2/client/Assets/Scripts/Def/ExploredCodec.cs`）：
//   var cells = GridBitSet.Encode(indices, w, h);          // 空串 = 尺寸非法 / 超上限
//   var n     = GridBitSet.Decode(cells, w, h, into);      // 坏串 ⇒ 0（into 不变）
//   var i     = GridBitSet.IndexOf(x, y, w, h);            // 越界 ⇒ -1
//   if (GridBitSet.ToCell(i, w, h, out var x, out var y)) { … }   // 越界 ⇒ false 且 (0,0)
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;

namespace CloverEngine
{
    /// <summary>
    /// 格集合的 base64 位图编解码（纯函数 / 无状态 / 越界与坏串一律容错，⛔ 不抛异常）。
    /// 语义与边界见文件头。
    /// </summary>
    public static class GridBitSet
    {
        /// <summary>
        /// 默认位图上限（格）：防御坏档里的超大 `w*h`（不设上限的话一个坏字段就能吃掉几百 MB）。
        /// </summary>
        public const int MaxCells = 1 << 20;          // 1,048,576 格

        /// <summary>
        /// 把格索引集合编码成 base64 位图（上限用 <see cref="MaxCells"/>）。
        /// <para>越界索引丢弃；`w/h &lt;= 0` 或 `w*h &gt;` 上限 ⇒ 返回空串。同集合 ⇒ 同串。</para>
        /// </summary>
        public static string Encode(IEnumerable<int> indices, int w, int h)
            => Encode(indices, w, h, MaxCells);

        /// <summary>
        /// 把格索引集合编码成 base64 位图（可指定上限）。
        /// <para>返回空串的两种情形：尺寸非法（`w/h &lt;= 0`）、`w*h` 超过 <paramref name="maxCells"/>。</para>
        /// </summary>
        public static string Encode(IEnumerable<int> indices, int w, int h, int maxCells)
        {
            if (w <= 0 || h <= 0) return string.Empty;
            var n = (long)w * h;
            if (maxCells > 0 && n > maxCells) return string.Empty;   // 防御：非法尺寸不做位图

            var bytes = new byte[(n + 7) / 8];
            if (indices != null)
            {
                foreach (var i in indices)
                {
                    if (i < 0 || i >= n) continue;
                    bytes[i >> 3] |= (byte)(1 << (i & 7));
                }
            }

            return Convert.ToBase64String(bytes);
        }

        /// <summary>
        /// 把 base64 位图解码成格索引（上限用 <see cref="MaxCells"/>；**只并入、不清空**目标列表）。
        /// </summary>
        /// <returns>本次并入的格数（坏串 / 尺寸非法 ⇒ 0，且 <paramref name="into"/> 不变）。</returns>
        public static int Decode(string cells, int w, int h, List<int> into)
            => Decode(cells, w, h, into, MaxCells);

        /// <summary>
        /// 把 base64 位图解码成格索引（可指定上限；**只并入、不清空** <paramref name="into"/>）。
        /// <para>兼容旧档 / 坏串：`into == null` / `cells` 空或 null / base64 非法 / 尺寸非法
        /// ⇒ 返回 0 且**不抛**（调用方拿到"空集合"，读档流程照常成功）。</para>
        /// </summary>
        /// <returns>本次并入的格数。</returns>
        public static int Decode(string cells, int w, int h, List<int> into, int maxCells)
        {
            if (into == null) return 0;
            if (w <= 0 || h <= 0) return 0;
            var n = (long)w * h;
            if (maxCells > 0 && n > maxCells) return 0;
            if (string.IsNullOrEmpty(cells)) return 0;

            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(cells);
            }
            catch (FormatException)
            {
                return 0;                                   // 坏串（旧档 / 手改）⇒ 当"没有已探索记录"
            }

            var count = 0;
            var total = (int)n;
            for (var i = 0; i < total; i++)
            {
                var b = i >> 3;
                if (b >= bytes.Length) break;               // 短串：后面一律视为未探索（不抛）
                if ((bytes[b] & (1 << (i & 7))) == 0) continue;
                into.Add(i);
                count++;
            }

            return count;
        }

        /// <summary>把格坐标折成索引（越界 / 尺寸非法返回 -1）。</summary>
        public static int IndexOf(int x, int y, int w, int h)
        {
            if (w <= 0 || h <= 0) return -1;
            if (x < 0 || y < 0 || x >= w || y >= h) return -1;
            return y * w + x;
        }

        /// <summary>
        /// 把索引还原成格坐标。
        /// </summary>
        /// <returns>是否成功（越界 / 尺寸非法 ⇒ <c>false</c>，且 <paramref name="x"/> / <paramref name="y"/> 为 0）。</returns>
        public static bool ToCell(int index, int w, int h, out int x, out int y)
        {
            x = 0;
            y = 0;
            if (w <= 0 || h <= 0 || index < 0 || index >= (long)w * h) return false;
            x = index % w;
            y = index / w;
            return true;
        }
    }
}
