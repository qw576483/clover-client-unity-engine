using NUnit.Framework;
using UnityEngine;

namespace CloverEngine.Tests
{
    /// <summary>
    /// <see cref="CloverFirstPersonCamera"/>（第一人称相机 rig）的回归用例：
    /// pitch 夹取（防翻面）、yaw 360° 回绕、分轴灵敏度 / Y 轴反转、后坐力跟随（快上慢回）、
    /// 摇晃线性衰减、<c>dt&lt;=0</c> 不推进视角、未绑定相机时不抛且朝向仍是单位向量。
    ///
    /// <para>纯逻辑为主（不依赖输入后端）；只有"下发位姿"两条用例需要真的相机 / 眼位节点，
    /// 用 <see cref="GameObject"/> 现造并在 <see cref="TearDown"/> 里销毁。
    /// <see cref="LogThrottle.Suppress"/> 打开以静音这些用例**故意触发**的非预期分支日志
    /// （dt&lt;=0 / 灵敏度 0 / 未绑定相机 / 未注入随机器）。</para>
    /// </summary>
    public class FirstPersonCameraTests
    {
        private GameObject _cameraGo;
        private GameObject _anchorGo;
        private CloverFirstPersonCamera _rig;

        [SetUp]
        public void SetUp()
        {
            LogThrottle.Suppress = true;
            _rig = new CloverFirstPersonCamera();
        }

        [TearDown]
        public void TearDown()
        {
            LogThrottle.Suppress = false;
            if (_cameraGo != null) Object.DestroyImmediate(_cameraGo);
            if (_anchorGo != null) Object.DestroyImmediate(_anchorGo);
            _cameraGo = null;
            _anchorGo = null;
        }

        // ─────────────────────── 视角累加 / 夹取 / 回绕 ───────────────────────

        /// <summary>灵敏度 = "每 count 多少度"：位移 × 灵敏度直接累加（不乘 dt —— 鼠标位移本身是增量）。</summary>
        [Test]
        public void ApplyLookDelta_AccumulatesDegreesPerCount()
        {
            _rig.SensitivityX = 2f;
            _rig.ApplyLookDelta(3f, 0f);
            Assert.AreEqual(6f, _rig.Look.Yaw, 1e-4f, "3 count × 2 度/count = 6 度");
        }

        /// <summary>yaw 是 360° 环绕（不夹紧、不出现负角 / 超 360）。</summary>
        [Test]
        public void ApplyLookDelta_YawWrapsInto0To360()
        {
            _rig.SensitivityX = 1f;
            _rig.ApplyLookDelta(400f, 0f);
            Assert.AreEqual(40f, _rig.Look.Yaw, 1e-4f, "Repeat(400, 360) = 40");

            _rig.SetView(10f, 0f);
            _rig.ApplyLookDelta(-25f, 0f);
            Assert.AreEqual(345f, _rig.Look.Yaw, 1e-4f, "10 - 25 = -15 ⇒ Repeat ⇒ 345（不出现负角）");
        }

        /// <summary>pitch 必须夹在 ±PitchLimit 内（否则视角翻面）。</summary>
        [Test]
        public void ApplyLookDelta_PitchIsClampedAtLimit()
        {
            _rig.SensitivityX = 1f;
            _rig.PitchLimit = 89f;

            _rig.ApplyLookDelta(0f, 1000f);      // 鼠标一直往下推
            Assert.AreEqual(89f, _rig.Look.Pitch, 1e-4f, "上限 +89（+ = 抬头）");
            Assert.AreEqual(89f, _rig.Pitch, 1e-4f, "含后坐力/摇晃后的最终 pitch 同样夹住");

            _rig.ApplyLookDelta(0f, -10000f);
            Assert.AreEqual(-89f, _rig.Look.Pitch, 1e-4f, "下限 -89");
        }

        /// <summary>Y 轴反转：勾选后鼠标下移（屏幕坐标 +y）= 视角向上（pitch 变小）。</summary>
        [Test]
        public void ApplyLookDelta_InvertY_FlipsPitchSign()
        {
            _rig.SensitivityX = 1f;
            _rig.InvertY = true;
            _rig.ApplyLookDelta(0f, 10f);
            Assert.AreEqual(-10f, _rig.Look.Pitch, 1e-4f, "反转后 mouse-down ⇒ pitch 负（视线向上）");

            _rig.InvertY = false;
            _rig.SetView(0f, 0f);
            _rig.ApplyLookDelta(0f, 10f);
            Assert.AreEqual(10f, _rig.Look.Pitch, 1e-4f, "不反转 ⇒ mouse-down 视线向下");
        }

