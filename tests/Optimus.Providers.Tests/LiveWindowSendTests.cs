namespace Optimus.Providers.Tests;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Optimus.Providers;
using Optimus.Providers.Windows;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// Exercises the real <see cref="WindowsAppAdapter"/> against a live window: real window
/// enumeration, real <c>SetForegroundWindow</c>, real <c>SendInput</c>.
/// </summary>
/// <remarks>
/// The target is a throwaway window this test owns, not one of the configured applications.
/// Two reasons: typing into Claude, Antigravity or the ChatGPT app submits a real prompt to a
/// live agent session, and the Claude desktop app is what hosts the session driving this work,
/// so a send there would type into its own conversation. A window under test control also lets
/// the typed text be read back and compared exactly, which is stronger evidence than "no error
/// was reported".
/// <para>
/// Gated on <c>OPTIMUS_UI_SMOKE=1</c> because it steals foreground focus while it runs.
/// </para>
/// </remarks>
public class LiveWindowSendTests
{
    private readonly ITestOutputHelper _output;

    public LiveWindowSendTests(ITestOutputHelper output) => _output = output;

    private static bool Enabled => Environment.GetEnvironmentVariable("OPTIMUS_UI_SMOKE") == "1";

    [Fact]
    public async Task RealAdapter_TypesExactlyTheConfirmedTextIntoALiveWindow()
    {
        if (!Enabled)
        {
            _output.WriteLine("Skipped: set OPTIMUS_UI_SMOKE=1 to run the live window send test.");
            return;
        }

        const string marker = "OptimusLiveSendTarget";
        const string prompt = "Add a retry to fetchUser in api.ts, then run the tests.";

        using var host = new TestWindowHost(marker);
        host.Start();

        string processName = Process.GetCurrentProcess().ProcessName;

        var adapter = new WindowsAppAdapter("test-target", "Live Test Window", processName);

        DestinationStatus status = adapter.Probe();
        WindowCandidate? target = status.Candidates.FirstOrDefault(c => c.Title.Contains(marker, StringComparison.Ordinal));

        Assert.True(target != null, $"Test window was not found among {status.Candidates.Count} candidates.");
        _output.WriteLine($"Target window     : {target!.DisplayLabel}");

        adapter.Bind(target);
        Assert.Equal(DestinationReadiness.Ready, adapter.Probe().Readiness);

        SendResult result = await adapter.SendAsync(new ConfirmedDraft(prompt, "test-target"));
        _output.WriteLine($"Send result       : {result.Status} ({result.ElapsedMilliseconds} ms) {result.Detail}");

        Assert.True(result.Succeeded, $"Send failed: {result.Detail}");

        // Give the message loop a moment to process the synthesized input.
        string received = await host.WaitForTextAsync(prompt, TimeSpan.FromSeconds(5));
        _output.WriteLine($"Text received     : {received}");
        _output.WriteLine($"Submit observed   : {host.SubmitCount}");

        // Byte-for-byte: what was confirmed is what arrived.
        Assert.Equal(prompt, received);

        // Enter reached the window exactly once, as the submit action.
        Assert.Equal(1, host.SubmitCount);
    }

    /// <summary>Probes the three configured destinations without sending anything.</summary>
    [Fact]
    public void ConfiguredDestinations_ProbeCleanlyAgainstTheRealMachine()
    {
        if (!Enabled)
        {
            _output.WriteLine("Skipped: set OPTIMUS_UI_SMOKE=1 to probe the configured applications.");
            return;
        }

        var registry = new DestinationRegistry();

        foreach ((IDestinationAdapter adapter, DestinationStatus status) in registry.ProbeAll())
        {
            _output.WriteLine(
                $"{adapter.DisplayName,-22} process={adapter.ProcessName,-14} " +
                $"readiness={status.Readiness,-18} candidates={status.Candidates.Count}");

            foreach (WindowCandidate candidate in status.Candidates)
            {
                _output.WriteLine($"    {candidate.DisplayLabel}");
            }

            // Nothing may be auto-bound, whatever the machine looks like.
            Assert.Null(adapter.BoundWindow);
            Assert.False(status.CanSend, $"{adapter.DisplayName} reported ready without an explicit binding.");
        }
    }

    /// <summary>A WinForms window on its own STA thread, used as a send target.</summary>
    private sealed class TestWindowHost : IDisposable
    {
        private readonly string _title;
        private readonly ManualResetEventSlim _ready = new(false);
        private Thread? _thread;
        private Form? _form;
        private TextBox? _textBox;

        public TestWindowHost(string title) => _title = title;

        public int SubmitCount { get; private set; }

        public void Start()
        {
            _thread = new Thread(() =>
            {
                _textBox = new TextBox
                {
                    Multiline = true,
                    Dock = DockStyle.Fill,
                    AcceptsReturn = false
                };

                _textBox.KeyDown += (_, e) =>
                {
                    if (e.KeyCode == Keys.Enter && !e.Shift)
                    {
                        SubmitCount++;
                        e.SuppressKeyPress = true; // Behave like a chat composer.
                    }
                };

                _form = new Form
                {
                    Text = _title,
                    Width = 700,
                    Height = 260,
                    TopMost = true,
                    StartPosition = FormStartPosition.CenterScreen
                };

                _form.Controls.Add(_textBox);
                _form.Shown += (_, _) =>
                {
                    _textBox.Focus();
                    _ready.Set();
                };

                Application.Run(_form);
            });

            _thread.SetApartmentState(ApartmentState.STA);
            _thread.IsBackground = true;
            _thread.Start();

            if (!_ready.Wait(TimeSpan.FromSeconds(15)))
            {
                throw new TimeoutException("Test window did not appear.");
            }

            Thread.Sleep(300); // Let the window settle before focus is taken.
        }

        public async Task<string> WaitForTextAsync(string expected, TimeSpan timeout)
        {
            DateTime deadline = DateTime.UtcNow + timeout;
            string current = string.Empty;

            while (DateTime.UtcNow < deadline)
            {
                current = ReadText();
                if (string.Equals(current, expected, StringComparison.Ordinal))
                {
                    return current;
                }

                await Task.Delay(50);
            }

            return current;
        }

        private string ReadText()
        {
            TextBox? box = _textBox;
            Form? form = _form;

            if (box == null || form == null || form.IsDisposed)
            {
                return string.Empty;
            }

            try
            {
                return form.InvokeRequired
                    ? (string)form.Invoke(new Func<string>(() => box.Text))
                    : box.Text;
            }
            catch (ObjectDisposedException)
            {
                return string.Empty;
            }
            catch (InvalidOperationException)
            {
                return string.Empty;
            }
        }

        public void Dispose()
        {
            try
            {
                Form? form = _form;
                if (form is { IsDisposed: false })
                {
                    form.Invoke(new Action(() => form.Close()));
                }
            }
            catch (InvalidOperationException)
            {
                // Message loop already gone, or the handle was destroyed.
            }

            _thread?.Join(TimeSpan.FromSeconds(3));
            _ready.Dispose();
        }
    }
}
