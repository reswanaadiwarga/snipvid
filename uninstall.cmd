@echo off
rem uninstall.cmd - remove the SnipVid install made by install.cmd.
rem
rem Stops a running copy, deletes the Desktop and Start menu shortcuts, the
rem HKCU Run value and %LOCALAPPDATA%\Programs\SnipVid. It deliberately leaves
rem %APPDATA%\SnipVid\config.ini and every recording in place, and prints those
rem paths so nothing disappears silently. No admin rights.
rem
rem Exit:   0 when done (missing files are not an error), 1 only if a folder
rem         that should be removable could not be removed.
rem ASCII-only: cmd.exe reads a BOM-less batch file as ANSI. Keep it ASCII.
setlocal EnableExtensions

set "INSTALLDIR=%LOCALAPPDATA%\Programs\SnipVid"
set "CONFIGINI=%APPDATA%\SnipVid\config.ini"
set "RECORDINGS=%USERPROFILE%\Videos\Screen Recordings"

rem Stop the app first: Windows keeps a running exe locked, so the folder
rem removal below would fail and leave a half-uninstalled copy.
taskkill /IM SnipVid.exe /F >nul 2>&1

rem Shortcuts: .NET folder paths, matching install.cmd, so a redirected Desktop
rem or Start menu is handled the same way.
powershell -NoProfile -ExecutionPolicy Bypass -Command "$ErrorActionPreference='SilentlyContinue'; $desktop=[Environment]::GetFolderPath('Desktop'); $start=Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'Microsoft\Windows\Start Menu\Programs'; foreach ($p in @((Join-Path $desktop 'SnipVid.lnk'), (Join-Path $start 'SnipVid.lnk'))) { Remove-Item -LiteralPath $p -Force }; exit 0"

reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v SnipVid /f >nul 2>&1

if exist "%INSTALLDIR%" rmdir /s /q "%INSTALLDIR%" >nul 2>&1
if exist "%INSTALLDIR%" goto failrmdir
if exist "%LOCALAPPDATA%\Programs\SnipVid\SnipVid.exe" goto failrmdir

echo.
echo UNINSTALL OK
echo   removed     %INSTALLDIR%
echo   removed     Desktop\SnipVid.lnk
echo   removed     Start Menu\Programs\SnipVid.lnk
echo   removed     HKCU Run value SnipVid
echo.
echo Left in place on purpose:
echo   config      %CONFIGINI%
echo   recordings  %RECORDINGS%
echo               (or the folder set in config.ini)
exit /b 0

:failrmdir
echo UNINSTALL FAILED: could not remove "%INSTALLDIR%".
echo Close SnipVid and run uninstall.cmd again.
exit /b 1
