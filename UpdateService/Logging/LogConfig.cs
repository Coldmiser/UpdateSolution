// UpdateService/Logging/LogConfig.cs
// Configures Serilog for the Windows service.
// Creates TWO sinks:
//   1. The main operational log  (UpdateService-YYYYMMDD.log)
//   2. A dedicated update-history log (UpdateHistory-YYYYMMDD.log)
// Both files roll daily and live in the directory read from the registry.

using Serilog;
using Serilog.Core;
using Shared.Constants;
using Shared.Helpers;

namespace UpdateService.Logging;

/// <summary>
/// Builds and exposes the two loggers used by the service.
/// Call <see cref="Configure"/> once at start-up before any other code runs.
/// </summary>
public static class LogConfig
{
    // ── Public logger references ─────────────────────────────────────────────

    /// <summary>
    /// General-purpose service logger. Use this for everything except update history.
    /// </summary>
    public static Serilog.ILogger ServiceLog { get; private set; } = Logger.None;

    /// <summary>
    /// Dedicated update-history logger. Only write completed update records here.
    /// This produces the audit trail of what was installed, when, and with what result.
    /// </summary>
    public static Serilog.ILogger HistoryLog { get; private set; } = Logger.None;

    // ── Configuration ────────────────────────────────────────────────────────

    /// <summary>
    /// Reads the log directory from the registry (or falls back to the default),
    /// creates the directory if it does not exist, then wires up both loggers.
    /// Must be called before the hosted service starts.
    /// </summary>
    public static void Configure()
    {
        var logDir = RegistryHelper.GetString(
            RegistryConstants.LogDirectory,
            AppConstants.DefaultLogDirectory);

        // Ensure the log directory exists before Serilog tries to create files there.
        Directory.CreateDirectory(logDir);

        // ── Operational / service log ────────────────────────────────────────
        ServiceLog = new LoggerConfiguration()
            .MinimumLevel.Verbose()                         // log absolutely everything
            .Enrich.WithThreadId()
            .Enrich.WithMachineName()
            .Enrich.FromLogContext()
            .WriteTo.Console(
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(
                path: Path.Combine(logDir, AppConstants.ServiceLogFileName),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30,
                outputTemplate:
                    "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] " +
                    "(Thread {ThreadId}) {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        // ── Update history log ───────────────────────────────────────────────
        // Kept separate so IT staff can quickly audit what was installed.
        HistoryLog = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                path: Path.Combine(logDir, AppConstants.UpdateHistoryLogFileName),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 90,             // keep 90 days of update history
                outputTemplate:
                    "{Timestamp:yyyy-MM-dd HH:mm:ss} | {Level:u3} | {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        ServiceLog.Information("Logging initialized. Directory: {LogDir}", logDir);
    }

    /// <summary>Flushes and disposes both loggers. Call on service shutdown.</summary>
    public static void CloseAndFlush()
    {
        (ServiceLog as IDisposable)?.Dispose();
        (HistoryLog as IDisposable)?.Dispose();
    }
}