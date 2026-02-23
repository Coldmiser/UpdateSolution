// UpdateService/SelfUpdate/SelfUpdater.cs
// Checks a plain-text version manifest hosted on the internet.
// If a newer version is available it downloads a ZIP, stages it, and
// runs an upgrade script that stops the service, replaces files, and restarts.
// The version file must contain ONLY a semantic version string, e.g.:  1.2.3

using System.IO.Compression;
using System.Reflection;
using Shared.Constants;
using Shared.Helpers;
using UpdateService.Logging;

namespace UpdateService.SelfUpdate;

/// <summary>
/// Compares the running version against a remote manifest and self-updates if necessary.
/// </summary>
public sealed class SelfUpdater
{
    // ── Fields ───────────────────────────────────────────────────────────────

    private readonly HttpClient _http;
    private readonly string     _versionFileUrl;
    private readonly string     _zipUrlTemplate;
    private readonly Version    _currentVersion;

    // ── Constructor ──────────────────────────────────────────────────────────

    public SelfUpdater(HttpClient http)
    {
        _http           = http;
        _versionFileUrl = RegistryHelper.GetString(
            RegistryConstants.VersionFileUrl, AppConstants.DefaultVersionFileUrl);
        _zipUrlTemplate = RegistryHelper.GetString(
            RegistryConstants.UpdateZipUrlTemplate, AppConstants.DefaultUpdateZipUrlTemplate);

        // Read the assembly's informational version (e.g. "1.0.0") at runtime.
        var infoVer = Assembly.GetExecutingAssembly()
                              .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                              ?.InformationalVersion ?? "0.0.0";

        _currentVersion = Version.TryParse(infoVer.Split('+')[0], out var v) ? v : new Version(0, 0, 0);
    }

    // ── Public API ───────────────────────────────────────────────────────────

    /// <summary>
    /// Checks the remote version manifest.  If a newer version is available,
    /// downloads and applies the update.  The service will be restarted
    /// automatically by the SCM recovery policy after the upgrade script stops it.
    /// </summary>
    public async Task CheckAndApplyAsync(CancellationToken cancellationToken)
    {
        LogConfig.ServiceLog.Information(
            "SelfUpdater: checking for updates. Current version: {Ver}", _currentVersion);

        Version remoteVersion;
        try
        {
            var raw = (await _http.GetStringAsync(_versionFileUrl, cancellationToken)).Trim();
            if (!Version.TryParse(raw, out remoteVersion!))
            {
                LogConfig.ServiceLog.Warning(
                    "SelfUpdater: remote version file contained invalid version string: '{Raw}'", raw);
                return;
            }
        }
        catch (Exception ex)
        {
            LogConfig.ServiceLog.Error(ex, "SelfUpdater: failed to fetch version manifest from {Url}",
                _versionFileUrl);
            return;
        }

        LogConfig.ServiceLog.Information(
            "SelfUpdater: remote version = {Remote}.", remoteVersion);

        if (remoteVersion <= _currentVersion)
        {
            LogConfig.ServiceLog.Information("SelfUpdater: already up to date.");
            return;
        }

        LogConfig.ServiceLog.Information(
            "SelfUpdater: new version {Remote} available — beginning upgrade.", remoteVersion);

        await DownloadAndApplyAsync(remoteVersion.ToString(), cancellationToken);
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Downloads the update ZIP, extracts it to a staging directory, then
    /// executes an upgrade batch script that handles the hot-swap while the
    /// service is briefly stopped.
    /// </summary>
    private async Task DownloadAndApplyAsync(string version, CancellationToken cancellationToken)
    {
        var zipUrl     = string.Format(_zipUrlTemplate, version);
        var stagingDir = AppConstants.UpdateStagingDirectory;
        var zipPath    = Path.Combine(stagingDir, $"CapTG-Update-{version}.zip");

        // Create (or clean) the staging directory.
        if (Directory.Exists(stagingDir))
            Directory.Delete(stagingDir, recursive: true);
        Directory.CreateDirectory(stagingDir);

        // ── Download ──────────────────────────────────────────────────────────
        LogConfig.ServiceLog.Information("SelfUpdater: downloading {Url}", zipUrl);
        try
        {
            await using var stream = await _http.GetStreamAsync(zipUrl, cancellationToken);
            await using var file   = File.Create(zipPath);
            await stream.CopyToAsync(file, cancellationToken);
        }
        catch (Exception ex)
        {
            LogConfig.ServiceLog.Error(ex, "SelfUpdater: download failed.");
            return;
        }

        // ── Extract ───────────────────────────────────────────────────────────
        LogConfig.ServiceLog.Information("SelfUpdater: extracting {Zip}", zipPath);
        try
        {
            ZipFile.ExtractToDirectory(zipPath, stagingDir, overwriteFiles: true);
            File.Delete(zipPath); // no longer needed
        }
        catch (Exception ex)
        {
            LogConfig.ServiceLog.Error(ex, "SelfUpdater: extraction failed.");
            return;
        }

        // ── Write and launch upgrade batch script ─────────────────────────────
        // The script runs outside the service process so it can replace the
        // service executable while the service itself is stopped.
        var serviceBin = AppConstants.ServiceName;  // used in sc commands
        var targetDir  = Path.GetDirectoryName(
            System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName)!;

        var batchPath = Path.Combine(stagingDir, "apply_update.cmd");
        var batchContent = $@"@echo off
:: Generated by SelfUpdater — do not edit manually.
timeout /t 3 /nobreak > nul
sc stop ""{serviceBin}""
timeout /t 5 /nobreak > nul
robocopy ""{stagingDir}"" ""{targetDir}"" /E /IS /IT /IM /XF apply_update.cmd
sc start ""{serviceBin}""
del /F /Q ""%~f0""
";
        await File.WriteAllTextAsync(batchPath, batchContent, cancellationToken);

        LogConfig.ServiceLog.Information(
            "SelfUpdater: launching upgrade script at {Path}", batchPath);

        // Launch the batch file as a detached, hidden process.
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName        = "cmd.exe",
            Arguments       = $"/c \"{batchPath}\"",
            CreateNoWindow  = true,
            UseShellExecute = false
        });

        // The SCM will restart us automatically after the script stops the service.
        LogConfig.ServiceLog.Information(
            "SelfUpdater: upgrade initiated. Service will restart momentarily.");
    }
}
