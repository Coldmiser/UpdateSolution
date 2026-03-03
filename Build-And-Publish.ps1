# Build-And-Publish.ps1
# Builds both projects as single-file, self-contained x64 executables,
# merges them into a deploy folder, and runs the installer packager to
# produce the SFX installer and the self-update ZIP + SHA-256 artefacts.
#
# Run from the solution root:
#   .\Build-And-Publish.ps1
#
# Optional parameters:
#   -Configuration Release|Debug  (default: Release)
#   -OutDir        path           (default: .\publish)

param(
    [string]$Configuration = "Release",
    [string]$OutDir        = "$PSScriptRoot\publish"
)
Import-Module PSWriteColor
Clear-Host

$Year = (Get-Date).Year - 2000
$DoY  = (((Get-Date).DayOfYear) * 2.7).ToString("000")
$Tm   = ((Get-Date).Hour * 41) + (Get-Date).Minute
$Ver  = "0.$Year.$DoY.$Tm"
Write-Color "Publishing version:  ", "$Ver" -Color White, Yellow

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Write-Host "`n=== CapTG Update Solution — Build & Publish ===" -ForegroundColor Cyan
Write-Host "Configuration : $Configuration"
Write-Host "Output        : $OutDir`n"

# ── Publish UpdateService ─────────────────────────────────────────────────────
Write-Host "Publishing UpdateService..." -ForegroundColor Yellow
dotnet publish "$PSScriptRoot\UpdateService\UpdateService.csproj" `
    --configuration $Configuration `
    --runtime win-x64 `
    --self-contained true `
    -p:WarningLevel=0 `
    -p:IncludeSourceRevisionInInformationalVersion=false `
    -p:Version=`"$Ver`" `
    -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=true `
    --output "$OutDir\UpdateService"

if ($LASTEXITCODE -ne 0) { throw "UpdateService publish failed." }
Write-Host "UpdateService published OK." -ForegroundColor Green

# ── Publish UpdateNotifier ────────────────────────────────────────────────────
Write-Host "`nPublishing UpdateNotifier..." -ForegroundColor Yellow
dotnet publish "$PSScriptRoot\UpdateNotifier\UpdateNotifier.csproj" `
    --configuration $Configuration `
    --runtime win-x64 `
    --self-contained true `
    -p:WarningLevel=0 `
    -p:IncludeSourceRevisionInInformationalVersion=false `
    -p:Version=`"$Ver`" `
    -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=false `
    --output "$OutDir\UpdateNotifier"

if ($LASTEXITCODE -ne 0) { throw "UpdateNotifier publish failed." }
Write-Host "UpdateNotifier published OK." -ForegroundColor Green

# ── Merge into a single deployment folder ────────────────────────────────────
# The service looks for UpdateNotifier.exe in its own directory,
# so both EXEs must live together.
Write-Color "`nMerging output into: $OutDir\Deploy" -Color Yellow
$deployDir = "$OutDir\Deploy"
New-Item -ItemType Directory -Force -Path $deployDir | Out-Null

# Copy ALL files from both publish outputs so native WPF DLLs are included.
Copy-Item "$OutDir\UpdateService\*"  $deployDir -Recurse -Force
Copy-Item "$OutDir\UpdateNotifier\*" $deployDir -Recurse -Force

# Copy the company logo next to the exe so LoadLogo() can find it.
# The logo is loaded from disk at runtime (not embedded) to avoid a WPF
# pack URI crash on .NET 10 during early window initialisation.
$logoSrc = "$PSScriptRoot\UpdateNotifier\Resources\CompanyLogo.png"
if (Test-Path $logoSrc) {
    Copy-Item $logoSrc $deployDir -Force
    Write-Host "CompanyLogo.png copied to deploy folder." -ForegroundColor Green
} else {
    Write-Host "WARNING: CompanyLogo.png not found at $logoSrc" -ForegroundColor Yellow
    Write-Host "         The notifier window will appear without a logo." -ForegroundColor Yellow
}

$exeVer1 = (Get-Item -Path "$deployDir\UpdateService.exe").VersionInfo.ProductVersion
Write-Color "Service version:         ", $exeVer1 -Color White, Yellow
$exeVer2 = (Get-Item -Path "$deployDir\UpdateNotifier.exe").VersionInfo.ProductVersion
Write-Color "Notifier version:        ", $exeVer2 -Color White, Yellow

Write-Color "`n✅  Deploy folder ready: $deployDir" -Color Green
Write-Host "   UpdateService.exe"
Write-Host "   UpdateNotifier.exe`n"
Write-Host "To install the service manually (run as Administrator):"
Write-Host "   $deployDir\UpdateService.exe --install`n"

if ($exeVer1 -eq $exeVer2) {
    # ── Write VersionControl.dat ──────────────────────────────────────────────
    # Written as pure ASCII with no BOM so that HttpClient / Version.TryParse
    # can read it without encountering an unexpected leading byte.
    Write-Color "`n✅  Updating version control to version: ", $Ver -Color Green, Yellow
    [System.IO.File]::WriteAllText(
        "$PSScriptRoot\VersionControl.dat",
        $Ver,
        [System.Text.Encoding]::ASCII)

    # ── Run installer packager ────────────────────────────────────────────────
    # Copies Installer\ template files to publish\Installer\, then runs
    # create_installer.bat to produce the SFX EXE, update ZIP, and SHA-256.
    $installerSrcDir = "$PSScriptRoot\Installer"
    $installerOutDir = "$OutDir\Installer"

    if (Test-Path $installerSrcDir) {
        Write-Color "`nBuilding installer artifacts ..." -Color Yellow

        New-Item -ItemType Directory -Force -Path $installerOutDir | Out-Null
        Copy-Item "$installerSrcDir\*" $installerOutDir -Recurse -Force

        Push-Location $installerOutDir
        cmd /c "create_installer.bat" "$Ver"
        $batExit = $LASTEXITCODE
        Pop-Location

        if ($batExit -eq 0) {
            Write-Color "`n✅  Installer artifacts ready in:" -Color Green
            Write-Host "   $installerOutDir"
            Write-Host ""
            Write-Host "   CapTG-Update-$Ver.zip"
            Write-Host "   CapTG-Update-$Ver.zip.sha256"
            Write-Host "   CapTG-UpdateService-$Ver-Setup.exe"
            Write-Host ""
            Write-Color "To publish the self-update files, copy them to the repo root and commit:" -Color Cyan
            Write-Host "   copy `"$installerOutDir\CapTG-Update-$Ver.zip`"        `"$PSScriptRoot\`""
            Write-Host "   copy `"$installerOutDir\CapTG-Update-$Ver.zip.sha256`" `"$PSScriptRoot\`""
        } else {
            Write-Color "`n⚠   Installer creation failed (bat exit $batExit). Check output above." -Color Yellow
        }
    } else {
        Write-Color "`nℹ   Skipping installer — Installer\ directory not found." -Color White
    }
} else {
    Write-Color "`n⚠   Version mismatch between EXEs — skipping VersionControl.dat update and installer packaging." -Color Yellow
}
