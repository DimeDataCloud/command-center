<#
.SYNOPSIS
  Builds the Command Center taskbar strip and makes it start with Windows. No admin, no scheduled task.

.DESCRIPTION
  A Startup-folder shortcut, deliberately -- NOT a scheduled task and NOT a Run-key entry.

  Task Scheduler is the wrong tool twice over here. It can run a task while no one is logged on,
  and this process draws on the taskbar: with no interactive desktop it would start, fail to find
  a tray, and log an error every login for a window that cannot exist. It also runs in a separate
  session whose DPI and theme do not necessarily match the user's. The Startup folder runs exactly
  once, in the real interactive session, as the real user, after the shell is up -- which is the
  only environment in which a taskbar strip is meaningful.

  The Run registry key would work, but it is invisible: nobody remembers to look in HKCU to find
  out why something starts. A .lnk you can see, rename, or delete in Explorer is the honest
  mechanism for something that has no business being hard to remove.

  SHIPS PAUSED. Installing does not start dispatching. supervisor.json's first legal value is
  {"paused":true} and this script never changes it -- thirteen unattended agents must not begin
  because someone ran an installer. the operator picks "Go" from the tray, or nothing runs.

.PARAMETER NoStart
  Install the shortcut but do not launch the app now.
#>
param([switch]$NoStart)

$ErrorActionPreference = 'Stop'

$Src     = $PSScriptRoot
$Exe     = Join-Path $env:USERPROFILE '.command-center\bin\CommandCenter.exe'
$Startup = [Environment]::GetFolderPath('Startup')
$Lnk     = Join-Path $Startup 'Command Center taskbar strip.lnk'

# --- 0. stop any running copy, gracefully -----------------------------------------------------
# build.ps1 refuses to overwrite a running binary, so without this an install over a running strip
# fails at step 1 and leaves the OLD binary in place while reporting an error -- i.e. reinstalling
# was impossible without manually quitting first. -Quit is a real Shutdown(), so the tray icon goes
# and UI settings flush; a force-kill would strand both.
if ((Test-Path $Exe) -and (Get-Process CommandCenter -ErrorAction SilentlyContinue)) {
  $q = (& $Exe -Quit | Out-String).Trim()
  Write-Host "stopping running copy: $q"
  if (Get-Process CommandCenter -ErrorAction SilentlyContinue) {
    Write-Host '  did not stop gracefully -- forcing'
    Get-Process CommandCenter -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 800
  }
}

# --- 1. build -------------------------------------------------------------------------------
& powershell -ExecutionPolicy Bypass -File (Join-Path $Src 'build.ps1')
if ($LASTEXITCODE -ne 0) { Write-Error "build failed (exit $LASTEXITCODE) -- not installing"; exit 1 }
if (-not (Test-Path $Exe)) { Write-Error "no binary at $Exe"; exit 1 }

# --- 2. shortcut ----------------------------------------------------------------------------
# Remove any earlier name first, so renaming this app cannot leave two autostart entries behind
# and two strips fighting for the same span. (The mutex would stop the second, but it would log an
# error every login forever and nobody would know why.)
Get-ChildItem $Startup -Filter '*.lnk' -ErrorAction SilentlyContinue | ForEach-Object {
  $sh = New-Object -ComObject WScript.Shell
  $t  = $sh.CreateShortcut($_.FullName).TargetPath
  if ($t -eq $Exe -and $_.FullName -ne $Lnk) {
    Write-Host "removing stale autostart entry: $($_.Name)"
    Remove-Item $_.FullName -Force
  }
}

$shell = New-Object -ComObject WScript.Shell
$sc = $shell.CreateShortcut($Lnk)
$sc.TargetPath       = $Exe
$sc.WorkingDirectory = Split-Path $Exe
$sc.Description      = 'Command Center taskbar strip -- thirteen officers, on the taskbar'
$sc.WindowStyle      = 7           # minimised; it is a tray app, it should never flash a window
$sc.Save()

# --- 3. start it ----------------------------------------------------------------------------
if (-not $NoStart) {
  $running = Get-Process CommandCenter -ErrorAction SilentlyContinue
  if ($running) { Write-Host "already running (pid $($running.Id))" }
  else { Start-Process $Exe; Start-Sleep -Seconds 3 }
}

# --- 4. verify, and say what is true ---------------------------------------------------------
Write-Host ''
Write-Host 'INSTALLED'
Write-Host "  binary     $Exe ($([int]((Get-Item $Exe).Length/1KB)) KB)"
Write-Host "  autostart  $Lnk"
Write-Host "  verified   $(Test-Path $Lnk)"

# PIPE IT. The binary is /target:winexe (GUI subsystem) so that logging in does not flash a black
# console -- and PowerShell does NOT wait on a GUI-subsystem process. A bare `& $Exe -Health` returns
# instantly, prints nothing into the pipeline, and leaves $LASTEXITCODE holding whatever the PREVIOUS
# command set. This line first reported `health rc=0` while the app was not running at all: the 0 was
# build.ps1's exit code from twenty lines earlier. Piping forces PowerShell to read stdout to EOF,
# which means it waits, which means the exit code is this process's own.
$hout = (& $Exe -Health | Out-String).Trim()
$hrc  = $LASTEXITCODE
Write-Host "  health     $hout"
Write-Host "             rc=$hrc  (0=ok 2=down 3=stale)"

$sup = "$env:USERPROFILE\.command-center\state\supervisor.json"
if (Test-Path $sup) {
  $paused = (Get-Content $sup -Raw) -match '"paused"\s*:\s*true'
  Write-Host "  dispatch   $(if ($paused) {'PAUSED -- pick Go from the tray to start officers'} else {'RUNNING'})"
}
Write-Host ''
Write-Host 'Remove it with: powershell -ExecutionPolicy Bypass -File uninstall.ps1'
exit 0
