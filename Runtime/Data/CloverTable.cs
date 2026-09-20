// ─────────────────────────────────────────────────────────────────────────────
// CloverEngine · Runtime/Data/CloverTable.cs
// 打表产物（tsv）加载器：读 clover 打表工具产出的 tsv + 按主键强类型取行 —— 通用横切能力，下沉到引擎。
//
// 为什么要有它（`clover-tools/ai-skill/patterns/table.md`「代码侧怎么读」）：
//   打表工具的产物是「**tsv 数据** + **强类型行类**」，而引擎既有的数据域入口
//   `CloverData.InitDataTable(dir)` / `IDataTable.Load<T>` 要求行类实现 `IDataRow`
//   （`Runtime/Core/Contracts.cs`，`int Id { get; }`）——打表生成的行类是**普通字段容器**
//   （`clover-tools/table/core/internal/gen/cs.go:259-267`），**不实现** `IDataRow`
//   ⇒ 两者不是同一条链路：每个业务项目只能自己再写一遍加载器
//   （Diablo2 项目原先那份 = `client/Assets/Scripts/Table/TableLoader.cs`）。
//   ⇒ 本类把「目录解析 + 逐 tsv 读取 + 按主键取行」下沉到引擎：业务侧只剩「一行加载 + 按表名取行」。
//
// 与打表产物**必须逐条对齐**的约定（改动任一条都会让值与生成代码分叉）：
//   · tsv：第 1 行是列名表头、第 2 行起是数据，`File.ReadAllLines` 读、`'\t'` 分隔，
//     只跳过**空行**（`string.IsNullOrEmpty`），⛔ 不识别 `#` 注释；
//     出处：`clover-tools/table/core/internal/gen/cs.go:277-285`（生成代码的 `Load`）。
//   · 主键 = **第 1 列**：int 主键取整数解析结果、string 主键取原样字符串；
//     出处：`cs.go:311`（`index[row.<第一列>] = row`）+ `cs.go:137-142`（`csPkType`）。
//   · 列名 → 行类字段名：按**非字母数字**拆分后每段首字母大写再拼接
//     （`hp_min` → `HpMin`、`is_act1_start` → `IsAct1Start`、`dmg2_min` → `Dmg2Min`）；
//     出处：`clover-tools/table/core/internal/gen/ident.go:15 ToGoIdent`（`splitIdent` + 首字母大写）。
//   · 单元格 → 字段值：**空值 / 非法值取类型默认值、不报错**（与生成代码同语义；
//     这里报错反而会与产物行为分叉）；出处：`cs.go:159-246`（`TableParsers.*`）。
//   · 重复主键：**后者覆盖前者**（生成代码就是字典赋值）；出处：`cs.go:311`。
//   · 行序 = 文件行序（`Rows` 与生成壳的 `All()` 同序）。
//
// 语义约束（改一条 = 语义漂移）：
//   ① 失败**返回可定位错误串**（哪个候选目录 / 哪个文件 / 为什么），⛔ 不抛异常、⛔ 不静默；
//   ② 失败**不破坏**上一次成功加载的数据（与生成壳「只在成功时整体替换」一致）；
//   ③ `Get` 取不到（表没加载 / 主键不存在）⇒ `null` + **限频告警**，不抛异常；
//   ④ 成功路径**不打日志**（业务侧自己打；引擎打会让每个宿主/每次启动多一行噪声）；
//   ⑤ 本类**不依赖 UnityEngine**（只用 `System.IO` / `System.Reflection` / `System.Collections.Generic`）
//      ⇒ 离线宿主（非 Unity 进程）可以直接把本文件链进工程跑；
//   ⑥ 非线程安全：主线程使用。
//
// 已知边界（防当万能药用）：
//   · `Get<T>` 用**反射**把单元格填进 `T` 的 public 字段 —— 打表行类就在业务程序集里，
//     引擎编译期不认识它们，这是唯一可行路径。反射查找结果**按 (表, 行类型, 列) 缓存**，
//     取行路径上不再做反射查找。这与「打表产物自身不用反射」（`clover-client-unity-engine-index.md` §3.3）
//     **不冲突**：生成代码仍是零反射，反射只在引擎这一层发生。`T` 必须像打表行类那样
//     「**public 无参构造 + public 字段**」。
//   · 支持的类型 = 打表工具会产出的那一组：`int` / `long` / `float` / `string` / `int[]` / `string[]` /
//     `Dictionary<int,int>` / `Dictionary<int,string>` / `Vector3` 形状的结构（public float `X`/`Y`/`Z`）。
//     其它类型**不填值** + 限频告警一次（⛔ 不假装填成功）。
//   · `Get<T>(table, int)` 只对 **int 主键**的表有意义；`Get<T>(table, string)` 只对 **string 主键**的表有意义
//     （产物里主键类型由表决定，见 `csPkType`）。用错重载 ⇒ 大概率取不到 ⇒ 走 ③。
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace CloverEngine
{
    /// <summary>
    /// 打表产物（tsv）加载器：与 clover 打表工具的输出格式配套。
    /// <para>
    /// 用法（业务侧唯一需要写的两件事）：
    /// <code>
    /// string err = CloverTable.LoadAll(Game.Res.StreamingAssetsDir, Game.Res.DataDir);
    /// if (err != null) Game.Logger?.Error("Table", err);
    /// var row = CloverTable.Get&lt;BaseMonsterRow&gt;("Monster", 1);
    /// </code>
    /// </para>
    /// <para>表名 = tsv 文件名去掉扩展名（打表产物里就是「逻辑名首字母大写」形式，如 `Item.tsv` → `"Item"`）。</para>
    /// </summary>
    public static class CloverTable
    {
        /// <summary>限频告警的 tag（引擎惯例：各能力用类名自报家门，见 `Runtime/Core/*.cs`）。</summary>
        private const string Tag = "CloverTable";

        /// <summary>
        /// 运行时目录的子目录名：tsv 放 **`&lt;streamingAssetsDir&gt;/Table/`**。
        /// <para>为什么不是 Resources：打表产物的读取走真实文件系统（生成代码是 `File.ReadAllLines`），
        /// 而 `Resources.Load` 拿到的是内存对象、没有真实路径，且 `.tsv` 不是 Unity 的 TextAsset 扩展名
        /// ⇒ 放在 `Assets/Resources/` 下读不到。编辑器与 Windows 独立版下
        /// `Application.streamingAssetsPath` 都是**真实目录**。</para>
        /// </summary>
        public const string StreamingSubDir = "Table";

        /// <summary>
        /// 开发期回退目录（相对 `dataDir`）：打表工具的客户端产物落点 = `&lt;client_dir&gt;/Tsv/`，
        /// 本项目 `client_dir` = `Assets/Scripts/Table` ⇒ **`Scripts/Table/Tsv`**。
        /// 用于「编辑器里还没把 tsv 同步进 StreamingAssets」的场景（离线宿主也走这一条）。
        /// </summary>
        public const string EditorFallbackSubDir = "Scripts/Table/Tsv";

        /// <summary>最近一次**成功**加载所用的目录；从未成功加载过为 null（失败不改它）。</summary>
        public static string Dir { get; private set; }

        /// <summary>
        /// 调用方**声明**的必需表（表名，不含 `.tsv`）——加载器本身不知道业务要哪几张表，
        /// 声明之后「少了一张」会被报成**哪个文件**（否则只会表现为「那张表空着」）。
        /// 为空 = 不校验（只要求目录里至少有一个 tsv）。
        /// </summary>
        public static IList<string> RequiredTables { get; } = new List<string>();

        /// <summary>一张已加载的表：表头 + 数据行 + 两个主键索引 + 物化缓存。</summary>
        private sealed class TableData
        {
            public string[] Columns;
            public readonly List<string[]> Rows = new List<string[]>();
            public readonly Dictionary<int, int> ByInt = new Dictionary<int, int>();
            public readonly Dictionary<string, int> ByStr = new Dictionary<string, int>(StringComparer.Ordinal);
            public readonly Dictionary<Type, FieldInfo[]> FieldCache = new Dictionary<Type, FieldInfo[]>();
            public object[] RowCache;
        }

        /// <summary>表名 → 数据（表名比较不区分大小写）。</summary>
        private static readonly Dictionary<string, TableData> Loaded =
            new Dictionary<string, TableData>(StringComparer.OrdinalIgnoreCase);

        /// <summary>`Vector3` 形状结构的 X/Y/Z 字段（按类型缓存）。</summary>
        private static readonly Dictionary<Type, FieldInfo[]> TripleCache = new Dictionary<Type, FieldInfo[]>();

        // ── 加载 ─────────────────────────────────────────────────────────────
        /// <summary>
        /// 解析配表目录（**优先级从高到低**）：
        /// <list type="number">
        /// <item><c>&lt;streamingAssetsDir&gt;/Table</c>（运行时目录；**目录存在即采用**——与既有业务加载器一致）</item>
        /// <item><c>streamingAssetsDir</c> 本身（直接给 tsv 目录的用法）</item>
        /// <item><c>&lt;dataDir&gt;/Scripts/Table/Tsv</c>（开发期回退；**目录存在即采用**）</item>
        /// <item><c>dataDir</c> 本身（直接给 tsv 目录的用法）</item>
        /// </list>
        /// </summary>
        /// <returns>命中的目录；都没有返回 null。</returns>
        public static string ResolveDir(string streamingAssetsDir, string dataDir)
        {
            if (!string.IsNullOrEmpty(streamingAssetsDir))
            {
                var runtimeDir = Combine(streamingAssetsDir, StreamingSubDir);
                if (Directory.Exists(runtimeDir)) return runtimeDir;
                if (HasTsv(streamingAssetsDir)) return streamingAssetsDir;
            }

            if (!string.IsNullOrEmpty(dataDir))
            {
                var fallbackDir = Combine(dataDir, EditorFallbackSubDir);
                if (Directory.Exists(fallbackDir)) return fallbackDir;
                if (HasTsv(dataDir)) return dataDir;
            }

            return null;
        }

        /// <summary>
        /// 读一个目录下**全部**打表产物 tsv，并建立按主键的索引。
        /// </summary>
        /// <param name="streamingAssetsDir">运行时根目录（`Application.streamingAssetsPath`）；可为 null。</param>
        /// <param name="dataDir">开发期回退根目录（`Application.dataPath`）；可为 null。</param>
        /// <returns>
        /// 成功返回 **null**；失败返回**可定位的错误串**（候选目录 / 文件名 / 原因）。
        /// ⛔ 不抛异常；失败时**保留**上一次成功加载的数据。
        /// </returns>
        public static string LoadAll(string streamingAssetsDir, string dataDir)
        {
            var dir = ResolveDir(streamingAssetsDir, dataDir);
            if (dir == null)
                return "[CloverTable] 未找到配表目录：" + DescribeCandidates(streamingAssetsDir, dataDir);

            // 调用方声明的必需表：缺哪一张就把**哪个文件**报出来。
            for (var i = 0; i < RequiredTables.Count; i++)
            {
                var name = RequiredTables[i];
                if (string.IsNullOrEmpty(name)) continue;
                var path = Path.Combine(dir, name + ".tsv");
                if (!File.Exists(path)) return "[CloverTable] 配表文件缺失：" + path;
            }

            string[] files;
            try
            {
                files = Directory.GetFiles(dir, "*.tsv");
            }
            catch (Exception ex)
            {
                return "[CloverTable] 配表目录不可读（dir=" + dir + "）：" + ex.GetType().Name + ": " + ex.Message;
            }
            if (files.Length == 0)
                return "[CloverTable] 目录下没有任何打表产物 tsv：" + dir;

            var parsed = new Dictionary<string, TableData>(StringComparer.OrdinalIgnoreCase);
            for (var fi = 0; fi < files.Length; fi++)
            {
                var file = files[fi];
                var tableName = Path.GetFileNameWithoutExtension(file);

                string[] lines;
                try
                {
                    lines = File.ReadAllLines(file);
                }
                catch (Exception ex)
                {
                    // 读文件失败（被占用 / 损坏 / 权限）⇒ 可定位到**文件**，不穿出异常。
                    return "[CloverTable] 配表文件读取失败：" + file + "：" + ex.GetType().Name + ": " + ex.Message;
                }

                if (parsed.ContainsKey(tableName))
                {
                    // 非预期分支（同名表两份）：留痕并**保留先读到的那份**，避免"谁赢取决于枚举顺序"。
                    LogThrottle.WarnThrottled(Tag, "dup:" + tableName,
                        "配表里有重名的表（" + file + "）⇒ 已忽略这一份，只保留先读到的同名 tsv");
                    continue;
                }

                var table = new TableData
                {
                    // 空文件时生成代码是 `lines.Length < 1` 直接返回 ⇒ 这里同样不报错，只是没有列、没有行。
                    Columns = lines.Length >= 1 ? lines[0].Split('\t') : new string[0],
                };

                for (var li = 1; li < lines.Length; li++)
                {
                    if (string.IsNullOrEmpty(lines[li])) continue;   // 与生成代码一致：只跳过空行
                    var rec = lines[li].Split('\t');
                    var key = Cell(rec, 0);

                    // 两个索引都建：产物里主键类型由表决定（int / string），调用方用哪个重载由表决定。
                    // 重复主键沿用生成代码的语义：后者覆盖前者。
                    table.ByInt[ToInt(key)] = table.Rows.Count;
                    table.ByStr[key] = table.Rows.Count;
                    table.Rows.Add(rec);
                }

                table.RowCache = new object[table.Rows.Count];
                parsed[tableName] = table;
            }

            // 全部读成功之后才整体替换（失败不破坏上一次的数据）。
            Loaded.Clear();
            foreach (var kv in parsed) Loaded[kv.Key] = kv.Value;
            Dir = dir;
            return null;
        }

        // ── 取行 ─────────────────────────────────────────────────────────────
        /// <summary>按表名 + int 主键取行；没有（表没加载 / 主键不存在）⇒ null + 限频告警。</summary>
        public static T Get<T>(string tableName, int id) where T : class
        {
            var table = Find(tableName);
            if (table == null) return null;

            int rowIndex;
            if (!table.ByInt.TryGetValue(id, out rowIndex))
            {
                LogThrottle.WarnThrottled(Tag, "miss:" + tableName + ":" + id,
                    "配表 " + tableName + " 里没有 int 主键 " + id + " ⇒ 返回 null（同一主键限频告警）");
                return null;
            }

            return Materialize<T>(table, tableName, rowIndex);
        }

        /// <summary>按表名 + string 主键取行；没有（表没加载 / 主键不存在）⇒ null + 限频告警。</summary>
        public static T Get<T>(string tableName, string key) where T : class
        {
            var table = Find(tableName);
            if (table == null) return null;

            int rowIndex;
            if (!table.ByStr.TryGetValue(key ?? string.Empty, out rowIndex))
            {
                LogThrottle.WarnThrottled(Tag, "miss:" + tableName + ":" + (key ?? "<null>"),
                    "配表 " + tableName + " 里没有 string 主键 \"" + (key ?? "<null>")
                    + "\" ⇒ 返回 null（同一主键限频告警）");
                return null;
            }

            return Materialize<T>(table, tableName, rowIndex);
        }

        // ── 内部：查表 ───────────────────────────────────────────────────────
        private static TableData Find(string tableName)
        {
            if (string.IsNullOrEmpty(tableName))
            {
                LogThrottle.WarnThrottled(Tag, "empty-name", "CloverTable.Get 收到空表名 ⇒ 返回 null");
                return null;
            }

            TableData table;
            if (Loaded.TryGetValue(tableName, out table)) return table;

            if (Dir == null)
            {
                LogThrottle.WarnThrottled(Tag, "not-loaded",
                    "配表尚未加载（CloverTable.LoadAll 未成功）⇒ Get(" + tableName + ") 返回 null");
            }
            else
            {
                LogThrottle.WarnThrottled(Tag, "no-table:" + tableName,
                    "配表 " + tableName + " 未加载（dir=" + Dir + "，已加载 " + Loaded.Count + " 张）⇒ 返回 null");
            }
            return null;
        }

        // ── 内部：物化 ───────────────────────────────────────────────────────
        /// <summary>把第 <paramref name="rowIndex"/> 行填进 <typeparamref name="T"/>（按 (表, 类型) 缓存实例）。</summary>
        private static T Materialize<T>(TableData table, string tableName, int rowIndex) where T : class
        {
            var cached = table.RowCache[rowIndex];
            if (cached != null) return cached as T;

            object row;
            try
            {
                row = Activator.CreateInstance(typeof(T));
            }
            catch (Exception ex)
            {
                // 非预期分支（行类没有 public 无参构造等）：留痕，不穿出异常。
                LogThrottle.ErrorThrottled(Tag, "ctor:" + typeof(T).FullName,
                    "行类 " + typeof(T).FullName + " 无法实例化（需要 public 无参构造）："
                    + ex.GetType().Name + ": " + ex.Message);
                return null;
            }

            var cells = table.Rows[rowIndex];
            var fields = FieldsOf(table, typeof(T));
            for (var i = 0; i < fields.Length; i++)
            {
                var field = fields[i];
                if (field == null) continue;
                Fill(row, field, Cell(cells, i));
            }

            table.RowCache[rowIndex] = row;
            return row as T;
        }

        /// <summary>列下标 → 字段（按 (表, 行类型) 缓存；没有对应字段的列为 null）。</summary>
        private static FieldInfo[] FieldsOf(TableData table, Type rowType)
        {
            FieldInfo[] cached;
            if (table.FieldCache.TryGetValue(rowType, out cached)) return cached;

            var map = new Dictionary<string, FieldInfo>(StringComparer.Ordinal);
            var declared = rowType.GetFields(BindingFlags.Public | BindingFlags.Instance);
            for (var i = 0; i < declared.Length; i++)
            {
                if (!map.ContainsKey(declared[i].Name)) map[declared[i].Name] = declared[i];
            }

            var result = new FieldInfo[table.Columns.Length];
            for (var c = 0; c < table.Columns.Length; c++)
            {
                var fieldName = ToIdent(table.Columns[c]);
                if (fieldName.Length == 0) continue;

                FieldInfo field;
                // 表里有这一列而行类没有这个字段 = 正常（行类可以只声明自己用到的列）⇒ 不告警。
                if (map.TryGetValue(fieldName, out field)) result[c] = field;
            }

            table.FieldCache[rowType] = result;
            return result;
        }

        // ── 内部：单元格 → 字段 ──────────────────────────────────────────────
        /// <summary>
        /// 把一格的原文填进字段。取值语义**逐条照搬** `gen/cs.go:159-246` 的 `TableParsers.*`
        /// （含「空 / 非法 ⇒ 类型默认值」「map 分隔符 `|` 与 `;`（兼容 `:`）」
        /// 「slice 用 `;`」「vector3 用 `;` 或 `,`」）。
        /// <para>⚠️ 数值解析**刻意不加 `InvariantCulture`**：生成代码用的是当前区域设置，
        /// 这里必须与它一致，否则在非英文区域下两边的值会不一样。</para>
        /// </summary>
        private static void Fill(object row, FieldInfo field, string raw)
        {
            var type = field.FieldType;

            if (type == typeof(int)) { field.SetValue(row, ToInt(raw)); return; }
            if (type == typeof(long)) { field.SetValue(row, ToLong(raw)); return; }
            if (type == typeof(float)) { field.SetValue(row, ToFloat(raw)); return; }
            if (type == typeof(string)) { field.SetValue(row, raw ?? string.Empty); return; }
            if (type == typeof(int[])) { field.SetValue(row, ParseSliceInt(raw)); return; }
            if (type == typeof(string[])) { field.SetValue(row, ParseSliceString(raw)); return; }

            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>))
            {
                var args = type.GetGenericArguments();
                if (args[0] == typeof(int) && args[1] == typeof(int))
                {
                    field.SetValue(row, ParseMapIntInt(raw));
                    return;
                }
                if (args[0] == typeof(int) && args[1] == typeof(string))
                {
                    field.SetValue(row, ParseMapIntString(raw));
                    return;
                }
            }

            var triple = TripleOf(type);
            if (triple != null)
            {
                // 值类型字段：先取出装箱值、改 X/Y/Z、再整体写回（直接对结构体字段的成员设值不会落到行上）。
                var box = field.GetValue(row);
                var sep = raw != null && raw.IndexOf(';') >= 0 ? ';' : ',';
                var parts = raw == null ? new string[0] : raw.Split(sep);
                if (parts.Length > 0) triple[0].SetValue(box, ToFloat(parts[0]));
                if (parts.Length > 1) triple[1].SetValue(box, ToFloat(parts[1]));
                if (parts.Length > 2) triple[2].SetValue(box, ToFloat(parts[2]));
                field.SetValue(row, box);
                return;
            }

            // 非预期分支（打表工具不会产出的类型）：留痕一次，⛔ 不假装填成功。
            LogThrottle.WarnThrottled(Tag, "type:" + type.FullName,
                "行类字段 " + field.DeclaringType.Name + "." + field.Name + " 的类型 " + type.Name
                + " 不在打表产物支持的类型里 ⇒ 该字段保持默认值");
        }

        /// <summary>
        /// `Vector3` 形状（public float `X`/`Y`/`Z`）的**结构体**类型；不是 ⇒ null。
        /// <para>为什么要按形状识别：产物里的 `Vector3` 定义在**项目的 `Table` 命名空间**
        /// （`cs.go:152-155`），引擎编译期看不见它，只能按形状填。按类型缓存。</para>
        /// </summary>
        private static FieldInfo[] TripleOf(Type type)
        {
            if (!type.IsValueType || type.IsPrimitive || type.IsEnum) return null;

            FieldInfo[] cached;
            if (TripleCache.TryGetValue(type, out cached)) return cached;

            var x = type.GetField("X", BindingFlags.Public | BindingFlags.Instance);
            var y = type.GetField("Y", BindingFlags.Public | BindingFlags.Instance);
            var z = type.GetField("Z", BindingFlags.Public | BindingFlags.Instance);
            FieldInfo[] result = null;
            if (x != null && y != null && z != null
                && x.FieldType == typeof(float) && y.FieldType == typeof(float) && z.FieldType == typeof(float))
            {
                result = new[] { x, y, z };
            }

            TripleCache[type] = result;
            return result;
        }

        // ── 内部：解析原语（与 gen/cs.go 的 TableParsers 逐条同语义）──────────
        private static string Cell(string[] rec, int i)
        {
            if (rec == null || i < 0 || i >= rec.Length) return string.Empty;
            return rec[i];
        }

        private static int ToInt(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return 0;
            int v;
            return int.TryParse(s.Trim(), out v) ? v : 0;
        }

        private static long ToLong(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return 0L;
            long v;
            return long.TryParse(s.Trim(), out v) ? v : 0L;
        }

        private static float ToFloat(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return 0f;
            float v;
            return float.TryParse(s.Trim(), out v) ? v : 0f;
        }

        private static Dictionary<int, int> ParseMapIntInt(string s)
        {
            var res = new Dictionary<int, int>();
            if (string.IsNullOrWhiteSpace(s)) return res;
            foreach (var pair in s.Split('|'))
            {
                var p = pair.Trim();
                if (p == string.Empty) continue;
                var kv = p.Split(new[] { ';' }, 2);
                if (kv.Length != 2) kv = p.Split(new[] { ':' }, 2);
                if (kv.Length != 2) continue;
                res[ToInt(kv[0])] = ToInt(kv[1]);
            }
            return res;
        }

        private static Dictionary<int, string> ParseMapIntString(string s)
        {
            var res = new Dictionary<int, string>();
            if (string.IsNullOrWhiteSpace(s)) return res;
            foreach (var pair in s.Split('|'))
            {
                var p = pair.Trim();
                if (p == string.Empty) continue;
                var kv = p.Split(new[] { ';' }, 2);
                if (kv.Length != 2) kv = p.Split(new[] { ':' }, 2);
                if (kv.Length != 2) continue;
                res[ToInt(kv[0])] = kv[1].Trim();
            }
            return res;
        }

        private static int[] ParseSliceInt(string s)
        {
            var res = new List<int>();
            if (string.IsNullOrWhiteSpace(s)) return res.ToArray();
            foreach (var e in s.Split(';'))
            {
                var x = e.Trim();
                if (x == string.Empty) continue;
                res.Add(ToInt(x));
            }
            return res.ToArray();
        }

        private static string[] ParseSliceString(string s)
        {
            var res = new List<string>();
            if (string.IsNullOrWhiteSpace(s)) return res.ToArray();
            foreach (var e in s.Split(';')) res.Add(e.Trim());
            return res.ToArray();
        }

        /// <summary>
        /// 列名 → 行类字段名（`hp_min` → `HpMin`）：按**非字母数字**拆段、每段首字母大写再拼接。
        /// 出处：`clover-tools/table/core/internal/gen/ident.go:15 ToGoIdent`。
        /// </summary>
        private static string ToIdent(string column)
        {
            if (string.IsNullOrEmpty(column)) return string.Empty;

            var sb = new System.Text.StringBuilder(column.Length);
            var atSegmentStart = true;
            for (var i = 0; i < column.Length; i++)
            {
                var c = column[i];
                if (char.IsLetterOrDigit(c))
                {
                    sb.Append(atSegmentStart ? char.ToUpperInvariant(c) : c);
                    atSegmentStart = false;
                }
                else
                {
                    atSegmentStart = true;
                }
            }
            return sb.ToString();
        }

        private static string Combine(string root, string sub)
        {
            if (string.IsNullOrEmpty(sub)) return root;
            // 常量里的分隔符按平台归一（`Scripts/Table/Tsv` 在 Windows 上要变成 `Scripts\Table\Tsv`，
            // 否则报错信息里的路径是混合分隔符，人工排查时容易看错）。
            return Path.Combine(root, sub.Replace('/', Path.DirectorySeparatorChar));
        }

        private static bool HasTsv(string dir)
        {
            if (!Directory.Exists(dir)) return false;
            try
            {
                return Directory.GetFiles(dir, "*.tsv").Length > 0;
            }
            catch (Exception ex)
            {
                // 非预期分支（权限 / 路径过长）：留痕，按"不是 tsv 目录"处理。
                LogThrottle.WarnThrottled(Tag, "probe:" + dir,
                    "探测配表目录失败（dir=" + dir + "）：" + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        /// <summary>四个候选路径的可读描述（去重；参数为空时说清是**哪个参数**为空，而不是给一个拼不出来的路径）。</summary>
        private static string DescribeCandidates(string streamingAssetsDir, string dataDir)
        {
            var paths = new List<string>();
            AddCandidate(paths, Candidate(streamingAssetsDir, StreamingSubDir));
            AddCandidate(paths, Candidate(streamingAssetsDir, null));
            AddCandidate(paths, Candidate(dataDir, EditorFallbackSubDir));
            AddCandidate(paths, Candidate(dataDir, null));

            var sb = new System.Text.StringBuilder();
            for (var i = 0; i < paths.Count; i++)
            {
                if (i > 0) sb.Append("；");
                sb.Append("候选 ").Append(i + 1).Append(" = ").Append(paths[i]);
            }
            return sb.ToString();
        }

        private static void AddCandidate(List<string> paths, string path)
        {
            for (var i = 0; i < paths.Count; i++)
            {
                if (string.Equals(paths[i], path, StringComparison.Ordinal)) return;
            }
            paths.Add(path);
        }

        private static string Candidate(string root, string sub)
        {
            if (string.IsNullOrEmpty(root)) return "(参数为空)";
            return sub == null ? root : Combine(root, sub);
        }
    }
}
