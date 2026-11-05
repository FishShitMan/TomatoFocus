using System;
using TomatoFocus.Core;

namespace TomatoFocus.Core
{
    /// <summary>
    /// 任务编排器：**不自带计时**，而是驱动现有 TimerEngine 按"专注轮 → 休息轮 → 专注轮…"跑，
    /// 让任务计时天然显示在主时间环上、并共享主界面的暂停/结束按钮（与主计时联动）。
    ///
    /// 前台检测是"软检测"：偏离期间照常计时，但连续偏离达到宽限（120 秒）就自动暂停；
    /// 切回目标程序会自动继续。休息轮不做检测（休息本来就要离开）。
    /// </summary>
    internal sealed class TaskRunner
    {
        /// <summary>连续偏离宽限（秒）：超过就自动暂停，避免长时间脱离。</summary>
        public const int AwayGraceSeconds = 60;

        /// <summary>抓取前台程序的倒计时（秒）。</summary>
        public const int GrabCountdownSeconds = 3;

        private readonly AppState _app;

        private int _lastSampleMs = -1;
        private DateTime _grabUntil = DateTime.MinValue;
        private bool _grabbing;

        public TaskRunner(AppState app) { _app = app; }

        public TaskState State { get { return _app.Data.CurrentTask; } }

        /// <summary>任务是否占据主界面（未结算）。</summary>
        public bool Active { get { return State != null && State.IsActive; } }

        /// <summary>当前累计专注 = 已完成轮次之和 + 当前轮已专注（尚未计入的部分）。</summary>
        public int EffectiveAccumulatedSec
        {
            get
            {
                var st = State;
                if (st == null) return 0;
                int extra = 0;
                // 只统计**任务轮**自己的秒数：普通番茄钟与任务共用一个计时器，
                // 不认 Preset 的话，随便跑个 5 分钟番茄钟都会被算进任务进度。
                if (CountsForTaskProgress(_app.Timer.Preset, _app.Timer.Phase))
                    extra = _app.Timer.FocusedSeconds;
                return st.AccumulatedFocusSec + extra;
            }
        }

        /// <summary>这一轮是否应计入任务进度：只有"任务轮"且处于专注/暂停时才计。</summary>
        public static bool CountsForTaskProgress(string preset, TimerPhase phase)
        {
            if (!string.Equals(preset, "task", StringComparison.Ordinal)) return false;
            return phase == TimerPhase.Focusing || phase == TimerPhase.Paused;
        }

        /// <summary>当前轮次序号（1 起）。</summary>
        public int CurrentRound { get { var st = State; return st == null ? 0 : st.RoundIndex; } }
        public int TotalRounds { get { var st = State; return st == null ? 0 : st.RoundsTotal; } }

        /// <summary>是否允许"我已提前完成"（进度需 ≥ 30%）。</summary>
        public bool CanClaimEarly
        {
            get
            {
                var st = State;
                if (st == null) return false;
                return TaskMath.CanClaimEarly(EffectiveAccumulatedSec, st.TotalFocusSeconds);
            }
        }

        // --- 抓取前台程序 ---------------------------------------------------
        public bool Grabbing { get { return _grabbing; } }

        /// <summary>开始 3 秒倒计时抓取（倒计时期间用户切换到目标程序）。</summary>
        public void BeginGrab()
        {
            _grabbing = true;
            _grabUntil = DateTime.UtcNow.AddSeconds(GrabCountdownSeconds);
        }

        /// <summary>剩余秒数（0 表示已到点）。</summary>
        public int GrabRemainingSeconds
        {
            get
            {
                if (!_grabbing) return 0;
                int left = (int)Math.Ceiling((_grabUntil - DateTime.UtcNow).TotalSeconds);
                return left < 0 ? 0 : left;
            }
        }

        /// <summary>抓取失败的提示键（空表示没失败）。</summary>
        public string GrabErrorKey = "";

