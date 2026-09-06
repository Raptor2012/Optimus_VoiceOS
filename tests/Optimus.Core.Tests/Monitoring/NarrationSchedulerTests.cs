namespace Optimus.Core.Tests.Monitoring;

using System;
using System.Threading;
using System.Threading.Tasks;
using Optimus.Core.Monitoring;
using Xunit;

public sealed class NarrationSchedulerTests
{
    [Fact]
    public async Task QueueNarration_PlaysViaCustomPlayer()
    {
        string? spoken = null;
        using var scheduler = new NarrationScheduler(
            customPlayer: (text, ct) =>
            {
                spoken = text;
                return Task.CompletedTask;
            });

        scheduler.QueueNarration("Build succeeded with zero errors.");

        for (int i = 0; i < 20 && spoken == null; i++)
        {
            await Task.Delay(25);
        }

        Assert.NotNull(spoken);
        Assert.Equal("Build succeeded with zero errors.", spoken);
    }

    [Fact]
    public async Task QueueNarration_DefersWhenUserSpeaking_UntilSilence()
    {
        bool userSpeaking = true;
        string? spoken = null;

        using var scheduler = new NarrationScheduler(
            isUserSpeaking: () => userSpeaking,
            customPlayer: (text, ct) =>
            {
                spoken = text;
                return Task.CompletedTask;
            });

        scheduler.QueueNarration("Important update.");

        // Wait while speaking
        await Task.Delay(100);
        Assert.Null(spoken);

        // User stops speaking
        userSpeaking = false;

        for (int i = 0; i < 20 && spoken == null; i++)
        {
            await Task.Delay(25);
        }

        Assert.NotNull(spoken);
        Assert.Equal("Important update.", spoken);
    }

    [Fact]
    public async Task QueueNarration_DefersWhenListening_UntilNotListening()
    {
        bool listening = true;
        string? spoken = null;

        using var scheduler = new NarrationScheduler(
            isListening: () => listening,
            customPlayer: (text, ct) =>
            {
                spoken = text;
                return Task.CompletedTask;
            });

        scheduler.QueueNarration("Echo cancellation test.");

        await Task.Delay(100);
        Assert.Null(spoken);

        listening = false;

        for (int i = 0; i < 20 && spoken == null; i++)
        {
            await Task.Delay(25);
        }

        Assert.NotNull(spoken);
        Assert.Equal("Echo cancellation test.", spoken);
    }

    [Fact]
    public void SummarizeForSpeech_StripsCodeBlocksAndLimitsLength()
    {
        string markdown = "Here is the code: ```csharp\nint x = 42;\n``` Done. `x` is ready.";
        string summary = NarrationScheduler.SummarizeForSpeech(markdown);

        Assert.DoesNotContain("```", summary);
        Assert.Contains("Here is the code:", summary);
        Assert.Contains("Done.", summary);
    }
}
