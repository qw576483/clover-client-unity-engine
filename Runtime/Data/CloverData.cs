using UnityEngine;

namespace CloverEngine
{
    /// <summary>
    /// 数据模块引导类：负责构造并挂接数据表 / 本地化子系统，
    /// 维持「Data → Core 单向依赖」的 asmdef 依赖规则（Core 不反向构造模块实现）。
    /// </summary>
    public static class CloverData
    {
        /// <summary>
        /// 初始化数据表模块，从指定目录加载数据表文件。
        /// 需在 Game.Launch 之后调用；重复调用无效。
        /// </summary>
        /// <param name="dataDir">TSV 数据文件所在的目录路径。</param>
        public static void InitDataTable(string dataDir)
        {
            if (!Game.IsRunning)
            {
                // 不用 Game.Logger：它此时指向 ConsoleLogger（**永不为 null**），但这些诊断发生在
                // 引擎引导最早的阶段，直接写 Unity Console 更可靠，也不受日志级别过滤。
                Debug.LogError("[DataTable] game not launched, call Game.Launch first");
                return;
            }
            if (Game.Table != null)
            {
                Game.Logger?.Warn("DataTable", "data table module already initialized");
                return;
            }

            Game.AttachDataTable(new DataTableManager(dataDir));
            Game.Logger?.Info("DataTable", $"data table module initialized: {dataDir}");
        }

        /// <summary>
        /// 初始化本地化模块并加载指定语言的翻译资源。
        /// 需在 Game.Launch 之后调用；重复调用无效。
        /// </summary>
        /// <param name="dataDir">语言 TSV 文件所在的目录路径（如 {dir}/zh.tsv）。</param>
        /// <param name="lang">初始语言标识（如 "zh"、"en"），传 null 跳过初始加载。</param>
        public static void InitLocalization(string dataDir, string lang)
        {
            if (!Game.IsRunning)
            {
                // 同上：未启动时 Game.Logger 指向 ConsoleLogger（**永不为 null**），
                // 走 Unity 原生日志是为了不受日志级别过滤；两者都会输出到 Console。
                Debug.LogError("[Localization] game not launched, call Game.Launch first");
                return;
            }
            if (Game.Localization != null)
            {
                Game.Logger?.Warn("Localization", "localization module already initialized");
                return;
            }

            var localization = new LocalizationManager(dataDir);
            Game.AttachLocalization(localization);

            if (!string.IsNullOrEmpty(lang))
                localization.Load(lang);

            Game.Logger?.Info("Localization", $"localization module initialized: {dataDir}, lang={lang ?? "none"}");
        }
    }
}
