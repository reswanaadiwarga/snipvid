# WO06Acceptance.ps1 - verifier-owned acceptance harness for WO-06 (hotkey).
#
# Independent of the implementer's tmp\ instruments: this file builds its own
# key injector, its own external hotkey squatter (WO06Squatter.ps1), its own
# window/message-loop probe and its own assertions. Expected values come from
# docs/DESIGN.md sections 1, 8, 9 and docs/WORKORDERS.md WO-06 C1-C3, not from
# the app's output.
#
# Inputs:  -Scenario c1 | c2 | c3 | release
# Outputs: PASS/FAIL lines with the raw log lines they refer to.
# Exit:    0 when every check in the scenario passed, 1 otherwise.
#
# c1      : press the configured chord 3x after a >= 6 s delay; assert one log
#           line per press, a process-start clock origin and t+ deltas that
#           match the measured wall-clock gaps.
# c2      : external process owns Ctrl+Alt+Shift+S before launch; assert the
#           fallback F9 is registered, exactly one startup note names it, the
#           squatted chord produces no invocation and the fallback does.
# c3      : both chords squatted before launch; assert one disarm note naming
#           both, no invocation, and that the message loop is alive (WM_NULL
#           answered, then a graceful WM_QUIT shutdown with exit code 0).
# release : while armed, an external RegisterHotKey of the armed chord must
#           fail 1409; after a graceful exit the same call must succeed.
#
# ASCII-only on purpose (PowerShell 5.1 reads a BOM-less file as ANSI).
# Launch rule: every app run goes through run.ps1 (AGENTS.md gotcha 6).

param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('c1', 'c2', 'c3', 'release')]
    [string]$Scenario
)

$ErrorActionPreference = 'Stop'

$Repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$Log = Join-Path $env:LOCALAPPDATA 'SnipVid\run\SnipVid.log'
$TempDir = Join-Path $env:TEMP 'wo06-verify'
$Squatter = Join-Path $PSScriptRoot 'WO06Squatter.ps1'
New-Item -ItemType Directory -Path $TempDir -Force | Out-Null

$script:Checks = 0
$script:Failures = 0
$script:SquatterProcs = @()

Add-Type @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class Wo06Native {
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, int modifiers, int vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, IntPtr dwExtraInfo);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    // Exit code via a handle opened before the process dies: the Process object
    // returned by Get-Process reported no ExitCode after the graceful shutdown
    // (measured), so the harness asks Win32 directly.
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr handle);

    private delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumProc callback, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder name, int max);
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);

    // One "hwnd|thread|class|visible|wm_null_answered" line per top-level window
    // owned by the pid. SMTO_ABORTIFHUNG (0x0002) so a hung thread reports false
    // instead of blocking this probe.
    public static string[] Describe(uint targetPid) {
        List<string> lines = new List<string>();
        EnumWindows(delegate(IntPtr hWnd, IntPtr lParam) {
            uint pid;
            GetWindowThreadProcessId(hWnd, out pid);
            if (pid == targetPid) {
                uint owner;
                uint tid = GetWindowThreadProcessId(hWnd, out owner);
                StringBuilder name = new StringBuilder(256);
                GetClassName(hWnd, name, name.Capacity);
                IntPtr result;
                bool pumping = SendMessageTimeout(
                    hWnd, 0, IntPtr.Zero, IntPtr.Zero, 0x0002, 2000, out result) != IntPtr.Zero;
                lines.Add(hWnd.ToInt64() + "|" + tid + "|" + name.ToString() + "|"
                    + IsWindowVisible(hWnd) + "|" + pumping);
            }
            return true;
        }, IntPtr.Zero);
        return lines.ToArray();
    }
}
'@

function Check([string]$name, [bool]$ok, [string]$detail) {
    $script:Checks++
    if ($ok) {
        Write-Host ("PASS {0}" -f $name)
    }
    else {
        $script:Failures++
        Write-Host ("FAIL {0} -- {1}" -f $name, $detail)
    }
}

