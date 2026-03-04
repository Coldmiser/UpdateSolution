// UpdateService/SelfUpdate/SelfUpdater.cs
// Checks a plain-text version manifest hosted on the internet.
// If the remote version differs from the running version, downloads the
// installer EXE and launches it.  The installer stops the service, replaces
// files, and restarts the service automatically.

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
    private readonly string     _installerUrl;
    private readonly Version    _currentVersion;

    // ── Constructor ──────────────────────────────────────────────────────────

    public SelfUpdater(HttpClient http)
    {
        _http           = http;
        _versionFileUrl = RegistryHelper.GetString(
            RegistryConstants.VersionFileUrl, AppConstants.DefaultVersionFileUrl);
        _installerUrl   = RegistryHelper.GetString(
            RegistryConstants.InstallerUrl, AppConstants.DefaultInstallerUrl);

        // Read the assembly's informational version (e.g. "1.0.0") at runtime.
        var infoVer = Assembly.GetExecutingAssembly()
                              .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                              ?.InformationalVersion ?? "0.0.0";

        _currentVersion = Version.TryParse(infoVer.Split('+')[0], out var v) ? v : new Version(0, 0, 0);
    }

    // ── Public API ───────────────────────────────────────────────────────────

    /// <summary>
    /// Checks the remote version manifest.  If the remote version differs from
    /// the running version, downloads and launches the installer EXE.
    /// The installer stops the service, replaces files, and restarts it.
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
            "SelfUpdater: new version {Remote} available — downloading installer.", remoteVersion);

        await DownloadAndRunInstallerAsync(cancellationToken);
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Downloads the installer EXE to the staging directory and launches it.
    /// The installer handles stopping the service, replacing files, and restarting.
    /// </summary>
    private async Task DownloadAndRunInstallerAsync(CancellationToken cancellationToken)
    {
        var stagingDir    = AppConstants.UpdateStagingDirectory;
        var installerPath = Path.Combine(stagingDir, "__CapTG_Updater_Latest.exe");

        // Prepare staging directory.
        if (Directory.Exists(stagingDir))
            Directory.Delete(stagingDir, recursive: true);
        Directory.CreateDirectory(stagingDir);

        // ── Download ──────────────────────────────────────────────────────────
        LogConfig.ServiceLog.Information("SelfUpdater: downloading installer from {Url}", _installerUrl);
        try
        {
            await using var stream = await _http.GetStreamAsync(_installerUrl, cancellationToken);
            await using var file   = File.Create(installerPath);
            await stream.CopyToAsync(file, cancellationToken);
        }
        catch (Exception ex)
        {
            LogConfig.ServiceLog.Error(ex, "SelfUpdater: installer download failed.");
            return;
        }

        // ── Launch installer ──────────────────────────────────────────────────
        LogConfig.ServiceLog.Information("SelfUpdater: launching installer at {Path}", installerPath);

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName        = installerPath,
            CreateNoWindow  = true,
            UseShellExecute = false
        });

        LogConfig.ServiceLog.Information(
            "SelfUpdater: installer launched. Service will be replaced momentarily.");
    }
}
