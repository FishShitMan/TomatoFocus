using System;
using System.Drawing;
using System.Windows.Forms;
using TomatoFocus.Core;
using TomatoFocus.Ui;

namespace TomatoFocus.App
{
    /// <summary>
    /// 主窗口：无边框 + 自绘标题栏 + 自绘缩放边框，全部内容由 UiRoot 绘制。
    /// </summary>
    internal sealed class MainForm : Form
    {
        private const int WM_NCHITTEST = 0x0084;
        private const int WM_DPICHANGED = 0x02E0;
        private const int WM_NCLBUTTONDOWN = 0x00A1;
        private const int HTCAPTION = 2;
        private const int HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13,
                          HTTOPRIGHT = 14, HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;

        private readonly AppState _app;
        private readonly UiRoot _ui;
        private readonly Timer _loop;
        private readonly System.Diagnostics.Stopwatch _frameClock = System.Diagnostics.Stopwatch.StartNew();
        private double _lastFrameTime;
        private int _frameInterval = 250;
        private float _scale = 1f;
        private bool _applying;
        private bool _wasAnimating;
        private bool _centerOnScreen = true;
        private TimerPhase _iconPhase = (TimerPhase)(-1);
        private int _lastPaintSecond = -1;

        public event EventHandler HideToTrayRequested;
        public event EventHandler ExitRequested;
        public UiRoot Ui { get { return _ui; } }

        public bool AllowClose { get; set; }

        public MainForm(AppState app)
        {
            _app = app;
            _ui = new UiRoot(app);

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.None;   // 全部自绘，禁用 WinForms 自动缩放
            Text = "TomatoFocus";
            DoubleBuffered = true;
            KeyPreview = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

            _ui.SetWindowActions(MinimizeToTray, OnCloseButton);
            _ui.Owner = this;

            Native.BeginHighResolutionTimers();
            _loop = new Timer();
            _loop.Interval = 50;
            _loop.Tick += OnLoop;
            _loop.Start();

            ApplyIcon();
        }

        public void ApplyIcon()
        {
            try
            {
                var icon = Tray.TrayIconArt.AppIcon(_app.CurrentTheme);
                if (icon != null)
                {
                    if (Icon != null) Icon.Dispose();
                    Icon = icon;
                }
            }
            catch { }
        }

        /// <summary>任务栏图标与托盘保持一致：状态变化时重建（进度推进不重建，避免句柄开销）。</summary>
        public void UpdateDynamicIcon()
        {
            var phase = _app.Timer.Phase;
            if (phase == _iconPhase) return;
            _iconPhase = phase;
            try
            {
                var bmp = Tray.TrayIconArt.Render(32, phase, _app.Timer.Progress, _app.CurrentTheme);
                var icon = Tray.IconFactory.FromBitmap(bmp);
                bmp.Dispose();
                if (icon != null)
                {
                    var old = Icon;
                    Icon = icon;
                    if (old != null) old.Dispose();
                }
            }
            catch { }
        }

        private void MinimizeToTray()
        {
            var h = HideToTrayRequested;
            if (h != null) h(this, EventArgs.Empty);
        }

        private void OnCloseButton()
        {
            OnCloseButtonCore();
        }

        private void OnCloseButtonCore()
        {
            if (_app.Data.Settings.CloseToTray)
            {
                var h = HideToTrayRequested;
                if (h != null) h(this, EventArgs.Empty);
            }
            else
            {
                var h = ExitRequested;
                if (h != null) h(this, EventArgs.Empty);
            }
        }

        private void OnLoop(object sender, EventArgs e)
        {
            // 用 QPC 计时：DateTime.UtcNow 在 Windows 上只有约 15.6ms 分辨率，会导致动画一顿一顿
            double now = _frameClock.Elapsed.TotalSeconds;
            double realDt = now - _lastFrameTime;
            _lastFrameTime = now;
            if (realDt <= 0) realDt = 1.0 / 120.0;
            if (realDt > 1.0) realDt = 1.0;

            // 动画步进钳制到 1/30 秒：空闲时帧间隔可能是 250ms，
            // 若直接把 250ms 喂给缓动，动画会在第一帧就跳完（表现为"动效丢失"）
            double animDt = FrameTiming.ClampAnimationDelta(realDt);

            _app.Timer.Update();
            _app.Tick();
            UpdateDynamicIcon();
            _ui.Update(animDt, realDt);

            bool animating = _ui.IsAnimating;
            bool running = _app.Timer.IsRunning;
            int sec = _app.Timer.RemainingSeconds;
            bool secondChanged = sec != _lastPaintSecond;
            bool uiDirty = _ui.TakeDirty();

            // 帧率策略：交互后 1.2 秒与短动效 60fps / 计时中 20fps / 空闲 250ms
            int target = (animating || _ui.InteractionBurst > 0) ? 16 : (running ? 50 : 250);
            if (target != _frameInterval)
            {
                _frameInterval = target;
                _loop.Interval = target;
            }

            // 动画结束的那一帧必须再重绘一次，否则最后一帧（含未清除的粒子/特效）会残留在屏幕上
            if ((animating || running || secondChanged || _wasAnimating || uiDirty) && Visible)
            {
                _lastPaintSecond = sec;
                Invalidate();
            }
            _wasAnimating = animating;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.RoundCorners(Handle);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            ApplySize(true);
            Log.Info("shown scale=" + _scale.ToString("0.##") + " client=" + ClientSize.Width + "x" + ClientSize.Height +
                     " dip=" + _ui.Bounds.Width.ToString("0") + "x" + _ui.Bounds.Height.ToString("0"));
        }

