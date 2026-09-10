using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using TomatoFocus.Core;

namespace TomatoFocus.Render
{
    /// <summary>
    /// 绘图原语封装。所有坐标使用“设备无关像素(DIP)”，由本类统一做 DPI 缩放。
    /// 字体对象进程内缓存，避免每帧创建。
    /// </summary>
    internal sealed class Painter
    {
        private static readonly Dictionary<string, Font> FontCache = new Dictionary<string, Font>(StringComparer.Ordinal);
        private static readonly object FontLock = new object();
        private static string _uiFamily;
        private static string _monoFamily;

        private readonly Graphics _g;
        private readonly float _scale;
        public Theme T;

        public Painter(Graphics g, float scale, Theme theme)
        {
            _g = g;
            _scale = scale <= 0 ? 1f : scale;
            T = theme;
            _g.SmoothingMode = SmoothingMode.AntiAlias;
            _g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            _g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            _g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            _g.ScaleTransform(_scale, _scale);
        }

        public Graphics Raw { get { return _g; } }
        public float Scale { get { return _scale; } }

        // --- 字体 ---------------------------------------------------------
        public static string UiFamily
        {
            get
            {
                if (_uiFamily == null) _uiFamily = PickFamily(new[] { "Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI", "SimSun" });
                return _uiFamily;
            }
        }

        public static string MonoFamily
        {
            get
            {
                if (_monoFamily == null) _monoFamily = PickFamily(new[] { "Cascadia Mono", "Consolas", "Courier New" });
                return _monoFamily;
            }
        }

        private static string PickFamily(string[] candidates)
        {
            var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (var ifc = new InstalledFontCollection())
                    foreach (var f in ifc.Families) installed.Add(f.Name);
            }
            catch { /* 字体枚举失败时直接回退 */ }
            foreach (var c in candidates) if (installed.Contains(c)) return c;
            return FontFamily.GenericSansSerif.Name;
        }

