using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace TomatoFocus.Render.Art
{
    /// <summary>
    /// 程序化勋章：金属双层渐变 + 内环 + 中心星芒 + 扫过的动态高光。
    /// 彩色勋章（m_rainbow）在若干套配色方案之间循环，方案切换时做短插值过渡。
    /// 未解锁时整体灰化且完全静止。全部为即时绘制，不依赖任何图片资源。
    /// </summary>
    internal static class MedalArt
    {
        /// <summary>高光带扫过一个来回的周期（秒）。</summary>
        private const double SweepSeconds = 2.4;

        /// <summary>每套配色停留时长（秒）与切换过渡时长（秒）。</summary>
        private const double SchemeHoldSeconds = 1.6;
        private const double SchemeFadeSeconds = 0.4;

        /// <summary>彩色勋章的配色方案（循环使用）。</summary>
        private static readonly Color[] RainbowSchemes =
        {
            Color.FromArgb(0xE8, 0x4B, 0x6A),   // 绯红
            Color.FromArgb(0x2F, 0xB0, 0xA0),   // 青碧
            Color.FromArgb(0x8B, 0x5C, 0xE0),   // 紫罗兰
            Color.FromArgb(0xF0, 0x9C, 0x2A),   // 橙金
            Color.FromArgb(0x3A, 0x8B, 0xE0),   // 湖蓝
        };

        /// <summary>勋章本色（不随时间变化）。</summary>
        public static Color BaseColor(string id, Theme t)
        {
            switch (id)
            {
                case "m_bronze": return Color.FromArgb(196, 124, 78);
                case "m_silver": return Color.FromArgb(168, 176, 184);
                case "m_gold": return Color.FromArgb(224, 176, 60);
                case "m_rainbow": return RainbowSchemes[0];
                default: return t.Accent;
            }
        }

        /// <summary>未解锁时的灰度色（保留一点原有明度层次）。</summary>
        public static Color LockedColor(string id, Theme t)
        {
            Color c = BaseColor(id, t);
            int lum = (int)Math.Round(0.299 * c.R + 0.587 * c.G + 0.114 * c.B);
            lum = 128 + (lum - 128) / 3;                 // 向中灰压缩，避免死板
            return Color.FromArgb(lum, lum, lum);
        }

        /// <summary>
        /// 勋章当前的显示色。彩色勋章在配色方案间循环：
        /// 每套停留 1.6 秒，用 0.4 秒插值过渡到下一套——不再是逐帧 HSV 连续旋转。
        /// </summary>
        public static Color ShownColor(string id, Theme t, double time)
        {
            if (id != "m_rainbow") return BaseColor(id, t);
            return SchemeColorAt(time);
        }

        /// <summary>按时间取彩色勋章的配色（供测试与绘制共用）。</summary>
        public static Color SchemeColorAt(double time)
        {
            int n = RainbowSchemes.Length;
            double cycle = SchemeHoldSeconds + SchemeFadeSeconds;
            double pos = time < 0 ? 0 : time / cycle;
            int idx = (int)Math.Floor(pos) % n;
            double phase = time - Math.Floor(pos) * cycle;
            if (phase <= SchemeHoldSeconds) return RainbowSchemes[idx];
            float k = (float)((phase - SchemeHoldSeconds) / SchemeFadeSeconds);
            return Theme.Blend(RainbowSchemes[idx], RainbowSchemes[(idx + 1) % n], k);
        }

        /// <summary>
        /// 绘制勋章。r 为正方形区域；
        /// shimmer 控制是否有扫光，locked 表示未解锁（灰化 + 完全静止）。
        /// </summary>
        public static void Draw(Painter pt, RectangleF r, string id, double time, bool shimmer = true, bool locked = false)
        {
            if (r.Width < 4f || r.Height < 4f) return;
            var t = pt.T;
            if (locked) shimmer = false;                    // 未解锁：一律无动效
            Color col = locked ? LockedColor(id, t) : ShownColor(id, t, time);

            float d = Math.Min(r.Width, r.Height);
            var c = new PointF(r.Left + r.Width / 2f, r.Top + r.Height / 2f);
            float rad = d / 2f;
            var disc = new RectangleF(c.X - rad, c.Y - rad, d, d);

            // 金属盘：径向渐变（固定光源在左上）
            using (var path = new GraphicsPath())
            {
                path.AddEllipse(disc);
                pt.Sphere(path, new PointF(c.X - rad * 0.34f, c.Y - rad * 0.38f),
                    Theme.Shade(col, 0.34f), Theme.Shade(col, -0.36f));
            }

            // 内环
            pt.StrokeCircle(c, rad * 0.72f, Theme.Alpha(Theme.Shade(col, 0.60f), 170), Math.Max(1f, d * 0.055f));

            // 中心星芒：固定角度（不自转）
            DrawStar(pt, c, rad * 0.42f, Theme.Shade(col, 0.66f), 0f);

            // 静态高光点
            pt.FillCircle(new PointF(c.X - rad * 0.32f, c.Y - rad * 0.36f), rad * 0.24f,
                Theme.Alpha(Color.White, locked ? 70 : 120));

            // 动态高光：一条倾斜的**实心半透明**光带扫过（不再用两端透明的渐变，只有整体降透明度）
            if (!shimmer) return;
            float phase = (float)((time % SweepSeconds) / SweepSeconds);
            float sx = r.Left - d * 0.9f + phase * (r.Width + d * 1.8f);
            float halfW = d * 0.10f;                        // 光带半宽
            float skew = d * 0.26f;                         // 倾斜量（上右下左）
            var band = new[]
            {
                new PointF(sx - halfW, r.Top - 1f),
                new PointF(sx + halfW, r.Top - 1f),
                new PointF(sx + halfW - skew, r.Bottom + 1f),
                new PointF(sx - halfW - skew, r.Bottom + 1f),
            };

            var st = pt.Raw.Save();
            try
            {
                using (var clip = new GraphicsPath())
                {
                    clip.AddEllipse(disc);
                    pt.Raw.SetClip(clip, CombineMode.Intersect);
                    using (var bp = new GraphicsPath())
                    using (var b = new SolidBrush(Theme.Alpha(Color.White, 88)))
                    {
                        bp.AddPolygon(band);
                        pt.Raw.FillPath(b, bp);
                    }
                }
            }
            finally { pt.Raw.Restore(st); }
        }

        /// <summary>奖励列表用的小尺寸勋章。</summary>
        public static void DrawIcon(Painter pt, RectangleF r, string medalId, double time, bool locked = false)
        {
            Draw(pt, r, medalId, time, !locked, locked);
        }

        private static void DrawStar(Painter pt, PointF c, float radius, Color color, float rotDeg)
        {
            const int points = 5;
            var pts = new PointF[points * 2];
            for (int i = 0; i < pts.Length; i++)
            {
                double a = (rotDeg - 90 + i * 180.0 / points) * Math.PI / 180.0;
                float rr = (i % 2 == 0) ? radius : radius * 0.42f;
                pts[i] = new PointF(c.X + (float)Math.Cos(a) * rr, c.Y + (float)Math.Sin(a) * rr);
            }
            using (var path = new GraphicsPath())
            using (var b = new SolidBrush(color))
            {
                path.AddPolygon(pts);
                pt.Raw.FillPath(b, path);
            }
        }

        /// <summary>HSV → RGB（备用配色工具）。</summary>
        public static Color FromHsv(double h, double s, double v)
        {
            h = ((h % 360.0) + 360.0) % 360.0;
            double c = v * s;
            double x = c * (1 - Math.Abs((h / 60.0) % 2 - 1));
            double m = v - c;
            double r, g, b;
            if (h < 60) { r = c; g = x; b = 0; }
            else if (h < 120) { r = x; g = c; b = 0; }
            else if (h < 180) { r = 0; g = c; b = x; }
            else if (h < 240) { r = 0; g = x; b = c; }
            else if (h < 300) { r = x; g = 0; b = c; }
            else { r = c; g = 0; b = x; }
            return Color.FromArgb(255,
                (int)Math.Round((r + m) * 255),
                (int)Math.Round((g + m) * 255),
                (int)Math.Round((b + m) * 255));
        }
    }
}
