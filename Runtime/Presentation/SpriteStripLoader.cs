// ─────────────────────────────────────────────────────────────────────────────
// CloverEngine · Runtime/Presentation/SpriteStripLoader.cs
// 「多帧条带 → 定长帧表」加载器：整条 LoadAll 主路 + 逐帧按名兜底 + 就绪回调 + 同路径去重缓存。
//
// 出处：clover-project-diablo2 `client/Assets/Scripts/UI/UiArt.cs`
//   · `FrameSet`（`:946-962`）/ `RequestFrames`（`:968-1016`）/ `OnFrameLoaded`（`:1018-1026`）/
//     `CompleteFrames`（`:1032-1070`）/ `RequestStrip`（`:1083-1098`）/ `TryBulkLoad`（`:1115-1145`）。
//     逐条照搬的语义：**同一路径只加载一次**（后来的调用只登记回调，就绪后统一触发）；
//     **结算只发生一次**；**缺帧不静默**（点名条带路径 + 缺口数）；**就绪后的回调可能是同帧同步的**
//     （缓存命中 / 整条 `LoadAll` 命中）⇒ 调用方必须能接受同步回调。
//   · 帧表形状：**一条横排条带 = 一张多 Sprite 贴图，子 sprite 名 = `{条带文件名}_{帧号}`**
//     （`:1131-1143` 的 `prefix = set.StripName + "_"` + `int.TryParse`）。
//
// 为什么下沉（每个用"多帧 UI 底图"的项目都会重踩一次）：
//   ① `Game.Res.LoadAsset<Sprite>("…/button_wide_0")`（**逐帧按名**）在一张 PNG 被切成多个子
//      Sprite 的导入设置下**返回 null**（实测出处同上 `:1076-1082`）—— 而 `LoadAll<Sprite>("…/button_wide")`
//      （**整条取**）拿得到 ⇒ "整条取 = 主路、逐帧按名 = 兜底"是本件的固定次序。
//      ⚠️ 与参考实现的**次序相反**：参考实现先逐帧按名，后来因为"这条路必然取不到图、却让资源模块
//      白刷每帧一条 `[Error] [Resource] 加载失败`"而改成整条优先（该文件 `:990-997` 的实测注释）。
//      本件直接采用改好之后的次序；⛔ 不是"把错误降级"，逐帧按名**原地保留**（真取不到时照样走）。
//   ② 逐帧按名的回调是**异步**的，且命中缓存时**可能同帧同步**回调 ⇒ 若按"发起时记 0、回调里 +1"
//      的写法，条带会在**第一个**回调时就被判成"全部就绪"（半套底图 + 零报错）。
//      固定写法：**先把 `Pending` 设成帧总数**，再逐帧发起（见 `RequestStrip`）。
//
// 与既有引擎件的边界（⛔ 不是重复实现）：
//   · ⛔ **不是 `FrameBank` 的重复**（`Runtime/Resource/FrameBank.cs`）：那个面向**目录**（N 张 PNG /
//     1 PNG 1 Sprite），一次抓整目录、按**帧号**排序、可选统一画布锚点；本件面向**一张横排条带**
//     （1 PNG / N 子 Sprite），按"子 sprite 名尾部的帧号"切成**定长帧表**，且带**异步登记 + 就绪回调**
//     （FrameBank 的 `LoadDir` 是纯同步的）。两者共享同一套"帧号解析"（本件直接复用
//     `FrameBank.ParseFrameIndex`，⛔ 不另写一份）。
//   · ⛔ **不是 `SpriteFrameAnimator` 的重复**：那个是"帧表 → `SpriteRenderer` 的**推进**器"；
//     本件是"**帧表从哪来**"。两者正交，可叠加（条带就绪 → 帧表 → 动画器）。
//   · ⛔ **不是 `UiImageLoader` 的重复**：那个管"**单张**贴图贴到一个 `Image`"（请求序号守卫 / 占位保留）；
//     本件管"**一整条**贴图切成帧表"，不碰任何 `Image`。
//
// 边界：
//   · 资源一律经 `IResourceManager`（`Game.Res`）—— ⛔ 本件不碰 Unity 的 `Resources` / `AssetBundle`
//     （那会让资源根前缀 / 缓存 / 卸载策略 / 热更后端全部失效，见 `结构规则.md` §5.2 G13）。
//   · ⛔ **不持有/不销毁**取到的 Sprite：`LoadAll` 的契约是"取到的对象由调用方自己持有"
//     （`Runtime/Core/Contracts.cs:1290-1306`）⇒ `Clear()` 只丢引用，⛔ 不 `Destroy` 任何东西。
//   · 整条取是**同步阻塞主线程**的（契约同 `LoadAll`）⇒ 进图前 / 读条阶段调用，⛔ 别在战斗热路径里
//     第一次调用。
//   · 主线程使用（与 `LogThrottle` 一致，非线程安全）。
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using UnityEngine;

