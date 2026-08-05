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
    public static readonly string DefaultLogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "CapTG", "UpdateService", "logs");

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
        "https://raw.githubusercontent.com/Coldmiser/UpdateSolution/refs/heads/main/VersionControl.dat";

    /// <summary>
    /// URL of the installer EXE to download and run when a newer version is available.
    /// </summary>
    public const string DefaultInstallerUrl =
        "https://github.com/Coldmiser/UpdateSolution/releases/latest/download/__CapTG_Updater_Latest.exe";

    /// <summary>Local directory where the update ZIP is extracted before applying.</summary>
    public static readonly string UpdateStagingDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "CapTG", "UpdateStaging");

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

    // ── Scheduled Reboot ("Reboot at 5:30 PM" option) ──────────────────────────

    /// <summary>
    /// Sentinel value sent as SnoozeMinutes when the user picks the scheduled
    /// 5:30 PM reboot option. The service then owns the schedule — no further
    /// user input is required.
    /// </summary>
    public const int ScheduledRebootSentinel = -1;

    /// <summary>Local time of day for the scheduled reboot option (5:30 PM).</summary>
    public static readonly TimeSpan ScheduledRebootTime = new(17, 30, 0);

    /// <summary>
    /// Minutes before the scheduled reboot that the countdown warning appears.
    /// </summary>
    public const int ScheduledRebootWarningMinutes = 5;

    /// <summary>
    /// Minutes the reboot is pushed back when the user clicks "Delay" on the
    /// countdown warning. Only one delay is allowed; the reboot then proceeds
    /// with no further interaction.
    /// </summary>
    public const int ScheduledRebootDelayMinutes = 5;

    // ── Reboot Day-of-Week Restriction ─────────────────────────────────────────

    /// <summary>
    /// Default value for <see cref="RegistryConstants.RebootDoW"/> when the registry
    /// value is absent: Monday–Friday allowed, Sunday and Saturday blocked.
    /// Bit 0 = Sunday, bit 1 = Monday, ... bit 6 = Saturday (matches
    /// <see cref="DateTime.DayOfWeek"/>'s integer values), so 0x3E = 0b0111110.
    /// </summary>
    public const int DefaultRebootDayOfWeekMask = 0x3E;
}
