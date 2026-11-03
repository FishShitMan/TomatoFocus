using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace TomatoFocus.Core
{
    /// <summary>
    /// 数据持久化：%APPDATA%\TomatoFocus\data.json
    /// 原子写入（临时文件 + File.Replace），并保留 .bak 备份。
    /// 纯本地，不联网。
    /// </summary>
    internal static class Store
    {
        public static string LastError = "";

        public static AppData Load()
        {
            LastError = "";
            var data = TryRead(AppPaths.DataFile);
            if (data == null) data = TryRead(AppPaths.BackupFile);
            if (data == null) data = new AppData();
            Normalize(data);
            return data;
        }

        private static AppData TryRead(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                string text = File.ReadAllText(path, Encoding.UTF8);
                var root = JsonObj.Parse(text);
                if (!root.Has("schema")) return null;
                return FromJson(root);
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return null;
            }
        }

        public static bool Save(AppData data)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.DataDir);
                string text = ToJson(data);
                string tmp = AppPaths.DataFile + ".tmp";
                File.WriteAllText(tmp, text, new UTF8Encoding(false));

                bool replaced = false;
                if (File.Exists(AppPaths.DataFile))
                {
                    try
                    {
                        File.Replace(tmp, AppPaths.DataFile, AppPaths.BackupFile);
                        replaced = true;
                    }
                    catch (Exception) { replaced = false; }
                }

                if (!replaced)
                {
                    // 回退路径：显式备份 + 覆盖，保证 .bak 一定存在
                    try { if (File.Exists(AppPaths.DataFile)) File.Copy(AppPaths.DataFile, AppPaths.BackupFile, true); }
                    catch (Exception) { }
                    File.Copy(tmp, AppPaths.DataFile, true);
                    try { File.Delete(tmp); } catch (Exception) { }
                }

                LastError = "";
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return false;
            }
        }

        public static string ToJson(AppData d)
        {
            var w = new JsonWriter(16384);
            w.BeginObject();
            w.Name("schema").Value(d.Schema);

            w.Name("settings").BeginObject();
            var s = d.Settings;
            w.Name("lang").Value(s.Lang);
            w.Name("theme").Value(s.ThemeId);
            w.Name("minimalMode").Value(s.MinimalMode);
            w.Name("autoStart").Value(s.AutoStart);
            w.Name("closeToTray").Value(s.CloseToTray);
            w.Name("hideCountdown").Value(s.HideCountdown);
            w.Name("sound").Value(s.Sound);
            w.Name("breakSeconds").Value(s.BreakSeconds);
            w.Name("showYearView").Value(s.ShowYearView);
            w.Name("customMinutes").Value(s.CustomMinutes);
            w.Name("breakCustomMinutes").Value(s.BreakCustomMinutes);
            w.Name("lastPresetMinutes").Value(s.LastPresetMinutes);
            w.Name("lastPresetId").Value(s.LastPresetId ?? "");
            w.Name("noteDraft").Value(s.NoteDraft);
            w.Name("rewardBadgeSeen").Value(s.RewardBadgeSeen);
            w.Name("greetingOn").Value(s.GreetingOn);
            w.Name("lastGreetDay").Value(s.LastGreetDay ?? "");
            w.Name("lastGreetPeriod").Value(s.LastGreetPeriod ?? "");
            w.Name("customPresets").BeginArray();
            foreach (int p in s.CustomPresets) w.Value(p);
            w.EndArray();
            w.EndObject();

            w.Name("wallet").BeginObject();
            w.Name("totalTenths").Value(d.Wallet.TotalTenths);
            w.Name("spentWhole").Value(d.Wallet.SpentWhole);
            w.EndObject();

            w.Name("sessions").BeginArray();
            foreach (var rec in d.Sessions)
            {
                w.BeginObject();
                w.Name("id").Value(rec.Id);
                w.Name("startedAt").Value(rec.StartedAt);
                w.Name("plannedSec").Value(rec.PlannedSec);
                w.Name("focusedSec").Value(rec.FocusedSec);
                w.Name("tenths").Value(rec.Tenths);
                w.Name("bonusTenths").Value(rec.BonusTenths);
                w.Name("preset").Value(rec.Preset);
                w.Name("taskProcess").Value(rec.TaskProcess ?? "");
                w.Name("taskExe").Value(rec.TaskExe ?? "");
                w.Name("aborted").Value(rec.Aborted);
                w.Name("note").Value(rec.Note);
                w.EndObject();
            }
            w.EndArray();

            // 任务配置（记住上次选的程序与时长）
            w.Name("taskConfig").BeginObject();
            w.Name("process").Value(d.TaskConfig.ProcessName);
            w.Name("pid").Value(d.TaskConfig.Pid);
            w.Name("title").Value(d.TaskConfig.WindowTitle);
            w.Name("totalSec").Value(d.TaskConfig.TotalFocusSeconds);
            w.Name("roundSec").Value(d.TaskConfig.FocusRoundSeconds);
            w.Name("breakSec").Value(d.TaskConfig.BreakRoundSeconds);
            w.EndObject();

            // 进行中 / 待恢复的任务（没有任务时写一个空对象，process 为空即表示无）
            var q = d.CurrentTask;
            w.Name("currentTask").BeginObject();
            w.Name("process").Value(q == null ? "" : q.ProcessName);
            if (q != null)
            {
                w.Name("pid").Value(q.Pid);
                w.Name("title").Value(q.WindowTitle);
                w.Name("totalSec").Value(q.TotalFocusSeconds);
                w.Name("roundSec").Value(q.FocusRoundSeconds);
                w.Name("breakSec").Value(q.BreakRoundSeconds);
                w.Name("accum").Value(q.AccumulatedFocusSec);
                w.Name("away").Value(q.AwaySec);
                w.Name("round").Value(q.RoundIndex);
                w.Name("rounds").Value(q.RoundsTotal);
                w.Name("status").Value(q.Status);
                w.Name("startedAt").Value(q.StartedAt);
                w.Name("claimedEarly").Value(q.ClaimedEarly);
            }
            w.EndObject();

            w.Name("days").BeginObject();
            foreach (var kv in d.Days)
            {
                w.Name(kv.Key).BeginObject();
                w.Name("tenths").Value(kv.Value.Tenths);
                w.Name("focusedSec").Value(kv.Value.FocusedSec);
                w.Name("sessions").Value(kv.Value.Sessions);
                w.Name("aborted").Value(kv.Value.AbortedCount);
                w.EndObject();
            }
            w.EndObject();

            w.Name("achievements").BeginObject();
            foreach (var kv in d.Achievements)
            {
                w.Name(kv.Key).BeginObject();
                w.Name("unlocked").Value(kv.Value.Unlocked);
                w.Name("at").Value(kv.Value.UnlockedAt);
                w.Name("progress").Value(kv.Value.Progress);
                w.Name("target").Value(kv.Value.Target);
                w.EndObject();
            }
            w.EndObject();

            w.Name("rewards").BeginObject();
            w.Name("owned").BeginArray();
            foreach (string id in d.Rewards.Owned) w.Value(id);
            w.EndArray();
            w.Name("title").Value(d.Rewards.EquippedTitle);
            w.Name("theme").Value(d.Rewards.EquippedTheme);
            w.Name("effect").Value(d.Rewards.EquippedEffect);
            w.Name("medal").Value(d.Rewards.EquippedMedal);
            w.Name("sound").Value(d.Rewards.EquippedSound);
            w.EndObject();

            w.Name("counters").BeginObject();
            foreach (var kv in d.Counters) w.Name(kv.Key).Value(kv.Value);
            w.EndObject();

            w.EndObject();
            return w.ToString();
        }

        public static AppData FromJson(JsonObj root)
        {
            var d = new AppData();
            d.Schema = root.Int("schema", 1);

            var so = root.Obj("settings");
            var s = d.Settings;
            s.Lang = so.Str("lang", I18n.DefaultLang);
            s.ThemeId = so.Str("theme", "fresh");
            s.MinimalMode = so.Bool("minimalMode");
            s.AutoStart = so.Bool("autoStart");
            s.CloseToTray = so.Bool("closeToTray", true);
            s.HideCountdown = so.Bool("hideCountdown");
            s.Sound = so.Bool("sound", true);
            s.BreakSeconds = so.Int("breakSeconds", 300);
            s.ShowYearView = so.Bool("showYearView");
            s.CustomMinutes = so.Int("customMinutes");
            s.BreakCustomMinutes = so.Int("breakCustomMinutes");
            s.LastPresetMinutes = so.Int("lastPresetMinutes");
            s.LastPresetId = so.Str("lastPresetId", "");
            s.NoteDraft = so.Str("noteDraft", "");
            s.RewardBadgeSeen = so.Int("rewardBadgeSeen");
            s.GreetingOn = so.Bool("greetingOn", true);
            s.LastGreetDay = so.Str("lastGreetDay", "");
            s.LastGreetPeriod = so.Str("lastGreetPeriod", "");
            s.CustomPresets = new List<int>();
            foreach (object o in so.Arr("customPresets"))
            {
                int m = (int)Convert.ToDouble(o, CultureInfo.InvariantCulture);
                if (m >= 1 && m <= 180) s.CustomPresets.Add(m);
            }

            var wo = root.Obj("wallet");
            d.Wallet.TotalTenths = wo.Int("totalTenths");
            d.Wallet.SpentWhole = wo.Int("spentWhole");

            foreach (object o in root.Arr("sessions"))
            {
                var oo = JsonObj.From(o);
                var rec = new SessionRecord();
                rec.Id = oo.Str("id");
                rec.StartedAt = oo.Str("startedAt");
                rec.PlannedSec = oo.Int("plannedSec");
                rec.FocusedSec = oo.Int("focusedSec");
                rec.Tenths = oo.Int("tenths");
                rec.BonusTenths = oo.Int("bonusTenths");
                rec.Preset = oo.Str("preset", "25");
                rec.TaskProcess = oo.Str("taskProcess", "");
                rec.TaskExe = oo.Str("taskExe", "");
                rec.Aborted = oo.Bool("aborted");
                rec.Note = oo.Str("note", "");
                d.Sessions.Add(rec);
            }

            // 任务配置
            var to = root.Obj("taskConfig");
            d.TaskConfig.ProcessName = to.Str("process", "");
            d.TaskConfig.Pid = to.Int("pid");
            d.TaskConfig.WindowTitle = to.Str("title", "");
            d.TaskConfig.TotalFocusSeconds = to.Int("totalSec", 90 * 60);
            d.TaskConfig.FocusRoundSeconds = to.Int("roundSec", 25 * 60);
            d.TaskConfig.BreakRoundSeconds = to.Int("breakSec", 5 * 60);

            // 进行中 / 待恢复的任务（process 为空 = 没有）
            var cto = root.Obj("currentTask");
            string proc = cto.Str("process", "");
            if (!string.IsNullOrEmpty(proc))
            {
                var st = new TaskState();
                st.ProcessName = proc;
                st.Pid = cto.Int("pid");
                st.WindowTitle = cto.Str("title", "");
                st.TotalFocusSeconds = cto.Int("totalSec");
                st.FocusRoundSeconds = cto.Int("roundSec");
                st.BreakRoundSeconds = cto.Int("breakSec");
                st.AccumulatedFocusSec = cto.Int("accum");
                st.AwaySec = cto.Int("away");
                st.RoundIndex = cto.Int("round");
                st.RoundsTotal = cto.Int("rounds");
                st.Status = cto.Str("status", TaskState.StatusInterrupted);
                st.StartedAt = cto.Str("startedAt", "");
                st.ClaimedEarly = cto.Bool("claimedEarly");
                d.CurrentTask = st;
            }

            var daysObj = root.Obj("days");
            foreach (string key in daysObj.Keys())
            {
                var oo = daysObj.Obj(key);
                var st = new DayStat();
                st.Tenths = oo.Int("tenths");
                st.FocusedSec = oo.Int("focusedSec");
                st.Sessions = oo.Int("sessions");
                st.AbortedCount = oo.Int("aborted");
                d.Days[key] = st;
            }

            var achObj = root.Obj("achievements");
            foreach (string key in achObj.Keys())
            {
                var oo = achObj.Obj(key);
                var st = new AchievementState();
                st.Id = key;
                st.Unlocked = oo.Bool("unlocked");
                st.UnlockedAt = oo.Str("at");
                st.Progress = oo.Int("progress");
                st.Target = oo.Int("target");
                d.Achievements[key] = st;
            }

            var ro = root.Obj("rewards");
            foreach (object o in ro.Arr("owned"))
            {
                string id = Convert.ToString(o, CultureInfo.InvariantCulture);
                if (!string.IsNullOrEmpty(id) && !d.Rewards.Owned.Contains(id)) d.Rewards.Owned.Add(id);
            }
            d.Rewards.EquippedTitle = ro.Str("title");
            d.Rewards.EquippedTheme = ro.Str("theme");
            d.Rewards.EquippedEffect = ro.Str("effect");
            d.Rewards.EquippedMedal = ro.Str("medal");
            d.Rewards.EquippedSound = ro.Str("sound");

            var co = root.Obj("counters");
            foreach (string key in co.Keys()) d.Counters[key] = co.Int(key);
            return d;
        }

        /// <summary>修复越界数据并重建日聚合缓存。</summary>
        public static void Normalize(AppData d)
        {
            if (d.Settings == null) d.Settings = new Settings();
            if (d.Wallet == null) d.Wallet = new Wallet();
            if (d.Sessions == null) d.Sessions = new List<SessionRecord>();
            if (d.Days == null) d.Days = new Dictionary<string, DayStat>(StringComparer.Ordinal);
            if (d.Achievements == null) d.Achievements = new Dictionary<string, AchievementState>(StringComparer.Ordinal);
            if (d.Rewards == null) d.Rewards = new RewardState();
            if (d.Counters == null) d.Counters = new Dictionary<string, int>(StringComparer.Ordinal);
            // 设置里的"休息时长"只影响专注结束后的提醒卡片，档位固定为 5/10/15/20 分钟；
            // 旧数据里的 3 / 25 等值统一回落到 5 分钟。
            if (d.Settings.BreakSeconds != 600 && d.Settings.BreakSeconds != 900 && d.Settings.BreakSeconds != 1200)
                d.Settings.BreakSeconds = 300;
            // 自定档位是"会话级"的：每次启动都回到「自定」无参数状态（退出时也会主动清一次）。
            // 旧版本曾把自定值当挡位存进 CustomPresets 并在这里回填，会导致"重启后凭空出现某个数值"，现已去掉。
            d.Settings.CustomMinutes = 0;
            d.Settings.BreakCustomMinutes = 0;
            if (d.Settings.LastPresetMinutes < 0 || d.Settings.LastPresetMinutes > 180) d.Settings.LastPresetMinutes = 0;
            // 默认主题与默认音效永远视为已拥有；空值规整为默认项
            if (!d.Rewards.Owned.Contains("th_fresh")) d.Rewards.Owned.Add("th_fresh");
            if (!d.Rewards.Owned.Contains("sn_default")) d.Rewards.Owned.Add("sn_default");
            if (string.IsNullOrEmpty(d.Rewards.EquippedTheme))
            {
                // 反推：由当前配色找对应的主题奖励，避免覆盖用户已选的配色
                var match = Rewards.ByThemeId(d.Settings.ThemeId);
                d.Rewards.EquippedTheme = match != null ? match.Id : "th_fresh";
            }
            else
            {
                var def = Rewards.ById(d.Rewards.EquippedTheme);
                if (def != null && def.Category == "theme" && !string.IsNullOrEmpty(def.ThemeId))
                    d.Settings.ThemeId = def.ThemeId;
            }
            if (string.IsNullOrEmpty(d.Rewards.EquippedSound)) d.Rewards.EquippedSound = "sn_default";
            if (d.Wallet.SpentWhole < 0) d.Wallet.SpentWhole = 0;
            if (d.Wallet.TotalTenths < 0) d.Wallet.TotalTenths = 0;

            // 任务配置值域夹紧
            var tc = d.TaskConfig;
            if (tc.TotalFocusSeconds < 5 * 60) tc.TotalFocusSeconds = 90 * 60;
            if (tc.TotalFocusSeconds > 8 * 3600) tc.TotalFocusSeconds = 8 * 3600;
            if (tc.FocusRoundSeconds < 5 * 60) tc.FocusRoundSeconds = 25 * 60;
            if (tc.FocusRoundSeconds > 120 * 60) tc.FocusRoundSeconds = 120 * 60;
            if (tc.BreakRoundSeconds < 60) tc.BreakRoundSeconds = 5 * 60;
            if (tc.BreakRoundSeconds > 60 * 60) tc.BreakRoundSeconds = 60 * 60;
            if (tc.Pid < 0) tc.Pid = 0;

            // 任务运行态：值域夹紧 + 非法状态归为"异常中断"
            var ct = d.CurrentTask;
            if (ct != null)
            {
                if (string.IsNullOrEmpty(ct.ProcessName)) d.CurrentTask = null;
                else
                {
                    if (ct.TotalFocusSeconds < 60) ct.TotalFocusSeconds = 60;
                    if (ct.TotalFocusSeconds > 8 * 3600) ct.TotalFocusSeconds = 8 * 3600;
                    if (ct.FocusRoundSeconds < 60) ct.FocusRoundSeconds = 60;
                    if (ct.BreakRoundSeconds < 30) ct.BreakRoundSeconds = 30;
                    if (ct.AccumulatedFocusSec < 0) ct.AccumulatedFocusSec = 0;
                    if (ct.AwaySec < 0) ct.AwaySec = 0;
                    if (ct.ContinuousAwaySec < 0) ct.ContinuousAwaySec = 0;
                    if (ct.RoundIndex < 1) ct.RoundIndex = 1;
                    if (ct.RoundsTotal < 1) ct.RoundsTotal = 1;
                    if (ct.Pid < 0) ct.Pid = 0;
                    string st2 = ct.Status;
                    if (st2 != TaskState.StatusRunning && st2 != TaskState.StatusAway &&
                        st2 != TaskState.StatusProgramGone && st2 != TaskState.StatusInterrupted)
                        ct.Status = TaskState.StatusInterrupted;
                }
            }

            RebuildDays(d);
        }

        /// <summary>按会话明细重建日聚合与钱包（自愈：明细是唯一事实来源）。</summary>
        public static void RebuildDays(AppData d)
        {
            d.Days.Clear();
            int total = 0;
            foreach (var rec in d.Sessions)
            {
                string key = DayKey.Of(rec.StartedLocal);
                if (key.Length == 0 || rec.StartedLocal == DateTime.MinValue) key = "0001-01-01";
                DayStat st;
                if (!d.Days.TryGetValue(key, out st)) { st = new DayStat(); d.Days[key] = st; }
                st.Add(rec);
                total += rec.Tenths + rec.BonusTenths;   // 任务额外奖励也计入钱包（第四节「均计入」）
            }
            d.Wallet.TotalTenths = total;
            if (d.Wallet.SpentWhole > TomatoMath.Whole(total)) d.Wallet.SpentWhole = TomatoMath.Whole(total);
        }
    }
}
