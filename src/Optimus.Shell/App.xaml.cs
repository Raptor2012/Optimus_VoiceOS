namespace Optimus.Shell;

using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Optimus.Core.Audio;
using Optimus.Core.Hotkeys;
using Optimus.Core.Phone;
using Optimus.Core.Narration;
using Optimus.Core.Speech;
using Optimus.Core.Voice;
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
    private PhoneEndpoint? _phoneEndpoint;
    private PhoneSession? _phoneSession;
    private SpokenReviewPlayer? _speech;
    private PhoneSpokenReview? _phoneSpeech;
    private OneShotApprovalListener? _approvalListener;
    private PhoneApprovalListener? _phoneApprovalListener;
    private PiperSpeechSynthesizer? _ttsSynthesizer;
    private AgentNarrationCoordinator? _narration;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Contains("--smoke"))
        {
            Shutdown(0);
            return;
        }

        bool useMock = e.Args.Contains("--mock");
        string? manualDraft = GetOptionValue(e.Args, "--draft");

        _audioCaptureService = useMock ? new InMemoryAudioCapture() : new WasapiAudioCapture();
        _hotkeyService = useMock ? new MockHotkeyService() : new WindowsKeyboardHook();

        _controller = new PushToTalkController(_hotkeyService, _audioCaptureService);
        _viewModel = new WidgetViewModel();
        _viewModel.AttachController(_controller);

        _approvalListener = new OneShotApprovalListener(_audioCaptureService);
        _viewModel.AttachApprovalListener(_approvalListener);

        // The three configured Windows targets. Nothing is selected by default; the user picks.
        _destinations = new DestinationRegistry();
        _viewModel.AttachDestinations(_destinations);
        _viewModel.LoadPreferences(PersonalSettings.DefaultPath);

        // --no-models runs capture only. A manual draft also skips the ~3.8 GB model load,
        // making destination-adapter dogfooding immediate even when the microphone is muted.
        if (!e.Args.Contains("--no-models") && manualDraft == null)
        {
            _pipeline = new VoicePipeline();
            _viewModel.AttachPipeline(_pipeline);

            WidgetViewModel viewModel = _viewModel;
            VoicePipeline pipeline = _pipeline;
            viewModel.StatusLine = "Loading local speech and intelligence models...";

            // Load AND prime off the UI thread, so the first utterance runs at steady-state
            // latency rather than paying the one-off prompt-processing cost.
            _ = Task.Run(async () =>
            {
                try
                {
                    await pipeline.WarmupAsync(includeCleanup: true).ConfigureAwait(false);
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

        // Spoken review. --no-voice skips it; the widget stays fully usable without speech.
        if (!e.Args.Contains("--no-voice"))
        {
            WidgetViewModel viewModel = _viewModel;
            _ttsSynthesizer = new PiperSpeechSynthesizer();
            _speech = new SpokenReviewPlayer(_ttsSynthesizer);
            _viewModel.AttachSpeech(_speech);

            SpokenReviewPlayer speech = _speech;

            // Warm the voice off the UI thread so the first review does not pay model load.
            _ = Task.Run(() =>
            {
                try
                {
                    speech.Warmup();
                    Dispatcher.Invoke(() =>
                        viewModel.SetSpeechReady($"Voice ready ({speech.WarmupMilliseconds} ms warmup)"));
                }
                catch (Exception ex)
                {
                    // A missing or broken voice must not block dictation or sending.
                    Dispatcher.Invoke(() => viewModel.SetSpeechReady($"Voice unavailable: {ex.Message}"));
                }
            });
        }

        // The phone endpoint. --no-phone skips it; it binds a LAN/Tailscale-reachable port and
        // has no authentication, so it must only ever run on a private network.
        if (!e.Args.Contains("--no-phone"))
        {
            WidgetViewModel viewModel = _viewModel;
            _phoneEndpoint = new PhoneEndpoint();
            _phoneSession = new PhoneSession(
                _phoneEndpoint,
                _pipeline,
                _destinations,
                onDraft: (raw, clean, timings) => Dispatcher.Invoke(() =>
                    viewModel.LoadPhoneDraft(raw, clean, timings)),
                onStatus: line => Dispatcher.Invoke(() => viewModel.PhoneStatus = line),
                onCancel: () => Dispatcher.Invoke(viewModel.Cancel),
                onSending: (text, destinationId, destinationName) => Dispatcher.Invoke(() =>
                    viewModel.BeginPhoneSend(text, destinationId, destinationName)),
                onSendCompleted: (text, destinationName, result) => Dispatcher.Invoke(() =>
                    viewModel.CompletePhoneSend(text, destinationName, result)),
                onDraftWithDestination: (raw, clean, timings, destId) => Dispatcher.Invoke(() =>
                    viewModel.LoadPhoneDraft(raw, clean, timings, destId)),
                onDestinationSelected: destId => Dispatcher.Invoke(() =>
                {
                    DestinationOption? match = viewModel.Destinations.FirstOrDefault(d =>
                        string.Equals(d.DestinationId, destId, StringComparison.OrdinalIgnoreCase));
                    if (match != null)
                    {
                        viewModel.SelectedDestination = match;
                    }
                }),
                onDraftEdited: text => Dispatcher.Invoke(() =>
                    viewModel.DraftText = text));
            _phoneSession.ProcessCapturedAudio = pcm => Dispatcher.Invoke(() => viewModel.ProcessPhoneAudio(pcm));
            _phoneSession.CaptureBeginning = () => Dispatcher.Invoke(viewModel.PhoneCaptureBeginning);
            _phoneSession.CaptureAbandoned = () => Dispatcher.Invoke(viewModel.PhoneCaptureAbandoned);
            _viewModel.PropertyChanged += (_, change) =>
            {
                if (change.PropertyName == nameof(WidgetViewModel.State))
                {
                    if (viewModel.IsDraftVisible)
                        _phoneEndpoint.SendDraft(viewModel.RawTranscript, viewModel.DraftText, viewModel.StageTimings, viewModel.SelectedDestination?.DestinationId);
                }
                if (change.PropertyName is nameof(WidgetViewModel.State) or nameof(WidgetViewModel.StatusLine))
                    _phoneEndpoint.SendStatus(viewModel.State.ToString(), viewModel.StatusLine);
            };

            _viewModel.SendingStarted += (_, ev) => _phoneSession?.NotifySending(ev.Text, ev.DestinationId, ev.DestinationName);
            _viewModel.SendCompleted += (_, ev) => _phoneSession?.NotifySendCompleted(ev.Text, ev.DestinationName, ev.Result);
            _viewModel.Cancelled += (_, _) => _phoneSession?.NotifyCancelled();

            if (_ttsSynthesizer != null)
            {
                _phoneSpeech = new PhoneSpokenReview(_ttsSynthesizer, _phoneEndpoint);
                _viewModel.AttachPhoneSpeech(_phoneSpeech);
            }
            _phoneApprovalListener = new PhoneApprovalListener(_phoneEndpoint);
            _viewModel.AttachPhoneApprovalListener(_phoneApprovalListener);

            _viewModel.SelectedDestinationChanged += (_, dest) =>
            {
                if (dest != null)
                {
                    _phoneEndpoint.SendSelectedDestination(dest.DestinationId);
                }
            };

            _viewModel.DestinationStatusChanged += (_, _) => _phoneSession?.PushDestinations();

            try
            {
                _phoneEndpoint.NarrationSettingsChanged += OnPhoneNarrationSettingsChanged;
                _phoneEndpoint.Start();
                _viewModel.PhoneStatus =
                    $"Phone endpoint on port {_phoneEndpoint.Port} — {string.Join(", ", PhoneEndpoint.LocalAddresses())}";
            }
            catch (Exception ex)
            {
                _viewModel.PhoneStatus = $"Phone endpoint failed: {ex.Message}";
            }
        }

        if (_ttsSynthesizer != null)
        {
            WidgetViewModel viewModel = _viewModel;
            _narration = new AgentNarrationCoordinator(
                _ttsSynthesizer,
                _phoneEndpoint,
                () => viewModel.CurrentNarrationOptions,
                line => Dispatcher.Invoke(() => viewModel.NarrationStatus = line));
            viewModel.AgentRunStarting += OnAgentRunStarting;
            viewModel.AgentRunCancelled += OnAgentRunCancelled;
            viewModel.NarrationMuteChanged += OnNarrationMuteChanged;

            // Keeps the session microphone shut while the tool is speaking.
            AgentNarrationCoordinator narration = _narration;
            viewModel.NarrationActive = () => narration.IsSpeakingOnPc;
        }

        if (manualDraft != null)
        {
            _viewModel.LoadManualDraft(manualDraft);
        }

        _controller.Start();

        var mainWindow = new MainWindow(_viewModel);
        MainWindow = mainWindow;
        mainWindow.Show();
    }

    internal static string? GetOptionValue(string[] args, string option)
    {
        for (int index = 0; index < args.Length; index++)
        {
            if (!string.Equals(args[index], option, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (index + 1 >= args.Length ||
                string.IsNullOrWhiteSpace(args[index + 1]) ||
                args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                return null;
            }

            return args[index + 1];
        }

        return null;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_viewModel != null)
        {
            _viewModel.AgentRunStarting -= OnAgentRunStarting;
            _viewModel.AgentRunCancelled -= OnAgentRunCancelled;
            _viewModel.NarrationMuteChanged -= OnNarrationMuteChanged;
        }
        _narration?.Dispose();
        _phoneApprovalListener?.Dispose();
        _approvalListener?.Dispose();
        _phoneSpeech?.Dispose();
        _speech?.Dispose();
        _ttsSynthesizer?.Dispose();
        _phoneSession?.Dispose();
        if (_phoneEndpoint != null)
        {
            _phoneEndpoint.NarrationSettingsChanged -= OnPhoneNarrationSettingsChanged;
        }
        _phoneEndpoint?.Dispose();
        _viewModel?.Dispose();
        _pipeline?.Dispose();
        _controller?.Dispose();
        _hotkeyService?.Dispose();
        _audioCaptureService?.Dispose();

        base.OnExit(e);
    }

    private void OnAgentRunStarting(object? sender, AgentRunEventArgs e)
    {
        try
        {
            _narration?.Start(e.Adapter, e.SpeakOnPhone, e.ConfirmedPrompt);
        }
        catch (Exception ex)
        {
            if (_viewModel != null) _viewModel.NarrationStatus = $"Observer unavailable: {ex.Message}";
        }
    }

    private void OnAgentRunCancelled(object? sender, EventArgs e) => _narration?.Cancel();

    private void OnNarrationMuteChanged(bool muted) => _narration?.SetMuted(muted);

    private void OnPhoneNarrationSettingsChanged(object? sender, PhoneNarrationSettingsEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            if (_viewModel == null) return;
            _viewModel.NarrationMode = string.Equals(e.Mode, "comprehensive", StringComparison.OrdinalIgnoreCase)
                ? NarrationMode.Comprehensive
                : NarrationMode.Concise;
            _viewModel.NarrateToolsAndSkills = e.NarrateToolsAndSkills;
        });
    }
}
