using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;
using TomatoFocus.Core;
using TomatoFocus.Tray;

namespace TomatoFocus.App
{
    /// <summary>应用外壳：把状态、主窗口、托盘串起来。</summary>
    internal sealed class AppShell : ApplicationContext
    {
        private readonly AppState _app = new AppState();
        private readonly MainForm _form;
        private readonly TrayController _tray;
        private bool _exiting;

        public AppShell(bool startMinimized) : this(startMinimized, null, null, 0) { }

        public AppShell(bool startMinimized, string shotPath) : this(startMinimized, shotPath, null, 0) { }

        public AppShell(bool startMinimized, string shotPath, string demoDir) : this(startMinimized, shotPath, demoDir, 0) { }

        public AppShell(bool startMinimized, string shotPath, string demoDir, int autoStartMinutes)
        {
            _app.Load();
            if (!System.IO.File.Exists(AppPaths.DataFile)) _app.Save();   // 首次运行即建立数据文件

            _app.Timer.BreakCompleted += OnBreakCompleted;
            _app.Timer.Suspended += OnSuspended;
            _app.Timer.StateChanged += OnStateChanged;
            _app.SessionRecorded += OnSessionRecorded;
            _app.ToastRequested += OnToast;
            _app.AchievementsUnlocked += OnAchievementsUnlocked;
            _app.Changed += OnAppChanged;
            _app.MinimalModeChanged += delegate { ApplyMinimalMode(); };
            _app.ClockTick += delegate { _tray.Refresh(); };   // 悬停提示里的剩余时间逐秒刷新

            _form = new MainForm(_app);
            _form.HideToTrayRequested += delegate { HideToTray(); };
            _form.ExitRequested += delegate { ExitApp(); };

            _tray = new TrayController(_app);
            _tray.ShowWindowRequested += delegate { ShowMainWindow(); };
            _tray.ExitRequested += delegate { ExitApp(); };

            _form.FormClosed += delegate { if (!_exiting) ExitApp(); };

            // 自检用：启动即开始一轮专注（便于测量计时状态的性能）
            if (autoStartMinutes > 0) _app.Timer.StartFocus(autoStartMinutes, "custom");

            // 注意：给 ApplicationContext.MainForm 赋值会让 Application.Run 自动 Show() 主窗口，
            // 因此只有需要显示窗口时才挂载 MainForm。
            // 手动双击启动一律显示窗口（极简模式只表示"关闭窗口后收纳进托盘"），
            // 只有开机自启传入 --minimized 时才静默启动
            bool hiddenStart = startMinimized;
            if (!hiddenStart)
            {
                MainForm = _form;
                ShowMainWindow();
            }
            else
            {
                _form.ShowInTaskbar = false;
                HideToTray();
            }

            Log.Info("shell startMinimized=" + startMinimized + " minimal=" + _app.Data.Settings.MinimalMode +
                     " visible=" + _form.Visible + " handle=" + _form.IsHandleCreated);

            // 兜底：启动后 250ms 再确认一次隐藏状态
            if (hiddenStart)
            {
                var guard = new Timer();
                guard.Interval = 250;
                guard.Tick += delegate
                {
                    guard.Stop();
                    guard.Dispose();
                    if (_form.Visible) _form.Hide();
                    _form.ShowInTaskbar = false;
                };
                guard.Start();
            }

            if (!string.IsNullOrEmpty(shotPath))
            {
                // 自截图模式：真实窗口渲染一帧后保存并退出（用于验收）
                var once = new Timer();
                once.Interval = 1500;
                once.Tick += delegate
                {
                    once.Stop();
                    once.Dispose();
                    try { CaptureTo(shotPath); }
                    catch (Exception ex) { Log.Error("CaptureTo", ex); }
                    ExitApp();
                };
                once.Start();
            }
            else if (!string.IsNullOrEmpty(demoDir))
            {
                RunDemo(demoDir);
            }
        }

        /// <summary>
        /// 演示/自检模式：记录一次完整专注，然后在动画进行中与结束后分别截图，
        /// 用于验证“特效是否按时清除、窗口是否已停止重绘”。
        /// </summary>
        private void RunDemo(string dir)
        {
            try { System.IO.Directory.CreateDirectory(dir); } catch { }
            var args = new FocusCompletedEventArgs();
            args.FocusedSeconds = 25 * 60;
            args.PlannedSeconds = 25 * 60;
            args.Aborted = false;
            args.Preset = "25";
            args.StartedLocal = DateTime.Now;

            var t = new Timer();
            t.Interval = 1000;
            int step = 0;
            t.Tick += delegate
            {
                step++;
                try
                {
                    if (step == 1)
                    {
                        _app.RecordSession(args);
                        _form.Ui.Celebrate();
                        _form.Invalidate();
                    }
                    else if (step == 2) CaptureTo(System.IO.Path.Combine(dir, "demo-a-animating.png"));
                    else if (step == 6) CaptureTo(System.IO.Path.Combine(dir, "demo-b-settled.png"));
                    else if (step == 10)
                    {
                        CaptureTo(System.IO.Path.Combine(dir, "demo-c-stable.png"));
                        t.Stop();
                        t.Dispose();
                        ExitApp();
                    }
                }
                catch (Exception ex) { Log.Error("RunDemo", ex); }
            };
            t.Start();
        }

