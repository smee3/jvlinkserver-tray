using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace JVLinkServerTray
{
    // The pictures: a horse-head silhouette (facing left), with a status badge at the bottom right for the
    // tray. Pure drawing, no Windows state: TrayIcons picks the size and the fill, MakeIcon draws the EXE icon.
    public static class Icons
    {
        static readonly Color ExeFill = FromRgb(IconLook.FillOnLightTaskbar);
        static readonly Color GreenBadge = Color.FromArgb(0x1f, 0x9e, 0x4a);
        static readonly Color YellowBadge = Color.FromArgb(0xe3, 0xa6, 0x00);
        static readonly Color RedBadge = Color.FromArgb(0xc4, 0x2b, 0x1c);
        static readonly Color GrayBadge = Color.FromArgb(0x6e, 0x6e, 0x73);

        // The silhouette's bounding box in its 24 x 24 design box, and the eye.
        const float Left = 2.4f, Right = 19.4f, Top = 5.5f, Bottom = 22f;
        const float EyeX = 12.6f, EyeY = 10.4f, EyeR = 0.9f;
        public const int EyeFromSize = 24;      // smaller icons have no eye
        public const int OutlineFromSize = 32;  // the EXE icon's white outline

        public static Color Fill(bool lightTaskbar)
        {
            return FromRgb(IconLook.FillFor(lightTaskbar));
        }

        static Color FromRgb(int rgb)
        {
            return Color.FromArgb(255, (rgb >> 16) & 0xff, (rgb >> 8) & 0xff, rgb & 0xff);
        }

        // The only glyph. The silhouette's bounding box fitted into box (aspect ratio kept), centred
        // horizontally, standing on the box's bottom edge.
        public static GraphicsPath HorsePath(RectangleF box)
        {
            var p = new GraphicsPath();
            p.StartFigure();
            p.AddLine(At(box, 7f, 22f), At(box, 19f, 22f));
            p.AddBezier(At(box, 19f, 22f), At(box, 19.4f, 17.4f), At(box, 18.4f, 13.5f), At(box, 16f, 10.4f));
            p.AddLine(At(box, 16f, 10.4f), At(box, 16.9f, 5.5f));
            p.AddLine(At(box, 16.9f, 5.5f), At(box, 14.3f, 7.4f));
            p.AddLine(At(box, 14.3f, 7.4f), At(box, 12.7f, 6.0f));
            p.AddBezier(At(box, 12.7f, 6.0f), At(box, 9.2f, 7f), At(box, 6.4f, 9.6f), At(box, 4.6f, 12.8f));
            p.AddLine(At(box, 4.6f, 12.8f), At(box, 3.2f, 14.3f));
            p.AddBezier(At(box, 3.2f, 14.3f), At(box, 2.4f, 15.2f), At(box, 2.8f, 16.7f), At(box, 4.1f, 16.9f));
            p.AddLine(At(box, 4.1f, 16.9f), At(box, 6.7f, 16.6f));
            p.AddBezier(At(box, 6.7f, 16.6f), At(box, 7.7f, 16.5f), At(box, 8.6f, 17.2f), At(box, 8.3f, 18.2f));
            p.CloseFigure();
            return p;
        }

        public static PointF EyeCentre(RectangleF box)
        {
            return At(box, EyeX, EyeY);
        }

        static float Scale(RectangleF box)
        {
            return Math.Min(box.Width / (Right - Left), box.Height / (Bottom - Top));
        }

        // A point of the 24 x 24 design box, in the icon.
        static PointF At(RectangleF box, float x, float y)
        {
            float s = Scale(box);
            float left = box.X + (box.Width - (Right - Left) * s) / 2f;
            return new PointF(left + (x - Left) * s, box.Bottom - (Bottom - y) * s);
        }

        // The tray icon's glyph box: about a pixel of margin per 16 px, on whole pixels.
        public static RectangleF GlyphBox(int size)
        {
            return Box(size, Margin(size));
        }

        // The EXE icon's: room for the outline as well.
        public static RectangleF ExeGlyphBox(int size)
        {
            float outline = Outline(size);
            int m = Margin(size);
            if (outline > 0) m = Math.Max(m, (int)Math.Ceiling(outline) + 1);
            return Box(size, m);
        }

        static int Margin(int size)
        {
            return Math.Max(1, (int)Math.Round(size / 16.0, MidpointRounding.AwayFromZero));
        }

        static float Outline(int size)
        {
            return size >= OutlineFromSize ? size / 24f : 0f;
        }

        static RectangleF Box(int size, int margin)
        {
            return new RectangleF(margin, margin, size - 2 * margin, size - 2 * margin);
        }

        // The tray icon. badge null: the silhouette alone.
        public static Bitmap Draw(int size, TrayColor? badge, Color fill)
        {
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            RectangleF box = GlyphBox(size);
            using (Graphics g = Graphics.FromImage(bmp))
            using (GraphicsPath p = HorsePath(box))
            using (var brush = new SolidBrush(fill))
            {
                g.Clear(Color.Transparent);
                Smooth(g);
                g.FillPath(brush, p);
            }
            if (size >= EyeFromSize) CutEye(bmp, box);
            if (badge.HasValue) DrawBadge(bmp, badge.Value);
            return bmp;
        }

        // The EXE icon: the navy silhouette with a thin white outline from 32 px, so it reads on a dark
        // Explorer background too. No badge.
        public static Bitmap DrawExe(int size)
        {
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            RectangleF box = ExeGlyphBox(size);
            float outline = Outline(size);
            using (Graphics g = Graphics.FromImage(bmp))
            using (GraphicsPath p = HorsePath(box))
            using (var brush = new SolidBrush(ExeFill))
            {
                g.Clear(Color.Transparent);
                Smooth(g);
                if (outline > 0)
                {
                    using (var pen = new Pen(Color.White, outline * 2f))   // half of it is covered by the fill
                    {
                        pen.LineJoin = LineJoin.Round;
                        g.DrawPath(pen, p);
                    }
                }
                g.FillPath(brush, p);
            }
            if (size >= EyeFromSize) CutEye(bmp, box);
            return bmp;
        }

        static void CutEye(Bitmap bmp, RectangleF box)
        {
            PointF c = EyeCentre(box);
            CutCircle(bmp, c.X, c.Y, EyeR * Scale(box));
        }

        static void Smooth(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
        }

        // About half the icon, bottom right, with a transparent ring cut around it so the badge stands apart
        // from the horse.
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

        // Clears a disc (anti-aliased edge) so whatever is behind the icon shows through.
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
    }
}
