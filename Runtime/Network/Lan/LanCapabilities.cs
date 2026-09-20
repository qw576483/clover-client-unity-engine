using UnityEngine;

namespace CloverEngine
{
    /// <summary>
    /// 局域网寻服平台能力表（先例：<see cref="TransportCapabilities"/>）。
    ///
    /// <para>
    /// 存在的理由与线路能力表一致：让「不支持」**可见**而不是静默失败 ——
    /// WebGL 没有 BSD socket，浏览器不能收发 UDP 广播，寻服在那里根本不可能工作；
    /// 若不显式判定，表现会是「点了扫描、什么都不发生」（最难查的一类问题）。
    /// 读取方：<see cref="CloverLan"/> 与 <see cref="LanBrowser"/>（扫描入口做平台校验）。
    /// </para>
    /// </summary>
    public static class LanCapabilities
    {
        /// <summary>当前平台能否寻服。</summary>
        public static bool IsSupported => IsSupportedOn(Application.platform);

        /// <summary>
        /// 指定平台能否寻服（供单测；<b>不读</b>当前平台，因此可以在 Editor 里断言 WebGL 的结论）。
        /// </summary>
        /// <param name="platform">待判定平台</param>
        public static bool IsSupportedOn(RuntimePlatform platform)
        {
            return platform != RuntimePlatform.WebGLPlayer;
        }

        /// <summary>
        /// 不能寻服时的原因（可直接显示给玩家）；能时为空串。
        /// </summary>
        /// <param name="platform">待判定平台</param>
        public static string ReasonFor(RuntimePlatform platform)
        {
            if (platform == RuntimePlatform.WebGLPlayer)
            {
                // 文案即产品文案：说清「为什么不能」而不是只报「不支持」。
                return "WebGL 无 BSD socket，浏览器不能收发 UDP 广播，局域网寻服不可用";
            }

            return string.Empty;
        }

        /// <summary>
        /// 寻服的**宿主工程前提**（"能力表说支持，实机却收不到"一类问题的可见化出口；
        /// 无前提的平台为空串）：
        /// <list type="bullet">
        /// <item>iOS：访问本地网络必须在宿主工程声明 <c>NSLocalNetworkUsageDescription</c>
        /// （iOS 14+ 首次访问弹权限），未声明时广播/组播收发会被系统静默拒绝；</item>
        /// <item>Android：普通 UDP 广播收发一般不需要额外权限（<c>CHANGE_WIFI_MULTICAST_STATE</c>
        /// 只有用组播锁时才需要）。</item>
        /// </list>
        /// </summary>
        public static string PlatformPrerequisite =>
            "iOS 需在宿主工程声明 NSLocalNetworkUsageDescription（否则本地网络收发被系统静默拒绝）；" +
            "Android 普通 UDP 广播收发无需额外权限";
    }
}
