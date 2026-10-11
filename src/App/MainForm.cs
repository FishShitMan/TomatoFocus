using System;
using System.Drawing;
using System.Threading;
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
        private readonly System.Diagnostics.Stopwatch _frameClock = System.Diagnostics.Stopwatch.StartNew();
        private readonly System.Diagnostics.Stopwatch _paintClock = new System.Diagnostics.Stopwatch();
        private double _lastFrameTime;
        private float _scale = 1f;
        private bool _applying;
        private bool _wasAnimating;
        private bool _centerOnScreen = true;
        private TimerPhase _iconPhase = (TimerPhase)(-1);
        private int _lastPaintSecond = -1;
        private bool _imeNoteOpen;
        private double _lastInputTime;          // 最后一次鼠标/键盘操作时刻（秒，_frameClock 基准）
        private int _noteChars;                 // 笔记聚焦期间收到的字符数（诊断用）
        private int _lastCharCode = -1;         // 最后一个字符的码点（诊断用）
        private int _imeOpenCount;              // 笔记聚焦打开输入法的次数（诊断用）
        private int _imeCompositionCount;       // 输入法开始组字的次数（诊断用）
        private int _imeResultCount;            // 输入法提交结果的次数（诊断用）
        private int _imeHintShown;              // 是否已提示过"系统没有中文输入法"

        /// <summary>是否把每秒帧统计写进日志（--frames-log，用于验证降档）。</summary>
        public static bool LogFrames;

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
            // 输入法完全自己管：NoControl 让 WinForms 不碰输入法状态，
            // 我们再显式关联上下文、并在笔记聚焦时打开输入法（见 EnsureImeContext / SyncImeWithNoteFocus）。
            ImeMode = ImeMode.NoControl;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

            _ui.SetWindowActions(MinimizeToTray, OnCloseButton);
            _ui.DiagnosticsRequested += ExportDiagnostics;
            _ui.Owner = this;

            Native.BeginHighResolutionTimers();
            ApplyIcon();
        }

        // --- 渲染泵 -------------------------------------------------------
        // 不再用 WinForms Timer 驱动帧：WM_TIMER 是低优先级消息且会被合并，
        // 设 16ms 实测只跑出 31~32ms（等效 31fps），观感就是"动效卡顿"。
        // 改为独立线程按 QPC 自校正节拍投递自定义消息，UI 线程收到后渲染一帧；
        // 若上一帧还没处理完则跳过本次投递，绝不堆积消息。
        private const int WM_FRAME = 0x0400 + 1;
        private Thread _pump;
        private volatile bool _pumpStop;
        private volatile int _frameInterval = 250;
        private int _framePending;

        private void StartPump()
        {
            if (_pump != null) return;
            _pumpStop = false;
            _pump = new Thread(PumpLoop);
            _pump.IsBackground = true;
            _pump.Name = "TomatoFocus.RenderPump";
            _pump.Start();
        }

        private void StopPump()
        {
            _pumpStop = true;
            _pump = null;
        }

        private void PumpLoop()
        {
            int lastPost = Environment.TickCount;
            while (!_pumpStop)
            {
                // 高帧率档补掉约 2ms 的调度开销（Sleep 返回 + 消息投递 + WM_PAINT 派发），
                // 否则 16ms 档实测只有 ~50fps。
                int ms = _frameInterval - (_frameInterval <= FrameTiming.BurstMs ? 2 : 0);
                if (ms < 1) ms = 1;
                if (PerfProbe.Active != null) PerfProbe.NoteSleep(ms);
                // 睡满即投递，并在醒来时刻重新对齐下一拍：
                // 既不累积漂移，也不去"补帧"；上一帧没处理完时 CompareExchange 会失败，自然形成背压。
                try { Thread.Sleep(ms); } catch { }
                if (_pumpStop) break;

                if (Interlocked.CompareExchange(ref _framePending, 1, 0) == 0)
                {
                    lastPost = Environment.TickCount;
                    try
                    {
                        if (IsHandleCreated)
                        {
                            if (PerfProbe.Active != null) PerfProbe.Posts++;
                            // 投递失败也必须立刻复位，否则背压标记会永久停在 1（同一个坑）
                            if (!Native.PostMessage(Handle, WM_FRAME, IntPtr.Zero, IntPtr.Zero))
                                Interlocked.Exchange(ref _framePending, 0);
                        }
                        else Interlocked.Exchange(ref _framePending, 0);
                    }
                    catch { Interlocked.Exchange(ref _framePending, 0); }
                }
                else
                {
                    // 看门狗兜底：WM_FRAME 只在 WndProc 里被消费时才复位背压标记，
                    // 一旦这条消息以任何方式丢失（句柄被重建、队列被清空…），标记就会永远停在 1，
                    // 渲染泵从此一帧都不再投递 —— 整个程序卡死，连倒计时都不走。
                    // 这里给"投递后长时间没人消费"设一条底线，宁可多发一帧也绝不允许泵停摆。
                    int quiet = unchecked(Environment.TickCount - lastPost);
                    if (quiet > PumpStallMs)
                    {
                        Interlocked.Exchange(ref _framePending, 0);
                        _pumpStalls++;
                        Log.Info("renderPump: 帧消息 " + quiet + "ms 未被消费，已强制复位（累计 " + _pumpStalls + " 次）");
                    }
                }
            }
        }

        /// <summary>投递后多久没被消费就认为消息已丢失（毫秒）。正常队列延迟在 100ms 以内。</summary>
        private const int PumpStallMs = 2000;

        private int _pumpStalls;
        /// <summary>诊断用：渲染泵因消息丢失而强制复位的次数（正常应恒为 0）。</summary>
        public int PumpStalls { get { return _pumpStalls; } }

        private int _loopTicks;
        /// <summary>诊断用：渲染循环已执行的帧数。停止增长即说明渲染泵停摆。</summary>
        public int LoopTicks { get { return _loopTicks; } }

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
            _loopTicks++;
            // 用 QPC 计时：DateTime.UtcNow 在 Windows 上只有约 15.6ms 分辨率，会导致动画一顿一顿
            double now = _frameClock.Elapsed.TotalSeconds;
            double realDt = now - _lastFrameTime;
            _lastFrameTime = now;
            if (realDt <= 0) realDt = 1.0 / 120.0;
            if (realDt > 1.0) realDt = 1.0;

            // 动画步进钳制到 1/30 秒：空闲时帧间隔可能是 250ms，
            // 若直接把 250ms 喂给缓动，动画会在第一帧就跳完（表现为"动效丢失"）
            double animDt = FrameTiming.ClampAnimationDelta(realDt);

            long tsLogic = PerfCounters.Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            _app.Timer.Update();
            _app.Tick();
            UpdateDynamicIcon();
            PerfCounters.Add("帧-逻辑(含托盘)", tsLogic);
            long tsUi = PerfCounters.Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            _ui.Update(animDt, realDt);
            SyncImeWithNoteFocus();
            PerfCounters.Add("帧-UI更新", tsUi);

            // 性能自检：分段推进场景，并保证"动画段"一直在动
            var probe = PerfProbe.Active;
            if (probe != null)
            {
                probe.Tick(OnPerfPhase);
                if (probe.Phase == 1 && now - probe.LastCelebrate > 1.1)
                {
                    probe.LastCelebrate = now;
                    _ui.Celebrate();
                }
                if (probe.Finished)
                {
                    PerfProbe.Active = null;
                    probe.WriteReport(PerfProbe.OutputPath);
                    var exit = ExitRequested;
                    if (exit != null) exit(this, EventArgs.Empty);
                    return;
                }
            }

            bool animating = _ui.IsAnimating;
            bool running = _app.Timer.IsRunning;
            int sec = _app.Timer.RemainingSeconds;
            bool secondChanged = sec != _lastPaintSecond;
            bool uiDirty = _ui.TakeDirty();

            // 本帧是否**必须**整窗重绘：有动画在跑、动画刚结束（要擦掉最后一帧残留）、
            // 界面状态变化、或秒数变了（时钟与进度环每秒才变一次）。
            // 把"是否必须整窗重绘"独立出来，是为了让计时中也能安全走快路径。
            bool needFullRedraw = animating || _wasAnimating || uiDirty || secondChanged;

            // 帧率策略；_frameInterval 由渲染泵线程读取，改档无需重建计时器。
            // 只有勋章在变时走"勋章快路径"：贴缓存 + 只重画那几枚勋章，因此可以给到 30fps。
            // 前台/后台：窗口没有焦点时完全不跑常驻动效（后台挂着 = 零重绘）。
            // 输入空闲降档：连续 AmbientIdleAfterSeconds 秒没有任何鼠标/键盘操作，
            // 常驻档从 30fps 降到约 6fps；一旦有操作立即恢复。
            //
            // 计时进行中**同样**走快路径：秒数与进度环本来就每秒才变一次，而以前只要 running
            // 就每帧 Invalidate，等于每秒白刷 19 次整窗（实测计时段要吃掉 1/3 个 CPU 核）。
            // 现在计时中只有"秒变化"那一下才整窗重绘、顺手刷新缓存位图，其余帧全是廉价贴图。
            // InteractionBurst 只负责把帧率拉到 60fps，不再强制整窗重绘——真正需要整窗重绘的
            // 时刻（悬停/按压动画）都已经由 animating/uiDirty 覆盖，不必再靠爆发档兜底。
            bool ambientMedal = _ui.CanDrawMedalOnly && ContainsFocus && Visible;
            _medalOnlyFrame = ambientMedal && _cacheValid && CacheMatches() && !needFullRedraw;
            if (needFullRedraw) _fullRedrawPending = true;

            // 快路径本帧只需刷新"勋章 + 笔记光标"那一小块：算出它的设备像素矩形，
            // 用 Invalidate(rect) 让系统只重画这一块——全窗贴图实测 3.9ms，其中绝大多数
            // 是白刷的像素；按面积裁掉之后快路径的单帧成本几乎归零。
            _fastRect = Rectangle.Empty;
            if (_medalOnlyFrame)
            {
                var ar = _ui.AmbientDirtyRect;
                if (ar.Width > 0.5f && ar.Height > 0.5f)
                {
                    int x0 = (int)Math.Floor(ar.Left * _scale) - 2;
                    int y0 = (int)Math.Floor(ar.Top * _scale) - 2;
                    int x1 = (int)Math.Ceiling(ar.Right * _scale) + 2;
                    int y1 = (int)Math.Ceiling(ar.Bottom * _scale) + 2;
                    if (x0 < 0) x0 = 0;
                    if (y0 < 0) y0 = 0;
                    if (x1 > ClientSize.Width) x1 = ClientSize.Width;
                    if (y1 > ClientSize.Height) y1 = ClientSize.Height;
                    if (x1 > x0 && y1 > y0) _fastRect = new Rectangle(x0, y0, x1 - x0, y1 - y0);
                }
            }
            double idleSeconds = now - _lastInputTime;
            int ambientMs = FrameTiming.AmbientMs(idleSeconds);

            int target = !Visible ? FrameTiming.IdleMs
                       : (animating || _ui.InteractionBurst > 0) ? FrameTiming.BurstMs
                       : (_medalOnlyFrame ? ambientMs
                       : (running ? FrameTiming.RunningMs : FrameTiming.IdleMs));
            // 帧统计：只在 --perf 自检或显式开启时输出，避免平时刷屏
            if ((probe != null || LogFrames) && now - _lastAmbientLog > 1.0 && _ui.CanDrawMedalOnly)
            {
                _lastAmbientLog = now;
                Log.Info("frames/s: 快路径=" + (_fastPathFrames - _lastFast) + " 全窗=" + (_fullFrames - _lastFull) +
                         " 档位=" + _frameInterval + "ms 空闲=" + idleSeconds.ToString("0.0") + "s" +
                         " animating=" + animating + " ambient=" + ambientMedal +
                         " 勋章数=" + _ui.AmbientMedalCount + " focus=" + ContainsFocus);
                _lastFast = _fastPathFrames;
                _lastFull = _fullFrames;
            }
            _frameInterval = target;
            if (probe != null) probe.NoteTarget(target);

            // 动画结束的那一帧必须再重绘一次，否则最后一帧（含未清除的粒子/特效）会残留在屏幕上。
            // 条件里刻意**不含 running**：计时中只有"秒变化"或内容真的变了才整窗重绘；
            // 若没装备勋章，这一秒内根本不需要重绘——计时期间的重绘次数因此从 20 次/秒降到 1 次/秒。
            if ((needFullRedraw || ambientMedal) && Visible)
            {
                _lastPaintSecond = sec;
                if (!needFullRedraw && !_fastRect.IsEmpty) Invalidate(_fastRect);   // 只刷勋章/光标
                else Invalidate();                                                  // 整窗
            }
            _wasAnimating = animating;
        }

        // --- 帧缓存（勋章快路径）------------------------------------------
        private Bitmap _frameCache;
        private Graphics _frameCacheG;
        private bool _cacheValid;
        private bool _medalOnlyFrame;
        /// <summary>本帧有"必须整窗重绘"的理由（动画/脏/秒变化）。由 OnLoop 置位、DrawFrame 消费，
        /// 这样即使 WM_PAINT 被推迟到下一轮循环之后，也不会误走快路径贴出过期画面。</summary>
        private bool _fullRedrawPending;
        /// <summary>本次 WM_PAINT 的无效区（客户区设备像素）。</summary>
        private Rectangle _paintClip;
        /// <summary>快路径本帧只需刷新的区域（设备像素）；空表示必须整窗刷新。</summary>
        private Rectangle _fastRect = Rectangle.Empty;
        private int _fastPathFrames;
        private int _fullFrames;
        private double _lastAmbientLog;
        private int _lastFast;
        private int _lastFull;

        private bool CacheMatches()
        {
            return _frameCache != null && _frameCacheG != null &&
                   _frameCache.Width == ClientSize.Width && _frameCache.Height == ClientSize.Height;
        }

        /// <summary>把刚刚画完的一整帧存进缓存位图（下一次勋章快路径要用它当背景）。</summary>
        private void UpdateFrameCache(Graphics g)
        {
            try
            {
                if (!CacheMatches())
                {
                    if (_frameCacheG != null) { _frameCacheG.Dispose(); _frameCacheG = null; }
                    if (_frameCache != null) { _frameCache.Dispose(); _frameCache = null; }
                    if (ClientSize.Width <= 0 || ClientSize.Height <= 0) { _cacheValid = false; return; }
                    _frameCache = new Bitmap(ClientSize.Width, ClientSize.Height,
                        System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                    _frameCacheG = Graphics.FromImage(_frameCache);
                }

                IntPtr src = IntPtr.Zero, dst = IntPtr.Zero;
                try
                {
                    src = g.GetHdc();
                    dst = _frameCacheG.GetHdc();
                    Native.BitBlt(dst, 0, 0, _frameCache.Width, _frameCache.Height, src, 0, 0, Native.SRCCOPY);
                }
                finally
                {
                    if (src != IntPtr.Zero) { try { g.ReleaseHdc(src); } catch (Exception) { } }
                    if (dst != IntPtr.Zero) { try { _frameCacheG.ReleaseHdc(dst); } catch (Exception) { } }
                }
                _cacheValid = true;
            }
            catch (Exception ex)
            {
                _cacheValid = false;
                Log.Error("frameCache", ex);
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);

            // 句柄重建（切 ShowInTaskbar、改某些窗口样式时 WinForms 会重建窗口）会让
            // **已投递但还没派发**的 WM_FRAME 随旧窗口一起被系统丢弃；而背压标记只在
            // WndProc 收到消息时才复位——于是它永远停在 1，渲染泵再也不投递任何一帧，
            // 表现为整个程序卡死（连计时器的倒计时都停）。
            // 所以句柄一重建就立刻复位背压标记，并强制下一帧整窗重绘（新窗口表面是空的）。
            Interlocked.Exchange(ref _framePending, 0);
            _fullRedrawPending = true;
            _cacheValid = false;
            if (_pumpStalls > 0) Log.Info("handle created（背压已复位，泵累计强制复位 " + _pumpStalls + " 次）");

            Native.RoundCorners(Handle);
            EnsureImeContext();
            StartPump();
            try { Invalidate(); } catch (Exception) { }
        }

        /// <summary>窗口重新可见（极简/托盘唤出）时必须整窗重绘一次，否则可能贴出旧画面。</summary>
        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible)
            {
                _fullRedrawPending = true;
                _cacheValid = false;
                // 抽屉若在隐藏期间已被逻辑关闭，直接落到关闭终态：
                // 否则那段"收起补间"会在窗口恢复可见的瞬间才播出来（观感是卡一下再滑走）。
                try { _ui.SettleDrawerForShow(); } catch (Exception) { }
                try { Invalidate(); } catch (Exception) { }
            }
        }

        /// <summary>
        /// 确保窗口关联了输入法上下文。
        /// 注意不要用 ImmCreateContext 造一个"空上下文"——那会切断与该窗口真实输入法的联系，
        /// 结果就是"输入法看起来打开了、打出来的却全是英文字母"。
        /// ImmAssociateContextEx(..., IACE_DEFAULT) 才是挂上系统默认输入法的正确做法。
        /// </summary>
        private void EnsureImeContext()
        {
            try
            {
                IntPtr himc = Native.ImmGetContext(Handle);
                if (himc != IntPtr.Zero) { Native.ImmReleaseContext(Handle, himc); return; }
                Native.ImmAssociateContextEx(Handle, IntPtr.Zero, Native.IACE_DEFAULT);
                Log.Info("ime: 窗口原本没有输入法上下文，已挂上系统默认上下文");
            }
            catch (Exception) { }
        }

        /// <summary>笔记聚焦时若当前不是中文布局，且系统装了中文输入法，则请求切换过去。</summary>
        private void TrySwitchToChineseLayout()
        {
            try
            {
                IntPtr cur = Native.GetKeyboardLayout(0);
                if (Native.IsChineseLayout(cur)) return;
                IntPtr zh = Native.FindChineseLayout();
                if (zh == IntPtr.Zero) return;      // 系统没装中文输入法，不折腾
                Native.PostMessage(Handle, Native.WM_INPUTLANGCHANGEREQUEST, IntPtr.Zero, zh);
                Log.Info("ime: 请求切换到中文布局 0x" + ((long)zh).ToString("X8") +
                         (Native.IsImeLayout(zh) ? "（真输入法）" : "（纯键盘布局！系统里可能没有中文输入法）"));
            }
            catch (Exception) { }
        }

        /// <summary>
        /// 重新取一次输入法上下文并"打开 + 切中文模式"。
        /// **必须在布局切换完成之后调用**：切换输入语言会让系统重建该窗口的输入法上下文，
        /// 在切换之前拿到的 himc 会失效，于是"打开输入法/切中文模式"全部落空——
        /// 表现就是输入法看着开着，打拼音却出来一串英文字母。
        /// </summary>
        private void EnsureNoteImeOpen()
        {
            IntPtr himc = IntPtr.Zero;
            try
            {
                himc = Native.ImmGetContext(Handle);
                if (himc == IntPtr.Zero)
                {
                    EnsureImeContext();
                    himc = Native.ImmGetContext(Handle);
                }
                if (himc == IntPtr.Zero) return;

                Native.ImmSetOpenStatus(himc, true);
                Native.ImmSetConversionStatus(himc, Native.IME_CMODE_NATIVE, Native.IME_SMODE_NONE);
                int conv = 0, sent = 0;
                Native.ImmGetConversionStatus(himc, ref conv, ref sent);
                Log.Info("ime: 笔记聚焦 → 布局=0x" + ((long)Native.GetKeyboardLayout(0)).ToString("X8") +
                         " 打开=" + (Native.ImmGetOpenStatus(himc) ? "是" : "否") +
                         " 转换=0x" + conv.ToString("X4"));
                PositionCompositionWindow();
            }
            catch (Exception) { }
            finally
            {
                if (himc != IntPtr.Zero) { try { Native.ImmReleaseContext(Handle, himc); } catch (Exception) { } }
            }
        }

        /// <summary>
        /// 只在笔记栏位聚焦期间打开输入法并切到中文模式，失焦立即关闭：
        /// 既让中文输入法可用，又不会让空格/快捷键被输入法吃掉。
        /// </summary>
        private void SyncImeWithNoteFocus()
        {
            bool want = _ui.IsNoteFocused;
            if (want == _imeNoteOpen) return;
            _imeNoteOpen = want;

            IntPtr himc = IntPtr.Zero;
            try
            {
                himc = Native.ImmGetContext(Handle);
                if (himc == IntPtr.Zero)
                {
                    EnsureImeContext();
                    himc = Native.ImmGetContext(Handle);
                }
                if (himc == IntPtr.Zero) return;

                if (want)
                {
                    // 顺序很重要：先请求切布局（异步），马上先按当前上下文开一次；
                    // 布局切换完成后系统会回发 WM_INPUTLANGCHANGE，那时会再开一次（用新的上下文）。
                    TrySwitchToChineseLayout();
                    if (!Native.HasChineseIme() && _imeHintShown == 0)
                    {
                        _imeHintShown = 1;
                        _ui.ShowToast(I18n.T("notes.noIme"));
                    }
                    Native.ImmSetOpenStatus(himc, true);
                    Native.ImmSetConversionStatus(himc, Native.IME_CMODE_NATIVE, Native.IME_SMODE_NONE);
                    _imeOpenCount++;
                    PositionCompositionWindow();
                }
                else
                {
                    Native.ImmSetOpenStatus(himc, false);
                }
            }
            catch (Exception) { }
            finally
            {
                if (himc != IntPtr.Zero) { try { Native.ImmReleaseContext(Handle, himc); } catch (Exception) { } }
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            ApplySize(true);
            _ui.Pulse();      // 显示瞬间给一段高帧率，勋章微光/入场动效更顺
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

            var probe = PerfProbe.Active;
            _paintClip = e.ClipRectangle;
            _paintClock.Restart();
            try { DrawFrame(e.Graphics); }
            catch (Exception ex) { Log.Error("OnPaint", ex); }
            _paintClock.Stop();
            if (probe != null) probe.RecordPaint(_paintClock.Elapsed.TotalMilliseconds);
            else PerfCounters.AddFrame(_paintClock.Elapsed.TotalMilliseconds);
        }

        /// <summary>
        /// 画一帧。
        /// 只有勋章在变时走快路径：贴缓存位图 + 只重画那几枚勋章（因此能跑 30fps）；
        /// 其余情况整窗重绘，并把结果存进缓存供快路径当背景。
        /// </summary>
        private void DrawFrame(Graphics g)
        {
            // 先消费"必须整窗重绘"标记：WM_PAINT 可能被推迟到下一轮 OnLoop 之后才派发，
            // 那时 _medalOnlyFrame 会被重算为 true，若只信它就会贴出过期的一帧。
            bool forceFull = _fullRedrawPending;
            _fullRedrawPending = false;

            if (!forceFull && _medalOnlyFrame && _cacheValid && CacheMatches() && NoBufferOffset(g))
            {
                try
                {
                    _fastPathFrames++;
                    if (_fastPathFrames == 1)
                        Log.Info("medalFastPath: 已启用 窗口=" + Left + "," + Top + " " + Width + "x" + Height +
                                 " 缓存=" + (_frameCache != null ? _frameCache.Width + "x" + _frameCache.Height : "无"));
                    long ts = PerfCounters.Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
                    // 贴图必须用 GDI+（DrawImageUnscaled），**不能用 BitBlt**：
                    // 目标 Graphics 是 WinForms 的绘制缓冲/离屏位图，通过 GetHdc() 向它做 GDI 写入
                    // 不会被保留（实测整帧变全黑，只剩 GDI+ 画的勋章），这条路径踩过一次坑。
                    var st = g.Save();
                    try { g.ResetTransform(); g.DrawImageUnscaled(_frameCache, 0, 0); }
                    finally { g.Restore(st); }
                    PerfCounters.Add("帧-快路径贴图", ts);

                    long ts2 = PerfCounters.Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
                    _ui.DrawAmbientOverlay(g);
                    if (_frameCacheG != null) _ui.DrawAmbientOverlay(_frameCacheG);   // 同步更新缓存
                    PerfCounters.Add("帧-快路径勋章", ts2);
                    PerfCounters.Add("帧-勋章快路径", ts);
                    return;
                }
                catch (Exception ex)
                {
                    // 快路径出任何问题都直接自愈：丢弃缓存，本帧改走整窗重绘
                    _cacheValid = false;
                    _medalOnlyFrame = false;
                    Log.Error("medalFastPath(回退整窗重绘)", ex);
                }
            }

            _medalOnlyFrame = false;
            _fullFrames++;
            _ui.Draw(g);
            // 只有整窗都被重绘时才能把这一帧存进缓存：局部重绘时缓冲区其余部分还是旧内容，
            // 存进去会把缓存污染成"拼接帧"，之后快路径就会贴出错位画面。
            if (_paintClip.Width >= ClientSize.Width && _paintClip.Height >= ClientSize.Height)
                UpdateFrameCache(g);
            else
                _cacheValid = false;    // 下一帧必须整窗重绘，重建缓存
        }

        /// <summary>
        /// 双缓冲的缓冲区若被框架按无效区分配，其 Graphics 会带一个平移变换，
        /// 那样贴图原点就不是 (0,0) 了。这里显式检查，一旦有位移立刻退回整窗重绘，
        /// 绝不冒着贴错位置的风险（这个坑已经在 BitBlt 那次踩过一遍）。
        /// </summary>
        private static bool NoBufferOffset(Graphics g)
        {
            try
            {
                var m = g.Transform;
                return Math.Abs(m.OffsetX) < 0.5f && Math.Abs(m.OffsetY) < 0.5f;
            }
            catch (Exception) { return false; }
        }

        /// <summary>性能自检的阶段切换：进入计时段时真的跑一轮计时，测 20fps 档。</summary>
        private void OnPerfPhase(int phase)
        {
            if (phase == 2) _app.Timer.StartFocus(25, "25");
        }

        /// <summary>导出诊断报告到桌面并打开（用户回传排查用）。</summary>
        private void ExportDiagnostics()
        {
            try
            {
                double idle = _frameClock.Elapsed.TotalSeconds - _lastInputTime;
                string frameInfo = "勋章快路径帧=" + _fastPathFrames + " 全窗帧=" + _fullFrames +
                                   " 当前帧档位=" + _frameInterval + "ms 空闲=" + idle.ToString("0.0") + "s" +
                                   " 动效勋章数=" + _ui.AmbientMedalCount +
                                   " | 渲染循环帧数=" + _loopTicks + " 泵强制复位=" + _pumpStalls +
                                   " | 笔记聚焦期间收到字符=" + _noteChars +
                                   " 最后字符码点=" + (_lastCharCode < 0 ? "无" : "U+" + _lastCharCode.ToString("X4")) +
                                   " 输入法打开次数=" + _imeOpenCount +
                                   " 组字次数=" + _imeCompositionCount +
                                   " 提交结果次数=" + _imeResultCount +
                                   " 系统有中文输入法=" + (Native.HasChineseIme() ? "是" : "否") +
                                   " 笔记当前聚焦=" + (_ui.IsNoteFocused ? "是" : "否");
                string text = Diagnostics.Build(_app, Handle, frameInfo);
                string path = Diagnostics.Export(text, "番茄专注-诊断报告.txt");
                if (string.IsNullOrEmpty(path))
                {
                    _ui.ShowToast(I18n.T("debug.reportFailed"));
                    return;
                }
                Log.Info("diagnostics -> " + path);
                _ui.ShowToast(I18n.T("debug.reportDone"));
                Diagnostics.OpenInEditor(path);
            }
            catch (Exception ex) { Log.Error("ExportDiagnostics", ex); }
        }

        /// <summary>把输入法组合窗口与候选框定位到笔记栏位的输入光标处。</summary>
        private void PositionCompositionWindow()
        {
            IntPtr hImc = IntPtr.Zero;
            try
            {
                hImc = Native.ImmGetContext(Handle);
                if (hImc == IntPtr.Zero) return;

                var caret = _ui.NoteCaretPoint;
                int x = (int)Math.Round(caret.X * _scale);
                int y = (int)Math.Round(caret.Y * _scale);

                var cf = new Native.COMPOSITIONFORM();
                cf.dwStyle = Native.CFS_POINT;
                cf.ptCurrentPos.x = x;
                cf.ptCurrentPos.y = y;
                Native.ImmSetCompositionWindow(hImc, ref cf);

                var cand = new Native.CANDIDATEFORM();
                cand.dwIndex = 0;
                cand.dwStyle = Native.CFS_POINT;
                cand.ptCurrentPos.x = x;
                cand.ptCurrentPos.y = y;
                Native.ImmSetCandidateWindow(hImc, ref cand);
            }
            catch { }
            finally
            {
                if (hImc != IntPtr.Zero) { try { Native.ImmReleaseContext(Handle, hImc); } catch { } }
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // 全部由 OnPaint 绘制，避免闪烁
        }

        /// <summary>记录一次输入活动：用于"长时间无操作就降低常驻动效帧率"。</summary>
        private void NoteInput() { _lastInputTime = _frameClock.Elapsed.TotalSeconds; }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            NoteInput();
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
            NoteInput();
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
            NoteInput();
            if (e.Button == MouseButtons.Left)
                _ui.OnMouseUp(new PointF(e.X / _scale, e.Y / _scale));
            base.OnMouseUp(e);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            NoteInput();
            _ui.OnMouseWheel(e.Delta);
            base.OnMouseWheel(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            NoteInput();
            _ui.OnKey(e.KeyCode);
            base.OnKeyDown(e);
        }

        protected override void OnActivated(EventArgs e)
        {
            NoteInput();          // 切回前台也算活动，立刻恢复常驻动效帧率
            base.OnActivated(e);
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
            if (m.Msg == WM_FRAME)
            {
                Interlocked.Exchange(ref _framePending, 0);
                if (PerfProbe.Active != null) PerfProbe.Frames++;
                try { OnLoop(this, EventArgs.Empty); }
                catch (Exception ex) { Log.Error("frame", ex); }
                return;
            }

            const int WM_CHAR = 0x0102;
            const int WM_IME_CHAR = 0x0286;
            if ((m.Msg == WM_CHAR || m.Msg == WM_IME_CHAR) && _ui.IsNoteFocused)
            {
                // 笔记聚焦时接管字符输入（含输入法提交的字符），并吞掉消息避免系统提示音
                char c = (char)(m.WParam.ToInt32() & 0xFFFF);
                if (c >= ' ')
                {
                    _noteChars++;                       // 诊断：字符确实到达了窗口
                    _lastCharCode = c;
                    _ui.OnChar(c);
                }
                _ui.MarkDirty();
                return;
            }

            // 输入法开始组字：把拼音组合窗口与候选框挪到笔记光标处（否则会跑到屏幕角落）
            const int WM_IME_STARTCOMPOSITION = 0x010D;
            if (m.Msg == WM_IME_STARTCOMPOSITION && _ui.IsNoteFocused)
            {
                _imeCompositionCount++;
                PositionCompositionWindow();
                base.WndProc(ref m);      // 仍交给默认处理，由它生成提交用的 WM_CHAR
                return;
            }

            // 组字结束（含结果串）：计数后交给默认处理，由它生成 WM_CHAR 提交
            const int WM_IME_COMPOSITION = 0x010F;
            const int GCS_RESULTSTR = 0x0800;
            if (m.Msg == WM_IME_COMPOSITION && _ui.IsNoteFocused)
            {
                if (((int)m.LParam & GCS_RESULTSTR) != 0) _imeResultCount++;
                base.WndProc(ref m);
                return;
            }

            // 切换输入法/键盘布局：系统会重建本窗口的输入法上下文，
            // 所以必须在这里**重新取上下文**并再次打开输入法——这是中文输入能否组字的关键。
            const int WM_INPUTLANGCHANGE = 0x0051;
            if (m.Msg == WM_INPUTLANGCHANGE)
            {
                base.WndProc(ref m);
                if (_ui.IsNoteFocused) EnsureNoteImeOpen();
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
                StopPump();
                Native.EndHighResolutionTimers();
                try { Log.Info("frames: 勋章快路径=" + _fastPathFrames + " 全窗=" + _fullFrames); } catch (Exception) { }
                try { if (_frameCacheG != null) _frameCacheG.Dispose(); } catch (Exception) { }
                try { if (_frameCache != null) _frameCache.Dispose(); } catch (Exception) { }
                _frameCacheG = null;
                _frameCache = null;
                _cacheValid = false;
            }
            base.Dispose(disposing);
        }
    }
}
