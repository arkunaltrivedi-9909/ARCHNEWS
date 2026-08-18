using System;
using System.IO;
using System.Text;

namespace KTA.SmartySheets.Core
{
    /// <summary>
    /// Append-only daily log. Every path in this class swallows its own failures:
    /// a tool that crashes because it could not write a log line is worse than a
    /// tool with no log.
    /// </summary>
    internal sealed class Log
    {
        private static readonly object Gate = new object();
        private static Log _instance;

        public static Log Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (Gate)
                    {
                        if (_instance == null) _instance = new Log();
                    }
                }
                return _instance;
            }
        }

        public static string Directory
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "KTA", "SmartySheets", "logs");
            }
        }

        private string CurrentFile
        {
            get { return Path.Combine(Directory, "smartysheets-" + DateTime.Now.ToString("yyyy-MM-dd") + ".log"); }
        }

        public void Info(string message) { Write("INFO ", message); }
        public void Warn(string message) { Write("WARN ", message); }

        public void Error(string message, Exception ex)
        {
            Write("ERROR", ex == null ? message : message + " :: " + ex.GetType().Name + ": " + ex.Message);
            if (ex != null && ex.StackTrace != null) Write("ERROR", ex.StackTrace);
        }

        private void Write(string level, string message)
        {
            try
            {
                lock (Gate)
                {
                    System.IO.Directory.CreateDirectory(Directory);
                    File.AppendAllText(
                        CurrentFile,
                        DateTime.Now.ToString("HH:mm:ss.fff") + " " + level + " " + message + Environment.NewLine,
                        Encoding.UTF8);
                }
            }
            catch
            {
                // Logging must never take the add-in down with it.
            }
        }
    }
}
