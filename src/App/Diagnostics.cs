using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using TomatoFocus.Core;

namespace TomatoFocus.App
{
    /// <summary>
    /// 诊断报告：把"运行环境 + 窗口 + 输入法 + 渲染"的关键状态写成一份纯文本，
    /// 供用户一键导出后回传。窗口程序没有控制台，这是唯一方便用户拿到日志的方式。
    /// </summary>
    internal static class Diagnostics
    {
        /// <summary>生成报告正文。app / hwnd 允许为 null（命令行自检时用临时窗口）。</summary>
        public static string Build(AppState app, IntPtr hwnd, string frameInfo)
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== 番茄专注 诊断报告 ===");
            sb.AppendLine("生成时间   : " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("程序版本   : " + AppInfo.Version);
            sb.AppendLine("系统       : " + Environment.OSVersion.VersionString + " / " +
                          (Environment.Is64BitProcess ? "64 位进程" : "32 位进程"));
            sb.AppendLine("数据目录   : " + AppPaths.DataDir);
            sb.AppendLine("日志文件   : " + AppPaths.LogFile);
            sb.AppendLine();

            sb.AppendLine("--- 窗口 ---");
            if (hwnd != IntPtr.Zero)
            {
                sb.AppendLine("窗口句柄   : 0x" + ((long)hwnd).ToString("X8"));
                sb.AppendLine("窗口 DPI 缩放: " + Native.WindowScale(hwnd).ToString("0.##"));
            }
            else
            {
                sb.AppendLine("窗口句柄   : （无）");
            }
            if (!string.IsNullOrEmpty(frameInfo)) sb.AppendLine("渲染统计   : " + frameInfo);
            sb.AppendLine();

            sb.AppendLine("--- 输入法 ---");
            AppendIme(sb, hwnd);
            sb.AppendLine();

            sb.AppendLine("--- 当前设置 ---");
            if (app != null)
            {
                var s = app.Data.Settings;
                sb.AppendLine("主题 / 语言: " + s.ThemeId + " / " + s.Lang);
                sb.AppendLine("极简 / 自启: " + s.MinimalMode + " / " + s.AutoStart);
                sb.AppendLine("音效开关   : " + s.Sound);
                sb.AppendLine("休息时长   : " + (s.BreakSeconds / 60) + " 分钟（仅影响专注结束后的提醒卡片）");
                sb.AppendLine("装备       : 称号=" + (string.IsNullOrEmpty(app.Data.Rewards.EquippedTitle) ? "无" : app.Data.Rewards.EquippedTitle) +
                              " 奖章=" + (string.IsNullOrEmpty(app.Data.Rewards.EquippedMedal) ? "无" : app.Data.Rewards.EquippedMedal) +
                              " 提示音=" + (string.IsNullOrEmpty(app.Data.Rewards.EquippedSound) ? "无" : app.Data.Rewards.EquippedSound));
                sb.AppendLine("计时状态   : " + app.Timer.Phase + " 剩余 " + TomatoMath.FormatClock(app.Timer.RemainingSeconds) +
                              " 档位 " + (app.Timer.PlannedSeconds / 60) + " 分钟");
            }
            else
            {
                sb.AppendLine("（命令行自检模式，无应用状态）");
            }
            sb.AppendLine();
            sb.AppendLine("--- 最近日志 ---");
            try
            {
                if (File.Exists(AppPaths.LogFile))
                {
                    var lines = File.ReadAllLines(AppPaths.LogFile);
                    int start = Math.Max(0, lines.Length - 40);
                    for (int i = start; i < lines.Length; i++) sb.AppendLine(lines[i]);
                }
                else sb.AppendLine("（暂无日志）");
            }
            catch (Exception ex) { sb.AppendLine("（读取日志失败：" + ex.Message + "）"); }

            return sb.ToString();
        }

