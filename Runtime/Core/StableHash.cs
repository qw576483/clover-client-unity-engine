// ─────────────────────────────────────────────────────────────────────────────
// CloverEngine · Runtime/Core/StableHash.cs
// 「程序化生成结果」的**通用自证设施**两半：
//   ① FNV-1a 64 位稳定哈希（同 seed 两次生成必须同哈希 —— 日志里贴一行哈希就够）；
//   ② 网格 → ASCII 字符画 dump（人可读快照：一眼分出"这条是河"还是"这条是石头"）。
//
// 口径（**顺序与常量即契约**）：
//   · 逐格哈希 = 常量 `14695981039346656037UL` / `1099511628211UL`、
//     **先混 width / height、再按 y 外层升序 x 内层升序逐格混入地形码**、`h.ToString("X16")`；
//   · ASCII dump = 行首 `y.ToString("D3") + '|'`、首行图例、`maxRows > 0` 只输出顶部若干行（地图北端在上）。
//   ⛔ "什么算一格 / 一格是什么字符"由**调用方传委托** —— 引擎不认地图容器 / 地形枚举这类项目语义。
//
// 设计依据：程序化生成（地图 / 关卡 / 迷宫 / 随机布置）靠同一套"**同 seed 复现**"判据，
//   而"肉眼比对图"既慢又不可判 —— 一行哈希 + 一张字符画是**离线可断言**的最小证据面。
//   ⛔ 哈希常量写错（off-by-one 的 prime）或"逐格顺序不一致"都会让**两条日志看起来都对、
//   却永远对不上**，且不报错 —— 故常量与顺序都必须集中在本件一处。
//
// 用法：
//   `var hex = StableHash.HashGridHex(w, h, ReadKind);`   // ReadKind = (x, y) => (byte)map.Get(x, y)
//   `Game.Logger.Info("Map", StableHash.ToAscii(w, h, CharOf, "图例: . 可走 / # 阻挡", maxRows: 12));`
//
// 边界（⛔ 防止当万能药用）：
//   · 委托签名是 **(x, y)**，与 `GridUtil` / `Vector2Int` 同序（⛔ 不是 (row, col)）；
//   · `HashGrid` 的口径是**逐格 `byte` 地形码**；要哈希复合结构请自己用 `Combine` 拼（顺序即契约）；
//   · 不线程安全（主线程使用）；`ToAscii` 每次调用分配一个 `StringBuilder`（诊断路径，⛔ 不进热循环）。
// ─────────────────────────────────────────────────────────────────────────────

using System.Text;

namespace CloverEngine
{
    /// <summary>读一格的**地形码**（稳定哈希用；<c>(x, y)</c> 顺序，越界由调用方自己给安全值）。</summary>
    /// <param name="x">格 x 下标（列）。</param>
    /// <param name="y">格 y 下标（行）。</param>
    public delegate byte GridCellCodeReader(int x, int y);

    /// <summary>读一格的**显示字符**（ASCII dump 用；<c>(x, y)</c> 顺序）。</summary>
    /// <param name="x">格 x 下标（列）。</param>
    /// <param name="y">格 y 下标（行）。</param>
    public delegate char GridCellCharReader(int x, int y);

    /// <summary>
    /// 稳定哈希 + 网格字符画（FNV-1a 64 位；口径见文件头）。
    /// <para>FNV-1a 的**稳定**含义：同输入恒得同值、跨机器 / 跨平台 / 跨 Unity 版本一致
    /// （⛔ 不用 <c>string.GetHashCode()</c> —— 它在 .NET Core 起**每进程随机化**，日志对不上）。</para>
    /// </summary>
    public static class StableHash
    {
        private const string Tag = "StableHash";

        /// <summary>FNV-1a 64 位偏移基（<c>hash := offset</c>；口径见文件头）。</summary>
        public const ulong OffsetBasis = 14695981039346656037UL;

        /// <summary>FNV-1a 64 位质数（每步 <c>hash *= prime</c>；口径见文件头）。</summary>
        public const ulong Prime = 1099511628211UL;

        /// <summary>FNV-1a 64 位：把 <paramref name="value"/> 混进已有哈希（<c>hash ^= value; hash *= prime</c>）。</summary>
        public static ulong Combine(ulong hash, ulong value)
        {
            hash ^= value;
            hash *= Prime;
            return hash;
        }

        /// <summary>
        /// 把 <see cref="int"/> 混进已有哈希。
        /// <para>⛔ 先转 <c>uint</c> 再混：负数不做符号扩展 ⇒ 同一数值在 32/64 位下哈希一致
        /// （直接 <c>(ulong)value</c> 会把 −1 混成 <c>0xFFFFFFFFFFFFFFFF</c>）。</para>
        /// </summary>
        public static ulong Combine(ulong hash, int value) => Combine(hash, unchecked((uint)value));

        /// <summary>把 <see cref="bool"/> 混进已有哈希（<c>false</c> = 0、<c>true</c> = 1）。</summary>
        public static ulong Combine(ulong hash, bool value) => Combine(hash, (ulong)(value ? 1 : 0));

