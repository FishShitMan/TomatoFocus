using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using TomatoFocus.Core;
using TomatoFocus.Render;
using TomatoFocus.Render.Art;

namespace TomatoFocus.Ui
{
    internal sealed class Hotspot
    {
        public string Id = "";
        public RectangleF Rect;
        public Action Click;
        public Action<PointF> Drag;
        public bool Enabled = true;
        public bool ConsumeWheel;
    }

    /// <summary>
    /// 全部界面（单窗口、零弹窗、零二级菜单）。
    /// 采用即时模式：每帧重建热区，输入按上一帧的热区命中。
    /// </summary>
    internal sealed partial class UiRoot
    {
        private readonly AppState _app;
        private readonly List<Hotspot> _hot = new List<Hotspot>();
        private readonly Dictionary<string, Anim> _anims = new Dictionary<string, Anim>();
        private readonly ParticleSystem _particles = new ParticleSystem();
        private readonly Timeline _celebrate = new Timeline();

        public RectangleF Bounds;
        public float Scale = 1f;

        private PointF _mouse;
        private bool _mouseInside;
        private bool _pressed;
        private bool _dragging;
        private string _pressedId = "";
        private string _hoverId = "";
        private double _time;
        private double _secondTick;
        private int _lastSecond = -1;
        private bool _dirty = true;
        private RectangleF _hotClip;
        private bool _hotClipActive;
        private float _holdProgress;          // 长按清除进度 0..1
        private bool _noteFocus;              // 笔记栏位是否聚焦
        public bool IsNoteFocused { get { return _noteFocus; } }

        /// <summary>
        /// 笔记栏位的输入锚点（DIP）。
        /// **这是"组合窗口左上角"的坐标，不是文字基线**：IMM32 的 `CFS_POINT.ptCurrentPos`
        /// 取的是组合窗口左上方，若按基线口径给（例如行高 82% 处），组合串会被画低约一行。
        /// </summary>
        public PointF NoteCaretPoint { get { return _noteCaret; } }
        private PointF _noteCaret = new PointF(200, 560);

        /// <summary>文本输入（来自 WM_CHAR，支持输入法提交的字符）。</summary>
        public void OnChar(char c)
        {
            if (!_noteFocus) return;
            if (c < ' ' || c == '\u007f') return;
            string s = _app.Data.Settings.NoteDraft ?? "";
            if (s.Length >= 500) return;
            _app.Data.Settings.NoteDraft = s + c;
            _app.MarkDirty();
            _dirty = true;
        }
        private float _scrollTarget = -1f;    // 抽屉平滑滚动目标
        private bool _scrollToEnd;

        private string _drawer = "";
        private string _menuTab = "achievements";
        // 关闭动画快照：抽屉一开始收起就冻结"刚才那一页"的内容与滚动位置，
        // 关闭过程中只有横向位移——不串页、不错位、标题也不会提前消失。
        private string _closingDrawer = "";
        private string _closingDayKey = "";
        private float _closingScroll;
        private string _mode = "focus";        // focus | break
        private float _modeK;                  // 胶囊滑动进度 0=专注 1=休息（配色渐变共用）
        private int _breakPresetMinutes = 5;
        private float _drawerScroll;
        private float _drawerMaxScroll;
        private float _calScroll;             // 日历网格内部滚动
        private float _calMaxScroll;
        private double _calBarUntil;          // 日历滚动条显示截止时间
        private string _rewardCat = "title";
        private string _dayKey = "";
        private DateTime _viewMonth = new DateTime(DateTime.Now.Year, DateTime.Now.Month, 1);
        private string _customInput = "";
        private bool _customFocus;
        private RectangleF _customChipRect;
        private string _toast = "";
        private double _toastT;

        // 布局缓存（供输入与绘制共用）
        private RectangleF _leftRect, _rightRect, _topBar, _ringRect, _calendarRect;
        private PointF _ringCenter;
        private float _ringRadius;

        public UiRoot(AppState app)
        {
            _app = app;
            _celebrate.Duration = 1.0;
            _celebrate.Easing = Ease.OutCubic;
            // 休息档位与"设置里的休息时长"是两件事：
            // 前者是用户主动开始休息时的档位（5/10/15/自定），后者只用于专注结束后的提醒卡片。
            // 默认选最低档（5 分钟）；自定值只在本次运行内有效。
            _breakPresetMinutes = 5;
            if (app.Data.Settings.BreakCustomMinutes > 0) _breakPresetMinutes = app.Data.Settings.BreakCustomMinutes;
        }

        public bool IsAnimating
        {
            get
            {
                if (_particles.Active || _celebrate.Playing || _toastT > 0) return true;
                if (_holdProgress > 0f || _scrollTarget >= 0f) return true;
                foreach (var a in _anims.Values) if (!a.Settled) return true;
                return false;
            }
        }

        /// <summary>温馨问候的自动消失时长（秒）。</summary>
        private const double GreetAutoDismissSeconds = 12.0;
        private double _greetT;

        /// <summary>触发一次短时高帧率（用于窗口显示、装备奖励等时刻的动效）。</summary>
        public void Pulse() { Burst(); _dirty = true; }

        /// <summary>调试面板里点「导出诊断报告」时触发。</summary>
        public event Action DiagnosticsRequested;

        /// <summary>长按清除的当前进度（供测试观察）。</summary>
        public float HoldProgress { get { return _holdProgress; } }

        /// <summary>诊断用：列出当前未收敛的动画键（定位"一直在动"的原因）。</summary>
        public string UnsettledAnimKeys()
        {
            var sb = new System.Text.StringBuilder();
            foreach (var kv in _anims)
                if (!kv.Value.Settled) { if (sb.Length > 0) sb.Append(","); sb.Append(kv.Key); }
            return sb.Length == 0 ? "(无)" : sb.ToString();
        }

        /// <summary>是否存在"常驻慢动效"（勋章微光/彩虹渐变）。刻意与 IsAnimating 分开：
        /// 突发动效走 60fps，常驻微光只走 6.7fps，避免长期占用 CPU。</summary>
        public bool IsAnimatingSlow { get { return _ambientAnimation; } }

        private bool _ambientAnimation;

        /// <summary>笔记输入光标的当前矩形（DIP）；未聚焦时为空。</summary>
        private RectangleF _noteCaretRect = RectangleF.Empty;

        /// <summary>光标是否处于显示相位（每 500ms 切换一次）。</summary>
        private bool CaretOn { get { return ((int)(_time * 2)) % 2 == 0; } }

        /// <summary>光标细条的矩形（相对行顶）。</summary>
        private static RectangleF CaretRect(float x, float lineTop)
        {
            return new RectangleF(x, lineTop + 3f, 1.6f, 11f);
        }

        // 本帧需要随快路径一起重画的光标（背景色用于擦除旧光标）
        private RectangleF _ambientCaret = RectangleF.Empty;
        private Color _ambientCaretBg;
        private Color _ambientCaretInk;

        private void RegisterAmbientCaret(RectangleF rect, Color bg, Color ink)
        {
            if (rect.Width <= 0f) return;
            _ambientCaret = rect;
            _ambientCaretBg = bg;
            _ambientCaretInk = ink;
        }

        /// <summary>本帧登记到的光标矩形（供测试观察）。</summary>
        public RectangleF AmbientCaretRect { get { return _ambientCaret; } }

        /// <summary>本帧画过的、需要持续动效的勋章（顶栏 + 奖励列表里已拥有的）。</summary>
        private struct AmbientMedal
        {
            public RectangleF Rect;
            public int Index;        // 绘制顺序（与整窗绘制保持一致）
            public string Id;
            public bool Locked;
            public RectangleF Clip;  // 非空时按此区域裁剪（抽屉里的勋章不能溢出面板）
        }
        private readonly List<AmbientMedal> _ambientMedals = new List<AmbientMedal>();

        /// <summary>登记一枚"会动"的勋章（未解锁的灰显勋章是静止的，不登记）。</summary>
        private void RegisterAmbientMedal(RectangleF rect, string id, bool locked)
        {
            RegisterAmbientMedal(rect, id, locked, RectangleF.Empty);
        }

        private void RegisterAmbientMedal(RectangleF rect, string id, bool locked, RectangleF clip)
        {
            if (string.IsNullOrEmpty(id) || rect.Width <= 0.5f) return;
            if (locked) return;                       // 未解锁：灰显且完全静止
            var m = new AmbientMedal();
            m.Rect = rect;
            m.Index = _ambientMedals.Count;
            m.Id = id;
            m.Locked = false;
            m.Clip = clip;
            _ambientMedals.Add(m);
        }

        /// <summary>本帧登记到的动效勋章数量（供测试观察）。</summary>
        public int AmbientMedalCount { get { return _ambientMedals.Count; } }

