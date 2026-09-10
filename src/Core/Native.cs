using System;
using System.Runtime.InteropServices;

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