namespace CloverEngine
{
    /// <summary>
    /// 多帧条带加载器：把一个<b>路径</b>（一张横排多 Sprite 贴图）加载成<b>定长帧表</b>。
    /// <para>
    /// <b>用法</b>：
    /// <code>
    /// var loader = new SpriteStripLoader();                       // 一般传 null 资源 → 走 Game.Res
    /// loader.RequestStrip("UI/Menu/button_wide", 3, frames =&gt;  // 就绪回调**可能是同帧同步的**
    /// {
    ///     if (frames == null || frames.Length == 0) return;       // 取不到：调用方保留占位
    ///     btn.spriteState = new SpriteState { highlightedSprite = frames[1], ... };
    /// });
    /// </code>
    /// </para>
    /// <para>
    /// <b>两条取帧路径</b>（次序固定，见文件头①）：① 整条 <see cref="IResourceManager.LoadAll{T}(string)"/>；
    /// ② ① 失败才逐帧按名 <see cref="IResourceManager.LoadAsset{T}(string, Action{T})"/>。
    /// </para>
    /// <para>
    /// <b>同路径去重</b>：同一路径只加载一次；就绪前重复调用只登记回调，就绪后统一触发
    /// （已就绪则**同步**回调）。
    /// </para>
    /// <para>
    /// 「子 sprite 名 → 帧号」的解析走本件的 <see cref="ParseFrameNumber"/>（纯函数，可离线断言）。
    /// ⛔ <b>刻意不复用 FrameBank</b>：它在 <c>CloverEngine.Resource</c> 程序集里，而本程序集
    /// （<c>CloverEngine.Presentation</c>）的 asmdef **只引用 <c>Core</c>** ⇒ 跨程序集调用会打穿分层
    /// （`结构规则.md` §4.1）；且两边口径本就不同（FrameBank = **整目录**逐帧文件的名字，
    /// 本件 = **一张条带的子 sprite** 名字 = `{条带名}_{帧号}`）。</para>
    /// </summary>
    public sealed class SpriteStripLoader
    {
        /// <summary>日志 tag（与 UI 通用件同域，便于按 <c>[SpriteStrip]</c> 检索）。</summary>
        private const string Tag = "SpriteStrip";

        /// <summary>一条条带的一次加载状态（只在 <see cref="RequestStrip"/> 首次见到该路径时建）。</summary>
        private sealed class Strip
        {
            /// <summary>调用方给的条带路径（= `LoadAll` 的路径，也用于日志点名）。</summary>
            public string Path;

            /// <summary>条带**文件名**（路径最后一段；子 sprite 名 = `{FileName}_{帧号}`）。</summary>
            public string FileName;

            /// <summary>定长帧表（长度 = 调用方要的帧数；取不到的槽为 <c>null</c>）。</summary>
            public Sprite[] Frames;

            /// <summary>还没回结果的帧数（**先把总数设好再逐帧发起**，见文件头②）。</summary>
            public int Pending;

            /// <summary>是否已结算（<c>true</c> ⇒ 帧表不会再变，重复请求直接同步回调）。</summary>
            public bool Ready;

            /// <summary>「整条 <c>LoadAll</c>」这条主路是否走成功过（诊断用）。</summary>
            public bool BulkOk;

            /// <summary>就绪前登记的回调（结算时**快照后清空**再逐个触发）。</summary>
            public readonly List<Action<Sprite[]>> Callbacks = new List<Action<Sprite[]>>();
        }

        /// <summary>显式注入的资源管理器；<c>null</c> 时回落到 <see cref="Game.Res"/>。</summary>
        private readonly IResourceManager _resource;

