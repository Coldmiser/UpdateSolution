// UpdateService/Workers/UpdateOrchestrator.cs
// Runs once per hour.  Calls both WingetWorker and WindowsUpdateWorker,
// aggregates results, decides whether a reboot is needed, then signals
// the PipeServer to notify the logged-in user.

using Microsoft.Win32;
using Shared.Constants;
using Shared.Helpers;
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
            // Enforce a minimum 3-day gap between forced reboots.
            // Use the more recent of the registry timestamp and the actual Windows
            // boot time so that a manual reboot also resets the clock.
            var systemBootTime = DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);

            var lastRebootStr = RegistryHelper.GetString(RegistryConstants.LastRebootUtc, string.Empty);
            var registryTime  = string.IsNullOrEmpty(lastRebootStr) ? DateTime.MinValue
                : DateTime.TryParse(lastRebootStr, null,
                      System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
                      ? parsed : DateTime.MinValue;

            var effectiveLastReboot = registryTime > systemBootTime ? registryTime : systemBootTime;
            var hoursSince          = (DateTime.UtcNow - effectiveLastReboot).TotalHours;

            if (!IsTodayAllowedForReboot())
            {
                LogConfig.ServiceLog.Information(
                    "UpdateOrchestrator: reboot required but today is not an allowed reboot day " +
                    "per RebootDoW — skipping notification until the next allowed day.");
            }
            else if ((DateTime.UtcNow - effectiveLastReboot) < TimeSpan.FromDays(3))
            {
                LogConfig.ServiceLog.Information(
                    "UpdateOrchestrator: reboot required but last reboot was {H:F1} hours ago — " +
                    "skipping notification until 3-day minimum has elapsed.",
                    hoursSince);
            }
            else
            {
                LogConfig.ServiceLog.Information(
                    "UpdateOrchestrator: reboot is required — notifying logged-in user.");
                await _onRebootRequired(allResults);
            }
        }
        else
        {
            LogConfig.ServiceLog.Information(
                "UpdateOrchestrator: no reboot required — cycle complete.");
        }
    }

    /// <summary>
    /// Reads the RebootDoW bitmask from HKLM\SOFTWARE\CapTG and checks whether
    /// today's day of week is permitted to show the reboot notifier. Bit index
    /// matches <see cref="DateTime.DayOfWeek"/> (bit 0 = Sunday ... bit 6 = Saturday).
    /// Defaults to <see cref="AppConstants.DefaultRebootDayOfWeekMask"/> (Mon-Fri)
    /// when the value is missing or unreadable.
    /// </summary>
    private static bool IsTodayAllowedForReboot()
    {
        int mask;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(RegistryConstants.CapTgKeyPath);
            mask = key?.GetValue(RegistryConstants.RebootDoW) is int m
                ? m
                : AppConstants.DefaultRebootDayOfWeekMask;
        }
        catch
        {
            mask = AppConstants.DefaultRebootDayOfWeekMask;
        }

        var todayBit = 1 << (int)DateTime.Now.DayOfWeek;
        return (mask & todayBit) != 0;
    }

    // ── Static helper ────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a concise human-readable summary of an update cycle result set.
    /// Used to populate <see cref="Shared.Models.PipeMessage.UpdateSummary"/>.
    /// </summary>
    public static string BuildSummary(List<UpdateResult> results)
    {
        // Exclude the PendingReboot sentinel — it is a system state, not an actual update.
        var actual    = results.Where(r => r.Identifier != "PendingReboot").ToList();
        var succeeded = actual.Count(r => r.Status == UpdateStatus.Succeeded);
        var failed    = actual.Count(r => r.Status == UpdateStatus.Failed);

        var lines = new List<string>();

        if (succeeded > 0)
            lines.Add($"{succeeded} update(s) applied successfully.");
        else if (results.Any(r => r.Identifier == "PendingReboot"))
            lines.Add("A restart is required to complete previously installed updates.");

        if (failed > 0)
            lines.Add($"{failed} update(s) failed and will be retried next cycle.");

        return lines.Count > 0 ? string.Join("  ", lines) : "Updates have been applied.";
    }

    /// <summary>
    /// Extracts Windows patch display names from a result set (KB-identified items only).
    /// Returns the full Title (e.g. "2024-12 Cumulative Update for Windows 11…") when available,
    /// falling back to the KB identifier.
    /// </summary>
    public static List<string> ExtractKbNumbers(List<UpdateResult> results) =>
        results
            .Where(r => r.Identifier.StartsWith("KB", StringComparison.OrdinalIgnoreCase)
                     && r.Status == UpdateStatus.Succeeded)
            .Select(r => !string.IsNullOrWhiteSpace(r.Title) ? r.Title : r.Identifier)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct()
            .ToList();

    /// <summary>
    /// Extracts winget package display names from a result set.
    /// Returns the friendly application name (Title) when available, falling back to the package ID.
    /// Excludes the internal PendingReboot sentinel.
    /// </summary>
    public static List<string> ExtractPackageIds(List<UpdateResult> results) =>
        results
            .Where(r => !r.Identifier.StartsWith("KB", StringComparison.OrdinalIgnoreCase)
                     && r.Identifier != "PendingReboot"
                     && r.Status == UpdateStatus.Succeeded)
            .Select(r => !string.IsNullOrWhiteSpace(r.Title) ? r.Title : r.Identifier)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct()
            .ToList();
}
