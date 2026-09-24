// ─────────────────────────────────────────────────────────────────────────────
// CloverEngine · Runtime/Core/Screenshot.cs
// 截图落盘（编辑器 / 自动化验证 / 玩家反馈用）—— 通用横切能力，下沉到引擎。
//
// 为什么下沉：每个项目都要写一遍"抓一张图存到某个目录"，而每一遍都会重踩同样的三件事，
//   且失败都是静默的（看起来像"截图没生成"，实际是目录不存在抛异常被吞掉 / 像素没读上）：
//   ① 目标目录不存在 ⇒ File 写入抛异常；② ReadPixels 只能在主线程且必须在渲染完成后同帧取；
//   ③ 失败被空 catch 吞掉 ⇒ 调用方以为存了，实际磁盘上没有文件。
//
// ⛔ **引擎不接管"帧末"**（这是本类最关键的取舍）：ReadPixels 必须在渲染完成之后、同帧内调用，
//   Play 模式下正确的挂点是 `yield return new WaitForEndOfFrame()`。但"排帧末"这件事
//   **属于业务 Tick / 协程的生命周期**：
//     · 引擎不接管业务 Tick（`Game.Tick` 由业务驱动器调用），引擎替业务起一个协程去等帧末
//       = 让引擎持有一次业务生命周期，业务 `Game.Shutdown` 时那个协程还在等，是典型的悬挂点；
//     · Editor 场景（编辑器菜单命令 / 自动化脚本）根本**没有**协程可挂，直接调用才是正解。
//   所以本方法保持**同步语义**：谁需要帧末，谁自己在帧末的那一拍调用它。
//
// 语义约束：
//   ① 永不抛异常：任何失败（读像素失败 / 写文件异常 / 屏幕尺寸非法）⇒ 返回 false + Error 留痕；
//   ② 传入路径的**父目录**不存在时自动创建（`Directory.CreateDirectory` 递归建）——
//      不建的话第一次截图必失败，而失败原因（目录不存在）常常被业务当成"截图功能没生效"；
//   ③ `superSize < 1` 按 1 处理并留痕（不是静默夹取）；
//   ④ 主线程使用，非线程安全（ReadPixels 只能在主线程）。
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.IO;
using UnityEngine;

namespace CloverEngine
{
    /// <summary>
    /// 截图 helper：把当前画面**立即**读成像素并写成 PNG 文件。
    /// <para>
    /// <b>调用方负责在帧末调用</b>：<c>ReadPixels</c> 只能主线程、且必须在渲染完成后**同帧内**取
    /// （Play 模式用 <c>yield return new WaitForEndOfFrame()</c>；Editor 里可直接调用）。
    /// 引擎**刻意不替调用方排帧末** —— 原因见本文件顶部注释（引擎不接管业务 Tick 的生命周期）。
    /// </para>
    /// <para>
    /// <b>失败不抛</b>：任何异常都吞成 <c>false</c> + <see cref="Game.Logger"/> 的 Error 留痕。
    /// 静默失败（调用方以为存了、磁盘上没有文件）是截图功能最常见的坑，所以这里必须留痕。
    /// </para>
    /// <para>
    /// <b>用法</b>：
    /// <code>
    /// // Play 模式（帧末那一拍）：
    /// yield return new WaitForEndOfFrame();
    /// Screenshot.CaptureToFile("&lt;项目根&gt;/.ai-tmp/screenshots/shot-01.png");
    ///
    /// // Editor 菜单 / 自动化脚本：直接调用即可
    /// Screenshot.CaptureToFile(path, superSize: 2);
    /// </code>
    /// </para>
    /// </summary>
    public static class Screenshot
    {
        /// <summary>日志 tag（引擎惯例：各能力用类名自报家门，见 Runtime/Core/*.cs）。</summary>
        private const string Tag = "Screenshot";

