using System;
using System.Collections.Generic;
using System.Drawing;

namespace TomatoFocus.Render
{
    /// <summary>配色主题（程序化生成，不依赖任何图片资源）。</summary>
    internal sealed class Theme
    {
        public string Id = "fresh";
        public string Name = "清新";
        public bool Dark;

        public Color Bg;
        public Color Surface;
        public Color SurfaceAlt;
        public Color Border;
        public Color Text;
        public Color TextMuted;
        public Color Accent;        // 番茄红
        public Color AccentDark;
        public Color AccentSoft;
        public Color Leaf;          // 叶绿
        public Color RingTrack;
        public Color Warn;          // 中断/提示
        public Color Good;
        public Color Shadow;

        private static readonly Dictionary<string, Theme> Map = new Dictionary<string, Theme>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<Theme> Order = new List<Theme>();

        static Theme()
        {
            Add(new Theme
            {
                Id = "fresh", Name = "清新绿",
                Bg = C(0xFDFBF8), Surface = C(0xFFFFFF), SurfaceAlt = C(0xF6F3EE),
                Border = C(0xE7E1D8), Text = C(0x2F2A26), TextMuted = C(0x8C857C),
                Accent = C(0xE2543C), AccentDark = C(0xC13E2A), AccentSoft = C(0xFBE7E2),
                Leaf = C(0x4CAF7D), RingTrack = C(0xEDE7DE),
                Warn = C(0xE0A32B), Good = C(0x4CAF7D), Shadow = Color.FromArgb(28, 90, 74, 60)
            });
            Add(new Theme
            {
                Id = "sakura", Name = "樱粉",
                Bg = C(0xFFF9FA), Surface = C(0xFFFFFF), SurfaceAlt = C(0xFDF0F2),
                Border = C(0xF0DDE1), Text = C(0x33282B), TextMuted = C(0x9A878C),
                Accent = C(0xE8607E), AccentDark = C(0xC74A66), AccentSoft = C(0xFCE4EA),
                Leaf = C(0x6FAF8B), RingTrack = C(0xF2E2E6),
                Warn = C(0xD9A02E), Good = C(0x6FAF8B), Shadow = Color.FromArgb(26, 120, 70, 88)
            });
            Add(new Theme
            {
                Id = "ocean", Name = "海蓝",
                Bg = C(0xF7FAFD), Surface = C(0xFFFFFF), SurfaceAlt = C(0xEDF4FA),
                Border = C(0xDCE7F1), Text = C(0x22303C), TextMuted = C(0x7E8E9C),
                Accent = C(0x2F7FC1), AccentDark = C(0x226AA5), AccentSoft = C(0xE1EEF9),
                Leaf = C(0x3FA98C), RingTrack = C(0xE1EAF2),
                Warn = C(0xD99B2B), Good = C(0x3FA98C), Shadow = Color.FromArgb(24, 50, 90, 130)
            });
            Add(new Theme
            {
                Id = "night", Name = "夜墨", Dark = true,
                Bg = C(0x1B1D21), Surface = C(0x24272C), SurfaceAlt = C(0x2C3037),
                Border = C(0x3A3F47), Text = C(0xEDEAE6), TextMuted = C(0x9AA1AB),
                Accent = C(0xE86A52), AccentDark = C(0xC7503C), AccentSoft = C(0x3A2A28),
                Leaf = C(0x5FBF95), RingTrack = C(0x33383F),
                Warn = C(0xE0B455), Good = C(0x5FBF95), Shadow = Color.FromArgb(70, 0, 0, 0)
            });
        }

        private static Color C(int rgb)
        {
            return Color.FromArgb(255, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
        }

        private static void Add(Theme t)
        {
            Map[t.Id] = t;
            Order.Add(t);
        }

        public static Theme[] All { get { return Order.ToArray(); } }

        public static Theme ById(string id)
        {
            Theme t;
            if (!string.IsNullOrEmpty(id) && Map.TryGetValue(id, out t)) return t;
            return Order[0];
        }

        public static Color Alpha(Color c, int a)
        {
            if (a < 0) a = 0;
            if (a > 255) a = 255;
            return Color.FromArgb(a, c.R, c.G, c.B);
        }

        public static Color Blend(Color a, Color b, float t)
        {
            if (t <= 0) return a;
            if (t >= 1) return b;
            return Color.FromArgb(
                (int)(a.A + (b.A - a.A) * t),
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }

        public static Color Shade(Color c, float amount)
        {
            // amount > 0 变亮，< 0 变暗
            if (amount >= 0)
                return Blend(c, Color.White, amount);
            return Blend(c, Color.Black, -amount);
        }
    }
}