        /// <summary>
        /// 是否处于"只有勋章（可选：笔记光标）在变"的状态。
        /// 满足时外壳可以走快路径：贴上上一帧的缓存位图 + 只重画这些元素，
        /// 不必整窗重绘——这正是常驻动效卡顿的根源（每帧 10ms 全窗重绘撑不起高帧率）。
        /// 抽屉（奖励页）打开、笔记聚焦时同样适用。
        /// </summary>
        public bool CanDrawMedalOnly
        {
            get
            {
                if (_ambientMedals.Count == 0) return false;
                if (_debugOpen || _confirmStop) return false;
                if (_app.Pending != null) return false;
                if (_app.PendingGreeting != null) return false;   // 问候卡在动：需要整窗重绘
                if (_toastT > 0) return false;
                // 笔记聚焦不再排除：光标已改为独立细条，快路径会连它一起擦/画
                return true;
            }
        }

        /// <summary>快路径重画：先重画全部动效勋章，再处理笔记光标。调用方需保证背景是上一帧的内容。</summary>
        public void DrawAmbientOverlay(Graphics g)
        {
            if (_ambientMedals.Count == 0 && _ambientCaret.Width <= 0f) return;
            var pt = new Painter(g, Scale, _app.CurrentTheme);
            for (int i = 0; i < _ambientMedals.Count; i++)
            {
                var m = _ambientMedals[i];
                if (m.Clip.Width > 0.5f && m.Clip.Height > 0.5f)
                {
                    var clip = m.Clip;
                    string id = m.Id;
                    var rect = m.Rect;
                    pt.Clip(clip, delegate { MedalArt.Draw(pt, rect, id, _time, true, false); });
                }
                else
                {
                    MedalArt.Draw(pt, m.Rect, m.Id, _time, true, m.Locked);
                }
            }

            if (_ambientCaret.Width > 0f)
            {
                // 先擦掉旧相位的光标（笔记卡片是纯色底），再按当前相位画
                var erase = RectangleF.Inflate(_ambientCaret, 1.5f, 0f);
                using (var b = new SolidBrush(_ambientCaretBg))
                    pt.Raw.FillRectangle(b, erase);
                if (CaretOn) pt.FillRound(_ambientCaret, _ambientCaret.Width / 2f, _ambientCaretInk);
            }
        }

        /// <summary>最近一帧时间环显示的分钟数（自定输入时会跟随输入实时变化）。</summary>
        public int PreviewMinutes { get; private set; }

        /// <summary>顶栏钱包徽章矩形（供测试校验与其他元素不重叠）。</summary>
        public RectangleF WalletBadgeRect { get; private set; }

        /// <summary>当前模式：focus / break（供测试观察）。</summary>
        public string CurrentMode { get { return _mode; } }

        /// <summary>休息模式当前选中的分钟数。</summary>
        public int BreakPresetMinutes { get { return _breakPresetMinutes; } }

        private void SetMode(string mode)
        {
            if (_app.Timer.Phase != TimerPhase.Idle) { ShowToast(I18n.T("focus.presetLocked")); return; }
            if (_mode == mode) return;
            _mode = mode;
            _customFocus = false;
            _dirty = true;
            Burst();
        }

        /// <summary>请求把抽屉平滑滚动到底部（用于展开新出现的确认卡）。</summary>
        public void ScrollDrawerToEnd() { _scrollToEnd = true; _dirty = true; }

        /// <summary>取走并清空"需要重绘"标记（交互或状态变化都会置位）。</summary>
        public bool TakeDirty()
        {
            bool d = _dirty;
            _dirty = false;
            return d;
        }

        public void MarkDirty() { _dirty = true; }

        /// <summary>文件对话框的宿主窗口（保证弹在主窗口前面）。</summary>
        public System.Windows.Forms.IWin32Window Owner;

        // --- 调试入口：连续点击左上番茄 5 次 ---------------------------------
        private int _debugClicks;
        private double _debugLastClick = -99;
        private bool _debugOpen;
        public bool IsDebugPanelOpen { get { return _debugOpen; } }

        private void OnTomatoClick()
        {
            if (!string.IsNullOrEmpty(_drawer) || _confirmStop) return;   // 只在主界面计数
            if (_time - _debugLastClick > 2.5) _debugClicks = 0;
            _debugLastClick = _time;
            _debugClicks++;
            if (_debugClicks >= 5)
            {
                _debugClicks = 0;
                _debugOpen = true;
                _dirty = true;
            }
        }

        /// <summary>最近一次点击位置（供无参 Click 回调读取）。</summary>
        private PointF _clickPoint;

        public void NotifyAchievements(List<AchievementDef> list)
        {
            if (list == null || list.Count == 0) return;
            ShowToast(I18n.T("ach.new") + " " + I18n.T(list[0].NameKey));
        }

        public void ShowToast(string message)
        {
            _toast = message;
            _toastT = 3.2;
            _dirty = true;
        }

        // --- 动画工具 ------------------------------------------------------
        private Anim A(string key, float initial = 0f, float speed = 14f)
        {
            Anim a;
            if (!_anims.TryGetValue(key, out a))
            {
                a = new Anim(initial, speed);
                _anims[key] = a;
            }
            return a;
        }

        private float HoverAmount(string id)
        {
            var a = A("h:" + id, 0f, 16f);
            a.Set(_hoverId == id ? 1f : 0f);
            return a.Value;
        }

        private float PressAmount(string id)
        {
            var a = A("p:" + id, 0f, 24f);
            a.Set(_pressed && _pressedId == id ? 1f : 0f);
            return a.Value;
        }

        private static PointF C(RectangleF r) { return new PointF(r.Left + r.Width / 2f, r.Top + r.Height / 2f); }

        public void Update(double dt) { Update(dt, dt); }

        /// <summary>dt = 动画步进（已钳制）；realDt = 真实经过时间（用于提示时长等）。</summary>
        public void Update(double dt, double realDt)
        {
            _time += realDt;
            foreach (var a in _anims.Values) a.Update(dt);
            _particles.Update(dt);
            _celebrate.Update(dt);
            if (_toastT > 0)
            {
                _toastT -= realDt;
                if (_toastT <= 0) _toast = "";
            }
            if (InteractionBurst > 0) InteractionBurst -= realDt;

            // 温馨问候自动消失（不强制点击），12 秒后自行淡出
            if (_app.PendingGreeting != null)
            {
                _greetT += realDt;
                if (_greetT >= GreetAutoDismissSeconds)
                {
                    _greetT = 0;
                    _app.DismissGreeting();
                    _dirty = true;
                }
            }
            else _greetT = 0;
            var open = !string.IsNullOrEmpty(_drawer);
            A("drawer", 0f, 13f).Set(open ? 1f : 0f);

            // 长按清除：按住推进，松手快速回收
            if (_pressed && _pressedId == "clearHold" && _clearStage == 2)
            {
                _holdProgress += (float)(realDt / 1.2);
                if (_holdProgress >= 1f)
                {
                    _holdProgress = 0f;
                    _clearStage = 0;
                    _scrollTarget = -1f;
                    _app.ClearAllData();
                }
                _dirty = true;
            }
            else if (_holdProgress > 0f)
            {
                _holdProgress -= (float)(realDt / 0.3);
                if (_holdProgress < 0f) _holdProgress = 0f;
                _dirty = true;
            }

            // 抽屉平滑滚动
            if (_scrollTarget >= 0f)
            {
                float d = _scrollTarget - _drawerScroll;
                if (Math.Abs(d) < 0.5f) { _drawerScroll = _scrollTarget; _scrollTarget = -1f; }
                else _drawerScroll += d * (float)(1 - Math.Exp(-14 * realDt));
                _dirty = true;
            }

            // 每秒刷新一次（时钟/托盘提示）
            _secondTick += realDt;
            if (_secondTick >= 1.0)
            {
                _secondTick = 0;
                _lastSecond = _app.Timer.RemainingSeconds;
            }
        }

        /// <summary>交互后的一小段时间内强制高帧率，保证点击立即有视觉反馈。</summary>
        public double InteractionBurst { get; private set; }
        private void Burst() { InteractionBurst = FrameTiming.InteractionBurstSeconds; }

