using System;
using System.Diagnostics;

namespace TomatoFocus.Core
{
    internal enum TimerPhase
    {
        Idle,
        Focusing,
        Paused,
        Break,
        /// <summary>休息被暂停（休眠挂起 / 崩溃恢复）。刻意加在末尾：不影响既有枚举值。</summary>
        PausedBreak
    }

    internal sealed class FocusCompletedEventArgs : EventArgs
    {
        public int FocusedSeconds;
        public int PlannedSeconds;
        public bool Aborted;
        public string Preset = "25";
        public DateTime StartedLocal;
    }

    /// <summary>
    /// 计时引擎。单调时钟 + 墙钟双轨，可检测系统休眠/挂起（挂起时自动暂停并提示）。
    /// 不依赖后台线程，由 UI 的渲染节拍驱动 Update()。
    /// </summary>
    internal sealed class TimerEngine
    {
        private const double MaxTickSeconds = 2.0;
        private const double SleepGapSeconds = 5.0;

        private double _elapsed;          // 当前阶段已用秒数（不含暂停）
        private double _lastMono;
        private double _lastWall;

        public TimerPhase Phase { get; private set; }
        public int PlannedSeconds { get; private set; }
        public int BreakSeconds { get; set; }
        public string Preset { get; private set; }
        public DateTime SessionStartedLocal { get; private set; }
        public bool SuspendDetected { get; private set; }

        public event EventHandler StateChanged;
        public event EventHandler Tick;
        public event EventHandler Suspended;
        public event EventHandler<FocusCompletedEventArgs> FocusCompleted;
        public event EventHandler BreakCompleted;

        public TimerEngine()
        {
            Phase = TimerPhase.Idle;
            // 默认档位取最低档（5 分钟）；用户上次选过的固定档位会在 AppState.Load 里恢复
            Preset = "5";
            PlannedSeconds = 5 * 60;
            BreakSeconds = 300;
            _lastMono = MonoNow();
            _lastWall = WallNow();
        }

        private static double MonoNow()
        {
            return Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        }

        private static double WallNow()
        {
            return DateTime.UtcNow.Ticks / (double)TimeSpan.TicksPerSecond;
        }

        public int FocusedSeconds { get { return (int)Math.Floor(_elapsed); } }

        /// <summary>当前（或暂停前的）阶段是不是休息。</summary>
        public bool IsBreakPhase { get { return Phase == TimerPhase.Break || Phase == TimerPhase.PausedBreak; } }

        public int RemainingSeconds
        {
            get
            {
                int total = IsBreakPhase ? BreakSeconds : PlannedSeconds;
                int left = total - FocusedSeconds;
                return left < 0 ? 0 : left;
            }
        }

        public double Progress
        {
            get
            {
                int total = IsBreakPhase ? BreakSeconds : PlannedSeconds;
                if (total <= 0) return 0;
                double p = _elapsed / total;
                return p < 0 ? 0 : (p > 1 ? 1 : p);
            }
        }

        public bool IsRunning { get { return Phase == TimerPhase.Focusing || Phase == TimerPhase.Break; } }

        /// <summary>当前若提前结束可获得的格数（用于确认提示，如实展示代价）。</summary>
        public int PreviewTenthsIfStopped
        {
            get { return TomatoMath.TenthsForAborted(FocusedSeconds); }
        }

        public void SetPresetMinutes(int minutes, string preset)
        {
            Preset = preset ?? "25";
            int sec = minutes * 60;
            if (sec < 60) sec = 60;
            PlannedSeconds = sec;
            RaiseStateChanged();
        }

        public void StartFocus(int minutes, string preset)
        {
            SetPresetMinutes(minutes, preset);
            _elapsed = 0;
            SuspendDetected = false;
            SessionStartedLocal = DateTime.Now;
            Phase = TimerPhase.Focusing;
            _lastMono = MonoNow();
            _lastWall = WallNow();
            RaiseStateChanged();
        }

        public void Pause()
        {
            if (Phase != TimerPhase.Focusing) return;
            Phase = TimerPhase.Paused;
            RaiseStateChanged();
        }

        public void Resume()
        {
            // 暂停的休息要回到"休息"，不能回到"专注"（否则剩余时间会按专注时长算）
            if (Phase == TimerPhase.PausedBreak)
            {
                Phase = TimerPhase.Break;
                _lastMono = MonoNow();
                _lastWall = WallNow();
                RaiseStateChanged();
                return;
            }
            if (Phase != TimerPhase.Paused) return;
            Phase = TimerPhase.Focusing;
            _lastMono = MonoNow();
            _lastWall = WallNow();
            RaiseStateChanged();
        }

        /// <summary>提前结束本轮，返回实际专注秒数；由调用方负责结算（半收益）。</summary>
        public int StopEarly()
        {
            // 只有"专注中/专注暂停"才能结算；休息阶段调用等于凭空记一笔，直接忽略
            if (Phase != TimerPhase.Focusing && Phase != TimerPhase.Paused) return 0;
            int focused = FocusedSeconds;
            var args = new FocusCompletedEventArgs();
            args.FocusedSeconds = focused;
            args.PlannedSeconds = PlannedSeconds;
            args.Aborted = true;
            args.Preset = Preset;
            args.StartedLocal = SessionStartedLocal;
            Phase = TimerPhase.Idle;
            _elapsed = 0;
            RaiseStateChanged();
            var h = FocusCompleted;
            if (h != null) h(this, args);
            return focused;
        }

