namespace Optimus.Inference;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

/// <summary>Audio produced for one spoken segment.</summary>
public sealed record SpeechSegment(string Text, byte[] Pcm, int SampleRate, long TimeToFirstAudioMs, long TotalMs)
{
    public double Seconds => SampleRate > 0 ? (Pcm.Length / 2.0) / SampleRate : 0;

    /// <summary>Synthesis time over audio produced. Below 1.0 is faster than real time.</summary>
    public double RealTimeFactor => Seconds > 0 ? (TotalMs / 1000.0) / Seconds : 0;
}

/// <summary>
/// Local text-to-speech using Piper, kept warm in one long-lived process.
/// </summary>
/// <remarks>
/// Piper was selected by the S006 measurement on this machine; see <c>docs/</c> notes in the
/// commit message. There is deliberately no engine abstraction, no second implementation and no
/// fallback: one measured winner, wired directly.
/// <para>
/// The process stays alive because the model load dominates first-call latency. Piper reads one
/// line of text per utterance on stdin and streams raw 16-bit PCM on stdout. It does not delimit
/// utterances, so a segment is considered finished after a short quiet period with no new bytes.
/// That wait lands on the tail, after playback of the segment has already started, so it does not
/// affect time-to-first-audio.
/// </para>
/// </remarks>
public sealed class PiperSpeechSynthesizer : IDisposable
{
    /// <summary>Quiet period that marks the end of one segment's audio.</summary>
    private const int SegmentQuietMs = 120;

    private const int SynthesisTimeoutMs = 30_000;

    private readonly string _executable;
    private readonly string _voiceModel;
    private readonly VoiceProfile _profile;
    private readonly object _lock = new();
    private readonly object _bufferLock = new();
    private readonly ChildProcessJob _job = new();

    private Process? _process;
    private Thread? _reader;
    private MemoryStream _buffer = new();
    private long _lastByteTicks;
    private bool _disposed;

    public PiperSpeechSynthesizer()
        : this(ModelLocator.PiperExecutable, ModelLocator.TtsVoiceModel, VoiceProfile.Default)
    {
    }

    public PiperSpeechSynthesizer(string executable, string voiceModel, VoiceProfile? profile = null)
    {
        _executable = executable ?? throw new ArgumentNullException(nameof(executable));
        _voiceModel = voiceModel ?? throw new ArgumentNullException(nameof(voiceModel));
        _profile = profile ?? VoiceProfile.Default;
    }

    /// <summary>The speaking character applied to every utterance.</summary>
    public VoiceProfile Profile => _profile;

    /// <summary>Output sample rate, read from the voice config.</summary>
    public int SampleRate { get; private set; } = 22050;

    public bool IsLoaded
    {
        get
        {
            lock (_lock)
            {
                return _process is { HasExited: false };
            }
        }
    }

    public long LoadMilliseconds { get; private set; }

    /// <summary>Loads the voice and pays the first-synthesis cost once, off the caller's path.</summary>
    public void Warmup()
    {
        EnsureStarted();
        // The first utterance in a fresh process is slow; spend it here rather than on the
        // user's first review.
        Speak("Ready.", CancellationToken.None);
    }