        private void FinishGrab()
        {
            _grabbing = false;
            GrabErrorKey = "";
            var fg = Native.ForegroundProcess();
            if (fg == null || fg.Pid == Native.SelfPid() || string.IsNullOrEmpty(fg.ProcessName))
            {
                GrabErrorKey = "task.grab.failed";
                return;
            }
            var cfg = _app.Data.TaskConfig;
            cfg.ProcessName = TaskMath.Normalize(fg.ProcessName);
            cfg.Pid = fg.Pid;
            cfg.WindowTitle = fg.WindowTitle ?? "";
        }

        // --- 启动 / 轮次 ----------------------------------------------------
        /// <summary>启动失败的原因（文案键），供界面提示用；成功时为空。</summary>
        public string LastStartError = "";

        /// <summary>
        /// 用当前 TaskConfig 启动任务。三道前置校验，任一不过都不开跑：
        /// ① 计时器必须空闲——任务与普通番茄钟共用同一个 TimerEngine，直接开始会把正在跑的那一轮顶掉；
        /// ② 不能已有进行中的任务；
        /// ③ **目标程序必须正在运行**——否则会开出一轮"注定抓不到程序"的计时，
        ///    1 秒后才被检测为"程序已退出"，用户看到的是"任务在跑、程序不在"的僵局。
        /// </summary>
        public bool Start()
        {
            LastStartError = "";

            if (_app.Timer.Phase != TimerPhase.Idle) { LastStartError = "focus.busy"; return false; }
            if (State != null && State.IsActive) { LastStartError = "task.busy"; return false; }

            var cfg = _app.Data.TaskConfig;
            if (string.IsNullOrEmpty(cfg.ProcessName)) { LastStartError = "task.setup.needProgram"; return false; }
            if (!Native.ProcessAlive(TaskMath.Normalize(cfg.ProcessName)))
            {
                LastStartError = "task.start.needProgram";
                return false;
            }

            var rounds = TaskMath.BuildRounds(cfg.TotalFocusSeconds, cfg.FocusRoundSeconds);
            if (rounds.Count == 0) { LastStartError = "task.busy"; return false; }

            var st = new TaskState();
            st.ProcessName = TaskMath.Normalize(cfg.ProcessName);
            st.Pid = cfg.Pid;
            st.WindowTitle = cfg.WindowTitle ?? "";
            st.TotalFocusSeconds = cfg.TotalFocusSeconds;
            st.FocusRoundSeconds = cfg.FocusRoundSeconds;
            st.BreakRoundSeconds = cfg.BreakRoundSeconds;
            st.RoundIndex = 1;
            st.RoundsTotal = rounds.Count;
            st.Status = TaskState.StatusRunning;
            st.StartedAt = DayKey.Stamp(DateTime.Now);
            _app.Data.CurrentTask = st;
            _lastSampleMs = -1;
            ProgramAlive = true;
            ResetAwayTracking();
            _app.Data.Settings.NoteDraft = "";    // 新任务从空白笔记开始
            StartRound(0);
            _app.Save();
            return true;
        }

        private void StartRound(int index)
        {
            var st = State;
            if (st == null) return;
            var rounds = TaskMath.BuildRounds(st.TotalFocusSeconds, st.FocusRoundSeconds);
            if (index >= rounds.Count) { Finish(TaskMath.TaskOutcome.Completed); return; }
            st.RoundIndex = index + 1;
            st.Status = TaskState.StatusRunning;
            st.ContinuousAwaySec = 0;
            int minutes = rounds[index] / 60;
            if (minutes < 1) minutes = 1;
            _app.Timer.StartFocus(minutes, "task");
        }

        /// <summary>专注轮完成（由 AppState 在任务模式下转交）。</summary>
        public void OnFocusCompleted(FocusCompletedEventArgs e)
        {
            var st = State;
            if (st == null) return;
            st.AccumulatedFocusSec += e.FocusedSeconds;
            st.ContinuousAwaySec = 0;
            if (st.AccumulatedFocusSec >= st.TotalFocusSeconds)
            {
                Finish(TaskMath.TaskOutcome.Completed);
                return;
            }
            // 弹任务版休息卡：4 个时长由用户选
            _app.ShowTaskBreakReminder();
            _app.Save();
        }

