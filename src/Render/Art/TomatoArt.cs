using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
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

        // --- 番茄精灵缓存 ---------------------------------------------------
        // 单颗番茄的即时绘制要付出：200 点超椭圆（每点 Cos/Sin + 2×Pow + 2×Sign）→ AddClosedCurve
        // → PathGradientBrush 球体渐变 → SetClip(路径) 建 Region → 五瓣萼片（每瓣一套
        // GraphicsPath + Matrix + 旋转平移）≈ 9 个 GDI+ 对象、数百次超越函数。
        // 而日历里**每一个有记录的日子都要画一颗**，一个月 20~30 颗/整窗帧，且这些番茄在帧间
        // 完全静止——这是"整窗重绘约 10ms"里最容易被忽略的一大块，弱 CPU 上会被放大数倍。
        // 这里照 Painter.Shadow 的既有做法：渲染成设备分辨率位图后 1:1 贴图，命中即近乎零成本。
        private sealed class SpriteKey
        {
            public string Id;
            public int W, H;
            public float Scale;
            public int BlitX, BlitY;      // 贴图位置（设备像素）
            public float FracX, FracY;    // 亚像素余量，烘进精灵内部
            public bool Ok;
        }

        private static readonly Dictionary<string, Bitmap> SpriteCache = new Dictionary<string, Bitmap>(StringComparer.Ordinal);
        private static readonly object SpriteLock = new object();
        private const int SpriteCacheMax = 512;

        /// <summary>精灵四周留出的设备像素余量，容纳抗锯齿外溢。</summary>
        private const int SpriteMargin = 3;

        private static SpriteKey MakeSpriteKey(Painter pt, RectangleF r, string kind, int tenths,
                                               Color a, Color b, Color c)
        {
            var k = new SpriteKey();
            float scale = pt.Scale <= 0f ? 1f : pt.Scale;

            // 关键：**把亚像素余量烘进精灵**，而不是简单四舍五入贴图位置。
            // 设备坐标 = floor(r.Left*scale) + frac，精灵内部按 (SpriteMargin+frac) 绘制、
            // 再贴到 (floor - SpriteMargin)，于是落点恰好等于 r.Left*scale ——
            // 与改动前的即时绘制**逐像素一致**，不会出现最多半个像素的整体偏移。
            int bx = (int)Math.Floor(r.Left * scale);
            int by = (int)Math.Floor(r.Top * scale);
            float fx = r.Left * scale - bx;
            float fy = r.Top * scale - by;

            int pw = (int)Math.Ceiling(r.Width * scale + fx) + SpriteMargin + 1;
            int ph = (int)Math.Ceiling(r.Height * scale + fy) + SpriteMargin + 1;
            if (r.Width <= 1f || r.Height <= 1f || pw < 6 || ph < 6 || pw > 512 || ph > 512) return k;

            k.Ok = true;
            k.W = pw;
            k.H = ph;
            k.Scale = scale;
            k.BlitX = bx - SpriteMargin;
            k.BlitY = by - SpriteMargin;
            k.FracX = fx;
            k.FracY = fy;
            // 余量按 1/1024 像素量化进 key：同名精灵的渲染位置最多差 1/1024 px，
            // 肉眼与逐像素比对都不可能分辨，但能让缓存键收敛。
            k.Id = kind + "|" + pw + "|" + ph + "|" + tenths + "|" +
                   ((int)Math.Round(fx * 1024f)) + "|" + ((int)Math.Round(fy * 1024f)) + "|" +
                   a.ToArgb() + "|" + b.ToArgb() + "|" + c.ToArgb() + "|" + pt.T.Bg.ToArgb() + "|" +
                   scale.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            return k;
        }

        private static Bitmap SpriteGet(SpriteKey k)
        {
            lock (SpriteLock)
            {
                Bitmap hit;
                return SpriteCache.TryGetValue(k.Id, out hit) ? hit : null;
            }
        }

        /// <summary>渲染精灵。只在缓存未命中时调用，因此这里的委托分配不影响稳态帧。</summary>
        private static Bitmap SpriteBuild(SpriteKey k, Painter pt, RectangleF r, Action<Painter, RectangleF> body)
        {
            Bitmap bmp = null;
            try
            {
                bmp = new Bitmap(k.W, k.H, PixelFormat.Format32bppPArgb);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    var inner = new Painter(g, k.Scale, pt.T);
                    body(inner, new RectangleF((SpriteMargin + k.FracX) / k.Scale,
                                               (SpriteMargin + k.FracY) / k.Scale,
                                               r.Width, r.Height));
                }
            }
            catch (Exception)
            {
                if (bmp != null) bmp.Dispose();
                return null;
            }

            lock (SpriteLock)
            {
                Bitmap hit;
                if (SpriteCache.TryGetValue(k.Id, out hit)) { bmp.Dispose(); return hit; }
                if (SpriteCache.Count >= SpriteCacheMax)
                {
                    foreach (var v in SpriteCache.Values) { try { v.Dispose(); } catch { } }
                    SpriteCache.Clear();
                }
                SpriteCache[k.Id] = bmp;
                return bmp;
            }
        }

        /// <summary>
        /// 把精灵 1:1 贴回界面。**必须临时重置世界变换**：带着 1.5 倍缩放去 DrawImage，
        /// GDI+ 会走 InterpolationMode 重采样管线，比即时绘制还慢（见 Painter.Shadow 的同一处教训）。
        /// </summary>
        private static void BlitSprite(Painter pt, SpriteKey k, Bitmap bmp)
        {
            var st = pt.Raw.Save();
            try
            {
                pt.Raw.ResetTransform();
                pt.Raw.DrawImageUnscaled(bmp, k.BlitX, k.BlitY);
            }
            finally { pt.Raw.Restore(st); }
        }

        /// <summary>按格数绘制一颗番茄（0..10 格）。走精灵缓存。</summary>
        public static void DrawTenths(Painter pt, RectangleF r, int tenthsInTomato, bool aborted)
        {
            if (r.Width <= 1f || r.Height <= 1f) return;
            float fill = tenthsInTomato / 10f;
            Color body = aborted ? Theme.Blend(pt.T.Accent, pt.T.Warn, 0.55f) : pt.T.Accent;
            Color dark = aborted ? Theme.Shade(pt.T.Warn, -0.25f) : pt.T.AccentDark;
            Color leaf = pt.T.Leaf;

            var k = MakeSpriteKey(pt, r, "tenths", tenthsInTomato, body, dark, leaf);
            if (k.Ok)
            {
                var bmp = SpriteGet(k) ?? SpriteBuild(k, pt, r,
                    delegate(Painter inner, RectangleF target) { DrawTomato(inner, target, fill, true, body, dark, leaf); });
                if (bmp != null) { BlitSprite(pt, k, bmp); return; }
            }
            DrawTomato(pt, r, fill, true, body, dark, leaf);     // 回退：尺寸异常或缓存失败
        }

        /// <summary>单色剪影版（用于深色底上的反色显示）：未获得部分为淡墨，已获得部分为纯墨。</summary>
        public static void DrawTenthsMono(Painter pt, RectangleF r, int tenthsInTomato, Color ink)
        {
            if (r.Width <= 1f || r.Height <= 1f) return;
            var k = MakeSpriteKey(pt, r, "mono", tenthsInTomato, ink, ink, ink);
            if (k.Ok)
            {
                var bmp = SpriteGet(k) ?? SpriteBuild(k, pt, r,
                    delegate(Painter inner, RectangleF target) { DrawTenthsMonoCore(inner, target, tenthsInTomato, ink); });
                if (bmp != null) { BlitSprite(pt, k, bmp); return; }
            }
            DrawTenthsMonoCore(pt, r, tenthsInTomato, ink);
        }

        /// <summary>单色剪影的即时绘制实现。</summary>
        private static void DrawTenthsMonoCore(Painter pt, RectangleF r, int tenthsInTomato, Color ink)
        {
            float fill = tenthsInTomato / 10f;
            if (fill < 0f) fill = 0f;
            if (fill > 1f) fill = 1f;

            float calyxH = r.Height * 0.26f;
            var bodyRect = new RectangleF(r.X, r.Y + calyxH, r.Width, r.Height - calyxH);
            if (bodyRect.Width < 2f || bodyRect.Height < 2f) return;

            using (var path = BodyPath(bodyRect))
            {
                using (var b = new SolidBrush(Theme.Alpha(ink, 70)))
                    pt.Raw.FillPath(b, path);

                if (fill > 0.01f)
                {
                    var clipRect = new RectangleF(
                        bodyRect.X - 2, bodyRect.Bottom - bodyRect.Height * fill - 1,
                        bodyRect.Width + 4, bodyRect.Height * fill + 2);
                    var st = pt.Raw.Save();
                    pt.Raw.SetClip(clipRect, CombineMode.Intersect);
                    using (var fb = new SolidBrush(ink))
                        pt.Raw.FillPath(fb, path);
                    pt.Raw.Restore(st);
                }

                using (var pen = new Pen(Theme.Alpha(ink, 190), Math.Max(1f, r.Width * 0.014f)))
                {
                    var strokeState = pt.Raw.Save();
                    pt.Raw.SetClip(path, CombineMode.Intersect);
                    pt.Raw.DrawPath(pen, path);
                    pt.Raw.Restore(strokeState);
                }
            }

            DrawCalyx(pt,
                new PointF(bodyRect.Left + bodyRect.Width / 2f, bodyRect.Top + bodyRect.Height * 0.02f),
                Math.Min(r.Width, r.Height) * 0.0027f,
                ink, Theme.Alpha(ink, 150));
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
            Color dark = Theme.Shade(body, -0.28f);
            Color leaf = pt.T.Leaf;

            var k = MakeSpriteKey(pt, r, "icon", 10, body, dark, leaf);
            if (k.Ok)
            {
                var bmp = SpriteGet(k) ?? SpriteBuild(k, pt, r,
                    delegate(Painter inner, RectangleF target) { DrawTomato(inner, target, 1f, true, body, dark, leaf); });
                if (bmp != null) { BlitSprite(pt, k, bmp); return; }
            }
            DrawTomato(pt, r, 1f, true, body, dark, leaf);
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