        /// <summary>
        /// 立即截图并写文件。
        /// </summary>
        /// <param name="path">
        /// 目标 PNG 路径。其**父目录**不存在时自动创建（递归）；空 / <c>null</c> ⇒ 返回 <c>false</c> + Error。
        /// </param>
        /// <param name="superSize">
        /// 放大倍数（&lt; 1 按 1 处理并留痕）。这里的放大是**读屏后最近邻放大**（不是渲染层超采样）：
        /// <c>ReadPixels</c> 只能读实际帧缓冲，拿不到比屏幕更高的分辨率。
        /// </param>
        /// <returns>写文件成功为 <c>true</c>；任何失败为 <c>false</c>（失败原因见日志）。</returns>
        public static bool CaptureToFile(string path, int superSize = 1)
        {
            if (string.IsNullOrEmpty(path))
            {
                // 非预期分支：调用方没给路径（拼路径失败 / 配置为空）⇒ 必须留痕，否则表现为"截图没生效"。
                Game.Logger?.Error(Tag, "CaptureToFile 收到空路径，已跳过（调用方必须给出目标 png 路径）");
                return false;
            }

            if (superSize < 1)
            {
                // 降频留痕（不是静默夹取）：调用方传了 0 / 负数，多半是配置算错了。
                LogThrottle.WarnOnce(Tag, "screenshot.supersize",
                    $"CaptureToFile 收到 superSize={superSize}（< 1），已按 1 处理；" +
                    "放大倍数应为 >= 1 的整数，传 0/负数通常是配置算错");
                superSize = 1;
            }

            Texture2D grabbed = null;
            Texture2D output = null;
            try
            {
                var width = Screen.width;
                var height = Screen.height;
                if (width <= 0 || height <= 0)
                {
                    // 非预期分支：编辑器窗口最小化 / 无渲染设备时尺寸可能为 0，ReadPixels 会读到空白。
                    Game.Logger?.Error(Tag,
                        $"屏幕尺寸非法（{width}x{height}），无法截图；编辑器窗口最小化或渲染设备不可用时会出现此情况");
                    return false;
                }

                // 目标目录自动创建：第一次截图时目录通常还不存在（.ai-tmp/screenshots 之类），
                // 不建目录的话 File.WriteAllBytes 直接抛 DirectoryNotFoundException。
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                // ── 读像素（主线程、同帧内）──────────────────────────────────
                grabbed = new Texture2D(width, height, TextureFormat.RGBA32, false);
                grabbed.ReadPixels(new Rect(0f, 0f, width, height), 0, 0, false);
                grabbed.Apply(false, false);

                // ── superSize：最近邻放大（ReadPixels 拿不到比屏幕更高的分辨率）──
                if (superSize > 1)
                {
                    var bigW = width * superSize;
                    var bigH = height * superSize;
                    var src = grabbed.GetPixels32();
                    var dst = new Color32[src.Length * superSize * superSize];
                    for (var y = 0; y < bigH; y++)
                    {
                        var sy = y / superSize;
                        var srcRow = sy * width;
                        var dstRow = y * bigW;
                        for (var x = 0; x < bigW; x++)
                            dst[dstRow + x] = src[srcRow + x / superSize];
                    }
                    output = new Texture2D(bigW, bigH, TextureFormat.RGBA32, false);
                    output.SetPixels32(dst);
                    output.Apply(false, false);
                }

                var tex = output ?? grabbed;
                var png = tex.EncodeToPNG();
                if (png == null || png.Length == 0)
                {
                    // 非预期分支：编码失败（纹理不可读 / 后端异常）⇒ 不写空文件（写空文件比不写更糟：
                    // 调用方看到"文件存在"就以为成功了）。
                    Game.Logger?.Error(Tag, $"PNG 编码失败（{tex.width}x{tex.height}），未写出文件：{path}");
                    return false;
                }

                File.WriteAllBytes(path, png);
                Game.Logger?.Info(Tag, $"截图已写入 {path}（{tex.width}x{tex.height}, superSize={superSize}, {png.Length} 字节）");
                return true;
            }
            catch (Exception ex)
            {
                // 读像素失败 / 写文件异常 ⇒ 返回 false 并留痕（⛔ 不许抛，也不许静默吞）。
                Game.Logger?.Error(Tag, $"截图失败（path={path}, superSize={superSize}）：{ex.Message}", ex);
                return false;
            }
            finally
            {
                // 临时纹理必须显式销毁：每帧截一张会持续泄漏 GPU 显存（output 与 grabbed 可能是同一张）。
                if (output != null && !ReferenceEquals(output, grabbed))
                    UnityEngine.Object.Destroy(output);
                if (grabbed != null)
                    UnityEngine.Object.Destroy(grabbed);
            }
        }
    }
}