        // --- 输入 ----------------------------------------------------------
        public void OnMouseMove(PointF p, bool inside)
        {
            _mouse = p;
            _mouseInside = inside;

            // 按住并拖动（滚动条等）
            if (_pressed && !string.IsNullOrEmpty(_pressedId))
            {
                for (int i = _hot.Count - 1; i >= 0; i--)
                {
                    var h = _hot[i];
                    if (h.Id != _pressedId) continue;
                    if (h.Drag != null)
                    {
                        _dragging = true;
                        try { h.Drag(p); } catch (Exception) { }
                        _dirty = true;
                    }
                    break;
                }
                if (_dragging) return;
            }

            string previous = _hoverId;
            _hoverId = "";
            HoverClickable = false;
            if (inside)
            {
                for (int i = _hot.Count - 1; i >= 0; i--)
                {
                    var h = _hot[i];
                    if (h.Enabled && h.Rect.Contains(p))
                    {
                        _hoverId = h.Id;
                        HoverClickable = h.Enabled && h.Click != null;
                        break;
                    }
                }
            }
            if (previous != _hoverId) { _dirty = true; Burst(); }
        }

        public void OnMouseDown(PointF p)
        {
            _mouse = p;
            _pressed = true;
            _dragging = false;
            _pressedId = "";
            for (int i = _hot.Count - 1; i >= 0; i--)
            {
                var h = _hot[i];
                if (h.Enabled && h.Rect.Contains(p)) { _pressedId = h.Id; break; }
            }
            _dirty = true;
            Burst();
        }

        public void OnMouseUp(PointF p)
        {
            // 关键：必须在派发点击之前记录确认态，否则「结束本轮」刚把状态置位就会被下面的取消逻辑撤销
            bool wasConfirming = _confirmStop;
            bool wasEditing = _customFocus;
            _clickPoint = p;

            string clicked = null;
            if (_pressed)
            {
                for (int i = _hot.Count - 1; i >= 0; i--)
                {
                    var h = _hot[i];
                    if (!h.Enabled || h.Id != _pressedId) continue;
                    clicked = h.Id;
                    if (!_dragging && h.Rect.Contains(p) && h.Click != null)
                    {
                        try { h.Click(); } catch (Exception) { }
                    }
                    break;
                }
            }

            // 自定输入：点击格子以外任何地方即取消，避免浮层长期遮挡。
            // 例外：点「开始」不算"点了别处"——按新规格，此时应由开始按钮统一判定
            // （有输入就直接开始，没输入就什么都不做，且不退出编辑态）。
            if (wasEditing && _customFocus && clicked != "presetCustom" && clicked != "btnPrimary" && !_customChipRect.Contains(p))
                CancelCustomInput();

            // 中断确认条：点击条外区域即取消（只在点击前就已处于确认态时生效）
            if (wasConfirming && _confirmStop && clicked != "cfYes" && clicked != "cfNo" && !_confirmBarRect.Contains(p))
            {
                _confirmStop = false;
                A("confirm").Set(0f);
            }

            // 笔记栏位：点击别处即失焦
            if (_noteFocus && clicked != "notesArea") _noteFocus = false;

            _pressed = false;
            _pressedId = "";
            _dragging = false;
            _dirty = true;
            Burst();
        }

        private void CancelCustomInput()
        {
            _customFocus = false;
            _customInput = "";
        }

        public void OnMouseLeave()
        {
            _mouseInside = false;
            _hoverId = "";
            _pressed = false;
            _pressedId = "";
        }

        public void OnMouseWheel(int delta)
        {
            for (int i = _hot.Count - 1; i >= 0; i--)
            {
                var h = _hot[i];
                if (h.ConsumeWheel && h.Rect.Contains(_mouse))
                {
                    // 上界必须当场钳制，否则值会越过 maxScroll 再被绘制时拉回，表现为到底后反复回弹
                    _drawerScroll -= delta / 120f * 56f;
                    if (_drawerScroll < 0) _drawerScroll = 0;
                    if (_drawerScroll > _drawerMaxScroll) _drawerScroll = _drawerMaxScroll;
                    _dirty = true;      // 必须触发重绘，否则滚了也不动
                    Burst();
                    return;
                }
            }
            // 日历区域滚轮：先把网格滚到底，到边界再翻月
            if (string.IsNullOrEmpty(_drawer) && _calendarRect.Contains(_mouse))
            {
                if (_calMaxScroll > 0.5f &&
                    ((delta < 0 && _calScroll < _calMaxScroll - 0.5f) || (delta > 0 && _calScroll > 0.5f)))
                {
                    _calScroll -= delta / 120f * 40f;
                    if (_calScroll < 0f) _calScroll = 0f;
                    if (_calScroll > _calMaxScroll) _calScroll = _calMaxScroll;
                    _calBarUntil = _time + 1.2;
                }
                else
                {
                    ShiftView(delta > 0 ? 1 : -1);
                }
                _dirty = true;
                Burst();
            }
        }

        public void OnKey(Keys key)
        {
            if (_debugOpen)
            {
                if (key == Keys.Escape) { _debugOpen = false; _dirty = true; }
                return;
            }
            if (_customFocus)
            {
                if (key == Keys.Back && _customInput.Length > 0) _customInput = _customInput.Substring(0, _customInput.Length - 1);
                else if (key == Keys.Escape) { _customFocus = false; _customInput = ""; }
                else if (key == Keys.Enter) { if (!TryCommitCustom(true)) CancelCustomInput(); }
                else if (key >= Keys.D0 && key <= Keys.D9) AppendCustom(((int)key - (int)Keys.D0).ToString());
                else if (key >= Keys.NumPad0 && key <= Keys.NumPad9) AppendCustom(((int)key - (int)Keys.NumPad0).ToString());
                _dirty = true;
                return;
            }

            // 抽屉打开时只响应 Esc，避免误触主界面快捷键
            if (_noteFocus)
            {
                string note = _app.Data.Settings.NoteDraft ?? "";
                if (key == Keys.Back)
                {
                    if (note.Length > 0) { _app.Data.Settings.NoteDraft = note.Substring(0, note.Length - 1); _app.MarkDirty(); }
                }
                else if (key == Keys.Enter)
                {
                    if (note.Length < 500) { _app.Data.Settings.NoteDraft = note + "\n"; _app.MarkDirty(); }
                }
                else if (key == Keys.Escape)
                {
                    _noteFocus = false;
                }
                _dirty = true;
                Burst();
                return;
            }

            if (!string.IsNullOrEmpty(_drawer))
            {
                if (key == Keys.Escape) BeginDrawerClose();
                return;
            }

            switch (key)
            {
                case Keys.Space: ToggleStartPause(); break;
                case Keys.D1: SelectPreset(5, "5"); break;
                case Keys.D2: SelectPreset(15, "15"); break;
                case Keys.D3: SelectPreset(25, "25"); break;
                case Keys.C: _customFocus = true; _customInput = ""; break;
                case Keys.T: _viewMonth = new DateTime(DateTime.Now.Year, DateTime.Now.Month, 1); break;
                case Keys.Escape:
                    if (_app.Timer.Phase == TimerPhase.Focusing) _app.Timer.Pause();
                    break;
                case Keys.Left: ShiftView(-1); break;
                case Keys.Right: ShiftView(1); break;
            }
            _dirty = true;
            Burst();
        }

        private void AppendCustom(string s)
        {
            if (_customInput.Length >= 3) return;
            _customInput += s;
        }

        /// <summary>提交自定输入。返回是否提交成功；startTimer=true 时提交后立即开始倒计时。</summary>
        private bool TryCommitCustom(bool startTimer)
        {
            int m;
            if (!int.TryParse(_customInput, out m) || m < 1 || m > 180) return false;

            // 不再新增挡位：只记住数值（且仅在本次运行内有效），自定栏位自身保留这个数字
            if (_mode == "break") _app.Data.Settings.BreakCustomMinutes = m;
            else _app.Data.Settings.CustomMinutes = m;
            SelectPreset(m, "custom");
            CancelCustomInput();
            if (startTimer) StartNow();
            return true;
        }

        /// <summary>只选择档位，不自动开始计时。</summary>
        private void SelectPreset(int minutes, string preset)
        {
            if (_app.Timer.Phase != TimerPhase.Idle)
            {
                ShowToast(I18n.T("focus.presetLocked"));
                return;
            }
            if (_mode == "break")
            {
                _breakPresetMinutes = Math.Max(1, minutes);
                _app.Data.Settings.BreakCustomMinutes = preset == "custom" ? Math.Max(1, minutes) : 0;
            }
            else
            {
                _app.Timer.SetPresetMinutes(minutes, preset);
                _app.RememberPreset();
            }
            _app.MarkDirty();
            _dirty = true;
        }

        private void ToggleStartPause()
        {
            switch (_app.Timer.Phase)
            {
                case TimerPhase.Focusing: _app.Timer.Pause(); break;
                case TimerPhase.Paused: _app.Timer.Resume(); break;
                case TimerPhase.Break: _app.Timer.SkipBreak(); break;
                default:
                    // 自定输入中：有效数值则直接开始倒计时；尚未输入或无效则什么都不做
                    if (_customFocus) { TryCommitCustom(true); return; }
                    StartNow();
                    break;
            }
            _dirty = true;
        }

