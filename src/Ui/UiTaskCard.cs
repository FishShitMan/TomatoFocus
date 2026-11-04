using System;
using System.Drawing;
using TomatoFocus.Core;
using TomatoFocus.Render;

namespace TomatoFocus.Ui
{
    /// <summary>
    /// 任务卡与"任务被异常中断"弹窗。
    /// 任务卡占据右侧栏位顶部（原「今日」卡片的位置），今日统计压缩为卡片底部一行。
    /// </summary>
    internal sealed partial class UiRoot
    {
        private bool _taskSetup;                        // 配置态是否展开
        private static readonly int[] TaskTotalMinutes = { 30, 60, 90, 120 };
        private static readonly int[] TaskRoundMinutes = { 15, 25, 45 };
        private static readonly int[] TaskBreakMinutes = { 5, 10, 15, 20 };

        private int _dialogCheckMs;
        private bool _dialogAlive;

        /// <summary>任务卡期望高度：只装任务本身，概览行已移到时间环下方。</summary>
        private float TaskCardHeight
        {
            get
            {
                var st = _app.Data.CurrentTask;
                if (st != null && st.IsActive)
                {
                    bool needsActions = st.Status == TaskState.StatusAway ||
                                        st.Status == TaskState.StatusProgramGone;
                    return needsActions ? 140f : 108f;
                }
                return _taskSetup ? 192f : 104f;
            }
        }

        private void DrawTaskCard(Painter pt, RectangleF card)
        {
            var t = pt.T;
            pt.Shadow(card, 16f, 7f, t.Shadow);
            pt.FillRound(card, 16f, t.Surface);

            var task = _app.Data.CurrentTask;
            bool active = task != null && task.IsActive;

            if (active) DrawTaskRunning(pt, card, t, task);
            else if (_taskSetup) DrawTaskSetup(pt, card, t);
            else DrawTaskIdle(pt, card, t);
        }

        private void DrawTaskIdle(Painter pt, RectangleF card, Theme t)
        {
            float x = card.Left + 18, w = card.Width - 36;
            pt.TextLeft(I18n.T("task.title"), pt.F(14f, true), t.Text, new RectangleF(x, card.Top + 8, w, 22));
            pt.TextLeft(I18n.T("task.idle.hint"), pt.F(10.5f), t.TextMuted,
                new RectangleF(x, card.Top + 32, w, 18));

            var btn = new RectangleF(x, card.Top + 52, w, 36);
            pt.FillRound(btn, 11f, t.AccentSoft);
            pt.TextCenter(I18n.T("task.setup.open"), pt.F(12f, true), t.AccentDark, btn);
            Hot("taskSetupOpen", btn, delegate { _taskSetup = true; _dirty = true; });
        }

