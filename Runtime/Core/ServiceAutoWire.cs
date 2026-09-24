using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;

namespace CloverEngine
{
    /// <summary>
    /// 反射装配（服务定位）：按**接口契约**在指定程序集里找实现类型并实例化。
    ///
    /// <para><b>用途</b>：业务组合根（如 <c>AppContext</c>）不必逐个 <c>new</c> → 也不怕
    /// "多个实现者并行开发、漏接一个模块"。调用方只声明「我要 <c>IMapModule</c>」，
    /// 本类负责找到实现并给出**可诊断**的失败原因。</para>
    ///
    /// <para><b>装配口径</b>（与业务组合根一致）：</para>
    /// <list type="bullet">
    /// <item><b>契约优先</b>：只匹配「<c>t</c> 非抽象、非接口、且 <c>typeof(T).IsAssignableFrom(t)</c>」的类型。</item>
    /// <item><b>确定性候选序</b>：候选按 <see cref="Type.FullName"/> 的 Ordinal 序排序 ⇒ 同一程序集
    /// 每次得到同一条结果（<c>Assembly.GetTypes()</c> 的返回顺序不保证稳定，直接取首个会让
    /// "装上了哪个实现"随运行而变）。</item>
    /// <item><b>多实现 = 可诊断，不静默</b>：候选 &gt; 1 时打一条 Warn（列出全部候选全名）并按确定性序取首个；
    /// ⛔ 不抛异常 —— 装配失败不该让游戏起不来，但**必须留下可查的日志**。</item>
    /// <item><b>逐个降级尝试</b>：某候选实例化失败（无公开无参构造 / 构造抛异常）时打 Warn 并试下一个；
    /// 全部失败才返回 <c>false</c>（<paramref name="error"/> 带最后一个异常的类型与信息）。</item>
    /// </list>
    ///
    /// <para><b>缓存</b>：两级进程内缓存，避免反复走反射（<c>GetTypes()</c> 是装配期最贵的一步）：</para>
    /// <list type="number">
    /// <item>程序集 → 其全部类型数组（<c>GetTypes</c> 每程序集只做一次，且吸收
    /// <see cref="ReflectionTypeLoadException"/>：能加载的类型照用，加载不了的跳过并留痕）。</item>
    /// <item>（程序集, 契约）→ 已排序候选数组。</item>
    /// </list>
    /// 两级都是 <see cref="ConcurrentDictionary{TKey,TValue}"/>，线程安全；缓存只存在进程内、
    /// ⛔ 不缓存**实例**（每次 <see cref="TryResolve{T}(Assembly, out T, out string)"/> 都新建 ——
    /// 是否单例由业务侧决定）。程序集热重载等场景用 <see cref="ClearCache"/> 复位。
    /// </summary>
    public static class ServiceAutoWire
    {
        /// <summary>程序集 → 其全部类型（<c>GetTypes</c> 结果缓存）。</summary>
        private static readonly ConcurrentDictionary<Assembly, Type[]> s_types =
            new ConcurrentDictionary<Assembly, Type[]>();

        /// <summary>（程序集, 契约）→ 已按 FullName 排序的候选实现类型。</summary>
        private static readonly ConcurrentDictionary<(Assembly, Type), Type[]> s_impls =
            new ConcurrentDictionary<(Assembly, Type), Type[]>();

        /// <summary>
        /// 按接口契约在 <paramref name="assembly"/> 里找唯一实现并实例化（忽略诊断文本）。
        /// </summary>
        public static bool TryResolve<T>(Assembly assembly, out T service) where T : class
        {
            return TryResolve(assembly, out service, out _);
        }

