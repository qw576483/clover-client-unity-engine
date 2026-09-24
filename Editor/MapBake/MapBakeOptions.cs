using System;
using UnityEditor;
using UnityEngine;

namespace CloverEngine.Editor
{
    /// <summary>
    /// 一次地图烘焙的参数（"什么算障碍 / 位图多大 / 原点在哪"）。
    ///
    /// **这些是业务参数，不是引擎常量**：不同项目的关卡尺寸、地面高度、障碍判定阈值都不一样。
    /// 引擎给的是「怎么烘」这条链路与中性默认值；具体填什么由项目在面板上给，
    /// 并被**持久化到 EditorPrefs** —— 因此命令行 / CI 里 <c>-executeMethod MapBaker.Export</c>
    /// 能复用同一次配置，而不必把参数再写一遍。
    /// </summary>
    [Serializable]
    public sealed class MapBakeOptions
    {
        private const string PrefsKey = "Clover.MapBake.Options";

        // ---- 输入（Unity 侧）----
        /// <summary>要烘焙的场景（相对工程根的 Assets 路径，如 <c>Assets/Scenes/Main.unity</c>）。</summary>
        public string ScenePath = string.Empty;

        // ---- 输出（数据契约，写进文件头）----
        /// <summary>逻辑地图 id（与服务端 mmo 场景 id、客户端 <c>CloverScene.SceneID</c> 对齐）。</summary>
        public ulong SceneId = 1;

        /// <summary>地图名（也是文件名主干）。</summary>
        public string MapName = "map";

        /// <summary>格边长（米）。服务端寻路 / AI 的精度上限。</summary>
        public float CellSize = 1f;

        /// <summary>位图原点在世界坐标的位置（格子 (0,0) 的角）。</summary>
        public Vector3 Origin = Vector3.zero;

        /// <summary>位图宽度（格，东西向）。</summary>
        public int MapWidth = 64;

        /// <summary>位图深度（格，南北向）。</summary>
        public int MapDepth = 64;

        // ---- 烘焙规则 ----
        /// <summary>地面顶面高度：低于「地面 + ObstacleMinHeight」的碰撞体视为地面，不算障碍。</summary>
        public float GroundTopY;

        /// <summary>障碍最小高度（米）。低于它的碰撞体是地面 / 贴地薄板，不参与阻挡。</summary>
        public float ObstacleMinHeight = 0.5f;

        /// <summary>
        /// 可行走性取样柱体的底面，**相对地面顶面（GroundTopY）的高度**（米）：实际世界 Y = GroundTopY + ProbeBottomY。
        /// 须严格 &gt; 0（即底面严格高于地面，避免把地面自身算成障碍）。
        /// <para>
        /// 语义修正：旧实现按**绝对 Y** 解释（默认 0.2~2.2）—— 地面 Y≠0 的关卡（如地面在 10m）
        /// 取样柱整根埋在地下，逐格都不与障碍相交，会烘出"全可走"空地图且无告警；
        /// 改为相对地面后默认值在任何地面高度都可用。
        /// </para>
        /// </summary>
        public float ProbeBottomY = 0.2f;

        /// <summary>可行走性取样柱体的顶面，**相对地面顶面（GroundTopY）的高度**（米）：实际世界 Y = GroundTopY + ProbeTopY。</summary>
        public float ProbeTopY = 2.2f;

        // ---- 层过滤（多层地图：逐层各烘一份）----
        //
        // 为什么需要它：本引擎的位图是**单层 2D**（格柱 ∩ 障碍 AABB ⇒ 这一格能不能走）。
        // 多层地图（上下两层楼板、T/CT 出生点差好几米、楼梯/高台）直接整场景烘一遍，
        // 会把"上层楼板"整片算成阻挡 ⇒ 上下两层在**一张平面**上互相遮挡，连通性当场断掉。
        // 引擎给的解法是「把哪一层算障碍」变成参数（而不是让项目去写"临时关碰撞体/临时改层级"的绕法）：
        // 逐层各烘一份单层位图，一份地图数据对应一个楼层。
        // ⚠️ 真·逐格地形高度场（一份数据带多层）列 V2，见 CloverMapFormat.FlagHeightField。

        /// <summary>
        /// 层过滤开关。<b>默认 false = 现状行为</b>：场景里所有"非地面"碰撞体都参与阻挡烘焙，
        /// 产出与加这个开关之前**逐字节一致**（旧工程不改参数 ⇒ 结果不变）。
        /// </summary>
        public bool LayerFilterEnabled;

        /// <summary>参与烘焙的 Unity Layer 区间**下界（含）**。仅当 <see cref="LayerFilterEnabled"/> 为 true 时生效。</summary>
        public int LayerMin;

        /// <summary>参与烘焙的 Unity Layer 区间**上界（含）**。仅当 <see cref="LayerFilterEnabled"/> 为 true 时生效。</summary>
        public int LayerMax = 31;