        /// <summary>按当前档位开始（不改档位）。</summary>
        private void StartNow()
        {
            if (_mode == "break")
            {
                _app.Timer.StartBreak(Math.Max(1, _breakPresetMinutes) * 60);
                return;
            }
            int m = _app.Timer.PlannedSeconds / 60;
            if (m <= 0) m = 5;                 // 兜底：默认最低档
            _app.Timer.StartFocus(m, _app.Timer.Preset);
            _app.RememberPreset();
        }

        private void Hot(string id, RectangleF rect, Action click, bool enabled = true, bool wheel = false, Action<PointF> drag = null)
        {
            // 抽屉内容会被裁剪：热区也必须跟着裁剪，否则滚上去的内容会抢走标题/关闭按钮的点击
            if (_hotClipActive)
            {
                rect.Intersect(_hotClip);
                if (rect.Width <= 0.5f || rect.Height <= 0.5f) return;
            }
            var h = new Hotspot();
            h.Id = id; h.Rect = rect; h.Click = click; h.Enabled = enabled; h.ConsumeWheel = wheel; h.Drag = drag;
            _hot.Add(h);
        }

        // --- 主绘制 --------------------------------------------------------
        public void Draw(Graphics g)
        {
            _hot.Clear();
            _ambientAnimation = false;      // 每帧由勋章绘制重新置位
            _ambientMedals.Clear();         // 每帧重新登记需要动效的勋章
            _ambientCaret = RectangleF.Empty;
            var t = _app.CurrentTheme;
            long ts0 = PerfCounters.Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            g.Clear(t.Bg);
            PerfCounters.Add("清屏", ts0);

            var pt = new Painter(g, Scale, t);

            var b = Bounds;
            _topBar = new RectangleF(0, 0, b.Width, 60);
            float pad = 16f;
            float contentTop = 60 + pad;
            float contentH = Math.Max(200f, b.Height - contentTop - pad);
            float leftW = Math.Max(460f, Math.Min(b.Width * 0.57f, b.Width - 420f));
            _leftRect = new RectangleF(pad, contentTop, leftW, contentH);
            _rightRect = new RectangleF(pad * 2 + leftW, contentTop, Math.Max(240f, b.Width - leftW - pad * 3), contentH);

            DrawTopBar(pt);
            long ts = PerfCounters.Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            DrawFocusPanel(pt);
            PerfCounters.Add("专注面板", ts);
            ts = PerfCounters.Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            DrawRightPanel(pt);
            PerfCounters.Add("右侧(日历+笔记)", ts);
            ts = PerfCounters.Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            DrawParticles(pt);
            PerfCounters.Add("粒子", ts);
            ts = PerfCounters.Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            DrawConfirmBar(pt);
            DrawReminderCard(pt);
            DrawGreetingCard(pt);
            DrawDrawer(pt);      // 抽屉永远在最上层，避免层级与热区错乱
            DrawToast(pt);
            DrawDebugPanel(pt);
            PerfCounters.Add("其它层", ts);
        }

        /// <summary>调试浮窗（连续点击左上番茄 5 次打开）。</summary>
        private void DrawDebugPanel(Painter pt)
        {
            if (!_debugOpen) return;
            var t = pt.T;
            var b = Bounds;

            using (var dim = new SolidBrush(Theme.Alpha(Color.Black, 110)))
                pt.Raw.FillRectangle(dim, 0, 0, b.Width, b.Height);
            Hot("debugBackdrop", new RectangleF(0, 0, b.Width, b.Height), delegate { _debugOpen = false; _dirty = true; });

            float w = 330f, h = 344f;
            var card = new RectangleF(b.Width / 2f - w / 2f, b.Height / 2f - h / 2f, w, h);
            pt.Shadow(card, 18f, 12f, t.Shadow);
            pt.FillRound(card, 16f, t.Surface);
            pt.StrokeRound(card, 16f, Theme.Alpha(t.Border, 200), 1f);

            pt.TextLeft(I18n.T("debug.title"), pt.F(15f, true), t.Text,
                new RectangleF(card.Left + 18, card.Top + 14, card.Width - 60, 24));
            DrawIconButton(pt, "debugClose", new RectangleF(card.Right - 42, card.Top + 12, 28, 28), "close", false,
                delegate { _debugOpen = false; _dirty = true; });

            float y = card.Top + 52f;
            y = DebugButton(pt, card, y, "dbgAdd", I18n.T("debug.addTomatoes"),
                delegate { _app.DebugAddTomatoes(10); });
            y = DebugButton(pt, card, y, "dbgAch", I18n.T("debug.unlockAchievements"),
                delegate { _app.DebugUnlockAllAchievements(); });
            y = DebugButton(pt, card, y, "dbgRw", I18n.T("debug.unlockRewards"),
                delegate { _app.DebugUnlockAllRewards(); });
            y = DebugButton(pt, card, y, "dbgFinish", I18n.T("debug.finishTimer"),
                delegate { _app.DebugCompleteTimer(); _debugOpen = false; _dirty = true; });
            y = DebugButton(pt, card, y, "dbgReport", I18n.T("debug.exportReport"),
                delegate
                {
                    var h2 = DiagnosticsRequested;
                    if (h2 != null) h2();
                    _debugOpen = false;
                    _dirty = true;
                });

            pt.TextCenter(I18n.T("debug.hint"), pt.F(9.5f), t.TextMuted,
                new RectangleF(card.Left, card.Bottom - 26, card.Width, 16));
        }

        private float DebugButton(Painter pt, RectangleF card, float y, string id, string label, Action onClick)
        {
            var t = pt.T;
            var r = new RectangleF(card.Left + 18, y, card.Width - 36, 40);
            float hv = HoverAmount(id);
            pt.FillRound(r, 12f, Theme.Blend(t.SurfaceAlt, t.AccentSoft, hv));
            pt.StrokeRound(r, 12f, Theme.Alpha(Theme.Blend(t.Border, t.Accent, hv), 220), 1f + hv * 0.6f);
            pt.TextCenter(label, pt.F(12.5f, true), Theme.Blend(t.TextMuted, t.AccentDark, hv), r);
            Hot(id, r, onClick);
            return y + 48f;
        }

