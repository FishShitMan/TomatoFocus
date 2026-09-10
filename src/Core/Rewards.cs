using System;
using System.Collections.Generic;

namespace TomatoFocus.Core
{
    internal sealed class RewardDef
    {
        public string Id = "";
        public string Category = "";     // title / medal / theme / effect / sound
        public int Cost;                 // 需要的“完整番茄”数量
        public string Icon = "trophy";
        public string ThemeId = "";      // 仅主题类使用
        public string NameKey { get { return "rw." + Id; } }
        public string DescKey { get { return "rw." + Id + ".desc"; } }
    }

    /// <summary>奖励兑换：只消耗“完整番茄”，碎片不可兑换。</summary>
    internal static class Rewards
    {
        private static readonly List<RewardDef> Defs = Build();
        public static List<RewardDef> All { get { return Defs; } }

        /// <summary>奖励页分类的展示顺序。</summary>
        public static readonly string[] CategoryOrder = { "title", "medal", "effect", "sound", "theme" };

        private static RewardDef R(string id, string cat, int cost, string icon, string themeId = "")
        {
            var d = new RewardDef();
            d.Id = id; d.Category = cat; d.Cost = cost; d.Icon = icon; d.ThemeId = themeId;
            return d;
        }

        private static List<RewardDef> Build()
        {
            var l = new List<RewardDef>();
            l.Add(R("t_novice", "title", 5, "leaf"));
            l.Add(R("t_farmer", "title", 15, "leaf"));
            l.Add(R("t_landlord", "title", 30, "tomato"));
            l.Add(R("t_artisan", "title", 50, "gear"));
            l.Add(R("t_collector", "title", 80, "sun"));

            l.Add(R("m_bronze", "medal", 10, "trophy"));
            l.Add(R("m_silver", "medal", 25, "trophy"));
            l.Add(R("m_gold", "medal", 50, "trophy"));
            l.Add(R("m_rainbow", "medal", 80, "trophy"));

            l.Add(R("th_fresh", "theme", 0, "sun", "fresh"));
            l.Add(R("th_sakura", "theme", 15, "sun", "sakura"));
            l.Add(R("th_ocean", "theme", 15, "sun", "ocean"));
            l.Add(R("th_night", "theme", 25, "sun", "night"));

            l.Add(R("ef_confetti", "effect", 10, "gift"));
            l.Add(R("ef_star", "effect", 15, "sun"));
            l.Add(R("ef_tomato", "effect", 20, "tomato"));

            l.Add(R("sn_default", "sound", 0, "leaf"));
            l.Add(R("sn_chime", "sound", 10, "leaf"));
            l.Add(R("sn_soft", "sound", 10, "leaf"));
            return l;
        }

        public static List<RewardDef> ByCategory(string cat)
        {
            var list = new List<RewardDef>();
            foreach (var d in Defs) if (d.Category == cat) list.Add(d);
            return list;
        }

        public static RewardDef ById(string id)
        {
            foreach (var d in Defs) if (d.Id == id) return d;
            return null;
        }

        /// <summary>按主题 id 找对应的主题奖励。</summary>
        public static RewardDef ByThemeId(string themeId)
        {
            foreach (var d in Defs)
                if (d.Category == "theme" && string.Equals(d.ThemeId, themeId, StringComparison.Ordinal)) return d;
            return null;
        }

        /// <summary>兑换。只消耗完整番茄；碎片不参与。</summary>
        public static bool Redeem(AppData data, string id, out string error)
        {
            error = "";
            var def = ById(id);
            if (def == null) { error = "unknown reward"; return false; }
            if (data.Rewards.IsOwned(id)) { error = I18n.T("reward.owned"); return false; }
            if (!data.Wallet.CanAfford(def.Cost)) { error = I18n.T("reward.insufficient"); return false; }
            data.Wallet.Spend(def.Cost);
            data.Rewards.Owned.Add(id);
            Equip(data, def);
            return true;
        }

        public static void Equip(AppData data, RewardDef def)
        {
            switch (def.Category)
            {
                case "title": data.Rewards.EquippedTitle = def.Id; break;
                case "medal": data.Rewards.EquippedMedal = def.Id; break;
                case "theme":
                    data.Rewards.EquippedTheme = def.Id;
                    data.Settings.ThemeId = string.IsNullOrEmpty(def.ThemeId) ? "fresh" : def.ThemeId;
                    break;
                case "effect": data.Rewards.EquippedEffect = def.Id; break;
                case "sound": data.Rewards.EquippedSound = def.Id; break;
            }
        }

        public static void Unequip(AppData data, RewardDef def)
        {
            switch (def.Category)
            {
                case "title": data.Rewards.EquippedTitle = ""; break;
                case "medal": data.Rewards.EquippedMedal = ""; break;
                case "theme":
                    data.Rewards.EquippedTheme = "";
                    data.Settings.ThemeId = "fresh";
                    break;
                case "effect": data.Rewards.EquippedEffect = ""; break;
                case "sound": data.Rewards.EquippedSound = ""; break;
            }
        }

        public static bool IsEquipped(AppData data, RewardDef def)
        {
            switch (def.Category)
            {
                case "title": return data.Rewards.EquippedTitle == def.Id;
                case "medal": return data.Rewards.EquippedMedal == def.Id;
                case "theme": return data.Rewards.EquippedTheme == def.Id;
                case "effect": return data.Rewards.EquippedEffect == def.Id;
                case "sound": return data.Rewards.EquippedSound == def.Id;
                default: return false;
            }
        }

        public static string EquippedName(AppData data)
        {
            if (string.IsNullOrEmpty(data.Rewards.EquippedTitle)) return "";
            return I18n.T("rw." + data.Rewards.EquippedTitle);
        }

        /// <summary>当前装备的提示音对应的音色 id。</summary>
        public static string SoundIdFor(AppData data)
        {
            switch (data.Rewards.EquippedSound)
            {
                case "sn_soft": return "soft";
                case "sn_chime": return "chime";
                default: return "chime";
            }
        }

        /// <summary>默认项（成本 0）：默认拥有、默认使用。</summary>
        public static bool IsDefault(string id)
        {
            return id == "th_fresh" || id == "sn_default";
        }

        /// <summary>当前"可兑换但尚未拥有"的数量（汉堡红点判定口径）。</summary>
        public static int AffordableUnowned(AppData data)
        {
            int n = 0;
            foreach (var d in Defs)
                if (!data.Rewards.IsOwned(d.Id) && data.Wallet.CanAfford(d.Cost)) n++;
            return n;
        }
    }
}
