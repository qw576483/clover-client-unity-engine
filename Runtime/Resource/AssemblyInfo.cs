using System.Runtime.CompilerServices;

// 资源域内部类型（ResourceManager / AssetBundleBackend 等）对测试与编辑器工具开放：
// 调试面板要展示「后端 / 版本 / 缓存水位 / 在途加载 / 已开包数」这些运行期状态，
// 它们都是内部实现细节，不适合做成公开 API。与 Core / Network / Presentation 同名文件一致。
[assembly: InternalsVisibleTo("CloverEngine.Tests.Editor")]
[assembly: InternalsVisibleTo("CloverEngine.Tests.PlayMode")]
[assembly: InternalsVisibleTo("CloverEngine.Editor")]
