namespace Optimus.Shell;

using System;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Threading.Tasks;
using Optimus.Shell.Services;
using Optimus.Shell.ViewModels;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "WPF lifetime disposes owned services when the window closes")]
public partial class MainWindow : Window
{
    public WidgetViewModel ViewModel { get; }
    public AgentCapacityViewModel CapacityViewModel { get; }
    private readonly TrayService _trayService;
    private readonly HotkeyService _companionHotkeys;
    private bool _allowClose;
    private DateTime _lastVoiceFocus = DateTime.MinValue;

    private static readonly string PositionFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Optimus", "capsule-position.json");

    public MainWindow(WidgetViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        CapacityViewModel = new AgentCapacityViewModel();
        AgentCapacity.DataContext = CapacityViewModel;
        DataContext = ViewModel;
        ViewModel.IsExpanded = ViewModel.Preferences.StartExpanded;

        _trayService = new TrayService();
        _trayService.ShowHideRequested += OnTrayShowHide;
        _trayService.ToggleListeningRequested += OnToggleListening;
        _trayService.QuitRequested += OnTrayQuit;
        _companionHotkeys = new HotkeyService(ViewModel.Preferences);
        _companionHotkeys.Pressed += OnCompanionHotkey;
        _companionHotkeys.RegistrationFailed += (_, message) => ViewModel.StatusLine = message;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;

        Settings.LoadSettings(ViewModel.Preferences, CapacityViewModel);
        Settings.Closed += (_, _) => CloseSettings();
        Settings.SettingsChanged += OnSettingsChanged;

        Loaded += OnLoaded;
        LocationChanged += OnLocationChanged;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        _companionHotkeys.Attach(this);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!RestorePosition())
        {
            // Default: bottom-right corner with margin
            var workArea = SystemParameters.WorkArea;
            Left = workArea.Right - ActualWidth - 24;
            Top = workArea.Bottom - ActualHeight - 24;
        }

        ClampToMonitor();
    }

    private void OnWindowMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            DragMove();
            SavePosition();
        }
    }

    private void OnLocationChanged(object? sender, EventArgs e)
    {
        // No-op during drag (position saved on mouse-up in OnWindowMouseDown).
        // This handles programmatic moves and ensures clamp on size changes.
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        Dispatcher.InvokeAsync(ClampToMonitor);
    }

    /// <summary>
    /// Clamps the window so it stays fully within the nearest monitor's work area.
    /// </summary>
    private void ClampToMonitor()
    {
        var workArea = SystemParameters.WorkArea;

        // Use the window center to find which monitor we belong to
        double cx = Left + ActualWidth / 2;
        double cy = Top + ActualHeight / 2;

        // Find the monitor containing the center point (WPF provides WorkArea for primary only,
        // so we use the primary work area as a reasonable clamp boundary)
        double newLeft = Math.Max(workArea.Left, Math.Min(Left, workArea.Right - ActualWidth));
        double newTop = Math.Max(workArea.Top, Math.Min(Top, workArea.Bottom - ActualHeight));

        if (Math.Abs(Left - newLeft) > 1 || Math.Abs(Top - newTop) > 1)
        {
            Left = newLeft;
            Top = newTop;
            SavePosition();
        }
    }

    #region Position persistence

    private void SavePosition()
    {
        try
        {
            var pos = new CapsulePosition { Left = Left, Top = Top };
            string dir = Path.GetDirectoryName(PositionFile)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(PositionFile, JsonSerializer.Serialize(pos));
        }
        catch
        {
            // Position persistence is best-effort
        }
    }

    private bool RestorePosition()
    {
        try
        {
            if (!File.Exists(PositionFile)) return false;
            string json = File.ReadAllText(PositionFile);
            var pos = JsonSerializer.Deserialize<CapsulePosition>(json);
            if (pos == null) return false;
            Left = pos.Left;
            Top = pos.Top;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private sealed class CapsulePosition
    {
        public double Left { get; set; }
        public double Top { get; set; }
    }

    #endregion

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        if (Settings.Visibility == Visibility.Visible) CloseSettings();
        else
        {
            Settings.LoadSettings(ViewModel.Preferences, CapacityViewModel);
            Settings.Visibility = Visibility.Visible;
            ViewModel.IsExpanded = true;
        }
    }

    private void CloseSettings() => Settings.Visibility = Visibility.Collapsed;

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        _companionHotkeys.SetBinding(CompanionHotkeyAction.ToggleListening,
            ParseHotkey(ViewModel.Preferences.ToggleListeningHotkey, _companionHotkeys.ListeningBinding));
        _companionHotkeys.SetBinding(CompanionHotkeyAction.ToggleCompanion,
            ParseHotkey(ViewModel.Preferences.ToggleCompanionHotkey, _companionHotkeys.CompanionBinding));
    }

    private void OnTrayShowHide(object? sender, EventArgs e)
    {
        if (IsVisible) Hide(); else ShowCompanion();
    }

    private void OnToggleListening(object? sender, EventArgs e) => ViewModel.IsPaused = !ViewModel.IsPaused;

    private void OnCompanionHotkey(object? sender, CompanionHotkeyAction action)
    {
        if (action == CompanionHotkeyAction.ToggleCompanion) OnTrayShowHide(sender, EventArgs.Empty);
        else OnToggleListening(sender, EventArgs.Empty);
    }

    private void ShowCompanion()
    {
        Show();
        _trayService.SetVisible(true);
        ClampToMonitor();
    }

    private void OnTrayQuit(object? sender, EventArgs e)
    {
        _allowClose = true;
        Application.Current.Shutdown();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
            _trayService.SetVisible(false);
        }
        base.OnClosing(e);
    }

    private async void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(WidgetViewModel.State) ||
            ViewModel.State is not (Optimus.Shell.Models.WidgetState.Listening or Optimus.Shell.Models.WidgetState.SessionListening)) return;
        if ((DateTime.UtcNow - _lastVoiceFocus).TotalMilliseconds < 500) return;
        _lastVoiceFocus = DateTime.UtcNow;
        ShowCompanion();
        Topmost = true;
        Activate();
        await Task.Delay(1400);
        if (IsVisible) Topmost = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _companionHotkeys.Pressed -= OnCompanionHotkey;
        _companionHotkeys.Dispose();
        _trayService.Dispose();
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        CapacityViewModel.Dispose();
        base.OnClosed(e);
    }

    private static Optimus.Core.Hotkeys.HotkeyBinding ParseHotkey(string? value, Optimus.Core.Hotkeys.HotkeyBinding fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        string[] parts = value.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        Optimus.Core.Hotkeys.HotkeyModifiers modifiers = Optimus.Core.Hotkeys.HotkeyModifiers.None;
        int key = 0;
        foreach (string part in parts)
        {
            if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase)) modifiers |= Optimus.Core.Hotkeys.HotkeyModifiers.Control;
            else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase)) modifiers |= Optimus.Core.Hotkeys.HotkeyModifiers.Shift;
            else if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase)) modifiers |= Optimus.Core.Hotkeys.HotkeyModifiers.Alt;
            else if (part.Equals("Space", StringComparison.OrdinalIgnoreCase)) key = 0x20;
            else if (part.StartsWith("F", StringComparison.OrdinalIgnoreCase) && int.TryParse(part[1..], out int function) && function is >= 1 and <= 12) key = 0x6F + function;
            else if (part.Length == 1) key = char.ToUpperInvariant(part[0]);
        }
        return key == 0 ? fallback : Optimus.Core.Hotkeys.HotkeyBinding.FromVirtualKey(key, modifiers);
    }
}
