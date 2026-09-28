// ─────────────────────────────────────────────────────────────────────────────
// CloverEngine · Runtime/Presentation/WorldLightMask.cs
//
// 【能力】以某个**世界坐标**为圆心、按调用方给的分级 alpha 剖面渲染一块「压暗遮罩」：
//   一个四边形 + 一张运行时生成的径向渐变贴图，随圆心平移（每帧只有一次
//   `transform.position` 写入），半径内按剖面渐显、半径外压满（alpha = 1）。
//
// 【通用性依据】「以一点为圆心、由亮到暗的圆形渐晕」是俯视 / 等距 / 地牢类 2D 项目都要的
//   表现件，与题材无关 —— 半径值、压到的颜色、衰减剖面**全部由调用方给**，本件不含任何
//   题材取值。为什么是「一张贴图」而不是「逐格改色」：
//     · 逐格改色要把每格 `SpriteRenderer.color` 按距离重算 ⇒ 角色每换一格就得重算一片格；
//       多项目的大图重铺都带**逐帧节点预算**，塞不进「每走一格乘一遍」；
//     · 一次绘制、随圆心平移 ⇒ 每帧只有一次位置写入；
//     · 渐变逐像素 ⇒ 边缘是柔和圆边，不是按格切出来的方块边。
//
// 【何时用】要在画面上叠一块「以某点为圆心的压暗渐晕」时（角色光照 / 火把 / 视野 / 聚光）。
//   ⛔ 不用于：逐格改色、真正的 2D 光照着色（那是 URP `Light2D` 的地盘）、UI 遮罩。
//   ⛔ 半径 / 压暗色 / 剖面不是引擎能定的量，必须由调用方按自己的参考物算好再传进来。
//
// 【已知边界】
//   ① 同进程每个实例各自持有 1 张贴图 + 1 个 `Sprite`（⛔ 不跨实例共享）——
//      `SetRadius` / `SetProfile` 值变了会按新值重铺贴图（O(分辨率²)），故半径与剖面
//      应当「开局设一次」，⛔ 不要每帧改值；
//   ② 世界坐标里的圆 = 屏幕上的圆（要求「世界 → 屏幕」是等比的；等距形变必须已烘进美术，
//      ⛔ 本件不做等距投影）；
//   ③ 四边形覆盖 `SpanUnits × SpanUnits` 世界单位，**必须同时包住「半径」与「整个视口」**
//      （否则屏幕上会看到贴图边缘 / 视口外没压到）—— 视口尺寸由调用方算好传进来；
//   ④ `parent == null` ⇒ 不建任何节点（各方法退化为空操作，只留一条 Warn）。
//
// 【用法与首个消费方】
//   var mask = new WorldLightMask(entityRoot, sortingOrder, spanUnits, textureSize);
//   mask.SetRadius(半径世界单位); mask.SetProfile(分级剖面);
//   mask.SetShadow(压到的颜色);
//   每帧 mask.Follow(圆心世界坐标);
//   进 / 出暗区 mask.SetVisible(true / false); 拆根节点时 mask.Dispose()。
//   首个消费方 = 项目 `Module/View/PlayerLightMask.cs`（薄转发：半径 / 剖面 / 压暗色在该侧
//   按自己的参考物算好，渲染全交本件）。
// ─────────────────────────────────────────────────────────────────────────────

using UnityEngine;

