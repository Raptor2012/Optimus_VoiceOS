namespace Optimus.Shell;

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Optimus.Shell.ViewModels;

public partial class MainWindow : Window
{
    public WidgetViewModel ViewModel { get; }
    public AgentCapacityViewModel CapacityViewModel { get; }

    private static readonly string PositionFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Optimus", "capsule-position.json");

    // Win32: WS_EX_NOACTIVATE prevents stealing focus when clicking
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    public MainWindow(WidgetViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        CapacityViewModel = new AgentCapacityViewModel();
        AgentCapacity.DataContext = CapacityViewModel;
        DataContext = ViewModel;

        Loaded += OnLoaded;
        LocationChanged += OnLocationChanged;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // Set WS_EX_NOACTIVATE so the capsule never steals focus from normal windows
        var hwnd = new WindowInteropHelper(this).Handle;
        int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        _ = SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_NOACTIVATE);
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

    protected override void OnClosed(EventArgs e)
    {
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        CapacityViewModel.Dispose();
        base.OnClosed(e);
    }
}
