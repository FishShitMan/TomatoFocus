using System;
using System.Drawing;
using System.Windows.Forms;
using TomatoFocus.Core;

namespace TomatoFocus.App
{
    /// <summary>
    /// 命令行输入法自检（<c>--ime [--ime-out 路径]</c>）。
    /// 在一张隐藏窗口上按主程序同样的方式关联输入法上下文，输出与
    /// 「导出诊断报告」相同的内容，默认写到数据目录（可用 --ime-out 指定）。
    /// </summary>
    internal static class ImeProbe
    {
        public static int Run(string outPath)
        {
            IntPtr hwnd = IntPtr.Zero;
            string extra = null;
            string text = null;

            try
            {
                using (var f = new Form())
                {
                    f.FormBorderStyle = FormBorderStyle.None;
                    f.ShowInTaskbar = false;
                    f.StartPosition = FormStartPosition.Manual;
                    f.Location = new Point(-4000, -4000);
                    f.Size = new Size(20, 20);
                    f.ImeMode = ImeMode.NoControl;

                    hwnd = f.Handle;                  // 强制创建窗口句柄
                    string before = Snapshot(hwnd);

                    // 模拟"笔记聚焦"时程序真正做的事，验证这条路径在本机是否可行
                    IntPtr zh = Native.FindChineseLayout();
                    if (zh != IntPtr.Zero && !Native.IsChineseLayout(Native.GetKeyboardLayout(0)))
                        Native.SendMessage(hwnd, Native.WM_INPUTLANGCHANGEREQUEST, IntPtr.Zero, zh);

                    IntPtr himc = Native.ImmGetContext(hwnd);
                    if (himc == IntPtr.Zero)
                    {
                        IntPtr ctx = Native.ImmCreateContext();
                        if (ctx != IntPtr.Zero) Native.ImmAssociateContext(hwnd, ctx);
                        himc = Native.ImmGetContext(hwnd);
                    }

                    string after = "（无法取得输入法上下文）";
                    if (himc != IntPtr.Zero)
                    {
                        Native.ImmSetOpenStatus(himc, true);
                        Native.ImmSetConversionStatus(himc, Native.IME_CMODE_NATIVE, Native.IME_SMODE_NONE);
                        int conv = 0, sent = 0;
                        Native.ImmGetConversionStatus(himc, ref conv, ref sent);
                        after = "布局=0x" + ((long)Native.GetKeyboardLayout(0)).ToString("X8") +
                                " 打开=" + (Native.ImmGetOpenStatus(himc) ? "是" : "否") +
                                " 转换模式=0x" + conv.ToString("X4") +
                                ((conv & Native.IME_CMODE_NATIVE) != 0 ? "（中文模式，可打拼音）" : "（非中文模式）");
                        Native.ImmReleaseContext(hwnd, himc);
                    }

                    extra = "聚焦前: " + before + Environment.NewLine +
                            "聚焦后: " + after + Environment.NewLine +
                            "（上面是模拟「笔记聚焦」时程序实际执行的操作：切中文布局 → 打开输入法 → 切本地语言模式）";

                    // 趁窗口还活着生成报告，否则输入法一节会显示"未关联上下文"
                    text = Diagnostics.Build(null, hwnd, extra);
                }
            }
            catch (Exception ex)
            {
                extra = "自检窗口创建失败: " + ex.Message;
            }

            if (text == null) text = Diagnostics.Build(null, IntPtr.Zero, extra);

            // 两条路径都带时间戳，多次导出不会互相覆盖
            string target = string.IsNullOrEmpty(outPath)
                ? System.IO.Path.Combine(AppPaths.DataDir, "诊断报告.txt")   // 命令行自检不往桌面丢文件
                : outPath;
            string written = null;
            try
            {
                string p = Diagnostics.StampFileName(target, DateTime.Now);
                string dir = System.IO.Path.GetDirectoryName(p);
                if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
                System.IO.File.WriteAllText(p, text, new System.Text.UTF8Encoding(false));
                written = p;
            }
            catch (Exception) { }

            try { Console.Write(text); } catch (Exception) { }
            Log.Info("ime probe -> " + (written ?? "(未写出)"));
            return 0;
        }

        private static string Snapshot(IntPtr hwnd)
        {
            try
            {
                IntPtr himc = Native.ImmGetContext(hwnd);
                if (himc == IntPtr.Zero) return "窗口无输入法上下文";
                int conv = 0, sent = 0;
                Native.ImmGetConversionStatus(himc, ref conv, ref sent);
                string s = "布局=0x" + ((long)Native.GetKeyboardLayout(0)).ToString("X8") +
                           " 打开=" + (Native.ImmGetOpenStatus(himc) ? "是" : "否") +
                           " 转换模式=0x" + conv.ToString("X4");
                Native.ImmReleaseContext(hwnd, himc);
                return s;
            }
            catch (Exception ex) { return "检测失败: " + ex.Message; }
        }
    }
}
