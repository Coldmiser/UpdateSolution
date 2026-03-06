// WatchDog/Program.cs
// Ensures the CapTG Update Service stays running via a scheduled task.
//
// Modes:
//   WatchDog.exe --install    Register the hourly Task Scheduler task (run elevated).
//   WatchDog.exe --uninstall  Remove the scheduled task (run elevated).
//   WatchDog.exe --run        Check the service and start it if stopped (called by the task).

using System.Diagnostics;
using System.ServiceProcess;

const string TaskName = @"\CapTG\CapTG Update Service Watchdog";
const string ServiceName = Shared.Constants.AppConstants.ServiceName;
const string LogPath     = @"C:\ProgramData\CapTG\Logs\watchdog.txt";

var mode = args.Length > 0 ? args[0].ToLowerInvariant() : "--run";

switch (mode)
{
    case "--install":   Install();   break;
    case "--uninstall": Uninstall(); break;
    default:            Run();       break;
}

// ── Logging ───────────────────────────────────────────────────────────────────

static void Log(string message)
{
    Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
    File.AppendAllText(LogPath,
        $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
}

static (int ExitCode, string Output) RunProcess(string exe, string arguments)
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

// ── --run: check service and start if needed ──────────────────────────────────

static void Run()
{
    try
    {
        using var sc = new ServiceController(ServiceName);
        var status = sc.Status;
        if (status == ServiceControllerStatus.Running)
        {
            Log($"Service '{ServiceName}' is already running.");
            return;
        }

        Log($"Service '{ServiceName}' is {status}. Starting...");
        sc.Start();
        sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
        Log($"Service '{ServiceName}' started successfully.");
    }
    catch (Exception ex)
    {
        Log($"ERROR in Run: {ex.Message}");
        Environment.Exit(1);
    }
}

// ── --install: register the scheduled task via schtasks.exe ──────────────────

static void Install()
{
    var exePath = Environment.ProcessPath
        ?? throw new InvalidOperationException("Cannot determine executable path.");

    // schtasks /create supports subfolders in the task name — the folder is created automatically.
    // /sc HOURLY /mo 1  = repeat every 1 hour
    // /ru SYSTEM        = run as SYSTEM
    // /rl HIGHEST       = highest privileges
    // /f                = force overwrite if already exists
    var args = string.Join(" ",
        "/create",
        $"/tn \"{TaskName}\"",
        $"/tr \"\\\"{exePath}\\\" --run\"",
        "/sc HOURLY /mo 1",
        "/ru SYSTEM",
        "/rl HIGHEST",
        "/f");

    var (exitCode, output) = RunProcess("schtasks.exe", args);

    if (exitCode == 0)
    {
        Log($"Registered task: {TaskName}");
        Log($"Task action: \"{exePath}\" --run");
        Console.WriteLine($"Registered: {TaskName}");
    }
    else
    {
        Log($"ERROR registering task (exit {exitCode}): {output}");
        Console.WriteLine($"Failed to register task: {output}");
        Environment.Exit(exitCode);
    }
}

// ── --uninstall: remove the scheduled task ────────────────────────────────────

static void Uninstall()
{
    var args = $"/delete /tn \"{TaskName}\" /f";
    var (exitCode, output) = RunProcess("schtasks.exe", args);

    if (exitCode == 0)
    {
        Log($"Removed task: {TaskName}");
        Console.WriteLine($"Removed task '{TaskName}'.");
    }
    else
    {
        Log($"Could not remove task (exit {exitCode}): {output}");
        Console.WriteLine($"Could not remove task: {output}");
    }
}
