namespace Optimus.Core.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using Optimus.Inference;
using Xunit;

/// <summary>
/// Segmentation exists to start playback sooner. It must never change what gets spoken.
/// </summary>
public class SpeechSegmenterTests
{
    /// <summary>
    /// The load-bearing property: the segments joined back together are byte-for-byte the input.
    /// If this ever fails, the spoken review no longer matches the displayed draft.
    /// </summary>
    [Theory]
    [InlineData("Add a retry to the fetchUser function in api.ts.")]
    [InlineData("Bump the --timeout flag to 30 seconds in config.yaml. Then run the tests.")]
    [InlineData("First sentence here. Second sentence here. Third one, with a clause, ends now.")]
    [InlineData("Short.")]
    [InlineData("No terminal punctuation at all")]
    [InlineData("Version 3.5 of the parser handles src/config.ts and Optimus.Shell correctly.")]
    public void Split_JoinsBackToTheExactInput(string text)
    {
        IReadOnlyList<string> segments = SpeechSegmenter.Split(text);

        Assert.Equal(text, string.Concat(segments));
    }

    [Fact]
    public void Split_ReturnsNothingForEmptyInput()
    {
        Assert.Empty(SpeechSegmenter.Split(""));
        Assert.Empty(SpeechSegmenter.Split("   "));
    }

    /// <summary>A filename's dot must not end a segment, or "api" and "ts" get separate audio.</summary>
    [Theory]
    [InlineData("Add a retry to the fetchUser function in api.ts.")]
    [InlineData("Write a unit test for parseConfig in src/config.ts.")]
    [InlineData("Open Optimus.Shell and check the widget.")]
    [InlineData("Set the threshold to 0.75 in the config.")]
    public void Split_DoesNotBreakInsideIdentifiersOrPaths(string text)
    {
        IReadOnlyList<string> segments = SpeechSegmenter.Split(text);

        // Every segment that is not the last must end at a real boundary, never mid-token.
        foreach (string segment in segments.Take(segments.Count - 1))
        {
            Assert.True(
                segment.EndsWith(' ') || segment.EndsWith('.') || segment.EndsWith(',') ||
                segment.EndsWith('?') || segment.EndsWith('!') || segment.EndsWith(';') ||
                segment.EndsWith(':'),
                $"Segment ended mid-token: '{segment}'");
        }

        // These particular inputs are short enough to stay whole.
        Assert.Single(segments);
    }

    [Fact]
    public void Split_SeparatesRealSentences()
    {
        IReadOnlyList<string> segments = SpeechSegmenter.Split(
            "Refactor the auth middleware to use async and await. Then run the whole test suite.");

        Assert.Equal(2, segments.Count);
        Assert.Contains("Refactor", segments[0], StringComparison.Ordinal);
        Assert.Contains("test suite", segments[1], StringComparison.Ordinal);
    }

    /// <summary>Very long text must still be broken up, so playback does not wait for all of it.</summary>
    [Fact]
    public void Split_BreaksUpVeryLongTextWithoutSentenceEnders()
    {
        string text = string.Join(" ", Enumerable.Repeat("some more words to say aloud", 40));

        IReadOnlyList<string> segments = SpeechSegmenter.Split(text);

        Assert.True(segments.Count > 1, "A long message should be split so audio can start early.");
        Assert.Equal(text, string.Concat(segments));
        Assert.All(segments, s => Assert.True(
            s.Length <= SpeechSegmenter.MaxSegmentChars + 40,
            $"Segment far past the cap: {s.Length} chars"));
    }

    /// <summary>A tiny trailing fragment is merged rather than getting its own synthesis call.</summary>
    [Fact]
    public void Split_DoesNotEmitAOneCharacterTail()
    {
        IReadOnlyList<string> segments = SpeechSegmenter.Split(
            "This is a complete sentence that is long enough to split. A");

        Assert.All(segments, s => Assert.True(s.Trim().Length >= 3, $"Tiny fragment: '{s}'"));
    }
}
