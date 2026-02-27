// UpdateService/Workers/WingetWorker.cs
// Runs `winget upgrade --all --include-unknown` silently in the background.
// Unknown versions are included so that software without a version tag is still upgraded.
// All output is captured and logged; individual package failures are recorded but
// do not abort the rest of the upgrade pass.

using System.Diagnostics;
using System.Text.RegularExpressions;
using Shared.Models;
using UpdateService.Logging;

namespace UpdateService.Workers;

/// <summary>
/// Wraps the winget CLI to upgrade all installed applications silently.
/// </summary>
public static class WingetWorker
{
    // ── Public API ───────────────────────────────────────────────────────────

    /// <summary>
    /// Runs a full winget upgrade pass.
    /// </summary>
    /// <param name="cancellationToken">Honoured between package upgrades.</param>
    /// <returns>
    /// A list of <see cref="UpdateResult"/> objects — one per package
    /// that winget attempted to upgrade.
    /// </returns>
    public static async Task<List<UpdateResult>> RunAsync(CancellationToken cancellationToken)
    {
        var results = new List<UpdateResult>();

        LogConfig.ServiceLog.Information("WingetWorker: starting upgrade pass.");

        // First, get the list of packages that have available upgrades.
        var availableUpgrades = await ListAvailableUpgradesAsync(cancellationToken);

        if (availableUpgrades.Count == 0)
        {
            LogConfig.ServiceLog.Information("WingetWorker: no winget upgrades available.");
            return results;
        }

        LogConfig.ServiceLog.Information(
            "WingetWorker: {Count} package(s) queued for upgrade.", availableUpgrades.Count);

        // Upgrade every package individually so a single failure doesn't block others.
        foreach (var pkg in availableUpgrades)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = await UpgradePackageAsync(pkg, cancellationToken);
            results.Add(result);

            // Log to history immediately so the record survives even if later steps fail.
            LogHistoryEntry(result);
        }

        LogConfig.ServiceLog.Information(
            "WingetWorker: pass complete. Succeeded={S} Failed={F} Skipped={K}",
            results.Count(r => r.Status == UpdateStatus.Succeeded),
            results.Count(r => r.Status == UpdateStatus.Failed),
            results.Count(r => r.Status == UpdateStatus.Skipped));

        return results;
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Queries `winget upgrade --include-unknown` to get the list of
    /// packages with available updates, without actually installing anything.
    /// </summary>
    private static async Task<List<string>> ListAvailableUpgradesAsync(
        CancellationToken cancellationToken)
    {
        var ids = new List<string>();

        var (exitCode, stdout, stderr) = await RunWingetAsync(
            "upgrade --include-unknown --accept-source-agreements",
            cancellationToken);

        if (exitCode != 0 && exitCode != 3010) // 3010 = success + reboot needed
        {
            LogConfig.ServiceLog.Warning(
                "WingetWorker: listing upgrades returned exit code {Code}. Stderr: {Err}",
                exitCode, stderr);
            return ids;
        }

        // Parse the tabular output.  Each data row looks like:
        //   DisplayName    Id                   Version  Available  Source
        // We want the Id column (index 1 when split on 2+ spaces).
        foreach (var line in stdout.Split('\n'))
        {
            var parts = Regex.Split(line.Trim(), @"\s{2,}");
            if (parts.Length >= 2 && !parts[1].StartsWith("Id", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(parts[1]))
            {
                ids.Add(parts[1].Trim());
            }
        }

        return ids;
    }

    /// <summary>
    /// Upgrades a single package by its winget Id.
    /// </summary>
    private static async Task<UpdateResult> UpgradePackageAsync(
        string packageId, CancellationToken cancellationToken)
    {
        LogConfig.ServiceLog.Information("WingetWorker: upgrading package '{Id}'.", packageId);

        var args = $"upgrade --id \"{packageId}\" "
                 + "--silent "
                 + "--accept-source-agreements "
                 + "--accept-package-agreements "
                 + "--include-unknown";

        var (exitCode, stdout, stderr) = await RunWingetAsync(args, cancellationToken);

        // Exit 0 = success, 3010 = success + reboot required.
        var succeeded    = exitCode is 0 or 3010;
        var rebootNeeded = exitCode == 3010;

        if (succeeded)
        {
            LogConfig.ServiceLog.Information(
                "WingetWorker: '{Id}' upgraded successfully. RebootRequired={R}",
                packageId, rebootNeeded);
        }
        else
        {
            LogConfig.ServiceLog.Warning(
                "WingetWorker: '{Id}' upgrade failed (exit {Code}). Stderr: {Err}",
                packageId, exitCode, stderr);
        }

        return new UpdateResult
        {
            Identifier     = packageId,
            Title          = packageId,
            Status         = succeeded ? UpdateStatus.Succeeded : UpdateStatus.Failed,
            ErrorMessage   = succeeded ? null : $"winget exit code {exitCode}: {stderr}",
            RebootRequired = rebootNeeded,
            AttemptedAt    = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Runs the winget executable with the given arguments and captures all output.
    /// Fully qualifies System.Diagnostics.Process to avoid conflict with the
    /// UpdateService.Process namespace (the UserProcessLauncher folder).
    /// </summary>
    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunWingetAsync(
        string arguments, CancellationToken cancellationToken)
    {
        using var proc = new System.Diagnostics.Process();
        proc.StartInfo = new ProcessStartInfo
        {
            FileName               = "winget",
            Arguments              = arguments,
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true,
            // Ensure winget can find its own resources even when launched by SYSTEM.
            Environment = { ["LOCALAPPDATA"] = @"C:\Windows\System32\config\systemprofile\AppData\Local" }
        };

        LogConfig.ServiceLog.Debug("WingetWorker: winget {Args}", arguments);

        proc.Start();

        // Read stdout/stderr asynchronously to prevent deadlocks on large output.
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = proc.StandardError.ReadToEndAsync(cancellationToken);

        await proc.WaitForExitAsync(cancellationToken);

        return (proc.ExitCode, await stdoutTask, await stderrTask);
    }

    /// <summary>
    /// Writes a single update-history log entry for one package result.
    /// </summary>
    private static void LogHistoryEntry(UpdateResult result)
    {
        if (result.Status == UpdateStatus.Succeeded)
        {
            LogConfig.HistoryLog.Information(
                "WINGET | {Id} | Status={Status} | RebootRequired={Reboot} | {At:O}",
                result.Identifier, result.Status, result.RebootRequired, result.AttemptedAt);
        }
        else
        {
            LogConfig.HistoryLog.Warning(
                "WINGET | {Id} | Status={Status} | Error={Err} | {At:O}",
                result.Identifier, result.Status, result.ErrorMessage, result.AttemptedAt);
        }
    }
}