        public Font F(float sizeDip, bool bold = false, string family = null)
        {
            float px = sizeDip;
            if (px < 4) px = 4;
            string fam = family ?? UiFamily;
            string key = fam + "|" + px.ToString("0.##") + "|" + (bold ? "b" : "n");
            lock (FontLock)
            {
                Font f;
                if (!FontCache.TryGetValue(key, out f))
                {
                    // 安全网：字号一旦出现无界变化，缓存会无限增长
                    if (FontCache.Count > 200)
                    {
                        FontCache.Clear();
                        Log.Info("Painter: 字体缓存超过 200 项已清空（说明有代码在按帧创建新字号）");
                    }
                    f = new Font(fam, px, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
                    FontCache[key] = f;
                }
                return f;
            }
        }

        public SizeF Measure(string s, Font f)
        {
            if (string.IsNullOrEmpty(s)) return SizeF.Empty;
            return _g.MeasureString(s, f, int.MaxValue, StringFormat.GenericTypographic);
        }

        public float TextWidth(string s, Font f) { return Measure(s, f).Width; }

        // --- 文本度量（按“墨迹”对齐，解决中英文/数字在按钮里偏上偏下的问题）-----
        private sealed class TextMetrics
        {
            public GraphicsPath Path;
            public RectangleF Ink;
            public float Ascent;
        }

        private static readonly Dictionary<string, TextMetrics> MetricsCache = new Dictionary<string, TextMetrics>(StringComparer.Ordinal);
        private static readonly object MetricsLock = new object();

        private static TextMetrics Metrics(string s, Font f)
        {
            string key = f.Name + "\u0001" + f.Size.ToString("0.##") + "\u0001" + (int)f.Style + "\u0001" + s;
            lock (MetricsLock)
            {
                TextMetrics m;
                if (MetricsCache.TryGetValue(key, out m)) return m;

                if (MetricsCache.Count > 4000)
                {
                    foreach (var v in MetricsCache.Values) { try { v.Path.Dispose(); } catch { } }
                    MetricsCache.Clear();
                }

                m = new TextMetrics();
                m.Path = new GraphicsPath();
                try { m.Path.AddString(s, f.FontFamily, (int)f.Style, f.Size, PointF.Empty, StringFormat.GenericTypographic); }
                catch (Exception) { }
                try { m.Ink = m.Path.PointCount > 0 ? m.Path.GetBounds() : RectangleF.Empty; }
                catch (Exception) { m.Ink = RectangleF.Empty; }
                try
                {
                    var ff = f.FontFamily;
                    int em = ff.GetEmHeight(f.Style);
                    m.Ascent = em > 0 ? ff.GetCellAscent(f.Style) * f.Size / em : f.Size * 0.86f;
                }
                catch (Exception) { m.Ascent = f.Size * 0.86f; }

                MetricsCache[key] = m;
                return m;
            }
        }

        /// <summary>墨迹包围盒（相对基线原点，向上为负）。</summary>
        public SizeF InkSize(string s, Font f)
        {
            if (string.IsNullOrEmpty(s)) return SizeF.Empty;
            var m = Metrics(s, f);
            return new SizeF(m.Ink.Width, m.Ink.Height);
        }

        // --- 文本 ---------------------------------------------------------
        public void Text(string s, Font f, Color c, float x, float y)
        {
            if (string.IsNullOrEmpty(s)) return;
            _g.DrawString(s, f, BrushOf(c), x, y, StringFormat.GenericTypographic);
        }

        /// <summary>以矩形左上角为起点绘制（不做对齐）。</summary>
        public void Text(string s, Font f, Color c, RectangleF r)
        {
            Text(s, f, c, r.Left, r.Top);
        }

        private PointF AlignedOrigin(string s, Font f, RectangleF r, float alignX, float alignY)
        {
            var m = Metrics(s, f);
            float inkW = m.Ink.Width, inkH = m.Ink.Height;
            // GDI+ DrawString 的原点是基线（不是行框顶部），因此只需减去墨迹相对基线的偏移
            float x = r.Left + (r.Width - inkW) * alignX - m.Ink.Left;
            float y = r.Top + (r.Height - inkH) * alignY - m.Ink.Top;
            return new PointF(x, y);
        }

        /// <summary>左对齐并按墨迹垂直居中。</summary>
        public void TextLeft(string s, Font f, Color c, RectangleF r)
        {
            if (string.IsNullOrEmpty(s)) return;
            var p = AlignedOrigin(s, f, r, 0f, 0.5f);
            Text(s, f, c, p.X, p.Y);
        }

        /// <summary>右对齐并按墨迹垂直居中。</summary>
        public void TextRight(string s, Font f, Color c, RectangleF r)
        {
            if (string.IsNullOrEmpty(s)) return;
            var p = AlignedOrigin(s, f, r, 1f, 0.5f);
            Text(s, f, c, p.X, p.Y);
        }

        /// <summary>水平垂直均按墨迹居中。</summary>
        public void TextCenter(string s, Font f, Color c, RectangleF r)
        {
            if (string.IsNullOrEmpty(s)) return;
            var p = AlignedOrigin(s, f, r, 0.5f, 0.5f);
            Text(s, f, c, p.X, p.Y);
        }

        public void TextCenterBaseline(string s, Font f, Color c, RectangleF r, float dy = 0f)
        {
            if (string.IsNullOrEmpty(s)) return;
            var p = AlignedOrigin(s, f, r, 0.5f, 0.5f);
            Text(s, f, c, p.X, p.Y + dy);
        }

        /// <summary>垂直居中、左对齐起点（用于需要手动控制横坐标的场合）。</summary>
        public void TextCenterV(string s, Font f, Color c, float x, RectangleF r)
        {
            if (string.IsNullOrEmpty(s)) return;
            var m = Metrics(s, f);
            Text(s, f, c, x - m.Ink.Left, r.Top + (r.Height - m.Ink.Height) * 0.5f - m.Ink.Top);
        }

        // --- 形状 ---------------------------------------------------------
        public static GraphicsPath RoundedPath(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            if (r.Width <= 0 || r.Height <= 0) return p;
            float rad = Math.Min(radius, Math.Min(r.Width, r.Height) / 2f);
            if (rad <= 0.5f) { p.AddRectangle(r); return p; }
            float d = rad * 2f;
            p.AddArc(r.Left, r.Top, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // --- GDI+ 对象缓存 ---------------------------------------------------
        // 每帧会画上百个圆角矩形/圆/线，若每次都 new Brush/Pen/Path，会产生大量
        // Gen0 分配并触发 GC 抖动（表现为"异常卡顿"）。这里按颜色/尺寸复用只读对象。
        private static readonly Dictionary<int, SolidBrush> BrushCache = new Dictionary<int, SolidBrush>();
        private static readonly Dictionary<long, Pen> PenCache = new Dictionary<long, Pen>();
        private static readonly Dictionary<string, GraphicsPath> RoundCache = new Dictionary<string, GraphicsPath>(StringComparer.Ordinal);
        private static readonly object GdiLock = new object();

        private static SolidBrush BrushOf(Color c)
        {
            int key = c.ToArgb();
            lock (GdiLock)
            {
                SolidBrush b;
                if (BrushCache.TryGetValue(key, out b)) return b;
                if (BrushCache.Count > 512) BrushCache.Clear();   // 交给 GC 终结，避免释放正在使用的对象
                b = new SolidBrush(c);
                BrushCache[key] = b;
                return b;
            }
        }

        private static Pen PenOf(Color c, float width, bool roundCap)
        {
            int wq = (int)Math.Round(width * 4f);
            if (wq < 1) wq = 1;
            long key = ((long)wq << 32) | (uint)c.ToArgb() | (roundCap ? (1L << 40) : 0L);
            lock (GdiLock)
            {
                Pen p;
                if (PenCache.TryGetValue(key, out p)) return p;
                if (PenCache.Count > 512) PenCache.Clear();
                p = new Pen(c, wq / 4f);
                if (roundCap) { p.StartCap = LineCap.Round; p.EndCap = LineCap.Round; }
                PenCache[key] = p;
                return p;
            }
        }

        private static GraphicsPath RoundPathOf(RectangleF r, float radius)
        {
            string key = ((int)(r.X * 2f)) + "," + ((int)(r.Y * 2f)) + "," + ((int)(r.Width * 2f)) + "," +
                         ((int)(r.Height * 2f)) + "," + ((int)(radius * 2f));
            lock (GdiLock)
            {
                GraphicsPath p;
                if (RoundCache.TryGetValue(key, out p)) return p;
                if (RoundCache.Count > 1024) RoundCache.Clear();
                p = RoundedPath(r, radius);
                RoundCache[key] = p;
                return p;
            }
        }

        public void FillRound(RectangleF r, float radius, Color c)
        {
            if (c.A == 0 || r.Width <= 0 || r.Height <= 0) return;
            _g.FillPath(BrushOf(c), RoundPathOf(r, radius));
        }

        public void StrokeRound(RectangleF r, float radius, Color c, float width)
        {
            if (c.A == 0 || width <= 0 || r.Width <= 0 || r.Height <= 0) return;
            var rr = new RectangleF(r.X + width / 2f, r.Y + width / 2f, r.Width - width, r.Height - width);
            if (rr.Width <= 0 || rr.Height <= 0) return;
            _g.DrawPath(PenOf(c, width, false), RoundPathOf(rr, radius - width / 2f));
        }

        public void FillCircle(PointF center, float radius, Color c)
        {
            if (c.A == 0 || radius <= 0) return;
            _g.FillEllipse(BrushOf(c), center.X - radius, center.Y - radius, radius * 2, radius * 2);
        }

        public void StrokeCircle(PointF center, float radius, Color c, float width)
        {
            if (c.A == 0 || radius <= 0) return;
            _g.DrawEllipse(PenOf(c, width, false), center.X - radius, center.Y - radius, radius * 2, radius * 2);
        }

        public void Line(PointF a, PointF b, Color c, float width, bool round = true)
        {
            _g.DrawLine(PenOf(c, width, round), a, b);
        }

        public void Arc(PointF center, float radius, float startDeg, float sweepDeg, Color c, float width)
        {
            if (Math.Abs(sweepDeg) < 0.05f || c.A == 0) return;
            _g.DrawArc(PenOf(c, width, true), center.X - radius, center.Y - radius, radius * 2, radius * 2, startDeg, sweepDeg);
        }

        /// <summary>用多层圆角矩形模拟柔和投影（无需位图）。</summary>
        public void Shadow(RectangleF r, float radius, float spread, Color color)
        {
            int layers = 8;
            for (int i = layers; i >= 1; i--)
            {
                float t = i / (float)layers;
                float grow = spread * t;
                int alpha = (int)(color.A * (1f - t) * 0.55f);
                if (alpha <= 0) continue;
                var rr = RectangleF.Inflate(r, grow, grow);
                rr.Offset(0, spread * 0.35f);
                FillRound(rr, radius + grow, Color.FromArgb(alpha, color.R, color.G, color.B));
            }
        }

        public void VerticalGradient(RectangleF r, Color top, Color bottom, float radius)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            using (var b = new LinearGradientBrush(r, top, bottom, 90f))
                _g.FillPath(b, RoundPathOf(r, radius));
        }

        public void HorizontalGradient(RectangleF r, Color left, Color right, float radius)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            using (var b = new LinearGradientBrush(r, left, right, 0f))
                _g.FillPath(b, RoundPathOf(r, radius));
        }

        /// <summary>带路径渐变的球体（番茄本体用）。</summary>
        public void Sphere(GraphicsPath path, PointF light, Color core, Color edge)
        {
            using (var b = new PathGradientBrush(path))
            {
                b.CenterColor = core;
                b.SurroundColors = new[] { edge };
                b.CenterPoint = light;
                b.FocusScales = new PointF(0.22f, 0.22f);
                _g.FillPath(b, path);
            }
        }

        public void Glow(GraphicsPath path, PointF center, Color core, Color edge)
        {
            using (var b = new PathGradientBrush(path))
            {
                b.CenterColor = core;
                b.SurroundColors = new[] { edge };
                b.CenterPoint = center;
                _g.FillPath(b, path);
            }
        }

        public void Clip(RectangleF r, Action body)
        {
            var state = _g.Save();
            _g.SetClip(r, CombineMode.Intersect);
            try { body(); }
            finally { _g.Restore(state); }
        }

        /// <summary>填充任意路径（用缓存的画刷）。</summary>
        public void FillPath(GraphicsPath path, Color c)
        {
            if (path == null || c.A == 0) return;
            _g.FillPath(BrushOf(c), path);
        }

        /// <summary>描边任意路径（用缓存的画笔）。</summary>
        public void StrokePath(GraphicsPath path, Color c, float width)
        {
            if (path == null || c.A == 0 || width <= 0) return;
            _g.DrawPath(PenOf(c, width, false), path);
        }

        /// <summary>按宽度贪心折行（中英混排按字符断行）。</summary>
        public List<string> WrapLines(string s, Font f, float maxWidth)
        {
            var lines = new List<string>();
            if (string.IsNullOrEmpty(s) || maxWidth <= 4f) return lines;
            var line = new System.Text.StringBuilder();
            foreach (char ch in s)
            {
                if (ch == '\n') { lines.Add(line.ToString()); line.Length = 0; continue; }
                line.Append(ch);
                if (line.Length > 1 &&
                    _g.MeasureString(line.ToString(), f, int.MaxValue, StringFormat.GenericTypographic).Width > maxWidth)
                {
                    line.Length -= 1;
                    lines.Add(line.ToString());
                    line.Length = 0;
                    line.Append(ch);
                }
            }
            if (line.Length > 0) lines.Add(line.ToString());
            return lines;
        }

        /// <summary>折行绘制；返回实际绘制的行数。</summary>
        public int TextWrapped(string s, Font f, Color c, RectangleF r, float lineHeight)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            var lines = WrapLines(s, f, r.Width);
            int drawn = 0;
            float y = r.Top;
            foreach (var line in lines)
            {
                if (y + lineHeight > r.Bottom + 1f) break;
                Text(line, f, c, r.Left, y);
                y += lineHeight;
                drawn++;
            }
            return drawn;
        }

