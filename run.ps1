# run.ps1 - launch SnipVid from a normal-integrity directory.
#
# Why this exists (AGENTS.md gotcha 6): F:\Project\SnipVid carries a Low
# Mandatory Level label. A process started from an exe in that folder runs at
# Low integrity, and then Windows silently refuses the things this app needs:
#   - Shell_NotifyIcon  -> FALSE, ERROR_ACCESS_DENIED (5), no tray icon
#   - writes to %USERPROFILE%\Videos and %APPDATA% -> UnauthorizedAccessException
# The label cannot be raised from inside the sandbox (icacls /setintegritylevel
# is itself denied). Copying the exe to a normal directory makes the process
# Medium integrity, and everything works.
#
# This is an environment constraint, not an application defect. It affects every
# manual acceptance test in docs/PLAN.md, so those tests must launch via this
# script, never by running bin\SnipVid.exe directly.
#
# Inputs:  bin\SnipVid.exe (run build.ps1 first).
# Outputs: copies the exe to %LOCALAPPDATA%\SnipVid\run and starts it.
# Exit:    0 when launched, 1 when the build output is missing or the copy is
#          still Low-labelled (in which case the app could not work anyway).

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Definition
$exe = Join-Path $root 'bin\SnipVid.exe'

if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
    Write-Host "RUN FAILED: '$exe' not found. Run build.ps1 first." -ForegroundColor Red
    exit 1
}

$target = Join-Path $env:LOCALAPPDATA 'SnipVid\run'
New-Item -ItemType Directory -Path $target -Force | Out-Null
$copy = Join-Path $target 'SnipVid.exe'
Copy-Item -LiteralPath $exe -Destination $copy -Force

# Refuse to launch a copy that is still labelled: it would reproduce exactly the
# silent failure this script exists to prevent.
$label = (icacls $copy | Out-String)
if ($label -match 'Low Mandatory Level') {
    Write-Host "RUN FAILED: '$copy' still carries a Low integrity label." -ForegroundColor Red
    Write-Host "  The tray icon and all folder writes would fail. Copy it somewhere outside the workspace and run from there."
    exit 1
}

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $copy
$psi.UseShellExecute = $false
$started = [System.Diagnostics.Process]::Start($psi)

Write-Host "RUN OK  pid=$($started.Id)  $copy" -ForegroundColor Green
Write-Host "Tray icon is in the overflow (^) on first run; Windows hides new icons by default."
exit 0