        /// <summary>休息结束 → 自动进入下一轮专注（第 3、4 条要求）。</summary>
        public void OnBreakCompleted()
        {
            var st = State;
            if (st == null || !st.IsActive) return;
            StartRound(st.RoundIndex);          // RoundIndex 已是下一轮序号（1 起）
            _app.Save();
        }

        /// <summary>用户点选休息时长（5/10/15/20 分钟）。</summary>
        public void TakeBreak(int minutes)
        {
            var st = State;
            if (st == null) return;
            // 互斥：只有"轮次已结束、正在等选休息"（Timer 空闲）时才允许；
            // 否则会把正在跑的那一轮顶掉（StartBreak 会清零 _elapsed）。
            if (_app.Timer.Phase != TimerPhase.Idle) return;
            st.Status = TaskState.StatusRunning;
            st.ContinuousAwaySec = 0;
            _app.Timer.StartBreak(Math.Max(1, minutes) * 60);
            _app.OnBreakTaken(minutes * 60);
            _app.DismissReminder();
            _app.Save();
        }

        /// <summary>
        /// 跳过休息 → 直接进下一轮专注。
        /// 两种入口都要接住：
        /// ① 休息卡上点「跳过休息」（此刻计时器空闲）；
        /// ② 主界面 / 托盘在**休息进行中**点「跳过」（此刻 Phase = Break）。
        /// 以前只处理了 ①，从主界面跳过时休息被清掉、任务却没进下一轮，
        /// 于是出现"任务卡写着专注中、计时器却是准备开始、还开不了新计时"的死局。
        /// </summary>
        public void SkipBreak()
        {
            var st = State;
            if (st == null) return;

            if (_app.Timer.Phase == TimerPhase.Break || _app.Timer.Phase == TimerPhase.PausedBreak)
                _app.Timer.SkipBreak();            // 正在休息 → 回到空闲
            else if (_app.Timer.Phase != TimerPhase.Idle)
                return;                            // 专注中：不是跳过休息的时机

            _app.DismissReminder();
            StartRound(st.RoundIndex);             // RoundIndex 已是"下一轮"
            _app.Save();
            _app.BumpBeat();
        }

        // --- 结算 -----------------------------------------------------------
        /// <summary>结算并清空任务。写**一条**记录（普通/任务共走通用记录通道）。</summary>
        public void Finish(TaskMath.TaskOutcome outcome)
        {
            var st = State;
            if (st == null) return;
            int focused = EffectiveAccumulatedSec;
            if (focused > st.TotalFocusSeconds) focused = st.TotalFocusSeconds;
            _app.RecordTaskSession(st, focused, outcome);
            _app.Data.CurrentTask = null;
            ResetAwayTracking();
            _app.DismissReminder();
            _app.Timer.Reset();
            _app.Save();
        }

        public void ClaimEarly()
        {
            var st = State;
            if (st == null) return;
            if (!CanClaimEarly) return;
            st.ClaimedEarly = true;
            Finish(TaskMath.TaskOutcome.ClaimedEarly);
        }

        public void Abandon()
        {
            Finish(TaskMath.TaskOutcome.Abandoned);
        }

        /// <summary>用户在"已偏离"状态点继续：恢复计时并清空连续偏离计数。</summary>
        public void ResumeFromAway()
        {
            var st = State;
            if (st == null) return;
            st.Status = TaskState.StatusRunning;
            st.ContinuousAwaySec = 0;
            if (_app.Timer.Phase == TimerPhase.Paused) _app.Timer.Resume();
            _app.Save();
        }

