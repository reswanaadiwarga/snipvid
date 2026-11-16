# install.ps1 - install SnipVid for this user and start it with Windows.
#
# What this is for: run.ps1 launches a Medium-integrity copy for development,
# which is a command you have to remember. This script turns the app into
# something you double-click: one exe under %LOCALAPPDATA%\Programs\SnipVid,
# Desktop and Start menu shortcuts, and the per-user Run entry whose check mark
# is the tray's "Start with Windows" item.
#
# The installed copy has to live outside the workspace. F:\Project\SnipVid is Low
# Integrity (AGENTS.md gotcha 6): an exe launched from it silently loses the tray
# icon and every write to Videos / %APPDATA%. Copying the built exe to
# %LOCALAPPDATA% gives it a Medium label, and the guard after the copy refuses to
# install a copy that is still Low.
#
# ASCII-only on purpose: PowerShell 5.1 reads a BOM-less script as ANSI, so a
# UTF-8 dash would decode to byte 0x94 and be parsed as a closing quote.
#
# Inputs:  build.ps1 and the src tree (the install is rebuilt, never stale).
# Outputs: %LOCALAPPDATA%\Programs\SnipVid\SnipVid.exe,
#          <Desktop>\SnipVid.lnk,
#          %APPDATA%\Microsoft\Windows\Start Menu\Programs\SnipVid.lnk,
#          HKCU\Software\Microsoft\Windows\CurrentVersion\Run\SnipVid.
# Exit:    0 when installed; 1 with the failing step named on any failure (build,
#          stop, copy, integrity guard, shortcut, Run value).
# Side effects: runs build.ps1, stops a running SnipVid, and writes the exe, the
#          two shortcuts and the Run value. Everything is per-user: no elevation
#          and no machine-wide state.
# Idempotent: every step overwrites or re-creates, so running it again replaces
#          the copy, re-points the shortcuts and re-writes the same value.

$ErrorActionPreference = 'Stop'

$root         = Split-Path -Parent $MyInvocation.MyCommand.Definition
$localAppData = [Environment]::GetFolderPath('LocalApplicationData')
$appData      = [Environment]::GetFolderPath('ApplicationData')
$installDir   = Join-Path $localAppData 'Programs\SnipVid'
$installedExe = Join-Path $installDir 'SnipVid.exe'
$builtExe     = Join-Path $root 'bin\SnipVid.exe'
$runKey       = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$valueName    = 'SnipVid'

function Fail([string]$message) {
    Write-Host "INSTALL FAILED: $message" -ForegroundColor Red
    exit 1
}

# 1. Build first: installing yesterday's exe is the outcome this script exists to
#    make impossible, so a failed build stops here with nothing half-installed.
$build = Join-Path $root 'build.ps1'
if (-not (Test-Path -LiteralPath $build -PathType Leaf)) {
    Fail "build.ps1 not found next to install.ps1 ('$build')."
}
Write-Host 'install: building (build.ps1)'
& powershell -NoProfile -ExecutionPolicy Bypass -File $build
if ($LASTEXITCODE -ne 0) {
    Fail "build.ps1 exited $LASTEXITCODE; nothing was installed."
}
if (-not (Test-Path -LiteralPath $builtExe -PathType Leaf)) {
    Fail "build.ps1 succeeded but '$builtExe' is missing."
}

# 2. Stop a running copy: the exe is locked while the app runs, and the
#    single-instance mutex would make the newly clicked copy exit silently. Any
#    SnipVid is stopped, including the run.ps1 development copy, because that copy
#    owns the same mutex and hotkey the installed one needs.
$running = @(Get-Process -Name 'SnipVid' -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    $described = ($running | ForEach-Object { "$($_.Id) $($_.Path)" }) -join '; '
    $running | Stop-Process -Force -ErrorAction SilentlyContinue
    $deadline = (Get-Date).AddSeconds(10)
    while ($null -ne (Get-Process -Name 'SnipVid' -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 100
    }
    if ($null -ne (Get-Process -Name 'SnipVid' -ErrorAction SilentlyContinue)) {
        Fail "SnipVid is still running after Stop-Process ($described); close it and re-run."
    }
    Write-Host "install: stopped the running SnipVid ($described) so the exe could be replaced."
}

# 3. Copy the built exe into a normal-integrity directory.
Write-Host "install: copying to $installDir"
try {
    New-Item -ItemType Directory -Path $installDir -Force | Out-Null
    Copy-Item -LiteralPath $builtExe -Destination $installedExe -Force
} catch {
    Fail "could not copy the exe to '$installedExe': $($_.Exception.Message)"
}
if (-not (Test-Path -LiteralPath $installedExe -PathType Leaf)) {
    Fail "'$installedExe' does not exist after the copy."
}

# 4. Refuse a copy that inherited the source folder's label: it would reproduce
#    exactly the silent failure this install exists to escape (run.ps1 uses the
#    same guard).
$label = (icacls $installedExe | Out-String)
if ($label -match 'Low Mandatory Level') {
    Fail "'$installedExe' still carries a Low integrity label, so the tray icon and all folder writes would fail. Copy it somewhere outside the workspace and re-run."
}

# 5. Two shortcuts, same target and working directory as a manual launch. WScript.Shell
#    is used because creating a .lnk without it needs COM interop that no dependency
#    policy here allows.
$shortcuts = @(
    (Join-Path ([Environment]::GetFolderPath('Desktop')) 'SnipVid.lnk'),
    (Join-Path $appData 'Microsoft\Windows\Start Menu\Programs\SnipVid.lnk')
)
$shell = New-Object -ComObject WScript.Shell
try {
    foreach ($path in $shortcuts) {
        $parent = Split-Path -Parent $path
        try {
            New-Item -ItemType Directory -Path $parent -Force | Out-Null
            $link = $shell.CreateShortcut($path)
            $link.TargetPath = $installedExe
            $link.WorkingDirectory = $installDir
            $link.Description = 'SnipVid - screen region recorder'
            $link.IconLocation = $installedExe + ',0'
            $link.Save()
        } catch {
            Fail "could not create the shortcut '$path': $($_.Exception.Message)"
        }
    }
} finally {
    [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($shell)
}

# 6. Per-user auto-start. The data is quoted for the same reason the app quotes it:
#    the Run key passes its data to CreateProcess unparsed, so a path with spaces
#    would otherwise be split at the first one.
$runData = '"' + $installedExe + '"'
try {
    if (-not (Test-Path -LiteralPath $runKey)) {
        New-Item -Path $runKey -Force | Out-Null
    }
    New-ItemProperty -Path $runKey -Name $valueName -Value $runData -PropertyType String -Force | Out-Null
} catch {
    Fail "could not write the auto-start value '$runKey\$valueName': $($_.Exception.Message)"
}

# 7. Say exactly what changed, so the result is checkable without reading the script.
Write-Host ''
Write-Host 'INSTALL OK' -ForegroundColor Green
Write-Host "  exe         $installedExe"
Write-Host "  shortcut    $($shortcuts[0])"
Write-Host "  shortcut    $($shortcuts[1])"
Write-Host "  autostart   enabled: $valueName = $runData"
Write-Host "              ($runKey)"
Write-Host 'The tray item "Start with Windows" turns this same value off and on.'
exit 0
