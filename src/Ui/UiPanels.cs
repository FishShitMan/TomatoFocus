using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;
using TomatoFocus.Core;
using TomatoFocus.Render;
using TomatoFocus.Render.Art;

namespace TomatoFocus.Ui
{
    internal sealed partial class UiRoot
    {
        private bool _confirmStop;
        private RectangleF _confirmBarRect;
        private bool _yearView;
        private int _clearStage;          // 0 未开始 / 1 一级确认 / 2 二级确认

        private float _yearCellW, _yearCellH;    // 年视图单元尺寸（供测试断言"铺满卡片"）

        public float YearCellWidth { get { return _yearCellW; } }
        public float YearCellHeight { get { return _yearCellH; } }

        private const float MonthViewCellRef = 56f;   // 月视图格子的参考边长；1.4px 的今天边框就是为这个尺寸定的

        /// <summary>今天格的边框颜色（跟随主题）：无番茄 = 主题强调色，有番茄 = 主题警示黄；今天不在当前年份时为 Empty。</summary>
        public Color YearTodayBorderColor { get; private set; }

        /// <summary>今天格边框线宽：按格子尺寸等比缩小，不直接套用月视图的 1.4px。</summary>
        public float YearTodayBorderWidth { get; private set; }

        /// <summary>年视图格子的圆角（填充与边框共用同一个值，保证边框紧贴格子）。</summary>
        private static float YearCellRadius(RectangleF cell)
        {
            return Math.Min(3f, Math.Min(cell.Width, cell.Height) / 3f);
        }

        /// <summary>
        /// 年视图格子配色：0 颗 = 默认色（SurfaceAlt）；有记录则按"番茄数 / 当年最高"
        /// 从默认色渐变到主题强调色，并给最小档 25% 起步——保证"只吃了 1 颗"和"完全没记录"一眼能分开。
        /// </summary>
        public Color YearCellColor(int tenths, int best)
        {
            var th = _app.CurrentTheme ?? Theme.ById(_app.Data.Settings.ThemeId);
            if (tenths <= 0) return th.SurfaceAlt;
            float k = 0.25f + 0.75f * Math.Min(1f, tenths / (float)Math.Max(1, best));
            return Theme.Blend(th.SurfaceAlt, th.Accent, k);
        }

        /// <summary>
        /// 年视图：12 行（月）× 31 列（日）热力图，一格一天。
        /// 旧版用 53×7 的"周"布局，格子宽度被 53 列摊薄到 ~7px，7 行只占卡片高度的六分之一，
        /// 月份刻度还贴在卡片最底部，和上方格子完全脱节。现在改为按卡片宽度铺满、
        /// 纵向居中，月份标签就写在每一行左边（与所在行同行对齐），顶部加一条日期刻度。
        /// 配色也不再靠"同一个红加不同透明度"（那样深浅过近），而是按番茄数由默认色渐变到强调色；
        /// 今天改用"细一圈的边框"提示（口径与月视图一致，线宽按格子尺寸等比缩小）：
        /// 没有番茄时用主题强调色，有番茄时改用主题警示黄；填充始终按番茄数走渐变。
        /// </summary>
        private void DrawYearView(Painter pt, RectangleF grid, int year)
        {
            var t = pt.T;
            var monthFont = pt.F(10f);
            float labelW = pt.TextWidth("12月", monthFont) + 12f;   // 左侧月份标签栏
            float rulerH = 15f;                                     // 顶部日期刻度
            var plot = new RectangleF(grid.Left + labelW, grid.Top + rulerH,
                Math.Max(60f, grid.Width - labelW), Math.Max(40f, grid.Height - rulerH));

            float cw = plot.Width / 31f;
            // 行高最多取宽度的 2.2 倍：既把卡片填起来，又不至于把格子拉成细长条
            float ch = Math.Min(plot.Height / 12f, cw * 2.2f);
            float gap = Math.Max(0.8f, Math.Min(cw, ch) * 0.18f);
            float top = plot.Top + (plot.Height - ch * 12f) / 2f;
            _yearCellW = cw - gap;
            _yearCellH = ch - gap;

            int best = 1;
            for (int m = 1; m <= 12; m++)
            {
                int days = DateTime.DaysInMonth(year, m);
                for (int d = 1; d <= days; d++)
                {
                    var st = Stats.DayOf(_app.Data, DayKey.Of(new DateTime(year, m, d)));
                    if (st != null && st.Tenths > best) best = st.Tenths;
                }
            }

            // 今天格边框的线宽：按格子尺寸等比缩小。月视图 56px 的格子配 1.4px 恰好，
            // 年视图格子只有 ~11px，直接套 1.4px 会比格子本身还抢眼；下限 0.8px 保证仍然看得见。
            float minSide = Math.Min(_yearCellW, _yearCellH);
            YearTodayBorderWidth = Math.Max(0.8f, Math.Min(1.4f, 1.4f * minSide / MonthViewCellRef));
            YearTodayBorderColor = Color.Empty;      // 今天不在当前年份就不画边框

            // 日期刻度：1 / 6 / 11 / 16 / 21 / 26 / 31，让列与"几号"对得上
            for (int d = 1; d <= 31; d += 5)
            {
                pt.TextCenter(d.ToString(), pt.F(9f), t.TextMuted,
                    new RectangleF(plot.Left + (d - 1) * cw - cw / 2f, grid.Top, cw * 2f, rulerH));
            }

            for (int m = 1; m <= 12; m++)
            {
                int days = DateTime.DaysInMonth(year, m);
                float rowTop = top + (m - 1) * ch;
                pt.TextRight(m + "月", monthFont, t.TextMuted, new RectangleF(grid.Left, rowTop, labelW - 6f, ch));

                for (int d = 1; d <= days; d++)
                {
                    var key = DayKey.Of(new DateTime(year, m, d));
                    var st = Stats.DayOf(_app.Data, key);
                    int tenths = st == null ? 0 : st.Tenths;
                    bool isToday = key == DayKey.Today;
                    var cell = new RectangleF(plot.Left + (d - 1) * cw, rowTop, cw - gap, ch - gap);

                    // 填充完全按番茄数走渐变（今天不特判）：0 颗 = 默认色，越多越接近主题强调色
                    Color fill = YearCellColor(tenths, best);
                    float radius = YearCellRadius(cell);

                    pt.FillRound(cell, radius, fill);
                    // 今天用"细一圈的边框"提示，口径与月视图一致：没有番茄时用主题强调色；
                    // 有番茄时改用主题警示黄——否则边框会被红色填充盖住、根本分不出来。
                    if (isToday)
                    {
                        YearTodayBorderColor = tenths > 0 ? t.Warn : Theme.Alpha(t.Accent, 200);
                        pt.StrokeRound(cell, radius, YearTodayBorderColor, YearTodayBorderWidth);
                    }
                    string k = key;
                    Hot("y" + key, cell, delegate
                    {
                        _dayKey = k;
                        _drawer = "day";
                        _drawerScroll = 0;
                    });
                }
            }
        }