        /// <summary>
        /// 用 Win32 以“物理像素 = DIP × 窗口真实 DPI”的方式设定尺寸。
        /// .NET Framework 的 WinForms 在 150% 缩放下会把窗体再次缩放，这里绕开它。
        /// </summary>
        private void ApplySize(bool force)
        {
            if (_applying) return;
            _applying = true;
            try
            {
                _scale = Native.WindowScale(Handle);
                int w = (int)Math.Round(1040 * _scale);
                int h = (int)Math.Round(700 * _scale);
                if (force || ClientSize.Width != w || ClientSize.Height != h)
                    Native.SetWindowPos(Handle, IntPtr.Zero, 0, 0, w, h,
                        Native.SWP_NOMOVE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);

                if (force && _centerOnScreen)
                {
                    // 尺寸是显示之后才设成 1040x700 DIP 的，CenterScreen 会按旧尺寸算中心，
                    // 因此这里显式把窗口移到工作区正中（自动避开任务栏）
                    _centerOnScreen = false;
                    var wa = Screen.FromControl(this).WorkingArea;
                    Native.SetWindowPos(Handle, IntPtr.Zero,
                        wa.Left + Math.Max(0, (wa.Width - w) / 2),
                        wa.Top + Math.Max(0, (wa.Height - h) / 2),
                        0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
                }
            }
            catch (Exception ex) { Log.Error("ApplySize", ex); }
            finally { _applying = false; }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            _scale = Native.WindowScale(Handle);
            _ui.Scale = _scale;
            _ui.Bounds = new RectangleF(0, 0, ClientSize.Width / _scale, ClientSize.Height / _scale);
            try { _ui.Draw(e.Graphics); }
            catch (Exception ex) { Log.Error("OnPaint", ex); }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // 全部由 OnPaint 绘制，避免闪烁
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            _ui.OnMouseMove(new PointF(e.X / _scale, e.Y / _scale), true);
            var want = _ui.HoverClickable ? Cursors.Hand : Cursors.Default;
            if (Cursor != want) Cursor = want;
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _ui.OnMouseLeave();
            if (Cursor != Cursors.Default) Cursor = Cursors.Default;
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                var p = new PointF(e.X / _scale, e.Y / _scale);
                if (_ui.IsDragArea(p))
                {
                    Native.ReleaseCapture();
                    Native.SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
                    return;
                }
                _ui.OnMouseDown(p);
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
                _ui.OnMouseUp(new PointF(e.X / _scale, e.Y / _scale));
            base.OnMouseUp(e);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            _ui.OnMouseWheel(e.Delta);
            base.OnMouseWheel(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            _ui.OnKey(e.KeyCode);
            base.OnKeyDown(e);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Invalidate();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!AllowClose && e.CloseReason == CloseReason.UserClosing && _app.Data.Settings.CloseToTray)
            {
                e.Cancel = true;
                MinimizeToTray();
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_CHAR = 0x0102;
            if (m.Msg == WM_CHAR && _ui.IsNoteFocused)
            {
                // 笔记聚焦时接管字符输入（含输入法提交的字符），并吞掉消息避免系统提示音
                char c = (char)(m.WParam.ToInt32() & 0xFFFF);
                if (c >= ' ') _ui.OnChar(c);
                _ui.MarkDirty();
                return;
            }

            const int WM_GETMINMAXINFO = 0x0024;
            if (m.Msg == WM_GETMINMAXINFO)
            {
                base.WndProc(ref m);
                try
                {
                    var mmi = (Native.MINMAXINFO)System.Runtime.InteropServices.Marshal.PtrToStructure(m.LParam, typeof(Native.MINMAXINFO));
                    float s = Native.WindowScale(Handle);
                    mmi.ptMinTrackSize.x = (int)(900 * s);
                    mmi.ptMinTrackSize.y = (int)(620 * s);
                    System.Runtime.InteropServices.Marshal.StructureToPtr(mmi, m.LParam, false);
                }
                catch (Exception) { }
                return;
            }

            if (m.Msg == WM_NCHITTEST)
            {
                base.WndProc(ref m);
                if ((int)m.Result == 1 /* HTCLIENT */)
                {
                    int lp = (int)(long)m.LParam;
                    int sx = (short)(lp & 0xFFFF);
                    int sy = (short)((lp >> 16) & 0xFFFF);
                    var p = PointToClient(new Point(sx, sy));
                    int b = (int)(7 * _scale);
                    bool left = p.X <= b, right = p.X >= ClientSize.Width - b;
                    bool top = p.Y <= b, bottom = p.Y >= ClientSize.Height - b;
                    if (top && left) m.Result = (IntPtr)HTTOPLEFT;
                    else if (top && right) m.Result = (IntPtr)HTTOPRIGHT;
                    else if (bottom && left) m.Result = (IntPtr)HTBOTTOMLEFT;
                    else if (bottom && right) m.Result = (IntPtr)HTBOTTOMRIGHT;
                    else if (left) m.Result = (IntPtr)HTLEFT;
                    else if (right) m.Result = (IntPtr)HTRIGHT;
                    else if (top) m.Result = (IntPtr)HTTOP;
                    else if (bottom) m.Result = (IntPtr)HTBOTTOM;
                }
                return;
            }

            if (m.Msg == WM_DPICHANGED)
            {
                var rect = (Native.RECT)System.Runtime.InteropServices.Marshal.PtrToStructure(m.LParam, typeof(Native.RECT));
                Bounds = new Rectangle(rect.left, rect.top, rect.right - rect.left, rect.bottom - rect.top);
                ApplySize(true);
                Invalidate();
            }

            base.WndProc(ref m);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _loop.Stop(); _loop.Dispose(); } catch { }
                Native.EndHighResolutionTimers();
            }
            base.Dispose(disposing);
        }
    }
}
