using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace CloverEngine
{
    /// <summary>
    /// CloverMap 二进制格式 v1 的**运行时解码器**（客户端本地碰撞用）。
    ///
    /// 同一份契约的三处实现，改动必须同步（缺一处的失败是**静默**的：文件能读、游戏能起、只是地图不对）：
    ///   写：`MapWriter.cs`（<see cref="CloverMapWriter"/>，Editor 烘焙导出）
    ///   读：本文件（客户端：本地碰撞查询）
    ///   读：[`clover-server-engine/pkg/domain/mmo/mapdata/`](https://github.com/qw576483/clover-server-engine/blob/main/pkg/domain/mmo/mapdata/README.md)（服务端：权威碰撞 / 寻路 / 出生点）
    ///
    /// ★ 本文件**不引用引擎其它部分**（只用 System.IO 与几个 Unity 值类型），因此可以被
    /// 直接链进一个裸 .NET 控制台程序做跨端字节校验 —— 源文件直链，不存在副本漂移。
    /// </summary>
    internal static class CloverMapFormat
    {
        public const string Magic = "CLVM";
        public const int Version = 1;
        public const int HeaderSize = 64;
        public const int ColliderStride = 24;
        public const int SpawnStride = 12;

        public const ushort FlagWalkable = 1 << 0;

        /// <summary>V1 未实现（预留 V2 的多层 / 地形高度场）。</summary>
        public const ushort FlagHeightField = 1 << 1;

        private const ushort KnownFlags = FlagWalkable | FlagHeightField;

        /// <summary>单边格数上限（与服务端 <c>mapdata.MaxDimension</c>、写入端同一数值）。</summary>
        public const int MaxDimension = 32768;

        /// <summary>
        /// 解码地图数据。失败时返回 false 并给出**与服务端同语义**的错误串（便于两端日志对照）。
        /// </summary>
        public static bool TryDecode(byte[] data, out CloverMapData map, out string error)
        {
            map = null;
            error = null;

            if (data == null || data.Length < HeaderSize)
            {
                error = $"文件太小：{data?.Length ?? 0} 字节 < 定长头 {HeaderSize} 字节";
                return false;
            }

            try
            {
                using (var ms = new MemoryStream(data, false))
                using (var br = new BinaryReader(ms, Encoding.UTF8))
                {
                    var magic = br.ReadBytes(4);
                    if (magic.Length < 4 || magic[0] != (byte)'C' || magic[1] != (byte)'L'
                        || magic[2] != (byte)'V' || magic[3] != (byte)'M')
                    {
                        error = $"魔数不符：期望 \"{Magic}\"，实际 \"{Encoding.ASCII.GetString(data, 0, 4)}\"" +
                                "（非 CloverMap 产物请用 Clover/地图烘焙 重新导出）";
                        return false;
                    }

                    int version = br.ReadUInt16();
                    int flags = br.ReadUInt16();
                    ulong sceneId = br.ReadUInt64();
                    float cellSize = br.ReadSingle();
                    var origin = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                    int width = (int)br.ReadUInt32();
                    int depth = (int)br.ReadUInt32();
                    int colliderCount = (int)br.ReadUInt32();
                    int spawnCount = (int)br.ReadUInt32();
                    int nameLen = (int)br.ReadUInt32();
                    br.ReadBytes(12); // 保留段，V2 用

                    if (version != Version)
                    {
                        error = $"不支持的数据版本 {version}（客户端支持 {Version}），请重新导出地图";
                        return false;
                    }
                    if ((flags & ~KnownFlags) != 0)
                    {
                        error = $"数据 flags=0x{flags:X4} 含未知段 0x{flags & ~KnownFlags:X4}（客户端只认识 0x{KnownFlags:X4}）";
                        return false;
                    }
                    if ((flags & FlagHeightField) != 0)
                    {
                        // 见到高度场段必须**明确拒绝**：V1 解码器不认识它的布局，
                        // 若放行会把高度段误读成位图，静默产出错误地图（"文件能读、游戏能起、只是地图不对"）。
                        error = $"文件含高度场段（flags=0x{flags:X4}，V1 解码器不支持），请用支持 V2 的端或重新导出";
                        return false;
                    }
                    if ((flags & FlagWalkable) == 0)
                    {
                        error = $"数据没有可行走位图（flags=0x{flags:X4}），无法做本地碰撞";
                        return false;
                    }
                    if (width <= 0 || depth <= 0)
                    {
                        error = $"位图尺寸非法：{width}x{depth}";
                        return false;
                    }
                    if (width > MaxDimension || depth > MaxDimension)
                    {
                        error = $"位图尺寸超上限 {MaxDimension}：{width}x{depth}";
                        return false;
                    }

                    // 格数**一律用 long 表达**，不依赖 MaxDimension 当前的大小：
                    // 本判据与下面的位图长度计算共用同一个 long 值，因此「上调 MaxDimension」后依然成立 ——
                    // 只要乘积超出 int 可表达范围就先拦下，绝不把溢出后的（负数/巨值）长度交给后续算术。
                    // 注意别改回 (width * depth) 的 int 乘法：那正是溢出发生的地方。
                    long cells = (long)width * depth;
                    if (cells > int.MaxValue)
                    {
                        error = $"位图格数过多：{cells} 格（超过 {int.MaxValue}）";
                        return false;
                    }
                    if (cellSize <= 0f || float.IsNaN(cellSize) || float.IsInfinity(cellSize))
                    {
                        error = $"格边长非法：{cellSize}";
                        return false;
                    }
                    if (float.IsNaN(origin.x) || float.IsInfinity(origin.x)
                        || float.IsNaN(origin.y) || float.IsInfinity(origin.y)
                        || float.IsNaN(origin.z) || float.IsInfinity(origin.z))
                    {
                        // 原点非有限值时 WalkableAt 的 FloorToInt 会产出错误格索引：损坏文件当合法接受，
                        // 碰撞结果静默错乱（比"直接报错"难查得多）。
                        error = $"位图原点非法（NaN/Inf）：{origin}";
                        return false;
                    }
                    if (nameLen < 0 || nameLen > data.Length)
                    {
                        error = $"地图名长度非法：{nameLen}";
                        return false;
                    }

                    string name = nameLen > 0
                        ? Encoding.UTF8.GetString(br.ReadBytes(nameLen))
                        : string.Empty;

                    // 用上面校验过的 long 格数算长度（不重做 int 乘法）：cells ≤ int.MaxValue ⇒ 结果必在 int 范围内。
                    int bitsLen = (int)((cells + 7) / 8);
                    var bits = br.ReadBytes(bitsLen);
                    if (bits.Length < bitsLen)
                    {
                        error = $"文件被截断：位图需要 {bitsLen} 字节，实际 {bits.Length} 字节";
                        return false;
                    }

                    // uint 转 int 后可为负：不拦的话 need 变负、段长度校验恒通过，损坏文件被当作合法接受。
                    if (colliderCount < 0 || spawnCount < 0)
                    {
                        error = $"碰撞体/出生点数量非法：colliders={colliderCount} spawns={spawnCount}";
                        return false;
                    }

                    // 碰撞体 / 出生点客户端不用，但必须校验段完整，否则"半截文件"会被当成好文件。
                    long need = (long)colliderCount * ColliderStride + (long)spawnCount * SpawnStride;
                    long remain = ms.Length - ms.Position;
                    if (remain < need)
                    {
                        error = $"文件被截断：碰撞体+出生点需要 {need} 字节，实际 {remain} 字节";
                        return false;
                    }

                    map = new CloverMapData(version, sceneId, name, cellSize, origin,
                                            width, depth, bits, colliderCount);
                    return true;
                }
            }
            catch (Exception e)
            {
                // 非预期分支：文件长度已校验过，走到这里说明解码逻辑本身有问题，必须留完整异常
                //（异常类型 + 堆栈：只留 Message 会把"解码器自身缺陷"变成无从定位的黑盒）。
                error = $"解码异常：{e.GetType().Name}: {e.Message}\n{e.StackTrace}";
                return false;
            }
        }
    }

    /// <summary>
    /// 解码后的地图数据（只读视图）。
    /// 位图不是输入数组的切片：解码时 <c>br.ReadBytes(bitsLen)</c> 会**拷贝一份**独立字节
    /// （大小 = ⌈W×D/8⌉），因此调用方可以立即释放原始字节数组。
    /// </summary>
    internal sealed class CloverMapData
    {
        public readonly int Version;
        public readonly ulong SceneId;
        public readonly string Name;
        public readonly float CellSize;
        public readonly Vector3 Origin;
        public readonly int Width;
        public readonly int Depth;
        public readonly int ColliderCount;
        public readonly int WalkableCount;
        public readonly int BlockedCount;

        /// <summary>
        /// 位图末字节补位（不属于任何格）中被置 1 的位数。约定必须为 0，非 0 说明写端没清补位 ——
        /// 已按格数截断统计，但该情况应记告警（与服务端一致；本格式层不引日志，由调用方据此告警）。
        /// </summary>
        public readonly int TailPaddingOnes;

        /// <summary>数据是否可用（<see cref="CloverMapFormat.TryDecode"/> 成功构造的实例恒为 true）。</summary>
        public bool Loaded => true;

        private readonly byte[] _bits;

        internal CloverMapData(int version, ulong sceneId, string name, float cellSize, Vector3 origin,
                               int width, int depth, byte[] bits, int colliderCount)
        {
            Version = version;
            SceneId = sceneId;
            Name = name;
            CellSize = cellSize;
            Origin = origin;
            Width = width;
            Depth = depth;
            _bits = bits;
            ColliderCount = colliderCount;

            int total = width * depth;
            int walkable = 0;
            for (int i = 0; i < bits.Length; i++)
            {
                walkable += PopCount(bits[i]);
            }
            // 最后一字节的补位（width*depth 不是 8 的倍数时）不属于任何格，必须扣掉。
            if (total % 8 != 0 && bits.Length > 0)
            {
                TailPaddingOnes = PopCount((byte)(bits[bits.Length - 1] >> (total % 8)));
                walkable -= TailPaddingOnes;
            }
            WalkableCount = walkable;
            BlockedCount = total - walkable;
        }

        /// <summary>世界坐标是否可走（与服务端 <c>mapdata.WalkableAt</c> 同一算法）。</summary>
        public bool WalkableAt(float x, float z)
        {
            // ★ 必须用 FloorToInt：C# 的 (int) 对负数是向零截断，会把图外 (-cell, 0) 那一格
            //   当成第 0 格 ⇒ "图外可走"。服务端用 math.Floor，两端必须同一口径。
            int ix = Mathf.FloorToInt((x - Origin.x) / CellSize);
            int iz = Mathf.FloorToInt((z - Origin.z) / CellSize);
            if (ix < 0 || iz < 0 || ix >= Width || iz >= Depth) return false;
            int idx = iz * Width + ix;
            return (_bits[idx >> 3] & (1 << (idx & 7))) != 0;
        }

        private static int PopCount(byte b)
        {
            int n = 0;
            while (b != 0) { n += b & 1; b >>= 1; }
            return n;
        }

        public override string ToString()
        {
            return $"{Name} scene={SceneId} {Width}x{Depth} cell={CellSize:F2} " +
                   $"可走={WalkableCount} 阻挡={BlockedCount} 碰撞体={ColliderCount} v={Version}";
        }
    }
}
