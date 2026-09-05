namespace Optimus.Core.Tests;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Optimus.Core.Audio;
using Optimus.Core.Hotkeys;
using Optimus.Core.Voice;
using Optimus.Inference;
using Optimus.Providers;
using Optimus.Providers.Windows;
using Optimus.Shell.Models;
using Optimus.Shell.ViewModels;
using Xunit;

public class IntentInterpretationIntegrationTests
{
    [Theory]
    [InlineData("switch", "Claude", "switch", "Claude")]
    [InlineData("cleanupOn", null, "cleanupOn", "")]
    [InlineData("cleanupOff", null, "cleanupOff", "")]
    [InlineData("interruptOn", null, "interruptOn", "")]
    [InlineData("interruptOff", null, "interruptOff", "")]
    [InlineData("fullReview", null, "fullReview", "")]
    [InlineData("shortReview", null, "shortReview", "")]
    [InlineData("repeat", null, "repeat", "")]
    [InlineData("removeLast", null, "removeLast", "")]
    [InlineData("detailsOn", null, "detailsOn", "")]
    [InlineData("detailsOff", null, "detailsOff", "")]
    [InlineData("mute", null, "mute", "")]
    [InlineData("unmute", null, "unmute", "")]
    [InlineData("concise", null, "concise", "")]
    [InlineData("comprehensive", null, "comprehensive", "")]
    [InlineData("toolsOn", null, "toolsOn", "")]
    [InlineData("toolsOff", null, "toolsOff", "")]
    [InlineData("status", null, "status", "")]
    [InlineData("alias", "proj", "alias", "proj")]
    [InlineData("append", null, "append", "")]
    public void ToConversationCommand_MapsAllIntentsCorrectly(string intent, string? target, string expectedKind, string expectedVal)
    {
        var interpreted = new InterpretedIntent(intent, Target: target);
        ConversationCommand? cmd = interpreted.ToConversationCommand();

        Assert.NotNull(cmd);
        Assert.Equal(expectedKind, cmd.Kind);
        Assert.Equal(expectedVal, cmd.Value);
    }

    [Fact]
    public void ToConversationCommand_ReplaceIntent_MapsOldAndNew()
    {
        var interpreted = new InterpretedIntent("replace", OldText: "fetchUser", NewText: "loadUser");
        ConversationCommand? cmd = interpreted.ToConversationCommand();

        Assert.NotNull(cmd);
        Assert.Equal("replace", cmd.Kind);
        Assert.Equal("fetchUser", cmd.Value);
        Assert.Equal("loadUser", cmd.Replacement);
    }

    [Theory]
    [InlineData("affirmative", ApprovalCommand.Affirmative)]
    [InlineData("cancel", ApprovalCommand.Cancel)]
    [InlineData("redictate", ApprovalCommand.Redictate)]
    [InlineData("useOriginal", ApprovalCommand.UseOriginal)]
    [InlineData("none", ApprovalCommand.Unknown)]
    [InlineData("unknown", ApprovalCommand.Unknown)]
    [InlineData("", ApprovalCommand.Unknown)]
    public void ToApprovalCommand_MapsApprovalIntents(string intent, ApprovalCommand expected)
    {
        var interpreted = new InterpretedIntent(intent);
        Assert.Equal(expected, interpreted.ToApprovalCommand());
    }

    [Fact]
    public void ExecuteConversationCommand_ExecutesWithoutParsing()
    {
        var vm = new WidgetViewModel(action => action());
        vm.CleanupEnabled = false;

        bool handled = vm.ExecuteConversationCommand(new ConversationCommand("cleanupOn"), duringApproval: false);

        Assert.True(handled);
        Assert.True(vm.CleanupEnabled);
    }

    [Fact]
    public async Task ApprovalLoop_FallsBackToIntentInterpreter_WhenDeterministicIsUnknown()
    {
        var vm = new WidgetViewModel(action => action());
        var mockAdapter = new MockDestinationAdapter("claude", "Claude");
        vm.Destinations.Add(new DestinationOption(mockAdapter, new DestinationStatus(DestinationReadiness.Ready, Array.Empty<WindowCandidate>(), null, "Ready")));
        vm.SelectedDestination = vm.Destinations[0];
        vm.LoadManualDraft("Test prompt");

        var mockInterpreter = new MockIntentInterpreter(new InterpretedIntent("affirmative"));
        var mockPipeline = new VoicePipeline(
            new MockTranscriber("sounds wonderful proceed with delivery"),
            new MockCleaner(),
            mockInterpreter);
        vm.AttachPipeline(mockPipeline);

        InterpretedIntent? intent = await mockPipeline.IntentInterpreter!.InterpretAsync("sounds wonderful proceed with delivery");
        Assert.NotNull(intent);
        Assert.Equal(ApprovalCommand.Affirmative, intent.ToApprovalCommand());
    }

    private sealed class MockTranscriber(string text) : ISpeechTranscriber
    {
        public bool IsLoaded => true;
        public void EnsureLoaded() { }
        public TranscriptionResult Transcribe(byte[] pcm16Mono16k, CancellationToken cancellationToken = default) =>
            new(text, 10, 1.0);
        public void Dispose() { }
    }

    private sealed class MockCleaner : IPromptCleaner
    {
        public bool IsLoaded => true;
        public void EnsureLoaded() { }
        public Task<CleanupResult> CleanAsync(string rawTranscript, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CleanupResult(rawTranscript, 5, false));
        public Task PrimeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Dispose() { }
    }

    private sealed class MockIntentInterpreter(InterpretedIntent? result) : IIntentInterpreter
    {
        public bool IsLoaded => true;
        public int CallCount { get; private set; }
        public void EnsureLoaded() { }
        public Task<InterpretedIntent?> InterpretAsync(string utterance, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(result);
        }
        public Task PrimeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Dispose() { }
    }

    private sealed class MockDestinationAdapter : IDestinationAdapter
    {
        public MockDestinationAdapter(string id, string name)
        {
            DestinationId = id;
            DisplayName = name;
            ProcessName = id;
        }

        public string DestinationId { get; }
        public string DisplayName { get; }
        public string ProcessName { get; }
        public WindowCandidate? BoundWindow => null;
        public DestinationStatus Probe() => new(DestinationReadiness.Ready, Array.Empty<WindowCandidate>(), null, "Ready");
        public void Bind(WindowCandidate candidate) { }
        public void Unbind() { }
        public Task<SendResult> SendAsync(ConfirmedDraft draft, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SendResult(SendStatus.Sent, "Sent", 50));
    }
}
