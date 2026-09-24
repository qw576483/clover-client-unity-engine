using System;
using System.Collections.Generic;

namespace CloverEngine
{
    /// <summary>
    /// 值类型标签（与服务端 object.Type 对齐）。
    /// 客户端不做类型校验，仅用于 schema 描述和序列化/反序列化辅助。
    /// </summary>
    public enum FieldType : byte
    {
        Nil = 0,
        Int = 1,
        Float = 2,
        String = 3,
        Bytes = 4,
        Bool = 5,
        Object = 6,
    }

    /// <summary>
    /// 单个字段定义：字段名 + 序号索引 + 值类型。
    /// 与服务端 object.PropSchema 对应，双端字段名和索引必须一致。
    /// </summary>
    public class FieldDef
    {
        /// <summary>字段名（与服务端 Schema.FieldName 一致）</summary>
        public string Name;

        /// <summary>字段序号索引（与服务端 PropSchema.Index 一致）</summary>
        public int Index;

        /// <summary>值类型</summary>
        public FieldType Type;

        public FieldDef() { }

        public FieldDef(string name, int index, FieldType type)
        {
            Name = name;
            Index = index;
            Type = type;
        }

        public override string ToString() => $"{Name}@{Index}:{Type}";
    }

    /// <summary>
    /// 客户端轻量 Schema：一组有序字段定义。
    /// 提供 name→index 双向查询。
    ///
    /// 用途：
    ///   - 业务层声明「player 数据有 name(int@0)、gold(int@1)、level(int@2)」
    ///   - ObjectRecord / ObjectBag 据此做类型转换辅助
    ///   - 与服务端 Schema 做一致性校验（可选）
    ///
    /// 线程安全：否（仅主线程使用）。
    /// </summary>
    public class ObjectSchema
    {
        private readonly List<FieldDef> _fields = new();
        private readonly Dictionary<string, int> _nameToIndex = new();

        /// <summary>字段总数</summary>
        public int FieldCount => _fields.Count;

        /// <summary>按序号获取字段定义</summary>
        public FieldDef this[int index] => _fields[index];

        /// <summary>按字段名获取字段定义（未找到返回 null）</summary>
        public FieldDef this[string name] =>
            _nameToIndex.TryGetValue(name, out var idx) ? _fields[idx] : null;

        /// <summary>
        /// 添加字段。索引必须与字段的追加位置连续一致。
        /// <para>
        /// 不变量：字段的 <see cref="FieldDef.Index"/> 必须 == 它在 <see cref="_fields"/> 中的位置 ——
        /// <see cref="ObjectBag"/> / <see cref="ObjectRecord"/> 都按 Index 直接索引定长数组。
        /// index 小于当前字段数 = 位置已被占用；index 大于当前字段数 = 中间留空洞
        /// （留下大 Index 会让之后按索引取值全部数组越界）—— 两种都修正为追加。
        /// </para>
        /// </summary>
        public ObjectSchema Add(string name, FieldType type, int index = -1)
        {
            if (_nameToIndex.ContainsKey(name))
            {
                // 重名字段：直接覆盖 _nameToIndex 会让 FieldCount 虚增、旧字段"不可达"，
                // 按名取值与按索引取值结果不一致。同名必然是 schema 定义写错，拒绝并告警。
                Game.Logger?.Warn("ObjectSchema",
                    $"Add field '{name}' rejected: duplicate name (field #{_nameToIndex[name]} already uses it)");
                return this;
            }

            if (index < 0) index = _fields.Count;
            if (index != _fields.Count)
            {
                Game.Logger?.Warn("ObjectSchema",
                    $"Add field '{name}': index {index} invalid at FieldCount {FieldCount} " +
                    "(indices must be contiguous; appended instead)");
                index = _fields.Count;
            }
            var field = new FieldDef(name, index, type);
            _fields.Add(field);
            _nameToIndex[name] = index;
            return this;
        }

        /// <summary>快捷方法：添加 int 字段</summary>
        public ObjectSchema Int(string name, int index = -1) => Add(name, FieldType.Int, index);

        /// <summary>快捷方法：添加 float 字段</summary>
        public ObjectSchema Float(string name, int index = -1) => Add(name, FieldType.Float, index);

