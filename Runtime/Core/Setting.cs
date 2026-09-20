using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using UnityEngine;

namespace CloverEngine
{
    /// <summary>
    /// 设置管理接口，提供键值对配置的读写、保存和加载功能。
    /// </summary>
    public interface ISetting
    {
        /// <summary>
        /// 获取指定键的值，若不存在或类型转换失败则返回默认值。
        /// </summary>
        /// <param name="key">配置键名。</param>
        /// <param name="defaultValue">默认值。</param>
        /// <returns>转换后的值或默认值。</returns>
        T Get<T>(string key, T defaultValue = default);
        /// <summary>
        /// 设置指定键的值。
        /// </summary>
        /// <param name="key">配置键名。</param>
        /// <param name="value">要设置的值。</param>
        void Set<T>(string key, T value);
        /// <summary>
        /// 将内存中的配置数据保存到文件。
        /// </summary>
        void Save();
        /// <summary>
        /// 从文件加载配置数据到内存。
        /// </summary>
        void Load();
        /// <summary>
        /// 删除指定键的配置项。
        /// <para>
        /// 【未接线】引擎内部（Runtime/Editor/Tests/Samples~）当前无消费点，属**公开契约**
        /// （业务清缓存 / 重置单项设置时直接用），不要按"无人使用"删除。
        /// </para>
        /// </summary>
        /// <param name="key">要删除的键名。</param>
        void Delete(string key);
        /// <summary>
        /// 清空所有配置项。
        /// <para>【未接线】同上：属**公开契约**，引擎内部无消费点，不要按"无人使用"删除。</para>
        /// </summary>
        void DeleteAll();
    }

    /// <summary>
    /// 设置管理的内部实现，基于 JSON 文件持久化键值对配置。
    ///
    /// <para><b>持久化语义（本轮加固）</b>：</para>
    /// <list type="bullet">
    /// <item>写盘走「先写 <c>settings.json.tmp</c> → <see cref="File.Replace"/> 原子替换」，
    /// 写一半崩溃/抛异常都不会截断或破坏既有 settings.json（旧实现 <c>File.WriteAllText</c> 直写目标文件，
    /// 中途失败会留下半截 json，下次 Load 解析失败 → 全部设置静默回默认）。</item>
    /// <item>Load 遇到损坏 json：先把原文件另存为 <c>settings.json.corrupt</c>（保留现场），
    /// 再打 Error 并回退默认值 —— 不再让损坏内容"留在磁盘上且永不修复"。</item>
    /// <item>目录为 null / 空串 / 非法时<b>不再抛异常</b>：旧实现 <c>Directory.CreateDirectory("")</c>
    /// 会抛 ArgumentException，把 <c>Game.Launch</c> 直接打崩；现在退化为内存存储并打 Warn。</item>
    /// <item><see cref="Set{T}"/> 会先验证值可 JSON 序列化，不可序列化时<b>拒绝写入并报错</b>：
    /// 否则一个坏值进字典后，此后每次 Save 都失败，整份配置再也落不了盘。</item>
    /// </list>
    ///
    /// <para><b>WebGL 限制</b>：<c>UNITY_WEBGL</c> 下没有可写的本地文件系统，
    /// 本类<b>不建目录、不读、不写</b>，设置只存在于内存中，进程结束即丢失（不抛异常）。
    /// 需要跨会话保留时请由业务自行上报/下发。</para>
    /// </summary>
    internal class Setting : ISetting
    {
        /// <summary>directory 为空/空白时的兜底目录（相对当前工作目录）。</summary>
        private const string DefaultDirectory = "setting";
        /// <summary>fileName 为空/空白时的兜底文件名。</summary>
        private const string DefaultFileName = "settings.json";
        /// <summary>原子写的临时文件后缀。</summary>
        private const string TempSuffix = ".tmp";
        /// <summary>损坏配置的留档后缀。</summary>
        private const string CorruptSuffix = ".corrupt";
        /// <summary>失败日志的降频阈值：第 1 次必报，之后每 N 次报一次。</summary>
        private const int FailureReportEvery = 100;

#if UNITY_WEBGL
        /// <summary>平台是否具备可用的本地文件系统（WebGL：没有）。用 static readonly 而非 const，
        /// 免得编译器把另一半分支判成不可达代码。</summary>
        private static readonly bool PlatformPersistent = false;
#else
        /// <summary>平台是否具备可用的本地文件系统。</summary>
        private static readonly bool PlatformPersistent = true;
#endif

