using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace SteamTune
{
    internal sealed class Config
    {
        public SteamSection steam = new SteamSection();
        public PresenceSection presence = new PresenceSection();
        public DetectSection detect = new DetectSection();
        public WindowSection window = new WindowSection();

        public static string Directory
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SteamTune"); }
        }

        public static string FilePath
        {
            get { return Path.Combine(Directory, "config.json"); }
        }

        /// <summary>AppData 不可写时退回到 exe 同目录（便携模式）</summary>
        private static string PortablePath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json"); }
        }

        public static Config Load()
        {
            string[] candidates = new string[] { FilePath, PortablePath };
            for (int i = 0; i < candidates.Length; i++)
            {
                try
                {
                    if (!File.Exists(candidates[i])) continue;
                    string json = File.ReadAllText(candidates[i]);
                    JavaScriptSerializer serializer = new JavaScriptSerializer();
                    Config cfg = serializer.Deserialize<Config>(json);
                    if (cfg == null) continue;
                    if (cfg.presence == null) cfg.presence = new PresenceSection();
                    if (cfg.detect == null) cfg.detect = new DetectSection();
                    if (cfg.steam == null) cfg.steam = new SteamSection();
                    if (cfg.window == null) cfg.window = new WindowSection();
                    cfg.presence.Normalize();
                    cfg.detect.Normalize();
                    return cfg;
                }
                catch
                {
                    // 损坏就试下一个位置
                }
            }
            return new Config();
        }

        public void Save()
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            string json = serializer.Serialize(this);
            string[] targets = new string[] { FilePath, PortablePath };
            for (int i = 0; i < targets.Length; i++)
            {
                try
                {
                    System.IO.Directory.CreateDirectory(Path.GetDirectoryName(targets[i]));
                    File.WriteAllText(targets[i], json);
                    return;
                }
                catch
                {
                    // 换下一个可写位置
                }
            }
        }
    }

    internal sealed class SteamSection
    {
        public int appId = 0;
    }

    internal sealed class WindowSection
    {
        // 点 × 时是否收进托盘。默认 false：关窗口 = 退出并清空 Steam 状态，避免“退了还显示在线”
        public bool closeToTray = false;
        // 启动后直接待在托盘里（配合开机自启）
        public bool startInTray = false;
        // 480 联调提示是否已经弹过
        public bool seenAppId480Notice = false;
    }

    internal sealed class PresenceSection
    {
        public bool enabled = true;
        public string style = "withPlayer";
        public string onPaused = "keep";
        public string onIdle = "clear";
        public int maxTokenLength = 100;
        public int minPublishIntervalMs = 3000;

        // 仅用于联调：强行指定 steam_display 的模板名（例如借用某个免费游戏的模板验证链路）
        public string displayToken = string.Empty;
        // 配合 displayToken 的额外占位符
        public Dictionary<string, string> extraTokens = new Dictionary<string, string>();

        public void Normalize()
        {
            if (style != "withPlayer" && style != "artist" && style != "title") style = "withPlayer";
            if (onPaused != "keep" && onPaused != "clear") onPaused = "keep";
            if (onIdle != "keep" && onIdle != "clear") onIdle = "clear";
            if (maxTokenLength <= 0 || maxTokenLength > 100) maxTokenLength = 100;
            if (minPublishIntervalMs < 0) minPublishIntervalMs = 3000;
            if (displayToken == null) displayToken = string.Empty;
            if (extraTokens == null) extraTokens = new Dictionary<string, string>();
        }
    }

    internal sealed class DetectSection
    {
        public int intervalMs = 1500;
        public List<string> blacklist = new List<string>(new string[] { "douyin.exe" });
        public bool allowWindowTitleFallback = true;

        public void Normalize()
        {
            if (intervalMs < 500) intervalMs = 1500;
            if (blacklist == null) blacklist = new List<string>();
        }
    }

    internal sealed class SessionDto
    {
        public string aumid { get; set; }
        public string status { get; set; }
        public string title { get; set; }
        public string artist { get; set; }
        public string album { get; set; }
        public long updated { get; set; }
    }

    internal sealed class ForegroundDto
    {
        public string title { get; set; }
        public string process { get; set; }
    }

    internal sealed class PayloadDto
    {
        public long ts { get; set; }
        public List<SessionDto> sessions { get; set; }
        public ForegroundDto foreground { get; set; }
        public string error { get; set; }
    }

    internal sealed class Detection
    {
        public string State = "idle";
        public string Title = string.Empty;
        public string Artist = string.Empty;
        public string Album = string.Empty;
        public string Player = string.Empty;
        public string PlayerKind = string.Empty;
        public string Aumid = string.Empty;
        public string Source = string.Empty;
        public string RawStatus = string.Empty;
        public bool LowConfidence;

        public static Detection Idle()
        {
            return new Detection();
        }

        public string Key
        {
            get { return State + "|" + Player + "|" + Title + "|" + Artist + "|" + Album; }
        }
    }

    internal sealed class Payload
    {
        public string Display;
        public Dictionary<string, string> Values = new Dictionary<string, string>();
    }
}