        private void DrawTaskSetup(Painter pt, RectangleF card, Theme t)
        {
            var cfg = _app.Data.TaskConfig;
            var task = _app.Task;
            float x = card.Left + 18, w = card.Width - 36;
            float y = card.Top + 8;

            // 目标程序
            pt.TextLeft(I18n.T("task.setup.program"), pt.F(10.5f), t.TextMuted, new RectangleF(x, y, 66, 22));
            string prog = string.IsNullOrEmpty(cfg.ProcessName) ? I18n.T("task.setup.noProgram") : cfg.ProcessName;
            pt.TextLeft(prog, pt.F(11.5f, true),
                string.IsNullOrEmpty(cfg.ProcessName) ? t.TextMuted : t.Text,
                new RectangleF(x + 68, y, w - 68 - 118, 22));

            var pick = new RectangleF(card.Right - 18 - 112, y - 1, 112, 24);
            pt.FillRound(pick, 12f, t.SurfaceAlt);
            pt.TextCenter(I18n.T("task.pick.button"), pt.F(10f, true), t.TextMuted, pick);
            Hot("taskPick", pick, delegate { PickProgram(); });
            y += 28;

            y = TaskChipRow(pt, t, card, y, I18n.T("task.setup.total"), TaskTotalMinutes,
                cfg.TotalFocusSeconds / 60, "tTotal");
            y = TaskChipRow(pt, t, card, y, I18n.T("task.setup.round"), TaskRoundMinutes,
                cfg.FocusRoundSeconds / 60, "tRound");
            y = TaskChipRow(pt, t, card, y, I18n.T("task.setup.break"), TaskBreakMinutes,
                cfg.BreakRoundSeconds / 60, "tBreak");

            if (!string.IsNullOrEmpty(task.GrabErrorKey))
                pt.TextLeft(I18n.T(task.GrabErrorKey), pt.F(9.5f), t.Warn, new RectangleF(x, y - 2, w, 16));
            else if (string.IsNullOrEmpty(cfg.ProcessName))
                pt.TextLeft(I18n.T("task.setup.grabHint"), pt.F(9.5f), t.TextMuted, new RectangleF(x, y - 2, w, 16));

            var start = new RectangleF(x, card.Bottom - 46, 118, 34);
            bool timerBusy = _app.Timer.Phase != TimerPhase.Idle;
            bool canStart = !string.IsNullOrEmpty(cfg.ProcessName) && !timerBusy;
            pt.FillRound(start, 17f, canStart ? t.Accent : Theme.Alpha(t.Accent, 110));
            pt.TextCenter(I18n.T("task.setup.start"), pt.F(12f, true), Color.White, start);
            Hot("taskStart", start, delegate
            {
                // 前置校验全部交给 TaskRunner.Start()：计时器忙 / 已有任务 / 目标程序没在跑
                // 都会返回具体原因，这里只负责把它弹出来。
                if (_app.Task.Start()) { _taskSetup = false; _dirty = true; return; }
                string key = string.IsNullOrEmpty(_app.Task.LastStartError) ? "task.busy" : _app.Task.LastStartError;
                ShowToast(I18n.T(key));
            });

            var cancel = new RectangleF(start.Right + 10, card.Bottom - 46, 80, 34);
            pt.FillRound(cancel, 17f, t.SurfaceAlt);
            pt.TextCenter(I18n.T("task.setup.cancel"), pt.F(11.5f, true), t.TextMuted, cancel);
            Hot("taskSetupCancel", cancel, delegate { _taskSetup = false; _dirty = true; });
        }

        private float TaskChipRow(Painter pt, Theme t, RectangleF card, float y,
                                  string label, int[] values, int current, string idPrefix)
        {
            float x = card.Left + 18, w = card.Width - 36;
            pt.TextLeft(label, pt.F(10.5f), t.TextMuted, new RectangleF(x, y, 66, 24));

            float cx = x + 68;
            float avail = w - 68;
            float cw = (avail - 6 * (values.Length - 1)) / values.Length;
            for (int i = 0; i < values.Length; i++)
            {
                int v = values[i];
                var r = new RectangleF(cx + i * (cw + 6), y, cw, 24);
                bool on = v == current;
                pt.FillRound(r, 12f, on ? t.AccentSoft : t.SurfaceAlt);
                pt.TextCenter(v + I18n.T("task.minuteUnit"), pt.F(10f, on), on ? t.AccentDark : t.TextMuted, r);
                string id = idPrefix + i;
                Hot(id, r, delegate { SetTaskMinutes(idPrefix, v); });
            }
            return y + 30;
        }

        private void SetTaskMinutes(string which, int minutes)
        {
            var cfg = _app.Data.TaskConfig;
            if (which == "tTotal") cfg.TotalFocusSeconds = minutes * 60;
            else if (which == "tRound") cfg.FocusRoundSeconds = minutes * 60;
            else if (which == "tBreak") cfg.BreakRoundSeconds = minutes * 60;
            _dirty = true;
        }