function Get-App {
    $all = @(Get-Process SnipVid -ErrorAction SilentlyContinue)
    if ($all.Count -eq 0) { return $null }
    return $all[0]
}

function Stop-AllApps {
    @(Get-Process SnipVid -ErrorAction SilentlyContinue) | ForEach-Object {
        Write-Host ("cleanup: stopping SnipVid pid {0}" -f $_.Id)
        Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
    }
}

function Stop-AllSquatters {
    Get-ChildItem -LiteralPath $TempDir -Filter 'wo06-squatter.*.pid' -ErrorAction SilentlyContinue | ForEach-Object {
        $stale = 0
        [void][int]::TryParse((Get-Content -LiteralPath $_.FullName -Raw), [ref]$stale)
        if ($stale -gt 0) { Stop-Process -Id $stale -Force -ErrorAction SilentlyContinue }
        Remove-Item -LiteralPath $_.FullName -Force -ErrorAction SilentlyContinue
    }
    foreach ($p in $script:SquatterProcs) {
        if ($p -and -not $p.HasExited) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
    }
}

function Get-LogCount {
    if (-not (Test-Path -LiteralPath $Log)) { return 0 }
    return @(Get-Content -LiteralPath $Log).Count
}

function Get-NewLogLines([int]$from) {
    if (-not (Test-Path -LiteralPath $Log)) { return @() }
    return @(Get-Content -LiteralPath $Log | Select-Object -Skip $from)
}

function Start-App {
    # run.ps1 is started with its stdout/stderr redirected to files, not
    # inherited: the launched app inherits those handles too, and an inherited
    # console pipeline would keep this script's own stdout open until the app
    # exits (measured: the harness hung at this call for 120 s).
    $tag = [guid]::NewGuid().ToString('N').Substring(0, 8)
    $out = Join-Path $TempDir ("run-{0}.out" -f $tag)
    $err = Join-Path $TempDir ("run-{0}.err" -f $tag)
    $rp = Start-Process powershell -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
        (Join-Path $Repo 'run.ps1') -RedirectStandardOutput $out -RedirectStandardError $err `
        -PassThru -WindowStyle Hidden
    [void]$rp.WaitForExit(20000)
    foreach ($line in @(Get-Content -LiteralPath $out -ErrorAction SilentlyContinue)) { Write-Host ("  run.ps1: " + $line) }
    foreach ($line in @(Get-Content -LiteralPath $err -ErrorAction SilentlyContinue)) { Write-Host ("  run.ps1 ERR: " + $line) }
    for ($i = 0; $i -lt 40; $i++) {
        $p = Get-App
        if ($p) { return $p }
        Start-Sleep -Milliseconds 250
    }
    throw "SnipVid did not appear within 10 s"
}

function Get-Chord([string]$chord) {
    $mods = @()
    $vk = 0
    foreach ($raw in $chord.Split('+')) {
        $token = $raw.Trim()
        if ($token -match '^(Ctrl|Control)$') { $mods += 0x11 }
        elseif ($token -match '^Alt$') { $mods += 0x12 }
        elseif ($token -match '^Shift$') { $mods += 0x10 }
        elseif ($token -match '^Win$') { $mods += 0x5B }
        else {
            if ($vk -ne 0) { throw "chord '$chord' names more than one key" }
            if ($token.Length -eq 1) { $vk = [int][char]$token.ToUpperInvariant()[0] }
            elseif ($token -match '^[Ff]([0-9]|[12][0-9])$') { $vk = 0x70 + [int]$token.Substring(1) - 1 }
            else { throw "unsupported key token '$token'" }
        }
    }
    if ($vk -eq 0) { throw "chord '$chord' names no key" }
    return @{ Modifiers = $mods; Vk = $vk }
}

function Press-Chord([string]$chord) {
    $k = Get-Chord $chord
    foreach ($m in $k.Modifiers) { [Wo06Native]::keybd_event([byte]$m, 0, 0, [IntPtr]::Zero) }
    [Wo06Native]::keybd_event([byte]$k.Vk, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 80
    [Wo06Native]::keybd_event([byte]$k.Vk, 0, 2, [IntPtr]::Zero)
    foreach ($m in $k.Modifiers) { [Wo06Native]::keybd_event([byte]$m, 0, 2, [IntPtr]::Zero) }
}

function Start-Squatter([string[]]$chords) {
    $tag = [guid]::NewGuid().ToString('N').Substring(0, 8)
    $out = Join-Path $TempDir ("squat-{0}.out" -f $tag)
    $err = Join-Path $TempDir ("squat-{0}.err" -f $tag)
    Remove-Item -LiteralPath $out, $err -Force -ErrorAction SilentlyContinue
    $p = Start-Process powershell -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $Squatter,
        '-Chords', ($chords -join ','), '-Seconds', '120', '-PidDir', $TempDir `
        -RedirectStandardOutput $out -RedirectStandardError $err -PassThru -WindowStyle Hidden
    $script:SquatterProcs += $p
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $deadline) {
        # [string] coercion matters: Get-Content -Raw of an empty file is $null,
        # and `$null -notmatch x` is $null (falsy), which would wrongly report a
        # not-yet-written squatter as ready. Measured on this harness.
        $text = if (Test-Path -LiteralPath $out) { [string](Get-Content -LiteralPath $out -Raw) } else { '' }
        $ready = $true
        foreach ($chord in $chords) {
            if ($text -notmatch [regex]::Escape("SQUAT $chord ok=True")) { $ready = $false }
        }
        if ($ready) { return @{ Process = $p; Out = $out; Err = $err; Text = $text } }
        if ($p.HasExited) { break }
        Start-Sleep -Milliseconds 250
    }
    $text = if (Test-Path -LiteralPath $out) { [string](Get-Content -LiteralPath $out -Raw) } else { '' }
    $errText = if (Test-Path -LiteralPath $err) { Get-Content -LiteralPath $err -Raw } else { '' }
    throw "squatter did not obtain the requested chord(s):`n$text`n$errText"
}

