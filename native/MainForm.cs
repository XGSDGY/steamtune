using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using Timer = System.Windows.Forms.Timer;

namespace SteamTune
{
    internal sealed class MainForm : Form
    {
        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);

        private readonly Config config;
        private readonly bool previewMode;
        private readonly SteamNative steam = new SteamNative();
        private readonly Detector detector;
        private readonly PresenceController presence;
        private readonly ArtSquare art = new ArtSquare();
        private readonly List<LogEntry> logs = new List<LogEntry>();
        private readonly System.Windows.Forms.Timer steamTimer;
        private readonly System.Windows.Forms.Timer clockTimer;
        private readonly NotifyIcon tray;
        private readonly Dictionary<string, Rectangle> zones = new Dictionary<string, Rectangle>();

        private Icon appIcon;
        private Detection detection = Detection.Idle();
        private string hoverZone = string.Empty;
        private string pressedZone = string.Empty;
        private string lastLoggedPreview = string.Empty;
        private bool dragging;
        private Point dragOffset;
        private bool hiddenToTray;
        private bool startHidden;
        private bool exitRequested;
        private EventWaitHandle showEvent;
        private ToolStripMenuItem pauseItem;
        private ToolStripMenuItem autoStartItem;

        private Rectangle rHeader, rMin, rClose, rBanner, rNow, rFriends, rSteamCard, rSettings, rLogs;
        private Rectangle rArt, rCopy, rRepublish, rOpen, rQuit;
        private Rectangle rToggleEnabled, rTogglePaused, rSegment;
        private Rectangle rModeChip;

