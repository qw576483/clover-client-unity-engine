using System;
using CloverEngine;
using NUnit.Framework;
using UnityEngine;

namespace CloverEngine.Tests
{
    /// <summary>
    /// CloverMap 二进制格式的契约测试（EditMode）。
    ///
    /// 为什么值得测：这个格式是 **C# 写、Go 读**的跨语言契约，漂移的代价是"服务端能起、能加载，
    /// 只是地图不对"—— 静默失败。单元测试钉住的是**字节层面的约定**（偏移、字节序、位图打包方向、
    /// 补位、负坐标取整口径），这些恰恰是"两端各自看着都对、合起来就是不对"的地方。
    ///
    /// 端到端的另一半（Unity 真产物 → Go 加载器）在服务端：业务工程自己的 golden 用例；
    /// Go 侧的解析与出生点用例在 <c>clover-server-engine/pkg/domain/mmo/mapdata/</c>。
    /// </summary>
    public class MapFormatTests
    {
        private const string Magic = "CLVM";

        private static CloverMapWriter.MapData Sample()
        {
            const int w = 4, d = 4;
            var cells = new bool[w * d];
            for (int i = 0; i < cells.Length; i++) cells[i] = true;
            cells[2 * w + 1] = false; // (1,2) 阻挡 —— 用来钉住"行主序 + 字节内 LSB 优先"

            return new CloverMapWriter.MapData
            {
                SceneId = 1,
                Name = "unit-map",
                CellSize = 1f,
                Origin = Vector3.zero,
                Width = w,
                Depth = d,
                Cells = cells,
                Colliders = new[] { new Bounds(new Vector3(1.5f, 1.5f, 2.5f), new Vector3(1f, 3f, 1f)) },
                Spawns = new[] { new Vector3(0.5f, 0f, 0.5f) },
            };
        }

        [Test]
        public void EncodeDecode_RoundTrip()
        {
            var d = Sample();
            byte[] bytes = CloverMapWriter.Encode(d);

            Assert.IsTrue(CloverMapFormat.TryDecode(bytes, out var map, out string err), "回读失败: " + err);
            Assert.AreEqual(d.SceneId, map.SceneId);
            Assert.AreEqual(d.Name, map.Name);
            Assert.AreEqual(d.CellSize, map.CellSize, 1e-6f);
            Assert.AreEqual(d.Width, map.Width);
            Assert.AreEqual(d.Depth, map.Depth);
            Assert.AreEqual(1, map.ColliderCount);
            Assert.AreEqual(d.Width * d.Depth - 1, map.WalkableCount);
            Assert.AreEqual(1, map.BlockedCount);
        }

