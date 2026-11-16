@echo off
rem install.cmd - install SnipVid for this user from the extracted release zip.
rem
rem Copies SnipVid.exe and ffmpeg.exe next to each other under
rem %LOCALAPPDATA%\Programs\SnipVid, then creates a Desktop shortcut, a Start menu
rem shortcut and the per-user Run value used by "Start with Windows". No admin
rem rights and nothing machine-wide; re-running it replaces the copy and re-points
rem the shortcuts.
rem
rem Reads:  SnipVid.exe and ffmpeg.exe in this folder.
rem Writes: %LOCALAPPDATA%\Programs\SnipVid\SnipVid.exe and ffmpeg.exe,
rem         the two .lnk files, HKCU\...\Run\SnipVid.
rem Exit:   0 on success, 1 with the failing step named.
rem ASCII-only: cmd.exe reads a BOM-less batch file as ANSI. Keep it ASCII.
setlocal EnableExtensions

set "SRC=%~dp0"
set "SRCEXE=%SRC%SnipVid.exe"
set "SRCFF=%SRC%ffmpeg.exe"
set "INSTALLDIR=%LOCALAPPDATA%\Programs\SnipVid"
set "DESTEXE=%INSTALLDIR%\SnipVid.exe"
set "DESTFF=%INSTALLDIR%\ffmpeg.exe"

if not exist "%SRCEXE%" goto noexe
if not exist "%SRCFF%" goto noffmpeg

if not exist "%INSTALLDIR%" mkdir "%INSTALLDIR%" >nul 2>&1
if not exist "%INSTALLDIR%" goto failmkdir

rem A running SnipVid holds its own exe open, so the copy would fail with a
rem sharing violation. Stop it first - re-running the installer means replacing
rem the app, and the user expects that to work without closing it by hand.
taskkill /IM SnipVid.exe /F >nul 2>&1

rem The handle can linger for a moment after the kill, so retry briefly rather
rem than failing on the first attempt.
set "TRIES=0"
:copyexe
copy /y "%SRCEXE%" "%DESTEXE%" >nul 2>&1
if not errorlevel 1 goto copyffmpeg
set /a TRIES+=1
if %TRIES% GEQ 10 goto failcopy
ping -n 2 127.0.0.1 >nul
goto copyexe

:copyffmpeg
copy /y "%SRCFF%" "%DESTFF%" >nul 2>&1
if errorlevel 1 goto failcopy

rem Shortcuts through WScript.Shell: the same COM object install.ps1 uses. The
rem paths come from .NET so a redirected Desktop or Start menu is honoured.
set "SNIPVID_EXE=%DESTEXE%"
set "SNIPVID_DIR=%INSTALLDIR%"
powershell -NoProfile -ExecutionPolicy Bypass -Command "$ErrorActionPreference='Stop'; $w=New-Object -ComObject WScript.Shell; $desktop=[Environment]::GetFolderPath('Desktop'); $start=Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'Microsoft\Windows\Start Menu\Programs'; foreach ($p in @((Join-Path $desktop 'SnipVid.lnk'), (Join-Path $start 'SnipVid.lnk'))) { $s=$w.CreateShortcut($p); $s.TargetPath=$env:SNIPVID_EXE; $s.WorkingDirectory=$env:SNIPVID_DIR; $s.Description='SnipVid - screen region recorder'; $s.IconLocation=$env:SNIPVID_EXE+',0'; $s.Save(); Write-Host ('  shortcut    ' + $p) }; exit 0"
if errorlevel 1 goto failshortcut

rem The Run value is quoted because CreateProcess gets its data unparsed and a
rem path with spaces would otherwise be split at the first space.
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v SnipVid /t REG_SZ /d "\"%DESTEXE%\"" /f >nul 2>&1
if errorlevel 1 goto failrun

echo.
echo INSTALL OK
echo   exe         %DESTEXE%
echo   ffmpeg      %DESTFF%
echo   autostart   enabled: SnipVid = "%DESTEXE%"
echo.
echo Start SnipVid from the shortcut, then press Ctrl+Alt+Shift+S to record.
echo Config and recordings are not touched by this script.
exit /b 0

:noexe
echo INSTALL FAILED: SnipVid.exe was not found next to install.cmd.
echo Extract the whole release zip first, then run install.cmd from that folder.
exit /b 1

:noffmpeg
echo INSTALL FAILED: ffmpeg.exe was not found next to install.cmd.
echo The release zip ships both files; extract it again.
exit /b 1

:failmkdir
echo INSTALL FAILED: could not create "%INSTALLDIR%".
exit /b 1

:failcopy
echo INSTALL FAILED: could not copy SnipVid.exe and ffmpeg.exe to "%INSTALLDIR%".
echo Close any running SnipVid and try again.
exit /b 1

:failshortcut
echo INSTALL FAILED: could not create the Desktop and Start menu shortcuts.
exit /b 1

:failrun
echo INSTALL FAILED: could not write the per-user auto-start value.
exit /b 1
