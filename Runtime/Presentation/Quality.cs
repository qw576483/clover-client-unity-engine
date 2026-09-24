using System;
using System.Collections.Generic;
using UnityEngine;

namespace CloverEngine
{
    // 契约（QualityTier / QualityConfig / IQualityManager）见
    // Runtime/Core/PresentationContracts.cs（Game 门面在 Core）。

    /// <summary>
    /// 设备管理器实现，基于内存/显存阈值分级，内置三档质量预设，支持运行时监控与自动降级
    /// </summary>
    internal class QualityManager : IQualityManager
    {
        private const string SettingKeyQualityTier = "quality_level";
        /// <summary>旧键名（Device 命名时期）：只读一次做迁移，避免老玩家配置被静默重置。</summary>
        private const string LegacySettingKeyQualityTier = "device_level";
        private const int FpsBufferSize = 60;
        private const float AutoDowngradeFpsRatio = 0.7f;
        private const float AutoDowngradeDuration = 3f;
        private const float AutoDowngradeCooldown = 30f;

        private QualityTier _level;
        /// <summary>预设是否已真正 Apply 过一次（_level 初值 Low：首次 AutoDetect 也选中 Low 时不能提前 return）。</summary>
        private bool _applied;
        private QualityConfig _config = new();
        private readonly List<Action<QualityTier>> _changeHandlers = new();
        private readonly List<Action<bool>> _throttleHandlers = new();

        private readonly float[] _fpsBuffer = new float[FpsBufferSize];
        private int _fpsIndex;
        private int _fpsCount;
        private float _fpsAccum;
        private float _lowFpsTimer;
        private float _lastAutoDowngradeTime = float.MinValue;

        private bool _isThrottling;
        private float _currentFps;

        private static readonly Dictionary<QualityTier, QualityConfig> Presets = new()
        {
            [QualityTier.Low] = new QualityConfig
            {
                ResolutionScale = 0.75f,
                ShadowEnabled = false,
                LODLevel = 2,
                ParticleDensity = 0.5f,
                MaxSameScreenCount = 10,
                TargetFrameRate = 30
            },
            [QualityTier.Medium] = new QualityConfig
            {
                ResolutionScale = 0.85f,
                ShadowEnabled = false,
                LODLevel = 1,
                ParticleDensity = 0.75f,
                MaxSameScreenCount = 20,
                TargetFrameRate = 60
            },
            [QualityTier.High] = new QualityConfig
            {
                ResolutionScale = 1f,
                ShadowEnabled = true,
                LODLevel = 0,
                ParticleDensity = 1f,
                MaxSameScreenCount = 30,
                TargetFrameRate = 60
            }
        };

        /// <inheritdoc/>
        public QualityTier Level => _level;

        /// <inheritdoc/>
        public QualityConfig Config => _config;

        /// <inheritdoc/>
        public bool IsThrottling => _isThrottling;

        /// <inheritdoc/>
        public float CurrentFPS => _currentFps;

        /// <inheritdoc/>
        public void SetLevel(QualityTier level)
        {
            // 越界枚举必须拦：否则 _level 被改而 Apply 因 TryGetValue 失败静默 no-op，
            // Level 与 Config / 实际画质长期不一致，且没有任何提示。
            if (level < QualityTier.Low || level > QualityTier.High)
            {
                Game.Logger?.Warn("Quality",
                    $"非法画质档位 {level}({(int)level})，已忽略（有效范围 {QualityTier.Low}~{QualityTier.High}）");
                return;
            }

            if (level == _level)
            {
                // 档位没变，但预设可能一次都没落过：_level 初值即 Low，低配机 AutoDetect 选中 Low 时
                // 不能提前 return —— 否则帧率 / 阴影预设永不 Apply，档位形同虚设。
                if (!_applied)
                {
                    Apply(level);
                    _applied = true;
                }
                return;
            }

            _level = level;
            Apply(level);
            _applied = true;
            SaveLevel(level);

            foreach (var h in _changeHandlers)
            {
                try { h?.Invoke(level); }
                catch (Exception ex)
                {
                    Game.Logger?.Error("Quality", $"Handler error: {ex.Message}", ex);
                }
            }
        }

