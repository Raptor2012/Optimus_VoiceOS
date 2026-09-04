namespace Optimus.Inference;

using System;
using System.Diagnostics;
using System.Threading;
using SherpaOnnx;

/// <summary>
/// Local speech-to-text using NVIDIA Parakeet TDT 0.6B v2 (English) through sherpa-onnx.
/// Input is the canonical 16 kHz mono PCM16 buffer produced by capture.
/// </summary>
public sealed class ParakeetTranscriber : ISpeechTranscriber
{
    private const int ExpectedSampleRate = 16000;

    private readonly object _lock = new();
    private OfflineRecognizer? _recognizer;
    private bool _disposed;

    /// <summary>Milliseconds spent loading the model, or 0 if not yet loaded.</summary>
    public long LoadMilliseconds { get; private set; }

    public bool IsLoaded
    {
        get
        {
            lock (_lock)
            {
                return _recognizer != null;
            }
        }
    }

    /// <summary>Loads the model. Safe to call repeatedly; the first call pays the cost.</summary>
    public void EnsureLoaded()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_recognizer != null)
            {
                return;
            }

            ModelLocator.RequireParakeet();

            var stopwatch = Stopwatch.StartNew();

            var config = new OfflineRecognizerConfig();
            config.ModelConfig.Transducer.Encoder = ModelLocator.ParakeetEncoder;
            config.ModelConfig.Transducer.Decoder = ModelLocator.ParakeetDecoder;
            config.ModelConfig.Transducer.Joiner = ModelLocator.ParakeetJoiner;
            config.ModelConfig.Tokens = ModelLocator.ParakeetTokens;
            config.ModelConfig.ModelType = "nemo_transducer";
            config.ModelConfig.NumThreads = Math.Max(1, Environment.ProcessorCount / 2);
            config.ModelConfig.Debug = 0;
            config.DecodingMethod = "greedy_search";

            _recognizer = new OfflineRecognizer(config);

            stopwatch.Stop();
            LoadMilliseconds = stopwatch.ElapsedMilliseconds;
        }
    }

    /// <summary>Transcribes one utterance of 16 kHz mono PCM16.</summary>
    public TranscriptionResult Transcribe(byte[] pcm16Mono16k, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pcm16Mono16k);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (pcm16Mono16k.Length < sizeof(short))
        {
            return new TranscriptionResult(string.Empty, 0, 0);
        }

        EnsureLoaded();
        cancellationToken.ThrowIfCancellationRequested();

        float[] samples = PcmToFloat(pcm16Mono16k);
        double audioSeconds = samples.Length / (double)ExpectedSampleRate;

        var stopwatch = Stopwatch.StartNew();

        string text;
        lock (_lock)
        {
            OfflineRecognizer recognizer = _recognizer
                ?? throw new InvalidOperationException("Recognizer was disposed during transcription.");

            using OfflineStream stream = recognizer.CreateStream();
            stream.AcceptWaveform(ExpectedSampleRate, samples);
            recognizer.Decode(stream);
            text = stream.Result.Text ?? string.Empty;
        }

        stopwatch.Stop();

        return new TranscriptionResult(text.Trim(), stopwatch.ElapsedMilliseconds, audioSeconds);
    }

    /// <summary>Converts signed 16-bit little-endian PCM to normalized float samples.</summary>
    internal static float[] PcmToFloat(byte[] pcm)
    {
        int sampleCount = pcm.Length / sizeof(short);
        float[] samples = new float[sampleCount];
        for (int i = 0; i < sampleCount; i++)
        {
            short s = (short)(pcm[i * 2] | (pcm[(i * 2) + 1] << 8));
            samples[i] = s / 32768f;
        }

        return samples;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _recognizer?.Dispose();
            _recognizer = null;
        }
    }
}
