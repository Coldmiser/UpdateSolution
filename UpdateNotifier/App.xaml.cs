// UpdateNotifier/App.xaml.cs

using System.IO;
using System.Windows;
using System.Windows.Threading;
using Shared.Models;
using UpdateNotifier.Logging;
using UpdateNotifier.Pipes;
using UpdateNotifier.Snooze;
using UpdateNotifier.ViewModels;
using UpdateNotifier.Views;

namespace UpdateNotifier;

public partial class App : Application
{
    // Hardcoded emergency log — written BEFORE Serilog is configured.
    // This file will always be created on startup so we know the exe ran at all.
/* EMERGENCY LOG
    private static readonly string EmergencyLog =
        Path.Combine(@"C:\ProgramData\CapTG\Logs", "UpdateNotifier-Emergency.txt");
*/
    private PipeClient?    _pipeClient;
    private MainViewModel? _viewModel;
    private SnoozeManager? _snoozeManager;

    // ── Static constructor — fires before ANYTHING else in the class ──────────
/* EMERGENCY LOG
    static App()
    {
        // Catch exceptions on ANY thread, even before OnStartup.
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
        {
            WriteEmergency($"AppDomain.UnhandledException: {ex.ExceptionObject}");
        };
    }
*/
    // ── Application startup ───────────────────────────────────────────────────
    protected override async void OnStartup(StartupEventArgs e)
    {
//* EMERGENCY LOG        WriteEmergency("OnStartup: entered.");

        try
        {
            base.OnStartup(e);
//* EMERGENCY LOG            WriteEmergency("OnStartup: base.OnStartup completed.");

            // Configure Serilog.
            LogConfig.Configure();
//* EMERGENCY LOG            WriteEmergency("OnStartup: LogConfig.Configure() completed.");
            LogConfig.Log.Information("UpdateNotifier: application starting.");

            // Catch dispatcher-thread exceptions.
            DispatcherUnhandledException += (_, ex) =>
            {
//* EMERGENCY LOG                WriteEmergency($"DispatcherUnhandledException: {ex.Exception}");
                LogConfig.Log.Fatal(ex.Exception, "Unhandled dispatcher exception.");
                ex.Handled = true;
                Shutdown(1);
            };

            // Merge args from both sources.
            var allArgs = e.Args
                .Concat(Environment.GetCommandLineArgs())
                .Select(a => a.Trim())
                .ToArray();

//* EMERGENCY LOG            WriteEmergency($"OnStartup: args = [{string.Join(", ", allArgs)}]");
            LogConfig.Log.Information("UpdateNotifier: args={Args}", string.Join(", ", allArgs));

            var isTestMode = allArgs.Any(a =>
                a.Equals("--test", StringComparison.OrdinalIgnoreCase));

            if (isTestMode)
            {
 //* EMERGENCY LOG               WriteEmergency("OnStartup: entering test mode.");
                await RunTestModeAsync();
            }
            else
            {
//* EMERGENCY LOG                WriteEmergency("OnStartup: entering normal mode.");
                await RunNormalModeAsync();
            }
        }
        catch (Exception ex)
		{
			try { LogConfig.Log.Fatal(ex, "OnStartup fatal exception."); } catch { }
			Shutdown(1);
		}
    }