        private static void AppendIme(StringBuilder sb, IntPtr hwnd)
        {
            try
            {
                IntPtr zh = Native.FindChineseLayout();
                sb.AppendLine("已装中文布局: " + (zh != IntPtr.Zero ? "是（0x" + ((long)zh).ToString("X8") + "）" : "否"));
                sb.AppendLine("中文布局是真输入法吗: " + (Native.IsImeLayout(zh)
                    ? "是（可打拼音）"
                    : "否——这只是「中文-美式键盘」这类纯键盘布局，切过去也只能打英文字母；"
                      + "请在「设置 → 时间和语言 → 语言和区域 → 中文(简体，中国) → 语言选项」里确认已安装微软拼音"));
                IntPtr cur = Native.GetKeyboardLayout(0);
                sb.AppendLine("当前键盘布局: 0x" + ((long)cur).ToString("X8") +
                              (Native.IsChineseLayout(cur) ? "（中文）" : "（非中文）"));

                if (hwnd != IntPtr.Zero)
                {
                    IntPtr himc = Native.ImmGetContext(hwnd);
                    sb.AppendLine("窗口已关联输入法上下文: " + (himc != IntPtr.Zero ? "是" : "否"));
                    if (himc != IntPtr.Zero)
                    {
                        sb.AppendLine("输入法是否打开: " + (Native.ImmGetOpenStatus(himc) ? "打开（中文）" : "关闭（英文）"));
                        int conv = 0, sent = 0;
                        Native.ImmGetConversionStatus(himc, ref conv, ref sent);
                        sb.AppendLine("转换模式   : 0x" + conv.ToString("X4") +
                                      ((conv & Native.IME_CMODE_NATIVE) != 0 ? "（本地语言=中文模式）" : "（非中文模式）"));
                        Native.ImmReleaseContext(hwnd, himc);
                    }
                }

                sb.AppendLine("已安装的键盘布局:");
                sb.Append(Native.DescribeLayouts());
            }
            catch (Exception ex) { sb.AppendLine("（输入法检测失败：" + ex.Message + "）"); }
        }

        /// <summary>
        /// 给文件名加上时间戳（如 `番茄专注-诊断报告-20260911-013045.txt`），避免多次导出互相覆盖；
        /// 同一秒内重复导出时再追加 -2、-3…… 直到不重名。
        /// </summary>
        public static string StampFileName(string fileName, DateTime now)
        {
            if (string.IsNullOrEmpty(fileName)) fileName = "诊断报告.txt";
            string name = Path.GetFileNameWithoutExtension(fileName);
            string ext = Path.GetExtension(fileName);
            if (string.IsNullOrEmpty(ext)) ext = ".txt";
            string dir = "";
            try { dir = Path.GetDirectoryName(fileName) ?? ""; } catch (Exception) { }

            string stem = name + "-" + now.ToString("yyyyMMdd-HHmmss");
            string path = string.IsNullOrEmpty(dir) ? stem + ext : Path.Combine(dir, stem + ext);
            int n = 2;
            while (File.Exists(path) && n < 1000)
            {
                string alt = stem + "-" + n + ext;
                path = string.IsNullOrEmpty(dir) ? alt : Path.Combine(dir, alt);
                n++;
            }
            return path;
        }

        /// <summary>
        /// 导出报告：优先写到桌面，失败则写数据目录；返回实际路径（失败返回 null）。
        /// 文件名带时间戳，多次导出不会覆盖。
        /// </summary>
        public static string Export(string text, string fileName)
        {
            try
            {
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                string path = null;
                if (!string.IsNullOrEmpty(desktop) && Directory.Exists(desktop))
                    path = StampFileName(Path.Combine(desktop, fileName), DateTime.Now);
                try { File.WriteAllText(path, text, new UTF8Encoding(false)); }
                catch
                {
                    Directory.CreateDirectory(AppPaths.DataDir);
                    path = StampFileName(Path.Combine(AppPaths.DataDir, fileName), DateTime.Now);
                    File.WriteAllText(path, text, new UTF8Encoding(false));
                }
                return path;
            }
            catch (Exception ex)
            {
                Log.Error("Diagnostics.Export", ex);
                return null;
            }
        }

        /// <summary>用记事本打开报告，方便用户直接复制发回。</summary>
        public static void OpenInEditor(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    Process.Start("notepad.exe", "\"" + path + "\"");
            }
            catch (Exception) { }
        }
    }
}
