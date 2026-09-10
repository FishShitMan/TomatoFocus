using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using TomatoFocus.App;
using TomatoFocus.Core;

namespace TomatoFocus
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate (object s, ThreadExceptionEventArgs e)
            {
                Log.Error("ThreadException", e.Exception);
            };
            AppDomain.CurrentDomain.UnhandledException += delegate (object s, UnhandledExceptionEventArgs e)
            {
                Log.Error("UnhandledException", e.ExceptionObject as Exception ?? new Exception("unknown"));
            };

            string dataDir = ArgValue(args, "--data");
            if (!string.IsNullOrEmpty(dataDir)) AppPaths.OverrideDataDir(dataDir);

            // 输入法自检：打印窗口的输入法上下文状态（用于定位"切不到中文输入法"）
            if (HasFlag(args, "--ime"))
            {
                Environment.ExitCode = ImeProbe.Run(ArgValue(args, "--ime-out"));
                return;
            }

            // 每秒帧统计写日志（验证"无操作降档"用）
            if (HasFlag(args, "--frames-log")) MainForm.LogFrames = true;

            // 快照模式：离屏渲染，不创建窗口
            string sheetDir = ArgValue(args, "--sheet");
            if (!string.IsNullOrEmpty(sheetDir))
            {
                int n = SheetRenderer.Render(sheetDir);
                Environment.ExitCode = n > 0 ? 0 : 1;
                return;
            }

            // 生成应用图标（程序化绘制后写出 .ico，供构建脚本嵌入）
            string iconOut = ArgValue(args, "--make-icon");
            if (!string.IsNullOrEmpty(iconOut))
            {
                try
                {
                    var icon = Tray.TrayIconArt.AppIcon(Render.Theme.ById("fresh"));
                    using (var fs = System.IO.File.Create(iconOut)) icon.Save(fs);
                    Console.WriteLine("icon -> " + iconOut);
                }
                catch (Exception ex) { Log.Error("make-icon", ex); Environment.ExitCode = 1; }
                return;
            }

            bool createdNew;
            using (var mutex = new Mutex(true, @"Local\TomatoFocus.SingleInstance", out createdNew))
            {
                if (!createdNew)
                {
                    Native.WakeExisting();
                    return;
                }

                bool minimized = HasFlag(args, "--minimized");
                string shot = ArgValue(args, "--shot");
                string demo = ArgValue(args, "--demo");
                int autoStart = 0;
                int.TryParse(ArgValue(args, "--autostart"), out autoStart);

                // 渲染性能自检：--perf 秒数 [--perf-out 报告路径]
                double perfSeconds = 0;
                double.TryParse(ArgValue(args, "--perf"), NumberStyles.Float, CultureInfo.InvariantCulture, out perfSeconds);
                if (perfSeconds > 0)
                {
                    PerfProbe.Active = new PerfProbe(perfSeconds);
                    string outPath = ArgValue(args, "--perf-out");
                    PerfProbe.OutputPath = string.IsNullOrEmpty(outPath)
                        ? Path.Combine(AppPaths.DataDir, "perf.txt")
                        : outPath;
                }

                using (var shell = new AppShell(minimized, shot, demo, autoStart))
                {
                    Application.Run(shell);
                }
            }
        }

        private static bool HasFlag(string[] args, string flag)
        {
            if (args == null) return false;
            foreach (string a in args) if (string.Equals(a, flag, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static string ArgValue(string[] args, string name)
        {
            if (args == null) return null;
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    return i + 1 < args.Length ? args[i + 1] : null;
                if (args[i].StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
                    return args[i].Substring(name.Length + 1);
            }
            return null;
        }
    }
}
