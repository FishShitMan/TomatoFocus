using System;
using System.Runtime.InteropServices;
using System.Text;

namespace TomatoFocus.Core
{
    /// <summary>Win32 互操作（仅用系统自带 API）。</summary>
    internal static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int left, top, right, bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int x, y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct MINMAXINFO
        {
            public POINT ptReserved;
            public POINT ptMaxSize;
            public POINT ptMaxPosition;
            public POINT ptMinTrackSize;
            public POINT ptMaxTrackSize;
        }

        [DllImport("user32.dll")]
        public static extern bool ReleaseCapture();

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        // GDI 位块传送：把一帧的绘制结果存进缓存位图（勋章快路径要用它当背景）
        [DllImport("gdi32.dll")]
        public static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int w, int h,
                                         IntPtr hdcSrc, int xSrc, int ySrc, int rop);
        public const int SRCCOPY = 0x00CC0020;

        // --- 输入法（IMM32）：把拼音组合窗口与候选框定位到笔记光标处 ---------
        public const int CFS_POINT = 0x0002;

        [StructLayout(LayoutKind.Sequential)]
        public struct COMPOSITIONFORM
        {
            public int dwStyle;
            public POINT ptCurrentPos;
            public RECT rcArea;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct CANDIDATEFORM
        {
            public int dwIndex;
            public int dwStyle;
            public POINT ptCurrentPos;
            public RECT rcArea;
        }

        [DllImport("imm32.dll")]
        public static extern IntPtr ImmGetContext(IntPtr hwnd);

        [DllImport("imm32.dll")]
        public static extern bool ImmReleaseContext(IntPtr hwnd, IntPtr himc);

        // 输入法上下文：窗口若没有关联上下文，中文输入法根本切不过来，
        // 必须用 ImmCreateContext + ImmAssociateContext 显式挂一个上去。
        [DllImport("imm32.dll")]
        public static extern IntPtr ImmCreateContext();

        [DllImport("imm32.dll")]
        public static extern IntPtr ImmAssociateContext(IntPtr hwnd, IntPtr himc);

        [DllImport("imm32.dll")]
        public static extern bool ImmAssociateContextEx(IntPtr hwnd, IntPtr himc, uint flags);

        [DllImport("imm32.dll")]
        public static extern bool ImmIsIME(IntPtr hkl);

        /// <summary>ImmAssociateContextEx：挂上"系统默认"输入法上下文（而不是自己造一个空的）。</summary>
        public const uint IACE_DEFAULT = 0x0010;

        [DllImport("imm32.dll")]
        public static extern bool ImmGetOpenStatus(IntPtr himc);

        [DllImport("imm32.dll")]
        public static extern bool ImmSetOpenStatus(IntPtr himc, bool open);

        [DllImport("imm32.dll")]
        public static extern bool ImmGetConversionStatus(IntPtr himc, ref int conversion, ref int sentence);

        [DllImport("imm32.dll")]
        public static extern bool ImmSetConversionStatus(IntPtr himc, int conversion, int sentence);

        [DllImport("imm32.dll", CharSet = CharSet.Unicode)]
        public static extern int ImmGetDescription(IntPtr hkl, System.Text.StringBuilder description, int size);

        [DllImport("imm32.dll", CharSet = CharSet.Unicode)]
        public static extern int ImmGetIMEFileName(IntPtr hkl, System.Text.StringBuilder fileName, int size);

        [DllImport("user32.dll")]
        public static extern IntPtr GetKeyboardLayout(int threadId);

        [DllImport("user32.dll")]
        public static extern int GetKeyboardLayoutList(int nBuff, IntPtr[] lpList);

        /// <summary>切换窗口输入语言的请求消息（由系统处理后回发 WM_INPUTLANGCHANGE）。</summary>
        public const int WM_INPUTLANGCHANGEREQUEST = 0x0050;

        /// <summary>语言 ID 的主语言部分（中文 = 0x04）。</summary>
        public static int PrimaryLangId(IntPtr hkl)
        {
            return (int)(((long)hkl) & 0x3FF);
        }

        public static bool IsChineseLayout(IntPtr hkl)
        {
            return hkl != IntPtr.Zero && PrimaryLangId(hkl) == 0x04;   // LANG_CHINESE
        }

        /// <summary>
        /// 在已安装的键盘布局里找一个中文输入法。
        /// **只接受真正的输入法**（ImmIsIME 为真）：中文语言下系统常常同时装着
        /// 「中文(简体) - 美式键盘」这类纯键盘布局，切过去照样只能打英文字母。
        /// 找不到真正的输入法时，退回任意中文布局（并在界面上提示用户）。
        /// </summary>
        public static IntPtr FindChineseLayout()
        {
            try
            {
                var list = new IntPtr[64];
                int n = GetKeyboardLayoutList(list.Length, list);
                IntPtr fallback = IntPtr.Zero;
                for (int i = 0; i < n && i < list.Length; i++)
                {
                    if (!IsChineseLayout(list[i])) continue;
                    if (IsImeLayout(list[i])) return list[i];     // 优先：真的输入法
                    if (fallback == IntPtr.Zero) fallback = list[i];
                }
                return fallback;
            }
            catch (Exception) { }
            return IntPtr.Zero;
        }

        /// <summary>该布局是不是"真正的输入法"（而非纯键盘布局）。</summary>
        public static bool IsImeLayout(IntPtr hkl)
        {
            try { return hkl != IntPtr.Zero && ImmIsIME(hkl); } catch (Exception) { return false; }
        }

        /// <summary>是否存在可用的中文输入法。</summary>
        public static bool HasChineseIme()
        {
            IntPtr hkl = FindChineseLayout();
            return hkl != IntPtr.Zero && IsImeLayout(hkl);
        }

        /// <summary>列出已安装的键盘布局（诊断用）。</summary>
        public static string DescribeLayouts()
        {
            var list = new IntPtr[64];
            int n = GetKeyboardLayoutList(list.Length, list);
            var sb = new StringBuilder();
            for (int i = 0; i < n && i < list.Length; i++)
            {
                var d = new StringBuilder(260);
                int len = ImmGetDescription(list[i], d, d.Capacity);
                sb.Append("  0x").Append(((long)list[i]).ToString("X8")).Append("  ")
                  .Append(len > 0 ? d.ToString() : "(未提供描述)")
                  .Append(IsChineseLayout(list[i]) ? "  [中文]" : "")
                  .Append(IsImeLayout(list[i]) ? "  [输入法]" : "  [纯键盘布局]")
                  .AppendLine();
            }
            return sb.Length == 0 ? "  （未枚举到任何键盘布局）\r\n" : sb.ToString();
        }

        /// <summary>中文（本地语言）输入模式。</summary>
        public const int IME_CMODE_NATIVE = 0x0001;
        /// <summary>英文（半角字母数字）输入模式。</summary>
        public const int IME_CMODE_ALPHANUMERIC = 0x0000;
        public const int IME_SMODE_NONE = 0x0000;

        [DllImport("imm32.dll")]
        public static extern bool ImmSetCompositionWindow(IntPtr himc, ref COMPOSITIONFORM lpCompForm);

        [DllImport("imm32.dll")]
        public static extern bool ImmSetCandidateWindow(IntPtr himc, ref CANDIDATEFORM lpCandidate);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        [DllImport("user32.dll")]
        public static extern uint GetDpiForWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

        [DllImport("winmm.dll")]
        private static extern uint timeBeginPeriod(uint ms);

        [DllImport("winmm.dll")]
        private static extern uint timeEndPeriod(uint ms);

        /// <summary>把系统定时器精度提高到 1ms，让动画帧间隔稳定（Windows 默认约 15.6ms）。</summary>
        public static void BeginHighResolutionTimers() { try { timeBeginPeriod(1); } catch { } }

        public static void EndHighResolutionTimers() { try { timeEndPeriod(1); } catch { } }

        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOZORDER = 0x0004;
        public const uint SWP_NOACTIVATE = 0x0010;

        /// <summary>窗口实际 DPI（PerMonitorV2 下比 Control.DeviceDpi 可靠）。</summary>
        public static float WindowScale(IntPtr hwnd)
        {
            try
            {
                if (hwnd != IntPtr.Zero)
                {
                    uint dpi = GetDpiForWindow(hwnd);
                    if (dpi >= 48) return dpi / 96f;
                }
            }
            catch { }
            return 1f;
        }

        public const int SW_RESTORE = 9;
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWCP_ROUND = 2;

        /// <summary>Windows 11 的圆角窗口（低版本静默失败）。</summary>
        public static void RoundCorners(IntPtr hwnd)
        {
            try
            {
                int pref = DWMWCP_ROUND;
                DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
            }
            catch { }
        }

        /// <summary>唤醒已在运行的实例。</summary>
        public static bool WakeExisting()
        {
            try
            {
                var h = FindWindow(null, "TomatoFocus");
                if (h == IntPtr.Zero) return false;
                ShowWindow(h, SW_RESTORE);
                SetForegroundWindow(h);
                return true;
            }
            catch { return false; }
        }
    }

    /// <summary>极简文件日志（出错时定位问题用，不联网）。</summary>
    internal static class Log
    {
        private static readonly object Lock = new object();

        public static void Error(string where, Exception ex)
        {
            Write("ERROR", where + ": " + ex.GetType().Name + " " + ex.Message + Environment.NewLine + ex.StackTrace);
        }

        public static void Info(string message) { Write("INFO", message); }

        private static void Write(string level, string message)
        {
            try
            {
                lock (Lock)
                {
                    System.IO.Directory.CreateDirectory(AppPaths.DataDir);
                    string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " [" + level + "] " + message + Environment.NewLine;
                    System.IO.File.AppendAllText(AppPaths.LogFile, line);
                }
            }
            catch { }
        }
    }
}