        /// <summary>条带路径 → 加载状态。</summary>
        private readonly Dictionary<string, Strip> _strips =
            new Dictionary<string, Strip>(StringComparer.Ordinal);

        /// <summary>
        /// 「只报一次」的代际：<see cref="Clear"/> 递增后，同一路径的告警会**再报一次**
        /// （换关卡 / 重进游戏后重新加载，值得再提醒一次）。
        /// ⛔ 不调 <see cref="LogThrottle.Reset"/>：那会连带清掉别的系统的限频记录，是越界副作用。
        /// </summary>
        private int _generation;

        /// <param name="resource">
        /// 资源管理器；一般传 <c>null</c>，取 <see cref="Game.Res"/>。
        /// 保留注入口是为了离线 / EditMode 自检与替换实现（引擎门面在 Launch 前为空）。
        /// </param>
        public SpriteStripLoader(IResourceManager resource = null)
        {
            _resource = resource;
        }

        /// <summary>已登记的条带数（诊断 / 自检用）。</summary>
        public int Count { get { return _strips.Count; } }

        private IResourceManager Res { get { return _resource ?? Game.Res; } }

        // ── 纯函数（离线可断言）──────────────────────────────────────────────

        /// <summary>
        /// 取路径的**最后一段**（条带文件名）—— 子 sprite 名的前缀就靠它（<c>{FileName}_{帧号}</c>）。
        /// 同时认 <c>/</c> 与 <c>\</c>（资源路径惯例是 <c>/</c>，但调用方从 Windows 路径拼过来时不该静默取错键）。
        /// </summary>
        /// <returns>最后一段；路径为空 ⇒ 空串（⛔ 不返回 null）。</returns>
        public static string FileNameOf(string stripPath)
        {
            if (string.IsNullOrEmpty(stripPath)) return string.Empty;
            var cut = stripPath.LastIndexOf('/');
            var cut2 = stripPath.LastIndexOf('\\');
            if (cut2 > cut) cut = cut2;
            return cut < 0 ? stripPath : stripPath.Substring(cut + 1);
        }

        /// <summary>
        /// 第 <paramref name="index"/> 帧的**逐帧按名**路径：<c>{条带路径}_{帧号}</c>
        /// （Unity 多 Sprite 导入给子 sprite 的默认命名；⛔ 只在兜底路径上用到）。
        /// </summary>
        public static string FramePath(string stripPath, int index) => stripPath + "_" + index;

        /// <summary>
        /// <b>纯函数</b>：把「整条 <c>LoadAll</c>」的结果按"子 sprite 名 = <c>{条带文件名}_{帧号}</c>"
        /// 切成**定长帧表**（离线宿主可直接喂一个假 <see cref="Sprite"/> 数组断言，无需资源管理器）。
        /// <para>
        /// 规则：名字不以 <c>{文件名}_</c> 开头的**跳过**；名字尾部的帧号由 <see cref="ParseFrameNumber"/>
        /// 解析（<c>-1</c> = 名字里没有数字段 ⇒ 跳过）；
        /// 帧号落在 <c>[0, frameCount)</c> 之外的**跳过**（不是收敛 —— 多出来的子 sprite 说明"帧数报错了"，
        /// 静默塞进末尾会让调用方拿到错帧）。同帧号**先到先得**（后到的覆盖前一个，与参考实现一致）。
        /// </para>
        /// </summary>
        /// <param name="all">整条取的原始结果（可为 <c>null</c> / 空）。</param>
        /// <param name="stripPath">条带路径（只用来取文件名）。</param>
        /// <param name="frameCount">要的帧数（&lt;= 0 ⇒ 返回空数组）。</param>
        /// <returns>长度 = <paramref name="frameCount"/> 的帧表；取不到的槽为 <c>null</c>（⛔ 不返回 null）。</returns>
        public static Sprite[] Slice(Sprite[] all, string stripPath, int frameCount)
        {
            if (frameCount <= 0) return Array.Empty<Sprite>();
            var frames = new Sprite[frameCount];
            Fill(frames, all, stripPath, FileNameOf(stripPath));
            return frames;
        }