        // --- 中断恢复 -------------------------------------------------------
        /// <summary>中断弹窗「继续任务」：程序还在就接着走（有存档时从断点续，否则重开当前轮）。</summary>
        public void ContinueFromInterrupted()
        {
            var st = State;
            if (st == null) return;
            if (!Native.ProcessAlive(st.ProcessName))
            {
                st.Status = TaskState.StatusProgramGone;
                ProgramAlive = false;
                _app.Save();
                return;
            }
            ProgramAlive = true;

            // 上一轮结束时留下的"选休息"卡片在这里必须作废：
            // 用户点的是「继续任务」，不是「开始休息」，留着它就会在计时中继续显示，
            // 而且随手一点休息时长就会把刚重启的这一轮顶掉。
            _app.DismissReminder();

            // 崩溃恢复的场景：时间已由存档还原成"暂停在断点"，直接接着走，
            // 而不是把这一轮清零重来（重来会白白丢掉已经专注的分钟数）。
            if (_app.Timer.Phase != TimerPhase.Idle)
            {
                if (_app.Timer.Phase != TimerPhase.Paused && _app.Timer.Phase != TimerPhase.PausedBreak)
                {
                    // 有别的计时在跑（普通番茄钟/休息）：不许抢断
                    _app.ShowToast(I18n.T("task.busy"));
                    return;
                }
                st.Status = TaskState.StatusRunning;
                st.ContinuousAwaySec = 0;
                _app.Timer.Resume();
                _app.Save();
                _app.BumpBeat();
                return;
            }

            StartRound(st.RoundIndex - 1);      // 重开当前这一轮
            _app.Save();
            _app.BumpBeat();
        }

        /// <summary>中断弹窗「结束任务」：半收益结算（第 4 条）。</summary>
        public void EndFromInterrupted()
        {
            Finish(TaskMath.TaskOutcome.Abandoned);
        }

        /// <summary>调试「立即检测是否偏离任务」的判定结果。</summary>
        public enum AwayProbeResult
        {
            /// <summary>当前没有进行中的任务。</summary>
            NoTask,
            /// <summary>目标程序正在前台：未偏离，什么都不做。</summary>
            OnTarget,
            /// <summary>本来就处于「已偏离」：不重复计数。</summary>
            AlreadyAway,
            /// <summary>已立刻置为「已偏离」，计数 +1。</summary>
            Entered,
            /// <summary>这一下是第 4 次偏离，任务已按中断结束。</summary>
            Finished
        }

        /// <summary>
        /// 调试用：立刻抓一次前台程序。
        /// 目标程序在前台 → 什么都不做；不在前台 → 立即进入「已偏离」（不等 60 秒宽限），
        /// 并让偏离计数 +1（若已点过「确认」，这一次就是第 4 次，直接按中断结束任务）。
        /// </summary>
        public AwayProbeResult DebugForceAway()
        {
            var st = State;
            if (st == null || !st.IsActive) return AwayProbeResult.NoTask;

            var fg = Native.ForegroundProcess();
            bool match = fg != null && TaskMath.MatchProcess(st.ProcessName, st.Pid, fg.ProcessName, fg.Pid);
            if (match) return AwayProbeResult.OnTarget;

            // 已经在「已偏离」：保持一次偏离=一次计数的不变式，不重复加
            if (st.Status == TaskState.StatusAway) return AwayProbeResult.AlreadyAway;

            st.AwaySec += AwayGraceSeconds;
            st.ContinuousAwaySec = 0;          // 卡片秒数同样从 0 起
            if (_app.Timer.Phase == TimerPhase.Focusing) _app.Timer.Pause();
            st.Status = TaskState.StatusAway;

            if (AwayArmed)
            {
                AwayArmed = false;
                AwayWarning = false;
                Log.Info("task: 调试触发第 4 次偏离，按中断结束任务");
                int reached = AwayCount + 1;      // 这一次也算，且 Finish 会清零，先记下来
                Finish(TaskMath.TaskOutcome.Abandoned);
                SettledAwayCount = reached;
                SettledNotice = true;
                _app.ShowToast(I18n.T("task.away.autoEnded", reached));
                _app.BumpBeat();
                return AwayProbeResult.Finished;
            }

            AwayCount++;
            if (AwayTriggersWarning(AwayCount, AwayArmed, AwayWarning)) AwayWarning = true;
            _app.Save();
            _app.BumpBeat();
            Log.Info("task: 调试强制偏离，计数 " + AwayCount);
            return AwayProbeResult.Entered;
        }

