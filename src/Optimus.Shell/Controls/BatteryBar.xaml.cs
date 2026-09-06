namespace Optimus.Shell.Controls;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

public partial class BatteryBar : UserControl
{
    public static readonly DependencyProperty AgentNameProperty = DependencyProperty.Register(
        nameof(AgentName), typeof(string), typeof(BatteryBar), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty CapacityPercentProperty = DependencyProperty.Register(
        nameof(CapacityPercent), typeof(double), typeof(BatteryBar), new PropertyMetadata(100d, OnCapacityChanged));

    public static readonly DependencyProperty IsLowProperty = DependencyProperty.Register(
        nameof(IsLow), typeof(bool), typeof(BatteryBar), new PropertyMetadata(false));

    public static readonly DependencyProperty CapacityBrushProperty = DependencyProperty.Register(
        nameof(CapacityBrush), typeof(Brush), typeof(BatteryBar), new PropertyMetadata(null));

    public string AgentName
    {
        get => (string)GetValue(AgentNameProperty);
        set => SetValue(AgentNameProperty, value);
    }

    public double CapacityPercent
    {
        get => (double)GetValue(CapacityPercentProperty);
        set => SetValue(CapacityPercentProperty, Math.Clamp(value, 0, 100));
    }

    public bool IsLow
    {
        get => (bool)GetValue(IsLowProperty);
        set => SetValue(IsLowProperty, value);
    }

    public Brush? CapacityBrush
    {
        get => (Brush?)GetValue(CapacityBrushProperty);
        private set => SetValue(CapacityBrushProperty, value);
    }

    public string CapacityTooltip => $"{CapacityPercent:0}% remaining";

    public BatteryBar()
    {
        InitializeComponent();
        Loaded += (_, _) => UpdateBrush();
    }

    private static void OnCapacityChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var bar = (BatteryBar)d;
        bar.IsLow = (double)e.NewValue < 20;
        bar.UpdateBrush();
        bar.ToolTip = bar.CapacityTooltip;
    }

    private void UpdateBrush()
    {
        string key = CapacityPercent < 20 ? "ErrorBrush" : CapacityPercent <= 50 ? "WarningBrush" : "AccentBrush";
        CapacityBrush = TryFindResource(key) as Brush;
    }
}