        /// <summary>
        /// 条带口径的**帧号解析**：子 sprite 名的剩余段形如 <c>帧号</c>（<c>0</c>）或
        /// <c>帧号_子序号</c>（<c>12_3</c>）⇒ 取**第一个全数字段**。
        /// <para>
        /// ⛔ <b>为什么不复用 <c>FrameBank.ParseFrameIndex</c></b>：它在 <c>CloverEngine.Resource</c>
        /// 程序集里，而 <c>CloverEngine.Presentation</c> 的 asmdef **只引用 <c>Core</c>**
        /// （`Runtime/Presentation/CloverEngine.Presentation.asmdef`）⇒ 跨程序集调用会打穿分层
        /// （`结构规则.md` §4.1）。两边口径也不同：那边是"**整目录**的逐帧文件名"
        /// （帧号可能出现在倒数第二段），本件是"**一张条带的子 sprite 名** = 条带名 + `_` + 帧号"。
        /// </para>
        /// </summary>
        /// <param name="tail">条带文件名之后的那一段（例：<c>"0"</c> / <c>"12_3"</c> / <c>"_1"</c>）。</param>
        /// <returns>帧号；剩余段里没有纯数字段 ⇒ <c>-1</c>（调用方按"不是本条带的帧"跳过）。</returns>
        public static int ParseFrameNumber(string tail)
        {
            if (string.IsNullOrEmpty(tail)) return -1;

            var segments = tail.Split('_');
            for (var i = 0; i < segments.Length; i++)
            {
                var segment = segments[i];
                if (segment.Length == 0) continue;

                var digits = true;
                for (var k = 0; k < segment.Length; k++)
                {
                    if (segment[k] < '0' || segment[k] > '9')
                    {
                        digits = false;
                        break;
                    }
                }

                if (!digits) continue;

                int value;
                if (int.TryParse(segment, out value)) return value;
            }

            return -1;
        }

        // ── 取帧 ─────────────────────────────────────────────────────────────

        /// <summary>
        /// 请求一条多帧条带；就绪后触发 <paramref name="onReady"/>（帧表长度 = <paramref name="frameCount"/>，
        /// 缺帧的槽为 <c>null</c>，⛔ 不返回 <c>null</c>）。
        /// <para>
        /// ⚠️ <b>回调可能是同帧同步的</b>：该路径已就绪（缓存命中）或"整条取"一次就成功时，
        /// <paramref name="onReady"/> 会在**本次调用返回前**被触发 ⇒ 调用方要能接受同步回调
        /// （写法上：先建好要补的对象，再调本方法）。
        /// </para>
        /// <para>
        /// 参数非法（路径空 / 帧数 &lt;= 0）⇒ 打一条降频 Warn 并**直接回调空数组**
        /// （⛔ 不静默、也不抛）。
        /// </para>
        /// </summary>
        /// <param name="stripPath">条带路径（引擎相对路径，⛔ 不要再带 <c>CloverRes.Init</c> 的根前缀）。</param>
        /// <param name="frameCount">要切几帧（= 条带的子 sprite 数）。</param>
        /// <param name="onReady">就绪回调（可为 <c>null</c>）；帧表在缺帧时为 <c>null</c> 槽。</param>
        public void RequestStrip(string stripPath, int frameCount, Action<Sprite[]> onReady)
        {
            if (string.IsNullOrEmpty(stripPath) || frameCount <= 0)
            {
                // 非预期分支：路径 / 帧数写错（帧数多半来自"数了条带上的几格"这类手写常量）
                LogThrottle.WarnThrottled(Tag, "strip.invalid-args[" + _generation + "]",
                    "RequestStrip 参数非法（path=\"" + stripPath + "\" frameCount=" + frameCount +
                    "）⇒ 直接回调空帧表（调用方保留占位）", 10f);
                onReady?.Invoke(Array.Empty<Sprite>());
                return;
            }

            Strip strip;
            if (_strips.TryGetValue(stripPath, out strip))
            {
                if (strip.Ready)
                {
                    onReady?.Invoke(strip.Frames);   // 已就绪 ⇒ 同步回调（见方法注释的同步口径）
                    return;
                }

                if (onReady != null) strip.Callbacks.Add(onReady);
                return;
            }

            // ★ `Pending` 必须在**发起任何加载之前**就设成帧总数（文件头②）：
            //   逐帧按名命中缓存时会**同步**回调，若那时 Pending 还是 0 / 还在累加，就会提前结算成"半套"。
            strip = new Strip
            {
                Path = stripPath,
                FileName = FileNameOf(stripPath),
                Frames = new Sprite[frameCount],
                Pending = frameCount,
            };
            _strips[stripPath] = strip;
            if (onReady != null) strip.Callbacks.Add(onReady);

            var res = Res;
            if (res == null)
            {
                // 非预期分支：资源模块没初始化（CloverRes.Init 未调用）⇒ 结算成空 + 只报一次
                LogThrottle.WarnOnce(Tag, "res.null[" + _generation + "]",
                    "Game.Res 未初始化（CloverRes.Init 未调用）⇒ 条带 " + stripPath +
                    " 取不到，调用方保留占位（UI 不会因此变黑 / 变透明）");
                Complete(strip);
                return;
            }

            // ① 主路：整条取（同步、阻塞主线程）。
            if (TryBulkInto(strip, res))
            {
                Complete(strip);
                return;
            }

            // ② 兜底：逐帧按名（异步）。整条取失败时才走这条路（见文件头①）。
            for (var i = 0; i < frameCount; i++)
            {
                var index = i;
                res.LoadAsset<Sprite>(FramePath(stripPath, index), sp => OnFrameLoaded(strip, index, sp));
            }
        }

