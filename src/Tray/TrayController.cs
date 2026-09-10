using System;
using System.Drawing;
using System.Windows.Forms;
using TomatoFocus.Core;
using TomatoFocus.Render;

namespace TomatoFocus.Tray
{
    /// <summary>
    /// 托盘：4 种状态用不同颜色区分，鼠标悬停显示剩余时间；
    /// 极简模式下窗口隐藏，全部功能由右键菜单提供。
    /// </summary>
    internal sealed class TrayController : IDisposable
    {
        private readonly AppState _app;
        private readonly NotifyIcon _icon;
        private readonly ContextMenuStrip _menu;
        private Icon _currentIcon;
        private TimerPhase _lastPhase = (TimerPhase)(-1);
        private int _lastRemaining = -1;
        private string _lastTip = "";
        private bool _lastMinimal;

        public event EventHandler ShowWindowRequested;
        public event EventHandler ExitRequested;

        public TrayController(AppState app)
        {
            _app = app;
            _menu = new ContextMenuStrip();
            _menu.ShowImageMargin = false;
            _menu.Renderer = new TrayMenuRenderer(app.CurrentTheme);
            _menu.DropShadowEnabled = true;
            _menu.Opened += delegate { ApplyRoundedRegion(); };
            _menu.SizeChanged += delegate { ApplyRoundedRegion(); };
            _menu.Opening += delegate { StartFade(true); };
            _menu.Closing += delegate (object s, ToolStripDropDownClosingEventArgs e)
            {
                if (_suppressClose) return;                 // 自己发起的关闭不再拦截
                if (_menu.Opacity > 0.05)
                {
                    e.Cancel = true;                        // 先淡出，再真正关闭
                    StartFade(false);
                }
            };

            _icon = new NotifyIcon();
            // 由系统托管弹出：点击外部必定关闭，且不会像手动 Show() 那样在任务栏留下条目
            _icon.ContextMenuStrip = _menu;
            _icon.Visible = true;
            _icon.MouseUp += delegate (object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left)
                {
                    var h = ShowWindowRequested;
                    if (h != null) h(this, EventArgs.Empty);
                }
            };
            EnsureMenu(true);      // 预构建：首次右键即为可用状态
            Refresh(true);
        }

        public void ApplyTheme()
        {
            _menu.Renderer = new TrayMenuRenderer(_app.CurrentTheme);
            _lastPhase = (TimerPhase)(-1);
            Refresh(true);
        }

        public void ShowBalloon(string title, string text)
        {
            try
            {
                _icon.BalloonTipTitle = title;
                _icon.BalloonTipText = text;
                _icon.ShowBalloonTip(4000);
            }
            catch { }
        }

        /// <summary>由渲染循环每秒调用一次；仅在状态或剩余秒数变化时真正更新。</summary>
        public void Refresh(bool force = false)
        {
            EnsureMenu(force);
            var timer = _app.Timer;
            int remaining = timer.RemainingSeconds;
            bool minimal = _app.Data.Settings.MinimalMode;

            if (!force && _lastPhase == timer.Phase && _lastRemaining == remaining && _lastMinimal == minimal)
                return;

            _lastPhase = timer.Phase;
            _lastRemaining = remaining;
            _lastMinimal = minimal;

            // 图标（仅状态变化时重建）
            if (force || _currentIcon == null || _lastTipPhase != timer.Phase)
            {
                var bmp = TrayIconArt.Render(32, timer.Phase, timer.Progress, _app.CurrentTheme);
                var newIcon = IconFactory.FromBitmap(bmp);
                bmp.Dispose();
                if (newIcon != null)
                {
                    if (_currentIcon != null) _currentIcon.Dispose();
                    _currentIcon = newIcon;
                    _icon.Icon = _currentIcon;
                }
                _lastTipPhase = timer.Phase;
            }

            string tip;
            string clock = TomatoMath.FormatClock(remaining);
            switch (timer.Phase)
            {
                case TimerPhase.Focusing: tip = I18n.T("tray.tip.running", clock); break;
                case TimerPhase.Paused: tip = I18n.T("tray.tip.paused", clock); break;
                case TimerPhase.Break: tip = I18n.T("tray.tip.breaking", clock); break;
                default: tip = I18n.T("tray.tip.idle", TomatoMath.Format(TodayTenths())); break;
            }
            if (tip.Length > 120) tip = tip.Substring(0, 120);
            if (tip != _lastTip)
            {
                _lastTip = tip;
                try { _icon.Text = tip; } catch { }
            }
        }

        private TimerPhase _lastTipPhase = (TimerPhase)(-1);

        // --- 菜单动效与圆角 --------------------------------------------------
        private System.Windows.Forms.Timer _fadeTimer;
        private bool _fadeIn;
        private bool _suppressClose;
        private Region _menuRegion;

        private void StartFade(bool fadeIn)
        {
            _fadeIn = fadeIn;
            try { _menu.Opacity = fadeIn ? 0.05 : Math.Max(0.05, _menu.Opacity); } catch (Exception) { }
            if (_fadeTimer == null)
            {
                _fadeTimer = new System.Windows.Forms.Timer();
                _fadeTimer.Interval = 16;
                _fadeTimer.Tick += delegate { StepFade(); };
            }
            _fadeTimer.Start();
        }

