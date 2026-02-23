# Build-And-Publish.ps1
# Builds both projects as single-file, self-contained x64 executables.
# Run from the solution root:
#   .\Build-And-Publish.ps1
# Optional parameters:
#   -Configuration Release|Debug  (default: Release)
#   -OutDir        path           (default: .\publish)

param(
    [string]$Configuration = "Release",
    [string]$OutDir        = "$PSScriptRoot\publish"
)

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
    -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=true `
    --output "$OutDir\UpdateNotifier"

if ($LASTEXITCODE -ne 0) { throw "UpdateNotifier publish failed." }
Write-Host "UpdateNotifier published OK." -ForegroundColor Green

# ── Merge into a single deployment folder ────────────────────────────────────
# The service looks for UpdateNotifier.exe in its own directory,
# so both EXEs must live together.
Write-Host "`nMerging output into: $OutDir\Deploy" -ForegroundColor Yellow
$deployDir = "$OutDir\Deploy"
New-Item -ItemType Directory -Force -Path $deployDir | Out-Null

Copy-Item "$OutDir\UpdateService\UpdateService.exe"   $deployDir -Force
Copy-Item "$OutDir\UpdateNotifier\UpdateNotifier.exe" $deployDir -Force

Write-Host "`n✅  Deploy folder ready: $deployDir" -ForegroundColor Green
Write-Host "   UpdateService.exe"
Write-Host "   UpdateNotifier.exe`n"
Write-Host "To install the service (run as Administrator):"
Write-Host "   $deployDir\UpdateService.exe --install`n"