        /// <summary>
        /// 该路径是否已就绪（= 帧表已经切好、不会再变）。⛔ <c>true</c> **不代表**每帧都取到了
        /// （缺帧的槽是 <c>null</c>，见 <see cref="Cached"/>）。
        /// </summary>
        public bool IsReady(string stripPath)
        {
            Strip strip;
            return !string.IsNullOrEmpty(stripPath)
                   && _strips.TryGetValue(stripPath, out strip) && strip.Ready;
        }

        /// <summary>
        /// 已就绪的帧表（⛔ 不触发加载、不登记）；未登记 / 未就绪 ⇒ <c>null</c>。
        /// ⚠️ 返回的是**内部数组本身**（⛔ 调用方不要改它，也不要长期持有 —— 换场景 <see cref="Clear"/>
        /// 后它会被丢掉）。
        /// </summary>
        public Sprite[] Cached(string stripPath)
        {
            Strip strip;
            if (string.IsNullOrEmpty(stripPath) || !_strips.TryGetValue(stripPath, out strip)) return null;
            return strip.Ready ? strip.Frames : null;
        }

        /// <summary>
        /// 丢掉全部登记与缓存（出图 / 换场景时调）。
        /// <para>
        /// ⛔ **本件不销毁任何 Sprite**：`LoadAll` 契约是"取到的对象由调用方自己持有"
        /// （<c>Runtime/Core/Contracts.cs:1290-1306</c>）⇒ 这里只丢引用，谁加载谁负责释放。
        /// </para>
        /// <para>代际 +1 ⇒ 同一路径的"只报一次"告警会再报一次（换关卡后值得再提醒）。</para>
        /// </summary>
        public void Clear()
        {
            _strips.Clear();
            _generation++;
        }

        // ── 内部 ─────────────────────────────────────────────────────────────

        /// <summary>逐帧按名的回调（<paramref name="sp"/> 为 <c>null</c> = 该帧取不到，槽保持 <c>null</c>）。</summary>
        private void OnFrameLoaded(Strip strip, int index, Sprite sp)
        {
            if (sp != null) strip.Frames[index] = sp;   // 已填过的槽不被后来的 null 覆盖
            if (--strip.Pending > 0) return;            // 还有帧没回结果
            Complete(strip);                            // 只结算一次（Pending 归零的唯一出口）
        }

