# WO06Squatter.ps1 - verifier-owned external hotkey owner for WO-06 C2/C3.
#
# WHY: C2 and C3 need a real other process that owns a chord BEFORE SnipVid
# starts. This script calls RegisterHotKey(hwnd = NULL) on its own thread and
# keeps that thread alive, so a real ERROR_HOTKEY_ALREADY_REGISTERED (1409) is
# produced for every other process that tries the same chord. Nothing about
# SnipVid is simulated or special-cased.
#
# Inputs:  -Chords 'Ctrl+Alt+Shift+S','Ctrl+Alt+Shift+F9' (any number)
#          -Seconds N (hold time, default 120)
# Outputs: one "SQUAT <chord> ok=<bool> err=<n>" line per chord, then a holding
#          line, on stdout. A pid file named wo06-squatter.<pid>.pid is written
#          into the directory named by -PidDir.
# Exit:    1 when any requested chord was not obtained, 0 otherwise.
#
# ASCII-only on purpose (PowerShell 5.1 reads a BOM-less file as ANSI).

param(
    [string[]]$Chords = @('Ctrl+Alt+Shift+S'),
    [int]$Seconds = 120,
    [string]$PidDir = ''
)

$ErrorActionPreference = 'Stop'

$Chords = @($Chords | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
if ($PidDir -eq '') { $PidDir = Join-Path $env:TEMP 'wo06-verify' }
New-Item -ItemType Directory -Path $PidDir -Force | Out-Null

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class Wo06SquatNative {
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, int modifiers, int vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
'@

function Parse-Chord([string]$chord) {
    $mods = 0
    $vk = 0
    foreach ($raw in $chord.Split('+')) {
        $token = $raw.Trim()
        if ($token -match '^(Ctrl|Control)$') { $mods = $mods -bor 2 }
        elseif ($token -match '^Alt$') { $mods = $mods -bor 1 }
        elseif ($token -match '^Shift$') { $mods = $mods -bor 4 }
        elseif ($token -match '^Win$') { $mods = $mods -bor 8 }
        else {
            if ($vk -ne 0) { throw "chord '$chord' names more than one key" }
            if ($token.Length -eq 1) {
                $c = $token.ToUpperInvariant()[0]
                if (($c -lt [char]'A' -or $c -gt [char]'Z') -and ($c -lt [char]'0' -or $c -gt [char]'9')) {
                    throw "unsupported key token '$token'"
                }
                $vk = [int]$c
            }
            elseif ($token -match '^[Ff]([0-9]|[12][0-9])$') {
                $n = [int]$token.Substring(1)
                if ($n -lt 1 -or $n -gt 24) { throw "unsupported key token '$token'" }
                $vk = 0x70 + $n - 1
            }
            else { throw "unsupported key token '$token'" }
        }
    }
    if ($mods -eq 0) { throw "chord '$chord' has no modifier" }
    if ($vk -eq 0) { throw "chord '$chord' has no key" }
    return @{ Mods = $mods; Vk = $vk }
}

$pidFile = Join-Path $PidDir ("wo06-squatter.{0}.pid" -f $PID)
Set-Content -LiteralPath $pidFile -Value $PID

$held = @()
$failed = $false
$id = 1
foreach ($chord in $Chords) {
    $parsed = Parse-Chord $chord
    $ok = [Wo06SquatNative]::RegisterHotKey([IntPtr]::Zero, $id, $parsed.Mods, $parsed.Vk)
    $err = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
    Write-Host ("SQUAT {0} ok={1} err={2}" -f $chord, $ok, $err)
    [Console]::Out.Flush()
    if ($ok) { $held += $id } else { $failed = $true }
    $id++
}

if ($held.Count -eq 0) {
    Write-Host "SQUAT nothing held; exiting"
    Remove-Item -LiteralPath $pidFile -Force -ErrorAction SilentlyContinue
    exit 1
}

Write-Host ("SQUAT holding {0} chord(s) for {1} s (pid {2})" -f $held.Count, $Seconds, $PID)
[Console]::Out.Flush()
Start-Sleep -Seconds $Seconds

foreach ($heldId in $held) { [void][Wo06SquatNative]::UnregisterHotKey([IntPtr]::Zero, $heldId) }
Remove-Item -LiteralPath $pidFile -Force -ErrorAction SilentlyContinue
if ($failed) { exit 1 }
exit 0