        private readonly string _filePath;
        /// <summary>false = 退化为内存存储（WebGL 或目录不可用），不读写任何文件。</summary>
        private readonly bool _persistent;

        private Dictionary<string, object> _data = new();
        private bool _dirty;
        private int _getFailures;
        private int _saveFailures;

        /// <summary>
        /// 初始化设置管理器，指定配置文件路径并加载已有数据。
        /// <para>本构造<b>不抛异常</b>：directory 为空串/非法时回退默认目录；目录创建失败时退化为内存存储。</para>
        /// </summary>
        /// <param name="directory">配置文件所在目录。</param>
        /// <param name="fileName">配置文件名，默认为 settings.json。</param>
        public Setting(string directory, string fileName = "settings.json")
        {
            if (!PlatformPersistent)
            {
                // WebGL：无本地文件系统，设置只活在内存里（见类注释）
                _filePath = null;
                _persistent = false;
                _data = new Dictionary<string, object>();
                _dirty = false;
                return;
            }

            if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(fileName))
            {
                Report(LogLevel.Warn,
                    $"directory/fileName 为空，回退到默认 '{DefaultDirectory}/{DefaultFileName}'");
            }

            var dir = string.IsNullOrWhiteSpace(directory) ? DefaultDirectory : directory;
            var name = string.IsNullOrWhiteSpace(fileName) ? DefaultFileName : fileName;
            _filePath = SafeCombine(dir, name);

            var parent = SafeDirectoryName(_filePath);
            try
            {
                if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
                    Directory.CreateDirectory(parent);
                _persistent = true;
            }
            catch (Exception ex)
            {
                // 非法路径（含无效字符）/ 无权限 / 路径过长：不许把异常抛回 Game.Launch
                _persistent = false;
                Report(LogLevel.Warn,
                    $"设置目录不可用（'{parent}'）：{ex.Message}；本次运行退化为内存存储，配置不会持久化");
            }

            Load();
        }

        /// <summary>
        /// 获取指定键的值，若不存在或类型转换失败则返回默认值。
        /// 转换失败（数值溢出、格式非法、类型不匹配）时<b>会留下日志</b>，再回默认值 ——
        /// 旧实现空 catch 吞掉异常，表现为"配置莫名其妙变默认值"且无从排查。
        /// </summary>
        /// <param name="key">配置键名。</param>
        /// <param name="defaultValue">默认值。</param>
        /// <returns>转换后的值或默认值。</returns>
        public T Get<T>(string key, T defaultValue = default)
        {
            if (key == null) return defaultValue;

            if (!_data.TryGetValue(key, out var value))
                return defaultValue;

            try
            {
                if (value is T typed)
                    return typed;

                if (value is IConvertible)
                    return (T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture);

                if (typeof(T) == typeof(string))
                    return (T)(object)(value?.ToString() ?? string.Empty);

                // 【已删除的死分支】原先这里还有两条 `value is Dictionary<string,object> && typeof(T)==typeof(object)`
                // 与 `value is List<object> && typeof(T)==typeof(object)` 的特判：它们不可达 ——
                // T 为 object 时上面的 `value is T typed` 对任何非 null 值都已命中并直接返回，
                // 根本走不到这里。保留注释以免后人再把它们加回来。
            }
            catch (Exception ex)
            {
                // Convert.ChangeType 的 OverflowException / FormatException / InvalidCastException 都在这里
                ReportConversionFailure<T>(key, value, ex);
                return defaultValue;
            }

            // 走到这里说明类型确实对不上（例如存的是 List、要取 int）：留一条日志再回默认
            ReportConversionFailure<T>(key, value, null);
            return defaultValue;
        }

