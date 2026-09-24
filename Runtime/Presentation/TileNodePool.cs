// ─────────────────────────────────────────────────────────────────────────────
// CloverEngine · Runtime/Presentation/TileNodePool.cs
// **SpriteRenderer 瓦片节点池**：逐格渲染节点的"借 / 还 / 清"，通用底座，从
// clover-project-diablo2 下沉（该项目的同名类型已改为薄转发到本类）。
//
// ⛔ 为什么这是**新件**、而不是扩展同目录的 `ObjectPool`（判据）：
//   `ObjectPool`（Runtime/Presentation/ObjectPool.cs）是**GameObject 级**池：key 寻址
//   （`Spawn(key, parent, group)` / `Despawn(obj)`）、按 key 维护 active/inactive 列表、
//   池根是 `DontDestroyOnLoad` 的 `[ObjectPool]`、来源是 `Resources.Load<GameObject>` 或
//   注册的代码工厂、归还时归零变换（`localPosition`/`localRotation`/`localScale`）。
//   它回答的是「**这个预制体/工厂的实例**借还」。
//   本件是**逐字段重设的瓦片节点池**：只池化一种东西（`GameObject` + `SpriteRenderer`，名字固定），
//   ⛔ 没有 key、没有预制体、没有工厂、**故意不归零变换**（位置/缩放/精灵/颜色/排序号由调用方
//   每格用 `TileRenderState` 无条件写全 —— 归零只会多一次无用的写入），池根由**调用方**给
//   （挂在业务的地图层根下 ⇒ 随场景一起销毁，不跨场景泄漏）。
//   两者**可以并存**且语义不同；把本件硬塞进 `ObjectPool` 会带来三处打架：① key 维度（瓦片无 key）；
//   ② 归还时的变换归零会与"调用方写完再取用"的语义重复且互相覆盖；③ 池根策略（DontDestroyOnLoad
//   的资源池根 vs 随场景销毁的地图层根）。⇒ 另起一件（判据 = 这两条：GameObject 池 vs
//   渲染状态值 + 无条件写全）。
//
// 三条硬规矩（逐字沿用原实现，⛔ 未改语义）：
//   ① **只有 `Take` 会把节点真建出来**（`new GameObject` + 挂 `SpriteRenderer`）⇒「复用」与「新建」
//      走同一条路，调用方拿到的节点一律由它自己把渲染字段写全 ⇒ 画面逐项相等；
//   ② 归还 = `SetActive(false)` + 挂回池根（**不销毁**）⇒ 块根被销毁时不会连带销毁它们；
//      与之**严格配对**：`Take` 取出即 `SetActive(true)`（Unity 语义：激活父节点**不**复活
//      `activeSelf=false` 的子节点 —— 少这一步，池化过的瓦片会永远不可见且不报错）。
//   ③ 池里可能残留**已被场景卸载销毁**的空引用（Unity 的 `==` 重载判 null）⇒ `Take` 跳过它们。
//
// 出处（逐字搬运，⛔ 未改数值与分支）：
//   clover-project-diablo2 · client/Assets/Scripts/Module/Map/TileNodePool.cs:26-125
//   （★ T0FIX-A 引入，首个消费方 = 同项目 `Module/Map/MapView.cs` 的 `EnsurePool`/`NewTile`/`Return`。）
//
// <para><b>最小复现</b>：整图重铺（Town 56×40 ≈ 2000+ 节点、洞穴最坏 ≈ 9000）单帧
//   46.3~73.9 ms（≫ 16.67 ms 一帧预算）—— 因为旧口径每格 `Destroy` + `new GameObject`；
//   第二次及以后的整图重铺（贴图流式到位 / 迷雾开关）**新建数应为 0**（池里全部复用）。</para>
// <para><b>自证</b>：① 纯函数 `SplitDemand(free, demand)` 的算术 —— 池够 ⇒ 新建 0、池不够 ⇒
//   只补差额、无需求 ⇒ 什么都不取（离线用例表在 clover-project-diablo2 的
//   `.ai-tmp/test/tile-equiv/`，本轮实测通过）；② `Take`/`Return` 的 `SetActive` 严格配对
//   （结构断言：取出即 `SetActive(true)` / 归还即 `SetActive(false)`，各恰 1 次）；
//   ③ 离线宿主的池行为计数器（`CreatedCount` / `ReusedCount` / `FreeCount`）在一次重铺前后一致。</para>
// <para><b>已知边界 / 精度限制</b>：① 计数**只增不减**（进程内累计自证量）：`Clear()` 真销毁池内
//   空闲节点但**不清零计数** ⇒ 语义是"本次进程累计新建/复用了几次"，⛔ 不要拿它当"池里现在几个"；
//   "现在几个"看 `FreeCount`；② `Return` 允许重复归还（会把同一节点压两次栈）—— 本池**不查重**
//   （查重要维护 `HashSet`，而调用方 `MapView` 的归还路径是"遍历块内子节点逐个归还"，天然不重复）；
//   重复归还的后果是 `FreeCount` 偏大 + `Take` 可能拿到同一节点两次 ⇒ 上游**必须**保证配对；
//   ③ `Clear()` 只销毁**空闲**节点，**不影响已取出的节点**（那些由调用方的层级负责销毁）；
//   ④ 池内节点一律 `inactive`、取出的一律 `active`（逐次配对）；⛔ 不要把节点"借用后长期不还"——
//   那是调用方的生命周期问题，本池不做超时回收（`ObjectPool` 的 `IdleExpirySeconds` 是给
//   资源实例用的，逐格节点不需要也不该有超时）。</para>
// <para><b>用法 + 首个消费方</b>：`var pool = new TileNodePool(rootTransform);` →
//   `Take(parent)` 取（取出即可见，渲染字段由调用方写全）→ 用完 `Return(sr)` → 退场 `Clear()`。
//   首个消费方 = `clover-project-diablo2` 的 `Module/Map/MapView.cs`（`EnsurePool` 建池、
//   `NewTile` 取、`ReturnTiles` 还、`Clear` 退场），业务还要接的线 = 该项目的 mapcheck 宿主
//   （结构断言 + 池化收益算术）。</para>
// ─────────────────────────────────────────────────────────────────────────────