        public void StartBreak(int seconds)
        {
            BreakSeconds = seconds < 30 ? 30 : seconds;
            _elapsed = 0;
            Phase = TimerPhase.Break;
            _lastMono = MonoNow();
            _lastWall = WallNow();
            RaiseStateChanged();
        }

        public void SkipBreak()
        {
            if (Phase != TimerPhase.Break && Phase != TimerPhase.PausedBreak) return;
            Phase = TimerPhase.Idle;
            _elapsed = 0;
            RaiseStateChanged();
        }

        public void Reset()
        {
            Phase = TimerPhase.Idle;
            _elapsed = 0;
            SuspendDetected = false;
            RaiseStateChanged();
        }

        /// <summary>
        /// 从崩溃存档恢复。**只恢复成暂停态**（专注暂停 / 休息暂停），绝不自动续跑——
        /// 用户可能已经离开，自动继续等于凭空给他记时间。
        /// </summary>
        /// <returns>恢复成功返回 true；参数不可信（阶段不对、已经到点）返回 false。</returns>
        public bool Restore(TimerPhase phase, int plannedSec, int breakSec, int elapsedSec, string preset, DateTime startedLocal)
        {
            if (phase != TimerPhase.Paused && phase != TimerPhase.PausedBreak) return false;
            int planned = plannedSec < 60 ? 60 : plannedSec;
            int brk = breakSec < 30 ? 30 : breakSec;
            if (elapsedSec < 0) elapsedSec = 0;
            if (elapsedSec >= (phase == TimerPhase.PausedBreak ? brk : planned)) return false;   // 已经到点

            Phase = phase;
            PlannedSeconds = planned;
            BreakSeconds = brk;
            Preset = string.IsNullOrEmpty(preset) ? "25" : preset;
            _elapsed = elapsedSec;
            SessionStartedLocal = startedLocal == DateTime.MinValue ? DateTime.Now : startedLocal;
            SuspendDetected = false;
            _lastMono = MonoNow();
            _lastWall = WallNow();
            return true;
        }

        /// <summary>由 UI 渲染节拍驱动（约每秒 4 次即可）。</summary>
        public void Update()
        {
            double mono = MonoNow();
            double wall = WallNow();
            double dMono = mono - _lastMono;
            double dWall = wall - _lastWall;
            _lastMono = mono;
            _lastWall = wall;
            if (dMono < 0) dMono = 0;
            if (dMono > MaxTickSeconds) dMono = MaxTickSeconds;

            bool slept = (dWall - dMono) > SleepGapSeconds;

            if (Phase == TimerPhase.Focusing || Phase == TimerPhase.Break)
            {
                _elapsed += dMono;

                if (slept)
                {
                    // 专注与休息都要自动暂停：睡一觉回来接着数，等于把休息也算成专注/把专注算成休息
                    SuspendDetected = true;
                    Phase = (Phase == TimerPhase.Break) ? TimerPhase.PausedBreak : TimerPhase.Paused;
                    RaiseStateChanged();
                    var sh = Suspended;
                    if (sh != null) sh(this, EventArgs.Empty);
                }
                CheckCompletion();
            }

            var th = Tick;
            if (th != null) th(this, EventArgs.Empty);
        }

        /// <summary>调试用：立即把当前专注推进到完成，走与自然结束完全相同的结算路径。</summary>
        public void DebugCompleteNow()
        {
            // 休息阶段也要能"一键完成"，否则调试按钮在休息时毫无反应
            if (Phase == TimerPhase.Break || Phase == TimerPhase.PausedBreak)
            {
                Phase = TimerPhase.Break;
                _lastMono = MonoNow();
                _lastWall = WallNow();
                _elapsed = BreakSeconds;
                CheckCompletion();
                return;
            }
            if (Phase != TimerPhase.Focusing && Phase != TimerPhase.Paused) return;
            Phase = TimerPhase.Focusing;
            _elapsed = PlannedSeconds;
            CheckCompletion();
        }

        private void CheckCompletion()
        {
            if (Phase == TimerPhase.Focusing && _elapsed >= PlannedSeconds)
            {
                _elapsed = PlannedSeconds;
                var args = new FocusCompletedEventArgs();
                args.FocusedSeconds = PlannedSeconds;
                args.PlannedSeconds = PlannedSeconds;
                args.Aborted = false;
                args.Preset = Preset;
                args.StartedLocal = SessionStartedLocal;
                Phase = TimerPhase.Idle;
                _elapsed = 0;
                RaiseStateChanged();
                var h = FocusCompleted;
                if (h != null) h(this, args);
            }
            else if (Phase == TimerPhase.Break && _elapsed >= BreakSeconds)
            {
                Phase = TimerPhase.Idle;
                _elapsed = 0;
                RaiseStateChanged();
                var h = BreakCompleted;
                if (h != null) h(this, EventArgs.Empty);
            }
        }

        private void RaiseStateChanged()
        {
            var h = StateChanged;
            if (h != null) h(this, EventArgs.Empty);
        }
    }
}
