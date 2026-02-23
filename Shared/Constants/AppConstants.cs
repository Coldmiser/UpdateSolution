// Shared/Constants/AppConstants.cs
// Central place for every magic string used by both projects.
// Change values here; they propagate automatically to both the service and the notifier.

namespace Shared.Constants;

/// <summary>
/// Application-wide constants shared between UpdateService and UpdateNotifier.
/// </summary>
public static class AppConstants
{
    // ── Named Pipe ─────────────────────────────────────────────────────────────

    /// <summary>The name of the named pipe the service listens on.</summary>
    public const string PipeName = "CapTG_UpdatePipe";

    /// <summary>Timeout (ms) the WPF client waits when connecting to the pipe.</summary>
    public const int PipeConnectTimeoutMs = 10_000;

    // ── Service Identity ────────────────────────────────────────────────────────

    /// <summary>Windows service short name (no spaces).</summary>
    public const string ServiceName = "CapTGUpdateService";

    /// <summary>Display name shown in Services.msc.</summary>
    public const string ServiceDisplayName = "CapTG Automatic Update Service";

    /// <summary>Service description shown in Services.msc.</summary>
    public const string ServiceDescription =
        "Automatically applies Windows Updates, driver updates, and winget package upgrades " +
        "hourly. Notifies the logged-in user when a reboot is required.";

    // ── Logging ─────────────────────────────────────────────────────────────────

    /// <summary>Default log directory (overridable from the registry).</summary>
    public const string DefaultLogDirectory = @"C:\ProgramData\CapTG\Logs";

    /// <summary>Service operational log file name template (Serilog rolling).</summary>
    public const string ServiceLogFileName = "UpdateService-.log";

    /// <summary>Dedicated update-history log file name template.</summary>
    public const string UpdateHistoryLogFileName = "UpdateHistory-.log";

    /// <summary>WPF notifier log file name template.</summary>
    public const string NotifierLogFileName = "UpdateNotifier-.log";

    // ── Self-Update ─────────────────────────────────────────────────────────────

    /// <summary>
    /// URL of the plain-text version file (just a semver string, e.g. "1.2.3").
    /// Override this in the registry to point to your own hosting.
    /// </summary>
    public const string DefaultVersionFileUrl =
        "https://raw.githubusercontent.com/Coldmiser/UpdateSolution/refs/heads/main/version.txt";

    /// <summary>
    /// URL template for the update ZIP.  {0} is replaced with the version string.
    /// E.g. https://updates.captg.example.com/releases/1.2.3/CapTG-Update-1.2.3.zip
    /// </summary>
    public const string DefaultUpdateZipUrlTemplate =
        "https://raw.githubusercontent.com/Coldmiser/UpdateSolution/refs/heads/main/CapTG-Update-{0}.zip";

    /// <summary>Local directory where the update ZIP is extracted before applying.</summary>
    public const string UpdateStagingDirectory = @"C:\ProgramData\CapTG\UpdateStaging";

    // ── Notifier Executable ─────────────────────────────────────────────────────

    /// <summary>
    /// File name of the WPF notifier executable.
    /// The service looks for it in the same folder as itself.
    /// </summary>
    public const string NotifierExecutableName = "UpdateNotifier.exe";

    // ── Update Schedule ─────────────────────────────────────────────────────────

    /// <summary>How often the service runs its update check cycle.</summary>
    public static readonly TimeSpan UpdateInterval = TimeSpan.FromHours(1);

    /// <summary>Delay before the very first update check after service start.</summary>
    public static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(2);
}
