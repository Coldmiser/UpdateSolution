// UpdateService/Workers/UpdateOrchestrator.cs
// Runs once per hour.  Calls both WingetWorker and WindowsUpdateWorker,
// aggregates results, decides whether a reboot is needed, then signals
// the PipeServer to notify the logged-in user.

using Shared.Models;
using UpdateService.Logging;

namespace UpdateService.Workers;

/// <summary>
/// Coordinates the full hourly update cycle.
/// </summary>
public sealed class UpdateOrchestrator
{
    // ── Dependencies ─────────────────────────────────────────────────────────

    // Callback invoked when the orchestrator determines that a reboot is required.
    // The caller (UpdateBackgroundService) wires this to PipeServer.NotifyRebootRequiredAsync.
    private readonly Func<List<UpdateResult>, Task> _onRebootRequired;

    // ── Constructor ──────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a new orchestrator.
    /// </summary>
    /// <param name="onRebootRequired">
    /// Async callback invoked with all update results when at least one
    /// result has <see cref="UpdateResult.RebootRequired"/> == true.
    /// </param>
    public UpdateOrchestrator(Func<List<UpdateResult>, Task> onRebootRequired)
    {
        _onRebootRequired = onRebootRequired;
    }

    // ── Public API ───────────────────────────────────────────────────────────

    /// <summary>
    /// Executes one full update cycle:
    /// 1. Runs Windows Update (patches + drivers)
    /// 2. Runs winget (application upgrades)
    /// 3. Notifies the user if any update requires a reboot.
    /// </summary>
    public async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        LogConfig.ServiceLog.Information("UpdateOrchestrator: === Update cycle starting ===");

        var allResults = new List<UpdateResult>();

        // ── Step 1: Windows Update ───────────────────────────────────────────
        try
        {
            var wuResults = await WindowsUpdateWorker.RunAsync(cancellationToken);
            allResults.AddRange(wuResults);
        }
        catch (OperationCanceledException)
        {
            LogConfig.ServiceLog.Warning("UpdateOrchestrator: Windows Update cancelled.");
            throw; // propagate cancellation
        }
        catch (Exception ex)
        {
            // Log and continue — a Windows Update failure must not prevent winget.
            LogConfig.ServiceLog.Error(ex, "UpdateOrchestrator: Windows Update worker threw an exception.");
        }

        // ── Step 2: winget ────────────────────────────────────────────────────
        try
        {
            var wingetResults = await WingetWorker.RunAsync(cancellationToken);
            allResults.AddRange(wingetResults);
        }
        catch (OperationCanceledException)
        {
            LogConfig.ServiceLog.Warning("UpdateOrchestrator: winget worker cancelled.");
            throw;
        }
        catch (Exception ex)
        {
            LogConfig.ServiceLog.Error(ex, "UpdateOrchestrator: winget worker threw an exception.");
        }

        // ── Step 3: Evaluate reboot need ─────────────────────────────────────
        var needsReboot = allResults.Any(r => r.RebootRequired);
        var succeeded   = allResults.Count(r => r.Status == UpdateStatus.Succeeded);
        var failed      = allResults.Count(r => r.Status == UpdateStatus.Failed);

        LogConfig.ServiceLog.Information(
            "UpdateOrchestrator: cycle complete. Total={T} Succeeded={S} Failed={F} RebootRequired={R}",
            allResults.Count, succeeded, failed, needsReboot);

        if (needsReboot)
        {
            LogConfig.ServiceLog.Information(
                "UpdateOrchestrator: reboot is required — notifying logged-in user.");
            await _onRebootRequired(allResults);
        }
        else
        {
            LogConfig.ServiceLog.Information(
                "UpdateOrchestrator: no reboot required — cycle complete.");
        }
    }

    // ── Static helper ────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a concise human-readable summary of an update cycle result set.
    /// Used to populate <see cref="Shared.Models.PipeMessage.UpdateSummary"/>.
    /// </summary>
    public static string BuildSummary(List<UpdateResult> results)
    {
        var succeeded  = results.Where(r => r.Status == UpdateStatus.Succeeded).ToList();
        var failed     = results.Where(r => r.Status == UpdateStatus.Failed).ToList();

        var lines = new List<string>
        {
            $"{succeeded.Count} update(s) applied successfully."
        };

        if (failed.Count > 0)
            lines.Add($"{failed.Count} update(s) failed and will be retried next cycle.");

        return string.Join("  ", lines);
    }

    /// <summary>
    /// Extracts KB numbers from a result set (Windows Update items only).
    /// </summary>
    public static List<string> ExtractKbNumbers(List<UpdateResult> results) =>
        results
            .Where(r => r.Identifier.StartsWith("KB", StringComparison.OrdinalIgnoreCase)
                     && r.Status == UpdateStatus.Succeeded)
            .Select(r => r.Identifier)
            .Distinct()
            .ToList();

    /// <summary>
    /// Extracts winget package IDs from a result set.
    /// </summary>
    public static List<string> ExtractPackageIds(List<UpdateResult> results) =>
        results
            .Where(r => !r.Identifier.StartsWith("KB", StringComparison.OrdinalIgnoreCase)
                     && r.Status == UpdateStatus.Succeeded)
            .Select(r => r.Identifier)
            .Distinct()
            .ToList();
}