        /// <summary>水平 / 垂直灵敏度分开时各用各的系数（分轴实现不得退化成同一系数）。</summary>
        [Test]
        public void ApplyLookDelta_SeparateAxes_UsesIndependentSensitivity()
        {
            _rig.SensitivityX = 1f;
            _rig.SensitivityY = 3f;
            _rig.SeparateAxes = true;

            _rig.ApplyLookDelta(10f, 10f);
            Assert.AreEqual(10f, _rig.Look.Yaw, 1e-4f, "水平用 SensitivityX");
            Assert.AreEqual(30f, _rig.Look.Pitch, 1e-4f, "垂直用 SensitivityY");

            _rig.SetView(0f, 0f);
            _rig.SeparateAxes = false;
            _rig.ApplyLookDelta(10f, 10f);
            Assert.AreEqual(10f, _rig.Look.Pitch, 1e-4f, "不分开时垂直跟随 SensitivityX");
        }

        /// <summary>灵敏度为 0：位移被忽略（视角不动），且**不抛**（边界之一）。</summary>
        [Test]
        public void ApplyLookDelta_ZeroSensitivity_KeepsView()
        {
            _rig.SensitivityX = 0f;
            _rig.SensitivityY = 0f;
            _rig.SeparateAxes = true;

            Assert.DoesNotThrow(() => _rig.ApplyLookDelta(100f, 100f));
            Assert.AreEqual(0f, _rig.Look.Yaw, 1e-4f);
            Assert.AreEqual(0f, _rig.Look.Pitch, 1e-4f);
        }

        /// <summary>SetView：yaw 规范化、pitch 按限位夹紧（直接对齐不做平滑）。</summary>
        [Test]
        public void SetView_NormalizesYawAndClampsPitch()
        {
            _rig.PitchLimit = 80f;
            _rig.SetView(-90f, 200f);
            Assert.AreEqual(270f, _rig.Look.Yaw, 1e-4f);
            Assert.AreEqual(80f, _rig.Look.Pitch, 1e-4f, "传入值超限必须在 SetView 里夹住");
        }

        // ─────────────────────── Tick 的边界 ───────────────────────

        /// <summary>dt &lt;= 0（暂停 / 首帧）：一帧也不推进（状态保持），不抛。</summary>
        [Test]
        public void Tick_NonPositiveDt_DoesNotAdvance()
        {
            _rig.SetView(10f, 5f);
            _rig.SetRecoil(7f, 7f);

            Assert.DoesNotThrow(() => _rig.Tick(0f));
            Assert.AreEqual(10f, _rig.Yaw, 1e-4f, "dt=0 不推进");
            Assert.AreEqual(0f, _rig.RecoilPitch, 1e-4f, "后坐力也不推进");

            Assert.DoesNotThrow(() => _rig.Tick(-1f));
            Assert.AreEqual(10f, _rig.Yaw, 1e-4f, "dt<0 同样不推进");
        }

        /// <summary>未绑定相机：Tick 不抛、朝向仍是单位向量、仍可被后续绑定接管。</summary>
        [Test]
        public void Tick_WithoutCamera_DoesNotThrowAndKeepsAimUnit()
        {
            _rig.SetView(45f, 30f);
            Assert.IsFalse(_rig.IsBound);

            Assert.DoesNotThrow(() => _rig.Tick(1f / 60f));
            Assert.AreEqual(1f, _rig.AimDirection.magnitude, 1e-4f, "视线方向必须是单位向量");
            Assert.AreEqual(0f, _rig.VerticalFieldOfView, 1e-4f, "未绑定相机 ⇒ 不下发 FOV");
        }

        // ─────────────────────── 后坐力（表现跟随） ───────────────────────

