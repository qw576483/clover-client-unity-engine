// ─────────────────────────────────────────────────────────────────────────────
// CloverEngine · Runtime/Presentation/UIPanelGuards.cs
// 面板参数取值守卫：把「OnOpen(param) 的载荷缺失 / 类型不符 ⇒ 留痕 + 降级，⛔ 不抛异常」
// 收敛成一处通用实现，项目侧只留一个 per-tag 薄转发。
//
// 出处：Diablo2 项目 `client/Assets/Scripts/UI/UiLog.cs` 的 `Require<T>` / `RequireInt`
//   —— 原语义逐条照搬（param == null ⇒ 留痕 + 返回 null / 兜底值；类型不符 ⇒ 留痕 + 返回 null / 兜底值；
//   失败一律走「面板按空数据打开」的降级分支，不崩）。唯一差异：留痕从「每次裸 Warn」改为
//   「同一 (tag, 面板, 原因) 只报一次」（见下面语义约束 ②，那是本件下沉时要治的刷屏）。
//
// 为什么下沉：`UIPanel.OnOpen(param)` 的契约是「param 在 `Awake` 之后才到」⇒ 缺参数不是崩溃，
//   而是「打开方漏传 / 传错 DTO」的降级场景。每个项目都会把这套「判空 + 打日志 + 走降级」的
//   样板各写一遍，而**降级口径**（返回什么 / 要不要限频）与**日志口径**（tag 怎么写）各写各的
//   ⇒ 新项目必再踩一次（样板里最容易漏的两条：① 漏判类型 ⇒ 强转抛异常；② 裸 Warn ⇒ 面板重开刷屏）。
//   收敛到引擎后项目侧只剩一行转发，口径由引擎保证（同 `LogThrottle` 的 D 桶判定）。
//
// 语义约束（改一条 = 语义漂移）：
//   ① **永不抛异常**：载荷缺失 / 类型不符 / `panelName` 为空，一律只走降级分支。
//   ② 留痕**必须**走 `Game.Logger` + `LogThrottle`（⛔ 不裸 Warn）：同一 `(tag, 面板, 原因)` **只报一次**
//      —— 面板重开 / 每帧取参都不会刷屏；tag 缺省用引擎自己的 UI 子系统 tag `"UI"`（与 `UI.cs` 同源）。
//   ③ ⛔ **零项目专有**：tag 与 panelName **由调用方给**，本件不含任何面板类名 / 项目文案 /
//      项目 tag 白名单（合法 tag 集是每个项目自己的约定，见 `LogThrottle.cs:9-11` 的同口径说明）。
//   ④ 两档语义分开：`TryGet<T>` = **纯探测**（不产生任何日志，供「有则用、无则走默认」的场景）；
//      `Require<T>` / `RequireValue<T>` = 探测 + 留痕 + 失败返回安全值（`default` / 调用方给的兜底值）。
//   ⑤ 泛型**不加 `class` 约束**：值类型载荷（如 `int skillId`）同样走本件，不必各项目再写一个
//      `RequireInt` 的平行实现（§4.4 复用规则第 4 条：已有能力不够用时优先扩展原实现）。
//
// 用法 + 首个消费方：
//   面板侧（一次性）——`Require` 拿引用型载荷，缺失时返回 `null` 让面板走空数据分支：
//     if (!UIPanelGuards.Require(param, nameof(XxxPanel), out XxxArgs args)) args = XxxArgs.Empty;
//   值类型载荷（缺失时用兜底值）：
//     var skillId = UIPanelGuards.RequireValue(param, nameof(XxxPanel), fallback: 0);
//   首个消费方 = Diablo2 的薄转发 `clover-project-diablo2/client/Assets/Scripts/UI/UiLog.cs`
//     （`UiLog.Require<T>` / `UiLog.RequireInt` 转发到本件，调用点零改动）。
// ─────────────────────────────────────────────────────────────────────────────

namespace CloverEngine
{
    /// <summary>
    /// 面板参数取值守卫：`OnOpen(param)` 载荷的**类型探测 + 缺失留痕 + 降级取值**。
    /// <para>
    /// **⛔ 永不抛异常**：载荷为 <c>null</c> / 类型不符 / 面板名为空，都只返回 <c>false</c>（或兜底值），
    /// 由调用方走降级分支（面板按空数据打开）。
    /// </para>
    /// <para>
    /// **留痕口径**：走 <see cref="LogThrottle.WarnOnce"/>（其内部即 <c>Game.Logger</c>），
    /// 同一 <c>(tag, 面板, 原因)</c> **只报一次** —— 面板重开 / 每帧取参不刷屏；
    /// <see cref="TryGet{T}"/> 是纯探测，**不产生任何日志**。
    /// </para>
    /// <para>
    /// **⛔ 零项目专有**：<c>tag</c> 与 <c>panelName</c> 由调用方传入，本件不含任何面板类名或文案。
    /// 泛型**不加 <c>class</c> 约束**，值类型载荷（如 <c>int</c>）也走本件。
    /// </para>
    /// </summary>
    public static class UIPanelGuards
    {
        /// <summary>
        /// 留痕的缺省 tag = 引擎 UI 子系统 tag <c>"UI"</c>（与 <c>UI.cs</c> 内既有日志同源）。
        /// 项目有自己的 tag 约定时由调用方显式传入覆盖。
        /// </summary>
        public const string DefaultTag = "UI";

