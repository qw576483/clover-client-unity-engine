using System;
using UnityEngine;

namespace CloverEngine
{
    /// <summary>
    /// 设备标识的来源。仅用于诊断（日志 / 上报），不影响业务用法——
    /// 无论哪个来源，<see cref="IDeviceIdProvider.Value"/> 都保证非空且在同一台设备上稳定。
    /// </summary>
    public enum DeviceIdSource
    {
        /// <summary>平台原生 ID。最理想：换机器才变，但常不可用或不可靠（见 DeviceIdProvider 注释）。</summary>
        Native = 0,

        /// <summary>本地持久化的随机码。原生 ID 不可用时的兜底，首次生成后长期固定。</summary>
        Persisted = 1,

        /// <summary>本次会话临时码。持久化也不可用时的最后兜底：非空可用，但重启会变。</summary>
        Ephemeral = 2,
    }

    /// <summary>
    /// 设备唯一标识提供者：给出「这台设备是谁」。
    ///
    /// 与 <see cref="IQualityManager"/>（<c>Game.Quality</c>）无关：那个管画质跑多好，这个管身份。
    /// </summary>
    public interface IDeviceIdProvider
    {
        /// <summary>
        /// 当前设备的稳定标识（**保证非空**）。
        /// 同一台设备在 <see cref="DeviceIdSource.Native"/> / <see cref="DeviceIdSource.Persisted"/>
        /// 两种来源下跨会话一致；<see cref="DeviceIdSource.Ephemeral"/> 时仅本次会话有效。
        /// </summary>
        string Value { get; }

        /// <summary>
        /// 当前标识的来源（诊断用：日志 / 上报里区分 Native / Persisted / Ephemeral）。
        /// <para>
        /// 说明：引擎内部目前没有读取点（<c>DeviceIdSource</c> 只在实现里赋值），
        /// 这是有意保留的公开诊断契约，供业务在排障时读取，不要按"无人使用"删除。
        /// </para>
        /// </summary>
        DeviceIdSource Source { get; }
    }

    /// <summary>
    /// 设备标识实现：**三层兜底**，保证始终返回一个可用值。
    ///
    /// <para>
    /// 为什么要兜底而不是直接用 <c>SystemInfo.deviceUniqueIdentifier</c>：
    /// 那个 API 在各平台语义不同，且存在**静默失效**——不报错，但返回一个"看起来正常"的坏值：
    /// </para>
    /// <list type="bullet">
    ///   <item>WebGL：不支持，返回固定值/空 → **所有网页玩家会撞成同一个人**</item>
    ///   <item>模拟器：多为克隆镜像 → **多个"玩家"返回同一个 ID**</item>
    ///   <item>Unity 编辑器：返回开发机 ID → **全组开发共用同一个号**</item>
    ///   <item>Android：始终是 ANDROID_ID 的 md5 → **恢复出厂设置会变**</item>
    ///   <item>iOS：identifierForVendor → **卸载全部同厂商 App 后重装会变**</item>
    ///   <item>Windows：硬件指纹 → **换主板/硬盘会变**</item>
    /// </list>
    /// <para>
    /// 因此这里先取原生 ID 并**校验**（过滤上述坏值），不可用则退到本地持久化的随机码，
    /// 再不可用则退到会话级临时码。业务只管读 <see cref="Value"/>，不必关心这些差异。
    /// </para>
    /// <para>
    /// 隐私说明：原生 ID 直接作为返回值，因为它在各平台**本身已是摘要值**
    /// （如 Android 是 ANDROID_ID 的 md5），不含可直接识别的硬件信息。
    /// 若产品有更严格的隐私要求（或应用商店审核问及），可在返回值上再加盐哈希后上传。
    /// </para>
    /// </summary>
    internal class DeviceIdProvider : IDeviceIdProvider
    {
        /// <summary>
        /// 持久化键名。**改这个键等于让所有存量用户换新身份**（旧值读不到 → 重新生成），
        /// 除非确实要做身份迁移，否则不要改。
        /// </summary>
        private const string SettingKey = "device_id";

        /// <summary>
        /// 原生 ID 的最小可信长度。过短的串（"0"、"1"、空）基本都是占位值或降级结果。
        /// 真实的原生 ID 是 32 位十六进制（Android md5）或 36 位 UUID（iOS），远长于此。
        /// </summary>
        private const int MinNativeIdLength = 16;

        public string Value { get; }

        public DeviceIdSource Source { get; }