        public MainForm(Config config, bool previewMode, bool startHidden)
        {
            this.config = config;
            this.previewMode = previewMode;
            this.startHidden = startHidden;
            Theme.Scale = DeviceDpi / 96f;

            Text = "SteamTune";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Theme.BgBottom;
            DoubleBuffered = true;
            AutoScaleMode = AutoScaleMode.None;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Size = new Size(Theme.S(1080), Theme.S(760));
            MinimumSize = Size;
            MaximumSize = Size;
            KeyPreview = true;

            try
            {
                appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (appIcon != null) Icon = appIcon;
            }
            catch
            {
            }

            art.Bounds = new Rectangle(0, 0, Theme.S(124), Theme.S(124));
            Controls.Add(art);

            if (!previewMode)
            {
                steam.Initialize(ResolveAppId(), AppDomain.CurrentDomain.BaseDirectory);
            }
            else
            {
                SetSteamError("--preview：本次运行不发布到 Steam");
                steam.AppId = ResolveAppId();
            }

            Localization.Load();
            presence = new PresenceController(steam, config);
            presence.Updated += OnPresenceChanged;

            detector = new Detector(config);
            detector.DetectionArrived += OnDetection;

            steamTimer = new Timer();
            steamTimer.Interval = 250;
            steamTimer.Tick += delegate
            {
                steam.Tick();
                LogSteamSidePresence();
            };
            steamTimer.Start();

            clockTimer = new Timer();
            clockTimer.Interval = 2000;
            clockTimer.Tick += delegate { Invalidate(rFriends); };
            clockTimer.Start();

            tray = new NotifyIcon();
            tray.Icon = Icon ?? SystemIcons.Application;
            tray.Text = "SteamTune";
            tray.Visible = true;
            tray.DoubleClick += delegate { RestoreWindow(); };

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("显示主界面", null, delegate { RestoreWindow(); });
            pauseItem = new ToolStripMenuItem("暂停同步");
            pauseItem.Click += delegate { ToggleSync(); };
            menu.Items.Add(pauseItem);
            menu.Items.Add("设置 AppID…", null, delegate { PromptAppId(); });
            menu.Items.Add("打开 Steamworks 后台", null, delegate { OpenSteamworks(); });
            menu.Items.Add("本地化文件位置", null, delegate { RevealLocalizationFile(); });
            autoStartItem = new ToolStripMenuItem("开机自启");
            autoStartItem.Click += delegate { SetAutoStart(!IsAutoStartEnabled()); };
            menu.Items.Add(autoStartItem);
            menu.Items.Add("打开配置目录", null, delegate { OpenConfigDir(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, delegate { ExitApplication(); });
            menu.Opening += delegate
            {
                pauseItem.Checked = !config.presence.enabled;
                autoStartItem.Checked = IsAutoStartEnabled();
                tray.Text = "SteamTune · " + (config.presence.enabled ? "同步中" : "已暂停");
            };
            tray.ContextMenuStrip = menu;

            AddLog("info", previewMode ? "以预览模式启动（不写入 Steam 好友状态）" : "Steamworks 初始化" +
                   (steam.Available ? "成功：" + (steam.PersonaName ?? "已登录") : "失败：" + (steam.Error ?? "未知原因")));
            if (steam.Available && steam.AppId == 480)
            {
                AddLog("warn", "AppID 480（Spacewar）没有本应用的 rich presence 模板，好友只会看到 In-Game 名称");
            }

            Load += delegate
            {
                LayoutUi();
                ApplyRoundedRegion();
                if (appIcon != null) Invalidate();
            };
            Resize += delegate
            {
                LayoutUi();
                ApplyRoundedRegion();
            };
            Shown += delegate
            {
                detector.Start();
                presence.Apply(detection);
                WatchShowSignal();
                if (startHidden)
                {
                    HideToTray(false);
                }
                else
                {
                    BeginInvoke(new Action(ShowFirstRunNotice));
                }
            };
        }

        private void SetSteamError(string message)
        {
            // 预览模式没有真实的 steam 错误，界面上用这条文案说明原因
            field_error = message;
        }

        private string field_error = null;

        private int ResolveAppId()
        {
            string env = Environment.GetEnvironmentVariable("SteamAppId");
            int parsed;
            if (!string.IsNullOrEmpty(env) && int.TryParse(env, out parsed) && parsed > 0) return parsed;

            env = Environment.GetEnvironmentVariable("STEAMTUNE_APPID");
            if (!string.IsNullOrEmpty(env) && int.TryParse(env, out parsed) && parsed > 0) return parsed;

            if (config.steam != null && config.steam.appId > 0) return config.steam.appId;

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string[] candidates = new string[]
            {
                Path.Combine(baseDir, "steam_appid.txt"),
                Path.Combine(Directory.GetCurrentDirectory(), "steam_appid.txt"),
            };
            for (int i = 0; i < candidates.Length; i++)
            {
                try
                {
                    if (!File.Exists(candidates[i])) continue;
                    string text = File.ReadAllText(candidates[i]).Trim();
                    string first = text.Split(new char[] { ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)[0];
                    if (int.TryParse(first, out parsed) && parsed > 0) return parsed;
                }
                catch
                {
                }
            }
            return 0;
        }

        private void ApplyRoundedRegion()
        {
            try
            {
                Region = Region.FromHrgn(CreateRoundRectRgn(0, 0, Width + 1, Height + 1, Theme.S(20), Theme.S(20)));
            }
            catch
            {
            }
        }

        private bool ShowBanner
        {
            get { return !steam.Available; }
        }

        private void LayoutUi()
        {
            int m = Theme.S(24);
            int width = ClientSize.Width;
            int top = ShowBanner ? Theme.S(122) : Theme.S(76);
            int leftWidth = Theme.S(616);
            int rightX = m + leftWidth + Theme.S(16);
            int rightWidth = Math.Max(Theme.S(320), width - rightX - m);

            rHeader = new Rectangle(0, 0, width, Theme.S(64));
            rBanner = new Rectangle(m, Theme.S(66), width - m * 2, Theme.S(40));

            rModeChip = new Rectangle(width - m - Theme.S(28) * 2 - Theme.S(8) - Theme.S(16) - Theme.S(128), Theme.S(20),
                Theme.S(128), Theme.S(26));
            rMin = new Rectangle(width - m - Theme.S(28) * 2 - Theme.S(8), Theme.S(16), Theme.S(28), Theme.S(28));
            rClose = new Rectangle(width - m - Theme.S(28), Theme.S(16), Theme.S(28), Theme.S(28));

            rNow = new Rectangle(m, top, leftWidth, Theme.S(190));
            rFriends = new Rectangle(m, top + Theme.S(206), leftWidth, Theme.S(150));
            rSteamCard = new Rectangle(m, top + Theme.S(372), leftWidth, Theme.S(172));
            rSettings = new Rectangle(rightX, top, rightWidth, Theme.S(268));
            int logsTop = top + Theme.S(284);
            rLogs = new Rectangle(rightX, logsTop, rightWidth, Math.Max(Theme.S(180), ClientSize.Height - logsTop - m));

            rArt = new Rectangle(rNow.X + Theme.S(20), rNow.Y + Theme.S(28), Theme.S(124), Theme.S(124));
            art.Bounds = rArt;

            rCopy = new Rectangle(rFriends.Right - Theme.S(20) - Theme.S(60), rFriends.Y + Theme.S(12), Theme.S(60), Theme.S(26));

            rRepublish = new Rectangle(rSteamCard.X + Theme.S(20), rSteamCard.Y + Theme.S(104), Theme.S(96), Theme.S(34));
            rOpen = new Rectangle(rRepublish.Right + Theme.S(10), rSteamCard.Y + Theme.S(104), Theme.S(132), Theme.S(34));
            rQuit = new Rectangle(rOpen.Right + Theme.S(10), rSteamCard.Y + Theme.S(104), Theme.S(72), Theme.S(34));

            rToggleEnabled = new Rectangle(rSettings.X + Theme.S(20), rSettings.Y + Theme.S(48), Theme.S(42), Theme.S(23));
            rTogglePaused = new Rectangle(rSettings.X + Theme.S(20), rSettings.Y + Theme.S(90), Theme.S(42), Theme.S(23));
            rSegment = new Rectangle(rSettings.X + Theme.S(20), rSettings.Y + Theme.S(148), rSettings.Width - Theme.S(40), Theme.S(40));

            zones.Clear();
            zones["min"] = rMin;
            zones["close"] = rClose;
            zones["copy"] = rCopy;
            zones["republish"] = rRepublish;
            zones["open"] = rOpen;
            zones["quit"] = rQuit;
            zones["hint"] = new Rectangle(rSteamCard.X + Theme.S(20), rSteamCard.Bottom - Theme.S(28),
                rSteamCard.Width - Theme.S(40), Theme.S(22));
            zones["toggle-enabled"] = new Rectangle(rToggleEnabled.X - Theme.S(4), rToggleEnabled.Y - Theme.S(8),
                Theme.S(200), Theme.S(40));
            zones["toggle-paused"] = new Rectangle(rTogglePaused.X - Theme.S(4), rTogglePaused.Y - Theme.S(8),
                Theme.S(220), Theme.S(40));

            int segWidth = rSegment.Width / 3;
            zones["seg-withPlayer"] = new Rectangle(rSegment.X, rSegment.Y, segWidth, rSegment.Height);
            zones["seg-artist"] = new Rectangle(rSegment.X + segWidth, rSegment.Y, segWidth, rSegment.Height);
            zones["seg-title"] = new Rectangle(rSegment.X + segWidth * 2, rSegment.Y, rSegment.Width - segWidth * 2, rSegment.Height);

            Invalidate();
        }

        // ---------- 数据 ----------

        private void OnDetection(Detection value)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try
            {
                BeginInvoke(new Action<Detection>(ApplyDetection), value);
            }
            catch
            {
            }
        }

        private void ApplyDetection(Detection value)
        {
            bool playerChanged = value.Player != detection.Player;
            detection = value;
            art.Playing = value.State == "playing";
            if (playerChanged && value.Player.Length > 0)
            {
                AddLog("info", "识别到播放器：" + value.Player +
                               (value.LowConfidence ? "（窗口标题推断）" : string.Empty));
            }
            presence.Apply(detection);
            Invalidate();
        }

        private void OnPresenceChanged()
        {
            if (IsDisposed || !IsHandleCreated) return;
            if (InvokeRequired)
            {
                try
                {
                    BeginInvoke(new Action(OnPresenceChanged));
                }
                catch
                {
                }
                return;
            }
            if (presence.Preview != lastLoggedPreview)
            {
                lastLoggedPreview = presence.Preview;
                AddLog("presence", presence.Preview.Length == 0 ? "已清除好友状态" : "好友看到 " + presence.Preview);
            }
            Invalidate();
        }

        private void AddLog(string level, string text)
        {
            logs.Add(new LogEntry(level, text));
            if (logs.Count > 200) logs.RemoveAt(0);
            Invalidate(rLogs);
        }

        private int lastPresenceLogCount = -1;

        /// <summary>发布后用 GetFriendRichPresence 读回自己账号，确认 Steam 真的收到了</summary>
        private void LogSteamSidePresence()
        {
            if (!steam.Available) return;
            if (presence.PublishedCount == lastPresenceLogCount) return;
            lastPresenceLogCount = presence.PublishedCount;

            try
            {
                Dictionary<string, string> map = steam.ReadOwnRichPresence();
                if (map.Count == 0)
                {
                    AddLog("info", "Steam 侧未返回自己的 presence（部分客户端只对好友返回），发布调用已成功");
                    return;
                }
                StringBuilder builder = new StringBuilder();
                foreach (KeyValuePair<string, string> pair in map)
                {
                    if (builder.Length > 0) builder.Append("  ");
                    builder.Append(pair.Key).Append('=').Append(pair.Value);
                }
                AddLog("info", "Steam 侧已收到：" + builder.ToString());
                Diag.Log("steam presence: " + builder.ToString());

                string display;
                if (!map.TryGetValue("steam_display", out display) || string.IsNullOrEmpty(display))
                {
                    AddLog("warn", "Steam 没有保存 steam_display：当前 AppID 没有这些模板，好友只会看到 In-Game 名称");
                }
            }
            catch
            {
            }
        }

        // ---------- 绘制 ----------

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Rectangle bounds = ClientRectangle;
            using (LinearGradientBrush brush = new LinearGradientBrush(bounds, Theme.BgTop, Theme.BgBottom, 90f))
            {
                g.FillRectangle(brush, bounds);
            }
            Rectangle glow = new Rectangle(-Width / 3, -Height / 2, Width, Height);
            using (GraphicsPath path = new GraphicsPath())
            {
                path.AddEllipse(glow);
                using (PathGradientBrush brush = new PathGradientBrush(path))
                {
                    brush.CenterColor = Color.FromArgb(30, Theme.Accent);
                    brush.SurroundColors = new Color[] { Color.FromArgb(0, Theme.Accent) };
                    g.FillPath(brush, path);
                }
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            DrawHeader(g);
            if (ShowBanner) DrawBanner(g);
            DrawNowCard(g);
            DrawFriendsCard(g);
            DrawSteamCard(g);
            DrawSettingsCard(g);
            DrawLogsCard(g);
            DrawFooter(g);
        }

        private void DrawFooter(Graphics g)
        {
            string version = typeof(MainForm).Assembly.GetName().Version.ToString(3);
            Theme.DrawText(g, "SteamTune v" + version, Theme.Tiny, Theme.Faint,
                new Rectangle(Theme.S(24), Height - Theme.S(28), Theme.S(140), Theme.S(18)),
                TextFormatFlags.Left | TextFormatFlags.SingleLine);
            Theme.DrawText(g, Config.FilePath, Theme.Tiny, Theme.Faint,
                new Rectangle(Theme.S(150), Height - Theme.S(28), Width - Theme.S(174), Theme.S(18)),
                TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        }

        private void DrawHeader(Graphics g)
        {
            if (appIcon != null)
            {
                Rectangle icon = new Rectangle(Theme.S(24), Theme.S(14), Theme.S(36), Theme.S(36));
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(appIcon.ToBitmap(), icon);
            }

            Theme.DrawText(g, "SteamTune", Theme.H1, Theme.Text,
                new Rectangle(Theme.S(72), Theme.S(8), Theme.S(300), Theme.S(30)),
                TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter);
            Theme.DrawText(g, "把正在听的歌放进 Steam 好友列表", Theme.Small, Theme.Muted,
                new Rectangle(Theme.S(74), Theme.S(36), Theme.S(360), Theme.S(20)),
                TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter);

            bool online = steam.Available;
            Color chipFore = online ? Color.FromArgb(157, 243, 204) : Color.FromArgb(255, 217, 154);
            Color chipFill = online ? Color.FromArgb(34, 61, 220, 151) : Color.FromArgb(30, 255, 200, 107);
            Color chipBorder = online ? Color.FromArgb(72, 61, 220, 151) : Color.FromArgb(72, 255, 200, 107);
            Theme.Chip(g, rModeChip, online ? "Steam 已连接" : "预览模式", chipFore, chipFill, chipBorder);

            DrawWindowButton(g, rMin, "min", false);
            DrawWindowButton(g, rClose, "close", true);
        }

        private void DrawWindowButton(Graphics g, Rectangle rect, string zone, bool close)
        {
            bool hover = hoverZone == zone;
            if (hover)
            {
                Theme.FillRound(g, rect, Theme.S(6),
                    close ? Color.FromArgb(46, 255, 107, 122) : Color.FromArgb(20, 255, 255, 255));
            }
            Color color = close && hover ? Color.FromArgb(255, 177, 184) : Theme.Muted;
            using (Pen pen = new Pen(color, 1.4f))
            {
                if (close)
                {
                    int pad = Theme.S(10);
                    g.DrawLine(pen, rect.X + pad, rect.Y + pad, rect.Right - pad, rect.Bottom - pad);
                    g.DrawLine(pen, rect.Right - pad, rect.Y + pad, rect.X + pad, rect.Bottom - pad);
                }
                else
                {
                    int pad = Theme.S(10);
                    g.DrawLine(pen, rect.X + pad, rect.Y + rect.Height / 2, rect.Right - pad, rect.Y + rect.Height / 2);
                }
            }
        }

        private void DrawBanner(Graphics g)
        {
            Theme.FillRound(g, rBanner, Theme.S(10), Color.FromArgb(24, 255, 200, 107));
            Theme.StrokeRound(g, rBanner, Theme.S(10), Color.FromArgb(56, 255, 200, 107), 1f);
            Theme.DrawText(g, "预览模式", Theme.BodyBold, Color.FromArgb(255, 217, 154),
                new Rectangle(rBanner.X + Theme.S(14), rBanner.Y, Theme.S(84), rBanner.Height),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);

            string message = field_error ?? steam.Error ?? "SteamAPI 未就绪，好友列表不会更新。";
            Theme.DrawText(g, message, Theme.Body, Color.FromArgb(255, 226, 183),
                new Rectangle(rBanner.X + Theme.S(100), rBanner.Y, rBanner.Width - Theme.S(116), rBanner.Height),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        }

        private void DrawNowCard(Graphics g)
        {
            Theme.Card(g, rNow);

            int textX = rArt.Right + Theme.S(22);
            int textWidth = rNow.Right - textX - Theme.S(20);

            bool playing = detection.State == "playing";
            bool paused = detection.State == "paused";
            Color dot = playing ? Theme.Ok : paused ? Theme.Warn : Color.FromArgb(74, 81, 99);

            Rectangle dotRect = new Rectangle(textX, rNow.Y + Theme.S(32), Theme.S(7), Theme.S(7));
            using (SolidBrush brush = new SolidBrush(dot))
            {
                g.FillEllipse(brush, dotRect);
            }

            string stateText = playing ? "正在播放" : paused ? "已暂停" : "没有正在播放的媒体";
            Theme.DrawText(g, stateText, Theme.Small, Theme.Muted,
                new Rectangle(textX + Theme.S(14), rNow.Y + Theme.S(24), textWidth - Theme.S(14), Theme.S(20)),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);

            string title = detection.Title.Length > 0 ? detection.Title : "等待播放器";
            Theme.DrawText(g, title, Theme.Title, Theme.Text,
                new Rectangle(textX, rNow.Y + Theme.S(44), textWidth, Theme.S(40)),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);

            string artist = detection.Artist.Length > 0 ? detection.Artist :
                            (detection.State == "idle" ? "打开任意音乐播放器开始播放" : "未知歌手");
            Theme.DrawText(g, artist, Theme.Body, Color.FromArgb(185, 194, 216),
                new Rectangle(textX, rNow.Y + Theme.S(86), textWidth, Theme.S(22)),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);

            int chipX = textX;
            int chipY = rNow.Y + Theme.S(116);
            string playerText = detection.Player.Length > 0 ? "来自 " + detection.Player : "未识别播放器";
            int playerWidth = TextRenderer.MeasureText(playerText, Theme.Small).Width + Theme.S(20);
            Rectangle playerChip = new Rectangle(chipX, chipY, playerWidth, Theme.S(24));
            Theme.Chip(g, playerChip, playerText,
                detection.Player.Length > 0 ? Color.FromArgb(195, 208, 255) : Theme.Muted,
                detection.Player.Length > 0 ? Color.FromArgb(36, 124, 156, 255) : Color.FromArgb(13, 255, 255, 255),
                detection.Player.Length > 0 ? Color.FromArgb(56, 124, 156, 255) : Color.FromArgb(20, 255, 255, 255));

            if (detection.LowConfidence)
            {
                string sourceText = "窗口标题推断";
                int sourceWidth = TextRenderer.MeasureText(sourceText, Theme.Small).Width + Theme.S(20);
                Rectangle sourceChip = new Rectangle(playerChip.Right + Theme.S(8), chipY, sourceWidth, Theme.S(24));
                Theme.Chip(g, sourceChip, sourceText, Theme.Muted,
                    Color.FromArgb(13, 255, 255, 255), Color.FromArgb(20, 255, 255, 255));
            }
        }

        private void DrawFriendsCard(Graphics g)
        {
            Theme.Card(g, rFriends);
            Theme.DrawText(g, "好友列表里看到的", Theme.BodyBold, Theme.Muted,
                new Rectangle(rFriends.X + Theme.S(20), rFriends.Y + Theme.S(16), Theme.S(240), Theme.S(22)),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);

            DrawButton(g, rCopy, "复制", false, "copy");

            Rectangle result = new Rectangle(rFriends.X + Theme.S(20), rFriends.Y + Theme.S(52), rFriends.Width - Theme.S(40), Theme.S(46));
            Theme.FillRound(g, result, Theme.S(10), Color.FromArgb(150, 6, 9, 16));
            Theme.StrokeRound(g, result, Theme.S(10), Color.FromArgb(18, 255, 255, 255), 1f);

            Rectangle label = new Rectangle(result.X + Theme.S(12), result.Y + Theme.S(13), Theme.S(56), Theme.S(20));
            Theme.FillRound(g, label, Theme.S(5), Color.FromArgb(14, 255, 255, 255));
            Theme.DrawText(g, "正在玩", Theme.Tiny, Theme.Muted, label,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);

            string preview = presence.Preview.Length > 0 ? presence.Preview : "（好友列表不显示播放状态）";
            bool missingTemplate = steam.Available && steam.AppId == 480;
            string mainText = missingTemplate ? "Spacewar（In-Game）" : preview;
            Color mainColor = missingTemplate
                ? Color.FromArgb(255, 200, 107)
                : preview.Length > 0 ? Theme.Text : Color.FromArgb(110, 118, 136);
            Theme.DrawText(g, mainText, Theme.BodyBold, mainColor,
                new Rectangle(label.Right + Theme.S(12), result.Y, result.Width - Theme.S(96), result.Height),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);

            string reason = missingTemplate
                ? "AppID 480 没有本应用的模板，好友看不到歌名 · 当前歌曲：" + preview
                : presence.Reason + (presence.ChangedAt > 0 ? " · " + Theme.Ago(presence.ChangedAt) : string.Empty);
            Theme.DrawText(g, reason, Theme.Tiny, missingTemplate ? Color.FromArgb(255, 200, 107) : Theme.Faint,
                new Rectangle(rFriends.X + Theme.S(20), rFriends.Y + Theme.S(106), rFriends.Width - Theme.S(40), Theme.S(18)),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);

            if (presence.DisplayToken.Length > 0 && !missingTemplate)
            {
                Theme.DrawText(g, presence.DisplayToken, Theme.MonoSmall, Theme.Faint,
                    new Rectangle(rFriends.Right - Theme.S(240), rFriends.Y + Theme.S(106), Theme.S(220), Theme.S(18)),
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            }
        }

        private void DrawSteamCard(Graphics g)
        {
            Theme.Card(g, rSteamCard);
            Theme.DrawText(g, "STEAM", Theme.BodyBold, Theme.Muted,
                new Rectangle(rSteamCard.X + Theme.S(20), rSteamCard.Y + Theme.S(16), Theme.S(120), Theme.S(22)),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);

            string status = steam.Available ? "API 正常" : "API 未就绪";
            int chipWidth = TextRenderer.MeasureText(status, Theme.Small).Width + Theme.S(24);
            Rectangle statusChip = new Rectangle(rSteamCard.Right - Theme.S(20) - chipWidth, rSteamCard.Y + Theme.S(12), chipWidth, Theme.S(24));
            Theme.Chip(g, statusChip, status,
                steam.Available ? Color.FromArgb(157, 243, 204) : Color.FromArgb(255, 177, 184),
                steam.Available ? Color.FromArgb(30, 61, 220, 151) : Color.FromArgb(30, 255, 107, 122),
                steam.Available ? Color.FromArgb(64, 61, 220, 151) : Color.FromArgb(64, 255, 107, 122));

            int columnWidth = (rSteamCard.Width - Theme.S(40)) / 3;
            DrawKeyValue(g, rSteamCard.X + Theme.S(20), rSteamCard.Y + Theme.S(52), columnWidth, "账号",
                steam.Available ? (steam.PersonaName ?? "—") : "—");
            DrawKeyValue(g, rSteamCard.X + Theme.S(20) + columnWidth, rSteamCard.Y + Theme.S(52), columnWidth, "AppID",
                steam.AppId > 0 ? steam.AppId.ToString() : "未设置");
            DrawKeyValue(g, rSteamCard.X + Theme.S(20) + columnWidth * 2, rSteamCard.Y + Theme.S(52), columnWidth, "状态",
                steam.Available ? "已连接 Steam" : "仅本地预览");

            DrawButton(g, rRepublish, "重新发布", true, "republish");
            DrawButton(g, rOpen, "打开配置目录", false, "open");
            DrawButton(g, rQuit, "退出", false, "quit");

            string hint = CurrentHint();
            if (!string.IsNullOrEmpty(hint))
            {
                Theme.DrawText(g, hint, Theme.Tiny, Color.FromArgb(166, 186, 255),
                    new Rectangle(rSteamCard.X + Theme.S(20), rSteamCard.Bottom - Theme.S(26),
                        rSteamCard.Width - Theme.S(40), Theme.S(18)),
                    TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            }
        }

        /// <summary>需要提示用户“为什么好友看不到歌名”时返回文案</summary>
        private string CurrentHint()
        {
            if (!steam.Available) return null;
            if (steam.AppId == 480) return "AppID 480 仅联调：好友只看到 In-Game 名称 · 点这里了解怎么显示歌名 ›";
            if (!presence.LastPublishOk) return "Steam 拒绝了 SetRichPresence，请确认 AppID 属于当前账号 ›";
            return null;
        }

        private void DrawKeyValue(Graphics g, int x, int y, int width, string label, string value)
        {
            Theme.DrawText(g, label, Theme.Tiny, Theme.Faint,
                new Rectangle(x, y, width - Theme.S(8), Theme.S(16)),
                TextFormatFlags.Left | TextFormatFlags.SingleLine);
            Theme.DrawText(g, Theme.Fit(value, Theme.Body, width - Theme.S(8)), Theme.Body, Theme.Text,
                new Rectangle(x, y + Theme.S(16), width - Theme.S(8), Theme.S(20)),
                TextFormatFlags.Left | TextFormatFlags.SingleLine);
        }

        private void DrawSettingsCard(Graphics g)
        {
            Theme.Card(g, rSettings);
            Theme.DrawText(g, "同步设置", Theme.BodyBold, Theme.Muted,
                new Rectangle(rSettings.X + Theme.S(20), rSettings.Y + Theme.S(16), Theme.S(200), Theme.S(22)),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);

            DrawToggle(g, rToggleEnabled, config.presence.enabled, "启用同步", "toggle-enabled");
            DrawToggle(g, rTogglePaused, config.presence.onPaused == "keep", "暂停时保留上一首", "toggle-paused");

            Theme.DrawText(g, "显示样式", Theme.Tiny, Theme.Faint,
                new Rectangle(rSettings.X + Theme.S(20), rSettings.Y + Theme.S(128), Theme.S(200), Theme.S(18)),
                TextFormatFlags.Left | TextFormatFlags.SingleLine);

            DrawSegments(g);

            Theme.DrawText(g, "文字格式由 Steamworks 后台的 rich presence 本地化决定，这里选择用哪个模板。",
                Theme.Tiny, Theme.Faint,
                new Rectangle(rSettings.X + Theme.S(20), rSettings.Y + Theme.S(200), rSettings.Width - Theme.S(40), Theme.S(52)),
                TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak);
        }

        private void DrawToggle(Graphics g, Rectangle track, bool on, string label, string zone)
        {
            bool hover = hoverZone == zone;

            Theme.FillRound(g, track, track.Height / 2,
                on ? Color.FromArgb(255, 124, 156, 255) : Color.FromArgb(26, 255, 255, 255));
            if (on)
            {
                using (LinearGradientBrush brush = new LinearGradientBrush(track, Theme.Accent, Theme.Accent2, 20f))
                using (GraphicsPath path = Theme.Round(track, track.Height / 2))
                {
                    g.FillPath(brush, path);
                }
            }

            if (hover)
            {
                Theme.StrokeRound(g, track, track.Height / 2, Color.FromArgb(90, 255, 255, 255), 1.2f);
            }

            int knob = track.Height - Theme.S(6);
            int knobX = on ? track.Right - knob - Theme.S(3) : track.X + Theme.S(3);
            Rectangle knobRect = new Rectangle(knobX, track.Y + Theme.S(3), knob, knob);
            using (SolidBrush brush = new SolidBrush(on ? Theme.Ink : Color.FromArgb(207, 214, 230)))
            {
                g.FillEllipse(brush, knobRect);
            }

            Theme.DrawText(g, label, Theme.Body, hover ? Color.FromArgb(255, 255, 255) : Theme.Text,
                new Rectangle(track.Right + Theme.S(12), track.Y - Theme.S(4), Theme.S(220), track.Height + Theme.S(8)),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }

        private void DrawSegments(Graphics g)
        {
            Theme.FillRound(g, rSegment, Theme.S(10), Color.FromArgb(10, 255, 255, 255));
            Theme.StrokeRound(g, rSegment, Theme.S(10), Color.FromArgb(18, 255, 255, 255), 1f);

            string[] keys = new string[] { "withPlayer", "artist", "title" };
            string[] labels = new string[] { "含播放器", "歌 + 歌手", "只有歌名" };
            string[] zoneNames = new string[] { "seg-withPlayer", "seg-artist", "seg-title" };

            int segWidth = rSegment.Width / 3;
            for (int i = 0; i < 3; i++)
            {
                Rectangle seg = new Rectangle(rSegment.X + segWidth * i, rSegment.Y,
                    i == 2 ? rSegment.Width - segWidth * 2 : segWidth, rSegment.Height);
                bool selected = config.presence.style == keys[i];
                bool hover = hoverZone == zoneNames[i];

                Rectangle inner = new Rectangle(seg.X + Theme.S(3), seg.Y + Theme.S(3), seg.Width - Theme.S(6), seg.Height - Theme.S(6));
                if (selected)
                {
                    Theme.FillRound(g, inner, Theme.S(8), Color.FromArgb(46, 124, 156, 255));
                    Theme.StrokeRound(g, inner, Theme.S(8), Color.FromArgb(86, 124, 156, 255), 1f);
                }
                else if (hover)
                {
                    Theme.FillRound(g, inner, Theme.S(8), Color.FromArgb(14, 255, 255, 255));
                }

                Theme.DrawText(g, labels[i], selected ? Theme.BodyBold : Theme.Body,
                    selected ? Color.FromArgb(222, 230, 255) : Theme.Muted, inner,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            }
        }

        private void DrawLogsCard(Graphics g)
        {
            Theme.Card(g, rLogs);
            Theme.DrawText(g, "日志", Theme.BodyBold, Theme.Muted,
                new Rectangle(rLogs.X + Theme.S(20), rLogs.Y + Theme.S(16), Theme.S(120), Theme.S(22)),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            Theme.DrawText(g, "最近 " + Math.Min(40, Math.Max(1, logs.Count)).ToString() + " 条", Theme.Tiny, Theme.Faint,
                new Rectangle(rLogs.Right - Theme.S(120), rLogs.Y + Theme.S(18), Theme.S(100), Theme.S(18)),
                TextFormatFlags.Right | TextFormatFlags.SingleLine);

            int lineHeight = Theme.S(18);
            int startY = rLogs.Y + Theme.S(44);
            int maxLines = Math.Max(1, (rLogs.Bottom - Theme.S(16) - startY) / lineHeight);
            int count = Math.Min(maxLines, logs.Count);
            if (count == 0)
            {
                Theme.DrawText(g, "等待播放器…", Theme.MonoSmall, Theme.Faint,
                    new Rectangle(rLogs.X + Theme.S(20), startY, rLogs.Width - Theme.S(40), lineHeight),
                    TextFormatFlags.Left | TextFormatFlags.SingleLine);
                return;
            }

            for (int i = 0; i < count; i++)
            {
                LogEntry entry = logs[logs.Count - 1 - i];
                int y = startY + i * lineHeight;
                Color color = entry.Level == "error" ? Theme.Bad :
                              entry.Level == "warn" ? Theme.Warn : Color.FromArgb(174, 183, 203);
                Theme.DrawText(g, Theme.Clock(entry.Ts), Theme.MonoSmall, Theme.Faint,
                    new Rectangle(rLogs.X + Theme.S(20), y, Theme.S(58), lineHeight),
                    TextFormatFlags.Left | TextFormatFlags.SingleLine);
                Theme.DrawText(g, entry.Text, Theme.MonoSmall, color,
                    new Rectangle(rLogs.X + Theme.S(82), y, rLogs.Width - Theme.S(102), lineHeight),
                    TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            }
        }

        private void DrawButton(Graphics g, Rectangle rect, string text, bool primary, string zone)
        {
            bool hover = hoverZone == zone;
            bool pressed = pressedZone == zone;

            Rectangle draw = pressed ? new Rectangle(rect.X, rect.Y + 1, rect.Width, rect.Height) : rect;

            if (primary)
            {
                using (LinearGradientBrush brush = new LinearGradientBrush(draw, Theme.Accent, Theme.Accent2, 20f))
                using (GraphicsPath path = Theme.Round(draw, Theme.S(8)))
                {
                    g.FillPath(brush, path);
                }
                Theme.DrawText(g, text, Theme.BodyBold, Theme.Ink, draw,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
                return;
            }

            Theme.FillRound(g, draw, Theme.S(8), Color.FromArgb(hover ? 22 : 10, 255, 255, 255));
            Theme.StrokeRound(g, draw, Theme.S(8), Color.FromArgb(22, 255, 255, 255), 1f);
            Theme.DrawText(g, text, Theme.Body, Theme.Text, draw,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }

        // ---------- 交互 ----------

        private string HitTest(Point point)
        {
            foreach (KeyValuePair<string, Rectangle> pair in zones)
            {
                if (!pair.Value.Contains(point)) continue;
                if (pair.Key == "hint" && CurrentHint() == null) return string.Empty;
                return pair.Key;
            }
            return string.Empty;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            if (dragging)
            {
                Point screen = PointToScreen(e.Location);
                Location = new Point(screen.X - dragOffset.X, screen.Y - dragOffset.Y);
                return;
            }

            string zone = HitTest(e.Location);
            if (zone != hoverZone)
            {
                hoverZone = zone;
                Cursor = zone.Length > 0 ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;

            string zone = HitTest(e.Location);
            if (zone.Length == 0 && e.Y < rHeader.Height)
            {
                dragging = true;
                dragOffset = PointToScreen(e.Location);
                dragOffset.Offset(-Location.X, -Location.Y);
                return;
            }
            pressedZone = zone;
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;

            if (dragging)
            {
                dragging = false;
                return;
            }

            string zone = HitTest(e.Location);
            string pressed = pressedZone;
            pressedZone = string.Empty;
            Invalidate();

            if (zone.Length == 0 || zone != pressed) return;
            Execute(zone);
        }

        private void Execute(string zone)
        {
            Diag.Log("Execute zone=" + zone);
            switch (zone)
            {
                case "min":
                    HideToTray(true);
                    break;
                case "close":
                    ExitApplication();
                    break;
                case "copy":
                    if (presence.Preview.Length > 0) Clipboard.SetText(presence.Preview);
                    AddLog("info", "已复制好友可见文案");
                    break;
                case "republish":
                    presence.Republish();
                    AddLog("info", "手动重新发布");
                    break;
                case "open":
                    OpenConfigDir();
                    break;
                case "quit":
                    ExitApplication();
                    break;
                case "hint":
                    ShowPresenceHelp();
                    break;
                case "toggle-enabled":
                    ToggleSync();
                    break;
                case "toggle-paused":
                    config.presence.onPaused = config.presence.onPaused == "keep" ? "clear" : "keep";
                    config.Save();
                    presence.SetConfig(config);
                    break;
                case "seg-withPlayer":
                    SetStyle("withPlayer");
                    break;
                case "seg-artist":
                    SetStyle("artist");
                    break;
                case "seg-title":
                    SetStyle("title");
                    break;
            }
        }

        private void SetStyle(string style)
        {
            if (config.presence.style == style) return;
            config.presence.style = style;
            config.Save();
            presence.SetConfig(config);
        }

        private void OpenConfigDir()
        {
            try
            {
                Directory.CreateDirectory(Config.Directory);
                Process.Start("explorer.exe", "\"" + Config.Directory + "\"");
            }
            catch
            {
            }
        }

        private static string LocalizationFilePath()
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "richpresence_localization.json");
        }

        private void OpenSteamworks()
        {
            try
            {
                Process.Start("https://partner.steamgames.com/apps/landing/");
            }
            catch
            {
            }
        }

        private void RevealLocalizationFile()
        {
            try
            {
                string path = LocalizationFilePath();
                if (File.Exists(path))
                {
                    Process.Start("explorer.exe", "/select,\"" + path + "\"");
                }
                else
                {
                    MessageBox.Show(this, "找不到 richpresence_localization.json，请重新构建一次。",
                        "SteamTune", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            catch
            {
            }
        }

        private void ShowPresenceHelp()
        {
            string message =
                "好友列表里要显示「正在听 …」，需要 Steam 认识这个应用的显示模板：\r\n\r\n" +
                "1. 在 Steamworks 建好应用，拿到自己的 AppID。\r\n" +
                "   partner.steamgames.com → Your Apps → Create New App（每款应用 $100 Steam Direct）。\r\n" +
                "   现在用的是 480（Spacewar），它没有我们的模板，所以好友只能看到 In-Game: Spacewar。\r\n\r\n" +
                "2. Steamworks 后台 → Community → Rich Presence，上传这个文件：\r\n" +
                "   " + LocalizationFilePath() + "\r\n" +
                "   然后点 Publish（只保存不发布会一直不显示）。\r\n\r\n" +
                "3. 托盘菜单 →「设置 AppID…」填进去（或写进 steam_appid.txt），重启程序即可。\r\n\r\n" +
                "详细步骤见 docs\\PUBLISHING.md。";
            MessageBox.Show(this, message, "如何让好友看到歌名", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        /// <summary>480 联调模式首次启动时讲清楚“为什么没有歌名”</summary>
        private void ShowFirstRunNotice()
        {
            if (!steam.Available || steam.AppId != 480) return;
            if (config.window != null && config.window.seenAppId480Notice) return;

            if (config.window != null)
            {
                config.window.seenAppId480Notice = true;
                config.Save();
            }

            string message =
                "现在用的是 AppID 480（Spacewar），好友列表里只会显示 In-Game: Spacewar，不会有歌名。\r\n\r\n" +
                "Steam 的这行文字必须由应用自己的 rich presence 模板渲染：\r\n" +
                "1. 在 Steamworks 建应用，拿到你的 AppID\r\n" +
                "2. 后台上传 richpresence_localization.json 并点 Publish\r\n" +
                "3. 点「设置 AppID…」填进去\r\n\r\n" +
                "要现在填写 AppID 吗？";

            DialogResult result = MessageBox.Show(this, message, "SteamTune · 联调说明",
                MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (result == DialogResult.Yes) PromptAppId();
        }

        private void PromptAppId()
        {
            int current = steam.AppId > 0 ? steam.AppId : (config.steam != null ? config.steam.appId : 0);
            using (InputDialog dialog = new InputDialog("设置 AppID", "Steamworks AppID（数字，例如 3123450）",
                       current > 0 ? current.ToString() : string.Empty,
                       "在 partner.steamgames.com 的 Your Apps 里创建应用后生成；" +
                       "480 是 Valve 的测试应用，只能显示 Spacewar，不会有歌名。"))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                int id;
                if (!int.TryParse(dialog.Value, out id) || id <= 0)
                {
                    MessageBox.Show(this, "AppID 必须是正整数。", "SteamTune",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                ApplyAppId(id);
            }
        }

        private void ApplyAppId(int id)
        {
            config.steam.appId = id;
            config.Save();

            try
            {
                presence.ClearAll();
            }
            catch
            {
            }

            steam.Dispose();
            bool ok = !previewMode && steam.Initialize(id, AppDomain.CurrentDomain.BaseDirectory);
            presence.SetConfig(config);
            AddLog(ok ? "info" : "warn",
                ok
                    ? "AppID 已切换为 " + id + "，Steam 连接正常"
                    : "AppID 已切换为 " + id + "，但 Steam 初始化失败：" + (steam.Error ?? "未知原因"));
            Invalidate();
        }

        private void HideToTray(bool balloon)
        {
            hiddenToTray = true;
            // 注意：这里不要动 ShowInTaskbar，它会重建窗口句柄，在关闭流程里会把窗口真的关掉
            Hide();
            if (balloon && tray != null)
            {
                try
                {
                    tray.ShowBalloonTip(1500, "SteamTune", "已收进右下角托盘，播放状态仍在同步。", ToolTipIcon.Info);
                }
                catch
                {
                }
            }
        }

        private void RestoreWindow()
        {
            if (IsDisposed) return;
            if (!hiddenToTray && Visible)
            {
                Activate();
                BringToFront();
                return;
            }
            hiddenToTray = false;
            if (!Visible) Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            BringToFront();
        }

        /// <summary>第二个实例启动时通知本实例把窗口弹出来</summary>
        private void WatchShowSignal()
        {
            try
            {
                showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "SteamTune.ShowWindow");
            }
            catch
            {
                return;
            }

            Thread thread = new Thread(delegate()
            {
                while (!IsDisposed)
                {
                    if (!showEvent.WaitOne(1000)) continue;
                    if (IsDisposed) return;
                    try
                    {
                        BeginInvoke(new Action(RestoreWindow));
                    }
                    catch
                    {
                        return;
                    }
                }
            });
            thread.IsBackground = true;
            thread.Name = "SteamTune.ShowWatcher";
            thread.Start();
        }

        private void ToggleSync()
        {
            config.presence.enabled = !config.presence.enabled;
            config.Save();
            presence.SetConfig(config);
            AddLog("info", config.presence.enabled ? "已启用同步" : "已暂停同步");
            Invalidate();
        }

        private void ExitApplication()
        {
            exitRequested = true;
            Close();
        }

        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValueName = "SteamTune";

        private static bool IsAutoStartEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath))
                {
                    return key != null && key.GetValue(RunValueName) != null;
                }
            }
            catch
            {
                return false;
            }
        }

        private void SetAutoStart(bool enabled)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
                {
                    if (key == null) return;
                    if (enabled)
                    {
                        key.SetValue(RunValueName, "\"" + Application.ExecutablePath + "\" --tray");
                    }
                    else
                    {
                        key.DeleteValue(RunValueName, false);
                    }
                }
                AddLog("info", enabled ? "已设置开机自启（启动后驻留托盘）" : "已取消开机自启");
            }
            catch (Exception ex)
            {
                AddLog("warn", "设置开机自启失败：" + ex.Message);
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode != Keys.Escape) return;
            if (config.window != null && config.window.closeToTray) HideToTray(true);
            else ExitApplication();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            bool closeToTray = config.window != null && config.window.closeToTray;
            Diag.Log("FormClosing reason=" + e.CloseReason + " closeToTray=" + closeToTray + " exitRequested=" + exitRequested);

            // 只有显式配置 closeToTray 时才把窗口关闭解释为“收进托盘”
            if (!exitRequested && closeToTray && (e.CloseReason == CloseReason.UserClosing || e.CloseReason == CloseReason.TaskManagerClosing))
            {
                e.Cancel = true;
                HideToTray(true);
                return;
            }
            ShutdownCore();
            base.OnFormClosing(e);
        }

        private bool shutdownDone;

        /// <summary>退出时务必清空 rich presence 并 Shutdown，否则 Steam 会短时间保留“正在玩”</summary>
        private void ShutdownCore()
        {
            if (shutdownDone) return;
            shutdownDone = true;
            Diag.Log("ShutdownCore");
            try
            {
                presence.ClearAll();
            }
            catch
            {
            }
            try
            {
                detector.Dispose();
            }
            catch
            {
            }
            try
            {
                presence.Dispose();
            }
            catch
            {
            }
            try
            {
                steam.Dispose();
            }
            catch
            {
            }
            try
            {
                steamTimer.Dispose();
            }
            catch
            {
            }
            try
            {
                clockTimer.Dispose();
            }
            catch
            {
            }
            if (tray != null)
            {
                tray.Visible = false;
                tray.Dispose();
            }
            if (appIcon != null) appIcon.Dispose();
        }

        /// <summary>进程被系统结束（注销/关机/任务管理器）时也要收尾</summary>
        public void EmergencyShutdown()
        {
            ShutdownCore();
        }
    }
}
