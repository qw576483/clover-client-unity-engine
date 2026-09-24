// ─────────────────────────────────────────────────────────────────────────────
// CloverEngine · Editor/capture-editor-screen.cs
// 采「用户真正看到的那个画面」—— **编辑器窗口级（屏幕像素）截屏**，判据资产。
//
// 为什么必须有它：
//   `Screenshot.CaptureToFile` / `ScreenCapture` / `unity command capture_game_view` 采的
//   都是**游戏自己渲染出来的后缓冲**（`source=screen` 也只多含 Overlay 画布）。Unity 编辑器
//   **在 Game view 上叠加绘制**的东西 —— 组件图标（AudioSource 喇叭 / Light 太阳 / Camera）、
//   `DrawGizmos` 线框、选中高亮 —— **不在那个后缓冲里**，所以既有取证路径全部采不到它们。
//   本驱动走 OS 层（`BitBlt` / `CopyFromScreen` 屏幕像素），与用户的眼睛同源。
//
// 与 `Runtime/Core/Screenshot.cs` 的 `Screenshot.CaptureToFile` 的区别（各自适用场景）：
//   · Screenshot.CaptureToFile —— **帧末取像素**（Texture2D.ReadPixels 读屏幕后缓冲），
//     采的是**游戏渲染出来的画面**。适用：Play 模式帧末那一拍、需要"渲染结果本身"的
//     像素 diff / 基线对照（Editor 无协程帧末，直接调用即可）。
//     ⛔ 采不到编辑器在 Game view 上的叠加层。
//   · 本类 —— **窗口级 / 屏幕像素**，采的是**用户真正看到的画面**，含编辑器叠加层。
//     适用：`表现类` 取证、复现"用户说看得见、截图却抓不到"。
//     ⛔ 代价：必须有可见窗口（最小化采不到）、受多显示器 / 遮挡影响、分辨率不超过屏幕。
//   两者互补，⛔ 不要拿一个替代另一个。
//
// 为什么必须在 Unity 进程内执行（而不是在 PowerShell 里做）：
//   编辑器可能是**以管理员身份**启动的（窗口标题前缀 "Administrator:"），AI 宿主的
//   PowerShell 不在同一完整性级别 ⇒ `SetWindowPos` 直接失败（UIPI），桌面合成采到的只会是
//   **压在上面的 AI IDE**。在 Unity 进程内调用则同进程同权限，可行。
//   （外部脚本路线见 `clover-tools/visual-verify/capture-editor-window.ps1`。）
//
// 用法（run_script；入口一律写**全限定名**）：
//   unity command run_script --file <本文件> --project-path <项目根>/client \
//         --entry CloverEngine.Editor.EditorScreenCapture.Shot
//   参数走 spec 文件 `<项目根>/.ai-tmp/test/editor-shot-spec.txt`（key=value 逐行，
//   与 CLI 的引号转义绝缘）。兼容旧名 `bv-shot-spec.txt`（先找新名，找不到再找旧名）。
//
//   entry 一览（返回一行自报读数的字符串，⛔ 不靠"看返回值猜成功"）：
//     Info    : 主窗口矩形 + Game view 矩形 + Gizmos 开关（只读）
//     Raise   : 把编辑器主窗口从最小化还原并置顶（HWND_TOPMOST）
//     Drop    : 取消置顶（幂等）
//     Shot    : 按 spec 截屏落 PNG
//     Gizmos  : spec 的 gizmos=on|off 写 Game view 的 showGizmos（A/B 用）
//     Freeze  : spec 的 freeze=1|0 / timescale=<float> 写 Time.timeScale（A/B 需要同一帧）
//
//   spec 键：
//     path=<绝对路径.png>            必填（必须单行、单路径、无空格、.png 结尾）
//     mode=gameview|window|screen    默认 gameview
//     x/y/w/h=<像素>                 mode=screen 时是屏幕矩形；缺省时 gameview/window 自动算
//     raise=1                        截屏前还原+置顶窗口，截完恢复（默认 0）
//     gizmos=on|off
//     freeze=1|0
//     timescale=<float>
//     flip=v|h|vh                    纠 180° 旋转（实测本机 DX12 + 虚拟显示会上下翻转），默认 v
//     min-mean-rgb=<float>           退化阈值（默认 2.0）：低于它 = 采失败，**不落盘**并返回错误
//
// 编辑器内人用入口：菜单 `Clover/截图/编辑器窗口 PNG`（同一入口的薄封装）。
// ─────────────────────────────────────────────────────────────────────────────
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace CloverEngine.Editor
{
    /// <summary>窗口枚举 / 置顶 / 还原所需的 Win32 入口。</summary>
    internal static class CaptureWin32
    {
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lp);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
        // 2026-09-23 实测：`HWND_TOPMOST + SWP_SHOWWINDOW` **拉不回最小化的窗口** —— 编辑器一旦被
        // 最小化，窗口矩形恒为 `-32000,-32000 160x28`，`GameViewRect()` 跟着跑到屏外，`BitBlt`
        // 于是采到**纯黑**（三张 `_abr-*.png` 各 10798 B、meanRGB=0.00、sha256 三同）。补这两个
        // API 才能做到"先还原、再置顶"。
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int nCmdShow);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
        public delegate bool EnumProc(IntPtr h, IntPtr lp);
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    }

    /// <summary>GDI 采集所需的入口（CreateCompatibleDC + BitBlt + GetDIBits）。</summary>
    internal static class CaptureGdi32
    {
        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hDC);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleBitmap(IntPtr hDC, int w, int h);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObj);
        [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr hObj);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hDC);
        [DllImport("gdi32.dll")] public static extern int GetDIBits(IntPtr hDC, IntPtr hBmp, uint start, uint lines, byte[] bits, ref BITMAPINFO bi, uint usage);

        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFOHEADER
        {
            public uint biSize; public int biWidth; public int biHeight;
            public ushort biPlanes; public ushort biBitCount; public uint biCompression; public uint biSizeImage;
            public int biXPelsPerMeter; public int biYPelsPerMeter; public uint biClrUsed; public uint biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFO
        {
            public BITMAPINFOHEADER bmiHeader;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)] public uint[] bmiColors;
        }
    }

    /// <summary>
    /// 编辑器窗口级截屏。入口见文件头注释（`run_script --entry CloverEngine.Editor.EditorScreenCapture.*`）。
    /// <para>所有入口都返回**一行自报读数**（矩形 / 亮度 / 字节数 / 落盘路径），调用方据此判断成败，
    /// ⛔ 不靠"返回值是不是空"来猜。</para>
    /// </summary>
    public static class EditorScreenCapture
    {
        private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_SHOWWINDOW = 0x40;
        private const int SW_RESTORE = 9;   // ShowWindow 的"还原"命令码：最小化/最大化 -> 正常
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
        private const double DefaultMinMeanRgb = 2.0;

        // Application.dataPath = <client>/Assets ⇒ 项目根（含 .ai-tmp 的那一层）要往上找，
        // 不能写死"上一级"（那是 client/，`.ai-tmp` 在项目根下）。
        //
        // 判据与 `tools/probes/probe-ui-visibility.cs` 的 FindProjectRoot() **逐字同口径**
        // （⛔ 不另造第二套）：项目根 = **同时**含 `client/` 与 `.ai-tmp/` 的那一层。
        // 为什么不只找"第一个含 .ai-tmp 的祖先"：本机实测 `client/.ai-tmp/screenshots/…` 曾存在
        // （别的切片把相对路径写歪了）⇒ 只判 `.ai-tmp` 会把 spec 路径解析到 `client/.ai-tmp/`
        // 并回 "spec missing"。这里是**防复发**，不是修当前故障。
        private static string FindProjectRoot()
        {
            var d = new DirectoryInfo(Application.dataPath);
            for (var i = 0; i < 5 && d != null; i++, d = d.Parent)
            {
                if (Directory.Exists(Path.Combine(d.FullName, ".ai-tmp")) &&
                    Directory.Exists(Path.Combine(d.FullName, "client"))) return d.FullName;
            }
            d = new DirectoryInfo(Application.dataPath);
            for (var i = 0; i < 5 && d != null; i++, d = d.Parent)
            {
                if (Directory.Exists(Path.Combine(d.FullName, ".ai-tmp"))) return d.FullName;
            }
            return Directory.GetParent(Application.dataPath).FullName;
        }

        private static string SpecPath()
        {
            var dir = Path.Combine(FindProjectRoot(), ".ai-tmp", "test");
            var preferred = Path.Combine(dir, "editor-shot-spec.txt");
            if (File.Exists(preferred)) return preferred;
            var legacy = Path.Combine(dir, "bv-shot-spec.txt");   // 兼容 cs16 时代的旧名
            return File.Exists(legacy) ? legacy : preferred;
        }

        private static Dictionary<string, string> ReadSpec()
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var p = SpecPath();
            if (!File.Exists(p)) return d;
            foreach (var raw in File.ReadAllLines(p))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                var i = line.IndexOf('=');
                if (i <= 0) continue;
                d[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
            }
            return d;
        }

        private static IntPtr MainWindow()
        {
            var me = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
            var best = IntPtr.Zero;
            long bestArea = 0;
            // EnumWindows 的回调委托必须保持强引用，否则会被 GC 回收（回调期间崩溃）。
            CaptureWin32.EnumProc cb = null;
            cb = delegate (IntPtr h, IntPtr lp)
            {
                uint pid;
                CaptureWin32.GetWindowThreadProcessId(h, out pid);
                if (pid != me || !CaptureWin32.IsWindowVisible(h)) return true;
                var sb = new StringBuilder(512);
                CaptureWin32.GetWindowTextW(h, sb, 512);
                if (sb.Length == 0) return true;
                CaptureWin32.RECT r;
                if (!CaptureWin32.GetWindowRect(h, out r)) return true;
                long area = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
                if (area > bestArea) { bestArea = area; best = h; }
                return true;
            };
            CaptureWin32.EnumWindows(cb, IntPtr.Zero);
            GC.KeepAlive(cb);
            return best;
        }

        private static string RectStr(CaptureWin32.RECT r)
        {
            return r.Left + "," + r.Top + " " + (r.Right - r.Left) + "x" + (r.Bottom - r.Top);
        }

        private static UnityEngine.Rect GameViewRect()
        {
            var t = Type.GetType("UnityEditor.GameView,UnityEditor");
            if (t == null) return new UnityEngine.Rect(0, 0, 0, 0);
            var wins = Resources.FindObjectsOfTypeAll(t);
            if (wins == null || wins.Length == 0) return new UnityEngine.Rect(0, 0, 0, 0);
            var ed = wins[0] as UnityEditor.EditorWindow;
            return ed == null ? new UnityEngine.Rect(0, 0, 0, 0) : ed.position;
        }

        private static string GizmoState()
        {
            var t = Type.GetType("UnityEditor.GameView,UnityEditor");
            if (t == null) return "type=NULL";
            var prop = t.GetProperty("showGizmos",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            var wins = Resources.FindObjectsOfTypeAll(t);
            var sb = new StringBuilder();
            sb.Append("instances=").Append(wins == null ? 0 : wins.Length);
            if (wins != null)
            {
                for (var i = 0; i < wins.Length; i++)
                {
                    object v = prop != null ? prop.GetValue(wins[i], null) : null;
                    sb.Append(" showGizmos[").Append(i).Append("]=").Append(v == null ? "?" : v.ToString());
                }
            }
            return sb.ToString();
        }

        private static string SetGizmos(bool on)
        {
            var t = Type.GetType("UnityEditor.GameView,UnityEditor");
            if (t == null) return "type=NULL";
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            var props = new[] { "showGizmos", "drawGizmos" };
            var field = t.GetField("m_Gizmos", flags);
            var winds = Resources.FindObjectsOfTypeAll(t);
            if (winds == null || winds.Length == 0) return "no GameView instance";
            var log = new StringBuilder();
            for (var i = 0; i < winds.Length; i++)
            {
                // 实测：只写 showGizmos 时画面**一个像素都不变**（A/B 两张 PNG MD5 完全相同）
                // ⇒ 真正决定"画不画"的是 m_Gizmos / drawGizmos，三个成员一起写，并在返回值里
                // 逐项回读，避免"设了但没生效"这种静默失败。
                foreach (var n in props)
                {
                    var p = t.GetProperty(n, flags);
                    if (p != null && p.CanWrite)
                    {
                        p.SetValue(winds[i], on, null);
                        log.Append(" ").Append(n).Append('=').Append(p.GetValue(winds[i], null));
                    }
                }
                if (field != null)
                {
                    field.SetValue(winds[i], on);
                    log.Append(" m_Gizmos=").Append(field.GetValue(winds[i]));
                }
            }
            UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
            return "set " + on + " ->" + log;
        }

        /// <summary>只读：主窗口矩形 + Game view 矩形 + Gizmos 开关。</summary>
        public static string Info()
        {
            var h = MainWindow();
            var r = new CaptureWin32.RECT();
            CaptureWin32.GetWindowRect(h, out r);
            var gv = GameViewRect();
            var sb = new StringBuilder();
            sb.Append("pid=").Append(System.Diagnostics.Process.GetCurrentProcess().Id);
            sb.Append(" hwnd=").Append(h);
            sb.Append(" window=").Append(RectStr(r));
            sb.Append(" screen=").Append(Screen.width).Append('x').Append(Screen.height);
            sb.Append(" gameView=").Append(gv.x.ToString("F0")).Append(',').Append(gv.y.ToString("F0")).Append(' ')
              .Append(gv.width.ToString("F0")).Append('x').Append(gv.height.ToString("F0"));
            sb.Append(" gameViewRel=").Append((gv.x - r.Left).ToString("F0")).Append(',').Append((gv.y - r.Top).ToString("F0"));
            sb.Append(" playMode=").Append(UnityEditor.EditorApplication.isPlaying);
            sb.Append(" spec=").Append(SpecPath());
            sb.Append(" gizmos: ").Append(GizmoState());
            return sb.ToString();
        }

        /// <summary>把编辑器主窗口从最小化还原并置顶（截屏前用）。</summary>
        public static string Raise()
        {
            var h = MainWindow();
            // ⛔ 顺序不能反：只 SetWindowPos(TOPMOST) 时最小化窗口纹丝不动，后面 Shot 采到的就是
            //    屏外那片黑。先 SW_RESTORE 把窗口拉回屏内，再置顶；wasIconic/restore 一并回读，
            //    调用方据此判断"这次 raise 到底有没有把窗口弄回来"（不再靠猜）。
            var iconic = CaptureWin32.IsIconic(h);
            var restore = CaptureWin32.ShowWindow(h, SW_RESTORE);
            var ok1 = CaptureWin32.SetWindowPos(h, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
            var ok2 = CaptureWin32.SetForegroundWindow(h);
            return "raise hwnd=" + h + " wasIconic=" + iconic + " restore=" + restore + " topmost=" + ok1 + " foreground=" + ok2;
        }

        /// <summary>取消置顶（幂等）。</summary>
        public static string Drop()
        {
            var h = MainWindow();
            var ok = CaptureWin32.SetWindowPos(h, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
            return "drop hwnd=" + h + " ok=" + ok;
        }

        /// <summary>spec 的 gizmos=on|off 写 Game view 的 showGizmos（A/B 用）。</summary>
        public static string Gizmos()
        {
            var s = ReadSpec();
            string v;
            if (!s.TryGetValue("gizmos", out v)) return "spec missing gizmos=on|off (" + SpecPath() + ")";
            return SetGizmos(string.Equals(v, "on", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>spec 的 freeze=1|0 / timescale=&lt;float&gt; 写 Time.timeScale（A/B 需要同一帧）。</summary>
        public static string Freeze()
        {
            var s = ReadSpec();
            string v;
            float ts;
            if (s.TryGetValue("timescale", out v) && float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out ts))
            {
                Time.timeScale = ts;
                return "timeScale=" + ts.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
            }
            if (s.TryGetValue("freeze", out v) && v == "1") { Time.timeScale = 0f; return "timeScale=0"; }
            Time.timeScale = 1f;
            return "timeScale=1";
        }

        /// <summary>按 spec 截屏落 PNG，返回一行自报读数（含 meanRGB / 字节数 / 落盘路径）。</summary>
        public static string Shot()
        {
            var s = ReadSpec();
            string path, mode;
            if (!s.TryGetValue("path", out path) || path.Length == 0)
                return "spec missing path= (" + SpecPath() + ")";
            // ⛔ 判据资产**不许静默产出名字不是 .png 的产物**：spec 文件里若把
            //    `path=… mode=screen raise=0` 写成**同一行**（拼行缺陷），`s["path"]` 拿到的就是
            //    带空格的长值，截出来的文件名会变成 `xxx.png mode=screen raise=0`。该名字不以
            //    `.png` 结尾 ⇒ 正常 glob 看不见它，**但 Windows 的 8.3 短名会让 `-Filter *.png`
            //    仍然匹配到它** ⇒ 它会以「畸形名字」混进引用审计。拒绝 >> 静默产出。
            if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || path.IndexOf(' ') >= 0)
                return "spec path= must be one .png path with no spaces (got: " + path + ")";
            if (!s.TryGetValue("mode", out mode) || mode.Length == 0) mode = "gameview";
            mode = mode.ToLowerInvariant();

            var raised = false;
            var h = MainWindow();
            if (s.ContainsKey("raise") && s["raise"] == "1")
            {
                CaptureWin32.ShowWindow(h, SW_RESTORE);   // 同 Raise()：最小化窗口不还原就还是采到屏外
                CaptureWin32.SetWindowPos(h, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
                System.Threading.Thread.Sleep(1200);   // 让 DWM 完成合成；主线程短暂阻塞是可接受的代价
                raised = true;
            }

            var wr = new CaptureWin32.RECT();
            CaptureWin32.GetWindowRect(h, out wr);
            var gv = GameViewRect();

            int x = 0, y = 0, w = 0, hh = 0;
            if (mode == "screen")
            {
                if (!s.ContainsKey("x") || !s.ContainsKey("y") || !s.ContainsKey("w") || !s.ContainsKey("h"))
                    return "mode=screen needs x= y= w= h=";
                x = int.Parse(s["x"]); y = int.Parse(s["y"]);
                w = int.Parse(s["w"]); hh = int.Parse(s["h"]);
            }
            else if (mode == "window")
            {
                x = wr.Left; y = wr.Top; w = wr.Right - wr.Left; hh = wr.Bottom - wr.Top;
            }
            else
            {
                x = (int)Math.Round(gv.x); y = (int)Math.Round(gv.y);
                w = (int)Math.Round(gv.width); hh = (int)Math.Round(gv.height);
            }
            if (w <= 0 || hh <= 0)
                return "bad rect " + x + "," + y + " " + w + "x" + hh +
                       " (window minimized? run entry Raise, or spec mode=screen with x/y/w/h)";

            // GDI 采集：CreateCompatibleDC + BitBlt(SRCCOPY) + GetDIBits(32bpp, 自上而下)
            var screenDc = CaptureGdi32.GetDC(IntPtr.Zero);
            var memDc = CaptureGdi32.CreateCompatibleDC(screenDc);
            var bmp = CaptureGdi32.CreateCompatibleBitmap(screenDc, w, hh);
            var old = CaptureGdi32.SelectObject(memDc, bmp);
            var blt = CaptureGdi32.BitBlt(memDc, 0, 0, w, hh, screenDc, x, y, 0x00CC0020);   // SRCCOPY

            var bi = new CaptureGdi32.BITMAPINFO();
            bi.bmiHeader.biSize = (uint)Marshal.SizeOf(typeof(CaptureGdi32.BITMAPINFOHEADER));
            bi.bmiHeader.biWidth = w;
            bi.bmiHeader.biHeight = -hh;            // 负 = 自上而下
            bi.bmiHeader.biPlanes = 1;
            bi.bmiHeader.biBitCount = 32;
            bi.bmiHeader.biCompression = 0;         // BI_RGB
            bi.bmiColors = new uint[256];
            var bytes = new byte[w * hh * 4];
            var lines = CaptureGdi32.GetDIBits(memDc, bmp, 0, (uint)hh, bytes, ref bi, 0);

            // 实测（本机 DX12 + 虚拟显示）：直接落盘会得到 **180 度旋转**的图
            // （工具栏跑到下边、文字同时左右镜像）⇒ 用 flip= 显式纠正，并把纠正值写进返回值供复核。
            string flip;
            if (!s.TryGetValue("flip", out flip)) flip = "v";
            flip = flip.ToLowerInvariant();
            if (flip.Contains("v"))
            {
                var row = new byte[w * 4];
                var tmp = new byte[w * 4];
                var a = 0;
                var b2 = hh - 1;
                for (; a < b2; a++, b2--)
                {
                    Buffer.BlockCopy(bytes, a * w * 4, row, 0, w * 4);
                    Buffer.BlockCopy(bytes, b2 * w * 4, tmp, 0, w * 4);
                    Buffer.BlockCopy(tmp, 0, bytes, a * w * 4, w * 4);
                    Buffer.BlockCopy(row, 0, bytes, b2 * w * 4, w * 4);
                }
            }
            if (flip.Contains("h"))
            {
                for (var a = 0; a < hh; a++)
                {
                    var off = a * w * 4;
                    var l = 0;
                    var rr = w - 1;
                    for (; l < rr; l++, rr--)
                    {
                        for (var c = 0; c < 4; c++)
                        {
                            var t = bytes[off + l * 4 + c];
                            bytes[off + l * 4 + c] = bytes[off + rr * 4 + c];
                            bytes[off + rr * 4 + c] = t;
                        }
                    }
                }
            }

            CaptureGdi32.SelectObject(memDc, old);
            CaptureGdi32.DeleteObject(bmp);
            CaptureGdi32.DeleteDC(memDc);
            CaptureGdi32.ReleaseDC(IntPtr.Zero, screenDc);

            // 均值亮度：全黑/全白 = 采失败（比"文件有字节"更强的自检）。**先判后退**——
            // 退化帧不落盘，⛔ 不留下一张看起来像证据的黑图。
            long sum = 0;
            for (var i = 0; i < bytes.Length; i += 4) sum += bytes[i] + bytes[i + 1] + bytes[i + 2];
            var mean = (double)sum / (bytes.Length / 4 * 3);
            double minMean = DefaultMinMeanRgb;
            string mmv;
            if (s.TryGetValue("min-mean-rgb", out mmv))
                double.TryParse(mmv, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out minMean);

            if (raised) CaptureWin32.SetWindowPos(h, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);

            if (mean < minMean)
                return "shot FAILED (degenerate): mode=" + mode + " rect=" + x + "," + y + " " + w + "x" + hh +
                       " blt=" + blt + " lines=" + lines + " meanRGB=" + mean.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) +
                       " < min-mean-rgb " + minMean.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) +
                       " -- nothing written; run entry Raise (or spec raise=1) and check the window is not covered/off-screen";

            var tex = new Texture2D(w, hh, TextureFormat.BGRA32, false);
            tex.LoadRawTextureData(bytes);
            tex.Apply();
            var png = tex.EncodeToPNG();
            UnityEngine.Object.DestroyImmediate(tex);
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllBytes(path, png);

            return "shot mode=" + mode + " rect=" + x + "," + y + " " + w + "x" + hh +
                   " blt=" + blt + " lines=" + lines + " px=" + (w * hh) + " flip=" + flip +
                   " meanRGB=" + mean.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) +
                   " bytes=" + png.Length + " -> " + path;
        }

        /// <summary>
        /// 编辑器内的人用入口：菜单 `Clover/截图/编辑器窗口 PNG`。
        /// 与 run_script 走**同一入口**（<see cref="Shot"/>），spec 缺失时落到
        /// `&lt;项目根&gt;/.ai-tmp/screenshots/editor-window.png`。
        /// </summary>
        [UnityEditor.MenuItem("Clover/截图/编辑器窗口 PNG")]
        public static void CaptureFromMenu()
        {
            var spec = SpecPath();
            if (!File.Exists(spec))
            {
                var fallback = Path.Combine(FindProjectRoot(), ".ai-tmp", "screenshots", "editor-window.png");
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(fallback));
                    File.WriteAllLines(spec, new[] { "path=" + fallback.Replace('\\', '/'), "mode=window", "raise=1" });
                }
                catch (Exception ex)
                {
                    Debug.LogError("[EditorScreenCapture] 无法写 spec（" + spec + "）：" + ex.Message);
                    return;
                }
            }
            var result = Shot();
            if (result.StartsWith("shot FAILED", StringComparison.OrdinalIgnoreCase))
                Debug.LogError("[EditorScreenCapture] " + result);
            else
                Debug.Log("[EditorScreenCapture] " + result);
        }
    }
}