        // --- 顶栏 ----------------------------------------------------------
        private void DrawTopBar(Painter pt)
        {
            var t = pt.T;
            var b = Bounds;
            pt.FillRound(new RectangleF(0, 0, b.Width, 59), 0, t.Surface);
            using (var sep = new SolidBrush(Theme.Alpha(t.Border, 150)))
                pt.Raw.FillRectangle(sep, 0, 59, b.Width, 1);

            var tomatoRect = new RectangleF(16, 12, 34, 34);
            TomatoArt.Icon(pt, tomatoRect);
            Hot("tomato", tomatoRect, OnTomatoClick);
            var titleFont = pt.F(17f, true);
            var subFont = pt.F(11f);
            pt.Text(I18n.T("app.title"), titleFont, t.Text, new RectangleF(58, 12, 200, 20));
            pt.Text(I18n.T("app.subtitle"), subFont, t.TextMuted, new RectangleF(58, 32, 320, 16));

            // 称号与奖章：两个固定独立的栏位，可同时显示；起点自适应左侧文本宽度，避免与应用标题重叠
            float blockRight = 58f + Math.Max(pt.TextWidth(I18n.T("app.title"), titleFont),
                                               pt.TextWidth(I18n.T("app.subtitle"), subFont));
            float slotX = blockRight + 18f;
            using (var sep = new SolidBrush(Theme.Alpha(t.Border, 190)))
                pt.Raw.FillRectangle(sep, slotX - 10f, 18f, 1f, 24f);

            // 奖章栏位
            var medalSlot = new RectangleF(slotX, 17f, 26f, 26f);
            string medalId = _app.Data.Rewards.EquippedMedal;
            if (!string.IsNullOrEmpty(medalId))
            {
                // 动态勋章：金属渐变 + 扫过的高光；彩色勋章在配色方案间循环
                MedalArt.Draw(pt, medalSlot, medalId, _time);
                RegisterAmbientMedal(medalSlot, medalId, false);
                _ambientAnimation = true;
            }
            else
            {
                pt.StrokeCircle(C(medalSlot), 12.5f, Theme.Alpha(t.Border, 210), 1.3f);
            }
            Hot("slotMedal", medalSlot, delegate { OpenMenuTab("rewards"); });

            // 称号栏位
            string titleName = Rewards.EquippedName(_app.Data);
            var titleSlotFont = pt.F(11.5f, true);
            float pillW = string.IsNullOrEmpty(titleName)
                ? pt.TextWidth(I18n.T("reward.cat.title"), pt.F(10.5f)) + 26f
                : pt.TextWidth(titleName, titleSlotFont) + 36f;
            var titleSlot = new RectangleF(medalSlot.Right + 8f, 17f, pillW, 26f);
            if (!string.IsNullOrEmpty(titleName))
            {
                pt.FillRound(titleSlot, 13f, t.AccentSoft);
                // 图标必须与该称号在兑换列表里的图标一致（此前这里写死 leaf，导致两处不一样）
                IconArt.Draw(pt, Rewards.IconForId(_app.Data.Rewards.EquippedTitle),
                    new RectangleF(titleSlot.Left + 9f, titleSlot.Top + 7f, 12f, 12f), t.AccentDark, 1.4f);
                pt.TextCenterV(titleName, titleSlotFont, t.AccentDark, titleSlot.Left + 25f, titleSlot);
            }
            else
            {
                pt.StrokeRound(titleSlot, 13f, Theme.Alpha(t.Border, 210), 1.2f);
                pt.TextCenter(I18n.T("reward.cat.title"), pt.F(10.5f), Theme.Alpha(t.TextMuted, 160), titleSlot);
            }
            Hot("slotTitle", titleSlot, delegate { OpenMenuTab("rewards"); });

            // 右上角按钮：菜单 + 最小化 + 关闭，距窗口右边缘 18px
            float rightPad = 18f;
            var closeR = new RectangleF(b.Width - rightPad - 36f, 12, 36, 36);
            var minR = new RectangleF(closeR.Left - 44f, 12, 36, 36);
            var menuR = new RectangleF(minR.Left - 44f, 12, 36, 36);

            // 钱包：「可用 x.x 颗」，小数点与后一位用灰色
            var wf = pt.F(13f, true);
            int usableTenths = _app.Data.Wallet.UsableTenths;
            string prefix = I18n.T("wallet.usablePrefix");
            string suffix = I18n.T("wallet.usableSuffix");
            string wholeStr = (usableTenths / 10).ToString();
            string decStr = "." + (usableTenths % 10);
            float prefixW = pt.TextWidth(prefix, wf);
            float wholeW = pt.TextWidth(wholeStr, wf);
            float decW = pt.TextWidth(decStr, wf);
            float suffixW = pt.TextWidth(suffix, wf);
            // 显式像素间距排版（靠字符串首尾空格无法控制实际间距）
            const float padL = 12f, iconW = 18f, iconGap = 8f, numGap = 6f, padR = 14f;
            float totalW = prefixW + numGap + wholeW + decW + numGap + suffixW;
            float badgeW = padL + iconW + iconGap + totalW + padR;
            float rightEdge = menuR.Left - 12f;
            float startX = rightEdge - badgeW;
            var badge = new RectangleF(startX, 12, badgeW, 34);
            WalletBadgeRect = badge;
            pt.FillRound(badge, 17f, t.SurfaceAlt);
            TomatoArt.Icon(pt, new RectangleF(badge.Left + padL, badge.Top + 8f, iconW, iconW));
            float tx = badge.Left + padL + iconW + iconGap;
            pt.TextCenterV(prefix, wf, t.Text, tx, badge); tx += prefixW + numGap;
            pt.TextCenterV(wholeStr, wf, t.Text, tx, badge); tx += wholeW;
            pt.TextCenterV(decStr, wf, t.TextMuted, tx, badge); tx += decW + numGap;
            pt.TextCenterV(suffix, wf, t.Text, tx, badge);

            DrawIconButton(pt, "menu", menuR, "menu", _drawer == "menu",
                delegate
                {
                    // 打开菜单即视为"已读"，红点消失；可兑换数量再增长时重新出现
                    _app.Data.Settings.RewardBadgeSeen = Rewards.AffordableUnowned(_app.Data);
                    _app.MarkDirty();
                    _noteFocus = false;
                    ToggleDrawer("menu");
                });
            DrawIconButton(pt, "min", minR, "minus", false, MinimizeWindow);
            DrawIconButton(pt, "close", closeR, "close", false, CloseWindow);

            // 有可兑换奖励时，在汉堡按钮右上角显示小红点
            if (Rewards.AffordableUnowned(_app.Data) > _app.Data.Settings.RewardBadgeSeen)
            {
                var dot = new PointF(menuR.Right - 8f, menuR.Top + 8f);
                pt.FillCircle(dot, 6.5f, Theme.Alpha(t.Surface, 230));
                pt.FillCircle(dot, 4.4f, t.Accent);
            }

            // 顶栏空白处可拖动窗口（起点让开称号栏位，否则会抢走栏位点击）
            float dragLeft = Math.Max(360f, titleSlot.Right + 12f);
            var drag = new RectangleF(dragLeft, 0, Math.Max(10f, startX - dragLeft), 59);
            Hot("drag", drag, null);
        }

        private Action MinimizeWindow;
        private Action CloseWindow;

        public void SetWindowActions(Action minimize, Action close)
        {
            MinimizeWindow = minimize;
            CloseWindow = close;
        }

        /// <summary>顶栏空白区域可拖动窗口。</summary>
        public bool IsDragArea(PointF p)
        {
            if (p.Y > 59f) return false;
            for (int i = 0; i < _hot.Count; i++)
            {
                var h = _hot[i];
                if (h.Enabled && h.Click != null && h.Rect.Contains(p)) return false;
            }
            return true;
        }

        /// <summary>鼠标是否悬停在可点击元素上（用于切换手型光标）。</summary>
        public bool HoverClickable { get; private set; }

        /// <summary>
        /// 开始收起抽屉：先把当前内容（种类 / 日期 / 滚动位置）冻结成快照，再清空状态。
        /// 关闭动画期间画面照旧，于是只剩面板横向滑出，不会出现"内容先错位再右收"。
        /// </summary>
        private void BeginDrawerClose()
        {
            if (!string.IsNullOrEmpty(_drawer))
            {
                _closingDrawer = _drawer;
                _closingDayKey = _dayKey;
                _closingScroll = _drawerScroll;
            }
            _drawer = "";
            _dayKey = "";
            _dirty = true;
        }

        /// <summary>日历翻页：月视图翻月，年视图翻年（右上箭头、滚轮、左右键共用同一口径）。</summary>
        private void ShiftView(int dir)
        {
            if (dir == 0) return;
            _viewMonth = _yearView ? _viewMonth.AddYears(dir) : _viewMonth.AddMonths(dir);
            _dirty = true;
        }

        private void ToggleDrawer(string name)
        {
            if (_drawer == name) { BeginDrawerClose(); }
            else { _drawer = name; _dayKey = ""; _drawerScroll = 0; }
            _clearStage = 0;
            _noteFocus = false;
            _dirty = true;
        }

        /// <summary>打开菜单抽屉并切到指定标签（供顶栏栏位点击使用）。</summary>
        private void OpenMenuTab(string tab)
        {
            _menuTab = tab;
            _drawer = "menu";
            _dayKey = "";
            _drawerScroll = 0;
            _clearStage = 0;
            _noteFocus = false;
            _dirty = true;
        }

        /// <summary>完成番茄时的庆祝动效（按已装备的动效播放）。</summary>
        public void Celebrate()
        {
            Celebrate(_app.Data.Rewards.EquippedEffect);
        }

        /// <summary>按指定动效播放一次庆祝（effectId 为空时用默认爆散）。</summary>
        public void Celebrate(string effectId)
        {
            _celebrate.Play(1.0);
            var t = _app.CurrentTheme;
            switch (effectId)
            {
                case "ef_confetti":
                    _particles.Confetti(_ringCenter.X - 150f, _ringCenter.Y - 130f, 300f,
                        new[] { t.Accent, t.Leaf, t.Warn, t.AccentDark }, 64);
                    break;
                case "ef_star":
                    _particles.Burst(_ringCenter.X, _ringCenter.Y, t.Warn, t.Accent, 44);
                    break;
                case "ef_tomato":
                    _particles.Confetti(_ringCenter.X - 160f, _ringCenter.Y - 150f, 320f,
                        new[] { t.Accent, t.AccentDark }, 54);
                    _particles.Burst(_ringCenter.X, _ringCenter.Y, t.Accent, t.Leaf, 22);
                    break;
                default:
                    _particles.Burst(_ringCenter.X, _ringCenter.Y, t.Accent, t.Leaf, 52);
                    break;
            }
            _dirty = true;
        }

        /// <summary>当前存活粒子数（供测试观察）。</summary>
        public int ParticleCount { get { return _particles.Count; } }

        /// <summary>装备奖励后给出一次试听/试看反馈。</summary>
        private void PreviewReward(RewardDef def)
        {
            if (def == null) return;
            if (def.Category == "sound")
                Sound.Play(Rewards.SoundIdFor(_app.Data), _app.Data.Settings.Sound);
            else if (def.Category == "effect")
                Celebrate(def.Id);
            else if (def.Category == "medal")
                Pulse();          // 装备勋章时给一段高帧率，让高光扫过看得清
        }

