using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;

namespace TomatoFocus.Core
{
    /// <summary>
    /// 国际化。默认简体中文；文案全部来自 Lang/&lt;lang&gt;.json，
    /// 优先读取 exe 同目录下的 Lang/ 文件夹（便于不重新编译就改文案），
    /// 否则回退到编译期嵌入的资源。
    /// </summary>
    internal static class I18n
    {
        public const string DefaultLang = "zh-CN";

        private static readonly string[] AvailableLangs = { "zh-CN", "en-US" };
        private static Dictionary<string, object> _map = new Dictionary<string, object>(StringComparer.Ordinal);
        private static string _current = DefaultLang;

        public static string Current { get { return _current; } }
        public static string[] Available { get { return (string[])AvailableLangs.Clone(); } }

        public static event EventHandler Changed;

        public static void Load(string lang)
        {
            if (string.IsNullOrEmpty(lang)) lang = DefaultLang;
            string json = ReadLangFile(lang);
            if (json == null && lang != DefaultLang) json = ReadLangFile(DefaultLang);
            var parsed = json == null ? null : Json.ParseOrNull(json) as Dictionary<string, object>;
            _map = parsed ?? new Dictionary<string, object>(StringComparer.Ordinal);
            _current = lang;
            var h = Changed;
            if (h != null) h(null, EventArgs.Empty);
        }

        private static string ReadLangFile(string lang)
        {
            try
            {
                string dir = Path.Combine(AppPaths.ExeDir, "Lang");
                string file = Path.Combine(dir, lang + ".json");
                if (File.Exists(file)) return File.ReadAllText(file, Encoding.UTF8);
            }
            catch { /* 外部文件不可用时静默回退 */ }

            try
            {
                var asm = Assembly.GetExecutingAssembly();
                using (var stream = asm.GetManifestResourceStream("Lang." + lang + ".json"))
                {
                    if (stream == null) return null;
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                        return reader.ReadToEnd();
                }
            }
            catch { return null; }
        }

        /// <summary>取一条文案；缺失时返回 key 本身（开发期便于发现漏词）。</summary>
        public static string T(string key)
        {
            object v;
            if (_map.TryGetValue(key, out v))
            {
                var s = v as string;
                if (s != null) return s;
                if (v is List<object>) return key; // 数组请用 List()
            }
            return key;
        }

        public static string T(string key, params object[] args)
        {
            string format = T(key);
            if (args == null || args.Length == 0) return format;
            try { return string.Format(CultureInfo.CurrentCulture, format, args); }
            catch (FormatException) { return format; }
        }

        /// <summary>取一个字符串数组（如关怀语句库）。</summary>
        public static string[] List(string key)
        {
            object v;
            if (!_map.TryGetValue(key, out v)) return new string[0];
            var list = v as List<object>;
            if (list == null) return new string[0];
            var result = new string[list.Count];
            for (int i = 0; i < list.Count; i++)
                result[i] = Convert.ToString(list[i], CultureInfo.CurrentCulture);
            return result;
        }

        /// <summary>语言自身的显示名，用于设置页。</summary>
        public static string DisplayName(string lang)
        {
            switch (lang)
            {
                case "zh-CN": return "简体中文";
                case "en-US": return "English";
                default: return lang;
            }
        }
    }

    /// <summary>应用路径。</summary>
    internal static class AppPaths
    {
        private static string _exeDir;
        private static string _dataDir;

        public static string ExeDir
        {
            get
            {
                if (_exeDir == null)
                    _exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? Environment.CurrentDirectory;
                return _exeDir;
            }
        }

        /// <summary>数据目录：%APPDATA%\TomatoFocus（可被 --data 参数覆盖，便于测试）。</summary>
        public static string DataDir
        {
            get
            {
                if (_dataDir == null)
                    _dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TomatoFocus");
                return _dataDir;
            }
        }

        public static void OverrideDataDir(string dir)
        {
            if (!string.IsNullOrEmpty(dir)) _dataDir = dir;
        }

        public static string DataFile { get { return Path.Combine(DataDir, "data.json"); } }
        public static string BackupFile { get { return Path.Combine(DataDir, "data.bak.json"); } }
        public static string AssetsDir { get { return Path.Combine(ExeDir, "assets"); } }
        public static string LogFile { get { return Path.Combine(DataDir, "log.txt"); } }
    }
}
