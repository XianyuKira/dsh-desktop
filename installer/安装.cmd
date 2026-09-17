@echo off
chcp 65001 >nul
title DeepSeek Harness Desktop - Installer
setlocal

rem Find the installer script next to this file. The name is NOT hardcoded:
rem it was once install.ps1 while the release asset was renamed to
rem zero-deploy-install.ps1, so double-clicking failed with "file not found".
rem Matching a wildcard survives any rename.
rem ASCII only on purpose - cmd parses this file before chcp takes effect.

set "SCRIPT="
for %%F in ("%~dp0*install*.ps1") do if not defined SCRIPT set "SCRIPT=%%~fF"

if not defined SCRIPT (
    echo.
    echo [ERROR] No installer script found. Expected a *install*.ps1 next to this file.
    echo.
    echo Put these three files in the same folder:
    echo    zero-deploy-install.cmd      ^<- the file you double-clicked
    echo    zero-deploy-install.ps1      ^<- installer script
    echo    zero-deploy-payload-*.zip    ^<- main data, about 227 MB
    echo.
    pause
    exit /b 1
)

echo Using installer: %SCRIPT%
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%"
set "CODE=%ERRORLEVEL%"
if not "%CODE%"=="0" (
    echo.
    echo Installer exited with code %CODE%
    pause
)
endlocal
