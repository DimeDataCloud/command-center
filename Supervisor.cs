// Supervisor.cs -- owns dispatch, holds the handles, and is the reason "working" is not a guess.
//
// WHY THIS DOES NOT SHELL OUT TO halo-dispatch.ps1 PER TICK.
// That script computes due-ness from ledger mtime. Measured 2026-08-04:
//   ops-20260803-172433.log ran 796 s (13.3 min) and tasks/ops-loop.md only advanced at 17:38:12.
// The mtime does not move until the iteration ENDS. So for any officer whose iteration exceeds its
// cadence -- outbound, security and inbound at 10 min; funding at 15 -- the next tick still sees it
// as due and fires it AGAIN while the first run is alive. The 2h lockfile cannot catch this: the
// dispatcher exits ~5 s after spawning and releases the lock. The artifacts show it happening:
// ops was dispatched at 15:09:34 and again at 15:10:31, 57 seconds apart.
//
// outbound SENDS EMAIL (560/mo of a 900 pool already projected to breach 2026-08-17). A double-fire
// there is money and sender reputation, not a glitch. So the supervisor owns the due-set, computed
// from dispatch bookkeeping it owns plus a live process handle, and spawns the officer itself.
//
// halo-dispatch.ps1 is retained as the documented manual instrument (-DryRun / -Only, both named in
// workspace CLAUDE.md's instrument table) and as the source of the `browser` flags.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace CommandCenter
{
    internal class RunRecord
    {
        public string Officer;
        public int Pid;
        public long StartTicks;      // Process.StartTime.ToUniversalTime().Ticks
        public DateTime SpawnedAt;
        public DateTime LedgerMtimeAtSpawn;
        public string LogPath;
        public Process Handle;       // null after a supervisor restart -- reconciled by tuple

        // Only ever set by -Simulate -Mode working, which attaches to a process it does not own and
        // therefore cannot end. MinValue on every real spawn: a real officer expires when it exits,
        // never on a clock.
        public DateTime ExpiresAt;
    }

    internal static class Supervisor
    {
        public const int TickMs = 60000;
        public const int MaxConcurrent = 2;
        public const int HungMinutes = 45;      // 3.4x the measured 13.3-min iteration
        public const int LockBreakMinutes = 10;

        private static readonly Dictionary<string, RunRecord> Running =
            new Dictionary<string, RunRecord>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> Faults =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, DateTime> LastAttempt =
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, int> FailStreak =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        private static DateTime _suppressUntil = DateTime.MinValue;
        private static bool _reconciled;
        public static bool SessionLocked;
        public static long TickCount;

        // ---- queries the renderer uses -------------------------------------------------------

        /// <summary>
        /// In-memory first, then the on-disk run.json validated by the identity tuple.
        ///
        /// The disk fallback is not belt-and-braces, it is correctness: if the command center is
        /// restarted (or crashes) while officers are mid-iteration, in-memory state is gone but the
        /// processes are still running and still writing to the brain. Reporting them as idle would
        /// be the same false-green this component exists to eliminate -- and would let the
        /// supervisor dispatch a second copy of a loop that is already running.
        /// </summary>
        public static bool IsRunning(string officer)
        {
            RunRecord r;
            if (Running.TryGetValue(officer, out r) && Alive(r)) return true;

            RunRecord d = ReadRunJson(officer);
            if (d == null) return false;
            if (Alive(d)) return true;

            // Dead and unclaimed. DO NOT DELETE THE MARKER BEFORE RECONCILE HAS SEEN IT.
            // Program.cs refreshes the strip before the first Tick(), so this query runs first on
            // every startup. Deleting here erased exactly the record Reconcile() exists to read --
            // an officer that died while the command center was down would come back reported as
            // `idle`, its fault never classified, never logged, never rendered. The strip's read
            // path silently destroying the crash evidence is the same false-green in a new costume.
            // Returning false is enough to keep the icon off `working`; the file is Reconcile's.
            if (_reconciled) { try { File.Delete(Paths.State1(officer + ".run.json")); } catch { } }
            return false;
        }

        private static RunRecord ReadRunJson(string officer)
        {
            try
            {
                string p = Paths.State1(officer + ".run.json");
                if (!File.Exists(p)) return null;
                string j = File.ReadAllText(p);
                string pid = Match1(j, "\"pid\"\\s*:\\s*(\\d+)");
                string tk = Match1(j, "\"startTicks\"\\s*:\\s*(\\d+)");
                if (pid == null || tk == null) return null;
                RunRecord r = new RunRecord();
                r.Officer = officer;
                r.Pid = int.Parse(pid);
                r.StartTicks = long.Parse(tk);
                r.LogPath = Match1(j, "\"log\"\\s*:\\s*\"([^\"]+)\"");
                DateTime t;
                r.SpawnedAt = DateTime.TryParse(Match1(j, "\"spawnedAt\"\\s*:\\s*\"([^\"]+)\""),
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out t) ? t : DateTime.Now;
                r.ExpiresAt = ParseExpiry(j);
                r.LedgerMtimeAtSpawn = ParseLedgerTicks(j);
                return r;
            }
            catch { return null; }
        }

        /// <summary>
        /// Absent (a run.json written before this field existed) is deliberately NOT MinValue --
        /// MinValue would make any ledger on disk look newer and classify the run clean. Unknown
        /// baseline means unknowable progress, so we return MaxValue and let it read as "did not
        /// advance": on the crash path, guess red, never green.
        /// </summary>
        private static DateTime ParseLedgerTicks(string j)
        {
            string s = Match1(j, "\"ledgerTicks\"\\s*:\\s*(\\d+)");
            long t;
            if (s != null && long.TryParse(s, out t) && t >= 0 && t <= DateTime.MaxValue.Ticks)
                return new DateTime(t);
            return DateTime.MaxValue;
        }

        /// <summary>MinValue when absent or unparseable -- i.e. no expiry, the real-spawn case.</summary>
        private static DateTime ParseExpiry(string j)
        {
            string s = Match1(j, "\"expiresAt\"\\s*:\\s*\"([^\"]+)\"");
            DateTime t;
            if (s != null && DateTime.TryParse(s, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out t)) return t;
            return DateTime.MinValue;
        }

        public static bool IsFaulted(string officer) { return Faults.ContainsKey(officer); }

        public static string FaultReason(string officer)
        {
            string s;
            return Faults.TryGetValue(officer, out s) ? s : null;
        }

        public static int RunningCount()
        {
            int n = 0;
            foreach (KeyValuePair<string, RunRecord> kv in Running) if (Alive(kv.Value)) n++;
            return n;
        }

        public static void ClearFault(string officer) { Faults.Remove(officer); }

        /// <summary>
        /// Liveness by IDENTITY TUPLE, not PID alone. Windows reuses PIDs; a stale record whose PID
        /// has been recycled onto an unrelated process would otherwise report `working` forever --
        /// the exact false-green this whole component exists to eliminate. Process.StartTime is
        /// exact to 100ns, so (pid, startTicks) is unique in practice.
        /// </summary>
        private static bool Alive(RunRecord r)
        {
            if (r == null) return false;

            // A simulated `working` window is bounded by a clock because it stands in on a process
            // the supervisor did not start and must never kill (explorer, or the command center
            // itself). Without this, -Simulate -Mode working pins an officer to `working` for the
            // life of that process -- forever, in practice -- and the supervisor politely declines
            // to ever dispatch it again. A diagnostic that can silently retire a department is worse
            // than no diagnostic.
            if (r.ExpiresAt != DateTime.MinValue && DateTime.Now >= r.ExpiresAt) return false;

            try
            {
                Process h = r.Handle;
                if (h != null) { h.Refresh(); return !h.HasExited; }

                // DISPOSE IT. Every GetProcessById opens a real kernel handle, and this runs for a
                // handle-less record (one adopted by Reconcile after a restart) on the RENDER path
                // -- IconRenderer's tooltip calls InFlight(), and the render timer ticks at 33 ms
                // while anything is animating. Leaking one handle per frame in a process built to
                // stay up for weeks is how a watcher becomes the thing that needs watching.
                using (Process p = Process.GetProcessById(r.Pid))
                {
                    if (p.StartTime.ToUniversalTime().Ticks != r.StartTicks) return false;
                    return !p.HasExited;
                }
            }
            catch (ArgumentException) { return false; }          // no such process
            catch (InvalidOperationException) { return false; }   // exited between calls
            catch (Exception ex) { Log.Warn("supervisor: liveness check: " + ex.Message); return false; }
        }

        // ---- the tick ------------------------------------------------------------------------

        public static void Tick()
        {
            TickCount++;
            try
            {
                if (!_reconciled) { Reconcile(); _reconciled = true; }
                Harvest();
                PublishBudget();
                // ABOVE every early return below, deliberately: paused and stood-down are exactly
                // the states a reader most needs named, and publishing only when we dispatch would
                // let a paused fleet decay into "the dispatcher stopped". See PublishDispatchStatus.
                PublishLiveness();
                if (TickCount % 60 == 1) PruneDispatchLogs();   // hourly; see PruneDispatchLogs

                if (StateStore.Paused) return;
                if (SessionLocked) return;                       // never dispatch at a locked desk

                // Before honouring a stand-down, check whether it still applies -- the account may
                // have been switched, or the limit lifted early. See TryLiftLimit.
                TryLiftLimit();
                if (StateStore.IsStoodDown()) return;            // account usage limit
                if (DateTime.Now < _suppressUntil) return;       // post-resume grace
                if (RunningCount() >= MaxConcurrent) return;
                if (!AuthorGateOk()) return;
                if (!LeaseOk()) return;

                Officer next = PickNext();
                if (next != null) Spawn(next);
            }
            catch (Exception ex) { Log.Err("supervisor: tick failed", ex); }
        }

        /// <summary>
        /// Most overdue against its OWN cadence -- a research loop 50 min late is far less overdue
        /// than an outbound loop 50 min late, and sorting by raw age would invert that. This is the
        /// existing rule from halo-dispatch.ps1:135-137, preserved deliberately. Ties break by
        /// roster index so the order is deterministic.
        /// </summary>
        private static Officer PickNext()
        {
            List<Officer> roster = Roster.Load();
            Officer best = null;
            double bestScore = 0;

            for (int i = 0; i < roster.Count; i++)
            {
                Officer o = roster[i];
                if (!Roster.IsAllowed(o.Name)) continue;
                if (IsRunning(o.Name)) continue;
                if (o.NeedsBrowser && !BrowserGateOk()) continue;
                if (Sends(o.Name) && BudgetExhausted()) continue;   // see BudgetExhausted

                // Due-ness from OUR bookkeeping, not ledger mtime. If we dispatched it and the run
                // has not been harvested yet, it is not due no matter what the file says.
                DateTime last;
                if (LastAttempt.TryGetValue(o.Name, out last))
                {
                    double sinceMin = (DateTime.Now - last).TotalMinutes;
                    double need = o.CadenceMin * BackoffFactor(o.Name);
                    if (sinceMin < need) continue;
                }
                else
                {
                    // Never dispatched by us this session -- fall back to the ledger, which is the
                    // only evidence available on a cold start. Absent ledger == maximally overdue.
                    OfficerState st;
                    if (StateStore.Officers.TryGetValue(o.Name, out st))
                    {
                        if (st.AgeMinutes != int.MaxValue && st.AgeMinutes < o.CadenceMin) continue;
                    }
                }

                OfficerState s2;
                double age = o.CadenceMin;
                if (StateStore.Officers.TryGetValue(o.Name, out s2))
                    age = s2.AgeMinutes == int.MaxValue ? double.MaxValue / 2 : s2.AgeMinutes;
                double score = age / Math.Max(1, o.CadenceMin);

                if (best == null || score > bestScore) { best = o; bestScore = score; }
            }
            return best;
        }

        // ---- getting back up after a usage limit ------------------------------------------------

        private static string _limitFingerprint;      // account identity when the limit was recorded
        private static DateTime _nextProbeAt = DateTime.MinValue;

        /// <summary>
        /// A short one-way hash of the stored credentials. Identity only -- NEVER the contents, and
        /// nothing derived from it is ever logged beyond these 12 hex chars, which are not
        /// reversible into a token.
        /// </summary>
        private static string AccountFingerprint()
        {
            try
            {
                string p = Path.Combine(Paths.Home, ".claude", ".credentials.json");
                string sha = Sha256File(p);
                return sha == null ? null : sha.Substring(0, 12);
            }
            catch { return null; }
        }

        /// <summary>
        /// GET BACK UP WITHOUT WAITING FOR THE CLOCK.
        ///
        /// A stand-down that only ever ends at the stated reset is wrong in the case the operator
        /// actually hits: he switches accounts. The old limit belongs to an account the fleet is no
        /// longer using, and sitting idle until 7:50pm on a fresh account is hours of dead time for
        /// a condition that no longer exists.
        ///
        /// Two independent ways out, cheapest first:
        ///
        ///   1. THE ACCOUNT CHANGED. `.credentials.json` is rewritten on login, so a changed
        ///      fingerprint means a different account is in force and the recorded limit cannot
        ///      apply to it. Free, instant, and it is the case he described.
        ///
        ///   2. THE PROBE. Ask the model something trivial and see whether it answers. This tests
        ///      the actual condition -- "can an officer run right now" -- rather than a proxy for
        ///      it, so it also covers a limit lifted early, a plan change, or a reset time we parsed
        ///      wrong. Throttled to once every 5 minutes, one short call, and only while stood down.
        ///
        /// Both fail SAFE: if the probe cannot run or its result is unclear, the stand-down stands.
        ///
        /// THE PROBE RUNS OFF THIS THREAD. Tick() is raised by a WinForms timer, so it IS the UI
        /// thread -- the one that paints fourteen layered windows and services the tray. Waiting up
        /// to 90 s here for a `claude` invocation froze the entire strip solid, once every 1-5
        /// minutes, for exactly as long as the account stayed limited. So the probe is dispatched to
        /// a background thread and its verdict is consumed here on the next tick; every state
        /// mutation (ClearLimit, the file delete, StateStore) stays single-threaded on this side.
        /// </summary>
        private static void TryLiftLimit()
        {
            if (!StateStore.IsStoodDown()) { _limitFingerprint = null; _probeSaidUp = false; return; }

            // A verdict from a probe started on an earlier tick, applied on the owning thread.
            if (_probeSaidUp)
            {
                _probeSaidUp = false;
                Log.Info("supervisor: probe answered -- the usage limit is over, resuming early");
                ClearLimit("probe succeeded");
                return;
            }

            string fp = AccountFingerprint();

            // Whose limit is this? Prefer the value recorded in limit.json -- it survives a restart,
            // where an in-memory baseline would silently re-take itself from the NEW account.
            if (_limitFingerprint == null)
            {
                _limitFingerprint = StoredLimitAccount();
                if (_limitFingerprint == null)
                {
                    // Nothing on file -- a limit written before this build, or by a path that did
                    // not record it. Baseline now AND persist, or every restart re-baselines to
                    // whatever account is current and a switch is never detectable.
                    _limitFingerprint = fp;
                    StampLimitAccount(fp);
                }
                _nextProbeAt = DateTime.Now.AddMinutes(1);

                // Only the ESTABLISHED-baseline case may return early. When fp is null the
                // credentials file is unreadable, so `_limitFingerprint` stays null and this branch
                // is re-entered every tick -- which re-armed _nextProbeAt a minute out each time and
                // meant the probe could NEVER fire. Route 1 is dead in that case; route 2 must not
                // be, or the fleet sits out the whole limit with no way back but the clock.
                if (fp != null && _limitFingerprint == fp) return;
            }

            if (fp != null && fp != _limitFingerprint)
            {
                Log.Info("supervisor: account changed (" + _limitFingerprint + " -> " + fp +
                         ") -- clearing the usage limit, it belonged to the previous account");
                ClearLimit("account switched");
                _limitFingerprint = fp;
                return;
            }

            if (DateTime.Now < _nextProbeAt) return;

            // Eager near the stated reset so the fleet comes back promptly rather than up to five
            // minutes late; relaxed when the reset is still hours out, where probing often is just
            // spend. The stated time is a hint, not a contract -- limits lift early, and the probe
            // is what actually decides.
            double minsToReset = (StateStore.LimitUntil - DateTime.Now).TotalMinutes;
            _nextProbeAt = DateTime.Now.AddMinutes(minsToReset <= 15 ? 1 : 5);

            StartProbe();
        }

        private static int _probeRunning;            // 0 idle, 1 in flight -- Interlocked only
        private static volatile bool _probeSaidUp;   // set by the probe thread, consumed by Tick

        /// <summary>
        /// Fire one probe on a background thread, at most one at a time. Never blocks the caller.
        /// </summary>
        private static void StartProbe()
        {
            if (Interlocked.CompareExchange(ref _probeRunning, 1, 0) != 0) return;
            Thread t = new Thread(delegate()
            {
                try { if (ProbeModelAvailable()) _probeSaidUp = true; }
                catch (Exception ex) { Log.Warn("supervisor: limit probe thread: " + ex.Message); }
                finally { Interlocked.Exchange(ref _probeRunning, 0); }
            });
            t.IsBackground = true;   // must never hold the process open at shutdown
            t.Start();
        }

        /// <summary>
        /// Record which account a limit belongs to, in place, without disturbing the reset time.
        /// Rewriting the whole file would risk losing a reset we already parsed correctly.
        /// </summary>
        private static void StampLimitAccount(string fp)
        {
            if (fp == null) return;
            try
            {
                string p = Paths.State1("limit.json");
                if (!File.Exists(p)) return;
                string j = File.ReadAllText(p);
                if (Regex.IsMatch(j, "\"account\"\\s*:\\s*\"[0-9a-f]+\"")) return;
                int close = j.LastIndexOf('}');
                if (close < 0) return;
                string updated = j.Substring(0, close).TrimEnd() + ",\"account\":\"" + fp + "\"}\n";
                WriteAtomic(p, updated);
                Log.Info("supervisor: recorded account " + fp + " against the active usage limit");
            }
            catch (Exception ex) { Log.Warn("supervisor: could not stamp limit account: " + ex.Message); }
        }

        /// <summary>The account fingerprint recorded in limit.json, or null if absent.</summary>
        private static string StoredLimitAccount()
        {
            try
            {
                string p = Paths.State1("limit.json");
                if (!File.Exists(p)) return null;
                Match m = Regex.Match(File.ReadAllText(p), "\"account\"\\s*:\\s*\"([0-9a-f]{0,64})\"");
                if (!m.Success) return null;
                string v = m.Groups[1].Value;
                return v.Length == 0 ? null : v;
            }
            catch { return null; }
        }

        private static void ClearLimit(string why)
        {
            try { File.Delete(Paths.State1("limit.json")); } catch { }
            StateStore.LimitUntil = DateTime.MinValue;
            StateStore.LimitUntilRaw = null;
            _limitFingerprint = null;
            Log.Info("supervisor: stand-down lifted (" + why + ") -- the fleet resumes next tick");
        }

        /// <summary>
        /// One trivial call. Returns true only on a clean answer; anything ambiguous -- a timeout,
        /// a non-zero exit, a limit message, an empty reply -- returns false and the fleet stays
        /// down. Guessing "we are back" wrong means thirteen officers firing into the wall, which is
        /// the exact failure this whole mechanism was built to stop.
        /// </summary>
        private static bool ProbeModelAvailable()
        {
            string claude = ResolveClaude();
            if (claude == null) return false;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = claude;
                psi.Arguments = "-p \"Reply with the single word: up\"";
                psi.WorkingDirectory = Paths.Repo;
                psi.UseShellExecute = false;   // BEFORE ScrubSessionEnv: .NET forbids touching
                ScrubSessionEnv(psi);          // EnvironmentVariables while it is true

                string all;
                int rc = Capture(psi, 90000, out all);
                if (rc == TimedOut) { Log.Info("supervisor: limit probe timed out -- staying down"); return false; }
                if (rc != 0) return false;
                if (LooksLimited(all)) return false;
                return all.Trim().Length > 0;
            }
            catch (Exception ex)
            {
                Log.Warn("supervisor: limit probe failed: " + ex.Message);
                return false;
            }
        }

        public const int TimedOut = int.MinValue;

        /// <summary>
        /// Run a child, capture stdout AND stderr, and come back within the timeout. Every process
        /// this app reads output from goes through here.
        ///
        /// THE BUG THIS ENDS IS A DEADLOCK, not untidiness. The three hand-rolled copies of this
        /// did `StandardOutput.ReadToEnd()` and then `StandardError.ReadToEnd()`, in that order.
        /// A pipe buffer is about 4 KB: a child that fills stderr while we are still blocked on
        /// stdout stops writing, never exits, and both sides wait forever -- the subsequent
        /// `WaitForExit(timeout)` is never reached, so the timeout that looks like the safety net
        /// cannot fire. `claude -p` writes to both streams and is the likeliest child to do it.
        ///
        /// The async handlers drain both pipes concurrently, so neither can fill. The bare
        /// WaitForExit() after the timed one is required, not belt-and-braces: it is what flushes
        /// the async readers, and without it the tail of the output is routinely lost.
        /// </summary>
        public static int Capture(ProcessStartInfo psi, int timeoutMs, out string combined)
        {
            StringBuilder sb = new StringBuilder();
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;

            using (Process p = new Process())
            {
                p.StartInfo = psi;
                DataReceivedEventHandler onData = delegate(object s, DataReceivedEventArgs e)
                {
                    if (e.Data == null) return;
                    lock (sb) sb.Append(e.Data).Append('\n');
                };
                p.OutputDataReceived += onData;
                p.ErrorDataReceived += onData;
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();

                if (!p.WaitForExit(timeoutMs))
                {
                    try { p.Kill(); } catch { }
                    lock (sb) combined = sb.ToString();
                    return TimedOut;
                }
                p.WaitForExit();                       // flush the async readers
                lock (sb) combined = sb.ToString();
                return p.ExitCode;
            }
        }

        private static bool LooksLimited(string s)
        {
            if (s == null) return false;
            string t = s.ToLowerInvariant();
            return t.Contains("usage limit") || t.Contains("session limit")
                || t.Contains("rate limit") || t.Contains("try again later");
        }

        /// <summary>
        /// The four officers with an outbound email channel. Hard-coded for the same reason the
        /// spawn allowlist is: a send gate that can be edited by a file is not a gate. Matches
        /// [[context/loops]] -- outbound 560/mo, funding 150, inbound 120, partner 70; the other
        /// nine send nothing by design.
        /// </summary>
        public static bool Sends(string officer)
        {
            return officer == "outbound" || officer == "inbound"
                || officer == "funding" || officer == "partner";
        }

        /// <summary>
        /// STOP DISPATCHING SENDERS AT THE CEILING. Defense in depth, not duplication.
        ///
        /// Each sending loop already pulls live SMTP2GO stats in preflight and cuts its own wave
        /// size when days_to_cap falls before cycle_end -- that self-regulation is the primary
        /// control and it works. But it is thirteen agents CHOOSING to behave, re-derived every
        /// iteration from a command file that a automated bot commits to every ~20 minutes. Running
        /// unattended, one loop that misreads its own budget section can burn the remaining pool and
        /// the sender reputation, which is shared with three reps' live client threads.
        ///
        /// So the supervisor refuses to launch a sender at all once the pool is spent. The nine
        /// non-sending officers keep running -- a spent email budget is no reason to stop security,
        /// eng or ops.
        /// </summary>
        public static bool BudgetExhausted()
        {
            if (StateStore.BudgetUsed < 0) return false;                  // unknown: do not invent a stop
            if (StateStore.BudgetUsed < StateStore.BudgetCeiling) return false;

            if (!_budgetStopLogged)
            {
                Log.Err(string.Format(
                    "supervisor: SEND POOL SPENT ({0}/{1}) -- outbound/inbound/funding/partner will " +
                    "not be dispatched until the cycle resets. The other nine keep running.",
                    StateStore.BudgetUsed, StateStore.BudgetCeiling));
                _budgetStopLogged = true;
            }
            return true;
        }

        private static bool _budgetStopLogged;

        /// <summary>
        /// Exponential backoff on consecutive faults, capped. Without it a permanently broken
        /// officer sorts to the top of the due-set forever -- its ledger never advances, so its age
        /// only grows -- and with MaxConcurrent 2 it would occupy half the fleet's capacity
        /// indefinitely while doing nothing.
        /// </summary>
        private static double BackoffFactor(string officer)
        {
            int n;
            if (!FailStreak.TryGetValue(officer, out n) || n <= 0) return 1.0;
            double f = Math.Pow(2, Math.Min(n, 5));   // 2,4,8,16,32x cadence
            return f;
        }

        // ---- spawn ---------------------------------------------------------------------------

        /// <summary>
        /// Dispatch exactly ONE officer, now, through the real Spawn path -- allowlist, pin gate,
        /// env scrub, log redirect, run.json, Classify. Everything the resident supervisor does,
        /// for one officer of your choosing, without unpausing the fleet.
        ///
        /// This exists because arming dispatch is a one-way door for thirteen agents at once, and
        /// until it is pulled the spawn path is exercised only by -Simulate, which substitutes
        /// cmd.exe for claude.cmd. So the interesting half -- does a real officer actually start,
        /// read its command file, do work and write a ledger row -- has never run. Finding that out
        /// with one officer you picked is strictly better than finding it out with thirteen in
        /// flight.
        ///
        /// Returns 0 if it launched, non-zero if a gate refused it.
        /// </summary>
        public static int DispatchOne(string name, out string detail)
        {
            detail = "";
            List<Officer> roster = Roster.Load();
            Officer target = null;
            for (int i = 0; i < roster.Count; i++)
                if (string.Equals(roster[i].Name, name, StringComparison.OrdinalIgnoreCase))
                { target = roster[i]; break; }

            if (target == null) { detail = "no such officer: " + name; return 2; }
            if (!Roster.IsAllowed(target.Name)) { detail = "not in the spawn allowlist"; return 2; }
            if (IsRunning(target.Name)) { detail = "already running"; return 3; }
            if (Sends(target.Name) && BudgetExhausted()) { detail = "send pool spent"; return 4; }

            Reconcile();
            _reconciled = true;

            int before = RunningCount();
            Spawn(target);
            if (RunningCount() <= before)
            { detail = "Spawn refused it -- see the log for which gate"; return 5; }

            RunRecord r;
            if (Running.TryGetValue(target.Name, out r))
                detail = string.Format("pid {0}, log {1}", r.Pid, r.LogPath);
            return 0;
        }

        private static void Spawn(Officer o)
        {
            // THE ALLOWLIST GATE. The argument vector is built from Roster's compiled-in array, so
            // no bytes parsed out of any file can reach a process argument. The repo these command
            // files live in takes commits from a automated bot every ~20 minutes and we spawn with
            // --dangerously-skip-permissions, unattended, so this is not paranoia.
            if (!Roster.IsAllowed(o.Name))
            { Log.Err("supervisor: REFUSED to spawn unlisted officer '" + o.Name + "'"); return; }

            string sha = Sha256File(o.CommandFile);
            if (sha == null)
            { Fault(o.Name, "command file missing: " + o.CommandFile); return; }

            string pinned = PinnedHash(o.Name);
            if (pinned != null && pinned != sha)
            {
                Fault(o.Name, "command file changed since review (pinned " + Short(pinned) +
                              ", now " + Short(sha) + ") -- refusing to spawn");
                return;
            }

            string claude = ResolveClaude();
            if (claude == null) { Fault(o.Name, "claude.cmd not found"); return; }

            try
            {
                Directory.CreateDirectory(Paths.DispatchLogs);
                string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                string log = Path.Combine(Paths.DispatchLogs, o.Name + "-" + stamp + ".log");

                DateTime ledgerBefore = File.Exists(o.LedgerPath)
                    ? new FileInfo(o.LedgerPath).LastWriteTime : DateTime.MinValue;

                // THE CHILD OWNS ITS LOG FILE, via a shell redirect -- not a pipe this process holds.
                //
                // RedirectStandardOutput hands the read end to US, so the log only survives as long
                // as this process does. Measured: a short-lived launcher exited and its officer's
                // entire output was lost to a 0-byte file, and the same thing happens to every
                // in-flight officer whenever the supervisor restarts (crash relaunch, an upgrade,
                // Quit). For a fleet that is supposed to run unattended for days, "your audit log
                // disappears if the watcher blinks" is the wrong trade.
                //
                // `cmd /c "..."` strips the OUTER quote pair, so the whole command is wrapped and
                // every embedded path carries its own. Verified in isolation before shipping --
                // this runs with --dangerously-skip-permissions and guessing at cmd quoting is not
                // acceptable. The officer name still comes from Roster's compiled-in allowlist, so
                // no bytes from any file reach this command line.
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = "cmd.exe";
                // -p is one-shot print mode. Each command file carries a "## One-shot mode" section
                // telling it to run exactly one iteration and NOT call ScheduleWakeup -- without
                // that, a dispatched loop also self-schedules and double-fires.
                psi.Arguments = "/c \"\"" + claude + "\" -p \"/" + o.Name +
                                "-loop\" --dangerously-skip-permissions > \"" + log + "\" 2>&1\"";
                psi.WorkingDirectory = Paths.Repo;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                ScrubSessionEnv(psi);   // an officer with no saved transcript has no audit trail

                Process p = new Process();
                p.StartInfo = psi;
                p.Start();

                RunRecord r = new RunRecord();
                r.Officer = o.Name;
                r.Pid = p.Id;
                r.StartTicks = p.StartTime.ToUniversalTime().Ticks;
                r.SpawnedAt = DateTime.Now;
                r.LedgerMtimeAtSpawn = ledgerBefore;
                r.LogPath = log;
                r.Handle = p;

                Running[o.Name] = r;
                LastAttempt[o.Name] = DateTime.Now;
                Faults.Remove(o.Name);
                WriteRunJson(r);
                AuditSpawn(r, sha, claude);

                Log.Info(string.Format("spawn {0} pid={1} log={2}", o.Name, p.Id, Path.GetFileName(log)));
            }
            catch (Exception ex)
            {
                Log.Err("supervisor: spawn " + o.Name + " failed", ex);
                Fault(o.Name, "spawn failed: " + ex.Message);
            }
        }

        // ---- harvest -------------------------------------------------------------------------

        /// <summary>
        /// Classify every finished child. This is the two-signal AND that gives us `fault`, the
        /// state no trace-based observability tool renders: the process is gone AND the ledger
        /// never moved. A real instance is on disk -- ops-20260803-150934.log, 0 bytes.
        /// </summary>
        /// <summary>
        /// Keep the newest N dispatch logs PER OFFICER and delete the rest.
        ///
        /// Nothing pruned these. Thirteen officers on 10-45 minute cadences produce roughly 800 log
        /// files a day, forever -- so a fleet left running for a month arrives at ~24,000 files in
        /// one directory, which is unbounded disk growth and makes every enumeration of it slower
        /// than the last. The crash files already had this exact fix; dispatch logs, which are
        /// generated two orders of magnitude faster, did not.
        ///
        /// Per officer rather than a global cap, so a 10-minute loop cannot crowd out the history of
        /// a 45-minute one. Fifteen is about a day for the fastest and a week for the slowest.
        ///
        /// Runs hourly, not per tick: the directory listing is the expensive part, and doing it
        /// every minute to delete nothing would be its own version of this bug.
        /// </summary>
        private static void PruneDispatchLogs()
        {
            try
            {
                if (!Directory.Exists(Paths.DispatchLogs)) return;
                string[] files = Directory.GetFiles(Paths.DispatchLogs, "*.log");
                if (files.Length <= 15) return;

                Dictionary<string, List<string>> byOfficer =
                    new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < files.Length; i++)
                {
                    string name = Path.GetFileName(files[i]);
                    int dash = name.IndexOf('-');
                    string who = dash > 0 ? name.Substring(0, dash) : name;
                    if (!byOfficer.ContainsKey(who)) byOfficer[who] = new List<string>();
                    byOfficer[who].Add(files[i]);
                }

                int removed = 0;
                foreach (KeyValuePair<string, List<string>> kv in byOfficer)
                {
                    List<string> list = kv.Value;
                    if (list.Count <= 15) continue;
                    list.Sort(delegate(string a, string b)
                    { return File.GetLastWriteTimeUtc(b).CompareTo(File.GetLastWriteTimeUtc(a)); });
                    for (int i = 15; i < list.Count; i++)
                    { try { File.Delete(list[i]); removed++; } catch { } }
                }
                if (removed > 0) Log.Info("supervisor: pruned " + removed + " old dispatch log(s)");
            }
            catch (Exception ex) { Log.Warn("supervisor: dispatch-log prune failed: " + ex.Message); }
        }

        public static void Harvest()
        {
            List<string> done = new List<string>();
            List<string> abandoned = new List<string>();
            foreach (KeyValuePair<string, RunRecord> kv in Running)
            {
                RunRecord r = kv.Value;
                if (Alive(r))
                {
                    if ((DateTime.Now - r.SpawnedAt).TotalMinutes > HungMinutes)
                    {
                        // ABANDON IT, DO NOT HOLD ITS SLOT.
                        //
                        // This used to Fault() and `continue`, which left the record in Running --
                        // so a hung officer occupied one of two concurrency slots FOREVER and
                        // re-faulted once a minute for as long as it lived. Two hung officers and
                        // the entire fleet stops permanently, with the log saying so 2,880 times a
                        // day. The watchdog existed and made the outage worse.
                        //
                        // NOT killed. A loop hung 45 min in may still be mid-send, and killing it
                        // after the email went out but before the ledger row lands destroys the
                        // receipt -- the SEC-020 data-loss class, same reason Pause never kills.
                        // Abandoning releases the slot without touching the process; it finishes or
                        // it does not, and either way the other twelve keep running.
                        abandoned.Add(kv.Key);
                    }
                    continue;
                }
                done.Add(kv.Key);
            }

            for (int i = 0; i < done.Count; i++)
            {
                RunRecord r = Running[done[i]];
                Running.Remove(done[i]);
                Classify(r);
                try { File.Delete(Paths.State1(r.Officer + ".run.json")); } catch { }
                if (r.Handle != null) { try { r.Handle.Dispose(); } catch { } }
            }

            for (int i = 0; i < abandoned.Count; i++)
            {
                RunRecord r = Running[abandoned[i]];
                Running.Remove(abandoned[i]);
                // The run.json goes too, or Reconcile re-adopts it on the next restart and the slot
                // is lost all over again -- this time across process lifetimes.
                try { File.Delete(Paths.State1(r.Officer + ".run.json")); } catch { }
                if (r.Handle != null) { try { r.Handle.Dispose(); } catch { } }

                // Counts as a fault so BackoffFactor holds it off rather than re-dispatching it
                // straight into the same wall. Logged ONCE, here, instead of once a minute forever.
                int n;
                FailStreak.TryGetValue(r.Officer, out n);
                FailStreak[r.Officer] = n + 1;
                Faults[r.Officer] = "abandoned after " + HungMinutes + " min";
                Log.Err(string.Format(
                    "supervisor: ABANDONED {0} -- alive {1:F0} min (cap {2}). Slot released, process " +
                    "left running deliberately: it may be mid-send and killing it would destroy the " +
                    "receipt. It is no longer tracked.",
                    r.Officer, (DateTime.Now - r.SpawnedAt).TotalMinutes, HungMinutes));
            }
        }

        private static void Classify(RunRecord r)
        {
            long logBytes;
            string logText = ReadTail(r.LogPath, 256 * 1024, out logBytes);

            // (c) usage-limited -- an account condition, NOT this officer's fault. Stand the whole
            // fleet down until the stated reset. The bug this replaces only scanned logs from the
            // last 30 minutes while the reset was 3h20m away, so it fired into the wall roughly
            // every half hour for three hours, each attempt showing up as a fresh fault.
            Match lm = Regex.Match(logText, @"hit your (session|usage) limit", RegexOptions.IgnoreCase);
            if (lm.Success)
            {
                WriteLimitJson(logText);
                Log.Warn("supervisor: usage limit hit -- standing the fleet down");
                return;
            }

            // A simulated `working` window that ran out is not an officer that died. Clear it and
            // say so, rather than inventing a fault for a department that was never dispatched.
            if (r.ExpiresAt != DateTime.MinValue && DateTime.Now >= r.ExpiresAt)
            {
                Faults.Remove(r.Officer);
                Log.Info("simulate: " + r.Officer + " working window elapsed, cleared");
                return;
            }

            bool advanced = false;
            try
            {
                string lp = LedgerPathOf(r.Officer);
                if (File.Exists(lp))
                    advanced = new FileInfo(lp).LastWriteTime > r.LedgerMtimeAtSpawn;
            }
            catch { }

            // NO HANDLE MEANS NO EXIT CODE -- NOT EXIT CODE -1.
            // Reconcile() rebuilds records from run.json after a restart, and those carry no Process
            // handle. Treating "unknown" as -1 made every clean iteration that finished while the
            // command center was down come back as a fault: the AND below can never pass without an
            // exit code, so `advanced` was ignored precisely when it was the only signal left. Fall
            // back to the ledger alone there -- it is the signal that actually matters, and it is
            // the one that survives the restart.
            bool haveExit = r.Handle != null;
            int exitCode = -1;
            try { if (haveExit) exitCode = r.Handle.ExitCode; }
            catch { haveExit = false; }

            if (advanced && (exitCode == 0 || !haveExit))
            {
                FailStreak[r.Officer] = 0;
                Faults.Remove(r.Officer);
                Log.Info(string.Format("clean {0} ({1:F1} min, ledger advanced{2})",
                    r.Officer, (DateTime.Now - r.SpawnedAt).TotalMinutes,
                    haveExit ? "" : ", exit code unknown -- adopted after a restart"));
                return;
            }

            int n;
            FailStreak.TryGetValue(r.Officer, out n);
            FailStreak[r.Officer] = n + 1;

            Fault(r.Officer, string.Format("exited code={0} after {1:F1} min, ledger {2} ({3} bytes logged)",
                haveExit ? exitCode.ToString(CultureInfo.InvariantCulture) : "unknown",
                (DateTime.Now - r.SpawnedAt).TotalMinutes,
                advanced ? "advanced" : "DID NOT advance", logBytes));
        }

        /// <summary>
        /// The tail of a file, and its true length. Classify only needs to find a limit banner and
        /// report a size, and it runs on the UI thread on every harvest -- so slurping the whole
        /// log was a needless multi-megabyte read of a verbose agent transcript. The banner is
        /// emitted as the session ends, so the tail is where it is. `bytes` stays the REAL file
        /// size: it is reported as evidence, and a 0-byte log is the signature of the failure this
        /// classifier exists to catch.
        /// </summary>
        private static string ReadTail(string path, int maxBytes, out long bytes)
        {
            bytes = 0;
            try
            {
                if (path == null || !File.Exists(path)) return "";
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                                                      FileShare.ReadWrite))
                {
                    bytes = fs.Length;
                    long start = Math.Max(0, fs.Length - maxBytes);
                    fs.Seek(start, SeekOrigin.Begin);
                    byte[] buf = new byte[fs.Length - start];
                    int read = fs.Read(buf, 0, buf.Length);
                    return new UTF8Encoding(false).GetString(buf, 0, read);
                }
            }
            catch { return ""; }
        }

        private static string LedgerPathOf(string officer)
        {
            List<Officer> all = Roster.Load();
            for (int i = 0; i < all.Count; i++) if (all[i].Name == officer) return all[i].LedgerPath;
            return Path.Combine(Paths.Tasks, officer + "-loop.md");
        }

        private static void Fault(string officer, string why)
        {
            Faults[officer] = why;
            Log.Warn("FAULT " + officer + ": " + why);
        }

        // ---- crash recovery ------------------------------------------------------------------

        /// <summary>
        /// On startup, adopt or discard orphaned run.json records. Critically: a claude.exe process
        /// this supervisor did NOT spawn is never touched -- not here, not on Pause, not ever.
        /// Those are the operator's own interactive sessions and killing one would be unforgivable.
        /// </summary>
        private static void Reconcile()
        {
            try
            {
                if (!Directory.Exists(Paths.State)) return;
                string[] files = Directory.GetFiles(Paths.State, "*.run.json");
                for (int i = 0; i < files.Length; i++)
                {
                    string j = File.ReadAllText(files[i]);
                    RunRecord r = new RunRecord();
                    r.Officer = Match1(j, "\"officer\"\\s*:\\s*\"([^\"]+)\"");
                    r.LogPath = Match1(j, "\"log\"\\s*:\\s*\"([^\"]+)\"");
                    string pid = Match1(j, "\"pid\"\\s*:\\s*(\\d+)");
                    string tk = Match1(j, "\"startTicks\"\\s*:\\s*(\\d+)");
                    string sp = Match1(j, "\"spawnedAt\"\\s*:\\s*\"([^\"]+)\"");
                    if (r.Officer == null || pid == null || tk == null) { File.Delete(files[i]); continue; }

                    r.Pid = int.Parse(pid);
                    r.StartTicks = long.Parse(tk);
                    DateTime t;
                    r.SpawnedAt = DateTime.TryParse(sp, CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out t) ? t : DateTime.Now;
                    r.ExpiresAt = ParseExpiry(j);
                    r.LedgerMtimeAtSpawn = ParseLedgerTicks(j);
                    if (r.LogPath != null) r.LogPath = r.LogPath.Replace("\\\\", "\\");

                    if (Alive(r))
                    {
                        Running[r.Officer] = r;      // adopt: still ours by identity tuple
                        LastAttempt[r.Officer] = r.SpawnedAt;
                        Log.Info("reconcile: adopted running " + r.Officer + " pid=" + r.Pid);
                    }
                    else
                    {
                        Classify(r);                  // it died while we were gone -- classify it
                        File.Delete(files[i]);
                        Log.Info("reconcile: harvested orphan " + r.Officer);
                    }
                }
            }
            catch (Exception ex) { Log.Err("supervisor: reconcile failed", ex); }
        }

        private static string Match1(string s, string rx)
        {
            Match m = Regex.Match(s, rx);
            return m.Success ? m.Groups[1].Value : null;
        }

        // ---- gates ---------------------------------------------------------------------------

        /// <summary>
        /// Refuse to dispatch if someone other than the operator last touched the command files since
        /// the pin. The chain being blocked: automated bot commits -> autosync pulls -> command file
        /// changes -> we spawn it with permissions disabled while he is asleep.
        /// </summary>
        private static bool AuthorGateOk()
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("git",
                    "log -1 --format=%an -- .claude/commands/");
                psi.WorkingDirectory = Paths.Repo;

                // Bounded, because this runs on the UI thread. The old `ReadToEnd()` had no timeout
                // at all -- the WaitForExit(5000) after it could not help, since ReadToEnd blocks
                // until the pipe closes. Any git that stalls (index.lock held by the autosync, a
                // credential prompt, a slow Drive-backed repo) froze the strip indefinitely.
                string outp;
                int rc = Capture(psi, 5000, out outp);
                if (rc == TimedOut)
                { Log.Warn("supervisor: author gate -- git timed out; allowing dispatch"); return true; }
                if (rc != 0) return true;                             // not a repo / no history: allow

                // stderr is captured now, so take the first non-empty line rather than the lot.
                string who = "";
                string[] lines = outp.Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    string t = lines[i].Trim();
                    if (t.Length > 0) { who = t; break; }
                }

                if (who.Length == 0) return true;                     // no history yet: allow
                if (who == Paths.OperatorName) return true;
                Log.Warn("supervisor: author gate -- command files last touched by '" + who +
                         "'; not dispatching until re-pinned");
                return false;
            }
            catch (Exception ex) { Log.Warn("supervisor: author gate check failed: " + ex.Message); return true; }
        }

        /// <summary>
        /// The dispatcher's lockfile, honoured so a manual -Only run cannot collide with us. We may
        /// break it at 10 minutes iff we own zero live children; the script's own 2h rule stays for
        /// the Task Scheduler path, which must never be installed alongside this anyway.
        /// </summary>
        private static bool LeaseOk()
        {
            try
            {
                if (!File.Exists(Paths.DispatchLock)) return true;
                double age = (DateTime.Now - new FileInfo(Paths.DispatchLock).LastWriteTime).TotalMinutes;
                if (age < LockBreakMinutes) return false;
                if (RunningCount() > 0) return false;
                File.Delete(Paths.DispatchLock);
                Log.Info(string.Format("lock: broken age={0:F0}m children=0", age));
                return true;
            }
            catch { return true; }
        }

        /// <summary>
        /// The two browser-dependent officers. halo-dispatch.ps1 skips them outright because a
        /// headless Task Scheduler run cannot reach a logged-in Chrome. We run inside the desktop
        /// session, so they MAY work -- but that is an experiment with a result, not an assumption,
        /// and until it is run they stay gated.
        /// </summary>
        private static bool BrowserGateOk()
        {
            return File.Exists(Paths.State1("browser-gate-open"));
        }

        // ---- writers (one writer per file, LF, no BOM) ----------------------------------------

        /// <summary>Same write-temp-then-move discipline, for callers outside this class.</summary>
        public static void WriteAtomicPublic(string path, string content) { WriteAtomic(path, content); }

        /// <summary>
        /// Strip the launching Claude session's identity out of a child's environment.
        ///
        /// A child process inherits its parent's environment, and if this app was started FROM a
        /// Claude Code session -- which is exactly how it gets started during development, and how
        /// it was running on 2026-08-04 -- it inherits that session's markers and hands them to
        /// every `claude` it spawns:
        ///
        ///   CLAUDE_CODE_CHILD_SESSION=1   the visible symptom: "Transcript saving is off"
        ///   CLAUDE_CODE_SESSION_ID=...    the child adopts the PARENT session's identity
        ///   CLAUDE_CODE_SSE_PORT=...      and points at the parent session's port
        ///   CLAUDECODE / CLAUDE_PID       ditto
        ///
        /// For the COO terminal that shows up as a warning banner. For the thirteen officers it is
        /// worse and silent: an officer whose transcript is not saved has no audit trail, and this
        /// whole system's claim is that you can go back and see what an unattended agent did. A
        /// dispatched loop must be a clean, independent session that happens to have been started by
        /// a program -- not a child of whatever window the program was launched from.
        ///
        /// Only the session-scoped markers go. CLAUDE_CONFIG_DIR and ANTHROPIC_* are real
        /// configuration and are deliberately left alone.
        /// </summary>
        public static void ScrubSessionEnv(ProcessStartInfo psi)
        {
            List<string> kill = new List<string>();
            foreach (string k in psi.EnvironmentVariables.Keys)
            {
                if (k == null) continue;
                string u = k.ToUpperInvariant();
                if (u.StartsWith("CLAUDE_CODE_") || u == "CLAUDECODE" || u == "CLAUDE_PID")
                    kill.Add(k);
            }
            for (int i = 0; i < kill.Count; i++) psi.EnvironmentVariables.Remove(kill[i]);
            if (kill.Count > 0)
                Log.Info("env: scrubbed " + kill.Count + " inherited session var(s) from child: " +
                         string.Join(",", kill.ToArray()));
        }

        /// <summary>
        /// Replace a file's contents in one step, LF and no BOM, creating the directory if needed.
        ///
        /// IT WAS NOT ACTUALLY ATOMIC. `Delete` then `Move` leaves a window -- short, but real, and
        /// hit by any reader polling in a loop -- in which the file DOES NOT EXIST. Every consumer
        /// here treats absence as meaning something specific and alarming: brain-surface.sh prints
        /// **"No dispatcher has ever run"** for a missing dispatch-status, which is the precise lie
        /// PublishDispatchStatus was written to stop telling. Re-introducing it a few microseconds
        /// at a time, on a file rewritten every 60 s, is not a rounding error.
        ///
        /// File.Replace is a single NTFS transaction: readers see the old bytes or the new bytes,
        /// never nothing. It requires the destination to exist, so first write falls back to Move.
        /// </summary>
        private static void WriteAtomic(string path, string content)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            string tmp = path + ".tmp";
            File.WriteAllText(tmp, content, new UTF8Encoding(false));
            if (!File.Exists(path)) { File.Move(tmp, path); return; }

            try
            {
                // No backup file, and ignore-metadata-errors: a failed ACL/attribute copy must not
                // fail the write itself.
                File.Replace(tmp, path, null, true);
            }
            catch (IOException)
            {
                // Replace is the better write but not a universal one -- it needs both files on one
                // volume and a destination nothing holds open. Falling back to the old
                // delete-then-move keeps the worst case at "same behaviour as before this change"
                // rather than "heartbeat.json stopped being written", which -Health would report as
                // the strip being DOWN while it is running perfectly.
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
        }

        private static void WriteRunJson(RunRecord r)
        {
            try
            {
                // expiresAt is emitted only when set, so a real spawn's run.json is byte-identical
                // to what it always was and cannot be mistaken for a simulation.
                string exp = r.ExpiresAt == DateTime.MinValue
                    ? "" : ",\"expiresAt\":\"" + r.ExpiresAt.ToString("o") + "\",\"sim\":true";

                // ledgerMtimeAtSpawn MUST survive to disk. It is one half of the two-signal AND,
                // and Reconcile() rebuilds records from this file after a restart. Left out, it
                // came back as MinValue, every existing ledger compared newer than it, and every
                // orphan -- including a genuine crash that wrote nothing -- would classify as
                // `clean`. A false-green on the crash path is the single failure this component
                // exists to prevent, so the baseline travels with the record.
                string j = string.Format(
                    "{{\"officer\":\"{0}\",\"pid\":{1},\"startTicks\":{2},\"spawnedAt\":\"{3}\"," +
                    "\"ledgerTicks\":{4},\"log\":\"{5}\"{6}}}\n",
                    r.Officer, r.Pid, r.StartTicks, r.SpawnedAt.ToString("o"),
                    r.LedgerMtimeAtSpawn.Ticks, r.LogPath.Replace("\\", "\\\\"), exp);
                WriteAtomic(Paths.State1(r.Officer + ".run.json"), j);
            }
            catch (Exception ex) { Log.Warn("supervisor: run.json write failed: " + ex.Message); }
        }

        /// <summary>
        /// Parse "resets 6:30pm (America/Chicago)" into a real instant and hold until it. Local tz
        /// IS America/Chicago here (Nashville). Unparseable -> 60-minute fallback: never fail open.
        /// </summary>
        private static void WriteLimitJson(string logText)
        {
            DateTime until = DateTime.Now.AddMinutes(60);
            string raw = "unknown";
            Match m = Regex.Match(logText, @"resets\s+(\d{1,2}:\d{2}\s*(?:am|pm))", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                raw = m.Groups[1].Value;
                DateTime t;
                if (DateTime.TryParseExact(raw.Replace(" ", "").ToUpperInvariant(), "h:mmtt",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out t))
                {
                    until = DateTime.Now.Date.AddHours(t.Hour).AddMinutes(t.Minute);
                    if (until < DateTime.Now) until = until.AddDays(1);
                }
            }
            try
            {
                // The account fingerprint is PERSISTED, not just held in memory: if the strip
                // restarts during a stand-down, an in-memory baseline would be re-taken from
                // whatever account is current, so a switch that happened while it was down would
                // look like no change at all -- and the fleet would sit out the old account's limit
                // on the new account. That is the exact case this is for.
                string fp = AccountFingerprint();
                WriteAtomic(Paths.State1("limit.json"), string.Format(
                    "{{\"until\":\"{0}\",\"raw\":\"{1}\",\"setAt\":\"{2}\",\"account\":\"{3}\"}}\n",
                    until.ToString("o"), raw, DateTime.Now.ToString("o"), fp == null ? "" : fp));
            }
            catch (Exception ex) { Log.Warn("supervisor: limit.json write failed: " + ex.Message); }
        }

        /// <summary>
        /// A THERMOSTAT, NOT A THERMOMETER. presend-guard.mjs caps the day at 180 while the safe
        /// rate is (900-used)/daysLeft = 26 today. A daily cap 6.9x the safe rate is not a guard,
        /// and switching on a resident supervisor makes the 2026-08-17 breach arrive sooner.
        /// </summary>
        private static void PublishBudget()
        {
            try
            {
                if (StateStore.BudgetUsed < 0) return;
                DateTime cycleEnd = new DateTime(2026, 8, 28);
                string c = Path.Combine(Paths.HookCache, "smtp2go.json");
                if (File.Exists(c))
                {
                    Match m = Regex.Match(File.ReadAllText(c), "\"cycle_end\"\\s*:\\s*\"(\\d{4}-\\d{2}-\\d{2})");
                    DateTime t;
                    if (m.Success && DateTime.TryParse(m.Groups[1].Value, CultureInfo.InvariantCulture,
                            DateTimeStyles.None, out t)) cycleEnd = t;
                }
                int daysLeft = Math.Max(1, (int)Math.Ceiling((cycleEnd - DateTime.Now).TotalDays));
                int ceiling = StateStore.BudgetCeiling;
                int allowance = Math.Max(0, (ceiling - StateStore.BudgetUsed) / daysLeft);

                WriteAtomic(Paths.State1("budget.json"), string.Format(
                    "{{\"cycleEnd\":\"{0}\",\"used\":{1},\"ceiling\":{2},\"daysLeft\":{3}," +
                    "\"dailyAllowance\":{4},\"computedAt\":\"{5}\"}}\n",
                    cycleEnd.ToString("yyyy-MM-dd"), StateStore.BudgetUsed, ceiling, daysLeft,
                    allowance, DateTime.Now.ToString("o")));
                StateStore.BudgetDaily = allowance;
            }
            catch (Exception ex) { Log.Warn("supervisor: budget.json write failed: " + ex.Message); }
        }

        /// <summary>
        /// SAY THAT WE ARE RUNNING, WHERE EVERY SESSION ALREADY LOOKS.
        ///
        /// `brain-surface.sh` gives every session -- including the COO terminal the operator actually
        /// talks to -- its loop picture, and it separates three states that look identical from
        /// ledger ages alone (loops dead / dispatcher stopped / account limited) by reading this one
        /// file. Nothing ever wrote it, so the hook took its file-absent branch and told every
        /// session **"No dispatcher has ever run"** while this supervisor was dispatching officers.
        /// supervisor.md:714 specified it as "Supervisor (every tick)". Designed, never wired.
        ///
        /// A supervisor that runs but never says so is, to every reader of the system, stopped --
        /// and being believed stopped is indistinguishable from being stopped.
        ///
        /// Schema is the existing three tokens (`value epoch [detail]`) so both readers keep working
        /// unchanged, plus one new value: `paused`. While paused the supervisor is perfectly healthy
        /// and dispatching nothing, and a fresh timestamp alone renders that as "last ran 0m ago" --
        /// healthy-looking silence over a fleet that is not moving, which is the same class of lie
        /// this method exists to end. The strip ships paused, so that is the FIRST state a new
        /// install publishes.
        ///
        /// LF and no BOM, via WriteAtomic. A CRLF or BOM in this file kills a `$(( ))` in the hook
        /// under `set -u` and silently truncates the ENTIRE session brief -- 15,165 B to 1,219 B,
        /// observed 2026-08-03.
        /// </summary>
        /// <summary>
        /// Everything the outside world reads to answer "is the dispatcher alive, and what is it
        /// deliberately not doing". One entry point so the tick and the startup call cannot drift
        /// into publishing different subsets.
        /// </summary>
        public static void PublishLiveness()
        {
            // The count has to be true the FIRST time it is published, not a minute later.
            // Reconcile is what re-adopts officers that outlived a restart, and until it has run
            // RunningCount() is 0 -- so the startup publish would tell halo-status.sh "dispatch up,
            // 0 in flight" over a fleet with two officers mid-iteration. Guarded, so this is a
            // no-op on every tick after the first, and it brings crash classification forward by a
            // minute as a side benefit.
            if (!_reconciled) { Reconcile(); _reconciled = true; }
            PublishDispatchStatus();
            PublishExclusions();
        }

        private static void PublishDispatchStatus()
        {
            try
            {
                long epoch = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0,
                                    DateTimeKind.Utc)).TotalSeconds;
                // SAME PRECEDENCE AS Tick(), WHICH TESTS Paused FIRST AND RETURNS.
                //
                // Reversed, this reported `limited` for a fleet that the operator had deliberately
                // paused, purely because an account limit happened to also be in force -- so the
                // brief told every session "it resumes on its own; nothing to fix" about a stop he
                // ordered, and would have gone on saying it after the limit lifted and dispatch
                // still did not resume. Naming the wrong reason for silence is the same failure as
                // not naming it: report the condition that is ACTUALLY holding the fleet.
                string line;
                if (StateStore.Paused)
                {
                    line = "paused " + epoch + " supervisor is up; dispatch is switched off";
                }
                else if (StateStore.IsStoodDown())
                {
                    // Token 3 is free-form to end-of-line in both readers, so the raw reset text
                    // ("7:50pm (America/Chicago)") travels intact and gets shown verbatim.
                    string w = StateStore.LimitUntilRaw;
                    if (string.IsNullOrEmpty(w)) w = StateStore.LimitUntil.ToString("t");
                    line = "limited " + epoch + " " + w.Replace("\n", " ").Replace("\r", " ");
                }
                else
                {
                    line = RunningCount().ToString(CultureInfo.InvariantCulture) + " " + epoch;
                }

                WriteAtomic(Path.Combine(Paths.HookCache, "dispatch-status"), line + "\n");
            }
            catch (Exception ex)
            {
                Log.Warn("supervisor: dispatch-status write failed: " + ex.Message);
            }
        }

        /// <summary>
        /// NAME WHAT WE ARE DELIBERATELY NOT DISPATCHING.
        ///
        /// `content` and `inbound` need a browser, so PickNext skips them until `browser-gate-open`
        /// exists. That file has never existed. The result: two of thirteen officers permanently
        /// excluded, rendering in every brief under **never** -- pixel-identical to a loop whose
        /// chain died -- with nothing anywhere saying the supervisor is doing it on purpose. Every
        /// reader, the operator included, concludes the loops are broken and goes looking for a fault
        /// that isn't there.
        ///
        /// A silent skip is worse than a loud refusal. Same behaviour, no evidence.
        ///
        /// Written every tick and EMPTY when nothing is excluded, so the brief pays for this only
        /// while it is true.
        /// </summary>
        private static void PublishExclusions()
        {
            try
            {
                List<Officer> roster = Roster.Load();
                List<string> browser = new List<string>();
                List<string> budget = new List<string>();
                List<string> blocked = new List<string>();
                bool gate = BrowserGateOk();
                bool spent = BudgetExhausted();

                for (int i = 0; i < roster.Count; i++)
                {
                    Officer o = roster[i];
                    // Same order as PickNext, so this reports the reason PickNext would actually
                    // stop at rather than a second opinion that can disagree with it.
                    if (!Roster.IsAllowed(o.Name)) { blocked.Add(o.Name); continue; }
                    if (o.NeedsBrowser && !gate) { browser.Add(o.Name); continue; }
                    if (Sends(o.Name) && spent) { budget.Add(o.Name); continue; }
                }

                StringBuilder sb = new StringBuilder();
                if (browser.Count > 0)
                    sb.Append("browser-gate " + string.Join(",", browser.ToArray()) + "\n");
                if (budget.Count > 0)
                    sb.Append("send-budget " + string.Join(",", budget.ToArray()) + "\n");
                if (blocked.Count > 0)
                    sb.Append("not-allowed " + string.Join(",", blocked.ToArray()) + "\n");

                WriteAtomic(Path.Combine(Paths.HookCache, "dispatch-excluded"), sb.ToString());
            }
            catch (Exception ex)
            {
                Log.Warn("supervisor: dispatch-excluded write failed: " + ex.Message);
            }
        }

        private static void AuditSpawn(RunRecord r, string sha, string exe)
        {
            try
            {
                string line = string.Format(
                    "{{\"ts\":\"{0}\",\"officer\":\"{1}\",\"pid\":{2},\"cmdSha256\":\"{3}\",\"exe\":\"{4}\",\"tick\":{5}}}\n",
                    DateTime.Now.ToString("o"), r.Officer, r.Pid, sha, exe.Replace("\\", "\\\\"), TickCount);
                File.AppendAllText(Path.Combine(Paths.Logs, "spawn.jsonl"), line, new UTF8Encoding(false));
            }
            catch { }
        }

        // ---- deterministic state forcing ------------------------------------------------------

        /// <summary>
        /// Drive a real supervised run with a harmless stand-in process, so every exit class can be
        /// reproduced on demand. A state that can only be observed by waiting 13 minutes for a real
        /// officer is a state nobody will ever test -- and `fault` in particular is the one claim
        /// this project makes that the whole agent-observability category cannot make, so it had
        /// better be exercised.
        ///
        /// This uses the SAME Alive() identity tuple and the SAME Classify() path as a real spawn.
        /// It only substitutes cmd.exe for claude.cmd, so nothing is mocked except the workload.
        /// </summary>
        public static string Simulate(string officer, int exitCode, int seconds, bool advanceLedger)
        {
            if (!Roster.IsAllowed(officer)) return "REFUSED: '" + officer + "' is not in the allowlist";
            try
            {
                Directory.CreateDirectory(Paths.DispatchLogs);
                string log = Path.Combine(Paths.DispatchLogs,
                    officer + "-SIM-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");

                string ledger = LedgerPathOf(officer);
                DateTime before = File.Exists(ledger) ? new FileInfo(ledger).LastWriteTime : DateTime.MinValue;

                ProcessStartInfo psi = new ProcessStartInfo("cmd.exe",
                    "/c ping -n " + Math.Max(1, seconds + 1) + " 127.0.0.1 >nul & exit " + exitCode);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                Process p = Process.Start(psi);

                RunRecord r = new RunRecord();
                r.Officer = officer;
                r.Pid = p.Id;
                r.StartTicks = p.StartTime.ToUniversalTime().Ticks;
                r.SpawnedAt = DateTime.Now;
                r.LedgerMtimeAtSpawn = before;
                r.LogPath = log;
                r.Handle = p;
                File.WriteAllText(log, "simulated run, exit=" + exitCode + "\n", new UTF8Encoding(false));
                Running[officer] = r;
                LastAttempt[officer] = DateTime.Now;
                Faults.Remove(officer);
                WriteRunJson(r);

                Log.Info(string.Format("SIMULATE {0}: pid={1} exit={2} after {3}s, ledger will {4}",
                    officer, p.Id, exitCode, seconds, advanceLedger ? "advance" : "NOT advance"));

                p.WaitForExit(Math.Max(3000, (seconds + 4) * 1000));

                // THE LEDGER MTIME IS PRODUCTION STATE, SO BORROW IT AND PUT IT BACK.
                // `halo-status.sh`, the dispatcher and the due-set all read this mtime as "when the
                // officer last wrote". Leaving it bumped means a -Mode clean run silently resets a
                // real department's cadence clock and tells the COO it reported in when it did not.
                // Two earlier runs did exactly that to tasks/ops-loop.md before this restored it.
                bool touched = false;
                if (advanceLedger && File.Exists(ledger))
                {
                    try { File.SetLastWriteTime(ledger, DateTime.Now); touched = true; }
                    catch (Exception ex) { Log.Warn("simulate: could not touch ledger: " + ex.Message); }
                }

                try { Harvest(); }
                finally
                {
                    if (touched && before != DateTime.MinValue)
                    {
                        try { File.SetLastWriteTime(ledger, before); }
                        catch (Exception ex) { Log.Warn("simulate: could NOT restore ledger mtime -- " +
                            ledger + " now reads as freshly written: " + ex.Message); }
                    }
                }

                string state = IsRunning(officer) ? "working"
                             : IsFaulted(officer) ? "fault: " + FaultReason(officer)
                             : "clean";
                return officer + " -> " + state;
            }
            catch (Exception ex) { return "simulate failed: " + ex.Message; }
        }

        /// <summary>
        /// Hold a stand-in in `working` for a BOUNDED window, so the pulse can be seen and the
        /// crash-recovery path has an orphan to reconcile.
        ///
        /// The window is a wall-clock expiry written into run.json, not a timer in this process:
        /// `-Simulate` exits immediately, and the command center that renders the strip is a
        /// different process entirely. A timer here would die with the CLI invocation and the
        /// simulation would outlive it -- which is exactly what happened before this existed.
        /// </summary>
        public static string SimulateWorking(string officer, int seconds)
        {
            if (!Roster.IsAllowed(officer)) return "REFUSED: '" + officer + "' is not in the allowlist";
            try
            {
                string ledger = LedgerPathOf(officer);

                // ATTACH TO AN ALREADY-RUNNING PROCESS RATHER THAN SPAWNING ONE.
                // Two attempts at spawning a stand-in (cmd/ping, with and without UseShellExecute)
                // both died within seconds of -Simulate exiting, so the strip never rendered
                // `working` and the test measured nothing. Adopting a process that is independently
                // long-lived removes the harness from the equation entirely: the identity tuple,
                // the disk round-trip and Alive() are all exercised for real, against a pid whose
                // liveness can be confirmed from outside with Get-Process.
                //
                // Prefer the resident command center itself; fall back to explorer. Only the pid and
                // start time are wanted, so every Process object opened here is closed here --
                // GetProcessesByName opens a handle per match and returns the whole array.
                int pid = 0;
                long startTicks = 0;
                int me = Process.GetCurrentProcess().Id;
                if (!PickStandIn("CommandCenter", me, ref pid, ref startTicks))
                    PickStandIn("explorer", me, ref pid, ref startTicks);
                if (pid == 0) return "no long-lived process available to stand in";

                RunRecord r = new RunRecord();
                r.Officer = officer;
                r.Pid = pid;
                r.StartTicks = startTicks;
                r.SpawnedAt = DateTime.Now;
                r.LedgerMtimeAtSpawn = File.Exists(ledger) ? new FileInfo(ledger).LastWriteTime : DateTime.MinValue;
                r.LogPath = Path.Combine(Paths.DispatchLogs, officer + "-SIM-working.log");
                r.ExpiresAt = DateTime.Now.AddSeconds(Math.Max(5, seconds));
                r.Handle = null;   // we did not start it and must never dispose or end it
                Running[officer] = r;
                WriteRunJson(r);
                Log.Info(string.Format("SIMULATE working {0} pid={1} until {2:HH:mm:ss}",
                    officer, pid, r.ExpiresAt));
                return officer + " -> working, pid=" + pid + ", expires " +
                       r.ExpiresAt.ToString("HH:mm:ss");
            }
            catch (Exception ex) { return "simulate failed: " + ex.Message; }
        }

        /// <summary>First live process of that name other than us, by pid and start time. Disposes
        /// every handle it opens.</summary>
        private static bool PickStandIn(string name, int selfPid, ref int pid, ref long startTicks)
        {
            Process[] all = Process.GetProcessesByName(name);
            try
            {
                for (int i = 0; i < all.Length; i++)
                {
                    if (pid != 0 || all[i].Id == selfPid) continue;
                    try { startTicks = all[i].StartTime.ToUniversalTime().Ticks; pid = all[i].Id; }
                    catch { }
                }
            }
            finally { for (int i = 0; i < all.Length; i++) { try { all[i].Dispose(); } catch { } } }
            return pid != 0;
        }

        /// <summary>
        /// Drop every trace of a simulation for one officer, or all of them. The escape hatch for
        /// the case the expiry is meant to make impossible -- because a diagnostic you cannot undo
        /// is one nobody runs twice.
        /// </summary>
        public static string ClearSimulation(string officer)
        {
            int n = 0;
            List<string> targets = new List<string>();
            if (string.IsNullOrEmpty(officer) || officer == "*")
            {
                List<Officer> all = Roster.Load();
                for (int i = 0; i < all.Count; i++) targets.Add(all[i].Name);
            }
            else targets.Add(officer);

            for (int i = 0; i < targets.Count; i++)
            {
                string t = targets[i];
                RunRecord r;
                bool had = Running.TryGetValue(t, out r) && r.ExpiresAt != DateTime.MinValue;
                if (had) Running.Remove(t);
                string p = Paths.State1(t + ".run.json");
                try
                {
                    if (File.Exists(p) && ParseExpiry(File.ReadAllText(p)) != DateTime.MinValue)
                    { File.Delete(p); had = true; }
                }
                catch { }
                if (had) { Faults.Remove(t); n++; Log.Info("simulate: cleared " + t); }
            }
            return "cleared " + n + " simulation(s)";
        }

        // ---- pause / resume ------------------------------------------------------------------

        /// <summary>
        /// PAUSE DOES NOT KILL. In-flight children run to completion. An officer killed after it
        /// sent an email but before it wrote its ledger row has lost its receipt -- that is exactly
        /// the SEC-020 data-loss class. Force-stop is a separate, explicit gesture.
        /// </summary>
        public static void SetPaused(bool paused, string reason)
        {
            try
            {
                WriteAtomic(Paths.State1("supervisor.json"), string.Format(
                    "{{\"paused\":{0},\"reason\":\"{1}\",\"lastTick\":\"{2}\",\"version\":1}}\n",
                    paused ? "true" : "false", reason, DateTime.Now.ToString("o")));
                StateStore.Paused = paused;
                Log.Info("supervisor: " + (paused ? "PAUSED" : "RUNNING") + " (" + reason + ")");
            }
            catch (Exception ex) { Log.Err("supervisor: could not persist pause state", ex); }
        }

        /// <summary>
        /// The system clock jumped. Every due-ness decision here is wall-clock arithmetic against
        /// DateTime.Now, so a BACKWARD jump (DST in November, or an NTP correction) leaves
        /// LastAttempt stamps in the future -- "minutes since last attempt" goes negative, no
        /// officer is ever due, and the fleet parks silently for the length of the jump with the
        /// strip showing everything green. Nothing would surface that; it just stops.
        ///
        /// Clamping future stamps to now costs at most one early iteration and cannot stall.
        /// </summary>
        public static void OnClockChanged()
        {
            try
            {
                DateTime now = DateTime.Now;
                List<string> ahead = new List<string>();
                foreach (KeyValuePair<string, DateTime> kv in LastAttempt)
                    if (kv.Value > now) ahead.Add(kv.Key);
                for (int i = 0; i < ahead.Count; i++) LastAttempt[ahead[i]] = now;

                if (ahead.Count > 0)
                    Log.Warn("supervisor: clock moved backward -- rebased " + ahead.Count +
                             " cadence stamp(s) that were left in the future");

                // Same grace as a power resume: after a jump forward the whole fleet reads overdue.
                SuppressFor(TimeSpan.FromMinutes(3), "system clock change");
            }
            catch (Exception ex) { Log.Err("supervisor: clock-change handling failed", ex); }
        }

        public static void SuppressFor(TimeSpan t, string why)
        {
            _suppressUntil = DateTime.Now.Add(t);
            Log.Info(string.Format("supervisor: suppressed {0:F0}s ({1})", t.TotalSeconds, why));
        }

        public static int InFlight() { return RunningCount(); }

        /// <summary>For -Selftest: which account is live, and whose limit is on file.</summary>
        public static string LimitAccountStatus()
        {
            string now = AccountFingerprint();
            string stored = StoredLimitAccount();
            if (!StateStore.IsStoodDown())
                return "account=" + (now == null ? "?" : now) + " (not stood down)";
            if (stored == null) return "account=" + (now == null ? "?" : now) + " limitAccount=unrecorded";
            return "account=" + (now == null ? "?" : now) + " limitAccount=" + stored +
                   (now != null && now != stored ? " CHANGED -- limit will lift next tick" : " (same account)");
        }

        // ---- helpers -------------------------------------------------------------------------

        public static string ResolveClaude()
        {
            // `claude` on PATH is a POSIX sh shim; Process.Start cannot exec it and fails with
            // "%1 is not a valid Win32 application". claude.cmd is the Windows entry point.
            string[] candidates = new string[] {
                Path.Combine(Paths.Home, @"scoop\apps\nodejs\current\bin\claude.cmd"),
                Path.Combine(Paths.Home, @"AppData\Roaming\npm\claude.cmd")
            };
            for (int i = 0; i < candidates.Length; i++)
                if (File.Exists(candidates[i])) return candidates[i];
            return null;
        }

        public static string Sha256File(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                using (SHA256 h = SHA256.Create())
                using (FileStream fs = File.OpenRead(path))
                {
                    byte[] d = h.ComputeHash(fs);
                    StringBuilder sb = new StringBuilder(64);
                    for (int i = 0; i < d.Length; i++) sb.Append(d[i].ToString("x2"));
                    return sb.ToString();
                }
            }
            catch { return null; }
        }

        private static string Short(string sha)
        {
            return sha == null ? "?" : sha.Substring(0, Math.Min(12, sha.Length));
        }

        private static Dictionary<string, string> _pins;

        private static string PinnedHash(string officer)
        {
            if (_pins == null)
            {
                _pins = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    string p = Paths.State1("commands.pinned.json");
                    if (File.Exists(p))
                    {
                        string j = File.ReadAllText(p);
                        foreach (Match m in Regex.Matches(j,
                            "\"([a-z]+)-loop\\.md\"\\s*:\\s*\\{\\s*\"sha256\"\\s*:\\s*\"([0-9a-f]{64})\""))
                            _pins[m.Groups[1].Value] = m.Groups[2].Value;
                    }
                }
                catch (Exception ex) { Log.Warn("supervisor: pin file unreadable: " + ex.Message); }
            }
            string v;
            return _pins.TryGetValue(officer, out v) ? v : null;
        }

        /// <summary>Pin the current hashes of all 13 command files. A deliberate human act only.</summary>
        public static int PinAll()
        {
            List<Officer> all = Roster.Load();
            StringBuilder sb = new StringBuilder();
            sb.Append("{\n");
            int n = 0;
            for (int i = 0; i < all.Count; i++)
            {
                string sha = Sha256File(all[i].CommandFile);
                if (sha == null) continue;
                if (n > 0) sb.Append(",\n");
                sb.Append(string.Format("  \"{0}-loop.md\": {{\"sha256\":\"{1}\",\"pinnedAt\":\"{2}\"}}",
                    all[i].Name, sha, DateTime.Now.ToString("o")));
                n++;
            }
            sb.Append("\n}\n");
            WriteAtomic(Paths.State1("commands.pinned.json"), sb.ToString());
            _pins = null;
            Log.Info("supervisor: pinned " + n + " command files");
            return n;
        }

        /// <summary>
        /// How many officers are pinned AND still match their command file.
        ///
        /// This counted pins that merely EXISTED, which made `pins=13/13` a lie of exactly the class
        /// this whole app exists to kill. On 2026-08-04 all thirteen pins were stale -- every
        /// command file had changed in the decision-queue commit -- and -Selftest still reported
        /// 13/13, exit 0, green. Arming dispatch would have faulted all thirteen officers at the pin
        /// gate and started nothing. A count that cannot fail is not a check.
        /// </summary>
        public static int PinnedCount() { int stale; return PinnedCount(out stale); }

        public static int PinnedCount(out int stale)
        {
            List<Officer> all = Roster.Load();
            int ok = 0;
            stale = 0;
            for (int i = 0; i < all.Count; i++)
            {
                string pinned = PinnedHash(all[i].Name);
                if (pinned == null) continue;                    // unpinned: not stale, just ungated
                string actual = Sha256File(all[i].CommandFile);
                if (actual != null && actual == pinned) ok++; else stale++;
            }
            return ok;
        }
    }
}
