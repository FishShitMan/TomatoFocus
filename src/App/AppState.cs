using System;
using System.Collections.Generic;
using TomatoFocus.Render;

namespace TomatoFocus.Core
{
    /// <summary>一次待展示的关怀提醒。</summary>
    internal sealed class Reminder
    {
        public string TitleKey = "break.title";
        public string Phrase = "";
        public string ExtraKey = "";
        public bool LongBreak;
        public bool Night;
        public bool DailyCap;
        public int BreakSeconds = 300;
    }

    /// <summary>应用状态门面：数据 + 计时器 + 主题 + 提醒规则。</summary>
    internal sealed class AppState
    {
        public AppData Data = new AppData();
        public TimerEngine Timer = new TimerEngine();
        public Theme CurrentTheme = Theme.ById("fresh");
        public Reminder Pending;

        /// <summary>最近一次结算中解锁的成就数（不落盘，仅供外壳决定播哪个音效）。</summary>
        public int LastUnlockedCount;

        public event EventHandler Changed;
        public event EventHandler<string> ToastRequested;
        public event EventHandler<List<AchievementDef>> AchievementsUnlocked;
        public event EventHandler<SessionRecord> SessionRecorded;
        public event EventHandler ReminderRequested;
        /// <summary>极简模式开关变化（设置页与托盘菜单共用这一条路径）。</summary>
        public event EventHandler MinimalModeChanged;
        /// <summary>每秒一次的节拍（供托盘提示等低频刷新使用）。</summary>
        public event EventHandler ClockTick;

        private DateTime _lastSaveUtc = DateTime.MinValue;
        private bool _dirty;
        private int _lastPhraseIndex = -1;
        private DateTime _lastWaterUtc = DateTime.MinValue;
        private DateTime _lastEyeUtc = DateTime.MinValue;
        private int _continuousFocusSeconds;

        public AppState()
        {
            // 结算由状态层自己负责，外壳只关心 UI 反应（音效/动效/托盘气泡）
            Timer.FocusCompleted += delegate (object s, FocusCompletedEventArgs e) { RecordSession(e); };
        }

        public void Load()
        {
            Data = Store.Load();
            I18n.Load(Data.Settings.Lang);
            CurrentTheme = Theme.ById(Data.Settings.ThemeId);
            Timer.BreakSeconds = Data.Settings.BreakSeconds;
            // 恢复上次选择的"固定档位"（5/15/25）。自定档位是会话级的，不参与恢复。
            if (Data.Settings.LastPresetMinutes > 0 &&
                !string.Equals(Data.Settings.LastPresetId, "custom", StringComparison.Ordinal))
                Timer.SetPresetMinutes(Data.Settings.LastPresetMinutes,
                    string.IsNullOrEmpty(Data.Settings.LastPresetId) ? "25" : Data.Settings.LastPresetId);
        }

        /// <summary>记住当前档位（仅固定档位；自定值按设计不跨会话保留）。</summary>
        public void RememberPreset()
        {
            if (string.Equals(Timer.Preset, "custom", StringComparison.Ordinal))
            {
                Data.Settings.LastPresetId = "";      // 自定不落盘，下次启动回到「自定」
                MarkDirty();
                return;
            }
            Data.Settings.LastPresetMinutes = Math.Max(0, Timer.PlannedSeconds / 60);
            Data.Settings.LastPresetId = Timer.Preset ?? "";
            MarkDirty();
        }

        /// <summary>退出时清除会话级设置（自定档位），保证下次启动是「自定」无参数状态。</summary>
        public void ClearEphemeralSettings()
        {
            if (Data.Settings.CustomMinutes == 0 && Data.Settings.BreakCustomMinutes == 0 &&
                string.IsNullOrEmpty(Data.Settings.LastPresetId)) return;
            Data.Settings.CustomMinutes = 0;
            Data.Settings.BreakCustomMinutes = 0;
            if (string.Equals(Data.Settings.LastPresetId, "custom", StringComparison.Ordinal))
            {
                Data.Settings.LastPresetId = "";
                Data.Settings.LastPresetMinutes = 0;
            }
            Save();
        }

        public void MarkDirty()
        {
            _dirty = true;
            var h = Changed;
            if (h != null) h(this, EventArgs.Empty);
        }

        /// <summary>周期调用：落盘 + 夜间/久坐提醒 + 秒级节拍。</summary>
        public void Tick()
        {
            if (_dirty && (DateTime.UtcNow - _lastSaveUtc).TotalSeconds > 5)
            {
                Save();
            }
            int sec = DateTime.Now.Second;
            if (sec != _lastClockSecond)
            {
                _lastClockSecond = sec;
                var h = ClockTick;
                if (h != null) h(this, EventArgs.Empty);
            }
        }

        private int _lastClockSecond = -1;

        public bool Save()
        {
            if (Store.Save(Data))
            {
                _dirty = false;
                _lastSaveUtc = DateTime.UtcNow;
                return true;
            }
            return false;
        }

