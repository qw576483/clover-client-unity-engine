using System.Runtime.CompilerServices;

// 表现域内部类型（UIWidgets 里的通用件实现等）对测试与编辑器工具开放，
// 使「纯逻辑」部分（如红点聚合）能在 EditMode 单测里直接验证，不必拉起 Canvas。
// 与 Network/Core 的同名文件保持一致。
[assembly: InternalsVisibleTo("CloverEngine.Tests.Editor")]
[assembly: InternalsVisibleTo("CloverEngine.Tests.PlayMode")]
[assembly: InternalsVisibleTo("CloverEngine.Editor")]
