@echo off
setlocal EnableDelayedExpansion

title CapTG Update Service — Installation

:: ── Elevation check ───────────────────────────────────────────────────────────
:: If not running as Administrator, re-launch this script elevated via UAC.
net session >nul 2>&1
if errorlevel 1 (
    echo Requesting administrator privileges ...
    powershell -NoProfile -Command ^
        "Start-Process -FilePath 'cmd.exe' -ArgumentList '/c \"%~f0\"' -Verb RunAs -Wait"
    exit /b
)

:: ── Paths ─────────────────────────────────────────────────────────────────────
:: %~dp0  is the directory where this script is running.
:: The 7-Zip SFX has already extracted all deploy files here alongside install.cmd.
set "SRC=%~dp0"
set "INSTALL_DIR=%ProgramFiles%\CapTG\UpdateService"

echo.
echo ============================================================================
echo   CapTG Update Service — Installation
echo ============================================================================
echo   Source  : %SRC%
echo   Target  : %INSTALL_DIR%
echo ============================================================================
echo.

:: ── Stop and remove previous service if it exists ────────────────────────────
sc query CapTGUpdateService >nul 2>&1
if not errorlevel 1 (
    echo Stopping existing service ...
    sc stop CapTGUpdateService >nul 2>&1
    timeout /t 4 /nobreak >nul
    sc delete CapTGUpdateService >nul 2>&1
    timeout /t 2 /nobreak >nul
    echo Previous service removed.
    echo.
)

:: ── Copy files to installation directory ──────────────────────────────────────
echo Creating installation directory ...
if not exist "%INSTALL_DIR%\" mkdir "%INSTALL_DIR%"

echo Copying files ...
:: /E  — copy subdirectories including empty
:: /IS — overwrite same-size files
:: /IT — overwrite files with same timestamp
:: /IM — overwrite modified files
:: /XF — exclude install.cmd (installer artefact, not needed at runtime)
:: /NFL /NDL /NJH /NJS — suppress verbose robocopy output
robocopy "%SRC%" "%INSTALL_DIR%" /E /IS /IT /IM /XF install.cmd /NFL /NDL /NJH /NJS

:: robocopy exit codes: 0-7 are success / informational; 8+ are errors.
if %errorlevel% gtr 7 (
    echo.
    echo ERROR: File copy failed ^(robocopy exit %errorlevel%^).
    pause
    exit /b 1
)
echo Files copied successfully.
echo.

:: ── Register and start the Windows service ───────────────────────────────────
echo Registering service ...
"%INSTALL_DIR%\UpdateService.exe" --install
if errorlevel 1 (
    echo.
    echo ERROR: Service registration failed ^(exit %errorlevel%^).
    pause
    exit /b 1
)

echo.
echo ============================================================================
echo   Installation complete!
echo.
echo   Service : CapTG Automatic Update Service
echo   Status  : Started  ^(delayed auto-start^)
echo   Logs    : C:\ProgramData\CapTG\Logs\
echo ============================================================================
echo.
pause

endlocal
exit /b 0