        private void DrawTaskRunning(Painter pt, RectangleF card, Theme t, TaskState st)
        {
            float x = card.Left + 18, w = card.Width - 36;
            float y = card.Top + 10;

            bool gone = st.Status == TaskState.StatusProgramGone;
            bool away = st.Status == TaskState.StatusAway;
            // 目标程序是否还在跑：由 TaskRunner 每秒采样一次并缓存，绘制里绝不枚举进程
            bool progAlive = _app.Task.ProgramAlive;

            pt.TextLeft(I18n.T("task.title"), pt.F(12.5f, true), t.TextMuted, new RectangleF(x, y, 46, 22));
            pt.TextLeft(st.ProcessName, pt.F(12f, true), t.Text, new RectangleF(x + 42, y, w - 42 - 96, 22));

            // 状态文案要跟随计时阶段：暂停 / 休息 / 专注，不能只按任务状态判断
            var phaseNow = _app.Timer.Phase;
            string stateText;
            Color stateColor;
            if (gone && progAlive) { stateText = I18n.T("task.state.back"); stateColor = t.Good; }
            else if (gone) { stateText = I18n.T("task.state.gone"); stateColor = t.Warn; }
            else if (away) { stateText = I18n.T("task.state.away"); stateColor = t.Warn; }
            else if (phaseNow == TimerPhase.Paused) { stateText = I18n.T("focus.paused"); stateColor = t.Warn; }
            else if (phaseNow == TimerPhase.Break) { stateText = I18n.T("focus.breaking"); stateColor = t.AccentDark; }
            else { stateText = I18n.T("task.state.focusing"); stateColor = t.Good; }
            pt.TextRight(stateText, pt.F(10.5f, true), stateColor, new RectangleF(card.Right - 106, y, 88, 22));
            y += 26;

            int accum = _app.Task.EffectiveAccumulatedSec;
            int total = st.TotalFocusSeconds;
            pt.TextLeft(I18n.T("task.progress", accum / 60, total / 60), pt.F(10.5f), t.TextMuted,
                new RectangleF(x, y, w, 16));
            y += 18;

            var barBg = new RectangleF(x, y, w, 8);
            pt.FillRound(barBg, 4f, t.SurfaceAlt);
            float ratio = TaskMath.ProgressRatio(accum, total);
            if (ratio > 0.002f)
                pt.FillRound(new RectangleF(x, y, Math.Max(6f, w * ratio), 8), 4f, t.Accent);
            y += 16;

            string info;
            Color infoColor = t.TextMuted;
            if (gone)
            {
                info = I18n.T(progAlive ? "task.wait.programBack" : "task.wait.programGone");
                if (!progAlive) infoColor = t.Warn;
            }
            else if (away)
            {
                if (st.ContinuousAwaySec >= TaskRunner.AwayGraceSeconds)
                    info = I18n.T("task.away.paused", st.ContinuousAwaySec);   // 已经自动暂停，别再显示"还剩 0 秒"
                else
                {
                    int left = Math.Max(0, TaskRunner.AwayGraceSeconds - st.ContinuousAwaySec);
                    info = I18n.T("task.away", st.ContinuousAwaySec) + "  " + I18n.T("task.away.left", left);
                }
                infoColor = t.Warn;
            }
            else info = I18n.T("task.round", st.RoundIndex, st.RoundsTotal);
            pt.TextLeft(info, pt.F(10.5f), infoColor, new RectangleF(x, y, w, 16));
            y += 22;

            // 专注/休息时主界面已有「暂停 / 结束本轮」，卡片内不再重复按钮（图一）。
            // 只有"已偏离""程序已退出""程序已回来"这几种主界面表达不了的状态，才在卡片内给动作。
            if (gone || away)
            {
                bool canEarly = _app.Task.CanClaimEarly;
                var b1 = new RectangleF(x, y, 108, 34);
                var b2 = new RectangleF(x + 118, y, 76, 34);
                var b3 = new RectangleF(x + 204, y, 76, 34);

                if (away)
                {
                    DrawTaskButton(pt, t, b1, I18n.T("task.claimEarly"), canEarly, "taskClaimEarly",
                        delegate { if (canEarly) _app.Task.ClaimEarly(); });
                    DrawTaskButton(pt, t, b2, I18n.T("task.resume"), true, "taskResume",
                        delegate { _app.Task.ResumeFromAway(); });
                }
                else if (progAlive)
                {
                    // 程序已经回来了：给「继续」，由用户决定什么时候接着走（不自动抢跑）
                    DrawTaskButton(pt, t, b1, I18n.T("task.continue"), true, "taskContinue",
                        delegate { _app.Task.ResumeFromGone(); });
                    DrawTaskButton(pt, t, b2, I18n.T("task.end"), true, "taskEnd",
                        delegate { BeginTaskEnd(); });
                }
                else
                {
                    DrawTaskButton(pt, t, b1, I18n.T("task.claimEarly"), canEarly, "taskClaimEarly",
                        delegate { if (canEarly) _app.Task.ClaimEarly(); });
                    DrawTaskButton(pt, t, b2, I18n.T("task.end"), true, "taskEnd",
                        delegate { BeginTaskEnd(); });
                }

                // 手动刷新：任何"程序已退出 / 已回来"的状态都必须能自己重新检测一次
                DrawTaskButton(pt, t, b3, I18n.T("task.recheck"), true, "taskRecheck", delegate
                {
                    bool alive = _app.Task.Recheck();
                    ShowToast(I18n.T(alive ? "task.recheck.ok" : "task.recheck.fail"));
                });
            }
        }

