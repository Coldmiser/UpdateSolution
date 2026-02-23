// UpdateNotifier/Logging/LogConfig.cs
// Configures Serilog for the WPF notifier application.
// Reads the log directory from the same registry key as the service
// so both programs write to the same folder.

using Serilog;
using Shared.Constants;
using Shared.Helpers;

namespace UpdateNotifier.Logging;

/// <summary>
/// Sets up the single Serilog logger used throughout the WPF notifier.
/// </summary>
public static class LogConfig
{
    /// <summary>The configured notifier logger. Available after <see cref="Configure"/> is called.</summary>
    public static ILogger Log { get; private set; } = Serilog.Core.Logger.None;

    /// <summary>
    /// Reads the log directory from the registry (falling back to the default),
    /// creates it if it doesn't exist, and initialises Serilog.
    /// </summary>
    public static void Configure()
    {
        var logDir = RegistryHelper.GetString(
            RegistryConstants.LogDirectory,
            AppConstants.DefaultLogDirectory);

        Directory.CreateDirectory(logDir);

        Log = new LoggerConfiguration()
            .MinimumLevel.Verbose()             // capture absolutely everything
            .Enrich.WithThreadId()
            .Enrich.FromLogContext()
            .WriteTo.File(
                path: System.IO.Path.Combine(logDir, AppConstants.NotifierLogFileName),
                rollingInterval: Serilog.RollingInterval.Day,
                retainedFileCountLimit: 30,
                outputTemplate:
                    "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] " +
                    "(Thread {ThreadId}) {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        Log.Information("UpdateNotifier logging initialised. Directory: {Dir}", logDir);
    }

    /// <summary>Flushes and disposes the logger. Call on application exit.</summary>
    public static void CloseAndFlush() => (Log as IDisposable)?.Dispose();
}
