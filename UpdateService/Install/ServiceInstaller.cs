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
    /// </summary>
    public static void Install()
    {
        var exePath = GetExecutablePath();
        LogConfig.ServiceLog.Information("Installing service from: {ExePath}", exePath);

        // 1. Create the service (binPath uses quotes to handle spaces in path).
        RunSc($@"create ""{AppConstants.ServiceName}"" "
            + $@"binPath= ""{exePath}"" "
            + $@"DisplayName= ""{AppConstants.ServiceDisplayName}"" "
            + @"start= delayed-auto "
            + @"obj= LocalSystem");

        // 2. Set the human-readable description shown in Services.msc.
        RunSc($@"description ""{AppConstants.ServiceName}"" "
            + $@"""{AppConstants.ServiceDescription}""");

        // 3. Configure failure recovery:
        //    • 1st failure  → restart after  0 s
        //    • 2nd failure  → restart after 60 s
        //    • 3rd+ failure → restart after 300 s
        //    Reset the failure counter after 86400 s (24 h) of successful uptime.
        RunSc($@"failure ""{AppConstants.ServiceName}"" "
            + @"reset= 86400 "
            + @"actions= restart/0/restart/60000/restart/300000");

        // 4. Seed the registry with defaults so that RegistryHelper reads succeed
        //    even before an admin has customised anything.
        RegistryHelper.EnsureKeyExists();
        RegistryHelper.SetString(RegistryConstants.LogDirectory,      AppConstants.DefaultLogDirectory);
        RegistryHelper.SetString(RegistryConstants.VersionFileUrl,    AppConstants.DefaultVersionFileUrl);
        RegistryHelper.SetString(RegistryConstants.UpdateZipUrlTemplate, AppConstants.DefaultUpdateZipUrlTemplate);

        // 5. Start the service immediately.
        RunSc($@"start ""{AppConstants.ServiceName}""");

        Console.WriteLine($"Service '{AppConstants.ServiceDisplayName}' installed and started.");
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