using System;
using System.Collections.Generic;
using UnityEngine;

namespace CloverEngine
{
    /// <summary>
    /// 逻辑地图模块：把 CloverMap 二进制解码成**只读空间查询面**（<c>Game.Map</c> 的实现）。
    ///
    /// 数据来源必须与服务端**同一份字节**（导出器一次写两份）：
    /// 本地预测与服务端跑同一套空间事实，否则本地能穿墙、服务端拒绝 ⇒ 位置越差越大 ⇒ 橡皮带。
    /// 格式契约（逐字节）见 [`clover-server-engine/pkg/domain/mmo/mapdata/README.md`](https://github.com/qw576483/clover-server-engine/blob/main/pkg/domain/mmo/mapdata/README.md)。
    ///
    /// ★ 本模块**只回答空间事实**（这一格能不能走）。「输入 → 位移 → 贴墙滑动」那套本地预测解算
    /// 不在引擎（见 `clover-client-unity-engine-index.md` §3.1 网络域「移动预测」）：业务拿 <see cref="WalkableAt"/> 自己写即可，
    /// 因为"用多大半径、几点采样、撞墙是停还是滑"都是玩法手感，不是引擎该定的。
    ///
    /// 客户端**只解位图 + 头部标量**：碰撞体 AABB 与出生点是服务端的事（客户端解析了也没人看），
    /// 这里只校验段长度以保证文件完整 —— 少一处会腐烂的代码，也少一份内存。
    /// 格式层在 <see cref="CloverMapFormat"/> / <see cref="CloverMapData"/>（`MapFormat.cs`）。
    /// </summary>
    internal sealed class MapModule : IMapData
    {
        private const string Tag = "Map";

        /// <summary>当前数据加载中（防重复发起；`Game.Res` 是异步的，连点两次会打两次加载）。</summary>
        private bool _loading;

        /// <summary>加载在途时其它调用方挂上来的完成回调（加载完成一并通知，避免"静默挂起"）。</summary>
        private readonly List<Action<bool>> _pendingCallbacks = new();

        /// <summary>最近一次经资源模块加载的路径（Clear 时按它归还资源引用）。</summary>
        private string _loadedPath;

        private CloverMapData _map;

        public bool Loaded => _map != null && _map.Loaded;
        public ulong SceneId => _map?.SceneId ?? 0;
        public string Name => _map?.Name;
        public int Version => _map?.Version ?? 0;
        public float CellSize => _map?.CellSize ?? 0f;
        public Vector3 Origin => _map?.Origin ?? Vector3.zero;
        public int Width => _map?.Width ?? 0;
        public int Depth => _map?.Depth ?? 0;
        public int CellCount => _map == null ? 0 : _map.Width * _map.Depth;
        public int WalkableCount => _map?.WalkableCount ?? 0;
        public int BlockedCount => _map?.BlockedCount ?? 0;
        public int ColliderCount => _map?.ColliderCount ?? 0;

        public string Status { get; private set; } = "未加载";

        public bool Load(byte[] data, out string error)
        {
            if (!CloverMapFormat.TryDecode(data, out var map, out error))
            {
                // 解析失败必须连同旧数据一起清掉：只改 Status 会让"已加载"与"数据非法"同时成立，
                // 业务仍能从过期地图读到空间事实（状态与数据自相矛盾）。
                _map = null;
                Status = "数据非法";
                Game.Logger?.Error(Tag, $"地图数据解析失败（{data?.Length ?? 0} 字节）：{error}", null);
                return false;
            }

            _map = map;
            Status = $"已加载 {map.Name} {map.Width}x{map.Depth} cell={map.CellSize:F1} 阻挡={map.BlockedCount}";
            Game.Logger?.Info(Tag, $"逻辑地图已加载：{Status}（scene={map.SceneId} colliders={map.ColliderCount} v={map.Version}）");
            if (map.TailPaddingOnes > 0)
                Game.Logger?.Warn(Tag,
                    $"位图末字节补位非 0（{map.TailPaddingOnes} 位）：写端未清补位，已按格数截断统计");
            return true;
        }

        public void LoadFromResource(string path, Action<bool> onDone = null)
        {
            if (Loaded)
            {
                onDone?.Invoke(true);
                return;
            }
            if (_loading)
            {
                // 非预期分支：重复请求（多个模块都调 Load）→ 留痕但不重复发起。
                // 但 onDone 必须并入本次加载的完成通知：直接 return 会让第二个等回调的调用方永久挂起。
                Game.Logger?.Warn(Tag, "地图数据正在加载中，本次请求已并入当前加载的完成通知");
                if (onDone != null) _pendingCallbacks.Add(onDone);
                return;
            }
            if (Game.Res == null)
            {
                Status = "资源模块未挂载";
                Game.Logger?.Error(Tag, "地图加载失败：Game.Res 为空（引擎尚未 Launch 或资源模块未挂载）", null);
                onDone?.Invoke(false);
                return;
            }

            _loading = true;
            // ★ 必须用 TextAsset.bytes 拿原始字节：Resources 的加载与扩展名无关，
            //   LoadAsset<TextAsset>("MapData/map-city") 会命中 map-city.bytes。
            Game.Res.LoadAsset<TextAsset>(path, ta =>
            {
                _loading = false;

                var ok = false;
                if (ta == null)
                {
                    // 非预期分支：地图数据没进 Resources（没跑过烘焙 / 路径变了）→ 必须留痕
                    Status = "数据缺失";
                    Game.Logger?.Error(Tag,
                        $"地图数据加载失败：Resources/{path}.bytes 不存在（用 Clover/地图烘焙 导出）", null);
                }
                else
                {
                    _loadedPath = path;   // 记下资源路径：Clear 时按它归还引用（重复 Load 会二次 +1）
                    Load(ta.bytes, out _);
                    ok = Loaded;
                }

                onDone?.Invoke(ok);
                // 在途期间挂上来的调用方一并通知（含失败：否则同样被挂起）。
                var pending = new List<Action<bool>>(_pendingCallbacks);
                _pendingCallbacks.Clear();
                foreach (var cb in pending)
                {
                    try { cb?.Invoke(ok); }
                    catch (Exception ex)
                    {
                        Game.Logger?.Error(Tag, $"地图加载完成回调异常: {ex.Message}", ex);
                    }
                }
            });
        }

        public void Clear()
        {
            // 归还资源引用：只置空数据会让引用计数永不归零（再次 LoadFromResource 又 +1，地图资源永久驻留）。
            if (_loadedPath != null)
            {
                Game.Res?.Release(_loadedPath);
                _loadedPath = null;
            }
            _map = null;
            Status = "未加载";
        }

        /// <summary>
        /// 世界坐标是否可走。★ 未加载时返回 true（不阻挡）：地图缺失是导出/配置问题，
        /// 不该表现成"玩家被锁死在原地" —— 那会把可诊断的问题变成不可诊断的现象。
        /// </summary>
        public bool WalkableAt(float x, float z) => _map == null || _map.WalkableAt(x, z);
    }
}
