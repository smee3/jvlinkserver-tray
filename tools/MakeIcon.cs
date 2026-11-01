using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;

// Build step (build.cmd): writes the EXE's icon -- the horse from src\App\Icons.cs, without a badge -- as
// an .ico with 16, 24, 32, 48 and 256 px images. Small images are 32-bit DIBs, 256 px is PNG.
// MakeIcon --preview <dir> writes PNGs to look at instead: every tray icon and the EXE icon, alone and on
// taskbar-like backgrounds.
namespace JVLinkServerTray.Tools
{
    public static class MakeIcon
    {
        static readonly int[] Sizes = { 16, 24, 32, 48, 256 };
        static readonly int[] PreviewSizes = { 16, 20, 24, 32, 48 };
        static readonly TrayColor[] Colors = { TrayColor.Green, TrayColor.Yellow, TrayColor.Red, TrayColor.Gray };

        public static int Main(string[] args)
        {
            if (args.Length == 2 && args[0] == "--preview") return Preview(args[1]);
            if (args.Length != 1)
            {
                Console.Error.WriteLine("usage: MakeIcon <out.ico> | MakeIcon --preview <dir>");
                return 2;
            }
            try
            {
                var images = new List<byte[]>();
                foreach (int size in Sizes)
                {
                    using (Bitmap b = Icons.DrawExe(size))
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

        static int Preview(string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                foreach (bool light in new[] { true, false })
                    foreach (TrayColor c in Colors)
                        foreach (int size in PreviewSizes)
                            using (Bitmap b = Icons.Draw(size, c, Icons.Fill(light)))
                                b.Save(Path.Combine(dir, "tray-" + (light ? "light" : "dark") + "-" + c.ToString().ToLowerInvariant() + "-" + size + ".png"), ImageFormat.Png);
                foreach (int size in PreviewSizes)
                    using (Bitmap b = Icons.DrawExe(size))
                        b.Save(Path.Combine(dir, "exe-" + size + ".png"), ImageFormat.Png);
                using (Bitmap sheet = Sheet())
                {
                    sheet.Save(Path.Combine(dir, "sheet-1x.png"), ImageFormat.Png);
                    using (Bitmap big = Enlarge(sheet, 4)) big.Save(Path.Combine(dir, "sheet-4x.png"), ImageFormat.Png);
                }
                return 0;
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("MakeIcon: " + e.Message);
                return 1;
            }
        }

        // One row per picture, one column per size, each on the background it will sit on: the four tray
        // icons on a light and on a dark taskbar, then the EXE icon on a white and on a dark Explorer window.
        static Bitmap Sheet()
        {
            const int label = 132, cell = 56;
            Color lightBar = Color.FromArgb(0xee, 0xee, 0xee), darkBar = Color.FromArgb(0x1c, 0x1c, 0x1c);
            Color white = Color.White, darkWindow = Color.FromArgb(0x19, 0x19, 0x19);
            var rows = new List<KeyValuePair<string, Func<int, Bitmap>>>();
            var backs = new List<Color>();
            foreach (bool light in new[] { true, false })
            {
                foreach (TrayColor c in Colors)
                {
                    bool l = light;
                    TrayColor tc = c;
                    rows.Add(new KeyValuePair<string, Func<int, Bitmap>>((light ? "明るい" : "暗い") + "タスクバー " + TrayText.ColorName(c),
                        delegate(int size) { return Icons.Draw(size, tc, Icons.Fill(l)); }));
                    backs.Add(light ? lightBar : darkBar);
                }
            }
            rows.Add(new KeyValuePair<string, Func<int, Bitmap>>("EXE（白い背景）", Icons.DrawExe));
            backs.Add(white);
            rows.Add(new KeyValuePair<string, Func<int, Bitmap>>("EXE（暗い背景）", Icons.DrawExe));
            backs.Add(darkWindow);

            var sheet = new Bitmap(label + cell * PreviewSizes.Length, cell * (rows.Count + 1), PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(sheet))
            using (var font = new Font("Yu Gothic UI", 9f, FontStyle.Regular, GraphicsUnit.Point))
            {
                g.Clear(white);
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                for (int i = 0; i < PreviewSizes.Length; i++)
                    g.DrawString(PreviewSizes[i] + " px", font, Brushes.Black, label + i * cell + 8, cell / 2 - 8);
                for (int r = 0; r < rows.Count; r++)
                {
                    int y = cell * (r + 1);
                    using (var back = new SolidBrush(backs[r])) g.FillRectangle(back, label, y, cell * PreviewSizes.Length, cell);
                    g.DrawString(rows[r].Key, font, Brushes.Black, 4, y + cell / 2 - 8);
                    for (int i = 0; i < PreviewSizes.Length; i++)
                    {
                        int size = PreviewSizes[i];
                        using (Bitmap b = rows[r].Value(size))
                        {
                            var at = new Rectangle(label + i * cell + (cell - size) / 2, y + (cell - size) / 2, size, size);
                            g.DrawImage(b, at, 0, 0, size, size, GraphicsUnit.Pixel);   // 1:1, blended like the shell does
                        }
                    }
                }
            }
            return sheet;
        }

        // Each pixel becomes a factor x factor block, to see the pixels on a high-DPI screen.
        static Bitmap Enlarge(Bitmap b, int factor)
        {
            var big = new Bitmap(b.Width * factor, b.Height * factor, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(big))
            {
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(b, new Rectangle(0, 0, big.Width, big.Height), new Rectangle(0, 0, b.Width, b.Height), GraphicsUnit.Pixel);
            }
            return big;
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
