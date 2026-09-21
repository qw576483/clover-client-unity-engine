using UnityEngine;

namespace CloverEngine
{
    /// <summary>
    /// 相机相关的**纯函数**：水平/垂直 FOV 换算、yaw/pitch → 视线方向、指数平滑跟随。
    ///
    /// <para><b>出处</b>：能力下沉。三个函数搬自 <c>clover-project-cs16</c> 的
    /// <c>client/Assets/Scripts/Module/CameraRig/FirstPersonCamera.cs</c>（<b>逐字搬移</b>，
    /// 数值与分支一个都没动 —— 那是已验收的 FOV 口径与镜头手感）；原处**已改为调用本件**、
    /// ⛔ 不留同名 `private static` 副本。</para>
    ///
    /// <para><b>为什么合并成三个</b>（`结构规则.md` §4.4「已有能力不准再起第二套」）：原项目里还有一个
    /// <c>FollowRecoil(current, target, dt)</c>，它**只是**「按 |target| &gt; |current| 在上升/回落两个
    /// 时间常数里挑一个，再调 <see cref="Follow"/>」—— 挑常数用的是业务手感数值，属调用点的事，
    /// 所以这里**只留一条 <see cref="Follow"/>**，那两行选择就地内联在调用点，⛔ 不留第二份跟随实现。</para>
    /// </summary>
    public static class CameraMath
    {
        /// <summary>
        /// 水平 FOV → 垂直 FOV（度）= 原版 <c>CalcFov</c> 的等价式。
        ///
        /// <para><b>出处</b>：<c>HLSDK/cl_dll/view.cpp:1737-1752</c>
        /// （<c>x = width/tan(fov_x/360*π)</c>；<c>a = atan(height/x)</c>；<c>return a*360/π</c>；
        /// 越界 <c>fov_x</c> 回退 90）⇒ <c>fov_y = 2·atan((H/W)·tan(fov_x/2))</c>。
        /// 本函数把它写成 <c>2·atan(tan(fov_x/2)/aspect)</c>（<c>aspect = W/H</c>），两者等价。</para>
        ///
        /// <para>用途：Unity 的 <c>Camera.fieldOfView</c> 是**垂直** FOV，而很多玩法（原版 GoldSrc 系）
        /// 的水平 FOV 才是固定口径 ⇒ 每帧按当前宽高比换算，⛔ 不许把水平值直接赋给 <c>fieldOfView</c>，
        /// 也不许写死垂直值。</para>
        /// </summary>
        /// <param name="fovXDegrees">水平 FOV（度）。&lt;1 或 &gt;179 时按 90 处理（原版回退）。</param>
        /// <param name="aspect">宽高比 <c>W/H</c>。&lt;=0 时原样返回水平值（取不到视口的退化分支）。</param>
        public static float FovYFromFovX(float fovXDegrees, float aspect)
        {
            var fovX = (fovXDegrees < 1f || fovXDegrees > 179f) ? 90f : fovXDegrees;   // 原版 CalcFov 的越界回退
            if (aspect <= 0f) return fovX;
            return 2f * Mathf.Atan(Mathf.Tan(fovX * 0.5f * Mathf.Deg2Rad) / aspect) * Mathf.Rad2Deg;
        }

        /// <summary>
        /// yaw/pitch（度）→ 单位方向向量：
        /// <c>(cos(pitch)·sin(yaw), sin(pitch), cos(pitch)·cos(yaw))</c>
        /// —— yaw 0 = +Z、pitch 正 = 抬头（与原版模拟的视线口径一致）。
        ///
        /// <para><b>注意符号与角度口径</b>：pitch **正数为抬头**（+Y），这与 Unity 的
        /// <c>Quaternion.Euler(-pitch, yaw, 0)</c>（X 轴正向与抬头相反，故取负）是同一件事的两种写法 ——
        /// 相机朝向用后者、射线方向用本函数，两边必须给出同一个方向。</para>
        ///
        /// <para><b>已知边界</b>：本函数是**纯几何**，不做俯仰夹紧。夹紧是玩法口径
        /// （例如某项目的 <c>PitchLimit = ±89°</c>），由调用方在传入前完成 —— 调用点本来就必须先夹
        /// （相机的<b>最终</b>俯仰角还要再叠受击抖动并再夹一次），⛔ 引擎不替业务定这个限位值。</para>
        /// </summary>
        public static Vector3 AimDirection(float yawDegrees, float pitchDegrees)
        {
            var yawRad = yawDegrees * Mathf.Deg2Rad;
            var pitchRad = pitchDegrees * Mathf.Deg2Rad;
            return new Vector3(
                Mathf.Cos(pitchRad) * Mathf.Sin(yawRad),
                Mathf.Sin(pitchRad),
                Mathf.Cos(pitchRad) * Mathf.Cos(yawRad));
        }

        /// <summary>
        /// 指数平滑跟随（帧率无关）：把 <paramref name="current"/> 朝 <paramref name="target"/> 拉，
        /// <paramref name="tau"/> = 时间常数（秒，越小越跟手）。
        ///
        /// <para><c>k = 1 - exp(-dt/tau)</c> 后 <c>Lerp</c>：<c>dt→0</c> 时不跳变、逐步趋近 target；
        /// <paramref name="tau"/> &lt;= 0 视为"不平滑"，直接返回 target。</para>
        ///
        /// <para><b>合并口径</b>（见类注释）：原项目的 <c>FollowRecoil</c> = 先按
        /// <c>|target| &gt; |current|</c> 判"是在上跳还是回正"，选各自的 tau，再调本函数 ——
        /// 那一步选择用的是业务手感数值 ⇒ 留在调用点内联，引擎只提供这一条跟随。</para>
        /// </summary>
        /// <param name="current">当前值。</param>
        /// <param name="target">目标值。</param>
        /// <param name="dt">帧间隔（秒）。</param>
        /// <param name="tau">时间常数（秒）。&lt;=0 ⇒ 直接吸附到 target。</param>
        public static float Follow(float current, float target, float dt, float tau)
        {
            if (tau <= 0f) return target;
            var k = 1f - Mathf.Exp(-dt / tau);
            return Mathf.Lerp(current, target, k);
        }
    }
}
