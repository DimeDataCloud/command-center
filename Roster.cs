// Roster.cs -- the thirteen officers, PARSED from halo-status.sh, never hand-authored here.
//
// WHY NOT A CONST ARRAY. The roster already exists in five places that drift independently:
//   1. each command file's "## Cadence -- N minutes" heading
//   2. BrainAI/context/loops.md's Cadence column
//   3. the SessionStart hook's LOOPSPEC (brain-surface.sh:131)
//   4. halo-dispatch.ps1's $Loops (:61)
//   5. halo-status.sh's LOOPS (:23) -- THE ONLY ONE CARRYING DESK MEMBERSHIP
// check-loop-suite.sh check 10 already reconciles 1-3. Hand-authoring a sixth copy here would be
// solving drift by adding drift, so this parses #5 and merges the `browser` flag from #4.
//
// The security allowlist below is the ONE deliberate hardcoded copy, and it exists precisely
// because it must not be reachable from a file. See Allowed.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace CommandCenter
{
    internal class Officer
    {
        public string Name;
        public string Ledger;
        public int CadenceMin;
        public string Desk;
        public bool NeedsBrowser;
        public int Index;

        public string LedgerPath { get { return Path.Combine(Paths.Tasks, Ledger + ".md"); } }
        public string CommandFile { get { return Path.Combine(Paths.Commands, Name + "-loop.md"); } }
    }

    internal static class Roster
    {
        public static readonly string[] Desks = new string[] { "Sell", "Attract", "Capital", "Machine" };

        // THE SPAWN ALLOWLIST. Supervisor builds its argument vector from this array and nothing
        // else. A name parsed out of halo-status.sh that is not in here is refused and logged --
        // so no file content can ever reach a process argument. This matters because the repo those
        // files live in receives commits from a automated bot every ~20 minutes, and the supervisor
        // spawns with --dangerously-skip-permissions, unattended.
        private static readonly string[] Allowed = new string[] {
            "outbound", "call", "offer", "rep",
            "inbound", "content", "research",
            "funding", "partner", "legal",
            "security", "eng", "ops"
        };

        public static bool IsAllowed(string name)
        {
            for (int i = 0; i < Allowed.Length; i++) if (Allowed[i] == name) return true;
            return false;
        }

        private static List<Officer> _cache;

        public static List<Officer> Load() { return Load(false); }

        public static List<Officer> Load(bool force)
        {
            if (_cache != null && !force) return _cache;
            List<Officer> list = Parse();
            _cache = list;
            return list;
        }

        /// <summary>
        /// Parse the LOOPS heredoc out of halo-status.sh. Format, one per line:
        ///     name:ledger:cadence-min:Desk
        /// Then merge NeedsBrowser from halo-dispatch.ps1's $Loops (its only unique field).
        /// </summary>
        private static List<Officer> Parse()
        {
            List<Officer> list = new List<Officer>();
            try
            {
                string sh = File.ReadAllText(Paths.StatusScript);
                int i = sh.IndexOf("LOOPS=\"", StringComparison.Ordinal);
                if (i < 0) { Log.Err("roster: LOOPS block not found in halo-status.sh"); return list; }
                int start = i + 7;
                int end = sh.IndexOf('"', start);
                if (end < 0) { Log.Err("roster: unterminated LOOPS block"); return list; }

                string block = sh.Substring(start, end - start);
                string[] lines = block.Split('\n');
                for (int k = 0; k < lines.Length; k++)
                {
                    string line = lines[k].Trim();
                    if (line.Length == 0) continue;
                    string[] p = line.Split(':');
                    if (p.Length < 4) { Log.Warn("roster: skipping malformed line: " + line); continue; }

                    int cad;
                    if (!int.TryParse(p[2].Trim(), out cad) || cad <= 0)
                    { Log.Warn("roster: bad cadence on line: " + line); continue; }

                    string name = p[0].Trim();
                    if (!IsAllowed(name))
                    {
                        // Not a crash: a new officer may be added to the contract before this binary
                        // is rebuilt. But it will never be spawned, and that is said out loud.
                        Log.Warn("roster: '" + name + "' is not in the spawn allowlist -- " +
                                 "it will render but will never be dispatched. Rebuild to add it.");
                    }

                    Officer o = new Officer();
                    o.Name = name;
                    o.Ledger = p[1].Trim();
                    o.CadenceMin = cad;
                    o.Desk = p[3].Trim();
                    o.Index = list.Count;
                    list.Add(o);
                }

                MergeBrowserFlags(list);
                Log.Info(string.Format("roster: {0} officers parsed from halo-status.sh", list.Count));
            }
            catch (Exception ex)
            {
                Log.Err("roster: parse failed", ex);
            }
            return list;
        }

        private static void MergeBrowserFlags(List<Officer> list)
        {
            try
            {
                string ps = File.ReadAllText(Paths.DispatchPs1);
                // @{ n='inbound';  l='revenue-loop-inbound';  m=10; browser=$true  }
                Regex rx = new Regex(@"n='(?<n>[a-z]+)'.*?browser=\$(?<b>true|false)",
                                     RegexOptions.IgnoreCase);
                foreach (Match m in rx.Matches(ps))
                {
                    string n = m.Groups["n"].Value;
                    bool b = m.Groups["b"].Value.ToLowerInvariant() == "true";
                    for (int i = 0; i < list.Count; i++)
                        if (list[i].Name == n) list[i].NeedsBrowser = b;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("roster: could not merge browser flags: " + ex.Message);
            }
        }

        public static List<Officer> OfDesk(string desk)
        {
            List<Officer> outp = new List<Officer>();
            List<Officer> all = Load();
            for (int i = 0; i < all.Count; i++) if (all[i].Desk == desk) outp.Add(all[i]);
            return outp;
        }

        /// <summary>Serialise for state\roster.json. check-loop-suite check 15 diffs against this.</summary>
        public static string ToJson()
        {
            List<Officer> all = Load();
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.Append("[\n");
            for (int i = 0; i < all.Count; i++)
            {
                Officer o = all[i];
                sb.Append(string.Format(
                    "  {{\"name\":\"{0}\",\"ledger\":\"{1}\",\"cadenceMin\":{2},\"desk\":\"{3}\",\"browser\":{4}}}{5}\n",
                    o.Name, o.Ledger, o.CadenceMin, o.Desk,
                    o.NeedsBrowser ? "true" : "false",
                    i < all.Count - 1 ? "," : ""));
            }
            sb.Append("]\n");
            return sb.ToString();
        }
    }
}
