using System.Collections.Generic;

namespace CloverEngine
{
    /// <summary>
    /// 数据 Schema 声明表：业务层登记「数据类型 → 字段结构」，供本地类型化访问与校验。
    /// 对应服务端 RegisterTypeBySchema。
    ///
    /// **不参与数据分发**——增量/全量订阅的唯一入口是 Game.Sync：
    /// <code>
    ///   Game.Sync.OnData((type, value) =&gt; { ... });          // 增量，按 type 自行分派
    ///   Game.Sync.OnFullSync((data, accountData) =&gt; { ... }); // 全量
    /// </code>
    /// 这样引擎里只有一套数据订阅入口，不存在
    /// 「Game.Schema.OnDataSync 与 Game.Sync.OnData 两个入口」的重复。
    /// </summary>
    internal class SchemaRegistryManager : ISchemaRegistry
    {
        // Schema 注册表：type → schema
        private readonly Dictionary<string, ObjectSchema> _schemas = new();

        public void RegisterSchema(string type, ObjectSchema schema)
        {
            if (string.IsNullOrEmpty(type) || schema == null)
            {
                Game.Logger?.Warn("Schema", "RegisterSchema: type or schema is null");
                return;
            }

            // 同名覆盖必须可见：schema 变更（字段增删）会静默生效，先前取到旧结构的调用方
            // 拿到不一致定义，出问题时无从定位（c-bug SchemaRegistryManager.cs:30）。
            // 同一实例重复注册属幂等调用，不告警。
            if (_schemas.TryGetValue(type, out var previous) && !ReferenceEquals(previous, schema))
            {
                Game.Logger?.Warn("Schema",
                    $"Schema overwritten: type={type}, fields {previous?.FieldCount ?? 0} -> {schema.FieldCount}");
            }

            _schemas[type] = schema;
            Game.Logger?.Info("Schema", $"Schema registered: type={type}, fields={schema.FieldCount}");
        }

        public ObjectSchema GetSchema(string type)
        {
            _schemas.TryGetValue(type, out var schema);
            return schema;
        }
    }
}
