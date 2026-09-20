using UnityEngine;

namespace CloverEngine
{
    /// <summary>
    /// 第三人称环绕相机组件（3D 玩法的通用机位）：右键拖拽转视角 / 滚轮与 +- 缩放 /
    /// 平滑跟随 / **被遮挡时避障** / 贴脸隐藏角色。
    ///
    /// ★ 为什么它是"组件"而不是 `ICameraManager` 的一个模式：
    /// `ICameraManager`（见 `Presentation/Camera.cs`）是**2D / 俯视**语义的相机管理器
    /// —— `Follow` 会锁住 Z 轴、`SetBounds` 按正交视口钳位，那是平面玩法的机位模型；
    /// 第三人称要的是"绕角色的球坐标机位 + 遮挡处理"，两者参数与失效模式完全不同，
    /// 塞进同一个接口只会让两边都变模糊。所以这里给一个**独立的 MonoBehaviour**：
    /// 业务挂到相机上、设 target 即可，**不需要也不应该**再调 `Game.Camera.Follow`
    /// （引擎的 CameraManager 只有在业务调了 Follow 之后才会去抢相机 Transform）。
    ///
    /// 遮挡处理按业界标准实现（这几条正是"相机被墙挡住还愣着不动 / 穿进墙里"的正解）：
    ///   ① **多高度探针**取最小可用距离 —— 只探一条射线时，墙沿/门框会让相机"露个缝"看到里面；
    ///   ② **被挡时允许拉到很近**（<see cref="MinDistance"/>）—— 把最小距离当硬下限就会把机位留在
    ///      墙体内部，表现是"镜头穿墙 / 看不到角色"；
    ///   ③ **最终 CheckSphere 兜底**：机位若仍与几何体重叠，逐级继续拉近；
    ///   ④ **收缩快、恢复慢**：遮挡出现立刻拉近（避免穿模），障碍离开缓缓退回（避免抖动）；
    ///   ⑤ **贴脸时隐藏角色模型**：拉到极近时模型必然糊住视野；
    ///   ⑥ **不钻到地面以下**。
    ///
    /// 用法（业务侧）：
    /// <code>
    /// var cam = Camera.main.gameObject.AddComponent&lt;CloverThirdPersonCamera&gt;();
    /// cam.SetTarget(playerRoot.transform);
    /// cam.SetControlEnabled(false); // 弹窗 / 死亡时
    /// </code>
    /// </summary>
    public sealed class CloverThirdPersonCamera : MonoBehaviour
    {
        [Header("目标")]
        /// <summary>被环绕的目标（角色根节点）。</summary>
        public Transform Target;

        /// <summary>注视点相对目标的高度偏移（角色胸口附近）。</summary>
        public Vector3 PivotOffset = new Vector3(0f, 1.35f, 0f);

        [Header("距离与角度")]
        public float Distance = 5.5f;

        /// <summary>被遮挡时**允许**拉到的最近距离（关键：不能把它当"下限"而把机位留在墙里）。</summary>
        public float MinDistance = 0.2f;

        public float MaxDistance = 14f;

        /// <summary>水平朝向（度）。SetTarget 时会取目标朝向初始化。</summary>
        public float Yaw;

        /// <summary>俯仰（度，正为俯视）。</summary>
        public float Pitch = 16f;

        public float MinPitch = -25f;
        public float MaxPitch = 75f;

        [Header("手感")]
        /// <summary>鼠标灵敏度（度/像素）。</summary>
        public float Sensitivity = 0.14f;

        /// <summary>机位平滑时间（越大越"黏"）。</summary>
        public float FollowSmooth = 0.05f;

        /// <summary>遮挡探针的球半径。</summary>
        public float CollisionRadius = 0.28f;

        /// <summary>会挡住相机的层。角色模型默认没有 Collider（FBX 不自动生成），所以一般不需要排除自己。</summary>
        public LayerMask BlockMask = ~0;

