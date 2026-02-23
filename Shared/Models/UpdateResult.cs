// Shared/Models/UpdateResult.cs
// Represents the outcome of a single update attempt (Windows patch or winget package).
// Used internally by the service and also serialised to the update history log.

namespace Shared.Models;

/// <summary>
/// Outcome classification for a single update item.
/// </summary>
public enum UpdateStatus
{
    /// <summary>The update installed without error.</summary>
    Succeeded,

    /// <summary>The update was skipped (already installed, not applicable, etc.).</summary>
    Skipped,

    /// <summary>The update failed but will be retried next cycle.</summary>
    Failed
}

/// <summary>
/// Captures the result of attempting to install one update.
/// </summary>
public sealed class UpdateResult
{
    /// <summary>KB number, winget package ID, or driver name — whatever identifies this item.</summary>
    public string Identifier { get; init; } = string.Empty;

    /// <summary>Display-friendly title / description of the update.</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>Outcome of this update attempt.</summary>
    public UpdateStatus Status { get; init; }

    /// <summary>Error message when <see cref="Status"/> is <see cref="UpdateStatus.Failed"/>.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Whether the operating system will need a reboot after this update.</summary>
    public bool RebootRequired { get; init; }

    /// <summary>UTC time the attempt was made.</summary>
    public DateTime AttemptedAt { get; init; } = DateTime.UtcNow;
}
