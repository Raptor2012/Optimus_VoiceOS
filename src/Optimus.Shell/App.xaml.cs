namespace Optimus.Shell;

using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Optimus.Core.Audio;
using Optimus.Core.Hotkeys;
using Optimus.Inference;
using Optimus.Providers;
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
    private VoicePipeline? _pipeline;
    private DestinationRegistry? _destinations;

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

        // The three configured Windows targets. Nothing is selected by default; the user picks.
        _destinations = new DestinationRegistry();
        _viewModel.AttachDestinations(_destinations);

        // --no-models runs capture only, for testing the widget without loading ~3.8 GB.
        if (!e.Args.Contains("--no-models"))
        {
            _pipeline = new VoicePipeline();
            _viewModel.AttachPipeline(_pipeline);

            WidgetViewModel viewModel = _viewModel;
            VoicePipeline pipeline = _pipeline;
            viewModel.StatusLine = "Loading speech and cleanup models...";

            // Load AND prime off the UI thread, so the first utterance runs at steady-state
            // latency rather than paying the one-off prompt-processing cost.
            _ = Task.Run(async () =>
            {
                try
                {
                    await pipeline.WarmupAsync().ConfigureAwait(false);
                    Dispatcher.Invoke(() => viewModel.StatusLine = $"Ready — Hold {viewModel.HotkeyLabel} to speak");
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() =>
                    {
                        viewModel.ErrorMessage = $"Model load failed: {ex.Message}";
                        viewModel.State = Models.WidgetState.Error;
                        viewModel.StatusLine = "Models unavailable";
                    });
                }
            });
        }

        _controller.Start();

        var mainWindow = new MainWindow(_viewModel);
        MainWindow = mainWindow;
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _viewModel?.Dispose();
        _pipeline?.Dispose();
        _controller?.Dispose();
        _hotkeyService?.Dispose();
        _audioCaptureService?.Dispose();

        base.OnExit(e);
    }
}