        /// <summary>贴到这么近时临时隐藏角色模型（否则模型糊住半个屏幕）。&lt;=0 表示不隐藏。</summary>
        public float HideTargetBelow = 0.85f;

        /// <summary>机位离地最低高度（防止相机钻到地面以下）。</summary>
        public float MinHeightAboveGround = 0.35f;

        /// <summary>滚轮缩放系数（每单位滚轮的米数）。</summary>
        public float ZoomSpeed = 8f;

        /// <summary>键盘（+/-）连续缩放的速率（米/秒）。</summary>
        public float KeyZoomSpeed = 7f;

        /// <summary>多高度探针的偏移（0=注视点；±0.5/1.0 覆盖墙沿与门框，避免"露缝看穿"）。</summary>
        static readonly float[] ProbeHeights = { 0f, 0.5f, -0.5f, 1.0f };

        /// <summary>遮挡收缩 / 恢复的阻尼系数（收缩快、恢复慢）。</summary>
        const float ShrinkDamping = 30f;
        const float RecoverDamping = 6f;

        Vector3 _vel;
        float _curDist;
        bool _inited;
        Renderer[] _targetRenderers;
        bool _targetHidden;
        bool _controlEnabled = true;

        /// <summary>相机是否响应输入（弹窗 / 死亡 / 过场时可关掉，机位仍会跟随）。</summary>
        public void SetControlEnabled(bool on) => _controlEnabled = on;

        /// <summary>切换跟随目标；会立刻贴到目标后方（避免从上一个目标"飞"过来）。</summary>
        public void SetTarget(Transform t)
        {
            Target = t;
            if (t != null)
            {
                Yaw = t.eulerAngles.y;
            }
            CacheTargetRenderers();
            _inited = false;
        }

        void Start()
        {
            _curDist = Distance;
            if (Target != null)
            {
                Yaw = Target.eulerAngles.y;
            }
            CacheTargetRenderers();
        }

        void CacheTargetRenderers()
        {
            // 换目标 / 清目标前，先把**旧目标**被隐藏的 Renderer 恢复 ——
            // 原实现只把 _targetHidden 复位（隐藏状态随之被遗忘），旧角色模型永久不可见。
            if (_targetHidden && _targetRenderers != null)
            {
                foreach (var r in _targetRenderers)
                {
                    if (r != null) r.enabled = true;
                }
            }

            _targetRenderers = Target != null ? Target.GetComponentsInChildren<Renderer>(true) : null;
            _targetHidden = false;
        }