        private void DrawTaskButton(Painter pt, Theme t, RectangleF r, string label, bool enabled,
                                    string id, Action onClick)
        {
            pt.FillRound(r, 17f, enabled ? t.AccentSoft : Theme.Alpha(t.SurfaceAlt, 160));
            pt.TextCenter(label, pt.F(11.5f, true), enabled ? t.AccentDark : Theme.Alpha(t.TextMuted, 140), r);
            if (enabled) Hot(id, r, onClick);
        }

        /// <summary>
        /// 呼出 Windows 文件对话框直接选目标 exe（比"3 秒抓前台"可靠：
        /// 不用抢焦点，程序没开也能先设定），之后用进程名 + 进程表判定是否在运行。
        /// </summary>
        private void PickProgram()
        {
            try
            {
                using (var dlg = new System.Windows.Forms.OpenFileDialog())
                {
                    dlg.Title = I18n.T("task.pick.title");
                    dlg.Filter = I18n.T("task.pick.filter");
                    dlg.CheckFileExists = true;
                    if (dlg.ShowDialog(Owner) != System.Windows.Forms.DialogResult.OK) return;
                    string path = dlg.FileName ?? "";
                    var cfg = _app.Data.TaskConfig;
                    cfg.ProcessName = TaskMath.Normalize(System.IO.Path.GetFileName(path));
                    cfg.Pid = 0;
                    cfg.WindowTitle = path;
                    _dirty = true;
                    _app.Save();
                }
            }
            catch (Exception) { }
        }

        // 任务程序图标缓存（详情行用；同一 exe 只取一次）
        private static readonly System.Collections.Generic.Dictionary<string, Bitmap> TaskIconCache =
            new System.Collections.Generic.Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);

        private static Bitmap TaskIcon(string exe)
        {
            if (string.IsNullOrEmpty(exe)) return null;
            lock (TaskIconCache)
            {
                Bitmap hit;
                if (TaskIconCache.TryGetValue(exe, out hit)) return hit;
                Bitmap bmp = null;
                try
                {
                    if (System.IO.File.Exists(exe))
                    {
                        using (var ico = System.Drawing.Icon.ExtractAssociatedIcon(exe))
                            if (ico != null) bmp = ico.ToBitmap();
                    }
                }
                catch (Exception) { bmp = null; }
                if (TaskIconCache.Count > 64)
                {
                    foreach (var b in TaskIconCache.Values) { try { if (b != null) b.Dispose(); } catch (Exception) { } }
                    TaskIconCache.Clear();
                }
                TaskIconCache[exe] = bmp;
                return bmp;
            }
        }

        /// <summary>点「结束」：任务走确认条（含「我已提前完成」选项）。</summary>
        private void BeginTaskEnd()
        {
            _confirmStop = true;
            A("confirm", 0f, 100f).Jump(1f);
            _dirty = true;
        }

        /// <summary>
        /// 「异常中断」弹窗的按钮行与提示文字布局（绘制与测试共用，避免再出现"文字压按钮"）。
        /// 返回：[0]=继续任务 [1]=结束任务 [2]=提示文字（宽度不足时为空矩形，不绘制）。
        /// 提示**必须**落在按钮右侧：早期把它放在按钮行内部，右对齐后正好盖住「结束任务」，
        /// 屏幕上糊成「结束任务·半收益结算」。
        /// </summary>
        internal static RectangleF[] InterruptLayout(RectangleF card)
        {
            float by = card.Bottom - 56;
            var cont = new RectangleF(card.Left + 24, by, 118, 38);
            var end = new RectangleF(cont.Right + 10, by, 96, 38);
            float hx = end.Right + 12;
            float hw = card.Right - 24 - hx;
            var hint = hw > 24f ? new RectangleF(hx, by + 10, hw, 18) : RectangleF.Empty;
            return new[] { cont, end, hint };
        }

