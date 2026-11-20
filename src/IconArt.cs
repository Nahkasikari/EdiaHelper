// Drawing of the program icon and the tray icons.
//
// Program icon: red OBD WiFi dongle (lower left) and a USB plug with cable (upper right)
// on a rounded blue tile.
// Tray icon: the dongle alone. Grey with a WiFi symbol = no adapter connected,
// red with a letter = connected (K = K-line, D = D-CAN, E = ENET, C = cable, I = ICOM).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

static class IconArt
{
    static readonly Color Dark = Color.FromArgb(30, 30, 36);
    static readonly Color Red = Color.FromArgb(214, 32, 40);
    static readonly Color RedDark = Color.FromArgb(150, 18, 24);
    static readonly Color Grey = Color.FromArgb(150, 154, 162);
    static readonly Color GreyDark = Color.FromArgb(100, 104, 112);
    static readonly Color Metal = Color.FromArgb(200, 206, 214);

    static GraphicsPath Rounded(RectangleF r, float rad)
    {
        GraphicsPath p = new GraphicsPath();
        float d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    // Dongle in a 128 x 136 box at (x, y): body on top, OBD connector below.
    // letter == null draws the WiFi symbol instead of a letter.
    static void DrawDongle(Graphics g, float x, float y, bool active, string letter, bool small)
    {
        Color top = active ? Red : Grey, bottom = active ? RedDark : GreyDark;

        // OBD connector: dark shell with a full rim around a recessed face with pins
        using (GraphicsPath shell = new GraphicsPath())
        {
            shell.AddPolygon(new[] { new PointF(x + 4, y + 82), new PointF(x + 124, y + 82), new PointF(x + 112, y + 134), new PointF(x + 16, y + 134) });
            using (Pen round = new Pen(Dark, 8)) { round.LineJoin = LineJoin.Round; g.DrawPath(round, shell); }
            using (SolidBrush db = new SolidBrush(Dark)) g.FillPath(db, shell);
        }
        if (!small)
        {
            using (GraphicsPath recess = new GraphicsPath())
            {
                recess.AddPolygon(new[] { new PointF(x + 16, y + 91), new PointF(x + 112, y + 91), new PointF(x + 103, y + 126), new PointF(x + 25, y + 126) });
                using (Pen round = new Pen(Color.FromArgb(70, 72, 82), 6)) { round.LineJoin = LineJoin.Round; g.DrawPath(round, recess); }
                using (SolidBrush rb = new SolidBrush(Color.FromArgb(70, 72, 82))) g.FillPath(rb, recess);
            }
            using (SolidBrush pin = new SolidBrush(Color.FromArgb(225, 190, 80)))
            {
                for (int i = 0; i < 8; i++) g.FillRectangle(pin, x + 24 + i * 10.6f, y + 95, 5, 10);
                for (int i = 0; i < 8; i++) g.FillRectangle(pin, x + 29 + i * 9.6f, y + 111, 5, 10);
            }
        }

        // Body, ending above the connector so the connector's upper edge stays visible
        using (GraphicsPath body = Rounded(new RectangleF(x, y, 128, 76), 22))
        using (LinearGradientBrush bb = new LinearGradientBrush(new PointF(0, y), new PointF(0, y + 76), top, bottom))
            g.FillPath(bb, body);

        if (letter != null)
        {
            using (Font f = new Font("Segoe UI", 64, FontStyle.Bold, GraphicsUnit.Pixel))
            using (StringFormat sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            using (SolidBrush wb = new SolidBrush(Color.White))
                g.DrawString(letter, f, wb, new RectangleF(x, y + 2, 128, 76), sf);
            return;
        }

        using (Pen wave = new Pen(Color.White, small ? 15 : 11))
        {
            wave.StartCap = LineCap.Round; wave.EndCap = LineCap.Round;
            if (small)
                g.DrawArc(wave, x + 32, y + 16, 64, 52, 220, 100);
            else
            {
                g.DrawArc(wave, x + 20, y + 10, 88, 64, 222, 96);
                g.DrawArc(wave, x + 40, y + 30, 48, 36, 222, 96);
            }
        }
        using (SolidBrush dot = new SolidBrush(Color.White))
            g.FillEllipse(dot, x + (small ? 53 : 55), y + (small ? 50 : 54), small ? 22 : 18, small ? 22 : 18);
    }

    public static Bitmap DrawAppIcon(int size)
    {
        Bitmap b = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(b))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);
            g.ScaleTransform(size / 256f, size / 256f);
            bool small = size <= 24;

            using (GraphicsPath tile = Rounded(new RectangleF(8, 8, 240, 240), 44))
            using (LinearGradientBrush bg = new LinearGradientBrush(new PointF(0, 8), new PointF(0, 248), Color.FromArgb(40, 110, 200), Color.FromArgb(18, 58, 120)))
                g.FillPath(bg, tile);

            // USB plug and cable, upper right
            using (Pen cable = new Pen(Color.FromArgb(225, 230, 240), small ? 16 : 12))
            {
                cable.StartCap = LineCap.Round; cable.EndCap = LineCap.Round;
                g.DrawBezier(cable, new PointF(196, 86), new PointF(196, 50), new PointF(220, 44), new PointF(232, 22));
            }
            using (GraphicsPath body = Rounded(new RectangleF(176, 78, 40, 40), 8))
            using (SolidBrush wb = new SolidBrush(Color.White)) g.FillPath(wb, body);
            using (SolidBrush mb = new SolidBrush(Metal)) g.FillRectangle(mb, 181, 116, 30, small ? 30 : 34);
            if (!small)
            {
                using (SolidBrush hole = new SolidBrush(Color.FromArgb(90, 96, 106)))
                {
                    g.FillRectangle(hole, 187, 124, 7, 8);
                    g.FillRectangle(hole, 198, 124, 7, 8);
                }
            }

            DrawDongle(g, 26, 104, true, null, small);
        }
        return b;
    }

    // Tray version: a taller body so the letter stays readable at 16 px,
    // and a plain connector strip underneath.
    public static Bitmap DrawTray(int size, bool active, string letter)
    {
        Bitmap b = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(b))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);
            g.ScaleTransform(size / 128f, size / 128f);
            Color top = active ? Red : Grey, bottom = active ? RedDark : GreyDark;

            using (GraphicsPath shell = new GraphicsPath())
            {
                shell.AddPolygon(new[] { new PointF(10, 98), new PointF(118, 98), new PointF(108, 124), new PointF(20, 124) });
                using (Pen round = new Pen(Dark, 6)) { round.LineJoin = LineJoin.Round; g.DrawPath(round, shell); }
                using (SolidBrush db = new SolidBrush(Dark)) g.FillPath(db, shell);
            }
            using (GraphicsPath body = Rounded(new RectangleF(2, 2, 124, 92), 20))
            using (LinearGradientBrush bb = new LinearGradientBrush(new PointF(0, 2), new PointF(0, 94), top, bottom))
                g.FillPath(bb, body);

            if (active && letter != null)
            {
                using (Font f = new Font("Segoe UI", 92, FontStyle.Bold, GraphicsUnit.Pixel))
                using (StringFormat sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                using (SolidBrush wb = new SolidBrush(Color.White))
                    g.DrawString(letter, f, wb, new RectangleF(2, 4, 124, 92), sf);
            }
            else
            {
                using (Pen wave = new Pen(Color.White, 14))
                {
                    wave.StartCap = LineCap.Round; wave.EndCap = LineCap.Round;
                    g.DrawArc(wave, 22, 16, 84, 62, 222, 96);
                    g.DrawArc(wave, 42, 38, 44, 34, 222, 96);
                }
                using (SolidBrush dot = new SolidBrush(Color.White)) g.FillEllipse(dot, 53, 62, 22, 22);
            }
        }
        return b;
    }

    public static Icon TrayIcon(bool active, string letter)
    {
        using (Bitmap bm = DrawTray(32, active, letter))
        {
            // Clone() makes an icon with its own handle; the temporary handle from GetHicon must be
            // released, otherwise one Windows icon handle is lost on every tray icon change
            IntPtr h = bm.GetHicon();
            try { using (Icon tmp = Icon.FromHandle(h)) return (Icon)tmp.Clone(); }
            finally { DestroyIcon(h); }
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr handle);

    public static void WriteIco(string path, Func<int, Bitmap> draw)
    {
        int[] sizes = { 16, 24, 32, 48, 64, 128, 256 };
        List<byte[]> pngs = new List<byte[]>();
        foreach (int sz in sizes)
            using (Bitmap bm = draw(sz))
            using (MemoryStream ms = new MemoryStream())
            {
                bm.Save(ms, ImageFormat.Png);
                pngs.Add(ms.ToArray());
            }
        using (BinaryWriter w = new BinaryWriter(File.Create(path)))
        {
            w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                w.Write((byte)0); w.Write((byte)0);
                w.Write((short)1); w.Write((short)32);
                w.Write(pngs[i].Length); w.Write(offset);
                offset += pngs[i].Length;
            }
            foreach (byte[] p in pngs) w.Write(p);
        }
    }
}