        public DeviceIdProvider()
        {
            // ① 平台原生 ID（校验后使用）
            var native = TryNativeId();
            if (native != null)
            {
                Value = native;
                Source = DeviceIdSource.Native;
                return;
            }

            // ② 本地持久化的随机码（没有就生成一个存下来）
            var saved = LoadPersisted();
            if (saved != null)
            {
                Value = saved;
                Source = DeviceIdSource.Persisted;
                return;
            }

            var generated = Generate();
            if (Persist(generated))
            {
                Value = generated;
                Source = DeviceIdSource.Persisted;
                return;
            }

            // ③ 持久化也不可用（隐私模式 / 无写权限 / 磁盘满）：
            //    用会话级临时码——它不跨会话，但至少非空、且不同设备不会撞。
            Value = generated;
            Source = DeviceIdSource.Ephemeral;
            Game.Logger?.Warn("DeviceId",
                "device id could not be persisted; using an ephemeral id for this session " +
                "(will change on restart). Anonymous identity may not survive a restart.");
        }

        /// <summary>
        /// 取平台原生 ID；不可用或疑似坏值时返回 null。
        /// </summary>
        private static string TryNativeId()
        {
            string raw;
            try
            {
                raw = SystemInfo.deviceUniqueIdentifier;
            }
            catch (Exception ex)
            {
                // 个别平台/受限环境下该属性可能抛异常（如 WebGL 早期版本）。
                Game.Logger?.Warn("DeviceId", $"SystemInfo.deviceUniqueIdentifier threw: {ex.Message}");
                return null;
            }

            if (!IsUsableNativeId(raw, out var reason))
            {
                // 降级是设计内的正常路径，但必须留痕：否则「网页端所有人同号」这类问题
                // 在现场表现为「玩家互相顶号」，没人会想到是设备码取的。
                // 用 Warn 而非 Info：网页端 / 模拟器上这会退化成"所有设备同一个码"，
                // 现场表现为"玩家互相顶号"，Info 级别很容易被忽略。
                Game.Logger?.Warn("DeviceId", $"native device id unusable ({reason}); falling back to persisted id");
                return null;
            }
            return raw;
        }

        /// <summary>
        /// 校验原生 ID 是否可信。这里是整个模块最关键的一处——
        /// 坏值不会报错，只会让大量设备"变成同一个人"。
        /// </summary>
        private static bool IsUsableNativeId(string id, out string reason)
        {
            // 编辑器里 SystemInfo.deviceUniqueIdentifier 返回的是**开发机 ID**：
            // 它不是"设备"的身份，且全组开发共用同一个号（画面上的表现是"互相顶号"）——
            // 属应过滤的坏值，必须在这里拦掉。
            if (Application.isEditor)
            {
                reason = "running in Unity Editor: dev machine id shared by the whole team";
                return false;
            }

            if (string.IsNullOrWhiteSpace(id))
            {
                reason = "empty";
                return false;
            }

            if (id.Length < MinNativeIdLength)
            {
                reason = $"too short ({id.Length} chars)";
                return false;
            }

            // 官方哨兵值：平台不支持该属性时返回它（WebGL 即属此类）。
            // 直接比常量而非硬编码 "n/a"，以免 Unity 将来调整哨兵值时这里失效。
            var unsupported = SafeGet(() => SystemInfo.unsupportedIdentifier);
            if (!string.IsNullOrEmpty(unsupported)
                && string.Equals(id, unsupported, StringComparison.Ordinal))
            {
                reason = $"unsupportedIdentifier (platform does not support it): {unsupported}";
                return false;
            }

            if (LooksLikePlaceholder(id))
            {
                reason = "placeholder value";
                return false;
            }

            // WebGL 与部分模拟器会退化成"设备型号"或"设备名"，导致同型号设备全部同号。
            // 这类值看起来完全正常，只能靠与 SystemInfo 的其它字段比对来识别。
            var model = SafeGet(() => SystemInfo.deviceModel);
            if (!string.IsNullOrEmpty(model) && string.Equals(id, model, StringComparison.OrdinalIgnoreCase))
            {
                reason = "equals device model";
                return false;
            }

            var name = SafeGet(() => SystemInfo.deviceName);
            if (!string.IsNullOrEmpty(name) && string.Equals(id, name, StringComparison.OrdinalIgnoreCase))
            {
                reason = "equals device name";
                return false;
            }

            reason = "";
            return true;
        }