    // ── Test mode ─────────────────────────────────────────────────────────────
    private async Task RunTestModeAsync()
    {
//* EMERGENCY LOG        WriteEmergency("RunTestModeAsync: building mock message.");
        LogConfig.Log.Information("UpdateNotifier: TEST MODE.");

        var mockMessage = new PipeMessage
        {
            Type            = MessageType.RebootRequired,
            UpdateSummary   = "3 update(s) applied successfully.",
            KbNumbers       = [
                "2025-03 Cumulative Update for Windows 11 Version 24H2 (KB5034441)",
                "2025-03 Cumulative Update for .NET Framework 3.5 and 4.8.1 (KB5032189)",
                "Windows Malicious Software Removal Tool x64 (KB890830)"
            ],
            UpdatedPackages = ["Visual Studio Code", "Notepad++", "Google Chrome"],
            Timestamp       = DateTime.UtcNow
        };

        _snoozeManager = new SnoozeManager();
        _viewModel     = new MainViewModel(_snoozeManager);
        _viewModel.LoadFromMessage(mockMessage);

        _viewModel.UserDecided += async (_, choice) =>
        {
            LogConfig.Log.Information("TEST MODE: user chose '{Label}'.", choice.Label);
            await Task.Delay(300);
            Shutdown(0);
        };

//* EMERGENCY LOG        WriteEmergency("RunTestModeAsync: creating MainWindow.");
        LogConfig.Log.Information("UpdateNotifier: creating window.");

        var testWindow = new Views.MainWindow(_viewModel);
        MainWindow = testWindow;

//* EMERGENCY LOG        WriteEmergency("RunTestModeAsync: calling ShowDialog.");
        LogConfig.Log.Information("UpdateNotifier: showing window.");

        testWindow.ShowDialog();

//* EMERGENCY LOG        WriteEmergency("RunTestModeAsync: ShowDialog returned.");
    }

    // ── Normal mode ───────────────────────────────────────────────────────────
    private async Task RunNormalModeAsync()
    {
        _pipeClient = new PipeClient();
        PipeMessage? message;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        try
        {
            message = await _pipeClient.ConnectAndReceiveAsync(cts.Token);
        }
        catch (Exception ex)
        {
            LogConfig.Log.Fatal(ex, "Failed to receive message from service.");
            Shutdown(1);
            return;
        }

        if (message is null || message.Type != MessageType.RebootRequired)
        {
            LogConfig.Log.Error("Unexpected message type={T}.", message?.Type.ToString() ?? "null");
            Shutdown(1);
            return;
        }

        _snoozeManager = new SnoozeManager(message.SnoozeCount);
        _viewModel     = new MainViewModel(_snoozeManager);
        _viewModel.LoadFromMessage(message);
        _viewModel.UserDecided += async (_, choice) => await HandleUserDecisionAsync(choice);

        var window = new Views.MainWindow(_viewModel);
        MainWindow = window;
        window.ShowDialog();
    }

    // ── User decision handler ─────────────────────────────────────────────────
    private async Task HandleUserDecisionAsync(SnoozeOption choice)
    {
        var snoozeMinutes = (int)choice.Duration.TotalMinutes;
        LogConfig.Log.Information("App: user chose '{Label}' ({Min} min).", choice.Label, snoozeMinutes);

        if (_pipeClient is not null)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await _pipeClient.SendResponseAsync(snoozeMinutes, cts.Token);
            }
            catch (Exception ex)
            {
                LogConfig.Log.Error(ex, "Failed to send response to service.");
            }
            finally
            {
                _pipeClient.Dispose();
                _pipeClient = null;
            }
        }
    }

    // ── Application exit ──────────────────────────────────────────────────────
    protected override void OnExit(ExitEventArgs e)
    {
//* EMERGENCY LOG        WriteEmergency($"OnExit: code={e.ApplicationExitCode}.");
        LogConfig.Log.Information("UpdateNotifier: exiting with code {Code}.", e.ApplicationExitCode);
        _pipeClient?.Dispose();
        LogConfig.CloseAndFlush();
        base.OnExit(e);
    }

    // ── Emergency log writer ──────────────────────────────────────────────────
    /// <summary>
    /// Writes a timestamped line to the emergency log file.
    /// Uses only BCL types — no Serilog dependency — so it works even if
    /// Serilog fails to initialise.
    /// </summary>
/* EMERGENCY LOG	
    private static void WriteEmergency(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(EmergencyLog)!);
            File.AppendAllText(EmergencyLog,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} | {message}{Environment.NewLine}");
        }
        catch
        {
            // Last-resort: if even file I/O fails, nothing we can do.
        }
    }
*/
}
