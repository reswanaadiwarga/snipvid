# build.ps1 - compile every src\**\*.cs into bin\SnipVid.exe.
#
# There is no .NET SDK on this machine (AGENTS.md), so this uses the Roslyn csc
# that ships with Visual Studio 2022 and the installed .NET Framework 4.8
# reference assemblies. Windows PowerShell 5.1 only. This file is deliberately
# ASCII-only: PowerShell 5.1 reads a BOM-less script as ANSI, and a UTF-8 em
# dash would decode to byte 0x94, which it treats as a closing quote.
#
# Inputs:  src\App\*.cs, src\Logic\*.cs, app.manifest, app.ico.
# Outputs: bin\SnipVid.exe, plus csc's own diagnostics on stdout.
# Exit:    0 on success, csc's non-zero exit code on any compile error, 1 when
#          the toolchain or the source set is missing. A stale bin\SnipVid.exe is
#          deleted first, so a failed build can never look like a successful one.
# Side effects: creates bin\, deletes the previous exe, writes the exe.

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Definition
$csc  = 'F:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\Roslyn\csc.exe'
$ref  = 'C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8'

# WPF for the app, WinForms only for the tray NotifyIcon (WPF has no tray API),
# System.Drawing for SystemIcons.Application. Nothing else may be referenced.
$refs = @(
    'PresentationFramework.dll',
    'PresentationCore.dll',
    'WindowsBase.dll',
    'System.Xaml.dll',
    'System.Drawing.dll',
    'System.Windows.Forms.dll'
)

function Fail([string]$message) {
    Write-Host "BUILD FAILED: $message" -ForegroundColor Red
    exit 1
}

if (-not (Test-Path -LiteralPath $csc -PathType Leaf)) {
    Fail "Roslyn csc not found at '$csc'. Install Visual Studio 2022 (or fix the path in build.ps1); a .NET SDK is not a substitute."
}
if (-not (Test-Path -LiteralPath $ref -PathType Container)) {
    Fail ".NET Framework 4.8 reference assemblies not found at '$ref'. Install the 4.8 targeting pack."
}
foreach ($name in $refs) {
    $path = Join-Path $ref $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        Fail "reference assembly '$name' missing from '$ref'."
    }
}

$srcDir = Join-Path $root 'src'
if (-not (Test-Path -LiteralPath $srcDir -PathType Container)) {
    Fail "no 'src' directory under '$root'."
}

$sources = @(
    Get-ChildItem -LiteralPath $srcDir -Filter *.cs -Recurse -File |
        Sort-Object FullName |
        ForEach-Object { $_.FullName }
)
if ($sources.Count -eq 0) {
    Fail "no .cs files found under '$srcDir'."
}

$bin = Join-Path $root 'bin'
if (-not (Test-Path -LiteralPath $bin -PathType Container)) {
    New-Item -ItemType Directory -Path $bin | Out-Null
}

$exe      = Join-Path $bin 'SnipVid.exe'
$manifest = Join-Path $root 'app.manifest'
if (-not (Test-Path -LiteralPath $manifest -PathType Leaf)) {
    Fail "app.manifest missing at '$manifest' (DPI awareness and supportedOS depend on it)."
}
$icon = Join-Path $root 'app.ico'
if (-not (Test-Path -LiteralPath $icon -PathType Leaf)) {
    Fail "app.ico missing at '$icon' (the release exe carries this icon)."
}
if (Test-Path -LiteralPath $exe) {
    Remove-Item -LiteralPath $exe -Force
}

$cscArgs = @(
    '/nologo',
    '/target:winexe',
    '/platform:anycpu',
    '/optimize+',
    "/win32manifest:$manifest",
    "/win32icon:$icon",
    "/out:$exe"
)
foreach ($name in $refs) {
    $cscArgs += '/r:' + (Join-Path $ref $name)
}
$cscArgs += $sources

$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
& $csc @cscArgs
$code = $LASTEXITCODE
$stopwatch.Stop()

if ($code -ne 0) {
    Write-Host ("BUILD FAILED: csc exit {0} after {1} ms (errors above name the offending file)" -f $code, $stopwatch.ElapsedMilliseconds) -ForegroundColor Red
    exit $code
}
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
    Fail "csc exited 0 but '$exe' was not produced."
}

$size = (Get-Item -LiteralPath $exe).Length
Write-Host ("BUILD OK  bin\SnipVid.exe  {0} bytes  {1} ms" -f $size, $stopwatch.ElapsedMilliseconds) -ForegroundColor Green
exit 0