        /// <summary>快捷方法：添加 string 字段</summary>
        public ObjectSchema String(string name, int index = -1) => Add(name, FieldType.String, index);

        /// <summary>快捷方法：添加 bool 字段</summary>
        public ObjectSchema Bool(string name, int index = -1) => Add(name, FieldType.Bool, index);

        /// <summary>快捷方法：添加 bytes 字段</summary>
        public ObjectSchema Bytes(string name, int index = -1) => Add(name, FieldType.Bytes, index);

        /// <summary>快捷方法：添加 object 字段</summary>
        public ObjectSchema Object(string name, int index = -1) => Add(name, FieldType.Object, index);

        /// <summary>判断是否包含指定字段名</summary>
        public bool Has(string name) => _nameToIndex.ContainsKey(name);

        /// <summary>获取字段序号（未找到返回 -1）</summary>
        public int IndexOf(string name) => _nameToIndex.TryGetValue(name, out var idx) ? idx : -1;

        /// <summary>获取所有字段定义（只读）</summary>
        public IReadOnlyList<FieldDef> Fields => _fields;

        public override string ToString()
        {
            var parts = new string[_fields.Count];
            for (int i = 0; i < _fields.Count; i++)
                parts[i] = _fields[i].ToString();
            return $"Schema[{string.Join(", ", parts)}]";
        }
    }

    /// <summary>
    /// 客户端轻量 Record：按 (行, 列) 存储 object 值。
    /// 与服务端 data.Record 对应，但无 mutex（Unity 主线程单线程）。
    ///
    /// 用途：
    ///   - 接收服务端 Record 增量推送后的本地存储
    ///   - 行级增删改 + 按列名/索引读写
    ///
    /// <para>
    /// 【调用面】引擎内部（Runtime/Editor/Tests/Samples~/Tools~）当前无消费点，属**公开契约**
    /// （业务侧数据容器，<see cref="ObjectSchema"/> 的消费类型之一）—— 业务可直接使用，
    /// 不要按"无人使用"删除。
    /// </para>
    ///
    /// 线程安全：否（仅主线程使用）。
    /// </summary>
    public class ObjectRecord
    {
        private readonly ObjectSchema _schema;
        private readonly List<object[]> _rows = new();

        /// <summary>关联 Schema</summary>
        public ObjectSchema Schema => _schema;

        /// <summary>当前行数</summary>
        public int RowCount => _rows.Count;

        /// <summary>当前列数</summary>
        public int ColCount => _schema.FieldCount;

        public ObjectRecord(ObjectSchema schema)
        {
            _schema = schema ?? throw new ArgumentNullException(nameof(schema));
        }

        /// <summary>
        /// 按行号 + 列序号读取值。
        /// </summary>
        public object GetCell(int row, int col)
        {
            if (row < 0 || row >= _rows.Count) return null;
            var r = _rows[row];
            if (col < 0 || col >= r.Length) return null;
            return r[col];
        }

        /// <summary>
        /// 按行号 + 列名读取值。
        /// </summary>
        public object GetCell(int row, string colName)
        {
            var idx = _schema.IndexOf(colName);
            return idx >= 0 ? GetCell(row, idx) : null;
        }

        /// <summary>
        /// 按行号 + 列序号写入值（同时创建行，如果行不存在）。
        /// <para>
        /// 与 <see cref="GetCell"/> 的防护对称：负行号、越界列一律**告警后忽略**（不抛越界异常）。
        /// </para>
        /// </summary>
        public void SetCell(int row, int col, object value)
        {
            if (row < 0)
            {
                Game.Logger?.Warn("ObjectRecord", $"SetCell ignored: row {row} < 0");
                return;
            }
            if (col < 0 || col >= _schema.FieldCount)
            {
                Game.Logger?.Warn("ObjectRecord",
                    $"SetCell ignored: col {col} out of range [0,{_schema.FieldCount})");
                return;
            }

            var r = EnsureRow(row);
            r[col] = value;
        }

