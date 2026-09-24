using NUnit.Framework;

namespace CloverEngine.Tests
{
    /// <summary>
    /// <see cref="EngineHostOptions"/> 的「默认值 = 引擎现状（不干预）」契约测试（EditMode，纯 C#）。
    /// <para>
    /// 为什么值得单独挡：这三个开关的默认值**一改就等于改了所有项目的行为**，而症状全在项目侧、
    /// 离这里很远 ——
    /// ① <c>RunInBackground</c> 默认变 <c>true</c> ⇒ 每个项目的后台行为被悄悄改掉；
    /// ② <c>DuplicateInstanceGuard</c> 默认变 <c>true</c> ⇒ 宿主开始主动销毁"后来的"驱动器
    ///    （业务自己挂的 Editor 期驱动器会被杀手）；
    /// ③ <c>OwnAudioListener</c> 默认变 <c>true</c> ⇒ 监听器被搬到原点上的宿主，
    ///    <c>PlaySFXAt</c> 的 3D 衰减从"相机位置"变成"原点"，听起来像音量乱了。
    /// </para>
    /// <para>
    /// 本用例就是那条默认值的回归闸门：改默认值 ⇒ 立刻红。
    /// </para>
    /// </summary>
    public class EngineHostOptionsTests
    {
        /// <summary>不配置时，三个开关都必须是"别管我"（null / false = 引擎不干预）。</summary>
        [Test]
        public void Defaults_AreAllLeaveAlone()
        {
            var options = new EngineHostOptions();

            Assert.IsNull(options.RunInBackground,
                "null = 不干预（沿用播放器设置）。默认值是行为契约，不许改成 true / false");
            Assert.IsFalse(options.DuplicateInstanceGuard,
                "默认不开启重复实例守卫（现状：不做任何检查）");
            Assert.IsFalse(options.OwnAudioListener,
                "默认不接管 AudioListener（现状：引擎完全不碰监听器归属）");
        }
    }
}
