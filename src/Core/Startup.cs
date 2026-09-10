using System;
using Microsoft.Win32;

namespace TomatoFocus.Core
{
    /// <summary>开机自启（写入 HKCU 的 Run 项，无需管理员权限）。默认关闭。</summary>
    internal static class Startup
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "TomatoFocus";

        public static bool IsEnabled()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    if (key == null) return false;
                    var v = key.GetValue(ValueName) as string;
                    return !string.IsNullOrEmpty(v);
                }
            }
            catch { return false; }
        }

        public static bool Set(bool enabled)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKey, true) ?? Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (key == null) return false;
                    if (enabled)
                    {
                        string exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
                        if (string.IsNullOrEmpty(exe)) return false;
                        key.SetValue(ValueName, "\"" + exe + "\" --minimized", RegistryValueKind.String);
                    }
                    else
                    {
                        if (key.GetValue(ValueName) != null) key.DeleteValue(ValueName, false);
                    }
                    return true;
                }
            }
            catch { return false; }
        }
    }
}
