using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace TomatoFocus.Core
{
    /// <summary>
    /// 轻量 JSON 写出器（net48 无 System.Text.Json，且本项目不引入任何第三方依赖）。
    /// </summary>
    internal sealed class JsonWriter
    {
        private readonly StringBuilder _sb;
        private readonly List<bool> _hasItems = new List<bool>();
        private int _depth;
        private bool _afterName;

        public JsonWriter(int capacity = 8192)
        {
            _sb = new StringBuilder(capacity);
        }

        private void NewLine()
        {
            if (_depth <= 0) return;
            _sb.Append('\n');
            for (int i = 0; i < _depth; i++) _sb.Append("  ");
        }

        private void Separate()
        {
            if (_afterName) { _afterName = false; return; }
            if (_hasItems.Count > 0 && _hasItems[_hasItems.Count - 1]) _sb.Append(',');
            NewLine();
        }

        private void MarkItem()
        {
            if (_hasItems.Count > 0) _hasItems[_hasItems.Count - 1] = true;
        }

        public JsonWriter Name(string name)
        {
            Separate();
            WriteStringLiteral(name);
            _sb.Append(": ");
            _afterName = true;
            return this;
        }

        public JsonWriter BeginObject()
        {
            Separate();
            MarkItem();
            _sb.Append('{');
            _hasItems.Add(false);
            _depth++;
            return this;
        }

        public JsonWriter EndObject()
        {
            bool any = _hasItems[_hasItems.Count - 1];
            _hasItems.RemoveAt(_hasItems.Count - 1);
            _depth--;
            if (any) NewLine();
            _sb.Append('}');
            return this;
        }

        public JsonWriter BeginArray()
        {
            Separate();
            MarkItem();
            _sb.Append('[');
            _hasItems.Add(false);
            _depth++;
            return this;
        }

        public JsonWriter EndArray()
        {
            bool any = _hasItems[_hasItems.Count - 1];
            _hasItems.RemoveAt(_hasItems.Count - 1);
            _depth--;
            if (any) NewLine();
            _sb.Append(']');
            return this;
        }

        public JsonWriter Value(string s)
        {
            Separate();
            MarkItem();
            if (s == null) _sb.Append("null");
            else WriteStringLiteral(s);
            return this;
        }

        public JsonWriter Value(bool b)
        {
            Separate();
            MarkItem();
            _sb.Append(b ? "true" : "false");
            return this;
        }

        public JsonWriter Value(int n)
        {
            Separate();
            MarkItem();
            _sb.Append(n.ToString(CultureInfo.InvariantCulture));
            return this;
        }

        public JsonWriter Value(long n)
        {
            Separate();
            MarkItem();
            _sb.Append(n.ToString(CultureInfo.InvariantCulture));
            return this;
        }

        public JsonWriter Value(double d)
        {
            Separate();
            MarkItem();
            if (double.IsNaN(d) || double.IsInfinity(d)) { _sb.Append("0"); return this; }
            if (d == Math.Floor(d) && Math.Abs(d) < 1e15)
                _sb.Append(((long)d).ToString(CultureInfo.InvariantCulture));
            else
                _sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
            return this;
        }

        public JsonWriter Null()
        {
            Separate();
            MarkItem();
            _sb.Append("null");
            return this;
        }

        private void WriteStringLiteral(string s)
        {
            _sb.Append('"');
            if (s != null)
            {
                foreach (char c in s)
                {
                    switch (c)
                    {
                        case '"': _sb.Append("\\\""); break;
                        case '\\': _sb.Append("\\\\"); break;
                        case '\b': _sb.Append("\\b"); break;
                        case '\f': _sb.Append("\\f"); break;
                        case '\n': _sb.Append("\\n"); break;
                        case '\r': _sb.Append("\\r"); break;
                        case '\t': _sb.Append("\\t"); break;
                        default:
                            if (c < ' ') _sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                            else _sb.Append(c);
                            break;
                    }
                }
            }
            _sb.Append('"');
        }

        public override string ToString()
        {
            return _sb.ToString();
        }
    }

    /// <summary>极简 JSON 解析器，产出 Dictionary&lt;string,object&gt; / List&lt;object&gt; / string / double / bool / null。</summary>
    internal static class Json
    {
        public static object Parse(string text)
        {
            if (text == null) return null;
            int i = 0;
            if (text.Length > 0 && text[0] == '\uFEFF') i = 1;
            SkipWs(text, ref i);
            if (i >= text.Length) return null;
            object v = ParseValue(text, ref i);
            SkipWs(text, ref i);
            if (i < text.Length) throw new FormatException("JSON 尾部存在多余内容（位置 " + i + "）。");
            return v;
        }

        public static object ParseOrNull(string text)
        {
            try { return Parse(text); }
            catch { return null; }
        }

        private static void SkipWs(string s, ref int i)
        {
            while (i < s.Length)
            {
                char c = s[i];
                if (c == ' ' || c == '\t' || c == '\n' || c == '\r') { i++; continue; }
                break;
            }
        }

        private static object ParseValue(string s, ref int i)
        {
            SkipWs(s, ref i);
            if (i >= s.Length) throw new FormatException("JSON 意外结束。");
            char c = s[i];
            switch (c)
            {
                case '{': return ParseObject(s, ref i);
                case '[': return ParseArray(s, ref i);
                case '"': return ParseString(s, ref i);
                case 't': Expect(s, ref i, "true"); return true;
                case 'f': Expect(s, ref i, "false"); return false;
                case 'n': Expect(s, ref i, "null"); return null;
                default: return ParseNumber(s, ref i);
            }
        }

        private static void Expect(string s, ref int i, string word)
        {
            if (i + word.Length > s.Length || string.CompareOrdinal(s, i, word, 0, word.Length) != 0)
                throw new FormatException("JSON 期望 '" + word + "'（位置 " + i + "）。");
            i += word.Length;
        }

        private static Dictionary<string, object> ParseObject(string s, ref int i)
        {
            var map = new Dictionary<string, object>(StringComparer.Ordinal);
            i++; // {
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return map; }
            while (true)
            {
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != '"') throw new FormatException("JSON 对象缺少键（位置 " + i + "）。");
                string key = ParseString(s, ref i);
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != ':') throw new FormatException("JSON 对象缺少 ':'（位置 " + i + "）。");
                i++;
                map[key] = ParseValue(s, ref i);
                SkipWs(s, ref i);
                if (i >= s.Length) throw new FormatException("JSON 对象未闭合。");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == '}') { i++; return map; }
                throw new FormatException("JSON 对象期望 ',' 或 '}'（位置 " + i + "）。");
            }
        }

        private static List<object> ParseArray(string s, ref int i)
        {
            var list = new List<object>();
            i++; // [
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return list; }
            while (true)
            {
                list.Add(ParseValue(s, ref i));
                SkipWs(s, ref i);
                if (i >= s.Length) throw new FormatException("JSON 数组未闭合。");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == ']') { i++; return list; }
                throw new FormatException("JSON 数组期望 ',' 或 ']'（位置 " + i + "）。");
            }
        }

        private static string ParseString(string s, ref int i)
        {
            i++; // opening quote
            var sb = new StringBuilder();
            while (true)
            {
                if (i >= s.Length) throw new FormatException("JSON 字符串未闭合。");
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= s.Length) throw new FormatException("JSON 转义未完成。");
                char e = s[i++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (i + 4 > s.Length) throw new FormatException("JSON \\u 转义不完整。");
                        sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        i += 4;
                        break;
                    default: throw new FormatException("JSON 未知转义 '\\" + e + "'。");
                }
            }
        }

        private static object ParseNumber(string s, ref int i)
        {
            int start = i;
            if (i < s.Length && (s[i] == '-' || s[i] == '+')) i++;
            while (i < s.Length && ((s[i] >= '0' && s[i] <= '9') || s[i] == '.' || s[i] == 'e' || s[i] == 'E' || s[i] == '-' || s[i] == '+')) i++;
            string num = s.Substring(start, i - start);
            double d;
            if (!double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                throw new FormatException("JSON 数字非法: '" + num + "'。");
            return d;
        }
    }

    /// <summary>Dictionary&lt;string,object&gt; 的便捷只读包装。</summary>
    internal sealed class JsonObj
    {
        private static readonly Dictionary<string, object> Empty = new Dictionary<string, object>(StringComparer.Ordinal);
        private readonly Dictionary<string, object> _map;

        public JsonObj(Dictionary<string, object> map)
        {
            _map = map ?? Empty;
        }

        public static JsonObj From(object o)
        {
            return new JsonObj(o as Dictionary<string, object>);
        }

        public static JsonObj Parse(string text)
        {
            return From(Json.ParseOrNull(text));
        }

        public bool Has(string key)
        {
            return _map.ContainsKey(key) && _map[key] != null;
        }

        /// <summary>本对象的全部键（保持 Dictionary 的枚举顺序）。</summary>
        public IEnumerable<string> Keys()
        {
            return _map.Keys;
        }

        public object Raw(string key)
        {
            object v;
            return _map.TryGetValue(key, out v) ? v : null;
        }

        public string Str(string key, string def = "")
        {
            object v = Raw(key);
            if (v == null) return def;
            return v as string ?? Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        public int Int(string key, int def = 0)
        {
            object v = Raw(key);
            if (v == null) return def;
            if (v is double) return (int)(double)v;
            int n;
            return int.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), out n) ? n : def;
        }

        public long Long(string key, long def = 0)
        {
            object v = Raw(key);
            if (v == null) return def;
            if (v is double) return (long)(double)v;
            long n;
            return long.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), out n) ? n : def;
        }

        public double Dbl(string key, double def = 0)
        {
            object v = Raw(key);
            if (v == null) return def;
            if (v is double) return (double)v;
            double n;
            return double.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out n) ? n : def;
        }

        public bool Bool(string key, bool def = false)
        {
            object v = Raw(key);
            if (v == null) return def;
            if (v is bool) return (bool)v;
            bool b;
            return bool.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), out b) ? b : def;
        }

        public JsonObj Obj(string key)
        {
            return new JsonObj(Raw(key) as Dictionary<string, object>);
        }

        public List<object> Arr(string key)
        {
            return Raw(key) as List<object> ?? new List<object>();
        }

        public string[] StrArray(string key)
        {
            var list = Arr(key);
            var result = new string[list.Count];
            for (int i = 0; i < list.Count; i++)
                result[i] = Convert.ToString(list[i], CultureInfo.InvariantCulture);
            return result;
        }
    }
}
