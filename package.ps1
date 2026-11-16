# package.ps1 - build the portable release zip for SnipVid 1.0.0.
#
# What it produces: dist\SnipVid-1.0.0-win64.zip, containing SnipVid.exe,
# ffmpeg.exe, install.cmd, uninstall.cmd, README.txt, LICENSE and
# THIRD-PARTY-NOTICES.txt at the archive root. A user extracts it anywhere and
# runs SnipVid.exe (or install.cmd for shortcuts and auto-start).
#
# ffmpeg is mandatory: the app does the capture, but ffmpeg does the encoding,
# and the zip must work offline on a machine that has never installed it. If no
# ffmpeg can be found this script fails instead of producing a crippled zip.
# Lookup order: -FfmpegPath, then `where ffmpeg.exe`, then the WinGet link.
#
# ASCII-only on purpose: PowerShell 5.1 reads a BOM-less script as ANSI, so a
# UTF-8 dash would decode to byte 0x94 and be parsed as a closing quote.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File package.ps1 [-FfmpegPath <path>] [-SkipBuild]
#
# Inputs:  build.ps1 (unless -SkipBuild), bin\SnipVid.exe, README.md, LICENSE,
#          THIRD-PARTY-NOTICES.txt, install.cmd, uninstall.cmd, and ffmpeg.exe.
# Outputs: dist\SnipVid-1.0.0-win64\ (staging) and
#          dist\SnipVid-1.0.0-win64.zip.
# Exit:    0 when the zip exists; 1 with the failing step named on any failure
#          (build, missing source file, missing ffmpeg, copy, compression).
# Side effects: runs build.ps1, replaces the staging folder and the zip. Never
#          launches SnipVid and never records.

param(
    [string]$FfmpegPath,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'

$root    = Split-Path -Parent $MyInvocation.MyCommand.Definition
$version = '1.0.0'
$name    = "SnipVid-$version-win64"
$dist    = Join-Path $root 'dist'
$stage   = Join-Path $dist $name
$zip     = Join-Path $dist ($name + '.zip')
$built   = Join-Path $root 'bin\SnipVid.exe'

function Fail([string]$message) {
    Write-Host "PACKAGE FAILED: $message" -ForegroundColor Red
    exit 1
}

# 1. Build unless asked to reuse the current bin\SnipVid.exe. A stale exe in a
#    release zip is the failure this step exists to prevent.
if (-not $SkipBuild) {
    $build = Join-Path $root 'build.ps1'
    if (-not (Test-Path -LiteralPath $build -PathType Leaf)) {
        Fail "build.ps1 not found at '$build'."
    }
    Write-Host 'package: building (build.ps1)'
    & powershell -NoProfile -ExecutionPolicy Bypass -File $build
    if ($LASTEXITCODE -ne 0) {
        Fail "build.ps1 exited $LASTEXITCODE; nothing was packaged."
    }
}
if (-not (Test-Path -LiteralPath $built -PathType Leaf)) {
    Fail "'$built' is missing. Run build.ps1 first (or omit -SkipBuild)."
}

# 2. Resolve ffmpeg. The zip is the only source of it for a fresh machine, so a
#    miss is fatal rather than a warning.
$ffmpeg = $null
if (-not [string]::IsNullOrEmpty($FfmpegPath)) {
    if (-not (Test-Path -LiteralPath $FfmpegPath -PathType Leaf)) {
        Fail "-FfmpegPath '$FfmpegPath' does not exist."
    }
    $ffmpeg = (Resolve-Path -LiteralPath $FfmpegPath).Path
} else {
    $whereOutput = @(& where.exe ffmpeg.exe 2>$null)
    $whereCode = $LASTEXITCODE
    if ($whereCode -eq 0) {
        foreach ($line in $whereOutput) {
            if (-not [string]::IsNullOrWhiteSpace($line)) {
                $ffmpeg = $line.Trim()
                break
            }
        }
    }
    if ([string]::IsNullOrEmpty($ffmpeg)) {
        $winget = Join-Path (Join-Path (Join-Path $env:LOCALAPPDATA 'Microsoft') 'WinGet') 'Links\ffmpeg.exe'
        if (Test-Path -LiteralPath $winget -PathType Leaf) {
            $ffmpeg = $winget
        }
    }
}
if ([string]::IsNullOrEmpty($ffmpeg) -or -not (Test-Path -LiteralPath $ffmpeg -PathType Leaf)) {
    Fail "ffmpeg.exe not found. Pass -FfmpegPath <path>, put it on PATH, or install it with WinGet. A zip without ffmpeg cannot record."
}
Write-Host "package: ffmpeg $ffmpeg"

# 3. Stage from scratch so a file removed from the release cannot linger.
if (Test-Path -LiteralPath $stage) {
    Remove-Item -LiteralPath $stage -Recurse -Force
}
New-Item -ItemType Directory -Path $stage -Force | Out-Null
if (Test-Path -LiteralPath $zip) {
    Remove-Item -LiteralPath $zip -Force
}

$files = @(
    @{ Source = $built; Name = 'SnipVid.exe' },
    @{ Source = $ffmpeg; Name = 'ffmpeg.exe' },
    @{ Source = (Join-Path $root 'install.cmd'); Name = 'install.cmd' },
    @{ Source = (Join-Path $root 'uninstall.cmd'); Name = 'uninstall.cmd' },
    @{ Source = (Join-Path $root 'README.md'); Name = 'README.txt' },
    @{ Source = (Join-Path $root 'LICENSE'); Name = 'LICENSE' },
    @{ Source = (Join-Path $root 'THIRD-PARTY-NOTICES.txt'); Name = 'THIRD-PARTY-NOTICES.txt' }
)
foreach ($file in $files) {
    if (-not (Test-Path -LiteralPath $file.Source -PathType Leaf)) {
        Fail "source file '$($file.Source)' is missing."
    }
    $destination = Join-Path $stage $file.Name
    Copy-Item -LiteralPath $file.Source -Destination $destination -Force
    if (-not (Test-Path -LiteralPath $destination -PathType Leaf)) {
        Fail "'$destination' does not exist after the copy."
    }
}

# 4. One archive, entries at the root. Windows Explorer's "Extract All" offers
#    the zip's own base name as the destination folder.
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal
if (-not (Test-Path -LiteralPath $zip -PathType Leaf)) {
    Fail "'$zip' was not produced."
}

$zipSize = (Get-Item -LiteralPath $zip).Length
Write-Host ''
Write-Host 'PACKAGE OK' -ForegroundColor Green
Write-Host ("  zip   {0}" -f $zip)
Write-Host ("  size  {0:N2} MB" -f ($zipSize / 1MB))
Write-Host '  entries:'
Get-ChildItem -LiteralPath $stage -File | Sort-Object Name | ForEach-Object {
    Write-Host ("    {0,-24} {1,12:N0} bytes" -f $_.Name, $_.Length)
}
exit 0