        // --- 右侧：今日卡片 + 日历 -----------------------------------------
        private void DrawRightPanel(Painter pt)
        {
            var t = pt.T;
            var r = _rightRect;

            // 今日卡片
            var card = new RectangleF(r.Left, r.Top, r.Width, 96);
            pt.Shadow(card, 16f, 7f, t.Shadow);
            pt.FillRound(card, 16f, t.Surface);

            int todayTenths = Stats.TodayTenths(_app.Data);
            DayStat today = Stats.DayOf(_app.Data, DayKey.Today);
            int focusMin = today == null ? 0 : today.FocusedSec / 60;
            int frag = TomatoMath.Fragment(todayTenths);

            // 第一行：今日 X（左） + 专注 X 分钟（右，同一基线）
            var line1 = new RectangleF(card.Left + 18, card.Top + 12, card.Width - 36, 28);
            pt.TextLeft(I18n.T("today.tomatoes", TomatoMath.Format(todayTenths)), pt.F(19f, true), t.Text, line1);
            pt.TextRight(I18n.T("today.minutes", focusMin), pt.F(12f), t.TextMuted, line1);

            // 第二行：碎片进度（与进度条分离，不再与上一行重叠）
            var line2 = new RectangleF(card.Left + 18, card.Top + 46, card.Width - 36, 16);
            pt.TextLeft(I18n.T("wallet.fragmentProgress", TomatoMath.FormatFragment(frag)), pt.F(10.5f), t.TextMuted, line2);

            // 碎片进度条
            var barBg = new RectangleF(card.Left + 18, card.Top + 68, card.Width - 36, 8);
            pt.FillRound(barBg, 4f, t.SurfaceAlt);
            if (frag > 0)
                pt.FillRound(new RectangleF(barBg.Left, barBg.Top, barBg.Width * frag / 10f, barBg.Height), 4f, t.Warn);

            // 日历
            float notesH = 116f;
            var notesCard = new RectangleF(r.Left, r.Bottom - notesH, r.Width, notesH);
            float calTop = card.Bottom + 14f;
            float calH = Math.Max(170f, notesCard.Top - 14f - calTop);
            _calendarRect = new RectangleF(r.Left, calTop, r.Width, calH);
            long t1 = PerfCounters.Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            DrawCalendar(pt, _calendarRect);
            PerfCounters.Add("  日历", t1);
            t1 = PerfCounters.Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            DrawNotes(pt, notesCard);
            PerfCounters.Add("  笔记", t1);
        }

        /// <summary>日历下方的「笔记」栏位：内容随本次番茄钟一起保存。</summary>
        private void DrawNotes(Painter pt, RectangleF card)
        {
            var t = pt.T;
            pt.Shadow(card, 16f, 7f, t.Shadow);
            pt.FillRound(card, 16f, t.Surface);
            if (_noteFocus) pt.StrokeRound(card, 16f, Theme.Alpha(t.Accent, 210), 1.4f);

            var head = new RectangleF(card.Left + 16, card.Top + 8, card.Width - 32, 20);
            pt.TextLeft(I18n.T("notes.title"), pt.F(13f, true), t.Text, head);
            pt.TextRight(I18n.T(_noteFocus ? "notes.hintFocus" : "notes.bound"), pt.F(9.5f), t.TextMuted, head);

            var area = new RectangleF(card.Left + 16, card.Top + 32, card.Width - 32, card.Height - 42);
            string text = _app.Data.Settings.NoteDraft ?? "";
            var font = pt.F(11.5f);
            const float lh = 16f;
            // 右侧留出 14px：光标不贴死右边缘，输入法组合串才有就地显示的空间
            const float noteTextW = 14f;
            _noteCaretRect = RectangleF.Empty;
            if (string.IsNullOrEmpty(text))
            {
                // 聚焦时不画占位提示：光标就在同一位置，快路径擦/画光标会把它啃掉一块
                if (!_noteFocus)
                    pt.Text(I18n.T("notes.placeholder"), font, Theme.Alpha(t.TextMuted, 150),
                        new RectangleF(area.Left, area.Top, area.Width, area.Height));
                _noteCaret = new PointF(area.Left + 1f, area.Top + 1f);
                if (_noteFocus) _noteCaretRect = CaretRect(area.Left + 1f, area.Top);
            }
            else
            {
                var lines = pt.WrapLines(text, font, area.Width - noteTextW);
                int maxLines = Math.Max(1, (int)(area.Height / lh));
                int start = Math.Max(0, lines.Count - maxLines);     // 内容过长时显示最后几行
                for (int i = start; i < lines.Count; i++)
                    pt.Text(lines[i], font, t.Text, new RectangleF(area.Left, area.Top + (i - start) * lh, area.Width - noteTextW, lh));

                // 组合窗口锚点 = 最后一行的行尾；y 取"行顶"，见 NoteCaretPoint 的说明
                if (lines.Count > 0)
                {
                    float cx = area.Left + pt.TextWidth(lines[lines.Count - 1], font) + 1f;
                    float lineTop = area.Top + (lines.Count - 1 - start) * lh;
                    _noteCaret = new PointF(cx, lineTop + 1f);
                    if (_noteFocus) _noteCaretRect = CaretRect(cx, lineTop);
                }
            }

            // 光标是独立的一根细条（不再拼进文本里）：这样"勋章快路径"可以只擦掉/重画这一小块
            if (_noteFocus && _noteCaretRect.Width > 0f)
            {
                RegisterAmbientCaret(_noteCaretRect, t.Surface, t.Text);
                if (CaretOn) pt.FillRound(_noteCaretRect, _noteCaretRect.Width / 2f, t.Text);
            }
            Hot("notesArea", card, delegate { _noteFocus = true; _dirty = true; });
        }

