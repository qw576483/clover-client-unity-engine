// ─────────────────────────────────────────────────────────────────────────────
// CloverEngine · Runtime/Core/Rng.cs
// 注入式可复现随机（**seed 可复现**）—— 通用横切能力。
//
// ★ 通用性依据：随机器是**任何项目都要的底座**（地图生成 / 掉落 / 洗牌 / 刷怪），
//   属 `结构规则.md` §2.2「新增模块判定五问」第 1 问（跨模块通用底座 ⇒ Core）。
//
// ⛔ 不要用 `UnityEngine.Random`：它是**全局静态**状态，任何模块随手调用都会打乱别人的序列
//   ⇒ 地图不可复现、掉落对不上。用法：调用方持有实例，并把 seed **显式传参**进需要随机的函数
//   （例：`_map.Generate(areaId, seed)` 内部 `new Rng(seed)`）。
// ⛔ 除「进游戏时决定本局 seed」这一个入口（<see cref="Rng.FromTime"/>）外，
//   禁止 new 一个无 seed 的 Rng 做业务随机（不可复现）。
//
// 语义约束（改一条 = 语义漂移）：
//   ① 内部固定用 `System.Random`（同源实现）⇒ **现有 seed 的地图/掉落序列一字不变**；
//   ② 非法参数**不抛异常**：返回安全值（0 / min / default / -1 / (0,0)）并打限频告警；
//   ③ 派生是纯函数：`DeriveSeed` / `Derive` 同 salt 恒得同一子流（「每层楼一个子流」的依据）；
//   ④ 非线程安全：主线程使用。
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using UnityEngine;

namespace CloverEngine
{
    /// <summary>基于 <see cref="System.Random"/> 的可复现随机器（非线程安全，主线程使用）。</summary>
    public sealed class Rng
    {
        private readonly System.Random _random;

        /// <summary>本实例的 seed（日志/存档要打它，便于复现问题现场）。</summary>
        public int Seed { get; }

        /// <summary>用指定 seed 创建。任意 int 均可（含负数与 0）。</summary>
        public Rng(int seed)
        {
            Seed = seed;
            _random = new System.Random(seed);
        }

        /// <summary>用当前时钟创建一个**不可复现**的实例（仅限「进游戏时决定本局 seed」这一处入口）。</summary>
        public static Rng FromTime()
        {
            var seed = unchecked((int)(DateTime.UtcNow.Ticks & 0x7FFFFFFF));
            var rng = new Rng(seed);
            Game.Logger?.Info("Rng", $"FromTime seed={seed}");
            return rng;
        }

        /// <summary>派生一个子序列（同一父 seed 恒定 ⇒ 子 seed 恒定，可用于「每层楼一个子流」）。</summary>
        /// <param name="salt">区分用途的常量（同一 salt 每次结果一致）。</param>
        public int DeriveSeed(int salt) => unchecked(Seed * 31 + salt);

        /// <summary>派生一个子随机器。</summary>
        public Rng Derive(int salt) => new Rng(DeriveSeed(salt));

        /// <summary>[0, int.MaxValue) 的随机整数。</summary>
        public int Next() => _random.Next();

        /// <summary>[0, maxExclusive) 的随机整数；<paramref name="maxExclusive"/> &lt;= 0 时返回 0 并告警（不抛异常）。</summary>
        public int Next(int maxExclusive)
        {
            if (maxExclusive <= 0)
            {
                LogThrottle.WarnThrottled("Rng", "next.badmax", $"Next(maxExclusive={maxExclusive}) 非法，返回 0");
                return 0;
            }
            return _random.Next(maxExclusive);
        }

        /// <summary>[minInclusive, maxExclusive) 的随机整数；区间非法时返回 min 并告警。</summary>
        public int Next(int minInclusive, int maxExclusive)
        {
            if (maxExclusive <= minInclusive)
            {
                LogThrottle.WarnThrottled("Rng", "next.badrange",
                    $"Next(min={minInclusive}, max={maxExclusive}) 区间非法，返回 min");
                return minInclusive;
            }
            return _random.Next(minInclusive, maxExclusive);
        }

