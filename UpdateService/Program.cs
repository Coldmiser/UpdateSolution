// UpdateService/Program.cs
// Entry point for the Windows service.
// Supports three modes:
//   --install    Register the service with Windows and set defaults.
//   --uninstall  Remove the service.
//   (default)    Run as a Windows Service (or as a console app when debugging).

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Shared.Constants;
using Shared.Helpers;
using UpdateService.Install;
using UpdateService.Logging;
using UpdateService.Pipes;
using UpdateService.SelfUpdate;
using UpdateService.Service;

// ── Bootstrap logging first so we can capture any startup failures ────────────
// This MUST happen before anything else so failures during host construction
// are written to the log file rather than disappearing silently.
LogConfig.Configure();
var log = LogConfig.ServiceLog;

log.Information("CapTG UpdateService starting. Args: [{Args}]", string.Join(", ", args));

// ── Handle installer flags before building the host ───────────────────────────
if (args.Contains("--install", StringComparer.OrdinalIgnoreCase))
{
    try { ServiceInstaller.Install(); }
    catch (Exception ex) { log.Fatal(ex, "Installation failed."); return 1; }
    LogConfig.CloseAndFlush();
    return 0;
}

if (args.Contains("--uninstall", StringComparer.OrdinalIgnoreCase))
{
    try { ServiceInstaller.Uninstall(); }
    catch (Exception ex) { log.Fatal(ex, "Uninstallation failed."); return 1; }
    LogConfig.CloseAndFlush();
    return 0;
}

// ── Validate / auto-detect InstType registry entry ───────────────────────────
// HKLM\SOFTWARE\CapTG\InstType must be "Server" or "Client".
// If present with any other value, delete it and then auto-detect.
// If absent, query InstallationType from the Windows NT CurrentVersion key and write it.
void DetectAndWriteInstType(RegistryKey key)
{
    using var winNtKey = Registry.LocalMachine.OpenSubKey(
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
    var installationType = winNtKey?.GetValue("InstallationType") as string;

    var detected = installationType switch
    {
        "Client"                    => "Client",
        "Server" or "Server Core"   => "Server",
        _                           => null
    };

    if (detected is not null)
    {
        key.SetValue(RegistryConstants.InstType, detected, RegistryValueKind.String);
        log.Information(
            "Detected InstallationType '{InstallationType}', wrote InstType = {InstType}.",
            installationType, detected);

        if (detected == "Server")
        {
            key.SetValue(RegistryConstants.RebootTime, "0400", RegistryValueKind.String);
            log.Information("Server detected — wrote RebootTime = 4:00am.");
        }
    }
    else
    {
        log.Warning(
            "InstallationType '{InstallationType}' is unrecognized — InstType not written.",
            installationType ?? "(null)");
    }
}

try
{
    // CreateSubKey (not OpenSubKey) so the block still works if the key doesn't exist yet.
    using var capTgKey = Registry.LocalMachine.CreateSubKey(
        RegistryConstants.CapTgKeyPath, writable: true);

    var instType = capTgKey.GetValue(RegistryConstants.InstType) as string;
    if (instType is not null)
    {
        if (instType.Equals("Server", StringComparison.OrdinalIgnoreCase) ||
            instType.Equals("Client", StringComparison.OrdinalIgnoreCase))
        {
            log.Information("InstType = {InstType}", instType);
        }
        else
        {
            log.Warning(
                "InstType value '{InstType}' is invalid (expected Server or Client) — deleting entry.",
                instType);
            capTgKey.DeleteValue(RegistryConstants.InstType, throwOnMissingValue: false);
            DetectAndWriteInstType(capTgKey);
        }
    }
    else
    {
        DetectAndWriteInstType(capTgKey);
    }
}
catch (Exception ex)
{
    log.Warning(ex, "Could not validate HKLM\\SOFTWARE\\CapTG\\InstType.");
}

// ── Resolve the path to the WPF notifier executable ──────────────────────────
// Default: same directory as this executable.
var exeDir = Path.GetDirectoryName(
    System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName)!;

var notifierPath = RegistryHelper.GetString(
    RegistryConstants.NotifierPath,
    Path.Combine(exeDir, AppConstants.NotifierExecutableName));

log.Information("Notifier path: {Path}", notifierPath);

// ── Build and run the .NET generic host ──────────────────────────────────────
try
{
    var host = Host.CreateDefaultBuilder(args)
        // Integrate with the Windows Service Control Manager.
        // This handles start/stop/pause signals from services.msc and sc.exe.
        .UseWindowsService(opts =>
        {
            opts.ServiceName = AppConstants.ServiceName;
        })
        // Suppress the default Microsoft console/debug loggers entirely —
        // Serilog is already configured and writing to file via LogConfig.Configure().
        // We do NOT call .UseSerilog() here because we configured Serilog statically
        // above; calling UseSerilog() again would try to reconfigure it and conflict.
        .ConfigureLogging(logging =>
        {
            logging.ClearProviders(); // remove console/debug/eventlog providers
        })
        .ConfigureServices(services =>
        {
            // Register HttpClient for the SelfUpdater (typed client pattern).
            // This also registers SelfUpdater itself as a transient service.
            services.AddHttpClient<SelfUpdater>(client =>
            {
                client.Timeout = TimeSpan.FromMinutes(10);
                client.DefaultRequestHeaders.UserAgent.ParseAdd(
                    $"CapTG-UpdateService/{System.Reflection.Assembly
                        .GetExecutingAssembly().GetName().Version}");
            });

            // PipeServer is a singleton — one instance manages the pipe lifecycle.
            services.AddSingleton(_ => new PipeServer(notifierPath));

            // The main background worker that drives the hourly update loop.
            services.AddHostedService<UpdateBackgroundService>();
        })
        .Build();

    log.Information("Host built — handing control to the SCM.");
    await host.RunAsync();
}
catch (Exception ex)
{
    log.Fatal(ex, "Host terminated unexpectedly.");
    return 1;
}
finally
{
    LogConfig.CloseAndFlush();
}

return 0;