using System.Collections.Generic;
using UnityEngine;

namespace CloverEngine
{
    /// <summary>
    /// 瓦片节点池（`SpriteRenderer` + 其 `GameObject`）。
    /// <para>池内节点**一律失活**、取出的一律复活（见 <see cref="Take"/> / <see cref="Return"/>）；
    /// 池根由调用方给（挂在业务自己的节点树里 ⇒ 随场景一起销毁）。</para>
    /// <para>非 MonoBehaviour（纯 C# 类）：不占 Update、不注册回调，由调用方驱动。</para>
    /// </summary>
    public sealed class TileNodePool
    {
        /// <summary>空闲节点栈（后进先出 ⇒ 复用的节点尽量是最近归还的，缓存友好）。</summary>
        private readonly Stack<SpriteRenderer> _free = new Stack<SpriteRenderer>();

        /// <summary>归还节点的挂载根（调用方给 ⇒ 随场景销毁，不跨场景泄漏）。</summary>
        private readonly Transform _root;

        /// <summary>累计**新建**（冷分支）次数（只增不减；语义见类注释的「已知边界」）。</summary>
        public int CreatedCount { get; private set; }

        /// <summary>累计**复用**（热分支）次数（只增不减）。</summary>
        public int ReusedCount { get; private set; }

        /// <summary>当前空闲节点数（"池里现在几个"看这个，⛔ 不是 `CreatedCount`）。</summary>
        public int FreeCount { get { return _free.Count; } }

        /// <summary>池根（归还节点的父节点；允许为 null ⇒ 归还时只失活、不换父）。</summary>
        public TileNodePool(Transform root)
        {
            _root = root;
        }

        /// <summary>
        /// 取一个节点（有可用的就复用；池空 / 池里全是已销毁引用才新建），并挂到
        /// <paramref name="parent"/> 的**末尾**（追加 ⇒ 调用方按"格序"取用时，兄弟序与
        /// "全新建"时逐项一致）。
        /// <para>取出即 <c>SetActive(true)</c>：与 <see cref="Return"/> 的 <c>SetActive(false)</c>
        /// **严格配对**。Unity 语义下"激活父节点不复活 `activeSelf=false` 的子节点"，
        /// 少这一步会让复用出来的节点永远不可见且**不报错**。</para>
        /// </summary>
        public SpriteRenderer Take(Transform parent)
        {
            SpriteRenderer sr = null;
            while (_free.Count > 0)
            {
                var pooled = _free.Pop();
                // 已被场景卸载销毁（Unity 的 == 重载判 null）：跳过，不留脏引用。
                // ⛔ 这条分支是**预期内**的（池根随场景销毁时的正常残留），故不记日志（逐格热路径）。
                if (pooled == null) continue;
                sr = pooled;
                ReusedCount++;
                break;
            }

            if (sr == null)
            {
                // 冷分支：全工程瓦片节点的**唯一**创建点。
                CreatedCount++;
                var go = new GameObject("T");
                sr = go.AddComponent<SpriteRenderer>();
            }

            sr.transform.SetParent(parent, false);
            sr.gameObject.SetActive(true);
            return sr;
        }

        /// <summary>归还一个节点：失活 + 脱离原父节点（挂回池根），**不销毁**。</summary>
        public void Return(SpriteRenderer sr)
        {
            if (sr == null) return;
            sr.gameObject.SetActive(false);
            if (_root != null) sr.transform.SetParent(_root, false);
            _free.Push(sr);
        }

        /// <summary>
        /// 真销毁池内全部**空闲**节点（退场用）。**不影响已取出的节点**（那些归调用方的层级管）。
        /// 计数不清零（进程内累计自证量，语义见类注释）。
        /// </summary>
        public void Clear()
        {
            while (_free.Count > 0)
            {
                var sr = _free.Pop();
                if (sr == null) continue;
                UnityEngine.Object.Destroy(sr.gameObject);
            }
        }

        /// <summary>
        /// **纯函数**：给定空闲数与需求数，算出「从池里取几个 / 新建几个」。
        /// 离线自检宿主用它做池化收益的算术断言（不需要真的建 `GameObject`）。
        /// </summary>
        public static void SplitDemand(int freeCount, int demand, out int fromFree, out int create)
        {
            if (demand <= 0)
            {
                fromFree = 0;
                create = 0;
                return;
            }
            var free = freeCount < 0 ? 0 : freeCount;
            fromFree = free < demand ? free : demand;
            create = demand - fromFree;
        }
    }
}
