using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace CloverEngine.Tests
{
    /// <summary>
    /// <see cref="ColorTransform"/> 的 EditMode 用例（不依赖业务工程、不依赖真实链路）：
    /// ① 8 位原值 → 归一化颜色的换算与恒等判定；② 恒等**不换材质**（"不影响既有件"的契约）；
    /// ③ 非恒等时挂上按变换键共享的材质、属性即 `mul` / `add`；④ **真渲染读数**：
    /// `mul = 0` + `add = 白` 的纯黑图元必须出纯白像素，而恒等的着色器输出必须与默认着色器逐像素一致。
    /// </summary>
    /// <remarks>
    /// 渲染用一条离屏相机 + `RenderTexture`：物体放 31 层并只让该相机看 31 层，
    /// 同时把相机放到世界远处 ⇒ 与工程里已打开的场景内容互不干扰。
    /// </remarks>
    public class ColorTransformTests
    {
        private const int Layer = 31;
        private const int RtSize = 8;
        private static readonly Vector3 Far = new Vector3(10000f, 10000f, 0f);

        private readonly List<Object> _created = new List<Object>();
        private RenderTexture _rt;
        private Camera _cam;

        [TearDown]
        public void TearDown()
        {
            foreach (var o in _created)
            {
                if (o != null) Object.DestroyImmediate(o);
            }
            _created.Clear();
            if (_rt != null) { _rt.Release(); Object.DestroyImmediate(_rt); _rt = null; }
        }

        private T Track<T>(T o) where T : Object
        {
            _created.Add(o);
            return o;
        }

        // ── 数值换算 ───────────────────────────────────────────────────────────

        [Test]
        public void MulBytes_And_AddBytes_MapEightBitValuesToNormalizedColor()
        {
            var mul = ColorTransform.MulBytes(0, 128, 255, 77);
            Assert.AreEqual(0f, mul.r, 1e-4f);
            Assert.AreEqual(128f / 255f, mul.g, 1e-4f);
            Assert.AreEqual(1f, mul.b, 1e-4f);
            Assert.AreEqual(77f / 255f, mul.a, 1e-4f, "alpha 是乘算缩放，放在 mul.a");

            var add = ColorTransform.AddBytes(255, 255, 255);
            Assert.AreEqual(1f, add.r, 1e-4f);
            Assert.AreEqual(1f, add.g, 1e-4f);
            Assert.AreEqual(1f, add.b, 1e-4f);
            Assert.AreEqual(0f, add.a, 1e-4f, "alpha 没有加项 ⇒ add.a 固定 0");
        }

        [Test]
        public void IsIdentity_TrueOnlyForFullMulAndZeroAdd()
        {
            Assert.IsTrue(ColorTransform.IsIdentity(Color.white, new Color(0f, 0f, 0f, 0f)));
            Assert.IsFalse(ColorTransform.IsIdentity(new Color(0.5f, 1f, 1f, 1f), new Color(0f, 0f, 0f, 0f)),
                "mul 不满 ⇒ 非恒等");
            Assert.IsTrue(ColorTransform.IsIdentity(Color.white, new Color(0f, 0f, 0f, 1f)),
                "add.a 不参与（alpha 只有乘算）⇒ 只由 add.rgb 决定");
            Assert.IsFalse(ColorTransform.IsIdentity(Color.white, new Color(0.5f, 0f, 0f, 0f)),
                "add.rgb 非零 ⇒ 非恒等");
            Assert.IsTrue(ColorTransform.IsIdentity(Color.white, new Color(0.001f, 0f, 0f, 0f)),
                "按 8 位量化判定：0.001 * 255 = 0.255 四舍五入到 0 ⇒ 仍是恒等");
        }

        // ── 材质挂载 ───────────────────────────────────────────────────────────

        [Test]
        public void ApplyUi_Identity_KeepsDefaultMaterial()
        {
            var untouched = NewImage(out _);
            var img = NewImage(out _);

            ColorTransform.Apply(img, ColorTransform.IdentityMul, ColorTransform.IdentityAdd);

            // `Graphic.material` 的 getter 在 `m_Material == null` 时返回 Canvas 默认 UI 材质
            // ⇒ 判据是"与没被调用过的图元拿到同一份材质"，而不是 `null`。
            Assert.AreSame(untouched.material, img.material,
                "恒等 ⇒ 材质指向与未调用过本件的图元相同（一个像素都不变）");
        }

        [Test]
        public void ApplyUi_AssignsTransformMaterialWithPackedProperties()
        {
            var img = NewImage(out _);

            ColorTransform.Apply(img, ColorTransform.MulBytes(0, 0, 0), ColorTransform.AddBytes(255, 255, 255));

            var mat = img.material;
            Assert.IsNotNull(mat, "非恒等 ⇒ 必须挂上颜色变换材质");
            Assert.AreEqual(ColorTransform.UiShaderName, mat.shader.name);
            Assert.AreEqual(new Color(0f, 0f, 0f, 1f), mat.GetColor("_ColorMul"));
            Assert.AreEqual(new Color(1f, 1f, 1f, 0f), mat.GetColor("_ColorAdd"));
        }

        [Test]
        public void ApplyUi_SameTransform_SharesOneMaterial()
        {
            var a = NewImage(out _);
            var b = NewImage(out _);
            var mul = ColorTransform.MulBytes(128, 128, 128);
            var add = ColorTransform.AddBytes(255, 255, 255);

            ColorTransform.Apply(a, mul, add);
            ColorTransform.Apply(b, mul, add);

            Assert.AreSame(a.material, b.material, "同一种变换共用一份材质 —— ⛔ 不逐实例克隆");
        }

        [Test]
        public void ApplySprite_Identity_WritesBackOriginalMaterial()
        {
            var sr = NewSprite(out _);
            var original = sr.sharedMaterial;

            ColorTransform.Apply(sr, ColorTransform.MulBytes(0, 0, 0), ColorTransform.AddBytes(255, 255, 255));
            Assert.AreEqual(ColorTransform.SpriteShaderName, sr.sharedMaterial.shader.name);

            ColorTransform.Apply(sr, ColorTransform.IdentityMul, ColorTransform.IdentityAdd);
            Assert.AreSame(original, sr.sharedMaterial, "恒等 ⇒ 写回首次调用前记下的材质");
        }

        // ── 真渲染读数 ─────────────────────────────────────────────────────────

        [Test]
        public void Pixel_SpriteBlackWithWhiteAdd_RendersWhite()
        {
            AssertGraphics();

            var sr = NewSprite(out _);
            ColorTransform.Apply(sr, ColorTransform.MulBytes(0, 0, 0), ColorTransform.AddBytes(255, 255, 255));

            var px = RenderCenter(Color.black);
            Assert.Greater(px.r, 0.95f, $"mul=0 + add=白 ⇒ 纯黑图元应变纯白，实测 {px}");
            Assert.Greater(px.g, 0.95f, $"实测 {px}");
            Assert.Greater(px.b, 0.95f, $"实测 {px}");
            Assert.Greater(px.a, 0.95f, $"mul.a = 1 ⇒ 不透明度不变，实测 {px}");
        }

        [Test]
        public void Pixel_SpriteIdentityShader_MatchesDefaultShader()
        {
            AssertGraphics();

            // 顶点色取半透明非灰 —— 让"乘算 + 预乘 + 混合"三段都参与比较
            var sr = NewSprite(out var go);
            sr.color = new Color(0.8f, 0.6f, 0.4f, 0.5f);

            var baseline = RenderCenter(new Color(0.2f, 0.4f, 0.6f, 1f));

            // 直接用**恒等默认值**的变换材质渲染（不调 Apply 的恒等短路），验证着色器自身与默认逐像素一致
            var mat = Track(new Material(Shader.Find(ColorTransform.SpriteShaderName)));
            sr.sharedMaterial = mat;
            var withShader = RenderCenter(new Color(0.2f, 0.4f, 0.6f, 1f));

            Assert.AreEqual((Color32)baseline, (Color32)withShader,
                $"恒等（mul=1 / add=0）的变换着色器必须与 Sprites/Default 逐像素一致：基准 {baseline} / 实得 {withShader}");
        }

        [Test]
        public void Pixel_UiBlackWithWhiteAdd_RendersWhite()
        {
            AssertGraphics();

            var img = NewImage(out _);
            ColorTransform.Apply(img, ColorTransform.MulBytes(0, 0, 0), ColorTransform.AddBytes(255, 255, 255));

            var px = RenderCenter(Color.black);
            Assert.Greater(px.r, 0.95f, $"uGUI 侧 mul=0 + add=白 ⇒ 纯黑图元应变纯白，实测 {px}");
            Assert.Greater(px.g, 0.95f, $"实测 {px}");
            Assert.Greater(px.b, 0.95f, $"实测 {px}");
        }

        [Test]
        public void Pixel_UiIdentityShader_MatchesDefaultMaterial()
        {
            AssertGraphics();

            var img = NewImage(out _);
            img.color = new Color(0.8f, 0.6f, 0.4f, 0.5f);
            var background = new Color(0.2f, 0.4f, 0.6f, 1f);

            var baseline = RenderCenter(background);

            var mat = Track(new Material(Shader.Find(ColorTransform.UiShaderName)));
            img.material = mat;
            var withShader = RenderCenter(background);

            Assert.AreEqual((Color32)baseline, (Color32)withShader,
                $"恒等（mul=1 / add=0）的变换着色器必须与 UI/Default 逐像素一致：基准 {baseline} / 实得 {withShader}");
        }

        // ── 场景搭建 ───────────────────────────────────────────────────────────

        /// <summary>一张 8×8 全不透明黑贴图（= 顶点色乘算做不出结果的图元）。</summary>
        private Sprite NewBlackSprite()
        {
            var tex = Track(new Texture2D(RtSize, RtSize, TextureFormat.RGBA32, false));
            var pixels = new Color32[RtSize * RtSize];
            for (var i = 0; i < pixels.Length; i++) pixels[i] = new Color32(0, 0, 0, 255);
            tex.SetPixels32(pixels);
            tex.Apply();
            return Track(Sprite.Create(tex, new Rect(0, 0, RtSize, RtSize), new Vector2(0.5f, 0.5f)));
        }

        private SpriteRenderer NewSprite(out GameObject go)
        {
            go = Track(new GameObject("xform-sprite"));
            go.layer = Layer;
            go.transform.position = Far;
            go.transform.localScale = Vector3.one * RtSize;          // 放大到正交视野内的中心一片（size 1 = 2 世界单位）
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = NewBlackSprite();
            return sr;
        }

        private Image NewImage(out GameObject go)
        {
            go = Track(new GameObject("xform-canvas"));
            go.layer = Layer;
            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvas.worldCamera = EnsureCamera();
            canvas.planeDistance = 1f;
            var scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;

            var imgGo = Track(new GameObject("xform-image"));
            imgGo.layer = Layer;
            imgGo.transform.SetParent(go.transform, false);
            var img = imgGo.AddComponent<Image>();
            img.sprite = NewBlackSprite();
            img.rectTransform.anchorMin = Vector2.zero;
            img.rectTransform.anchorMax = Vector2.one;
            img.rectTransform.offsetMin = Vector2.zero;
            img.rectTransform.offsetMax = Vector2.zero;
            Canvas.ForceUpdateCanvases();
            return img;
        }

        private Camera EnsureCamera()
        {
            if (_cam != null) return _cam;

            var go = Track(new GameObject("xform-camera"));
            go.layer = Layer;
            _cam = go.AddComponent<Camera>();
            _cam.orthographic = true;
            _cam.orthographicSize = 1f;
            _cam.nearClipPlane = 0.1f;
            _cam.farClipPlane = 100f;
            _cam.clearFlags = CameraClearFlags.SolidColor;
            _cam.cullingMask = 1 << Layer;      // 只看本用例的层 ⇒ 与工程场景内容互不干扰
            _cam.transform.position = Far + new Vector3(0f, 0f, -10f);
            _cam.transform.rotation = Quaternion.identity;

            _rt = Track(new RenderTexture(RtSize, RtSize, 0, RenderTextureFormat.ARGB32));
            _cam.targetTexture = _rt;
            return _cam;
        }

        private Color RenderCenter(Color background)
        {
            var cam = EnsureCamera();
            cam.backgroundColor = background;

            var prev = RenderTexture.active;
            RenderTexture.active = _rt;
            cam.Render();
            var read = new Texture2D(RtSize, RtSize, TextureFormat.RGBA32, false);
            read.ReadPixels(new Rect(0, 0, RtSize, RtSize), 0, 0);
            read.Apply();
            RenderTexture.active = prev;

            var px = read.GetPixel(RtSize / 2, RtSize / 2);
            Object.DestroyImmediate(read);
            return px;
        }

        private static void AssertGraphics()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                Assert.Ignore("无图形设备（-nographics）⇒ 渲染读数不可用");
            }
        }
    }
}
