namespace Optimus.Shell;

using System;
using System.Linq;
using System.Windows;
using Optimus.Core.Audio;
using Optimus.Core.Hotkeys;
using Optimus.Shell.ViewModels;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "WPF Application lifecycle cleans up disposable resources in OnExit")]
public partial class App : Application
{
    private IAudioCaptureService? _audioCaptureService;
    private IHotkeyService? _hotkeyService;
    private PushToTalkController? _controller;
    private WidgetViewModel? _viewModel;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Contains("--smoke"))
        {
            Shutdown(0);
            return;
        }

        bool useMock = e.Args.Contains("--mock");

        _audioCaptureService = useMock ? new InMemoryAudioCapture() : new WasapiAudioCapture();
        _hotkeyService = useMock ? new MockHotkeyService() : new WindowsKeyboardHook();

        _controller = new PushToTalkController(_hotkeyService, _audioCaptureService);
        _viewModel = new WidgetViewModel();
        _viewModel.AttachController(_controller);

        _controller.Start();

        var mainWindow = new MainWindow(_viewModel);
        MainWindow = mainWindow;
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _viewModel?.Dispose();
        _controller?.Dispose();
        _hotkeyService?.Dispose();
        _audioCaptureService?.Dispose();

        base.OnExit(e);
    }
}
