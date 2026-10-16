<#
.SYNOPSIS
  Builds the Command Center command center with the in-box C# compiler. No SDK, no NuGet, no admin.

.DESCRIPTION
  There is no .NET SDK on this machine -- no `dotnet` CLI, no MSBuild. What there IS is
  csc.exe shipped with .NET Framework 4.8.1 at the path below, and that is enough:
  WinForms, GDI+, System.Web.Extensions' JavaScriptSerializer, and P/Invoke all compile.

  THE COMPILER IS C# 5. Verified 2026-08-04 -- the banner reads "Microsoft (R) Visual C#
  Compiler version 4.8.9221.0" and a probe using $"..." fails with CS1056: Unexpected
  character '$'. No string interpolation, no ?., no nameof, no expression-bodied members,
  no auto-property initializers anywhere in the source.

  RUN THIS FROM POWERSHELL OR CMD, NEVER GIT BASH. MSYS path-mangling rewrites /nologo into
  C:/Program Files/Git/nologo and the build dies with CS2001 on a file that does not exist.

.PARAMETER Force
  Rebuild even when the exe is newer than every source file.
#>
param([switch]$Force)

$ErrorActionPreference = 'Stop'

$Src  = $PSScriptRoot
$Out  = Join-Path $env:USERPROFILE '.command-center\bin\CommandCenter.exe'
$Csc  = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'

if (-not (Test-Path $Csc)) { Write-Error "csc.exe not found at $Csc"; exit 1 }
New-Item -ItemType Directory -Force -Path (Split-Path $Out) | Out-Null

$sources = Get-ChildItem -Path $Src -Filter *.cs | Sort-Object Name
if ($sources.Count -eq 0) { Write-Error "no .cs files in $Src"; exit 1 }

# Refuse to overwrite a running binary -- the error csc gives for a locked file is unhelpful.
$running = Get-Process CommandCenter -ErrorAction SilentlyContinue
if ($running) {
  Write-Host "CommandCenter is running (pid $($running.Id))."
  Write-Host "  Stop it gracefully with:  & '$Out' -Quit"
  Write-Host "  (or Quit from the tray, or run install.ps1 which does it for you)"
  exit 1
}

# Incremental: skip when nothing changed. A build that always runs is a build nobody runs.
if ((-not $Force) -and (Test-Path $Out)) {
  $exeTime = (Get-Item $Out).LastWriteTime
  $newest  = ($sources | Sort-Object LastWriteTime -Descending | Select-Object -First 1).LastWriteTime
  $manifest = Join-Path $Src 'app.manifest'
  if (Test-Path $manifest) {
    $mt = (Get-Item $manifest).LastWriteTime
    if ($mt -gt $newest) { $newest = $mt }
  }
  if ($newest -le $exeTime) {
    Write-Host "up to date ($([int]((Get-Item $Out).Length/1KB)) KB) -- pass -Force to rebuild"
    exit 0
  }
}

$args = @(
  '/nologo'
  '/target:winexe'
  '/platform:x64'
  '/optimize+'
  '/debug-'
  '/nowarn:1701,1702,0169,0414'
  "/win32manifest:$Src\app.manifest"
  '/reference:System.dll'
  '/reference:System.Drawing.dll'
  '/reference:System.Windows.Forms.dll'
  '/reference:System.Web.Extensions.dll'
  '/reference:System.Core.dll'
  '/reference:System.Xml.dll'
  "/out:$Out"
)
$args += ($sources | ForEach-Object { $_.FullName })

Write-Host "compiling $($sources.Count) files -> $Out"
& $Csc $args
if ($LASTEXITCODE -ne 0) { Write-Error "BUILD FAILED (exit $LASTEXITCODE)"; exit $LASTEXITCODE }

$kb = [int]((Get-Item $Out).Length / 1KB)
Write-Host "ok: $Out ($kb KB)"
exit 0