        // --- 偏离次数与强提醒 -------------------------------------------------
        /// <summary>离开目标程序满这么多次后开始"再走就结束"的强提醒。</summary>
        public const int AwayWarnAfter = 3;

        /// <summary>
        /// 已发生的「已偏离」次数：**进入一次"已偏离"状态才算一次**
        /// （即离开目标程序满 <see cref="AwayGraceSeconds"/> 秒、计时被暂停那一刻）。
        /// 回来以后再离开，重新数 60 秒才会再 +1。
        /// </summary>
        public int AwayCount;

        /// <summary>用户点过「确认」：下一次进入「已偏离」就直接按中断结束任务。</summary>
        public bool AwayArmed;

        /// <summary>正在显示"再偏就结束"的中部强提醒。</summary>
        public bool AwayWarning;

        /// <summary>第 4 次偏离把任务结束后，弹一次"再接再厉"的中部提示，等用户点「知道了」。</summary>
        public bool SettledNotice;

        /// <summary>结束时的偏离次数（Finish 会把 AwayCount 清零，这里留一份给提示文案用）。</summary>
        public int SettledAwayCount = 1;

        /// <summary>累计到第 3 次「已偏离」之后、用户回来时要不要弹强提醒（纯函数，便于测试）。</summary>
        internal static bool AwayTriggersWarning(int count, bool armed, bool warning)
        {
            return !armed && !warning && count >= AwayWarnAfter;
        }

        private void ResetAwayTracking()
        {
            AwayCount = 0;
            AwayArmed = false;
            AwayWarning = false;
        }

        /// <summary>强提醒「确认」：进入第 4 次检测——下次离开目标程序立即按中断结束任务。</summary>
        public void ConfirmAwayWarning()
        {
            var st = State;
            if (st == null) { AwayWarning = false; return; }

            AwayWarning = false;
            AwayArmed = true;
            st.Status = TaskState.StatusRunning;
            st.ContinuousAwaySec = 0;
            if (_app.Timer.Phase == TimerPhase.Paused) _app.Timer.Resume();
            _app.Save();
            _app.BumpBeat();
        }

        /// <summary>强提醒「我在处理其他事务」：暂停计时，并把偏离次数归零重新开始计。</summary>
        public void DeferAwayWarning()
        {
            var st = State;
            if (st == null) { AwayWarning = false; return; }

            AwayWarning = false;
            AwayArmed = false;
            AwayCount = 0;
            st.Status = TaskState.StatusAway;
            st.ContinuousAwaySec = 0;
            if (_app.Timer.Phase == TimerPhase.Focusing) _app.Timer.Pause();
            _app.Save();
            _app.BumpBeat();
        }

        /// <summary>「任务中断了，再接再厉」提示点「知道了」后收起。</summary>
        public void DismissSettledNotice()
        {
            SettledNotice = false;
            _app.BumpBeat();
        }

        // --- 目标程序检测（手动刷新 / 程序重新启动）--------------------------
        /// <summary>
        /// 目标程序当前是否在运行（每秒采样一次的结果缓存）。
        /// 卡片与主按钮都读它，避免在绘制里反复枚举进程（那才是真正的性能杀手）。
        /// </summary>
        public bool ProgramAlive = true;

        /// <summary>
        /// 手动「重新检测」：立刻重新采样目标程序并让界面刷新一帧。
        /// 不自动恢复计时——恢复与否由用户点「继续」决定。
        /// </summary>
        public bool Recheck()
        {
            var st = State;
            if (st == null) return false;
            ProgramAlive = Native.ProcessAlive(st.ProcessName);
            _lastSampleMs = -1;                 // 解除 1 秒节流，下次 Update 立刻重新采样
            _app.BumpBeat();                    // 强制重绘一帧
            return ProgramAlive;
        }

