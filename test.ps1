# test.ps1 - compile src\Logic\*.cs + tests\*.cs to bin\LogicTests.exe and run it.
#
# The compile boundary is the point (DESIGN.md section 11): src\App is never part
# of this argument list and no WPF/WinForms reference is passed, so a logic test
# physically cannot reach WPF, P/Invoke, the clock, the filesystem or a process.
#
# ASCII-only on purpose: PowerShell 5.1 reads a BOM-less script as ANSI, so a
# UTF-8 em dash would decode to byte 0x94 and be parsed as a closing quote.
#
# Inputs:  src\Logic\*.cs (optional - may be absent), tests\*.cs.
# Outputs: bin\LogicTests.exe, then the harness's PASS/FAIL lines on stdout.
# Exit:    0 when every check passed, 1 when any check failed, csc's exit code on
#          a compile error, 1 when there is nothing to compile.
# Side effects: creates bin\, overwrites bin\LogicTests.exe. Never launches
#          SnipVid.exe and never spawns ffmpeg.

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Definition
$csc  = 'F:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\Roslyn\csc.exe'

function Fail([string]$message) {
    Write-Host "TEST FAILED: $message" -ForegroundColor Red
    exit 1
}

if (-not (Test-Path -LiteralPath $csc -PathType Leaf)) {
    Fail "Roslyn csc not found at '$csc'. Install Visual Studio 2022; a .NET SDK is not a substitute."
}

# src\Logic does not exist yet (WO-02 is the first file there). An absent or
# empty directory is a valid state, not an error: the harness still runs and
# reports zero checks.
$sources = @()
foreach ($relative in @('src\Logic', 'tests')) {
    $dir = Join-Path $root $relative
    if (Test-Path -LiteralPath $dir -PathType Container) {
        $sources += @(
            Get-ChildItem -LiteralPath $dir -Filter *.cs -Recurse -File |
                Sort-Object FullName |
                ForEach-Object { $_.FullName }
        )
    }
}
if ($sources.Count -eq 0) {
    Fail "no .cs files under src\Logic or tests - the harness itself is missing."
}

$bin = Join-Path $root 'bin'
if (-not (Test-Path -LiteralPath $bin -PathType Container)) {
    New-Item -ItemType Directory -Path $bin | Out-Null
}

$testExe = Join-Path $bin 'LogicTests.exe'
if (Test-Path -LiteralPath $testExe) {
    Remove-Item -LiteralPath $testExe -Force
}

$cscArgs = @(
    '/nologo',
    '/target:exe',
    '/platform:anycpu',
    '/optimize+',
    "/out:$testExe"
) + $sources

& $csc @cscArgs
if ($LASTEXITCODE -ne 0) {
    Write-Host ("TEST BUILD FAILED: csc exit {0} (errors above name the offending file)" -f $LASTEXITCODE) -ForegroundColor Red
    exit $LASTEXITCODE
}

& $testExe
exit $LASTEXITCODE