        /// <summary>[0f, 1f) 的随机浮点。</summary>
        public float NextFloat() => (float)_random.NextDouble();

        /// <summary>[min, max) 的随机浮点；区间非法时返回 min 并告警。</summary>
        public float Range(float min, float max)
        {
            if (max <= min)
            {
                LogThrottle.WarnThrottled("Rng", "range.bad", $"Range(min={min}, max={max}) 区间非法，返回 min");
                return min;
            }
            return min + (float)_random.NextDouble() * (max - min);
        }

        /// <summary>[minInclusive, maxExclusive) 的随机整数（语义化别名）。</summary>
        public int Range(int minInclusive, int maxExclusive) => Next(minInclusive, maxExclusive);

        /// <summary>以 <paramref name="probability"/> 的概率返回 true（&lt;=0 恒 false，&gt;=1 恒 true，且两者都不消耗随机流）。</summary>
        public bool Chance(float probability)
        {
            if (probability <= 0f) return false;
            if (probability >= 1f) return true;
            return _random.NextDouble() < probability;
        }

        /// <summary>从列表随机取一个元素；列表为 null/空时返回 <c>default</c> 并告警（不抛异常）。</summary>
        public T Pick<T>(IList<T> items)
        {
            if (items == null || items.Count == 0)
            {
                LogThrottle.WarnThrottled("Rng", "pick.empty", "Pick 传入了 null/空列表，返回 default");
                return default;
            }
            return items[_random.Next(items.Count)];
        }

        /// <summary>按权重随机取下标（权重全为 0 时返回 -1 并告警）。</summary>
        public int PickWeighted(IList<int> weights)
        {
            if (weights == null || weights.Count == 0)
            {
                LogThrottle.WarnThrottled("Rng", "pickw.empty", "PickWeighted 传入了 null/空权重，返回 -1");
                return -1;
            }

            var total = 0L;
            for (var i = 0; i < weights.Count; i++)
            {
                if (weights[i] > 0) total += weights[i];
            }
            if (total <= 0)
            {
                LogThrottle.WarnThrottled("Rng", "pickw.zero", "PickWeighted 权重全为 0，返回 -1");
                return -1;
            }

            var roll = (long)(_random.NextDouble() * total);
            var acc = 0L;
            for (var i = 0; i < weights.Count; i++)
            {
                if (weights[i] <= 0) continue;
                acc += weights[i];
                if (roll < acc) return i;
            }
            return weights.Count - 1;
        }

        /// <summary>原地洗牌（Fisher-Yates）。</summary>
        public void Shuffle<T>(IList<T> list)
        {
            if (list == null || list.Count <= 1) return;
            for (var i = list.Count - 1; i > 0; i--)
            {
                var j = _random.Next(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }

        /// <summary>[0, width) × [0, height) 的随机格（宽高非正时返回 (0,0) 并告警）。</summary>
        public Vector2Int NextGrid(int width, int height)
        {
            if (width <= 0 || height <= 0)
            {
                LogThrottle.WarnThrottled("Rng", "grid.bad", $"NextGrid(width={width}, height={height}) 非法，返回 (0,0)");
                return Vector2Int.zero;
            }
            return new Vector2Int(_random.Next(width), _random.Next(height));
        }

        /// <summary>[0, width) × [0, height) 的随机格（用 <paramref name="from"/> 做偏移，便于分区生成）。</summary>
        public Vector2Int NextGrid(int width, int height, Vector2Int from)
        {
            var g = NextGrid(width, height);
            return new Vector2Int(g.x + from.x, g.y + from.y);
        }

        /// <summary>[0, size) 的随机下标（越界返回 0）。</summary>
        public int Index(int size) => Next(size);
    }
}
