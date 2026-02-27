// UpdateNotifier/Views/MainWindow.xaml.cs
// Code-behind for the reboot-notification window.
// Responsibilities:
//   • Load company logo from a file next to the exe (avoids WPF pack URI crash)
//   • Prevent the window from being moved   — via LocationChanged event
//   • Prevent the window from being closed  — via Closing event
//   • Prevent the window from being minimised — via StateChanged event
//   • Re-assert Topmost on every activation
//   • Forward the user's decision back through the PipeClient

using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using UpdateNotifier.Logging;
using UpdateNotifier.ViewModels;

namespace UpdateNotifier.Views;

/// <summary>
/// Always-on-top, non-movable reboot notification window.
/// </summary>
public partial class MainWindow : Window
{
    // ── Fields ────────────────────────────────────────────────────────────────

    private readonly MainViewModel _viewModel;

    // Set to true once the user confirms a choice — allows the window to close.
    private bool _decisionMade = false;

    // Locked screen position — restored if the user tries to drag the window.
    private double _lockedLeft;
    private double _lockedTop;

    // ── Constructor ──────────────────────────────────────────────────────────

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;

        // Subscribe to ViewModel decision event.
        _viewModel.UserDecided += OnUserDecided;

        // ── Prevent moving ────────────────────────────────────────────────────
        Loaded += (_, _) =>
        {
            // Load the logo here — after InitializeComponent — so WPF's
            // internal infrastructure is fully ready before we touch images.
            LoadLogo();

            _lockedLeft = Left;
            _lockedTop  = Top;
            LogConfig.Log.Debug(
                "MainWindow: position locked at ({Left}, {Top}).", _lockedLeft, _lockedTop);
        };

        LocationChanged += (_, _) =>
        {
            if (_lockedLeft == 0 && _lockedTop == 0) return;
            if (Math.Abs(Left - _lockedLeft) > 0.5 || Math.Abs(Top - _lockedTop) > 0.5)
            {
                Left = _lockedLeft;
                Top  = _lockedTop;
                LogConfig.Log.Debug("MainWindow: move attempt blocked.");
            }
        };

        // ── Prevent minimising / maximising ──────────────────────────────────
        StateChanged += (_, _) =>
        {
            if (WindowState != WindowState.Normal)
            {
                WindowState = WindowState.Normal;
                LogConfig.Log.Debug("MainWindow: state change blocked.");
            }
        };

        // ── Prevent closing before a decision ────────────────────────────────
        Closing += (_, cancelArgs) =>
        {
            if (!_decisionMade)
            {
                cancelArgs.Cancel = true;
                LogConfig.Log.Debug("MainWindow: close attempt blocked.");
            }
        };

        // ── Stay on top ───────────────────────────────────────────────────────
        Activated += (_, _) =>
        {
            Topmost = true;
            LogConfig.Log.Debug("MainWindow: Topmost re-asserted.");
        };

        LogConfig.Log.Information("MainWindow: constructor complete.");
    }

    // ── Logo loading ──────────────────────────────────────────────────────────

    /// <summary>
    /// Loads CompanyLogo.png from the same directory as the executable.
    /// Loading from disk avoids WPF's pack URI infrastructure entirely,
    /// which prevents the internal SetWindowLongPtr / HwndSubclass crash
    /// that occurs when pack URIs are resolved during early WPF startup.
    ///
    /// To deploy the logo: place CompanyLogo.png in the same folder as
    /// UpdateNotifier.exe. The Build-And-Publish script handles this automatically.
    /// </summary>
    private void LoadLogo()
    {
        try
        {
            // Find the exe's own directory — works for both debug and published builds.
            var exeDir   = AppContext.BaseDirectory;
            var logoPath = Path.Combine(exeDir, "CompanyLogo.png");

            if (!File.Exists(logoPath))
            {
                LogConfig.Log.Warning(
                    "MainWindow: CompanyLogo.png not found at {Path}. " +
                    "Copy the file next to UpdateNotifier.exe.", logoPath);
                return;
            }

            // Load with OnLoad cache so the file handle is released immediately.
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource      = new Uri(logoPath, UriKind.Absolute);
            bitmap.CacheOption    = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze(); // make it thread-safe and improve render performance

            CompanyLogoImage.Source = bitmap;
            LogConfig.Log.Information("MainWindow: logo loaded from {Path}.", logoPath);
        }
        catch (Exception ex)
        {
            // Logo failure must never prevent the notification window from showing.
            LogConfig.Log.Warning(ex, "MainWindow: logo load failed — continuing without logo.");
        }
    }

    // ── ViewModel event handler ───────────────────────────────────────────────

    /// <summary>
    /// Raised by the ViewModel when the user presses Confirm.
    /// </summary>
    private void OnUserDecided(object? sender, Shared.Models.SnoozeOption choice)
    {
        LogConfig.Log.Information(
            "MainWindow: user decided '{Label}'. Closing window.", choice.Label);

        _decisionMade = true;
        DialogResult  = true;
        Close();
    }
}