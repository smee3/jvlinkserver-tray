using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

// Build step (build.cmd): writes the EXE's icon -- the app tile from src\App\Icons.cs without a badge --
// as an .ico with 16, 24, 32, 48 and 256 px images. Small images are 32-bit DIBs, 256 px is PNG.
namespace JVLinkServerTray.Tools
{
    public static class MakeIcon
    {
        static readonly int[] Sizes = { 16, 24, 32, 48, 256 };

        public static int Main(string[] args)
        {
            if (args.Length != 1)
            {
                Console.Error.WriteLine("usage: MakeIcon <out.ico>");
                return 2;
            }
            try
            {
                var images = new List<byte[]>();
                foreach (int size in Sizes)
                {
                    using (Bitmap b = Icons.Draw(size, null))
                    {
                        images.Add(size >= 256 ? Png(b) : Dib(b));
                    }
                }
                File.WriteAllBytes(args[0], Ico(images));
                return 0;
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("MakeIcon: " + e.Message);
                return 1;
            }
        }

        static byte[] Ico(List<byte[]> images)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write((ushort)0);                  // reserved
                w.Write((ushort)1);                  // type: icon
                w.Write((ushort)images.Count);
                int offset = 6 + 16 * images.Count;
                for (int i = 0; i < images.Count; i++)
                {
                    int size = Sizes[i];
                    w.Write((byte)(size >= 256 ? 0 : size));   // width (0 = 256)
                    w.Write((byte)(size >= 256 ? 0 : size));   // height
                    w.Write((byte)0);                // colours in palette
                    w.Write((byte)0);                // reserved
                    w.Write((ushort)1);              // planes
                    w.Write((ushort)32);             // bits per pixel
                    w.Write(images[i].Length);
                    w.Write(offset);
                    offset += images[i].Length;
                }
                foreach (byte[] image in images) w.Write(image);
                w.Flush();
                return ms.ToArray();
            }
        }

        static byte[] Png(Bitmap b)
        {
            using (var ms = new MemoryStream())
            {
                b.Save(ms, ImageFormat.Png);
                return ms.ToArray();
            }
        }

        // BITMAPINFOHEADER, then BGRA rows bottom-up, then the 1-bit AND mask (1 = transparent).
        static byte[] Dib(Bitmap b)
        {
            int w = b.Width, h = b.Height;
            int maskStride = ((w + 31) / 32) * 4;
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write(40);                         // biSize
                bw.Write(w);
                bw.Write(h * 2);                      // colour image + mask
                bw.Write((ushort)1);                  // planes
                bw.Write((ushort)32);                 // bit count
                bw.Write(0);                          // BI_RGB
                bw.Write(w * h * 4 + maskStride * h); // image size
                bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);
                for (int y = h - 1; y >= 0; y--)
                {
                    for (int x = 0; x < w; x++)
                    {
                        Color c = b.GetPixel(x, y);
                        bw.Write(c.B); bw.Write(c.G); bw.Write(c.R); bw.Write(c.A);
                    }
                }
                for (int y = h - 1; y >= 0; y--)
                {
                    var row = new byte[maskStride];
                    for (int x = 0; x < w; x++)
                        if (b.GetPixel(x, y).A == 0) row[x / 8] |= (byte)(0x80 >> (x % 8));
                    bw.Write(row);
                }
                bw.Flush();
                return ms.ToArray();
            }
        }
    }
}