        /// <summary>快照/测试用：直接设置抽屉状态。</summary>
        public void SetDrawer(string drawer, string dayKey = "")
        {
            _drawer = drawer;
            _dayKey = dayKey;
            _drawerScroll = 0;
            A("drawer", 0f, 100f).Jump(string.IsNullOrEmpty(drawer) ? 0f : 1f);
        }

        public void SetRewardCategory(string cat) { _rewardCat = cat; _dirty = true; }
        public void SetMenuTab(string tab) { if (!string.IsNullOrEmpty(tab)) _menuTab = tab; _drawerScroll = 0; _clearStage = 0; _holdProgress = 0f; _scrollTarget = -1f; _dirty = true; }

        /// <summary>自动化/快照用：直接设置「清除数据」的确认阶段。</summary>
        public void SetClearStage(int stage) { _clearStage = stage < 0 ? 0 : stage; _holdProgress = 0f; _dirty = true; }

        /// <summary>收起抽屉（例如切到极简模式前）。</summary>
        public void CloseDrawer() { BeginDrawerClose(); _clearStage = 0; _holdProgress = 0f; _scrollTarget = -1f; }
        public void SetViewMonth(DateTime month) { _viewMonth = new DateTime(month.Year, month.Month, 1); _dirty = true; }
        public void SetYearView(bool on) { _yearView = on; _dirty = true; }
        public string CurrentDrawer { get { return _drawer; } }
        /// <summary>当前"正在画"的抽屉种类：关闭动画期间仍返回刚才那一页。</summary>
        public string CurrentDrawerKind { get { return string.IsNullOrEmpty(_drawer) ? _closingDrawer : _drawer; } }
        public string CurrentMenuTab { get { return _menuTab; } }
        public DateTime ViewMonth { get { return _viewMonth; } }
        public bool IsYearView { get { return _yearView; } }
        public bool IsCustomInputActive { get { return _customFocus; } }
        public string CustomInputText { get { return _customInput; } }
        public float DrawerScroll { get { return _drawerScroll; } }
        public float DrawerMaxScroll { get { return _drawerMaxScroll; } }
        public float CalendarScroll { get { return _calScroll; } }
        public float CalendarMaxScroll { get { return _calMaxScroll; } }
        public bool IsConfirming { get { return _confirmStop; } }
        public PointF RingCenter { get { return _ringCenter; } }
        public string RewardCategory { get { return _rewardCat; } }

        /// <summary>测试用：当前帧的全部热区。</summary>
        public IEnumerable<Hotspot> Hotspots { get { return _hot; } }

        /// <summary>测试与自动化用：取某个热区（需先 Draw 一帧）。</summary>
        public bool TryGetHotspot(string id, out RectangleF rect)
        {
            for (int i = _hot.Count - 1; i >= 0; i--)
                if (_hot[i].Id == id) { rect = _hot[i].Rect; return true; }
            rect = RectangleF.Empty;
            return false;
        }

        /// <summary>测试用：模拟一次点击。</summary>
        public void ClickAt(PointF p)
        {
            OnMouseMove(p, true);
            OnMouseDown(p);
            OnMouseUp(p);
        }

        /// <summary>测试用：模拟输入数字。</summary>
        public void TypeDigits(string digits)
        {
            foreach (char c in digits)
                if (c >= '0' && c <= '9') OnKey((Keys)((int)Keys.D0 + (c - '0')));
        }
        public void SetConfirmStop(bool visible) { _confirmStop = visible; A("confirm", 0f, 100f).Jump(visible ? 1f : 0f); }

        private void DrawIconButton(Painter pt, string id, RectangleF r, string icon, bool active, Action onClick = null)
        {
            var t = pt.T;
            float hv = HoverAmount(id);
            float pv = PressAmount(id);
            // 悬停放大、按下回缩（热区保持原尺寸，避免边缘抖动）
            float grow = hv * 1.6f - pv * 2.4f;
            var rr = RectangleF.Inflate(r, grow, grow);

            // 静息态也给一层浅底，避免看起来像空白占位
            Color bg = active ? t.AccentSoft : Theme.Blend(t.SurfaceAlt, t.AccentSoft, hv);
            pt.FillRound(rr, 10f, bg);
            float borderK = Math.Max(hv, active ? 1f : 0f);
            if (borderK > 0.01f)
                pt.StrokeRound(rr, 10f, Theme.Alpha(t.Accent, (int)(210 * borderK)), 1f + hv * 0.6f);

            var col = active ? t.AccentDark : Theme.Blend(t.TextMuted, t.AccentDark, hv);
            IconArt.Draw(pt, icon, RectangleF.Inflate(rr, -10, -10), col, 1.8f + hv * 0.6f);
            Hot(id, r, onClick);
        }