function Stop-Gracefully([System.Diagnostics.Process]$proc) {
    $described = [Wo06Native]::Describe([uint32]$proc.Id)
    if ($described.Count -eq 0) {
        return @{ Exited = $false; ExitCode = $null; Thread = 0; Posted = $false; Windows = $described }
    }
    $threadId = [uint32]($described[0].Split('|')[1])
    $handle = [Wo06Native]::OpenProcess(0x00100000 -bor 0x1000, $false, [uint32]$proc.Id)
    $posted = [Wo06Native]::PostThreadMessage($threadId, 0x0012, [IntPtr]::Zero, [IntPtr]::Zero)
    $wait = [Wo06Native]::WaitForSingleObject($handle, 5000)
    $exited = ($wait -eq 0)
    $code = $null
    if ($exited) {
        [uint32]$nativeCode = 0
        $got = [Wo06Native]::GetExitCodeProcess($handle, [ref]$nativeCode)
        if ($got) { $code = [int]$nativeCode }
    }
    if ($handle -ne [IntPtr]::Zero) { [void][Wo06Native]::CloseHandle($handle) }
    return @{ Exited = $exited; ExitCode = $code; Thread = $threadId; Posted = $posted; Windows = $described }
}

function Write-Lines([string]$title, $lines) {
    Write-Host ("--- {0} ---" -f $title)
    if (@($lines).Count -eq 0) { Write-Host "  (none)" }
    foreach ($line in $lines) { Write-Host ("  " + $line) }
}

$configPath = Join-Path $env:APPDATA 'SnipVid\config.ini'
$configHotkey = ''
if (Test-Path -LiteralPath $configPath) {
    $line = @(Get-Content -LiteralPath $configPath | Where-Object { $_ -match '^\s*hotkey\s*=' })
    if ($line.Count -gt 0) { $configHotkey = ($line[0] -split '=', 2)[1].Trim() }
}
Write-Host ("config hotkey = '{0}'" -f $configHotkey)