        /// <summary>
        /// 设置指定键的值，标记数据为已修改。
        /// <para>值不可 JSON 序列化时<b>拒绝写入并报错</b>：否则它进字典之后
        /// 每次 <see cref="Save"/> 都会在序列化阶段失败，整份配置永久落不了盘。</para>
        /// </summary>
        /// <param name="key">配置键名。</param>
        /// <param name="value">要设置的值。</param>
        public void Set<T>(string key, T value)
        {
            if (key == null)
            {
                Report(LogLevel.Warn, "Set 忽略 null key（该键未变更）");
                return;
            }

            // 无论当前是否落盘都校验（WebGL 下同样拒绝），保证各平台行为一致：
            // 值不可 JSON 序列化时拒绝写入并报错，否则它进字典之后每次 Save 都会失败，
            // 整份配置永久落不了盘。
            if (!IsDumpable(value))
            {
                Report(LogLevel.Error,
                    $"Set 拒绝写入：值无法序列化为 JSON（key='{key}', type={typeof(T)}），该键未变更");
                return;
            }

            _data[key] = value;
            _dirty = true;
        }

        /// <summary>
        /// 将内存中的配置数据保存到文件，仅当数据已修改时执行。
        /// <para>写盘是原子的（临时文件 + <see cref="File.Replace"/>），失败时保留旧文件、
        /// 记 Error 并保持 <c>_dirty</c> 为 true 以便下次重试；失败日志按次数降频，不会刷屏。</para>
        /// </summary>
        public void Save()
        {
            if (!_dirty) return;

            if (!_persistent)
            {
                // WebGL 或目录不可用：无盘可写，清掉脏标记，避免每次 Save 都重试并报错
                _dirty = false;
                return;
            }

            string json;
            try
            {
                json = MiniJson.Dump(_data);
            }
            catch (Exception ex)
            {
                NoteSaveFailure(ex, "序列化失败");
                return;
            }

            var tmp = _filePath + TempSuffix;
            try
            {
                var parent = SafeDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
                    Directory.CreateDirectory(parent);

                // 先写临时文件：写一半失败也只留下 .tmp，目标文件仍是上一次的完整内容
                File.WriteAllText(tmp, json);
                ReplaceAtomically(tmp, _filePath);

                _dirty = false;
                _saveFailures = 0;
            }
            catch (Exception ex)
            {
                try
                {
                    if (File.Exists(tmp))
                        File.Delete(tmp);
                }
                catch
                {
                    // 清理临时文件失败无副作用，下次 Save 会覆盖它
                }

                NoteSaveFailure(ex, "写盘失败");
            }
        }

        /// <summary>
        /// 从文件加载配置数据到内存，若文件不存在则保持空数据。
        /// <para>json 损坏时：另存 <c>{file}.corrupt</c> 副本 + 打 Error，然后回退默认值；
        /// 并把 <c>_dirty</c> 置 true，使下次 <see cref="Save"/> 用当前内容覆盖损坏文件（现场已留档）。</para>
        /// </summary>
        public void Load()
        {
            if (!_persistent)
            {
                _data = new Dictionary<string, object>();
                _dirty = false;
                return;
            }

            if (!File.Exists(_filePath)) return;

            string json;
            try
            {
                json = File.ReadAllText(_filePath);
            }
            catch (Exception ex)
            {
                Report(LogLevel.Error, $"Load 失败（读取 '{_filePath}'）：{ex.Message}；本次使用默认值", ex);
                _data = new Dictionary<string, object>();
                _dirty = false;
                return;
            }

            try
            {
                var parsed = MiniJson.Parse(json);
                if (parsed is Dictionary<string, object> dict)
                {
                    _data = dict;
                    _dirty = false;
                    return;
                }

                throw new FormatException($"顶层不是 JSON 对象（实际为 {parsed?.GetType().Name ?? "null"}）");
            }
            catch (Exception ex)
            {
                var backup = BackupCorrupt();
                _data = new Dictionary<string, object>();
                // 置脏：下次 Save 会用当前（默认）内容覆盖损坏文件。损坏内容已留档，不会真的丢失。
                _dirty = true;
                Report(LogLevel.Error,
                    $"Load 失败（json 损坏）：{ex.Message}；已保留副本 '{backup ?? "（保留失败）"}'，配置回退默认值", ex);
            }
        }

        /// <summary>
        /// 删除指定键的配置项，若存在则标记数据为已修改。
        /// </summary>
        /// <param name="key">要删除的键名。</param>
        public void Delete(string key)
        {
            if (_data.Remove(key))
                _dirty = true;
        }

