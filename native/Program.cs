using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyVersion("0.1.0")]
[assembly: AssemblyFileVersion("0.1.0")]
[assembly: AssemblyTitle("SteamTune")]
[assembly: AssemblyProduct("SteamTune")]
[assembly: AssemblyDescription("把正在听的歌同步到 Steam 好友列表")]

namespace SteamTune
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Directory.SetCurrentDirectory(AppDomain.CurrentDomain.BaseDirectory);

            bool preview = HasFlag(args, "--preview") ||
                           Environment.GetEnvironmentVariable("STEAMTUNE_NO_STEAM") == "1";
            bool once = HasFlag(args, "--once");
            bool startHidden = HasFlag(args, "--tray");

            Localization.Load();
            Config config = Config.Load();
            Diag.Log("start args=" + string.Join(" ", args) + " preview=" + preview + " hidden=" + startHidden);

            if (once)
            {
                ConsoleHelper.Attach();
                Environment.ExitCode = RunOnce(config, preview);
                ConsoleHelper.Detach();
                return;
            }

            bool created;
            using (Mutex mutex = new Mutex(true, "SteamTune.App.SingleInstance", out created))
            {
                if (!created)
                {
                    // 已经有一个实例在跑（可能正躲在托盘里），让它把窗口显示出来
                    SignalRunningInstance();
                    return;
                }

                // 发布版：用户直接双击 exe 时，请求 Steam 重新拉起（开发时存在 steam_appid.txt，跳过）
                if (!preview && !File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "steam_appid.txt")))
                {
                    int appId = ResolveAppId(config);
                    if (appId > 0 && SteamNative.RestartThroughSteam((uint)appId, AppDomain.CurrentDomain.BaseDirectory))
                    {
                        return;
                    }
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += delegate(object sender, System.Threading.ThreadExceptionEventArgs e)
                {
                    Diag.LogException("UI thread exception", e.Exception);
                    MessageBox.Show("SteamTune 遇到错误：" + e.Exception.Message + "\r\n日志：" + Diag.LogPath,
                        "SteamTune", MessageBoxButtons.OK, MessageBoxIcon.Error);
                };
                AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
                {
                    Diag.LogException("Unhandled exception", e.ExceptionObject as Exception);
                };

                MainForm form = new MainForm(config, preview, startHidden || config.window.startInTray);
                AppDomain.CurrentDomain.ProcessExit += delegate { form.EmergencyShutdown(); };
                Application.Run(form);
            }
        }

        private static void SignalRunningInstance()
        {
            try
            {
                using (EventWaitHandle handle = EventWaitHandle.OpenExisting("SteamTune.ShowWindow"))
                {
                    handle.Set();
                }
            }
            catch
            {
                // 对方还没创建事件（刚启动），忽略即可
            }
        }

        private static int ResolveAppId(Config config)
        {
            int parsed;
            string env = Environment.GetEnvironmentVariable("SteamAppId");
            if (!string.IsNullOrEmpty(env) && int.TryParse(env, out parsed) && parsed > 0) return parsed;
            env = Environment.GetEnvironmentVariable("STEAMTUNE_APPID");
            if (!string.IsNullOrEmpty(env) && int.TryParse(env, out parsed) && parsed > 0) return parsed;
            if (config.steam != null && config.steam.appId > 0) return config.steam.appId;

            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "steam_appid.txt");
            try
            {
                if (File.Exists(path))
                {
                    string first = File.ReadAllText(path).Trim().Split(new char[] { ' ', '\r', '\n', '\t' },
                        StringSplitOptions.RemoveEmptyEntries)[0];
                    if (int.TryParse(first, out parsed) && parsed > 0) return parsed;
                }
            }
            catch
            {
            }
            return 0;
        }

        private static bool HasFlag(string[] args, string flag)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static int RunOnce(Config config, bool preview)
        {
            PayloadDto payload = Detector.ProbeOnce(config, 15000);
            if (payload == null)
            {
                Console.WriteLine("探测失败：没有拿到媒体会话数据。");
                return 1;
            }

            Detection detection = Detector.Pick(payload, config);
            SteamNative steam = new SteamNative();
            using (PresenceController presence = new PresenceController(steam, config))
            {
                presence.Apply(detection);
                Console.WriteLine();
                Console.WriteLine("播放器   " + (detection.Player.Length > 0 ? detection.Player : "未识别") +
                                  (detection.Aumid.Length > 0 ? "  (" + detection.Aumid + ")" : ""));
                Console.WriteLine("状态     " + (detection.State == "playing" ? "播放中" :
                                                    detection.State == "paused" ? "已暂停" : "空闲") +
                                  (detection.Source == "window" ? " · 窗口标题推断" : ""));
                Console.WriteLine("歌曲     " + (detection.Title.Length > 0 ? detection.Title : "—"));
                if (detection.Artist.Length > 0) Console.WriteLine("歌手     " + detection.Artist);
                if (detection.Album.Length > 0) Console.WriteLine("专辑     " + detection.Album);
                Console.WriteLine();
                Console.WriteLine("好友看到 " + (presence.Preview.Length > 0 ? presence.Preview : "（不发布 / 已清除）"));
                if (presence.DisplayToken.Length > 0) Console.WriteLine("模板     " + presence.DisplayToken);
                Console.WriteLine();
            }

            if (payload.foreground != null && !string.IsNullOrEmpty(payload.foreground.title))
            {
                Console.WriteLine("前台窗口 " + payload.foreground.title);
            }
            if (preview) Console.WriteLine("（--preview：本次探测不会发布到 Steam）");
            Console.WriteLine();
            return 0;
        }
    }

    internal static class ConsoleHelper
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FreeConsole();

        private const int AttachParentProcess = -1;

        public static void Attach()
        {
            // 由资源管理器双击时没有控制台，AttachConsole 会失败；此时只是没有输出，不影响使用
            AttachConsole(AttachParentProcess);

            // 重定向到文件/管道时设置代码页会抛异常，必须和 SetOut 分开处理
            try
            {
                Console.OutputEncoding = new UTF8Encoding(false);
            }
            catch
            {
            }

            try
            {
                Stream stdout = Console.OpenStandardOutput();
                StreamWriter writer = new StreamWriter(stdout, new UTF8Encoding(false));
                writer.AutoFlush = true;
                Console.SetOut(writer);
                Console.SetError(writer);
            }
            catch
            {
            }
        }

        public static void Detach()
        {
            try
            {
                Console.Out.Flush();
            }
            catch
            {
            }
            FreeConsole();
        }
    }
}
