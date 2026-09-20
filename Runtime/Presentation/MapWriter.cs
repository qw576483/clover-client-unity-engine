using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace CloverEngine
{
    /// <summary>
    /// CloverMap 二进制格式 v1 的**编码器** —— 格式层"写入"的那半边，与 <see cref="CloverMapFormat"/> 成对。
    ///
    /// 归属说明（为什么它不在 Editor/）：它**不依赖 UnityEditor**，只用 System.IO 与几个 Unity 值类型，
    /// 因此放在格式层与解码器同处；Editor 侧的烘焙器只负责「读场景 → 收集几何 → 调这里编码」。
    /// 这样分层还有个实际好处：测试程序集能直接测它（Editor 程序集是测试引用不到的），
    /// 编码器 / 解码器的**对称性**才有单元测试兜住。
    ///
    /// 运行时不引用本类型（只有 Editor 与测试用），托管代码剥离会把它摘掉，不进玩家包。
    ///
    /// 同一份契约的三处实现，改动必须同步（缺一处的失败是**静默**的：文件能写、能读、只是地图不对）：
    ///   写：本文件（Editor 烘焙导出）
    ///   读：`MapFormat.cs` 的 <see cref="CloverMapFormat"/>（客户端本地碰撞；本文件的自检也用它）
    ///   读：`clover-server-engine/pkg/domain/mmo/mapdata/`（服务端权威逻辑地图）
    ///
    /// 为什么导出二进制而不是 JSON：位图按格增长，真实地图动辄百万格 —— JSON 要么逐格写布尔（体积爆炸），
    /// 要么 base64（膨胀 33% 且服务端要多解码一趟）。二进制版位图就是原始字节，服务端**零拷贝**直接引用。
    /// </summary>
    internal static class CloverMapWriter
    {
        /// <summary>单边格数上限（与服务端 <c>mapdata.MaxDimension</c>、解码端同一数值）。</summary>
        internal const int MaxDimension = CloverMapFormat.MaxDimension;

        /// <summary>一次烘焙的输入（烘焙器收集好的数据）。</summary>
        internal struct MapData
        {
            public ulong SceneId;
            public string Name;
            public float CellSize;
            public Vector3 Origin;
            public int Width;
            public int Depth;

            /// <summary>可行走位图，行主序 idx = z * Width + x，长度必须 = Width * Depth。</summary>
            public bool[] Cells;

            /// <summary>障碍物世界 AABB（服务端 Collider3 用）。</summary>
            public Bounds[] Colliders;

            /// <summary>出生点（服务端会做净空校验并可能挪动，见服务端 <c>mapdata/spawn.go</c>）。</summary>
            public Vector3[] Spawns;
        }

        /// <summary>按格式布局编码为字节。参数非法时抛异常（宁可导出失败，也不要写出一份"看着正常"的坏数据）。</summary>
        internal static byte[] Encode(MapData d)
        {
            if (d.Width <= 0 || d.Depth <= 0)
                throw new ArgumentException($"位图尺寸非法: {d.Width}x{d.Depth}");
            if (d.Width > MaxDimension || d.Depth > MaxDimension)
                throw new ArgumentException($"位图尺寸超上限 {MaxDimension}: {d.Width}x{d.Depth}");
            if (d.Cells == null || d.Cells.Length != d.Width * d.Depth)
                throw new ArgumentException($"位图格数不符: Cells={d.Cells?.Length ?? 0}, 期望 {d.Width * d.Depth}");
            if (d.CellSize <= 0f || float.IsNaN(d.CellSize) || float.IsInfinity(d.CellSize))
                throw new ArgumentException($"格边长非法: {d.CellSize}");

            var name = Encoding.UTF8.GetBytes(d.Name ?? string.Empty);
            var bits = PackCells(d.Cells);
            var colliders = d.Colliders ?? Array.Empty<Bounds>();
            var spawns = d.Spawns ?? Array.Empty<Vector3>();

            // 段长度用格式层常量（ColliderStride=24 / SpawnStride=12），不写 12*2 / 12 字面量：
            // 改格式时只改一处，避免编码长度漂移成"文件能写、读端截断"的静默失败。
            int total = CloverMapFormat.HeaderSize + name.Length + bits.Length
                        + colliders.Length * CloverMapFormat.ColliderStride
                        + spawns.Length * CloverMapFormat.SpawnStride;

            using (var ms = new MemoryStream(total))
            using (var bw = new BinaryWriter(ms, Encoding.UTF8))
            {
                // ---- 定长头（64 字节，小端）----
                bw.Write(new[] { (byte)'C', (byte)'L', (byte)'V', (byte)'M' });
                bw.Write((ushort)CloverMapFormat.Version);
                bw.Write(CloverMapFormat.FlagWalkable);
                bw.Write(d.SceneId);
                bw.Write(d.CellSize);
                WriteVec3(bw, d.Origin);
                bw.Write((uint)d.Width);
                bw.Write((uint)d.Depth);
                bw.Write((uint)colliders.Length);
                bw.Write((uint)spawns.Length);
                bw.Write((uint)name.Length);
                bw.Write(new byte[12]); // 保留段（V2 用），必须全 0

                // ---- 不定长段 ----
                bw.Write(name);
                bw.Write(bits);
                foreach (var b in colliders)
                {
                    WriteVec3(bw, b.min);
                    WriteVec3(bw, b.max);
                }
                foreach (var s in spawns)
                {
                    WriteVec3(bw, s);
                }

                bw.Flush();
                var bytes = ms.ToArray();
                if (bytes.Length != total)
                {
                    // 非预期分支：布局算出来的长度与实际写出的不一致 = 字段宽度用错了。
                    throw new InvalidOperationException($"编码长度不符: 实际 {bytes.Length} 字节, 期望 {total} 字节");
                }
                return bytes;
            }
        }

        /// <summary>位图 → 字节（行主序，字节内 LSB 优先，1=可走）。与服务端 / 解码器同一约定。</summary>
        private static byte[] PackCells(bool[] cells)
        {
            var raw = new byte[(cells.Length + 7) / 8];
            for (int i = 0; i < cells.Length; i++)
            {
                if (cells[i]) raw[i >> 3] |= (byte)(1 << (i & 7));
            }
            return raw;
        }

        private static void WriteVec3(BinaryWriter bw, Vector3 v)
        {
            bw.Write(v.x);
            bw.Write(v.y);
            bw.Write(v.z);
        }

        /// <summary>
        /// 写完立刻回读自检：用**同一套解码器**（运行时那套）读回来，比对关键字段。
        ///
        /// 为什么值得做：写坏了这种失败是"静默"的 —— 文件在、能加载、只是地图不对，
        /// 等到联调才发现（历史上真踩过：导出了一张空的"全可走"地图）。
        /// </summary>
        internal static string Verify(byte[] bytes)
        {
            if (!CloverMapFormat.TryDecode(bytes, out var map, out string err))
            {
                throw new InvalidOperationException($"导出自检失败（回读不了）：{err}");
            }
            return $"{map.Name} scene={map.SceneId} {map.Width}x{map.Depth} cell={map.CellSize:F2} " +
                   $"可走={map.WalkableCount} 阻挡={map.BlockedCount} 碰撞体={map.ColliderCount}";
        }
    }
}