        /// <summary>取（必要时创建）第 <paramref name="row"/> 行，并保证行长与当前 Schema 对齐。</summary>
        private object[] EnsureRow(int row)
        {
            while (row >= _rows.Count)
                _rows.Add(new object[_schema.FieldCount]);

            var r = _rows[row];
            if (r.Length < _schema.FieldCount)
            {
                // schema 在行创建之后新增了字段：补齐行长，否则按新字段索引写入会数组越界。
                Array.Resize(ref r, _schema.FieldCount);
                _rows[row] = r;
            }
            return r;
        }

        /// <summary>
        /// 按行号 + 列名写入值。
        /// </summary>
        public void SetCell(int row, string colName, object value)
        {
            var idx = _schema.IndexOf(colName);
            if (idx < 0)
            {
                Game.Logger?.Warn("ObjectRecord", $"SetCell failed: column '{colName}' not in schema");
                return;
            }
            SetCell(row, idx, value);
        }

        /// <summary>
        /// 追加一行。返回新行索引。
        /// </summary>
        public int AddRow(object[] values)
        {
            var row = new object[_schema.FieldCount];
            if (values != null)
            {
                var count = Math.Min(values.Length, row.Length);
                for (int i = 0; i < count; i++)
                    row[i] = values[i];
            }
            _rows.Add(row);
            return _rows.Count - 1;
        }

        /// <summary>
        /// 删除指定行（尾部高效，中间 O(n)）。
        /// </summary>
        public void DeleteRow(int row)
        {
            if (row >= 0 && row < _rows.Count)
                _rows.RemoveAt(row);
        }

        /// <summary>清空所有行。</summary>
        public void ClearRows() => _rows.Clear();

        /// <summary>
        /// 获取指定行的只读视图。
        /// </summary>
        public IReadOnlyList<object> GetRow(int row)
        {
            if (row < 0 || row >= _rows.Count) return Array.Empty<object>();
            return _rows[row];
        }

        /// <summary>
        /// 类型安全读取：int。单元格**未赋值（null）**时返回 fallback
        /// （⛔ 不可直接 <c>Convert.ToInt32(null)</c>：恒得 0，fallback 参数对空单元格完全失效）。
        /// </summary>
        public int GetInt(int row, int col, int fallback = 0)
        {
            var v = GetCell(row, col);
            if (v == null) return fallback;
            try { return Convert.ToInt32(v); }
            catch { return fallback; }
        }

        /// <summary>类型安全读取：long。未赋值时返回 fallback（理由同 <see cref="GetInt"/>）。</summary>
        public long GetLong(int row, int col, long fallback = 0)
        {
            var v = GetCell(row, col);
            if (v == null) return fallback;
            try { return Convert.ToInt64(v); }
            catch { return fallback; }
        }

        /// <summary>类型安全读取：float。未赋值时返回 fallback（理由同 <see cref="GetInt"/>）。</summary>
        public float GetFloat(int row, int col, float fallback = 0f)
        {
            var v = GetCell(row, col);
            if (v == null) return fallback;
            try { return Convert.ToSingle(v); }
            catch { return fallback; }
        }

        /// <summary>
        /// 类型安全读取：string
        /// </summary>
        public string GetString(int row, int col, string fallback = null)
        {
            return GetCell(row, col) as string ?? fallback;
        }

        /// <summary>类型安全读取：bool。未赋值时返回 fallback（理由同 <see cref="GetInt"/>）。</summary>
        public bool GetBool(int row, int col, bool fallback = false)
        {
            var v = GetCell(row, col);
            if (v == null) return fallback;
            try { return Convert.ToBoolean(v); }
            catch { return fallback; }
        }

        public override string ToString() => $"Record(rows={RowCount}, cols={ColCount})";
    }

    /// <summary>
    /// 客户端轻量 Bag：按字段名存储属性值。
    /// 与服务端 object.Bag 对应：一个「属性袋」包含多个命名字段，每个字段有一个值。
    ///
    /// 用途：
    ///   - 实体属性的强类型容器
    ///   - 与服务端 Bag compact JSON 格式双向解析
    ///
    /// <para>
    /// 【调用面】引擎内部（Runtime/Editor/Tests/Samples~/Tools~）当前无消费点，属**公开契约**
    /// （业务侧属性袋，<see cref="ObjectInstance.Bag"/> 即本类型）—— 业务可直接使用，
    /// 不要按"无人使用"删除。
    /// </para>
    ///
    /// 线程安全：否（仅主线程使用）。
    /// </summary>
    public class ObjectBag
    {
        private readonly ObjectSchema _schema;