        /// <summary>按比例绘制路径的"顺时针进度"（用于长按进度边框）。</summary>
        public void StrokePathProgress(GraphicsPath path, Color c, float width, float progress)
        {
            if (path == null || c.A == 0 || progress <= 0.002f) return;
            using (var flat = (GraphicsPath)path.Clone())
            {
                flat.Flatten(new Matrix(), 0.5f);
                var pts = flat.PathPoints;
                if (pts == null || pts.Length < 2) return;

                var lens = new float[pts.Length - 1];
                float total = 0f;
                for (int i = 1; i < pts.Length; i++)
                {
                    float dx = pts[i].X - pts[i - 1].X, dy = pts[i].Y - pts[i - 1].Y;
                    lens[i - 1] = (float)Math.Sqrt(dx * dx + dy * dy);
                    total += lens[i - 1];
                }
                if (total <= 0.01f) return;

                float need = total * (progress > 1f ? 1f : progress);
                var pen = PenOf(c, width, true);
                float acc = 0f;
                for (int i = 1; i < pts.Length && acc < need; i++)
                {
                    float seg = lens[i - 1];
                    if (seg <= 0.001f) continue;
                    float take = Math.Min(seg, need - acc);
                    float t = take / seg;
                    var a = pts[i - 1];
                    var b = new PointF(a.X + (pts[i].X - a.X) * t, a.Y + (pts[i].Y - a.Y) * t);
                    _g.DrawLine(pen, a, b);
                    acc += seg;
                }
            }
        }

        public void ClipPath(GraphicsPath p, Action body)
        {
            var state = _g.Save();
            _g.SetClip(p, CombineMode.Intersect);
            try { body(); }
            finally { _g.Restore(state); }
        }
    }
}