        private void StepFade()
        {
            try
            {
                const double step = 16.0 / 110.0;     // 约 110ms 完成
                if (_fadeIn)
                {
                    double o = _menu.Opacity + step;
                    if (o >= 1.0) { _menu.Opacity = 1.0; _fadeTimer.Stop(); }
                    else _menu.Opacity = o;
                }
                else
                {
                    double o = _menu.Opacity - step;
                    if (o <= 0.05)
                    {
                        _menu.Opacity = 1.0;
                        _fadeTimer.Stop();
                        _suppressClose = true;
                        try { _menu.Close(); } finally { _suppressClose = false; }
                    }
                    else _menu.Opacity = o;
                }
            }
            catch (Exception) { try { _fadeTimer.Stop(); } catch (Exception) { } }
        }

        /// <summary>给菜单套上圆角（与主窗口同一套设计语言）。</summary>
        private void ApplyRoundedRegion()
        {
            try
            {
                if (_menu.Width <= 0 || _menu.Height <= 0) return;
                using (var path = TomatoFocus.Render.Painter.RoundedPath(
                    new RectangleF(0, 0, _menu.Width, _menu.Height), 10f))
                {
                    var region = new Region(path);
                    var old = _menuRegion;
                    _menuRegion = region;
                    _menu.Region = region;
                    if (old != null) old.Dispose();
                }
            }
            catch (Exception) { }
        }

        private int TodayTenths() { return Stats.TodayTenths(_app.Data); }

        private void BuildMenu()
        {
            BuildMenuInto(_app, _menu,
                delegate { var h = ShowWindowRequested; if (h != null) h(this, EventArgs.Empty); },
                delegate { var h = ExitRequested; if (h != null) h(this, EventArgs.Empty); });
        }

        private string _menuSignature = "";

        /// <summary>
        /// 菜单在"状态变化"时预构建，而不是在弹出瞬间重建——
        /// 这样既能保证内容实时，又不会出现首次右键空菜单的问题。
        /// </summary>
        private void EnsureMenu(bool force)
        {
            string sig = (int)_app.Timer.Phase + "|" + _app.Data.Settings.MinimalMode + "|" +
                         _app.Data.Settings.AutoStart + "|" + Stats.TodayTenths(_app.Data) + "|" +
                         _app.Data.Wallet.UsableTenths + "|" + _app.Data.Settings.CustomMinutes;
            if (!force && sig == _menuSignature) return;
            _menuSignature = sig;
            BuildMenu();
        }

        /// <summary>
        /// 把托盘菜单填进指定菜单（静态实现，便于在不创建 NotifyIcon 的情况下测试菜单内容）。
        /// </summary>
        internal static void BuildMenuInto(AppState app, ContextMenuStrip menu, EventHandler showWindow, EventHandler exit)
        {
            menu.Items.Clear();
            var timer = app.Timer;
            if (timer.Phase == TimerPhase.Focusing)
            {
                AddItem(menu, I18n.T("menu.pause"), delegate { timer.Pause(); });
                AddItem(menu, I18n.T("menu.stop"), delegate { if (timer.Phase == TimerPhase.Focusing || timer.Phase == TimerPhase.Paused) timer.StopEarly(); });
            }
            else if (timer.Phase == TimerPhase.Paused)
            {
                AddItem(menu, I18n.T("menu.resume"), delegate { timer.Resume(); });
                AddItem(menu, I18n.T("menu.stop"), delegate { if (timer.Phase == TimerPhase.Focusing || timer.Phase == TimerPhase.Paused) timer.StopEarly(); });
            }
            else if (timer.Phase == TimerPhase.Break)
            {
                AddItem(menu, I18n.T("break.skip"), delegate { timer.SkipBreak(); });
            }
            else
            {
                var preset = new ToolStripMenuItem(I18n.T("menu.preset"));
                preset.DropDownItems.Add(MenuPreset(app, I18n.T("menu.preset.5"), 5, "5"));
                preset.DropDownItems.Add(MenuPreset(app, I18n.T("menu.preset.15"), 15, "15"));
                preset.DropDownItems.Add(MenuPreset(app, I18n.T("menu.preset.25"), 25, "25"));
                if (app.Data.Settings.CustomMinutes > 0)
                    preset.DropDownItems.Add(MenuPreset(app, I18n.T("focus.minutes", app.Data.Settings.CustomMinutes),
                        app.Data.Settings.CustomMinutes, "custom"));
                menu.Items.Add(preset);
                AddItem(menu, I18n.T("menu.start"), delegate
                {
                    int m = Math.Max(1, app.Timer.PlannedSeconds / 60);
                    app.Timer.StartFocus(m, app.Timer.Preset);
                });
            }

            menu.Items.Add(new ToolStripSeparator());
            AddItem(menu, I18n.T("menu.showWindow"), showWindow);

            var minimal = new ToolStripMenuItem(I18n.T("menu.minimal"));
            minimal.Checked = app.Data.Settings.MinimalMode;
            minimal.Click += delegate { app.SetMinimalMode(!app.Data.Settings.MinimalMode); };
            menu.Items.Add(minimal);

            var auto = new ToolStripMenuItem(I18n.T("menu.autoStart"));
            auto.Checked = app.Data.Settings.AutoStart;
            auto.Click += delegate { app.SetAutoStart(!app.Data.Settings.AutoStart); };
            menu.Items.Add(auto);

            menu.Items.Add(new ToolStripSeparator());
            var info = new ToolStripMenuItem(I18n.T("menu.todayInfo",
                TomatoMath.Format(Stats.TodayTenths(app.Data)) + "（" +
                I18n.T("wallet.usable", TomatoMath.Format(app.Data.Wallet.UsableTenths)).Replace(I18n.T("wallet.usablePrefix"), "") + "）"));
            info.Enabled = false;
            menu.Items.Add(info);

            AddItem(menu, I18n.T("menu.quit"), exit);
        }

