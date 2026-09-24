using System;
using System.Collections.Generic;
using System.IO;

namespace CloverEngine
{
    /// <summary>
    /// 本地化管理器，从 TSV 文件加载语言文本并管理语言切换与事件通知。
    /// 契约类型（ILocalization）定义于 Core 程序集 Contracts.cs；
    /// 模块初始化经 CloverData.InitLocalization 完成。
    /// </summary>
    internal class LocalizationManager : ILocalization
    {
        private string _language;
        private readonly string _dataDir;
        private readonly Dictionary<string, string> _texts = new();
        private readonly List<Action<string>> _changeHandlers = new();

        /// <inheritdoc/>
        public string Language => _language;

        /// <summary>
        /// 初始化本地化管理器，指定语言文件存放目录。
        /// </summary>
        /// <param name="dataDir">语言 TSV 文件所在的目录路径。</param>
        public LocalizationManager(string dataDir)
        {
            _dataDir = dataDir;
        }

        /// <inheritdoc/>
        public string Get(string key)
        {
            return _texts.TryGetValue(key, out var text) ? text : key;
        }

        /// <inheritdoc/>
        public void SetLanguage(string lang)
        {
            if (lang == _language) return;
            Load(lang);
        }

        /// <inheritdoc/>
        public void Load(string lang)
        {
            // 先检查文件是否存在，再设置语言和清空文本
            string path;
            string[] lines;
            try
            {
                path = Path.Combine(_dataDir, $"{lang}.tsv");
                if (!File.Exists(path))
                {
                    // 语言文件缺失不能"静默保持旧语言"：必须 Error 并写清当前状态，
                    // 否则表现为「切了语言、文案没变」，排查无处下手。
                    Game.Logger?.Error("Localization",
                        $"{DataPathHint.ComposeNotFoundHint(path)}；语言未切换（保持 {_language ?? "(未加载)"}）");
                    return;
                }

                lines = File.ReadAllLines(path);
            }
            catch (Exception e)
            {
                // 读文件失败（被占用 / 目录非法）不能以异常形式冒泡给业务：留在日志里，语言保持原样
                Game.Logger?.Error("Localization", $"语言文件读取失败（语言未切换）：{e.Message}", e);
                return;
            }

            _language = lang;
            _texts.Clear();

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var parts = line.Split('\t');
                if (parts.Length >= 2)
                    _texts[parts[0]] = parts[1];
            }

            Game.Logger?.Info("Localization", $"Loaded language: {lang}, {_texts.Count} keys");

            // 快照派发：回调里可能 On/OffLanguageChanged（直接遍历原列表会抛 InvalidOperationException，
            // 中断本批剩余订阅）。
            var handlers = _changeHandlers.ToArray();
            foreach (var h in handlers)
            {
                try { h?.Invoke(lang); }
                catch (Exception ex)
                {
                    Game.Logger?.Error("Localization", $"Handler error: {ex.Message}", ex);
                }
            }
        }

        /// <inheritdoc/>
        public void OnLanguageChanged(Action<string> handler)
        {
            if (handler == null) return;
            // 去重：允许重复注册 + Off 只删首个，会让「注册两次、注销一次」残留一份回调（重复触发 + 订阅泄漏）
            if (!_changeHandlers.Contains(handler))
                _changeHandlers.Add(handler);
        }

        /// <inheritdoc/>
        public void OffLanguageChanged(Action<string> handler)
        {
            _changeHandlers.Remove(handler);
        }
    }
}