        /// <summary>逐字节钉住布局：改动编码器 / 解码器 / 服务端任一处，这里会立刻红。</summary>
        [Test]
        public void Encode_Layout_MatchesSpec()
        {
            var d = Sample();
            d.SceneId = 0x1_0000_0007UL; // 超过 uint32，验证 uint64 段宽度
            byte[] b = CloverMapWriter.Encode(d);

            Assert.AreEqual(Magic, System.Text.Encoding.ASCII.GetString(b, 0, 4), "magic");
            Assert.AreEqual(1, BitConverter.ToUInt16(b, 4), "version");
            Assert.AreEqual(1, BitConverter.ToUInt16(b, 6), "flags=FlagWalkable");
            Assert.AreEqual(d.SceneId, BitConverter.ToUInt64(b, 8), "scene_id");
            Assert.AreEqual(d.CellSize, BitConverter.ToSingle(b, 16), 1e-6f, "cell_size");
            Assert.AreEqual(d.Origin.x, BitConverter.ToSingle(b, 20), 1e-6f, "origin.x");
            Assert.AreEqual(d.Origin.y, BitConverter.ToSingle(b, 24), 1e-6f, "origin.y");
            Assert.AreEqual(d.Origin.z, BitConverter.ToSingle(b, 28), 1e-6f, "origin.z");
            Assert.AreEqual(d.Width, (int)BitConverter.ToUInt32(b, 32), "width");
            Assert.AreEqual(d.Depth, (int)BitConverter.ToUInt32(b, 36), "depth");
            Assert.AreEqual(1u, BitConverter.ToUInt32(b, 40), "collider_count");
            Assert.AreEqual(1u, BitConverter.ToUInt32(b, 44), "spawn_count");
            Assert.AreEqual(d.Name.Length, (int)BitConverter.ToUInt32(b, 48), "name_len");

            for (int i = 52; i < 64; i++)
            {
                Assert.AreEqual(0, b[i], $"保留段 [52,64) 必须全 0（偏移 {i}）");
            }

            // 名字紧跟在头之后，位图紧跟名字
            int nameOff = CloverMapFormat.HeaderSize;
            Assert.AreEqual(d.Name, System.Text.Encoding.UTF8.GetString(b, nameOff, d.Name.Length), "name");

            int bitsOff = nameOff + d.Name.Length;
            // idx = z*Width + x：(0,0)=0 可走 → 第 0 字节 bit0 = 1
            Assert.AreEqual(1, b[bitsOff] & 0x01, "(0,0) 可走，位图第 0 字节 bit0 应为 1");
            // (1,2) → idx = 2*4+1 = 9 → 第 1 字节 bit1 = 0（阻挡）
            Assert.AreEqual(0, b[bitsOff + 1] & 0x02, "(1,2) 阻挡，位图第 1 字节 bit1 应为 0");

            // 位图 2 字节之后是碰撞体（6 个 float32：min.xyz + max.xyz）与出生点（3 个 float32）
            int collOff = bitsOff + 2;
            Assert.AreEqual(1.0f, BitConverter.ToSingle(b, collOff), 1e-6f, "collider[0].min.x");
            Assert.AreEqual(0.0f, BitConverter.ToSingle(b, collOff + 4), 1e-6f, "collider[0].min.y");
            Assert.AreEqual(2.0f, BitConverter.ToSingle(b, collOff + 8), 1e-6f, "collider[0].min.z");
            Assert.AreEqual(2.0f, BitConverter.ToSingle(b, collOff + 12), 1e-6f, "collider[0].max.x");
            Assert.AreEqual(3.0f, BitConverter.ToSingle(b, collOff + 16), 1e-6f, "collider[0].max.y");
            Assert.AreEqual(3.0f, BitConverter.ToSingle(b, collOff + 20), 1e-6f, "collider[0].max.z");
            Assert.AreEqual(collOff + CloverMapFormat.ColliderStride + CloverMapFormat.SpawnStride, b.Length, "总长度");
        }

        [Test]
        public void Decode_RejectsBadData()
        {
            var d = Sample();

            var badMagic = CloverMapWriter.Encode(d);
            badMagic[0] = (byte)'X';
            AssertRejected(badMagic, "魔数");

            var badVersion = CloverMapWriter.Encode(d);
            BitConverter.GetBytes((ushort)99).CopyTo(badVersion, 4);
            AssertRejected(badVersion, "版本");

            var unknownFlags = CloverMapWriter.Encode(d);
            BitConverter.GetBytes((ushort)(1 | (1 << 15))).CopyTo(unknownFlags, 6);
            AssertRejected(unknownFlags, "未知段");

            var noBitmap = CloverMapWriter.Encode(d);
            BitConverter.GetBytes((ushort)0).CopyTo(noBitmap, 6);
            AssertRejected(noBitmap, "可行走位图");

            var zeroCell = CloverMapWriter.Encode(d);
            BitConverter.GetBytes(0f).CopyTo(zeroCell, 16);
            AssertRejected(zeroCell, "格边长");

            AssertRejected(new byte[8], "太小");
            AssertRejected(Array.Empty<byte>(), "太小");
        }

