using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace JVLinkServerTray.Tests
{
    public static class IconTests
    {
        static readonly int[] TraySizes = { 16, 20, 24, 32, 48 };

        static int Rgb(Color c) { return c.ToArgb() & 0xffffff; }

        static Point PixelAt(PointF p) { return new Point((int)Math.Floor(p.X), (int)Math.Floor(p.Y)); }

        [Test] public static void IconLook_fill_followsTheTaskbarTheme() {
            Assert.True(IconLook.LightTaskbar(1), "SystemUsesLightTheme 1 = light taskbar");
            Assert.False(IconLook.LightTaskbar(0), "SystemUsesLightTheme 0 = dark taskbar");
            Assert.True(IconLook.LightTaskbar(null), "missing = light");
            Assert.True(IconLook.LightTaskbar("0"), "not a DWORD = light");
            Assert.Equal(0x1d3557, IconLook.FillFor(true), "light taskbar: dark navy horse");
            Assert.Equal(0xe8eef7, IconLook.FillFor(false), "dark taskbar: light horse");
            Assert.Equal(0x1d3557, Rgb(Icons.Fill(true)), "drawing colour, light taskbar");
            Assert.Equal(0xe8eef7, Rgb(Icons.Fill(false)), "drawing colour, dark taskbar");
            Assert.Equal(255, (int)Icons.Fill(false).A, "opaque");
        }

        [Test] public static void IconLook_smallIconSize_followsTheDpi() {
            // Without GetSystemMetricsForDpi (before Windows 10 1607): 16 px scaled by dpi / 96.
            Assert.Equal(16, IconLook.SmallIconSize(96, null), "96 dpi (100 %)");
            Assert.Equal(20, IconLook.SmallIconSize(120, null), "120 dpi (125 %)");
            Assert.Equal(24, IconLook.SmallIconSize(144, null), "144 dpi (150 %)");
            Assert.Equal(32, IconLook.SmallIconSize(192, null), "192 dpi (200 %)");
            // Windows' own metric wins when it gives a sane value.
            Assert.Equal(20, IconLook.SmallIconSize(120, d => d * 16 / 96), "metric 120");
            Assert.Equal(32, IconLook.SmallIconSize(192, d => d * 16 / 96), "metric 192");
            Assert.Equal(22, IconLook.SmallIconSize(120, d => 22), "the metric is used");
            Assert.Equal(20, IconLook.SmallIconSize(120, d => 0), "metric 0: scaled");
            Assert.Equal(20, IconLook.SmallIconSize(120, d => { throw new EntryPointNotFoundException("GetSystemMetricsForDpi"); }), "metric missing: scaled");
            Assert.Equal(16, IconLook.SmallIconSize(0, null), "unknown dpi");
            Assert.Equal(16, IconLook.SmallIconSize(100000, d => 9999), "absurd dpi and metric");
        }

        [Test] public static void Icons_horsePath_fitsTheBox() {
            foreach (RectangleF box in new[] { new RectangleF(0, 0, 17, 16.5f), new RectangleF(1, 1, 14, 14), new RectangleF(3, 3, 42, 42) })
            {
                using (GraphicsPath p = Icons.HorsePath(box))
                {
                    RectangleF b = p.GetBounds();
                    string what = "box " + box + ", bounds " + b;
                    Assert.True(b.Left >= box.Left - 0.01f && b.Right <= box.Right + 0.01f, what + ": inside horizontally");
                    Assert.True(b.Top >= box.Top - 0.01f && b.Bottom <= box.Bottom + 0.01f, what + ": inside vertically");
                    Assert.True(b.Width >= box.Width * 0.9f, what + ": fills the width");
                    Assert.True(Math.Abs(b.Bottom - box.Bottom) < 0.01f, what + ": stands on the bottom");
                }
            }
        }

        [Test] public static void Icons_horse_isDrawnInTheFillColour() {
            foreach (bool light in new[] { true, false })
            {
                foreach (int size in TraySizes)
                {
                    Color fill = Icons.Fill(light);
                    int opaque = 0, other = 0;
                    using (Bitmap b = Icons.Draw(size, null, fill))
                    {
                        Assert.Equal(size, b.Width, "width");
                        Assert.Equal(size, b.Height, "height");
                        for (int y = 0; y < size; y++)
                            for (int x = 0; x < size; x++)
                            {
                                Color c = b.GetPixel(x, y);
                                if (c.A != 255) continue;
                                opaque++;
                                if (Rgb(c) != Rgb(fill)) other++;
                            }
                    }
                    string what = (light ? "light" : "dark") + " taskbar, " + size + " px";
                    Assert.True(opaque >= size * size / 4, what + ": the horse covers a good part (" + opaque + " opaque pixels)");
                    Assert.Equal(0, other, what + ": opaque pixels in another colour");
                }
            }
        }

        [Test] public static void Icons_eye_onlyFrom24px() {
            foreach (int size in TraySizes)
            {
                Point eye = PixelAt(Icons.EyeCentre(Icons.GlyphBox(size)));
                using (Bitmap b = Icons.Draw(size, null, Icons.Fill(true)))
                {
                    int a = b.GetPixel(eye.X, eye.Y).A;
                    if (size >= 32) Assert.Equal(0, a, size + " px: the eye is a hole");
                    else if (size >= 24) Assert.True(a < 64, size + " px: the eye is (nearly) a hole, alpha " + a);
                    else Assert.Equal(255, a, size + " px: no eye");
                }
            }
            Point exeEye = PixelAt(Icons.EyeCentre(Icons.ExeGlyphBox(48)));
            using (Bitmap b = Icons.DrawExe(48)) Assert.Equal(0, (int)b.GetPixel(exeEye.X, exeEye.Y).A, "EXE 48 px: the eye is a hole");
            exeEye = PixelAt(Icons.EyeCentre(Icons.ExeGlyphBox(16)));
            using (Bitmap b = Icons.DrawExe(16)) Assert.Equal(255, (int)b.GetPixel(exeEye.X, exeEye.Y).A, "EXE 16 px: no eye");
        }

        // Pixels well inside the transparent ring between the badge and the horse: opaque without the badge,
        // fully transparent with it, for every colour.
        [Test] public static void Icons_badgeRing_isCutOut() {
            foreach (int size in new[] { 32, 48 })
            {
                float d = size * 0.5f, ring = Math.Max(1f, size / 16f);
                float cx = size - d / 2f, cy = size - d / 2f;
                var ringPixels = new System.Collections.Generic.List<Point>();
                using (Bitmap plain = Icons.Draw(size, null, Icons.Fill(true)))
                {
                    for (int y = 0; y < size; y++)
                        for (int x = 0; x < size; x++)
                        {
                            double r = Math.Sqrt((x + 0.5 - cx) * (x + 0.5 - cx) + (y + 0.5 - cy) * (y + 0.5 - cy));
                            if (r >= d / 2f + 0.75 && r <= d / 2f + ring - 0.75 && plain.GetPixel(x, y).A == 255) ringPixels.Add(new Point(x, y));
                        }
                }
                Assert.True(ringPixels.Count >= 4, size + " px: ring pixels over the horse: " + ringPixels.Count);
                foreach (TrayColor c in new[] { TrayColor.Green, TrayColor.Yellow, TrayColor.Red, TrayColor.Gray })
                {
                    using (Bitmap b = Icons.Draw(size, c, Icons.Fill(true)))
                    {
                        foreach (Point p in ringPixels)
                            Assert.Equal(0, (int)b.GetPixel(p.X, p.Y).A, size + " px " + c + ": ring pixel " + p);
                        Color centre = b.GetPixel((int)cx, (int)cy);
                        if (c != TrayColor.Gray) Assert.Equal(255, (int)centre.A, size + " px " + c + ": the badge is solid");
                    }
                }
            }
        }

        [Test] public static void Icons_exe_whiteOutlineFrom32px_noBadge() {
            foreach (int size in new[] { 16, 24, 32, 48, 256 })
            {
                int white = 0, opaque = 0, coloured = 0;
                using (Bitmap b = Icons.DrawExe(size))
                {
                    Assert.Equal(size, b.Width, "width");
                    for (int y = 0; y < size; y++)
                        for (int x = 0; x < size; x++)
                        {
                            Color c = b.GetPixel(x, y);
                            if (c.A != 255) continue;
                            opaque++;
                            if (c.R >= 250 && c.G >= 250 && c.B >= 250) white++;
                            int max = Math.Max(c.R, Math.Max(c.G, c.B)), min = Math.Min(c.R, Math.Min(c.G, c.B));
                            if (max - min > 80 || c.R > c.B + 8) coloured++;   // not navy, white or a blend of the two
                        }
                }
                Assert.True(opaque >= size * size / 4, size + " px: opaque " + opaque);
                if (size >= 32) Assert.True(white >= size / 2, size + " px: a white outline (" + white + " white pixels)");
                else Assert.Equal(0, white, size + " px: no outline");
                Assert.Equal(0, coloured, size + " px: no badge colours");
            }
        }
    }
}