        void LateUpdate()
        {
            ReadInput();
            if (Target == null)
            {
                return;
            }
            if (_targetRenderers == null)
            {
                CacheTargetRenderers();
            }

            var pivot = Target.position + PivotOffset;
            var rot = Quaternion.Euler(Pitch, Yaw, 0f);
            var dirBack = -(rot * Vector3.forward);

            // ---------- ① 多高度探针：取最小可用距离 ----------
            var want = Distance;
            for (var i = 0; i < ProbeHeights.Length; i++)
            {
                var origin = pivot + Vector3.up * ProbeHeights[i];
                if (Physics.SphereCast(origin, CollisionRadius, dirBack, out var hit, Distance,
                        BlockMask, QueryTriggerInteraction.Ignore))
                {
                    var d = hit.distance - 0.12f; // 留一点余量，别把相机贴在面上
                    if (d < want)
                    {
                        want = d;
                    }
                }
            }

            // ---------- ② 被挡时允许拉到很近（"看不到角色 / 镜头穿墙"的正解）----------
            want = Mathf.Clamp(want, MinDistance, Distance);

            // ---------- ③ 收缩快、恢复慢 ----------
            var k = want < _curDist
                ? 1f - Mathf.Exp(-Time.deltaTime * ShrinkDamping)
                : 1f - Mathf.Exp(-Time.deltaTime * RecoverDamping);
            _curDist = Mathf.Lerp(_curDist, want, k);

            var finalPos = pivot + dirBack * _curDist;

            // ---------- ④ 最终兜底：机位若仍与几何体重叠，继续拉近 ----------
            var guard = 0;
            while (Physics.CheckSphere(finalPos, CollisionRadius * 0.9f, BlockMask, QueryTriggerInteraction.Ignore)
                   && _curDist > MinDistance && guard++ < 8)
            {
                _curDist = Mathf.Max(MinDistance, _curDist - 0.18f);
                finalPos = pivot + dirBack * _curDist;
            }

            // ---------- ⑤ 不钻到地面以下（相对**地面**而不是绝对世界 Y）----------
            // 原实现与绝对 Y 比较：地形整体抬高后（地面 Y≠0）固定的 0.35 早已在地下，相机照样钻地。
            var minY = MinHeightAboveGround;
            if (Physics.Raycast(pivot + Vector3.up, Vector3.down, out var groundHit, 500f,
                    BlockMask, QueryTriggerInteraction.Ignore))
            {
                minY = groundHit.point.y + MinHeightAboveGround;
            }
            finalPos.y = Mathf.Max(finalPos.y, minY);

            if (!_inited)
            {
                transform.position = finalPos;
                _inited = true;
            }
            else
            {
                transform.position = Vector3.SmoothDamp(transform.position, finalPos, ref _vel, FollowSmooth);
            }
            transform.rotation = Quaternion.LookRotation(pivot - transform.position, Vector3.up);

            // ---------- ⑥ 贴脸时隐藏角色模型 ----------
            SetTargetHidden(HideTargetBelow > 0f && _curDist < HideTargetBelow);
        }

        /// <summary>
        /// 读输入：右键拖拽转视角、滚轮与 +/- 缩放。
        ///
        /// 走引擎 `Game.Input`（旧 InputManager / 新 InputSystem 都能用，
        /// 直连 `Input.mousePosition` 之类的写法在只有新后端的工程里会静默失效）。
        /// 滚轮轴（"Mouse ScrollWheel"）两个后端都已支持（InputSystem 后端会把它换算成
        /// 与旧后端一致的量纲）；+/- 键位缩放不受后端影响。
        /// 输入被锁（UI 弹窗 / 死亡遮罩）或引擎未启动时**只跳过输入**，机位仍会跟随 ——
        /// 相机不响应输入≠相机卡住不动。
        /// </summary>
        void ReadInput()
        {
            if (!_controlEnabled)
            {
                return;
            }
            var input = Game.Input;
            if (input == null || input.IsLocked)
            {
                return;
            }

            var wheel = input.GetAxis("Mouse ScrollWheel");
            if (Mathf.Abs(wheel) > 0.0001f)
            {
                Distance = Mathf.Clamp(Distance - wheel * ZoomSpeed, MinDistance + 0.5f, MaxDistance);
            }

            var keyZoom = 0f;
            if (input.GetKey(GameKey.Equals) || input.GetKey(GameKey.Plus))
            {
                keyZoom -= 1f;
            }
            if (input.GetKey(GameKey.Minus))
            {
                keyZoom += 1f;
            }
            if (keyZoom != 0f)
            {
                Distance = Mathf.Clamp(Distance + keyZoom * KeyZoomSpeed * Time.deltaTime,
                    MinDistance + 0.5f, MaxDistance);
            }

            if (input.GetMouseButton(1))
            {
                var d = input.MouseDelta;
                Yaw += d.x * Sensitivity;
                Pitch = Mathf.Clamp(Pitch - d.y * Sensitivity, MinPitch, MaxPitch);
            }
        }

        /// <summary>贴脸时隐藏 / 恢复角色模型（相机被墙逼到角色身上时，避免模型糊满屏幕）。</summary>
        void SetTargetHidden(bool hide)
        {
            if (hide == _targetHidden || _targetRenderers == null)
            {
                return;
            }
            _targetHidden = hide;
            foreach (var r in _targetRenderers)
            {
                if (r != null)
                {
                    r.enabled = !hide;
                }
            }
        }
    }
}