Stop-AllApps
Stop-AllSquatters
Start-Sleep -Milliseconds 700

try {
    switch ($Scenario) {
        'c1' {
            Write-Host "===== WO-06 C1: timestamp per press, process-start clock origin ====="
            Check "config carries the default chord Ctrl+Alt+Shift+S before C1" ($configHotkey -eq 'Ctrl+Alt+Shift+S') "config hotkey is '$configHotkey'"

            $before = Get-LogCount
            $app = Start-App
            $launch = Get-Date
            Write-Host ("launched pid={0} at {1:HH:mm:ss.fff}; waiting 6 s before the first press" -f $app.Id, $launch)
            Start-Sleep -Seconds 6

            $injected = @()
            $gaps = @(1200, 1700)
            for ($i = 0; $i -lt 3; $i++) {
                $at = Get-Date
                Press-Chord 'Ctrl+Alt+Shift+S'
                $injected += $at
                Write-Host ("injected press {0} at {1:HH:mm:ss.fff} ({2:N0} ms after run.ps1 returned)" -f ($i + 1), $at, ($at - $launch).TotalMilliseconds)
                if ($i -lt 2) { Start-Sleep -Milliseconds $gaps[$i] }
            }
            Start-Sleep -Milliseconds 700

            $new = @(Get-NewLogLines $before)
            Write-Lines "log lines since launch" $new
            $invoked = @($new | Where-Object { $_ -match 'invoked at t\+(\d+) ms' })

            Check "one log line per press: 3 presses -> 3 lines" ($invoked.Count -eq 3) ("got {0}: {1}" -f $invoked.Count, ($invoked -join ' | '))
            Check "every line names the armed chord Ctrl+Alt+Shift+S" (($invoked.Count -eq 3) -and (@($invoked | Where-Object { $_ -notmatch 'hotkey: Ctrl\+Alt\+Shift\+S invoked' }).Count -eq 0)) ($invoked -join ' | ')

            $values = @()
            foreach ($line in $invoked) { if ($line -match 'invoked at t\+(\d+) ms') { $values += [int]$Matches[1] } }
            Write-Host ("t+ values: {0}" -f ($values -join ', '))

            if ($values.Count -ge 1) {
                Check "first t+ >= 6000 ms (clock origin is process start, not first press)" ($values[0] -ge 6000) ("first t+ = {0} ms after a 6 s wait" -f $values[0])
            }
            else {
                Check "first t+ >= 6000 ms (clock origin is process start, not first press)" $false "no invoked line"
            }

            if ($values.Count -ge 3) {
                $logDelta1 = $values[1] - $values[0]
                $wallDelta1 = [int](($injected[1] - $injected[0]).TotalMilliseconds)
                $logDelta2 = $values[2] - $values[1]
                $wallDelta2 = [int](($injected[2] - $injected[1]).TotalMilliseconds)
                Write-Host ("delta 1: log {0} ms vs wall {1} ms; delta 2: log {2} ms vs wall {3} ms" -f $logDelta1, $wallDelta1, $logDelta2, $wallDelta2)
                Check "increment 1 matches the wall-clock gap (+/-150 ms)" ([math]::Abs($logDelta1 - $wallDelta1) -le 150) ("log {0} vs wall {1}" -f $logDelta1, $wallDelta1)
                Check "increment 2 matches the wall-clock gap (+/-150 ms)" ([math]::Abs($logDelta2 - $wallDelta2) -le 150) ("log {0} vs wall {1}" -f $logDelta2, $wallDelta2)

                # The wall-clock stamp on each line must belong to the press it
                # reports, so the t+ value is checkable against real time.
                for ($i = 0; $i -lt 3; $i++) {
                    $stampText = $invoked[$i].Substring(0, 23)
                    $stamp = [datetime]::ParseExact($stampText, 'yyyy-MM-dd HH:mm:ss.fff', $null)
                    $diff = [math]::Abs(($stamp - $injected[$i]).TotalMilliseconds)
                    Check ("line {0} wall-clock stamp is within 2 s of the injection" -f ($i + 1)) ($diff -le 2000) ("stamp {0} vs injection {1:HH:mm:ss.fff}" -f $stampText, $injected[$i])
                }
            }
            else {
                Check "increment 1 matches the wall-clock gap (+/-150 ms)" $false "fewer than 3 invoked lines"
                Check "increment 2 matches the wall-clock gap (+/-150 ms)" $false "fewer than 3 invoked lines"
            }
        }

        'c2' {
            Write-Host "===== WO-06 C2: squatted chord -> fallback chord ====="
            Check "config carries the default chord Ctrl+Alt+Shift+S before C2" ($configHotkey -eq 'Ctrl+Alt+Shift+S') "config hotkey is '$configHotkey'"

            $squat = Start-Squatter @('Ctrl+Alt+Shift+S')
            Write-Lines "external squatter output (before SnipVid starts)" @(Get-Content -LiteralPath $squat.Out)
            Check "external process really owns Ctrl+Alt+Shift+S" ($squat.Text -match 'SQUAT Ctrl\+Alt\+Shift\+S ok=True') $squat.Text

            $before = Get-LogCount
            $app = Start-App
            Write-Host ("launched pid={0}; waiting 2 s for notes and arming" -f $app.Id)
            Start-Sleep -Seconds 2

            $startup = @(Get-NewLogLines $before)
            Write-Lines "startup log lines" $startup
            $notes = @($startup | Where-Object { $_ -match '\snote: ' })
            Check "exactly one startup note" ($notes.Count -eq 1) ("got {0}: {1}" -f $notes.Count, ($notes -join ' | '))
            $note = if ($notes.Count -gt 0) { $notes[0] } else { '' }
            Check "the note names the squatted chord Ctrl+Alt+Shift+S" ($note -match 'Ctrl\+Alt\+Shift\+S') $note
            Check "the note names the fallback Ctrl+Alt+Shift+F9" ($note -match 'Ctrl\+Alt\+Shift\+F9') $note
            Check "the note says the chord was already registered and the fallback is in use" (($note -match 'already registered') -and ($note -match 'using')) $note
            Check "the log records arming with the fallback Ctrl+Alt+Shift+F9" (@($startup | Where-Object { $_ -match 'hotkey: armed Ctrl\+Alt\+Shift\+F9' }).Count -eq 1) ($startup -join ' | ')

            $mark = Get-LogCount
            Press-Chord 'Ctrl+Alt+Shift+S'
            Start-Sleep -Milliseconds 1200
            $afterS = @(Get-NewLogLines $mark)
            Write-Lines "press Ctrl+Alt+Shift+S (still owned by the squatter)" $afterS
            Check "the squatted chord invokes nothing" (@($afterS | Where-Object { $_ -match 'invoked at t\+' }).Count -eq 0) ($afterS -join ' | ')

            $mark = Get-LogCount
            $t1 = Get-Date
            Press-Chord 'Ctrl+Alt+Shift+F9'
            Start-Sleep -Milliseconds 1300
            $t2 = Get-Date
            Press-Chord 'Ctrl+Alt+Shift+F9'
            Start-Sleep -Milliseconds 900
            $afterF9 = @(Get-NewLogLines $mark)
            Write-Lines "press Ctrl+Alt+Shift+F9 x2 (the fallback)" $afterF9
            $f9 = @($afterF9 | Where-Object { $_ -match 'invoked at t\+(\d+) ms' })
            Check "the fallback chord invokes: 2 presses -> 2 lines" ($f9.Count -eq 2) ("got {0}: {1}" -f $f9.Count, ($f9 -join ' | '))
            Check "each fallback line names Ctrl+Alt+Shift+F9" (($f9.Count -eq 2) -and (@($f9 | Where-Object { $_ -notmatch 'hotkey: Ctrl\+Alt\+Shift\+F9 invoked' }).Count -eq 0)) ($f9 -join ' | ')

            if ($f9.Count -eq 2) {
                $v = @()
                foreach ($line in $f9) { if ($line -match 'invoked at t\+(\d+) ms') { $v += [int]$Matches[1] } }
                $wall = [int](($t2 - $t1).TotalMilliseconds)
                Write-Host ("fallback delta: log {0} ms vs wall {1} ms (t+ values {2})" -f ($v[1] - $v[0]), $wall, ($v -join ', '))
                Check "fallback increments match the wall-clock gap (+/-150 ms)" ([math]::Abs(($v[1] - $v[0]) - $wall) -le 150) ("log {0} vs wall {1}" -f ($v[1] - $v[0]), $wall)
                Check "fallback C1 property holds: first fallback t+ is above zero" ($v[0] -gt 0) ("t+ = {0}" -f $v[0])
            }
        }

        'c3' {
            Write-Host "===== WO-06 C3: both chords squatted -> disarmed but alive ====="
            Check "config carries the default chord Ctrl+Alt+Shift+S before C3" ($configHotkey -eq 'Ctrl+Alt+Shift+S') "config hotkey is '$configHotkey'"

            $squat = Start-Squatter @('Ctrl+Alt+Shift+S', 'Ctrl+Alt+Shift+F9')
            Write-Lines "external squatter output (before SnipVid starts)" @(Get-Content -LiteralPath $squat.Out)
            Check "external process owns Ctrl+Alt+Shift+S" ($squat.Text -match 'SQUAT Ctrl\+Alt\+Shift\+S ok=True') $squat.Text
            Check "external process owns Ctrl+Alt+Shift+F9" ($squat.Text -match 'SQUAT Ctrl\+Alt\+Shift\+F9 ok=True') $squat.Text

            $before = Get-LogCount
            $app = Start-App
            Write-Host ("launched pid={0}; waiting 2 s for notes and disarming" -f $app.Id)
            Start-Sleep -Seconds 2

            $startup = @(Get-NewLogLines $before)
            Write-Lines "startup log lines" $startup
            $notes = @($startup | Where-Object { $_ -match '\snote: ' })
            Check "exactly one startup note" ($notes.Count -eq 1) ("got {0}: {1}" -f $notes.Count, ($notes -join ' | '))
            $note = if ($notes.Count -gt 0) { $notes[0] } else { '' }
            Check "the note names Ctrl+Alt+Shift+S" ($note -match 'Ctrl\+Alt\+Shift\+S') $note
            Check "the note names the fallback Ctrl+Alt+Shift+F9" ($note -match 'Ctrl\+Alt\+Shift\+F9') $note
            Check "the note says the hotkey is disarmed" ($note -match 'disarmed') $note
            Check "the log records a disarmed hotkey" (@($startup | Where-Object { $_ -match 'hotkey: disarmed' }).Count -eq 1) ($startup -join ' | ')

            $alive = Get-App
            Check "the process is still alive after disarming" ($null -ne $alive) "no SnipVid process"
            if ($alive) {
                Check "Windows reports the process as responding" ($alive.Responding) ("Responding=" + $alive.Responding)
                $described = @([Wo06Native]::Describe([uint32]$alive.Id))
                Write-Lines "top-level windows owned by pid $($alive.Id)" $described
                Check "the process owns at least one top-level window" ($described.Count -ge 1) "no windows"
                $pumping = @($described | Where-Object { $_.Split('|')[4] -eq 'True' })
                Check "the UI thread answers WM_NULL (message loop is alive, not just the process object)" ($pumping.Count -ge 1) ($described -join ' | ')
            }

            $mark = Get-LogCount
            Press-Chord 'Ctrl+Alt+Shift+S'
            Start-Sleep -Milliseconds 500
            Press-Chord 'Ctrl+Alt+Shift+F9'
            Start-Sleep -Milliseconds 1200
            $afterPresses = @(Get-NewLogLines $mark)
            Write-Lines "press both squatted chords" $afterPresses
            Check "neither squatted chord produces a log line" (@($afterPresses | Where-Object { $_ -match 'invoked at t\+' }).Count -eq 0) ($afterPresses -join ' | ')

            if ($alive) {
                $quit = Stop-Gracefully $alive
                Write-Host ("graceful shutdown: posted={0} thread={1} exited={2} exit_code={3}" -f $quit.Posted, $quit.Thread, $quit.Exited, $quit.ExitCode)
                Check "a disarmed app still quits cleanly (exit code 0)" (($quit.Exited) -and ($quit.ExitCode -eq 0)) ("exited={0} code={1}" -f $quit.Exited, $quit.ExitCode)
            }
        }

        'release' {
            Write-Host "===== WO-06 release on exit: external registration, both directions ====="
            Check "config carries the default chord Ctrl+Alt+Shift+S before the release check" ($configHotkey -eq 'Ctrl+Alt+Shift+S') "config hotkey is '$configHotkey'"

            $before = Get-LogCount
            $app = Start-App
            Write-Host ("launched pid={0}; waiting 2 s for arming" -f $app.Id)
            Start-Sleep -Seconds 2
            $startup = @(Get-NewLogLines $before)
            Write-Lines "startup log lines" $startup
            Check "the app armed Ctrl+Alt+Shift+S" (@($startup | Where-Object { $_ -match 'hotkey: armed Ctrl\+Alt\+Shift\+S' }).Count -eq 1) ($startup -join ' | ')

            $try1 = [Wo06Native]::RegisterHotKey([IntPtr]::Zero, 42, 7, 0x53)
            $err1 = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
            Write-Host ("BEFORE-EXIT external RegisterHotKey(Ctrl+Alt+Shift+S): ok={0} err={1}" -f $try1, $err1)
            Check "while the app runs, an external RegisterHotKey of the armed chord fails" ($try1 -eq $false) ("ok={0} err={1}" -f $try1, $err1)
            Check "that failure is ERROR_HOTKEY_ALREADY_REGISTERED (1409)" ($err1 -eq 1409) ("err={0}" -f $err1)

            $mark = Get-LogCount
            $quit = Stop-Gracefully $app
            Write-Host ("graceful shutdown: posted={0} thread={1} exited={2} exit_code={3}" -f $quit.Posted, $quit.Thread, $quit.Exited, $quit.ExitCode)
            Check "the app quit with exit code 0" (($quit.Exited) -and ($quit.ExitCode -eq 0)) ("exited={0} code={1}" -f $quit.Exited, $quit.ExitCode)
            Start-Sleep -Milliseconds 500
            $shutdownLines = @(Get-NewLogLines $mark)
            Write-Lines "log lines after the graceful shutdown request" $shutdownLines
            Check "the shutdown path unregistered Ctrl+Alt+Shift+S before the process went away" (@($shutdownLines | Where-Object { $_ -match 'hotkey: unregistered Ctrl\+Alt\+Shift\+S' }).Count -eq 1) ($shutdownLines -join ' | ')

            $try2 = [Wo06Native]::RegisterHotKey([IntPtr]::Zero, 43, 7, 0x53)
            $err2 = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
            Write-Host ("AFTER-EXIT external RegisterHotKey(Ctrl+Alt+Shift+S): ok={0} err={1}" -f $try2, $err2)
            Check "after the app exits, the same external registration succeeds" ($try2 -eq $true) ("ok={0} err={1}" -f $try2, $err2)
            Check "the successful registration reports error 0" ($err2 -eq 0) ("err={0}" -f $err2)
            if ($try2) { [void][Wo06Native]::UnregisterHotKey([IntPtr]::Zero, 43) }
        }
    }
}
finally {
    Stop-AllApps
    Stop-AllSquatters
}

Write-Host ("SUMMARY scenario={0} checks={1} failed={2}" -f $Scenario, $script:Checks, $script:Failures)
if ($script:Failures -gt 0) { exit 1 }
exit 0