        private void DrawCalendar(Painter pt, RectangleF card)
        {
            var t = pt.T;
            pt.Shadow(card, 16f, 7f, t.Shadow);
            pt.FillRound(card, 16f, t.Surface);

            var head = new RectangleF(card.Left + 12, card.Top + 10, card.Width - 24, 30);
            // 年视图只留年份（月份由每行左侧的标签承担，不再重复显示"某年某月"）
            string title = _yearView
                ? I18n.T("cal.titleYear", _viewMonth.Year)
                : I18n.T("cal.title", _viewMonth.Year, _viewMonth.Month);
            pt.TextLeft(title, pt.F(14.5f, true), t.Text, head);

            // 翻页：月视图翻月，年视图翻年
            DrawIconButton(pt, "prevMonth", new RectangleF(head.Right - 74, head.Top + 1, 28, 28), "chevronLeft", false,
                delegate { ShiftView(-1); });
            DrawIconButton(pt, "nextMonth", new RectangleF(head.Right - 38, head.Top + 1, 28, 28), "chevronRight", false,
                delegate { ShiftView(1); });

            // 今天
            var todayBtn = new RectangleF(head.Right - 74 - 62, head.Top + 2, 56, 26);
            float th = HoverAmount("todayBtn");
            pt.FillRound(todayBtn, 13f, Theme.Blend(t.SurfaceAlt, t.AccentSoft, th));
            pt.TextCenter(I18n.T("cal.today"), pt.F(11.5f, true), Theme.Blend(t.TextMuted, t.Text, th), todayBtn);
            Hot("todayBtn", todayBtn, delegate
            {
                _viewMonth = new DateTime(DateTime.Now.Year, DateTime.Now.Month, 1);
                _yearView = false;
            });

            // 月 / 年 视图切换
            var viewBtn = new RectangleF(todayBtn.Left - 66, head.Top + 2, 60, 26);
            float vh = HoverAmount("viewBtn");
            pt.FillRound(viewBtn, 13f, Theme.Blend(t.SurfaceAlt, t.AccentSoft, vh));
            pt.TextCenter(I18n.T(_yearView ? "cal.month" : "cal.year"), pt.F(11.5f, true),
                Theme.Blend(t.TextMuted, t.Text, vh), viewBtn);
            Hot("viewBtn", viewBtn, delegate { _yearView = !_yearView; });

            var grid = new RectangleF(card.Left + 12, head.Bottom + 6, card.Width - 24, card.Bottom - head.Bottom - 18);

            if (_yearView)
            {
                DrawYearView(pt, grid, _viewMonth.Year);
                return;
            }

            // 星期表头
            string[] wd = I18n.List("cal.weekdays");
            if (wd.Length < 7) wd = new[] { "一", "二", "三", "四", "五", "六", "日" };
            // 网格：按可用高度收缩单元格，并整体裁剪，避免盖到下方的笔记
            float cellW = grid.Width / 7f;
            float headH = 22f;
            float rowsH = Math.Max(40f, grid.Height - headH);
            // 单元格不低于 30px 以保证可读；放不下时改为网格内部滚动（已做裁剪，绝不越界）
            float cellH = Math.Max(30f, Math.Min(cellW * 1.05f, rowsH / 6f));
            float contentRowsH = cellH * 6f;
            _calMaxScroll = Math.Max(0f, contentRowsH - rowsH);
            if (_calScroll > _calMaxScroll) _calScroll = _calMaxScroll;
            if (_calScroll < 0f) _calScroll = 0f;

            for (int i = 0; i < 7; i++)
            {
                var hr = new RectangleF(grid.Left + i * cellW, grid.Top, cellW, headH);
                pt.TextCenter(wd[i], pt.F(11f, true), (i >= 5 ? t.Accent : t.TextMuted), hr);
            }

            DateTime first = new DateTime(_viewMonth.Year, _viewMonth.Month, 1);
            int lead = ((int)first.DayOfWeek + 6) % 7;
            int daysInMonth = DateTime.DaysInMonth(_viewMonth.Year, _viewMonth.Month);
            int bestDay = 0;
            for (int d = 1; d <= daysInMonth; d++)
            {
                var st = Stats.DayOf(_app.Data, DayKey.Of(new DateTime(_viewMonth.Year, _viewMonth.Month, d)));
                if (st != null && st.Tenths > bestDay) bestDay = st.Tenths;
            }
            if (bestDay <= 0) bestDay = 10;

            var rowsView = new RectangleF(grid.Left, grid.Top + headH, grid.Width, rowsH);
            float scroll = _calScroll;
            pt.Clip(rowsView, delegate
            {
                long tcell = PerfCounters.Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
                for (int d = 1; d <= daysInMonth; d++)
                {
                    int idx = lead + d - 1;
                    int col = idx % 7, row = idx / 7;
                    var cell = new RectangleF(
                        rowsView.Left + col * cellW + 2,
                        rowsView.Top + row * cellH + 2 - scroll,
                        cellW - 4, cellH - 4);

                    var key = DayKey.Of(new DateTime(_viewMonth.Year, _viewMonth.Month, d));
                    var st = Stats.DayOf(_app.Data, key);
                    int tenths = st == null ? 0 : st.Tenths;
                    bool isToday = key == DayKey.Today;
                    string id = "day" + key;

                    float hv = HoverAmount(id);
                    Color fill = tenths > 0
                        ? Theme.Alpha(t.AccentSoft, (int)(90 + 130f * Math.Min(1f, tenths / (float)bestDay)))
                        : t.SurfaceAlt;
                    if (isToday) fill = Theme.Blend(fill, t.AccentSoft, 0.6f);

                    // 悬停：整格过渡为实心深红色块，过半后日期/番茄/数量切换为反色剪影。
                    // 用 AccentDark 而非 Accent 是为了对比度：白字压 #C13E2A 约 5.3:1（达 WCAG AA），
                    // 压亮红 #E2543C 只有 3.75:1，小字号会发虚。
                    fill = Theme.Blend(fill, t.AccentDark, hv);
                    bool mono = hv > 0.42f;

                    pt.FillRound(cell, 9f, fill);
                    if (isToday)
                        pt.StrokeRound(cell, 9f,
                            mono ? Theme.Alpha(Color.White, 210) : Theme.Alpha(t.Accent, 200), 1.4f);

                    pt.Text(d.ToString(), pt.F(10.5f, isToday || mono),
                        mono ? Color.White : (isToday ? t.AccentDark : t.TextMuted),
                        new RectangleF(cell.Left + 5, cell.Top + 3, 24, 14));

                    if (tenths > 0)
                    {
                        float gs = Math.Min(cell.Width * 0.46f, cell.Height * 0.52f);
                        var gr = new RectangleF(cell.Left + cell.Width / 2f - gs / 2f, cell.Top + cell.Height * 0.30f, gs, gs);
                        bool aborted = st != null && st.AbortedCount > 0;
                        int inTomato = tenths >= 10 ? 10 : tenths;
                        if (mono) TomatoArt.DrawTenthsMono(pt, gr, inTomato, Color.White);
                        else TomatoArt.DrawTenths(pt, gr, inTomato, aborted && tenths < 10);
                        if (tenths >= 10)
                            pt.TextRight(TomatoMath.Format(tenths), pt.F(9.5f, true),
                                mono ? Color.White : t.AccentDark,
                                new RectangleF(cell.Left, cell.Bottom - 15, cell.Width - 4, 14));
                        if (aborted)
                            pt.FillCircle(new PointF(cell.Right - 7, cell.Top + 7), 2.6f,
                                mono ? Theme.Alpha(Color.White, 230) : t.Warn);
                    }

                    string k = key;
                    Hot(id, cell, delegate
                    {
                        _dayKey = k;
                        _drawer = "day";
                        _drawerScroll = 0;
                        _dirty = true;
                    });
                }
                PerfCounters.Add("  日历格", tcell);
            });

            // 内部滚动条：需要时出现，1.2 秒无操作自动淡出
            var barAnim = A("calbar", 0f, 10f);
            barAnim.Set(_time < _calBarUntil || _hoverId.StartsWith("day", StringComparison.Ordinal) ? 1f : 0f);
            if (_calMaxScroll > 0.5f)
            {
                float ba = barAnim.Value;
                float thumbH = Math.Max(28f, rowsH * rowsH / contentRowsH);
                float thumbY = rowsView.Top + (rowsH - thumbH) * (_calScroll / _calMaxScroll);
                var track = new RectangleF(card.Right - 9f, rowsView.Top, 8f, rowsH);
                if (ba > 0.02f)
                {
                    pt.FillRound(new RectangleF(track.Left + 2.5f, track.Top, 3f, track.Height), 1.5f, Theme.Alpha(t.TextMuted, (int)(26 * ba)));
                    pt.FillRound(new RectangleF(track.Left + 1.5f, thumbY, 5f, thumbH), 2.5f, Theme.Alpha(t.TextMuted, (int)(130 * ba)));
                }
                float trackTop = track.Top, thumbH0 = thumbH, ms = _calMaxScroll, viewH = rowsH;
                Hot("calBar", track, delegate
                {
                    float ratio = (_clickPoint.Y - trackTop - thumbH0 / 2f) / Math.Max(1f, viewH - thumbH0);
                    _calScroll = Math.Max(0f, Math.Min(ms, ratio * ms));
                    _calBarUntil = _time + 1.2;
                    _dirty = true;
                }, true, false, delegate (PointF p)
                {
                    float ratio = (p.Y - trackTop - thumbH0 / 2f) / Math.Max(1f, viewH - thumbH0);
                    _calScroll = Math.Max(0f, Math.Min(ms, ratio * ms));
                    _calBarUntil = _time + 1.2;
                });
            }
        }

