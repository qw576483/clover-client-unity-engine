// ─────────────────────────────────────────────────────────────────────────────
// CloverEngine · Runtime/Core/ScreenPointUtil.cs
// 指针/屏幕点 ↔ 画布矩形 / 世界点 换算的**统一入口**（含"正交相机到地面"的退化口径）。
//
// 核心规则（唯一实现处）：`canvas = context.GetComponentInParent<Canvas>()；
//   if (canvas == null || canvas.renderMode == ScreenSpaceOverlay) return null; return canvas.worldCamera;`
//   —— Overlay 画布不可用相机，其余画布模式用画布自己的 `worldCamera`。
//   调用点（共 6 处）：`ScreenPointToWorldPointInRectangle` / `ScreenPointToLocalPointInRectangle` /
//   `RectangleContainsScreenPoint` —— 拖拽幽灵体定位、拖出列表上沿判定、命中测试、落点格换算。
//   另：`IsoLayout.ScreenToWorldOnGround`（`Runtime/Core/IsoLayout.cs:104-125`）是**同一条**
//   "正交 → 地面"规则的**格坐标版**（它的退化兜底写死 10，且顺带产出格坐标）。
//
// ★ 通用性依据（为什么落 Core）：
//   ① 这是 UI / View / 输入三处都要用的**只读换算**，而模块之间禁止互相引用（结构规则 §2.1）
//      ⇒ 只有放 `Core` 才能被所有模块共用（`Core` 已直接用 `RectTransform`：
//      `PresentationContracts.cs:198` 的 `ShowGuide(RectTransform, …)`；`Core` 已引用
//      `UnityEngine.UIModule`，`Canvas` / `RectTransformUtility` 都在其中）。
//   ② 越界代价：`RectTransformUtility` 收**非空**相机时会把屏幕点当成"相机视锥里的一个方向"
//      再投到画布平面 ⇒ 与 Overlay 画布（世界坐标**就是屏幕像素**）相差一次相机投影。
//      三份重复实现各自把这条写了一遍注释 ⇒ 口径必须统一在一处（本件）。
//
// ★ 实机读数（2026-09-22）：
//   常驻画布 `canvas.renderMode = ScreenSpaceOverlay`（引擎 `Runtime/Presentation/UI.cs:49`），其世界坐标
//   **就是屏幕像素**。实测（1080×1920 画布 + 正交半高 16 的场地相机，相机在 (0,0,-10)）：
//   某 UI 元素中心的真屏幕点 =(214,221)，
//   `RectangleContainsScreenPoint(rect, (214,221), mainCam) = False`、传 `null` 时为 `True` ⇒
//   命中测试返回 -1 ⇒ **按下根本不进入拖拽链**（症状 = "卡牌拖不动 / 放不上战场"）。
//   ⇒ **⛔ 相机必须按画布模式取，不是"取一台相机就完事"**：Overlay ⇒ null；
//     ScreenSpaceCamera / WorldSpace 才用画布自己的 `worldCamera`。本件的
//     <see cref="CameraForCanvas"/> / <see cref="CameraForUi"/> 就是这条口径的唯一实现。
//   ⚠️ 与之**相反**的一条别混：把**世界点投到屏幕**（再转画布局部点）要的**正是**相机的投影，用
//     `UIFactory.UICamera()`（`Runtime/Presentation/UIWidgets.cs:261-266`，引擎给 UI 侧取世界相机
//     的官方入口）—— 两件事，⛔ 不要互换。
//
// ★ "正交 + depth ≈ 0 退化为固定值"的口径（本件 <see cref="TryScreenToGround"/>）：
//   正交相机下"屏幕点 → 地面 z=0"需要**显式给"相机到地面的距离"**，即 `depth = -camera.position.z`；
//   少了它点击位置会整体偏移（`IsoLayout.ScreenToWorldOnGround` 的注释同款提醒）。
//   当 `camera.position.z ≈ 0` 时该距离退化为 0 ⇒ `ScreenToWorldPoint` 恒返回同一个点（丢失 y 信息），
//   所以必须**退化为固定值**（本件把它做**参数** <c>fallbackDepth</c>，默认 10；`IsoLayout` 那份写死 10）。
//   ⛔ 唯一真相包的边界：面向**格坐标**的换算请用 `IsoLayout.ScreenToWorldOnGround` / `ScreenToGrid`；
//     本件只做「屏幕 ↔ 画布矩形 / 世界点」与「正交屏幕 → 地面世界点（退化值可参数化）」。
//   ★ 裁决（2026-09-24）：两份**保持两份、不收敛**。理由与前提：
//     · 「正交 → 地面」是同一条规则的两份实现 —— 本件 fallback **参数化**、只出**世界点**（面向 UI / 输入侧）；
//       `IsoLayout` 那份 fallback **写死 `10`**、顺带产出**格**坐标（面向格子的调用点）。
//     · ⛔ **将来若要收敛，前提是先给 `IsoLayout` 补 static 入口** —— 否则调用方（UI / 输入侧）为了拿一个
//       世界点，得先 `new IsoLayout(...)` 填齐 4 个它根本用不到的构造参（格宽 / 格高 / 排序基准 / 步长）。
//       即：收敛的**前置条件**是 `IsoLayout` 自己先提供一个不需要实例的等价入口，
//       ⛔ 而不是把调用方赶去填 4 个用不到的参数。
//
// 失效模式：
//   · `rect` 为 null ⇒ 一律返回 false + 降频 Warn（返回"成功 + 零向量"会让调用方把元素摆到原点）。
//   · 画布模式取错（把场地相机喂进 Overlay 画布的换算）**不会抛异常**，只会让命中**恒为 false**
//     （"点了没反应"）或让元素整体偏一次投影 —— 这是本件存在的首要原因。
//   · `context` 为 null（面板还没挂到画布上）⇒ 取不到画布 ⇒ 按 Overlay 处理（null 相机）。
// ─────────────────────────────────────────────────────────────────────────────

