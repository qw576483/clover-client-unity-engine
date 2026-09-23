using UnityEngine;

namespace CloverEngine
{
    /// <summary>
    /// 相机相关的**纯函数**：水平/垂直 FOV 换算、yaw/pitch → 视线方向、指数平滑跟随、临界阻尼跟随。
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
    ///
    /// <para><b>★ 本片新增两条（镜头跟随抖动 / 能力下沉）</b>：
    /// <see cref="Follow(Vector3,Vector3,float,float)"/> 与
    /// <see cref="SmoothDamp(Vector3,Vector3,ref Vector3,float,float)"/>。
    /// 前者逐字复用既有 float 版公式（同一口径的 Vector3 重载）；
    /// 后者是**临界阻尼**跟随（带速度状态），下沉自 <c>clover-project-diablo2</c> 的相机跟随
    /// （原实现是「纯指数滞后」，在 8 向锯齿路径上滞后矢量每换向转 45° ⇒ 画面左右摆）。
    /// ⛔ 两处都只允许这一个实现，业务侧不许再写同名私有副本。</para>
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

        /// <summary>
        /// 指数平滑跟随的 **Vector3 重载**：把 <paramref name="current"/> 朝 <paramref name="target"/> 拉。
        ///
        /// <para><b>出处 / 下沉记录</b>：逐字复用上方 float 版的公式（<c>k = 1 - exp(-dt/tau)</c> +
        /// <c>Lerp</c>，两个早退分支一致），**数值口径一字未改** —— 它下沉自
        /// <c>clover-project-diablo2</c> 的 <c>Module/Camera/CameraRig.SmoothTowards</c>
        /// （该文件的原注释写「与引擎 ThirdPersonCamera 同风格」）；原处的那份私有实现已删除、
        /// 改为调用本件（⛔ 不留第二份）。同项目 `tools/probes/hosts/playercheck` 直接断言
        /// 「本重载与 float 版同公式」。</para>
        ///
        /// <para>⚠️ 与 <c>dt</c> 的关系：<c>dt &lt;= 0</c>（暂停 / 首帧）时返回 <paramref name="current"/>
        /// —— 调用方因此不需要自己判 <c>dt</c>。注意 float 版没有这一支（它的 <c>dt=0</c> 靠
        /// <c>k=0</c> 自然保持不动，结果相同），两支语义一致。</para>
        /// </summary>
        /// <param name="current">当前值。</param>
        /// <param name="target">目标值。</param>
        /// <param name="dt">帧间隔（秒）。&lt;=0 ⇒ 保持不动。</param>
        /// <param name="tau">时间常数（秒）。&lt;=0 ⇒ 直接吸附到 target。</param>
        public static Vector3 Follow(Vector3 current, Vector3 target, float dt, float tau)
        {
            if (tau <= 0f) return target;
            if (dt <= 0f) return current;
            var k = 1f - Mathf.Exp(-dt / tau);
            return Vector3.Lerp(current, target, k);
        }

        /// <summary>
        /// **临界阻尼**跟随（二阶、带速度状态）：把 <paramref name="current"/> 朝 <paramref name="target"/>
        /// 拉，<paramref name="velocity"/> 是**跨帧保留**的速度状态（调用方自己持有）。
        ///
        /// <para><b>与 <see cref="Follow(Vector3,Vector3,float,float)"/> 的区别（为什么需要第二条）</b>：
        /// 一阶指数滞后只保存"位置误差"，速度方向在目标换向的那一帧就跟着换 ⇒ 8 向锯齿路径上
        /// 滞后矢量会突然转 45°（画面左右摆）；临界阻尼保存**速度**状态，换向时速度是连续的
        /// （加速度也不跳），收敛过程不过冲、不振荡 —— 这是引擎自带 <c>CameraManager.Tick</c>
        /// （<c>Runtime/Presentation/Camera.cs:79</c>）一直在用的同一套语义（<c>Vector3.SmoothDamp</c>），
        /// 本函数把它变成**可被业务直接调用**的公开件。</para>
        ///
        /// <para><b>实现口径</b>：直接委托 <c>UnityEngine.Vector3.SmoothDamp(current, target, ref velocity,
        /// smoothTime, maxSpeed: Infinity, deltaTime: dt)</c> —— 与引擎 <c>CameraManager</c> 调的是同一个
        /// Unity 实现（那里用默认 <c>deltaTime</c>），本件只是把 <c>dt</c> 显式化（本项目由
        /// <c>App/Bootstrap</c> 的 tick 链把 <c>dt</c> 一路传下来，⛔ 不读 <c>Time.deltaTime</c> 以免与
        /// 业务 tick 的 dt 不一致）。<c>maxSpeed = Infinity</c> = 不额外限速（限速是玩法口径）。</para>
        ///
        /// <para><b>稳态滞后（调用方要拿它算数值）</b>：连续域的二阶临界阻尼方程
        /// <c>y'' + 2ωy' + ω²y = ω²x</c>（<c>ω = 2/smoothTime</c>）在目标匀速 <c>v</c> 下的稳态解是
        /// <c>y = v·t − v·smoothTime</c> ⇒ 连续解析滞后 <c>= v × smoothTime</c>（与一阶的 <c>v × tau</c> 同式）。
        /// **Unity 的离散实现滞后更小**：本机实测（`tools/probes/hosts/playercheck` 的稳态行，
        /// <c>v=3 格/s</c>、<c>smoothTime=0.02s</c>、<c>dt=1/60</c>）= 0.0346 格 = 解析值的 **0.577 倍**
        /// ⇒ 调用方要判"滞后上界"就用 <c>v × smoothTime</c>（保守），要判"实测值"就用 0.58 倍这一系数。
        /// 即：要消掉"换向摆幅"，靠的是把 <c>smoothTime</c> 取小；要消掉"过冲/振荡"，靠的是临界阻尼。</para>
        ///
        /// <para><b>退化分支</b>：<paramref name="dt"/> &lt;= 0（暂停 / 首帧）⇒ 保持不动**并把速度清零**
        /// （否则恢复播放时会带上暂停前的旧速度冲一下）；<paramref name="smoothTime"/> &lt;= 0 ⇒
        /// 直接吸附到 target 并把速度清零。</para>
        /// </summary>
        /// <param name="current">当前值。</param>
        /// <param name="target">目标值。</param>
        /// <param name="velocity">速度状态（**跨帧保留**；由调用方持有，本函数读写它）。</param>
        /// <param name="smoothTime">阻尼时间常数（秒，约等于稳态滞后 ÷ 目标速度）。&lt;=0 ⇒ 吸附。</param>
        /// <param name="dt">帧间隔（秒）。&lt;=0 ⇒ 保持不动。</param>
        public static Vector3 SmoothDamp(Vector3 current, Vector3 target, ref Vector3 velocity, float smoothTime, float dt)
        {
            if (smoothTime <= 0f) { velocity = Vector3.zero; return target; }
            if (dt <= 0f) { velocity = Vector3.zero; return current; }
            return Vector3.SmoothDamp(current, target, ref velocity, smoothTime, Mathf.Infinity, dt);
        }
    }
}
