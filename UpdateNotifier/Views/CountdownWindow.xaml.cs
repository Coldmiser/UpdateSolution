// UpdateNotifier/Views/CountdownWindow.xaml.cs
// Code-behind for the pre-reboot countdown warning.
// Uses ONLY pure WPF events + DispatcherTimer — no HwndSource.AddHook or
// P/Invoke (see CLAUDE.md §4: Win32 subclassing crashes WPF on .NET 10).

using System.Windows;
using System.Windows.Threading;
using UpdateNotifier.Logging;

namespace UpdateNotifier.Views;

/// <summary>
/// Small always-on-top window shown minutes before a scheduled reboot.
/// Counts down to the reboot time; the single button requests a 5-minute
/// delay. Closing the window (or letting the countdown expire) means the
/// reboot proceeds with no further interaction.
/// </summary>
public partial class CountdownWindow : Window
{
    // ── Fields ───────────────────────────────────────────────────────────────

    private readonly DispatcherTimer _timer;
    private readonly DateTime        _rebootAtLocal;

    /// <summary>True when the user clicked "Delay 5 Minutes".</summary>
    public bool DelayRequested { get; private set; }

    // ── Constructor ──────────────────────────────────────────────────────────

    public CountdownWindow(DateTime rebootAtLocal)
    {
        InitializeComponent();
        _rebootAtLocal = rebootAtLocal;

        RebootTimeRun.Text = string.Format(
            "Your computer will restart at {0:h:mm tt} to complete updates.",
            rebootAtLocal);

        // Re-assert topmost whenever the window regains focus (pure WPF event).
        Activated += (_, _) => Topmost = true;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, _) => UpdateCountdown();
        _timer.Start();
        UpdateCountdown();

        Closed += (_, _) => _timer.Stop();

        LogConfig.Log.Information(
            "CountdownWindow: shown — reboot scheduled for {At}.", rebootAtLocal);
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Refreshes the countdown text; auto-closes when the reboot time arrives.
    /// </summary>
    private void UpdateCountdown()
    {
        var remaining = _rebootAtLocal - DateTime.Now;

        if (remaining <= TimeSpan.Zero)
        {
            LogConfig.Log.Information("CountdownWindow: countdown expired — closing.");
            _timer.Stop();
            Close();
            return;
        }

        CountdownText.Text = string.Format(
            "{0}:{1:D2}", (int)remaining.TotalMinutes, remaining.Seconds);
    }

    /// <summary>User clicked "Delay 5 Minutes".</summary>
    private void DelayButton_Click(object sender, RoutedEventArgs e)
    {
        LogConfig.Log.Information("CountdownWindow: user requested a 5-minute delay.");
        DelayRequested = true;
        _timer.Stop();
        Close();
    }
}
