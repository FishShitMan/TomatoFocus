using System;
using System.Collections.Generic;

namespace TomatoFocus.Core
{
    internal sealed class AchievementDef
    {
        public string Id = "";
        public string NameKey = "";
        public string DescKey = "";
        public string Icon = "trophy";
        public int Target;
        public int Tier = 1;                        // 1 铜 2 银 3 金 4 彩
        public Func<AppData, int> Value = delegate { return 0; };
    }

    internal static class Achievements
    {
        private static readonly List<AchievementDef> Defs = Build();
        public static List<AchievementDef> All { get { return Defs; } }

        private static AchievementDef Def(string id, string name, string desc, string icon, int target, int tier, Func<AppData, int> value)
        {
            var d = new AchievementDef();
            d.Id = id; d.NameKey = name; d.DescKey = desc; d.Icon = icon;
            d.Target = target; d.Tier = tier; d.Value = value;
            return d;
        }

        private static List<AchievementDef> Build()
        {
            var list = new List<AchievementDef>();
            list.Add(Def("first", "ach.first.name", "ach.first.desc", "tomato", 1, 1,
                delegate (AppData d) { return d.Wallet.TotalTenths; }));
            list.Add(Def("total10", "ach.total10.name", "ach.total10.desc", "tomato", 10, 1,
                delegate (AppData d) { return TomatoMath.Whole(d.Wallet.TotalTenths); }));
            list.Add(Def("total50", "ach.total50.name", "ach.total50.desc", "tomato", 50, 2,
                delegate (AppData d) { return TomatoMath.Whole(d.Wallet.TotalTenths); }));
            list.Add(Def("total100", "ach.total100.name", "ach.total100.desc", "tomato", 100, 3,
                delegate (AppData d) { return TomatoMath.Whole(d.Wallet.TotalTenths); }));
            list.Add(Def("total500", "ach.total500.name", "ach.total500.desc", "tomato", 500, 4,
                delegate (AppData d) { return TomatoMath.Whole(d.Wallet.TotalTenths); }));

            list.Add(Def("day4", "ach.day4.name", "ach.day4.desc", "sun", 40, 1,
                delegate (AppData d) { string k; return Stats.BestDayTenths(d, out k); }));
            list.Add(Def("day8", "ach.day8.name", "ach.day8.desc", "sun", 80, 2,
                delegate (AppData d) { string k; return Stats.BestDayTenths(d, out k); }));
            list.Add(Def("day12", "ach.day12.name", "ach.day12.desc", "sun", 120, 3,
                delegate (AppData d) { string k; return Stats.BestDayTenths(d, out k); }));

            list.Add(Def("streak3", "ach.streak3.name", "ach.streak3.desc", "leaf", 3, 1,
                delegate (AppData d) { return Stats.LongestStreak(d); }));
            list.Add(Def("streak7", "ach.streak7.name", "ach.streak7.desc", "leaf", 7, 2,
                delegate (AppData d) { return Stats.LongestStreak(d); }));
            list.Add(Def("streak30", "ach.streak30.name", "ach.streak30.desc", "leaf", 30, 3,
                delegate (AppData d) { return Stats.LongestStreak(d); }));

            list.Add(Def("long25", "ach.long25.name", "ach.long25.desc", "person", 25 * 60, 1,
                delegate (AppData d) { return Stats.LongestSessionSeconds(d); }));
            list.Add(Def("long50", "ach.long50.name", "ach.long50.desc", "person", 50 * 60, 2,
                delegate (AppData d) { return Stats.LongestSessionSeconds(d); }));
            list.Add(Def("long90", "ach.long90.name", "ach.long90.desc", "person", 90 * 60, 3,
                delegate (AppData d) { return Stats.LongestSessionSeconds(d); }));

            list.Add(Def("clean10", "ach.clean10.name", "ach.clean10.desc", "check", 10, 2,
                delegate (AppData d) { return Stats.CleanPomodoros(d); }));
            list.Add(Def("merged10", "ach.merged10.name", "ach.merged10.desc", "tomato", 10, 2,
                delegate (AppData d) { return Stats.MergedWhole(d); }));

            list.Add(Def("early", "ach.early.name", "ach.early.desc", "sun", 1, 1,
                delegate (AppData d) { return Stats.CountEarly(d); }));
            list.Add(Def("night", "ach.night.name", "ach.night.desc", "leaf", 1, 1,
                delegate (AppData d) { return Stats.CountLate(d); }));

            list.Add(Def("break20", "ach.break20.name", "ach.break20.desc", "person", 20, 2,
                delegate (AppData d) { return d.Counter("breaksTaken"); }));
            list.Add(Def("week20", "ach.week20.name", "ach.week20.desc", "trophy", 20, 3,
                delegate (AppData d) { return TomatoMath.Whole(Stats.WeekTenths(d, DateTime.Now)); }));
            return list;
        }

        public static AchievementState State(AppData d, string id)
        {
            AchievementState st;
            if (!d.Achievements.TryGetValue(id, out st))
            {
                st = new AchievementState();
                st.Id = id;
                d.Achievements[id] = st;
            }
            return st;
        }

        /// <summary>评估全部成就，返回本次新解锁的成就。</summary>
        public static List<AchievementDef> Evaluate(AppData d, DateTime now)
        {
            var unlocked = new List<AchievementDef>();
            foreach (var def in Defs)
            {
                var st = State(d, def.Id);
                int value = 0;
                try { value = def.Value(d); }
                catch { value = 0; }
                st.Target = def.Target;
                st.Progress = value > def.Target ? def.Target : value;
                if (!st.Unlocked && value >= def.Target)
                {
                    st.Unlocked = true;
                    st.UnlockedAt = DayKey.Stamp(now);
                    unlocked.Add(def);
                }
            }
            return unlocked;
        }

        public static int UnlockedCount(AppData d)
        {
            int n = 0;
            foreach (var def in Defs)
            {
                AchievementState st;
                if (d.Achievements.TryGetValue(def.Id, out st) && st.Unlocked) n++;
            }
            return n;
        }
    }
}
