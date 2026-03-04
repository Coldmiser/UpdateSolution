// UpdateNotifier/Snooze/SnoozeManager.cs
// Maintains the ordered list of snooze tiers available to the user.
// Rules:
//   • "Reboot Now" (zero duration) is always the last option and is never removed.
//   • "Snooze for 15 Minutes" is the shortest snooze and is also never removed.
//   • Every time the user snoozes, the LONGEST available snooze tier is permanently removed.
//   • Once only "Snooze for 15 Minutes" and "Reboot Now" remain, those two stay forever.

using Shared.Models;
using UpdateNotifier.Logging;

namespace UpdateNotifier.Snooze;

/// <summary>
/// Tracks which snooze options are still available and enforces the
/// "remove-longest-after-each-use" rule.
/// </summary>
public sealed class SnoozeManager
{
    // ── Fields ───────────────────────────────────────────────────────────────

    // The mutable ordered list — always ends with "Reboot Now" (zero duration).
    private readonly List<SnoozeOption> _available;

    // ── Constructor ──────────────────────────────────────────────────────────

    /// <summary>
    /// Initialises the manager with the full set of snooze tiers, then removes the
    /// longest options once for each previous snooze so the list reflects what the
    /// user has already consumed across earlier notifier launches.
    /// </summary>
    /// <param name="previousSnoozeCount">
    /// Number of times the user has already snoozed in this notification cycle
    /// (supplied by the service via <see cref="Shared.Models.PipeMessage.SnoozeCount"/>).
    /// </param>
    public SnoozeManager(int previousSnoozeCount = 0)
    {
        // Start from the canonical full list (longest → Reboot Now).
        _available = [.. SnoozeOption.AllTiers];

        // Re-apply the removals from previous snooze cycles.
        for (int i = 0; i < previousSnoozeCount; i++)
        {
            var toRemove = _available.FirstOrDefault(o => o.Duration > TimeSpan.FromMinutes(15));
            if (toRemove is not null)
                _available.Remove(toRemove);
            else
                break; // only 15min + Reboot Now remain — nothing left to remove
        }

        LogConfig.Log.Information(
            "SnoozeManager initialised with {Count} options (previousSnoozeCount={N}).",
            _available.Count, previousSnoozeCount);
    }

    // ── Public API ───────────────────────────────────────────────────────────

    /// <summary>
    /// Current set of selectable options, ordered longest-first with "Reboot Now" last.
    /// </summary>
    public IReadOnlyList<SnoozeOption> AvailableOptions => _available.AsReadOnly();

    /// <summary>
    /// Records that the user has snoozed and removes the longest snooze tier.
    /// Stops removing once only the 15-minute and "Reboot Now" entries remain.
    /// </summary>
    /// <param name="chosenDuration">The duration the user just selected.</param>
    public void RecordSnooze(TimeSpan chosenDuration)
    {
        LogConfig.Log.Information(
            "SnoozeManager: user snoozed for {Duration}. Options before removal: {Count}",
            chosenDuration, _available.Count);

        // "Reboot Now" (index last) and "15 Minutes" (index second-to-last) are protected.
        // We need at least 3 options before we can remove one.
        if (_available.Count <= 2)
        {
            LogConfig.Log.Debug(
                "SnoozeManager: only minimum options remain — nothing removed.");
            return;
        }

        // The longest snooze is always the first item (positive duration, not "Reboot Now").
        var longest = _available.FirstOrDefault(o => o.Duration > TimeSpan.Zero);
        if (longest is not null && longest.Duration > TimeSpan.FromMinutes(15))
        {
            _available.Remove(longest);
            LogConfig.Log.Information(
                "SnoozeManager: removed option '{Label}'. Remaining: {Count}",
                longest.Label, _available.Count);
        }
        else
        {
            LogConfig.Log.Debug("SnoozeManager: longest remaining option is 15 min — protected.");
        }
    }

    /// <summary>
    /// Returns true when the user has exhausted all snooze tiers beyond 15 minutes.
    /// </summary>
    public bool IsAtMinimum => _available.Count <= 2;
}
