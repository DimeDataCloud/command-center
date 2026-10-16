// Log.cs -- forensics for a resident process. A failure at 3am must be diagnosable the next
// morning without a repro, because there will not be one.

using System;
using System.IO;
using System.Text;

namespace CommandCenter
{
    internal static class Log
    {
        private static readonly object Gate = new object();
        private static string _path;
        public static bool Echo = false;   // -Selftest and -Calibrate mirror to stdout

        public static string Path
        {
            get
            {
                if (_path == null)
                {
                    Directory.CreateDirectory(Paths.Logs);
                    _path = System.IO.Path.Combine(Paths.Logs, "command-center.log");
                }
                return _path;
            }
        }

        public static void Info(string m) { Write("INFO", m); }
        public static void Warn(string m) { Write("WARN", m); }
        public static void Err(string m) { Write("ERR ", m); }

        public static void Err(string m, Exception ex)
        {
            Write("ERR ", m + " :: " + ex.GetType().Name + ": " + ex.Message);
            Write("ERR ", "  " + ex.StackTrace);
        }

        private static void Write(string lvl, string m)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + lvl + " " + m;
            // Echo WARN/ERR only. -Selftest's stdout is a diff target for the phase gates, and
            // PowerShell 5.1 wraps every native stderr line in a NativeCommandError record -- so
            // echoing routine INFO would turn a passing gate into a screen of red.
            if (Echo && lvl != "INFO") Console.Error.WriteLine(line);
            lock (Gate)
            {
                try
                {
                    Rotate();
                    // LF, no BOM. Everything this project writes that a bash consumer might read is
                    // written this way -- Set-Content -Encoding utf8 emits BOM+CRLF, and that once
                    // killed the session-brief hook mid-arithmetic and silently truncated the whole
                    // brief from 15,165B to 1,219B with no error surfaced anywhere.
                    File.AppendAllText(Path, line + "\n", new UTF8Encoding(false));
                }
                catch { /* logging must never take the process down */ }
            }
        }

        private static void Rotate()
        {
            try
            {
                FileInfo fi = new FileInfo(Path);
                if (!fi.Exists || fi.Length < 2 * 1024 * 1024) return;
                string old = Path + ".1";
                if (File.Exists(old)) File.Delete(old);
                File.Move(Path, old);
            }
            catch { }
        }
    }
}
