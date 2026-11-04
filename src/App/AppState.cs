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

    /// <summary>一次待展示的分时段温馨问候。</summary>
    internal sealed class Greeting
    {
        public string Period = "";          // morning / noon / afternoon / night
        public string TitleKey = "";
        public string PhraseKey = "";
        public string PrimaryKey = "";      // 主按钮文案键（空 = 只显示「知道了」）
    }

    /// <summary>应用状态门面：数据 + 计时器 + 主题 + 提醒规则。</summary>
    internal sealed class AppState
    {
        public AppData Data = new AppData();
        public TimerEngine Timer = new TimerEngine();
        public Theme CurrentTheme = Theme.ById("fresh");
        public Reminder Pending;

        /// <summary>任务编排器（与主计时联动：任务轮次直接跑在 TimerEngine 上）。</summary>
        public TaskRunner Task;

        /// <summary>最近一次结算中解锁的成就数（不落盘，仅供外壳决定播哪个音效）。</summary>
        public int LastUnlockedCount;

        public event EventHandler Changed;
        public event EventHandler<string> ToastRequested;
        public event EventHandler<List<AchievementDef>> AchievementsUnlocked;
        public event EventHandler<SessionRecord> SessionRecorded;
        public event EventHandler ReminderRequested;
        /// <summary>需要展示一次温馨问候（窗口可见时弹卡片，隐藏时走托盘气泡）。</summary>
        public event EventHandler GreetingRequested;
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
            Task = new TaskRunner(this);
            // 结算由状态层自己负责，外壳只关心 UI 反应（音效/动效/托盘气泡）
            Timer.FocusCompleted += delegate (object s, FocusCompletedEventArgs e)
            {
                // 任务模式：不逐轮写记录，交给编排器推进；任务结束时才写**一条**
                if (Task != null && Task.Active) { Task.OnFocusCompleted(e); return; }
                RecordSession(e);
            };
            Timer.BreakCompleted += delegate
            {
                if (Task != null && Task.Active) Task.OnBreakCompleted();
            };
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
            // 上次退出时任务还在跑（程序自身崩溃/被杀）→ 转"异常中断"，由中断弹窗让用户决定
            if (Data.CurrentTask != null && Data.CurrentTask.Status == TaskState.StatusRunning)
                Data.CurrentTask.Status = TaskState.StatusInterrupted;
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
                SecondBeat++;                     // 逐秒文案的统一节拍（外壳据此重绘）
                var h = ClockTick;
                if (h != null) h(this, EventArgs.Empty);
            }
            CheckGreeting(DateTime.Now);
            if (Task != null) Task.Update();      // 任务：秒节拍采样前台、推进轮次、宽限自动暂停
            UpdateJournal();                      // 计时运行态签到（崩溃恢复用）
        }

        private int _lastClockSecond = -1;

        /// <summary>
        /// 秒节拍计数：每跨一秒 +1。**只用于重绘**，不落盘、不置脏。
        /// 用途：任务卡上的「已偏离 x 秒」这类"每秒自己变、却不经过任何状态变更"的文案，
        /// 过去没有任何机制触发重绘，必须等鼠标划过才刷新。外壳比较这个计数即可每秒补一帧。
        /// </summary>
        public int SecondBeat { get; private set; }

        /// <summary>强制补一拍（手动刷新等场景用：让外壳立刻重绘一次）。</summary>
        public void BumpBeat() { SecondBeat++; }

        // --- 崩溃恢复（1KB 计时存档）---------------------------------------
        private int _journalSecond = -1;
        private TimerPhase _journalPhase = (TimerPhase)(-1);
        private bool _journalArmed;

        /// <summary>本次启动是否从崩溃存档恢复了计时（供外壳弹一条提示）。</summary>
        public bool RecoveredSession;

        /// <summary>
        /// 启动时尝试从 <c>session.journal</c> 恢复：**只恢复成暂停态**，绝不自动续跑。
        /// 存档超过 24 小时、已经到点、格式不可信时直接丢弃。
        /// 注意：这里只碰计时器，不碰日历/笔记/钱包——那些仍以 data.json 为准。
        /// </summary>
        public bool TryRecoverSession()
        {
            var snap = Journal.Read();
            if (snap == null) return false;
            if (snap.SavedLocal != DateTime.MinValue && (DateTime.Now - snap.SavedLocal).TotalHours > 24)
            {
                Journal.Clear();
                return false;
            }

            TimerPhase phase;
            switch (snap.Phase)
            {
                case "focusing":
                case "paused": phase = TimerPhase.Paused; break;
                case "break":
                case "pausedBreak": phase = TimerPhase.PausedBreak; break;   // 休息也恢复成"暂停的休息"
                default: Journal.Clear(); return false;
            }

            if (!Timer.Restore(phase, snap.PlannedSec, snap.BreakSec, snap.ElapsedSec, snap.Preset, snap.StartedLocal))
            {
                Journal.Clear();
                return false;
            }

            _journalArmed = true;
            _journalPhase = Timer.Phase;
            _journalSecond = Timer.RemainingSeconds;
            RecoveredSession = true;
            Log.Info("recover: 上次计时已恢复为暂停 phase=" + Timer.Phase +
                     " 剩余=" + Timer.RemainingSeconds + "s 计划=" + Timer.PlannedSeconds + "s");
            return true;
        }

        /// <summary>
        /// 计时运行态签到：阶段或剩余秒数一变就写一次（约 1 次/秒，1KB 定长文件），
        /// 回到空闲就删掉存档。**只写 session.journal，不重写 data.json**，
        /// 因此不会影响日历卡片与笔记内容。
        /// </summary>
        private void UpdateJournal()
        {
            var t = Timer;
            if (t.Phase == TimerPhase.Idle)
            {
                if (_journalArmed) { Journal.Clear(); _journalArmed = false; }
                _journalPhase = t.Phase;
                _journalSecond = -1;
                return;
            }

            int sec = t.RemainingSeconds;
            if (_journalArmed && sec == _journalSecond && t.Phase == _journalPhase) return;
            _journalPhase = t.Phase;
            _journalSecond = sec;

            var snap = new Journal.Snapshot();
            snap.Phase = PhaseKey(t.Phase);
            snap.PlannedSec = t.PlannedSeconds;
            snap.BreakSec = t.BreakSeconds;
            snap.ElapsedSec = t.FocusedSeconds;
            snap.Preset = t.Preset ?? "25";
            snap.StartedLocal = t.SessionStartedLocal;
            snap.TaskRound = (Task != null && Task.State != null) ? Task.State.RoundIndex : 0;
            if (Journal.Write(snap)) _journalArmed = true;
        }

        private static string PhaseKey(TimerPhase p)
        {
            switch (p)
            {
                case TimerPhase.Focusing: return "focusing";
                case TimerPhase.Paused: return "paused";
                case TimerPhase.Break: return "break";
                case TimerPhase.PausedBreak: return "pausedBreak";
                default: return "idle";
            }
        }

        // --- 分时段温馨问候 -------------------------------------------------
        /// <summary>待展示的问候（null 表示没有）。</summary>
        public Greeting PendingGreeting;

        private static readonly Random GreetRng = new Random(Environment.TickCount);

        /// <summary>
        /// 每秒检查一次：进入某个问候时段、且当天该时段还没问候过，就安排一次问候。
        /// **不打断专注**：计时/休息中、或关怀提醒卡正在显示时，留到下一个节拍再判（本轮结束后自然会弹）。
        /// </summary>
        public void CheckGreeting(DateTime now)
        {
            if (!Data.Settings.GreetingOn) return;
            if (PendingGreeting != null) return;

            var period = HealthRules.PeriodOf(now);
            if (period == GreetingPeriod.None) return;

            string key = HealthRules.PeriodKey(period);
            if (Data.Settings.LastGreetDay == DayKey.Today && Data.Settings.LastGreetPeriod == key) return;

            if (Timer.Phase != TimerPhase.Idle) return;     // 计时/休息中：不打断
            if (Pending != null) return;                    // 提醒卡正在显示：不叠加

            ShowGreeting(period, DayKey.Today);
        }

        /// <summary>安排一次问候（记录"今天该时段已问候"，保证每段每天只弹一次）。</summary>
        public void ShowGreeting(GreetingPeriod period, string day)
        {
            var g = new Greeting();
            g.Period = HealthRules.PeriodKey(period);
            g.TitleKey = "greet." + g.Period + ".title";
            g.PhraseKey = PickGreetingPhrase(g.Period);
            // 只有早上的问候才附带「开始专注」，夜里不该鼓励继续干活
            g.PrimaryKey = period == GreetingPeriod.Morning ? "greet.action.focus" : "";

            PendingGreeting = g;
            Data.Settings.LastGreetDay = day;
            Data.Settings.LastGreetPeriod = g.Period;
            MarkDirty();

            var h = GreetingRequested;
            if (h != null) h(this, EventArgs.Empty);
        }

        public void DismissGreeting()
        {
            if (PendingGreeting == null) return;
            PendingGreeting = null;
            MarkDirty();
        }

        /// <summary>随机取一条该时段的问候语句键（语句不足时回退到第 1 条）。</summary>
        private static string PickGreetingPhrase(string period)
        {
            string key = "greet." + period + "." + GreetRng.Next(1, 4);
            if (I18n.T(key) == key) key = "greet." + period + ".1";
            return key;
        }

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

        /// <summary>
        /// 统一的「开始专注」入口：计时器忙、或任务进行中一律拒绝并给出文案键。
        /// 主界面按钮、托盘菜单都走这里——以前托盘是直接调 Timer.StartFocus 的，
        /// 于是任务进行中从托盘点「开始专注」照样能把任务轮顶掉。
        /// </summary>
        public bool TryStartFocus(int minutes, string preset, out string blockKey)
        {
            blockKey = "";
            if (Timer.Phase != TimerPhase.Idle) { blockKey = "focus.busy"; return false; }
            if (Task != null && Task.Active) { blockKey = "task.busy"; return false; }
            Timer.StartFocus(minutes, preset);
            return true;
        }

        /// <summary>
        /// 启动时：任务还活着、却没有恢复出任何计时（计时器空闲）→ 转"待用户决定"。
        /// 否则会出现"任务卡还在、左边是全新准备开始"的僵局：用户随便跑个普通番茄钟，
        /// 还会被算进任务进度。
        /// </summary>
        public void MarkTaskNeedsAttention()
        {
            var st = Data.CurrentTask;
            if (st == null || !st.IsActive) return;
            if (st.Status == TaskState.StatusInterrupted) return;
            if (Timer.Phase != TimerPhase.Idle) return;      // 已恢复出计时：不用打扰

            st.Status = TaskState.StatusInterrupted;
            Save();
            Log.Info("task: 启动时任务仍活跃但没有可恢复的计时 → 转异常中断，等待用户决定");
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
            // 记录已落盘，恢复点必须立刻作废：否则"刚好崩在收尾的那 30ms 里"，
            // 下次启动会把这一轮当成没跑完而恢复，等于重复结算。
            Journal.Clear();
            _journalArmed = false;

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

        /// <summary>
        /// 任务结算：写入**一条**会话记录（普通与任务共走通用记录通道）。
        /// 基础按实际专注秒数折算（放弃走半收益）；唯有"完成且未声明提前完成"才加 20% 额外奖励。
        /// </summary>
        public SessionRecord RecordTaskSession(TaskState st, int focusedSec, TaskMath.TaskOutcome outcome)
        {
            if (st == null) return null;
            int tenths = TomatoMath.TenthsFor(focusedSec, TaskMath.IsAborted(outcome));
            int bonus = TaskMath.HasBonus(outcome) ? TaskMath.BonusTenths(tenths) : 0;

            var rec = new SessionRecord();
            rec.Id = Guid.NewGuid().ToString("N");
            rec.StartedAt = string.IsNullOrEmpty(st.StartedAt) ? DayKey.Stamp(DateTime.Now) : st.StartedAt;
            rec.PlannedSec = st.TotalFocusSeconds;
            rec.FocusedSec = focusedSec;
            rec.Tenths = tenths;
            rec.BonusTenths = bonus;
            rec.Preset = "task";
            rec.TaskProcess = st.ProcessName ?? "";
            rec.TaskExe = st.WindowTitle ?? "";
            rec.Aborted = TaskMath.IsAborted(outcome);
            rec.Note = Data.Settings.NoteDraft ?? "";
            Data.Sessions.Add(rec);
            LastRecordedNote = rec.Note;
            Data.Settings.NoteDraft = "";
            Store.RebuildDays(Data);

            _continuousFocusSeconds = 0;
            var unlocked = Achievements.Evaluate(Data, DateTime.Now);
            Save();
            // 同 RecordSession：任务结算一旦落盘，恢复点即刻作废，避免重复结算
            Journal.Clear();
            _journalArmed = false;
            LastUnlockedCount = unlocked.Count;

            var rh = SessionRecorded;
            if (rh != null) rh(this, rec);
            if (unlocked.Count > 0)
            {
                var ah = AchievementsUnlocked;
                if (ah != null) ah(this, unlocked);
            }
            MarkDirty();

            if (tenths <= 0)
                ShowToast(I18n.T("task.settled.none", TomatoMath.FormatClock(focusedSec)));
            else
                ShowToast(I18n.T("task.settled", TomatoMath.Format(tenths), TomatoMath.Format(bonus)));
            return rec;
        }

        /// <summary>任务模式：专注轮完成后的"选休息时长"卡片（复用提醒卡通道）。</summary>
        public void ShowTaskBreakReminder()
        {
            var r = new Reminder();
            r.BreakSeconds = Data.CurrentTask == null ? Data.Settings.BreakSeconds : Data.CurrentTask.BreakRoundSeconds;
            r.TitleKey = "task.break.title";
            r.ExtraKey = "task.break.body";
            r.Phrase = HealthRules.NextPhrase(ref _lastPhraseIndex);
            Pending = r;
            var h = ReminderRequested;
            if (h != null) h(this, EventArgs.Empty);
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
            Data.Settings.LastGreetDay = "";       // 问候记录也清掉：清空数据后当天可以重新问候
            Data.Settings.LastGreetPeriod = "";
            PendingGreeting = null;
            // 兑换状态被整体重置了：默认主题 / 默认提示音本来就无需兑换，必须回到"装备中"，
            // 否则奖励页会把它们画成"兑换 0 颗"、汉堡还会多出一个假红点。
            // 这里不能用 Store.Normalize —— 那会连"设置"一起改掉，而本操作明确要保留设置。
            Data.Rewards.EquippedTheme = "th_fresh";
            Data.Rewards.EquippedSound = "sn_default";
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