        /// <summary>字段值。长度跟随 Schema 增长（见 <see cref="EnsureCapacity"/>），故不加 readonly。</summary>
        private object[] _values;

        /// <summary>关联 Schema</summary>
        public ObjectSchema Schema => _schema;

        public ObjectBag(ObjectSchema schema)
        {
            _schema = schema ?? throw new ArgumentNullException(nameof(schema));
            _values = new object[schema.FieldCount];
        }

        /// <summary>
        /// 保证 <see cref="_values"/> 能容纳当前 Schema 的全部字段（只增不减）。
        /// <para>
        /// schema 事后新增字段后，<c>Set/Get(name)</c> 用新索引读写必须能落进 <see cref="_values"/>
        /// （长度在构造时定型则会抛 IndexOutOfRangeException）—— 故动态扩容。
        /// </para>
        /// </summary>
        private void EnsureCapacity()
        {
            var fieldCount = _schema.FieldCount;
            if (_values.Length < fieldCount)
            {
                Array.Resize(ref _values, fieldCount);
            }
        }

        /// <summary>
        /// 按字段名获取值（类型安全转换辅助）。不在 schema 中、或 schema 增长后尚未赋值的字段 → null。
        /// </summary>
        public object Get(string name)
        {
            var idx = _schema.IndexOf(name);
            if (idx < 0) return null;
            // 读路径不扩容：schema 增长后新增区域未赋值就是 null
            return idx < _values.Length ? _values[idx] : null;
        }

        /// <summary>
        /// 按字段序号获取值。
        /// </summary>
        public object Get(int index)
        {
            if (index < 0 || index >= _values.Length) return null;
            return _values[index];
        }

        /// <summary>
        /// 按字段名设置值。
        /// </summary>
        public void Set(string name, object value)
        {
            var idx = _schema.IndexOf(name);
            if (idx < 0)
            {
                Game.Logger?.Warn("ObjectBag", $"Set failed: field '{name}' not in schema");
                return;
            }
            EnsureCapacity(); // schema 可能在构造后新增字段
            _values[idx] = value;
        }

        /// <summary>
        /// 按字段序号设置值。负索引 / 超出 Schema 字段数时**告警后忽略**。
        /// </summary>
        public void Set(int index, object value)
        {
            if (index < 0)
            {
                Game.Logger?.Warn("ObjectBag", $"Set ignored: index {index} < 0");
                return;
            }
            if (index >= _schema.FieldCount)
            {
                Game.Logger?.Warn("ObjectBag",
                    $"Set ignored: index {index} out of range [0,{_schema.FieldCount})");
                return;
            }
            EnsureCapacity();
            _values[index] = value;
        }

        /// <summary>判断字段是否有值（非 null）</summary>
        public bool Has(string name)
        {
            var idx = _schema.IndexOf(name);
            return idx >= 0 && idx < _values.Length && _values[idx] != null;
        }

        // ---- 类型安全读取 ----

        /// <summary>类型安全读取 int。未赋值（null）时返回 fallback
        /// （⛔ 不可用 <c>Convert.ToInt32(null)</c>：恒得 0，fallback 失效）。</summary>
        public int GetInt(string name, int fallback = 0)
        {
            var v = Get(name);
            if (v == null) return fallback;
            try { return Convert.ToInt32(v); }
            catch { return fallback; }
        }

        /// <summary>类型安全读取 long。未赋值时返回 fallback（理由同 <see cref="GetInt"/>）。</summary>
        public long GetLong(string name, long fallback = 0)
        {
            var v = Get(name);
            if (v == null) return fallback;
            try { return Convert.ToInt64(v); }
            catch { return fallback; }
        }

        /// <summary>类型安全读取 float。未赋值时返回 fallback（理由同 <see cref="GetInt"/>）。</summary>
        public float GetFloat(string name, float fallback = 0f)
        {
            var v = Get(name);
            if (v == null) return fallback;
            try { return Convert.ToSingle(v); }
            catch { return fallback; }
        }

