namespace Optimus.Shell;

using System;
using System.Windows;
using System.Windows.Input;
using Optimus.Shell.ViewModels;

public partial class MainWindow : Window
{
    public WidgetViewModel ViewModel { get; }

    public MainWindow(WidgetViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = ViewModel;

        // Position in bottom-right corner of main work area
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - ActualWidth - 24;
        Top = workArea.Bottom - ActualHeight - 24;
    }

    private void OnWindowMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            DragMove();
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