        public void ApplyTheme()
        {
            CurrentTheme = Theme.ById(Data.Settings.ThemeId);
            MarkDirty();
        }

        public void SetLanguage(string lang)
        {
            Data.Settings.Lang = lang;
            I18n.Load(lang);
            MarkDirty();
        }

        public void SetAutoStart(bool enabled)
        {
            Data.Settings.AutoStart = enabled;
            Startup.Set(enabled);
            MarkDirty();
        }

        /// <summary>切换极简模式；由外壳订阅事件后真正隐藏/显示窗口。</summary>
        public void SetMinimalMode(bool enabled)
        {
            if (Data.Settings.MinimalMode == enabled) return;
            Data.Settings.MinimalMode = enabled;
            MarkDirty();
            var h = MinimalModeChanged;
            if (h != null) h(this, EventArgs.Empty);
        }

        /// <summary>显式退出极简模式（显示窗口时调用，保证设置页开关同步）。</summary>
        public void ExitMinimalMode()
        {
            if (!Data.Settings.MinimalMode) return;
            Data.Settings.MinimalMode = false;
            MarkDirty();
            var h = MinimalModeChanged;
            if (h != null) h(this, EventArgs.Empty);
        }

        public void ShowToast(string message)
        {
            var h = ToastRequested;
            if (h != null) h(this, message);
        }

        // --- 会话结算 ------------------------------------------------------
        /// <summary>
        /// 如实结算一次专注。返回写入的记录；若中断且不满 0.1 颗，则按要求不计数并返回 null。
        /// </summary>
        public SessionRecord RecordSession(FocusCompletedEventArgs e)
        {
            int tenths = TomatoMath.TenthsFor(e.FocusedSeconds, e.Aborted);
            if (tenths <= 0)
            {
                ShowToast(I18n.T("focus.stop.none", TomatoMath.FormatClock(e.FocusedSeconds)));
                _continuousFocusSeconds = 0;
                return null;
            }

            var rec = new SessionRecord();
            rec.Id = Guid.NewGuid().ToString("N");
            rec.StartedAt = DayKey.Stamp(e.StartedLocal == DateTime.MinValue ? DateTime.Now : e.StartedLocal);
            rec.PlannedSec = e.PlannedSeconds;
            rec.FocusedSec = e.FocusedSeconds;
            rec.Tenths = tenths;
            rec.Preset = e.Preset ?? "25";
            rec.Aborted = e.Aborted;
            rec.Note = Data.Settings.NoteDraft ?? "";     // 本次番茄钟内记录的内容随会话保存
            Data.Sessions.Add(rec);
            LastRecordedNote = rec.Note;                  // 供提醒卡的「保留笔记」恢复续写
            Data.Settings.NoteDraft = "";
            Store.RebuildDays(Data);

            _continuousFocusSeconds += e.FocusedSeconds;

            var unlocked = Achievements.Evaluate(Data, DateTime.Now);
            Save();

            // 先把本次解锁数交给外壳：由它二选一播放"成就音/完成音"，
            // 避免完成音与成就音先后各响一次而听起来像重复播放。
            LastUnlockedCount = unlocked.Count;

            var rh = SessionRecorded;
            if (rh != null) rh(this, rec);
            if (unlocked.Count > 0)
            {
                var ah = AchievementsUnlocked;
                if (ah != null) ah(this, unlocked);
            }
            MarkDirty();

            Pending = BuildReminder();
            var rem = ReminderRequested;
            if (rem != null) rem(this, EventArgs.Empty);
            return rec;
        }

        /// <summary>撤回上一条记录（误操作时用）。</summary>
        public bool UndoLastSession()
        {
            if (Data.Sessions.Count == 0) return false;
            Data.Sessions.RemoveAt(Data.Sessions.Count - 1);
            Store.RebuildDays(Data);
            Achievements.Evaluate(Data, DateTime.Now);
            Save();
            MarkDirty();
            return true;
        }

        public void OnBreakTaken(int seconds)
        {
            Data.Bump("breaksTaken");
            Data.Bump("breakSeconds", seconds);
            Save();
            MarkDirty();
        }

