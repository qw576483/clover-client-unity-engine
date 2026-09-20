using UnityEngine;

namespace CloverEngine
{
    /// <summary>
    /// 视点晃动（view bob）的**数值口径**。七个字段全部由业务传入 ——
    /// 引擎不内置任何项目取值（出处的数值真源在业务侧：幅度 / 频率 / 满幅基准速度来自项目常量，
    /// 增益与衰减速度、横滚角、落地沉降来自项目的战斗手感表）。
    /// </summary>
    public struct ViewBobConfig
    {
        /// <summary>满幅基准速度（米/秒）：水平速度达到该值时 bob 取满幅度。</summary>
        public float FullAmplitudeSpeed;

        /// <summary>幅度（米）。</summary>
        public float Amount;

        /// <summary>频率系数（每秒推进的相位，弧度）：相位推进速率 = 该值 × 当前振幅。</summary>
        public float Speed;

        /// <summary>振幅平滑速度（每秒的归一化变化量）：**起停共用**这一个值。</summary>
        public float BlendSpeed;

        /// <summary>落地沉降幅度（米）。</summary>
        public float LandDipAmount;

        /// <summary>落地沉降的恢复时间常数（秒）。</summary>
        public float LandDipTau;

        /// <summary>横滚角（度，绕视线轴）。</summary>
        public float RollDegrees;
    }

    /// <summary>
    /// 第一人称的视点晃动与落地沉降 —— **纯逻辑**（刻意**不是** <c>MonoBehaviour</c>：
    /// 无 Unity 生命周期依赖 ⇒ 可离线断言、可被任意持有者驱动）。
    ///
    /// <para><b>出处</b>：能力下沉（E-core-18）。搬自 <c>clover-project-cs16</c> 的
    /// <c>client/Assets/Scripts/Module/CameraRig/ViewBob.cs</c>（**原** 77 行，<b>零业务类型引用</b>；
    /// 下沉后该项目**已删除该文件**并改用本件）—— 只把当时写死的 7 个常量换成
    /// <see cref="ViewBobConfig"/>；**逐字搬移、一个分支都没改**
    /// —— 那套数值与分支是已验收的手感，⛔ 不许"顺手修正"。</para>
    ///
    /// <para><b>契约：只输出偏移，不碰相机</b>。由调用方决定怎么用；典型的第一人称相机把它当作
    /// "画面局部偏移"叠加到眼睛位置上。特别注意：<b>bob 不影响射线起点 / 视线方向</b> ——
    /// 否则子弹会随脚步左右摆（射线由同一个调用方用不含 bob 的位姿发出）。</para>
    ///
    /// <para><b>语义（照搬原实现，逐条）</b>：</para>
    /// <list type="number">
    /// <item><c>dt &lt;= 0</c> 直接返回（不推进任何状态）；</item>
    /// <item>目标振幅 = 速度 / <see cref="ViewBobConfig.FullAmplitudeSpeed"/> 并夹到 0~1；</item>
    /// <item>振幅用 <c>Mathf.MoveTowards</c> 朝目标走 —— <b>起与停用同一个平滑速度</b>
    /// （突然停步时振幅慢慢收，不会"抖一下"）；</item>
    /// <item>相位推进速率 = <see cref="ViewBobConfig.Speed"/> × 当前振幅 ⇒
    /// <b>只有有幅度（在动）时相位才推进</b>；超过 2π 回绕；</item>
    /// <item>落地沉降**只在 <c>onGround</c> 由假变真**时触发一次（等于给一次
    /// <see cref="ViewBobConfig.LandDipAmount"/>）；</item>
    /// <item>沉降按 <c>exp(-dt / LandDipTau)</c> 指数衰减，剩量 &lt; 0.0005 米时**归零**
    /// （避免永远留个极小值）；</item>
    /// <item>输出 <c>Offset = (cos(phase)·amount, sin(phase·2)·amount - landDip, 0)</c>、
    /// <c>Roll = cos(phase)·RollDegrees·amplitude</c>。</item>
    /// </list>
    ///
    /// <para><b>已知前提 / 边界</b>：<see cref="Config"/> 必须由业务填满（尤其
    /// <see cref="ViewBobConfig.FullAmplitudeSpeed"/> 必须 &gt; 0）—— 它按比例参与运算，
    /// 引擎**不做兜底、不替业务定数值**；<see cref="Reset"/> 之外的字段不对外可写。
    /// 非线程安全（主线程使用）。</para>
    /// </summary>
    public sealed class ViewBob
    {
        /// <summary>数值口径（业务填充；见类注释的「已知前提」）。</summary>
        public ViewBobConfig Config { get; set; }

        private float _amplitude;      // 0~1，随速度平滑
        private float _phase;          // 步伐相位（弧度）
        private float _landDip;        // 落地沉降的剩余量（米）
        private bool _wasOnGround = true;

        /// <summary>本帧的**摄像机局部空间**偏移（右/上/前）。</summary>
        public Vector3 Offset { get; private set; }

        /// <summary>本帧的横滚角（度，绕视线轴）。</summary>
        public float Roll { get; private set; }

        /// <summary>停止 / 重生 / 观战切换时把状态清零（避免残留偏移）。</summary>
        public void Reset()
        {
            _amplitude = 0f;
            _phase = 0f;
            _landDip = 0f;
            Offset = Vector3.zero;
            Roll = 0f;
        }

        /// <param name="dt">帧间隔。</param>
        /// <param name="speedXZ">水平速度（米/秒，业务一般取角色速度的 XZ 模长）。</param>
        /// <param name="onGround">是否在地面（用于落地沉降）。</param>
        public void Tick(float dt, float speedXZ, bool onGround)
        {
            if (dt <= 0f) return;

            var cfg = Config;
            var targetAmp = Mathf.Clamp01(speedXZ / cfg.FullAmplitudeSpeed);

            // 起停都用同一个平滑速度：突然停步时振幅慢慢收（不会"抖一下"）。
            _amplitude = Mathf.MoveTowards(_amplitude, targetAmp, dt * cfg.BlendSpeed);

            // 频率随速度增长（慢走时步伐慢），相位只在动的时候推进。
            _phase += dt * cfg.Speed * _amplitude;
            if (_phase > Mathf.PI * 2f) _phase -= Mathf.PI * 2f;

            // 落地：脚刚接地时给一次"沉一下"。
            if (onGround && !_wasOnGround) _landDip = cfg.LandDipAmount;
            _wasOnGround = onGround;

            if (_landDip > 0f)
            {
                var keep = Mathf.Exp(-dt / cfg.LandDipTau);
                _landDip *= keep;
                if (_landDip < 0.0005f) _landDip = 0f;
            }

            var amount = cfg.Amount * _amplitude;
            var x = Mathf.Cos(_phase) * amount;
            var y = Mathf.Sin(_phase * 2f) * amount;

            Offset = new Vector3(x, y - _landDip, 0f);
            Roll = Mathf.Cos(_phase) * cfg.RollDegrees * _amplitude;
        }
    }
}
