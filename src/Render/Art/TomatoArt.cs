using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using TomatoFocus.Core;

namespace TomatoFocus.Render.Art
{
    /// <summary>
    /// 程序化番茄美术。所有形状均由代码生成的路径与渐变绘制，不含任何位图资源。
    /// 如需替换为图片，只需在 assets/manifest.json 中为对应槽位提供文件。
    /// </summary>
    internal static class TomatoArt
    {
        /// <summary>超椭圆（Lamé 曲线）本体路径。</summary>
        public static GraphicsPath BodyPath(RectangleF r, float n = 2.35f)
        {
            var p = new GraphicsPath();
            const int steps = 200;
            var pts = new PointF[steps];
            float cx = r.Left + r.Width / 2f, cy = r.Top + r.Height / 2f;
            float a = r.Width / 2f, b = r.Height / 2f;
            for (int i = 0; i < steps; i++)
            {
                double t = 2 * Math.PI * i / steps;
                double ct = Math.Cos(t), st = Math.Sin(t);
                double x = Math.Sign(ct) * Math.Pow(Math.Abs(ct), 2.0 / n);
                double y = Math.Sign(st) * Math.Pow(Math.Abs(st), 2.0 / n);
                pts[i] = new PointF(cx + (float)(x * a), cy + (float)(y * b));
            }
            p.AddClosedCurve(pts, 0.0f);
            return p;
        }

        /// <summary>五瓣萼片 + 茎。</summary>
        public static void DrawCalyx(Painter pt, PointF center, float scale, Color leaf, Color leafDark)
        {
            using (var grad = new LinearGradientBrush(
                new RectangleF(center.X - 60 * scale, center.Y - 70 * scale, 120 * scale, 90 * scale),
                Theme.Shade(leaf, 0.12f), leafDark, 62f))
            {
                for (int i = 0; i < 5; i++)
                {
                    // 叶片局部朝上 (0,-94)，这里以 0 度为中心向两侧张开，形成向上的萼片簇
                    double ang = (i - 2) * 0.45;
                    using (var leafPath = new GraphicsPath())
                    {
                        leafPath.AddBezier(
                            new PointF(0, 0),
                            new PointF(-24 * scale, -26 * scale),
                            new PointF(-32 * scale, -70 * scale),
                            new PointF(0, -94 * scale));
                        leafPath.AddBezier(
                            new PointF(0, -94 * scale),
                            new PointF(32 * scale, -70 * scale),
                            new PointF(24 * scale, -26 * scale),
                            new PointF(0, 0));
                        using (var m = new Matrix())
                        {
                            // 必须先旋转再平移（Append），否则萼片会绕原点被甩出可见区域
                            m.RotateAt((float)(ang * 180 / Math.PI), new PointF(0, 0), MatrixOrder.Append);
                            m.Translate(center.X, center.Y, MatrixOrder.Append);
                            leafPath.Transform(m);
                            pt.Raw.FillPath(grad, leafPath);
                        }
                    }
                }
            }
            using (var stem = new Pen(leafDark, 9f * scale))
            {
                stem.StartCap = LineCap.Round;
                stem.EndCap = LineCap.Round;
                pt.Raw.DrawLine(stem, center.X, center.Y - 6 * scale, center.X, center.Y - 78 * scale);
            }
        }