        // --- 专注面板 ------------------------------------------------------
        private void DrawFocusPanel(Painter pt)
        {
            var t = pt.T;
            var lr = _leftRect;

            float jarH = 44f;
            float btnH = 54f;
            float presetH = 42f;
            float jarTop = lr.Bottom - jarH;
            float btnTop = jarTop - 14 - btnH;
            float presetTop = btnTop - 12 - presetH;

            // 视觉重心上移：下方按钮/概览条会带来下沉感，这里补偿可用高度的 4.5%
            _ringCenter = new PointF(lr.Left + lr.Width / 2f,
                (lr.Top + presetTop) / 2f - (presetTop - lr.Top) * 0.045f);
            _ringRadius = Math.Max(70f, Math.Min(lr.Width * 0.30f, (presetTop - lr.Top) * 0.44f));

            var timer = _app.Timer;
            bool focusing = timer.Phase == TimerPhase.Focusing;
            bool paused = timer.Phase == TimerPhase.Paused;
            bool breaking = timer.Phase == TimerPhase.Break;
            bool hideClock = _app.Data.Settings.HideCountdown;

            // 自定输入过程中，让时间环跟随输入的数字实时变化（只影响预览，不改真实状态）
            int previewMinutes = timer.PlannedSeconds / 60;
            if (timer.Phase == TimerPhase.Idle && _mode == "break") previewMinutes = Math.Max(1, _breakPresetMinutes);
            if (_customFocus)
            {
                // 已点开自定栏但还没输入数字时，时间环显示 00:00；输入后实时跟随
                int typed;
                previewMinutes = (int.TryParse(_customInput, out typed) && typed >= 1 && typed <= 180) ? typed : 0;
            }
            int displaySeconds = timer.Phase == TimerPhase.Idle ? previewMinutes * 60 : timer.RemainingSeconds;
            PreviewMinutes = previewMinutes;

            // 计时进行中，模式自动跟随实际状态
            if (timer.Phase == TimerPhase.Focusing || timer.Phase == TimerPhase.Paused) _mode = "focus";
            else if (timer.Phase == TimerPhase.Break) _mode = "break";

            // 模式配色的渐变进度：与胶囊滑块共用同一个动画值
            var modeAnim = A("mode", 0f, 16f);
            modeAnim.Set(_mode == "break" ? 1f : 0f);
            _modeK = Math.Max(0f, Math.Min(1f, modeAnim.Value));

            // 半径恒定：环与内部文字都不再逐帧变化（避免缩放抖动与每帧新建字体）
            float radius = _ringRadius;
            float ringW = Math.Max(10f, radius * 0.115f);

            // 轨道
            pt.StrokeCircle(_ringCenter, radius, t.RingTrack, ringW);

            // 进度
            float progress = timer.Phase == TimerPhase.Idle ? 0f : (float)timer.Progress;
            Color arcColor = paused ? t.Warn : Tint(t.Accent);
            if (progress > 0.001f)
                pt.Arc(_ringCenter, radius, -90f, 360f * progress, arcColor, ringW);

            // 完成脉冲光环
            if (_celebrate.Playing)
            {
                float k = _celebrate.Value;
                pt.StrokeCircle(_ringCenter, radius * (1f + 0.35f * k), Theme.Alpha(t.Accent, (int)(120 * (1 - k))), ringW * 0.6f);
            }

            // 状态文字
            string status = breaking ? I18n.T("focus.breaking")
                : focusing ? I18n.T("focus.focusing")
                : paused ? I18n.T("focus.paused")
                : I18n.T("focus.idle");
            Color statusColor = breaking ? t.Leaf : (paused ? t.Warn : t.TextMuted);

            if (!hideClock)
            {
                var clockFont = pt.F(Math.Max(30f, radius * 0.52f), true, Painter.MonoFamily);
                pt.TextCenterBaseline(TomatoMath.FormatClock(displaySeconds), clockFont, t.Text,
                    new RectangleF(_ringCenter.X - radius, _ringCenter.Y - radius * 0.42f, radius * 2, radius * 0.84f));
                pt.TextCenter(status, pt.F(Math.Max(12f, radius * 0.135f), true), statusColor,
                    new RectangleF(_ringCenter.X - radius, _ringCenter.Y + radius * 0.30f, radius * 2, 24));
            }
            else
            {
                // 开启「隐藏倒计时」后彻底不绘制时钟，状态文字移到环心，避免中心空洞
                pt.TextCenter(status, pt.F(Math.Max(16f, radius * 0.20f), true), statusColor,
                    new RectangleF(_ringCenter.X - radius, _ringCenter.Y - radius * 0.22f, radius * 2, radius * 0.44f));
            }

            // 本轮预计收获（休息模式不产生收益，不显示）
            if (!breaking && _mode != "break")
            {
                int est = TomatoMath.TenthsForMinutes(Math.Max(1, previewMinutes));
                string hint = I18n.T("cal.earned", TomatoMath.Format(est));
                pt.TextCenter(hint, pt.F(11.5f), t.TextMuted,
                    new RectangleF(_ringCenter.X - radius, _ringCenter.Y + radius * 0.62f, radius * 2, 20));
            }

            // 专注 / 休息 胶囊开关（计时中不可切换）
            float segH = 32f;
            float segTop = presetTop - segH - 10f;
            DrawModeSwitch(pt, new RectangleF(lr.Left, segTop, lr.Width, segH));

            // 档位
            DrawPresets(pt, new RectangleF(lr.Left, presetTop, lr.Width, presetH));

            // 按钮
            // 按钮：暂停/结束各 140px；空闲时开始按钮宽 = 两者之和（292px），
            // 状态切换时播放「水滴分裂/合并」动效
            if (!_confirmStop)
            {
                bool showStop = focusing || paused;
                float gap = 12f;
                float btnW = 140f;
                float pairW = btnW * 2f + gap;

                var splitAnim = A("split", 0f, 15f);
                splitAnim.Set(showStop ? 1f : 0f);
                float split = splitAnim.Value;
                if (split < 0f) split = 0f;
                if (split > 1f) split = 1f;
                float splitShown = Ease.OutBack(split);                 // 轻微回弹，形成"气泡"感

                float pairLeft = lr.Left + (lr.Width - pairW) / 2f;
                // 主按钮左端固定、向右生长
                float primaryW = pairW - (pairW - btnW) * split;
                // 次按钮右端锚定在配对区右端、宽度向左生长；
                // 再钳制到 pairW - primaryW - gap，从几何上保证两个按钮永不重叠、永不越界
                float maxSecondary = pairW - primaryW - gap;
                if (maxSecondary < 0f) maxSecondary = 0f;
                float secondaryW = Math.Min(maxSecondary, Math.Max(0f, splitShown) * btnW);

                var primary = new RectangleF(pairLeft, btnTop, primaryW, btnH);

                string primaryLabel = breaking ? I18n.T("break.skip")
                    : focusing ? I18n.T("focus.pause")
                    : paused ? I18n.T("focus.resume")
                    : I18n.T("focus.start");
                DrawPrimaryButton(pt, "btnPrimary", primary, primaryLabel,
                    breaking ? "pause" : (focusing ? "pause" : "play"), ToggleStartPause, Tint(t.Accent));

                if (secondaryW > 1.5f)
                {
                    var secondary = new RectangleF(pairLeft + pairW - secondaryW, btnTop, secondaryW, btnH);
                    DrawGhostButton(pt, "btnStop", secondary, I18n.T("focus.stop"), "stop", delegate
                    {
                        BeginDrawerClose();
                        _confirmStop = true;
                        _dirty = true;
                    }, showStop);

                    // 接缝处的水滴：随分裂进度缩放并渐隐
                    if (split > 0.02f && split < 0.98f)
                    {
                        float seamX = (primary.Right + secondary.Left) / 2f;
                        float dropR = (float)Math.Sin(Math.PI * split) * 10f;
                        int alpha = (int)(170 * Math.Sin(Math.PI * split));
                        pt.FillCircle(new PointF(seamX, btnTop + btnH / 2f), dropR, Theme.Alpha(t.Accent, alpha));
                    }
                }
            }

            // 底部概览（替代原先与右侧卡片重复的番茄罐）
            DrawSummaryLine(pt, new RectangleF(lr.Left, jarTop, lr.Width, jarH));
        }

        /// <summary>按模式在"专注红"与"休息绿"之间插值，使切换时整体渐变。</summary>
        private Color Tint(Color accent)
        {
            return Theme.Blend(accent, _app.CurrentTheme.Leaf, _modeK);
        }

        /// <summary>模式对应的浅色底（用于悬停填充）。</summary>
        private Color TintSoft()
        {
            var t = _app.CurrentTheme;
            return Theme.Blend(t.AccentSoft, Theme.Shade(t.Leaf, 0.72f), _modeK);
        }

        /// <summary>「专注 / 休息」两段式胶囊，带滑块位移动效。</summary>
        private void DrawModeSwitch(Painter pt, RectangleF row)
        {
            var t = pt.T;
            float segW = 92f, gap = 4f;
            float totalW = segW * 2f + gap;
            float x = row.Left + (row.Width - totalW) / 2f;
            float y = row.Top + (row.Height - 30f) / 2f;
            var track = new RectangleF(x, y, totalW, 30f);
            pt.FillRound(track, 15f, t.SurfaceAlt);
            pt.StrokeRound(track, 15f, Theme.Alpha(t.Border, 200), 1f);

            var modeAnim = A("mode", 0f, 16f);
            bool breakMode = _mode == "break";
            float k = _modeK;

            var pill = new RectangleF(x + 3f + k * (segW + gap), y + 3f, segW - 6f, 24f);
            pt.FillRound(pill, 12f, Tint(t.Accent));

            var segFont = pt.F(12f, true);
            var left = new RectangleF(x, y, segW, 30f);
            var right = new RectangleF(x + segW + gap, y, segW, 30f);
            bool idle = _app.Timer.Phase == TimerPhase.Idle;
            pt.TextCenter(I18n.T("mode.focus"), segFont, Theme.Blend(Color.White, t.TextMuted, k), left);
            pt.TextCenter(I18n.T("mode.break"), segFont, Theme.Blend(t.TextMuted, Color.White, k), right);

            Hot("modeFocus", left, delegate { SetMode("focus"); }, idle);
            Hot("modeBreak", right, delegate { SetMode("break"); }, idle);
        }

