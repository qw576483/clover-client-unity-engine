using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace CloverEngine
{
    // 契约（UILayer / IUIPanel / IUIManager）已下沉到 Runtime/Core/PresentationContracts.cs，
    // 因为 Game 门面在 Core，而依赖方向是 Presentation → Core 单向。

    /// <summary>
    /// UI 管理器实现：窗口栈、固定层级、互斥遮罩、面板生命周期驱动。
    /// 由 CloverPresentation.Init 挂接到 Game.UI（通常随 Game.Launch 自动完成）。
    /// </summary>
    internal class UIManager : IUIManager
    {
        private readonly Dictionary<string, IUIPanel> _panels = new();
        private readonly Dictionary<string, GameObject> _panelRoots = new();
        private readonly Transform[] _layers;
        private readonly List<string> _history = new();
        private readonly List<Action<string>> _openedHandlers = new();
        private readonly List<Action<string>> _closedHandlers = new();
        private readonly GameObject _root;
        // Tick 每帧调用，复用缓冲避免逐帧分配（游戏引擎里每帧一次 GC 是明确的异味）。
        private readonly List<string> _tickBuffer = new();
        private bool _missingEventSystemWarned;
        private GameObject _popupMask;

        // 通用件：与窗口栈完全解耦 —— 不进 _panels / _history，不参与 Popup 互斥。
        // 否则「弹一条 Toast」会把业务正在看的弹窗顶掉，是明确的错误行为。
        private readonly ToastLayer _toasts;
        private readonly FloatTextLayer _floats;
        private readonly LoadingLayer _loading;
        private readonly ConfirmLayer _confirm;
        private readonly RedDotRegistry _redDots = new();
        private readonly GuideLayer _guide;

        /// <summary>
        /// 创建 UI 管理器：建立常驻 Canvas（含缩放适配与射线检测）与 5 个层级节点。
        /// 业务面板预制体需放在 Resources/UI/{面板类型名} 下（Open&lt;T&gt; 按此路径加载）。
        /// </summary>
        public UIManager()
        {
            _root = new GameObject("[UI]");
            UnityEngine.Object.DontDestroyOnLoad(_root);

            // 没有 Canvas 面板无法渲染，也没有射线检测 → 按钮点不动。这里一次性建好。
            var canvas = _root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 0;

            var scaler = _root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            // ★ 画布适配可配置：参考分辨率与匹配权重改为**可配置**（`CloverPresentation.ReferenceResolution` /
            //   `.MatchWidthOrHeight`），默认值 = 本文件原先写死的 1920×1080 / 0.5
            //   ⇒ 不配置时行为与旧版逐字一致（横版项目零影响）；竖版项目在 Game.Launch **之前**
            //   设成 1080×1920 / match=0。
            //   ⛔ 这里只读一次，Launch 之后改这两个属性不生效（画布已建好）。
            scaler.referenceResolution = CloverPresentation.ReferenceResolution;
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = CloverPresentation.MatchWidthOrHeight;

            _root.AddComponent<GraphicRaycaster>();

            _layers = new Transform[5];
            var layerNames = new[] { "Background", "Normal", "Popup", "Top", "System" };
            for (var i = 0; i < 5; i++)
            {
                var layerGo = new GameObject(layerNames[i]);
                layerGo.transform.SetParent(_root.transform, false);
                var rt = layerGo.AddComponent<RectTransform>();
                rt.anchorMin = Vector2.zero;
                rt.anchorMax = Vector2.one;
                rt.offsetMin = Vector2.zero;
                rt.offsetMax = Vector2.zero;
                _layers[i] = layerGo.transform;
            }

            // 通用件各自挂在自己那一层：Toast/飘字进 Top，Loading/引导进 System（最高），确认框进 Top。
            //
            // ★ 本项目实测到的引擎缺陷（2026-09-17），最小修复：
            //   确认框原先挂在 **Popup** 层（`UILayer.Popup = 2`），而**暂停菜单 / 死亡屏在 `Top = 3`**
            //   （见 `Core/PresentationContracts.cs:20-27` 的层序 Background=0 < Normal=1 < Popup=2 < Top=3 < System=4）。
            //   Top 层的节点在层级里排在 Popup 之后 ⇒ 它的所有子节点**画在确认框之上、且先被射线命中**
            //   ⇒ 从暂停菜单点「回主菜单」弹出的确认框**整块被暂停菜单盖住，鼠标永远点不到**（确认/取消都点不动）。
            //   实测（clover-project-diablo2 · 验收 #42）：`Game.UI.Confirm` 后 `confirm=1` 但面板一直在，
            //   真鼠标点击落到的是暂停菜单那一层的按钮（还会误触「选项」，把选项屏重新打开）。
            //   修复：确认框改挂 Top 层（后创建 ⇒ 兄弟序在后 ⇒ 在暂停菜单之上、在其之上的只有 System）；
            //   它本来就是"模态确认"，必须在所有交互层之上。
            _toasts = new ToastLayer(_layers[(int)UILayer.Top]);
            _floats = new FloatTextLayer((RectTransform)_layers[(int)UILayer.Top]);
            _loading = new LoadingLayer(_layers[(int)UILayer.System]);
            _confirm = new ConfirmLayer(_layers[(int)UILayer.Top]);
            _guide = new GuideLayer((RectTransform)_layers[(int)UILayer.System]);

            // ★ 把**实际生效**的画布适配参数打出来（判据用，一次）。
            //   竖版项目应看到「参考分辨率=1080×1920 / match=0 / Screen=<宽>×<高>（高 > 宽）」；
            //   若这里仍是 1920×1080，说明业务侧那两行写在了 Game.Launch **之后**（改晚了，不生效）。
            Game.Logger?.Info("UI",
                $"画布适配：参考分辨率={CloverPresentation.ReferenceResolution.x}×{CloverPresentation.ReferenceResolution.y} " +
                $"/ match={CloverPresentation.MatchWidthOrHeight} / Screen={Screen.width}×{Screen.height}");
        }

        /// <inheritdoc/>
        public void Open<T>(object param = null) where T : class, IUIPanel
        {
            // 只在「真正用 UI」时提醒一次：EventSystem 归输入模块（CloverInput）管，
            // 缺失时面板能打开但按钮点不动，这是容易踩且难查的静默失效。
            if (!_missingEventSystemWarned && UnityEngine.EventSystems.EventSystem.current == null)
            {
                _missingEventSystemWarned = true;
                Game.Logger?.Error("UI",
                    "场景中没有 EventSystem，UI 可打开但按钮点不动；请在 Game.Launch 后调用 CloverInput.Init()");
            }

            var typeName = typeof(T).Name;

            // 面板可能以自定义 PanelName 注册，按**类型**找已打开实例，避免 typeof(T).Name 找不到。
            if (TryFind<T>(out var openKey, out var existing))
            {
                _history.Remove(openKey);
                _history.Add(openKey);
                // 重开已存在面板要沿层内兄弟序**置顶**：只重排历史栈不会真的显示到最前
                //（后开的面板仍盖在它上面，与"重新 OnOpen 并置顶"的契约不符）。
                if (_panelRoots.TryGetValue(openKey, out var openRoot) && openRoot != null)
                    openRoot.transform.SetAsLastSibling();
                existing.OnOpen(param);
                return;
            }

            // 预制体来源可插拔：默认 Resources/UI/{类名}，可经 CloverPresentation.PanelProvider 换成 Addressables 等。
            var prefab = CloverPresentation.PanelProvider != null
                ? CloverPresentation.PanelProvider(typeName)
                : Resources.Load<GameObject>($"UI/{typeName}");
            if (prefab == null)
            {
                Game.Logger?.Error("UI",
                    $"Panel prefab not found: {typeName}（默认查 Resources/UI/{typeName}；可设置 CloverPresentation.PanelProvider 自定义来源）");
                return;
            }

            var panelGo = UnityEngine.Object.Instantiate(prefab);
            var panel = panelGo.GetComponent<T>();
            if (panel == null)
            {
                Game.Logger?.Error("UI", $"Component {typeName} not found on prefab, panel MUST have it");
                UnityEngine.Object.Destroy(panelGo);
                return;
            }

            // 注册键取**面板自己声明的 PanelName**（业务 override 生效），为空时回落类型名；
            // 泛型入口（Open/Close/Get/IsOpen<T>）按类型查找，不受键名影响。
            var name = string.IsNullOrEmpty(panel.PanelName) ? typeName : panel.PanelName;

            if (panel.Layer == UILayer.Popup)
            {
                CloseMutexPanels();
                ShowMask();
            }

            _panels[name] = panel;
            _panelRoots[name] = panelGo;
            _history.Add(name);

            var layerIndex = (int)panel.Layer;
            if (layerIndex >= 0 && layerIndex < _layers.Length)
                panelGo.transform.SetParent(_layers[layerIndex], false);

            panel.OnOpen(param);

            var openedHandlersCopy = new List<Action<string>>(_openedHandlers);
            foreach (var h in openedHandlersCopy)
            {
                try { h?.Invoke(name); }
                catch (Exception ex)
                {
                    Game.Logger?.Error("UI", $"Handler error: {ex.Message}", ex);
                }
            }
        }

        /// <inheritdoc/>
        public void Close<T>() where T : class, IUIPanel
        {
            // 面板可能以自定义 PanelName 注册：按类型找键再关（直接 Close(typeof(T).Name) 会找不到）。
            string key = null;
            foreach (var kv in _panels)
            {
                if (kv.Value is T)
                {
                    key = kv.Key;
                    break;
                }
            }
            if (key != null) Close(key);
        }

        /// <inheritdoc/>
        public void Close(string panelName)
        {
            if (!_panels.TryGetValue(panelName, out var panel)) return;

            // 先摘表、再回调 OnClose：OnClose 里若 Open 弹窗（会经 CloseMutexPanels 再次 Close 到本面板），
            // 没摘表就会让 OnClose 二次执行、面板重复销毁。
            _panels.Remove(panelName);
            _history.Remove(panelName);
            var layer = panel.Layer;

            panel.OnClose();

            if (_panelRoots.TryGetValue(panelName, out var root))
            {
                if (root != null) UnityEngine.Object.Destroy(root);
                _panelRoots.Remove(panelName);
            }

            if (layer == UILayer.Popup)
                HideMask();

            var closedHandlersCopy = new List<Action<string>>(_closedHandlers);
            foreach (var h in closedHandlersCopy)
            {
                try { h?.Invoke(panelName); }
                catch (Exception ex)
                {
                    Game.Logger?.Error("UI", $"Handler error: {ex.Message}", ex);
                }
            }
        }

        /// <inheritdoc/>
        public void CloseAll()
        {
            for (var i = _history.Count - 1; i >= 0; i--)
                Close(_history[i]);
        }

        /// <inheritdoc/>
        public T Get<T>() where T : class, IUIPanel
        {
            // 按类型找（键可能是自定义 PanelName），找不到返回 null。
            foreach (var kv in _panels)
            {
                if (kv.Value is T typed) return typed;
            }
            return null;
        }

        /// <inheritdoc/>
        public bool IsOpen<T>() where T : class, IUIPanel
        {
            foreach (var kv in _panels)
            {
                if (kv.Value is T) return true;
            }
            return false;
        }

        /// <summary>按类型查找已打开的面板（返回其注册键，可能是自定义 PanelName）。</summary>
        private bool TryFind<T>(out string key, out IUIPanel panel) where T : class, IUIPanel
        {
            foreach (var kv in _panels)
            {
                if (kv.Value is T)
                {
                    key = kv.Key;
                    panel = kv.Value;
                    return true;
                }
            }
            key = null;
            panel = null;
            return false;
        }

        /// <inheritdoc/>
        public void OnPanelOpened(Action<string> handler)
        {
            _openedHandlers.Add(handler);
        }

        /// <inheritdoc/>
        public void OnPanelClosed(Action<string> handler)
        {
            _closedHandlers.Add(handler);
        }

        // ─────────────── 通用件（Toast / 飘字 / Loading / 确认框 / 红点 / 引导） ───────────────

        /// <inheritdoc/>
        public void Toast(string text, float duration = 2f)
        {
            _toasts.Show(text, duration);
        }

        /// <inheritdoc/>
        /// <remarks>
        /// 参数原样透传给 <c>FloatTextLayer</c>（默认值只写在契约上，实现里不重复一份，
        /// 避免"两份默认值悄悄分歧"）。<c>riseWorld = 0</c> / <c>fade = true</c> 时逐字等于旧行为。
        /// </remarks>
        public void FloatText(Vector3 worldPos, string text, Color? color = null, float duration = 1.2f,
            float riseWorld = 0f, bool fade = true)
        {
            _floats.Show(worldPos, text, color ?? new Color(1f, 0.92f, 0.4f), duration, riseWorld, fade);
        }

        /// <inheritdoc/>
        public void ShowLoading(string text = null)
        {
            // 旧签名 = "不确定进度"（不显示进度条）⇒ 与本次改动之前**逐字一致**。
            _loading.Show(text, LoadingLayer.Indeterminate);
        }

        /// <inheritdoc/>
        public void ShowLoading(string text, float progress01)
        {
            _loading.Show(text, progress01);
        }

        /// <inheritdoc/>
        public void SetLoadingProgress(float progress01)
        {
            _loading.SetProgress(progress01);
        }

        /// <inheritdoc/>
        public void HideLoading()
        {
            _loading.Hide();
        }

        /// <inheritdoc/>
        public bool IsLoading => _loading.IsVisible;

        /// <inheritdoc/>
        public void Confirm(string title, string message, Action onConfirm, Action onCancel = null,
            string confirmText = null, string cancelText = null)
        {
            _confirm.Show(title, message, onConfirm, onCancel, confirmText, cancelText);
        }

        /// <inheritdoc/>
        public void SetRedDot(string key, bool on)
        {
            _redDots.Set(key, on);
        }

        /// <inheritdoc/>
        public bool GetRedDot(string key)
        {
            return _redDots.Get(key);
        }

        /// <inheritdoc/>
        public void OnRedDotChanged(Action<string, bool> handler)
        {
            _redDots.OnChanged(handler);
        }

        /// <inheritdoc/>
        public void ShowGuide(RectTransform target, string tip = null, Action onClick = null, bool blockTarget = true)
        {
            // 只在「真正用引导」时提醒一次：引导的两条链路都要 EventSystem 才能点到
            if (!_missingEventSystemWarned && UnityEngine.EventSystems.EventSystem.current == null)
            {
                _missingEventSystemWarned = true;
                Game.Logger?.Error("UI",
                    "场景中没有 EventSystem，引导遮罩无法接收点击；请在 Game.Launch 后调用 CloverInput.Init()");
            }

            _guide.Show(target, tip, onClick, blockTarget);
        }

        /// <inheritdoc/>
        public void HideGuide()
        {
            _guide.Hide();
        }

        /// <inheritdoc/>
        public void Tick(float dt)
        {
            // 通用件先于面板推进：它们与窗口栈无关，即使一个业务面板都没开也要动
            // （否则 Toast 不淡出、Loading 不转、确认框停在原地）。
            // ★ 通用件用**不受 timeScale 影响**的 dt：暂停 / 结算（timeScale=0）时 Loading 仍要转、
            //   Toast 仍要淡出，否则遮罩停转、提示永不消失（同 Timer unscaled 教训）。
            var widgetDt = Time.unscaledDeltaTime;
            TickWidget(_toasts.Tick, widgetDt, "Toast");
            TickWidget(_floats.Tick, widgetDt, "FloatText");
            TickWidget(_loading.Tick, widgetDt, "Loading");
            // 确认框与引导遮罩没有逐帧动画（按钮由 uGUI 自己响应），无需 Tick

            if (_panels.Count == 0) return;

            // 复用缓冲快照 key，避免 OnUpdate 内开关面板导致集合在遍历中被修改。
            _tickBuffer.Clear();
            _tickBuffer.AddRange(_panels.Keys);
            foreach (var key in _tickBuffer)
            {
                if (!_panels.TryGetValue(key, out var panel)) continue;
                try
                {
                    panel.OnUpdate(dt);
                }
                catch (Exception ex)
                {
                    Game.Logger?.Error("UI", $"Panel {key} OnUpdate error: {ex.Message}", ex);
                }
            }
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            CloseAll();

            // 通用件各自持有节点，须一并销毁；确认框的排队请求按「取消」回调，
            // 避免业务等在回调里解锁的状态永远解不开。
            _toasts.Dispose();
            _floats.Dispose();
            _loading.Dispose();
            _confirm.CloseAll();
            _confirm.Dispose();
            _guide.Dispose();
            _redDots.Clear();

            _openedHandlers.Clear();
            _closedHandlers.Clear();
            _tickBuffer.Clear();
            _popupMask = null;

            if (_root != null)
                UnityEngine.Object.Destroy(_root);
        }

        /// <summary>通用件 Tick 的异常隔离：单个件的动画出错不应连带面板不刷新。</summary>
        private static void TickWidget(Action<float> tick, float dt, string name)
        {
            try
            {
                tick(dt);
            }
            catch (Exception ex)
            {
                Game.Logger?.Error("UI", $"{name} Tick error: {ex.Message}", ex);
            }
        }

        private void CloseMutexPanels()
        {
            var toClose = new List<string>();
            foreach (var kv in _panels)
            {
                if (kv.Value.Layer == UILayer.Popup)
                    toClose.Add(kv.Key);
            }
            foreach (var name in toClose)
                Close(name);
        }

        private void ShowMask()
        {
            if (_popupMask != null) return;

            _popupMask = new GameObject("PopupMask");
            var rt = _popupMask.AddComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;

            _popupMask.AddComponent<CanvasRenderer>();
            var img = _popupMask.AddComponent<UnityEngine.UI.Image>();
            img.color = new Color(0, 0, 0, 0.5f);
            img.raycastTarget = true;

            _popupMask.transform.SetParent(_layers[(int)UILayer.Popup], false);
            _popupMask.transform.SetAsFirstSibling();
        }

        private void HideMask()
        {
            // 还有 Popup 面板开着就保留遮罩（直接判集合即可，原实现用临时结构体上的
            // GetEnumerator().MoveNext() 判空，写法晦涩且易被误改）。
            foreach (var p in _panels.Values)
            {
                if (p.Layer == UILayer.Popup) return;
            }

            if (_popupMask != null)
            {
                UnityEngine.Object.Destroy(_popupMask);
                _popupMask = null;
            }
        }
    }

    /// <summary>
    /// 安全区适配：把节点对齐到 <see cref="Screen.safeArea"/>（刘海 / 挖孔 / 圆角屏）。
    /// 挂在需要避让的节点上即可，只在安全区实际变化时改写锚点。
    /// 全铺满的遮罩（Loading Blocker / 确认框 Overlay）特意**不**挂它 —— 遮罩要盖满全屏，
    /// 只让里面的内容（转圈、文案、面板）避让。
    /// </summary>
    internal sealed class SafeAreaFitter : MonoBehaviour
    {
        /// <summary>true = 只让顶部锚点跟到安全区上沿（顶部悬挂的 Toast 条用），其余保持不变。</summary>
        public bool TopEdgeOnly;

        private RectTransform _rt;
        private Rect _applied = new Rect(-1f, -1f, -1f, -1f);

        private void Start()
        {
            _rt = transform as RectTransform;
            if (_rt == null)
            {
                // 非预期分支：挂在非 UI 节点上 → 关掉自己，别让每帧的 Update 空转。
                Game.Logger?.Warn("UI", $"SafeAreaFitter 挂在非 RectTransform 节点上，已停用：{name}");
                enabled = false;
                return;
            }
            Apply();
        }

        private void Update()
        {
            if (_rt == null) return;
            if (Screen.safeArea != _applied) Apply();
        }

        private void Apply()
        {
            if (_rt == null) _rt = transform as RectTransform;
            if (_rt == null || Screen.width <= 0 || Screen.height <= 0) return;

            var sa = Screen.safeArea;
            _applied = sa;

            var min = new Vector2(sa.xMin / Screen.width, sa.yMin / Screen.height);
            var max = new Vector2(sa.xMax / Screen.width, sa.yMax / Screen.height);

            if (TopEdgeOnly)
            {
                _rt.anchorMin = new Vector2(_rt.anchorMin.x, max.y);
                _rt.anchorMax = new Vector2(_rt.anchorMax.x, max.y);
                return;
            }

            _rt.anchorMin = min;
            _rt.anchorMax = max;
            _rt.offsetMin = Vector2.zero;
            _rt.offsetMax = Vector2.zero;
        }
    }
}