        /// <summary>
        /// 整条取一次（主路）。成功 = 至少切出 1 帧 ⇒ 返回 <c>true</c> 并已写进 <paramref name="strip"/>。
        /// <para>⛔ 不抛：后端异常一律接住 + 降频 Warn，转回"走兜底路径"。</para>
        /// </summary>
        private static bool TryBulkInto(Strip strip, IResourceManager res)
        {
            Sprite[] all;
            try
            {
                all = res.LoadAll<Sprite>(strip.Path);
            }
            catch (Exception ex)
            {
                // 非预期分支：后端不支持 LoadAll / 加载途中出错 ⇒ 降级到逐帧按名，不静默
                LogThrottle.WarnThrottled(Tag, "bulk.fail:" + strip.Path,
                    "整条取帧异常（" + strip.Path + "）：" + ex.GetType().Name + ": " + ex.Message +
                    " ⇒ 降级为逐帧按名");
                return false;
            }

            if (all == null || all.Length == 0) return false;

            var taken = Fill(strip.Frames, all, strip.Path, strip.FileName);
            if (taken <= 0)
            {
                // 非预期分支：整条取回来了、但没有一个子 sprite 的名字匹配 `{文件名}_{帧号}`
                // ⇒ 多半是导入设置把这批 PNG 导成了单 Sprite（名字 = 文件名）
                LogThrottle.WarnThrottled(Tag, "bulk.nomatch:" + strip.Path,
                    "整条取到 " + all.Length + " 个子 sprite，但没有一个匹配 `" + strip.FileName +
                    "_<帧号>`（" + strip.Path + "）⇒ 降级为逐帧按名（检查导入设置是否切成了多 Sprite）");
                return false;
            }

            strip.BulkOk = true;
            return true;
        }

        /// <summary>
        /// 把整条结果切进定长帧表 <paramref name="dest"/>。
        /// </summary>
        /// <returns>实际填进的帧数（0 = 一个都没匹配上）。</returns>
        private static int Fill(Sprite[] dest, Sprite[] all, string stripPath, string fileName)
        {
            if (dest == null || dest.Length == 0) return 0;
            if (all == null || all.Length == 0) return 0;
            if (string.IsNullOrEmpty(fileName)) return 0;

            var prefix = fileName + "_";
            var taken = 0;
            for (var i = 0; i < all.Length; i++)
            {
                var sp = all[i];
                if (sp == null || string.IsNullOrEmpty(sp.name)) continue;
                if (!sp.name.StartsWith(prefix, StringComparison.Ordinal)) continue;

                // 帧号解析走本件纯函数（见 ParseFrameNumber 的"为什么不复用 FrameBank"）
                var frame = ParseFrameNumber(sp.name.Substring(prefix.Length));
                if (frame < 0 || frame >= dest.Length) continue;

                dest[frame] = sp;
                taken++;
            }

            return taken;
        }

        /// <summary>
        /// 结算（**只发生一次**）：标记就绪、清空等待者、逐个触发回调。
        /// <para>缺帧**不静默**：点名条带路径 + 缺口数（"只报一次"，见文件头）。</para>
        /// </summary>
        private void Complete(Strip strip)
        {
            strip.Ready = true;
            strip.Pending = 0;

            var missing = 0;
            for (var i = 0; i < strip.Frames.Length; i++)
                if (strip.Frames[i] == null) missing++;

            if (missing > 0)
            {
                LogThrottle.WarnOnce(Tag, "strip.missing[" + _generation + "]:" + strip.Path,
                    "条带 " + strip.Path + " 缺 " + missing + "/" + strip.Frames.Length +
                    " 帧（整条取 + 逐帧按名都没拿到；帧表里缺的槽是 null）" +
                    " ⇒ 调用方按占位处理，⛔ 不要当成功（检查路径 / 导入类型 / 帧数）");
            }
            else if (!strip.BulkOk)
            {
                // 设计内但值得留痕：整条取没成，是靠逐帧按名一帧帧拼回来的（慢、且说明导入设置与预期不符）
                Game.Logger?.Info(Tag, "条带 " + strip.Path + "：整条取不到 ⇒ 已用逐帧按名拼齐 " +
                                       strip.Frames.Length + " 帧");
            }

            // 快照后再触发：回调里可能又对同一路径发起请求 / 清缓存（⛔ 不要边遍历边改）
            var callbacks = strip.Callbacks.ToArray();
            strip.Callbacks.Clear();
            for (var i = 0; i < callbacks.Length; i++)
            {
                try
                {
                    callbacks[i]?.Invoke(strip.Frames);
                }
                catch (Exception ex)
                {
                    // 非预期分支：回调异常不许打断"同一条带的其它等待者"
                    Game.Logger?.Error(Tag, "条带就绪回调抛出异常（" + strip.Path + "）：" + ex.Message, ex);
                }
            }
        }
    }
}
