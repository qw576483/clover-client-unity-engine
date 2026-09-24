using System;
using System.Collections.Generic;
using UnityEngine;

namespace CloverEngine
{
    // 契约（IAnimPlayer / IAnimationManager）见
    // Runtime/Core/PresentationContracts.cs（Game 门面在 Core）。

    internal class AnimationManager : IAnimationManager
    {
        private readonly List<IAnimPlayer> _players = new();

        public IAnimPlayer CreateAnimator(GameObject go, RuntimeAnimatorController controller)
        {
            var animator = go.GetComponent<Animator>();
            if (animator == null)
                animator = go.AddComponent<Animator>();

            if (controller != null)
            {
                animator.runtimeAnimatorController = controller;
            }
            else
            {
                // 非预期分支：controller 为空 → Play/CrossFade 全部静默无效。留痕，且**不覆盖**已有控制器。
                Game.Logger?.Warn("Anim",
                    $"CreateAnimator 的 controller 为空（{go.name}）：动画无法播放，请检查生成物 / 路径");
            }

            // 同一 GameObject 重复 CreateAnimator：复用已有播放器 —— 否则两个 AnimatorPlayer 共享同一 Animator，
            // Update 与 OnComplete 各触发两次、播放器列表无限累积。
            foreach (var p in _players)
            {
                if (p is AnimatorPlayer existing && existing.Animator == animator)
                    return existing;
            }

            var player = new AnimatorPlayer(animator);
            _players.Add(player);
            return player;
        }

        public void Destroy(IAnimPlayer player)
        {
            _players.Remove(player);
        }

        public void Tick(float dt)
        {
            for (var i = _players.Count - 1; i >= 0; i--)
            {
                var player = _players[i];
                
                if (player is AnimatorPlayer ap)
                {
                    // 只在 Animator 被**真正销毁**时移除；仅"未激活"（SetActive(false) 暂藏）不能摘掉 ——
                    // 一摘就永不复位，重新激活后 OnComplete 永不触发。
                    if (ap.Animator == null)
                    {
                        _players.RemoveAt(i);
                        continue;
                    }
                    if (!ap.Animator.isActiveAndEnabled) continue;
                    ap.Update();
                }
            }
        }
    }

    internal class AnimatorPlayer : IAnimPlayer
    {
        private readonly Animator _animator;
        private readonly List<Action> _onCompleteHandlers = new();
        private readonly HashSet<string> _missingStatesReported = new();
        private bool _completeFired;
        private bool _destroyedWarned;

        public Animator Animator => _animator;

        public AnimatorPlayer(Animator animator)
        {
            _animator = animator;
        }

        /// <summary>
        /// Animator 可用性守卫：视图销毁后业务仍可能持有 IAnimPlayer 继续调用，
        /// 直接访问已销毁的组件会抛 MissingReferenceException —— 这里降级为"忽略 + 一次性告警"。
        /// </summary>
        private bool EnsureAlive()
        {
            if (_animator != null) return true;
            if (!_destroyedWarned)
            {
                _destroyedWarned = true;
                Game.Logger?.Warn("Anim", "Animator 已被销毁，本次（及后续）播放调用被忽略（业务持有的是已失效的 IAnimPlayer）");
            }
            return false;
        }

        /// <summary>状态名校验：拼错时只由 Unity 打一条不显眼的警告、OnComplete 永不触发，这里补一条引擎告警（每个名字只报一次）。</summary>
        private void WarnIfStateMissing(string stateName)
        {
            if (_animator == null || string.IsNullOrEmpty(stateName)) return;
            if (_animator.HasState(0, Animator.StringToHash(stateName))) return;
            if (_missingStatesReported.Add(stateName))
                Game.Logger?.Warn("Anim",
                    $"Animator 第 0 层不存在状态 \"{stateName}\"（拼写错误？）：播放无效且 OnComplete 永不触发");
        }

        public void Play(string stateName, float normalizedTime = 0f)
        {
            if (!EnsureAlive()) return;
            WarnIfStateMissing(stateName);
            _animator.Play(stateName, 0, normalizedTime);
            _completeFired = false;
        }

        public void CrossFade(string stateName, float duration = 0.25f)
        {
            if (!EnsureAlive()) return;
            WarnIfStateMissing(stateName);
            _animator.CrossFade(stateName, duration);
            _completeFired = false;
        }

        public void SetBool(string name, bool value) { if (EnsureAlive()) _animator.SetBool(name, value); }
        public void SetFloat(string name, float value) { if (EnsureAlive()) _animator.SetFloat(name, value); }
        public void SetInteger(string name, int value) { if (EnsureAlive()) _animator.SetInteger(name, value); }
        public void SetTrigger(string name) { if (EnsureAlive()) _animator.SetTrigger(name); }

        public void OnComplete(Action callback)
        {
            if (callback == null) return;
            // 多订阅者要**逐个通知**（覆盖式赋值会让第二个订阅者把第一个顶掉且无任何提示）；
            // 本接口没有"注销"，同一回调重复订阅按去重处理，避免列表无限累积。
            if (!_onCompleteHandlers.Contains(callback))
                _onCompleteHandlers.Add(callback);
        }

        public void Update()
        {
            if (_animator == null || !_animator.isActiveAndEnabled) return;

            var stateInfo = _animator.GetCurrentAnimatorStateInfo(0);
            if (stateInfo.normalizedTime >= 1f && !_completeFired)
            {
                _completeFired = true;
                var handlers = _onCompleteHandlers.ToArray();
                foreach (var h in handlers)
                {
                    try { h?.Invoke(); }
                    catch (Exception ex)
                    {
                        Game.Logger?.Error("Anim", $"OnComplete 回调异常: {ex.Message}", ex);
                    }
                }
            }

            if (stateInfo.normalizedTime < 1f)
                _completeFired = false;
        }
    }
}
