@echo off
setlocal EnableExtensions DisableDelayedExpansion
for /f "tokens=2 delims=:" %%C in ('chcp') do set "ORIGINAL_CODEPAGE=%%C"
chcp 65001 >nul
rem --ci must be the first argument. All supported options fit in the nine
rem remaining positional arguments; SHIFT preserves quoted Unicode/space paths.
set "NONINTERACTIVE=0"
if /I "%~1"=="--ci" goto ci
if /I "%~1"=="-NonInteractive" goto ci
goto prepare
:ci
set "NONINTERACTIVE=1"
shift
:prepare
set "LOG_ROOT=%LOCALAPPDATA%\DApps-Preview-Builds\logs"
if not defined LOCALAPPDATA set "LOG_ROOT=%~dp0out\build-logs"
if not exist "%LOG_ROOT%\" mkdir "%LOG_ROOT%" >nul 2>&1
if not exist "%LOG_ROOT%\" goto log_error
:new_log
set "BOOTSTRAP_LOG=%LOG_ROOT%\DIndex-bootstrap-%RANDOM%-%RANDOM%-%RANDOM%.log"
if exist "%BOOTSTRAP_LOG%" goto new_log
>"%BOOTSTRAP_LOG%" echo DIndex local preview build
set "PWSH=pwsh.exe"
where pwsh.exe >nul 2>&1
if not errorlevel 1 goto run
set "PWSH=%ProgramFiles%\PowerShell\7\pwsh.exe"
if exist "%PWSH%" goto run
>>"%BOOTSTRAP_LOG%" echo PowerShell 7.2 or later is required. No tools were installed.
>>"%BOOTSTRAP_LOG%" echo Official instructions: https://learn.microsoft.com/powershell/scripting/install/install-powershell-on-windows
set "BUILD_EXIT=2"
goto show_result
:run
echo Building DIndex Preview. This may take a few minutes.
echo Keep this window open. The build does not install or start the app.
echo Progress log: "%BOOTSTRAP_LOG%"
if "%NONINTERACTIVE%"=="1" goto run_ci
"%PWSH%" -NoLogo -NoProfile -File "%~dp0packaging\Build-Local.ps1" %1 %2 %3 %4 %5 %6 %7 %8 %9 >>"%BOOTSTRAP_LOG%" 2>&1
set "BUILD_EXIT=%ERRORLEVEL%"
goto show_result
:run_ci
"%PWSH%" -NoLogo -NoProfile -NonInteractive -File "%~dp0packaging\Build-Local.ps1" -NonInteractive %1 %2 %3 %4 %5 %6 %7 %8 %9 >>"%BOOTSTRAP_LOG%" 2>&1
set "BUILD_EXIT=%ERRORLEVEL%"
:show_result
>>"%BOOTSTRAP_LOG%" echo Build exit code: %BUILD_EXIT%
type "%BOOTSTRAP_LOG%"
echo.
echo Saved log: "%BOOTSTRAP_LOG%"
if not "%BUILD_EXIT%"=="0" echo See docs\LOCAL_CHECK.md for prerequisites and troubleshooting.
if "%NONINTERACTIVE%"=="0" pause
if defined ORIGINAL_CODEPAGE chcp %ORIGINAL_CODEPAGE% >nul
exit /b %BUILD_EXIT%
:log_error
echo Cannot create the log folder: "%LOG_ROOT%"
echo Choose a writable local folder. Nothing was built or installed.
if "%NONINTERACTIVE%"=="0" pause
if defined ORIGINAL_CODEPAGE chcp %ORIGINAL_CODEPAGE% >nul
exit /b 2
