using System;
using System.Collections.Generic;
using System.Globalization;

namespace TomatoFocus.Core
{
    /// <summary>统计与聚合（全部基于会话明细，可随时重建）。</summary>
    internal static class Stats
    {
        public static int TodayTenths(AppData d)
        {
            DayStat st;
            return d.Days.TryGetValue(DayKey.Today, out st) ? st.Tenths : 0;
        }

        public static DayStat DayOf(AppData d, string key)
        {
            DayStat st;
            return d.Days.TryGetValue(key, out st) ? st : null;
        }

        public static List<SessionRecord> SessionsOf(AppData d, string dayKey)
        {
            var list = new List<SessionRecord>();
            foreach (var rec in d.Sessions)
                if (DayKey.Of(rec.StartedLocal) == dayKey) list.Add(rec);
            list.Sort(delegate (SessionRecord a, SessionRecord b)
            {
                return string.CompareOrdinal(a.StartedAt, b.StartedAt);
            });
            return list;
        }

        public static int TotalTenths(AppData d) { return d.Wallet.TotalTenths; }
        public static int TotalSessions(AppData d) { return d.Sessions.Count; }

        public static int TotalFocusedSeconds(AppData d)
        {
            int sum = 0;
            foreach (var rec in d.Sessions) sum += rec.FocusedSec;
            return sum;
        }

        /// <summary>单日最高（返回天数格数；没有记录时返回 0）。</summary>
        public static int BestDayTenths(AppData d, out string bestDayKey)
        {
            int best = 0;
            bestDayKey = "";
            foreach (var kv in d.Days)
            {
                if (kv.Value.Tenths > best || (kv.Value.Tenths == best && best > 0 && string.CompareOrdinal(kv.Key, bestDayKey) < 0))
                {
                    best = kv.Value.Tenths;
                    bestDayKey = kv.Key;
                }
            }
            return best;
        }

        /// <summary>单次最长专注（秒）。</summary>
        public static int LongestSessionSeconds(AppData d)
        {
            int best = 0;
            foreach (var rec in d.Sessions) if (rec.FocusedSec > best) best = rec.FocusedSec;
            return best;
        }

        /// <summary>完整完成（未中断）且达到标准时长的会话数。</summary>
        public static int CleanPomodoros(AppData d)
        {
            int n = 0;
            foreach (var rec in d.Sessions)
                if (!rec.Aborted && rec.FocusedSec >= TomatoMath.StandardSeconds) n++;
            return n;
        }

        /// <summary>中断会话数。</summary>
        public static int AbortedCount(AppData d)
        {
            int n = 0;
            foreach (var rec in d.Sessions) if (rec.Aborted) n++;
            return n;
        }

        /// <summary>连续天数：以今天或昨天为终点，向前数有记录的天数。</summary>
        public static int Streak(AppData d, DateTime today)
        {
            int streak = 0;
            DateTime cursor = today.Date;
            if (!HasRecord(d, cursor)) cursor = cursor.AddDays(-1);
            while (HasRecord(d, cursor))
            {
                streak++;
                cursor = cursor.AddDays(-1);
            }
            return streak;
        }

        public static int LongestStreak(AppData d)
        {
            var keys = new List<string>(d.Days.Keys);
            keys.Sort(StringComparer.Ordinal);
            int best = 0, run = 0;
            DateTime prev = DateTime.MinValue;
            foreach (string k in keys)
            {
                var dt = DayKey.Parse(k);
                if (dt == DateTime.MinValue) continue;
                if (d.Days[k].Tenths <= 0) continue;
                if (prev != DateTime.MinValue && (dt - prev).TotalDays == 1) run++;
                else run = 1;
                if (run > best) best = run;
                prev = dt;
            }
            return best;
        }

        private static bool HasRecord(AppData d, DateTime day)
        {
            DayStat st;
            return d.Days.TryGetValue(DayKey.Of(day), out st) && st.Tenths > 0;
        }

        /// <summary>本月总格数。</summary>
        public static int MonthTenths(AppData d, int year, int month)
        {
            int sum = 0;
            int days = DateTime.DaysInMonth(year, month);
            for (int i = 1; i <= days; i++)
            {
                DayStat st;
                if (d.Days.TryGetValue(DayKey.Of(new DateTime(year, month, i)), out st)) sum += st.Tenths;
            }
            return sum;
        }

        /// <summary>某年总格数。</summary>
        public static int YearTenths(AppData d, int year)
        {
            int sum = 0;
            string prefix = year.ToString(CultureInfo.InvariantCulture) + "-";
            foreach (var kv in d.Days)
                if (kv.Key.StartsWith(prefix, StringComparison.Ordinal)) sum += kv.Value.Tenths;
            return sum;
        }

        /// <summary>本周（周一起）总格数。</summary>
        public static int WeekTenths(AppData d, DateTime today)
        {
            int diff = ((int)today.DayOfWeek + 6) % 7;   // 周一 = 0
            DateTime monday = today.Date.AddDays(-diff);
            int sum = 0;
            for (int i = 0; i < 7; i++)
            {
                DayStat st;
                if (d.Days.TryGetValue(DayKey.Of(monday.AddDays(i)), out st)) sum += st.Tenths;
            }
            return sum;
        }

        /// <summary>首次记录时间（用于“陪伴天数”）。</summary>
        public static DateTime FirstRecordDate(AppData d)
        {
            DateTime best = DateTime.MaxValue;
            foreach (var rec in d.Sessions)
            {
                var dt = rec.StartedLocal;
                if (dt != DateTime.MinValue && dt < best) best = dt;
            }
            return best == DateTime.MaxValue ? DateTime.MinValue : best;
        }

        /// <summary>某会话的展示时长（mm:ss）。</summary>
        public static string FormatDuration(int seconds) { return TomatoMath.FormatClock(seconds); }

        /// <summary>由碎片拼合而成的完整番茄数量 = 完整颗总数 − 各会话直接产出的完整颗之和。</summary>
        public static int MergedWhole(AppData d)
        {
            int direct = 0;
            foreach (var rec in d.Sessions) direct += rec.Tenths / TomatoMath.TenthsPerTomato;
            int merged = TomatoMath.Whole(d.Wallet.TotalTenths) - direct;
            return merged < 0 ? 0 : merged;
        }

        /// <summary>早晨（9:00 前开始）完成的会话数。</summary>
        public static int CountEarly(AppData d)
        {
            int n = 0;
            foreach (var rec in d.Sessions)
            {
                var dt = rec.StartedLocal;
                if (dt != DateTime.MinValue && dt.Hour < 9) n++;
            }
            return n;
        }

        /// <summary>夜间（22:00 后开始）完成的会话数。</summary>
        public static int CountLate(AppData d)
        {
            int n = 0;
            foreach (var rec in d.Sessions)
            {
                var dt = rec.StartedLocal;
                if (dt != DateTime.MinValue && dt.Hour >= 22) n++;
            }
            return n;
        }
    }
}
