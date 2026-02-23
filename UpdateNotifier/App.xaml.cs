// UpdateNotifier/App.xaml.cs
// Application entry point for the WPF notifier.
// Execution flow:
//   1. Configure Serilog.
//   2. Connect to the service's named pipe and receive the RebootRequired message.
//   3. Build and show the notification window.
//   4. When the user decides, send the response back through the pipe.
//   5. Shut down.
// The window is shown once — snooze timing is managed by the SERVICE, which
// relaunches this executable when the snooze period expires.

using System.Windows;
using System.Windows.Threading;
using Shared.Models;
using UpdateNotifier.Logging;
using UpdateNotifier.Pipes;
using UpdateNotifier.Snooze;
using UpdateNotifier.ViewModels;
using UpdateNotifier.Views;

namespace UpdateNotifier;

/// <summary>
/// WPF Application host for UpdateNotifier.
/// </summary>
public partial class App : Application
{
    // ── Fields ───────────────────────────────────────────────────────────────

    private PipeClient?    _pipeClient;
    private MainViewModel? _viewModel;
    private SnoozeManager? _snoozeManager;

    // The snooze manager must survive across window re-shows within the SAME process
    // run (edge case: this is here for future extensibility; the service normally
    // relaunches the process on each snooze wake-up).

    // ── Application startup ──────────────────────────────────────────────────

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Prevent multiple instances by checking for the pipe — the service only
        // launches one notifier at a time, but belt-and-suspenders is fine.

        // 1. Configure logging first.
        LogConfig.Configure();
        LogConfig.Log.Information("UpdateNotifier: application starting.");

        // Catch any unhandled exceptions on the dispatcher thread.
        DispatcherUnhandledException += (_, ex) =>
        {
            LogConfig.Log.Fatal(ex.Exception, "UpdateNotifier: unhandled dispatcher exception.");
            ex.Handled = true;
            Shutdown(1);
        };

        // 2. Connect to the service pipe and get the update message.
        _pipeClient = new PipeClient();
        PipeMessage? message;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        try
        {
            message = await _pipeClient.ConnectAndReceiveAsync(cts.Token);
        }
        catch (Exception ex)
        {
            LogConfig.Log.Fatal(ex, "UpdateNotifier: failed to receive message from service.");
            Shutdown(1);
            return;
        }

        if (message is null || message.Type != MessageType.RebootRequired)
        {
            LogConfig.Log.Error(
                "UpdateNotifier: unexpected or missing message (type={Type}). Exiting.",
                message?.Type.ToString() ?? "null");
            Shutdown(1);
            return;
        }

        LogConfig.Log.Information(
            "UpdateNotifier: received RebootRequired message. KBs={K} Pkgs={P}",
            message.KbNumbers.Count, message.UpdatedPackages.Count);

        // 3. Build the ViewModel and window.
        _snoozeManager = new SnoozeManager();
        _viewModel     = new MainViewModel(_snoozeManager);
        _viewModel.LoadFromMessage(message);

        // Subscribe to the user's decision BEFORE showing the window.
        _viewModel.UserDecided += async (_, choice) =>
            await HandleUserDecisionAsync(choice);

        var window = new Views.MainWindow(_viewModel);
        MainWindow = window;

        LogConfig.Log.Information("UpdateNotifier: showing notification window.");
        window.ShowDialog(); // blocks until window closes
    }

    // ── User decision handler ────────────────────────────────────────────────

    /// <summary>
    /// Sends the user's snooze/reboot decision back to the service through the pipe.
    /// </summary>
    private async Task HandleUserDecisionAsync(SnoozeOption choice)
    {
        var snoozeMinutes = (int)choice.Duration.TotalMinutes;

        LogConfig.Log.Information(
            "App: user chose '{Label}' — sending SnoozeMinutes={Min} to service.",
            choice.Label, snoozeMinutes);

        if (_pipeClient is not null)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await _pipeClient.SendResponseAsync(snoozeMinutes, cts.Token);
                LogConfig.Log.Information("App: response sent to service.");
            }
            catch (Exception ex)
            {
                LogConfig.Log.Error(ex, "App: failed to send response to service.");
            }
            finally
            {
                _pipeClient.Dispose();
                _pipeClient = null;
            }
        }
    }

    // ── Application exit ─────────────────────────────────────────────────────

    protected override void OnExit(ExitEventArgs e)
    {
        LogConfig.Log.Information("UpdateNotifier: exiting with code {Code}.", e.ApplicationExitCode);
        _pipeClient?.Dispose();
        LogConfig.CloseAndFlush();
        base.OnExit(e);
    }
}