        /// <summary>
        /// 「程序已退出」后用户点「继续」：程序确实回来了才允许恢复。
        /// 三种相位都要接住：暂停→续上；空闲（重启后没恢复出计时）→重开当前轮；
        /// 有别的计时在跑→拒绝，绝不允许任务把普通番茄钟顶掉。
        /// </summary>
        public bool ResumeFromGone()
        {
            var st = State;
            if (st == null) return false;
            if (!Native.ProcessAlive(st.ProcessName)) { ProgramAlive = false; _app.BumpBeat(); return false; }
            ProgramAlive = true;

            switch (_app.Timer.Phase)
            {
                case TimerPhase.Paused:
                case TimerPhase.PausedBreak:
                    st.Status = TaskState.StatusRunning;
                    st.ContinuousAwaySec = 0;
                    _app.Timer.Resume();
                    break;
                case TimerPhase.Idle:
                    _app.DismissReminder();
                    StartRound(st.RoundIndex - 1);
                    break;
                default:
                    _app.ShowToast(I18n.T("task.busy"));
                    return false;      // Focusing / Break：别的计时正在跑
            }

            _app.Save();
            _app.BumpBeat();
            return true;
        }

        // --- 每帧驱动 -------------------------------------------------------
        /// <summary>由渲染节拍调用；内部按 1 秒节拍采样前台与做轮次判断。</summary>
        public void Update()
        {
            if (_grabbing)
            {
                if (DateTime.UtcNow >= _grabUntil) FinishGrab();
                return;
            }

            var st = State;
            if (st == null) return;

            // 注意：这里**不再**把「Running + Timer 空闲」当成"异常中断"。
            // 专注轮刚跑完、正在等用户选休息时，状态本来就是 Running + Idle，
            // 以前那条判定会把正常流程误判成崩溃，弹窗盖在休息卡上（"任务被异常中断"误报）。
            // 启动时的恢复由 AppState.Load（Running → Interrupted）负责，唯一且可靠。
            if (!st.IsActive) return;

            int nowMs = Environment.TickCount;
            if (_lastSampleMs >= 0 && unchecked(nowMs - _lastSampleMs) < 1000) return;
            _lastSampleMs = nowMs;

            // 兜底自愈：任务还在跑、计时器却空闲、又没有待处理的休息卡 —— 这一轮丢了。
            // （旧版本从主界面「跳过」休息就会留下这种死局：任务卡写"专注中"、
            //   计时器却是"准备开始"，而且开不了新计时，只能重启程序。）
            // 这里直接重开当前轮，把用户从死局里捞出来。
            if (st.Status == TaskState.StatusRunning && _app.Timer.Phase == TimerPhase.Idle &&
                _app.Pending == null && !AwayWarning)
            {
                Log.Info("task: 检测到计时器空转（无待办休息卡），自动重开当前轮");
                StartRound(st.RoundIndex - 1);
                _app.Save();
                return;
            }

            // 只有"专注轮（含暂停）"才做前台检测：休息轮不检测
            if (_app.Timer.Phase != TimerPhase.Focusing && _app.Timer.Phase != TimerPhase.Paused) return;

            // 目标程序是否还活着（每秒一次，结果缓存给界面用）
            bool alive = Native.ProcessAlive(st.ProcessName);
            if (alive != ProgramAlive) { ProgramAlive = alive; _app.BumpBeat(); }

            if (!alive)
            {
                if (st.Status != TaskState.StatusProgramGone)
                {
                    st.Status = TaskState.StatusProgramGone;
                    st.ContinuousAwaySec = 0;
                    // 只有正在跑的时候才需要暂停；已经暂停/偏离暂停的状态不要去动它
                    if (_app.Timer.Phase == TimerPhase.Focusing) _app.Timer.Pause();
                    _app.Save();
                    _app.BumpBeat();
                }
                return;
            }

            // 程序回来了：把状态从"已退出"切到"可继续"，但仍要用户点「继续」，
            // 不自动抢跑（他可能还没准备好）。
            if (st.Status == TaskState.StatusProgramGone)
            {
                st.Status = TaskState.StatusRunning;
                st.ContinuousAwaySec = 0;
                _app.Save();
                _app.BumpBeat();
                return;
            }

            var fg = Native.ForegroundProcess();
            bool match = fg != null && TaskMath.MatchProcess(st.ProcessName, st.Pid, fg.ProcessName, fg.Pid);

            if (match)
            {
                bool hadAway = st.ContinuousAwaySec > 0 || st.Status == TaskState.StatusAway;
                st.ContinuousAwaySec = 0;

                // 强提醒还挂着：保持暂停，等用户点按钮（否则下一秒又会被自动续上）
                if (AwayWarning) return;

                if (AwayTriggersWarning(AwayCount, AwayArmed, AwayWarning))
                {
                    // 第 3 次"已偏离"之后又回来了：中部强提醒 + 保持暂停，等用户选
                    // 「确认」（进入第 4 次检测）还是「我在处理其他事务」（清零重计）
                    AwayWarning = true;
                    st.Status = TaskState.StatusAway;
                    _app.Save();
                    _app.BumpBeat();
                    return;
                }

                if (hadAway)
                {
                    st.Status = TaskState.StatusRunning;
                    if (_app.Timer.Phase == TimerPhase.Paused) _app.Timer.Resume();
                    _app.Save();
                    _app.BumpBeat();
                }
                return;
            }

            // 偏离（软检测）：照常计时，只累计偏离秒数。
            if (_app.Timer.Phase == TimerPhase.Focusing) { TickAway(); return; }

            // 已经进入「已偏离」：计时器是暂停的，但秒数要继续走——
            // 任务卡上的"已偏离 X 秒"是实时读数，停住会让人以为卡死。
            if (st.Status == TaskState.StatusAway) TickAway();
        }

