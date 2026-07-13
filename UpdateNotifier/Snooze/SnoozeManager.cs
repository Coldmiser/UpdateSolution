// UpdateNotifier/Snooze/SnoozeManager.cs
// Maintains the ordered list of snooze tiers available to the user.
// Rules:
//   • Three options are PROTECTED and never removed:
//       "Snooze for 15 Minutes", "Reboot at 5:30 PM", and "Reboot Now".
//   • Every time the user snoozes, the LONGEST available snooze tier is permanently removed.
//   • Once only the three protected options remain, they stay forever.

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
        // Only tiers longer than 15 minutes are removable — "Snooze for 15
        // Minutes", "Reboot at 5:30 PM", and "Reboot Now" are always protected.
        for (int i = 0; i < previousSnoozeCount; i++)
        {
            var toRemove = _available.FirstOrDefault(IsRemovable);
            if (toRemove is not null)
                _available.Remove(toRemove);
            else
                break; // only the protected options remain — nothing left to remove
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

        // "Snooze for 15 Minutes", "Reboot at 5:30 PM", and "Reboot Now" are
        // protected and never removed. The longest REMOVABLE snooze is always
        // the first removable item (list is ordered longest-first).
        var longest = _available.FirstOrDefault(IsRemovable);
        if (longest is not null)
        {
            _available.Remove(longest);
            LogConfig.Log.Information(
                "SnoozeManager: removed option '{Label}'. Remaining: {Count}",
                longest.Label, _available.Count);
        }
        else
        {
            LogConfig.Log.Debug(
                "SnoozeManager: only protected options remain — nothing removed.");
        }
    }

    /// <summary>
    /// Returns true when the user has exhausted all snooze tiers beyond 15 minutes
    /// and only the three protected options remain.
    /// </summary>
    public bool IsAtMinimum => !_available.Any(IsRemovable);

    /// <summary>
    /// A tier may be removed only when it is a snooze longer than 15 minutes.
    /// "Snooze for 15 Minutes", "Reboot at 5:30 PM" (scheduled), and
    /// "Reboot Now" never match — they are permanently protected.
    /// </summary>
    private static bool IsRemovable(SnoozeOption option) =>
        !option.IsScheduledReboot && option.Duration > TimeSpan.FromMinutes(15);
}
