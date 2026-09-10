using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace TomatoFocus.Render.Art
{
    /// <summary>程序化图标集（全部由线条/路径生成，无位图）。</summary>
    internal static class IconArt
    {
        public static void Draw(Painter pt, string name, RectangleF r, Color c, float width = 2f)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            float cx = r.Left + r.Width / 2f, cy = r.Top + r.Height / 2f;
            float s = Math.Min(r.Width, r.Height);
            switch (name)
            {
                case "play":
                    {
                        using (var p = new GraphicsPath())
                        {
                            p.AddPolygon(new[]
                            {
                                new PointF(cx - s * 0.24f, cy - s * 0.32f),
                                new PointF(cx + s * 0.32f, cy),
                                new PointF(cx - s * 0.24f, cy + s * 0.32f)
                            });
                            using (var b = new SolidBrush(c)) pt.Raw.FillPath(b, p);
                        }
                        break;
                    }
                case "pause":
                    {
                        float w = s * 0.20f, h = s * 0.62f;
                        pt.FillRound(new RectangleF(cx - s * 0.26f - w / 2, cy - h / 2, w, h), w * 0.35f, c);
                        pt.FillRound(new RectangleF(cx + s * 0.26f - w / 2, cy - h / 2, w, h), w * 0.35f, c);
                        break;
                    }
                case "stop":
                    pt.FillRound(new RectangleF(cx - s * 0.26f, cy - s * 0.26f, s * 0.52f, s * 0.52f), s * 0.12f, c);
                    break;
                case "reset":
                    {
                        float rad = s * 0.32f;
                        pt.Arc(new PointF(cx, cy), rad, -50, 290, c, width);
                        // 箭头
                        double a = -50 * Math.PI / 180;
                        float ax = cx + (float)(Math.Cos(a) * rad), ay = cy + (float)(Math.Sin(a) * rad);
                        using (var p = new GraphicsPath())
                        {
                            p.AddPolygon(new[]
                            {
                                new PointF(ax - s * 0.02f, ay - s * 0.14f),
                                new PointF(ax + s * 0.14f, ay - s * 0.02f),
                                new PointF(ax - s * 0.04f, ay + s * 0.10f)
                            });
                            using (var b = new SolidBrush(c)) pt.Raw.FillPath(b, p);
                        }
                        break;
                    }
                case "gear":
                    {
                        float rad = s * 0.28f;
                        pt.StrokeCircle(new PointF(cx, cy), rad, c, width);
                        pt.StrokeCircle(new PointF(cx, cy), rad * 0.34f, c, width * 0.9f);
                        for (int i = 0; i < 8; i++)
                        {
                            double a = Math.PI * 2 * i / 8;
                            float x1 = cx + (float)(Math.Cos(a) * rad * 1.05f);
                            float y1 = cy + (float)(Math.Sin(a) * rad * 1.05f);
                            float x2 = cx + (float)(Math.Cos(a) * rad * 1.45f);
                            float y2 = cy + (float)(Math.Sin(a) * rad * 1.45f);
                            pt.Line(new PointF(x1, y1), new PointF(x2, y2), c, width);
                        }
                        break;
                    }
                case "chevronLeft":
                case "chevronRight":
                    {
                        float d = name == "chevronLeft" ? -1 : 1;
                        pt.Line(new PointF(cx - d * s * 0.12f, cy - s * 0.24f),
                                new PointF(cx + d * s * 0.14f, cy), c, width);
                        pt.Line(new PointF(cx + d * s * 0.14f, cy),
                                new PointF(cx - d * s * 0.12f, cy + s * 0.24f), c, width);
                        break;
                    }
                case "close":
                    pt.Line(new PointF(cx - s * 0.24f, cy - s * 0.24f), new PointF(cx + s * 0.24f, cy + s * 0.24f), c, width);
                    pt.Line(new PointF(cx + s * 0.24f, cy - s * 0.24f), new PointF(cx - s * 0.24f, cy + s * 0.24f), c, width);
                    break;
                case "menu":
                    {
                        float w = s * 0.52f;
                        for (int i = -1; i <= 1; i++)
                            pt.Line(new PointF(cx - w / 2, cy + i * s * 0.20f),
                                    new PointF(cx + w / 2, cy + i * s * 0.20f), c, width);
                        break;
                    }
                case "minus":
                    pt.Line(new PointF(cx - s * 0.24f, cy), new PointF(cx + s * 0.24f, cy), c, width);
                    break;
                case "trophy":
                    {
                        float w = s * 0.44f, h = s * 0.38f;
                        var cup = new RectangleF(cx - w / 2, cy - h * 0.85f, w, h);
                        using (var p = new GraphicsPath())
                        {
                            p.AddArc(cup.Left, cup.Top, cup.Width, cup.Height, 180, 180);
                            p.AddLine(cup.Right, cup.Top + cup.Height / 2, cup.Left, cup.Top + cup.Height / 2);
                            p.CloseFigure();
                            using (var pen = new Pen(c, width)) pt.Raw.DrawPath(pen, p);
                        }
                        pt.Line(new PointF(cx, cy + h * 0.12f), new PointF(cx, cy + h * 0.42f), c, width);
                        pt.Line(new PointF(cx - w * 0.36f, cy + h * 0.46f), new PointF(cx + w * 0.36f, cy + h * 0.46f), c, width);
                        break;
                    }
                case "gift":
                    {
                        var box = new RectangleF(cx - s * 0.32f, cy - s * 0.14f, s * 0.64f, s * 0.42f);
                        pt.StrokeRound(box, s * 0.05f, c, width);
                        pt.Line(new PointF(cx, box.Top), new PointF(cx, box.Bottom), c, width * 0.9f);
                        pt.Line(new PointF(box.Left, box.Top + box.Height * 0.34f), new PointF(box.Right, box.Top + box.Height * 0.34f), c, width * 0.9f);
                        pt.Arc(new PointF(cx - s * 0.10f, cy - s * 0.24f), s * 0.10f, 180, 200, c, width);
                        pt.Arc(new PointF(cx + s * 0.10f, cy - s * 0.24f), s * 0.10f, 160, 200, c, width);
                        break;
                    }
                case "calendar":
                    {
                        var box = new RectangleF(cx - s * 0.32f, cy - s * 0.28f, s * 0.64f, s * 0.56f);
                        pt.StrokeRound(box, s * 0.07f, c, width);
                        pt.Line(new PointF(box.Left, box.Top + s * 0.16f), new PointF(box.Right, box.Top + s * 0.16f), c, width * 0.9f);
                        pt.Line(new PointF(cx - s * 0.16f, box.Top - s * 0.08f), new PointF(cx - s * 0.16f, box.Top + s * 0.05f), c, width);
                        pt.Line(new PointF(cx + s * 0.16f, box.Top - s * 0.08f), new PointF(cx + s * 0.16f, box.Top + s * 0.05f), c, width);
                        break;
                    }
                case "check":
                    pt.Line(new PointF(cx - s * 0.24f, cy + s * 0.02f), new PointF(cx - s * 0.06f, cy + s * 0.20f), c, width);
                    pt.Line(new PointF(cx - s * 0.06f, cy + s * 0.20f), new PointF(cx + s * 0.26f, cy - s * 0.20f), c, width);
                    break;
                case "person":
                    {
                        pt.StrokeCircle(new PointF(cx, cy - s * 0.18f), s * 0.12f, c, width);
                        using (var p = new GraphicsPath())
                        {
                            p.AddArc(cx - s * 0.22f, cy + s * 0.02f, s * 0.44f, s * 0.44f, 180, 180);
                            using (var pen = new Pen(c, width)) pt.Raw.DrawPath(pen, p);
                        }
                        break;
                    }
                case "leaf":
                    {
                        using (var p = new GraphicsPath())
                        {
                            p.AddBezier(new PointF(cx - s * 0.3f, cy + s * 0.3f), new PointF(cx - s * 0.34f, cy - s * 0.2f),
                                new PointF(cx + s * 0.2f, cy - s * 0.36f), new PointF(cx + s * 0.3f, cy - s * 0.28f));
                            p.AddBezier(new PointF(cx + s * 0.3f, cy - s * 0.28f), new PointF(cx + s * 0.34f, cy + s * 0.14f),
                                new PointF(cx - s * 0.16f, cy + s * 0.34f), new PointF(cx - s * 0.3f, cy + s * 0.3f));
                            using (var pen = new Pen(c, width)) pt.Raw.DrawPath(pen, p);
                        }
                        break;
                    }
                case "sun":
                    {
                        pt.StrokeCircle(new PointF(cx, cy), s * 0.18f, c, width);
                        for (int i = 0; i < 8; i++)
                        {
                            double a = Math.PI * 2 * i / 8;
                            pt.Line(
                                new PointF(cx + (float)(Math.Cos(a) * s * 0.26f), cy + (float)(Math.Sin(a) * s * 0.26f)),
                                new PointF(cx + (float)(Math.Cos(a) * s * 0.36f), cy + (float)(Math.Sin(a) * s * 0.36f)),
                                c, width * 0.9f);
                        }
                        break;
                    }
                default:
                    pt.StrokeCircle(new PointF(cx, cy), s * 0.28f, c, width);
                    break;
            }
        }
    }
}
