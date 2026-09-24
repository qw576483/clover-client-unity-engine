// ─────────────────────────────────────────────────────────────────────────────
// CloverEngine.Editor · PixelArtSlicing —— 切图规则的**请求载体 + 工程侧切图器契约**
//
// 【为什么是"契约 + 注册"而不是引擎直接切】
//   真正写子精灵登记要靠 `UnityEditor.U2D.Sprites.ISpriteEditorDataProvider`，该命名空间属
//   **U2D 包程序集**；引擎 Editor 程序集（`CloverEngine.Editor.asmdef`）只引用引擎自己的程序集，
//   加那条引用会让"没装 2D Sprite 包的工程"连带编译失败。
//   旧 API `TextureImporter.spritesheet`（`SpriteMetaData`）在 Unity 6 **已被移除**
//   （只剩 `CS0618` 警告、写进去是空操作 = 典型静默失效），故也不可用。
//   ⇒ 分工：**引擎出规则与矩形**（`PixelArtSlicingRule`），**工程出适配器**（实现本文件的接口并注册）。
//
// 【用法（工程侧，editor 程序集）】
//   [InitializeOnLoadMethod]
//   static void Wire() => PixelArtImportPostprocessor.RegisterSlicer(new MySlicer());
//   class MySlicer : CloverEngine.Editor.IPixelArtSlicer { public bool TryApply(...) { ... } }
//
// 【已知边界】⛔ 引擎不做"已切好就不重写"的判定 —— 那是切图器的责任（保住 `spriteID`/fileID，
//   否则已引用这些子精灵的 prefab 会断引用）。引擎只保证：算好矩形、失败/未注册都**明确告警**。
// ─────────────────────────────────────────────────────────────────────────────

using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace CloverEngine.Editor
{
    /// <summary>要切出的一张子精灵（名字 + 矩形；矩形**原点左上、y 向下**，与 Unity 精灵编辑器同口径）。</summary>
    public struct PixelArtSliceItem
    {
        public string Name;
        public Rect Rect;
    }

    /// <summary>一次切图的请求：命中哪条规则、要切成哪几块（矩形的唯一来源是引擎，切图器只落地）。</summary>
    public sealed class PixelArtSliceRequest
    {
        /// <summary>规范化后的资源路径（`Assets/...`）。</summary>
        public string AssetPath;

        /// <summary>命中的切图规则（边框 / `maxTextureSize` 等已由引擎先写进 importer）。</summary>
        public PixelArtSlicingRule Rule;

        /// <summary>要切出的子精灵（顺序 = 规则的遍历顺序；⛔ 切图器不要重排）。</summary>
        public List<PixelArtSliceItem> Items;
    }

    /// <summary>
    /// **工程侧切图器**：把 <see cref="PixelArtSliceRequest"/> 落地到 `TextureImporter`
    /// （通常实现里用 `ISpriteEditorDataProvider`）。
    /// <para>约定：① 返回 true = 已切好；false = 未切（引擎会告警并保持原导入模式，⛔ 不是静默）；
    /// ② 抛异常由引擎捕获并告警（不影响本次导入的其余设置）；③ **已切好就别重写**（保 `spriteID` 稳定）。</para>
    /// </summary>
    public interface IPixelArtSlicer
    {
        /// <summary>把请求落地；返回是否成功切好。</summary>
        bool TryApply(TextureImporter importer, PixelArtSliceRequest request);
    }
}
