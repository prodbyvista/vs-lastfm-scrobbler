@echo off
setlocal EnableDelayedExpansion
rem ============================================================
rem  V's Last.fm Scrobbler - build script
rem  Lives in <app>\Source\, builds <app>\V's Last.fm Scrobbler.exe
rem ============================================================
cd /d "%~dp0"
set "APP=%~dp0.."
set "EXE=%APP%\V's Last.fm Scrobbler.exe"

echo.
echo  V's Last.fm Scrobbler - build
echo  -----------------------------

rem --- close a running copy so the .exe can be replaced
taskkill /f /im "V's Last.fm Scrobbler.exe" >nul 2>&1
taskkill /f /im VsLastfmScrobbler.exe >nul 2>&1
taskkill /f /im AppleMusicScrobbler.exe >nul 2>&1
taskkill /f /im iTunesScrobbler.exe >nul 2>&1
timeout /t 2 >nul

rem --- tidy the app folder: source files, old builds and logs from earlier versions
for %%F in (VsLastfmScrobbler.exe VsLastfmScrobbler.cs SmtcReader.cs Features.cs Social.cs WrapPage.cs Ui.cs Toast.cs History.cs Friends.cs iTunesScrobbler.cs AppleMusicScrobbler.cs iTunesScrobbler.exe AppleMusicScrobbler.exe icon.ico build_log.txt build.bat) do (
  if exist "%APP%\%%F" del "%APP%\%%F" >nul 2>&1
)
if exist "%EXE%" (
  del "%EXE%" >nul 2>&1
  if exist "%EXE%" (
    echo  The app is still running. Exit it from the tray icon, then run this again.
    pause & exit /b 1
  )
)

set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
if not exist "%FW%\csc.exe" set FW=%WINDIR%\Microsoft.NET\Framework\v4.0.30319
if not exist "%FW%\csc.exe" (
  echo  Could not find the .NET Framework 4 C# compiler. Enable ".NET Framework 4.8" in Windows Features.
  pause & exit /b 1
)
set CSC=%FW%\csc.exe
set ICONOPT=/win32icon:icon.ico
if not exist icon.ico (
  echo  WARNING: Source\icon.ico is missing - building without the Last.fm icon.
  set ICONOPT=
)
set SOURCES=VsLastfmScrobbler.cs SmtcReader.cs Features.cs Social.cs WrapPage.cs Ui.cs Toast.cs History.cs Friends.cs Stats.cs Updater.cs AudioMeter.cs
set COMMON=/nologo /target:winexe /platform:anycpu /optimize+ !ICONOPT! /out:"%EXE%" /r:System.dll /r:System.Core.dll /r:Microsoft.CSharp.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Xml.dll /r:System.Security.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll

rem --- what the Windows media controls reader (Apple Music support) needs
set WINMD=%WINDIR%\System32\WinMetadata
if exist "%WINDIR%\Sysnative\WinMetadata" set WINMD=%WINDIR%\Sysnative\WinMetadata
set "PF86=%ProgramFiles(x86)%"
set FACADES=
for %%V in (v4.8.1 v4.8 v4.7.2 v4.7.1 v4.7 v4.6.2 v4.6.1 v4.6 v4.5.2 v4.5.1 v4.5) do (
  if not defined FACADES if exist "!PF86!\Reference Assemblies\Microsoft\Framework\.NETFramework\%%V\Facades\System.Runtime.dll" set "FACADES=!PF86!\Reference Assemblies\Microsoft\Framework\.NETFramework\%%V\Facades"
)
set SYSRUNTIME=
if exist "%FW%\System.Runtime.dll" set "SYSRUNTIME=%FW%\System.Runtime.dll"
if not defined SYSRUNTIME if defined FACADES set "SYSRUNTIME=%FACADES%\System.Runtime.dll"
set SMTCREFS=/r:"%WINMD%\Windows.Foundation.winmd" /r:"%WINMD%\Windows.Media.winmd" /r:"%WINMD%\Windows.UI.winmd" /r:"%WINMD%\Windows.Data.winmd"
if defined SYSRUNTIME set SMTCREFS=!SMTCREFS! /r:"!SYSRUNTIME!"

echo  Building with iTunes + Apple Music support...
"%CSC%" %COMMON% /define:SMTC !SMTCREFS! %SOURCES% > build_log.txt 2>&1
if not errorlevel 1 (
  echo    OK.
  goto run
)

echo    That didn't compile on this PC ^(details in Source\build_log.txt^).
echo  Building with iTunes support only...
"%CSC%" %COMMON% %SOURCES% >> build_log.txt 2>&1
if errorlevel 1 (
  type build_log.txt
  echo. & echo  Build FAILED - send me Source\build_log.txt. & pause & exit /b 1
)
echo    OK ^(Apple Music support NOT included - send me Source\build_log.txt^).
pause

:run
echo.
echo  Starting V's Last.fm Scrobbler - look for the Last.fm icon in the tray.
start "" "%EXE%"
timeout /t 3 >nul
