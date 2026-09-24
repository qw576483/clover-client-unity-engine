using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace CloverEngine
{
    // UI 通用件（Toast / 飘字 / Loading / 确认框 / 红点 / 引导遮罩 / 世界空间血条）的运行时实现。
    //
    // 设计取舍：
    //   1. **零美术依赖**：这些件全部用代码搭 uGUI 节点，不要求业务先做预制体。
    //      新工程 `Game.Launch` 之后直接 `Game.UI.Toast("...")` 就有反馈，不会因为
    //      「预制体还没做」而静默什么都不显示。
    //   2. **不进面板栈**：通用件不是业务面板，不参与 Open/Close 的窗口栈与 Popup 互斥，
    //      否则一个 Toast 会把正在看的弹窗顶掉。它们各自独立挂在自己的层级节点上。
    //   3. 需要换成美术版时，替换本文件的实现即可（或按件替换），对外接口不变。

    /// <summary>
    /// 运行时 uGUI 构件工厂：创建节点 / 面板 / 文本 / 按钮，取内置字体。
    /// <para>
    /// <b>对业务公开</b>：业务"用代码搭 UI"时一律用这里，不要自己再写一套
    /// 锚点/铺满/文字的工具类 —— 那类重复实现正是踩坑高发区
    /// （业务自建的 `UIBuilder` 就栽在"面板根节点没铺满"上，而本类的
    /// <see cref="Stretch"/> 正好能防住它）。
    /// </para>
    /// <para>
    /// 只暴露"造节点"这几个纯机械函数；<see cref="ToastLayer"/> 等通用件仍是 internal，
    /// 业务通过 <c>Game.UI</c> 的 Toast / Confirm / Loading 等入口使用。
    /// </para>
    /// <para>
    /// 通用控件工厂（Slider / InputField / Selector / ToggleRow）与布局助手见同程序集的
    /// <c>UIWidgetControls.cs</c> —— 同一个 <c>partial</c> 类的另一半，按"通用件"与"控件工厂"分文件。
    /// </para>
    /// </summary>
    public static partial class UIFactory
    {
        private static Font _font;
        private static bool _fontResolved;

        /// <summary>
        /// 取内置字体：Unity 6 为 <c>LegacyRuntime.ttf</c>，旧版本为 <c>Arial.ttf</c>；
        /// 两者都取不到时回退操作系统字体（保证中文能显示）。取不到返回 null（文本不显示但不会抛异常）。
        /// </summary>
        public static Font DefaultFont()
        {
            if (_fontResolved) return _font;
            _fontResolved = true;

            _font = TryBuiltin("LegacyRuntime.ttf");
            if (_font == null) _font = TryBuiltin("Arial.ttf");
            if (_font == null)
            {
                try
                {
                    _font = Font.CreateDynamicFontFromOSFont(
                        new[] { "Microsoft YaHei", "PingFang SC", "Noto Sans CJK SC", "Arial" }, 24);
                }
                catch
                {
                    _font = null;
                }
            }

            if (_font == null)
                Game.Logger?.Warn("UI", "未找到可用字体，通用件（Toast/Loading/确认框/引导）文本将不显示");

            return _font;
        }

        /// <summary>
        /// 取通用件文案：优先走本地化（<see cref="ILocalization"/>），未接入 / 未收录该 key 时回落内置中文。
        /// 通用件是"零美术依赖"的兜底件，不能因为业务还没配多语言就把文案显示成 key。
        /// </summary>
        internal static string Localize(string key, string fallback)
        {
            var text = Game.Localization?.Get(key);
            return string.IsNullOrEmpty(text) || text == key ? fallback : text;
        }

        private static Font TryBuiltin(string name)
        {
            try
            {
                return Resources.GetBuiltinResource<Font>(name);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>创建一个铺满父节点的空矩形节点。</summary>
        public static RectTransform CreateNode(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            Stretch(rt);
            return rt;
        }

        /// <summary>把矩形设为完全铺满父节点（全屏遮罩 / 整层容器用）。</summary>
        public static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        /// <summary>创建一个居中定尺的矩形节点（anchorMin=anchorMax=(0.5,0.5)）。</summary>
        public static RectTransform CreateCentered(string name, Transform parent, Vector2 size, Vector2 pos)
        {
            var rt = CreateNode(name, parent);
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = size;
            rt.anchoredPosition = pos;
            return rt;
        }

        /// <summary>创建一个纯色面板（背景 / 遮罩 / 按钮底板）。</summary>
        public static Image CreatePanel(string name, Transform parent, Color color, bool raycastTarget)
        {
            var rt = CreateNode(name, parent);
            var img = rt.gameObject.AddComponent<Image>();
            img.color = color;
            img.raycastTarget = raycastTarget;
            return img;
        }

        /// <summary>
        /// 创建一个文本节点。
        /// <para>
        /// 这是**引擎里唯一**建 uGUI <see cref="Text"/> 的地方（Toast / Loading / 确认框 / 飘字 /
        /// 引导遮罩 / 按钮文字全部经此），因此也是"文字渲染后端"的唯一注入点：建好之后会把 Text
        /// 交给业务注册的 <see cref="TextHooks.Current"/>（未注册 ⇒ 空转，见 <see cref="TextHooks"/>）。
        /// </para>
        /// </summary>
        public static Text CreateText(string name, Transform parent, string content, int fontSize,
            TextAnchor alignment, Color color, bool raycastTarget = false)
        {
            var rt = CreateNode(name, parent);
            var text = rt.gameObject.AddComponent<Text>();
            text.font = DefaultFont();
            text.fontSize = fontSize;
            text.alignment = alignment;
            text.color = color;
            text.text = content ?? string.Empty;
            text.raycastTarget = raycastTarget;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.supportRichText = false;

            // ★ 2026-09-19 纯新增：通知"文字渲染挂钩"。
            //   ① 位置必须在**最后**：文案 / 字号 / 对齐 / 颜色 / 溢出策略都已就位，挂钩里才读得到
            //      （挂钩会把这些当数据源，例如按 fontSize 选字模档位）；也才不会被上面的
            //      `text.font = DefaultFont()` 覆盖回去（本例里挂钩通常把 font 清成 null）。
            //   ② 未注册挂钩（`TextHooks.Current == null`）时它只做一次判空即返回 ⇒
            //      **行为与本次改动之前逐字一致**（不分配、不写 Text、不打日志）。
            //   ③ 挂钩抛异常由 `TextHooks.NotifyCreated` 吞掉 + Warn 一次 ⇒ 通用件照常打开。
            TextHooks.NotifyCreated(text);
            return text;
        }

        /// <summary>
        /// 创建一个按钮：底板 Image + 居中 Text，返回底板 Image（其 <see cref="Button"/> 已挂好）。
        /// </summary>
        public static Image CreateButton(string name, Transform parent, string label, Vector2 size, Vector2 pos,
            Color bg, Action onClick)
        {
            var img = CreatePanel(name, parent, bg, true);
            var rt = img.rectTransform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = size;
            rt.anchoredPosition = pos;

            var btn = img.gameObject.AddComponent<Button>();
            btn.targetGraphic = img;
            if (onClick != null)
                btn.onClick.AddListener(() => onClick());

            var labelText = CreateText("Label", rt, label, 26, TextAnchor.MiddleCenter, new Color(0.95f, 0.96f, 1f));
            Stretch(labelText.rectTransform);
            return img;
        }

        // ── 引擎署名行 ─────────────────────────────────────────────────────────
        /// <summary>署名行的默认宽度（画布单位）：文本短，给足宽度只为避免自动换行，不参与任何配对。</summary>
        private const float CreditWidth = 600f;

        /// <summary>署名行字体回退的降频 key（只报一次）。</summary>
        private const string CreditFontKey = "ui.credit.font";

        /// <summary>署名行默认颜色：半透明白，压暗到"看得见但不抢画面"（引擎默认值，业务可读回 <see cref="Text"/> 自行改）。</summary>
        private static readonly Color CreditColor = new Color(1f, 1f, 1f, 0.55f);

        /// <summary>
        /// 创建**引擎署名行**（默认文案 <c>by clover-engine</c>）：贴父节点**底部居中**、字号小、颜色低调。
        /// <para>
        /// <b>为什么进引擎</b>：每个游戏都必须有这一行（首页画面底部一行 <c>by clover-engine</c>），
        /// 而"贴底"这个定位反复被写错 —— 用左上角锚点 + 大负 y 去放底部元素时，
        /// <c>CanvasScaler</c>（match=0.5）的真实画布高度随窗口变化（1600×900 时只有约 972），
        /// y 一旦超过画布高度元素就**整体掉到屏幕外**（节点 active、文本正确，但一个像素都看不见，
        /// 只有实机截图才发现）。本方法把定位钉死为底部锚点：
        /// <c>anchorMin = anchorMax = (0.5, 0)</c> / <c>pivot = (0.5, 0)</c> /
        /// <c>anchoredPosition.y = bottomOffset</c>，业务一行即可。
        /// </para>
        /// <para>
        /// <b>字体与"静默变全大写"</b>：<paramref name="font"/> 为 <c>null</c> 时用引擎内置字体
        /// （<see cref="DefaultFont"/>），并**降频 Warn 一次** —— 像素 / 点阵字体常常只有大写字形，
        /// 小写会被静默渲染成全大写（<c>BY CLOVER-ENGINE</c>），这是真实发生过的事故；
        /// 而"源码里字符串对"并不等于"画面上文字对"（唯一判据是实机截图）。
        /// 需要保证小写的项目请显式传入带小写字形的字体。
        /// </para>
        /// <para>
        /// <b>复用</b>：建 Text 一律走 <see cref="CreateText"/>（引擎唯一的 Text 创建点，保证
        /// <see cref="TextHooks"/> 挂钩不被绕过），贴底一律走 <see cref="AnchoredBottom"/>
        /// （<see cref="TextAnchor.LowerCenter"/> = 底部居中），本方法不新建第三套定位 / 建文本工具。
        /// </para>
        /// </summary>
        /// <param name="parent">宿主节点（通常是 Canvas 根或某一层）。</param>
        /// <param name="font">字体；<c>null</c> = 引擎内置字体（会 Warn 一次，见上）。</param>
        /// <param name="fontSize">字号（默认 14：小而不抢画面）。</param>
        /// <param name="bottomOffset">离父节点底边的距离（画布单位，正数向上）。</param>
        /// <param name="text">文案；默认值逐字 = <c>by clover-engine</c>（首字母小写，⛔ 不要改大小写）。</param>
        /// <returns>创建出的 <see cref="Text"/> 组件（供业务改色 / 改字号 / 断言实际文本与字体）。</returns>
        public static UnityEngine.UI.Text CreateCreditLabel(
            UnityEngine.Transform parent,
            UnityEngine.Font font = null,
            int fontSize = 14,
            float bottomOffset = 16f,
            string text = "by clover-engine")
        {
            var label = CreateText("CreditLabel", parent, text, fontSize, TextAnchor.MiddleCenter, CreditColor);
            // 定位：复用 AnchoredBottom（LowerCenter ⇒ anchorMin = anchorMax = (0.5, 0)、pivot = (0.5, 0)）；
            // 高度给 fontSize + 10f 只为单行垂直居中，宽度固定只需包住短文本。
            AnchoredBottom(label.rectTransform, new Vector2(0f, bottomOffset),
                new Vector2(CreditWidth, fontSize + 10f));

            // 字体必须在 CreateText **之后**写：CreateText 里先落 DefaultFont() 再通知 TextHooks，
            // 显式传入的字体只有在此处覆盖才会生效（挂钩若把 font 清成 null，说明它自带文字渲染后端，
            // 本赋值对它无副作用）。
            label.font = font ?? DefaultFont();

            if (font == null)
            {
                // 非预期分支（未指定字体）：像素/点阵字体常只有大写字形 ⇒ 小写会被静默渲染成全大写。
                // 静默通过比报错更糟（源码字符串逐字正确、画面上却是 BY CLOVER-ENGINE），故必须留痕。
                LogThrottle.WarnOnce("UI", CreditFontKey,
                    $"署名行未指定字体，已回落到引擎内置字体（{label.font?.name}）：" +
                    "像素/点阵字体常只有大写字形，小写会被静默渲染成全大写（BY CLOVER-ENGINE）；" +
                    "要保证小写请显式传入带小写字形的字体");
            }

            return label;
        }

        /// <summary>取用于世界坐标 → 屏幕坐标换算的相机（主相机缺失时退到任一启用相机）。</summary>
        public static Camera UICamera()
        {
            var main = Camera.main;
            if (main != null) return main;
            return Camera.allCamerasCount > 0 ? Camera.allCameras[0] : null;
        }
    }

    /// <summary>
    /// Toast 层：顶部居中堆叠的短提示，每条到期后淡出回收。
    /// 同时最多显示 <see cref="MaxVisible"/> 条，超出丢弃最旧的一条（提示类信息不值得排长队）。
    /// </summary>
    internal sealed class ToastLayer
    {
        private const int MaxVisible = 5;
        private const float FadeIn = 0.12f;
        private const float FadeOut = 0.35f;
        private const float ItemHeight = 40f;
        private const float Gap = 6f;
        private const float Width = 720f;

        private sealed class Entry
        {
            public RectTransform Rt;
            public Text Label;
            public CanvasGroup Group;
            public float Elapsed;
            public float Duration;
        }

        private readonly RectTransform _root;
        private readonly List<Entry> _live = new();
        private readonly Stack<Entry> _pool = new();

        public ToastLayer(Transform parent)
        {
            _root = UIFactory.CreateNode("Toasts", parent);
            _root.anchorMin = _root.anchorMax = new Vector2(0.5f, 1f);
            _root.pivot = new Vector2(0.5f, 1f);
            _root.sizeDelta = new Vector2(Width, 0f);
            _root.anchoredPosition = new Vector2(0f, -140f);
            // 顶部悬挂条：锚点跟到安全区上沿，避免第一条被刘海 / 挖孔裁掉。
            _root.gameObject.AddComponent<SafeAreaFitter>().TopEdgeOnly = true;
        }

        /// <summary>弹出一条提示。</summary>
        public void Show(string text, float duration)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (duration <= 0f) duration = 2f;

            while (_live.Count >= MaxVisible)
                Recycle(_live[0], removeImmediately: true);

            var entry = _pool.Count > 0 ? _pool.Pop() : Create();
            entry.Rt.gameObject.SetActive(true);
            entry.Label.text = text;
            entry.Group.alpha = 0f;
            entry.Elapsed = 0f;
            entry.Duration = duration;
            _live.Add(entry);
            Reflow();
        }

        /// <summary>推进淡入淡出。</summary>
        public void Tick(float dt)
        {
            for (var i = _live.Count - 1; i >= 0; i--)
            {
                var e = _live[i];
                e.Elapsed += dt;

                if (e.Elapsed < FadeIn)
                    e.Group.alpha = e.Elapsed / FadeIn;
                else if (e.Elapsed < e.Duration)
                    e.Group.alpha = 1f;
                else
                    e.Group.alpha = 1f - (e.Elapsed - e.Duration) / FadeOut;

                if (e.Elapsed >= e.Duration + FadeOut)
                    // 到期回收必须**当场**重排：否则剩余提示留在原 index，栈顶留出空档（等下一条 Show 才复位）。
                    Recycle(e, removeImmediately: true);
            }
        }

        /// <summary>清空全部提示并回收节点。</summary>
        public void Clear()
        {
            for (var i = _live.Count - 1; i >= 0; i--)
                Recycle(_live[i], removeImmediately: true);
            Reflow();
        }

        /// <summary>销毁全部节点（含池）。</summary>
        public void Dispose()
        {
            foreach (var e in _live) Destroy(e);
            _live.Clear();
            while (_pool.Count > 0) Destroy(_pool.Pop());
            if (_root != null) UnityEngine.Object.Destroy(_root.gameObject);
        }

        private Entry Create()
        {
            var entry = new Entry();
            entry.Rt = UIFactory.CreatePanel("Toast", _root, new Color(0f, 0f, 0f, 0.72f), false).rectTransform;
            entry.Rt.anchorMin = entry.Rt.anchorMax = new Vector2(0.5f, 1f);
            entry.Rt.pivot = new Vector2(0.5f, 1f);
            entry.Rt.sizeDelta = new Vector2(Width, ItemHeight);
            entry.Group = entry.Rt.gameObject.AddComponent<CanvasGroup>();
            entry.Group.blocksRaycasts = false;
            entry.Group.interactable = false;
            entry.Label = UIFactory.CreateText("Label", entry.Rt, string.Empty, 24, TextAnchor.MiddleCenter,
                new Color(1f, 1f, 1f, 0.96f));
            UIFactory.Stretch(entry.Label.rectTransform);
            return entry;
        }

        private void Recycle(Entry entry, bool removeImmediately)
        {
            _live.Remove(entry);
            entry.Rt.gameObject.SetActive(false);
            _pool.Push(entry);
            if (removeImmediately) Reflow();
        }

        private void Reflow()
        {
            for (var i = 0; i < _live.Count; i++)
                _live[i].Rt.anchoredPosition = new Vector2(0f, -i * (ItemHeight + Gap));
        }

        private static void Destroy(Entry entry)
        {
            if (entry.Rt != null)
                UnityEngine.Object.Destroy(entry.Rt.gameObject);
        }
    }

    /// <summary>
    /// 飘字层：世界坐标处冒出的短文本（伤害数字 / 获得物品），向上飘并淡出。
    /// 世界坐标经相机投到屏幕，再换算到 Canvas 局部坐标，因此跟随相机移动。
    /// </summary>
    internal sealed class FloatTextLayer
    {
        private const float DefaultDuration = 1.2f;

        /// <summary>
        /// **本次下沉前**的上升距离，单位 = 画布局部单位（≈ 参考分辨率下的屏幕像素），与相机距离无关。
        /// 它同时是契约上 <c>riseWorld = 0</c> 时的默认升距 —— 保留它 = 保留旧行为，
        /// 旧的调用方（不传新参）一个像素都不会变。
        /// </summary>
        private const float RiseDistance = 70f;

        /// <summary>世界升距投影失败时的限频日志 key（限频表按 key 分，别用裸 Warn 刷屏）。</summary>
        private const string RiseFallbackKey = "floattext.rise";

        private sealed class Entry
        {
            public RectTransform Rt;
            public Text Label;
            public CanvasGroup Group;
            public Vector2 StartPos;
            public float Elapsed;
            public float Duration;
            /// <summary>本行的升距向量（画布局部单位）：已按世界升距或旧屏幕升距算好，Tick 只管乘 <c>t</c>。</summary>
            public Vector2 Rise;
            /// <summary>是否在本行存活期内淡出（false = 全程不透明，到点直接隐藏）。</summary>
            public bool Fade;
        }

        private readonly RectTransform _canvas;
        private readonly RectTransform _root;
        private readonly List<Entry> _live = new();
        private readonly Stack<Entry> _pool = new();

        public FloatTextLayer(RectTransform canvas)
        {
            _canvas = canvas;
            _root = UIFactory.CreateNode("FloatTexts", canvas);
            // 飘字出现在任意世界坐标的投影处：限制在安全区内，避免靠边时被刘海 / 圆角裁掉。
            _root.gameObject.AddComponent<SafeAreaFitter>();
        }

        /// <summary>
        /// 在指定世界坐标弹出一段飘字。
        /// </summary>
        /// <param name="worldPos">起点（世界坐标）。</param>
        /// <param name="text">文案；空则忽略（不建节点）。</param>
        /// <param name="color">文字颜色。</param>
        /// <param name="duration">存活时长（秒）；&lt;= 0 用 <see cref="DefaultDuration"/>。</param>
        /// <param name="riseWorld">
        /// 世界单位升距；<c>0</c> = 沿用旧的屏幕升距（<see cref="RiseDistance"/> 画布单位）。
        /// </param>
        /// <param name="fade">是否淡出；<c>false</c> = 全程不透明。</param>
        public void Show(Vector3 worldPos, string text, Color color, float duration,
            float riseWorld, bool fade)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (duration <= 0f) duration = DefaultDuration;

            // 相机背面 / 相机缺失时直接不弹：换算出来的屏幕点会落在画面外
            var cam = UIFactory.UICamera();
            if (cam == null) return;
            var screen = cam.WorldToScreenPoint(worldPos);
            if (screen.z < 0f) return;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    _canvas, new Vector2(screen.x, screen.y), null, out var local))
                return;

            var entry = _pool.Count > 0 ? _pool.Pop() : Create();
            entry.Rt.gameObject.SetActive(true);
            entry.Label.text = text;
            entry.Label.color = color;
            entry.StartPos = local;
            entry.Rt.anchoredPosition = local;
            entry.Elapsed = 0f;
            entry.Duration = duration;
            entry.Rise = ResolveRise(cam, worldPos, local, riseWorld);
            entry.Fade = fade;
            // 复用节点时必须复位 alpha：上一次可能是淡出到 0 的（fade = false 时 Tick 不再复写 alpha，
            // 不复位就会"复用出来一行看不见的字"）。
            entry.Group.alpha = 1f;
            _live.Add(entry);
        }

        /// <summary>
        /// 算本行的升距向量（画布局部单位）。
        /// <para>
        /// <paramref name="riseWorld"/> &gt; 0 ⇒ 把"沿世界 +Y 走 riseWorld"这段位移的**起点与终点各投一次**，
        /// 取画布局部差值：这样相机拉远拉近、画布缩放变化、相机旋转都自动是对的（不是按固定像素折）。
        /// </para>
        /// <para>
        /// 否则（含默认 0）⇒ 旧的屏幕升距：屏幕向上 <see cref="RiseDistance"/> 画布单位（与相机无关）。
        /// </para>
        /// </summary>
        private Vector2 ResolveRise(Camera cam, Vector3 worldPos, Vector2 local, float riseWorld)
        {
            var legacy = new Vector2(0f, RiseDistance);
            if (riseWorld <= 0f) return legacy;

            var topScreen = cam.WorldToScreenPoint(worldPos + Vector3.up * riseWorld);
            if (topScreen.z >= 0f &&
                RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    _canvas, new Vector2(topScreen.x, topScreen.y), null, out var topLocal))
                return topLocal - local;

            // 世界升距投影不出结果（终点落到相机背面 / 画布换算失败）：回落成屏幕升距并**限频留痕** ——
            // ⛔ 不许静默变成"飘字一动不动"（那看起来像飘字坏了，实际是投影失败）。
            LogThrottle.WarnThrottled("UI", RiseFallbackKey,
                $"飘字的世界升距（riseWorld={riseWorld}）投影失败，本行回落为屏幕升距 {RiseDistance}：" +
                "通常是飘字起点已越过相机背面（相机缺失/在反面由 Show 提前返回，不走到这里）");
            return legacy;
        }

        /// <summary>推进上升与淡出（升距/是否淡出逐行取 <see cref="Entry"/> 上的值，见 <see cref="Show"/>）。</summary>
        public void Tick(float dt)
        {
            for (var i = _live.Count - 1; i >= 0; i--)
            {
                var e = _live[i];
                e.Elapsed += dt;
                var t = Mathf.Clamp01(e.Elapsed / e.Duration);
                e.Rt.anchoredPosition = e.StartPos + e.Rise * t;
                // fade = false 时不碰 alpha（保持 Show 里复位的 1f）：到点后 entry 被 SetActive(false)，
                // 所以"不淡出"不会留下残影。
                if (e.Fade) e.Group.alpha = 1f - t * t;
                if (e.Elapsed >= e.Duration)
                {
                    _live.RemoveAt(i);
                    e.Rt.gameObject.SetActive(false);
                    _pool.Push(e);
                }
            }
        }

        /// <summary>清空全部飘字。</summary>
        public void Clear()
        {
            foreach (var e in _live)
            {
                e.Rt.gameObject.SetActive(false);
                _pool.Push(e);
            }
            _live.Clear();
        }

        /// <summary>销毁全部节点。</summary>
        public void Dispose()
        {
            foreach (var e in _live) Destroy(e);
            _live.Clear();
            while (_pool.Count > 0) Destroy(_pool.Pop());
            if (_root != null) UnityEngine.Object.Destroy(_root.gameObject);
        }

        private Entry Create()
        {
            var entry = new Entry();
            var rt = UIFactory.CreateCentered("FloatText", _root, new Vector2(320f, 48f), Vector2.zero);
            entry.Rt = rt;
            entry.Group = rt.gameObject.AddComponent<CanvasGroup>();
            entry.Group.blocksRaycasts = false;
            entry.Group.interactable = false;
            var label = UIFactory.CreateText("Label", rt, string.Empty, 30, TextAnchor.MiddleCenter,
                new Color(1f, 0.92f, 0.4f));
            label.fontStyle = FontStyle.Bold;
            UIFactory.Stretch(label.rectTransform);
            entry.Label = label;
            return entry;
        }

        private static void Destroy(Entry entry)
        {
            if (entry.Rt != null)
                UnityEngine.Object.Destroy(entry.Rt.gameObject);
        }
    }

    /// <summary>
    /// Loading 层：全屏半透明遮罩 + 旋转方块 + 文案。
    /// 用**引用计数**支持嵌套调用（两个异步流程同时 Show，先完成的那个 Hide 不会把还在等的那层关掉）。
    /// </summary>
    internal sealed class LoadingLayer
    {
        private const float SpinDegreesPerSecond = -240f;

        /// <summary>
        /// 「不确定进度」的哨兵（<c>NaN</c>）：<see cref="Show"/> 收到它 ⇒ 不显示进度条。
        /// <para>为什么用 NaN 而不是 -1：<see cref="IUIManager.ShowLoading(string)"/>（旧签名）与
        /// 带进度的重载共用同一个 <see cref="Show"/>，而 <c>NaN</c> 是唯一**永远不可能是合法进度**的值
        /// （0~1 的区间里没有它），不会被业务"传个负数当进度"之类的写法误撞。</para>
        /// </summary>
        internal const float Indeterminate = float.NaN;

        /// <summary>进度条轨道宽（画布单位）—— 引擎默认值（与 Spinner 64 / Label 600 同来源：本件自定）。</summary>
        private const float ProgressWidth = 560f;

        /// <summary>进度条高度（画布单位）—— 引擎默认值。</summary>
        private const float ProgressHeight = 18f;

        /// <summary>进度条中心相对屏幕中心的 y（画布单位，负 = 向下）—— 引擎默认值，落在文案下方。</summary>
        private const float ProgressY = -140f;

        /// <summary>百分比文字相对屏幕中心的 y（画布单位）—— 引擎默认值，落在进度条下方。</summary>
        private const float PercentY = -172f;

        private readonly RectTransform _root;
        private readonly RectTransform _spinner;
        private readonly Text _label;
        private readonly RectTransform _progressTrack;
        private readonly RectTransform _progressFill;
        private readonly Text _percent;
        private int _depth;

        /// <summary>当前进度；<see cref="Indeterminate"/> = 不确定（不显示进度条）。</summary>
        private float _progress = Indeterminate;

        public LoadingLayer(Transform parent)
        {
            _root = UIFactory.CreateNode("Loading", parent);

            var blocker = UIFactory.CreatePanel("Blocker", _root, new Color(0f, 0f, 0f, 0.55f), true);
            UIFactory.Stretch(blocker.rectTransform);

            // 指示器与文案放进安全区容器：内容不能压到刘海 / 挖孔 / 圆角上（遮罩本身仍铺满全屏）。
            var safe = UIFactory.CreateNode("SafeArea", _root);
            safe.gameObject.AddComponent<SafeAreaFitter>();

            _spinner = UIFactory.CreateCentered("Spinner", safe, new Vector2(64f, 64f), Vector2.zero);
            var bar = UIFactory.CreatePanel("Bar", _spinner, new Color(0.55f, 0.78f, 1f, 0.95f), false);
            bar.rectTransform.anchorMin = new Vector2(0.5f, 0f);
            bar.rectTransform.anchorMax = new Vector2(0.5f, 0.62f);
            bar.rectTransform.offsetMin = new Vector2(-9f, 0f);
            bar.rectTransform.offsetMax = new Vector2(9f, 0f);

            _label = UIFactory.CreateText("Label", safe, UIFactory.Localize("ui.loading", "加载中..."), 26,
                TextAnchor.MiddleCenter, new Color(0.95f, 0.96f, 1f));
            _label.rectTransform.anchorMin = _label.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            _label.rectTransform.sizeDelta = new Vector2(600f, 44f);
            _label.rectTransform.anchoredPosition = new Vector2(0f, -80f);

            // ★ 进度条（本次新增）：**确定进度**时才显示，与上面的"转圈"并列 ——
            //   转圈回答"没卡死"，进度条回答"还差多少"，两者不互斥（原版加载页也是转圈 + 读条同时有）。
            var track = UIFactory.CreateCentered("ProgressTrack", safe,
                new Vector2(ProgressWidth, ProgressHeight), new Vector2(0f, ProgressY));
            var trackBg = UIFactory.CreatePanel("Bg", track, new Color(0.10f, 0.12f, 0.16f, 0.85f), false);
            UIFactory.Stretch(trackBg.rectTransform);

            // 填充用**锚点宽度**而不是 `Image.fillAmount`（sprite 为空时 fillAmount 会静默失效，
            // 看着就是"进度条永不动"）—— 与 UIFactory.SetBarWidth 的注释同一口径。
            var fill = UIFactory.CreatePanel("Fill", track, new Color(0.55f, 0.78f, 1f, 0.95f), false);
            _progressFill = fill.rectTransform;
            UIFactory.SetBarWidth(_progressFill, 0f);
            _progressTrack = track;

            _percent = UIFactory.CreateText("ProgressPercent", safe, string.Empty, 22,
                TextAnchor.MiddleCenter, new Color(0.85f, 0.89f, 0.95f));
            _percent.rectTransform.anchorMin = _percent.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            _percent.rectTransform.sizeDelta = new Vector2(240f, 30f);
            _percent.rectTransform.anchoredPosition = new Vector2(0f, PercentY);

            _progressTrack.gameObject.SetActive(false);
            _percent.gameObject.SetActive(false);

            _root.gameObject.SetActive(false);
        }

        /// <summary>是否正在显示（引用计数 &gt; 0）。</summary>
        public bool IsVisible => _depth > 0;

        /// <summary>
        /// 显示一层 Loading。<paramref name="progress"/> 为 <see cref="Indeterminate"/>（NaN）⇒ 不确定形态
        /// （不显示进度条）；否则按 0~1 计入并显示进度条 + 百分比。
        /// <para>
        /// <b>嵌套语义</b>：不确定形态**不会**抹掉已经记下的进度值（上层多套一层"转圈"不该让下层的读条消失）；
        /// 引用计数归零（真正隐藏）时才复位成不确定 —— 否则下一次 <see cref="IUIManager.ShowLoading(string)"/>
        /// 会带着上一轮的旧进度突然出现一条 90% 的进度条。
        /// </para>
        /// </summary>
        public void Show(string text, float progress)
        {
            _depth++;
            if (!string.IsNullOrEmpty(text)) _label.text = text;
            if (!float.IsNaN(progress))
            {
                // NaN 之外的值一律按"确定进度"处理并夹到 [0,1]（越界是调用方笔误，夹取比抛异常安全）。
                _progress = Mathf.Clamp01(progress);
            }
            ApplyProgress();
            if (!_root.gameObject.activeSelf) _root.gameObject.SetActive(true);
        }

        /// <summary>更新进度（不改引用计数、不改文案）。<c>NaN</c> ⇒ 忽略并降频留痕（非法入参）。</summary>
        public void SetProgress(float progress)
        {
            if (float.IsNaN(progress))
            {
                LogThrottle.WarnThrottled("UI", "loading.progress.nan",
                    "SetLoadingProgress 收到 NaN ⇒ 忽略（进度仍是上一次的值）；" +
                    "要显示/隐藏进度条请用 ShowLoading(text, progress01) / 引用计数配对");
                return;
            }

            _progress = Mathf.Clamp01(progress);
            ApplyProgress();
        }

        /// <summary>收掉一层 Loading；引用计数归零才真正隐藏（并复位进度形态）。</summary>
        public void Hide()
        {
            if (_depth == 0) return;
            _depth--;
            if (_depth == 0)
            {
                _root.gameObject.SetActive(false);
                _progress = Indeterminate;    // 复位：下一次 Show 不该带着上一轮的旧进度出现
                ApplyProgress();
            }
        }

        // 说明：原 `public void Reset()`（强制把 _depth 归零并隐藏遮罩）已**删除**。
        // 它所在的 LoadingLayer 是 internal 类型、门面 IUIManager 也未透出该入口，
        // 引擎内与业务侧都没有任何调用点 —— 是一个"永远调不到"的死方法；
        // 它想解决的「遮罩卡死」由 Dispose()（同样把 _depth 归零）覆盖：
        // Dispose 在收起 UI 的路径上必然执行，Reset 只是它的弱化副本。

        /// <summary>驱动旋转。</summary>
        public void Tick(float dt)
        {
            if (_depth == 0) return;
            _spinner.Rotate(0f, 0f, SpinDegreesPerSecond * dt);
        }

        /// <summary>销毁全部节点。</summary>
        public void Dispose()
        {
            _depth = 0;
            _progress = Indeterminate;
            if (_root != null) UnityEngine.Object.Destroy(_root.gameObject);
        }

        /// <summary>
        /// 把当前进度写到进度条与百分比文字上：不确定形态下**两个节点都隐藏**（不是画一条 0% 的条 ——
        /// "没进度"与"进度为 0"是两件事，前者不该给玩家一个永远不动的空条）。
        /// </summary>
        private void ApplyProgress()
        {
            var determinate = _depth > 0 && !float.IsNaN(_progress);

            if (_progressTrack != null && _progressTrack.gameObject.activeSelf != determinate)
                _progressTrack.gameObject.SetActive(determinate);
            if (_percent != null && _percent.gameObject.activeSelf != determinate)
                _percent.gameObject.SetActive(determinate);
            if (!determinate) return;

            if (_progressFill != null) UIFactory.SetBarWidth(_progressFill, _progress);
            if (_percent != null) _percent.text = Mathf.RoundToInt(_progress * 100f) + "%";
        }
    }

    /// <summary>
    /// 确认框层：标题 + 正文 + 确认 / 取消两个按钮。
    /// 同时只显示一个，后到的请求**排队**（丢请求比排队更糟：玩家点了确认却什么都没发生）。
    /// </summary>
    internal sealed class ConfirmLayer
    {
        private sealed class Request
        {
            public string Title;
            public string Message;
            public string ConfirmText;
            public string CancelText;
            public Action OnConfirm;
            public Action OnCancel;
        }

        private readonly Transform _parent;
        private readonly Queue<Request> _queue = new();
        private RectTransform _view;
        // 正在显示的那条请求（Build 时记录）：CloseAll 时它不在 _queue 里，必须单独按「取消」回调。
        private Request _current;

        public ConfirmLayer(Transform parent)
        {
            _parent = parent;
        }

        /// <summary>是否有一个确认框正在等待玩家操作。</summary>
        public bool IsShowing => _view != null;

        /// <summary>入队一个确认框请求。</summary>
        public void Show(string title, string message, Action onConfirm, Action onCancel,
            string confirmText, string cancelText)
        {
            _queue.Enqueue(new Request
            {
                Title = title,
                Message = message,
                OnConfirm = onConfirm,
                OnCancel = onCancel,
                ConfirmText = string.IsNullOrEmpty(confirmText) ? UIFactory.Localize("ui.confirm", "确认") : confirmText,
                CancelText = string.IsNullOrEmpty(cancelText) ? UIFactory.Localize("ui.cancel", "取消") : cancelText,
            });
            PopNext();
        }

        /// <summary>关闭当前框（按取消处理）并清空排队中的请求。</summary>
        public void CloseAll()
        {
            // 正在显示的那条也要按「取消」回调：它不在 _queue 里，只清队列会把它的 OnCancel 丢掉，
            // 业务等在取消回调里解锁的状态（按钮置灰、Loading 引用计数…）会永久卡住。
            var current = _current;
            _current = null;
            if (_view != null)
            {
                var view = _view;
                _view = null;
                UnityEngine.Object.Destroy(view.gameObject);
            }
            Invoke(current?.OnCancel);

            while (_queue.Count > 0)
            {
                var req = _queue.Dequeue();
                Invoke(req.OnCancel);
            }
        }

        /// <summary>销毁节点（不回调）。</summary>
        public void Dispose()
        {
            if (_view != null)
            {
                UnityEngine.Object.Destroy(_view.gameObject);
                _view = null;
            }
            _current = null;
            _queue.Clear();
        }

        private void PopNext()
        {
            // 已有框在显示时不再弹下一个：Finish 的回调里若再 Confirm，会先在这里建好新框；
            // 此时若继续弹队列，会用新框覆盖掉刚建好的那条（既不销毁也不受 CloseAll 管辖）。
            if (_view != null || _queue.Count == 0) return;
            Build(_queue.Dequeue());
        }

        private void Build(Request req)
        {
            var overlay = UIFactory.CreatePanel("Confirm", _parent, new Color(0f, 0f, 0f, 0.6f), true);
            UIFactory.Stretch(overlay.rectTransform);
            _view = overlay.rectTransform;
            _current = req;

            // 遮罩铺满全屏（避免"点了遮罩外面还能穿透"），内容放进安全区容器，不压刘海 / 挖孔 / 圆角。
            var safe = UIFactory.CreateNode("SafeArea", _view);
            safe.gameObject.AddComponent<SafeAreaFitter>();

            var panel = UIFactory.CreateCentered("Panel", safe, new Vector2(760f, 420f), Vector2.zero);
            var bg = UIFactory.CreatePanel("Bg", panel, new Color(0.11f, 0.13f, 0.18f, 0.99f), true);
            UIFactory.Stretch(bg.rectTransform);

            var title = UIFactory.CreateText("Title", panel,
                string.IsNullOrEmpty(req.Title) ? UIFactory.Localize("ui.tip", "提示") : req.Title, 34,
                TextAnchor.MiddleCenter, new Color(1f, 1f, 1f));
            title.fontStyle = FontStyle.Bold;
            title.rectTransform.anchorMin = title.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            title.rectTransform.sizeDelta = new Vector2(680f, 60f);
            title.rectTransform.anchoredPosition = new Vector2(0f, 148f);

            var message = UIFactory.CreateText("Message", panel, req.Message ?? string.Empty, 26,
                TextAnchor.UpperCenter, new Color(0.86f, 0.89f, 0.95f));
            message.rectTransform.anchorMin = message.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            message.rectTransform.sizeDelta = new Vector2(660f, 190f);
            message.rectTransform.anchoredPosition = new Vector2(0f, 12f);

            UIFactory.CreateButton("Cancel", panel, req.CancelText, new Vector2(260f, 84f),
                new Vector2(-150f, -148f), new Color(0.22f, 0.25f, 0.32f), () => Finish(req, false));
            UIFactory.CreateButton("Confirm", panel, req.ConfirmText, new Vector2(260f, 84f),
                new Vector2(150f, -148f), new Color(0.16f, 0.44f, 0.78f), () => Finish(req, true));
        }

        private void Finish(Request req, bool confirmed)
        {
            if (_view != null)
            {
                UnityEngine.Object.Destroy(_view.gameObject);
                _view = null;
            }
            _current = null;

            if (confirmed) Invoke(req.OnConfirm);
            else Invoke(req.OnCancel);

            PopNext();
        }

        private static void Invoke(Action action)
        {
            if (action == null) return;
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Game.Logger?.Error("UI", $"确认框回调异常: {ex.Message}", ex);
            }
        }
    }

    /// <summary>
    /// 红点注册表：key → 是否亮红点。
    /// <para>
    /// key 用 <c>/</c> 分层（如 <c>mail</c> / <c>mail/reward</c>），父节点**自动聚合**后代：
    /// 只要有一个后代被点亮，<c>Get("mail")</c> 即为 true，业务不必手工维护父节点。
    /// </para>
    /// <para>
    /// 不变量：**父节点恒为「自身显式亮起 或 任一后代显式亮起」**，因此不会出现
    /// 「子节点亮红点、父节点不亮」这种让人困惑的状态。要熄灭整棵子树就逐个熄灭叶子
    /// （或直接 <see cref="Clear"/>），**不能**靠"把父节点设成 false"来压制子节点。
    /// </para>
    /// <para>
    /// 纯数据、无 GameObject 依赖，因此在 EditMode 单测里也能直接构造使用。
    /// </para>
    /// </summary>
    internal sealed class RedDotRegistry
    {
        private const char Separator = '/';

        private readonly Dictionary<string, bool> _explicit = new();
        private readonly List<Action<string, bool>> _handlers = new();

        /// <summary>查询某 key 当前是否亮红点（含后代聚合）。</summary>
        public bool Get(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            if (_explicit.TryGetValue(key, out var own)) return own;

            var prefix = key + Separator;
            foreach (var kv in _explicit)
            {
                if (kv.Value && kv.Key.StartsWith(prefix, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// 设置某 key 的红点（显式值）。仅在**有效值发生变化**时回调：
        /// 点亮 <c>mail/reward</c> 时，<c>mail/reward</c> 与父节点 <c>mail</c> 都会各收到一次通知。
        /// </summary>
        public void Set(string key, bool on)
        {
            if (string.IsNullOrEmpty(key)) return;

            // _explicit 只存 true（熄灭即删除），故 ContainsKey 即「当前是显式亮起」
            if (_explicit.ContainsKey(key) == on) return;

            // 先记下自身与全部祖先的**有效值**（此时尚未变更），变更后再逐个比对
            var chain = new List<string>();
            var cursor = key;
            while (!string.IsNullOrEmpty(cursor))
            {
                chain.Add(cursor);
                var cut = cursor.LastIndexOf(Separator);
                cursor = cut > 0 ? cursor.Substring(0, cut) : null;
            }

            var before = new bool[chain.Count];
            for (var i = 0; i < chain.Count; i++)
                before[i] = Get(chain[i]);

            if (on) _explicit[key] = true;
            else _explicit.Remove(key);

            for (var i = 0; i < chain.Count; i++)
            {
                var now = Get(chain[i]);
                if (now != before[i]) Notify(chain[i], now);
            }
        }

        /// <summary>订阅红点变化（参数为 key 与变化后的有效值）。</summary>
        public void OnChanged(Action<string, bool> handler)
        {
            if (handler != null) _handlers.Add(handler);
        }

        /// <summary>清空全部红点（退出登录 / 切号时调用），逐 key 回调为熄灭。</summary>
        public void Clear()
        {
            if (_explicit.Count == 0) return;

            // 受影响的不只是显式亮起的叶子：父节点是靠聚合亮的，清空后同样由 true 变 false，
            // 不通知父节点会让订阅父 key 的业务红点永久卡在亮起状态。
            var affected = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var key in _explicit.Keys)
            {
                var cursor = key;
                while (!string.IsNullOrEmpty(cursor))
                {
                    if (seen.Add(cursor)) affected.Add(cursor);
                    var cut = cursor.LastIndexOf(Separator);
                    cursor = cut > 0 ? cursor.Substring(0, cut) : null;
                }
            }

            var before = new bool[affected.Count];
            for (var i = 0; i < affected.Count; i++)
                before[i] = Get(affected[i]);   // 变更前快照（此刻这些 key 的有效值必为 true）

            _explicit.Clear();

            for (var i = 0; i < affected.Count; i++)
            {
                var now = Get(affected[i]);
                if (now != before[i]) Notify(affected[i], now);
            }
        }

        /// <summary>亮着的 key 数量（仅显式设置的叶子，供调试面板展示）。</summary>
        public int ExplicitCount => _explicit.Count;

        private void Notify(string key, bool value)
        {
            foreach (var h in new List<Action<string, bool>>(_handlers))
            {
                try { h?.Invoke(key, value); }
                catch (Exception ex) { Game.Logger?.Error("UI", $"红点回调异常: {ex.Message}", ex); }
            }
        }
    }

    /// <summary>
    /// 引导遮罩层：挖空一块区域高亮目标控件，其余部分压暗，并挂一句提示。
    /// <para>
    /// 点击行为两种模式：
    /// <list type="bullet">
    ///   <item><c>blockTarget = true</c>（默认）：点击屏幕任意处都触发 <c>onClick</c>，被高亮的控件**不**接收点击 ——
    ///   「点这里继续」式引导；</item>
    ///   <item><c>blockTarget = false</c>：只有挖空区域外拦点击，被高亮的控件能正常点，业务自己在目标控件回调里调 <c>HideGuide</c> ——
    ///   「请点击 XX 完成操作」式引导。</item>
    /// </list>
    /// </para>
    /// </summary>
    internal sealed class GuideLayer
    {
        private readonly RectTransform _parent;
        private RectTransform _view;

        public GuideLayer(RectTransform parent)
        {
            _parent = parent;
        }

        /// <summary>是否正在显示引导。</summary>
        public bool IsShowing => _view != null;

        /// <summary>显示引导遮罩（已在显示时先关掉旧的）。</summary>
        public void Show(RectTransform target, string tip, Action onClick, bool blockTarget)
        {
            Hide();

            var overlay = UIFactory.CreateNode("Guide", _parent);
            _view = overlay;

            // 挖空区域：由目标控件在世界空间的四角投到 Canvas 局部坐标后取包围盒
            var hole = ComputeHole(target, overlay);

            // 全屏点击拦截（blockTarget=true 专用）：先于四块遮挡加入 ⇒ 兄弟序在前 ⇒ 绘制在遮挡**下方**；
            // 此时遮挡块不吃射线，点击由本层接住后回调并关掉引导。
            if (blockTarget)
            {
                var catcher = UIFactory.CreatePanel("Catcher", overlay, new Color(0f, 0f, 0f, 0f), true);
                UIFactory.Stretch(catcher.rectTransform);
                var btn = catcher.gameObject.AddComponent<Button>();
                btn.transition = Selectable.Transition.None;
                btn.targetGraphic = catcher;
                btn.onClick.AddListener(() =>
                {
                    Invoke(onClick);
                    Hide();
                });
            }

            if (hole.width > 0f && hole.height > 0f)
                BuildMaskStrips(overlay, hole, blockTarget, onClick);

            BuildTip(overlay, hole, tip);
        }

        /// <summary>关闭引导遮罩。</summary>
        public void Hide()
        {
            if (_view == null) return;
            UnityEngine.Object.Destroy(_view.gameObject);
            _view = null;
        }

        /// <summary>销毁节点。</summary>
        public void Dispose() => Hide();

        /// <summary>
        /// 用四块暗色矩形拼出「中间挖空」的效果（比 stencil/自定义材质更省事，且不依赖任何资源）。
        /// </summary>
        private static void BuildMaskStrips(RectTransform parent, Rect hole, bool blockTarget, Action onClick)
        {
            // parent.rect 以中心为原点（pivot 0.5 + 全铺满锚点），故左下角为 (-w/2, -h/2)
            var size = parent.rect.size;

            var left = hole.xMin;
            var bottom = hole.yMin;
            var right = hole.xMax;
            var top = hole.yMax;

            // 四块遮挡的拦截语义：
            //   blockTarget = false：只有洞外拦点击（**并回调 onClick**，否则回调静默丢失），
            //                        洞内放行给被高亮的控件（「点这里完成操作」）；
            //   blockTarget = true ：全屏拦截层（Catcher）负责接收点击，遮挡块必须**不**吃射线，
            //                        否则点在洞外会打在没挂回调的遮挡块上，表现为「点了没反应」。
            var raycast = !blockTarget;
            var stripClick = blockTarget ? null : onClick;
            var dim = new Color(0f, 0f, 0f, 0.62f);
            AddStrip("Top", parent, new Vector2(0f, (top + size.y * 0.5f) * 0.5f),
                new Vector2(size.x, size.y * 0.5f - top), dim, raycast, stripClick);
            AddStrip("Bottom", parent, new Vector2(0f, (-size.y * 0.5f + bottom) * 0.5f),
                new Vector2(size.x, bottom + size.y * 0.5f), dim, raycast, stripClick);
            AddStrip("Left", parent, new Vector2((-size.x * 0.5f + left) * 0.5f, (bottom + top) * 0.5f),
                new Vector2(left + size.x * 0.5f, top - bottom), dim, raycast, stripClick);
            AddStrip("Right", parent, new Vector2((right + size.x * 0.5f) * 0.5f, (bottom + top) * 0.5f),
                new Vector2(size.x * 0.5f - right, top - bottom), dim, raycast, stripClick);
        }

        private static void AddStrip(string name, RectTransform parent, Vector2 center, Vector2 size,
            Color color, bool raycast, Action onClick)
        {
            if (size.x <= 0f || size.y <= 0f) return;
            var strip = UIFactory.CreatePanel(name, parent, color, raycast);
            var rt = strip.rectTransform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = size;
            rt.anchoredPosition = center;

            // 洞外的点击由一个"遮挡块 + 按钮"接住：拦截（不吃穿到世界）之外还要回调 onClick ——
            // 契约明写「两种模式下都会调用」；blockTarget=false 时业务在自己的目标回调里调 HideGuide。
            if (onClick != null)
            {
                var btn = strip.gameObject.AddComponent<Button>();
                btn.transition = Selectable.Transition.None;
                btn.targetGraphic = strip;
                btn.onClick.AddListener(() => Invoke(onClick));
            }
        }

        private static void BuildTip(RectTransform parent, Rect hole, string tip)
        {
            if (string.IsNullOrEmpty(tip)) return;

            var y = hole.yMax > 0f ? hole.yMin - 60f : hole.yMax + 60f;
            var bubble = UIFactory.CreatePanel("Tip", parent, new Color(0.1f, 0.12f, 0.16f, 0.96f), false);
            var rt = bubble.rectTransform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(620f, 120f);
            rt.anchoredPosition = new Vector2(0f, Mathf.Clamp(y, -320f, 320f));

            var label = UIFactory.CreateText("Label", rt, tip, 26, TextAnchor.MiddleCenter,
                new Color(0.95f, 0.96f, 1f));
            UIFactory.Stretch(label.rectTransform);
            label.rectTransform.offsetMin = new Vector2(20f, 10f);
            label.rectTransform.offsetMax = new Vector2(-20f, -10f);
        }

        /// <summary>把目标控件的屏幕矩形换算成遮罩层局部（以中心为原点）的矩形；目标为空时退化为屏幕中央的小孔。</summary>
        private static Rect ComputeHole(RectTransform target, RectTransform parent)
        {
            var canvas = parent.rect.size;
            if (target == null)
                return new Rect(-canvas.x * 0.15f, -canvas.y * 0.1f, canvas.x * 0.3f, canvas.y * 0.2f);

            var corners = new Vector3[4];
            target.GetWorldCorners(corners);

            var min = new Vector2(float.MaxValue, float.MaxValue);
            var max = new Vector2(float.MinValue, float.MinValue);
            for (var i = 0; i < 4; i++)
            {
                // UIManager 建的是 ScreenSpaceOverlay Canvas，其世界坐标即屏幕像素坐标，
                // 因此这里以 null 相机把角点换算到遮罩层的局部坐标（pivot 在中心）。
                var screen = new Vector2(corners[i].x, corners[i].y);
                if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, screen, null, out var local))
                    return new Rect(-canvas.x * 0.15f, -canvas.y * 0.1f, canvas.x * 0.3f, canvas.y * 0.2f);
                min = Vector2.Min(min, local);
                max = Vector2.Max(max, local);
            }

            const float padding = 10f;
            min -= new Vector2(padding, padding);
            max += new Vector2(padding, padding);
            return new Rect(min.x, min.y, max.x - min.x, max.y - min.y);
        }

        private static void Invoke(Action action)
        {
            if (action == null) return;
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Game.Logger?.Error("UI", $"引导回调异常: {ex.Message}", ex);
            }
        }
    }

    /// <summary>
    /// 世界空间头顶血条（3D 实体用）：挂在实体根节点下的「背景 Quad + 填充 Quad」组合，
    /// 随血量比例收缩、按比例分档变色，并**始终面向相机**（广告牌）。
    ///
    /// 与 uGUI 通用件（<see cref="IUIManager"/> 的 Toast / 飘字 …）的区别：那些跑在屏幕空间，
    /// 这个跑在**世界空间**（浮在怪物头顶）—— 两者互不替代，所以单独放这一类。
    ///
    /// 为什么它该进引擎（而不是每个项目自己写一遍）：每个有血量的 MMO/ARPG 都要一条世界空间血条，
    /// 而这四件事**每个项目都会重踩**，且失败都是"看着不对、却不报错"：
    /// <list type="number">
    ///   <item>Quad 上的 <see cref="Collider"/> 必须删掉 —— 否则相机的 SphereCast 会打到血条，
    ///     镜头莫名其妙被拉近；</item>
    ///   <item>广告牌必须**每帧**写世界朝向 —— 血条是实体根的子物体，根会随角色转身，
    ///     只在创建时设一次朝向会随角色一起转；</item>
    ///   <item>血量必须能带上**上限**（<see cref="SetHp"/>）：只喂当前血量时，60 血的目标会被按
    ///     默认分母画成"一出来就是残血"；</item>
    ///   <item>屏幕血条要用**锚点宽度**而不是 <c>Image.fillAmount</c>（无 sprite 的 Filled Image
    ///     会退化成整块矩形，扣血看不出来）—— 这里因为是世界空间 Quad，用 localScale 表达比例，
    ///     同一类陷阱的另一种形态。</item>
    /// </list>
    ///
    /// <h4>① 缺陷（D129b 修）</h4>
    /// 两个 Quad 的 <see cref="Renderer.sortingOrder"/> 从来没设过 ⇒ 恒为 0。而业务侧的单位/怪物精灵
    /// 通常在一个远大于 0 的层级上（例：`UnitView.SortingOrder.Unit` = 1000 + 纵深），于是**血条被
    /// 自己单位的精灵盖住**：血条"时有时无、贴脸才看得见"，且不报任何错。
    ///
    /// <h4>② 最小复现</h4>
    /// 在任意 2D 项目里给一个 <c>sortingOrder = 1000</c> 的 SpriteRenderer 挂一条 `WorldHpBar`
    /// （其 Quad order = 0），两者屏幕位置重叠时血条不可见；把 <c>sortingOrder</c> 传成 &gt; 1000 即显示。
    /// 判据 = 运行时读 <c>GetComponentInChildren&lt;WorldHpBar&gt;().transform.GetChild(0)
    /// .GetComponent&lt;MeshRenderer&gt;().sortingOrder</c>（修复前 0，修复后 = 传入值）。
    ///
    /// <h4>③ 为什么是"加参数 + 默认 0"而不是"改默认值"</h4>
    /// 工作区内还有别的消费方（`clover-project-diablo2` 的 `ViewModule.cs` 用
    /// <c>WorldHpBar.Create(...)</c> 且不传本参数）⇒ 改默认值会**静默改变它们的渲染顺序**。
    /// 所以本参数默认 0（= 旧行为，逐位不变），需要"压在单位之上"的项目自己传（首个消费方 =
    /// `clover-project-cr` 的 `UnitView.cs`，传 `SortingOrder.HpBar` = 2000）。
    ///
    /// <h4>④ 已知边界</h4>
    /// 引擎不知道业务层级表，所以本类只**透传**这个值，不猜。同一实例的 <c>sortingOrder</c> 在
    /// 幂等复用（`Create` 命中已有实例）时会一起更新（`ApplyParams`），热更/换皮场景不会留旧值。
    /// 两个 Quad 用**同一个** order：它们的先后由广告牌朝向决定（Fill 的 local z = -0.01 ⇒ 更贴近相机
    /// ⇒ 后画 ⇒ 盖住 Bg），与修复前一致，不受本改动影响。
    ///
    /// <h4>⑤ 用法 / 首个消费方</h4>
    /// <code>
    /// var bar = WorldHpBar.Create(viewRoot.transform, tag: "Knight.Hp", sortingOrder: 2000);
    /// bar.SetHp(60, 60);
    /// </code>
    /// 首个消费方：`clover-project-cr/client/Assets/Scripts/View/UnitView.cs`（`SortingOrder.HpBar`）。
    ///
    /// 用法：
    /// <code>
    /// var bar = WorldHpBar.Create(viewRoot.transform);   // 挂在实体视图根下
    /// bar.SetHp(60, 60);                                  // 收到属性/战斗推送后更新
    /// </code>
    /// 目标被销毁时血条自动销毁（不依赖业务记得清理）。
    /// </summary>
    public sealed class WorldHpBar : MonoBehaviour
    {
        /// <summary>默认宽度（米，世界空间）。</summary>
        public const float DefaultWidth = 1.0f;

        /// <summary>默认高度（米）。</summary>
        public const float DefaultHeight = 0.12f;

        /// <summary>默认离脚底高度（米）：要能在 1600×900 下一眼看清，0.8 以下会细成一条线。</summary>
        public const float DefaultYOffset = 2.15f;

        /// <summary>
        /// 默认渲染顺序。**刻意保持 0**（= 修复前行为）：引擎不知道业务的层级表，改默认值会静默改变
        /// 已有消费方（如 `clover-project-diablo2`）的渲染顺序。需要"压在单位精灵之上"的项目自行传
        /// （首个消费方 `clover-project-cr` 传 2000，推导见 `UnitView.SortingOrder.HpBar`）。
        /// </summary>
        public const int DefaultSortingOrder = 0;

        private static readonly Color BgColor = new Color(0.06f, 0.06f, 0.06f, 0.85f);

        // 血量分档：绿 → 橙 → 红（一眼看出危险）
        private static readonly Color HighColor = new Color(0.35f, 0.85f, 0.3f, 0.95f);
        private static readonly Color MidColor = new Color(0.95f, 0.75f, 0.2f, 0.95f);
        private static readonly Color LowColor = new Color(0.9f, 0.2f, 0.2f, 0.95f);

        private Transform _target;
        private Transform _fill;
        private MeshRenderer _bgRenderer;
        private MeshRenderer _fillRenderer;
        private float _width = DefaultWidth;
        private float _height = DefaultHeight;
        private int _sortingOrder = DefaultSortingOrder;
        private float _ratio = 1f;
        private float _hp = 1f;
        private float _maxHp = 1f;
        private Camera _cam;
        private bool _visible = true;

        /// <summary>当前血量比例 [0,1]。</summary>
        public float Ratio => _ratio;

        /// <summary>最近一次 SetHp 传入的当前血量（未调用过 SetHp 时为 1）。</summary>
        public float Hp => _hp;

        /// <summary>最近一次 SetHp 传入的血量上限（未调用过 SetHp 时为 1）。</summary>
        public float MaxHp => _maxHp;

        /// <summary>
        /// 两个 Quad 的渲染顺序（= <see cref="Create"/> 传入值）。业务侧可用它做**不变量断言**：
        /// 血条层必须大于自己的单位/怪物精灵层，否则血条会被盖住（修复前恒为 0）。
        /// </summary>
        public int SortingOrder => _sortingOrder;

        private string _tag = "HpBar";

        /// <summary>
        /// 在 <paramref name="target"/> 下创建一条头顶血条（幂等：已有则复用）。
        /// </summary>
        /// <param name="target">宿主节点（通常是实体视图根）</param>
        /// <param name="width">宽度（米）</param>
        /// <param name="height">高度（米）</param>
        /// <param name="yOffset">离宿主节点的高度（米）</param>
        /// <param name="tag">日志标签（多套实体用不同标签便于排障）</param>
        /// <param name="sortingOrder">两个 Quad 的渲染顺序。默认 0 = 旧行为；业务侧单位精灵层级较高时
        /// 必须传一个更大的值，否则血条会被单位精灵盖住（见类注释「① 缺陷」）。</param>
        public static WorldHpBar Create(Transform target, float width = DefaultWidth,
            float height = DefaultHeight, float yOffset = DefaultYOffset, string tag = "HpBar",
            int sortingOrder = DefaultSortingOrder)
        {
            if (target == null) return null;

            var existing = target.GetComponentInChildren<WorldHpBar>(true);
            if (existing != null)
            {
                // 幂等复用，但 width/height/tag/sortingOrder 不能静默作废（改了尺寸或标签却"没反应、也无日志"
                // 是明确的静默失败）：尺寸变化时重建 Quad，标签变化时改名并留痕。
                existing.SetYOffset(yOffset);
                existing.ApplyParams(width, height, tag, sortingOrder);
                return existing;
            }

            var root = new GameObject(tag);
            root.transform.SetParent(target, false);
            root.transform.localPosition = new Vector3(0f, yOffset, 0f);

            var bar = root.AddComponent<WorldHpBar>();
            bar._target = target;
            bar._tag = tag;
            bar._width = width;
            bar._height = height;
            bar._sortingOrder = sortingOrder;
            bar.Build(width, height);
            return bar;
        }

        /// <summary>
        /// 创建一条头顶血条（**排序层必填**的重载）。
        /// <para>
        /// <b>为什么要有它</b>：另一个 <see cref="Create(Transform, float, float, float, string, int)"/> 的
        /// <c>sortingOrder</c> 有默认值 <see cref="DefaultSortingOrder"/>（= 0，为兼容既有消费方刻意保留），
        /// 于是"忘了传"这个失败模式**完全静默**：血条节点在、两个 Quad 的 <c>enabled</c> 也是 true，
        /// 只是被自己的单位 / 场地底图精灵盖住 ⇒ 表现成"血条时有时无 / 一条都看不见"，且不报任何错。
        /// 本重载把该参数提到**第 2 位且无默认值** ⇒ 漏传是**编译期**错误，而不是渲染期的静默失败。
        /// </para>
        /// <para>
        /// 参数位置与旧重载不同是刻意的：<paramref name="sortingOrder"/> 是唯一**没有安全默认值**的参数，
        /// 只有把它放到"必须给"的位置才挡得住漏传。其余参数语义逐个与旧重载一致。
        /// </para>
        /// <para>层级取值仍是业务的事（引擎不知道你的层级表）：一般取"大于本单位精灵层、小于特效层"。</para>
        /// </summary>
        /// <param name="target">宿主节点（通常是实体视图根）。</param>
        /// <param name="sortingOrder">两个 Quad 的渲染顺序（必填）。</param>
        /// <param name="width">宽度（米）。</param>
        /// <param name="height">高度（米）。</param>
        /// <param name="yOffset">离宿主节点的高度（米）。</param>
        /// <param name="tag">日志标签（多套实体用不同标签便于排障）。</param>
        public static WorldHpBar Create(Transform target, int sortingOrder, float width = DefaultWidth,
            float height = DefaultHeight, float yOffset = DefaultYOffset, string tag = "HpBar")
        {
            return Create(target, width, height, yOffset, tag, sortingOrder);
        }

        /// <summary>对已存在的血条应用新的尺寸 / 标签 / 排序层（供幂等复用的 <see cref="Create"/> 分支使用）。</summary>
        private void ApplyParams(float width, float height, string tag, int sortingOrder)
        {
            if (width > 0f && height > 0f &&
                (!Mathf.Approximately(width, _width) || !Mathf.Approximately(height, _height)))
            {
                _width = width;
                _height = height;
                for (var i = transform.childCount - 1; i >= 0; i--)
                    Destroy(transform.GetChild(i).gameObject);
                _fill = null;
                _bgRenderer = null;
                _fillRenderer = null;
                _sortingOrder = sortingOrder;
                Build(_width, _height);
                Game.Logger?.Info("HpBar", $"血条尺寸已更新：{_width:F2}x{_height:F2}");
            }
            else if (sortingOrder != _sortingOrder)
            {
                // 尺寸没变但仍要换排序层：直接改两个渲染器（与 Build 同一处行为，避免重建）。
                _sortingOrder = sortingOrder;
                if (_bgRenderer != null) _bgRenderer.sortingOrder = _sortingOrder;
                if (_fillRenderer != null) _fillRenderer.sortingOrder = _sortingOrder;
                Game.Logger?.Info("HpBar", $"血条排序层已更新：{_sortingOrder}");
            }

            if (!string.IsNullOrEmpty(tag) && tag != _tag)
            {
                Game.Logger?.Info("HpBar", $"血条标签已更新：{_tag} -> {tag}");
                _tag = tag;
                gameObject.name = tag;
            }
        }

        private void Build(float width, float height)
        {
            var bg = CreateQuad("Bg", transform, new Vector3(width + 0.04f, height + 0.035f, 1f), Vector3.zero, _sortingOrder);
            _fill = CreateQuad("Fill", transform, new Vector3(width, height, 1f), new Vector3(0f, 0f, -0.01f), _sortingOrder);
            _bgRenderer = bg.GetComponent<MeshRenderer>();
            _fillRenderer = _fill.GetComponent<MeshRenderer>();
            AssignMaterial(_bgRenderer, BgColor);
            ApplyRatio();
        }

        /// <summary>按血量与上限更新（推荐入口：带上限才不会把 60 血的目标画成残血）。</summary>
        public void SetHp(float hp, float maxHp)
        {
            _hp = hp;
            _maxHp = maxHp;
            SetRatio(maxHp > 0.0001f ? hp / maxHp : 0f);
        }

        /// <summary>直接按比例更新 [0,1]。</summary>
        public void SetRatio(float ratio)
        {
            _ratio = Mathf.Clamp01(ratio);
            ApplyRatio();
        }

        /// <summary>显示 / 隐藏（如相机贴脸时隐藏角色视图，血条一起隐藏）。</summary>
        public void SetVisible(bool visible)
        {
            // 早退条件必须**同时**看缓存值与渲染器的实际状态：`Build()`（尺寸变化时重建 Quad）与
            // 池复用都会让 `_visible` 与 `renderer.enabled` 脱节 —— 只看缓存值时，
            // "取用同一个池对象后强制显示"会退化成**空操作**，血条永久不显示且不报错。
            if (_visible == visible && _bgRenderer != null && _bgRenderer.enabled == visible) return;
            _visible = visible;
            // 只切两个 Quad 的渲染器（原实现每次 GetComponentsInChildren 都新分配一个数组，
            // 频繁显隐会在热路径上持续产生 GC）。
            if (_bgRenderer != null) _bgRenderer.enabled = visible;
            if (_fillRenderer != null) _fillRenderer.enabled = visible;
        }

        /// <summary>调整离宿主节点的高度（米）。</summary>
        public void SetYOffset(float yOffset)
        {
            var local = transform.localPosition;
            transform.localPosition = new Vector3(local.x, yOffset, local.z);
        }

        private void ApplyRatio()
        {
            if (_fill == null) return;
            // 以 localScale.x 表达比例并左对齐（Quad 以中心为原点，所以要补回一半偏移）。
            _fill.localScale = new Vector3(_width * _ratio, _height, 1f);
            _fill.localPosition = new Vector3(-(_width * (1f - _ratio)) * 0.5f, 0f, -0.01f);

            // 材质从静态共享池取（每条血条各 new 两个材质会在实体多时线性增长、打断合批并抬高内存）；
            // 渲染器只换引用，不改共享材质本体。
            AssignMaterial(_fillRenderer, _ratio > 0.5f ? HighColor : _ratio > 0.25f ? MidColor : LowColor);
        }

        private void LateUpdate()
        {
            // 宿主没了（视图销毁）→ 血条自己跟着消失，不指望业务记得清理。
            if (_target == null)
            {
                Destroy(gameObject);
                return;
            }

            if (!_visible) return;

            if (_cam == null) _cam = Camera.main;
            if (_cam == null) return;

            // 广告牌：宿主根会随角色转身，所以必须每帧写世界朝向（LateUpdate 保证在移动结算之后）。
            // 只取相机朝向并保持 up=Vector3.up —— 直接复制相机全量 rotation 会把相机的 roll 也带过来（血条倾斜）。
            transform.rotation = Quaternion.LookRotation(_cam.transform.forward, Vector3.up);
        }

        // 静态共享的 Shader 与材质：Shader.Find 是慢操作、材质直接 new 会逐条累积
        //（每帧新建材质会打断合批）。全部血条复用同一批材质，随比例换引用、不改本体。
        private static Shader _sharedShader;
        private static bool _shaderResolved;
        private static readonly Dictionary<Color, Material> SharedMats = new();

        private static Shader ResolveShader()
        {
            if (_shaderResolved) return _sharedShader;
            _shaderResolved = true;

            _sharedShader = Shader.Find("Sprites/Default");
            if (_sharedShader == null) _sharedShader = Shader.Find("Unlit/Color");
            if (_sharedShader == null) _sharedShader = Shader.Find("Standard");
            if (_sharedShader == null)
            {
                // 非预期分支：shader 全找不到（打包裁剪 / 平台不支持）→ 留痕。
                // 此时不写材质，Quad 会退回 Unity 默认材质（外观不正确但至少可见，便于排查）。
                Game.Logger?.Warn("HpBar",
                    "未找到可用 Shader（Sprites/Default、Unlit/Color、Standard 全失败），血条将使用默认材质、外观可能不正确");
            }
            return _sharedShader;
        }

        private static Material SharedMaterial(Color color)
        {
            if (SharedMats.TryGetValue(color, out var mat) && mat != null) return mat;

            var shader = ResolveShader();
            if (shader == null) return null;

            mat = new Material(shader) { color = color, name = "HpBarShared" };
            SharedMats[color] = mat;
            return mat;
        }

        private static void AssignMaterial(MeshRenderer mr, Color color)
        {
            if (mr == null) return;
            var mat = SharedMaterial(color);
            if (mat != null && mr.sharedMaterial != mat)
                mr.sharedMaterial = mat;
        }

        private static Transform CreateQuad(string name, Transform parent, Vector3 scale, Vector3 localPos,
            int sortingOrder)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = scale;

            // ★ 必须去掉 Collider：否则相机的 SphereCast / 射线会打到血条上（镜头莫名拉近）。
            var col = go.GetComponent<Collider>();
            if (col != null) Destroy(col);

            var mr = go.GetComponent<MeshRenderer>();
            if (mr != null)
            {
                // ★ 必须显式写 sortingOrder：Quad 的 MeshRenderer 默认 0，而业务侧单位精灵常在高层级
                //   （例：本项目 1000+）⇒ 血条会被单位盖住（见类注释「① 缺陷」）。
                mr.sortingOrder = sortingOrder;
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr.receiveShadows = false;
            }
            return go.transform;
        }
    }
}
