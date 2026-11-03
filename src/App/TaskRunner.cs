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
        public const int AwayGraceSeconds = 120;

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
                var phase = _app.Timer.Phase;
                if (phase == TimerPhase.Focusing || phase == TimerPhase.Paused)
                    extra = _app.Timer.FocusedSeconds;
                return st.AccumulatedFocusSec + extra;
            }
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
        /// <summary>用当前 TaskConfig 启动任务。</summary>
        public bool Start()
        {
            var cfg = _app.Data.TaskConfig;
            if (string.IsNullOrEmpty(cfg.ProcessName)) return false;
            var rounds = TaskMath.BuildRounds(cfg.TotalFocusSeconds, cfg.FocusRoundSeconds);
            if (rounds.Count == 0) return false;

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
            st.Status = TaskState.StatusRunning;
            st.ContinuousAwaySec = 0;
            _app.Timer.StartBreak(Math.Max(1, minutes) * 60);
            _app.OnBreakTaken(minutes * 60);
            _app.DismissReminder();
            _app.Save();
        }

        /// <summary>跳过休息 → 直接进下一轮专注。</summary>
        public void SkipBreak()
        {
            var st = State;
            if (st == null) return;
            _app.Timer.Reset();
            _app.DismissReminder();
            StartRound(st.RoundIndex);
            _app.Save();
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
        /// <summary>中断弹窗「继续任务」：重新开当前轮。</summary>
        public void ContinueFromInterrupted()
        {
            var st = State;
            if (st == null) return;
            if (!Native.ProcessAlive(st.ProcessName))
            {
                st.Status = TaskState.StatusProgramGone;
                _app.Save();
                return;
            }
            StartRound(st.RoundIndex - 1);      // 重开当前这一轮
            _app.Save();
        }

        /// <summary>中断弹窗「结束任务」：半收益结算（第 4 条）。</summary>
        public void EndFromInterrupted()
        {
            Finish(TaskMath.TaskOutcome.Abandoned);
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

            // 程序启动时发现"上次还在跑"的任务 → 转异常中断，等用户决定
            if (st.Status == TaskState.StatusRunning && _app.Timer.Phase == TimerPhase.Idle)
            {
                st.Status = TaskState.StatusInterrupted;
                _app.Save();
                return;
            }
            if (!st.IsActive) return;

            int nowMs = Environment.TickCount;
            if (_lastSampleMs >= 0 && unchecked(nowMs - _lastSampleMs) < 1000) return;
            _lastSampleMs = nowMs;

            // 只有"专注轮正在走"时才做前台检测：休息轮不检测
            if (_app.Timer.Phase != TimerPhase.Focusing && _app.Timer.Phase != TimerPhase.Paused) return;

            // 目标程序是否还活着
            if (!Native.ProcessAlive(st.ProcessName))
            {
                if (st.Status != TaskState.StatusProgramGone)
                {
                    st.Status = TaskState.StatusProgramGone;
                    _app.Timer.Pause();
                    _app.Save();
                }
                return;
            }

            var fg = Native.ForegroundProcess();
            bool match = fg != null && TaskMath.MatchProcess(st.ProcessName, st.Pid, fg.ProcessName, fg.Pid);

            if (match)
            {
                st.ContinuousAwaySec = 0;
                if (st.Status == TaskState.StatusAway)
                {
                    st.Status = TaskState.StatusRunning;
                    if (_app.Timer.Phase == TimerPhase.Paused) _app.Timer.Resume();
                    _app.Save();
                }
                return;
            }

            // 偏离（软检测）：照常计时，只累计偏离秒数
            st.AwaySec++;
            st.ContinuousAwaySec++;
            if (st.ContinuousAwaySec >= AwayGraceSeconds && _app.Timer.Phase == TimerPhase.Focusing)
            {
                _app.Timer.Pause();
                st.Status = TaskState.StatusAway;
                _app.Save();
            }
        }
    }
}
