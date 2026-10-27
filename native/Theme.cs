using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace SteamTune
{
    internal static class Theme
    {
        public static readonly Color BgTop = Color.FromArgb(13, 17, 28);
        public static readonly Color BgBottom = Color.FromArgb(6, 8, 13);
        public static readonly Color CardFill = Color.FromArgb(176, 20, 24, 36);
        public static readonly Color CardBorder = Color.FromArgb(28, 255, 255, 255);
        public static readonly Color Text = Color.FromArgb(232, 236, 247);
        public static readonly Color Muted = Color.FromArgb(139, 147, 167);
        public static readonly Color Faint = Color.FromArgb(92, 101, 118);
        public static readonly Color Accent = Color.FromArgb(124, 156, 255);
        public static readonly Color Accent2 = Color.FromArgb(72, 224, 200);
        public static readonly Color Ok = Color.FromArgb(61, 220, 151);
        public static readonly Color Warn = Color.FromArgb(255, 200, 107);
        public static readonly Color Bad = Color.FromArgb(255, 107, 122);
        public static readonly Color Ink = Color.FromArgb(10, 13, 20);

        public static float Scale = 1f;

        public static readonly Font H1 = new Font("Microsoft YaHei UI", 16f, FontStyle.Bold, GraphicsUnit.Point);
        public static readonly Font Title = new Font("Microsoft YaHei UI", 20f, FontStyle.Bold, GraphicsUnit.Point);
        public static readonly Font Body = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Regular, GraphicsUnit.Point);
        public static readonly Font BodyBold = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold, GraphicsUnit.Point);
        public static readonly Font Small = new Font("Microsoft YaHei UI", 8.25f, FontStyle.Regular, GraphicsUnit.Point);
        public static readonly Font Tiny = new Font("Microsoft YaHei UI", 7.5f, FontStyle.Regular, GraphicsUnit.Point);
        public static readonly Font Mono = new Font("Consolas", 8.75f, FontStyle.Regular, GraphicsUnit.Point);
        public static readonly Font MonoSmall = new Font("Consolas", 8f, FontStyle.Regular, GraphicsUnit.Point);

        public static int S(int value)
        {
            return (int)Math.Round(value * Scale);
        }

        public static float SF(float value)
        {
            return value * Scale;
        }

        public static GraphicsPath Round(Rectangle r, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            if (radius <= 0)
            {
                path.AddRectangle(r);
                return path;
            }
            int d = Math.Min(Math.Min(radius * 2, r.Width), r.Height);
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        public static void FillRound(Graphics g, Rectangle r, int radius, Color fill)
        {
            using (GraphicsPath path = Round(r, radius))
            using (SolidBrush brush = new SolidBrush(fill))
            {
                g.FillPath(brush, path);
            }
        }

        public static void StrokeRound(Graphics g, Rectangle r, int radius, Color color, float width)
        {
            using (GraphicsPath path = Round(r, radius))
            using (Pen pen = new Pen(color, width))
            {
                g.DrawPath(pen, path);
            }
        }

        public static void Card(Graphics g, Rectangle r)
        {
            FillRound(g, r, S(14), CardFill);
            StrokeRound(g, r, S(14), CardBorder, 1f);
        }

        public static Color Mix(Color a, Color b, float t)
        {
            if (t < 0f) t = 0f;
            if (t > 1f) t = 1f;
            return Color.FromArgb(
                (int)(a.A + (b.A - a.A) * t),
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }

        public static LinearGradientBrush AccentBrush(Rectangle r, float angle)
        {
            LinearGradientBrush brush = new LinearGradientBrush(r, Accent, Accent2, angle);
            return brush;
        }

        public static void DrawText(Graphics g, string text, Font font, Color color, Rectangle rect, TextFormatFlags flags)
        {
            TextRenderer.DrawText(g, text ?? string.Empty, font, rect, color, flags | TextFormatFlags.NoPadding);
        }

        public static void Chip(Graphics g, Rectangle r, string text, Color foreground, Color fill, Color border)
        {
            FillRound(g, r, r.Height / 2, fill);
            StrokeRound(g, r, r.Height / 2, border, 1f);
            TextFormatFlags flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                                    TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis;
            DrawText(g, text, Small, foreground, r, flags);
        }

        public static string Fit(string text, Font font, int width)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            if (TextRenderer.MeasureText(text, font).Width <= width) return text;
            string cut = text;
            while (cut.Length > 1 && TextRenderer.MeasureText(cut + "…", font).Width > width)
            {
                cut = cut.Substring(0, cut.Length - 1);
            }
            return cut + "…";
        }

        public static string Clock(long unixMs)
        {
            DateTime time = DateTimeOffset.FromUnixTimeMilliseconds(unixMs).LocalDateTime;
            return time.ToString("HH:mm:ss");
        }

        public static string Ago(long unixMs)
        {
            if (unixMs <= 0) return string.Empty;
            double seconds = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - unixMs) / 1000.0;
            if (seconds < 2) return "刚刚";
            if (seconds < 60) return ((int)seconds) + " 秒前";
            int minutes = (int)(seconds / 60);
            if (minutes < 60) return minutes + " 分钟前";
            return (minutes / 60) + " 小时前";
        }
    }
}
