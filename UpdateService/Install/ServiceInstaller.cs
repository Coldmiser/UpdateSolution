// UpdateService/Install/ServiceInstaller.cs
// Handles --install and --uninstall command-line flags.
// Uses sc.exe to register the service with:
//   • A description
//   • Auto-start with delayed start
//   • Three-tier failure recovery (restart immediately twice, then every 5 min)
// Also seeds the registry with default configuration values on first install.

using System.Diagnostics;
using System.Reflection;
using Shared.Constants;
using Shared.Helpers;
using UpdateService.Logging;

namespace UpdateService.Install;

/// <summary>
/// Installs or removes the Windows service and its registry configuration.
/// </summary>
public static class ServiceInstaller
{
    // ── Public entry points ──────────────────────────────────────────────────

    /// <summary>
    /// Registers the service with the Windows Service Control Manager,
    /// sets its description, configures failure-recovery actions, and
    /// seeds default registry values.
    /// If the service already exists (upgrade scenario) the running instance is
    /// stopped, the binary path is updated via <c>sc config</c>, and the service
    /// is restarted — no delete/recreate required.
    /// </summary>
    public static void Install()
    {
        var exePath = GetExecutablePath();
        LogConfig.ServiceLog.Information("Installing service from: {ExePath}", exePath);

        if (ServiceExists())
        {
            // ── Upgrade path ─────────────────────────────────────────────────
            LogConfig.ServiceLog.Information(
                "Service '{Name}' already exists — stopping for upgrade.", AppConstants.ServiceName);

            // Stop the running instance (ignore errors — may already be stopped).
            RunSc($@"stop ""{AppConstants.ServiceName}""", ignoreErrors: true);
            WaitForServiceStop();

            // Update the binary path (and re-assert start type / account in one call).
            RunSc($@"config ""{AppConstants.ServiceName}"" "
                + $@"binPath= ""{exePath}"" "
                + @"start= delayed-auto "
                + @"obj= LocalSystem");

            Console.WriteLine($"Service '{AppConstants.ServiceDisplayName}' updated.");
        }
        else
        {
            // ── Fresh install path ────────────────────────────────────────────
            RunSc($@"create ""{AppConstants.ServiceName}"" "
                + $@"binPath= ""{exePath}"" "
                + $@"DisplayName= ""{AppConstants.ServiceDisplayName}"" "
                + @"start= delayed-auto "
                + @"obj= LocalSystem");

            Console.WriteLine($"Service '{AppConstants.ServiceDisplayName}' created.");
        }

        // These sc operations are idempotent — run them on both fresh install and upgrade.

        // Set the human-readable description shown in Services.msc.
        RunSc($@"description ""{AppConstants.ServiceName}"" "
            + $@"""{AppConstants.ServiceDescription}""");

        // Configure failure recovery:
        //   • 1st failure  → restart after  0 s
        //   • 2nd failure  → restart after 60 s
        //   • 3rd+ failure → restart after 300 s
        //   Reset the failure counter after 86400 s (24 h) of successful uptime.
        RunSc($@"failure ""{AppConstants.ServiceName}"" "
            + @"reset= 86400 "
            + @"actions= restart/0/restart/60000/restart/300000");

        // Seed registry defaults — use SetStringIfAbsent so admin-customised
        // values are preserved across upgrades.
        RegistryHelper.EnsureKeyExists();
        RegistryHelper.SetStringIfAbsent(RegistryConstants.LogDirectory,   AppConstants.DefaultLogDirectory);
        RegistryHelper.SetStringIfAbsent(RegistryConstants.VersionFileUrl, AppConstants.DefaultVersionFileUrl);
        RegistryHelper.SetStringIfAbsent(RegistryConstants.InstallerUrl,   AppConstants.DefaultInstallerUrl);
        RegistryHelper.SetStringIfAbsent(RegistryConstants.WingetExclusions, "Syncthing.Syncthing");

        // Start (or restart) the service.
        RunSc($@"start ""{AppConstants.ServiceName}""");

        Console.WriteLine($"Service '{AppConstants.ServiceDisplayName}' started.");
    }