        private static ToolStripMenuItem MenuPreset(AppState app, string text, int minutes, string preset)
        {
            var item = new ToolStripMenuItem(text);
            item.Click += delegate { app.Timer.StartFocus(minutes, preset); };
            return item;
        }

        private static void AddItem(ContextMenuStrip menu, string text, EventHandler onClick)
        {
            var item = new ToolStripMenuItem(text);
            if (onClick != null) item.Click += onClick;
            menu.Items.Add(item);
        }

        private void StopWithRecord()
        {
            if (_app.Timer.Phase != TimerPhase.Focusing && _app.Timer.Phase != TimerPhase.Paused) return;
            _app.Timer.StopEarly();   // 触发半收益结算
        }

        public void Dispose()
        {
            try { if (_fadeTimer != null) { _fadeTimer.Stop(); _fadeTimer.Dispose(); } } catch { }
            try { if (_menuRegion != null) { _menuRegion.Dispose(); _menuRegion = null; } } catch { }
            try { _icon.Visible = false; _icon.Dispose(); } catch { }
            try { _menu.Dispose(); } catch { }
            if (_currentIcon != null) { _currentIcon.Dispose(); _currentIcon = null; }
        }

        /// <summary>测试用：构建菜单并返回菜单项数量（不创建 NotifyIcon）。</summary>
        public static int BuildMenuForTest(AppState app, ContextMenuStrip menu)
        {
            BuildMenuInto(app, menu, null, null);
            return menu.Items.Count;
        }

        /// <summary>让菜单和整体风格一致。</summary>
        private sealed class TrayMenuRenderer : ToolStripProfessionalRenderer
        {
            private readonly Theme _theme;

            public TrayMenuRenderer(Theme theme) : base(new Colors(theme)) { _theme = theme; }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                e.TextColor = e.Item.Enabled
                    ? (e.Item.Selected ? _theme.AccentDark : _theme.Text)
                    : Theme.Alpha(_theme.TextMuted, 150);
                base.OnRenderItemText(e);
            }

            protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
            {
                // 圆角背景（与主窗口同一套设计语言）
                var rect = new RectangleF(0, 0, e.ToolStrip.Width, e.ToolStrip.Height);
                using (var path = TomatoFocus.Render.Painter.RoundedPath(rect, 10f))
                using (var brush = new SolidBrush(_theme.Surface))
                    e.Graphics.FillPath(brush, path);
            }

            protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
            {
                var rect = new RectangleF(0.5f, 0.5f, e.ToolStrip.Width - 1f, e.ToolStrip.Height - 1f);
                using (var path = TomatoFocus.Render.Painter.RoundedPath(rect, 10f))
                using (var pen = new Pen(Theme.Alpha(_theme.Border, 210)))
                    e.Graphics.DrawPath(pen, path);
            }

            private sealed class Colors : ProfessionalColorTable
            {
                private readonly Theme _t;
                public Colors(Theme t) { _t = t; }
                public override Color ToolStripDropDownBackground { get { return _t.Surface; } }
                public override Color ImageMarginGradientBegin { get { return _t.Surface; } }
                public override Color ImageMarginGradientMiddle { get { return _t.Surface; } }
                public override Color ImageMarginGradientEnd { get { return _t.Surface; } }
                public override Color MenuBorder { get { return Theme.Alpha(_t.Border, 200); } }
                public override Color MenuItemSelected { get { return _t.AccentSoft; } }
                public override Color MenuItemBorder { get { return _t.AccentSoft; } }
                public override Color MenuItemSelectedGradientBegin { get { return _t.AccentSoft; } }
                public override Color MenuItemSelectedGradientEnd { get { return _t.AccentSoft; } }
                public override Color SeparatorDark { get { return Theme.Alpha(_t.Border, 220); } }
                public override Color SeparatorLight { get { return _t.Surface; } }
                public override Color CheckBackground { get { return _t.Accent; } }
                public override Color CheckSelectedBackground { get { return _t.AccentDark; } }
            }
        }
    }
}
