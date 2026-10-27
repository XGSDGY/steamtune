using System;
using System.IO;
using System.Text;

namespace SteamTune
{
    /// <summary>极简本地日志：只用于排查启动/退出问题，不收集任何用户数据</summary>
    internal static class Diag
    {
        private static readonly object Sync = new object();

        public static string LogPath
        {
            get { return Path.Combine(Config.Directory, "app.log"); }
        }

        public static void Log(string message)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + message + Environment.NewLine;
            string[] targets = new string[]
            {
                Config.Directory,
                AppDomain.CurrentDomain.BaseDirectory,
                Path.Combine(Path.GetTempPath(), "SteamTune"),
            };

            for (int i = 0; i < targets.Length; i++)
            {
                try
                {
                    Directory.CreateDirectory(targets[i]);
                    File.AppendAllText(Path.Combine(targets[i], "app.log"), line, new UTF8Encoding(false));
                    return;
                }
                catch
                {
                    // 换下一个可写目录
                }
            }
        }

        public static void LogException(string context, Exception error)
        {
            Log(context + ": " + (error == null ? "(null)" : error.ToString()));
        }
    }
}
