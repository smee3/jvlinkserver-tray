using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace JVLinkServerTray
{
    // The tray icon: the app tile with a status badge at the bottom right (cut out of the tile).
    // The four tray icons are drawn once at the system small-icon size and reused.
    public static class Icons
    {
        static readonly Color TileColor = Color.FromArgb(0x2a, 0x6c, 0xc0);
        static readonly Color GreenBadge = Color.FromArgb(0x1f, 0x9e, 0x4a);
        static readonly Color YellowBadge = Color.FromArgb(0xe3, 0xa6, 0x00);
        static readonly Color RedBadge = Color.FromArgb(0xc4, 0x2b, 0x1c);
        static readonly Color GrayBadge = Color.FromArgb(0x6e, 0x6e, 0x73);

        static Icon[] _icons;
        static IntPtr[] _handles;

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool DestroyIcon(IntPtr handle);

        // UI thread only. Falls back to the stock application icon if drawing fails.
        public static Icon For(TrayColor c)
        {
            try
            {
                if (_icons == null) Build();
                return _icons[(int)c];
            }
            catch (Exception)
            {
                return SystemIcons.Application;
            }
        }

        public static void DisposeAll()
        {
            if (_icons == null) return;
            for (int i = 0; i < _icons.Length; i++)
            {
                try { if (_icons[i] != null) _icons[i].Dispose(); } catch (Exception) { }
                try { if (_handles[i] != IntPtr.Zero) DestroyIcon(_handles[i]); } catch (Exception) { }
            }
            _icons = null;
            _handles = null;
        }

        static void Build()
        {
            int size = SmallIconSize();
            var icons = new Icon[4];
            var handles = new IntPtr[4];
            try
            {
                for (int i = 0; i < 4; i++)
                {
                    using (Bitmap b = Draw(size, (TrayColor)i))
                    {
                        handles[i] = b.GetHicon();
                        icons[i] = Icon.FromHandle(handles[i]);   // does not own the handle: DestroyIcon in DisposeAll
                    }
                }
            }
            catch (Exception)
            {
                for (int i = 0; i < 4; i++)
                {
                    if (icons[i] != null) icons[i].Dispose();
                    if (handles[i] != IntPtr.Zero) DestroyIcon(handles[i]);
                }
                throw;
            }
            _icons = icons;
            _handles = handles;
        }

        static int SmallIconSize()
        {
            try
            {
                Size s = SystemInformation.SmallIconSize;
                int n = Math.Max(s.Width, s.Height);
                if (n >= 16 && n <= 256) return n;
            }
            catch (Exception)
            {
            }
            return 16;
        }

        // badge null: the tile alone, centred (the EXE icon). Otherwise the tray icon for that colour.
        public static Bitmap Draw(int size, TrayColor? badge)
        {
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            float s = size;
            float tile = s * 0.84f;
            float off = badge.HasValue ? 0f : (s - tile) / 2f;
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                Smooth(g);
                DrawGlyph(g, new RectangleF(off, off, tile, tile), size);
            }
            if (badge.HasValue) DrawBadge(bmp, badge.Value);
            return bmp;
        }

        static void Smooth(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
        }

        // The app glyph: a blue rounded tile with a white bold "JV". All glyph drawing lives here so a
        // more detailed version for large sizes can replace it later; for now it is the same at every size.
        static void DrawGlyph(Graphics g, RectangleF tile, int iconSize)
        {
            using (GraphicsPath p = RoundRect(tile, tile.Width * 0.22f))
            using (var brush = new SolidBrush(TileColor))
            {
                g.FillPath(brush, p);
            }

            float em = tile.Width * 0.5f;
            using (FontFamily family = TextFamily())
            using (var font = new Font(family, em, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var white = new SolidBrush(Color.White))
            using (var fmt = (StringFormat)StringFormat.GenericTypographic.Clone())
            {
                // Place by the letters' ink, not by font metrics: "J" then "V" with a small gap, the pair
                // centred in the tile.
                RectangleF j = InkBounds("J", family, em, fmt);
                RectangleF v = InkBounds("V", family, em, fmt);
                float gap = em * 0.05f;
                float width = j.Width + gap + v.Width;
                float top = Math.Min(j.Top, v.Top);
                float height = Math.Max(j.Bottom, v.Bottom) - top;
                float left = tile.X + (tile.Width - width) / 2f;
                float y = tile.Y + (tile.Height - height) / 2f - top;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.DrawString("J", font, white, left - j.Left, y, fmt);
                g.DrawString("V", font, white, left + j.Width + gap - v.Left, y, fmt);
            }
        }

        static FontFamily TextFamily()
        {
            foreach (string name in new[] { "Segoe UI", "Arial" })
            {
                try
                {
                    var f = new FontFamily(name);
                    if (f.IsStyleAvailable(FontStyle.Bold)) return f;
                    f.Dispose();
                }
                catch (ArgumentException)
                {
                }
            }
            return new FontFamily(GenericFontFamilies.SansSerif);
        }

        static RectangleF InkBounds(string text, FontFamily family, float em, StringFormat fmt)
        {
            using (var path = new GraphicsPath())
            {
                path.AddString(text, family, (int)FontStyle.Bold, em, PointF.Empty, fmt);
                RectangleF b = path.GetBounds();
                if (b.Width <= 0 || b.Height <= 0) return new RectangleF(0, 0, em * 0.5f, em * 0.7f);
                return b;
            }
        }

        static void DrawBadge(Bitmap bmp, TrayColor color)
        {
            float s = bmp.Width;
            float d = s * 0.5f;
            float ring = Math.Max(1f, s / 16f);
            float cx = s - d / 2f, cy = s - d / 2f;
            CutCircle(bmp, cx, cy, d / 2f + ring);

            using (Graphics g = Graphics.FromImage(bmp))
            {
                Smooth(g);
                var box = new RectangleF(s - d, s - d, d, d);
                switch (color)
                {
                    case TrayColor.Green:
                        Fill(g, GreenBadge, box);
                        break;
                    case TrayColor.Yellow:
                        Fill(g, YellowBadge, box);
                        break;
                    case TrayColor.Red:
                        Fill(g, RedBadge, box);
                        RectangleF bar = Snap(new RectangleF(cx - d * 0.28f, cy - d * 0.09f, d * 0.56f, d * 0.18f), bmp.Width <= 32);
                        using (var white = new SolidBrush(Color.White)) g.FillRectangle(white, bar);
                        break;
                    default:
                        float stroke = d * 0.24f;
                        using (var pen = new Pen(GrayBadge, stroke))
                        {
                            g.DrawEllipse(pen, box.X + stroke / 2f, box.Y + stroke / 2f, d - stroke, d - stroke);
                        }
                        break;
                }
            }
        }

        static void Fill(Graphics g, Color c, RectangleF box)
        {
            using (var b = new SolidBrush(c)) g.FillEllipse(b, box);
        }

        // Clears a disc (anti-aliased edge) so whatever is behind the icon shows through around the badge.
        static void CutCircle(Bitmap bmp, float cx, float cy, float r)
        {
            int x0 = Math.Max(0, (int)Math.Floor(cx - r - 1)), y0 = Math.Max(0, (int)Math.Floor(cy - r - 1));
            int x1 = Math.Min(bmp.Width - 1, (int)Math.Ceiling(cx + r + 1)), y1 = Math.Min(bmp.Height - 1, (int)Math.Ceiling(cy + r + 1));
            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    double dx = x + 0.5 - cx, dy = y + 0.5 - cy;
                    double cover = r - Math.Sqrt(dx * dx + dy * dy) + 0.5;
                    if (cover <= 0) continue;
                    if (cover > 1) cover = 1;
                    Color c = bmp.GetPixel(x, y);
                    if (c.A == 0) continue;
                    bmp.SetPixel(x, y, Color.FromArgb((int)Math.Round(c.A * (1 - cover)), c.R, c.G, c.B));
                }
            }
        }

        // Small icons: put the edges on whole pixels so a thin bar stays sharp.
        static RectangleF Snap(RectangleF r, bool snap)
        {
            if (!snap) return r;
            float left = (float)Math.Round(r.Left), right = (float)Math.Round(r.Right);
            float top = (float)Math.Round(r.Top), bottom = (float)Math.Round(r.Bottom);
            if (right <= left) right = left + 1;
            if (bottom <= top) bottom = top + 1;
            return RectangleF.FromLTRB(left, top, right, bottom);
        }

        static GraphicsPath RoundRect(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float dia = Math.Min(radius * 2f, Math.Min(r.Width, r.Height));
            if (dia <= 0.01f)
            {
                p.AddRectangle(r);
                return p;
            }
            p.AddArc(r.X, r.Y, dia, dia, 180, 90);
            p.AddArc(r.Right - dia, r.Y, dia, dia, 270, 90);
            p.AddArc(r.Right - dia, r.Bottom - dia, dia, dia, 0, 90);
            p.AddArc(r.X, r.Bottom - dia, dia, dia, 90, 90);
            p.CloseFigure();
            return p;
        }
    }
}
