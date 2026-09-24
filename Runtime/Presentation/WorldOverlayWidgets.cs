// ─────────────────────────────────────────────────────────────────────────────
// CloverEngine · Runtime/Presentation/WorldOverlayWidgets.cs
// 「世界 / 屏幕叠加层」四件通用件（**只做机制**：池化 · 定位 · 显隐 · 渐隐 · 跟随）。
//
// 来源（四件分别来自 clover-project-diablo2 的 UI 实现，逐项把**机制**收敛到引擎）：
//   · UI/GroundItemLabelView.cs → WorldProjectedLabelLayer（世界投影标签层：**按 id 池化复用、持久显示**）
//   · UI/EnemyBarView.cs        → ScreenTargetBar（屏幕顶部目标条）+ WorldNameplate（世界内名牌）
//   · UI/LevelEntryTitle.cs     → CenterAnnounceLayer（居中公告条：淡入 / 停留 / 淡出）
//   · UI/CursorView.cs          → SoftwareCursorLayer（软件多态光标：独立常驻画布 + 跟随鼠标）
//
// ⛔ 本文件**零项目专有**：不出现任何业务类名 / 文案 / 配色 / 字号 / 图标 / 素材路径 / 数据来源。
//   文字渲染（位图字模 / uGUI `Text` / 图片拼字都行）由调用方实现 `IOverlayLabelView` 注入；
//   文案、颜色、进度值、矩形几何、时长**逐次传参**；`nodeName` 这类只为可断言性存在，默认值中性。
//   屏幕 / 世界点换算**一律复用** `ScreenPointUtil`（⛔ 本文件不直接调 `RectTransformUtility`）。
//
// 为什么四件合在一个文件（与 `UIWidgets.cs` 同一先例，⛔ 不是新起的组织方式）：
//   `UIWidgets.cs` 已把 Toast / 飘字 / Loading / 确认框 / 红点 / 引导遮罩 / `WorldHpBar` 七件
//   「屏幕 / 世界空间通用件」合在一个文件。本件是同一类东西的续集 —— 四件共用同一条
//   「世界点 → `UIFactory.UICamera()` 投影 → `ScreenPointUtil` → 画布局部点」口径，
//   拆成四个文件只会多 4 个 `.cs.meta` 与 4 条 csproj 条目，换来的却是每件不足 300 行的碎片。
//
// 每件挡住的坑（失败模式都是"看着不对、却不报错"）：
//   · 世界点投影失败（相机缺失 / 点在相机背面 / 换算失败）⇒ **把该件藏起来**，⛔ 绝不画在错位置；
//   · 池化复用必须**每次写全** 文案 / 颜色 / 位置 / 显隐 —— 漏写一项就是"复用出上一帧的残留"；
//   · 目标条的进度填充走 `Slider.fillRect`（**锚点宽度**）而不是 `Image.fillAmount`：
//     无 sprite 的 Filled Image 会退化成整块矩形（扣血看不出来），且零报错；
//   · 渐隐用 `CanvasGroup.alpha`，⛔ 不逐帧 `SetText`（字模标签每次重排都会销毁重建字形节点 ⇒ 每帧 GC）；
//   · 软件光标**只有调用方说"贴图到位"**（`SetVisible(true)`）才隐藏系统光标 ——
//     否则会出现"连指针都看不见"，比系统默认箭头更糟。
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace CloverEngine
{
    /// <summary>一件世界投影标签的数据（<see cref="Id"/> 用于池化复用；世界点 / 文案 / 颜色由调用方给）。</summary>
    public struct WorldLabelItem
    {
        /// <summary>池化键：同 id 复用同一个标签节点。<c>&lt; 0</c> ⇒ 本次跳过（调用方不该传）。</summary>
        public int Id;

        /// <summary>世界落点（引擎负责投影到画布局部点）。</summary>
        public Vector3 World;

        /// <summary>文案；<c>null</c> ⇒ 本次不改文案（保留上一次）。</summary>
        public string Text;

        /// <summary>文字颜色。</summary>
        public Color Color;
    }

    /// <summary>
    /// 叠加层标签视图：引擎只认这 5 个动作 —— **画什么字 / 用什么字体 / 怎么排字全在实现方**
    /// （位图字模、uGUI <see cref="Text"/>、多张 Image 拼的字都行）。
    /// <para>引擎只写 <see cref="Rect"/> 的 <c>anchoredPosition</c> / <c>sizeDelta</c>，
    /// ⛔ 不改锚点 / pivot（那是实现方建节点时的排版决定）。</para>
    /// </summary>
    public interface IOverlayLabelView
    {
        /// <summary>该视图是否仍然有效（宿主未销毁）。false ⇒ 引擎丢弃它并重建。</summary>
        bool IsAlive { get; }

        /// <summary>根矩形（引擎写它的位置 / 尺寸）。</summary>
        RectTransform Rect { get; }

        /// <summary>改文案。</summary>
        void SetText(string text);

        /// <summary>改颜色。</summary>
        void SetColor(Color color);

        /// <summary>显隐。</summary>
        void SetActive(bool active);
    }

    /// <summary>
    /// 叠加层共用的「世界点 → 画布局部点」换算（四件口径唯一真相）。
    /// <para>口径：世界点 → 相机投影（<see cref="UIFactory.UICamera"/>）→ 屏幕点 → 画布局部点
    /// （<see cref="ScreenPointUtil.TryScreenToLocalInRect"/>，相机由它按**画布模式**取 —— Overlay 画布 ⇒ null）。</para>
    /// <para>相机缺失 / 点在相机背面 / 换算失败 ⇒ 返回 false 并**降频留痕**（调用方把该件藏起来）。</para>
    /// </summary>
    internal static class OverlayProjection
    {
        private const string CamKey = "worldoverlay.camera";
        private const string ConvertKey = "worldoverlay.convert";

        /// <summary>世界点 → 画布局部点；失败 ⇒ <c>false</c> 且 <paramref name="local"/> 为 <c>Vector2.zero</c>。</summary>
        public static bool TryWorldToCanvas(RectTransform canvas, Vector3 world, string tag, out Vector2 local)
        {
            local = Vector2.zero;
            if (canvas == null)
            {
                LogThrottle.WarnThrottled(tag, "worldoverlay.nocanvas",
                    $"{tag}：没有画布 ⇒ 世界点无法投影到画布局部点（本件不显示，⛔ 不画在错误位置）");
                return false;
            }

            var cam = UIFactory.UICamera();
            if (cam == null)
            {
                LogThrottle.WarnThrottled(tag, CamKey,
                    $"{tag}：找不到相机（Camera.main 缺失且无其它启用相机）⇒ 世界点无法投到屏幕（本件不显示）");
                return false;
            }

            var screen = cam.WorldToScreenPoint(world);
            if (screen.z < 0f) return false;   // 相机背面（切场景那一帧 / 视锥外）

            if (!ScreenPointUtil.TryScreenToLocalInRect(canvas, new Vector2(screen.x, screen.y), canvas, out local))
            {
                LogThrottle.WarnThrottled(tag, ConvertKey,
                    $"{tag}：屏幕点 → 画布局部点换算失败（本件位置不更新）");
                return false;
            }
            return true;
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    // 一、世界投影标签层（按 id 池化复用、持久显示）
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 世界投影标签层：把一批「id + 世界点 + 文案 + 颜色」摊到画布上，**按 id 池化复用**节点。
    /// <para>典型场景：地面物品名牌 / 敌人血条上的名字 / 任何"跟着世界物体走的一段字"。</para>
    /// <para>语义（调用方一次 <see cref="Apply"/> 一批）：
    /// <list type="bullet">
    ///   <item>载荷里的 id ⇒ 显示在它当前的世界投影处（投影失败 ⇒ 该件隐藏，⛔ 不画在错位置）；</item>
    ///   <item>载荷里**没出现**的 id ⇒ 隐藏（节点留着复用，不销毁）；</item>
    ///   <item>空 / null 载荷 ⇒ 全部隐藏（等价 <see cref="Clear"/>）。</item>
    /// </list>
    /// <c>id</c> 复用 ⇒ 同一 id 的文案 / 颜色 / 位置**每次都写全**（漏写会复用出上一帧的残留）。</para>
    /// </summary>
    public sealed class WorldProjectedLabelLayer
    {
        /// <summary>标签工厂：由调用方造一个"能改字 / 能改色 / 能显隐"的视图（引擎不关心它怎么画）。</summary>
        /// <param name="id">池化键（调用方可用它给节点命名，便于实机 / 离线断言）。</param>
        /// <param name="parent">宿主节点（引擎已按画布语义给好；通常是 HUD 根）。</param>
        public delegate IOverlayLabelView LabelFactory(int id, Transform parent);

        /// <summary>默认日志标签。</summary>
        public const string DefaultTag = "WorldLabelLayer";

        private readonly RectTransform _canvas;
        private readonly Transform _parent;
        private readonly LabelFactory _factory;
        private readonly string _tag;

        /// <summary>id → 标签节点（按 id 复用，避免每次刷新都新建 / 销毁）。</summary>
        private readonly Dictionary<int, IOverlayLabelView> _nodes = new Dictionary<int, IOverlayLabelView>();

        /// <summary>本次载荷里没出现的 id（收尾时隐藏）。</summary>
        private readonly List<int> _stale = new List<int>();

        /// <summary>当前显示中的标签数。</summary>
        public int VisibleCount { get; private set; }

        /// <summary>已建出的节点数（含当前隐藏的；自证 / 断言用）。</summary>
        public int PooledCount { get { return _nodes.Count; } }

        /// <param name="canvas">投影目标画布（<c>null</c> ⇒ 只建节点不定位置 + 降频 Warn）。</param>
        /// <param name="parent">标签节点的父节点。</param>
        /// <param name="factory">标签工厂（调用方注入渲染方式；返回 <c>null</c> ⇒ 本次跳过该 id）。</param>
        /// <param name="tag">日志标签（多套叠加层用不同标签便于排障）。</param>
        public WorldProjectedLabelLayer(RectTransform canvas, Transform parent, LabelFactory factory,
            string tag = DefaultTag)
        {
            _canvas = canvas;
            _parent = parent;
            _factory = factory;
            _tag = string.IsNullOrEmpty(tag) ? DefaultTag : tag;

            if (canvas == null)
            {
                LogThrottle.WarnOnce(_tag, "worldoverlay.layer.nocanvas",
                    $"{_tag}：构造时没给画布 ⇒ 标签仍会创建，但无法从世界坐标投到画布（不会跟随物体）");
            }
        }

        /// <summary>
        /// 应用一批标签（本层的唯一入口）。
        /// </summary>
        /// <returns>本次真正显示出来的条数。</returns>
        public int Apply(IList<WorldLabelItem> items)
        {
            if (items == null || items.Count == 0)
            {
                Clear();
                return 0;
            }

            _stale.Clear();
            foreach (var kv in _nodes) _stale.Add(kv.Key);

            var shown = 0;
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item.Id < 0) continue;                 // 非预期：调用方传了非法 id

                _stale.Remove(item.Id);

                var view = ViewOf(item.Id);
                if (view == null) continue;                // 工厂造不出来（已 Warn）

                if (item.Text != null) view.SetText(item.Text);
                view.SetColor(item.Color);

                if (OverlayProjection.TryWorldToCanvas(_canvas, item.World, _tag, out var local))
                {
                    var rt = view.Rect;
                    if (rt != null)
                    {
                        rt.anchoredPosition = local;
                        view.SetActive(true);
                        shown++;
                        continue;
                    }
                }

                // 非预期但可解释：相机缺失 / 点在相机背面 / 换算失败 ⇒ 本件藏起来（⛔ 不画在错误位置）
                view.SetActive(false);
            }

            for (var i = 0; i < _stale.Count; i++)
            {
                if (_nodes.TryGetValue(_stale[i], out var node) && node != null) node.SetActive(false);
            }

            VisibleCount = shown;
            Game.Logger?.Info(_tag, $"[{_tag}] 标签应用：载荷 {items.Count} 条 ⇒ 可见 {shown} 条、"
                + $"隐藏 {_stale.Count} 条（节点池 {_nodes.Count}）");
            return shown;
        }

        /// <summary>取某 id 已建出的视图（自证 / 断言用）；没有或已销毁 ⇒ false。</summary>
        public bool TryGetView(int id, out IOverlayLabelView view)
        {
            view = null;
            if (!_nodes.TryGetValue(id, out var found) || found == null || !found.IsAlive) return false;
            view = found;
            return true;
        }

        /// <summary>隐藏全部标签（离场 / 载荷为空）；节点保留复用。</summary>
        public void Clear()
        {
            foreach (var kv in _nodes)
            {
                var v = kv.Value;
                if (v != null && v.IsAlive) v.SetActive(false);
            }
            if (VisibleCount != 0)
                Game.Logger?.Info(_tag, $"[{_tag}] 标签全部隐藏（原可见 {VisibleCount} 条，节点池保留 {_nodes.Count}）");
            VisibleCount = 0;
        }

        /// <summary>销毁全部标签节点（宿主关闭 / 换场景）；层本身可继续用（下次 <see cref="Apply"/> 重建）。</summary>
        public void Dispose()
        {
            foreach (var kv in _nodes)
            {
                var v = kv.Value;
                if (v == null || !v.IsAlive) continue;
                var go = v.Rect != null ? v.Rect.gameObject : null;
                if (go != null) UnityEngine.Object.Destroy(go);
            }
            _nodes.Clear();
            _stale.Clear();
            VisibleCount = 0;
        }

        /// <summary>取（或建）某 id 的标签视图。</summary>
        private IOverlayLabelView ViewOf(int id)
        {
            if (_nodes.TryGetValue(id, out var exists) && exists != null && exists.IsAlive) return exists;

            if (_factory == null)
            {
                LogThrottle.WarnOnce(_tag, "worldoverlay.layer.nofactory",
                    $"{_tag}：没给标签工厂 ⇒ 标签建不出来（请把 LabelFactory 传进构造）");
                return null;
            }

            var created = _factory(id, _parent);
            if (created == null)
            {
                LogThrottle.WarnOnce(_tag, "worldoverlay.layer.createfail." + id,
                    $"{_tag}：标签工厂对 id={id} 返回 null ⇒ 该条不显示");
                return null;
            }

            created.SetActive(false);      // 建完先藏；等本次 Apply 决定位置与显隐
            _nodes[id] = created;
            Game.Logger?.Info(_tag, $"[{_tag}] 标签节点已创建：id={id}（节点池 {_nodes.Count}）");
            return created;
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    // 二、屏幕顶部目标条（名字 + 进度）
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>目标条的几何 / 配色（**全部由调用方给**，引擎不含任何默认配色与尺寸）。</summary>
    public struct ScreenTargetBarSpec
    {
        /// <summary>根节点名（自证 / 离线断言用）。</summary>
        public string NodeName;

        /// <summary>条矩形尺寸（画布单位）。</summary>
        public Vector2 Size;

        /// <summary>条中心（画布单位，中心锚点）。</summary>
        public Vector2 Pos;

        /// <summary>底色。</summary>
        public Color Background;

        /// <summary>填充色。</summary>
        public Color Fill;

        /// <summary>标题矩形尺寸（相对条中心）。</summary>
        public Vector2 TitleSize;

        /// <summary>标题中心相对条中心的偏移。</summary>
        public Vector2 TitlePos;

        /// <summary>标题节点名（默认 <c>Title</c>）。</summary>
        public string TitleNodeName;

        /// <summary>填充节点名（默认 <c>Fill</c>）。</summary>
        public string FillNodeName;
    }

    /// <summary>
    /// 屏幕顶部目标条：一条「背景 + 进度填充 + 标题文字」的定尺 uGUI 条，显示当前悬停 / 选中目标。
    /// <para>只做机制：**进度值 / 标题文案 / 颜色由调用方给**；几何与配色由 <see cref="ScreenTargetBarSpec"/> 注入。</para>
    /// <para>进度走 <see cref="Slider.fillRect"/>（锚点宽度），⛔ 不用 <c>Image.fillAmount</c> ——
    /// 无 sprite 的 Filled Image 会退化成整块矩形（"扣血看不出来"且零报错）。</para>
    /// <para>语义：<see cref="Show"/> 显示（连标题一起），<see cref="Hide"/> 整根藏起来（⛔ 不留半截）。</para>
    /// </summary>
    public sealed class ScreenTargetBar
    {
        /// <summary>标题工厂：调用方决定用什么渲染标题文字。</summary>
        public delegate IOverlayLabelView TitleFactory(RectTransform parent, string nodeName, Vector2 size, Vector2 pos);

        private readonly ScreenTargetBarSpec _spec;
        private readonly RectTransform _root;
        private readonly Slider _slider;
        private readonly Image _fill;
        private readonly IOverlayLabelView _title;

        private string _loggedTitle;
        private float _loggedValue = float.NaN;
        private float _loggedMax = float.NaN;

        /// <summary>整根是否可见。</summary>
        public bool IsVisible { get { return _root != null && _root.gameObject.activeSelf; } }

        /// <summary>当前标题文本（不可见 / 未建 ⇒ 空串）。</summary>
        public string Title
        {
            get { return IsVisible && _title != null ? _lastTitle ?? string.Empty : string.Empty; }
        }

        private string _lastTitle;

        /// <summary>当前进度值（未构建 ⇒ -1）。</summary>
        public float Value { get { return _slider != null ? _slider.value : -1f; } }

        /// <summary>当前进度上限（未构建 ⇒ -1）。</summary>
        public float MaxValue { get { return _slider != null ? _slider.maxValue : -1f; } }

        /// <summary>进度比例 [0,1]（上限 &lt;= 0 ⇒ 0）。</summary>
        public float Fill01
        {
            get
            {
                if (_slider == null || _slider.maxValue <= 0f) return 0f;
                return Mathf.Clamp01(_slider.value / _slider.maxValue);
            }
        }

        private ScreenTargetBar(ScreenTargetBarSpec spec, RectTransform root, Slider slider,
            Image fill, IOverlayLabelView title)
        {
            _spec = spec;
            _root = root;
            _slider = slider;
            _fill = fill;
            _title = title;
            _loggedTitle = null;
        }

        /// <summary>
        /// 造一条目标条（挂在 <paramref name="parent"/> 下）。
        /// </summary>
        /// <param name="canvas">所属画布（日志 / 自证用；本件不需要投影）。</param>
        /// <param name="parent">宿主节点。</param>
        /// <param name="spec">几何与配色（⛔ 引擎不提供默认值：全部由调用方给）。</param>
        /// <param name="titleFactory">标题工厂；返回 <c>null</c> ⇒ 建失败（打 Error 并返回 <c>null</c>）。</param>
        public static ScreenTargetBar Create(RectTransform canvas, Transform parent,
            ScreenTargetBarSpec spec, TitleFactory titleFactory)
        {
            if (parent == null)
            {
                Game.Logger?.Error("ScreenTargetBar", "Create 收到 null 父节点 ⇒ 目标条建不出来");
                return null;
            }
            if (titleFactory == null)
            {
                Game.Logger?.Error("ScreenTargetBar", "Create 没给标题工厂 ⇒ 目标条建不出来（标题是本件的一半）");
                return null;
            }

            var rootName = string.IsNullOrEmpty(spec.NodeName) ? "ScreenTargetBar" : spec.NodeName;
            var root = UIFactory.CreateCentered(rootName, parent, spec.Size, spec.Pos);

            // 层级：根 → Slider（铺满）→ { Background（铺满）, Fill Area（铺满）→ Fill（锚点宽度）, Title }
            var sliderRt = UIFactory.CreateNode("Slider", root);
            UIFactory.Stretch(sliderRt);

            var backRt = UIFactory.CreateNode("Background", sliderRt);
            UIFactory.Stretch(backRt);
            var back = backRt.gameObject.AddComponent<Image>();
            back.sprite = null;                    // 纯色 uGUI（原版同款：`m_Sprite` 为空）
            back.color = spec.Background;
            back.raycastTarget = false;

            var areaRt = UIFactory.CreateNode("Fill Area", sliderRt);
            UIFactory.Stretch(areaRt);
            var fillName = string.IsNullOrEmpty(spec.FillNodeName) ? "Fill" : spec.FillNodeName;
            var fillRt = UIFactory.CreateNode(fillName, areaRt);
            fillRt.anchorMin = fillRt.anchorMax = new Vector2(0f, 0f);
            fillRt.pivot = new Vector2(0.5f, 0.5f);
            fillRt.anchoredPosition = Vector2.zero;
            fillRt.sizeDelta = Vector2.zero;
            var fill = fillRt.gameObject.AddComponent<Image>();
            fill.sprite = null;
            fill.color = spec.Fill;
            fill.raycastTarget = false;

            var slider = sliderRt.gameObject.AddComponent<Slider>();
            slider.fillRect = fillRt;
            slider.handleRect = null;
            slider.direction = Slider.Direction.LeftToRight;
            slider.minValue = 0f;
            slider.maxValue = 1f;                  // 运行时必被调用方给的 maxValue 覆盖
            slider.wholeNumbers = false;
            slider.interactable = false;
            slider.transition = Selectable.Transition.None;
            slider.targetGraphic = null;
            slider.navigation = new Navigation { mode = Navigation.Mode.None };

            // Title 与 Slider 同级但排在它之后 ⇒ 画在最上层（几何与"挂在 Slider 下"等价，因为 Slider 铺满根）
            var titleName = string.IsNullOrEmpty(spec.TitleNodeName) ? "Title" : spec.TitleNodeName;
            var title = titleFactory(root, titleName, spec.TitleSize, spec.TitlePos);
            if (title == null)
            {
                UnityEngine.Object.Destroy(root.gameObject);
                Game.Logger?.Error("ScreenTargetBar", "标题工厂返回 null ⇒ 目标条已回滚，不半画");
                return null;
            }
            title.SetActive(false);

            var bar = new ScreenTargetBar(spec, root, slider, fill, title);
            root.gameObject.SetActive(false);
            Game.Logger?.Info("ScreenTargetBar", $"[目标条] 已建：{rootName} 尺寸={spec.Size} @ {spec.Pos}；"
                + $"底色={spec.Background} / 填充={spec.Fill}；标题 {titleName} 尺寸={spec.TitleSize} @ {spec.TitlePos}；"
                + $"画布={(canvas != null ? canvas.name : "<null>")}");
            return bar;
        }

        /// <summary>
        /// 显示（或刷新）目标条。
        /// </summary>
        /// <param name="title">标题文案（空 ⇒ 原样写入空串，由调用方的渲染实现决定怎么显示）。</param>
        /// <param name="value">当前进度值（与 <paramref name="maxValue"/> 同一单位）。</param>
        /// <param name="maxValue">进度上限；<c>&lt;= 0</c> ⇒ 内部按 1 处理（避免 Slider 除零）。</param>
        /// <param name="titleColor">标题颜色（如"精英怪金名"，颜色由调用方决定）。</param>
        public void Show(string title, float value, float maxValue, Color titleColor)
        {
            if (_root == null || _slider == null)
            {
                LogThrottle.WarnOnce("ScreenTargetBar", "screentargetbar.notbuilt",
                    "[目标条] 构件未就绪（Create 失败过）⇒ 本次不显示");
                return;
            }

            _root.gameObject.SetActive(true);
            _slider.maxValue = maxValue > 0f ? maxValue : 1f;
            _slider.value = value;
            if (_fill != null) _fill.color = _spec.Fill;

            _lastTitle = title;
            if (_title != null)
            {
                _title.SetText(title);
                _title.SetColor(titleColor);
                _title.SetActive(true);
            }

            // 只在"内容真的变了"时打一行（悬停期间每帧 Show 不该刷屏；变了就必须留痕）
            if (!string.Equals(_loggedTitle, title, StringComparison.Ordinal)
                || !Mathf.Approximately(_loggedValue, _slider.value)
                || !Mathf.Approximately(_loggedMax, _slider.maxValue))
            {
                _loggedTitle = title;
                _loggedValue = _slider.value;
                _loggedMax = _slider.maxValue;
                Game.Logger?.Info("ScreenTargetBar", $"[目标条] 显示：\"{title}\" 值={_slider.value}/{_slider.maxValue}"
                    + $"（比例 {Fill01:0.###}）标题色={titleColor} 矩形={(Vector2)_spec.Size} @ {_spec.Pos}");
            }
        }

        /// <summary>整根隐藏（⛔ 不留半截：根 + 标题一起关）。</summary>
        public void Hide()
        {
            if (_root != null && _root.gameObject.activeSelf)
                Game.Logger?.Info("ScreenTargetBar", $"[目标条] 隐藏（上次标题 \"{_lastTitle}\"）");
            if (_title != null) _title.SetActive(false);
            if (_root != null) _root.gameObject.SetActive(false);
            _loggedTitle = null;
            _loggedValue = float.NaN;
            _loggedMax = float.NaN;
        }

        /// <summary>销毁整根（宿主关闭）。</summary>
        public void Dispose()
        {
            if (_root != null) UnityEngine.Object.Destroy(_root.gameObject);
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    // 三、世界内名牌（跟着世界物体走的一块"底 + 字"，pivot 在底边中点）
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>世界内名牌的样式 / 几何（**全部由调用方给**）。</summary>
    public struct WorldNameplateSpec
    {
        /// <summary>根节点名（自证 / 离线断言用）。</summary>
        public string NodeName;

        /// <summary>底板色（如半透明黑底）；<c>a == 0</c> 也照画（调用方自己决定要不要底板）。</summary>
        public Color BackColor;

        /// <summary>文字节点名（默认 <c>Label</c>）。</summary>
        public string LabelNodeName;

        /// <summary>本件 pivot（如 <c>(0.5, 0)</c> = 锚点在底边中点、向上长）。</summary>
        public Vector2 Pivot;
    }

    /// <summary>
    /// 世界内名牌：**同一时刻只显示一块**（悬停 / 选中一个目标），跟着世界点走。
    /// <para>只做机制：定位（世界投影）· 显隐 · 跟随 · 底板随文字尺寸伸缩；
    /// 文案 / 颜色 / 尺寸量法 / 字体由调用方注入（尺寸量法走 <see cref="SizeMeasure"/>，因为"一串字有多宽"
    /// 是排版实现的事，引擎不猜）。</para>
    /// </summary>
    public sealed class WorldNameplate
    {
        /// <summary>文字工厂：调用方决定用什么渲染（与 <see cref="WorldProjectedLabelLayer.LabelFactory"/> 同构）。</summary>
        public delegate IOverlayLabelView LabelFactory(RectTransform parent, string nodeName);

        /// <summary>文案 → 底板 / 文字尺寸（画布单位）；<c>null</c> ⇒ 不自动改尺寸（由调用方自己管）。</summary>
        public delegate Vector2 SizeMeasure(string text);

        private readonly RectTransform _canvas;
        private readonly RectTransform _root;
        private readonly IOverlayLabelView _label;
        private readonly SizeMeasure _measure;
        private readonly Color _backColor;

        private string _lastText;
        private string _loggedText;

        /// <summary>本件是否可见。</summary>
        public bool IsVisible { get { return _root != null && _root.gameObject.activeSelf; } }

        /// <summary>当前文字（不可见 ⇒ 空串）。</summary>
        public string Text { get { return IsVisible ? _lastText ?? string.Empty : string.Empty; } }

        private WorldNameplate(RectTransform canvas, RectTransform root, IOverlayLabelView label,
            SizeMeasure measure, Color backColor)
        {
            _canvas = canvas;
            _root = root;
            _label = label;
            _measure = measure;
            _backColor = backColor;
        }

        /// <summary>
        /// 造一块世界内名牌（挂在 <paramref name="parent"/> 下，初始隐藏）。
        /// </summary>
        /// <param name="canvas">投影目标画布。</param>
        /// <param name="parent">宿主节点。</param>
        /// <param name="spec">样式 / pivot（⛔ 引擎不给默认配色）。</param>
        /// <param name="labelFactory">文字工厂；返回 <c>null</c> ⇒ 建失败。</param>
        /// <param name="measure">文案 → 尺寸；<c>null</c> ⇒ 不自动改尺寸。</param>
        public static WorldNameplate Create(RectTransform canvas, Transform parent, WorldNameplateSpec spec,
            LabelFactory labelFactory, SizeMeasure measure = null)
        {
            if (parent == null || labelFactory == null)
            {
                Game.Logger?.Error("WorldNameplate", "Create 收到 null 父节点 / 没给文字工厂 ⇒ 名牌建不出来");
                return null;
            }

            var rootName = string.IsNullOrEmpty(spec.NodeName) ? "WorldNameplate" : spec.NodeName;
            var root = UIFactory.CreateCentered(rootName, parent, Vector2.zero, Vector2.zero);
            if (root == null) return null;
            root.pivot = spec.Pivot;

            var back = root.gameObject.AddComponent<Image>();
            back.sprite = null;
            back.color = spec.BackColor;
            back.raycastTarget = false;

            var labelName = string.IsNullOrEmpty(spec.LabelNodeName) ? "Label" : spec.LabelNodeName;
            var label = labelFactory(root, labelName);
            if (label == null || label.Rect == null)
            {
                UnityEngine.Object.Destroy(root.gameObject);
                Game.Logger?.Error("WorldNameplate", "文字工厂返回 null / 视图没有根矩形 ⇒ 名牌已回滚，不半画");
                return null;
            }
            label.Rect.pivot = new Vector2(0.5f, 0f);
            label.Rect.anchoredPosition = Vector2.zero;
            label.SetActive(false);
            root.gameObject.SetActive(false);

            Game.Logger?.Info("WorldNameplate", $"[世界名牌] 已建：{rootName} pivot={spec.Pivot} 底板色={spec.BackColor}"
                + $"（尺寸由量法决定：{(measure != null ? "调用方注入" : "未注入 ⇒ 不自动改尺寸")}）");
            return new WorldNameplate(canvas, root, label, measure, spec.BackColor);
        }

        /// <summary>
        /// 在世界点处显示名牌。
        /// </summary>
        /// <returns>true = 已显示；false = 投影失败（本件隐藏，⛔ 不画在错位置）。</returns>
        public bool Show(Vector3 world, string text, Color color)
        {
            if (_root == null)
            {
                LogThrottle.WarnOnce("WorldNameplate", "worldnameplate.notbuilt",
                    "[世界名牌] 构件未就绪（Create 失败过）⇒ 本次不显示");
                return false;
            }

            if (!OverlayProjection.TryWorldToCanvas(_canvas, world, "WorldNameplate", out var local))
            {
                Hide();
                return false;
            }

            _root.gameObject.SetActive(true);
            _root.anchoredPosition = local;

            _lastText = text;
            if (_label != null)
            {
                _label.SetText(text);
                _label.SetColor(color);
                _label.SetActive(true);
            }

            // 底板 / 文字尺寸：量法由调用方注入（"一串字有多宽"是排版实现的事）
            if (_measure != null)
            {
                var size = _measure(text);
                _root.sizeDelta = size;
                if (_label != null && _label.Rect != null) _label.Rect.sizeDelta = size;
            }

            if (!string.Equals(_loggedText, text, StringComparison.Ordinal))
            {
                _loggedText = text;
                Game.Logger?.Info("WorldNameplate", $"[世界名牌] 显示：\"{text}\" 世界点={world} 画布点={local}"
                    + $" 尺寸={_root.sizeDelta} 底板={_backColor}");
            }
            return true;
        }

        /// <summary>隐藏（节点保留）。</summary>
        public void Hide()
        {
            if (_label != null) _label.SetActive(false);
            if (_root != null && _root.gameObject.activeSelf)
            {
                _root.gameObject.SetActive(false);
                Game.Logger?.Info("WorldNameplate", $"[世界名牌] 隐藏（上次 \"{_lastText}\"）");
            }
            _loggedText = null;
        }

        /// <summary>销毁（宿主关闭）。</summary>
        public void Dispose()
        {
            if (_root != null) UnityEngine.Object.Destroy(_root.gameObject);
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    // 四、居中公告条（淡入 / 停留 / 淡出）
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>居中公告条的几何与时长（**全部由调用方给**）。</summary>
    public struct CenterAnnounceSpec
    {
        /// <summary>根节点名（自证 / 离线断言用）。</summary>
        public string NodeName;

        /// <summary>外框尺寸（画布单位）。</summary>
        public Vector2 Size;

        /// <summary>外框中心（画布单位，中心锚点）。</summary>
        public Vector2 Pos;

        /// <summary>文字节点名（默认 <c>Label</c>）。</summary>
        public string LabelNodeName;

        /// <summary>淡入时长（秒）。</summary>
        public float FadeIn;

        /// <summary>满不透明停留时长（秒）。</summary>
        public float Hold;

        /// <summary>淡出时长（秒）。</summary>
        public float FadeOut;
    }

    /// <summary>
    /// 居中公告条：屏幕中央的一行大字，**淡入 → 停留 → 淡出**，走完自动隐藏。
    /// <para>只做机制：时长 / 几何由 <see cref="CenterAnnounceSpec"/> 给，文案 / 颜色 / 字体由调用方给
    /// （颜色在 <see cref="LabelFactory"/> 里定，因为它是"这一件看起来什么样"的一部分）。</para>
    /// <para>渐隐表达用 <see cref="CanvasGroup.alpha"/>（⛔ 不逐帧重排文字 —— 字模标签每次重排都会销毁重建字形节点）。</para>
    /// <para>透明度曲线是**纯函数** <see cref="AlphaAt(float, float, float, float)"/>，离线可逐点断言。</para>
    /// </summary>
    public sealed class CenterAnnounceLayer
    {
        /// <summary>文字工厂：调用方决定用什么渲染、什么颜色、什么字号。</summary>
        public delegate IOverlayLabelView LabelFactory(RectTransform parent, string nodeName);

        /// <summary>本件的总时长（秒）= 淡入 + 停留 + 淡出。</summary>
        public float TotalSeconds { get { return _fadeIn + _hold + _fadeOut; } }

        private readonly RectTransform _root;
        private readonly CanvasGroup _group;
        private readonly IOverlayLabelView _label;
        private readonly float _fadeIn;
        private readonly float _hold;
        private readonly float _fadeOut;

        private float _elapsed;
        private string _lastText;

        /// <summary>是否正在显示（true 期间 <see cref="Tick"/> 推透明度）。</summary>
        public bool IsShowing { get; private set; }

        /// <summary>自弹出起已过的秒数（隐藏后保留，便于读取"可见多久"）。</summary>
        public float Elapsed { get { return _elapsed; } }

        /// <summary>当前文案（未显示 ⇒ 空串）。</summary>
        public string Text { get { return IsShowing ? _lastText ?? string.Empty : string.Empty; } }

        /// <summary>当前透明度（由 <see cref="AlphaAt(float, float, float, float)"/> 得出）。</summary>
        public float Alpha { get { return _group != null ? _group.alpha : 0f; } }

        private CenterAnnounceLayer(RectTransform root, CanvasGroup group, IOverlayLabelView label,
            float fadeIn, float hold, float fadeOut)
        {
            _root = root;
            _group = group;
            _label = label;
            _fadeIn = fadeIn;
            _hold = hold;
            _fadeOut = fadeOut;
        }

        /// <summary>
        /// 造一条居中公告条（挂在 <paramref name="parent"/> 下，初始隐藏、透明）。
        /// </summary>
        /// <param name="parent">宿主节点（引擎把根节点建在它下面，并在最末位 siblings ⇒ 盖在已有构件之上）。</param>
        /// <param name="spec">几何与时长。</param>
        /// <param name="labelFactory">文字工厂；返回 <c>null</c> ⇒ 建失败。</param>
        public static CenterAnnounceLayer Create(Transform parent, CenterAnnounceSpec spec,
            LabelFactory labelFactory)
        {
            if (parent == null || labelFactory == null)
            {
                Game.Logger?.Error("CenterAnnounceLayer", "Create 收到 null 父节点 / 没给文字工厂 ⇒ 公告条建不出来");
                return null;
            }

            var rootName = string.IsNullOrEmpty(spec.NodeName) ? "CenterAnnounce" : spec.NodeName;
            var root = UIFactory.CreateCentered(rootName, parent, spec.Size, spec.Pos);
            if (root == null) return null;

            var group = root.gameObject.AddComponent<CanvasGroup>();
            group.interactable = false;      // 纯表现，不挡点击
            group.blocksRaycasts = false;
            group.alpha = 0f;

            var labelName = string.IsNullOrEmpty(spec.LabelNodeName) ? "Label" : spec.LabelNodeName;
            var label = labelFactory(root, labelName);
            if (label == null)
            {
                UnityEngine.Object.Destroy(root.gameObject);
                Game.Logger?.Error("CenterAnnounceLayer", "文字工厂返回 null ⇒ 公告条已回滚，不半画");
                return null;
            }
            label.SetActive(false);
            root.gameObject.SetActive(false);

            var layer = new CenterAnnounceLayer(root, group, label, spec.FadeIn, spec.Hold, spec.FadeOut);
            Game.Logger?.Info("CenterAnnounceLayer", $"[居中公告] 已建：{rootName} 尺寸={spec.Size} @ {spec.Pos}；"
                + $"总时长 {layer.TotalSeconds}s（淡入 {spec.FadeIn} + 停留 {spec.Hold} + 淡出 {spec.FadeOut}）");
            return layer;
        }

        /// <summary>
        /// 某一帧的透明度（**纯函数**，0..1）。
        /// <para>分段：<c>[0, fadeIn)</c> 线性 0→1；<c>[fadeIn, fadeIn+hold)</c> 恒 1；
        /// <c>[fadeIn+hold, 总时长)</c> 线性 1→0；之后 0。总时长 &lt;= 0 ⇒ 恒 0（没有时长就没有可见期）。</para>
        /// </summary>
        /// <param name="elapsed">自弹出起的秒数（负数 / NaN ⇒ 0 = 还没出现）。</param>
        /// <param name="fadeIn">淡入时长（秒，&gt;= 0）。</param>
        /// <param name="hold">停留时长（秒，&gt;= 0）。</param>
        /// <param name="fadeOut">淡出时长（秒，&gt;= 0）。</param>
        public static float AlphaAt(float elapsed, float fadeIn, float hold, float fadeOut)
        {
            if (float.IsNaN(elapsed) || elapsed <= 0f) return 0f;

            if (fadeIn < 0f) fadeIn = 0f;
            if (hold < 0f) hold = 0f;
            if (fadeOut < 0f) fadeOut = 0f;
            var total = fadeIn + hold + fadeOut;
            if (total <= 0f) return 0f;

            if (elapsed < fadeIn) return elapsed / fadeIn;
            if (elapsed < fadeIn + hold) return 1f;
            if (elapsed < total) return 1f - (elapsed - fadeIn - hold) / fadeOut;
            return 0f;
        }

        /// <summary>
        /// 弹出公告（已在显示则**重新开始**这一轮）。文案由调用方给（⛔ 引擎不自造文案）。
        /// </summary>
        /// <returns>true = 真的弹了；false = 构件不可用。</returns>
        public bool Show(string text)
        {
            if (_root == null)
            {
                LogThrottle.WarnOnce("CenterAnnounceLayer", "centerannounce.notbuilt",
                    "[居中公告] 控件未就绪（Create 失败过）⇒ 忽略本次公告");
                return false;
            }

            IsShowing = true;
            _elapsed = 0f;
            _lastText = text;

            _root.gameObject.SetActive(true);
            SetAlpha(0f);                        // 从全透明开始 ⇒ 才是"淡入"
            if (_label != null)
            {
                _label.SetText(text);
                _label.SetActive(true);
            }

            Game.Logger?.Info("CenterAnnounceLayer", $"[居中公告] 弹出：\"{text}\""
                + $"（淡入 {_fadeIn}s → 停留 {_hold}s → 淡出 {_fadeOut}s，合计 {TotalSeconds}s）");
            return true;
        }

        /// <summary>按时间推进淡入 / 停留 / 淡出；走完总时长即隐藏。</summary>
        /// <param name="dt">帧间隔（秒）；<c>&lt;= 0</c> ⇒ 冻结（暂停时不推进，与"游戏内时间"一致）。</param>
        public void Tick(float dt)
        {
            if (!IsShowing || _root == null) return;
            if (dt <= 0f) return;

            _elapsed += dt;

            if (_elapsed < TotalSeconds)
            {
                SetAlpha(AlphaAt(_elapsed, _fadeIn, _hold, _fadeOut));
                return;
            }

            // 走完总时长 ⇒ 隐藏（末端已淡到 0；⛔ 不留在屏幕上"透明但活着"）
            IsShowing = false;
            SetAlpha(0f);
            if (_label != null) _label.SetActive(false);
            _root.gameObject.SetActive(false);
            Game.Logger?.Info("CenterAnnounceLayer", $"[居中公告] 淡出结束并隐藏：\"{_lastText}\""
                + $"（可见 {_elapsed:0.00}s / 总时长 {TotalSeconds}s）");
        }

        /// <summary>立刻收起（换场景 / 重置）。</summary>
        public void Hide()
        {
            IsShowing = false;
            _elapsed = 0f;
            SetAlpha(0f);
            if (_label != null) _label.SetActive(false);
            if (_root != null) _root.gameObject.SetActive(false);
        }

        /// <summary>销毁（宿主关闭）。</summary>
        public void Dispose()
        {
            IsShowing = false;
            if (_root != null) UnityEngine.Object.Destroy(_root.gameObject);
        }

        private void SetAlpha(float a)
        {
            if (_group != null) _group.alpha = Mathf.Clamp01(a);
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    // 五、软件多态光标（独立常驻画布 + 跟随鼠标 + 系统光标接管）
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>软件光标的画布 / 节点参数（**全部由调用方给**：引擎不知道项目的参考分辨率）。</summary>
    public struct SoftwareCursorSpec
    {
        /// <summary>光标画布节点名（自证 / 离线断言用）。</summary>
        public string CanvasNodeName;

        /// <summary>光标画布的 <c>sortingOrder</c>（要比业务面板画的层高，否则被面板盖住）。</summary>
        public int SortingOrder;

        /// <summary>画布参考分辨率（与项目其余 UI 同一套，⛔ 引擎不写死 1920×1080）。</summary>
        public Vector2 ReferenceResolution;

        /// <summary>画布缩放匹配权重（0 = 按宽，1 = 按高，0.5 = 折中）。</summary>
        public float MatchWidthOrHeight;

        /// <summary>光标贴图节点名（默认 <c>Cursor</c>）。</summary>
        public string ImageNodeName;

        /// <summary>光标在画布上的尺寸（画布单位）。</summary>
        public Vector2 ImageSize;

        /// <summary>光标 pivot（如 <c>(0,1)</c> = 贴图左上角贴住鼠标点 = 箭头 hot spot）。</summary>
        public Vector2 ImagePivot;
    }

    /// <summary>
    /// 软件多态光标：自建一块**独立画布**（可被任何面板盖住之前先画在最上层）+ 一张跟随鼠标的
    /// <see cref="Image"/>，并在需要时接管系统光标。
    /// <para>只做机制：跟随鼠标 · 显隐（含系统光标隐藏 / 恢复）· 独立画布。
    /// **形态贴图 / 配色 / 有几态 / 何时该是哪一态**全在调用方（引擎只透传 <see cref="Image"/> 给它）。</para>
    /// <para>⛔ 为什么不用 `Cursor.SetCursor`：硬件光标要求贴图 Read/Write Enabled、且尺寸是屏幕像素、
    /// 不吃 <see cref="CanvasScaler"/> —— 与项目"全部 UI 按同一套参考分辨率缩放"的口径不同；</para>
    /// <para>安全口径：**只有调用方显式 <see cref="SetVisible"/>(true)（= 贴图真到位）才会隐藏系统光标**，
    /// 否则会出现"连指针都看不见"（比系统默认箭头更糟）。</para>
    /// </summary>
    public sealed class SoftwareCursorLayer
    {
        /// <summary>光标图工厂：调用方造那张 Image（贴图 / 色调 / 是否 unlit 都在它手里）。</summary>
        public delegate Image ImageFactory(RectTransform canvasRoot, string nodeName, Vector2 size, Vector2 pivot);

        private readonly RectTransform _canvas;
        private readonly Image _image;
        private bool _visible;
        private bool _systemCursorHidden;

        /// <summary>光标画布根（调用方一般不需要；自证 / 断言用）。</summary>
        public RectTransform Canvas { get { return _canvas; } }

        /// <summary>光标贴图节点（调用方拿它换 sprite / 色调 / 显隐）。</summary>
        public Image Image { get { return _image; } }

        /// <summary>当前是否显示（<c>false</c> 期间系统光标一定是恢复的）。</summary>
        public bool IsVisible { get { return _visible; } }

        /// <summary>系统光标当前是否被本件隐藏。</summary>
        public bool SystemCursorHidden { get { return _systemCursorHidden; } }

        private SoftwareCursorLayer(RectTransform canvas, Image image)
        {
            _canvas = canvas;
            _image = image;
        }

        /// <summary>
        /// 造一层软件光标（画布挂在 <paramref name="parent"/> 下，初始隐藏）。
        /// </summary>
        /// <param name="parent">宿主节点（调用方自持生命周期；如自安装 MonoBehaviour）。</param>
        /// <param name="spec">画布与节点参数。</param>
        /// <param name="factory">光标图工厂；返回 <c>null</c> ⇒ 建失败（此时**绝不**动系统光标）。</param>
        public static SoftwareCursorLayer Create(Transform parent, SoftwareCursorSpec spec, ImageFactory factory)
        {
            if (parent == null || factory == null)
            {
                Game.Logger?.Error("SoftwareCursorLayer", "Create 收到 null 父节点 / 没给光标图工厂 ⇒ 光标建不出来");
                return null;
            }

            var canvasName = string.IsNullOrEmpty(spec.CanvasNodeName) ? "SoftwareCursorCanvas" : spec.CanvasNodeName;
            // ⚠️ 用 `UIFactory.CreateNode`（它建出来的 GameObject 自带 RectTransform）再挂 Canvas。
            var canvasNode = UIFactory.CreateNode(canvasName, parent);
            var canvas = canvasNode.gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = spec.SortingOrder;

            var scaler = canvasNode.gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = spec.ReferenceResolution;
            scaler.matchWidthOrHeight = spec.MatchWidthOrHeight;

            var imageName = string.IsNullOrEmpty(spec.ImageNodeName) ? "Cursor" : spec.ImageNodeName;
            var image = factory(canvasNode, imageName, spec.ImageSize, spec.ImagePivot);
            if (image == null)
            {
                UnityEngine.Object.Destroy(canvasNode.gameObject);
                Game.Logger?.Error("SoftwareCursorLayer", "光标图工厂返回 null ⇒ 画布已回滚（系统光标保持不变）");
                return null;
            }

            var rt = image.rectTransform;
            rt.pivot = spec.ImagePivot;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            image.raycastTarget = false;      // 光标不许吃点击
            image.enabled = false;            // 贴图到位前不显示（也绝不藏系统光标）

            var layer = new SoftwareCursorLayer(canvasNode, image);
            Game.Logger?.Info("SoftwareCursorLayer", $"[软件光标] 画布已建：{canvasName} sortingOrder={spec.SortingOrder}"
                + $" 参考分辨率={spec.ReferenceResolution} match={spec.MatchWidthOrHeight}；"
                + $"光标节点 {imageName} 尺寸={spec.ImageSize} pivot={spec.ImagePivot}（初始隐藏，系统光标未动）");
            return layer;
        }

        /// <summary>
        /// 跟随鼠标：把屏幕点换算到光标画布局部点并写下位置。
        /// </summary>
        /// <returns>true = 位置已更新；false = 换算失败（本帧不动，⛔ 不把光标弹到原点）。</returns>
        public bool Follow(Vector2 screenPos)
        {
            if (_canvas == null || _image == null) return false;
            if (!ScreenPointUtil.TryScreenToLocalInRect(_canvas, screenPos, _canvas, out var local)) return false;
            _image.rectTransform.anchoredPosition = local;
            return true;
        }

        /// <summary>
        /// 显示 / 隐藏：<paramref name="visible"/>=true ⇒ 画自己的光标**并隐藏系统光标**；
        /// false ⇒ 只藏自己的光标**并恢复系统光标**（⛔ 绝不出现"两个光标"或"一个都没有"）。
        /// </summary>
        public void SetVisible(bool visible)
        {
            if (_image != null) _image.enabled = visible;
            SetSystemCursorHidden(visible);
            if (_visible != visible)
            {
                _visible = visible;
                Game.Logger?.Info("SoftwareCursorLayer", visible
                    ? "[软件光标] 已接管（显示自绘光标 + 隐藏系统光标）"
                    : "[软件光标] 已放开（隐藏自绘光标 + 恢复系统光标；贴图未到位 / 非游戏内 / 已销毁）");
            }
        }

        /// <summary>单独控制系统光标（幂等；状态变化才写 <see cref="UnityEngine.Cursor.visible"/> 并留痕）。</summary>
        public void SetSystemCursorHidden(bool hidden)
        {
            if (_systemCursorHidden == hidden) return;
            _systemCursorHidden = hidden;
            Cursor.visible = !hidden;
            Game.Logger?.Info("SoftwareCursorLayer", hidden
                ? "系统光标已隐藏（改由本层自绘光标）"
                : "系统光标已恢复");
        }

        /// <summary>销毁画布（**并保证系统光标回到可见** —— 绝不把玩家的指针留在"藏"的状态）。</summary>
        public void Dispose()
        {
            if (_image != null) _image.enabled = false;
            _visible = false;
            SetSystemCursorHidden(false);
            if (_canvas != null) UnityEngine.Object.Destroy(_canvas.gameObject);
        }
    }
}