        // --- 关怀提醒 ------------------------------------------------------
        private Reminder BuildReminder()
        {
            var r = new Reminder();
            r.BreakSeconds = Data.Settings.BreakSeconds;

            int todayTenths = Stats.TodayTenths(Data);
            int cleanToday = 0;
            string today = DayKey.Today;
            foreach (var rec in Data.Sessions)
                if (DayKey.Of(rec.StartedLocal) == today && !rec.Aborted && rec.FocusedSec >= TomatoMath.StandardSeconds) cleanToday++;

            bool night = HealthRules.IsNight(DateTime.Now);
            bool cap = todayTenths >= HealthRules.DailyCapTenths;
            bool longBreak = cleanToday > 0 && cleanToday % HealthRules.PomodorosPerLongBreak == 0;
            bool longSit = _continuousFocusSeconds >= HealthRules.LongSitSeconds;

            r.Night = night;
            r.DailyCap = cap;
            r.LongBreak = longBreak || longSit;
            r.Phrase = HealthRules.NextPhrase(ref _lastPhraseIndex);

            if (night) { r.TitleKey = "break.title"; r.ExtraKey = "break.night"; }
            else if (cap) { r.TitleKey = "break.title"; r.ExtraKey = "break.dailyCap"; }
            else if (r.LongBreak) { r.TitleKey = "break.long.title"; r.ExtraKey = "break.long.body"; r.BreakSeconds = 900; }

            if (r.LongBreak) _continuousFocusSeconds = 0;
            return r;
        }

        /// <summary>额外的喝水/护眼提醒（休息卡片中附带）。</summary>
        public string WaterOrEyeHint()
        {
            var now = DateTime.UtcNow;
            if ((now - _lastWaterUtc).TotalSeconds >= HealthRules.WaterIntervalSeconds)
            {
                _lastWaterUtc = now;
                return I18n.T("break.water");
            }
            if ((now - _lastEyeUtc).TotalSeconds >= HealthRules.EyeIntervalSeconds)
            {
                _lastEyeUtc = now;
                return I18n.T("break.eye");
            }
            return "";
        }

        public void DismissReminder()
        {
            Pending = null;
        }

        /// <summary>最近一次结算保存的笔记（供提醒卡的「保留笔记」按钮恢复续写）。</summary>
        public string LastRecordedNote = "";

        /// <summary>
        /// 把上一次结算保存的笔记恢复到草稿，便于在原内容基础上继续续写。
        /// 只恢复一次，避免反复覆盖用户新写的内容；没有可恢复的笔记时返回 false。
        /// </summary>
        public bool KeepLastNote()
        {
            string note = LastRecordedNote ?? "";
            if (note.Length == 0) return false;
            Data.Settings.NoteDraft = note;
            LastRecordedNote = "";
            Save();
            MarkDirty();
            return true;
        }

        // --- 数据清除与调试功能 ---------------------------------------------
        /// <summary>清空全部进度数据（记录 / 日聚合 / 钱包 / 成就 / 兑换 / 计数器 / 当前笔记草稿），保留设置。</summary>
        public void ClearAllData()
        {
            Data.Sessions.Clear();
            Data.Days.Clear();
            Data.Achievements.Clear();
            Data.Rewards = new RewardState();
            Data.Counters.Clear();
            Data.Wallet = new Wallet();
            Data.Settings.NoteDraft = "";          // 当前笔记属于工作数据，一并清掉
            Store.RebuildDays(Data);
            Achievements.Evaluate(Data, DateTime.Now);
            Save();
            MarkDirty();
            ShowToast(I18n.T("settings.clearData.done"));
        }

        /// <summary>调试：直接增加指定颗完整番茄（以一条完整会话记录的形式写入，保证日聚合一致）。</summary>
        public void DebugAddTomatoes(int whole)
        {
            if (whole <= 0) return;
            var rec = new SessionRecord();
            rec.Id = "debug-" + Guid.NewGuid().ToString("N");
            rec.StartedAt = DayKey.Stamp(DateTime.Now);
            rec.PlannedSec = whole * TomatoMath.StandardSeconds;
            rec.FocusedSec = rec.PlannedSec;
            rec.Tenths = whole * TomatoMath.TenthsPerTomato;
            rec.Preset = "debug";
            rec.Aborted = false;
            Data.Sessions.Add(rec);
            Store.RebuildDays(Data);
            Achievements.Evaluate(Data, DateTime.Now);
            Save();
            MarkDirty();
            ShowToast(I18n.T("debug.done"));
        }

        /// <summary>调试：一键解锁全部成就。</summary>
        public void DebugUnlockAllAchievements()
        {
            foreach (var def in Achievements.All)
            {
                var st = Achievements.State(Data, def.Id);
                st.Target = def.Target;
                st.Progress = def.Target;
                if (!st.Unlocked)
                {
                    st.Unlocked = true;
                    st.UnlockedAt = DayKey.Stamp(DateTime.Now);
                }
            }
            Save();
            MarkDirty();
            ShowToast(I18n.T("debug.done"));
        }

        /// <summary>调试：一键解锁全部奖励。</summary>
        public void DebugUnlockAllRewards()
        {
            foreach (var def in Rewards.All)
                if (!Data.Rewards.IsOwned(def.Id)) Data.Rewards.Owned.Add(def.Id);
            Save();
            MarkDirty();
            ShowToast(I18n.T("debug.done"));
        }

        /// <summary>调试：立即完成当前计时（走正常完成流程，结算与提醒都会触发）。</summary>
        public void DebugCompleteTimer()
        {
            Timer.DebugCompleteNow();
            MarkDirty();
        }
    }
}