        /// <summary>
        /// 按接口契约在 <paramref name="assembly"/> 里找实现并实例化。
        /// 成功 ⇒ 返回 <c>true</c> 且 <paramref name="service"/> 非 null（<paramref name="error"/> 为 null）；
        /// 失败 ⇒ 返回 <c>false</c>，<paramref name="service"/> 为 null，<paramref name="error"/> 给出原因。
        /// </summary>
        /// <param name="assembly">被扫描的程序集（业务侧通常传自己的程序集）。</param>
        /// <param name="service">解析到的实例。</param>
        /// <param name="error">失败原因（成功时为 null）。</param>
        public static bool TryResolve<T>(Assembly assembly, out T service, out string error) where T : class
        {
            service = null;
            error = null;

            if (assembly == null)
            {
                error = "程序集为 null";
                Game.Logger?.Warn("AutoWire", $"无法解析 {typeof(T).Name}：{error}");
                return false;
            }

            var impls = FindImplementations(typeof(T), assembly);
            if (impls.Count == 0)
            {
                // 与业务组合根原有措辞一致：调用方拼出的降级日志不变。
                error = "程序集里找不到实现该接口的类型";
                return false;
            }

            if (impls.Count > 1)
            {
                // 多实现：可诊断（列全、取确定性的第一个），但⛔ 不抛 —— 装配失败不该让游戏起不来。
                Game.Logger?.Warn("AutoWire",
                    $"{typeof(T).Name} 在程序集 {assembly.GetName().Name} 里有 {impls.Count} 个实现" +
                    $"（{string.Join(", ", Names(impls))}）⇒ 按 FullName 序取首个 {impls[0].FullName}");
            }

            Exception last = null;
            for (var i = 0; i < impls.Count; i++)
            {
                var t = impls[i];
                try
                {
                    service = (T)Activator.CreateInstance(t);
                    return true;
                }
                catch (Exception ex)
                {
                    // 无公开无参构造 / 构造函数抛异常都到这里：留痕并降级试下一个候选。
                    last = ex;
                    Game.Logger?.Warn("AutoWire",
                        $"{typeof(T).Name} 的候选实现 {t.FullName} 实例化失败：" +
                        $"{ex.GetType().Name}: {ex.Message}（继续尝试下一个候选）");
                }
            }

            error = last == null
                ? "候选实现全部实例化失败"
                : $"候选实现全部实例化失败，最后错误：{last.GetType().Name}: {last.Message}";
            return false;
        }

        /// <summary>
        /// 列出 <paramref name="assembly"/> 里**可实例化**的 <paramref name="contract"/> 实现类型
        /// （非抽象、非接口、非开放泛型），按 <see cref="Type.FullName"/> 的 Ordinal 序返回。
        /// 结果进缓存；空列表表示没有实现。
        /// </summary>
        public static IReadOnlyList<Type> FindImplementations(Type contract, Assembly assembly)
        {
            if (contract == null) throw new ArgumentNullException(nameof(contract));
            if (assembly == null) throw new ArgumentNullException(nameof(assembly));

            return s_impls.GetOrAdd((assembly, contract), key =>
            {
                var all = GetLoadableTypes(key.Item1);
                var found = new List<Type>();
                for (var i = 0; i < all.Length; i++)
                {
                    var t = all[i];
                    if (t == null || t.IsAbstract || t.IsInterface || t.IsGenericTypeDefinition) continue;
                    if (!key.Item2.IsAssignableFrom(t)) continue;
                    found.Add(t);
                }
                found.Sort((a, b) => string.CompareOrdinal(a.FullName, b.FullName));
                return found.ToArray();
            });
        }

        /// <summary>
        /// 清空两级缓存（程序集热重载 / 编辑器中重新编译后调用，避免拿到已卸载程序集的类型）。
        /// </summary>
        public static void ClearCache()
        {
            s_types.Clear();
            s_impls.Clear();
        }

        /// <summary>
        /// 取程序集里"能加载的类型"（缓存）：<see cref="Assembly.GetTypes"/> 在部分引用缺失时会抛
        /// <see cref="ReflectionTypeLoadException"/> —— 那时 <c>Types</c> 里能加载的仍可用（不是全废），
        /// 于是照用并把加载失败的个数与首个原因写成一条 Warn（⛔ 不吞、⛔ 不整体放弃）。
        /// </summary>
        private static Type[] GetLoadableTypes(Assembly assembly)
        {
            return s_types.GetOrAdd(assembly, a =>
            {
                try
                {
                    return a.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    var loaded = ex.Types;
                    var count = 0;
                    for (var i = 0; i < loaded.Length; i++)
                        if (loaded[i] != null) count++;

                    var first = ex.LoaderExceptions != null && ex.LoaderExceptions.Length > 0
                        ? ex.LoaderExceptions[0]
                        : null;
                    Game.Logger?.Warn("AutoWire",
                        $"程序集 {a.GetName().Name} 有 {loaded.Length - count} 个类型加载失败" +
                        $"（可用 {count} 个）⇒ 仅在可用类型里找实现" +
                        (first != null ? $"；首个原因：{first.GetType().Name}: {first.Message}" : ""));

                    var filtered = new List<Type>(count);
                    for (var i = 0; i < loaded.Length; i++)
                        if (loaded[i] != null) filtered.Add(loaded[i]);
                    return filtered.ToArray();
                }
                catch (Exception ex)
                {
                    // GetTypes 的其它异常（如动态程序集 / 权限）：留痕并当作"没有可用类型"。
                    Game.Logger?.Warn("AutoWire",
                        $"程序集 {a.GetName().Name} 的类型枚举失败：{ex.GetType().Name}: {ex.Message}（按无可用类型处理）");
                    return Array.Empty<Type>();
                }
            });
        }

        private static IEnumerable<string> Names(IReadOnlyList<Type> types)
        {
            for (var i = 0; i < types.Count; i++)
                yield return types[i].FullName ?? types[i].Name;
        }
    }
}
