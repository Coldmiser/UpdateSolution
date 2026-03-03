@echo off
setlocal EnableDelayedExpansion

:: ============================================================================
:: create_installer.bat  —  CapTG Update Service release packager
::
:: Usage (called automatically by Build-And-Publish.ps1):
::   create_installer.bat <version>
::   e.g. create_installer.bat 0.26.165.427
::
:: Expected layout (all paths relative to this script):
::   ..\Deploy\        — merged deploy output from Build-And-Publish.ps1
::   sfx_config.txt    — 7-Zip SFX installer configuration
::   install.cmd       — installation script bundled into the SFX
::
:: Outputs (written to the same directory as this script):
::   CapTG-Update-{version}.zip               — update archive for SelfUpdater
::   CapTG-Update-{version}.zip.sha256        — SHA-256 digest of the ZIP
::   CapTG-UpdateService-{version}-Setup.exe  — single-file SFX installer for IT
::
:: Requirements:
::   7-Zip (7z.exe + 7zSD.sfx)  https://www.7-zip.org/
:: ============================================================================

if "%~1"=="" (
    echo ERROR: Version argument required.
    echo Usage: create_installer.bat ^<version^>
    exit /b 1
)

set "VER=%~1"
set "SCRIPT_DIR=%~dp0"
set "DEPLOY_DIR=%SCRIPT_DIR%..\Deploy"

:: Verify the deploy folder exists before doing any work
if not exist "%DEPLOY_DIR%\" (
    echo ERROR: Deploy directory not found:
    echo        %DEPLOY_DIR%
    echo        Run Build-And-Publish.ps1 first to produce the deploy folder.
    exit /b 1
)

:: ── Locate 7z.exe ────────────────────────────────────────────────────────────
set "SEVENZIP="
if exist "%ProgramFiles%\7-Zip\7z.exe"        set "SEVENZIP=%ProgramFiles%\7-Zip\7z.exe"
if exist "%ProgramFiles(x86)%\7-Zip\7z.exe"   set "SEVENZIP=%ProgramFiles(x86)%\7-Zip\7z.exe"
if "%SEVENZIP%"=="" (
    where 7z.exe >nul 2>&1
    if not errorlevel 1 set "SEVENZIP=7z.exe"
)
if "%SEVENZIP%"=="" (
    echo ERROR: 7-Zip ^(7z.exe^) not found.
    echo        Install from https://www.7-zip.org/ and re-run.
    exit /b 1
)

:: ── Locate 7zSD.sfx (GUI SFX module, ships with 7-Zip) ───────────────────────
set "SFX_MODULE="
if exist "%ProgramFiles%\7-Zip\7zSD.sfx"       set "SFX_MODULE=%ProgramFiles%\7-Zip\7zSD.sfx"
if exist "%ProgramFiles(x86)%\7-Zip\7zSD.sfx"  set "SFX_MODULE=%ProgramFiles(x86)%\7-Zip\7zSD.sfx"
if "%SFX_MODULE%"=="" (
    echo ERROR: 7zSD.sfx not found in the 7-Zip installation directory.
    echo        Re-install 7-Zip to restore the SFX module.
    exit /b 1
)

echo.
echo ============================================================================
echo   CapTG Update Service — Release Packager    v%VER%
echo ============================================================================
echo   Deploy  : %DEPLOY_DIR%
echo   7-Zip   : %SEVENZIP%
echo   SFX mod : %SFX_MODULE%
echo ============================================================================

:: ── Output file paths ─────────────────────────────────────────────────────────
set "ZIP_FILE=%SCRIPT_DIR%CapTG-Update-%VER%.zip"
set "HASH_FILE=%SCRIPT_DIR%CapTG-Update-%VER%.zip.sha256"
set "SFX_EXE=%SCRIPT_DIR%CapTG-UpdateService-%VER%-Setup.exe"
set "INST_ARCHIVE=%TEMP%\captg_installer_%VER%.7z"

:: ── [1/4] Create update ZIP (for SelfUpdater auto-download) ──────────────────
echo.
echo [1/4] Creating update ZIP ...
if exist "%ZIP_FILE%" del /F /Q "%ZIP_FILE%"

"%SEVENZIP%" a -tzip -mx9 "%ZIP_FILE%" "%DEPLOY_DIR%\*" -r -x!*.pdb >nul
if errorlevel 1 (
    echo ERROR: ZIP creation failed.
    exit /b 1
)
echo       Created : %ZIP_FILE%

:: ── [2/4] Compute SHA-256 of the update ZIP ───────────────────────────────────
echo.
echo [2/4] Computing SHA-256 ...
if exist "%HASH_FILE%" del /F /Q "%HASH_FILE%"

powershell -NoProfile -Command ^
    "(Get-FileHash '%ZIP_FILE%' -Algorithm SHA256).Hash.ToLower()" ^
    > "%HASH_FILE%"
if errorlevel 1 (
    echo ERROR: SHA-256 computation failed.
    exit /b 1
)
set /p HASH_VALUE=<"%HASH_FILE%"
echo       Hash    : %HASH_VALUE%
echo       Created : %HASH_FILE%

:: ── [3/4] Build installer archive (7z) ───────────────────────────────────────
:: The installer archive contains all deploy files plus install.cmd.
:: It is a 7z (not ZIP) archive since the SFX module requires 7z format.
echo.
echo [3/4] Building installer archive ...
if exist "%INST_ARCHIVE%" del /F /Q "%INST_ARCHIVE%"

"%SEVENZIP%" a "%INST_ARCHIVE%" "%DEPLOY_DIR%\*" -r -x!*.pdb >nul
if errorlevel 1 (
    echo ERROR: Installer archive creation failed.
    exit /b 1
)
"%SEVENZIP%" a "%INST_ARCHIVE%" "%SCRIPT_DIR%install.cmd" >nul
if errorlevel 1 (
    echo ERROR: Could not add install.cmd to installer archive.
    exit /b 1
)
echo       Archive : %INST_ARCHIVE%

:: ── [4/4] Combine SFX module + config + archive → single EXE ─────────────────
echo.
echo [4/4] Creating self-extracting installer EXE ...
if exist "%SFX_EXE%" del /F /Q "%SFX_EXE%"

copy /b "%SFX_MODULE%" + "%SCRIPT_DIR%sfx_config.txt" + "%INST_ARCHIVE%" "%SFX_EXE%" >nul
if errorlevel 1 (
    echo ERROR: SFX EXE creation failed.
    del /F /Q "%INST_ARCHIVE%"
    exit /b 1
)

:: Clean up the temporary installer archive
del /F /Q "%INST_ARCHIVE%"
echo       Created : %SFX_EXE%

echo.
echo ============================================================================
echo   Done.  Three artifacts created in:
echo   %SCRIPT_DIR%
echo.
echo   CapTG-Update-%VER%.zip
echo      ^-^> Commit to repo root so SelfUpdater can download it.
echo   CapTG-Update-%VER%.zip.sha256
echo      ^-^> Commit to repo root alongside the ZIP.
echo   CapTG-UpdateService-%VER%-Setup.exe
echo      ^-^> Distribute to IT staff for fresh installations.
echo ============================================================================
echo.

endlocal
exit /b 0
