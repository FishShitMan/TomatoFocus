using System;
using System.Collections.Generic;

namespace TomatoFocus.Core
{
    /// <summary>
    /// 任务系统的纯逻辑（无副作用、可单测）：奖励折算、轮次编排、前台进程匹配、结算口径。
    /// </summary>
    internal static class TaskMath
    {
        /// <summary>额外奖励比例：结算格数的 20%。</summary>
        public const double BonusRatio = 0.20;

        /// <summary>声明"我已提前完成"的最低进度门槛（目标的 30%）。</summary>
        public const double EarlyClaimMinRatio = 0.30;

        /// <summary>
        /// 额外奖励格数 = 基础结算格数 × 20%，**四舍五入**。
        /// 不用向上取整：短任务向上取整会被放大到 +50%（2 格→1 格），
        /// 四舍五入只在 .6 / .8 这类尾数上差 1 格，整体更贴近 20%。
        /// </summary>
        public static int BonusTenths(int baseTenths)
        {
            if (baseTenths <= 0) return 0;
            return (int)Math.Round(baseTenths * BonusRatio, MidpointRounding.AwayFromZero);
        }

        /// <summary>基础结算格数（按实际专注秒数与是否中断，复用既有换算口径）。</summary>
        public static int BaseTenths(int focusedSeconds, bool aborted)
        {
            return TomatoMath.TenthsFor(focusedSeconds, aborted);
        }

        /// <summary>
        /// 轮次编排：把总目标按"单轮专注"拆成若干轮，末轮取余量。
        /// 返回各专注轮的时长（秒），不含休息。
        /// </summary>
        public static List<int> BuildRounds(int totalSeconds, int roundSeconds)
        {
            var list = new List<int>();
            if (totalSeconds <= 0) return list;
            if (roundSeconds <= 0) roundSeconds = totalSeconds;
            int left = totalSeconds;
            while (left > 0)
            {
                int take = left > roundSeconds ? roundSeconds : left;
                list.Add(take);
                left -= take;
                if (list.Count > 1000) break;      // 安全网，防止参数异常时死循环
            }
            return list;
        }

        /// <summary>轮次数量（不分配列表）。</summary>
        public static int RoundCount(int totalSeconds, int roundSeconds)
        {
            if (totalSeconds <= 0) return 0;
            if (roundSeconds <= 0) return 1;
            return (totalSeconds + roundSeconds - 1) / roundSeconds;
        }

        /// <summary>进度比例 0..1。</summary>
        public static float ProgressRatio(int accumulatedSec, int totalSec)
        {
            if (totalSec <= 0) return 1f;
            float p = accumulatedSec / (float)totalSec;
            if (p < 0f) return 0f;
            return p > 1f ? 1f : p;
        }

        /// <summary>是否达到"我已提前完成"的门槛（目标进度的 30%）。</summary>
        public static bool CanClaimEarly(int accumulatedSec, int totalSec)
        {
            if (totalSec <= 0) return false;
            if (accumulatedSec <= 0) return false;
            return accumulatedSec >= totalSec * EarlyClaimMinRatio;
        }

        /// <summary>
        /// 前台进程是否就是任务目标。
        /// **进程名为主**：目标程序重启后 pid 会变，靠名字才能继续；
        /// **pid 为辅**：抓取时那个进程仍在跑就直接算命中（同名多实例时优先它）。
        /// </summary>
        public static bool MatchProcess(string targetName, int targetPid, string fgName, int fgPid)
        {
            if (targetPid > 0 && fgPid > 0 && targetPid == fgPid) return true;
            string a = Normalize(targetName);
            string b = Normalize(fgName);
            if (a.Length == 0 || b.Length == 0) return false;
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>进程名规整：去目录、去 .exe、去首尾空白。</summary>
        public static string Normalize(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            string s = name.Trim();
            int slash = s.LastIndexOfAny(new[] { '\\', '/' });
            if (slash >= 0 && slash + 1 < s.Length) s = s.Substring(slash + 1);
            if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) s = s.Substring(0, s.Length - 4);
            return s.Trim();
        }

        /// <summary>任务结算出口。</summary>
        internal enum TaskOutcome
        {
            /// <summary>累计达标且未声明提前完成 → 全额 + 额外奖励。</summary>
            Completed,
            /// <summary>用户声明"我已提前完成" → 全额、无额外奖励。</summary>
            ClaimedEarly,
            /// <summary>主动结束 → 半收益、无额外奖励。</summary>
            Abandoned
        }

        /// <summary>该出口是否按"中断"（半收益）结算。</summary>
        public static bool IsAborted(TaskOutcome outcome)
        {
            return outcome == TaskOutcome.Abandoned;
        }

        /// <summary>该出口是否给额外奖励。</summary>
        public static bool HasBonus(TaskOutcome outcome)
        {
            return outcome == TaskOutcome.Completed;
        }
    }
}