        /// <summary>把真实窗口内容保存成 PNG（走 WM_PRINT，等价于屏幕所见）。</summary>
        public void CaptureTo(string path)
        {
            using (var bmp = new Bitmap(_form.ClientSize.Width, _form.ClientSize.Height))
            {
                _form.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
                bmp.Save(path, ImageFormat.Png);
            }
            Console.WriteLine("shot -> " + path + " " + _form.ClientSize.Width + "x" + _form.ClientSize.Height);
        }

        public AppState State { get { return _app; } }

        private void OnAppChanged(object sender, EventArgs e)
        {
            _tray.Refresh();
            if (_form != null && _form.Visible) _form.Invalidate();
        }

        private void OnStateChanged(object sender, EventArgs e)
        {
            _tray.Refresh();
            if (_form != null && _form.Visible) _form.Invalidate();
        }

        /// <summary>会话已由 AppState 记录完成，这里只做 UI 反应。</summary>
        private void OnSessionRecorded(object sender, SessionRecord rec)
        {
            if (rec == null) return;

            if (rec.Aborted)
                Sound.Play(SoundId("soft"), _app.Data.Settings.Sound);
            else
                Sound.Play(SoundId("chime"), _app.Data.Settings.Sound);

            if (_form != null && _form.Visible)
            {
                _form.Ui.Celebrate();
                _form.Invalidate();
            }
            else
            {
                _tray.ShowBalloon(I18n.T("app.title"), TomatoMath.Format(rec.Tenths) + " 颗");
            }
        }

        private void OnBreakCompleted(object sender, EventArgs e)
        {
            _app.ShowToast(I18n.T("break.done"));
            Sound.Play(SoundId("start"), _app.Data.Settings.Sound);
        }

        /// <summary>按已装备的提示音奖励选择音色。</summary>
        private string SoundId(string fallback)
        {
            string eq = _app.Data.Rewards.EquippedSound;
            if (eq == "sn_soft") return "soft";
            if (eq == "sn_chime") return "chime";
            return fallback;
        }

        private void OnSuspended(object sender, EventArgs e)
        {
            _app.ShowToast(I18n.T("focus.paused"));
            if (!_form.Visible) _tray.ShowBalloon(I18n.T("app.title"), I18n.T("focus.paused"));
        }

        private void OnToast(object sender, string message)
        {
            if (_form != null && _form.Visible) _form.Ui.ShowToast(message);
        }

        private void OnAchievementsUnlocked(object sender, List<AchievementDef> list)
        {
            if (list == null || list.Count == 0) return;
            if (_form != null && _form.Visible)
            {
                _form.Ui.NotifyAchievements(list);
                _form.Ui.ShowToast(I18n.T("ach.new") + " " + I18n.T(list[0].NameKey));
                _form.Invalidate();
            }
            else
            {
                _tray.ShowBalloon(I18n.T("ach.new"), I18n.T(list[0].NameKey));
            }
            Sound.Play("chime", _app.Data.Settings.Sound);
        }

        private void ApplyMinimalMode()
        {
            if (_app.Data.Settings.MinimalMode)
            {
                _form.Ui.CloseDrawer();      // 先收起抽屉，避免下次唤出窗口时抽屉还开着
                _form.Hide();
                _form.ShowInTaskbar = false;
            }
            else
            {
                ShowMainWindow();
            }
            _tray.Refresh(true);
        }

        private void ShowMainWindow()
        {
            try
            {
                // 语义统一：窗口出现 = 退出极简模式（否则设置页里的开关会与窗口状态不一致）
                _app.ExitMinimalMode();
                _form.ShowInTaskbar = true;
                if (!_form.Visible) _form.Show();
                if (_form.WindowState == FormWindowState.Minimized) _form.WindowState = FormWindowState.Normal;
                _form.Activate();
                _form.BringToFront();
                _form.Invalidate();
            }
            catch (Exception ex) { Log.Error("ShowMainWindow", ex); }
        }

        private void HideToTray()
        {
            _form.Hide();
        }

        private void ExitApp()
        {
            if (_exiting) return;
            _exiting = true;
            try { _app.Save(); } catch { }
            try { _tray.Dispose(); } catch { }
            try
            {
                _form.AllowClose = true;
                _form.Close();
            }
            catch { }
            ExitThread();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _app.Save(); } catch { }
            }
            base.Dispose(disposing);
        }
    }
}