using UnityEngine;

namespace CloverEngine
{
    /// <summary>
    /// 屏幕点 ↔ 画布矩形 / 世界点换算的统一入口（+ 正交"屏幕 → 地面"的退化口径）。
    ///
    /// <para>
    /// 所有 <c>Try*</c> 入口：失败返回 <c>false</c> 并把 <c>out</c> 置零；**不抛异常**
    /// （拖拽链路上"抛出去"会打断 uGUI 事件派发）。
    /// </para>
    /// <para>无状态、主线程使用（内部只读 `Canvas` / `RectTransform`，不改任何状态）。</para>
    /// </summary>
    public static class ScreenPointUtil
    {
        /// <summary>日志标签。</summary>
        public const string Tag = "ScreenPointUtil";

        /// <summary>`camera.position.z ≈ 0`（到地面距离退化为 0）时使用的兜底距离。</summary>
        public const float DefaultFallbackDepth = 10f;

        /// <summary>
        /// 按**画布模式**取"屏幕点换取世界/矩形点"该用的相机（本件的核心口径，见文件头）。
        /// <para>Overlay ⇒ <c>null</c>（它的世界坐标就是屏幕像素，不能再投一次）；否则 ⇒ 画布自己的 <c>worldCamera</c>。</para>
        /// </summary>
        /// <param name="canvas">画布；<c>null</c> ⇒ 返回 <c>null</c>（调用方按"取不到画布"处理）。</param>
        public static Camera CameraForCanvas(Canvas canvas)
        {
            if (canvas == null) return null;
            if (canvas.renderMode == RenderMode.ScreenSpaceOverlay) return null;
            return canvas.worldCamera;
        }

        /// <summary>
        /// 按**画布模式**取相机（从 <paramref name="context"/> 向上找画布，口径同 <see cref="CameraForCanvas"/>）。
        /// </summary>
        /// <param name="context">画布下的任一组件（面板自身 / 它的根节点）；<c>null</c> ⇒ 返回 <c>null</c>（按 Overlay 口径）。</param>
        public static Camera CameraForUi(Component context)
        {
            if (context == null) return null;
            return CameraForCanvas(context.GetComponentInParent<Canvas>());
        }

        /// <summary>屏幕点 → 矩形所在平面上的**世界点**（拖幽灵体 / 定位子节点用）。</summary>
        /// <param name="rect">目标矩形（画布下的节点或画布本身）。</param>
        /// <param name="screen">屏幕坐标。</param>
        /// <param name="cam">由 <see cref="CameraForUi"/> 取；Overlay 画布传 <c>null</c>（⛔ 别传场地相机）。</param>
        /// <param name="world">输出：世界点（含 z）。</param>
        public static bool TryScreenToWorldInRect(RectTransform rect, Vector2 screen, Camera cam, out Vector3 world)
        {
            world = Vector3.zero;
            if (rect == null)
            {
                LogThrottle.WarnThrottled(Tag, "world.rect-null",
                    "TryScreenToWorldInRect: rect 为 null ⇒ 本次换算跳过（返回值会骗人，所以返回 false）");
                return false;
            }

            return RectTransformUtility.ScreenPointToWorldPointInRectangle(rect, screen, cam, out world);
        }

        /// <summary>屏幕点 → 世界点（相机按 <paramref name="context"/> 的画布模式自动取）。</summary>
        /// <param name="rect">目标矩形。</param>
        /// <param name="screen">屏幕坐标。</param>
        /// <param name="context">画布下的任一组件（取相机用）。</param>
        /// <param name="world">输出：世界点。</param>
        public static bool TryScreenToWorldInRect(RectTransform rect, Vector2 screen, Component context, out Vector3 world)
            => TryScreenToWorldInRect(rect, screen, CameraForUi(context), out world);

        /// <summary>屏幕点 → 矩形内的**局部点**（命中测试 / 列表内定位用；口径随锚点轴心，⛔ 别自己拿 anchoredPosition 凑）。</summary>
        /// <param name="rect">目标矩形。</param>
        /// <param name="screen">屏幕坐标。</param>
        /// <param name="cam">由 <see cref="CameraForUi"/> 取；Overlay 画布传 <c>null</c>。</param>
        /// <param name="local">输出：局部点。</param>
        public static bool TryScreenToLocalInRect(RectTransform rect, Vector2 screen, Camera cam, out Vector2 local)
        {
            local = Vector2.zero;
            if (rect == null)
            {
                LogThrottle.WarnThrottled(Tag, "local.rect-null",
                    "TryScreenToLocalInRect: rect 为 null ⇒ 本次换算跳过");
                return false;
            }

            return RectTransformUtility.ScreenPointToLocalPointInRectangle(rect, screen, cam, out local);
        }