        // --- 抽屉 ----------------------------------------------------------
        private void DrawDrawer(Painter pt)
        {
            float k = A("drawer").Value;
            if (k < 0.01f)
            {
                _closingDrawer = "";          // 动画彻底收敛，关闭快照丢掉
                _closingDayKey = "";
                return;
            }
            var t = pt.T;
            var b = Bounds;
            bool open = !string.IsNullOrEmpty(_drawer);
            // 关闭动画期间沿用"刚才那一页"的内容、日期与滚动位置：面板只做横向滑出，
            // 不会串页、标题不会提前消失、滚动位置也不会被就地归零。
            string kind = open ? _drawer : _closingDrawer;
            string dayKey = open ? _dayKey : _closingDayKey;
            float scroll = open ? _drawerScroll : _closingScroll;

            // 点击抽屉以外区域关闭（顶栏除外，便于直接切换抽屉）
            Hot("backdrop", new RectangleF(0, 60, b.Width, Math.Max(0, b.Height - 60)), delegate
            {
                _confirmStop = false;
                BeginDrawerClose();
            });

            // 遮罩与面板都从顶栏下方开始：顶栏（标题/钱包/菜单/窗口按钮）始终可见可点
            using (var dim = new SolidBrush(Theme.Alpha(Color.Black, (int)(46 * k))))
                pt.Raw.FillRectangle(dim, 0, 60, b.Width, Math.Max(0, b.Height - 60));

            float w = Math.Min(440f, b.Width - 80f);
            float panelTop = 60f;
            var panel = new RectangleF(b.Width - w * k, panelTop, w, b.Height - panelTop);

            // 面板整体热区：标题栏、内边距等空白处不再穿透到遮罩把抽屉关掉
            Hot("drawerPanel", panel, delegate { }, true, true);

            pt.Shadow(panel, 0f, 14f, t.Shadow);
            pt.FillRound(panel, 0, t.Surface);

            var inner = new RectangleF(panel.Left + 22, panel.Top + 20, panel.Width - 44, panel.Height - 40);
            string title = kind == "menu" ? I18n.T("menu.title")
                : kind == "day" ? I18n.T("cal.dayDetail", DayKey.Parse(dayKey).Month, DayKey.Parse(dayKey).Day)
                : "";

            pt.TextLeft(title, pt.F(17f, true), t.Text, new RectangleF(inner.Left, inner.Top, inner.Width - 40, 30));
            DrawIconButton(pt, "drawerClose", new RectangleF(inner.Right - 32, inner.Top, 32, 30), "close", false,
                delegate { BeginDrawerClose(); });

            // 菜单抽屉：顶部三段式切换（成就 / 奖励 / 设置）
            float contentTop = inner.Top + 42;
            if (kind == "menu")
            {
                string[] tabs = { "achievements", "rewards", "settings" };
                string[] tabKeys = { "ach.title", "reward.title", "settings.title" };
                float segGap = 6f;
                float segW = (inner.Width - segGap * 2) / 3f;
                for (int i = 0; i < 3; i++)
                {
                    var seg = new RectangleF(inner.Left + i * (segW + segGap), contentTop, segW, 32);
                    bool active = _menuTab == tabs[i];
                    float hv = HoverAmount("tab" + tabs[i]);
                    pt.FillRound(seg, 10f, active ? t.Accent : Theme.Blend(t.SurfaceAlt, t.AccentSoft, hv));
                    pt.TextCenter(I18n.T(tabKeys[i]), pt.F(12f, true),
                        active ? Color.White : Theme.Blend(t.TextMuted, t.Text, hv), seg);
                    string tab = tabs[i];
                    // 关闭动画期间不接受切换，避免"边收边换页"
                    if (open) Hot("tab" + tabs[i], seg, delegate { _menuTab = tab; _drawerScroll = 0; _dirty = true; });
                    else Hot("tab" + tabs[i], seg, null, true, true);
                }
                contentTop += 44;
            }

            var fullBody = new RectangleF(inner.Left, contentTop, inner.Width, inner.Bottom - contentTop);
            Hot("drawerBg", fullBody, null, true, true);
            // 右侧永久预留 14px 作为滚动条间隔，内容不再被滚动条压住
            var body = new RectangleF(fullBody.Left, fullBody.Top, Math.Max(40f, fullBody.Width - 14f), fullBody.Height);

            // 内容整体裁剪到内容区：既避免溢出绘制，也避免滚上去的内容抢走标题/关闭按钮的点击
            float contentH = 0;
            _hotClip = body;
            _hotClipActive = true;
            pt.Clip(body, delegate
            {
                if (kind == "day") { contentH = DrawDayDetail(pt, body, scroll, dayKey); return; }
                switch (_menuTab)
                {
                    case "achievements": contentH = DrawAchievements(pt, body, scroll); break;
                    case "rewards": contentH = DrawRewards(pt, body, scroll); break;
                    case "settings": contentH = DrawSettings(pt, body, scroll); break;
                }
            });
            _hotClipActive = false;
            // 关闭动画期间内容只是"画面"：整块吞掉点击，避免滑动中误触兑换 / 切换标签
            if (!open) Hot("drawerClosing", fullBody, null, true, true);

            // 滚动条：绘制在预留槽内，命中优先级最高
            if (contentH > fullBody.Height + 1f)
            {
                float maxScroll = contentH - fullBody.Height;
                if (open)
                {
                    _drawerMaxScroll = maxScroll;           // 供滚轮/拖动当场钳制
                    if (_scrollToEnd) { _scrollTarget = maxScroll; _scrollToEnd = false; }   // 新内容出现时平滑滚到底
                    if (_drawerScroll > maxScroll) _drawerScroll = maxScroll;
                    if (_drawerScroll < 0) _drawerScroll = 0;
                    scroll = _drawerScroll;
                }
                else if (scroll > maxScroll) scroll = maxScroll;   // 关闭期间只做视觉钳制，不改真实状态
                if (scroll < 0f) scroll = 0f;

                float thumbH = Math.Max(40f, fullBody.Height * fullBody.Height / contentH);
                float thumbY = fullBody.Top + (fullBody.Height - thumbH) * (scroll / maxScroll);
                var track = new RectangleF(fullBody.Right - 12f, fullBody.Top, 12f, fullBody.Height);
                var thumb = new RectangleF(track.Left + 4f, thumbY, 5f, thumbH);
                bool active = _pressedId == "scrollBar";

                pt.FillRound(new RectangleF(track.Left + 5.5f, track.Top, 3f, track.Height), 1.5f, Theme.Alpha(t.TextMuted, 26));
                pt.FillRound(thumb, 2.5f, Theme.Alpha(t.TextMuted, active ? 200 : 115));

                float bodyH = fullBody.Height, th = thumbH, ms = maxScroll, trackTop = track.Top;
                if (open)
                    Hot("scrollBar", track, delegate
                    {
                        float ratio = (_clickPoint.Y - trackTop - th / 2f) / Math.Max(1f, bodyH - th);
                        _drawerScroll = Math.Max(0f, Math.Min(ms, ratio * ms));
                        _dirty = true;
                    }, true, false, delegate (PointF p)
                    {
                        float ratio = (p.Y - trackTop - th / 2f) / Math.Max(1f, bodyH - th);
                        _drawerScroll = Math.Max(0f, Math.Min(ms, ratio * ms));
                    });
                else Hot("scrollBar", track, null, true, true);
            }
            else if (open) { _drawerScroll = 0; _drawerMaxScroll = 0; _scrollToEnd = false; }
        }

        private float DrawAchievements(Painter pt, RectangleF body, float scroll)
        {
            var t = pt.T;
            float y = body.Top - scroll;
            int unlocked = Achievements.UnlockedCount(_app.Data);

            pt.Text(I18n.T("ach.progress", unlocked, Achievements.All.Count), pt.F(12f), t.TextMuted,
                new RectangleF(body.Left, y, body.Width, 20));
            y += 30;

            foreach (var def in Achievements.All)
            {
                var st = Achievements.State(_app.Data, def.Id);
                float h = 74f;                                   // 行高 66px，描述与进度条之间留出间隔
                var row = new RectangleF(body.Left, y, body.Width, h - 8);
                if (row.Bottom > body.Top && row.Top < body.Bottom)
                {
                    pt.FillRound(row, 12f, st.Unlocked ? Theme.Alpha(t.AccentSoft, 150) : t.SurfaceAlt);

                    var iconRect = new RectangleF(row.Left + 12, row.Top + (row.Height - 28f) / 2f, 28, 28);
                    if (st.Unlocked)
                    {
                        pt.FillCircle(C(iconRect), 15f, t.Accent);
                        IconArt.Draw(pt, def.Icon == "tomato" ? "leaf" : def.Icon, RectangleF.Inflate(iconRect, -8, -8), Color.White, 1.8f);
                    }
                    else
                    {
                        pt.StrokeCircle(C(iconRect), 14f, Theme.Alpha(t.TextMuted, 120), 1.4f);
                        IconArt.Draw(pt, def.Icon == "tomato" ? "leaf" : def.Icon, RectangleF.Inflate(iconRect, -9, -9), Theme.Alpha(t.TextMuted, 110), 1.4f);
                    }

                    pt.Text(I18n.T(def.NameKey), pt.F(13f, true), st.Unlocked ? t.Text : t.TextMuted,
                        new RectangleF(row.Left + 50, row.Top + 8, row.Width - 70, 20));
                    pt.Text(I18n.T(def.DescKey), pt.F(10.5f), t.TextMuted,
                        new RectangleF(row.Left + 50, row.Top + 28, row.Width - 70, 16));

                    // 进度条与描述之间留 8px 间隔
                    var bar = new RectangleF(row.Left + 50, row.Top + 52, row.Width - 120, 5);
                    pt.FillRound(bar, 2.5f, Theme.Alpha(t.Border, 160));
                    float p = def.Target <= 0 ? 0 : Math.Min(1f, st.Progress / (float)def.Target);
                    if (p > 0) pt.FillRound(new RectangleF(bar.Left, bar.Top, bar.Width * p, bar.Height), 2.5f, st.Unlocked ? t.Accent : t.Warn);
                    pt.TextCenter(I18n.T("ach.progress", st.Progress, def.Target), pt.F(9.5f), t.TextMuted,
                        new RectangleF(bar.Right + 6, bar.Top - 7, 54, 19));
                }
                y += h;
            }
            return y - body.Top + scroll + 10;
        }