    /// <summary>
    /// Stops and removes the service from the Windows Service Control Manager.
    /// Registry values are intentionally left behind so that configuration
    /// survives a reinstall.
    /// </summary>
    public static void Uninstall()
    {
        LogConfig.ServiceLog.Information("Uninstalling service: {Name}", AppConstants.ServiceName);

        // Stop first (ignore errors — service may already be stopped).
        RunSc($@"stop ""{AppConstants.ServiceName}""", ignoreErrors: true);

        // Give the SCM a moment to process the stop.
        Thread.Sleep(2000);

        RunSc($@"delete ""{AppConstants.ServiceName}""");

        Console.WriteLine($"Service '{AppConstants.ServiceDisplayName}' uninstalled.");
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Returns true if the named service is already registered with the SCM.
    /// Uses <c>sc query</c> exit code: 0 = exists, 1060 = not found.
    /// </summary>
    private static bool ServiceExists()
    {
        using var proc = new System.Diagnostics.Process();
        proc.StartInfo = new ProcessStartInfo
        {
            FileName               = "sc.exe",
            Arguments              = $@"query ""{AppConstants.ServiceName}""",
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true
        };

        proc.Start();
        proc.StandardOutput.ReadToEnd(); // must drain to avoid deadlock
        proc.StandardError.ReadToEnd();
        proc.WaitForExit();

        return proc.ExitCode == 0;
    }

    /// <summary>
    /// Polls <c>sc query</c> until the service reports STOPPED or the timeout elapses.
    /// Gives the SCM time to finish stopping before we replace the executable on disk.
    /// </summary>
    private static void WaitForServiceStop(int timeoutSeconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);

        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(1000);

            using var proc = new System.Diagnostics.Process();
            proc.StartInfo = new ProcessStartInfo
            {
                FileName               = "sc.exe",
                Arguments              = $@"query ""{AppConstants.ServiceName}""",
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true
            };

            proc.Start();
            var output = proc.StandardOutput.ReadToEnd();
            proc.StandardError.ReadToEnd();
            proc.WaitForExit();

            if (output.Contains("STOPPED", StringComparison.OrdinalIgnoreCase))
            {
                LogConfig.ServiceLog.Information("WaitForServiceStop: service is stopped.");
                return;
            }
        }

        LogConfig.ServiceLog.Warning(
            "WaitForServiceStop: service did not stop within {Timeout}s — proceeding anyway.",
            timeoutSeconds);
    }

    /// <summary>
    /// Runs sc.exe with the given arguments and throws on non-zero exit
    /// unless <paramref name="ignoreErrors"/> is true.
    /// </summary>
    private static void RunSc(string arguments, bool ignoreErrors = false)
    {
        LogConfig.ServiceLog.Debug("sc.exe {Arguments}", arguments);

        using var proc = new System.Diagnostics.Process();
        proc.StartInfo = new ProcessStartInfo
        {
            FileName               = "sc.exe",
            Arguments              = arguments,
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true
        };

        proc.Start();
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();

        if (!string.IsNullOrWhiteSpace(stdout))
            LogConfig.ServiceLog.Debug("sc.exe stdout: {Output}", stdout.Trim());

        if (!string.IsNullOrWhiteSpace(stderr))
            LogConfig.ServiceLog.Warning("sc.exe stderr: {Error}", stderr.Trim());

        if (proc.ExitCode != 0 && !ignoreErrors)
        {
            var msg = $"sc.exe returned exit code {proc.ExitCode} for: sc {arguments}";
            LogConfig.ServiceLog.Error(msg);
            throw new InvalidOperationException(msg);
        }
    }

    /// <summary>
    /// Returns the full path to the currently running executable.
    /// Works correctly for single-file published binaries.
    /// </summary>
    private static string GetExecutablePath() =>
        System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
            ?? Assembly.GetExecutingAssembly().Location;
}