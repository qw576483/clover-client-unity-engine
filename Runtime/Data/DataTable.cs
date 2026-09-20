using System;
using System.Collections.Generic;
using System.IO;

namespace CloverEngine
{
    /// <summary>
    /// 数据文件读取的平台说明（配表 / 多语言共用）。
    /// <para>
    /// Android 的 StreamingAssets 位于 APK 内、WebGL 的 StreamingAssets 是 URL：<c>System.IO</c> 读不到，
    /// 必须走 <c>UnityWebRequest</c>（异步）。而 <see cref="IDataTable.Load{T}"/> 与 <see cref="ILocalization.Load"/>
    /// 都是同步接口，引擎内没有同步的替代读取 —— 因此这里识别到这类目录时给出**明确、可执行**的报错，
    /// 而不是让它表现为「表全空 / 文案全没」。
    /// </para>
    /// </summary>
    internal static class DataPathHint
    {
        /// <summary>路径是否指向「打包目录且当前平台不能用 System.IO 读」。</summary>
        public static bool IsUnreadablePackagePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
#if (UNITY_ANDROID || UNITY_WEBGL) && !UNITY_EDITOR
            var streamingAssets = UnityEngine.Application.streamingAssetsPath;
            return !string.IsNullOrEmpty(streamingAssets) && path.StartsWith(streamingAssets, StringComparison.Ordinal);
#else
            return false;
#endif
        }

        /// <summary>给「文件不存在」类错误补上可执行的引导（打包目录重定向到可写目录）。</summary>
        public static string ComposeNotFoundHint(string path)
        {
            if (!IsUnreadablePackagePath(path)) return $"File not found: {path}";

            return $"File not found: {path} —— 该路径位于打包目录（StreamingAssets），当前平台不能用 System.IO 读取；" +
                   "请把目录指向可写目录（如 Game.Res.ContentDir 的热更落地目录）或改用打进包体的资源加载";
        }
    }

    /// <summary>
    /// 数据表管理器，负责从 TSV 文件加载数据表并提供基于类型的查询能力。
    /// 契约类型（IDataRow / IDataTable）定义于 Core 程序集 Contracts.cs；
    /// 模块初始化经 CloverData.InitDataTable 完成。
    /// </summary>
    internal class DataTableManager : IDataTable
    {
        /// <summary>数据目录；构造时校验失败为 null（后续 Load 直接拒绝并给出明确日志）。</summary>
        private readonly string _dataDir;
        private readonly Dictionary<Type, Dictionary<int, object>> _tables = new();

        /// <summary>
        /// 初始化数据表管理器，指定数据文件存放目录。
        /// </summary>
        /// <param name="dataDir">TSV 数据文件所在的目录路径。</param>
        public DataTableManager(string dataDir)
        {
            if (string.IsNullOrEmpty(dataDir))
            {
                // 空目录名不能当「相对当前目录」用：那会让读到的文件取决于进程工作目录，
                // 与「数据目录」的配置语义不符；这里直接拒绝，后续 Load 会打出明确日志。
                Game.Logger?.Error("DataTable",
                    "DataTableManager 构造收到空的数据目录，加载将被拒绝（请检查 CloverData.InitDataTable 的参数）");
                return;
            }

            _dataDir = dataDir;
        }

        /// <summary>
        /// 从指定文件加载数据表，使用提供的解析函数将每行文本转换为数据行对象。
        /// 文件格式为 TSV，首行为表头（跳过），后续每行为一条记录。
        /// </summary>
        /// <typeparam name="T">数据行类型，必须实现 <see cref="IDataRow"/>。</typeparam>
        /// <param name="fileName">数据文件名（TSV 格式，首行为表头）。</param>
        /// <param name="parser">将制表符分隔的字段数组解析为数据行对象的函数。</param>
        public void Load<T>(string fileName, Func<string[], T> parser) where T : class, IDataRow
        {
            if (_dataDir == null)
            {
                Game.Logger?.Error("DataTable", $"数据目录未配置，无法加载：{fileName}");
                return;
            }

            var path = Path.Combine(_dataDir, fileName);
            if (!File.Exists(path))
            {
                Game.Logger?.Error("DataTable", DataPathHint.ComposeNotFoundHint(path));
                return;
            }

            string[] lines;
            try
            {
                lines = File.ReadAllLines(path);
            }
            catch (Exception e)
            {
                // 读文件失败（被占用 / 损坏 / 目录不可读）不能穿出 Load 中断整个配表初始化
                Game.Logger?.Error("DataTable", $"数据文件读取失败：{path}: {e.Message}", e);
                return;
            }

            if (lines.Length < 2) return;

            var dict = new Dictionary<int, object>();
            for (var i = 1; i < lines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i])) continue;
                var fields = lines[i].Split('\t');
                try
                {
                    var row = parser(fields);
                    if (row != null)
                        dict[row.Id] = row;
                }
                catch (Exception ex)
                {
                    Game.Logger?.Error("DataTable", $"Parse error at line {i}: {ex.Message}", ex);
                }
            }

            _tables[typeof(T)] = dict;
            Game.Logger?.Info("DataTable", $"Loaded {typeof(T).Name}: {dict.Count} rows");
        }

        /// <summary>
        /// 根据唯一标识查询指定类型的数据行。
        /// </summary>
        /// <typeparam name="T">数据行类型，必须实现 <see cref="IDataRow"/>。</typeparam>
        /// <param name="id">要查询的行唯一标识。</param>
        /// <returns>匹配的数据行对象，若未找到则返回 null。</returns>
        public T Get<T>(int id) where T : class, IDataRow
        {
            if (_tables.TryGetValue(typeof(T), out var dict) && dict.TryGetValue(id, out var row))
                return row as T;
            return null;
        }

        /// <summary>
        /// 获取指定类型数据表中的所有数据行。
        /// </summary>
        /// <typeparam name="T">数据行类型，必须实现 <see cref="IDataRow"/>。</typeparam>
        /// <returns>所有数据行的可枚举集合，若类型未加载则返回空集合。</returns>
        public IEnumerable<T> GetAll<T>() where T : class, IDataRow
        {
            if (_tables.TryGetValue(typeof(T), out var dict))
            {
                foreach (var kv in dict)
                    yield return kv.Value as T;
            }
        }

        /// <summary>
        /// 清空所有已加载的数据表缓存。
        /// </summary>
        public void Clear()
        {
            _tables.Clear();
        }
    }
}