        /// <summary>绘制一颗番茄。fill 为 0..1 的“饱满度”，用于表现残缺碎片。</summary>
        public static void DrawTomato(Painter pt, RectangleF r, float fill, bool withCalyx, Color body, Color bodyDark, Color leaf)
        {
            if (r.Width <= 1 || r.Height <= 1) return;
            fill = fill < 0 ? 0 : (fill > 1 ? 1 : fill);

            // 顶部预留萼片与茎的空间，保证任何尺寸下都不会超出给定区域
            float calyxH = r.Height * 0.26f;
            var bodyRect = new RectangleF(r.X, r.Y + calyxH, r.Width, r.Height - calyxH);
            if (bodyRect.Height < 2f) return;

            using (var path = BodyPath(bodyRect))
            {
                // 底色（暗淡）
                using (var b = new SolidBrush(Theme.Blend(bodyDark, pt.T.Bg, 0.55f)))
                    pt.Raw.FillPath(b, path);

                // 已获得的份额：从底部向上填充
                if (fill > 0.01f)
                {
                    var clipRect = new RectangleF(
                        bodyRect.X - 2, bodyRect.Bottom - bodyRect.Height * fill - 1,
                        bodyRect.Width + 4, bodyRect.Height * fill + 2);
                    var st = pt.Raw.Save();
                    pt.Raw.SetClip(clipRect, CombineMode.Intersect);
                    pt.Sphere(path,
                        new PointF(bodyRect.Left + bodyRect.Width * 0.34f, bodyRect.Top + bodyRect.Height * 0.30f),
                        Theme.Shade(body, 0.10f), bodyDark);
                    pt.Raw.Restore(st);
                }

                // 描边（裁剪到路径内，保证不越出给定区域）
                using (var pen = new Pen(Theme.Alpha(bodyDark, 90), Math.Max(1f, r.Width * 0.012f)))
                {
                    var strokeState = pt.Raw.Save();
                    pt.Raw.SetClip(path, CombineMode.Intersect);
                    pt.Raw.DrawPath(pen, path);
                    pt.Raw.Restore(strokeState);
                }
            }

            // 高光
            if (fill > 0.25f)
            {
                var hl = new RectangleF(
                    bodyRect.Left + bodyRect.Width * 0.24f,
                    bodyRect.Top + bodyRect.Height * 0.16f,
                    bodyRect.Width * 0.28f, bodyRect.Height * 0.20f);
                using (var hp = new GraphicsPath())
                {
                    hp.AddEllipse(hl);
                    pt.Glow(hp,
                        new PointF(hl.Left + hl.Width / 2, hl.Top + hl.Height / 2),
                        Theme.Alpha(Color.White, (int)(150 * fill)),
                        Theme.Alpha(Color.White, 0));
                }
            }

            if (withCalyx)
                DrawCalyx(pt,
                    new PointF(bodyRect.Left + bodyRect.Width / 2f, bodyRect.Top + bodyRect.Height * 0.02f),
                    Math.Min(r.Width, r.Height) * 0.0027f,
                    leaf, Theme.Shade(leaf, -0.22f));
        }

        /// <summary>按格数绘制一颗番茄（0..10 格）。</summary>
        public static void DrawTenths(Painter pt, RectangleF r, int tenthsInTomato, bool aborted)
        {
            float fill = tenthsInTomato / 10f;
            Color body = aborted ? Theme.Blend(pt.T.Accent, pt.T.Warn, 0.55f) : pt.T.Accent;
            Color dark = aborted ? Theme.Shade(pt.T.Warn, -0.25f) : pt.T.AccentDark;
            DrawTomato(pt, r, fill, true, body, dark, pt.T.Leaf);
        }

        /// <summary>番茄图标（用于按钮/标签）。存在 assets 图片时优先使用图片，否则程序化绘制。</summary>
        public static void Icon(Painter pt, RectangleF r, Color? tint = null)
        {
            var img = Assets.AssetRegistry.Default.Get("icon.tomato");
            if (img != null)
            {
                DrawImage(pt, img, r);
                return;
            }
            Color body = tint ?? pt.T.Accent;
            DrawTomato(pt, r, 1f, true, body, Theme.Shade(body, -0.28f), pt.T.Leaf);
        }

        /// <summary>按比例居中绘制外部图片。</summary>
        public static void DrawImage(Painter pt, Image img, RectangleF r)
        {
            if (img == null || r.Width <= 0 || r.Height <= 0) return;
            float scale = Math.Min(r.Width / img.Width, r.Height / img.Height);
            float w = img.Width * scale, h = img.Height * scale;
            var dest = new RectangleF(r.Left + (r.Width - w) / 2f, r.Top + (r.Height - h) / 2f, w, h);
            pt.Raw.DrawImage(img, dest);
        }
    }
}
