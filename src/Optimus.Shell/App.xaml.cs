namespace Optimus.Shell;

using System.Linq;
using System.Windows;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    // Window and widget lifecycle is owned by T005.

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Contains("--smoke"))
        {
            Shutdown(0);
            return;
        }

        // T005 owns creating the floating widget window and tray icon.
        // For scaffold, no window is created.
    }
}
