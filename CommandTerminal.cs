// CommandTerminal.cs -- clicking the Command Center icon opens the COO. Focus-or-spawn, never a second window.
//
// "will i be able to communicate through just the orchestration agent instead of all the other
// agents" -- yes, and this is the button that makes it literal. The terminal that opens is an
// ordinary interactive Claude Code session in the workspace repo, which means workspace CLAUDE.md loads and
// the session IS the COO: no department, all of them.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace CommandCenter
{
    internal static class CommandTerminal
    {
        private static int _pid = -1;
        private static IntPtr _hwnd = IntPtr.Zero;
        private static int _lastSpawnTick;

        /// <summary>
        /// Set on the Windows Terminal tab so the window can be found again by title.
        ///
        /// THE BUG THIS FIXES: wt.exe is a LAUNCHER STUB. It hands the request to the already
        /// running WindowsTerminal.exe and exits within about a second, so the pid Process.Start
        /// returns is dead almost immediately. Focus() saw HasExited, concluded there was no COO
        /// session, and spawned another one -- three windows in 61 seconds in the real log on
        /// 2026-08-04. Tracking that pid could never have worked; the window has to be found by
        /// something that outlives the launcher.
        /// </summary>
        private const string CooTitle = "COO";

        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr h, int cmd);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr p);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr h, StringBuilder s, int max);
        private delegate bool EnumWindowsProc(IntPtr h, IntPtr p);
        private const int SW_RESTORE = 9;

        /// <summary>
        /// Focus the existing COO terminal, or start one. Two rapid clicks must yield ONE window --
        /// the plan makes that an acceptance test, so the pid is remembered and validated rather
        /// than assumed dead.
        /// </summary>
        public static void Open(string preloadQuestion)
        {
            try
            {
                if (Focus()) return;

                // A terminal takes a second or two to put a window on screen. Without this guard a
                // second click in that window finds nothing to focus and starts another session --
                // the same double-click race, independent of the wt.exe stub problem.
                int since = Environment.TickCount - _lastSpawnTick;
                if (_lastSpawnTick != 0 && since >= 0 && since < 4000)
                {
                    Log.Info("terminal: spawn already in flight " + since + "ms ago -- ignoring click");
                    return;
                }
                Spawn(preloadQuestion);
            }
            catch (Exception ex) { Log.Err("terminal: open failed", ex); }
        }

        private static bool Focus()
        {
            // 1. The window we found last time, if it is still there.
            if (_hwnd != IntPtr.Zero && IsWindow(_hwnd) && TitleOf(_hwnd).IndexOf(
                    CooTitle, StringComparison.OrdinalIgnoreCase) >= 0)
                return Raise(_hwnd, "cached");
            _hwnd = IntPtr.Zero;

            // 2. Any visible top-level window carrying the COO title. This is what survives the
            //    wt.exe hand-off, and it also finds a session started before this strip did.
            IntPtr found = IntPtr.Zero;
            try
            {
                EnumWindows(delegate(IntPtr h, IntPtr p)
                {
                    if (!IsWindowVisible(h)) return true;
                    if (TitleOf(h).IndexOf(CooTitle, StringComparison.OrdinalIgnoreCase) < 0) return true;
                    found = h;
                    return false;   // stop enumerating
                }, IntPtr.Zero);
            }
            catch (Exception ex) { Log.Warn("terminal: window scan failed: " + ex.Message); }

            if (found != IntPtr.Zero) { _hwnd = found; return Raise(found, "by title"); }

            // 3. Fallback for the cmd.exe path. There is no --suppressApplicationTitle for a bare
            //    console, so claude overwrites that title too and step 2 cannot find it -- but
            //    cmd.exe does NOT hand off the way wt.exe does, so its pid stays valid and works.
            if (_pid <= 0) _pid = ReadPid();
            if (_pid <= 0) return false;
            try
            {
                // Disposed: this runs on every click of the Command Center glyph, and GetProcessById opens a
                // kernel handle each time.
                using (Process p = Process.GetProcessById(_pid))
                {
                    if (p.HasExited) { _pid = -1; return false; }
                    IntPtr h = p.MainWindowHandle;
                    if (h == IntPtr.Zero) return false;
                    _hwnd = h;
                    return Raise(h, "pid " + _pid);
                }
            }
            catch (ArgumentException) { _pid = -1; return false; }
            catch (Exception ex) { Log.Warn("terminal: focus failed: " + ex.Message); return false; }
        }

        private static bool Raise(IntPtr h, string how)
        {
            if (IsIconic(h)) ShowWindowAsync(h, SW_RESTORE);
            SetForegroundWindow(h);
            Log.Info("terminal: focused existing COO session (" + how + ")");
            return true;
        }

        private static string TitleOf(IntPtr h)
        {
            try
            {
                StringBuilder sb = new StringBuilder(320);
                int n = GetWindowText(h, sb, sb.Capacity);
                return n > 0 ? sb.ToString() : "";
            }
            catch { return ""; }
        }

        private static void Spawn(string preload)
        {
            string wt = Path.Combine(Paths.Home, @"AppData\Local\Microsoft\WindowsApps\wt.exe");
            string claude = Supervisor.ResolveClaude();
            if (claude == null) { Log.Err("terminal: claude.cmd not found"); return; }

            // A preloaded question is written to a file and referenced, never interpolated into a
            // command line. Ask titles are officer-authored text; putting them straight into an
            // argument vector would be a shell-injection surface pointed at a permissioned session.
            string arg = "";
            if (!string.IsNullOrEmpty(preload))
            {
                try
                {
                    string qf = Paths.State1("coo-question.txt");
                    File.WriteAllText(qf, preload + "\n", new UTF8Encoding(false));
                    arg = " \"Read " + qf.Replace("\\", "/") + " and answer the question in it as COO.\"";
                }
                catch (Exception ex) { Log.Warn("terminal: could not stage question: " + ex.Message); }
            }

            // Both paths stamp the window with a findable title -- that title, not the pid, is how
            // Focus() locates this session on the next click.
            ProcessStartInfo psi = new ProcessStartInfo();
            if (File.Exists(wt))
            {
                psi.FileName = wt;
                // --suppressApplicationTitle IS LOAD-BEARING. claude sets the window title to
                // "* Claude Code" the moment it starts, which silently erased --title and defeated
                // tracking by title completely -- the first version of this fix looked correct and
                // still spawned two sessions back to back. Measured 2026-08-04.
                //
                // -w pins every launch to ONE named wt window, so even if the focus scan misses,
                // the worst case degrades to an extra tab rather than another window.
                psi.Arguments = "-w command-center-coo -d \"" + Paths.Repo + "\" --title \"" + CooTitle
                                + "\" --suppressApplicationTitle cmd /k \"" + claude + "\"" + arg;
            }
            else
            {
                psi.FileName = "cmd.exe";
                psi.Arguments = "/k title " + CooTitle + " && \"" + claude + "\"" + arg;
            }
            psi.WorkingDirectory = Paths.Repo;

            // UseShellExecute MUST be false here: .NET forbids touching EnvironmentVariables when it
            // is true, and scrubbing the environment is the whole point. The window still appears --
            // wt.exe and cmd.exe both create their own -- this only changes who creates the process.
            psi.UseShellExecute = false;
            Supervisor.ScrubSessionEnv(psi);

            _lastSpawnTick = Environment.TickCount;
            _hwnd = IntPtr.Zero;

            Process proc = Process.Start(psi);
            if (proc != null)
            {
                _pid = proc.Id;
                WritePid(_pid);
                Log.Info("terminal: spawned COO session pid=" + _pid +
                         (File.Exists(wt) ? " (wt launcher -- tracked by title)" : ""));
            }
        }

        private static int ReadPid()
        {
            try
            {
                string p = Paths.State1("coo.json");
                if (!File.Exists(p)) return -1;
                System.Text.RegularExpressions.Match m =
                    System.Text.RegularExpressions.Regex.Match(File.ReadAllText(p), "\"pid\"\\s*:\\s*(\\d+)");
                if (m.Success) return int.Parse(m.Groups[1].Value);
            }
            catch { }
            return -1;
        }

        private static void WritePid(int pid)
        {
            try
            {
                File.WriteAllText(Paths.State1("coo.json"),
                    string.Format("{{\"pid\":{0},\"startedAt\":\"{1}\"}}\n", pid, DateTime.Now.ToString("o")),
                    new UTF8Encoding(false));
            }
            catch { }
        }
    }
}