    private void EnsureStarted()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_process is { HasExited: false })
            {
                return;
            }

            ModelLocator.RequireTts();
            SampleRate = ReadSampleRate(_voiceModel);

            var stopwatch = Stopwatch.StartNew();

            var startInfo = new ProcessStartInfo
            {
                FileName = _executable,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            startInfo.ArgumentList.Add("--model");
            startInfo.ArgumentList.Add(_voiceModel);
            startInfo.ArgumentList.Add("--output_raw");
            startInfo.ArgumentList.Add("--quiet");

            // The profile is what makes this Optimus's voice rather than the stock voice.
            foreach (string argument in _profile.ToArguments())
            {
                startInfo.ArgumentList.Add(argument);
            }

            _process = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"Could not start {_executable}.");

            // Tie the child to this process at the kernel level so a crash cannot orphan it.
            _job.Assign(_process.Handle);

            // Piper logs to stderr; drain it so a full pipe cannot block synthesis.
            _process.ErrorDataReceived += (_, _) => { };
            _process.BeginErrorReadLine();

            _reader = new Thread(DrainStdout)
            {
                Name = "Optimus.PiperReader",
                IsBackground = true
            };
            _reader.Start();

            stopwatch.Stop();
            LoadMilliseconds = stopwatch.ElapsedMilliseconds;
        }
    }

    private void DrainStdout()
    {
        Process? process;
        lock (_lock)
        {
            process = _process;
        }

        if (process == null)
        {
            return;
        }

        Stream stdout = process.StandardOutput.BaseStream;
        byte[] block = new byte[8192];

        while (true)
        {
            int read;
            try
            {
                read = stdout.Read(block, 0, block.Length);
            }
            catch (IOException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            if (read <= 0)
            {
                return;
            }

            lock (_bufferLock)
            {
                _buffer.Write(block, 0, read);
                _lastByteTicks = Stopwatch.GetTimestamp();
            }
        }
    }

    /// <summary>
    /// Synthesizes one segment. Blocks until the segment's audio is complete.
    /// </summary>
    public SpeechSegment Speak(string text, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ObjectDisposedException.ThrowIf(_disposed, this);

        EnsureStarted();

        Process process;
        lock (_lock)
        {
            process = _process ?? throw new InvalidOperationException("Speech process is not running.");
        }

        lock (_bufferLock)
        {
            _buffer = new MemoryStream();
            _lastByteTicks = 0;
        }

        var stopwatch = Stopwatch.StartNew();

        // Piper takes one utterance per line; newlines inside the text would split it.
        string line = text.Replace("\r", " ", StringComparison.Ordinal)
                          .Replace("\n", " ", StringComparison.Ordinal);

        process.StandardInput.Write(line);
        process.StandardInput.Write('\n');
        process.StandardInput.Flush();

        long firstAudioMs = -1;

        while (stopwatch.ElapsedMilliseconds < SynthesisTimeoutMs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (process.HasExited)
            {
                throw new InvalidOperationException("The speech process exited during synthesis.");
            }

            long length;
            long lastTicks;
            lock (_bufferLock)
            {
                length = _buffer.Length;
                lastTicks = _lastByteTicks;
            }

            if (length > 0 && firstAudioMs < 0)
            {
                firstAudioMs = stopwatch.ElapsedMilliseconds;
            }

            if (length > 0)
            {
                double idleMs = (Stopwatch.GetTimestamp() - lastTicks) * 1000.0 / Stopwatch.Frequency;
                if (idleMs > SegmentQuietMs)
                {
                    break;
                }
            }

            Thread.Sleep(2);
        }

        stopwatch.Stop();

        byte[] pcm;
        lock (_bufferLock)
        {
            pcm = _buffer.ToArray();
        }

        if (pcm.Length == 0)
        {
            throw new InvalidOperationException("The speech engine produced no audio.");
        }

        return new SpeechSegment(
            text,
            pcm,
            SampleRate,
            firstAudioMs < 0 ? stopwatch.ElapsedMilliseconds : firstAudioMs,
            stopwatch.ElapsedMilliseconds);
    }

    /// <summary>Synthesizes each segment in order, yielding as soon as one is ready.</summary>
    public IEnumerable<SpeechSegment> SpeakSegments(IEnumerable<string> segments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(segments);

        foreach (string segment in segments)
        {
            if (string.IsNullOrWhiteSpace(segment))
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            yield return Speak(segment, cancellationToken);
        }
    }

    internal static int ReadSampleRate(string voiceModel)
    {
        string configPath = voiceModel + ".json";

        using FileStream stream = File.OpenRead(configPath);
        using JsonDocument document = JsonDocument.Parse(stream);

        if (document.RootElement.TryGetProperty("audio", out JsonElement audio) &&
            audio.TryGetProperty("sample_rate", out JsonElement rate) &&
            rate.TryGetInt32(out int value))
        {
            return value;
        }

        throw new InvalidDataException(
            $"Voice config {configPath} has no audio.sample_rate; cannot play its output correctly.");
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

            try
            {
                if (_process is { HasExited: false })
                {
                    _process.StandardInput.Close();
                    _process.Kill(entireProcessTree: true);
                    _process.WaitForExit(3000);
                }
            }
            catch (InvalidOperationException)
            {
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }
            catch (IOException)
            {
            }

            _process?.Dispose();
            _process = null;
            _job.Dispose();
        }
    }
}
