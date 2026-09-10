using System;
using System.Collections.Generic;

namespace TomatoFocus.Core
{
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
    }
}