        /// <inheritdoc/>
        public void AutoDetect()
        {
            var saved = LoadLevel();
            if (saved.HasValue)
            {
                SetLevel(saved.Value);
                return;
            }

            var systemMemory = SystemInfo.systemMemorySize;
            var graphicsMemory = SystemInfo.graphicsMemorySize;

            if (systemMemory >= 6000 && graphicsMemory >= 3000)
                SetLevel(QualityTier.High);
            else if (systemMemory >= 3000 && graphicsMemory >= 1500)
                SetLevel(QualityTier.Medium);
            else
                SetLevel(QualityTier.Low);
        }

        /// <inheritdoc/>
        public void Tick(float dt)
        {
            // 帧率口径必须与真实时间一致：传入的 dt 是受 timeScale 影响的缩放时间
            //（慢动作下 1/dt 会虚高），而 _lowFpsTimer 用的是 unscaledDeltaTime ——
            // 两套计时口径混用会让降档判定失真。这里统一用 unscaledDeltaTime。
            var frameDt = Time.unscaledDeltaTime;
            if (frameDt <= 0f) return;

            var fps = 1f / frameDt;
            UpdateFpsBuffer(fps);
            _currentFps = ComputeAverageFps();
            CheckAutoDowngrade();
            CheckBatteryTemperature();
        }

        /// <inheritdoc/>
        public void OnLevelChanged(Action<QualityTier> handler)
        {
            _changeHandlers.Add(handler);
        }

        /// <inheritdoc/>
        public void OffLevelChanged(Action<QualityTier> handler)
        {
            // 空操作语义（与契约一致）：未订阅过的 handler / null 都直接返回，不抛也不报错 ——
            // 退订比订阅更容易被写成"防御性调用"（场景卸载路径上通常先退订再判空），
            // 在这里报错只会让调用方多包一层 try。
            if (handler == null) return;
            _changeHandlers.Remove(handler);
        }

        /// <inheritdoc/>
        public void OnThrottling(Action<bool> handler)
        {
            _throttleHandlers.Add(handler);
        }

        private void Apply(QualityTier level)
        {
            if (!Presets.TryGetValue(level, out var preset)) return;

            // 拷一份再对外暴露（直接引用静态 Presets 里的实例会被业务改 Config 污染，
            // 后续切档拿到被改坏的配置）。
            _config = Clone(preset);

            Application.targetFrameRate = preset.TargetFrameRate;
            // vSync 开着时 targetFrameRate 会被忽略（移动端常见）→ 必须同时关掉 vSync，帧率档位才生效。
            QualitySettings.vSyncCount = 0;

            // 只设 shadowCascades 不会真正启停阴影：还要切 QualitySettings.shadows。
            QualitySettings.shadows = preset.ShadowEnabled ? ShadowQuality.All : ShadowQuality.Disable;
            QualitySettings.shadowCascades = preset.ShadowEnabled ? 2 : 0;

            // 分辨率缩放：Unity 的全局近似开关（URP 下由后处理栈读取）。
            ScalableBufferManager.ResizeBuffers(preset.ResolutionScale, preset.ResolutionScale);
            // LOD：数值越小细节越多，映射到 Unity 的 maximumLODLevel（0 = 全用）。
            QualitySettings.maximumLODLevel = preset.LODLevel;

            // ParticleDensity / MaxSameScreenCount 是给业务读的数据（Unity 无全局粒子密度 API），
            // 业务从 Config 取值自行应用到粒子系统 / 同屏上限。
        }

        private static QualityConfig Clone(QualityConfig c) => new QualityConfig
        {
            ResolutionScale = c.ResolutionScale,
            ShadowEnabled = c.ShadowEnabled,
            LODLevel = c.LODLevel,
            ParticleDensity = c.ParticleDensity,
            MaxSameScreenCount = c.MaxSameScreenCount,
            TargetFrameRate = c.TargetFrameRate,
        };