        /// <summary>后坐力只**跟随**权威值：上升快（小 tau）、回正慢（大 tau），且不自行累加。</summary>
        [Test]
        public void SetRecoil_RisesFastAndFallsSlow()
        {
            _rig.RecoilRiseTau = 0.02f;
            _rig.RecoilFallTau = 0.5f;

            _rig.SetRecoil(10f, 0f);
            _rig.Tick(0.02f);
            var risen = _rig.RecoilPitch;
            Assert.IsTrue(risen > 0f, "上跳方向：目标为正 ⇒ 表现偏移也为正");
            Assert.IsTrue(risen < 10f, "只跟随、不瞬移（tau>0 时是平滑）");

            // 权威值归零 ⇒ 慢回正：一帧只回落一点点（远小于上升那一帧的比例）。
            _rig.SetRecoil(0f, 0f);
            _rig.Tick(0.02f);
            var fallen = _rig.RecoilPitch;
            Assert.IsTrue(fallen < risen, "回正：偏移必须减小");
            Assert.IsTrue(fallen > risen * 0.9f, "回正慢：一帧只掉不到 10%（tau=0.5 vs 0.02）");

            // tau<=0 = 立即到位（引擎默认"不平滑"）。目标取**大于当前值** ⇒ 走上升支（tau=0 ⇒ 吸附）。
            _rig.RecoilRiseTau = 0f;
            _rig.SetRecoil(40f, -3f);
            _rig.Tick(0.02f);
            Assert.AreEqual(40f, _rig.RecoilPitch, 1e-4f, "上升 tau<=0 ⇒ 立即吸附到权威值");
            Assert.AreEqual(-3f, _rig.RecoilYaw, 1e-4f);

            _rig.ClearRecoil();
            Assert.AreEqual(0f, _rig.RecoilPitch, 1e-4f);
            Assert.AreEqual(0f, _rig.RecoilYaw, 1e-4f);
        }

        // ─────────────────────── 摇晃 ───────────────────────

        /// <summary>未注入随机器：摇晃被忽略（返回 false、不抛）—— 引擎不用全局静态随机器兜底。</summary>
        [Test]
        public void AddShake_WithoutRng_IsIgnored()
        {
            Assert.IsFalse(_rig.AddShake(5f, 0.3f));
            _rig.Tick(0.1f);
            Assert.AreEqual(0f, _rig.ShakeAmplitude, 1e-4f);
        }

        /// <summary>参数非正：忽略（幅度 / 时长都由业务传入，引擎没有默认值可退）。</summary>
        [Test]
        public void AddShake_NonPositiveArgs_IsIgnored()
        {
            _rig.ShakeRng = new Rng(12345);
            Assert.IsFalse(_rig.AddShake(0f, 0.3f));
            Assert.IsFalse(_rig.AddShake(5f, 0f));
            _rig.Tick(0.1f);
            Assert.AreEqual(0f, _rig.ShakeAmplitude, 1e-4f);
        }

        /// <summary>注入随机器后：幅度随时间线性衰减、到点归零（可复现：同一 seed 同一序列）。</summary>
        [Test]
        public void AddShake_WithRng_DecaysLinearlyToZero()
        {
            _rig.ShakeRng = new Rng(12345);
            _rig.ShakeRollScale = 1f;

            Assert.IsTrue(_rig.AddShake(5f, 0.5f));
            _rig.Tick(0.25f);
            Assert.AreEqual(2.5f, _rig.ShakeAmplitude, 1e-4f, "剩一半时间 ⇒ 幅度一半");
            Assert.AreNotEqual(0f, _rig.ShakePitch, "pitch 偏移应随幅度非 0");

            _rig.Tick(0.25f);
            Assert.AreEqual(0f, _rig.ShakeAmplitude, 1e-4f, "时长用完 ⇒ 幅度归零");
            Assert.AreEqual(0f, _rig.ShakePitch, 1e-4f);

            _rig.Tick(0.1f);
            Assert.AreEqual(0f, _rig.ShakeRoll, 1e-4f, "结束后横滚同样为 0");
        }

        // ─────────────────────── 下发位姿（需要真相机） ───────────────────────