        /// <summary>屏幕点 → 矩形内局部点（相机按 <paramref name="context"/> 的画布模式自动取）。</summary>
        public static bool TryScreenToLocalInRect(RectTransform rect, Vector2 screen, Component context, out Vector2 local)
            => TryScreenToLocalInRect(rect, screen, CameraForUi(context), out local);

        /// <summary>屏幕点是否落在矩形的**屏幕矩形**内（命中测试；⛔ 相机必须按画布模式取，见文件头口径）。</summary>
        public static bool ContainsScreenPoint(RectTransform rect, Vector2 screen, Camera cam)
        {
            if (rect == null)
            {
                LogThrottle.WarnThrottled(Tag, "contains.rect-null",
                    "ContainsScreenPoint: rect 为 null ⇒ 返回 false");
                return false;
            }

            return RectTransformUtility.RectangleContainsScreenPoint(rect, screen, cam);
        }

        /// <summary>屏幕点是否落在矩形的屏幕矩形内（相机按 <paramref name="context"/> 的画布模式自动取）。</summary>
        public static bool ContainsScreenPoint(RectTransform rect, Vector2 screen, Component context)
            => ContainsScreenPoint(rect, screen, CameraForUi(context));

        /// <summary>
        /// 屏幕点 → **地面（z = 0）世界点**（正交相机）。
        ///
        /// <para>
        /// 契约：<c>depth = -cam.transform.position.z</c>；`Mathf.Approximately(depth, 0)` ⇒
        /// **退化为固定值** <paramref name="fallbackDepth"/>（否则 <c>ScreenToWorldPoint</c> 恒返回同一点，
        /// 表现为"点击位置不随鼠标移动"）。相机为 null ⇒ 返回 false + 降频 Warn（⛔ 不返回 (0,0,0) 当成功）。
        /// </para>
        /// <para>
        /// ⚠️ 与 `IsoLayout.ScreenToWorldOnGround` 的关系：**同一条"正交 → 地面"规则的两份实现** ——
        /// 那边 fallback 写死 `10` 且直接产出**格**坐标（面向格子的调用点用），本件把 fallback **参数化**、
        /// 只给**世界点**（面向 UI / 输入侧）。**评审裁决 = 保持两份、不收敛**；**将来若要收敛，
        /// 前提是先给 `IsoLayout` 补 static 入口**（否则调用方要为用不到的方法填 4 个构造参）——
        /// 全文与理由见本文件头注释「裁决」段。
        /// </para>
        /// </summary>
        /// <param name="cam">主相机（**应为正交**；非正交会降频 Warn 后照算）。</param>
        /// <param name="screen">屏幕坐标。</param>
        /// <param name="world">输出：地面世界点（z 恒 0）。</param>
        /// <param name="fallbackDepth">`depth ≈ 0` 时的兜底距离；&lt;=0 或非有限 ⇒ <see cref="DefaultFallbackDepth"/>。</param>
        public static bool TryScreenToGround(Camera cam, Vector2 screen, out Vector3 world,
            float fallbackDepth = DefaultFallbackDepth)
        {
            world = Vector3.zero;
            if (cam == null)
            {
                LogThrottle.WarnThrottled(Tag, "ground.nocam",
                    "TryScreenToGround: 相机为 null（Camera.main 没找到？）⇒ 返回 false；" +
                    "⛔ 调用方不要拿 (0,0,0) 当落点，宁可不发请求");
                return false;
            }

            if (!cam.orthographic)
            {
                // 非预期分支：该换算的前提是正交（透视下"到地面的距离"不是常数）
                LogThrottle.WarnThrottled(Tag, "ground.nonortho",
                    $"TryScreenToGround: 相机 {cam.name} 不是正交 ⇒ 换算可能整体偏移（前提是正交）");
            }

            var depth = -cam.transform.position.z;
            if (Mathf.Approximately(depth, 0f) || !IsUsableDepth(fallbackDepth))
            {
                var fallback = IsUsableDepth(fallbackDepth) ? fallbackDepth : DefaultFallbackDepth;
                if (Mathf.Approximately(depth, 0f))
                {
                    LogThrottle.WarnThrottled(Tag, "ground.zerodepth",
                        $"TryScreenToGround: 相机 z={cam.transform.position.z} ⇒ 到地面距离退化为 0，" +
                        $"按 {fallback} 处理（否则点击位置不随鼠标移动）");
                }
                depth = fallback;
            }

            var p = cam.ScreenToWorldPoint(new Vector3(screen.x, screen.y, depth));
            p.z = 0f;
            world = p;
            return true;
        }

        private static bool IsUsableDepth(float depth) => depth > 0f && !float.IsInfinity(depth) && !float.IsNaN(depth);
    }
}
