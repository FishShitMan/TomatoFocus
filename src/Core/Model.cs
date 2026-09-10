using System;
using System.Collections.Generic;
using System.Globalization;

namespace TomatoFocus.Core
{
    /// <summary>用户设置。</summary>
    internal sealed class Settings
    {
        public string Lang = I18n.DefaultLang;
        public string ThemeId = "fresh";
        public bool MinimalMode = false;
        public bool AutoStart = false;          // 默认关闭
        public bool CloseToTray = true;
        public bool HideCountdown = false;
        public bool Sound = true;
        public int BreakSeconds = 300;          // 默认 5 分钟休息
        public List<int> CustomPresets = new List<int>();
        public int CustomMinutes = 0;           // 自定档位记忆值（0 = 未设定）
        public int BreakCustomMinutes = 0;      // 休息模式的自定值（0 = 未设定）
        public int LastPresetMinutes = 0;       // 上次选中的档位分钟数（0 = 未记录）
        public string LastPresetId = "";        // 上次选中的档位标识
        public string NoteDraft = "";           // 笔记草稿：结算时写入该次会话
        public int RewardBadgeSeen = 0;         // 已读的"可兑换奖励"数量（红点用）
        public bool ShowYearView = false;
    }

    /// <summary>一次专注会话的如实记录。</summary>
    internal sealed class SessionRecord
    {
        public string Id = "";
        public string StartedAt = "";           // yyyy-MM-dd HH:mm:ss（本地时间）
        public int PlannedSec;                  // 计划时长
        public int FocusedSec;                  // 实际专注秒数（不含暂停）
        public int Tenths;                      // 结算格数（中断已减半）
        public string Preset = "25";            // 5 / 15 / 25 / custom
        public bool Aborted;                    // 是否提前中断
        public string Note = "";                // 本次番茄钟内的笔记

        public DateTime StartedLocal
        {
            get
            {
                DateTime dt;
                return DateTime.TryParseExact(StartedAt, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out dt) ? dt : DateTime.MinValue;
            }
        }
    }

    /// <summary>按日聚合缓存。</summary>
    internal sealed class DayStat
    {
        public int Tenths;
        public int FocusedSec;
        public int Sessions;
        public int AbortedCount;

        public void Add(SessionRecord s)
        {
            Tenths += s.Tenths;
            FocusedSec += s.FocusedSec;
            Sessions += 1;
            if (s.Aborted) AbortedCount += 1;
        }

        public void Remove(SessionRecord s)
        {
            Tenths -= s.Tenths;
            FocusedSec -= s.FocusedSec;
            Sessions -= 1;
            if (s.Aborted) AbortedCount -= 1;
            if (Sessions < 0) Sessions = 0;
            if (Tenths < 0) Tenths = 0;
            if (FocusedSec < 0) FocusedSec = 0;
            if (AbortedCount < 0) AbortedCount = 0;
        }
    }

    /// <summary>
    /// 钱包。完整颗才是可兑换货币；碎片只是累计中的余数，
    /// 攒满 10 格（1.0 颗）会自动体现为完整颗 +1（“组合为整数时恢复正常功能”）。
    /// </summary>
    internal sealed class Wallet
    {
        public int TotalTenths;                 // 累计获得（含碎片）
        public int SpentWhole;                  // 已兑换的完整颗

        public int UsableWhole { get { return TomatoMath.Whole(TotalTenths) - SpentWhole; } }
        public int FragmentTenths { get { return TomatoMath.Fragment(TotalTenths); } }
        public int EarnedWhole { get { return TomatoMath.Whole(TotalTenths); } }

        /// <summary>可用格数（含不足 1 颗的部分），用于「可用 x.x 颗」展示。</summary>
        public int UsableTenths
        {
            get
            {
                int t = TotalTenths - SpentWhole * TomatoMath.TenthsPerTomato;
                return t < 0 ? 0 : t;
            }
        }

        public bool CanAfford(int wholeCost) { return wholeCost <= UsableWhole; }

        public bool Spend(int wholeCost)
        {
            if (!CanAfford(wholeCost)) return false;
            SpentWhole += wholeCost;
            return true;
        }
    }

    internal sealed class AchievementState
    {
        public string Id = "";
        public bool Unlocked;
        public string UnlockedAt = "";
        public int Progress;
        public int Target;
    }

    internal sealed class RewardState
    {
        public List<string> Owned = new List<string>();
        public string EquippedTitle = "";
        public string EquippedTheme = "";
        public string EquippedEffect = "";
        public string EquippedMedal = "";
        public string EquippedSound = "";
        public bool IsOwned(string id) { return Owned.Contains(id); }
    }

    /// <summary>全部持久化数据。</summary>
    internal sealed class AppData
    {
        public const int CurrentSchema = 1;

        public int Schema = CurrentSchema;
        public Settings Settings = new Settings();
        public Wallet Wallet = new Wallet();
        public List<SessionRecord> Sessions = new List<SessionRecord>();
        public Dictionary<string, DayStat> Days = new Dictionary<string, DayStat>(StringComparer.Ordinal);
        public Dictionary<string, AchievementState> Achievements = new Dictionary<string, AchievementState>(StringComparer.Ordinal);
        public RewardState Rewards = new RewardState();

        /// <summary>通用计数器（起身活动次数等），避免为每个新统计改 schema。</summary>
        public Dictionary<string, int> Counters = new Dictionary<string, int>(StringComparer.Ordinal);

        public int Counter(string key)
        {
            int v;
            return Counters.TryGetValue(key, out v) ? v : 0;
        }

        public void Bump(string key, int delta = 1)
        {
            Counters[key] = Counter(key) + delta;
        }
    }

    internal static class DayKey
    {
        public static string Of(DateTime local) { return local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); }
        public static string Today { get { return Of(DateTime.Now); } }
        public static DateTime Parse(string key)
        {
            DateTime dt;
            return DateTime.TryParseExact(key, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out dt)
                ? dt : DateTime.MinValue;
        }
        public static string Stamp(DateTime local) { return local.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture); }
    }
}