        /// <summary>
        /// 偏离累计 1 秒。
        /// **满 60 秒才算一次偏离**：暂停计时、状态转「已偏离」、计数 +1；
        /// 若此前已点过「确认」，这第 4 次偏离直接按中断结束任务。
        /// 抽成独立方法是为了能脱离"每秒采样"直接测这条状态机。
        /// </summary>
        /// <returns>true 表示已进入「已偏离」或任务已结束，本轮不必再做别的。</returns>
        internal bool TickAway()
        {
            var st = State;
            if (st == null) return true;

            st.AwaySec++;
            st.ContinuousAwaySec++;
            _app.BumpBeat();                     // 逐秒文案：让卡片立刻刷新

            // 已经在「已偏离」里：只滚秒数，不重复计数、也不重复暂停
            if (st.Status == TaskState.StatusAway) return true;

            if (st.ContinuousAwaySec < AwayGraceSeconds) return false;

            // 满 60 秒：进入「已偏离」
            if (_app.Timer.Phase == TimerPhase.Focusing) _app.Timer.Pause();
            st.Status = TaskState.StatusAway;
            // 卡片上的"已偏离 X 秒"从进入偏离这一刻起从 0 开始爬（回到目标程序时再清零）
            st.ContinuousAwaySec = 0;

            if (AwayArmed)
            {
                AwayArmed = false;
                AwayWarning = false;
                Log.Info("task: 第 4 次偏离目标程序，按中断结束任务");
                int reached = AwayCount + 1;      // 这一次也算，且 Finish 会清零，先记下来
                Finish(TaskMath.TaskOutcome.Abandoned);
                // 结算完成后再弹"再接再厉"：Finish 会重置偏离相关状态，所以放在它后面
                SettledAwayCount = reached;
                SettledNotice = true;
                // 顶部提示：说清楚"偏离了几次 + 任务被自动中断了"
                _app.ShowToast(I18n.T("task.away.autoEnded", reached));
                _app.BumpBeat();
                return true;
            }

            AwayCount++;                          // 一次「已偏离」= 计数 +1
            // 第 3 次**当场**就弹强提醒：不再等"回到目标程序"那一刻。
            // （用调试按钮或卡片「继续」时永远不经过那条路径，会一路越过阈值不弹窗。）
            if (AwayTriggersWarning(AwayCount, AwayArmed, AwayWarning)) AwayWarning = true;
            _app.Save();
            _app.BumpBeat();
            Log.Info("task: 第 " + AwayCount + " 次偏离目标程序（已暂停）");
            return true;
        }
    }
}