        private static void AssertRejected(byte[] data, string expectInMessage)
        {
            bool ok = CloverMapFormat.TryDecode(data, out _, out string err);
            Assert.IsFalse(ok, "期望解码失败但成功了");
            StringAssert.Contains(expectInMessage, err ?? string.Empty,
                $"错误信息里应出现「{expectInMessage}」，实际: {err}");
        }

        [Test]
        public void Decode_TruncatedTail_Rejected()
        {
            byte[] full = CloverMapWriter.Encode(Sample());
            for (int cut = 1; cut <= 4; cut++)
            {
                var cutOff = new byte[full.Length - cut];
                Array.Copy(full, cutOff, cutOff.Length);
                Assert.IsFalse(CloverMapFormat.TryDecode(cutOff, out _, out _),
                    $"砍掉尾部 {cut} 字节后仍被当成合法文件（截断必须报错）");
            }
        }

        /// <summary>
        /// 负坐标取整口径：必须 Floor 而不是向零截断，否则图外 (-cell, 0) 那一格会被当成第 0 格。
        /// 服务端用的是 math.Floor，两端必须同口径（否则"图外的一米"客户端能走、服务端拒绝）。
        /// </summary>
        [Test]
        public void WalkableAt_NegativeAndOutOfBounds_Rejected()
        {
            byte[] bytes = CloverMapWriter.Encode(Sample());
            Assert.IsTrue(CloverMapFormat.TryDecode(bytes, out var map, out string err), err);

            Assert.IsTrue(map.WalkableAt(0.5f, 0.5f), "图内可走格应为 true");
            Assert.IsFalse(map.WalkableAt(1.5f, 2.5f), "(1,2) 是阻挡格");
            Assert.IsFalse(map.WalkableAt(-0.5f, 0.5f), "(-0.5) 在图外，必须不可走");
            Assert.IsFalse(map.WalkableAt(0.5f, -0.5f), "(z=-0.5) 在图外，必须不可走");
            Assert.IsFalse(map.WalkableAt(4.5f, 0.5f), "超出 width 必须不可走");
            Assert.IsFalse(map.WalkableAt(0.5f, 4.5f), "超出 depth 必须不可走");
        }

        /// <summary>位图最后一字节的补位（w*d 不是 8 的倍数时）不该被算成可走格。</summary>
        [Test]
        public void Walkable_CountExcludesTailPadding()
        {
            const int w = 3, d = 3; // 9 格 → 2 字节，第 2 字节只有 1 位有效，其余 7 位是补位
            var cells = new bool[w * d];
            for (int i = 0; i < cells.Length; i++) cells[i] = true;

            byte[] bytes = CloverMapWriter.Encode(new CloverMapWriter.MapData
            {
                SceneId = 1, Name = "pad", CellSize = 1f, Origin = Vector3.zero,
                Width = w, Depth = d, Cells = cells,
            });

            // 故意把 7 个补位全写成 1（编码器理论上不该这么写），解码端必须按格数截断统计
            int bitsOff = CloverMapFormat.HeaderSize + 3;
            bytes[bitsOff + 1] |= 0xFE;

            Assert.IsTrue(CloverMapFormat.TryDecode(bytes, out var map, out string err), err);
            Assert.AreEqual(w * d, map.WalkableCount, "补位的 1 被算进可走格了");
            Assert.AreEqual(0, map.BlockedCount);
        }

        [Test]
        public void Encode_RejectsMismatchedCells()
        {
            var d = Sample();
            d.Cells = new bool[3]; // 与 4x4 不符
            Assert.Throws<ArgumentException>(() => CloverMapWriter.Encode(d));
        }

        [Test]
        public void Encode_RejectsOversizedMap()
        {
            var d = Sample();
            d.Width = CloverMapWriter.MaxDimension + 1;
            Assert.Throws<ArgumentException>(() => CloverMapWriter.Encode(d));
        }
    }
}
