using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace CloverEngine
{
    internal class WorldSyncManager : IWorldSync
    {
        private sealed class MoveSnapshot
        {
            public float CurrentX;
            public float CurrentY;
            public float CurrentZ;
            public float TargetX;
            public float TargetY;
            public float TargetZ;
        }

        private IRouter _router;

        // 实体号一律用 ulong：服务端对象号为 uint64，用有符号 long 在 ≥ 2^63 时会解析成负数。
        private readonly Dictionary<ulong, MoveSnapshot> _moveSnapshots = new();
        private readonly List<Action<Dictionary<string, Dictionary<string, object>>, Dictionary<string, object>>> _fullSyncHandlers = new();
        private readonly List<Action<string, object>> _dataHandlers = new();
        private readonly List<Action<ulong, Dictionary<string, object>>> _enterHandlers = new();
        private readonly List<Action<ulong>> _leaveHandlers = new();
        private readonly List<Action<ulong, float, float, float>> _moveHandlers = new();
        private readonly List<Action<ulong, string, object>> _propertyHandlers = new();
        private MsgHandler _attachedFullSyncHandler;
        private MsgHandler _attachedDataSyncHandler;

        public void Attach(IRouter router)
        {
            Detach();
            _router = router;
            _attachedFullSyncHandler = HandleFullSyncPush;
            _attachedDataSyncHandler = HandleDataSyncPush;
            _router?.OnMsg(EMsg.PushPlayerFullSync, _attachedFullSyncHandler);
            _router?.OnMsg(EMsg.PushDataSync, _attachedDataSyncHandler);
        }

        public void Detach()
        {
            if (_router == null) return;
            // 只移除自己注册的handler，不影响其他handler
            if (_attachedFullSyncHandler != null)
                _router.OffMsg(EMsg.PushPlayerFullSync, _attachedFullSyncHandler);
            if (_attachedDataSyncHandler != null)
                _router.OffMsg(EMsg.PushDataSync, _attachedDataSyncHandler);
            _router = null;
            _attachedFullSyncHandler = null;
            _attachedDataSyncHandler = null;
        }

        public void Tick(float dt)
        {
            if (dt <= 0f || _moveSnapshots.Count == 0) return;
            var blend = Math.Min(1f, dt * 10f);
            foreach (var snapshot in _moveSnapshots.Values)
            {
                snapshot.CurrentX += (snapshot.TargetX - snapshot.CurrentX) * blend;
                snapshot.CurrentY += (snapshot.TargetY - snapshot.CurrentY) * blend;
                snapshot.CurrentZ += (snapshot.TargetZ - snapshot.CurrentZ) * blend;
            }
        }

        public bool TryGetPosition(ulong entityId, out float x, out float y, out float z)
        {
            if (_moveSnapshots.TryGetValue(entityId, out var snapshot))
            {
                x = snapshot.CurrentX;
                y = snapshot.CurrentY;
                z = snapshot.CurrentZ;
                return true;
            }
            x = y = z = 0f;
            return false;
        }

        public void OnFullSync(Action<Dictionary<string, Dictionary<string, object>>, Dictionary<string, object>> handler)
        {
            _fullSyncHandlers.Add(handler);
        }

        public void OnData(Action<string, object> handler)
        {
            _dataHandlers.Add(handler);
        }

        public void OnEntityEnter(Action<ulong, Dictionary<string, object>> handler)
        {
            _enterHandlers.Add(handler);
        }

        public void OnEntityLeave(Action<ulong> handler)
        {
            _leaveHandlers.Add(handler);
        }

        public void OnEntityMove(Action<ulong, float, float, float> handler)
        {
            _moveHandlers.Add(handler);
        }

        public void OnEntityProperty(Action<ulong, string, object> handler)
        {
            _propertyHandlers.Add(handler);
        }

        public void OffFullSync(Action<Dictionary<string, Dictionary<string, object>>, Dictionary<string, object>> handler)
        {
            _fullSyncHandlers.Remove(handler);
        }

        public void OffData(Action<string, object> handler)
        {
            _dataHandlers.Remove(handler);
        }

        public void OffEntityEnter(Action<ulong, Dictionary<string, object>> handler)
        {
            _enterHandlers.Remove(handler);
        }

        public void OffEntityLeave(Action<ulong> handler)
        {
            _leaveHandlers.Remove(handler);
        }

        public void OffEntityMove(Action<ulong, float, float, float> handler)
        {
            _moveHandlers.Remove(handler);
        }

        public void OffEntityProperty(Action<ulong, string, object> handler)
        {
            _propertyHandlers.Remove(handler);
        }

        public void Clear()
        {
            _fullSyncHandlers.Clear();
            _dataHandlers.Clear();
            _enterHandlers.Clear();
            _leaveHandlers.Clear();
            _moveHandlers.Clear();
            _propertyHandlers.Clear();
            _moveSnapshots.Clear();
        }

        private void HandleFullSyncPush(NetCtx ctx)
        {
            Dictionary<string, object> root;
            try
            {
                root = MiniJson.Parse(ctx.Body, ctx.BodyOffset, ctx.BodyLength) as Dictionary<string, object>;
            }
            catch (Exception ex)
            {
                Game.Logger?.Warn("WorldSync", $"player full sync payload parse failed: {ex.Message}");
                return;
            }

            if (root == null)
            {
                Game.Logger?.Warn("WorldSync", "player full sync payload is not an object");
                return;
            }

            var data = new Dictionary<string, Dictionary<string, object>>();
            if (Get(root, "data") is Dictionary<string, object> dataMap)
            {
                foreach (var kv in dataMap)
                {
                    var bucket = new Dictionary<string, object>();
                    if (kv.Value is Dictionary<string, object> types)
                    {
                        foreach (var item in types)
                            bucket[item.Key] = item.Value;
                    }
                    else
                    {
                        // 契约是 kind → (type → value)：kind 对应的值不是 JSON 对象时该类数据会被
                        // 整体丢弃。必须留痕——静默丢弃会让「全量同步少了某一类」无从定位。
                        Game.Logger?.Warn("WorldSync",
                            $"full sync kind '{kv.Key}' dropped: value is {(kv.Value == null ? "null" : kv.Value.GetType().Name)}, expected object");
                    }
                    data[kv.Key] = bucket;
                }
            }

            var accountData = Get(root, "account_data") as Dictionary<string, object>
                ?? new Dictionary<string, object>();
            // 快照遍历：回调内退订（OffFullSync 等）会修改列表，直接 foreach 会抛
            // InvalidOperationException 并中断本批后续回调（实现按"回调内可退订"处理）。
            foreach (var handler in _fullSyncHandlers.ToArray())
            {
                try { handler?.Invoke(data, accountData); }
                catch (Exception ex) { Game.Logger?.Error("WorldSync", $"Full sync handler error: {ex.Message}", ex); }
            }
        }

        private void HandleDataSyncPush(NetCtx ctx)
        {
            Dictionary<string, object> root;
            try
            {
                root = MiniJson.Parse(ctx.Body, ctx.BodyOffset, ctx.BodyLength) as Dictionary<string, object>;
            }
            catch (Exception ex)
            {
                Game.Logger?.Warn("WorldSync", $"data sync payload parse failed: {ex.Message}");
                return;
            }

            if (root == null)
            {
                Game.Logger?.Warn("WorldSync", "data sync payload is not an object");
                return;
            }

            // 服务端只以「未注册类型合并」形态下发（internal/transport/event/sync.go 的 pushBatchGroup）：
            // body 恒为 map[type]json.RawMessage，不存在 {type, body} 包装。
            // 已注册类型走各自的业务消息号、不经 EPushDataSync(4003)，所以这里只按批量展开。
            foreach (var kv in root)
                DispatchData(kv.Key, kv.Value);
        }

        private void DispatchData(string type, object value)
        {
            foreach (var handler in _dataHandlers.ToArray())
            {
                try { handler?.Invoke(type, value); }
                catch (Exception ex) { Game.Logger?.Error("WorldSync", $"Data sync handler error [type={type}]: {ex.Message}", ex); }
            }
            DispatchEntityEvent(type, value);
        }

        private void DispatchEntityEvent(string type, object value)
        {
            if (value is List<object> list)
            {
                foreach (var item in list)
                    DispatchEntityEvent(type, item);
                return;
            }

            var body = value as Dictionary<string, object>;
            if (body == null) return;

            var eventName = NormalizeEvent(GetString(body, "event") ?? GetString(body, "type") ?? type);
            if (eventName == "" || eventName == "data" || eventName == "entity")
                eventName = NormalizeEvent(GetString(body, "event") ?? GetString(body, "action"));

            if (!TryGetEntityId(body, out var entityId)) return;
            if (eventName == "enter" || eventName == "spawn" || eventName == "create")
            {
                var attrs = Get(body, "attrs") as Dictionary<string, object>
                    ?? Get(body, "properties") as Dictionary<string, object>
                    ?? new Dictionary<string, object>();
                foreach (var handler in _enterHandlers.ToArray())
                {
                    try { handler?.Invoke(entityId, attrs); }
                    catch (Exception ex) { Game.Logger?.Error("WorldSync", $"Entity enter handler error: {ex.Message}", ex); }
                }
                if (TryGetPosition(body, out var x, out var y, out var z))
                    SetMoveTarget(entityId, x, y, z, false);
                return;
            }

            if (eventName == "leave" || eventName == "despawn" || eventName == "remove" || eventName == "delete")
            {
                _moveSnapshots.Remove(entityId);
                foreach (var handler in _leaveHandlers.ToArray())
                {
                    try { handler?.Invoke(entityId); }
                    catch (Exception ex) { Game.Logger?.Error("WorldSync", $"Entity leave handler error: {ex.Message}", ex); }
                }
                return;
            }

            if (eventName == "move" || eventName == "position" || eventName == "updateposition")
            {
                if (TryGetPosition(body, out var x, out var y, out var z))
                    SetMoveTarget(entityId, x, y, z, true);
                return;
            }

            if (eventName == "property" || eventName == "properties" || eventName == "attr" || eventName == "attribute" || eventName == "update")
            {
                DispatchProperties(entityId, body);
            }
        }

        private void DispatchProperties(ulong entityId, Dictionary<string, object> body)
        {
            var properties = Get(body, "properties") as Dictionary<string, object>
                ?? Get(body, "attrs") as Dictionary<string, object>;
            if (properties != null)
            {
                foreach (var property in properties)
                    DispatchProperty(entityId, property.Key, property.Value);
                return;
            }

            var name = GetString(body, "property") ?? GetString(body, "key") ?? GetString(body, "name");
            if (name != null && Contains(body, "value"))
                DispatchProperty(entityId, name, Get(body, "value"));
        }

        private void DispatchProperty(ulong entityId, string name, object value)
        {
            foreach (var handler in _propertyHandlers.ToArray())
            {
                try { handler?.Invoke(entityId, name, value); }
                catch (Exception ex) { Game.Logger?.Error("WorldSync", $"Entity property handler error: {ex.Message}", ex); }
            }
        }

        private void SetMoveTarget(ulong entityId, float x, float y, float z, bool dispatch)
        {
            if (!_moveSnapshots.TryGetValue(entityId, out var snapshot))
            {
                snapshot = new MoveSnapshot { CurrentX = x, CurrentY = y, CurrentZ = z };
                _moveSnapshots[entityId] = snapshot;
            }
            snapshot.TargetX = x;
            snapshot.TargetY = y;
            snapshot.TargetZ = z;
            if (!dispatch) return;
            foreach (var handler in _moveHandlers.ToArray())
            {
                try { handler?.Invoke(entityId, x, y, z); }
                catch (Exception ex) { Game.Logger?.Error("WorldSync", $"Entity move handler error: {ex.Message}", ex); }
            }
        }

        private static bool TryGetEntityId(Dictionary<string, object> body, out ulong entityId)
        {
            foreach (var key in new[] { "entity_id", "object_id", "id" })
            {
                if (TryGetUlong(Get(body, key), out entityId)) return true;
            }
            entityId = 0;
            return false;
        }

        private static bool TryGetPosition(Dictionary<string, object> body, out float x, out float y, out float z)
        {
            var position = Get(body, "position") as Dictionary<string, object>
                ?? Get(body, "pos") as Dictionary<string, object>;
            x = ToFloat(position == null ? Get(body, "x") : Get(position, "x"));
            y = ToFloat(position == null ? Get(body, "y") : Get(position, "y"));
            z = ToFloat(position == null ? Get(body, "z") : Get(position, "z"));
            return Contains(body, "x") || Contains(body, "y") || Contains(body, "z") || position != null;
        }

        /// <summary>
        /// 把 JSON 值解析成实体号（ulong，与服务端 uint64 对象号对齐）。
        /// <para>
        /// 服务端下发的实体号可能是 JSON 数字（MiniJson 解析成 long/double）或字符串。
        /// 用 <c>ulong.TryParse</c> 而不是 <c>Convert.ToInt64</c>：后者对 ≥ 2^63 的号会抛
        /// OverflowException（被 catch 吞成"解析失败"），负数号也会被静默接受 ——
        /// 两者都会让实体事件静默丢失或错配。
        /// </para>
        /// </summary>
        private static bool TryGetUlong(object value, out ulong result)
        {
            if (value == null)
            {
                result = 0;
                return false;
            }

            if (value is string text)
                return ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);

            // MiniJson 的数字：整数走 long，超出 long 范围或带小数走 double。
            if (value is long l)
            {
                if (l < 0)
                {
                    Game.Logger?.Warn("WorldSync", $"entity id {l} is negative, ignored (expected uint64)");
                    result = 0;
                    return false;
                }
                result = (ulong)l;
                return true;
            }
            if (value is double d)
            {
                if (d < 0 || d > ulong.MaxValue || d != Math.Floor(d))
                {
                    Game.Logger?.Warn("WorldSync", $"entity id '{value}' is not a valid uint64, ignored");
                    result = 0;
                    return false;
                }
                result = (ulong)d;
                return true;
            }

            // 兜底：交给 ulong.TryParse（覆盖 int / uint 等装箱值）。
            return ulong.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
        }

        private static float ToFloat(object value)
        {
            try
            {
                return Convert.ToSingle(value, CultureInfo.InvariantCulture);
            }
            catch (Exception e)
            {
                // 静默兜 0 会把坐标字段类型不匹配 / 非法值变成"实体瞬移到原点"且无任何告警：
                // 必须留痕（null 不在此列——缺字段按 0 处理是既定语义，Convert 对 null 返回 0）。
                Game.Logger?.Warn("WorldSync",
                    $"position value '{value}' is not a number ({e.GetType().Name}), fallback 0");
                return 0f;
            }
        }

        /// <summary>
        /// 取键值：先精确匹配，再退化为**大小写不敏感**匹配。
        /// 服务端各语言的 JSON 键大小写不统一（Go 结构体字段无 tag 时输出 "X"/"Y"/"Z"，
        /// 手动拼装时又常是小写 "x"/"y"/"z"），精确匹配会让整条位置**静默丢失**
        /// （TryGetPosition 返回 false，不抛错、不进日志），因此这里放宽匹配规则。
        /// </summary>
        private static object Get(Dictionary<string, object> dict, string key)
        {
            if (dict == null) return null;
            if (dict.TryGetValue(key, out var value)) return value;
            foreach (var pair in dict)
            {
                if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase)) return pair.Value;
            }
            return null;
        }

        /// <summary>是否存在该键（精确匹配优先，再大小写不敏感回退），语义与 <see cref="Get"/> 保持一致。</summary>
        private static bool Contains(Dictionary<string, object> dict, string key)
        {
            if (dict == null) return false;
            if (dict.ContainsKey(key)) return true;
            foreach (var pair in dict)
            {
                if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static string GetString(Dictionary<string, object> dict, string key)
        {
            return Get(dict, key) as string;
        }

        private static string NormalizeEvent(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            var text = value.ToLowerInvariant();
            var chars = new StringBuilder(text.Length);
            foreach (var c in text)
                if (char.IsLetterOrDigit(c)) chars.Append(c);
            var normalized = chars.ToString();
            if (normalized.StartsWith("entity")) normalized = normalized.Substring(6);
            return normalized;
        }
    }
}
