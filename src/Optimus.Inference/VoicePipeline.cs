namespace Optimus.Inference;

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Captured audio to visible draft: Parakeet transcription, then Gemma cleanup, with a stage
/// timing for each. Also hosts <see cref="GemmaIntentInterpreter"/> using the shared resident Gemma process.
/// </summary>
public sealed class VoicePipeline : IDisposable
{
    private readonly ISpeechTranscriber _transcriber;
    private readonly IPromptCleaner _cleaner;
    private readonly IIntentInterpreter? _intentInterpreter;
    private readonly IDigestSummarizer? _digestSummarizer;
    private readonly LlamaServerProcess? _llamaServer;
    private readonly ILocalModelClient? _localModelClient;
    private readonly bool _ownsDependencies;
    private bool _disposed;

    public VoicePipeline()
    {
        var transcriber = new ParakeetTranscriber();
        var llama = new LlamaServerProcess(ModelLocator.LlamaServerExecutable, ModelLocator.GemmaCleanupModel);
        var cleaner = new GemmaPromptCleaner(llama);
        var interpreter = new GemmaIntentInterpreter(llama);
        var summarizer = new GemmaDigestSummarizer(llama);

        _transcriber = transcriber;
        _cleaner = cleaner;
        _intentInterpreter = interpreter;
        _digestSummarizer = summarizer;
        _llamaServer = llama;
        _localModelClient = new LocalModelClient(llama);
        _ownsDependencies = true;
    }

    public VoicePipeline(
        ISpeechTranscriber transcriber,
        IPromptCleaner cleaner,
        IIntentInterpreter? intentInterpreter = null,
        IDigestSummarizer? digestSummarizer = null,
        bool ownsDependencies = false)
    {
        _transcriber = transcriber ?? throw new ArgumentNullException(nameof(transcriber));
        _cleaner = cleaner ?? throw new ArgumentNullException(nameof(cleaner));
        _intentInterpreter = intentInterpreter;
        _digestSummarizer = digestSummarizer;
        _ownsDependencies = ownsDependencies;
    }

    public bool IsWarm => _transcriber.IsLoaded && _cleaner.IsLoaded;

    public ISpeechTranscriber Transcriber => _transcriber;

    public IPromptCleaner Cleaner => _cleaner;

    public IIntentInterpreter? IntentInterpreter => _intentInterpreter;

    public IDigestSummarizer? DigestSummarizer => _digestSummarizer;

    /// <summary>Shared local model client for the end-to-end desktop orchestrator.</summary>
    public ILocalModelClient? LocalModelClient => _localModelClient;

    /// <summary>
    /// Transcribes audio directly through Parakeet without invoking prompt cleanup.
    /// Used for spoken approval commands.
    /// </summary>
    public TranscriptionResult TranscribeOnly(
        byte[] pcm16Mono16k,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pcm16Mono16k);
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _transcriber.Transcribe(pcm16Mono16k, cancellationToken);
    }

    /// <summary>Loads both models. Prefer <see cref="WarmupAsync"/>, which also primes them.</summary>
    public void Warmup()
    {
        _transcriber.EnsureLoaded();
        _cleaner.EnsureLoaded();
        _intentInterpreter?.EnsureLoaded();
        _digestSummarizer?.EnsureLoaded();
    }

    /// <summary>
    /// Loads and then primes both models, so the first real utterance runs at steady-state
    /// latency instead of paying first-call cost.
    /// </summary>
    public async Task WarmupAsync(bool includeCleanup = true, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _transcriber.EnsureLoaded();

        // One short silent buffer forces the ONNX graph through a real execution.
        byte[] primingAudio = new byte[16000 * sizeof(short) / 2]; // 0.5 s of 16 kHz mono PCM16
        _transcriber.Transcribe(primingAudio, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();

        if (includeCleanup || _intentInterpreter != null)
        {
            _cleaner.EnsureLoaded();
            await _cleaner.PrimeAsync(cancellationToken).ConfigureAwait(false);
            if (_intentInterpreter != null)
            {
                await _intentInterpreter.PrimeAsync(cancellationToken).ConfigureAwait(false);
            }
            if (_digestSummarizer != null)
            {
                await _digestSummarizer.PrimeAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Runs one utterance through both stages. A cleanup failure degrades to the raw
    /// transcript rather than failing the utterance; the user still confirms explicitly.
    /// </summary>
    public async Task<VoicePipelineResult> ProcessAsync(
        byte[] pcm16Mono16k,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pcm16Mono16k);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var total = Stopwatch.StartNew();

        TranscriptionResult transcription = _transcriber.Transcribe(pcm16Mono16k, cancellationToken);

        if (transcription.IsEmpty)
        {
            total.Stop();
            return new VoicePipelineResult(
                RawTranscript: string.Empty,
                CleanedDraft: string.Empty,
                TranscribeMilliseconds: transcription.ElapsedMilliseconds,
                CleanupMilliseconds: 0,
                TotalMilliseconds: total.ElapsedMilliseconds,
                AudioSeconds: transcription.AudioSeconds,
                CleanupApplied: false,
                CleanupUnavailableReason: "No speech detected");
        }

        cancellationToken.ThrowIfCancellationRequested();

        CleanupResult cleanup;
        try
        {
            cleanup = await _cleaner.CleanAsync(transcription.Text, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            cleanup = new CleanupResult(transcription.Text, 0, Applied: false, ex.Message);
        }

        total.Stop();

        return new VoicePipelineResult(
            RawTranscript: transcription.Text,
            CleanedDraft: cleanup.Text,
            TranscribeMilliseconds: transcription.ElapsedMilliseconds,
            CleanupMilliseconds: cleanup.ElapsedMilliseconds,
            TotalMilliseconds: total.ElapsedMilliseconds,
            AudioSeconds: transcription.AudioSeconds,
            CleanupApplied: cleanup.Applied,
            CleanupUnavailableReason: cleanup.UnavailableReason);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_ownsDependencies)
        {
            _transcriber.Dispose();
            _cleaner.Dispose();
            _intentInterpreter?.Dispose();
            _digestSummarizer?.Dispose();
            (_localModelClient as IDisposable)?.Dispose();
            if (_localModelClient == null)
            {
                _llamaServer?.Dispose();
            }
        }
    }
}
