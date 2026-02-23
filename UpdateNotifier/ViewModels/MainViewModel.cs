// UpdateNotifier/ViewModels/MainViewModel.cs
// MVVM ViewModel for the reboot-notification window.
// Manages the snooze options list, selected option,
// update summary text, and the command executed when the user confirms.

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Shared.Models;
using UpdateNotifier.Logging;
using UpdateNotifier.Snooze;

namespace UpdateNotifier.ViewModels;

/// <summary>
/// ViewModel bound to <see cref="Views.MainWindow"/>.
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged
{
    // ── Fields ───────────────────────────────────────────────────────────────

    private readonly SnoozeManager _snoozeManager;
    private SnoozeOption?          _selectedOption;
    private string                 _updateSummary  = string.Empty;
    private string                 _kbList         = string.Empty;
    private string                 _packageList    = string.Empty;

    // ── Events ────────────────────────────────────────────────────────────────

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised when the user has made a final choice (snooze or reboot).</summary>
    public event EventHandler<SnoozeOption>? UserDecided;

    // ── Constructor ──────────────────────────────────────────────────────────

    public MainViewModel(SnoozeManager snoozeManager)
    {
        _snoozeManager = snoozeManager;
        RefreshOptions();

        // Default selection = "Reboot Now" (last / zero-duration option).
        SelectedOption = AvailableOptions.LastOrDefault();

        ConfirmCommand = new RelayCommand(OnConfirm);
    }

    // ── Bindable properties ──────────────────────────────────────────────────

    /// <summary>Options displayed in the combo-box.</summary>
    public ObservableCollection<SnoozeOption> AvailableOptions { get; } = [];

    /// <summary>Currently highlighted option in the combo-box.</summary>
    public SnoozeOption? SelectedOption
    {
        get => _selectedOption;
        set { _selectedOption = value; OnPropertyChanged(); }
    }

    /// <summary>Paragraph describing what was updated this cycle.</summary>
    public string UpdateSummary
    {
        get => _updateSummary;
        set { _updateSummary = value; OnPropertyChanged(); }
    }

    /// <summary>Comma-separated KB numbers, or empty string if none.</summary>
    public string KbList
    {
        get => _kbList;
        set { _kbList = value; OnPropertyChanged(); OnPropertyChanged(nameof(KbListVisible)); }
    }

    /// <summary>Comma-separated winget package IDs, or empty string if none.</summary>
    public string PackageList
    {
        get => _packageList;
        set { _packageList = value; OnPropertyChanged(); OnPropertyChanged(nameof(PackageListVisible)); }
    }

    /// <summary>Show the KB section only when there are KB numbers to display.</summary>
    public bool KbListVisible => !string.IsNullOrWhiteSpace(_kbList);

    /// <summary>Show the package section only when there are packages to display.</summary>
    public bool PackageListVisible => !string.IsNullOrWhiteSpace(_packageList);

    /// <summary>Bound to the Confirm button.</summary>
    public ICommand ConfirmCommand { get; }

    // ── Data loading ─────────────────────────────────────────────────────────

    /// <summary>
    /// Populates the view model from the <see cref="PipeMessage"/> sent by the service.
    /// </summary>
    public void LoadFromMessage(PipeMessage message)
    {
        LogConfig.Log.Information(
            "MainViewModel: loading message. KBs={KB} Pkgs={Pkg}",
            message.KbNumbers.Count, message.UpdatedPackages.Count);

        UpdateSummary = message.UpdateSummary;
        KbList        = message.KbNumbers.Count    > 0 ? string.Join(", ", message.KbNumbers)    : string.Empty;
        PackageList   = message.UpdatedPackages.Count > 0 ? string.Join(", ", message.UpdatedPackages) : string.Empty;
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    /// <summary>Executed when the user presses the Confirm button.</summary>
    private void OnConfirm()
    {
        if (SelectedOption is null)
        {
            LogConfig.Log.Warning("MainViewModel: Confirm called with no selected option.");
            return;
        }

        LogConfig.Log.Information(
            "MainViewModel: user confirmed choice: '{Label}' ({Duration}).",
            SelectedOption.Label, SelectedOption.Duration);

        if (SelectedOption.Duration > TimeSpan.Zero)
        {
            // User chose a snooze tier — record it so the longest is removed next time.
            _snoozeManager.RecordSnooze(SelectedOption.Duration);
        }

        UserDecided?.Invoke(this, SelectedOption);
    }

    /// <summary>
    /// Re-reads available options from the snooze manager and updates the collection.
    /// Call after each snooze to refresh the combo-box.
    /// </summary>
    public void RefreshOptions()
    {
        AvailableOptions.Clear();
        foreach (var opt in _snoozeManager.AvailableOptions)
            AvailableOptions.Add(opt);

        // Ensure "Reboot Now" is pre-selected each time the window opens.
        SelectedOption = AvailableOptions.LastOrDefault();

        LogConfig.Log.Debug(
            "MainViewModel: options refreshed. Count={Count}", AvailableOptions.Count);
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

// ── Minimal ICommand implementation ──────────────────────────────────────────

/// <summary>
/// Lightweight ICommand that delegates execute/canExecute to lambdas.
/// </summary>
file sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;
    public void Execute(object? parameter)     => execute();
    public event EventHandler? CanExecuteChanged
    {
        add    => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }
}