        private float DrawRewards(Painter pt, RectangleF body, float scroll)
        {
            var t = pt.T;
            float y = body.Top - scroll;

            pt.Text(I18n.T("reward.available", _app.Data.Wallet.UsableWhole), pt.F(12.5f, true), t.Text,
                new RectangleF(body.Left, y, body.Width, 20));
            y += 22;
            pt.Text(I18n.T("reward.wholeHint"), pt.F(10.5f), t.TextMuted,
                new RectangleF(body.Left, y, body.Width, 20));
            y += 30;

            // 分类
            // 分类顺序：称号 · 奖章 · 动效 · 提示音 · 主题（主题放在提示音右侧）
            string[] cats = Rewards.CategoryOrder;
            var font = pt.F(11.5f, true);
            float x = body.Left;
            foreach (string cat in cats)
            {
                string label = I18n.T("reward.cat." + cat);
                float w = pt.TextWidth(label, font) + 22f;
                var r = new RectangleF(x, y, w, 26);
                bool sel = _rewardCat == cat;
                float hv = HoverAmount("rc" + cat);
                pt.FillRound(r, 13f, sel ? t.Accent : Theme.Blend(t.SurfaceAlt, t.AccentSoft, hv));
                pt.TextCenter(label, font, sel ? Color.White : Theme.Blend(t.TextMuted, t.Text, hv), r);
                string c = cat;
                Hot("rc" + cat, r, delegate { _rewardCat = c; _drawerScroll = 0; });
                x += w + 6;
            }
            y += 38;

            foreach (var def in Rewards.ByCategory(_rewardCat))
            {
                var row = new RectangleF(body.Left, y, body.Width, 60);
                if (row.Bottom > body.Top && row.Top < body.Bottom)
                {
                    // 默认主题 / 默认提示音（0 成本）永远视为已拥有：不再显示"兑换 0 颗"
                    bool owned = Rewards.IsOwnedOrDefault(_app.Data, def);
                    bool equipped = Rewards.IsEquipped(_app.Data, def);
                    pt.FillRound(row, 12f, equipped ? Theme.Alpha(t.AccentSoft, 170) : t.SurfaceAlt);

                    var iconRect = new RectangleF(row.Left + 12, row.Top + 14, 28, 28);
                    if (def.Category == "medal")
                    {
                        // 未解锁：整体灰化且完全静止；已拥有：本色 + 动态扫光
                        MedalArt.Draw(pt, iconRect, def.Id, _time, !owned, !owned);
                        RegisterAmbientMedal(iconRect, def.Id, !owned, body);   // 已拥有的登记进动效清单（按可见区裁剪）
                        if (owned) _ambientAnimation = true;
                    }
                    else
                    {
                        pt.FillCircle(C(iconRect), 14f, equipped ? t.Accent : Theme.Alpha(t.TextMuted, 60));
                        // 与顶栏展示栏共用同一套图标口径
                        IconArt.Draw(pt, Rewards.IconFor(def), RectangleF.Inflate(iconRect, -8, -8), Color.White, 1.7f);
                    }

                    pt.Text(I18n.T(def.NameKey), pt.F(13f, true), t.Text,
                        new RectangleF(row.Left + 50, row.Top + 10, row.Width - 160, 20));
                    pt.Text(I18n.T(def.DescKey), pt.F(10.5f), t.TextMuted,
                        new RectangleF(row.Left + 50, row.Top + 31, row.Width - 160, 16));

                    var btn = new RectangleF(row.Right - 100, row.Top + 15, 88, 30);
                    if (equipped)
                    {
                        pt.FillRound(btn, 15f, t.Accent);
                        pt.TextCenter(I18n.T("reward.equipped"), pt.F(11.5f, true), Color.White, btn);
                        // 已装备：保留热区（避免点击穿透关闭抽屉），但不再支持点击取消装备
                        Hot("rw" + def.Id, btn, delegate { });
                    }
                    else if (owned)
                    {
                        pt.FillRound(btn, 15f, Theme.Alpha(t.Accent, 210));
                        pt.TextCenter(I18n.T("reward.equip"), pt.F(11.5f, true), Color.White, btn);
                        string id = def.Id;
                        Hot("rw" + def.Id, btn, delegate
                        {
                            var target = Rewards.ById(id);
                            Rewards.Equip(_app.Data, target);
                            _app.ApplyTheme();
                            PreviewReward(target);      // 装备后立刻试听/试看
                        });
                    }
                    else
                    {
                        bool afford = _app.Data.Wallet.CanAfford(def.Cost);
                        pt.FillRound(btn, 15f, afford ? t.Accent : Theme.Alpha(t.TextMuted, 70));
                        pt.TextCenter(I18n.T("reward.redeem") + " " + I18n.T("reward.cost", def.Cost),
                            pt.F(11f, true), Color.White, btn);
                        string id = def.Id;
                        // 始终可点击：余额不足时给出提示，而不是让点击落到背景上把抽屉关掉
                        Hot("rw" + def.Id, btn, delegate
                        {
                            string err;
                            if (Rewards.Redeem(_app.Data, id, out err))
                            {
                                _app.ApplyTheme();
                                ShowToast(I18n.T("msg.saved"));
                                PreviewReward(Rewards.ById(id));   // 兑换后立刻试听/试看
                            }
                            else ShowToast(err);
                        });
                    }
                }
                y += 68;
            }
            return y - body.Top + scroll + 10;
        }