        /// <summary>
        /// 有相机 + 眼位节点时：机位贴在"眼位 + 偏移"上、朝向与 <see cref="CloverFirstPersonCamera.AimDirection"/> 同向
        /// （<c>-pitch</c> 的符号口径必须让"pitch 正 = 抬头"在相机 forward 上成立）、
        /// <c>fieldOfView</c> = 水平 FOV 按宽高比换算出的**垂直**值。
        /// </summary>
        [Test]
        public void Tick_WithCamera_WritesEyePositionAimAndVerticalFov()
        {
            _cameraGo = new GameObject("fps-test-camera");
            var cam = _cameraGo.AddComponent<Camera>();
            cam.aspect = 16f / 9f;
            Assert.IsTrue(_rig.Bind(cam));

            _anchorGo = new GameObject("fps-test-anchor");
            _anchorGo.transform.position = new Vector3(3f, 0f, -2f);
            _rig.EyeAnchor = _anchorGo.transform;
            _rig.EyeOffset = new Vector3(0f, 1.62f, 0f);
            _rig.FovX = 90f;

            _rig.SetView(30f, 20f);
            _rig.Tick(1f / 60f);

            var eye = new Vector3(3f, 1.62f, -2f);
            Assert.AreEqual(0f, Vector3.Distance(eye, cam.transform.position), 1e-4f,
                "机位 = 眼位（ViewOffset 为 0）");
            Assert.AreEqual(0f, Vector3.Distance(eye, _rig.EyePosition), 1e-4f);

            Assert.AreEqual(0f, Vector3.Distance(_rig.AimDirection, cam.transform.rotation * Vector3.forward), 1e-4f,
                "相机 forward 必须与 AimDirection 同向（pitch 正 = 抬头）");
            Assert.AreEqual(1f, _rig.AimDirection.magnitude, 1e-4f);

            var expectFovY = CameraMath.FovYFromFovX(90f, 16f / 9f);
            Assert.AreEqual(expectFovY, cam.fieldOfView, 1e-3f, "fieldOfView 是**垂直**值（水平 90 → 垂直换算）");
            Assert.AreEqual(expectFovY, _rig.VerticalFieldOfView, 1e-3f);
        }

        /// <summary>ViewOffset（引擎件 ViewBob 的输出）加在**相机局部空间**：不影响眼位与视线方向。</summary>
        [Test]
        public void Tick_ViewOffset_IsLocalAndDoesNotMoveEyeOrAim()
        {
            _cameraGo = new GameObject("fps-test-camera");
            var cam = _cameraGo.AddComponent<Camera>();
            cam.aspect = 1f;
            _rig.Bind(cam);

            _anchorGo = new GameObject("fps-test-anchor");
            _anchorGo.transform.position = Vector3.zero;
            _rig.EyeAnchor = _anchorGo.transform;

            _rig.SetView(0f, 0f);
            _rig.ViewOffset = new Vector3(0.2f, 0.1f, 0f);
            _rig.Tick(1f / 60f);

            Assert.AreEqual(0f, Vector3.Distance(Vector3.zero, _rig.EyePosition), 1e-4f,
                "EyePosition 不含 ViewOffset（射线起点不受脚步晃动影响）");
            Assert.AreEqual(0f, Vector3.Distance(Vector3.forward, _rig.AimDirection), 1e-4f,
                "视线方向不受 ViewOffset 影响");
            Assert.AreEqual(0f, Vector3.Distance(new Vector3(0.2f, 0.1f, 0f), cam.transform.position), 1e-4f,
                "机位 = 眼位 + 视线空间的 ViewOffset");
        }

        /// <summary>解绑后 Tick 不再写相机（也不抛）。</summary>
        [Test]
        public void Unbind_StopsDrivingCamera()
        {
            _cameraGo = new GameObject("fps-test-camera");
            var cam = _cameraGo.AddComponent<Camera>();
            cam.aspect = 1f;
            _rig.Bind(cam);
            _rig.SetView(0f, 0f);
            _rig.Tick(1f / 60f);

            _rig.Unbind();
            Assert.IsFalse(_rig.IsBound);

            var outside = new Vector3(9f, 9f, 9f);
            _rig.EyeAnchor = null;
            cam.transform.SetPositionAndRotation(outside, Quaternion.identity);

            Assert.DoesNotThrow(() => _rig.Tick(1f / 60f));
            Assert.AreEqual(0f, Vector3.Distance(outside, cam.transform.position), 1e-4f,
                "解绑后相机位姿不许再被本 rig 改写");
        }

        /// <summary>引擎未启动（<c>Game.Input == null</c>）时的输入锁钩子：不抛、不认领锁。</summary>
        [Test]
        public void LockInput_WithoutEngineInput_DoesNotClaimLock()
        {
            Assert.DoesNotThrow(() => _rig.LockInput());
            Assert.DoesNotThrow(() => _rig.UnlockInput());
            Assert.IsFalse(_rig.InputLockedByRig);
        }
    }
}