        private void UpdateFpsBuffer(float fps)
        {
            if (_fpsCount >= FpsBufferSize)
                _fpsAccum -= _fpsBuffer[_fpsIndex];
            _fpsBuffer[_fpsIndex] = fps;
            _fpsAccum += fps;
            _fpsIndex = (_fpsIndex + 1) % FpsBufferSize;
            if (_fpsCount < FpsBufferSize) _fpsCount++;
        }

        private float ComputeAverageFps()
        {
            return _fpsCount > 0 ? _fpsAccum / _fpsCount : 0f;
        }

        private void CheckAutoDowngrade()
        {
            // 已是最低档：无处可降，清掉节流态（否则会永久停留在 true）
            if (_level <= QualityTier.Low)
            {
                _lowFpsTimer = 0f;
                SetThrottling(false);
                return;
            }

            var targetFps = _config.TargetFrameRate * AutoDowngradeFpsRatio;
            if (_currentFps < targetFps)
            {
                _lowFpsTimer += Time.unscaledDeltaTime;
                SetThrottling(true);
                if (_lowFpsTimer >= AutoDowngradeDuration && Time.unscaledTime - _lastAutoDowngradeTime >= AutoDowngradeCooldown)
                {
                    _lowFpsTimer = 0f;
                    _lastAutoDowngradeTime = Time.unscaledTime;
                    var downgraded = (QualityTier)((int)_level - 1);
                    Game.Logger?.Warn("Quality", $"Auto-downgrading from {_level} to {downgraded} (avg fps={_currentFps:F1})");
                    SetLevel(downgraded);
                }
            }
            else
            {
                _lowFpsTimer = 0f;
                SetThrottling(false);
            }
        }

        /// <summary>
        /// 更新节流状态并在变化时通知订阅者。
        /// 判定信号说明：Unity 的 SystemInfo 拿不到电池温度，因此「节流」以
        /// **当前平均 FPS 低于该档位目标帧率的 70%** 为准（即正在被性能压制），
        /// 而不是硬件热节流。已在最低档时恒为 false。
        /// </summary>
        private void SetThrottling(bool value)
        {
            if (_isThrottling == value) return;
            _isThrottling = value;

            foreach (var h in _throttleHandlers)
            {
                try { h?.Invoke(value); }
                catch (Exception ex)
                {
                    Game.Logger?.Error("Quality", $"Handler error: {ex.Message}", ex);
                }
            }
        }

        private void CheckBatteryTemperature()
        {
            // Unity 的 SystemInfo 不提供电池温度（只有 batteryLevel / batteryStatus），
            // 该能力在 Unity 平台无法获取，跳过温度节流检测，避免误报。
        }

        private void SaveLevel(QualityTier level)
        {
            if (Game.Setting == null) return;
            Game.Setting.Set(SettingKeyQualityTier, (int)level);
            Game.Setting.Save();
        }

        private QualityTier? LoadLevel()
        {
            if (Game.Setting == null) return null;

            var val = Game.Setting.Get(SettingKeyQualityTier, int.MinValue);
            if (val == int.MinValue)
            {
                // 旧版本用的键是 "device_level"（Device 命名时期）：读一次并迁移到新键，
                // 避免老玩家的画质选择在更名后被静默重置。
                val = Game.Setting.Get(LegacySettingKeyQualityTier, -1);
                if (val >= 0 && val <= (int)QualityTier.High)
                {
                    Game.Setting.Set(SettingKeyQualityTier, val);
                    Game.Setting.Save();
                    Game.Logger?.Info("Quality", $"已把旧设置键 {LegacySettingKeyQualityTier} 迁移为 {SettingKeyQualityTier}");
                }
            }

            if (val < 0 || val > (int)QualityTier.High) return null;
            return (QualityTier)val;
        }
    }
}
