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
    -p:IncludeNativeLibrariesForSelfExtract=false `
    --output "$OutDir\UpdateNotifier"

if ($LASTEXITCODE -ne 0) { throw "UpdateNotifier publish failed." }
Write-Host "UpdateNotifier published OK." -ForegroundColor Green

# ── Publish WatchDog ──────────────────────────────────────────────────────────
Write-Host "`nPublishing WatchDog..." -ForegroundColor Yellow
dotnet publish "$PSScriptRoot\WatchDog\WatchDog.csproj" `
    --configuration $Configuration `
    --runtime win-x64 `
    --self-contained true `
	-p:WarningLevel=0  `
	-p:IncludeSourceRevisionInInformationalVersion=false `
	-p:Version=`"$Ver`" `
    -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=true `
    --output "$OutDir\WatchDog"

if ($LASTEXITCODE -ne 0) { throw "WatchDog publish failed." }
Write-Host "WatchDog published OK." -ForegroundColor Green

# ── Merge into a single deployment folder ────────────────────────────────────
# The service looks for UpdateNotifier.exe in its own directory,
# so both EXEs must live together.
Write-Color "`nMerging output into: $OutDir\Deploy" -Color Yellow
$deployDir = "$OutDir\Deploy"
New-Item -ItemType Directory -Force -Path $deployDir | Out-Null

# Copy ALL files from both publish outputs so native WPF DLLs are included.
Copy-Item "$OutDir\UpdateService\*"  $deployDir -Recurse -Force
Copy-Item "$OutDir\UpdateNotifier\*" $deployDir -Recurse -Force
Copy-Item "$OutDir\WatchDog\*"       $deployDir -Recurse -Force

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
write-Color "Service version:        ", $exeVer1 -Color White, Yellow
$exeVer2 = (Get-Item -Path "$deployDir\UpdateNotifier.exe").VersionInfo.ProductVersion
write-Color "Notifier version:        ", $exeVer2 -Color White, Yellow
$exeVer3 = (Get-Item -Path "$deployDir\WatchDog.exe").VersionInfo.ProductVersion
write-Color "WatchDog version:        ", $exeVer3 -Color White, Yellow

Write-Color "`n✅  Deploy folder ready: $deployDir" -Color Green
Write-Host "   UpdateService.exe"
Write-Host "   UpdateNotifier.exe"
Write-Host "   WatchDog.exe`n"
Write-Host "To install the service (run as Administrator):"
Write-Host "   $deployDir\UpdateService.exe --install`n"

$not = "not "

if ($exeVer1 -eq $exeVer2 -and $exeVer1 -eq $exeVer3){
	Write-Color "`n✅  Updating version control to version: ", $ver -Color Green, Yellow
	$Ver  | Out-File -FilePath VersionControl.dat
	$not = ""
}

$title = 'Do you wish to create an installer?'
$msg = "$Ver has$not been created, do you wish to continue?"
$options = '&Yes', '&No' # The '&' makes the letter a shortcut
$default = 1 # 0 is Yes, 1 is No (based on index in $options array)

$result = $Host.UI.PromptForChoice($title, $msg, $options, $default)

switch ($result) {
    0 {
#		"You selected Yes."
		.\publish\Installer\create_installer.bat
	}
	1 {
		"You selected No."
	}
}
