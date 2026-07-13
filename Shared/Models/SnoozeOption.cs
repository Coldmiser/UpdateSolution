// Shared/Models/SnoozeOption.cs
// Defines each selectable snooze tier shown in the WPF notification window.
// The service does not reference this directly; it lives in Shared so both
// projects can reason about the same tier values.

namespace Shared.Models;

/// <summary>
/// A single snooze tier available to the user in the notification window.
/// </summary>
public sealed class SnoozeOption
{
    /// <summary>Label shown in the combo-box / button (e.g. "Snooze for 6 Hours").</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>
    /// How long to defer the reboot notification.
    /// Zero (<see cref="TimeSpan.Zero"/>) represents "Reboot Now"
    /// (or the scheduled-reboot option, discriminated by <see cref="IsScheduledReboot"/>).
    /// </summary>
    public TimeSpan Duration { get; init; }

    /// <summary>
    /// True for the "Reboot at 5:30 PM" option: the service schedules the
    /// reboot at that time with a short warning popup beforehand — no further
    /// user input is required. Duration is Zero so the snooze-tier removal
    /// rules never remove it.
    /// </summary>
    public bool IsScheduledReboot { get; init; }

    /// <summary>
    /// The canonical ordered list of all possible snooze tiers,
    /// from longest to shortest (Reboot Now is last at zero).
    /// </summary>
    public static IReadOnlyList<SnoozeOption> AllTiers { get; } =
    [
        new() { Label = "Snooze for 6 Hours",   Duration = TimeSpan.FromHours(6)   },
        new() { Label = "Snooze for 3 Hours",   Duration = TimeSpan.FromHours(3)   },
        new() { Label = "Snooze for 1 Hour",    Duration = TimeSpan.FromHours(1)   },
        new() { Label = "Snooze for 30 Minutes",Duration = TimeSpan.FromMinutes(30) },
        new() { Label = "Snooze for 15 Minutes",Duration = TimeSpan.FromMinutes(15) },
        new() { Label = "Reboot at 5:30 PM",     Duration = TimeSpan.Zero, IsScheduledReboot = true },
        new() { Label = "Reboot Now",            Duration = TimeSpan.Zero            }
    ];
}
