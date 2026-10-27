using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace SteamTune
{
    /// <summary>读取内置的 rich presence 本地化模板，用于预览“好友会看到什么”</summary>
    internal static class Localization
    {
        private static Dictionary<string, string> tokens = new Dictionary<string, string>();

        public static void Load()
        {
            tokens = new Dictionary<string, string>();
            try
            {
                string json = null;
                using (Stream stream = typeof(Localization).Assembly.GetManifestResourceStream("localization.json"))
                {
                    if (stream != null)
                    {
                        using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                        {
                            json = reader.ReadToEnd();
                        }
                    }
                }
                if (json == null) return;

                JavaScriptSerializer serializer = new JavaScriptSerializer();
                Dictionary<string, object> root = serializer.Deserialize<Dictionary<string, object>>(json);
                if (root == null || !root.ContainsKey("lang")) return;
                Dictionary<string, object> langs = root["lang"] as Dictionary<string, object>;
                if (langs == null) return;

                MergeTokens(langs, "english");
                MergeTokens(langs, "schinese");
            }
            catch
            {
                tokens = new Dictionary<string, string>();
            }
        }

        private static void MergeTokens(Dictionary<string, object> langs, string language)
        {
            if (!langs.ContainsKey(language)) return;
            Dictionary<string, object> block = langs[language] as Dictionary<string, object>;
            if (block == null || !block.ContainsKey("tokens")) return;
            Dictionary<string, object> map = block["tokens"] as Dictionary<string, object>;
            if (map == null) return;
            foreach (KeyValuePair<string, object> pair in map)
            {
                tokens[pair.Key] = Convert.ToString(pair.Value);
            }
        }

        public static string Render(string display, Dictionary<string, string> values)
        {
            string template;
            if (string.IsNullOrEmpty(display) || !tokens.TryGetValue(display, out template)) return null;
            StringBuilder builder = new StringBuilder(template);
            foreach (KeyValuePair<string, string> pair in values)
            {
                builder.Replace("%" + pair.Key + "%", pair.Value ?? string.Empty);
            }
            return Cleanup(builder.ToString());
        }

        private static string Cleanup(string text)
        {
            string result = text.Replace("\r", " ").Replace("\n", " ");
            while (result.IndexOf("  ", StringComparison.Ordinal) >= 0) result = result.Replace("  ", " ");
            // 去掉空占位留下的悬空分隔符
            result = System.Text.RegularExpressions.Regex.Replace(result, @"\s*[—–·|,、]\s*(?=[—–·|,、]|$)", string.Empty);
            result = System.Text.RegularExpressions.Regex.Replace(result, @"\(\s*\)|\[\s*\]", string.Empty);
            return result.Trim();
        }
    }

    /// <summary>探测结果 → rich presence payload，并做节流发布</summary>
    internal sealed class PresenceController : IDisposable
    {
        private readonly SteamNative steam;
        private Config config;
        private readonly Timer throttle;
        private Payload pending;
        private Payload lastPublished;
        private HashSet<string> liveKeys = new HashSet<string>();
        private long lastPublishAt;
        private Detection current = Detection.Idle();

        public string Preview = string.Empty;
        public string DisplayToken = string.Empty;
        public string Reason = "启动中";
        public long ChangedAt;
        public int PublishedCount;
        public bool LastPublishOk = true;
        public event Action Updated;

        public PresenceController(SteamNative steam, Config config)
        {
            this.steam = steam;
            this.config = config;
            throttle = new Timer();
            throttle.Interval = 200;
            throttle.Tick += OnThrottle;
            throttle.Start();
        }

        public void SetConfig(Config value)
        {
            config = value;
            lastPublished = null;
            Apply(current);
        }

        public void Apply(Detection detection)
        {
            current = detection ?? Detection.Idle();

            if (!config.presence.enabled)
            {
                Clear("同步已关闭");
                return;
            }

            if (current.State == "idle")
            {
                if (config.presence.onIdle == "keep" && lastPublished != null)
                {
                    Reason = "空闲，保留上次内容";
                    Raise();
                    return;
                }
                Clear("空闲，已清除状态");
                return;
            }

            if (current.State == "paused" && config.presence.onPaused == "keep" && lastPublished != null)
            {
                Reason = "已暂停，保留上次内容";
                Raise();
                return;
            }

            if (current.State == "paused" && config.presence.onPaused != "keep")
            {
                Clear("已暂停，已清除状态");
                return;
            }

            Payload payload = Build(current);

            // 联调开关：强行使用指定模板（例如借用免费游戏的 rich presence 模板验证渲染链路）
            if (!string.IsNullOrEmpty(config.presence.displayToken))
            {
                payload.Display = config.presence.displayToken;
                foreach (KeyValuePair<string, string> pair in config.presence.extraTokens)
                {
                    payload.Values[pair.Key] = pair.Value;
                }
            }
            string preview = Localization.Render(payload.Display, payload.Values) ?? Values.GetValueOrDefault(payload.Values, "song");
            Preview = preview;
            DisplayToken = payload.Display;
            Reason = current.State == "paused" ? "已暂停，显示当前这首" : "正在播放";

            if (lastPublished != null && Same(lastPublished, payload))
            {
                Raise();
                return;
            }

            pending = payload;
            Flush(false);
            Raise();
        }

        public void Republish()
        {
            lastPublished = null;
            lastPublishAt = 0;
            Apply(current);
        }

        /// <summary>退出前清空好友可见状态</summary>
        public void ClearAll()
        {
            try
            {
                Clear("已退出");
            }
            catch
            {
            }
        }

        private void OnThrottle(object sender, EventArgs e)
        {
            Flush(false);
        }

        private void Flush(bool force)
        {
            if (pending == null) return;
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (!force && now - lastPublishAt < config.presence.minPublishIntervalMs) return;

            Payload payload = pending;
            pending = null;
            if (!steam.Available) return;

            // 清掉这一轮不再使用的 token
            foreach (string key in new List<string>(liveKeys))
            {
                if (key == "steam_display") continue;
                if (!payload.Values.ContainsKey(key)) steam.SetPresence(key, null);
            }

            bool ok = steam.SetPresence("steam_display", payload.Display);
            liveKeys = new HashSet<string>();
            liveKeys.Add("steam_display");
            foreach (KeyValuePair<string, string> pair in payload.Values)
            {
                ok &= steam.SetPresence(pair.Key, pair.Value);
                liveKeys.Add(pair.Key);
            }
            LastPublishOk = ok;
            if (!ok) Reason = "Steam 拒绝了 rich presence（检查 AppID 授权）";

            lastPublished = payload;
            lastPublishAt = now;
            PublishedCount++;
            Preview = Localization.Render(payload.Display, payload.Values) ?? Preview;
            DisplayToken = payload.Display;
        }

        private void Clear(string reason)
        {
            Preview = string.Empty;
            DisplayToken = string.Empty;
            Reason = reason;
            pending = null;
            if (lastPublished == null && liveKeys.Count == 0)
            {
                Raise();
                return;
            }
            if (steam.Available)
            {
                foreach (string key in liveKeys) steam.SetPresence(key, null);
            }
            liveKeys.Clear();
            lastPublished = null;
            Raise();
        }

        private Payload Build(Detection detection)
        {
            string song = Limit(detection.Title);
            if (song.Length == 0) song = "未知歌曲";
            string artist = Limit(detection.Artist);
            string player = Limit(detection.Player);

            Payload payload = new Payload();
            if (config.presence.style == "title")
            {
                payload.Display = "#Status_Listening";
                payload.Values["song"] = song;
                return payload;
            }
            if (config.presence.style == "artist")
            {
                if (artist.Length > 0)
                {
                    payload.Display = "#Status_ListeningArtist";
                    payload.Values["song"] = song;
                    payload.Values["artist"] = artist;
                }
                else
                {
                    payload.Display = "#Status_Listening";
                    payload.Values["song"] = song;
                }
                return payload;
            }

            if (artist.Length > 0 && player.Length > 0)
            {
                payload.Display = "#Status_ListeningWithPlayer";
                payload.Values["song"] = song;
                payload.Values["artist"] = artist;
                payload.Values["player"] = player;
            }
            else if (artist.Length > 0)
            {
                payload.Display = "#Status_ListeningArtist";
                payload.Values["song"] = song;
                payload.Values["artist"] = artist;
            }
            else if (player.Length > 0)
            {
                payload.Display = "#Status_ListeningWithPlayerNoArtist";
                payload.Values["song"] = song;
                payload.Values["player"] = player;
            }
            else
            {
                payload.Display = "#Status_Listening";
                payload.Values["song"] = song;
            }
            return payload;
        }

        private string Limit(string value)
        {
            string text = Detector.Clean(value);
            if (text.Length == 0) return string.Empty;
            int maxChars = Math.Min(config.presence.maxTokenLength, 100);
            int maxBytes = 200;
            StringBuilder builder = new StringBuilder();
            int bytes = 0;
            for (int i = 0; i < text.Length; i++)
            {
                string ch = text.Substring(i, 1);
                int size = Encoding.UTF8.GetByteCount(ch);
                if (builder.Length + 1 > maxChars || bytes + size > maxBytes) return builder.ToString() + "…";
                builder.Append(ch);
                bytes += size;
            }
            return builder.ToString();
        }

        private static bool Same(Payload a, Payload b)
        {
            if (a.Display != b.Display) return false;
            if (a.Values.Count != b.Values.Count) return false;
            foreach (KeyValuePair<string, string> pair in a.Values)
            {
                string other;
                if (!b.Values.TryGetValue(pair.Key, out other)) return false;
                if (other != pair.Value) return false;
            }
            return true;
        }

        private void Raise()
        {
            ChangedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            Action handler = Updated;
            if (handler != null) handler();
        }

        public void Dispose()
        {
            if (throttle != null) throttle.Dispose();
        }
    }

    internal static class Values
    {
        public static string GetValueOrDefault(Dictionary<string, string> map, string key)
        {
            string value;
            return map.TryGetValue(key, out value) ? value : string.Empty;
        }
    }
}
