// Paths.cs -- every path in one place, and the rule that governs them.
//
// Runtime files (binaries, state, logs) live in the run directory, never in the workspace that the
// strip watches. A workspace is usually a git repo that is synced to a cloud drive, and nothing the
// running process writes should end up in it.
//
// Every location can be overridden in config.json (see README.md). The file is read once, at startup,
// from %COMMAND_CENTER_CONFIG% if set, otherwise from %USERPROFILE%\.command-center\config.json.
// A missing or unreadable config file is not an error: every setting has a default.

using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace CommandCenter
{
    internal static class Paths
    {
        public static readonly string Home =
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        private static readonly Dictionary<string, string> Config = LoadConfig();

        private static string Setting(string key, string fallback)
        {
            string v;
            if (Config.TryGetValue(key, out v) && !string.IsNullOrWhiteSpace(v)) return Expand(v);
            return fallback;
        }

        // Expands %VAR% and ~ so config values can be written portably.
        private static string Expand(string v)
        {
            v = Environment.ExpandEnvironmentVariables(v);
            if (v.StartsWith("~")) v = Path.Combine(Home, v.Substring(1).TrimStart('/', '\\'));
            return v;
        }

        public static readonly string Run   = Setting("runDir", Path.Combine(Home, ".command-center"));
        public static readonly string Bin   = Path.Combine(Run, "bin");
        public static readonly string State = Setting("stateDir", Path.Combine(Run, "state"));
        public static readonly string Logs  = Setting("logDir", Path.Combine(Run, "logs"));

        // The workspace the strip watches. Defaults to a folder that does not exist, so every file
        // lookup below comes back empty and the strip shows no officers instead of failing.
        public static readonly string Repo     = Setting("workspaceDir", Path.Combine(Run, "workspace"));
        public static readonly string Brain    = Path.Combine(Repo, Setting("brainSubdir", "BrainAI"));
        public static readonly string Tasks    = Path.Combine(Brain, "tasks");
        public static readonly string Systems  = Path.Combine(Brain, "systems");
        public static readonly string Commands = Path.Combine(Repo, ".claude", "commands");
        public static readonly string Scripts  = Path.Combine(Repo, "scripts");

        public static readonly string StatusScript = Path.Combine(Systems, "halo-status.sh");
        public static readonly string DispatchPs1  = Path.Combine(Scripts, "halo-dispatch.ps1");

        // Dispatch logs and the dispatch lock. Default to the run directory.
        public static readonly string DispatchLogs = Setting("dispatchLogDir", Path.Combine(Run, "dispatch-logs"));
        public static readonly string DispatchLock = Path.Combine(Run, "dispatch.lock");

        // Shared with the SessionStart hook in the workspace, which writes smtp2go.json and
        // loop-suite-status here. Point it wherever that hook writes.
        public static readonly string HookCache = Setting("hookCacheDir",
            Path.Combine(Path.GetTempPath().TrimEnd('\\'), "command-center-hook-cache"));

        public static string State1(string name) { return Path.Combine(State, name); }

        public static readonly string OperatorName = Setting("operatorName", "");

        public static void EnsureDirs()
        {
            Directory.CreateDirectory(Run);
            Directory.CreateDirectory(Bin);
            Directory.CreateDirectory(State);
            Directory.CreateDirectory(Logs);
        }

        private static Dictionary<string, string> LoadConfig()
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string env = Environment.GetEnvironmentVariable("COMMAND_CENTER_CONFIG");
                string file = !string.IsNullOrWhiteSpace(env) ? env
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                                   ".command-center", "config.json");
                if (!File.Exists(file)) return result;

                var raw = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(
                    File.ReadAllText(file));
                if (raw == null) return result;
                foreach (var kv in raw)
                {
                    if (kv.Value is string) result[kv.Key] = (string)kv.Value;
                }
            }
            catch (Exception)
            {
                // Deliberately silent here: Log is not initialised yet. Defaults apply.
            }
            return result;
        }
    }
}
