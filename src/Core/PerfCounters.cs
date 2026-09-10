using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace TomatoFocus.Core
{
    /// <summary>
    /// 绘制分段计时（仅性能自检时开启）。用来回答"这一帧的 17ms 花在哪"，
    /// 关闭时每个分段只多一次布尔判断，对正常渲染无影响。
    /// </summary>
    internal static class PerfCounters
    {
        public static bool Enabled;

        private static readonly Dictionary<string, double> Totals = new Dictionary<string, double>(StringComparer.Ordinal);
        private static readonly Dictionary<string, int> Counts = new Dictionary<string, int>(StringComparer.Ordinal);
        private static double _allTotalMs;

        /// <summary>记录一个分段的耗时（startTimestamp 由 Stopwatch.GetTimestamp() 取得）。</summary>
        public static void Add(string name, long startTimestamp)
        {
            if (!Enabled) return;
            double ms = (Stopwatch.GetTimestamp() - startTimestamp) * 1000.0 / Stopwatch.Frequency;
            double cur;
            Totals.TryGetValue(name, out cur);
            Totals[name] = cur + ms;
            int n;
            Counts.TryGetValue(name, out n);
            Counts[name] = n + 1;
        }

        public static void AddMs(string name, double ms)
        {
            if (!Enabled) return;
            double cur;
            Totals.TryGetValue(name, out cur);
            Totals[name] = cur + ms;
            int n;
            Counts.TryGetValue(name, out n);
            Counts[name] = n + 1;
        }

        /// <summary>一帧的总绘制耗时（由 OnPaint 记录）。</summary>
        public static void AddFrame(double ms)
        {
            if (!Enabled) return;
            _allTotalMs += ms;
        }

        public static void Reset()
        {
            Totals.Clear();
            Counts.Clear();
            _allTotalMs = 0;
        }

        /// <summary>按累计耗时降序输出分段占比。</summary>
        public static string Dump()
        {
            if (Totals.Count == 0) return "";

            var list = new List<KeyValuePair<string, double>>(Totals);
            list.Sort(delegate (KeyValuePair<string, double> a, KeyValuePair<string, double> b) { return b.Value.CompareTo(a.Value); });

            var sb = new StringBuilder();
            sb.AppendLine("--- 绘制分段（按累计耗时降序）---");
            foreach (var kv in list)
            {
                double per = kv.Value / Math.Max(1, Counts[kv.Key]);
                double share = _allTotalMs > 0 ? kv.Value / _allTotalMs * 100.0 : 0;
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "  {0,-14} 累计={1,8:0.0}ms  单次={2,6:0.00}ms  次数={3,5}  占绘制={4,5:0.0}%",
                    kv.Key, kv.Value, per, Counts[kv.Key], share));
            }
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  {0,-14} 累计={1,8:0.0}ms", "（帧总绘制）", _allTotalMs));
            return sb.ToString();
        }
    }
}