namespace CloverEngine
{
    /// <summary>
    /// 以世界坐标为圆心的压暗遮罩：**一个节点 + 一张运行时生成的径向渐变贴图**，不逐帧重算。
    /// <para>
    /// <b>渲染口径</b>：贴图中心 = 圆心；某个纹素到中心的距离 <c>d</c>（世界单位）⇒ 归一化
    /// <c>t = d / 半径</c> ⇒ alpha = <see cref="AlphaAt"/>（<c>t ≥ 1</c> ⇒ 1 = 压满）。
    /// 节点颜色（<see cref="SetShadow"/>）= 「压到的颜色」⇒ 半径内不压暗、半径外压到该颜色。
    /// </para>
    /// <para>
    /// <b>生命周期</b>：非 <c>MonoBehaviour</c>（由持有者驱动）：
    /// 构造 → <see cref="SetRadius"/> / <see cref="SetProfile"/> / <see cref="SetShadow"/> →
    /// 每帧 <see cref="Follow"/> → <see cref="SetVisible"/> → <see cref="Dispose"/>。
    /// 构造后默认**不可见**（调用方首次 <see cref="SetVisible"/> 才上屏）。
    /// </para>
    /// <para>
    /// <b>自证</b>：<see cref="AlphaAt"/> 是纯函数（无状态、不碰 Unity 运行时对象）
    /// ⇒ 可被离线宿主逐值断言；上屏表现（以该点为圆心的压暗渐晕）由进 Play 的探针截图目检
    /// （默认的两个区域都不是暗区 ⇒ 实机默认看不到本件，必须由探针显式给半径 / 阴影 / 剖面）。
    /// </para>
    /// <para>
    /// <b>已知边界</b>：
    /// ① <see cref="SetRadius"/> / <see cref="SetProfile"/> 的**值变了**才重铺贴图（同值 = 幂等，
    ///    不重铺）；构造期不铺，第一次落在「首次 <see cref="SetVisible"/>(true) 或首次设值」上；
    ///    重铺只重写**同一张**贴图的像素，⛔ 不新建贴图 / <c>Sprite</c>；
    /// ② 剖面数组按**副本**持有（调用方之后再改自己那份不影响本件）；
    /// ③ 非线程安全（主线程使用）。
    /// </para>
    /// </summary>
    public sealed class WorldLightMask
    {
        /// <summary>默认贴图分辨率（方形；越大渐变越细、生成越慢）。</summary>
        public const int DefaultTextureSize = 512;

        /// <summary>默认四边形覆盖的世界边长（方形，世界单位）。</summary>
        public const float DefaultSpanUnits = 64f;

        /// <summary>限频日志 / 单次日志的标签。</summary>
        public const string Tag = "WorldLightMask";

        private GameObject _node;
        private SpriteRenderer _renderer;
        private Texture2D _texture;
        private Sprite _sprite;
        private Color32[] _pixels;
        private float[] _profile;
        private float _radius;
        private float _spanUnits;
        private bool _visible;
        private bool _baked;

        /// <summary>当前是否可见（由 <see cref="SetVisible"/> 维护；构造后为 <c>false</c>）。</summary>
        public bool IsVisible => _visible;

        /// <summary>当前光照半径（世界单位；由 <see cref="SetRadius"/> 写入）。</summary>
        public float Radius => _radius;

        /// <summary>四边形覆盖的世界边长（构造时定，之后不变）。</summary>
        public float SpanUnits => _spanUnits;

        /// <summary>
        /// 在 <paramref name="parent"/> 下建遮罩节点（一张贴图 + 一个 <see cref="SpriteRenderer"/>；
        /// 默认隐藏）。
        /// </summary>
        /// <param name="parent">宿主（通常是实体根节点）；<c>null</c> ⇒ 不建节点（各方法退化为空操作）。</param>
        /// <param name="sortingOrder">排序值（本件不猜层级 —— 层级预算由调用方定）。</param>
        /// <param name="spanUnits">四边形覆盖的世界边长（须 &gt; 0 且有限；非法值回落到
        /// <see cref="DefaultSpanUnits"/> 并留一条 Warn）。</param>
        /// <param name="textureSize">贴图分辨率（方形，按 ≥ 2 收口）。</param>
        /// <param name="nodeName">节点名（默认 <c>WorldLightMask</c>；调用方按自己的层次命名习惯改）。</param>
        public WorldLightMask(Transform parent, int sortingOrder,
            float spanUnits = DefaultSpanUnits, int textureSize = DefaultTextureSize,
            string nodeName = "WorldLightMask")
        {
            _spanUnits = SanitizeSpan(spanUnits);
            if (textureSize < 2) textureSize = 2;
            if (string.IsNullOrEmpty(nodeName)) nodeName = nameof(WorldLightMask);

            if (parent == null)
            {
                LogThrottle.WarnOnce(Tag, "parent.missing",
                    "宿主 Transform 为 null（实体根未建？）⇒ 本次不建光照遮罩（各方法退化为空操作）");
                return;
            }

            _node = new GameObject(nodeName);
            _node.transform.SetParent(parent, false);
            _node.transform.localPosition = Vector3.zero;

            // 贴图：白底 + 剖面 alpha；PPU = 分辨率 ÷ 覆盖边长 ⇒ 节点不缩放即覆盖 SpanUnits。
            //   ⛔ 不要改成缩放节点：半径是以世界单位算的，缩放会连带改半径。
            _texture = new Texture2D(textureSize, textureSize, TextureFormat.RGBA32, false);
            _texture.name = nodeName;
            _texture.wrapMode = TextureWrapMode.Clamp;
            _pixels = new Color32[textureSize * textureSize];

            _sprite = Sprite.Create(_texture, new Rect(0f, 0f, textureSize, textureSize),
                new Vector2(0.5f, 0.5f), textureSize / _spanUnits);
            _sprite.name = nodeName;

            _renderer = _node.AddComponent<SpriteRenderer>();
            _renderer.sprite = _sprite;
            _renderer.sortingOrder = sortingOrder;
            _renderer.enabled = false;

            // 贴图**延迟到首次需要时**才铺（首次 SetVisible(true) 或首次 SetRadius / SetProfile）：
            // 构造后调用方通常紧接着设半径与剖面，构造期就铺会白铺一次。
            // 本件构造后默认不可见 ⇒ 不铺也不会露未初始化的像素。
        }

