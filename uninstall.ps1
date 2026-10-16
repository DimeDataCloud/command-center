<#
.SYNOPSIS
  Removes the Command Center taskbar strip: autostart entry, process, binary. Keeps logs and state.

.DESCRIPTION
  WHAT THIS DELIBERATELY DOES NOT TOUCH:

  * Running officers. If the supervisor has children in flight, this stops the strip but never
    kills a claude.exe. An officer killed after it sent an email but before it wrote its ledger
    row has lost its receipt -- that is data loss, and uninstalling a status display is not a
    reason to accept it. The children finish; they simply lose their watcher.
  * ~/.command-center/logs and ~/.command-center/state. Logs are the forensic record of everything the app
    ever did, and state holds the decision answers the operator gave through the flyout. An uninstall
    that erases the evidence of its own behaviour is not something you can audit afterwards.
    Pass -Purge if you genuinely want them gone.
  * Anything in your workspace folder. The source is versioned; removing it is git's job, not this
    script's.

.PARAMETER Purge
  Also delete ~/.command-center entirely -- logs, state, answers, the lot. Irreversible.
#>
param([switch]$Purge)

$ErrorActionPreference = 'Stop'

$Exe     = Join-Path $env:USERPROFILE '.command-center\bin\CommandCenter.exe'
$Run     = Join-Path $env:USERPROFILE '.command-center'
$Startup = [Environment]::GetFolderPath('Startup')

# --- 1. autostart ---------------------------------------------------------------------------
$removed = 0
Get-ChildItem $Startup -Filter '*.lnk' -ErrorAction SilentlyContinue | ForEach-Object {
  $sh = New-Object -ComObject WScript.Shell
  if ($sh.CreateShortcut($_.FullName).TargetPath -eq $Exe) {
    Remove-Item $_.FullName -Force; $removed++
    Write-Host "removed autostart: $($_.Name)"
  }
}
if ($removed -eq 0) { Write-Host 'no autostart entry found' }

# --- 2. report in-flight officers BEFORE stopping the watcher --------------------------------
$inflight = @(Get-ChildItem "$Run\state\*.run.json" -ErrorAction SilentlyContinue)
if ($inflight.Count -gt 0) {
  Write-Host ''
  Write-Host "NOTE: $($inflight.Count) officer run record(s) present:"
  $inflight | ForEach-Object { Write-Host "  $($_.BaseName -replace '\.run$','')" }
  Write-Host '  These processes are NOT being killed. They will finish and write their ledgers.'
  Write-Host ''
}

# --- 3. stop the strip ----------------------------------------------------------------------
# Ask first, force second. Stop-Process is TerminateProcess: Shutdown() never runs, so the
# NotifyIcon is never hidden and Windows leaves a ghost tray icon sitting there after the binary is
# already gone -- the last thing an uninstall should leave behind.
$p = Get-Process CommandCenter -ErrorAction SilentlyContinue
if ($p) {
  if (Test-Path $Exe) {
    $q = (& $Exe -Quit | Out-String).Trim()
    Write-Host "asked it to quit: $q"
  }
  $p = Get-Process CommandCenter -ErrorAction SilentlyContinue
  if ($p) { $p | Stop-Process -Force; Start-Sleep -Milliseconds 800; Write-Host "forced pid $($p.Id)" }
  else    { Write-Host 'stopped gracefully -- tray icon removed itself' }
}
else { Write-Host 'not running' }

# --- 4. binary ------------------------------------------------------------------------------
if (Test-Path $Exe) { Remove-Item $Exe -Force; Write-Host "deleted $Exe" }

# --- 5. optional purge ----------------------------------------------------------------------
if ($Purge) {
  if (Test-Path $Run) { Remove-Item $Run -Recurse -Force; Write-Host "PURGED $Run" }
} else {
  Write-Host ''
  Write-Host "kept: $Run\logs and $Run\state  (pass -Purge to delete)"
}

Write-Host ''
Write-Host 'UNINSTALLED'
Write-Host "  autostart gone   $(-not (Test-Path (Join-Path $Startup 'Command Center taskbar strip.lnk')))"
Write-Host "  binary gone      $(-not (Test-Path $Exe))"
Write-Host "  process gone     $(@(Get-Process CommandCenter -EA SilentlyContinue).Count -eq 0)"
exit 0
