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
Import-Module PSWriteColor
Clear-Host

$Year=(Get-Date).Year - 2000
$DoY=(((Get-Date).DayOfYear) * 2.7).ToString("000")
$Tm=((Get-Date).Hour * 41) + (Get-Date).Minute
$Ver="0.$Year.$DoY.$Tm"
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
	-p:WarningLevel=0  `
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
	-p:WarningLevel=0  `
	-p:IncludeSourceRevisionInInformationalVersion=false `
	-p:Version=`"$Ver`" `
    -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=true `
    --output "$OutDir\UpdateNotifier"

if ($LASTEXITCODE -ne 0) { throw "UpdateNotifier publish failed." }
Write-Host "UpdateNotifier published OK." -ForegroundColor Green

# ── Merge into a single deployment folder ────────────────────────────────────
# The service looks for UpdateNotifier.exe in its own directory,
# so both EXEs must live together.
Write-Color "`nMerging output into: $OutDir\Deploy" -Color Yellow
$deployDir = "$OutDir\Deploy"
New-Item -ItemType Directory -Force -Path $deployDir | Out-Null

Copy-Item "$OutDir\UpdateService\UpdateService.exe"   $deployDir -Force
Copy-Item "$OutDir\UpdateNotifier\UpdateNotifier.exe" $deployDir -Force

$exeVer1 = (Get-Item -Path "$deployDir\UpdateService.exe").VersionInfo.ProductVersion
write-Color "Service version:        ", $exeVer1 -Color White, Yellow
$exeVer2 = (Get-Item -Path "$deployDir\UpdateNotifier.exe").VersionInfo.ProductVersion
write-Color "Notifier version:        ", $exeVer2 -Color White, Yellow

Write-Color "`n✅  Deploy folder ready: $deployDir" -Color Green
Write-Host "   UpdateService.exe"
Write-Host "   UpdateNotifier.exe`n"
Write-Host "To install the service (run as Administrator):"
Write-Host "   $deployDir\UpdateService.exe --install`n"

if ($exeVer1 eq $exever2){
	Write-Color "`n✅  Updating version control to version: ", $ver -Color Green, Yellow
	$Ver  | Out-File -FilePath version.txt
}
