using System;
using System.Reflection;

namespace TomatoFocus.Core
{
    /// <summary>应用元信息。版本号来自程序集属性（唯一来源，见 Properties/AssemblyInfo.cs）。</summary>
    internal static class AppInfo
    {
        private static string _version;

        public static string Version
        {
            get
            {
                if (_version != null) return _version;
                try
                {
                    var asm = Assembly.GetExecutingAssembly();
                    var info = (AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(
                        asm, typeof(AssemblyInformationalVersionAttribute));
                    if (info != null && !string.IsNullOrEmpty(info.InformationalVersion))
                    {
                        _version = info.InformationalVersion;
                        return _version;
                    }
                    var file = (AssemblyFileVersionAttribute)Attribute.GetCustomAttribute(
                        asm, typeof(AssemblyFileVersionAttribute));
                    if (file != null && !string.IsNullOrEmpty(file.Version))
                    {
                        _version = file.Version.TrimEnd('.', '0').TrimEnd('.');
                        return _version;
                    }
                    var ver = asm.GetName().Version;
                    _version = ver == null ? "0.0.0" : ver.Major + "." + ver.Minor + "." + ver.Build;
                }
                catch (Exception)
                {
                    _version = "0.0.0";
                }
                return _version;
            }
        }
    }
}
