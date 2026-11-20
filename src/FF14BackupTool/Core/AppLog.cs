using System;
using System.IO;
using System.Text;

namespace FF14BackupTool.Core
{
    public static class AppLog
    {
        private static readonly object Sync = new object();

        public static void Info(string message)
        {
            Write("INFO", message);
        }

        public static void Error(string message, Exception ex)
        {
            Write("ERROR", message + (ex == null ? string.Empty : " | " + ex));
        }

        private static void Write(string level, string message)
        {
            try
            {
                lock (Sync)
                {
                    string dir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "FF14BackupTool", "logs");
                    Directory.CreateDirectory(dir);
                    string path = Path.Combine(dir, "app-" + DateTime.Now.ToString("yyyyMMdd") + ".log");
                    string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " [" + level + "] " + message +
                                  Environment.NewLine;
                    File.AppendAllText(path, line, new UTF8Encoding(false));
                }
            }
            catch
            {
                // Logging must never break the app.
            }
        }
    }
}
