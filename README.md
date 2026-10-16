<h1 align="center">Command Center</h1>

<p align="center">A Windows taskbar strip that shows the state of an autonomous agent fleet at a glance.</p>

## What it is

Command Center is a tray application for Windows that draws a strip on the taskbar. Each cell is
one agent "officer" (a named loop that runs an AI coding agent on a schedule), coloured by its
current state, with a summary cell for the fleet as a whole. Clicking a cell opens a flyout with
details. A supervisor inside the same process decides when each officer is due, starts the agent
process, watches for usage limits, and writes the state files that the strip reads.

It is an extract of a working system built for one operator's workspace. It is not a general agent
framework. Read the honest notes below before pointing it at anything.

## Features

- Taskbar strip that auto-fits the available taskbar span, with per-officer state colours and badges
- Tray icon, flyout with details, and a settings window
- Supervisor with a concurrency cap (`MaxConcurrent`), usage-limit detection and stand-down
- Pause never kills in-flight work: running officers finish, nothing new starts
- Ships paused: nothing dispatches until the operator picks "Go" from the tray
- Author gate: refuses to dispatch when command files were last changed by someone other than the configured operator
- `-selftest`, `-health` and `-dispatchone` switches for checks and manual runs
- Single instance, crash log, relaunch and quit switches

## Tech stack

- C# 5 on .NET Framework 4.8: WinForms, GDI+, P/Invoke, `System.Web.Extensions` for JSON
- Compiled with the in-box `csc.exe`. No .NET SDK, no NuGet packages, no admin rights needed
- PowerShell for build, install and uninstall scripts

## Quick start

Requirements: Windows 10 or 11, .NET Framework 4.8 (`C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`),
and PowerShell. Run the scripts from PowerShell or cmd, not Git Bash: Git Bash rewrites the
`/switch` arguments passed to the compiler.

```powershell
# 1. Build (writes %USERPROFILE%\.command-center\bin\CommandCenter.exe)
powershell -ExecutionPolicy Bypass -File build.ps1

# 2. Run it, and check its health
& "$env:USERPROFILE\.command-center\bin\CommandCenter.exe"
& "$env:USERPROFILE\.command-center\bin\CommandCenter.exe" -health

# 3. Optional: start with Windows (Startup-folder shortcut, no admin)
powershell -ExecutionPolicy Bypass -File install.ps1

# Remove it
powershell -ExecutionPolicy Bypass -File uninstall.ps1
```

Verified on Windows 11 (build 10.0.26200) with the in-box compiler: `build.ps1` produced
`CommandCenter.exe` (144 KB, 18 source files) and a second run reported "up to date". `-selftest`
run against an empty workspace exits `2`, because the roster is empty and the self-test reports
`roster=DRIFT(0)`. That is the expected result without a workspace, not a crash. The install script
was not run during verification, because it changes the Startup folder.

## Configuration

Settings are read once at startup from `%USERPROFILE%\.command-center\config.json`. Set
`COMMAND_CENTER_CONFIG` to use a different file. Every key is optional. Values may use
`%VAR%` environment variables and a leading `~` for the user profile.

```json
{
  "workspaceDir": "C:\\src\\my-agent-workspace",
  "brainSubdir": "BrainAI",
  "operatorName": "Your Name",
  "runDir": "%USERPROFILE%\\.command-center"
}
```

| Key | Default | Purpose |
|---|---|---|
| `runDir` | `%USERPROFILE%\.command-center` | Binaries, state, and logs. Keep it outside any synced or shared folder. |
| `stateDir` | `<runDir>\state` | State JSON the strip reads: `supervisor.json`, `heartbeat.json`, `budget.json`, `limit.json`, `ui.json`. |
| `logDir` | `<runDir>\logs` | Daily logs. |
| `dispatchLogDir` | `<runDir>\dispatch-logs` | One log per officer run. Pruned hourly. |
| `hookCacheDir` | `%TEMP%\command-center-hook-cache` | Cache shared with a session-start hook in the workspace (`smtp2go.json`, `loop-suite-status`). |
| `workspaceDir` | `<runDir>\workspace` (missing, so no officers) | The repo the officers run in. It must contain the roster script, the command files and the scripts described below. |
| `brainSubdir` | `BrainAI` | Folder inside the workspace that holds `tasks\` and `systems\`. |
| `operatorName` | empty | Git author name allowed to change command files. **Empty blocks dispatch**, which is the safe default. |

### What the workspace must contain

The strip has no roster of its own. It builds the officer list from the workspace:

| Path in the workspace | Used for |
|---|---|
| `<brainSubdir>\systems\halo-status.sh` | Roster and desk membership. Parsed, never executed. |
| `<brainSubdir>\tasks\*.md` | One ledger per officer. Read for mtime and blocker lines. |
| `.claude\commands\<name>-loop.md` | Command file per officer. Its heading holds the cadence. |
| `scripts\halo-dispatch.ps1` | Used with `-DryRun` to get the due set. |

Names are fixed at compile time in `Roster.cs` (`Allowed`). A name parsed from the roster that is not
on that list is refused and logged, so file contents never reach a process argument.

### Note on `fleet.json`

The design docs for this project describe the strip reading a `fleet.json` status file. **This code
does not read or write one.** Its status comes from the state files in the table above, and from a
`dispatch-status` line it publishes to `hookCacheDir`. If you need a single status file for another
tool, write it from `Supervisor.cs` and point the strip at it; nothing here depends on one.

## Safety notes

- Dispatched agents run with `--dangerously-skip-permissions`, unattended. Read `Supervisor.cs` and the
  command files before you press Go, and point `workspaceDir` at a workspace you trust.
- The command files are a trust boundary. The author gate blocks dispatch when the last commit to
  them is not from `operatorName`. Review changes before re-pinning them.
- Usage-limit detection stands the whole fleet down. A false positive stops every officer, so the
  detection rules are deliberately narrow.

## Project structure

```
.
├── Program.cs          Entry point, single instance, tray menu, -selftest and -health
├── Paths.cs            Every path, and the config.json loader
├── Supervisor.cs       Dispatch, concurrency, usage limits, pause and stand-down
├── StateStore.cs       Reads the state files the supervisor writes
├── Roster.cs           Parses the officer roster from the workspace
├── StripWindow.cs      The taskbar strip window and its placement
├── IconRenderer.cs     Draws cells, states and badges
├── BrandMarks.cs       Per-officer marks
├── Flyout.cs           Details panel for a cell
├── CommandTerminal.cs  Opens an interactive session in the workspace
├── SettingsForm.cs     Strip settings window
├── UiSettings.cs       Persisted UI choices (in runDir, never in the workspace)
├── Animator.cs, Geometry.cs, Theme.cs, Tooltip.cs, Native.cs, Log.cs
├── build.ps1           Compiles with csc.exe; skips when nothing changed
├── install.ps1         Startup-folder shortcut and first start
├── uninstall.ps1       Removes the shortcut and the binary; keeps logs and state
└── app.manifest        DPI awareness and common controls
```

## License

MIT. Copyright (c) 2026 Dime Data. See [LICENSE](LICENSE).
