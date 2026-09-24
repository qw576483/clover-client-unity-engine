// ─────────────────────────────────────────────────────────────────────────────
// CloverEngine · Runtime/Data/FileSlotStore.cs
// 「键 → 文本」的文件**槽位**存储：原子写 + 损坏留档 + 枚举 —— 通用横切能力。
//
// 为什么要有它：引擎只有 `Setting`（`Runtime/Core/Setting.cs`）—— **单文件 JSON、键值对**
//   （一个 `settings.json` 里一个键存一份文本）。想做「一只角色一个文件 / 一局回放一个文件 /
//   一章关卡草稿一个文件」的项目，只能自己再写一遍「先写 `<f>.tmp` → `File.Replace` 原子替换
//   + 坏文件留档 + 目录枚举」—— 每个新项目重写一次、写法还各不相同
//   （`patterns/engine-fix.md` §7 的 D 桶判定）。
//   ⇒ 本类提供这一层。与 `Setting` **互补**、不改它：`Setting` = 单文件 KV（设置在内存里攒、
//   `Save()` 一次写全），本类 = **一槽一文件**（每次 `Write` 独立落盘、`List()` 可枚举）。
//
// 原子写那一段与 `Setting` 同族写法（同目录，⛔ 不另创一套）：
//   · 写盘：先写 `<key><ext>.tmp` → `File.Replace` 原子替换；目标不存在 / 平台不支持 ⇒ `删除 + 改名` 兜底。
//     出处 `Runtime/Core/Setting.cs:349-380`（`ReplaceAtomically`）。
//   · 损坏留档 `<key><ext>.corrupt`：**副本**语义（原文件保留 ⇒ 现场可见、之后 `Write` 能正常覆盖）。
//     出处 `Runtime/Core/Setting.cs:382-396`（`BackupCorrupt`）。
//   · 失败限频上报：第 1 次必报，之后每 100 次一条（⛔ 不刷屏、⛔ 不静默）。出处 `Setting.cs:411-421`。
//
// 与 `Setting` 的**差异**（用错会踩）：
//   · **一个键一个文件** ⇒ `List()` 能枚举槽位。`ISetting` 没有枚举 key 的接口，所以项目侧只能自己
//     维护一个「索引键」（例如 `playerIndex`）来记住有哪些槽 —— 把槽位清单交给本类就不必维护索引。
//   · `List()` 是**字典序**（`StringComparer.Ordinal`），⛔ **不是插入序** —— 要「按创建先后」显示
//     就自己维护索引键（本类只保证「排序稳定、跨进程一致」）。
//   · 损坏判定**按槽**：扩展名是 `.json`（默认）时要求内容能被引擎自己的 `MiniJson.Parse` 解析
//     （解析抛异常 / 内容为空 ⇒ 判坏：`null` + 留档）；**其它扩展名一律视为合法文本**
//     （引擎没法定「任意文本里什么叫坏」，⛔ 不猜）。
//
// 语义约束（改一条 = 语义漂移）：
//   ① 失败**返回可定位错误串**（哪个路径 / 为什么），⛔ 不抛异常、⛔ 不静默；
//   ② `Read` 遇到坏内容 ⇒ `null` + 留档（`.corrupt`）+ **限频告警一次**（同一槽只说一次），⛔ 不抛；
//   ③ `Write` 之前若目标已是坏文件：**先留档再写**（⛔ 不静默把现场覆盖掉）；
//   ④ 目录不可用（null / 空 / 非法字符 / 无本地文件系统）⇒ `Write` 返回 false + error、
//      `Read` 返回 null、`List()` 返回空（⛔ 不伪装成功、⛔ 不退化成"内存存储"骗调用方）；
//   ⑤ key 非法（null / 空 / 含 `'/'` `'\\'` 或非法文件名字符）⇒ `Write` false + error、`Read` null、
//      `Exists` false、`Delete` false（⛔ 不抛，也⛔ 不允许用 `../` 逃出槽目录）；
//   ⑥ 本类**不依赖 UnityEngine**（只用 `System.IO` / `System.Collections.Generic`；`.json` 校验用
//      引擎自己的 `MiniJson`）⇒ 离线宿主（非 Unity 进程）可直接把本文件链进工程跑；
//   ⑦ WebGL 没有可写的本地文件系统 ⇒ 不建目录、不读、不写、不抛（与 `Setting` 同一处置）；
//   ⑧ 非线程安全：主线程使用（与 `Setting` 一致）。
//
// 用法：
//   var store = new FileSlotStore(Path.Combine(Game.Config.SettingDir, "saves"));
//   if (!store.Write("hero_01", json, out var err)) Game.Logger?.Error("Save", err);
//   var text = store.Read("hero_01");          // 不存在 / 坏了 ⇒ null
//   var all  = store.List();                   // 字典序（跨进程一致）
//   var archived = store.LastCorruptPath;      // 最近一次留档的坏文件路径（没有 ⇒ null）
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace CloverEngine
{
    /// <summary>
    /// 「键 → 文本」的槽位存储：原子写（`*.tmp` → `File.Replace`）+ 损坏留档 + 枚举。
    /// <para>
    /// 一个键 = 一个 `<dir>/<key><extension>` 文件。存档 / 配置 / 回放 / 关卡草稿都可用；
    /// 与 `Setting`（单文件 KV）互补 —— 需要「一个槽一个文件、能枚举」时用它。
    /// </para>
    /// <para>
    /// **任何路径都不抛异常**：失败返回 <c>false</c> + 可定位的 <c>error</c>（<see cref="Write"/>）或
    /// <c>null</c>（<see cref="Read"/>），并把非预期分支写进 <c>Game.Logger</c>（限频，不刷屏）。
    /// </para>
    /// </summary>
    public sealed class FileSlotStore
    {
        /// <summary>日志/告警的 tag（引擎惯例：各能力用类名自报家门，见 `Runtime/Core/*.cs`）。</summary>
        private const string Tag = "FileSlotStore";

        /// <summary>原子写的临时文件后缀（与 `Setting.TempSuffix` 一致）。</summary>
        private const string TempSuffix = ".tmp";

        /// <summary>损坏槽位的留档后缀（与 `Setting.CorruptSuffix` 一致）。</summary>
        private const string CorruptSuffix = ".corrupt";

        /// <summary>失败日志的降频阈值：第 1 次必报，之后每 N 次报一次（与 `Setting.FailureReportEvery` 一致）。</summary>
        private const int FailureReportEvery = 100;

        /// <summary>默认扩展名（`.json` 槽会做 JSON 合法性校验，见类注释）。</summary>
        private const string DefaultExtension = ".json";

#if UNITY_WEBGL
        /// <summary>平台是否具备可用的本地文件系统（WebGL：没有）。用 static readonly 而非 const，
        /// 免得编译器把另一半分支判成不可达代码（与 `Setting.cs:85-92` 同一处置）。</summary>
        private static readonly bool PlatformPersistent = false;
#else
        /// <summary>平台是否具备可用的本地文件系统。</summary>
        private static readonly bool PlatformPersistent = true;
#endif

        private readonly string _extension;

        /// <summary>false ⇒ 所有文件操作都不可用（目录非法 / 无本地文件系统），失败路径照 ④ 走。</summary>
        private readonly bool _usable;

        /// <summary>true ⇒ 该扩展名的槽按 JSON 校验内容（坏 ⇒ 留档；见类注释）。</summary>
        private readonly bool _jsonSlots;

        private int _writeFailures;
        private int _readFailures;

        /// <summary>
        /// 槽位目录（构造函数传入的原样字符串；为空/空白时为 `""` = 不可用）。
        /// <para>目录**不在构造时创建**：第一次成功 `Write` 时才建（与 `Setting.Save` 一致的懒创建）——
        /// 构造不许因为"目录暂时不存在"就废弃整个存储。</para>
        /// </summary>
        public string Dir { get; }

        /// <summary>最近一次被留档的**坏**槽文件路径；从未留档过 ⇒ <c>null</c>（供"损坏留档"判据用）。</summary>
        public string LastCorruptPath { get; private set; }

        /// <summary>
        /// 初始化槽位存储。
        /// <para>本构造**不抛异常**：<paramref name="dir"/> 为空/空白/含非法字符 ⇒ 退化为"不可用"
        /// （写失败、读为 null、枚举为空，且失败路径会给出可定位错误串）。</para>
        /// </summary>
        /// <param name="dir">槽位文件所在目录。</param>
        /// <param name="extension">槽位文件扩展名，默认 <c>".json"</c>；不含前导点时自动补上。</param>
        public FileSlotStore(string dir, string extension = DefaultExtension)
        {
            Dir = string.IsNullOrWhiteSpace(dir) ? "" : dir;
            _extension = NormalizeExtension(extension);
            // 只有 ".json" 槽做内容校验：引擎能判"这段 JSON 坏了"，判不了"任意文本坏了"
            _jsonSlots = string.Equals(_extension, DefaultExtension, StringComparison.OrdinalIgnoreCase);
            _usable = IsUsableDir(Dir);
        }

        // ── 查询 ───────────────────────────────────────────────────────────────

        /// <summary>
        /// 该槽是否存在（**只看文件在不在，不解析内容** ⇒ 坏内容的槽也算存在）。
        /// </summary>
        public bool Exists(string key)
        {
            if (!_usable || !IsValidKey(key)) return false;

            try
            {
                return File.Exists(PathOf(key));
            }
            catch (Exception)
            {
                // PathOf 已经在 _usable / IsValidKey 之后，理论上到不了这里；真到了也不许抛
                return false;
            }
        }

        // ── 写 ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// 原子写：先写 <c>&lt;key&gt;&lt;ext&gt;.tmp</c>，再 <c>File.Replace</c> 落到 <c>&lt;key&gt;&lt;ext&gt;</c>。
        /// <para>失败返回 <c>false</c> + <paramref name="error"/>（可定位：路径 + 异常类型 + 原因），⛔ 不抛异常。
        /// 目标已是坏文件时**先留档**（<c>.corrupt</c>，见 <see cref="LastCorruptPath"/>）再写。</para>
        /// </summary>
        /// <param name="key">槽位键（非空、不含路径分隔符/非法文件名字符）。</param>
        /// <param name="content">要写入的文本（可为空串；<c>null</c> 不合法 ⇒ 返回 false）。</param>
        /// <param name="error">成功时为 <c>null</c>；失败时为可定位错误串。</param>
        /// <returns>是否成功落盘。</returns>
        public bool Write(string key, string content, out string error)
        {
            if (!IsValidKey(key))
            {
                error = $"槽位 key 非法（'{Describe(key)}'）：必须非空，且不含路径分隔符与非法文件名字符";
                ReportWriteFailure(error);
                return false;
            }

            if (content == null)
            {
                error = $"写入槽位 '{key}' 的内容为 null（槽位存的是文本，null 无法表达；要清空请用 Delete）";
                ReportWriteFailure(error);
                return false;
            }

            if (!_usable)
            {
                error = $"槽位目录不可用（'{Dir}'）：目录为空/非法或当前平台没有本地文件系统 ⇒ 本次写入未落盘";
                ReportWriteFailure(error);
                return false;
            }

            var target = PathOf(key);
            var tmp = target + TempSuffix;
            try
            {
                // ③ 目标已是坏文件 ⇒ 先把现场留档再写（否则坏内容被静默覆盖，事后无从查）
                ArchiveIfCorrupt(key, target);

                var parent = SafeDirectoryName(target);
                if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
                    Directory.CreateDirectory(parent);

                // 先写临时文件：写一半失败也只留下 .tmp，目标文件仍是上一次的完整内容
                File.WriteAllText(tmp, content);
                ReplaceAtomically(tmp, target);

                error = null;
                _writeFailures = 0;
                return true;
            }
            catch (Exception ex)
            {
                TryDelete(tmp);
                error = $"写入槽位 '{key}' 失败（{target}）：{ex.GetType().Name}: {ex.Message}";
                ReportWriteFailure(error);
                return false;
            }
        }

        // ── 读 ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// 读回槽位文本。
        /// <para>文件不存在 ⇒ <c>null</c>（正常情形，不告警）；内容坏了 ⇒ <c>null</c> + 留档
        /// （<c>.corrupt</c>，见 <see cref="LastCorruptPath"/>）+ **限频告警一次**（同一槽只说一次）；
        /// 读取出错 ⇒ <c>null</c> + 限频告警。⛔ 不抛异常。</para>
        /// </summary>
        public string Read(string key)
        {
            if (!IsValidKey(key))
            {
                ReportReadFailure($"读取槽位 '{Describe(key)}' 被拒：key 非法（空 / 含路径分隔符 / 含非法文件名字符）");
                return null;
            }

            if (!_usable)
            {
                ReportReadFailure($"读取槽位 '{key}' 失败：槽位目录不可用（'{Dir}'）");
                return null;
            }

            var path = PathOf(key);
            string text;
            try
            {
                if (!File.Exists(path)) return null;
                text = File.ReadAllText(path);
            }
            catch (Exception ex)
            {
                ReportReadFailure($"读取槽位 '{key}' 失败（{path}）：{ex.GetType().Name}: {ex.Message}");
                return null;
            }

            if (IsAcceptableContent(text)) return text;

            // 坏内容：留档 + 只说一次（存档坏了是「说一遍就够」的事，⛔ 不许每次读都刷屏）
            var archived = Archive(path);
            var kept = archived != null ? archived : "（留档失败）";
            LogThrottle.WarnOnce(Tag, "corrupt:" + path,
                $"槽位 '{key}' 内容损坏（{path}）：已留档副本 '{kept}' ⇒ 本次按「没有这个槽」处理（Read 返回 null）");
            return null;
        }

        // ── 删除 / 枚举 ────────────────────────────────────────────────────────

        /// <summary>
        /// 删除槽位。<para>文件本来就不存在 ⇒ <c>false</c>（"没删到"）；删除出错 ⇒ <c>false</c> + 限频告警。
        /// ⛔ 不抛异常。留档（<c>.corrupt</c>）**不随槽位删除**：那是坏内容的现场，属于证据。</para>
        /// </summary>
        public bool Delete(string key)
        {
            if (!IsValidKey(key) || !_usable) return false;

            var path = PathOf(key);
            try
            {
                if (!File.Exists(path)) return false;
                File.Delete(path);
                return true;
            }
            catch (Exception ex)
            {
                ReportReadFailure($"删除槽位 '{key}' 失败（{path}）：{ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 全部槽位键，**排序稳定**（`StringComparer.Ordinal` 字典序 ⇒ 跨进程 / 跨机器一致），
        /// 便于比对与 UI 列表。目录不存在 / 不可用 ⇒ 空清单（⛔ 不抛）。
        /// <para>⚠️ 是字典序，**不是创建先后**（要"创建先后"请自己维护索引键）。</para>
        /// </summary>
        public List<string> List()
        {
            var res = new List<string>();
            if (!_usable) return res;

            try
            {
                if (!Directory.Exists(Dir)) return res;

                var files = Directory.GetFiles(Dir, "*" + _extension);
                for (var i = 0; i < files.Length; i++)
                {
                    var name = Path.GetFileName(files[i]);
                    if (name.Length <= _extension.Length) continue;
                    res.Add(name.Substring(0, name.Length - _extension.Length));
                }

                // 文件系统返回顺序不保证（不同进程/不同卷都可能不同）⇒ 自己定序，保证判据可复现
                res.Sort(StringComparer.Ordinal);
            }
            catch (Exception ex)
            {
                ReportReadFailure($"枚举槽位目录失败（'{Dir}'）：{ex.GetType().Name}: {ex.Message}");
                res.Clear();
            }

            return res;
        }

        // ── 内部：路径与合法性 ─────────────────────────────────────────────────

        /// <summary>槽位文件路径（调用方必须先过 `IsValidKey` / `_usable`）。</summary>
        private string PathOf(string key)
        {
            try
            {
                return Path.Combine(Dir, key + _extension);
            }
            catch (Exception)
            {
                // 路径非法（如含 '\0'）：退化为拼接串，交由后续 File.* 失败并回报（⛔ 不抛）
                return Dir + Path.DirectorySeparatorChar + key + _extension;
            }
        }

        /// <summary>
        /// key 是否合法：非空、非纯空白、不含目录分隔符（⛔ 不许 `../` 逃出槽目录）、不含非法文件名字符。
        /// </summary>
        private static bool IsValidKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return false;
            if (key.IndexOf('/') >= 0 || key.IndexOf('\\') >= 0) return false;
            if (key.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;

            // 双保险：拼出来之后文件名必须还是它自己（挡住 '.' / '..' 之类的退化形态）
            try
            {
                if (Path.GetFileName(key) != key) return false;
            }
            catch (Exception)
            {
                return false;
            }

            return true;
        }

        /// <summary>目录是否可用（空/非法字符/平台无文件系统 ⇒ 不可用）。本方法**不抛异常**。</summary>
        private static bool IsUsableDir(string dir)
        {
            if (!PlatformPersistent) return false;
            if (string.IsNullOrWhiteSpace(dir)) return false;

            try
            {
                if (dir.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return false;
                Path.Combine(dir, "probe");     // 触发 ArgumentException 一类问题（如 '\0'）
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>扩展名规范化：空/空白 ⇒ `".json"`；缺前导点 ⇒ 补上。</summary>
        private static string NormalizeExtension(string extension)
        {
            if (string.IsNullOrWhiteSpace(extension)) return DefaultExtension;
            return extension[0] == '.' ? extension : "." + extension;
        }

        /// <summary>内容是否可接受：`.json` 槽要求能被 `MiniJson.Parse` 解析（不抛）+ 非空白；其它扩展名一律接受。</summary>
        private bool IsAcceptableContent(string text)
        {
            if (!_jsonSlots) return true;
            if (string.IsNullOrWhiteSpace(text)) return false;

            try
            {
                // 只认"能不能解析"（解析结果不重要：合法 JSON 里的 "null" 也是合法内容）
                MiniJson.Parse(text);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // ── 内部：原子写 / 留档（与 `Setting` 同族写法，见文件头出处） ─────────

        /// <summary>
        /// 把临时文件原子替换到目标位置；平台不支持 `File.Replace` 时退化为「删除 + 改名」。
        /// 出处：`Runtime/Core/Setting.cs:349-380`（同异常清单）。
        /// </summary>
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

        /// <summary>目标已是坏文件时先留档（⛔ 不静默覆盖现场）。留档失败只告警，不阻断本次写入。</summary>
        private void ArchiveIfCorrupt(string key, string target)
        {
            if (!File.Exists(target)) return;

            string text;
            try
            {
                text = File.ReadAllText(target);
            }
            catch (Exception)
            {
                // 读不出来（被占用等）⇒ 不是"内容坏"，照常写入（新内容会整体替换它）
                return;
            }

            if (IsAcceptableContent(text)) return;

            Archive(target);
            LogThrottle.WarnOnce(Tag, "corrupt-overwrite:" + target,
                $"槽位 '{key}' 原有内容已损坏（{target}）：已把现场留档为副本，本次写入将整体替换它");
        }

        /// <summary>保留坏文件的现场**副本**，返回副本路径（失败返回 null）。出处：`Setting.cs:382-396`。</summary>
        private string Archive(string path)
        {
            var backup = path + CorruptSuffix;
            try
            {
                File.Copy(path, backup, true);
                LastCorruptPath = backup;
                return backup;
            }
            catch (Exception ex)
            {
                LogThrottle.WarnOnce(Tag, "archive-fail:" + backup,
                    $"留档损坏槽位失败（'{backup}'）：{ex.GetType().Name}: {ex.Message}（原文件仍在，未丢失）");
                return null;
            }
        }

        private static void TryDelete(string tmp)
        {
            try
            {
                if (File.Exists(tmp)) File.Delete(tmp);
            }
            catch (Exception)
            {
                // 清理临时文件失败无副作用：下一次 Write 会覆盖它，List() 也不会把它当成槽位
            }
        }

        // ── 内部：失败上报（降频，与 `Setting` 同一口径） ───────────────────────

        /// <summary>写失败上报：第 1 次必报，之后每 <see cref="FailureReportEvery"/> 次报一次。</summary>
        private void ReportWriteFailure(string message)
        {
            var n = Interlocked.Increment(ref _writeFailures);
            if (n != 1 && n % FailureReportEvery != 0) return;

            Game.Logger?.Error(Tag, message + $"（写失败第 {n} 次）");
        }

        /// <summary>读/删/枚举失败上报：口径同 <see cref="ReportWriteFailure"/>。</summary>
        private void ReportReadFailure(string message)
        {
            var n = Interlocked.Increment(ref _readFailures);
            if (n != 1 && n % FailureReportEvery != 0) return;

            Game.Logger?.Error(Tag, message + $"（读失败第 {n} 次）");
        }

        private static string Describe(string key)
        {
            return key == null ? "null" : (key.Length == 0 ? "(空串)" : key);
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
