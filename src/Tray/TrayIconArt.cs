using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using TomatoFocus.Core;
using TomatoFocus.Render;
using TomatoFocus.Render.Art;

namespace TomatoFocus.Tray
{
    /// <summary>程序化生成托盘图标：不同状态使用不同颜色，运行中叠加进度环。</summary>
    internal static class TrayIconArt
    {
        public static Color StateColor(TimerPhase phase, Theme theme)
        {
            switch (phase)
            {
                case TimerPhase.Focusing: return theme.Accent;
                case TimerPhase.Paused: return theme.Warn;
                case TimerPhase.Break: return theme.Leaf;
                default: return Color.FromArgb(150, 150, 150);   // 未运行：灰调
            }
        }

        public static Bitmap Render(int size, TimerPhase phase, double progress, Theme theme)
        {
            var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                var pt = new Painter(g, 1f, theme);

                float pad = size * 0.06f;
                float ringW = Math.Max(1.4f, size * 0.085f);
                float cx = size / 2f, cy = size / 2f;
                float ringR = size / 2f - pad - ringW / 2f;

                Color col = StateColor(phase, theme);

                // 轨道
                pt.StrokeCircle(new PointF(cx, cy), ringR, Theme.Alpha(col, 60), ringW);

                // 进度
                if (phase == TimerPhase.Focusing || phase == TimerPhase.Break)
                {
                    float sweep = (float)Math.Max(0.0, Math.Min(1.0, progress)) * 360f;
                    if (sweep > 0.5f)
                        pt.Arc(new PointF(cx, cy), ringR, -90f, sweep, col, ringW);
                }
                else if (phase == TimerPhase.Paused)
                {
                    pt.Arc(new PointF(cx, cy), ringR, -90f, 360f * (float)Math.Max(0.0, Math.Min(1.0, progress)), col, ringW);
                }

                // 番茄本体
                float bodySize = (ringR - ringW) * 1.62f;
                var body = new RectangleF(cx - bodySize / 2f, cy - bodySize / 2f + size * 0.02f, bodySize, bodySize);
                TomatoArt.DrawTomato(pt, body, 1f, size >= 20, col, Theme.Shade(col, -0.35f),
                    phase == TimerPhase.Idle ? Theme.Shade(theme.Leaf, -0.25f) : theme.Leaf);
            }
            return bmp;
        }

        /// <summary>窗口图标（多尺寸）。</summary>
        public static Icon AppIcon(Theme theme)
        {
            int[] sizes = { 16, 20, 24, 32, 48, 64, 128, 256 };
            var bmps = new Bitmap[sizes.Length];
            for (int i = 0; i < sizes.Length; i++) bmps[i] = Render(sizes[i], TimerPhase.Idle, 0, theme);
            var icon = IconFactory.FromBitmaps(bmps);
            foreach (var b in bmps) b.Dispose();
            return icon;
        }
    }
}
