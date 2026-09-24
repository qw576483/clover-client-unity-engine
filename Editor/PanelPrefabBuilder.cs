// ─────────────────────────────────────────────────────────────────────────────
// CloverEngine · Editor/PanelPrefabBuilder.cs
// 一键生成「面板壳预制体」到 Resources/UI/{类名}.prefab。
//
// 为什么需要（引擎侧缺口）：UIManager.Open<T>() 要求预制体**已存在**于
//   `Resources/UI/{typeof(T).Name}`（Runtime/Presentation/UI.cs:131-140：找不到就
//   Error "Panel prefab not found" 并 return，面板打不开），于是**每个工程都得自己写一遍生成器**。
//
// ⛔ **不改 CloverPresentation.PanelProvider 的默认行为**：存量工程依赖"缺预制体 = 报错"
//   （那比静默开出一个空面板好定位）。本件是**新增的开发期工具**，不参与运行时分发。
//
// 为什么用反射扫描而不是手写类型表：
//   手登记表漏一行的症状是**运行时**才出现，
//   而扫描把"登记"这一步彻底去掉。代价是同名类型要显式报错（见 Build 的重名检查）。
//
// 两处注意：
//   ① **根节点必须铺满父层**：新建 RectTransform 默认是 anchor(0.5,0.5)+sizeDelta(100,100)，
//      不撑开则面板根只有 100×100，而面板内容按"铺满父节点"建的 ⇒ 整屏 UI 缩成中央一小块。
//   ② **存盘后必须校验 `m_Script` 引用非 0**：脚本尚未导入时 SaveAsPrefabAsset 会把
//      `m_Script` 写成 `{fileID: 0}` —— 预制体存在、**编译不报错、运行时组件为 null**
//      （表现是 `Component X not found on prefab`）。所以：存盘前查 MonoScript、
//      存盘后**回读资产**查组件、再扫一遍文件文本里的 `m_Script: {fileID: 0}`，三关都过才算成功。
//
// 日志：Editor 侧用 Debug.*（Game.Logger 在编辑器域重载后未必是落盘 Logger，
//   见 Runtime/Core/Game.cs:141-149；Editor 工具的读者就是 Console）。
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace CloverEngine.Editor
{
    /// <summary>
    /// 生成面板壳预制体（只有"一个挂好面板组件的、铺满父层的 RectTransform 根节点"的预制体；
    /// 内容由面板自己在 <c>OnOpen/Awake</c> 里搭）。
    /// <para>
    /// 目录固定为 <c>Assets/Resources/UI</c>：这是 <c>UIManager</c> 默认的查法
    ///（UIManager 在 Resources 下按 key <c>"UI/{类名}"</c> 取，见 Runtime/Presentation/UI.cs:134），
    /// 换别的目录默认就查不到 —— 除非工程自己设了 <c>CloverPresentation.PanelProvider</c>。
    /// </para>
    /// <para>重复执行安全：已存在的同名预制体会被覆盖重建。</para>
    /// </summary>
    internal static class PanelPrefabBuilder
    {
        /// <summary>预制体目录（与 UI.cs 默认查法一致）。</summary>
        private const string UiDir = "Assets/Resources/UI";

        private const string MenuScanAll = "Clover/面板壳预制体/扫描 IUIPanel 实现并全部生成";
        private const string MenuDryRun = "Clover/面板壳预制体/打印待生成清单（只读）";
        private const string MenuFromSelection = "Clover/面板壳预制体/只为选中的面板脚本生成";

        // ── 菜单 ─────────────────────────────────────────────────────────────

        [MenuItem(MenuScanAll, false, 220)]
        private static void BuildAll()
        {
            var notes = new List<string>();
            var types = ScanPanelTypes(notes);
            if (types.Count == 0)
            {
                Debug.LogWarning($"[Clover][PanelPrefab] 没扫到任何 IUIPanel 实现" +
                                 $"（面板需继承 {nameof(UIPanel)} 或直接实现 IUIPanel、且是 MonoBehaviour）。");
                return;
            }

            Build(types, "扫描 IUIPanel 实现");

            foreach (var note in notes) Debug.LogWarning($"[Clover][PanelPrefab] {note}");
        }

        [MenuItem(MenuDryRun, false, 221)]
        private static void DryRun()
        {
            var notes = new List<string>();
            var types = ScanPanelTypes(notes);

            var text = new StringBuilder();
            text.Append($"[Clover][PanelPrefab] 待生成 {types.Count} 个面板壳（目录 {UiDir}）—— 本菜单只打印，不写盘：");
            foreach (var t in types)
            {
                var path = $"{UiDir}/{t.Name}.prefab";
                var exists = AssetDatabase.LoadAssetAtPath<GameObject>(path) != null;
                text.Append($"\n  {(exists ? "[已存在，将被覆盖]" : "[新建]")} {t.FullName} ⇒ {path}");
            }

            foreach (var note in notes) text.Append($"\n  [注] {note}");
            if (types.Count == 0) Debug.LogWarning(text.ToString());
            else Debug.Log(text.ToString());
        }

        [MenuItem(MenuFromSelection, false, 222)]
        private static void BuildFromSelection()
        {
            var types = new List<Type>();
            foreach (var guid in Selection.assetGUIDs)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var script = string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadAssetAtPath<MonoScript>(path);
                var type = script != null ? script.GetClass() : null;

                if (type == null) continue;
                var reason = SkipReason(type);
                if (reason != null)
                {
                    Debug.LogWarning($"[Clover][PanelPrefab] 跳过选中脚本 {type.Name}：{reason}");
                    continue;
                }

                if (!types.Contains(type)) types.Add(type);
            }

            if (types.Count == 0)
            {
                Debug.LogWarning("[Clover][PanelPrefab] 选中的资产里没有可生成的面板脚本（选中 .cs 脚本再试）。");
                return;
            }

            Build(types, "选中的面板脚本");
        }

        // ── 生成 ─────────────────────────────────────────────────────────────

        private static void Build(List<Type> types, string source)
        {
            EnsureFolder(UiDir);

            // 轻量 Refresh，让刚编译完的脚本先进 AssetDatabase
            //（⛔ 不用 ForceSynchronousImport：那会重导整个工程）。
            // 真正兜住"脚本还没导入"的是下面每个类型的三关校验，不是这次 Refresh。
            AssetDatabase.Refresh();

            var ok = 0;
            var failed = 0;
            var skipped = 0;
            var lines = new List<string>();
            var byName = new Dictionary<string, Type>(StringComparer.Ordinal);

            foreach (var type in types)
            {
                var reason = SkipReason(type);
                if (reason != null)
                {
                    skipped++;
                    lines.Add($"跳过 {SafeName(type)}：{reason}");
                    continue;
                }

                // 预制体路径**按类名**（UI.cs:134 就是这么查的）⇒ 跨命名空间同名类型必须报错，
                // 否则后生成的那个会覆盖前一个，表现为"某个面板莫名其妙变成了另一个面板"。
                if (byName.TryGetValue(type.Name, out var first))
                {
                    skipped++;
                    lines.Add($"跳过 {SafeName(type)}：类名与 {SafeName(first)} 重复" +
                              "（预制体路径按类名，无法区分；请改名或改用 CloverPresentation.PanelProvider）");
                    continue;
                }

                byName[type.Name] = type;

                if (BuildOne(type, out var why)) ok++;
                else
                {
                    failed++;
                    lines.Add($"失败 {SafeName(type)}：{why}");
                }
            }

            AssetDatabase.SaveAssets();

            var head = $"[Clover][PanelPrefab] 面板壳预制体（来源：{source}）：成功 {ok} / 失败 {failed} / 跳过 {skipped}，目录 {UiDir}";
            var body = lines.Count == 0 ? string.Empty : "\n" + string.Join("\n", lines);
            if (failed > 0) Debug.LogError(head + body);
            else Debug.Log(head + body);
        }

        /// <summary>生成一个面板壳；失败时通过 <paramref name="why"/> 给出原因（不回滚已有资产，见 <see cref="DeleteIfFresh"/>）。</summary>
        private static bool BuildOne(Type type, out string why)
        {
            why = null;
            var prefabPath = $"{UiDir}/{type.Name}.prefab";
            var existedBefore = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath) != null;

            var go = new GameObject(type.Name, typeof(RectTransform));
            try
            {
                // ★ 注意 ①：根节点必须铺满父层（锚点/偏移全 0），否则面板内容只铺成 100×100
                var rt = go.GetComponent<RectTransform>();
                rt.anchorMin = Vector2.zero;
                rt.anchorMax = Vector2.one;
                rt.pivot = new Vector2(0.5f, 0.5f);
                rt.anchoredPosition = Vector2.zero;
                rt.sizeDelta = Vector2.zero;

                var component = go.AddComponent(type);
                var mb = component as MonoBehaviour;
                if (mb == null)
                {
                    why = "AddComponent 没拿到 MonoBehaviour";
                    return false;
                }

                // 存盘前自检：拿不到 MonoScript 说明脚本还没进 AssetDatabase，这时存下去就是废预制体
                if (MonoScript.FromMonoBehaviour(mb) == null)
                {
                    why = "取不到 MonoScript（脚本尚未导入）—— 等导入完成后重跑本菜单";
                    return false;
                }

                PrefabUtility.SaveAsPrefabAsset(go, prefabPath);
            }
            catch (Exception ex)
            {
                // 非预期分支：必须留痕（含例外类型，便于区分"类型不可挂载"与"写盘失败"）
                why = $"{ex.GetType().Name}: {ex.Message}";
                return false;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);       // 只留资产，不留场景里的临时对象
            }

            // ★ 注意 ②：存盘后三关校验 —— 坏预制体"编译不报错、运行时组件为 null"
            var saved = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (saved == null)
            {
                why = "存盘后取不到预制体资产";
                DeleteIfFresh(prefabPath, existedBefore);
                return false;
            }

            if (saved.GetComponent(type) == null)
            {
                why = "预制体上取不到组件（m_Script 引用是空的 = 坏预制体）";
                DeleteIfFresh(prefabPath, existedBefore);
                return false;
            }

            if (HasZeroScriptRef(prefabPath))
            {
                why = "预制体文本里存在 `m_Script: {fileID: 0}`（脚本引用没解析）";
                DeleteIfFresh(prefabPath, existedBefore);
                return false;
            }

            Debug.Log($"[Clover][PanelPrefab] 生成 {prefabPath}（组件 {SafeName(type)}，根节点锚点/偏移全 0）");
            return true;
        }

        /// <summary>
        /// 坏预制体且**本次是新建** ⇒ 删掉（留着一个坏资产比没有更坏：运行时是"组件为 null"，
        /// 会被误判成面板逻辑写错了）。覆盖前已存在的资产**不删** —— 那等于把用户的东西删了。
        /// </summary>
        private static void DeleteIfFresh(string prefabPath, bool existedBefore)
        {
            if (existedBefore)
            {
                Debug.LogWarning($"[Clover][PanelPrefab] {prefabPath} 已被本次重建覆盖且校验未过；" +
                                 "没有删除覆盖前的资产（不擅自删用户资产）—— 请修好脚本引用后重跑本菜单。");
                return;
            }

            AssetDatabase.DeleteAsset(prefabPath);
        }

        /// <summary>直接读预制体文本查 `m_Script: {fileID: 0}`（读文件本身，不看内存状态）。读不了不阻断。</summary>
        private static bool HasZeroScriptRef(string prefabPath)
        {
            try
            {
                return File.ReadAllText(prefabPath).Contains("m_Script: {fileID: 0}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Clover][PanelPrefab] 读不了 {prefabPath} 做 m_Script 校验：" +
                                 $"{ex.GetType().Name}: {ex.Message}（已由资产回读校验兜底）");
                return false;
            }
        }

        // ── 类型扫描与校验 ───────────────────────────────────────────────────

        /// <summary>
        /// 扫描当前 AppDomain 里全部 IUIPanel 实现（按类型全名排序，结果确定）。
        /// <para>跳过测试程序集：里面的测试用面板不是游戏面板，生成出来只会污染 Resources/UI。</para>
        /// </summary>
        private static List<Type> ScanPanelTypes(List<string> notes)
        {
            var found = new List<Type>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var assemblyName = assembly.GetName().Name ?? string.Empty;
                if (assemblyName.StartsWith("nunit", StringComparison.OrdinalIgnoreCase) ||
                    assemblyName.StartsWith("UnityEngine.TestRunner", StringComparison.Ordinal) ||
                    assemblyName.StartsWith("UnityEditor.TestRunner", StringComparison.Ordinal))
                    continue;

                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    // 有些程序集（编辑器扩展 / 平台专有）会有加载不出来的类型 —— 取能拿到的那些
                    types = ex.Types;
                    notes?.Add($"{assemblyName} 有类型加载失败（{ex.LoaderExceptions?.Length ?? 0} 个），已按可加载的继续");
                }
                catch (Exception ex)
                {
                    notes?.Add($"跳过程序集 {assemblyName}：{ex.GetType().Name}");
                    continue;
                }

                if (types == null) continue;

                foreach (var type in types)
                {
                    if (type == null || SkipReason(type) != null) continue;
                    if (!seen.Add(type.FullName ?? type.Name)) continue;
                    found.Add(type);
                }
            }

            found.Sort((a, b) => string.CompareOrdinal(a.FullName, b.FullName));
            return found;
        }

        /// <summary>生成前校验：返回非 null = 不能生成的原因（同时用于扫描过滤）。</summary>
        private static string SkipReason(Type type)
        {
            if (type == null) return "类型为 null";
            if (!typeof(MonoBehaviour).IsAssignableFrom(type))
                return "不是 MonoBehaviour（UIManager 要求面板组件挂在预制体上）";
            if (!typeof(IUIPanel).IsAssignableFrom(type)) return "没有实现 IUIPanel";
            if (type.IsAbstract) return "抽象类型（如 UIPanel 基类本身）";
            if (type.IsInterface) return "接口";
            if (type.ContainsGenericParameters) return "泛型类型（无法挂到 GameObject 上）";
            if (string.IsNullOrEmpty(type.Name)) return "类型名为空（预制体路径无从命名）";

            // 非 public 类型**不跳过**：AddComponent(Type) 是按 Type 挂载的，不要求 public
            //（Inspector 里看不到组件名是另一回事，运行时拿得到）。
            return null;
        }

        // ── 工具 ─────────────────────────────────────────────────────────────

        /// <summary>递归创建目录（<c>Assets</c> 本身一定是合法目录 ⇒ 递归必定收敛）。</summary>
        private static void EnsureFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder) || AssetDatabase.IsValidFolder(folder)) return;

            var parent = Path.GetDirectoryName(folder);
            var leaf = Path.GetFileName(folder);
            if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(leaf)) return;

            parent = parent.Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, leaf);
        }

        private static string SafeName(Type type) => type?.FullName ?? "<null>";
    }
}
