using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace SteamTune
{
    /// <summary>
    /// Windows 媒体会话（SMTC）探测。
    /// WinRT 元数据在未装 Windows SDK 的机器上不能直接引用，所以用系统自带的
    /// PowerShell 读取 SMTC（隐藏窗口），本进程只负责解析与展示。
    /// </summary>
    internal sealed class Detector : IDisposable
    {
        private sealed class PlayerRule
        {
            public Regex Pattern;
            public string Name;
            public string Kind;
        }

        private static readonly PlayerRule[] PlayerRules = new PlayerRule[]
        {
            Rule("cloudmusic|netease", "网易云音乐", "player"),
            Rule("qqmusic", "QQ音乐", "player"),
            Rule("kugou", "酷狗音乐", "player"),
            Rule("kuwo", "酷我音乐", "player"),
            Rule("qishui|luna", "汽水音乐", "player"),
            Rule("spotify", "Spotify", "player"),
            Rule("foobar", "foobar2000", "player"),
            Rule("aimp", "AIMP", "player"),
            Rule("musicbee", "MusicBee", "player"),
            Rule("applemusic|apple\\.music|itunes", "Apple Music", "player"),
            Rule("zunemusic|mediaplayer", "媒体播放器", "player"),
            Rule("vlc", "VLC", "player"),
            Rule("potplayer", "PotPlayer", "media"),
            Rule("chrome", "Chrome", "browser"),
            Rule("msedge", "Edge", "browser"),
            Rule("firefox", "Firefox", "browser"),
            Rule("brave", "Brave", "browser"),
            Rule("opera", "Opera", "browser"),
            Rule("bilibili", "哔哩哔哩", "media"),
            Rule("douyin", "抖音", "media"),
        };

        private static PlayerRule Rule(string pattern, string name, string kind)
        {
            PlayerRule rule = new PlayerRule();
            rule.Pattern = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
            rule.Name = name;
            rule.Kind = kind;
            return rule;
        }

        private readonly Config config;
        private readonly JavaScriptSerializer json = new JavaScriptSerializer();
        private readonly object sync = new object();
        private Thread thread;
        private Process process;
        private volatile bool running;
        private static string scriptPath;

        public event Action<Detection> DetectionArrived;

        public Detector(Config config)
        {
            this.config = config;
        }

        public void Start()
        {
            if (running) return;
            running = true;
            thread = new Thread(Loop);
            thread.IsBackground = true;
            thread.Name = "SteamTune.Detector";
            thread.Start();
        }

        public void Stop()
        {
            running = false;
            Process current;
            lock (sync)
            {
                current = process;
                process = null;
            }
            if (current != null)
            {
                try
                {
                    if (!current.HasExited) current.Kill();
                }
                catch
                {
                }
            }
        }

        private void Loop()
        {
            while (running)
            {
                try
                {
                    RunWatcher();
                }
                catch
                {
                    // 探测失败就等一会儿重试，不影响界面
                }
                if (!running) break;
                Thread.Sleep(3000);
            }
        }

        private void RunWatcher()
        {
            string script = EnsureScript();
            ProcessStartInfo info = BuildStartInfo(script, Math.Max(500, config.detect.intervalMs), false);
            Process started = Process.Start(info);
            lock (sync) process = started;

            string line;
            while (running && (line = started.StandardOutput.ReadLine()) != null)
            {
                if (line.Length == 0 || line[0] != '{') continue;
                HandlePayload(line);
            }

            try
            {
                if (!started.HasExited) started.WaitForExit(1500);
            }
            catch
            {
            }
            lock (sync)
            {
                if (ReferenceEquals(process, started)) process = null;
            }
        }

        private static ProcessStartInfo BuildStartInfo(string script, int intervalMs, bool once)
        {
            string arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + script +
                               "\" -IntervalMs " + intervalMs.ToString() +
                               " -ParentPid " + Process.GetCurrentProcess().Id.ToString();
            if (once) arguments += " -Once";

            ProcessStartInfo info = new ProcessStartInfo("powershell.exe", arguments);
            info.UseShellExecute = false;
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;
            info.CreateNoWindow = true;
            info.StandardOutputEncoding = Encoding.UTF8;
            info.StandardErrorEncoding = Encoding.UTF8;
            return info;
        }

        private static string EnsureScript()
        {
            if (scriptPath != null) return scriptPath;

            string content;
            using (Stream stream = typeof(Detector).Assembly.GetManifestResourceStream("smtc.ps1"))
            {
                if (stream == null) throw new InvalidOperationException("内置探测脚本 smtc.ps1 缺失");
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                {
                    content = reader.ReadToEnd();
                }
            }

            string[] candidates = new string[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SteamTune"),
                Path.Combine(Path.GetTempPath(), "SteamTune"),
                AppDomain.CurrentDomain.BaseDirectory,
            };

            Exception last = null;
            for (int i = 0; i < candidates.Length; i++)
            {
                try
                {
                    Directory.CreateDirectory(candidates[i]);
                    string target = Path.Combine(candidates[i], "smtc.ps1");
                    File.WriteAllText(target, content, new UTF8Encoding(false));
                    scriptPath = target;
                    return target;
                }
                catch (Exception ex)
                {
                    last = ex;
                }
            }
            throw new InvalidOperationException("无法准备探测脚本：" + (last == null ? "未知原因" : last.Message));
        }

        private void HandlePayload(string line)
        {
            PayloadDto payload;
            try
            {
                payload = json.Deserialize<PayloadDto>(line);
            }
            catch
            {
                return;
            }
            if (payload == null) return;

            Detection detection = Pick(payload, config);
            Action<Detection> handler = DetectionArrived;
            if (handler != null) handler(detection);
        }

        /// <summary>一次性探测，供命令行 <c>--once</c> 使用</summary>
        public static PayloadDto ProbeOnce(Config config, int timeoutMs)
        {
            string script = EnsureScript();
            ProcessStartInfo info = BuildStartInfo(script, Math.Max(500, config.detect.intervalMs), true);
            using (Process process = Process.Start(info))
            {
                string line = null;
                Thread watchdog = new Thread(delegate()
                {
                    Thread.Sleep(timeoutMs);
                    try
                    {
                        if (!process.HasExited) process.Kill();
                    }
                    catch
                    {
                    }
                });
                watchdog.IsBackground = true;
                watchdog.Start();

                string current;
                while ((current = process.StandardOutput.ReadLine()) != null)
                {
                    if (current.Length > 0 && current[0] == '{')
                    {
                        line = current;
                        break;
                    }
                }

                try
                {
                    if (!process.HasExited) process.Kill();
                }
                catch
                {
                }

                if (line == null) return null;
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                try
                {
                    return serializer.Deserialize<PayloadDto>(line);
                }
                catch
                {
                    return null;
                }
            }
        }

        public static Detection Pick(PayloadDto payload, Config config)
        {
            List<SessionDto> sessions = payload.sessions ?? new List<SessionDto>();
            List<SessionDto> usable = new List<SessionDto>();
            for (int i = 0; i < sessions.Count; i++)
            {
                SessionDto session = sessions[i];
                if (string.IsNullOrEmpty(session.aumid)) continue;
                if (IsBlacklisted(session.aumid, config)) continue;
                usable.Add(session);
            }

            usable.Sort(CompareSessions);
            if (usable.Count > 0)
            {
                Detection picked = FromSession(usable[0]);
                if (usable[0].status != "Playing")
                {
                    picked.State = usable[0].status == "Paused" ? "paused" : "idle";
                }
                return picked;
            }

            if (config.detect.allowWindowTitleFallback && payload.foreground != null)
            {
                Detection fallback = FromForeground(payload.foreground);
                if (fallback != null) return fallback;
            }

            return Detection.Idle();
        }

        private static int CompareSessions(SessionDto a, SessionDto b)
        {
            int rankA = Rank(a.status);
            int rankB = Rank(b.status);
            if (rankA != rankB) return rankA.CompareTo(rankB);
            return b.updated.CompareTo(a.updated);
        }

        private static int Rank(string status)
        {
            if (status == "Playing") return 0;
            if (status == "Paused") return 1;
            return 2;
        }

        private static bool IsBlacklisted(string aumid, Config config)
        {
            List<string> blacklist = config.detect.blacklist;
            if (blacklist == null) return false;
            string lower = aumid.ToLowerInvariant();
            for (int i = 0; i < blacklist.Count; i++)
            {
                string entry = blacklist[i];
                if (string.IsNullOrEmpty(entry)) continue;
                if (lower.IndexOf(entry.ToLowerInvariant(), StringComparison.Ordinal) >= 0) return true;
            }
            return false;
        }

        private static Detection FromSession(SessionDto session)
        {
            PlayerRule rule = MatchRule(session.aumid);
            Detection detection = new Detection();
            detection.State = session.status == "Playing" ? "playing" :
                              session.status == "Paused" ? "paused" : "idle";
            detection.Title = Clean(session.title);
            detection.Artist = Clean(session.artist);
            detection.Album = Clean(session.album);
            detection.Aumid = session.aumid;
            detection.RawStatus = session.status;
            detection.Source = "smtc";
            detection.Player = rule == null ? Pretty(session.aumid) : PlayerLabel(rule);
            detection.PlayerKind = rule == null ? "unknown" : rule.Kind;
            return detection;
        }

        private static Detection FromForeground(ForegroundDto foreground)
        {
            if (string.IsNullOrEmpty(foreground.title) || string.IsNullOrEmpty(foreground.process)) return null;
            PlayerRule rule = MatchRule(foreground.process + ".exe");
            if (rule == null || rule.Kind != "player") return null;

            Detection detection = new Detection();
            detection.State = "playing";
            detection.Title = Clean(foreground.title);
            detection.Player = PlayerLabel(rule);
            detection.PlayerKind = rule.Kind;
            detection.Aumid = foreground.process + ".exe";
            detection.Source = "window";
            detection.LowConfidence = true;
            return detection;
        }

        private static string PlayerLabel(PlayerRule rule)
        {
            return rule.Kind == "browser" ? rule.Name + " 网页播放" : rule.Name;
        }

        private static PlayerRule MatchRule(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            for (int i = 0; i < PlayerRules.Length; i++)
            {
                if (PlayerRules[i].Pattern.IsMatch(value)) return PlayerRules[i];
            }
            return null;
        }

        private static string Pretty(string aumid)
        {
            if (string.IsNullOrEmpty(aumid)) return "未知播放器";
            string part = aumid;
            int bang = part.LastIndexOf('!');
            if (bang >= 0) part = part.Substring(bang + 1);
            if (part.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) part = part.Substring(0, part.Length - 4);
            part = part.Replace('_', ' ').Replace('.', ' ').Trim();
            return part.Length == 0 ? "未知播放器" : part;
        }

        /// <summary>去掉控制字符、压缩空白（播放器标题里经常带换行）</summary>
        internal static string Clean(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            StringBuilder builder = new StringBuilder(value.Length);
            bool pendingSpace = false;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (char.IsControl(c) || c == '\u200b' || c == '\ufeff')
                {
                    pendingSpace = true;
                    continue;
                }
                if (char.IsWhiteSpace(c))
                {
                    pendingSpace = true;
                    continue;
                }
                if (pendingSpace && builder.Length > 0) builder.Append(' ');
                pendingSpace = false;
                builder.Append(c);
            }
            return builder.ToString();
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
