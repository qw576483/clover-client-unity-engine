// ─────────────────────────────────────────────────────────────────────────────
// CloverEngine · Editor/PixelArtImportPostprocessor.cs
// 像素素材导入后处理器：按配置资产（Editor/PixelArtImportSettings.cs）给纹理配好
// PPU / FilterMode.Point / 不压缩 / 无 mipmap / alphaIsTransparency / 轴心。
//
// 出处：clover-project-super-mario `client/Assets/Editor/SpriteImportPostprocessor.cs:20-64`。
//   ⛔ 与出处**刻意不同**的两点（下沉的必要条件）：
//   ① **没有写死的目录**：出处用 `path.Contains("/Resources/Sprites/")` 当作用域，
//      下沉后作用域 = 配置资产的规则表（`/Resources/Sprites/`、`Mario/Enemies/Items` 全去掉）；
//   ② **默认不生效**：出处"文件存在即生效"；这里要同时满足 ① 总开关打开（EditorPrefs，默认关）
//      ② 已选中配置资产 ③ 该纹理命中某条规则 —— 三者缺一就不碰。理由见类型注释。
//
// ★ 必须保留的坑（出处 42-62 行的注释，实测踩过）：
//   `spriteAlignment` / `spritePivot` **不在** TextureImporter 上，而在 TextureImporterSettings 里
//   —— 直接写 `importer.spriteAlignment` **编译不过**（不是"不生效"，是编译错误）。
//   正确写法是 ReadTextureSettings → 改 → SetTextureSettings 这套**往返读写**。
//
// 日志：Editor 侧用 Debug.Log/LogWarning/LogError。原因：后处理器可能在 Game.Launch 之前、
//   域重载之后再跑，`Game.Logger` 此刻未必是落盘 Logger（引擎在未 Launch 时是 ConsoleLogger.Instance，
//   见 Runtime/Core/Game.cs:141-149）；Editor 工具的读者就是编辑器 Console，直接用 Debug 更直接。
//   ⛔ Runtime 侧不适用这条（那边一律 Game.Logger）。
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.IO;
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace CloverEngine.Editor
{
    /// <summary>
    /// 像素素材导入规范（<see cref="AssetPostprocessor"/> 实现）。
    /// <para>
    /// <b>默认关闭</b>：不配置就**不会改动任何纹理的导入设置** —— 后处理器是跨工程全局生效的，
    /// 默认生效等于"打开 A 工程就把 B 工程的素材导入设置也改了"，且症状（图糊了 / 人物浮空）极难归因。
    /// </para>
    /// <para>
    /// <b>三步开关（全开才动手）</b>：
    /// ① 菜单 <see cref="MenuEnabled"/> 打开总开关（EditorPrefs，**按工程路径隔离**，默认关）；
    /// ② 菜单 <see cref="MenuPickSettings"/> 创建/选中一份 <see cref="PixelArtImportSettings"/>；
    /// ③ 该纹理路径命中配置里某条规则（规则表即作用域）。
    /// </para>
    /// <para>
    /// 手工重导已有素材：选中它们 → 菜单 <see cref="MenuReimportSelection"/>
    ///（导入后处理器只在"导入时"触发，已经导入过的纹理不会自己回头再走一遍）。
    /// </para>
    /// </summary>
    public sealed class PixelArtImportPostprocessor : AssetPostprocessor
    {
        // ── 菜单路径（逐字）──────────────────────────────────────────────────
        /// <summary>总开关（勾选项，EditorPrefs，按工程隔离，默认关）。</summary>
        public const string MenuEnabled = "Clover/像素素材导入/启用（按配置资产处理导入的纹理）";

        /// <summary>创建或选择配置资产（选中后即登记，后处理器据此取规则）。</summary>
        public const string MenuPickSettings = "Clover/像素素材导入/创建或选择配置资产…";

        /// <summary>打印当前配置摘要与自检结果（只读，不改任何东西）。</summary>
        public const string MenuReport = "Clover/像素素材导入/打印配置摘要与自检";

        /// <summary>用当前配置重导选中纹理（导入时钩子不会回溯已导入的素材）。</summary>
        public const string MenuReimportSelection = "Clover/像素素材导入/用当前配置重导选中纹理";

        // ── EditorPrefs（机器全局 ⇒ key 必须按工程隔离）───────────────────────
        private const string EnabledKeyPrefix = "CloverEngine.Editor.PixelArtImport.Enabled.";
        private const string SettingsKeyPrefix = "CloverEngine.Editor.PixelArtImport.SettingsPath.";

        /// <summary>「取不到配置资产」这类降噪用的一次性提示（每编辑器会话一次）。</summary>
        private const string SessionMissingSettingsKey = "CloverEngine.Editor.PixelArtImport.MissingSettingsWarned";

        // ── 导入钩子 ─────────────────────────────────────────────────────────

        private void OnPreprocessTexture()
        {
            var settings = ActiveSettings;
            if (settings == null) return;                       // ★ 默认不生效：未启用 / 未配置

            var path = Normalize(assetPath);
            var rule = settings.MatchRule(path);
            if (rule == null) return;                           // ★ 不在任何规则目录里 ⇒ 一律不碰

            var importer = assetImporter as TextureImporter;
            if (importer == null) return;                       // 非预期分支，但不留噪声：非纹理不会进本钩子

            Apply(importer, settings, rule, path);

            // ★ 切图（可选、默认无规则 ⇒ 行为与加它之前完全一致）：命中切图规则 ⇒ 覆盖上面的 ImportMode
            //   并写逐帧矩形。顺序**必须在 Apply 之后**（Apply 会按目录规则设 `spriteImportMode`）。
            var slicing = settings.MatchSlicingRule(path);
            if (slicing != null) ApplySlicing(importer, slicing, path);
        }

        /// <summary>
        /// 把一套像素画设置写到 importer 上。轴心走 <see cref="TextureImporterSettings"/> 往返
        ///（见文件头 ★）。
        /// </summary>
        private static void Apply(TextureImporter importer, PixelArtImportSettings settings,
            PixelArtDirectoryRule rule, string assetPath)
        {
            importer.textureType = settings.TextureType;
            // 导入模式**逐目录解析**：规则没勾"覆盖"就用全局值（= 加本字段之前的行为）。
            // ⛔ 不要在这里自己写三元判断 —— 覆盖逻辑只在 ResolveImportMode 一处（见其注释）。
            importer.spriteImportMode = rule.ResolveImportMode(settings.ImportMode);
            importer.spritePixelsPerUnit = settings.PixelsPerUnit;
            importer.filterMode = settings.Filter;
            importer.wrapMode = settings.WrapMode;
            importer.mipmapEnabled = settings.MipmapEnabled;
            importer.alphaIsTransparency = settings.AlphaIsTransparency;
            importer.textureCompression = settings.Uncompressed
                ? TextureImporterCompression.Uncompressed
                : TextureImporterCompression.Compressed;
            importer.maxTextureSize = settings.MaxTextureSize;
            importer.sRGBTexture = settings.SRGB;

            // ★ 轴心：`spriteAlignment` / `spritePivot` 只存在于 TextureImporterSettings，
            //   必须 ReadTextureSettings → 改 → SetTextureSettings 往返才生效。
            var textureSettings = new TextureImporterSettings();
            importer.ReadTextureSettings(textureSettings);

            if (rule.Pivot == PixelArtPivot.BottomCenter)
            {
                textureSettings.spriteAlignment = (int)SpriteAlignment.Custom;
                textureSettings.spritePivot = new Vector2(0.5f, 0f);    // 底部居中 = 脚底
            }
            else
            {
                textureSettings.spriteAlignment = (int)SpriteAlignment.Center;
            }

            importer.SetTextureSettings(textureSettings);

            // 只在真正改过的资产上留痕（导入是批量动作，但每条都属于"非预期的自动改写"，需要可追溯）
            Debug.Log($"[Clover][PixelArt] {assetPath} ⇒ {rule.Pivot} / PPU={settings.PixelsPerUnit} / " +
                      $"ImportMode={importer.spriteImportMode}" +
                      $"({(rule.OverrideImportMode ? "逐目录覆盖" : "沿用全局")}) / " +
                      $"Point / 不压缩 / 无 mipmap（规则 key={rule.NormalizedKey()}）");
        }

        // ── 配置来源 ─────────────────────────────────────────────────────────

        /// <summary>
        /// 当前生效的配置资产；**未启用 / 未选中 / 资产已失效** 均返回 <c>null</c>
        ///（= 后处理器什么都不做 —— 这是"默认不生效"的落地口径）。
        /// </summary>
        // ── 切图（可选规则；配置结构见 PixelArtSlicingRule）──────────────────────

        /// <summary>
        /// 按切图规则把一张图切成多张子精灵 —— **引擎只算"要切成什么样"，落地交给注册的切图器**。
        /// <para>⚠️ **为什么是挂钩而不是引擎自己切**：真正写子精灵登记要靠官方
        /// `UnityEditor.U2D.Sprites.ISpriteEditorDataProvider`，那个命名空间属 **U2D 包程序集**；
        /// 引擎 Editor 程序集只引用引擎自己的程序集，加那条引用会让"没装 2D Sprite 包的工程"连带编译失败。
        /// 旧 API `TextureImporter.spritesheet` 在 Unity 6 **已被移除**（只剩 `CS0618` 警告、写进去是空操作
        /// ⇒ 典型的静默失效），故也不能用。⇒ 由**工程侧**（它的 editor 程序集拿得到该包）实现
        /// <see cref="IPixelArtSlicer"/> 并注册；引擎负责规则、算矩形、以及在**没注册时明确告警**（⛔ 不静默）。</para>
        /// <para>契约：① 配置错误 / 空结果 ⇒ 限频告警 + 跳过；② 未注册切图器 ⇒ **限频告警 + 保持原导入模式**
        /// （⛔ 不许悄悄把 `spriteImportMode` 改成 Multiple 却不切）；③ 不抛异常。</para>
        /// </summary>
        private static void ApplySlicing(TextureImporter importer, PixelArtSlicingRule rule, string assetPath)
        {
            var bad = rule.Validate();
            if (bad != null)
            {
                LogThrottle.WarnOnce("PixelArtImport", "slicing.bad:" + assetPath, "切图规则配置错误，已跳过：" + bad);
                return;
            }

            if (rule.Border != Vector4.zero) importer.spriteBorder = rule.Border;
            if (rule.MaxTextureSizeOverride > 0) importer.maxTextureSize = rule.MaxTextureSizeOverride;

            if (rule.Mode == PixelArtSlicingMode.WholeTexture)
            {
                importer.spriteImportMode = SpriteImportMode.Single;
                Debug.Log($"[Clover][PixelArt] {assetPath} ⇒ 整幅不切（maxTextureSize={importer.maxTextureSize}）");
                return;
            }

            var items = BuildSliceItems(rule, assetPath);
            if (items == null || items.Count == 0)
            {
                LogThrottle.WarnOnce("PixelArtImport", "slicing.empty:" + assetPath,
                    "切图规则没产出任何子精灵，已跳过（保持原导入模式）：" + assetPath);
                return;
            }

            var slicer = Slicer;
            if (slicer == null)
            {
                LogThrottle.WarnOnce("PixelArtImport", "slicing.noslicer:" + assetPath,
                    $"命中切图规则 `{rule.PathContains}` 但**本工程没有注册切图器** ⇒ 未切图（仍是整幅）。" +
                    "工程侧实现 `CloverEngine.Editor.IPixelArtSlicer` 并调 " +
                    "`PixelArtImportPostprocessor.RegisterSlicer(...)` 即可（见引擎索引文档）");
                return;
            }

            var request = new PixelArtSliceRequest
            {
                AssetPath = assetPath,
                Rule = rule,
                Items = items
            };

            try
            {
                if (!slicer.TryApply(importer, request))
                {
                    LogThrottle.WarnOnce("PixelArtImport", "slicing.fail:" + assetPath,
                        "切图器返回失败 ⇒ 未切图（保持原导入模式）：" + assetPath);
                    return;
                }
            }
            catch (Exception e)
            {
                LogThrottle.WarnOnce("PixelArtImport", "slicing.throw:" + assetPath,
                    $"切图器抛异常（已捕获，导入继续）：{e.GetType().Name}: {e.Message}");
                return;
            }

            Debug.Log($"[Clover][PixelArt] {assetPath} ⇒ 已按 {items.Count} 张子精灵切分"
                      + $"（{rule.Mode}，key={rule.PathContains}）");
        }

        /// <summary>按规则算出「要切成哪几块」（名字 + 矩形；顺序 = 规则的遍历顺序）。</summary>
        private static List<PixelArtSliceItem> BuildSliceItems(PixelArtSlicingRule rule, string assetPath)
        {
            var prefix = string.IsNullOrEmpty(rule.NamePrefix)
                ? System.IO.Path.GetFileNameWithoutExtension(assetPath)
                : rule.NamePrefix;

            var items = new List<PixelArtSliceItem>();

            if (rule.Mode == PixelArtSlicingMode.ExplicitRects)
            {
                var list = rule.Rects;
                if (list == null) return items;

                for (var i = 0; i < list.Count; i++)
                    items.Add(new PixelArtSliceItem { Name = prefix + "_" + i, Rect = list[i] });
                return items;
            }

            for (var row = 0; row < rule.Rows; row++)
            {
                for (var col = 0; col < rule.Columns; col++)
                {
                    items.Add(new PixelArtSliceItem
                    {
                        Name = prefix + "_" + row + "_" + col,
                        Rect = new Rect(col * rule.CellWidth, row * rule.CellHeight, rule.CellWidth, rule.CellHeight)
                    });
                }
            }

            return items;
        }

        // ── 切图器注册（工程侧实现，见 IPixelArtSlicer）──────────────────────────

        private static IPixelArtSlicer Slicer;

        /// <summary>
        /// 工程侧注册切图器（幂等：后注册的覆盖先注册的）。
        /// <para>典型用法：工程 editor 程序集里的 `[InitializeOnLoadMethod]` 调一次；
        /// ⛔ 不注册也不会静默 —— 命中切图规则时会明确告警"未注册切图器"。</para>
        /// </summary>
        public static void RegisterSlicer(IPixelArtSlicer slicer) => Slicer = slicer;

        public static PixelArtImportSettings ActiveSettings
        {
            get
            {
                if (!Enabled) return null;

                var path = SettingsPath;
                if (string.IsNullOrEmpty(path)) return null;    // 没选过 = 未配置

                var asset = AssetDatabase.LoadAssetAtPath<PixelArtImportSettings>(path);
                if (asset == null && !SessionState.GetBool(SessionMissingSettingsKey, false))
                {
                    // 一次性提示（每会话一次）：配置资产被删/被移走时，开关看着是开的却毫无效果，
                    // 这种"静默失效"必须说出来。
                    SessionState.SetBool(SessionMissingSettingsKey, true);
                    Debug.LogWarning($"[Clover][PixelArt] 配置资产取不到：{path}。" +
                                     $"用菜单 `{MenuPickSettings}` 重新创建/选择；在修好之前不会处理任何纹理。");
                }

                return asset;
            }
        }

        private static bool Enabled => EditorPrefs.GetBool(EnabledKey, false);

        private static string SettingsPath => EditorPrefs.GetString(SettingsKeyPrefix + ProjectTag(), string.Empty);

        private static string EnabledKey => EnabledKeyPrefix + ProjectTag();

        // ── 菜单 ─────────────────────────────────────────────────────────────

        [MenuItem(MenuEnabled, false, 210)]
        private static void ToggleEnabled()
        {
            var next = !Enabled;
            EditorPrefs.SetBool(EnabledKey, next);

            if (next && string.IsNullOrEmpty(SettingsPath))
            {
                Debug.LogWarning($"[Clover][PixelArt] 已打开总开关，但还没选配置资产 ⇒ 仍不会处理任何纹理。" +
                                 $"请用菜单 `{MenuPickSettings}` 创建/选择一份配置。");
                return;
            }

            var summary = ActiveSettings?.Describe();
            Debug.Log($"[Clover][PixelArt] 像素素材导入后处理器：{(next ? "已启用" : "已关闭")}" +
                      (next ? $"（配置：{SettingsPath}；{summary}）" : "（已导入过的纹理不会回溯，需手动重导）"));
        }

        [MenuItem(MenuEnabled, true)]
        private static bool ToggleEnabledChecked()
        {
            Menu.SetChecked(MenuEnabled, Enabled);
            return true;
        }

        [MenuItem(MenuPickSettings, false, 211)]
        private static void PickOrCreateSettings()
        {
            var current = SettingsPath;
            var startDir = "Assets";
            if (!string.IsNullOrEmpty(current))
            {
                var dir = Path.GetDirectoryName(current);
                if (!string.IsNullOrEmpty(dir)) startDir = dir.Replace('\\', '/');
            }

            var path = EditorUtility.SaveFilePanelInProject(
                "像素素材导入配置（默认关闭）", "PixelArtImportSettings", "asset",
                "选一个空目录放置配置文件；本工具只处理配置里规则命中的目录", startDir);

            if (string.IsNullOrEmpty(path)) return;             // 用户取消：什么都不改

            var asset = AssetDatabase.LoadAssetAtPath<PixelArtImportSettings>(path);
            if (asset == null)
            {
                asset = ScriptableObject.CreateInstance<PixelArtImportSettings>();
                AssetDatabase.CreateAsset(asset, path);
                Debug.Log($"[Clover][PixelArt] 已创建配置资产：{path}（默认规则表为空 ⇒ 什么都不处理）");
            }

            EditorPrefs.SetString(SettingsKeyPrefix + ProjectTag(), path);
            AssetDatabase.SaveAssets();
            Selection.activeObject = asset;
            EditorGUIUtility.PingObject(asset);

            Debug.Log($"[Clover][PixelArt] 已登记配置资产：{path}；总开关当前" +
                      $"{(Enabled ? "已开" : "未开")}（菜单 `{MenuEnabled}`）。" +
                      "规则表为空时不会处理任何纹理，请先在 Inspector 里加规则。");
        }

        [MenuItem(MenuReport, false, 212)]
        private static void PrintReport()
        {
            var settings = ActiveSettings;
            if (settings == null)
            {
                Debug.LogWarning($"[Clover][PixelArt] 当前未生效：总开关={(Enabled ? "开" : "关")} / " +
                                 $"配置资产={(string.IsNullOrEmpty(SettingsPath) ? "(未选)" : SettingsPath)}。");
                return;
            }

            var issues = settings.Validate();
            var text = $"[Clover][PixelArt] 配置自检：{(issues.Count == 0 ? "通过" : $"{issues.Count} 个问题")}\n" +
                       $"  资产：{SettingsPath}\n  参数：{settings.Describe()}";
            foreach (var issue in issues) text += $"\n  [问题] {issue}";
            if (issues.Count == 0) Debug.Log(text);
            else Debug.LogWarning(text);
        }

        [MenuItem(MenuReimportSelection, false, 213)]
        private static void ReimportSelection()
        {
            var settings = ActiveSettings;
            if (settings == null)
            {
                Debug.LogWarning($"[Clover][PixelArt] 未启用或未配置，未重导任何纹理" +
                                 $"（用菜单 `{MenuEnabled}` 与 `{MenuPickSettings}` 配好后重试）。");
                return;
            }

            var textures = 0;
            var others = 0;
            var notMatched = 0;

            foreach (var guid in Selection.assetGUIDs)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path) || path.EndsWith(".meta", StringComparison.Ordinal)) continue;

                if (!(AssetImporter.GetAtPath(path) is TextureImporter))
                {
                    others++;
                    continue;
                }

                if (settings.MatchRule(Normalize(path)) == null)
                {
                    notMatched++;                               // 不在规则目录里：不重导（避免白跑一遍导入）
                    continue;
                }

                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                textures++;
            }

            Debug.Log($"[Clover][PixelArt] 重导完成：纹理 {textures} 个；" +
                      $"选中项里不在规则目录内 {notMatched} 个、非纹理 {others} 个（均未改动）。");
        }

        // ── 内部 ─────────────────────────────────────────────────────────────

        private static string Normalize(string path)
            => string.IsNullOrEmpty(path) ? string.Empty : path.Replace('\\', '/');

        /// <summary>
        /// 工程标识：EditorPrefs 是**机器全局**的（所有工程共用一份），必须按工程路径隔离，
        /// 否则在 A 工程打开开关会连带把 B 工程的像素导入改写也打开。
        /// FNV-1a 32 位（同一工程恒定；⚠️ ⛔ 不用 <c>string.GetHashCode()</c> —— 它在
        /// .NET Core 下**每进程随机**，用它当 key 等于每次重启编辑器都换一套开关）。
        /// </summary>
        private static string ProjectTag()
        {
            var root = Path.GetDirectoryName(Application.dataPath) ?? Application.dataPath;
            unchecked
            {
                var hash = 2166136261u;
                foreach (var c in root)
                {
                    hash ^= c;
                    hash *= 16777619u;
                }

                return hash.ToString("x8");
            }
        }
    }
}