        /// <summary>
        /// 参与烘焙的**高度带下界**（世界 Y，米；含）。仅当 <see cref="LayerFilterEnabled"/> 为 true 时生效。
        /// 完全低于它的几何（典型：上一层/下一层的楼板）不参与阻挡。
        /// </summary>
        public float LayerMinY;

        /// <summary>
        /// 参与烘焙的**高度带上界**（世界 Y，米；含）。仅当 <see cref="LayerFilterEnabled"/> 为 true 时生效。
        /// <b>&lt;= <see cref="LayerMinY"/> 表示不设上界</b>（即「按高度阈值取层」：只要不低于下界就算参与）。
        /// </summary>
        public float LayerMaxY;

        // ---- 输出位置 ----
        /// <summary>服务端读取的产物目录（服务端自己按路径探测该文件）。</summary>
        public string ServerDir = "Assets/MapData";

        /// <summary>客户端运行时读取的产物目录（必须落在 Resources 下才打进包体）。</summary>
        public string ClientDir = "Assets/Resources/MapData";

        /// <summary>
        /// 出生点标记的对象名前缀：场景里以此开头的对象（含空物体）的位置即出生点，业务可精确摆放。
        /// 一个都没有时回退到「地图两角内缩 4m」—— 能跑，但可能贴着建筑；
        /// 服务端加载时还会做净空校验兜底（贴墙出生会让玩家一进图就被本地碰撞锁死）。
        /// </summary>
        public string SpawnMarkerPrefix = "Spawn";

        /// <summary>
        /// **命名标记点**的根对象名（场景里的根对象，如 cs16 的 <c>Level/Markers</c> 那个 <c>Markers</c>）：
        /// 该根下每个子物体的**对象名 = 标记名、世界坐标 = 点位**，随同一份 .bytes 导出
        /// （见 <c>CloverMapFormat.FlagMarkers</c>；客户端用 <c>Game.Map.GetPoints(名字)</c> 取）。
        /// <para>
        /// <b>留空 = 不导出标记点段（现状行为）</b>。不导出时文件里没有该段，
        /// 客户端按名取点会全部落空（引擎会打 Warn 而不是静默）。
        /// </para>
        /// <para>
        /// Y 取对象的**真实高度**（同一名字的点可以在不同楼层），引擎不做任何业务吸附 ——
        /// "落阻挡格就吸附到最近可走格心"这类规则属于项目（与出生点 <see cref="SpawnMarkerPrefix"/> 同一分工）。
        /// </para>
        /// </summary>
        public string MarkerRootName = string.Empty;

        /// <summary>产物文件名（<c>&lt;地图名&gt;.bytes</c>：Unity 按 <c>TextAsset</c> 导入，取其 <c>bytes</c>）。</summary>
        public string FileName => MapName + ".bytes";

        public MapBakeOptions Clone() => (MapBakeOptions)MemberwiseClone();

        /// <summary>
        /// 该碰撞体是否参与**阻挡**烘焙（层过滤判定）。<see cref="LayerFilterEnabled"/> 为 false 时
        /// **恒返回 true** —— 即现状行为，产出与旧版逐字节一致。
        /// <para>
        /// 开关打开时的判据：Unity Layer 落在 [<see cref="LayerMin"/>, <see cref="LayerMax"/>]（闭区间），
        /// 且碰撞体的垂直跨度 [<paramref name="minY"/>, <paramref name="maxY"/>] 与高度带
        /// [<see cref="LayerMinY"/>, <see cref="LayerMaxY"/>] **相交**（<see cref="LayerMaxY"/> ≤
        /// <see cref="LayerMinY"/> 视为不设上界）。
        /// 用"相交"而不是"完全包含"：跨层的大墙 / 立柱**仍然算障碍**（它真的挡路），
        /// 只有**完全落在带外**的楼层（典型：上层楼板）才被排除。
        /// </para>
        /// <para>
        /// 刻意抽成**纯函数**（不碰场景 / 不碰 UnityEditor）：它是最容易配错、也最该被离线自检
        /// 直接跑断言的那一段。
        /// </para>
        /// </summary>
        /// <param name="layer">碰撞体所在 Unity Layer（0~31）。</param>
        /// <param name="minY">碰撞体世界 AABB 的最小 Y（米）。</param>
        /// <param name="maxY">碰撞体世界 AABB 的最大 Y（米）。</param>
        public bool ShouldBakeCollider(int layer, float minY, float maxY)
        {
            if (!LayerFilterEnabled) return true;
            if (layer < LayerMin || layer > LayerMax) return false;
            if (maxY < LayerMinY) return false;                            // 整个在高度带下方
            if (LayerMaxY > LayerMinY && minY > LayerMaxY) return false;   // 整个在高度带上方
            return true;
        }