        private float DrawSettings(Painter pt, RectangleF body, float scroll)
        {
            var t = pt.T;
            var s = _app.Data.Settings;
            float y = body.Top - scroll;

            y = SectionTitle(pt, body, y, I18n.T("settings.general"));
            y = ToggleRow(pt, body, y, "tgMinimal", I18n.T("settings.minimalMode"), I18n.T("settings.minimalMode.hint"), s.MinimalMode,
                delegate (bool v) { _app.SetMinimalMode(v); });
            y = ToggleRow(pt, body, y, "tgTray", I18n.T("settings.closeToTray"), "", s.CloseToTray,
                delegate (bool v) { s.CloseToTray = v; _app.MarkDirty(); });
            y = ToggleRow(pt, body, y, "tgAuto", I18n.T("settings.autoStart"), I18n.T("settings.autoStart.hint"), s.AutoStart,
                delegate (bool v) { _app.SetAutoStart(v); });
            y = ToggleRow(pt, body, y, "tgHide", I18n.T("settings.hideCountdown"), "", s.HideCountdown,
                delegate (bool v) { s.HideCountdown = v; _app.MarkDirty(); });
            y = ToggleRow(pt, body, y, "tgSound", I18n.T("settings.sound"), "", s.Sound,
                delegate (bool v)
                {
                    s.Sound = v;
                    _app.MarkDirty();
                    if (v) Sound.Play(Rewards.SoundIdFor(_app.Data), true);   // 打开时试听一次
                });
            y = ToggleRow(pt, body, y, "tgGreet", I18n.T("settings.greeting"), I18n.T("settings.greeting.hint"), s.GreetingOn,
                delegate (bool v)
                {
                    s.GreetingOn = v;
                    if (!v) _app.DismissGreeting();      // 关掉时顺手收起正在显示的问候
                    _app.MarkDirty();
                });

            y = SectionTitle(pt, body, y, I18n.T("settings.breakSeconds"));
            string[] options = { "5", "10", "15", "20" };
            float x = body.Left;
            foreach (string opt in options)
            {
                int sec = int.Parse(opt) * 60;
                string label = I18n.T("settings.breakMinutes", opt);
                float w = pt.TextWidth(label, pt.F(11.5f, true)) + 22f;
                var r = new RectangleF(x, y, w, 28);
                bool sel = s.BreakSeconds == sec;
                float hv = HoverAmount("bk" + opt);
                pt.FillRound(r, 14f, sel ? t.Accent : Theme.Blend(t.SurfaceAlt, t.AccentSoft, hv));
                pt.TextCenter(label, pt.F(11.5f, true), sel ? Color.White : Theme.Blend(t.TextMuted, t.Text, hv), r);
                int sc = sec;
                Hot("bk" + opt, r, delegate
                {
                    s.BreakSeconds = sc;      // 只作为"下一次休息"的默认值；
                    _app.MarkDirty();          // 不写运行中的 Timer.BreakSeconds，避免改变正在进行的休息倒计时
                });
                x += w + 6;
            }
            y += 42;

            y = SectionTitle(pt, body, y, I18n.T("settings.language"));
            {
                var r = new RectangleF(body.Left, y, body.Width, 40);
                pt.FillRound(r, 12f, t.SurfaceAlt);
                pt.TextLeft(I18n.DisplayName(s.Lang), pt.F(12.5f, true), t.Text, new RectangleF(r.Left + 14, r.Top, r.Width - 120, r.Height));
                pt.TextRight(I18n.T("settings.language.hint"), pt.F(10.5f), t.TextMuted, new RectangleF(r.Left, r.Top, r.Width - 14, r.Height));
                y += 48;
            }

            y = SectionTitle(pt, body, y, I18n.T("settings.health"));
            // 统一为「项目符号 + 折行」，行高按实际折行数计算，任何长度都不会越出右侧
            var srcFont = pt.F(10.5f);
            string[] srcKeys = { "health.source.who", "health.source.cn", "health.source.diet", "health.source.aoa" };
            foreach (string key in srcKeys)
                y = BulletLine(pt, body, y, "·", I18n.T(key), srcFont, t.Text);
            y += 4;
            string[] ruleKeys = { "health.rule.sedentary", "health.rule.weekly", "health.rule.water", "health.rule.eye", "health.rule.sleep" };
            foreach (string key in ruleKeys)
                y = BulletLine(pt, body, y, "—", I18n.T(key), srcFont, t.TextMuted);
            y += 4;

            y = SectionTitle(pt, body, y, I18n.T("settings.dataDir"));
            pt.Text(AppPaths.DataDir, pt.F(10.5f), t.TextMuted, new RectangleF(body.Left, y, body.Width, 18)); y += 26;

            {
                float bx = body.Left;
                var exp = new RectangleF(bx, y, (body.Width - 10) / 2f, 36);
                pt.FillRound(exp, 18f, Theme.Blend(t.SurfaceAlt, t.AccentSoft, HoverAmount("btnExport")));
                pt.TextCenter(I18n.T("settings.export"), pt.F(12f, true), t.Text, exp);
                Hot("btnExport", exp, ExportData);
                var imp = new RectangleF(exp.Right + 10, y, (body.Width - 10) / 2f, 36);
                pt.FillRound(imp, 18f, Theme.Blend(t.SurfaceAlt, t.AccentSoft, HoverAmount("btnImport")));
                pt.TextCenter(I18n.T("settings.import"), pt.F(12f, true), t.Text, imp);
                Hot("btnImport", imp, ImportData);
                y += 46;
            }

            pt.Text(I18n.T("app.title") + " v" + AppInfo.Version, pt.F(11f), t.TextMuted, new RectangleF(body.Left, y, body.Width, 18)); y += 26;

            // 危险操作：清除所有数据（一级确认 → 长按确认）
            y = SectionTitle(pt, body, y, I18n.T("settings.danger"));
            if (_clearStage == 0)
            {
                var clearBtn = new RectangleF(body.Left, y, body.Width, 40);
                float chv = HoverAmount("btnClear");
                pt.FillRound(clearBtn, 12f, Theme.Alpha(t.Warn, (int)(38 + 45 * chv)));
                pt.StrokeRound(clearBtn, 12f, Theme.Alpha(t.Warn, 165), 1f);
                pt.TextCenter(I18n.T("settings.clearData"), pt.F(12.5f, true), t.Warn, clearBtn);
                Hot("btnClear", clearBtn, delegate
                {
                    _clearStage = 1;
                    _holdProgress = 0f;
                    ScrollDrawerToEnd();          // 卡片在下方，自动展开
                    _dirty = true;
                });
                y += 50;
            }
            else
            {
                string msg = I18n.T(_clearStage == 1 ? "settings.clearData.confirm1" : "settings.clearData.confirm2");
                var msgFont = pt.F(11f);
                int lineCount = Math.Max(1, pt.WrapLines(msg, msgFont, body.Width - 32f).Count);
                float cardH = 16f + lineCount * 16f + 54f;
                var card = new RectangleF(body.Left, y, body.Width, cardH);
                pt.FillRound(card, 12f, Theme.Alpha(t.Warn, 34));
                pt.StrokeRound(card, 12f, Theme.Alpha(t.Warn, 170), 1.3f);
                pt.TextWrapped(msg, msgFont, t.Text,
                    new RectangleF(card.Left + 16, card.Top + 12, card.Width - 32, lineCount * 16f + 4f), 16f);

                float btnY = card.Bottom - 44f;
                if (_clearStage == 1)
                {
                    var yes = new RectangleF(card.Left + 16, btnY, 110, 34);
                    pt.FillRound(yes, 17f, t.Warn);
                    pt.TextCenter(I18n.T("settings.clearData.yes1"), pt.F(12f, true), Color.White, yes);
                    Hot("clearYes", yes, delegate
                    {
                        _clearStage = 2;
                        _holdProgress = 0f;
                        ScrollDrawerToEnd();
                        _dirty = true;
                    });
                }
                else
                {
                    // 二级确认：长按按钮，边框顺时针加粗推进，松手回收
                    var hold = new RectangleF(card.Left + 16, btnY, 168, 34);
                    float hp = _holdProgress;
                    pt.FillRound(hold, 17f, Theme.Alpha(t.Warn, (int)(26 + 70 * hp)));
                    pt.StrokeRound(hold, 17f, Theme.Alpha(t.Warn, 120), 1f);
                    using (var holdPath = Painter.RoundedPath(hold, 17f))
                        pt.StrokePathProgress(holdPath, t.Warn, 1.2f + hp * 3.2f, hp);
                    pt.TextCenter(I18n.T("settings.clearData.hold"), pt.F(12f, true), t.Warn, hold);
                    Hot("clearHold", hold, delegate { });     // 只响应长按
                }

                var no = new RectangleF(card.Right - 108, btnY, 92, 34);
                pt.FillRound(no, 17f, t.SurfaceAlt);
                pt.TextCenter(I18n.T("settings.clearData.no"), pt.F(12f, true), t.TextMuted, no);
                Hot("clearNo", no, delegate { _clearStage = 0; _holdProgress = 0f; _dirty = true; });
                y += cardH + 10f;
            }

            return y - body.Top + scroll + 10;
        }

        private float SectionTitle(Painter pt, RectangleF body, float y, string text)
        {
            pt.Text(text, pt.F(12.5f, true), pt.T.AccentDark, new RectangleF(body.Left, y, body.Width, 20));
            return y + 26;
        }