        /// <summary>
        /// 距圆心**归一化** <paramref name="normalizedDistance"/>（= 距离 ÷ 半径）处的遮蔽不透明度：
        /// 0 = 全亮、1 = 压满。**纯函数**（可离线断言）。
        /// <para><b>口径</b>：<c>t ≤ 0</c> ⇒ 0；<c>t ≥ 1</c> ⇒ 1；<c>0 &lt; t &lt; 1</c> ⇒ 按
        /// <paramref name="profile"/> 在 <c>[0, 1]</c> 上线性插值（<paramref name="profile"/> 的第 i 个
        /// 采样点 = <c>t = i / (长度 - 1)</c>）。</para>
        /// <para><b>边界</b>：<paramref name="profile"/> 为 <c>null</c> / 空 ⇒ **线性剖面**（<c>alpha = t</c>）；
        /// 长度 1 ⇒ 该值恒定；采样值按 <c>[0,1]</c> 收口（<c>NaN</c> 按 0）。</para>
        /// </summary>
        /// <param name="profile">分级 alpha 剖面（按归一化距离升序）；可为 <c>null</c>（= 线性）。</param>
        /// <param name="normalizedDistance">距离 ÷ 半径。</param>
        public static float AlphaAt(float[] profile, float normalizedDistance)
        {
            var t = normalizedDistance;
            if (!(t > 0f)) return 0f;      // 含 NaN ⇒ 0
            if (t >= 1f) return 1f;

            if (profile == null || profile.Length == 0) return t;   // 无剖面 ⇒ 线性
            if (profile.Length == 1) return Sanitize01(profile[0]);

            var x = t * (profile.Length - 1);
            var i = (int)x;
            if (i >= profile.Length - 1) return Sanitize01(profile[profile.Length - 1]);

            var f = x - i;
            return Sanitize01(profile[i] + (profile[i + 1] - profile[i]) * f);
        }

        /// <summary>遮罩中心跟到 <paramref name="world"/>（每帧一次写入；节点未建 ⇒ 空操作）。</summary>
        public void Follow(Vector3 world)
        {
            if (_node == null) return;
            _node.transform.position = world;
        }

        /// <summary>
        /// 设定光照半径（世界单位）：<c>t = 距离 ÷ 半径</c>。
        /// <para><b>幂等</b>：与当前值相同 ⇒ 不重铺贴图；值变了 ⇒ 按新半径重铺（O(分辨率²)）。</para>
        /// <para><b>边界</b>：非有限值（<c>NaN</c> / <c>±Inf</c>）⇒ 忽略本次（保持原值）+ 一条 Warn；
        /// 负值 ⇒ 按 0 收口（除圆心那一个纹素外整块压满，退化态）。</para>
        /// </summary>
        public void SetRadius(float units)
        {
            if (float.IsNaN(units) || float.IsInfinity(units))
            {
                LogThrottle.WarnOnce(Tag, "radius.nonfinite",
                    "SetRadius 收到非有限值（NaN / Inf）⇒ 忽略本次（保持原半径）");
                return;
            }

            if (units < 0f) units = 0f;
            if (units == _radius) return;   // 同值 ⇒ 不重铺（幂等）

            _radius = units;
            if (_radius <= 0f)
            {
                LogThrottle.WarnOnce(Tag, "radius.nonpositive",
                    "光照半径为 0 ⇒ 除圆心那一个纹素外整块压满（退化态；多半是调用方还没 SetRadius）");
            }

            Bake();
        }

        /// <summary>
        /// 设定「半径外压到的颜色」：只取 <paramref name="color"/> 的 **RGB**，不透明度恒按 1 写
        /// —— 压暗程度**唯一**由剖面决定（⛔ 两条通道同时改不透明度会让剖面失去意义）。
        /// </summary>
        public void SetShadow(Color color)
        {
            if (_renderer == null) return;
            _renderer.color = new Color(color.r, color.g, color.b, 1f);
        }

