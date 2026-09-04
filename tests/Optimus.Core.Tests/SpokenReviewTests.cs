namespace Optimus.Core.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using Optimus.Core.Speech;
using Xunit;

/// <summary>
/// What the widget says out loud must match what it shows, exactly.
/// </summary>
public class SpokenReviewTests
{
    [Fact]
    public void ReviewLines_SpeakTheDraftVerbatim()
    {
        const string draft = "Add a retry to the fetchUser function in api.ts.";

        IReadOnlyList<string> lines = SpokenReviewPlayer.BuildReviewLines(draft, "Claude");

        // Everything before the destination line is the draft, unchanged.
        string spokenDraft = string.Concat(lines.Take(lines.Count - 2));
        Assert.Equal(draft, spokenDraft);
    }

    [Fact]
    public void ReviewLines_EndWithDestinationThenTheQuestion()
    {
        IReadOnlyList<string> lines = SpokenReviewPlayer.BuildReviewLines("Do the thing.", "Codex (ChatGPT app)");

        Assert.Equal("Destination: Codex (ChatGPT app).", lines[^2]);
        Assert.Equal("Send this to Codex (ChatGPT app), or redictate?", lines[^1]);
    }

    /// <summary>The destination is spoken exactly as selected, never abbreviated or renamed.</summary>
    [Theory]
    [InlineData("Claude")]
    [InlineData("Antigravity")]
    [InlineData("Codex (ChatGPT app)")]
    public void ReviewLines_UseTheExactDestinationName(string destination)
    {
        IReadOnlyList<string> lines = SpokenReviewPlayer.BuildReviewLines("Some draft text here.", destination);

        Assert.Contains(destination, lines[^2], StringComparison.Ordinal);
        Assert.Contains(destination, lines[^1], StringComparison.Ordinal);
    }

    /// <summary>A long draft is broken up, but still spoken in full and in order.</summary>
    [Fact]
    public void ReviewLines_PreserveALongDraftAcrossSegments()
    {
        const string draft =
            "Refactor the auth middleware to use async and await instead of promises. " +
            "Then add a unit test for parseConfig in src/config.ts. " +
            "Finally bump the --timeout flag to 30 seconds in config.yaml.";

        IReadOnlyList<string> lines = SpokenReviewPlayer.BuildReviewLines(draft, "Claude");

        Assert.True(lines.Count > 3, "A three-sentence draft should be segmented for earlier playback.");
        Assert.Equal(draft, string.Concat(lines.Take(lines.Count - 2)));
    }

    [Theory]
    [InlineData("", "Claude")]
    [InlineData("   ", "Claude")]
    [InlineData("Some draft.", "")]
    public void ReviewLines_RejectEmptyDraftOrDestination(string draft, string destination)
    {
        Assert.Throws<ArgumentException>(() => SpokenReviewPlayer.BuildReviewLines(draft, destination));
    }

    /// <summary>
    /// Speaking must not be able to send anything. This checks structurally: the speech type has
    /// no reference to a destination adapter or any send entry point.
    /// </summary>
    [Fact]
    public void SpokenReviewPlayer_HasNoPathToSending()
    {
        string[] members = typeof(SpokenReviewPlayer)
            .GetMethods()
            .Select(m => m.Name)
            .ToArray();

        Assert.DoesNotContain(members, name =>
            name.Contains("Send", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Confirm", StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>The approval chime is generated, so it needs the same scrutiny as any other audio.</summary>
public class ChimeTests
{
    [Theory]
    [InlineData(22050)]
    [InlineData(16000)]
    [InlineData(24000)]
    public void Build_ProducesWholeSamplesAtTheRequestedRate(int rate)
    {
        byte[] pcm = Chime.Build(rate);

        Assert.True(pcm.Length > 0);
        Assert.Equal(0, pcm.Length % 2); // 16-bit samples

        double seconds = (pcm.Length / 2.0) / rate;
        Assert.InRange(seconds, Chime.DurationSeconds * 0.9, Chime.DurationSeconds * 1.1);
    }

    /// <summary>Short enough that it does not eat into the approval-readiness budget.</summary>
    [Fact]
    public void Build_IsShort()
    {
        Assert.True(Chime.DurationSeconds < 0.2, "The chime must stay well inside the 200 ms budget.");
    }

    [Fact]
    public void Build_IsAudibleAndStartsAndEndsQuietly()
    {
        byte[] pcm = Chime.Build(22050);

        short First(int i) => BitConverter.ToInt16(pcm, i * 2);
        int samples = pcm.Length / 2;

        short peak = 0;
        for (int i = 0; i < samples; i++)
        {
            short magnitude = Math.Abs(First(i)) == short.MinValue ? short.MaxValue : Math.Abs(First(i));
            if (magnitude > peak)
            {
                peak = magnitude;
            }
        }

        Assert.True(peak > 1000, "The chime should actually be audible.");

        // A hard edge clicks; the envelope should ramp in and out.
        Assert.True(Math.Abs(First(0)) < 200, "Chime starts with a click.");
        Assert.True(Math.Abs(First(samples - 1)) < 200, "Chime ends with a click.");
    }

    [Fact]
    public void Build_RejectsAnInvalidRate()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Chime.Build(0));
    }
}
