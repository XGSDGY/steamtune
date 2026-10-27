using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SteamTune
{
    /// <summary>专辑封面占位块：渐变底 + 播放时跳动的频谱条（独立控件，避免整窗高频重绘）</summary>
    internal sealed class ArtSquare : Control
    {
        private readonly Timer timer;
        private float phase;
        private bool playing;

        public bool Playing
        {
            get { return playing; }
            set
            {
                if (playing == value) return;
                playing = value;
                Invalidate();
            }
        }

        public ArtSquare()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.BgBottom;
            timer = new Timer();
            timer.Interval = 45;
            timer.Tick += OnTick;
            timer.Start();
        }

        private void OnTick(object sender, EventArgs e)
        {
            phase += 0.22f;
            if (phase > 628f) phase = 0f;
            if (playing) Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle bounds = new Rectangle(0, 0, Width - 1, Height - 1);

            using (GraphicsPath path = Theme.Round(bounds, Theme.S(16)))
            using (LinearGradientBrush brush = new LinearGradientBrush(bounds,
                       Color.FromArgb(38, 52, 92), Color.FromArgb(14, 26, 36), 55f))
            {
                g.FillPath(brush, path);
            }

            // 右上角冷色光晕
            Rectangle glow = new Rectangle(Width / 4, -Height / 3, Width, Height);
            using (GraphicsPath glowPath = new GraphicsPath())
            {
                glowPath.AddEllipse(glow);
                using (PathGradientBrush glowBrush = new PathGradientBrush(glowPath))
                {
                    glowBrush.CenterColor = Color.FromArgb(64, Theme.Accent);
                    glowBrush.SurroundColors = new Color[] { Color.FromArgb(0, Theme.Accent) };
                    g.FillPath(glowBrush, glowPath);
                }
            }

            int bars = 5;
            float barWidth = Theme.SF(5.5f);
            float gap = Theme.SF(4.5f);
            float totalWidth = bars * barWidth + (bars - 1) * gap;
            float left = (Width - totalWidth) / 2f;
            float baseLine = Height * 0.66f;

            for (int i = 0; i < bars; i++)
            {
                float wobble = (float)Math.Sin(phase + i * 0.85f);
                float height = playing ? Theme.SF(12f) + Math.Abs(wobble) * Theme.SF(30f) : Theme.SF(10f + i * 2f);
                float x = left + i * (barWidth + gap);
                RectangleF bar = new RectangleF(x, baseLine - height, barWidth, height);
                using (GraphicsPath barPath = Theme.Round(Rectangle.Round(bar), Theme.S(3)))
                using (LinearGradientBrush barBrush = new LinearGradientBrush(
                           Rectangle.Round(bar), Theme.Accent2, Theme.Accent, 90f))
                {
                    g.FillPath(barBrush, barPath);
                }
            }

            using (Pen pen = new Pen(Color.FromArgb(34, 255, 255, 255), 1f))
            using (GraphicsPath outline = Theme.Round(bounds, Theme.S(16)))
            {
                g.DrawPath(pen, outline);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && timer != null) timer.Dispose();
            base.Dispose(disposing);
        }
    }

    internal sealed class LogEntry
    {
        public long Ts;
        public string Level;
        public string Text;

        public LogEntry(string level, string text)
        {
            Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            Level = level;
            Text = text;
        }
    }
}
