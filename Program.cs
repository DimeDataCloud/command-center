// Program.cs -- entry point, single instance, tray menu, and the -Selftest contract.
//
// SHIPS PAUSED. Autonomous dispatch has been the operator's explicit call throughout this project and
// stays that way: the app installs, the strip renders, and nothing runs unattended until he picks
// "Go" from the tray. supervisor.json's only legal first value is {"paused":true}.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace CommandCenter
{
    internal static class Program
    {
        private const string MutexName = "Global\\CommandCenter";

        [STAThread]
        static int Main(string[] args)
        {
            bool selftest = Has(args, "-selftest");
            bool calibrate = Has(args, "-calibrate");
            bool pin = Has(args, "-pin");
            bool health = Has(args, "-health");
            bool coo = Has(args, "-coo");
            bool quit = Has(args, "-quit");
            string iconSheet = Value(args, "-iconsheet");
            string dispatchOne = Value(args, "-dispatchone");
            string simulate = Value(args, "-simulate");
            bool console = selftest || calibrate || pin || health || coo || quit
                           || iconSheet != null || dispatchOne != null || simulate != null;

            // /target:winexe allocates no console (deliberately -- /target:exe flashes a black
            // window on every login). So for the diagnostic modes we borrow the caller's console
            // and rebind the CLR's cached stdout/stderr to it. Without this rebind, AttachConsole
            // succeeds and Console.WriteLine STILL writes nowhere, because Console captured a null
            // handle at first touch.
            if (console) BindConsole();

            Log.Echo = console;

            try { Paths.EnsureDirs(); }
            catch (Exception ex) { Console.Error.WriteLine("cannot create .command-center: " + ex.Message); return 1; }

            FirstRun();

            if (health) return Health();
            if (quit) return QuitRunning();
            if (pin) { int n = Supervisor.PinAll(); Console.WriteLine("pinned=" + n); return 0; }
            if (selftest) return SelfTest();
            if (calibrate) return Calibrate();

            // Focus-or-open the COO terminal, the same gesture as clicking Command Center on the strip.
            // Useful from a shortcut or a script, and it makes the focus path testable without
            // having to click a 40px glyph on the taskbar.
            if (coo) { CommandTerminal.Open(null); return 0; }
            if (iconSheet != null) return IconSheet(iconSheet);
            if (dispatchOne != null)
            {
                StateStore.Refresh();
                string detail;
                int rc = Supervisor.DispatchOne(dispatchOne, out detail);
                Console.WriteLine(rc == 0
                    ? "launched " + dispatchOne + " -- " + detail
                    : "REFUSED " + dispatchOne + " -- " + detail);
                if (rc != 0) return rc;

                // WAIT FOR IT. The redirected stdout pipe is owned by THIS process; if we exit now
                // the read end closes, the async readers die with us, and the dispatch log stays 0
                // bytes forever -- which is exactly what happened the first time this ran. The
                // resident supervisor does not have this problem because it stays alive. A
                // diagnostic that discards the output of the thing it is diagnosing is useless.
                Console.WriteLine("waiting (Ctrl-C is safe -- the officer keeps running)...");
                int waited = 0;
                while (Supervisor.InFlight() > 0 && waited < 1800)
                {
                    Thread.Sleep(2000);
                    waited += 2;
                    if (waited % 60 == 0) Console.WriteLine("  ... " + waited + "s");
                    Supervisor.Harvest();
                }
                Console.WriteLine(Supervisor.InFlight() == 0
                    ? "finished after " + waited + "s -- see the log and the ledger"
                    : "still running after " + waited + "s -- leaving it");
                return 0;
            }
            if (simulate != null) return Simulate(args, simulate);

            bool relaunch = Has(args, "-relaunch");
            CrashAfterSeconds = IntValue(args, "-crashafter", 0);

            bool createdNew;
            using (Mutex mx = new Mutex(true, MutexName, out createdNew))
            {
                // A crash relaunch is started BY the dying process, which still holds the mutex and
                // only releases it by exiting. So the replacement always arrives to a taken mutex
                // and must wait it out rather than refuse itself -- otherwise auto-recovery reliably
                // does nothing, which is the worst kind of recovery: the kind you believe you have.
                if (!createdNew && relaunch)
                {
                    for (int i = 0; i < 40 && !createdNew; i++)   // up to ~10s
                    {
                        Thread.Sleep(250);
                        try { createdNew = mx.WaitOne(0); } catch (AbandonedMutexException) { createdNew = true; }
                    }
                    Log.Info(createdNew
                        ? "relaunch: acquired the instance mutex after the crashed process exited"
                        : "relaunch: mutex still held after 10s -- refusing to start a second strip");
                }

                if (!createdNew)
                {
                    // Never a second strip, never a silent no-op.
                    Console.Error.WriteLine("Command Center command center is already running.");
                    return 0;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                // A UI exception is survivable -- one bad repaint must not take the strip down --
                // so log it and keep running. Anything reaching the AppDomain handler is not.
                Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e)
                { Log.Err("unhandled UI exception", e.Exception); };
                AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
                { Crash(e.ExceptionObject); };

                try { Application.Run(new StripContext()); }
                catch (Exception ex) { Crash(ex); return 4; }
            }
            return 0;
        }

        /// <summary>
        /// Give the diagnostic modes a working stdout. /target:winexe is deliberate -- /target:exe
        /// flashes a black console on every login -- but it means no console exists at startup and
        /// the CLR caches a null stdout on first touch. AttachConsole alone therefore leaves
        /// Console.WriteLine writing into nothing: the first -Selftest run printed zero bytes and
        /// still exited 0. Opening CONOUT$ by name and binding a writer to it is what actually works.
        /// </summary>
        private static void BindConsole()
        {
            try
            {
                IntPtr std = Native.GetStdHandle(Native.STD_OUTPUT_HANDLE);
                bool redirected = std != IntPtr.Zero && std != Native.INVALID_HANDLE_VALUE;

                IntPtr h;
                if (redirected)
                {
                    h = std;                       // a pipe or file the parent set up -- use it
                }
                else
                {
                    if (!Native.AttachConsole(Native.ATTACH_PARENT_PROCESS)) Native.AllocConsole();
                    h = Native.CreateFile("CONOUT$", Native.GENERIC_WRITE,
                        Native.FILE_SHARE_WRITE | Native.FILE_SHARE_READ, IntPtr.Zero,
                        Native.OPEN_EXISTING, 0, IntPtr.Zero);
                }
                if (h == IntPtr.Zero || h == Native.INVALID_HANDLE_VALUE) return;

                // ownsHandle:false when it came from GetStdHandle -- closing the parent's pipe on
                // exit would be rude at best.
                Microsoft.Win32.SafeHandles.SafeFileHandle sh =
                    new Microsoft.Win32.SafeHandles.SafeFileHandle(h, !redirected);
                FileStream fs = new FileStream(sh, FileAccess.Write);
                StreamWriter w = new StreamWriter(fs, new UTF8Encoding(false));
                w.AutoFlush = true;
                Console.SetOut(w);
                Console.SetError(w);
            }
            catch { /* diagnostics must never be the reason the app fails to start */ }
        }

        private static bool Has(string[] a, string flag)
        {
            for (int i = 0; i < a.Length; i++)
                if (string.Equals(a[i], flag, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>
        /// Write a crash file, then relaunch ourselves once and exit 4.
        ///
        /// NEVER SHOW A DIALOG. A modal error box on a locked machine at 3am is invisible, blocks
        /// the process from exiting, and therefore blocks anything from restarting it -- the app is
        /// then "running" forever while showing nothing.
        ///
        /// The relaunch is what makes this a thing you can leave alone: autostart only fires at
        /// login, so without it a 3am crash means no strip until the next reboot, and the officers
        /// keep running with nobody watching. It is deliberately NOT a loop -- restarting is gated
        /// on having stayed up past RestartMinUptimeMin and on a stamp file counting restarts in the
        /// last hour, because a strip that crashes on startup and relaunches instantly is a fork
        /// bomb wearing a recovery hat.
        /// </summary>
        private const int RestartMinUptimeMin = 5;
        private const int RestartMaxPerHour = 3;

        /// <summary>
        /// -Crashafter N: throw an unhandled exception on a background thread N seconds after start.
        ///
        /// Same reasoning that produced -Simulate. Crash recovery that has never been fired is a
        /// claim, not a feature, and this one has two guards and a mutex handoff that are exactly
        /// the kind of thing that looks right and does nothing. Firing it on demand is the only way
        /// to know. A background thread rather than the UI thread deliberately: Application.
        /// ThreadException swallows UI exceptions on purpose, so throwing there would test the
        /// survivable path, not the fatal one.
        /// </summary>
        internal static int CrashAfterSeconds;
        private static System.Threading.Timer _crashTimer;

        internal static void ArmCrashTest()
        {
            if (CrashAfterSeconds <= 0) return;
            Log.Warn("CRASHTEST armed -- will throw on a background thread in " +
                     CrashAfterSeconds + "s");
            _crashTimer = new System.Threading.Timer(delegate
            {
                throw new InvalidOperationException(
                    "deliberate -Crashafter test crash (" + CrashAfterSeconds + "s)");
            }, null, CrashAfterSeconds * 1000, System.Threading.Timeout.Infinite);
        }

        private static void Crash(object err)
        {
            string epoch = ((long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds)
                .ToString(CultureInfo.InvariantCulture);
            string path = null;
            try
            {
                path = System.IO.Path.Combine(Paths.Logs, "crash-" + epoch + ".txt");
                Exception ex = err as Exception;
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("Command Center taskbar strip crash");
                sb.AppendLine("when    " + DateTime.Now.ToString("o"));
                sb.AppendLine("pid     " + Process.GetCurrentProcess().Id);
                sb.AppendLine("uptime  " + UptimeMinutes().ToString("F1") + " min");
                sb.AppendLine("ticks   " + Supervisor.TickCount);
                sb.AppendLine();
                sb.AppendLine(ex != null ? ex.ToString() : Convert.ToString(err));
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            }
            catch { }

            Log.Err("CRASH -- see " + (path ?? "(crash file could not be written)"));
            PruneCrashFiles();

            try { if (RestartAllowed()) RelaunchSelf(); }
            catch (Exception ex2) { Log.Err("crash: relaunch failed", ex2); }

            Environment.Exit(4);
        }

        /// <summary>
        /// Keep the ten most recent crash files. The log itself rotates at 2MB; these did not
        /// rotate at all, so a repeatedly-crashing build would quietly fill the directory with
        /// files nobody reads past the first two. Ten is enough to see a pattern.
        /// </summary>
        private static void PruneCrashFiles()
        {
            try
            {
                string[] files = Directory.GetFiles(Paths.Logs, "crash-*.txt");
                if (files.Length <= 10) return;
                Array.Sort(files, delegate(string a, string b)
                { return File.GetLastWriteTimeUtc(b).CompareTo(File.GetLastWriteTimeUtc(a)); });
                for (int i = 10; i < files.Length; i++) { try { File.Delete(files[i]); } catch { } }
                Log.Info("crash: pruned " + (files.Length - 10) + " old crash file(s)");
            }
            catch { }
        }

        private static double UptimeMinutes()
        {
            try { return (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMinutes; }
            catch { return 0; }
        }

        /// <summary>
        /// Restart only if we were actually up for a while AND have not already burned through the
        /// hourly budget. Both conditions matter: the uptime test catches crash-on-startup, the
        /// counter catches a slow crash loop that clears the uptime bar every time.
        /// </summary>
        private static bool RestartAllowed()
        {
            if (UptimeMinutes() < RestartMinUptimeMin)
            {
                Log.Warn(string.Format("crash: up only {0:F1} min (< {1}) -- not relaunching, this " +
                    "looks like a startup crash and a loop would make it worse",
                    UptimeMinutes(), RestartMinUptimeMin));
                return false;
            }

            string stamp = Paths.State1("restarts.txt");
            List<string> keep = new List<string>();
            try
            {
                if (File.Exists(stamp))
                {
                    string[] lines = File.ReadAllLines(stamp);
                    for (int i = 0; i < lines.Length; i++)
                    {
                        DateTime t;
                        if (DateTime.TryParse(lines[i], CultureInfo.InvariantCulture,
                                DateTimeStyles.RoundtripKind, out t) &&
                            (DateTime.Now - t).TotalHours < 1) keep.Add(lines[i]);
                    }
                }
            }
            catch { }

            if (keep.Count >= RestartMaxPerHour)
            {
                Log.Err("crash: " + keep.Count + " restarts in the last hour -- STAYING DOWN. " +
                        "Something is repeatably wrong; -Health will report DOWN until it is fixed.");
                return false;
            }

            keep.Add(DateTime.Now.ToString("o"));
            try { File.WriteAllLines(stamp, keep.ToArray(), new UTF8Encoding(false)); } catch { }
            return true;
        }

        private static void RelaunchSelf()
        {
            string exe = Application.ExecutablePath;
            ProcessStartInfo psi = new ProcessStartInfo(exe);
            psi.WorkingDirectory = System.IO.Path.GetDirectoryName(exe);
            psi.UseShellExecute = false;
            Supervisor.ScrubSessionEnv(psi);
            // -relaunch tells the replacement to WAIT for the mutex instead of refusing itself.
            // We still hold it here and only release by exiting, so the new process necessarily
            // starts while it is taken. Sleeping on this side would not help -- it would just delay
            // the same collision -- so the wait has to happen over there.
            psi.Arguments = "-relaunch";
            Process.Start(psi);
            Log.Info("crash: relaunched " + exe);
        }

        /// <summary>
        /// -Health: is the strip actually alive, and is what it is showing current?
        ///
        /// Exit codes are the point -- this is meant to be callable from a script or a check without
        /// parsing prose. 0 = alive and beating. 2 = not running. 3 = a process exists but the beat
        /// is stale, which is the nastiest case: the strip is still on screen showing whatever it
        /// last painted, so it LOOKS fine while its state is frozen. That is the false-green this
        /// whole thing exists to kill, and it deserves a distinct code.
        /// </summary>
        private static int Health()
        {
            bool others = ResidentInstanceExists();

            string hb = Paths.State1("heartbeat.json");
            if (!File.Exists(hb))
            {
                Console.WriteLine(others
                    ? "STARTING  strip up, no heartbeat written yet"
                    : "DOWN      not running, no heartbeat on disk");
                return others ? 3 : 2;
            }

            string j = File.ReadAllText(hb);
            DateTime beat;
            if (!DateTime.TryParse(Grab(j, "beatAt"), CultureInfo.InvariantCulture,
                                   DateTimeStyles.RoundtripKind, out beat))
            { Console.WriteLine("UNKNOWN   heartbeat.json is unparseable"); return 3; }

            double age = (DateTime.Now - beat).TotalSeconds;
            string detail = string.Format(
                "pid={0} ticks={1} paused={2} inflight={3} gdi={4} user={5} ws={6}MB beat={7:F0}s ago",
                Grab(j, "pid"), Grab(j, "ticks"), Grab(j, "paused"), Grab(j, "inFlight"),
                Grab(j, "gdi"), Grab(j, "user"), Grab(j, "wsMB"), age);

            if (!others) { Console.WriteLine("DOWN      " + detail); return 2; }
            // Tick is 60s; 150s is two missed beats plus slack for a resume-from-sleep stall.
            if (age > 150) { Console.WriteLine("STALE     " + detail); return 3; }
            Console.WriteLine("OK        " + detail);
            return 0;
        }

        /// <summary>
        /// Every state must clear the WCAG 3.0:1 non-text floor on BOTH taskbar themes, measured
        /// with its alpha composited over the background. `never` is the one deliberate exception.
        ///
        /// This exists because the original figures were reasoned rather than measured and one of
        /// them was wrong for months: idle was recorded at 3.0:1 and actually rendered at 2.59:1.
        /// A palette is exactly the kind of thing someone tweaks by eye later, so the floor is now
        /// asserted by a command instead of by a comment.
        /// </summary>
        private static string ContrastCheck()
        {
            bool savedLight = Theme.IsLight;
            double savedLum = Theme.BackgroundLuminance;
            List<string> bad = new List<string>();
            double worst = 99;
            string worstName = "";

            try
            {
                IconState[] states = new IconState[] {
                    IconState.Idle, IconState.Due, IconState.Working,
                    IconState.Attention, IconState.Fault };

                for (int t = 0; t < 2; t++)
                {
                    Theme.IsLight = t == 0;
                    Theme.BackgroundLuminance = Theme.IsLight ? 224.0 : 32.0;
                    string theme = Theme.IsLight ? "light" : "dark";

                    for (int i = 0; i < states.Length; i++)
                    {
                        Color c; int a;
                        Palette.For(states[i], out c, out a);
                        double ratio = Theme.ContrastAgainstBackground(c, a);
                        if (ratio < worst)
                        { worst = ratio; worstName = IconRenderer.StateName(states[i]) + "/" + theme; }
                        if (ratio < 3.0)
                            bad.Add(string.Format(CultureInfo.InvariantCulture, "{0} on {1} = {2:F2}:1",
                                IconRenderer.StateName(states[i]), theme, ratio));
                    }

                    // THE BRAND TILES TOO. A gate that only reads Palette would print "ok" while the
                    // marks the user actually sees sat below the floor -- the earlier icon draft
                    // brightened its own tops toward white and would have shipped at 1.65:1 with a
                    // green selftest. Measured at the state that desaturates most while still being
                    // a working mark, because that is the worst case the ladder produces.
                    for (int i = 0; i < BrandMarks.Keys.Length; i++)
                    {
                        string key = BrandMarks.Keys[i];
                        Color toned = BrandMarks.Toned(BrandMarks.BodyOf(key), IconState.Idle);
                        double ratio = Theme.ContrastAgainstBackground(toned, 210);
                        if (ratio < worst) { worst = ratio; worstName = key + "/" + theme; }
                        if (ratio < 3.0)
                            bad.Add(string.Format(CultureInfo.InvariantCulture, "mark {0} on {1} = {2:F2}:1",
                                key, theme, ratio));
                    }
                }
            }
            finally
            {
                Theme.IsLight = savedLight;
                Theme.BackgroundLuminance = savedLum;
            }

            if (bad.Count > 0) return "FAIL below 3.0:1 -- " + string.Join(", ", bad.ToArray());
            return string.Format(CultureInfo.InvariantCulture,
                "ok (worst {0} at {1:F2}:1, floor 3.0)", worstName, worst);
        }

        /// <summary>
        /// -IconSheet &lt;path.png&gt;: render every mark, in every state, at every size, on BOTH a light
        /// and a dark taskbar, into one contact sheet.
        ///
        /// Icons are the one part of this app that cannot be verified by an exit code. Screenshotting
        /// the live taskbar only ever shows six marks in whatever states they happen to be in today,
        /// on whichever theme is set -- so 'fault on a dark bar at 0.8x' would ship unseen. This
        /// renders the whole matrix in about a second and it is the only honest way to review a
        /// drawing change.
        /// </summary>
        private static int IconSheet(string path)
        {
            try
            {
                IconState[] states = new IconState[] {
                    IconState.Never, IconState.Idle, IconState.Due,
                    IconState.Working, IconState.Attention, IconState.Fault };
                double[] scales = new double[] { 0.80, 1.00, 1.50, 1.91 };
                bool[] themes = new bool[] { true, false };

                int cell = 64, pad = 8, labelW = 150, header = 34;
                int cols = 6;                                  // the six marks
                int rowsPerBlock = states.Length;
                int blockH = header + rowsPerBlock * (cell + pad) + 18;
                int totalH = 40 + themes.Length * scales.Length * blockH;
                int totalW = labelW + cols * (cell + pad) + 24;

                using (Bitmap sheet = new Bitmap(totalW, totalH, PixelFormat.Format32bppArgb))
                using (Graphics sg = Graphics.FromImage(sheet))
                using (Font hf = new Font("Segoe UI", 12f, FontStyle.Bold))
                using (Font lf = new Font("Segoe UI", 9f))
                {
                    sg.Clear(Color.FromArgb(0x1E, 0x1E, 0x1E));
                    sg.SmoothingMode = SmoothingMode.AntiAlias;
                    sg.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

                    using (SolidBrush tb = new SolidBrush(Color.White))
                        sg.DrawString("Command Center strip icons -- every mark, state, size and theme", hf, tb, 12, 10);

                    int y = 40;
                    for (int t = 0; t < themes.Length; t++)
                    {
                        for (int si = 0; si < scales.Length; si++)
                        {
                            double s = scales[si];
                            Theme.IsLight = themes[t];
                            // The real taskbar luminance on each theme, so any contrast logic that
                            // reads it behaves here exactly as it does live.
                            Theme.BackgroundLuminance = themes[t] ? 224.0 : 32.0;
                            Color bg = themes[t] ? Color.FromArgb(0xE8, 0xEC, 0xF2)
                                                 : Color.FromArgb(0x20, 0x20, 0x20);
                            Color fg = themes[t] ? Color.Black : Color.White;

                            int blockTop = y;
                            using (SolidBrush bb = new SolidBrush(bg))
                                sg.FillRectangle(bb, 0, blockTop, totalW, blockH);

                            using (SolidBrush tb = new SolidBrush(fg))
                                sg.DrawString(string.Format(CultureInfo.InvariantCulture,
                                    "{0} taskbar    scale {1:F2}x    glyph box {2}px",
                                    themes[t] ? "LIGHT" : "DARK", s, (int)Math.Round(22 * s)),
                                    hf, tb, 12, blockTop + 8);

                            int ry = blockTop + header;
                            for (int r = 0; r < states.Length; r++)
                            {
                                using (SolidBrush tb = new SolidBrush(fg))
                                    sg.DrawString(IconRenderer.StateName(states[r]), lf, tb, 12, ry + cell / 2 - 8);

                                Color colour; int alpha;
                                Palette.For(states[r], out colour, out alpha);

                                string[] keys = new string[] { "core", "Sell", "Attract", "Capital", "Machine", "asks" };
                                for (int c = 0; c < keys.Length; c++)
                                {
                                    Rectangle b = new Rectangle(labelW + c * (cell + pad), ry, cell, cell);
                                    IconRenderer.PaintOne(sg, keys[c], b, s, colour, alpha, states[r]);
                                }
                                ry += cell + pad;
                            }

                            y += blockH;
                        }
                    }
                    sheet.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                }
                Console.WriteLine("wrote " + path);
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("icon sheet failed: " + ex.Message);
                return 1;
            }
        }

        public const string QuitEventName = @"Global\CommandCenterQuit";

        /// <summary>
        /// -Quit: ask the resident strip to stop the way the tray's Quit does.
        ///
        /// Everything used to reach for Stop-Process, which is TerminateProcess: Shutdown() never
        /// runs, so the NotifyIcon is never hidden and Windows leaves a ghost tray icon behind until
        /// something makes the notification area repaint, and any unflushed UI settings are lost.
        /// build.ps1 also refuses to overwrite a running binary, so every rebuild needed a kill.
        /// A named event costs nothing and makes the ordinary path the graceful one.
        /// </summary>
        private static int QuitRunning()
        {
            EventWaitHandle ev;
            if (!EventWaitHandle.TryOpenExisting(QuitEventName, out ev))
            {
                Console.WriteLine(ResidentInstanceExists()
                    ? "running, but too old to accept -Quit -- stop it from the tray"
                    : "not running");
                return ResidentInstanceExists() ? 3 : 2;
            }

            ev.Set();
            for (int i = 0; i < 60; i++)          // up to 6s
            {
                Thread.Sleep(100);
                if (!ResidentInstanceExists()) { Console.WriteLine("stopped"); return 0; }
            }
            Console.WriteLine("still running after 6s -- it did not honour the quit request");
            return 3;
        }

        /// <summary>
        /// Is the RESIDENT strip running? Tested by the single-instance mutex, not by counting
        /// processes named CommandCenter.
        ///
        /// Counting by name is wrong because every diagnostic mode -- including -Health itself --
        /// is that same binary. Two health checks in a row saw each other and reported the strip as
        /// alive when nothing was on the taskbar at all. A health check that can invent its own
        /// subject is worse than none: it is the false-green this app exists to eliminate, produced
        /// by the tool that measures it. The mutex is exact -- only the resident UI instance takes
        /// it, and it is released the instant that process dies, however it dies.
        /// </summary>
        private static bool ResidentInstanceExists()
        {
            try { using (Mutex.OpenExisting(MutexName)) return true; }
            catch (WaitHandleCannotBeOpenedException) { return false; }
            catch (UnauthorizedAccessException) { return true; }   // exists, owned by another user
            catch { return false; }
        }

        private static string Grab(string j, string key)
        {
            System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(
                j, "\"" + key + "\"\\s*:\\s*\"?([^\",}]+)\"?");
            return m.Success ? m.Groups[1].Value : "?";
        }

        private static string Value(string[] a, string flag)
        {
            for (int i = 0; i < a.Length - 1; i++)
                if (string.Equals(a[i], flag, StringComparison.OrdinalIgnoreCase)) return a[i + 1];
            return null;
        }

        private static int IntValue(string[] a, string flag, int dflt)
        {
            string v = Value(a, flag);
            int n;
            if (v != null && int.TryParse(v, out n)) return n;
            return dflt;
        }

        /// <summary>
        /// -Simulate &lt;officer|*&gt; -Mode clean|fault|working|clear [-Seconds N]
        /// Forces each exit class with a harmless stand-in process through the real Classify path.
        /// `working` holds for -Seconds and then expires on its own; `clear` ends it early.
        /// </summary>
        private static int Simulate(string[] args, string officer)
        {
            StateStore.Refresh();
            string mode = Value(args, "-mode");
            if (mode == null) mode = "fault";
            int seconds = IntValue(args, "-seconds", 2);

            string result;
            if (mode == "clear") result = Supervisor.ClearSimulation(officer);
            else if (mode == "working") result = Supervisor.SimulateWorking(officer, Math.Max(seconds, 20));
            else if (mode == "clean") result = Supervisor.Simulate(officer, 0, seconds, true);
            else result = Supervisor.Simulate(officer, 1, seconds, false);

            Console.WriteLine("simulate=" + result);
            StateStore.Refresh();
            for (int i = 0; i < StateStore.Desks.Count; i++)
                Console.WriteLine("desk=" + StateStore.Desks[i].Name + ":" +
                                  IconRenderer.StateName(StateStore.Desks[i].State));
            return 0;
        }

        /// <summary>
        /// First launch on a machine where .command-center does not exist. PAUSED is the only legal
        /// initial state -- an installer that starts thirteen permissioned agents by existing is
        /// not an installer, it is an incident.
        /// </summary>
        private static void FirstRun()
        {
            string sup = Paths.State1("supervisor.json");
            if (!File.Exists(sup))
            {
                try
                {
                    File.WriteAllText(sup,
                        "{\"paused\":true,\"reason\":\"first run\",\"lastTick\":null,\"version\":1}\n",
                        new UTF8Encoding(false));
                    Log.Info("first run: created supervisor.json (paused)");
                }
                catch (Exception ex) { Log.Err("first run: cannot write supervisor.json", ex); }
            }

            try
            {
                // roster.json is GENERATED, never hand-authored -- it would otherwise become a
                // sixth copy of a roster that already drifts across five files.
                string rj = Paths.State1("roster.json");
                string want = Roster.ToJson();
                string have = File.Exists(rj) ? File.ReadAllText(rj) : null;
                if (have != want) File.WriteAllText(rj, want, new UTF8Encoding(false));
            }
            catch (Exception ex) { Log.Warn("first run: roster.json: " + ex.Message); }
        }

        // ---- -Selftest -----------------------------------------------------------------------
        // Machine-checkable, one key=value per line, LF. This is a diff target for the phase gates,
        // not prose. Exit 0 = all invariants hold, 1 = geometry unresolved, 2 = roster drift.

        private static int SelfTest()
        {
            IntPtr tray = Native.FindWindow("Shell_TrayWnd", null);
            if (tray == IntPtr.Zero) { Console.WriteLine("taskbar=NOT_FOUND"); return 1; }

            Native.RECT tr;
            Native.GetWindowRect(tray, out tr);
            uint dpi = Native.DpiOf(tray);

            string trace;
            Span span = Geometry.Scan(tray, IntPtr.Zero, tr, dpi, out trace);
            Theme.Refresh();
            StateStore.Refresh();

            Console.WriteLine(string.Format(
                "taskbar=Shell_TrayWnd rect={0},{1},{2},{3} dpi={4} theme={5} accent=#{6:X2}{7:X2}{8:X2}",
                tr.Left, tr.Top, tr.Right, tr.Bottom, dpi, Theme.IsLight ? "light" : "dark",
                Theme.Accent.R, Theme.Accent.G, Theme.Accent.B));

            IntPtr rebar = Native.FindWindowEx(tray, IntPtr.Zero, "ReBarWindow32", null);
            Native.RECT rb = new Native.RECT();
            if (rebar != IntPtr.Zero) Native.GetWindowRect(rebar, out rb);

            double s = dpi / 96.0;
            LayoutTier tier = span == null ? LayoutTier.Micro
                                           : IconRenderer.PickTier(span.Width, s, LayoutTier.Full);
            Console.WriteLine(string.Format(
                "rebar={0},{1},{2},{3} leftBound={4} rightBound={5} runway={6} layout=desk cells=6 tier={7}",
                rb.Left, rb.Top, rb.Right, rb.Bottom,
                span == null ? -1 : span.Left, span == null ? -1 : span.Right,
                span == null ? -1 : span.Width, tier));

            string limit = StateStore.IsStoodDown()
                ? "until " + StateStore.LimitUntil.ToString("o") : "none";
            Console.WriteLine(string.Format(
                "supervisor={0} inflight={1} limit={2} budget={3}/{4} daily={5} sendgate={6}",
                StateStore.Paused ? "PAUSED" : "RUNNING", Supervisor.InFlight(), limit,
                StateStore.BudgetUsed, StateStore.BudgetCeiling, StateStore.BudgetDaily,
                Supervisor.BudgetExhausted()
                    ? "CLOSED (outbound/inbound/funding/partner held)" : "open"));

            List<string> never = new List<string>();
            foreach (KeyValuePair<string, OfficerState> kv in StateStore.Officers)
                if (kv.Value.State == IconState.Never) never.Add(kv.Key);
            never.Sort();
            Console.WriteLine("never=" + (never.Count == 0 ? "-" : string.Join(",", never.ToArray())));

            StringBuilder desks = new StringBuilder();
            for (int i = 0; i < StateStore.Desks.Count; i++)
            {
                if (i > 0) desks.Append(" ");
                desks.Append("desk=" + StateStore.Desks[i].Name + ":" +
                             IconRenderer.StateName(StateStore.Desks[i].State));
            }
            desks.Append(" asks=" + StateStore.TotalOpenAsks);
            Console.WriteLine(desks.ToString());

            int rosterN = Roster.Load().Count;
            bool rosterOk = rosterN == 13;
            int stalePins;
            int goodPins = Supervisor.PinnedCount(out stalePins);
            Console.WriteLine(string.Format("roster={0}({1}) pins={2}/{3}{4} suite={5} topmostTray={6} exit={7}",
                rosterOk ? "ok" : "DRIFT", rosterN, goodPins, rosterN,
                stalePins > 0 ? " STALE:" + stalePins + " (they will FAULT at the pin gate)" : "",
                StateStore.SuiteDrift ? "DRIFT" : "clean",
                (Native.GetWindowLong(tray, Native.GWL_EXSTYLE) & Native.WS_EX_TOPMOST) != 0,
                span == null ? 1 : (rosterOk ? 0 : 2)));

            Console.WriteLine("brief=" + BriefStatusCheck());
            Console.WriteLine("envscrub=" + EnvScrubCheck());
            Console.WriteLine("contrast=" + ContrastCheck());
            Console.WriteLine("limit=" + Supervisor.LimitAccountStatus());

            UiSettings.Load();
            Native.RECT trayRect;
            int trayH = Native.GetWindowRect(tray, out trayRect) ? trayRect.Height : 0;
            double eff = UiSettings.EffectiveScale(s, span == null ? 0 : span.Width, trayH);
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "ui=scale{0:F2} gap{1:F2} opacity{2:F2} effective={3:F2}{4} needs={5}px runway={6}px",
                UiSettings.Scale, UiSettings.Gap, UiSettings.Opacity, eff,
                Math.Abs(eff - UiSettings.Scale) > 0.02 ? " (CAPPED)" : "",
                IconRenderer.WidthFor(LayoutTier.Full, s * eff),
                span == null ? 0 : span.Width));

            // A hard prerequisite nothing else surfaces. `claude` on PATH is a POSIX sh shim that
            // Process.Start cannot exec, so the supervisor resolves claude.cmd by absolute path from
            // two known install locations. If a scoop update or an npm reinstall moves it, the strip
            // still renders perfectly and every single dispatch faults with a message buried in a
            // log nobody opens -- thirteen departments dead, and a green taskbar saying otherwise.
            string claude = Supervisor.ResolveClaude();
            Console.WriteLine("claude=" + (claude == null
                ? "NOT FOUND -- every dispatch would fault"
                : "ok " + claude));

            if (span == null) { Console.WriteLine("geometry=UNRESOLVED " + trace); return 1; }
            if (!rosterOk) return 2;
            if (claude == null) return 3;
            return 0;
        }

        /// <summary>
        /// Does the SessionStart brief have a live read of us?
        ///
        /// `dispatch-status` was written into the design (supervisor.md:714, "Supervisor, every
        /// tick") and never implemented. Nothing tested for it, so for the supervisor's entire life
        /// the brief took its file-absent branch and told every session -- the operator included --
        /// "No dispatcher has ever run" while officers were being dispatched that minute. Selftest
        /// reported green throughout, because it only ever checked what the supervisor DID, never
        /// what the rest of the system could SEE of it.
        ///
        /// Staleness is the assertion that matters. The file existing proves only that some past
        /// build wrote it once; a fresh mtime proves this build still does.
        /// </summary>
        private static string BriefStatusCheck()
        {
            try
            {
                string p = Path.Combine(Paths.HookCache, "dispatch-status");
                if (!File.Exists(p))
                    return "MISSING (the brief will claim no dispatcher has ever run)";

                double ageMin = (DateTime.Now - new FileInfo(p).LastWriteTime).TotalMinutes;
                string first = File.ReadAllText(p).Replace("\n", "").Replace("\r", "");
                string excl = "";
                string xp = Path.Combine(Paths.HookCache, "dispatch-excluded");
                if (File.Exists(xp))
                {
                    string x = File.ReadAllText(xp).Trim();
                    if (x.Length > 0) excl = " excluded=" + x.Replace("\r", "").Replace("\n", ";");
                }
                // 3 min = three missed ticks. Tighter false-alarms on a busy machine; looser and a
                // dead publisher hides behind "probably just slow".
                string verdict = ageMin > 3
                    ? "STALE(" + ((int)ageMin) + "m -- the brief is reading a dead publisher)"
                    : "ok";
                return verdict + " [" + first + "]" + excl;
            }
            catch (Exception ex) { return "ERR " + ex.Message; }
        }

        /// <summary>
        /// Prove -- not assume -- that a child spawned through our ProcessStartInfo path carries no
        /// inherited Claude session markers.
        ///
        /// This is a gate check rather than a one-off because the failure is silent and expensive:
        /// a `claude` that inherits CLAUDE_CODE_CHILD_SESSION saves no transcript, so an officer
        /// dispatched with it leaves no audit trail, and the only visible symptom is a warning
        /// banner on a terminal nobody is reading at 3am. It surfaced on 2026-08-04 only because
        /// the operator clicked the Command Center button and read the banner.
        ///
        /// Deliberately poisons the child's environment first, so the check fails if ScrubSessionEnv
        /// is ever removed -- testing a clean parent would pass no matter what the code did.
        /// </summary>
        private static string EnvScrubCheck()
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", "/c set");
                psi.UseShellExecute = false;   // before EnvironmentVariables is touched
                psi.EnvironmentVariables["CLAUDE_CODE_CHILD_SESSION"] = "1";
                psi.EnvironmentVariables["CLAUDE_CODE_SESSION_ID"] = "selftest";
                psi.EnvironmentVariables["CLAUDECODE"] = "1";
                psi.EnvironmentVariables["CLAUDE_PID"] = "1";
                psi.EnvironmentVariables["CLAUDE_CONFIG_DIR"] = "keepme";

                Supervisor.ScrubSessionEnv(psi);

                string outp;
                if (Supervisor.Capture(psi, 5000, out outp) == Supervisor.TimedOut)
                    return "ERROR(timed out)";

                int leaked = 0;
                string[] lines = outp.Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    string u = lines[i].ToUpperInvariant();
                    if (u.StartsWith("CLAUDE_CODE_") || u.StartsWith("CLAUDECODE=") ||
                        u.StartsWith("CLAUDE_PID=")) leaked++;
                }
                bool keptConfig = outp.ToUpperInvariant().Contains("CLAUDE_CONFIG_DIR=KEEPME");
                if (leaked == 0 && keptConfig) return "ok(0 leaked, config preserved)";
                return "FAIL(" + leaked + " leaked, config kept=" + keptConfig + ")";
            }
            catch (Exception ex) { return "ERROR(" + ex.Message + ")"; }
        }

        private static int Calibrate()
        {
            IntPtr tray = Native.FindWindow("Shell_TrayWnd", null);
            if (tray == IntPtr.Zero) { Console.WriteLine("no taskbar"); return 1; }
            Native.RECT tr;
            Native.GetWindowRect(tray, out tr);
            uint dpi = Native.DpiOf(tray);
            string trace;
            Span span = Geometry.Scan(tray, IntPtr.Zero, tr, dpi, out trace);
            if (span == null) { Console.WriteLine("scan failed: " + trace); return 1; }

            string j = string.Format(
                "{{\"left\":{0},\"w\":{1},\"dpi\":{2},\"trayW\":{3},\"measuredAt\":\"{4}\",\"method\":\"variance\"}}\n",
                span.Left, span.Width, dpi, tr.Width, DateTime.Now.ToString("o"));
            File.WriteAllText(Paths.State1("strip.primary.json"), j, new UTF8Encoding(false));
            Console.WriteLine(j.Trim());
            Console.WriteLine("trace: " + trace);
            return 0;
        }
    }

    /// <summary>The message pump: strip, tray icon, and the supervisor tick.</summary>
    internal class StripContext : ApplicationContext
    {
        private StripWindow _strip;
        private NotifyIcon _tray;
        private EventWaitHandle _quitEvent;
        private System.Windows.Forms.Timer _tick;
        private System.Windows.Forms.Timer _debounce;
        private Control _marshal;

        public StripContext()
        {
            StateStore.Refresh();

            IntPtr tray = Native.FindWindow("Shell_TrayWnd", null);
            if (tray == IntPtr.Zero) { Log.Err("no Shell_TrayWnd -- cannot start"); ExitThread(); return; }

            // Before the strip exists -- the first paint must already be at the operator's size, or
            // the strip visibly snaps from default to chosen a frame after login.
            UiSettings.Load();

            _strip = new StripWindow();
            _strip.CellClicked += OnCellClicked;
            if (!_strip.Create(tray)) { Log.Err("strip creation failed"); ExitThread(); return; }

            BuildTray();

            // FileSystemWatcher fires on a threadpool thread; every UI touch must be marshalled
            // back or we corrupt the render mid-frame. A 250 ms debounce collapses the burst of
            // events a single atomic write produces (tmp created, renamed, old deleted).
            _debounce = new System.Windows.Forms.Timer();
            _debounce.Interval = 250;
            _debounce.Tick += delegate(object s, EventArgs e)
            {
                _debounce.Stop();
                StateStore.Refresh();
                if (_strip != null) _strip.Render();
                UpdateTrayText();
            };
            _marshal = new Control();
            _marshal.CreateControl();

            // Listen for -Quit from another process, so stopping this is a graceful Shutdown()
            // rather than a TerminateProcess that strands the tray icon. Held in a field because a
            // collected EventWaitHandle would close the handle and the signal would go nowhere.
            try
            {
                _quitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.QuitEventName);
                Thread qt = new Thread(delegate()
                {
                    try { _quitEvent.WaitOne(); } catch { return; }
                    try
                    {
                        if (_marshal != null && _marshal.IsHandleCreated)
                            _marshal.BeginInvoke((MethodInvoker)delegate { Shutdown(); });
                    }
                    catch { }
                });
                qt.IsBackground = true;      // must never hold the process open by itself
                qt.Start();
            }
            catch (Exception ex) { Log.Warn("quit listener unavailable: " + ex.Message); }
            StateStore.StartWatching(delegate
            {
                try
                {
                    if (_marshal.IsHandleCreated)
                        _marshal.BeginInvoke((MethodInvoker)delegate { _debounce.Stop(); _debounce.Start(); });
                }
                catch { }
            });

            _tick = new System.Windows.Forms.Timer();
            _tick.Interval = Supervisor.TickMs;
            _tick.Tick += delegate(object s, EventArgs e)
            {
                StateStore.Refresh();
                Supervisor.Tick();
                UpdateTrayText();
                if (_strip != null) _strip.Render();
                Heartbeat();
            };
            _tick.Start();
            // Both of these say "we are here" to a different reader, and the first Tick is a full
            // minute away. -Health has always been made truthful at startup this way; the session
            // brief deserves the same, because ITS file-absent branch prints "No dispatcher has
            // ever run" -- so on a fresh machine the first minute after login told every session
            // the opposite of the truth, which is the exact lie the publisher exists to end.
            Heartbeat();
            Supervisor.PublishLiveness();
            Program.ArmCrashTest();

            Log.Info("Command Center command center started -- " + (StateStore.Paused ? "PAUSED" : "RUNNING"));
        }

        private void BuildTray()
        {
            // Idempotent: "Reset to defaults" rebuilds the menu to show the reset slider positions,
            // and without this that would leave a second NotifyIcon in the tray every time.
            if (_tray != null)
            {
                try { _tray.Visible = false; _tray.Dispose(); } catch { }
                _tray = null;
            }

            _tray = new NotifyIcon();
            _tray.Icon = SystemIcons.Application;
            _tray.Visible = true;
            _tray.Text = "Command Center command center";

            ContextMenuStrip m = new ContextMenuStrip();

            ToolStripMenuItem go = new ToolStripMenuItem("Go -- start dispatching");
            go.Click += delegate { Supervisor.SetPaused(false, "tray: Go"); UpdateTrayText(); };
            m.Items.Add(go);

            ToolStripMenuItem pause = new ToolStripMenuItem("Pause -- stop dispatching (in-flight finish)");
            pause.Click += delegate { Supervisor.SetPaused(true, "tray: Pause"); UpdateTrayText(); };
            m.Items.Add(pause);

            m.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem board = new ToolStripMenuItem("Open the board (halo-status.sh)");
            board.Click += delegate { OpenBoard(); };
            m.Items.Add(board);

            ToolStripMenuItem coo = new ToolStripMenuItem("Talk to Command Center");
            coo.Click += delegate { CommandTerminal.Open(null); };
            m.Items.Add(coo);

            m.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem repin = new ToolStripMenuItem("Re-pin command files (after review)");
            repin.Click += delegate
            {
                int n = Supervisor.PinAll();
                _tray.ShowBalloonTip(3000, "Command Center", "Pinned " + n + " command files.", ToolTipIcon.Info);
            };
            m.Items.Add(repin);

            ToolStripMenuItem cal = new ToolStripMenuItem("Re-measure taskbar span");
            cal.Click += delegate { if (_strip != null) { _strip.Rescan(true); _strip.Render(); } };
            m.Items.Add(cal);

            ToolStripMenuItem logs = new ToolStripMenuItem("Open logs");
            logs.Click += delegate
            { try { System.Diagnostics.Process.Start("explorer.exe", Paths.Logs); } catch { } };
            m.Items.Add(logs);

            m.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem settings = new ToolStripMenuItem("Settings -- position, size, layout");
            settings.Click += delegate
            { SettingsForm.ShowSingleton(delegate { if (_strip != null) _strip.Relayout(); }); };
            m.Items.Add(settings);

            m.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem quit = new ToolStripMenuItem("Quit");
            quit.Click += delegate { Shutdown(); };
            m.Items.Add(quit);

            // Slider drags fire dozens of change events; the file is written once, here.
            m.Closed += delegate { UiSettings.Flush(); };

            _tray.ContextMenuStrip = m;
            _tray.DoubleClick += delegate { CommandTerminal.Open(null); };
            UpdateTrayText();
        }

        /// <summary>
        /// One line of proof-of-life per tick, to disk. A log that simply stops at 02:14 tells you
        /// the process died at 02:14; without a beat you cannot tell "died" from "nothing happened",
        /// and this app's whole job is being trusted about state. `-Health` reads this file, so a
        /// silent death is a question anyone can answer in one command instead of by squinting at a
        /// log tail. The GDI/USER handle counts are the early warning for a leak in a process that
        /// repaints fourteen layered windows every second for weeks at a time.
        /// </summary>
        private void Heartbeat()
        {
            try
            {
                // Disposed: this runs once a minute forever, and an undisposed Process holds a
                // kernel handle until a finalizer happens to reclaim it. A heartbeat that slowly
                // leaks handles is a poor advertisement for a leak detector.
                using (Process me = Process.GetCurrentProcess())
                {
                    int gdi = Native.GetGuiResources(me.Handle, 0);
                    int user = Native.GetGuiResources(me.Handle, 1);
                    long wsMB = me.WorkingSet64 / (1024 * 1024);

                    Supervisor.WriteAtomicPublic(Paths.State1("heartbeat.json"), string.Format(
                        "{{\"pid\":{0},\"startedAt\":\"{1}\",\"beatAt\":\"{2}\",\"ticks\":{3}," +
                        "\"paused\":{4},\"inFlight\":{5},\"gdi\":{6},\"user\":{7},\"wsMB\":{8}}}\n",
                        me.Id, me.StartTime.ToString("o"), DateTime.Now.ToString("o"),
                        Supervisor.TickCount, StateStore.Paused ? "true" : "false",
                        Supervisor.InFlight(), gdi, user,
                        wsMB.ToString(CultureInfo.InvariantCulture)));

                    if (Supervisor.TickCount % 60 == 0)      // hourly, so the log stays readable
                        Log.Info(string.Format("HB ticks={0} gdi={1} user={2} ws={3}MB inflight={4}",
                            Supervisor.TickCount, gdi, user, wsMB, Supervisor.InFlight()));
                }
            }
            catch { /* proof-of-life must never be the thing that ends the life */ }
        }

        private void UpdateTrayText()
        {
            if (_tray == null) return;
            string t;
            if (StateStore.IsStoodDown())
                t = "Command Center -- stood down (usage limit)";
            else
                t = string.Format("Command Center -- {0}, {1} in flight, {2} ask(s)",
                    StateStore.Paused ? "paused" : "running",
                    Supervisor.InFlight(), StateStore.TotalOpenAsks);
            if (t.Length > 63) t = t.Substring(0, 63);   // NotifyIcon.Text hard limit
            _tray.Text = t;
        }

        private void OnCellClicked(Cell c, bool right)
        {
            try
            {
                Tooltip.HideNow();

                // Anchor to where the strip was actually DRAWN, not to the free span -- once a
                // position nudge is set those are different, and every popup would open somewhere
                // the icons are not.
                int anchorX = _strip.OriginX + c.Bounds.X;
                int anchorTop = _strip.OriginY;

                if (c.Key == "core")
                {
                    // Left-click keeps its promise: one click to the COO, never a menu on the way.
                    // Right-click is the settings gesture, and it only offers a gear -- see
                    // SettingsForm for why it is not the settings window itself.
                    if (right)
                    {
                        GearButton.ShowAt(anchorX + c.Bounds.Width / 2, anchorTop,
                            delegate { SettingsForm.ShowSingleton(delegate { if (_strip != null) _strip.Relayout(); }); });
                        return;
                    }
                    CommandTerminal.Open(null);
                    return;
                }

                if (c.Key == "asks")
                {
                    FlyoutForm.ShowFor("Decisions waiting on you", Asks.Open(null), null,
                                       anchorX, anchorTop);
                    return;
                }

                // A desk: its open asks first, then its officers' last rows underneath.
                List<AskRecord> deskAsks = new List<AskRecord>();
                List<OfficerState> officers = new List<OfficerState>();
                for (int i = 0; i < StateStore.Desks.Count; i++)
                {
                    if (StateStore.Desks[i].Name != c.Key) continue;
                    officers = StateStore.Desks[i].Officers;
                    for (int k = 0; k < officers.Count; k++)
                        deskAsks.AddRange(Asks.Open(officers[k].Officer.Name));
                }
                FlyoutForm.ShowFor(c.Key, deskAsks, officers, anchorX, anchorTop);
            }
            catch (Exception ex) { Log.Err("click handler", ex); }
        }

        private void OpenBoard()
        {
            try
            {
                string bash = @"C:\Program Files\Git\bin\bash.exe";
                if (!File.Exists(bash)) { CommandTerminal.Open(null); return; }
                System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo();
                psi.FileName = "cmd.exe";
                psi.Arguments = "/k \"\"" + bash + "\" \"" +
                    Paths.StatusScript.Replace("\\", "/") + "\"\"";
                psi.WorkingDirectory = Paths.Repo;
                psi.UseShellExecute = true;
                System.Diagnostics.Process.Start(psi);
            }
            catch (Exception ex) { Log.Err("open board", ex); }
        }

        private void Shutdown()
        {
            try
            {
                UiSettings.Flush();   // a size change made seconds before Quit must survive it
                if (_tick != null) _tick.Stop();
                if (_debounce != null) _debounce.Stop();
                StateStore.StopWatching();
                if (_marshal != null) _marshal.Dispose();
                if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
                Tooltip.Dispose();
                if (_strip != null) _strip.Dispose();
                Log.Info("Command Center command center stopped");
            }
            catch { }
            ExitThread();
        }
    }
}
