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
    /// REG_SZ — URL template for the update ZIP download.
    /// Use {0} as the version placeholder.
    /// Defaults to <see cref="AppConstants.DefaultUpdateZipUrlTemplate"/>.
    /// </summary>
    public const string UpdateZipUrlTemplate = "UpdateZipUrlTemplate";

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
    /// REG_SZ — URL template for the SHA-256 hash file of the update ZIP.
    /// Use {0} as the version placeholder.
    /// Defaults to <see cref="AppConstants.DefaultHashFileUrlTemplate"/>.
    /// </summary>
    public const string HashFileUrlTemplate = "HashFileUrlTemplate";

    /// <summary>
    /// REG_SZ — minimum Serilog log level for both service and notifier logs.
    /// Accepted values (case-insensitive): Verbose, Debug, Information, Warning, Error, Fatal.
    /// Defaults to "Information".
    /// </summary>
    public const string LogMinimumLevel = "LogMinimumLevel";
}