        /// <summary>
        /// 是否形如占位值：全 0、全 f、以及常见的 unknown 字面量。
        /// </summary>
        private static bool LooksLikePlaceholder(string id)
        {
            // 全同字符（"0000000000000000"、"ffffffffffffffff" 等）
            var first = id[0];
            var allSame = true;
            for (var i = 1; i < id.Length; i++)
            {
                if (id[i] != first)
                {
                    allSame = false;
                    break;
                }
            }
            if (allSame)
            {
                return true;
            }

            return id.Equals("unknown", StringComparison.OrdinalIgnoreCase)
                || id.Equals("n/a", StringComparison.OrdinalIgnoreCase)
                || id.Equals("notsupported", StringComparison.OrdinalIgnoreCase)
                || id.Equals("unsupported", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 读取本地持久化的设备码；未存过或已损坏时返回 null。
        /// </summary>
        private static string LoadPersisted()
        {
            var saved = SafeGet(() => Game.Setting?.Get<string>(SettingKey, null));
            if (string.IsNullOrWhiteSpace(saved))
            {
                return null;
            }
            // 持久化的值由本类生成，格式可控；但仍做长度校验，防止被外部写脏。
            return saved.Length >= MinNativeIdLength ? saved : null;
        }

        /// <summary>
        /// 写入本地持久化。返回**真实的持久化结果**——失败时不抛异常，由调用方决定降级
        /// （走到会话级临时码并告警，见构造函数 ③）。
        /// <para>
        /// <c>Setting.Save</c> 不向外抛异常（内部自吞），若无条件 <c>return true</c>，
        /// 写盘失败也照样"成功"，<see cref="DeviceIdSource.Ephemeral"/> 兜底永远走不到，
        /// 表现为设备码每次重启都变、身份静默漂移 —— 故必须回报真实结果。
        /// </para>
        /// <para>
        /// 本方法能确证的是"值已进入设置存储且能读回"；真正的磁盘写入结果由 Setting 内部负责
        /// （失败会记 Error 并保持脏标记重试），外部无法观测 —— 因此 WebGL（Setting 无本地文件系统）
        /// 直接判失败，让调用方走临时码，而不是假装持久化成功。
        /// </para>
        /// </summary>
        private static bool Persist(string id)
        {
            if (Game.Setting == null)
            {
                Game.Logger?.Warn("DeviceId",
                    "persist device id skipped: Setting not available (Game.Launch not called?); using ephemeral id");
                return false;
            }

#if UNITY_WEBGL
            // WebGL 没有可写的本地文件系统：Setting 退化为内存存储，写进去的码跨会话必丢。
            // 直接判失败走 Ephemeral —— 不假装成功。
            Game.Logger?.Warn("DeviceId",
                "persist device id skipped: WebGL has no local filesystem; using an ephemeral id for this session");
            return false;
#else
            try
            {
                Game.Setting.Set(SettingKey, id);
                Game.Setting.Save();

                // 回读校验：Set 可能因值不可序列化等原因被拒绝写入；读不回来即没写进去。
                var readBack = Game.Setting.Get<string>(SettingKey, null);
                if (!string.Equals(readBack, id, StringComparison.Ordinal))
                {
                    Game.Logger?.Warn("DeviceId",
                        $"persist device id failed: read-back mismatch (got '{readBack ?? "null"}'); falling back to ephemeral id");
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                // Setting.Save 内部已记录一次；这里补上下文说明「设备码因此不持久」。
                Game.Logger?.Warn("DeviceId", $"persist device id failed: {ex.Message}; falling back to ephemeral id");
                return false;
            }
#endif
        }

        /// <summary>
        /// 生成一个新的随机标识（32 位十六进制，与 Android 原生 ID 形态一致，便于日志阅读）。
        ///
        /// 补充一条 Android 的限制：Android 8.0+ 的 <c>ANDROID_ID</c> 依赖应用**签名密钥**，
        /// 因此用 debug keystore 打的包与正式签名包在同一台设备上会得到**不同的**原生 ID；
        /// 走 Google Play 代签时，本地构建与商店下载版本的 ID 也不同。
        /// 对开发阶段意味着「换签名 = 换设备」，属预期行为，不必排查——
        /// 若本地反复重启拿到不同身份，先确认是不是换了签名/构建渠道。
        /// </summary>
        private static string Generate()
        {
            return Guid.NewGuid().ToString("N");
        }

        /// <summary>
        /// 安全求值：个别平台访问 SystemInfo 的某些字段可能抛异常，
        /// 这里统一降级为 null 而不是让设备码初始化整体失败。
        /// </summary>
        private static string SafeGet(Func<string> getter)
        {
            try
            {
                return getter();
            }
            catch
            {
                return null;
            }
        }
    }
}
