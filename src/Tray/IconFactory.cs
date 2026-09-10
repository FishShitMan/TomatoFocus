using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace TomatoFocus.Tray
{
    /// <summary>把程序化绘制的位图打包成 ICO（PNG 帧，Vista+ 支持，可保留完整 alpha）。</summary>
    internal static class IconFactory
    {
        public static Icon FromBitmaps(params Bitmap[] bitmaps)
        {
            if (bitmaps == null || bitmaps.Length == 0) return null;
            var pngs = new byte[bitmaps.Length][];
            for (int i = 0; i < bitmaps.Length; i++)
            {
                using (var ms = new MemoryStream())
                {
                    bitmaps[i].Save(ms, ImageFormat.Png);
                    pngs[i] = ms.ToArray();
                }
            }

            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write((ushort)0);                 // reserved
                w.Write((ushort)1);                 // type = icon
                w.Write((ushort)bitmaps.Length);    // count

                int offset = 6 + 16 * bitmaps.Length;
                for (int i = 0; i < bitmaps.Length; i++)
                {
                    int w0 = bitmaps[i].Width >= 256 ? 0 : bitmaps[i].Width;
                    int h0 = bitmaps[i].Height >= 256 ? 0 : bitmaps[i].Height;
                    w.Write((byte)w0);
                    w.Write((byte)h0);
                    w.Write((byte)0);               // color count
                    w.Write((byte)0);               // reserved
                    w.Write((ushort)1);             // planes
                    w.Write((ushort)32);            // bpp
                    w.Write(pngs[i].Length);
                    w.Write(offset);
                    offset += pngs[i].Length;
                }
                for (int i = 0; i < bitmaps.Length; i++) w.Write(pngs[i]);
                w.Flush();
                ms.Position = 0;
                return new Icon(ms);
            }
        }

        public static Icon FromBitmap(Bitmap bmp)
        {
            return FromBitmaps(bmp);
        }
    }
}
