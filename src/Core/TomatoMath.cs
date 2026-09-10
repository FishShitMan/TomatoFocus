using System;
using System.Globalization;

namespace TomatoFocus.Core
{
    /// <summary>
    /// 番茄计量核心。全部使用整数“格”(tenths) 运算，避免任何浮点误差。
    ///
    ///   1 颗番茄 = 10 格 ； 25 分钟 = 1500 秒 = 10 格  =&gt;  1 格 = 150 秒
    ///
    /// 规则：
    ///   正常完成    tenths = focusedSec / 150          （向下取整）
    ///   提前中断    tenths = focusedSec / 300          （收益减半；结果为 0 则不记录）
    ///
    /// 钱包：
    ///   完整颗(可兑换) = totalTenths / 10 - spentWhole
    ///   碎片(不可兑换) = totalTenths % 10               （攒满 10 格自动进位为 1 颗完整番茄）
    /// </summary>
    internal static class TomatoMath
    {
        /// <summary>一颗番茄的格数。</summary>
        public const int TenthsPerTomato = 10;

        /// <summary>标准番茄时长（秒）。</summary>
        public const int StandardSeconds = 25 * 60;

        /// <summary>正常完成时，每格对应的秒数（1500 / 10）。</summary>
        public const int SecondsPerTenth = StandardSeconds / TenthsPerTomato;

        /// <summary>提前中断时，每格对应的秒数（收益减半）。</summary>
        public const int SecondsPerTenthOnAbort = SecondsPerTenth * 2;

        /// <summary>
        /// 正常完成结算：向下取整到 0.1 颗，且**最低 0.1 颗**（跑满一轮不会颗粒无收）。
        /// </summary>
        public static int TenthsForCompleted(int focusedSeconds)
        {
            if (focusedSeconds <= 0) return 0;
            int tenths = focusedSeconds / SecondsPerTenth;
            return tenths < 1 ? 1 : tenths;
        }

        /// <summary>提前中断结算：收益减半，向下取整到 0.1 颗；不满 0.1 返回 0（不记录）。</summary>
        public static int TenthsForAborted(int focusedSeconds)
        {
            if (focusedSeconds <= 0) return 0;
            return focusedSeconds / SecondsPerTenthOnAbort;
        }

        /// <summary>按是否中断结算。</summary>
        public static int TenthsFor(int focusedSeconds, bool aborted)
        {
            return aborted ? TenthsForAborted(focusedSeconds) : TenthsForCompleted(focusedSeconds);
        }

        /// <summary>完整番茄数（可兑换货币）。</summary>
        public static int Whole(int totalTenths)
        {
            return totalTenths / TenthsPerTomato;
        }

        /// <summary>碎片格数（0..9），不可兑换。</summary>
        public static int Fragment(int totalTenths)
        {
            return totalTenths % TenthsPerTomato;
        }

        /// <summary>把分钟换算成格数（仅用于显示/预估，正常完成口径）。</summary>
        public static int TenthsForMinutes(int minutes)
        {
            return TenthsForCompleted(minutes * 60);
        }

        /// <summary>格式化为一位小数，如 "1.2"。</summary>
        public static string Format(int tenths)
        {
            if (tenths < 0) tenths = 0;
            return string.Concat(
                (tenths / TenthsPerTomato).ToString(CultureInfo.InvariantCulture),
                ".",
                (tenths % TenthsPerTomato).ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>格式化碎片，如 "0.4"。</summary>
        public static string FormatFragment(int fragmentTenths)
        {
            if (fragmentTenths < 0) fragmentTenths = 0;
            return string.Concat("0.", fragmentTenths.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>把秒格式化为 mm:ss 或 h:mm:ss。</summary>
        public static string FormatClock(int seconds)
        {
            if (seconds < 0) seconds = 0;
            int h = seconds / 3600;
            int m = (seconds % 3600) / 60;
            int s = seconds % 60;
            if (h > 0)
                return string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}", h, m, s);
            return string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}", m, s);
        }

        /// <summary>把分钟格式化为可读文本，如 "1 小时 25 分钟"。</summary>
        public static string FormatMinutes(int minutes)
        {
            if (minutes < 60) return minutes.ToString(CultureInfo.InvariantCulture) + " 分钟";
            int h = minutes / 60;
            int m = minutes % 60;
            return m == 0
                ? h.ToString(CultureInfo.InvariantCulture) + " 小时"
                : h.ToString(CultureInfo.InvariantCulture) + " 小时 " + m.ToString(CultureInfo.InvariantCulture) + " 分钟";
        }
    }
}
