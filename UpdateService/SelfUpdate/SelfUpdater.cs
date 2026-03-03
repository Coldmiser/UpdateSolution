// UpdateService/SelfUpdate/SelfUpdater.cs
// Checks a plain-text version manifest hosted on the internet.
// If a newer version is available it downloads a ZIP, verifies its SHA-256
// hash against a companion hash file, extracts it to a staging directory, and
// runs an upgrade script that stops the service, replaces files, and restarts.
//
// Hash file format: a single line containing the lower-case hex SHA-256 digest
// of the ZIP, e.g.:
//   e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855
//
// The version file must contain ONLY a semantic version string, e.g.:  1.2.3

using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
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
    private readonly string     _hashUrlTemplate;
    private readonly Version    _currentVersion;

    // ── Constructor ──────────────────────────────────────────────────────────

    public SelfUpdater(HttpClient http)
    {
        _http             = http;
        _versionFileUrl   = RegistryHelper.GetString(
            RegistryConstants.VersionFileUrl, AppConstants.DefaultVersionFileUrl);
        _zipUrlTemplate   = RegistryHelper.GetString(
            RegistryConstants.UpdateZipUrlTemplate, AppConstants.DefaultUpdateZipUrlTemplate);
        _hashUrlTemplate  = RegistryHelper.GetString(
            RegistryConstants.HashFileUrlTemplate, AppConstants.DefaultHashFileUrlTemplate);

        // Read the assembly's informational version (e.g. "1.0.0") at runtime.
        var infoVer = Assembly.GetExecutingAssembly()
                              .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                              ?.InformationalVersion ?? "0.0.0";

        _currentVersion = Version.TryParse(infoVer.Split('+')[0], out var v) ? v : new Version(0, 0, 0);
    }

    // ── Public API ───────────────────────────────────────────────────────────

    /// <summary>
    /// Checks the remote version manifest.  If a newer version is available,
    /// downloads, verifies, and applies the update.  The service will be restarted
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
    /// Downloads the update ZIP, verifies its SHA-256 hash, extracts it to a
    /// staging directory, then executes an upgrade batch script that handles the
    /// hot-swap while the service is briefly stopped.
    /// </summary>
    private async Task DownloadAndApplyAsync(string version, CancellationToken cancellationToken)
    {
        var zipUrl     = string.Format(_zipUrlTemplate, version);
        var hashUrl    = string.Format(_hashUrlTemplate, version);
        var stagingDir = AppConstants.UpdateStagingDirectory;
        var zipPath    = Path.Combine(stagingDir, $"CapTG-Update-{version}.zip");

        // Create (or clean) the staging directory.
        if (Directory.Exists(stagingDir))
            Directory.Delete(stagingDir, recursive: true);
        Directory.CreateDirectory(stagingDir);

        // ── Fetch expected hash ───────────────────────────────────────────────
        string expectedHash;
        try
        {
            expectedHash = (await _http.GetStringAsync(hashUrl, cancellationToken))
                           .Trim()
                           .ToLowerInvariant();

            if (expectedHash.Length != 64 || !IsHex(expectedHash))
            {
                LogConfig.ServiceLog.Error(
                    "SelfUpdater: hash file at {Url} did not contain a valid 64-char hex SHA-256 digest. " +
                    "Aborting upgrade.", hashUrl);
                return;
            }
        }
        catch (Exception ex)
        {
            LogConfig.ServiceLog.Error(ex, "SelfUpdater: failed to fetch hash file from {Url}. " +
                "Aborting upgrade.", hashUrl);
            return;
        }

        // ── Download ZIP ──────────────────────────────────────────────────────
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

        // ── Verify SHA-256 ────────────────────────────────────────────────────
        LogConfig.ServiceLog.Information("SelfUpdater: verifying SHA-256 of downloaded ZIP.");
        string actualHash;
        try
        {
            await using var fs = File.OpenRead(zipPath);
            var hashBytes = await SHA256.HashDataAsync(fs, cancellationToken);
            actualHash = Convert.ToHexString(hashBytes).ToLowerInvariant();
        }
        catch (Exception ex)
        {
            LogConfig.ServiceLog.Error(ex, "SelfUpdater: failed to hash ZIP file.");
            return;
        }

        if (!string.Equals(actualHash, expectedHash, StringComparison.Ordinal))
        {
            LogConfig.ServiceLog.Error(
                "SelfUpdater: SHA-256 mismatch — aborting upgrade. " +
                "Expected={Expected} Actual={Actual}", expectedHash, actualHash);
            try { File.Delete(zipPath); } catch { /* best effort */ }
            return;
        }

        LogConfig.ServiceLog.Information("SelfUpdater: SHA-256 verified.");

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

    private static bool IsHex(string s)
    {
        foreach (var c in s)
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
                return false;
        return true;
    }
}