        /// <summary>参数校验；返回 null 表示合法，否则返回一条人类可读的原因。</summary>
        public string Validate()
        {
            if (string.IsNullOrWhiteSpace(ScenePath)) return "场景路径为空";
            if (string.IsNullOrWhiteSpace(MapName)) return "地图名为空";
            if (MapName.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0) return "地图名含非法文件名字符";
            if (CellSize <= 0f || float.IsNaN(CellSize)) return "格边长必须 > 0";
            if (MapWidth <= 0 || MapDepth <= 0) return "宽 / 深必须 > 0";
            if (MapWidth > CloverMapWriter.MaxDimension || MapDepth > CloverMapWriter.MaxDimension)
                return $"宽 / 深不能超过 {CloverMapWriter.MaxDimension} 格";
            if (ProbeTopY <= ProbeBottomY) return "取样柱顶面必须高于底面";
            // 取样柱以地面顶面为基准（MapBaker 用 GroundTopY + Probe* 求实际 Y）：
            // 实际底面 = GroundTopY + ProbeBottomY 必须严格高于 GroundTopY，即 ProbeBottomY > 0
            // （绝对 Y 语义下等价于"取样柱底面 > GroundTopY"，这正是"须严格高于地面"的字段契约）。
            // 不拦的话配错也能烘焙成功，产出取样柱埋在地下的"全可走"空碰撞地图。
            if (ProbeBottomY <= 0f)
                return "取样柱底面必须严格高于地面（相对 GroundTopY 的高度须 > 0）";
            // 层过滤只在开关打开时校验（开关关着时这些值是死值，不该让旧配置因为"默认值恰好非法"而导不出）。
            if (LayerFilterEnabled)
            {
                if (LayerMin < 0 || LayerMin > LayerMax || LayerMax > 31)
                    return $"层过滤的 Unity Layer 区间非法：[{LayerMin}, {LayerMax}]（须 0 ≤ 下界 ≤ 上界 ≤ 31）";
                if (float.IsNaN(LayerMinY) || float.IsInfinity(LayerMinY)
                    || float.IsNaN(LayerMaxY) || float.IsInfinity(LayerMaxY))
                    return $"层过滤的高度带含 NaN/Inf：Y[{LayerMinY}, {LayerMaxY}]";
            }
            if (string.IsNullOrWhiteSpace(ServerDir)) return "服务端目录为空";
            if (string.IsNullOrWhiteSpace(ClientDir)) return "客户端目录为空";
            // 客户端产物必须落 Resources 下才会被 Unity 打进包体：否则导出"成功"但运行时读不到，
            // 本地碰撞静默退化（表现为本地能穿墙 / 出图，再被服务端拽回）。
            if (!IsUnderResourcesFolder(ClientDir))
                return "客户端目录必须位于 Assets/Resources 下（否则不会打进包体，客户端运行时读不到地图）";
            return null;
        }

        /// <summary>客户端目录是否位于 <c>Assets/Resources</c> 下（含 <c>Assets/Resources</c> 本身）。</summary>
        private static bool IsUnderResourcesFolder(string dir)
        {
            // 统一分隔符后按前缀判断（面板里手工输入可能用反斜杠 / 结尾斜杠）。
            var d = (dir ?? string.Empty).Replace('\\', '/').TrimEnd('/');
            return d.Equals("Assets/Resources", StringComparison.OrdinalIgnoreCase) ||
                   d.StartsWith("Assets/Resources/", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 读取上次配置（面板里改过就会存下来）。没有配置过时返回一份中性默认值并**标记为未配置** ——
        /// 调用方据此决定是"报错让人先配一次"还是"直接用"（见 <see cref="IsConfigured"/>）。
        /// </summary>
        public static MapBakeOptions Load()
        {
            var json = EditorPrefs.GetString(PrefsKey, null);
            if (string.IsNullOrEmpty(json)) return new MapBakeOptions();
            try
            {
                var o = JsonUtility.FromJson<MapBakeOptions>(json);
                return o ?? new MapBakeOptions();
            }
            catch (Exception)
            {
                // 非预期分支：EditorPrefs 里是坏 JSON（手工改过 / 版本变了）→ 回到默认并留痕，
                // 不能让一个坏配置把整个面板卡死。
                Debug.LogWarning("[MapBake] 已保存的烘焙参数无法解析，已回到默认值");
                return new MapBakeOptions();
            }
        }

        /// <summary>保存本次配置（面板 / 成功导出后调用），供下次打开与命令行复用。</summary>
        public void Save()
        {
            EditorPrefs.SetString(PrefsKey, JsonUtility.ToJson(this));
        }

        /// <summary>是否已配置过（<c>ScenePath</c> 非空即视为配置过）。</summary>
        public bool IsConfigured => !string.IsNullOrWhiteSpace(ScenePath);
    }
}