        /// <summary>FNV-1a 64 位：整个字节序列（可指定区间）。</summary>
        /// <param name="data">字节数组；为 null ⇒ 只混 0（不打日志：null 是合法入参）。</param>
        /// <param name="offset">起始下标（越界自动夹到合法区间）。</param>
        /// <param name="count">字节数（越界自动夹到合法区间）。</param>
        public static ulong Fnv1a64(byte[] data, int offset = 0, int count = -1)
        {
            var h = OffsetBasis;
            if (data == null) return h;

            if (count < 0) count = data.Length - offset;
            if (offset < 0) { count += offset; offset = 0; }
            if (count > data.Length - offset) count = data.Length - offset;
            for (var i = 0; i < count; i++) h = Combine(h, data[offset + i]);
            return h;
        }

        /// <summary>FNV-1a 64 位：字符串按 **UTF-8 字节**哈希（⚠️ 不是逐 <c>char</c>：后者会让高字节恒 0 的 ASCII 与中文撞在一起）。</summary>
        public static ulong Fnv1a64(string text)
            => string.IsNullOrEmpty(text) ? OffsetBasis : Fnv1a64(Encoding.UTF8.GetBytes(text));

        /// <summary>哈希 → 16 位大写十六进制（日志里贴它；`"X16"` 口径见文件头）。</summary>
        public static string Hex(ulong hash) => hash.ToString("X16");

        /// <summary>
        /// 网格逐格哈希：先混 <paramref name="width"/> / <paramref name="height"/>，再按
        /// **y 升序外层、x 升序内层**逐格混入 `byte` 地形码（顺序即契约：换顺序 = 同图不同哈希）。
        /// </summary>
        /// <param name="width">格宽（列数）。</param>
        /// <param name="height">格高（行数）。</param>
        /// <param name="read">读一格的 `byte` 地形码（<c>(x, y)</c> 顺序）。</param>
        /// <param name="seed">起始哈希（默认 <see cref="OffsetBasis"/>；要串联多张图就传上一张的结果）。</param>
        public static ulong HashGrid(int width, int height, GridCellCodeReader read, ulong seed = OffsetBasis)
        {
            if (read == null)
            {
                // 非预期分支：漏传读取器。只说一次（每帧调一次会把它变成刷屏源）
                LogThrottle.ErrorOnce(Tag, "hashgrid.null-reader", "HashGrid 的 read 为 null ⇒ 只返回尺寸哈希");
                return Combine(Combine(seed, width), height);
            }
            if (width <= 0 || height <= 0)
            {
                // 非预期分支：尺寸非正（参数顺序写反 / 图没生成）⇒ 立即可见，别静默算出一个"看起来正常"的哈希
                LogThrottle.ErrorThrottled(Tag, "hashgrid.bad-size",
                    $"HashGrid 尺寸非正（width={width}, height={height}）⇒ 只返回尺寸哈希");
                return Combine(Combine(seed, width), height);
            }

            var h = Combine(Combine(seed, width), height);
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                h = Combine(h, read(x, y));
            return h;
        }

        /// <summary><see cref="HashGrid"/> 的十六进制形式（日志里贴这一串）。</summary>
        public static string HashGridHex(int width, int height, GridCellCodeReader read, ulong seed = OffsetBasis)
            => Hex(HashGrid(width, height, read, seed));

        /// <summary>
        /// 网格 → ASCII 字符画（**人可读自证**：同 seed 两次生成应逐字符相同）。
        /// <para>行首 <c>y.ToString("D" + digits) + '|'</c>；<paramref name="northFirst"/> = true 时
        /// **y 从大到小**输出（地图「北」在顶部），false 时 y 从 0 升序。</para>
        /// </summary>
        /// <param name="width">格宽（列数）。</param>
        /// <param name="height">格高（行数）。</param>
        /// <param name="charOf">读一格的显示字符（<c>(x, y)</c> 顺序）。</param>
        /// <param name="header">首行说明 / 图例（可为 null ⇒ 不打首行）。</param>
        /// <param name="maxRows">最多输出多少行（<c>&lt;= 0</c> = 全部；大图默认只打头部若干行，否则刷屏）。</param>
        /// <param name="northFirst">true = y 从大到小（北在上）；false = y 从 0 升序。</param>
        /// <param name="rowLabelDigits">行首 y 的位数（默认 3）。</param>
        public static string ToAscii(int width, int height, GridCellCharReader charOf,
            string header = null, int maxRows = 0, bool northFirst = true, int rowLabelDigits = 3)
        {
            if (charOf == null)
            {
                // 非预期分支：漏传读取器 ⇒ 返回空串（⛔ 不返回"看起来打过图"的内容）
                LogThrottle.ErrorOnce(Tag, "toascii.null-reader", "ToAscii 的 charOf 为 null ⇒ 返回空串");
                return string.Empty;
            }
            if (width <= 0 || height <= 0)
            {
                LogThrottle.ErrorThrottled(Tag, "toascii.bad-size",
                    $"ToAscii 尺寸非正（width={width}, height={height}）⇒ 返回空串");
                return string.Empty;
            }

            if (rowLabelDigits < 1) rowLabelDigits = 1;
            var rows = maxRows > 0 && maxRows < height ? maxRows : height;
            var sb = new StringBuilder((width + rowLabelDigits + 2) * rows + 64);
            if (!string.IsNullOrEmpty(header)) sb.AppendLine(header);

            for (var i = 0; i < rows; i++)
            {
                var y = northFirst ? height - 1 - i : i;
                sb.Append(y.ToString("D" + rowLabelDigits)).Append('|');
                for (var x = 0; x < width; x++) sb.Append(charOf(x, y));
                sb.AppendLine();
            }
            return sb.ToString();
        }
    }
}
