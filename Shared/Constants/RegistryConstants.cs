// Shared/Constants/RegistryConstants.cs
// Names of every registry value that can override a default setting.
// Both the service and the notifier read from the same hive so that
// a single registry push from IT covers both programs.

namespace Shared.Constants;

/// <summary>
/// Registry locations and value names used to configure the solution.
/// All values live under HKLM\SOFTWARE\CapTG\UpdateService.
/// </summary>
public static class RegistryConstants
{
    /// <summary>HKLM registry key path (no leading backslash).</summary>
    public const string RootKeyPath = @"SOFTWARE\CapTG\UpdateService";

    /// <summary>HKLM registry key path of the CapTG parent key (no leading backslash).</summary>
    public const string CapTgKeyPath = @"SOFTWARE\CapTG";

    // ── Value names ─────────────────────────────────────────────────────────────

    /// <summary>
    /// REG_SZ — full path to the directory where log files are written.
    /// Defaults to <see cref="AppConstants.DefaultLogDirectory"/>.
    /// </summary>
    public const string LogDirectory = "LogDirectory";

    /// <summary>
    /// REG_SZ — URL of the plain-text version manifest for self-update.
    /// Defaults to <see cref="AppConstants.DefaultVersionFileUrl"/>.
    /// </summary>
    public const string VersionFileUrl = "VersionFileUrl";

    /// <summary>
    /// REG_SZ — URL of the installer EXE to download and run on self-update.
    /// Defaults to <see cref="AppConstants.DefaultInstallerUrl"/>.
    /// </summary>
    public const string InstallerUrl = "InstallerUrl";

    /// <summary>
    /// REG_SZ — full path to the UpdateNotifier.exe.
    /// Defaults to the same directory as the service executable.
    /// </summary>
    public const string NotifierPath = "NotifierPath";

    /// <summary>
    /// REG_DWORD — update interval in minutes (default 60).
    /// </summary>
    public const string UpdateIntervalMinutes = "UpdateIntervalMinutes";

    /// <summary>
    /// REG_SZ — semicolon-separated list of winget package IDs to skip during upgrades.
    /// Example: "Syncthing.Syncthing;SomeOther.Package"
    /// </summary>
    public const string WingetExclusions = "WingetExclusions";

    /// <summary>
    /// REG_SZ — ISO 8601 UTC timestamp of the last winget upgrade pass.
    /// Used to enforce a once-per-week throttle on winget upgrades.
    /// </summary>
    public const string WingetLastRunUtc = "WingetLastRunUtc";

    /// <summary>
    /// REG_SZ — ISO 8601 UTC timestamp of the last service-initiated reboot.
    /// Used to enforce a minimum 2-day gap between forced reboots.
    /// </summary>
    public const string LastRebootUtc = "LastRebootUtc";

    /// <summary>
    /// REG_SZ — ISO 8601 UTC timestamp of a user-scheduled "Reboot at 5:30 PM"
    /// that has not happened yet. Persisted so the reboot survives a service
    /// restart, sleep, or power-off: on service start, an overdue value causes
    /// the reboot to run upon waking. Cleared when the reboot is initiated.
    /// </summary>
    public const string ScheduledRebootUtc = "ScheduledRebootUtc";

    /// <summary>
    /// REG_SZ under <see cref="CapTgKeyPath"/> — machine type: "Server" or "Client".
    /// Auto-detected at service startup from Windows' own InstallationType value.
    /// </summary>
    public const string InstType = "InstType";

    /// <summary>
    /// REG_SZ under <see cref="CapTgKeyPath"/> — local time of day at which servers
    /// are silently rebooted (e.g. "4:00am"). Written when InstType = Server is detected.
    /// </summary>
    public const string RebootTime = "RebootTime";
}
