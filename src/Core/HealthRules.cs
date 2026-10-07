using System;
using System.Collections.Generic;

namespace TomatoFocus.Core
{
    /// <summary>分时段温馨问候的时段（None 表示当前不属于任何问候时段）。</summary>
    internal enum GreetingPeriod
    {
        None,
        Morning,
        Noon,
        Afternoon,
        Night
    }

    /// <summary>健康关怀规则（依据 WHO / 健康中国行动 / 中国居民膳食指南 / AOA，见 I18n 的 health.* 文案）。</summary>
    internal static class HealthRules
    {
        /// <summary>每 4 颗完整番茄建议一次长休息（番茄工作法原版节奏）。</summary>
        public const int PomodorosPerLongBreak = 4;

        /// <summary>连续专注超过 90 分钟，建议长休息（WHO：限制久坐时间）。</summary>
        public const int LongSitSeconds = 90 * 60;

        /// <summary>单日超过 12 颗（5 小时）给出温和的收工提示。</summary>
        public const int DailyCapTenths = 120;

        /// <summary>22:30 之后给出温和的收工提示。</summary>
        public static readonly TimeSpan NightStart = new TimeSpan(22, 30, 0);

        /// <summary>两次喝水提醒之间至少间隔 60 分钟（膳食指南：每 1–2 小时一杯）。</summary>
        public const int WaterIntervalSeconds = 60 * 60;

        /// <summary>护眼提醒间隔（AOA 20-20-20）。</summary>
        public const int EyeIntervalSeconds = 20 * 60;

        private static readonly Random Rng = new Random(Environment.TickCount);

        /// <summary>随机取一条关怀语句，尽量避免与上一条重复。</summary>
        public static string NextPhrase(ref int lastIndex)
        {
            var phrases = I18n.List("phrases");
            if (phrases == null || phrases.Length == 0) return I18n.T("break.title");
            if (phrases.Length == 1) { lastIndex = 0; return phrases[0]; }
            int idx = Rng.Next(phrases.Length);
            if (idx == lastIndex) idx = (idx + 1 + Rng.Next(phrases.Length - 1)) % phrases.Length;
            lastIndex = idx;
            return phrases[idx];
        }

        public static bool IsNight(DateTime now) { return now.TimeOfDay >= NightStart; }

        public static bool IsEarlyMorning(DateTime now) { return now.Hour < 9; }

        public static bool IsLateEvening(DateTime now) { return now.Hour >= 22; }

        // --- 分时段温馨问候的时间窗（本地时间）-------------------------------
        // 时段表集中在这里：以后要加"傍晚"之类，只改这一处即可。
        public static readonly TimeSpan MorningStart = new TimeSpan(5, 0, 0);
        public static readonly TimeSpan MorningEnd = new TimeSpan(9, 0, 0);
        public static readonly TimeSpan NoonStart = new TimeSpan(11, 30, 0);
        public static readonly TimeSpan NoonEnd = new TimeSpan(13, 30, 0);
        public static readonly TimeSpan AfternoonStart = new TimeSpan(14, 0, 0);
        public static readonly TimeSpan AfternoonEnd = new TimeSpan(17, 30, 0);
        // 夜深 = [22:30, 24:00) ∪ [00:00, 05:00)，即 NightStart 起、到 MorningStart 前
        public static readonly TimeSpan NightEnd = MorningStart;

        /// <summary>取当前时段；不在任何问候时段内返回 None。</summary>
        public static GreetingPeriod PeriodOf(DateTime now)
        {
            var t = now.TimeOfDay;
            if (t >= NightStart || t < NightEnd) return GreetingPeriod.Night;
            if (t >= MorningStart && t < MorningEnd) return GreetingPeriod.Morning;
            if (t >= NoonStart && t < NoonEnd) return GreetingPeriod.Noon;
            if (t >= AfternoonStart && t < AfternoonEnd) return GreetingPeriod.Afternoon;
            return GreetingPeriod.None;
        }

        /// <summary>时段标识（用于文案键与"今天是否已问候"的记录）。</summary>
        public static string PeriodKey(GreetingPeriod p)
        {
            switch (p)
            {
                case GreetingPeriod.Morning: return "morning";
                case GreetingPeriod.Noon: return "noon";
                case GreetingPeriod.Afternoon: return "afternoon";
                case GreetingPeriod.Night: return "night";
                default: return "";
            }
        }
    }
}
