using System;
using UnityEngine;

namespace CloverEngine
{
    // 契约（ICameraManager）已下沉到 Runtime/Core/PresentationContracts.cs。

    /// <summary>
    /// 相机管理器实现，基于 SmoothDamp 实现跟随，并通过钳位实现边界约束
    /// </summary>
    internal class CameraManager : ICameraManager
    {
        private Transform _target;
        private float _smoothTime;
        private Vector3 _velocity;
        private Bounds _bounds;
        private bool _hasBounds;

        private float _shakeDuration;
        private float _shakeIntensity;
        private float _shakeTimer;

        // 缓存主相机：原实现每帧 Camera.main（带 tag 查找语义），是可避免的静态查找开销。
        private Camera _cam;
        // 平滑跟随的"基准位置"：震动作为一次性视觉偏移叠加，绝不写回基准（否则震动位移会被
        // 下一帧的 SmoothDamp 当成跟随起点累加，跟随与震动互相污染）。
        private Vector3 _basePos;
        private bool _posInited;

        /// <inheritdoc/>
        public void Follow(Transform target, float smoothTime = 0.1f)
        {
            _target = target;
            _smoothTime = smoothTime;
            // 重新跟随：下一帧从相机**实际位置**重新取基准（不要拿上次跟随遗留的旧基准当起点）。
            _posInited = false;
        }

        /// <inheritdoc/>
        public void Unfollow()
        {
            _target = null;
            _posInited = false;
        }

        /// <inheritdoc/>
        public void Shake(float duration, float intensity)
        {
            _shakeDuration = duration;
            _shakeIntensity = intensity;
            _shakeTimer = 0f;
        }

        /// <inheritdoc/>
        public void SetBounds(Bounds bounds)
        {
            _bounds = bounds;
            _hasBounds = true;
        }

        /// <inheritdoc/>
        public void Tick(float dt)
        {
            // 相机被销毁 / 被禁用后重新抓一次（不想每帧都走 Camera.main 的静态查找）。
            if (_cam == null || !_cam.isActiveAndEnabled) _cam = Camera.main;
            var cam = _cam;
            if (cam == null) return;

            if (!_posInited)
            {
                _basePos = cam.transform.position;
                _posInited = true;
            }

            if (_target != null)
            {
                var targetPos = _target.position;
                targetPos.z = _basePos.z;
                _basePos = Vector3.SmoothDamp(_basePos, targetPos, ref _velocity, _smoothTime);
            }

            if (_hasBounds)
            {
                float halfW, halfH;
                if (cam.orthographic)
                {
                    halfH = cam.orthographicSize;
                    halfW = halfH * cam.aspect;
                }
                else
                {
                    // 透视投影没有 orthographicSize（原实现拿它算半宽高，钳位结果无意义）：
                    // 用 FOV × 相机到目标平面（取目标 z）的距离估算可视半高。
                    var planeZ = _target != null ? _target.position.z : 0f;
                    var dist = Mathf.Max(0.1f, Mathf.Abs(cam.transform.position.z - planeZ));
                    halfH = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) * dist;
                    halfW = halfH * cam.aspect;
                }
                _basePos.x = Mathf.Clamp(_basePos.x, _bounds.min.x + halfW, _bounds.max.x - halfW);
                _basePos.y = Mathf.Clamp(_basePos.y, _bounds.min.y + halfH, _bounds.max.y - halfH);
            }

            var shaking = _shakeTimer < _shakeDuration;
            var pos = _basePos;
            if (shaking)
            {
                _shakeTimer += dt;
                var t = _shakeTimer / _shakeDuration;
                var offset = UnityEngine.Random.insideUnitCircle * _shakeIntensity * (1f - t);
                pos += new Vector3(offset.x, offset.y, 0f);
            }

            // 未跟随且未震动时不碰相机（跟随交给别的系统 / 业务自己控制机位）。
            if (_target != null || shaking)
                cam.transform.position = pos;
        }
    }
}
