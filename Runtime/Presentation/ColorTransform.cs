// ─────────────────────────────────────────────────────────────────────────────
// CloverEngine · Runtime/Presentation/ColorTransform.cs
// 逐实例**颜色变换**入口（乘 + 加）：uGUI `Graphic` 与 `SpriteRenderer` 两路。
//
// 语义（与 `.sc` 颜色变换记录 `09` 等价，字段序 `add.r, add.g, add.b, alpha, mul.r, mul.g, mul.b`）：
//   out_rgb = clamp(src.rgb * mul + add)     out_a = clamp(src.a * alpha)
//   `255` = 1.0；alpha 只做乘算（没有加项）⇒ 本件把 alpha 并进 `mul.a`，`add.a` 不参与。
//
// 为什么需要它：顶点色（`Image.color` / `SpriteRenderer.color`）只能做**乘算**，且进顶点流时被
//   clamp 到 [0,1] ⇒ 乘不出"比图元更亮"或"换色相"的结果。`mul = 0` + `add = 白` 这类记录
//   （纯黑图元 ⇒ 结果纯白）用顶点色表达不出来（乘任何值仍是黑）。
//
// 用法：
//   `ColorTransform.Apply(img, ColorTransform.MulBytes(0, 0, 0), ColorTransform.AddBytes(255, 255, 255));`
//   `ColorTransform.Apply(spriteRenderer, mul, add);`
//   恒等（`mul = 白` 且 `add = 黑`）⇒ **不换材质**（uGUI 写回 `material = null`、精灵写回首次调用前记下的
//   材质）—— 没调用过本件的图元一个像素都不变。
//
// 边界（⛔ 别当万能药用）：
//   · 变换**只作用在图元自身**，顶点色仍乘在最后（那是调用方的逐实例 tint 通道，本件不占用它）；
//     外层遮罩 / 裁剪 / stencil（uGUI）与 `_RendererColor` / `_Flip`（精灵）都按各自默认语义保留。
//   · 颜色按 **8 位量化**入键（原版记录即 8 位）⇒ 同一种变换共用一份材质，⛔ 不走 `renderer.material`
//     （`Graphic.material` / `SpriteRenderer.sharedMaterial` 都是引用赋值，不克隆材质、不打断合批）。
//   · 写的是渲染器的材质**引用** ⇒ 会覆盖该渲染器上原有的自定义材质引用（如逐帧混合档材质）；
//     两者要叠加属于另一件事（需要一份同时带混合与颜色变换的着色器）。
//   · 着色器取不到（名字改了 / 打包裁剪）⇒ 记一次 Warn 并**保持默认材质**（表现退回"只有顶点色"）。
//     打包时若要走本条能力，需保证两份着色器进入包体（放进工程 `Resources` 或 Always Included Shaders）。
//   · 主线程使用；缓存是静态的、按变换键去重，条目数 = 用到的不同变换数（原版记录种类有限）。
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace CloverEngine
{
    /// <summary>
    /// 逐实例颜色变换（乘 + 加）入口：把 <c>mul</c> / <c>add</c> 写进渲染器的材质引用，
    /// 供 uGUI <see cref="Graphic"/> 与 <see cref="SpriteRenderer"/> 使用。
    /// <para>
    /// <b>数值口径</b>：<c>out_rgb = clamp(src.rgb * mul + add)</c>、<c>out_a = clamp(src.a * mul.a)</c>；
    /// 8 位原值到归一化用 <see cref="MulBytes"/> / <see cref="AddBytes"/>（<c>255</c> = 1.0，alpha 只做乘算）。
    /// </para>
    /// <para>
    /// <b>恒等</b>（<c>mul = (1,1,1,1)</c> 且 <c>add = (0,0,0,0)</c>，见 <see cref="IsIdentity"/>）
    /// ⇒ 不换材质，与未调用过本件逐像素一致。
    /// </para>
    /// </summary>
    public static class ColorTransform
    {
        /// <summary>精灵侧着色器名（见 <c>Runtime/Presentation/Shaders/ColorTransformSprite.shader</c>）。</summary>
        public const string SpriteShaderName = "Clover/ColorTransform/Sprite";

        /// <summary>uGUI 侧着色器名（见 <c>Runtime/Presentation/Shaders/ColorTransformUi.shader</c>）。</summary>
        public const string UiShaderName = "Clover/ColorTransform/UI";

        /// <summary>日志 tag（便于按 <c>[ColorTransform]</c> 检索）。</summary>
        private const string Tag = "ColorTransform";

        /// <summary>恒等的乘色（<c>1.0</c>）。</summary>
        public static readonly Color IdentityMul = Color.white;

        /// <summary>恒等的加色（<c>0</c>）。</summary>
        public static readonly Color IdentityAdd = new Color(0f, 0f, 0f, 0f);

        private static readonly Dictionary<Key, Material> SpriteMats = new Dictionary<Key, Material>();
        private static readonly Dictionary<Key, Material> UiMats = new Dictionary<Key, Material>();

        /// <summary>精灵渲染器**首次被本件写材质之前**的材质（恒等时写回它）。</summary>
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SpriteRenderer, Original> Originals =
            new System.Runtime.CompilerServices.ConditionalWeakTable<SpriteRenderer, Original>();

        private static Shader _spriteShader;
        private static Shader _uiShader;
        private static bool _spriteResolved;
        private static bool _uiResolved;

        /// <summary>一份 8 位颜色变换（键 = 原值，等价比较 ⇒ 同一变换复用同一份材质）。</summary>
        private readonly struct Key : IEquatable<Key>
        {
            public readonly byte R, G, B, A, AddR, AddG, AddB;

            public Key(byte r, byte g, byte b, byte a, byte addR, byte addG, byte addB)
            {
                R = r; G = g; B = b; A = a;
                AddR = addR; AddG = addG; AddB = addB;
            }

            /// <summary>恒等：乘色全满、加色全零。</summary>
            public bool IsIdentity => R == 255 && G == 255 && B == 255 && A == 255 && AddR == 0 && AddG == 0 && AddB == 0;

            public bool Equals(Key other) =>
                R == other.R && G == other.G && B == other.B && A == other.A &&
                AddR == other.AddR && AddG == other.AddG && AddB == other.AddB;

            public override bool Equals(object obj) => obj is Key other && Equals(other);

            public override int GetHashCode() =>
                (R << 24) | (G << 16) | (B << 8) | A ^ (AddR << 16) ^ (AddG << 8) ^ AddB;
        }

        /// <summary>CWT 值类型（<see cref="System.Runtime.CompilerServices.ConditionalWeakTable{TKey,TValue}"/> 要类）。</summary>
        private sealed class Original
        {
            public Material Material;
        }

        // ── 8 位原值 → 归一化颜色 ────────────────────────────────────────────────

        /// <summary>
        /// 乘色：<paramref name="r"/> / <paramref name="g"/> / <paramref name="b"/> 是 8 位原值，
        /// <paramref name="alpha"/> 是 alpha 缩放（<c>255</c> = 1.0，默认 255 = 原样），一并放进返回值的 <c>a</c>。
        /// </summary>
        public static Color MulBytes(byte r, byte g, byte b, byte alpha = 255) =>
            new Color(r / 255f, g / 255f, b / 255f, alpha / 255f);

        /// <summary>加色：<paramref name="r"/> / <paramref name="g"/> / <paramref name="b"/> 是 8 位原值（alpha 固定 0）。</summary>
        public static Color AddBytes(byte r, byte g, byte b) => new Color(r / 255f, g / 255f, b / 255f, 0f);

        /// <summary>是否恒等（<paramref name="mul"/> 全满且 <paramref name="add"/> 全零，按 8 位量化判定）。</summary>
        public static bool IsIdentity(Color mul, Color add) => KeyOf(mul, add).IsIdentity;

        /// <summary>已缓存的材质份数（两路合计；供自检 / 断言用）。</summary>
        public static int CachedMaterialCount => SpriteMats.Count + UiMats.Count;

        /// <summary>着色器是否已解析到（两路都取到才为 true；供调用方判断本条能力是否可用）。</summary>
        public static bool ShadersAvailable => ResolveShader(false) != null && ResolveShader(true) != null;

        // ── 入口 ───────────────────────────────────────────────────────────────

        /// <summary>
        /// 给 uGUI 图元套逐实例颜色变换。
        /// <para>
        /// 恒等 ⇒ <c>graphic.material = null</c>（回到 Canvas 默认 UI 材质）；
        /// 否则指向按变换键共享的一份材质（⛔ 不逐实例克隆）。
        /// </para>
        /// </summary>
        public static void Apply(Graphic graphic, Color mul, Color add)
        {
            if (graphic == null) return;

            var key = KeyOf(mul, add);
            if (key.IsIdentity)
            {
                if (graphic.material != null) graphic.material = null;   // 恒等 = 不动材质
                return;
            }

            var mat = MaterialFor(key, true);
            if (mat == null) return;                                      // 着色器取不到：保持默认材质（已留痕）
            if (graphic.material != mat) graphic.material = mat;
        }

        /// <summary>
        /// 给精灵渲染器套逐实例颜色变换。
        /// <para>
        /// 恒等 ⇒ 写回**首次被本件改材质之前**的那份材质（未记过则 <c>null</c>）；
        /// 否则指向按变换键共享的一份材质。
        /// </para>
        /// </summary>
        public static void Apply(SpriteRenderer renderer, Color mul, Color add)
        {
            if (renderer == null) return;

            var original = OriginalOf(renderer);
            var key = KeyOf(mul, add);
            if (key.IsIdentity)
            {
                if (renderer.sharedMaterial != original.Material) renderer.sharedMaterial = original.Material;
                return;
            }

            var mat = MaterialFor(key, false);
            if (mat == null) return;                                      // 着色器取不到：保持原材质（已留痕）
            if (renderer.sharedMaterial != mat) renderer.sharedMaterial = mat;
        }

        // ── 内部 ───────────────────────────────────────────────────────────────

        private static Original OriginalOf(SpriteRenderer renderer)
        {
            if (Originals.TryGetValue(renderer, out var original)) return original;
            original = new Original { Material = renderer.sharedMaterial };
            Originals.Add(renderer, original);
            return original;
        }

        private static Key KeyOf(Color mul, Color add) => new Key(
            ToByte(mul.r), ToByte(mul.g), ToByte(mul.b), ToByte(mul.a),
            ToByte(add.r), ToByte(add.g), ToByte(add.b));

        private static byte ToByte(float v) => (byte)Mathf.Clamp(Mathf.Round(v * 255f), 0f, 255f);

        private static Material MaterialFor(Key key, bool ui)
        {
            var cache = ui ? UiMats : SpriteMats;
            // 缓存有效性检查：`Resources.UnloadUnusedAssets` 会销毁未被引用的运行时材质，
            // 拿到"假 null"时直接返回会让颜色变换整段消失且零报错 ⇒ 失效即重造。
            if (cache.TryGetValue(key, out var cached) && cached != null && cached.shader != null) return cached;

            var shader = ResolveShader(ui);
            if (shader == null) return null;

            var mat = new Material(shader) { name = (ui ? "CloverUiXform_" : "CloverSpriteXform_") + key.GetHashCode() };
            mat.SetColor("_ColorMul", new Color(key.R / 255f, key.G / 255f, key.B / 255f, key.A / 255f));
            mat.SetColor("_ColorAdd", new Color(key.AddR / 255f, key.AddG / 255f, key.AddB / 255f, 0f));
            cache[key] = mat;
            return mat;
        }

        private static Shader ResolveShader(bool ui)
        {
            if (ui)
            {
                if (_uiResolved) return _uiShader;
                _uiResolved = true;
                _uiShader = Shader.Find(UiShaderName);
                if (_uiShader == null)
                {
                    // 非预期分支必须留痕：取不到 ⇒ uGUI 侧的加色项做不出来（表现退回只有顶点色）。
                    Game.Logger?.Warn(Tag, $"取不到着色器 {UiShaderName} ⇒ uGUI 图元的颜色变换不可用" +
                                           "（保持默认材质；打包时需让该着色器进入包体）");
                }
                return _uiShader;
            }

            if (_spriteResolved) return _spriteShader;
            _spriteResolved = true;
            _spriteShader = Shader.Find(SpriteShaderName);
            if (_spriteShader == null)
            {
                // 非预期分支必须留痕：取不到 ⇒ 精灵侧的加色项做不出来（表现退回只有顶点色）。
                Game.Logger?.Warn(Tag, $"取不到着色器 {SpriteShaderName} ⇒ 精灵件的颜色变换不可用" +
                                       "（保持原材质；打包时需让该着色器进入包体）");
            }
            return _spriteShader;
        }
    }
}
