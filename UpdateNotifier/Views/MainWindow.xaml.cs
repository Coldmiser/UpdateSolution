// UpdateNotifier/Views/MainWindow.xaml.cs
// Code-behind for the reboot-notification window.
// Responsibilities:
//   • Prevent the window from being moved (intercept WM_SYSCOMMAND SC_MOVE).
//   • Prevent Alt+F4 close (intercept WM_SYSCOMMAND SC_CLOSE).
//   • Prevent minimise (intercept WM_SYSCOMMAND SC_MINIMIZE).
//   • Re-assert Topmost on every activation so the window stays in front.
//   • Forward the user's decision back through the PipeClient.

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using UpdateNotifier.Logging;
using UpdateNotifier.ViewModels;

namespace UpdateNotifier.Views;

/// <summary>
/// Code-behind for the always-on-top, non-movable reboot notification window.
/// </summary>
public partial class MainWindow : Window
{
    // ── WM_SYSCOMMAND constants ───────────────────────────────────────────────
    private const int WM_SYSCOMMAND = 0x0112;
    private const int SC_MOVE       = 0xF010;
    private const int SC_CLOSE      = 0xF060;
    private const int SC_MINIMIZE   = 0xF020;
    private const int SC_MAXIMIZE   = 0xF030;

    // ── Fields ────────────────────────────────────────────────────────────────

    private readonly MainViewModel _viewModel;

    // ── Constructor ──────────────────────────────────────────────────────────

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;

        // Subscribe to the ViewModel's decision event.
        _viewModel.UserDecided += OnUserDecided;

        // Hook into the Win32 message loop once the window handle is created.
        SourceInitialized += OnSourceInitialized;

        // Re-assert Topmost on every activation.
        Activated += (_, _) =>
        {
            Topmost = true;
            LogConfig.Log.Debug("MainWindow: activated — Topmost re-asserted.");
        };

        LogConfig.Log.Information("MainWindow: constructor complete.");
    }

    // ── Window-level event handlers ──────────────────────────────────────────

    /// <summary>
    /// Hooks the Win32 message loop so we can intercept move/close/minimise commands.
    /// </summary>
    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
        LogConfig.Log.Debug("MainWindow: WndProc hook installed.");
    }

    /// <summary>
    /// Win32 message handler.  Swallows move, close, minimize, and maximize syscommands.
    /// </summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_SYSCOMMAND)
        {
            // Mask off the lower 4 bits (used for drag-resizing details).
            var command = wParam.ToInt32() & 0xFFF0;

            if (command is SC_MOVE or SC_CLOSE or SC_MINIMIZE or SC_MAXIMIZE)
            {
                LogConfig.Log.Debug(
                    "MainWindow: blocked WM_SYSCOMMAND 0x{Command:X4}.", command);
                handled = true;
                return IntPtr.Zero;
            }
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// Raised by the ViewModel when the user presses Confirm.
    /// Initiates the pipe response and closes the window.
    /// </summary>
    private void OnUserDecided(object? sender, Shared.Models.SnoozeOption choice)
    {
        LogConfig.Log.Information(
            "MainWindow: user decided '{Label}'. Closing window.", choice.Label);

        // The App class is listening for this event and will send the pipe response.
        // We just need to close the window.
        // Remove our hook so WndProc no longer blocks WM_CLOSE.
        var hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(hwnd)?.RemoveHook(WndProc);

        DialogResult = true;  // signals App that a clean decision was made
        Close();
    }
}

// ── BooleanToVisibilityConverter singleton ────────────────────────────────────
// Declared here as a static singleton for x:Static reference in XAML.

/// <summary>
/// Standard bool→Visibility converter accessible via x:Static in XAML.
/// </summary>
public sealed class BooleanToVisibilityConverter : System.Windows.Data.IValueConverter
{
    /// <summary>Singleton instance referenced by XAML.</summary>
    public static readonly BooleanToVisibilityConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
        throw new NotSupportedException();
}
