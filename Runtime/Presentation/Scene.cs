using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CloverEngine
{
    // 契约（ISceneManager）已下沉到 Runtime/Core/PresentationContracts.cs（Game 门面在 Core）。

    /// <summary>
    /// 场景管理器实现：异步加载/卸载 + 加载门控 + 场景级清理。
    /// </summary>
    internal class SceneModule : ISceneManager
    {
        private string _currentScene;
        private long _progressTimerId;
        private AsyncOperation _progressOp;     // 最近一次 Load 的 op（与 _progressTimerId 同生命周期）
        private string _progressScene;          // 上面那个 op 的目标场景名（只给"被顶掉的旧 op"那条 Warn 用）
        private readonly List<Action<string>> _loadedHandlers = new();
        private readonly List<Action<string>> _unloadedHandlers = new();

        public string CurrentScene => _currentScene;

        public void Load(string sceneName, Action<float> progress = null, Action onDone = null)
        {
            Game.Logger?.Info("Scene", $"Loading scene: {sceneName}");

            var previousScene = _currentScene;

            var op = UnityEngine.SceneManagement.SceneManager.LoadSceneAsync(sceneName);
            if (op == null)
            {
                Game.Logger?.Error("Scene", $"Scene not found: {sceneName}");
                // 完成回调必达：等 onDone 才继续的业务（Loading 计数、流程推进）否则永久挂起。
                onDone?.Invoke();
                return;
            }

            op.allowSceneActivation = false;

            // ★ 用 EveryUnscaled + 唯一 id，而不是固定名 "SceneProgress" 的 EveryName：
            //   EveryName 受 timeScale 影响：加载遮罩把 timeScale 置 0 时进度停在 0.9 永不激活、场景卡死。
            // 上一次 Load 的 op 若还没激活，必须先放行它**再**停它的轮询：否则它没人再推进度、激活门又关着
            // ⇒ 永远停在 isLoaded=false/rootCount=0（僵尸场景），并拖住后续 LoadSceneAsync（progress 恒 0.000）。
            var pending = _progressOp;
            if (pending != null && !pending.isDone)
            {
                Game.Logger?.Warn("Scene",
                    $"上一次加载未激活就被新的 Load 顶掉（非预期分支）：旧场景={_progressScene} 新场景={sceneName} " +
                    $"旧 op 进度={pending.progress:0.###} ⇒ 放行 allowSceneActivation=true 再停它的轮询");
                pending.allowSceneActivation = true;
            }
            _progressOp = op;
            _progressScene = sceneName;
            Game.Timer?.Stop(_progressTimerId);
            _progressTimerId = Game.Timer?.EveryUnscaled(0.05f, () =>
            {
                // Timer.Stop 延迟到 Tick 生效 ⇒ 被顶掉的旧 op 本轮仍可能回调一次，这里自判归属再动。
                if (_progressOp != op) return;
                progress?.Invoke(op.progress);

                if (op.progress >= 0.9f)
                {
                    Game.Timer?.Stop(_progressTimerId);
                    _progressTimerId = 0;
                    op.allowSceneActivation = true;

                    op.completed += _ =>
                    {
                        // 旧 op 的 completed 若晚到，不许把新 op 的字段清掉（这里自判归属）。
                        if (_progressOp == op)
                        {
                            _progressOp = null;
                            _progressScene = null;
                        }
                        _currentScene = sceneName;

                        // 重载**同一个**场景时 previousScene == sceneName：此时不能清理，
                        // 否则会把刚激活的新场景实体 / 对象池组一起清掉。
                        if (previousScene != null && previousScene != sceneName)
                        {
                            CleanupSceneResources(previousScene);
                        }

                        Game.Dispatcher?.Post(() =>
                        {
                            foreach (var h in _loadedHandlers)
                            {
                                try { h?.Invoke(sceneName); }
                                catch (Exception ex)
                                {
                                    Game.Logger?.Error("Scene", $"Handler error: {ex.Message}", ex);
                                }
                            }
                            onDone?.Invoke();
                        });
                    };
                }
            }) ?? 0;
        }

        public void Unload(string sceneName, Action onDone = null)
        {
            Game.Logger?.Info("Scene", $"Unloading scene: {sceneName}");

            CleanupSceneResources(sceneName);

            var op = UnityEngine.SceneManagement.SceneManager.UnloadSceneAsync(sceneName);
            if (op == null)
            {
                Game.Logger?.Warn("Scene", $"Scene not found for unload: {sceneName}");
                onDone?.Invoke();
                return;
            }

            op.completed += _ =>
            {
                // 卸载完成后不再有"当前场景"；不置空会让 CurrentScene 一直返回已卸载的场景名。
                if (_currentScene == sceneName) _currentScene = null;

                Game.Dispatcher?.Post(() =>
                {
                    foreach (var h in _unloadedHandlers)
                    {
                        try { h?.Invoke(sceneName); }
                        catch (Exception ex)
                        {
                            Game.Logger?.Error("Scene", $"Handler error: {ex.Message}", ex);
                        }
                    }
                    onDone?.Invoke();
                });
            };
        }

        public void OnSceneLoaded(Action<string> handler)
        {
            // 契约（ISceneManager）没有对应移除接口，只能去重：同一 handler 重复订阅否则永久驻留
            //（重复回调 + 业务引用泄漏，切场景后也清不掉）。
            if (handler != null && !_loadedHandlers.Contains(handler))
                _loadedHandlers.Add(handler);
        }

        public void OnSceneUnloaded(Action<string> handler)
        {
            // 同上：去重，避免重复订阅永久驻留。
            if (handler != null && !_unloadedHandlers.Contains(handler))
                _unloadedHandlers.Add(handler);
        }

        private void CleanupSceneResources(string sceneName)
        {
            Game.Entity?.DestroyGroup(sceneName);
            Game.Pool?.ClearGroup(sceneName);
            // 清掉可能仍在跑的加载进度定时器（正常会在 progress>=0.9 时自停；场景被提前销毁 /
            // 加载中断时靠这里兜住）。用 id 停，不再依赖固定名。
            Game.Timer?.Stop(_progressTimerId);
            _progressTimerId = 0;
            // 场景级定时器批量回收（ISceneManager 的承诺：scope=场景名的定时器随场景卸载清理）。
            Game.Timer?.StopScope(sceneName);
        }
    }
}