        /// <summary>
        /// 清空所有配置项，并标记数据为已修改。
        /// </summary>
        public void DeleteAll()
        {
            _data.Clear();
            _dirty = true;
        }

        /// <summary>把临时文件原子替换到目标位置；平台不支持时退化为「删除 + 改名」。</summary>
        private static void ReplaceAtomically(string tmp, string target)
        {
            if (File.Exists(target))
            {
                try
                {
                    // 第三个参数传 null：不保留备份，替换后 tmp 自动删除。
                    // 这一步是原子替换，中途失败不会留下半截目标文件。
                    File.Replace(tmp, target, null);
                    return;
                }
                catch (PlatformNotSupportedException)
                {
                }
                catch (NotSupportedException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
                catch (IOException)
                {
                }
            }

            // 兜底：目标不存在，或平台不支持 File.Replace → 删除 + 改名。
            // 严格说不是原子的，但由于内容已经完整落在 tmp 里，旧文件不会被写坏。
            if (File.Exists(target))
                File.Delete(target);
            File.Move(tmp, target);
        }

        /// <summary>保留损坏配置的现场副本，返回副本路径（失败返回 null）。</summary>
        private string BackupCorrupt()
        {
            var backup = _filePath + CorruptSuffix;
            try
            {
                File.Copy(_filePath, backup, true);
                return backup;
            }
            catch (Exception ex)
            {
                Report(LogLevel.Warn, $"保留损坏配置副本失败（'{backup}'）：{ex.Message}");
                return null;
            }
        }

        private static bool IsDumpable(object value)
        {
            try
            {
                MiniJson.Dump(value);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Save 失败的降频上报：第 1 次必报，之后每 FailureReportEvery 次报一次。</summary>
        private void NoteSaveFailure(Exception ex, string what)
        {
            var n = Interlocked.Increment(ref _saveFailures);
            if (n != 1 && n % FailureReportEvery != 0) return;

            // 注意：失败后 _dirty 保持 true，下次 Save 会重试（瞬时故障可自愈）；
            // 由于走临时文件 + 原子替换，重试不会污染已落盘的旧文件。
            Report(LogLevel.Error,
                $"Save 失败（{what}，第 {n} 次）：{ex.Message}；数据仍在内存中，将重试", ex);
        }

        /// <summary>Get 转换失败的降频上报：第 1 次必报，之后每 FailureReportEvery 次报一次。</summary>
        private void ReportConversionFailure<T>(string key, object value, Exception ex)
        {
            var n = Interlocked.Increment(ref _getFailures);
            if (n != 1 && n % FailureReportEvery != 0) return;

            var why = ex == null ? "类型不匹配" : $"转换异常：{ex.Message}";
            Report(LogLevel.Error,
                $"Get<{typeof(T).Name}>('{key}') {why}（第 {n} 次），回退默认值；" +
                $"实际值类型={value?.GetType().Name ?? "null"}", ex);
        }

        /// <summary>
        /// 统一的上报通道：优先 <c>Game.Logger</c>（落盘 + Console），
        /// 它为 null 时退到 <c>UnityEngine.Debug</c> —— 保证失败路径一定留痕。
        /// 注意这里<b>不再</b>回落到本类自身，避免自递归。
        /// </summary>
        private static void Report(LogLevel level, string msg, Exception ex = null)
        {
            var logger = Game.Logger;
            if (logger != null)
            {
                if (level >= LogLevel.Error)
                    logger.Error("Setting", msg, ex);
                else
                    logger.Warn("Setting", msg);
                return;
            }

            // Game.Logger 为 null（理论上不会发生：未启动时它是 ConsoleLogger）时的最后出口
            try
            {
                UnityEngine.Debug.LogError($"[Setting] {msg}");
            }
            catch
            {
                // 无处可写，放弃
            }
        }

        private static string SafeCombine(string dir, string name)
        {
            try
            {
                return Path.Combine(dir, name);
            }
            catch (Exception)
            {
                // 路径非法（如含 '\0'）：退化为纯文件名
                return name;
            }
        }

        private static string SafeDirectoryName(string path)
        {
            try
            {
                return Path.GetDirectoryName(path);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