        /// <summary>「任务被异常中断，是否继续？」弹窗（目标程序崩溃 / 程序自身崩溃后首启）。</summary>
        private void DrawTaskInterrupted(Painter pt)
        {
            var st = _app.Data.CurrentTask;
            if (st == null || st.Status != TaskState.StatusInterrupted) return;

            var t = pt.T;
            var b = Bounds;
            float w = Math.Min(520f, b.Width - 80f);
            float h = 208f;
            var card = new RectangleF(b.Width / 2f - w / 2f, b.Height / 2f - h / 2f, w, h);

            using (var dim = new SolidBrush(Theme.Alpha(Color.Black, 90)))
                pt.Raw.FillRectangle(dim, 0, 0, b.Width, b.Height);

            // 整窗屏障：热区"后注册者优先"，这一条必须紧跟遮罩注册，
            // 否则弹窗下方的休息卡、日历格子、日详情抽屉都还能被点中。
            Hot("taskInterruptBackdrop", new RectangleF(0, 0, b.Width, b.Height), null);

            pt.Shadow(card, 20f, 12f, t.Shadow);
            pt.FillRound(card, 20f, t.Surface);
            pt.StrokeRound(card, 20f, Theme.Alpha(t.Warn, 170), 1.4f);

            pt.Text(I18n.T("task.interrupt.title"), pt.F(15f, true), t.Warn,
                new RectangleF(card.Left + 24, card.Top + 20, w - 48, 24));

            // 每秒刷新一次目标程序是否已回来
            int nowMs = Environment.TickCount;
            if (unchecked(nowMs - _dialogCheckMs) > 1000)
            {
                _dialogCheckMs = nowMs;
                _dialogAlive = Native.ProcessAlive(st.ProcessName);
            }

            var task = _app.Task;
            string mid;
            Color mc;
            if (task.Grabbing) { mid = I18n.T("task.interrupt.grabbing"); mc = t.TextMuted; }
            else if (_dialogAlive) { mid = I18n.T("task.interrupt.found", st.ProcessName); mc = t.Good; }
            else { mid = I18n.T("task.interrupt.notFound"); mc = t.Warn; }
            pt.Text(mid, pt.F(12f), mc, new RectangleF(card.Left + 24, card.Top + 58, w - 140, 22));

            if (!task.Grabbing && !_dialogAlive)
            {
                var refresh = new RectangleF(card.Right - 24 - 72, card.Top + 56, 72, 22);
                pt.FillRound(refresh, 11f, t.SurfaceAlt);
                pt.TextCenter(I18n.T("task.interrupt.refresh"), pt.F(10f, true), t.AccentDark, refresh);
                Hot("taskRefresh", refresh, delegate { _dialogCheckMs = 0; _dirty = true; });
                if (!string.IsNullOrEmpty(task.GrabErrorKey))
                    pt.Text(I18n.T(task.GrabErrorKey), pt.F(10f), t.Warn,
                        new RectangleF(card.Left + 24, card.Top + 84, w - 48, 18));
            }

            var layout = InterruptLayout(card);
            var cont = layout[0];
            var end = layout[1];
            var hint = layout[2];

            pt.FillRound(cont, 19f, t.Accent);
            pt.TextCenter(I18n.T("task.interrupt.cont"), pt.F(12f, true), Color.White, cont);
            Hot("taskInterruptCont", cont, delegate { task.ContinueFromInterrupted(); _dirty = true; });

            pt.FillRound(end, 19f, t.SurfaceAlt);
            pt.TextCenter(I18n.T("task.interrupt.end"), pt.F(12f, true), t.TextMuted, end);
            Hot("taskInterruptEnd", end, delegate { task.EndFromInterrupted(); _dirty = true; });

            if (hint.Width > 24f)
                pt.TextLeft(I18n.T("task.interrupt.hint"), pt.F(9.5f), t.TextMuted, hint);
        }
    }
}