        /// <summary>项目符号 + 折行文本，返回下一行的 y。行高按实际折行数计算。</summary>
        private float BulletLine(Painter pt, RectangleF body, float y, string bullet, string text, Font font, Color color)
        {
            const float lineH = 15f;
            float bulletW = pt.TextWidth(bullet, font) + 6f;
            var lines = pt.WrapLines(text, font, Math.Max(40f, body.Width - bulletW));
            if (lines.Count == 0) lines.Add("");
            pt.Text(bullet, font, color, new RectangleF(body.Left, y, bulletW, lineH));
            float ty = y;
            foreach (var line in lines)
            {
                pt.Text(line, font, color, new RectangleF(body.Left + bulletW, ty, body.Width - bulletW, lineH));
                ty += lineH;
            }
            return ty + 3f;
        }

        private float ToggleRow(Painter pt, RectangleF body, float y, string id, string label, string hint, bool value, Action<bool> onChange)
        {
            var t = pt.T;
            float h = string.IsNullOrEmpty(hint) ? 42f : 58f;
            var row = new RectangleF(body.Left, y, body.Width, h - 6);
            pt.FillRound(row, 12f, t.SurfaceAlt);
            pt.TextLeft(label, pt.F(12.5f, true), t.Text, new RectangleF(row.Left + 14, row.Top, row.Width - 100, string.IsNullOrEmpty(hint) ? row.Height : 26));
            if (!string.IsNullOrEmpty(hint))
                pt.TextLeft(hint, pt.F(10.5f), t.TextMuted, new RectangleF(row.Left + 14, row.Top + 24, row.Width - 100, 18));

            float sw = 44f, sh = 24f;
            var track = new RectangleF(row.Right - sw - 14, row.Top + (row.Height - sh) / 2f, sw, sh);
            var knob = A("sw" + id, value ? 1f : 0f, 16f);
            knob.Set(value ? 1f : 0f);
            float k = knob.Value;
            pt.FillRound(track, sh / 2f, Theme.Blend(Theme.Alpha(t.TextMuted, 90), t.Accent, k));
            float knobX = track.Left + sh / 2f + (track.Width - sh) * k;
            pt.FillCircle(new PointF(knobX, track.Top + sh / 2f), sh / 2f - 3f, Color.White);
            Hot(id, row, delegate { onChange(!value); });
            return y + h;
        }

        private float DrawDayDetail(Painter pt, RectangleF body, float scroll, string dayKey)
        {
            var t = pt.T;
            float y = body.Top - scroll;
            var list = Stats.SessionsOf(_app.Data, dayKey);
            var day = Stats.DayOf(_app.Data, dayKey);

            pt.Text(I18n.T("today.tomatoes", TomatoMath.Format(day == null ? 0 : day.Tenths)) + " · " +
                    I18n.T("cal.sessions", list.Count), pt.F(12.5f, true), t.Text, new RectangleF(body.Left, y, body.Width, 20));
            y += 30;

            if (list.Count == 0)
            {
                pt.Text(I18n.T("cal.noRecord"), pt.F(12f), t.TextMuted, new RectangleF(body.Left, y, body.Width, 20));
                return y - body.Top + scroll + 20;
            }

            foreach (var rec in list)
            {
                bool hasNote = !string.IsNullOrEmpty(rec.Note);
                float h = hasNote ? 86f : 62f;
                var row = new RectangleF(body.Left, y, body.Width, h);
                if (row.Bottom > body.Top && row.Top < body.Bottom)
                {
                    pt.FillRound(row, 12f, rec.Aborted ? Theme.Alpha(t.Warn, 40) : t.SurfaceAlt);
                    var dt = rec.StartedLocal;
                    pt.Text(dt.ToString("HH:mm"), pt.F(13f, true, Painter.MonoFamily), t.Text,
                        new RectangleF(row.Left + 14, row.Top + 10, 60, 20));
                    pt.Text(I18n.T("cal.preset", rec.PlannedSec / 60), pt.F(11f), t.TextMuted,
                        new RectangleF(row.Left + 14, row.Top + 32, 120, 18));

                    pt.Text(I18n.T("cal.focused", TomatoMath.FormatClock(rec.FocusedSec)), pt.F(11f), t.TextMuted,
                        new RectangleF(row.Left + 100, row.Top + 32, 140, 18));

                    // 番茄图形
                    int inTomato = rec.Tenths >= 10 ? 10 : rec.Tenths;
                    TomatoArt.DrawTenths(pt, new RectangleF(row.Right - 110, row.Top + 12, 34, 34), inTomato, rec.Aborted);
                    pt.TextRight(TomatoMath.Format(rec.Tenths), pt.F(12f, true), rec.Aborted ? t.Warn : t.AccentDark,
                        new RectangleF(row.Right - 70, row.Top, 60, h - 22));
                    if (rec.Aborted)
                        pt.TextRight(I18n.T("cal.aborted"), pt.F(10f), t.Warn,
                            new RectangleF(row.Right - 70, row.Top + 34, 60, 16));

                    if (hasNote)
                        pt.TextWrapped(rec.Note, pt.F(10f), t.TextMuted,
                            new RectangleF(row.Left + 14, row.Bottom - 30, row.Width - 130, 26), 13f);
                }
                y += h + 8f;
            }
            return y - body.Top + scroll + 10;
        }

        // --- 关怀提醒卡片 --------------------------------------------------
        private void DrawReminderCard(Painter pt)
        {
            var r = _app.Pending;
            float k = A("reminder", 0f, 12f).Value;
            A("reminder").Set(r != null ? 1f : 0f);
            k = A("reminder").Value;
            if (k < 0.01f || r == null) return;

            var t = pt.T;
            var b = Bounds;
            float w = Math.Min(560f, b.Width - 80f);
            float h = 176f;
            var card = new RectangleF(b.Width / 2f - w / 2f, b.Height - h - 28 - (1 - k) * 24, w, h);

            pt.Shadow(card, 20f, 12f, t.Shadow);
            pt.FillRound(card, 20f, t.Surface);
            pt.StrokeRound(card, 20f, Theme.Alpha(t.Accent, 70), 1.2f);

            TomatoArt.Icon(pt, new RectangleF(card.Left + 22, card.Top + 24, 34, 34));
            pt.Text(I18n.T(r.TitleKey), pt.F(15.5f, true), t.Text, new RectangleF(card.Left + 68, card.Top + 20, card.Width - 90, 24));
            pt.Text(r.Phrase, pt.F(14f), t.AccentDark, new RectangleF(card.Left + 68, card.Top + 48, card.Width - 90, 24));
            if (!string.IsNullOrEmpty(r.ExtraKey))
                pt.Text(I18n.T(r.ExtraKey), pt.F(11f), t.TextMuted, new RectangleF(card.Left + 68, card.Top + 72, card.Width - 90, 34));

            float by = card.Bottom - 52;
            var take = new RectangleF(card.Left + 22, by, 190, 38);
            pt.FillRound(take, 19f, t.Accent);
            pt.TextCenter(I18n.T("break.take", TomatoMath.FormatClock(r.BreakSeconds)), pt.F(12.5f, true), Color.White, take);
            Hot("remTake", take, delegate
            {
                _app.Timer.StartBreak(r.BreakSeconds);
                _app.OnBreakTaken(r.BreakSeconds);
                _app.DismissReminder();
            });

            var skip = new RectangleF(take.Right + 10, by, 88, 38);
            pt.FillRound(skip, 19f, t.SurfaceAlt);
            pt.TextCenter(I18n.T("break.skip"), pt.F(12f, true), t.TextMuted, skip);
            Hot("remSkip", skip, delegate { _app.DismissReminder(); });

            // 第三颗按钮：把上一次结算保存的笔记恢复到草稿，便于在原内容上继续续写
            bool hasNote = !string.IsNullOrEmpty(_app.LastRecordedNote);
            var keep = new RectangleF(skip.Right + 10, by, 106, 38);
            pt.FillRound(keep, 19f, Theme.Alpha(t.SurfaceAlt, hasNote ? 255 : 140));
            pt.TextCenter(I18n.T("break.keepNote"), pt.F(12f, true),
                Theme.Alpha(t.TextMuted, hasNote ? 255 : 130), keep);
            Hot("remKeep", keep, delegate
            {
                ShowToast(I18n.T(_app.KeepLastNote() ? "break.keepNote.done" : "break.keepNote.none"));
                _app.DismissReminder();
            });
        }