        /// <summary>
        /// **纯探测（不产生任何日志）**：<paramref name="param"/> 是 <typeparamref name="T"/> ⇒
        /// 通过 <paramref name="value"/> 交出并返回 <c>true</c>；否则 <paramref name="value"/> = <c>default</c>
        /// 并返回 <c>false</c>。适合「有则用、无则走默认」而不需要留痕的场景。
        /// <para>⛔ 不抛异常：<paramref name="param"/> 为 <c>null</c> 时对引用型 <typeparamref name="T"/> 返回 false。</para>
        /// </summary>
        public static bool TryGet<T>(object param, out T value)
        {
            if (param is T typed)
            {
                value = typed;
                return true;
            }

            value = default;
            return false;
        }

        /// <summary>
        /// 校验 <c>OnOpen(param)</c> 的载荷：类型不符 / 为空 ⇒ **留痕（只报一次）并返回 <c>false</c>**，
        /// <paramref name="value"/> = <c>default</c>（引用型即 <c>null</c> ⇒ 面板按空数据打开，不崩）。
        /// <para>tag 用缺省值 <see cref="DefaultTag"/>；需要项目自己的 tag 时用三参重载。</para>
        /// </summary>
        /// <typeparam name="T">期望的载荷类型。</typeparam>
        /// <param name="param"><c>OnOpen</c> 收到的 object。</param>
        /// <param name="panelName">面板名（日志里点名，便于定位；由调用方给，如 <c>nameof(XxxPanel)</c>）。</param>
        /// <param name="value">通过时 = 载荷；失败时 = <c>default</c>。</param>
        /// <returns>载荷可用 ⇒ <c>true</c>；已走降级 ⇒ <c>false</c>。</returns>
        public static bool Require<T>(object param, string panelName, out T value)
            => Require(param, panelName, DefaultTag, out value);

        /// <summary>
        /// 同 <see cref="Require{T}(object,string,out T)"/>，但留痕 tag 由调用方指定
        /// （面板重开时同名 key **只报一次**）。
        /// </summary>
        /// <param name="tag">日志 tag（由调用方给，⛔ 引擎不写死项目 tag）；空 / <c>null</c> ⇒ 回落 <see cref="DefaultTag"/>。</param>
        public static bool Require<T>(object param, string panelName, string tag, out T value)
        {
            if (param is T typed)
            {
                value = typed;
                return true;
            }

            value = default;
            WarnPayloadUnusable(typeof(T).Name, param, panelName, tag);
            return false;
        }

        /// <summary>
        /// 校验 <c>OnOpen(param)</c> 的**值类型**载荷（如 <c>int skillId</c>）：缺失 / 类型不符 ⇒
        /// **留痕（只报一次）并返回调用方给的兜底值**（⛔ 不抛异常）。
        /// <para>tag 用缺省值 <see cref="DefaultTag"/>；需要项目自己的 tag 时用四参重载。</para>
        /// </summary>
        /// <param name="fallback">载荷不可用时的安全值（由调用方给，⛔ 引擎不替业务定数值）。</param>
        public static T RequireValue<T>(object param, string panelName, T fallback)
            => RequireValue(param, panelName, fallback, DefaultTag);

        /// <summary>
        /// 同 <see cref="RequireValue{T}(object,string,T)"/>，但留痕 tag 由调用方指定
        /// （面板重开时同名 key **只报一次**）。
        /// </summary>
        public static T RequireValue<T>(object param, string panelName, T fallback, string tag)
        {
            if (param is T typed) return typed;

            WarnPayloadUnusable(typeof(T).Name, param, panelName, tag);
            return fallback;
        }

        /// <summary>
        /// 统一的降级留痕：走 <see cref="LogThrottle.WarnOnce"/>（内部即 <c>Game.Logger.Warn</c>），
        /// key = <c>{面板}.OnOpen:{原因}</c> ⇒ **同一面板的同一原因整个进程只报一条**（⛔ 不刷屏）。
        /// <para>原因分两类（各自独立 key）：载荷为 <c>null</c>（漏传）/ 类型不符（传错 DTO）。</para>
        /// </summary>
        private static void WarnPayloadUnusable(string expectedType, object param, string panelName, string tag)
        {
            var who = string.IsNullOrEmpty(panelName) ? "(未命名面板)" : panelName;
            var reason = param == null
                ? "param=null（打开方漏传载荷）"
                : $"类型不符：期望 {expectedType}，实际 {param.GetType().Name}";

            LogThrottle.WarnOnce(
                string.IsNullOrEmpty(tag) ? DefaultTag : tag,
                $"{who}.OnOpen:{reason}",
                $"{who}.OnOpen 参数不可用（{reason}）⇒ 已按降级分支继续（返回默认 / 兜底值），不抛异常；"
                + "请检查打开方是否漏传或传错了载荷类型");
        }
    }
}
