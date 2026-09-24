// ─────────────────────────────────────────────────────────────────────────────
// CloverEngine · Editor/PixelArtImportSettings.cs
// 像素素材导入规范（配置资产）：规则表 + 一套像素画纹理设置。
//
// 出处（语义与"坑"逐条照搬，⛔ 不含任何项目专属目录/数值）：
//   clover-project-super-mario `client/Assets/Editor/SpriteImportPostprocessor.cs:20-64`
//   —— 那份是**写死目录**的（`/Resources/Sprites/`、`/Sprites/Mario/`…），且"开关"就是
//      "文件存在即生效"。下沉时必须去掉这两点：
//      ① 目录一律来自本资产的 <see cref="Rules"/>（规则表同时是**作用域**：不在表里的纹理一律不碰）；
//      ② **默认不生效** —— 本资产不存在 / 未被 `Clover/像素素材导入/创建或选择配置资产…` 选中 /
//         总开关（EditorPrefs，默认关）未打开 ⇒ 后处理器直接 return。
//      原因：同一台机器上会有多个工程，后处理器是**全局**的；默认生效会悄悄改掉别的工程的导入设置。
//
// ⛔ 刻意**不提供** `[CreateAssetMenu]`：从 Assets 菜单凭空建出来的资产不会在 EditorPrefs 里登记，
//    后处理器看不见它 —— 那是一个"建了但没生效"的静默陷阱。本资产**只**由菜单项
//    `Clover/像素素材导入/创建或选择配置资产…` 创建/选择（创建后即登记）。
//
// 为什么这层配置要落在资产里而不是写死在脚本里：素材是**美术产物**，一批几百张，
// 手设必漏；而漏设的症状是"某几张图糊了 / 人物浮在半空"，极难查（出处文件开头的三条后果）。
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace CloverEngine.Editor
{
    /// <summary>
    /// 轴心对齐方式（两套，覆盖参考实现的两种）。
    /// </summary>
    public enum PixelArtPivot
    {
        /// <summary>居中：平铺类（瓦片 / 背景 / 特效）—— 格子中心即轴心。</summary>
        Center = 0,

        /// <summary>
        /// 底部居中：站在地上的（角色 / 敌人 / 道具）—— 轴心 = 脚底，
        /// 于是"贴地"就是 <c>y = 地面高度</c>，不必每张图量偏移。
        /// </summary>
        BottomCenter = 1,
    }

    /// <summary>
    /// 一条「目录 → 轴心」规则。
    /// <para>
    /// <see cref="PathContains"/> 是**资产路径片段**（如 <c>"Art/Sprites/Units/"</c>），
    /// 会被规范成两侧带 <c>/</c> 的形式再参与匹配（见 <see cref="NormalizedKey"/>），
    /// 因此写 <c>"Units"</c> 与 <c>"/Units/"</c> 等价、且不会把 <c>Units2</c> 一起匹配进来。
    /// </para>
    /// <para>同一条路径被多条规则命中时取 <b>最长 key</b> 那条（更具体的目录优先）。</para>
    /// </summary>
    [Serializable]
    public sealed class PixelArtDirectoryRule
    {
        /// <summary>资产路径片段（大小写敏感，与 <c>AssetDatabase</c> 路径一致）。</summary>
        [Tooltip("资产路径片段，如 Art/Sprites/Units/（两侧斜杠可省；大小写敏感）")]
        public string PathContains;

        /// <summary>命中该目录的纹理用哪种轴心。</summary>
        public PixelArtPivot Pivot = PixelArtPivot.Center;

        /// <summary>
        /// 规范化的匹配键：两侧补 <c>/</c>、反斜杠转正斜杠；空/空白返回 <c>null</c>（= 该规则无效）。
        /// <para><b>为什么补斜杠</b>：直接 <c>Contains("Units")</c> 会连 <c>Units2</c> 一起命中 ——
        /// 目录片段匹配必须带边界，否则一个拼写相近的目录会被悄悄改掉导入设置。</para>
        /// </summary>
        public string NormalizedKey()
        {
            var s = PathContains;
            if (string.IsNullOrEmpty(s)) return null;

            s = s.Replace('\\', '/').Trim();
            if (s.Length == 0) return null;

            if (s[0] != '/') s = "/" + s;
            if (s[s.Length - 1] != '/') s += "/";
            return s;
        }
    }

    /// <summary>
    /// 像素素材导入配置：规则表（作用域 + 轴心）+ 一套像素画纹理设置。
    /// <para>
    /// <b>总开关不在本资产里</b>：开关是机器本地的 EditorPrefs 菜单项（默认关），
    /// 本资产是工程内可提交的"规则与参数"。这样"规则进版本库、是否生效由本机决定"，
    /// 拉别人工程不会因为对方提交了配置就改掉你本机的导入设置。
    /// </para>
    /// <para>
    /// <b>规则表即作用域</b>：<see cref="Rules"/> 为空 ⇒ 即使开关打开也**一张纹理都不处理**
    /// —— 不存在"没写规则就全量处理"这种默认，那是会改坏别的工程的路径。
    /// </para>
    /// </summary>
    public sealed class PixelArtImportSettings : ScriptableObject
    {
        // ── 作用域与轴心 ─────────────────────────────────────────────────────
        /// <summary>
        /// 「目录 → 轴心」规则表。<b>同时是作用域</b>：只有命中某条规则的纹理才会被本工具改写。
        /// 空表 = 不处理任何纹理（刻意，见类型注释）。
        /// </summary>
        public List<PixelArtDirectoryRule> Rules = new List<PixelArtDirectoryRule>();

        // ── 纹理设置（像素画）────────────────────────────────────────────────
        /// <summary>
        /// 每单位像素数：决定"多少像素 = 1 世界单位"。
        /// <para>
        /// ⛔ 这里给 Unity 自己的默认值 <c>100</c>，**不预设**任何参考实现的数值
        ///（参考实现写死 16，那是"一格 16px 的素材"专属值）。用错的表现是角色比瓦片大/小 100 倍，
        /// 所以必须按素材显式配置。
        /// </para>
        /// </summary>
        [Tooltip("每单位像素数（按素材配；Unity 默认 100。填错会让贴图相对世界尺寸放大/缩小）")]
        public int PixelsPerUnit = 100;

        /// <summary>导入类型。像素素材一般是 Sprite；改它会让纹理不再是 Sprite。</summary>
        public TextureImporterType TextureType = TextureImporterType.Sprite;

        /// <summary>
        /// Sprite 导入模式。默认 <see cref="SpriteImportMode.Single"/>（一图一精灵）。
        /// <para><b>注意</b>：设成 <see cref="SpriteImportMode.Multiple"/>（图集/条带）时，
        /// 每张子精灵的轴心由它自己的 <c>SpriteMetaData</c> 决定，本工具的
        /// <see cref="PixelArtPivot"/> 只写"全局默认轴心"，不会逐子精灵改 —— 需要逐帧轴心时
        /// 由切图工具/图集配置负责。</para>
        /// </summary>
        public SpriteImportMode ImportMode = SpriteImportMode.Single;

        /// <summary>过滤模式。像素画默认 <c>Point</c>：双线性会把像素画糊成一团。</summary>
        public FilterMode Filter = FilterMode.Point;

        /// <summary>平铺模式。像素素材默认 <c>Clamp</c>，避免边缘采样串色。</summary>
        public TextureWrapMode WrapMode = TextureWrapMode.Clamp;

        /// <summary>是否生成 mipmap。像素画默认关（缩小时会糊）。</summary>
        public bool MipmapEnabled;

        /// <summary>透明通道是否按透明处理（默认开）。</summary>
        public bool AlphaIsTransparency = true;

        /// <summary>不压缩（默认开）。有损压缩会在像素画上产生色块。</summary>
        public bool Uncompressed = true;

        /// <summary>最大纹理尺寸。</summary>
        public int MaxTextureSize = 2048;

        /// <summary>是否按 sRGB 采样（默认开，与现有素材一致）。</summary>
        public bool SRGB = true;

        // ── 查询与自检 ───────────────────────────────────────────────────────

        /// <summary>
        /// 在规则表里找命中 <paramref name="normalizedAssetPath"/> 的规则；
        /// 同一路径被多条命中时取 <b>key 最长</b>的一条（更具体的目录优先）；没有命中返回 <c>null</c>。
        /// </summary>
        /// <param name="normalizedAssetPath">已规范成正斜杠的资产路径（<c>AssetPostprocessor.assetPath</c>）。</param>
        public PixelArtDirectoryRule MatchRule(string normalizedAssetPath)
        {
            if (string.IsNullOrEmpty(normalizedAssetPath) || Rules == null) return null;

            PixelArtDirectoryRule best = null;
            var bestLength = 0;

            foreach (var rule in Rules)
            {
                if (rule == null) continue;
                var key = rule.NormalizedKey();
                if (key == null) continue;
                if (!normalizedAssetPath.Contains(key)) continue;
                if (key.Length <= bestLength) continue;

                best = rule;
                bestLength = key.Length;
            }

            return best;
        }

        /// <summary>
        /// 自检：返回问题描述列表，**空列表 = 通过**。供菜单项在动手前把配置问题说清楚
        ///（坏配置的代价是"某几张图糊了"，那种症状不看这份清单很难归因）。
        /// </summary>
        public List<string> Validate()
        {
            var issues = new List<string>();

            if (Rules == null || Rules.Count == 0)
            {
                issues.Add("规则表为空：规则表同时是作用域，空表 ⇒ 开启后也不会处理任何纹理");
            }
            else
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var rule in Rules)
                {
                    if (rule == null)
                    {
                        issues.Add("规则表里有 null 项（已跳过）");
                        continue;
                    }

                    var key = rule.NormalizedKey();
                    if (key == null)
                    {
                        issues.Add("存在 PathContains 为空的规则（已跳过）");
                        continue;
                    }

                    if (!seen.Add(key)) issues.Add($"规则重复：{key}");
                }
            }

            if (PixelsPerUnit <= 0) issues.Add($"PixelsPerUnit={PixelsPerUnit} 非法（必须 > 0）");
            if (MaxTextureSize <= 0) issues.Add($"MaxTextureSize={MaxTextureSize} 非法（必须 > 0）");

            return issues;
        }

        /// <summary>调试与日志用的单行配置摘要。</summary>
        public string Describe()
        {
            var ruleCount = Rules == null ? 0 : Rules.Count;
            return $"规则 {ruleCount} 条 / PPU={PixelsPerUnit} / Filter={Filter} / " +
                   $"Wrap={WrapMode} / 压缩={(Uncompressed ? "关" : "开")} / mipmap={(MipmapEnabled ? "开" : "关")} / " +
                   $"alphaIsTransparency={(AlphaIsTransparency ? "开" : "关")} / maxSize={MaxTextureSize}";
        }
    }
}