        /// <summary>
        /// 设定分级 alpha 剖面（按归一化距离升序；见 <see cref="AlphaAt"/>）。
        /// <para><b>幂等</b>：内容与当前剖面逐值相同 ⇒ 不重铺贴图；不同 ⇒ 重铺。</para>
        /// <para><b>边界</b>：<c>null</c> / 空 ⇒ 回落**线性剖面**；本件持有**副本**
        /// （调用方之后再改自己那份不影响本件）。</para>
        /// </summary>
        public void SetProfile(float[] alphaByNormDist)
        {
            if (SameProfile(_profile, alphaByNormDist)) return;

            _profile = alphaByNormDist == null ? null : (float[])alphaByNormDist.Clone();
            Bake();
        }

        /// <summary>
        /// 显示 / 隐藏（隐藏 = 整块不参与渲染，位置照样跟；节点未建 ⇒ 空操作）。
        /// <para>首次显示时若贴图还没铺过 ⇒ 先铺一次（保证上屏时像素已初始化）。</para>
        /// </summary>
        public void SetVisible(bool visible)
        {
            _visible = visible;
            if (visible && !_baked) Bake();
            if (_renderer != null) _renderer.enabled = visible;
        }

        /// <summary>拆掉节点与自持的贴图 / <c>Sprite</c>（幂等；之后各方法退化为空操作）。</summary>
        public void Dispose()
        {
            if (_node != null) Object.Destroy(_node);
            if (_sprite != null) Object.Destroy(_sprite);
            if (_texture != null) Object.Destroy(_texture);

            _node = null;
            _renderer = null;
            _sprite = null;
            _texture = null;
            _pixels = null;
            _visible = false;
            _baked = false;
        }

        /// <summary>
        /// 按当前半径与剖面重铺**同一张**贴图的像素（⛔ 不新建贴图 / <c>Sprite</c>）。
        /// <para>贴图中心 = 圆心；一个纹素的世界边长 = <c>SpanUnits / 分辨率</c>。</para>
        /// </summary>
        private void Bake()
        {
            if (_texture == null || _pixels == null) return;

            var size = _texture.width;
            var half = size * 0.5f;
            var texelUnits = _spanUnits / size;

            for (var y = 0; y < size; y++)
            {
                var dv = (y + 0.5f - half) * texelUnits;
                var row = y * size;
                for (var x = 0; x < size; x++)
                {
                    var du = (x + 0.5f - half) * texelUnits;
                    var d = Mathf.Sqrt(du * du + dv * dv);
                    // 半径 ≤ 0 时不除零：除圆心外一律压满
                    var t = _radius > 0f ? d / _radius : (d > 0f ? 1f : 0f);
                    var a = Mathf.RoundToInt(AlphaAt(_profile, t) * 255f);
                    _pixels[row + x] = new Color32(255, 255, 255, (byte)a);
                }
            }

            _texture.SetPixels32(_pixels);
            _texture.Apply(false, false);
            _baked = true;
        }

        /// <summary>两个剖面是否逐值相同（长度不同即不同；两侧都为 <c>null</c> 视为相同）。</summary>
        private static bool SameProfile(float[] a, float[] b)
        {
            if (a == null || b == null) return a == null && b == null;
            if (a.Length != b.Length) return false;
            for (var i = 0; i < a.Length; i++)
            {
                // NaN 与自身不等 ⇒ 两个 NaN 视为不同（会重铺一次，语义安全）
                if (a[i] != b[i]) return false;
            }

            return true;
        }

        /// <summary>采样值收口到 [0,1]（NaN 按 0；⛔ 不许把 NaN 写进贴图 alpha）。</summary>
        private static float Sanitize01(float v)
        {
            if (float.IsNaN(v)) return 0f;
            if (v < 0f) return 0f;
            return v > 1f ? 1f : v;
        }

        /// <summary>覆盖边长的兜底：非有限值 / ≤ 0 ⇒ <see cref="DefaultSpanUnits"/> 并留一条 Warn。</summary>
        private static float SanitizeSpan(float spanUnits)
        {
            if (float.IsNaN(spanUnits) || float.IsInfinity(spanUnits) || spanUnits <= 0f)
            {
                LogThrottle.WarnOnce(Tag, "span.invalid",
                    "覆盖边长非法（非有限值 / ≤ 0）⇒ 回落到默认 " + DefaultSpanUnits +
                    "；请由调用方按自己的视口尺寸传入");
                return DefaultSpanUnits;
            }

            return spanUnits;
        }
    }
}
