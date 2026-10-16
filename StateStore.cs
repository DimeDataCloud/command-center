// StateStore.cs -- resolve every officer's state, and be honest about what the signal can prove.
//
// THE CORE CLAIM OF THIS PROJECT, AND ITS LIMIT. Ledger mtime cannot distinguish "idle" from
// "mid-iteration". Measured 2026-08-04 from .command-center-git\dispatch-logs: a real ops iteration ran
// 796 seconds (13.3 min) and the ledger mtime only advanced at 17:38:12, after it finished. Two
// further writers corrupt mtime independently of any iteration -- the Stop-hook autosync, and the
// automated Command Center bot's ~20-minute commits arriving through pull --rebase, which rewrite working-tree
// files. Ledger mtime is a lying liveness signal in BOTH directions.
//
// So `working` comes from a live process handle the supervisor holds, never from mtime. And
// `fault` is the two-signal AND that no trace-based agent-observability tool renders:
// process exited AND the ledger did not advance. There is a real instance of that on disk --
// ops-20260803-150934.log is 0 bytes: spawned, exited, produced nothing, and every existing
// instrument in this system reports that officer as merely "quiet".

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace CommandCenter
{
    internal class OfficerState
    {
        public Officer Officer;
        public IconState State;
        public int AgeMinutes;          // int.MaxValue == never run
        public string LastRow;          // last "## " heading in the ledger, title only
        public bool HasLedger;
        public int OpenAsks;
        public DateTime EnteredState;

        public string AgeText()
        {
            if (AgeMinutes == int.MaxValue) return "never";
            if (AgeMinutes < 60) return AgeMinutes + "m";
            if (AgeMinutes < 1440) return (AgeMinutes / 60) + "h";
            return (AgeMinutes / 1440) + "d";
        }
    }

    internal class DeskState
    {
        public string Name;
        public IconState State;
        public int NonIdleCount;
        public List<OfficerState> Officers;
        public DeskState() { Officers = new List<OfficerState>(); }
    }

    internal static class StateStore
    {
        public static Dictionary<string, OfficerState> Officers =
            new Dictionary<string, OfficerState>(StringComparer.OrdinalIgnoreCase);
        public static List<DeskState> Desks = new List<DeskState>();

        public static int TotalOpenAsks;
        public static bool SuiteDrift;
        public static string LimitUntilRaw;         // null when not stood down
        public static DateTime LimitUntil = DateTime.MinValue;
        public static bool Paused = true;           // fails safe: paused until proven otherwise
        private static string _lastPauseWhy;        // dedup key for the "cannot read pause state" warning
        public static int BudgetUsed = -1, BudgetCeiling = 900, BudgetDaily = -1;

        private static readonly Dictionary<string, IconState> Previous =
            new Dictionary<string, IconState>(StringComparer.OrdinalIgnoreCase);

        private static FileSystemWatcher _watcher;

        /// <summary>
        /// Watch the state directory so an ask raised by an officer lights the strip in about a
        /// second rather than on the next 30 s poll. THE POLL REMAINS AUTHORITATIVE -- watchers
        /// drop events under load and after some network/volume hiccups, so this is an optimisation
        /// that makes it feel instant, never the mechanism correctness depends on.
        /// </summary>
        public static void StartWatching(Action onChange)
        {
            try
            {
                Directory.CreateDirectory(Paths.State);
                _watcher = new FileSystemWatcher(Paths.State, "*.json");
                _watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size;
                FileSystemEventHandler h = delegate(object s, FileSystemEventArgs e) { onChange(); };
                _watcher.Created += h;
                _watcher.Changed += h;
                _watcher.Deleted += h;
                _watcher.Renamed += delegate(object s, RenamedEventArgs e) { onChange(); };
                _watcher.EnableRaisingEvents = true;
                Log.Info("state: watching " + Paths.State);
            }
            catch (Exception ex) { Log.Warn("state: watcher unavailable (polling only): " + ex.Message); }
        }

        public static void StopWatching()
        {
            try { if (_watcher != null) { _watcher.EnableRaisingEvents = false; _watcher.Dispose(); _watcher = null; } }
            catch { }
        }

        public static void Refresh()
        {
            try { Paths.EnsureDirs(); } catch { }

            ReadSupervisor();
            ReadLimit();
            ReadBudget();
            ReadSuite();
            Dictionary<string, int> asks = ReadAsks();

            List<Officer> roster = Roster.Load();
            DateTime now = DateTime.Now;

            Dictionary<string, OfficerState> fresh =
                new Dictionary<string, OfficerState>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < roster.Count; i++)
            {
                Officer o = roster[i];
                OfficerState st = new OfficerState();
                st.Officer = o;

                FileInfo fi = new FileInfo(o.LedgerPath);
                st.HasLedger = fi.Exists;
                if (st.HasLedger)
                {
                    st.AgeMinutes = (int)Math.Max(0, (now - fi.LastWriteTime).TotalMinutes);
                    st.LastRow = LastHeading(o.LedgerPath);
                }
                else
                {
                    st.AgeMinutes = int.MaxValue;
                    st.LastRow = null;
                }

                int a = 0;
                asks.TryGetValue(o.Name, out a);
                st.OpenAsks = a;

                st.State = Resolve(o, st);

                IconState prev;
                if (Previous.TryGetValue(o.Name, out prev) && prev == st.State)
                {
                    OfficerState old;
                    st.EnteredState = Officers.TryGetValue(o.Name, out old) ? old.EnteredState : now;
                }
                else
                {
                    st.EnteredState = now;
                }
                Previous[o.Name] = st.State;

                fresh[o.Name] = st;
            }

            Officers = fresh;
            TotalOpenAsks = 0;
            foreach (KeyValuePair<string, int> kv in asks) TotalOpenAsks += kv.Value;

            BuildDesks();
        }

        /// <summary>
        /// First match wins. Order matters: a running process outranks an overdue clock, and an
        /// unanswered ask outranks both, because it is the only state that needs a human.
        /// </summary>
        private static IconState Resolve(Officer o, OfficerState st)
        {
            // 1. fault -- a supervised process exited without advancing the ledger, or the last
            //    dispatch log for this officer records a hard failure. NOT a usage limit: that is
            //    an account condition, stands the whole fleet down, and is shown on the Command Center icon.
            if (Supervisor.IsFaulted(o.Name)) return IconState.Fault;

            // 2. working -- a live handle, held by us. The only signal that cannot be faked by a
            //    file timestamp that some other writer touched.
            if (Supervisor.IsRunning(o.Name)) return IconState.Working;

            // 3. attention -- an open, unanswered, unsnoozed ask.
            if (st.OpenAsks > 0) return IconState.Attention;

            // 4. never -- no ledger at all. Verified 2026-08-04: 8 of 13 officers are in this state
            //    (call, offer, rep, content, research, partner, legal, eng). Without this state they
            //    would all render as fault or due, and the very first frame the strip ever paints
            //    would be a lie. halo-status.sh already distinguishes NEVER from STALL; so do we.
            if (!st.HasLedger) return IconState.Never;

            // 5. due -- past its own cadence.
            if (st.AgeMinutes >= o.CadenceMin) return IconState.Due;

            return IconState.Idle;
        }

        private static void BuildDesks()
        {
            List<DeskState> desks = new List<DeskState>();
            for (int d = 0; d < Roster.Desks.Length; d++)
            {
                DeskState ds = new DeskState();
                ds.Name = Roster.Desks[d];
                List<Officer> mine = Roster.OfDesk(ds.Name);
                IconState worst = IconState.Idle;
                bool any = false;
                int nonIdle = 0;

                for (int i = 0; i < mine.Count; i++)
                {
                    OfficerState st;
                    if (!Officers.TryGetValue(mine[i].Name, out st)) continue;
                    ds.Officers.Add(st);
                    if (!any) { worst = st.State; any = true; }
                    else if (Severity(st.State) > Severity(worst)) worst = st.State;
                    if (st.State != IconState.Idle) nonIdle++;
                }

                ds.State = any ? worst : IconState.Never;
                ds.NonIdleCount = nonIdle;
                desks.Add(ds);
            }
            Desks = desks;
        }

        /// <summary>fault > attention > working > due > never > idle. A desk shows its worst officer.</summary>
        public static int Severity(IconState s)
        {
            switch (s)
            {
                case IconState.Fault: return 5;
                case IconState.Attention: return 4;
                case IconState.Working: return 3;
                case IconState.Due: return 2;
                case IconState.Never: return 1;
                default: return 0;
            }
        }

        public static IconState CoreState()
        {
            if (SuiteDrift) return IconState.Fault;
            if (IsStoodDown()) return IconState.Fault;
            for (int i = 0; i < Desks.Count; i++)
                if (Desks[i].State == IconState.Fault) return IconState.Fault;
            if (TotalOpenAsks > 0) return IconState.Attention;
            for (int i = 0; i < Desks.Count; i++)
                if (Desks[i].State == IconState.Working) return IconState.Working;
            return IconState.Idle;
        }

        public static bool IsStoodDown()
        {
            return LimitUntil > DateTime.Now;
        }

        // ---- readers -------------------------------------------------------------------------

        // Cache keyed on (path, mtime, length). Refresh runs up to 3x/minute across 13 ledgers, and
        // re-reading a 64 KB tail from each every time was the largest remaining idle cost. A
        // ledger that has not been written cannot have a different last row, so this is exact --
        // not an approximation with a staleness window.
        private static readonly Dictionary<string, string> HeadCache =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private static string LastHeading(string path)
        {
            string key;
            try
            {
                FileInfo fi = new FileInfo(path);
                key = path + "|" + fi.LastWriteTimeUtc.Ticks + "|" + fi.Length;
                string hit;
                if (HeadCache.TryGetValue(key, out hit)) return hit;
            }
            catch { key = null; }

            string result = ReadLastHeading(path);
            if (key != null)
            {
                // Bound the cache: one live entry per ledger plus churn. 64 is far above 13.
                if (HeadCache.Count > 64) HeadCache.Clear();
                HeadCache[key] = result;
            }
            return result;
        }

        private static string ReadLastHeading(string path)
        {
            try
            {
                // Read the tail only. Ledgers reach ~1,000 lines before rotation and this runs on a
                // 30 s timer, so reading whole files would be wasteful for one line of output.
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    int want = 64 * 1024;
                    long start = Math.Max(0, fs.Length - want);
                    fs.Seek(start, SeekOrigin.Begin);
                    byte[] buf = new byte[fs.Length - start];
                    int read = fs.Read(buf, 0, buf.Length);
                    string text = new UTF8Encoding(false).GetString(buf, 0, read);
                    string[] lines = text.Split('\n');
                    for (int i = lines.Length - 1; i >= 0; i--)
                    {
                        string ln = lines[i].TrimEnd('\r');
                        if (ln.StartsWith("## ", StringComparison.Ordinal))
                        {
                            string t = ln.Substring(3).Trim();
                            if (t.Length > 90) t = t.Substring(0, 88) + "..";
                            return t;
                        }
                    }
                }
            }
            catch (Exception ex) { Log.Warn("state: cannot read " + path + ": " + ex.Message); }
            return null;
        }

        private static void ReadSupervisor()
        {
            string p = Paths.State1("supervisor.json");
            bool was = Paused;
            string why = null;

            if (!File.Exists(p)) { Paused = true; why = "supervisor.json is missing"; }
            else
            {
                try
                {
                    string j = File.ReadAllText(p);
                    // Fails safe: anything other than an explicit "paused":false means paused.
                    Paused = !Regex.IsMatch(j, "\"paused\"\\s*:\\s*false", RegexOptions.IgnoreCase);
                }
                catch (Exception ex) { Paused = true; why = "supervisor.json unreadable: " + ex.Message; }
            }

            // Failing safe is right, but failing safe SILENTLY is not: the fleet would stop and the
            // only evidence would be thirteen officers quietly not running. The strip shows PAUSED,
            // but nothing would say it was not the operator who paused it.
            //
            // Deduplicated by REASON rather than by transition. Paused initialises to true, so a
            // transition test can only ever fire after the supervisor has been armed -- which would
            // have made a persistently unreadable file invisible on every ordinary start. Keyed on
            // the reason, this logs once and then stays quiet through a 30s poll.
            if (why != null)
            {
                if (why != _lastPauseWhy) { Log.Warn("supervisor: forcing PAUSED -- " + why); _lastPauseWhy = why; }
            }
            else
            {
                if (_lastPauseWhy != null) Log.Info("supervisor: pause state readable again");
                _lastPauseWhy = null;
                if (Paused && !was) Log.Info("supervisor: PAUSED");
            }
        }

        private static void ReadLimit()
        {
            LimitUntil = DateTime.MinValue;
            LimitUntilRaw = null;
            string p = Paths.State1("limit.json");
            if (!File.Exists(p)) return;
            try
            {
                string j = File.ReadAllText(p);
                Match m = Regex.Match(j, "\"until\"\\s*:\\s*\"([^\"]+)\"");
                if (!m.Success) return;
                DateTime t;
                if (DateTime.TryParse(m.Groups[1].Value, CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out t))
                {
                    LimitUntil = t;
                    Match r = Regex.Match(j, "\"raw\"\\s*:\\s*\"([^\"]+)\"");
                    LimitUntilRaw = r.Success ? r.Groups[1].Value : t.ToString("t");
                    // Self-clearing: an expired limit file is deleted so it can never wedge the
                    // fleet permanently. The dispatcher-side bug this replaces did the opposite --
                    // it only looked at logs from the last 30 minutes while the reset was 3h20m
                    // out, so it fired into the wall roughly every half hour for three hours.
                    if (LimitUntil <= DateTime.Now)
                    {
                        try { File.Delete(p); Log.Info("limit: expired, cleared"); } catch { }
                        LimitUntil = DateTime.MinValue;
                        LimitUntilRaw = null;
                    }
                }
            }
            catch (Exception ex) { Log.Warn("state: limit.json unreadable: " + ex.Message); }
        }

        private static void ReadBudget()
        {
            string p = Paths.State1("budget.json");
            if (!File.Exists(p))
            {
                // Fall back to the SessionStart hook's cached SMTP2GO read, which already exists.
                try
                {
                    string c = Path.Combine(Paths.HookCache, "smtp2go.json");
                    if (File.Exists(c))
                    {
                        string j = File.ReadAllText(c);
                        Match m = Regex.Match(j, "\"cycle_used\"\\s*:\\s*(\\d+)");
                        if (m.Success) BudgetUsed = int.Parse(m.Groups[1].Value);
                    }
                }
                catch { }
                return;
            }
            try
            {
                string j = File.ReadAllText(p);
                Match u = Regex.Match(j, "\"used\"\\s*:\\s*(\\d+)");
                Match c = Regex.Match(j, "\"ceiling\"\\s*:\\s*(\\d+)");
                Match d = Regex.Match(j, "\"dailyAllowance\"\\s*:\\s*(\\d+)");
                if (u.Success) BudgetUsed = int.Parse(u.Groups[1].Value);
                if (c.Success) BudgetCeiling = int.Parse(c.Groups[1].Value);
                if (d.Success) BudgetDaily = int.Parse(d.Groups[1].Value);
            }
            catch (Exception ex) { Log.Warn("state: budget.json unreadable: " + ex.Message); }
        }

        private static void ReadSuite()
        {
            SuiteDrift = false;
            try
            {
                string p = Path.Combine(Paths.HookCache, "loop-suite-status");
                if (!File.Exists(p)) return;
                // Strip CR and any BOM: this file is written by bash but has been touched by
                // PowerShell before, and a stray \r turns "CLEAN" into something that never matches.
                string s = File.ReadAllText(p).Replace("\r", "").Replace("﻿", "").Trim();
                SuiteDrift = s.StartsWith("DRIFT", StringComparison.OrdinalIgnoreCase);
            }
            catch { }
        }

        /// <summary>
        /// Open asks per officer. An ask is open when its .ask.json exists and there is no
        /// .answer.json, no .closed.json, and no unexpired .snoozed.json beside it.
        /// Filenames are &lt;officer&gt;.&lt;id&gt;.ask.json -- one writer per file, always.
        /// </summary>
        private static Dictionary<string, int> ReadAsks()
        {
            Dictionary<string, int> counts =
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!Directory.Exists(Paths.State)) return counts;
                string[] files = Directory.GetFiles(Paths.State, "*.ask.json");
                for (int i = 0; i < files.Length; i++)
                {
                    string name = Path.GetFileName(files[i]);
                    string stem = name.Substring(0, name.Length - ".ask.json".Length);
                    int dot = stem.IndexOf('.');
                    if (dot <= 0) continue;
                    string officer = stem.Substring(0, dot);

                    string baseP = Path.Combine(Paths.State, stem);
                    if (File.Exists(baseP + ".answer.json")) continue;
                    if (File.Exists(baseP + ".closed.json")) continue;
                    if (IsSnoozed(baseP + ".snoozed.json")) continue;

                    int c;
                    counts.TryGetValue(officer, out c);
                    counts[officer] = c + 1;
                }
            }
            catch (Exception ex) { Log.Warn("state: ask scan failed: " + ex.Message); }
            return counts;
        }

        private static bool IsSnoozed(string path)
        {
            if (!File.Exists(path)) return false;
            try
            {
                Match m = Regex.Match(File.ReadAllText(path), "\"until\"\\s*:\\s*\"([^\"]+)\"");
                DateTime t;
                if (m.Success && DateTime.TryParse(m.Groups[1].Value, CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out t))
                {
                    if (t > DateTime.Now) return true;
                    try { File.Delete(path); } catch { }   // expired snooze clears itself
                }
            }
            catch { }
            return false;
        }
    }
}
