using UnityEngine;

namespace CloverEngine
{
    /// <summary>
    /// 鼠标位移 → 视角（yaw / pitch）累加器 —— **纯逻辑**（刻意**不是** <c>MonoBehaviour</c>：
    /// 无生命周期依赖 ⇒ 可离线断言）。
    ///
    /// <para><b>口径</b>：下面的符号方向、夹紧口径与灵敏度口径是**已验收的手感**，⛔ 不许"顺手修正"，
    /// 也⛔ 不留第二份副本：</para>
    /// <code>
    /// var degPerCount = sensitivity * degreesPerMouseCount;                     // 灵敏度 = 每 count 多少度
    /// _yaw = Mathf.Repeat(_yaw + delta.x * degPerCount, 360f);                   // 水平：360° 环绕（不夹紧）
    /// var pitchDelta = delta.y * degPerCount * (_invertY ? -1f : 1f);            // 垂直：鼠标向下 = 视角向下（反转则取负）
    /// _pitch = Mathf.Clamp(_pitch + pitchDelta, -limit, limit);                  // 俯仰夹在 ±limit
    /// // ForceLook：_yaw = Mathf.Repeat(yaw, 360f); _pitch = Mathf.Clamp(pitch, -limit, limit);
    /// </code>
    ///
    /// <para><b>Y 轴符号</b>：<c>delta.y</c> 在屏幕坐标里向下为正，而这里**不取负** ⇒
    /// 鼠标下移 = pitch 变小 = 视角向下（与"Y 轴反转"开关的语义一致：勾选即取负）。</para>
    ///
    /// <para><b>灵敏度口径</b>：参数是**"每 count 多少度"**（不是倍率）：由调用方把
    /// 玩家设置里的灵敏度乘上玩法自己的换算系数后传入（示例 = 设置值 × 0.022°/count）。
    /// 累加**不乘 dt**（鼠标位移本身就是增量，与帧率无关）。</para>
    ///
    /// <para><b>已知边界</b>：俯仰限位 <paramref name="pitchLimitDegrees"/> 由调用方传入
    /// （引擎不内置玩法限位值）；<see cref="Reset"/> **只做 yaw 的 360° 规范化**，pitch 的夹紧
    /// 属限位口径 ⇒ 由调用方传入已夹紧的值（<c>Mathf.Clamp</c> 保留在调用点）。
    /// 非线程安全（主线程使用）。</para>
    /// </summary>
    public sealed class LookAccumulator
    {
        /// <summary>当前水平朝向（度，0 = +Z；<c>Repeat(…, 360)</c> 规范化到 [0, 360)）。</summary>
        public float Yaw { get; private set; }

        /// <summary>当前俯仰（度，+ = 抬头；已按 <c>±pitchLimitDegrees</c> 夹紧）。</summary>
        public float Pitch { get; private set; }

        /// <summary>
        /// 把视角**直接**对齐到给定角度（出生 / 观战切换 / 接管本地玩家时用，不做平滑）：
        /// yaw 走 360° 规范化；pitch **按传入值原样接收**（限位夹紧由调用方完成，见类注释）。
        /// </summary>
        public void Reset(float yawDegrees, float pitchDegrees)
        {
            Yaw = Mathf.Repeat(yawDegrees, 360f);
            Pitch = pitchDegrees;
        }

        /// <summary>
        /// 累加一次鼠标位移。<paramref name="degreesPerCount"/> 已在调用方算好
        /// （= 灵敏度 × 玩法换算系数），本方法不读任何设置、不乘 <c>dt</c>。
        /// </summary>
        /// <param name="mouseDeltaX">本帧鼠标水平位移（count/像素）。</param>
        /// <param name="mouseDeltaY">本帧鼠标垂直位移（count/像素，屏幕坐标：向下为正）。</param>
        /// <param name="degreesPerCount">每 count 转多少度。</param>
        /// <param name="invertY">Y 轴反转（勾选后鼠标下移 = 视角向上）。</param>
        /// <param name="pitchLimitDegrees">俯仰角上下限（度，取绝对值使用）。</param>
        public void Add(float mouseDeltaX, float mouseDeltaY, float degreesPerCount, bool invertY,
            float pitchLimitDegrees)
        {
            Yaw = Mathf.Repeat(Yaw + mouseDeltaX * degreesPerCount, 360f);

            var pitchDelta = mouseDeltaY * degreesPerCount * (invertY ? -1f : 1f);
            Pitch = Mathf.Clamp(Pitch + pitchDelta, -pitchLimitDegrees, pitchLimitDegrees);
        }
    }
}
