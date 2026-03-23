// Uninstall/Program.cs
// Removes the CapTG Updater installation:
//   - Stops and deletes the Windows service (CapTGUpdateService)
//   - Removes the Task Scheduler watchdog task (\CapTG\CapTG Update Service Watchdog)
//   - Kills any running UpdateNotifier / WatchDog processes
//   - Deletes C:\Program Files\CapTG Updater\
//   - Leaves HKLM\SOFTWARE\CapTG\UpdateService and all its values intact
//   - Appends a "<version>-Removal" tombstone entry with the uninstall date
//
// Must be run elevated (as Administrator) — app.manifest enforces this via UAC.
// All activity is logged to C:\ProgramData\CapTG\Logs\Uninstall-YYYYMMDD.log.

using System.Diagnostics;
using Microsoft.Win32;
using Serilog;
using Shared.Constants;
using Shared.Helpers;

const string ServiceName     = AppConstants.ServiceName;
const string TaskName        = @"\CapTG\CapTG Update Service Watchdog";
const string InstallDir      = @"C:\Program Files\CapTG Updater";
const string RegistryKeyPath = RegistryConstants.RootKeyPath;

// ── Configure Serilog before anything else ────────────────────────────────────
// Read the log directory from the registry (same key the service uses) so log
// files always land in the same folder regardless of custom configuration.
var logDir = RegistryHelper.GetString(RegistryConstants.LogDirectory, AppConstants.DefaultLogDirectory);
Directory.CreateDirectory(logDir);

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Verbose()
    .WriteTo.File(
        path: Path.Combine(logDir, "Uninstall-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30,
        outputTemplate:
            "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

Log.Information("CapTG Updater Uninstaller started.");

// ── Elevation guard ───────────────────────────────────────────────────────────
// app.manifest requests requireAdministrator so Windows blocks unelevated launches
// automatically. This check is a belt-and-suspenders fallback.
if (!IsElevated())
{
    Log.Fatal("Process is not elevated. Uninstall aborted.");
    Log.CloseAndFlush();
    return 1;
}

// ── Confirmation prompt (skipped when /YES is passed on the command line) ─────
var silentMode = args.Contains("/YES", StringComparer.OrdinalIgnoreCase);

if (!silentMode)
{
    Console.WriteLine();
    Console.WriteLine("CapTG Updater — Uninstaller");
    Console.WriteLine("═══════════════════════════");
    Console.WriteLine();
    Console.WriteLine("This will permanently remove:");
    Console.WriteLine($"  Service        : {ServiceName}");
    Console.WriteLine($"  Scheduled task : {TaskName}");
    Console.WriteLine($"  Install folder : {InstallDir}");
    Console.WriteLine();
    Console.Write("Type YES to confirm: ");
    var confirm = Console.ReadLine();
    if (!string.Equals(confirm?.Trim(), "YES", StringComparison.Ordinal))
    {
        Log.Information("Uninstall cancelled by user.");
        Console.WriteLine("Uninstall cancelled.");
        Log.CloseAndFlush();
        return 0;
    }
}

Log.Information("User confirmed uninstall. Proceeding.");

// ── Detect installed version before deleting anything ────────────────────────
var version = ReadInstalledVersion();
Log.Information("Detected installed version: {Version}", version);

// ── Kill running UpdateNotifier and WatchDog processes ────────────────────────
Log.Information("Killing running UpdateNotifier and WatchDog processes.");
foreach (var procName in new[] { "UpdateNotifier", "WatchDog" })
{
    foreach (var proc in System.Diagnostics.Process.GetProcessesByName(procName))
    {
        try
        {
            Log.Debug("Killing {ProcessName} PID {Pid}.", procName, proc.Id);
            proc.Kill(entireProcessTree: true);
            proc.WaitForExit(3000);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not kill {ProcessName} PID {Pid}.", procName, proc.Id);
        }
        finally { proc.Dispose(); }
    }
}

// ── Stop and remove the Windows service ──────────────────────────────────────
Log.Information("Stopping service '{ServiceName}'.", ServiceName);
RunProcess("sc.exe", $@"stop ""{ServiceName}""", ignoreErrors: true);
Thread.Sleep(3000);

Log.Information("Deleting service '{ServiceName}'.", ServiceName);
var (scExit, scOut) = RunProcess("sc.exe", $@"delete ""{ServiceName}""");
if (scExit == 0)
    Log.Information("Service deleted successfully.");
else
    Log.Warning("sc delete exited {ExitCode}: {Output}", scExit, scOut);

// ── Remove the Task Scheduler watchdog task ───────────────────────────────────
Log.Information("Removing scheduled task '{TaskName}'.", TaskName);
var (taskExit, taskOut) = RunProcess("schtasks.exe", $@"/delete /tn ""{TaskName}"" /f");
if (taskExit == 0)
    Log.Information("Scheduled task removed successfully.");
else
    Log.Warning("schtasks delete exited {ExitCode}: {Output}", taskExit, taskOut);

// schtasks.exe has no folder-delete command; use the Schedule.Service COM API via PowerShell.
Log.Information("Removing Task Scheduler folder '\\CapTG'.");
const string psRemoveFolder =
    "$svc = New-Object -ComObject Schedule.Service; " +
    "$svc.Connect(); " +
    "$svc.GetFolder('\\').DeleteFolder('CapTG', 0)";
var (folderExit, folderOut) = RunProcess(
    "powershell.exe",
    "-NoProfile -NonInteractive -Command \"" + psRemoveFolder + "\"",
    ignoreErrors: true);
if (folderExit == 0)
    Log.Information("Task Scheduler folder '\\CapTG' removed.");
else
    Log.Warning("Task Scheduler folder removal exited {ExitCode}: {Output}", folderExit, folderOut);

// ── Delete the install directory ─────────────────────────────────────────────
Log.Information("Removing install directory: {Dir}", InstallDir);
if (Directory.Exists(InstallDir))
{
    try
    {
        Directory.Delete(InstallDir, recursive: true);
        Log.Information("Install directory removed.");
    }
    catch (Exception ex)
    {
        Log.Warning(ex, "Could not fully remove install directory. Scheduling removal on next reboot.");
        ScheduleDirectoryRemoval(InstallDir);
    }
}
else
{
    Log.Warning("Install directory '{Dir}' not found — skipped.", InstallDir);
}

// ── Write removal tombstone (registry key and all existing values are preserved) ──
// Value name : "<version>-Removal"  e.g. "0.26.194.425-Removal"
// Value data : date/time of uninstall (REG_SZ)
var removalValueName = $"{version}-Removal";
var removalDate      = DateTime.Now.ToString("ddd MM/dd/yyyy");

Log.Information(
    "Writing removal tombstone: HKLM\\{Key}\\{ValueName} = {Date}",
    RegistryKeyPath, removalValueName, removalDate);
try
{
    using var key = Registry.LocalMachine.CreateSubKey(RegistryKeyPath, writable: true);
    key?.SetValue(removalValueName, removalDate, RegistryValueKind.String);
    Log.Information("Removal tombstone written.");
}
catch (Exception ex)
{
    Log.Error(ex, "Failed to write removal tombstone.");
}

// ── Remove Windows "Programs and Features" uninstall entry ───────────────────
const string uninstallRegKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\CapTG Updater";
Log.Information("Removing uninstall registry key: HKLM\\{Key}", uninstallRegKey);
try
{
    Registry.LocalMachine.DeleteSubKeyTree(uninstallRegKey, throwOnMissingSubKey: false);
    Log.Information("Uninstall registry key removed.");
}
catch (Exception ex)
{
    Log.Warning(ex, "Could not remove uninstall registry key.");
}

// ── Create and launch self-cleanup batch ─────────────────────────────────────
// Uninstall.exe cannot delete itself while it is running, so we write a batch
// file to C:\Windows\Temp that waits 5 seconds (giving this process time to
// exit), then removes the exe and the install directory, then deletes itself.
var batchPath = Path.Combine(@"C:\Windows\Temp", "CapTG_SelfCleanup.bat");

const string batchTemplate =
    "@echo off\r\n" +
    "timeout /t 5 /nobreak > nul\r\n" +
    "del /f /q \"INSTALL_DIR\\Uninstall.exe\"\r\n" +
    "rd /s /q \"INSTALL_DIR\"\r\n" +
    "del \"%~f0\"\r\n";

var batchContent = batchTemplate.Replace("INSTALL_DIR", InstallDir);

try
{
    File.WriteAllText(batchPath, batchContent, System.Text.Encoding.ASCII);
    Log.Information("Self-cleanup batch written: {BatchPath}", batchPath);

    var cleanup = new System.Diagnostics.Process();
    cleanup.StartInfo = new ProcessStartInfo
    {
        FileName        = "cmd.exe",
        Arguments       = $"/c \"{batchPath}\"",
        UseShellExecute = false,
        CreateNoWindow  = true
    };
    cleanup.Start();
    Log.Information("Self-cleanup batch launched. Exiting.");
}
catch (Exception ex)
{
    Log.Warning(ex, "Could not launch self-cleanup batch.");
}

Log.Information("Uninstall complete. Version={Version} Date={Date}", version, removalDate);
Log.CloseAndFlush();
return 0;

// ═════════════════════════════════════════════════════════════════════════════
// Helper functions
// ═════════════════════════════════════════════════════════════════════════════

static bool IsElevated()
{
    using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
    return new System.Security.Principal.WindowsPrincipal(id)
        .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
}

/// <summary>
/// Reads the installed version string.
/// 1. VersionControl.dat next to this exe (present when deployed alongside service files).
/// 2. ProductVersion embedded in UpdateService.exe in the install directory.
/// 3. "Unknown" if neither is available.
/// </summary>
static string ReadInstalledVersion()
{
    var datPath = Path.Combine(AppContext.BaseDirectory, "VersionControl.dat");
    if (File.Exists(datPath))
    {
        // PowerShell Out-File writes UTF-16 LE with BOM; ReadAllText auto-detects it.
        var text = File.ReadAllText(datPath).Trim('\uFEFF', '\r', '\n', ' ');
        if (!string.IsNullOrWhiteSpace(text))
            return text;
    }

    var exePath = Path.Combine(InstallDir, "UpdateService.exe");
    if (File.Exists(exePath))
    {
        var info = FileVersionInfo.GetVersionInfo(exePath);
        var ver  = info.ProductVersion ?? info.FileVersion;
        if (!string.IsNullOrWhiteSpace(ver))
            return ver.Split('+')[0].Trim();
    }

    return "Unknown";
}

static (int ExitCode, string Output) RunProcess(string exe, string arguments, bool ignoreErrors = false)
{
    using var proc = new System.Diagnostics.Process();
    proc.StartInfo = new ProcessStartInfo
    {
        FileName               = exe,
        Arguments              = arguments,
        RedirectStandardOutput = true,
        RedirectStandardError  = true,
        UseShellExecute        = false,
        CreateNoWindow         = true,
    };
    proc.Start();
    var output = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
    proc.WaitForExit();
    return (proc.ExitCode, output.Trim());
}

/// <summary>
/// Schedules removal of a locked directory on the next reboot via RunOnce.
/// Fallback for files held open by the OS after the service is deleted.
/// </summary>
static void ScheduleDirectoryRemoval(string path)
{
    try
    {
        using var runOnce = Registry.LocalMachine.OpenSubKey(
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce", writable: true);
        runOnce?.SetValue(
            "CapTG_Cleanup",
            $@"cmd.exe /c rd /s /q ""{path}""",
            RegistryValueKind.String);
        Log.Information("Scheduled directory removal on next reboot via RunOnce.");
    }
    catch (Exception ex)
    {
        Log.Warning(ex, "Could not schedule directory removal via RunOnce.");
    }
}