        private void DrawPresets(Painter pt, RectangleF row)
        {
            var t = pt.T;
            bool breakMode = _mode == "break";
            var presets = new List<int>(breakMode ? new[] { 5, 10, 15 } : new[] { 5, 15, 25 });

            var font = pt.F(12.5f, true);
            float gap = 8f;
            int currentMin = breakMode ? _breakPresetMinutes : _app.Timer.PlannedSeconds / 60;
            bool locked = _app.Timer.Phase != TimerPhase.Idle;

            // 自定栏位：保留本次运行内设过的数字，不再新建挡位
            int customValue = breakMode ? _app.Data.Settings.BreakCustomMinutes : _app.Data.Settings.CustomMinutes;
            string customLabel = customValue > 0
                ? I18n.T("focus.minutes", customValue)
                : I18n.T("focus.custom");
            bool customSelected = breakMode
                ? (_breakPresetMinutes != 5 && _breakPresetMinutes != 10 && _breakPresetMinutes != 15)
                : string.Equals(_app.Timer.Preset, "custom", StringComparison.Ordinal);

            // 先量总宽，再整体居中，使档位行与开始按钮对齐同一中心
            float customW = pt.TextWidth(customLabel, font) + 34f;
            float total = customW;
            foreach (int m in presets) total += pt.TextWidth(I18n.T("focus.minutes", m), font) + 30f + gap;
            float x = row.Left + Math.Max(0f, (row.Width - total) / 2f);

            foreach (int m in presets)
            {
                string label = I18n.T("focus.minutes", m);
                float w = pt.TextWidth(label, font) + 30f;
                var r = new RectangleF(x, row.Top + 4, w, row.Height - 8);
                bool selected = currentMin == m;
                string id = "preset" + m;
                float hv = HoverAmount(id);
                float pv = PressAmount(id);
                var rr = RectangleF.Inflate(r, -pv * 1.5f + hv * 1.4f, -pv * 1.5f + hv * 1.4f);
                if (selected)
                {
                    pt.FillRound(rr, rr.Height / 2f, Tint(t.Accent));
                }
                else
                {
                    pt.FillRound(rr, rr.Height / 2f, Theme.Blend(t.SurfaceAlt, TintSoft(), hv));
                    pt.StrokeRound(rr, rr.Height / 2f, Theme.Alpha(Theme.Blend(t.Border, Tint(t.Accent), hv), 235), 1f + hv * 0.8f);
                }
                pt.TextCenter(label, font,
                    selected ? Color.White : Theme.Blend(t.TextMuted, Theme.Shade(Tint(t.Accent), -0.25f), hv), rr);
                int mm = m;
                string ps = mm == 5 ? "5" : mm == 15 ? "15" : mm == 25 ? "25" : "custom";
                Hot(id, r, delegate { SelectPreset(mm, ps); });
                x += w + gap;
            }

            // 自定：就地输入（不弹二级面板）
            {
                float w = customW;
                if (x + w > row.Right) w = Math.Max(64f, row.Right - x);
                var r = new RectangleF(x, row.Top + 4, w, row.Height - 8);
                _customChipRect = r;

                float hv = HoverAmount("presetCustom");
                var rr = RectangleF.Inflate(r, -PressAmount("presetCustom") * 1.5f, -PressAmount("presetCustom") * 1.5f);

                if (_customFocus)
                {
                    pt.FillRound(rr, rr.Height / 2f, t.Surface);
                    pt.StrokeRound(rr, rr.Height / 2f, Tint(t.Accent), 1.6f);
                    // 格子里只显示输入内容 + 闪烁光标
                    string shown = _customInput.Length == 0 ? "" : _customInput;
                    bool caretOn = ((int)(_time * 2)) % 2 == 0;
                    pt.TextCenter(shown + (caretOn ? "|" : " "), font, Theme.Shade(Tint(t.Accent), -0.25f), rr);
                }
                else if (customSelected)
                {
                    // 选中的自定档位必须也有选中态：此前完全没有，看起来像"点了没生效"
                    pt.FillRound(rr, rr.Height / 2f, Tint(t.Accent));
                    pt.TextCenter(customLabel, font, Color.White, rr);
                }
                else
                {
                    pt.FillRound(rr, rr.Height / 2f, Theme.Blend(t.SurfaceAlt, TintSoft(), hv));
                    pt.StrokeRound(rr, rr.Height / 2f, Theme.Alpha(t.Border, 220), 1f);
                    pt.TextCenter(customLabel, font, Theme.Blend(t.TextMuted, t.Text, hv), rr);
                }

                Hot("presetCustom", r, delegate
                {
                    if (_app.Timer.Phase != TimerPhase.Idle) { ShowToast(I18n.T("focus.presetLocked")); return; }
                    _customFocus = true;
                    _customInput = "";
                });

                // 提示改为向上弹出的气泡，避免文字挤在格子里溢出
                var hintAnim = A("customHint", 0f, 16f);
                hintAnim.Set(_customFocus ? 1f : 0f);
                float hk = hintAnim.Value;
                if (hk > 0.01f)
                {
                    string hint = I18n.T("focus.custom.hint");
                    var hf = pt.F(11f);
                    float bw2 = pt.TextWidth(hint, hf) + 26f;
                    float bh2 = 30f;
                    float bx2 = r.Left + r.Width / 2f - bw2 / 2f;
                    if (bx2 < 8f) bx2 = 8f;
                    if (bx2 + bw2 > Bounds.Width - 8f) bx2 = Bounds.Width - 8f - bw2;
                    var bubble = new RectangleF(bx2, r.Top - bh2 - 12f + (1f - hk) * 8f, bw2, bh2);
                    float tailX = r.Left + r.Width / 2f;

                    // 白色气泡：圆角矩形与小尾巴合成一条路径一次性填充，避免接缝
                    float tipY = bubble.Bottom + 8f;
                    using (var bubblePath = new System.Drawing.Drawing2D.GraphicsPath())
                    using (var tailPath = new System.Drawing.Drawing2D.GraphicsPath())
                    {
                        bubblePath.FillMode = System.Drawing.Drawing2D.FillMode.Winding;
                        bubblePath.AddPath(Painter.RoundedPath(bubble, 9f), false);
                        bubblePath.AddPolygon(new[]
                        {
                            new PointF(tailX - 7f, bubble.Bottom - 2f),
                            new PointF(tailX + 7f, bubble.Bottom - 2f),
                            new PointF(tailX, tipY)
                        });
                        tailPath.AddPolygon(new[]
                        {
                            new PointF(tailX - 7f, bubble.Bottom - 2f),
                            new PointF(tailX + 7f, bubble.Bottom - 2f),
                            new PointF(tailX, tipY)
                        });

                        var border = Theme.Alpha(t.Accent, (int)(115 * hk));
                        pt.Shadow(bubble, 9f, 7f, Theme.Alpha(Color.Black, (int)(65 * hk)));
                        pt.FillPath(bubblePath, t.Surface);
                        using (var rectPath = Painter.RoundedPath(bubble, 9f))
                            pt.StrokePath(rectPath, border, 1.1f);
                        // 用小三角盖住矩形下边框的接缝，再补上尾巴两侧的斜边
                        pt.FillPath(tailPath, t.Surface);
                        pt.Line(new PointF(tailX - 7f, bubble.Bottom - 2f), new PointF(tailX, tipY), border, 1.1f, false);
                        pt.Line(new PointF(tailX + 7f, bubble.Bottom - 2f), new PointF(tailX, tipY), border, 1.1f, false);
                    }
                    pt.TextCenter(hint, hf, t.Text, bubble);
                }

                x += w + gap;
            }
        }

        /// <summary>底部概览：本周 / 连续天数 / 累计，与右侧“今日”卡片不重复。</summary>
        private void DrawSummaryLine(Painter pt, RectangleF r)
        {
            var t = pt.T;
            int weekTenths = Stats.WeekTenths(_app.Data, DateTime.Now);
            int streak = Stats.Streak(_app.Data, DateTime.Now);
            int totalWhole = TomatoMath.Whole(_app.Data.Wallet.TotalTenths);
            string text = I18n.T("week.summary", TomatoMath.Format(weekTenths), streak, totalWhole);
            pt.TextCenter(text, pt.F(12f), t.TextMuted, r);
        }

        private void DrawPrimaryButton(Painter pt, string id, RectangleF r, string label, string icon, Action onClick, Color tint)
        {
            var t = pt.T;
            float hv = HoverAmount(id);
            float pv = PressAmount(id);
            var rr = RectangleF.Inflate(r, -pv * 2f + hv * 1.5f, -pv * 2f + hv * 1.5f);
            rr.Offset(0, -hv * 1.2f);   // 悬停时轻微上浮
            pt.Shadow(rr, 16f, 8f + hv * 5f, Theme.Alpha(tint, (int)(55 + 70 * hv)));
            pt.VerticalGradient(rr, Theme.Shade(tint, 0.10f + hv * 0.14f), Theme.Shade(tint, -0.22f + hv * 0.10f), 16f);

            var font = pt.F(15.5f, true);
            float iconW = 22f;
            float tw = pt.TextWidth(label, font);
            float total = tw + iconW + 10f;
            float startX = rr.Left + (rr.Width - total) / 2f;
            IconArt.Draw(pt, icon, new RectangleF(startX, rr.Top + rr.Height / 2 - 10, 20, 20), Color.White, 2.2f);
            pt.TextCenterV(label, font, Color.White, startX + iconW + 10, rr);
            Hot(id, r, onClick);
        }

        private void DrawGhostButton(Painter pt, string id, RectangleF r, string label, string icon, Action onClick, bool showLabel = true)
        {
            var t = pt.T;
            float hv = HoverAmount(id);
            float pv = PressAmount(id);
            var rr = RectangleF.Inflate(r, -pv * 2f + hv * 1.2f, -pv * 2f + hv * 1.2f);
            pt.FillRound(rr, 16f, Theme.Blend(t.Surface, t.SurfaceAlt, hv));
            pt.StrokeRound(rr, 16f, Theme.Alpha(Theme.Blend(t.Border, Tint(t.Accent), hv), 230), 1f + hv * 0.9f);
            var font = pt.F(13f, true);
            var col = Theme.Blend(t.TextMuted, Theme.Shade(Tint(t.Accent), -0.25f), hv);
            float iconW = 18f;
            float tw = pt.TextWidth(label, font);
            float total = tw + iconW + 8f;
            // 合并瞬间（showLabel=false）立即不画标签；按钮宽度不足时也不画，避免文字溢出压住主按钮
            if (showLabel && rr.Width >= total + 14f)
            {
                float startX = rr.Left + (rr.Width - total) / 2f;
                IconArt.Draw(pt, icon, new RectangleF(startX, rr.Top + rr.Height / 2 - 8, 16, 16), col, 1.8f);
                pt.TextCenterV(label, font, col, startX + iconW + 8, rr);
            }
            Hot(id, r, onClick);
        }
    }
}