        /// <summary>类型安全读取 string</summary>
        public string GetString(string name, string fallback = null)
        {
            return Get(name) as string ?? fallback;
        }

        /// <summary>类型安全读取 bool。未赋值时返回 fallback（理由同 <see cref="GetInt"/>）。</summary>
        public bool GetBool(string name, bool fallback = false)
        {
            var v = Get(name);
            if (v == null) return fallback;
            try { return Convert.ToBoolean(v); }
            catch { return fallback; }
        }

        /// <summary>
        /// 从 MiniJson 解析的 Dictionary 构建 ObjectBag。
        /// JSON key 必须与 Schema 字段名一致。
        /// </summary>
        public static ObjectBag FromDictionary(ObjectSchema schema, Dictionary<string, object> dict)
        {
            var bag = new ObjectBag(schema);
            if (dict == null) return bag;
            foreach (var kv in dict)
                bag.Set(kv.Key, kv.Value);
            return bag;
        }

        /// <summary>
        /// 导出为 Dictionary（便于与 MiniJson 交互）。
        /// </summary>
        public Dictionary<string, object> ToDictionary()
        {
            var dict = new Dictionary<string, object>();
            foreach (var field in _schema.Fields)
            {
                // schema 增长但尚未触发扩容时，新增字段按"未赋值"处理（直接索引会越界）
                var val = field.Index < _values.Length ? _values[field.Index] : null;
                if (val != null)
                    dict[field.Name] = val;
            }
            return dict;
        }

        public override string ToString() => $"Bag(fields={_schema.FieldCount})";
    }

    /// <summary>
    /// 客户端 GObject 投影：统一对象内核的客户端最小投影。
    /// 对应服务端 gobject.GObject，但只保留客户端需要的部分。
    ///
    /// 与 EntityInfo 的区别：
    ///   - EntityInfo：Core 层只读实体快照（ObjectID / TypeID / SceneGroup；**不含 View**，视图改由 IEntityManager.GetView 读取）
    ///   - ObjectInstance：Network/业务层数据对象（含 Schema + Bag + Record，纯数据）
    ///
    /// 使用方式：
    /// <code>
    ///   var schema = new ObjectSchema().Int("hp", 0).Int("mp", 1).String("name", 2);
    ///   var obj = new ObjectInstance(objectID: 1001, schema: schema);
    ///   obj.Bag.Set("hp", 100);
    ///   obj.Bag.Set("name", "Player1");
    ///   int hp = obj.Bag.GetInt("hp");
    /// </code>
    ///
    /// <para>
    /// 【调用面】引擎内部（Runtime/Editor/Tests/Samples~/Tools~）当前无消费点，属**公开契约**
    /// （业务侧数据对象）—— 业务可直接使用，不要按"无人使用"删除。
    /// </para>
    /// </summary>
    public class ObjectInstance
    {
        /// <summary>唯一标识（与服务端 GObject.ID 一致）</summary>
        public long ObjectID { get; }

        /// <summary>类型标识（与服务端 GObject.TypeID 一致）</summary>
        public int TypeID { get; }

        /// <summary>属性袋（强类型字段访问）</summary>
        public ObjectBag Bag { get; }

        /// <summary>记录表（按名称索引，每个名称对应一个 ObjectRecord）</summary>
        public Dictionary<string, ObjectRecord> Records { get; } = new();

        public ObjectInstance(long objectID, ObjectSchema schema, int typeID = 0)
        {
            ObjectID = objectID;
            TypeID = typeID;
            Bag = new ObjectBag(schema);
        }

        /// <summary>
        /// 获取或创建指定名称的 Record。
        /// </summary>
        public ObjectRecord GetOrCreateRecord(string name, ObjectSchema recordSchema)
        {
            if (!Records.TryGetValue(name, out var record))
            {
                record = new ObjectRecord(recordSchema);
                Records[name] = record;
            }
            return record;
        }

        public override string ToString() => $"GObject(id={ObjectID}, type={TypeID})";
    }
}