        // --- 分时段温馨问候卡 ----------------------------------------------
        private void DrawGreetingCard(Painter pt)
        {
            var g = _app.PendingGreeting;
            A("greeting", 0f, 12f).Set(g != null ? 1f : 0f);
            float k = A("greeting").Value;
            if (k < 0.01f || g == null) return;

            var t = pt.T;
            var b = Bounds;
            float w = Math.Min(520f, b.Width - 80f);
            float h = 132f;
            var card = new RectangleF(b.Width / 2f - w / 2f, b.Height - h - 28 - (1 - k) * 24, w, h);

            pt.Shadow(card, 20f, 12f, t.Shadow);
            pt.FillRound(card, 20f, t.Surface);
            pt.StrokeRound(card, 20f, Theme.Alpha(t.Accent, 70), 1.2f);

            TomatoArt.Icon(pt, new RectangleF(card.Left + 22, card.Top + 22, 30, 30));
            pt.Text(I18n.T(g.TitleKey), pt.F(15f, true), t.Text,
                new RectangleF(card.Left + 62, card.Top + 18, card.Width - 84, 24));
            pt.Text(I18n.T(g.PhraseKey), pt.F(13.5f), t.AccentDark,
                new RectangleF(card.Left + 62, card.Top + 46, card.Width - 84, 24));

            float by = card.Bottom - 48;
            var ok = new RectangleF(card.Right - 22 - 96, by, 96, 34);
            pt.FillRound(ok, 17f, t.SurfaceAlt);
            pt.TextCenter(I18n.T("greet.action.ok"), pt.F(12f, true), t.TextMuted, ok);
            Hot("greetOk", ok, delegate { _app.DismissGreeting(); _dirty = true; });

            if (!string.IsNullOrEmpty(g.PrimaryKey))
            {
                var go = new RectangleF(ok.Left - 10 - 130, by, 130, 34);
                pt.FillRound(go, 17f, t.Accent);
                pt.TextCenter(I18n.T(g.PrimaryKey), pt.F(12f, true), Color.White, go);
                Hot("greetGo", go, delegate
                {
                    _app.DismissGreeting();
                    ToggleStartPause();       // 直接开始本轮专注
                    _dirty = true;
                });
            }
        }

        // --- 中断确认（行内，不弹窗） --------------------------------------
        private void DrawConfirmBar(Painter pt)
        {
            float k = A("confirm").Value;
            A("confirm").Set(_confirmStop ? 1f : 0f);
            k = A("confirm").Value;
            if (k < 0.01f) return;

            var t = pt.T;
            var lr = _leftRect;
            float h = 66f;
            var bar = new RectangleF(lr.Left, lr.Bottom - h - 6 - (1 - k) * 12, lr.Width, h);
            _confirmBarRect = bar;
            pt.Shadow(bar, 16f, 8f, t.Shadow);
            pt.FillRound(bar, 16f, t.Surface);
            pt.StrokeRound(bar, 16f, Theme.Alpha(t.Warn, 160), 1.4f);

            int sec = _app.Timer.FocusedSeconds;
            int tenths = TomatoMath.TenthsForAborted(sec);
            string msg = tenths <= 0
                ? I18n.T("focus.stop.none", TomatoMath.FormatClock(sec))
                : I18n.T("focus.stop.confirm") + "　" + I18n.T("focus.stop.estimate", TomatoMath.FormatClock(sec), TomatoMath.Format(tenths));

            pt.Text(I18n.T("focus.stop.confirm"), pt.F(12.5f, true), t.Warn, new RectangleF(bar.Left + 16, bar.Top + 10, bar.Width - 220, 20));
            pt.Text(tenths <= 0 ? I18n.T("focus.stop.none", TomatoMath.FormatClock(sec))
                                : I18n.T("focus.stop.estimate", TomatoMath.FormatClock(sec), TomatoMath.Format(tenths)),
                pt.F(11f), t.TextMuted, new RectangleF(bar.Left + 16, bar.Top + 32, bar.Width - 220, 20));

            var yes = new RectangleF(bar.Right - 186, bar.Top + 14, 90, 38);
            pt.FillRound(yes, 19f, t.Warn);
            pt.TextCenter(I18n.T("focus.stop.yes"), pt.F(12f, true), Color.White, yes);
            Hot("cfYes", yes, delegate
            {
                _confirmStop = false;
                A("confirm").Set(0f);
                _app.Timer.StopEarly();
            });

            var no = new RectangleF(bar.Right - 90, bar.Top + 14, 74, 38);
            pt.FillRound(no, 19f, t.SurfaceAlt);
            pt.TextCenter(I18n.T("focus.stop.no"), pt.F(12f, true), t.TextMuted, no);
            Hot("cfNo", no, delegate { _confirmStop = false; A("confirm").Set(0f); });
        }

        // --- 提示与粒子 ----------------------------------------------------
        private void DrawToast(Painter pt)
        {
            if (string.IsNullOrEmpty(_toast) || _toastT <= 0) return;
            var t = pt.T;
            var b = Bounds;
            var font = pt.F(12.5f, true);
            float w = pt.TextWidth(_toast, font) + 48f;
            // 顶部居中，避免与底部提醒卡片/确认条重叠
            var r = new RectangleF(b.Width / 2f - w / 2f, 74f, w, 40f);
            float a = (float)Math.Min(1.0, _toastT / 0.6);
            pt.Shadow(r, 20f, 8f, Theme.Alpha(Color.Black, (int)(40 * a)));
            pt.FillRound(r, 20f, Theme.Alpha(t.Text, (int)(230 * a)));
            pt.TextCenter(_toast, font, Theme.Alpha(Color.White, (int)(255 * a)), r);
        }

        private void DrawParticles(Painter pt)
        {
            if (!_particles.Active) return;
            _particles.Draw(pt);
        }

        // --- 数据导出 / 导入 ------------------------------------------------
        private void ExportData()
        {
            try
            {
                using (var dlg = new SaveFileDialog())
                {
                    dlg.Filter = "JSON|*.json";
                    dlg.FileName = "tomato-focus-" + DayKey.Today + ".json";
                    if (dlg.ShowDialog(Owner) != DialogResult.OK) return;
                    File.WriteAllText(dlg.FileName, Store.ToJson(_app.Data), new UTF8Encoding(false));
                    ShowToast(I18n.T("msg.exported", Path.GetFileName(dlg.FileName)));
                }
            }
            catch (Exception ex) { ShowToast(ex.Message); }
        }

        private void ImportData()
        {
            try
            {
                using (var dlg = new OpenFileDialog())
                {
                    dlg.Filter = "JSON|*.json";
                    if (dlg.ShowDialog(Owner) != DialogResult.OK) return;
                    var imported = Store.FromJson(JsonObj.Parse(File.ReadAllText(dlg.FileName, Encoding.UTF8)));
                    Store.Normalize(imported);

                    var existing = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var rec in _app.Data.Sessions) existing.Add(rec.Id);
                    int added = 0;
                    foreach (var rec in imported.Sessions)
                    {
                        if (string.IsNullOrEmpty(rec.Id)) rec.Id = Guid.NewGuid().ToString("N");
                        if (existing.Contains(rec.Id)) continue;
                        _app.Data.Sessions.Add(rec);
                        existing.Add(rec.Id);
                        added++;
                    }
                    foreach (var kv in imported.Counters)
                        if (_app.Data.Counter(kv.Key) < kv.Value) _app.Data.Counters[kv.Key] = kv.Value;
                    foreach (string id in imported.Rewards.Owned)
                        if (!_app.Data.Rewards.IsOwned(id)) _app.Data.Rewards.Owned.Add(id);

                    Store.RebuildDays(_app.Data);
                    Achievements.Evaluate(_app.Data, DateTime.Now);
                    _app.Save();
                    _app.MarkDirty();
                    ShowToast(I18n.T("msg.imported", added));
                }
            }
            catch (Exception ex) { ShowToast(ex.Message); }
        }
    }
}
