namespace Optimus.Inference.Tests;

using System.Collections.Generic;
using System.Linq;
using Optimus.Inference;
using Xunit;

/// <summary>
/// Model-free tests for the cleanup prompt shape and output sanitizer.
/// </summary>
public class GemmaPromptCleanerTests
{
    [Fact]
    public void BuildMessages_PutsFewShotBeforeTheLiveTranscript()
    {
        IReadOnlyList<LlamaServerProcess.ChatMessage> messages =
            GemmaPromptCleaner.BuildMessages("um add a test");

        Assert.Equal("system", messages[0].Role);

        // Four worked examples, alternating user/assistant, then the live turn last.
        Assert.Equal(10, messages.Count);
        Assert.Equal("user", messages[^1].Role);
        Assert.Equal("um add a test", messages[^1].Content);
        Assert.Equal(4, messages.Count(m => m.Role == "assistant"));
    }

    [Fact]
    public void BuildMessages_ExamplesDemonstrateIdentifierFormattingAndInstructionPreservation()
    {
        IReadOnlyList<LlamaServerProcess.ChatMessage> messages =
            GemmaPromptCleaner.BuildMessages("anything");

        string examples = string.Join(" ", messages.Where(m => m.Role == "assistant").Select(m => m.Content));

        Assert.Contains("fetchUser", examples, System.StringComparison.Ordinal);
        Assert.Contains("api.ts", examples, System.StringComparison.Ordinal);
        Assert.Contains("--timeout", examples, System.StringComparison.Ordinal);
        Assert.Contains("do not reply anything, just send this message", examples, System.StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Add a retry to fetchUser in api.ts.", "Add a retry to fetchUser in api.ts.")]
    [InlineData("  Add a retry.  ", "Add a retry.")]
    [InlineData("\"Add a retry.\"", "Add a retry.")]
    [InlineData("Add a retry.<end_of_turn>", "Add a retry.")]
    [InlineData("<think>reasoning here</think>Add a retry.", "Add a retry.")]
    public void Sanitize_StripsScaffolding(string modelOutput, string expected)
    {
        Assert.Equal(expected, GemmaPromptCleaner.Sanitize(modelOutput, "raw fallback"));
    }

    /// <summary>An empty or whitespace reply must never blank the user's words.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<think>only reasoning, no answer</think>")]
    public void Sanitize_FallsBackToRawTranscript(string modelOutput)
    {
        Assert.Equal("raw fallback", GemmaPromptCleaner.Sanitize(modelOutput, "raw fallback"));
    }

    /// <summary>
    /// Gemma narrates across several lines when it slips into reasoning; keep the final line,
    /// which is the cleaned sentence, rather than showing the narration as a draft.
    /// </summary>
    [Fact]
    public void Sanitize_KeepsOnlyFinalLineOfMultiLineNarration()
    {
        const string narration = "The user wants me to clean this up.\nI should remove fillers.\nAdd a retry to fetchUser in api.ts.";

        Assert.Equal("Add a retry to fetchUser in api.ts.", GemmaPromptCleaner.Sanitize(narration, "raw fallback"));
    }

    /// <summary>The server must never be reachable from off the machine.</summary>
    [Fact]
    public void ServerArguments_BindLoopbackOnlyAndDisableReasoning()
    {
        string[] args = LlamaServerProcess.BuildArguments(@"C:\model.gguf", 8080, 4096).ToArray();

        int hostIndex = System.Array.IndexOf(args, "--host");
        Assert.True(hostIndex >= 0);
        Assert.Equal("127.0.0.1", args[hostIndex + 1]);
        Assert.DoesNotContain("0.0.0.0", args);

        int reasoningIndex = System.Array.IndexOf(args, "--reasoning-budget");
        Assert.True(reasoningIndex >= 0);
        Assert.Equal("0", args[reasoningIndex + 1]);

        Assert.Contains("--jinja", args);
    }
}
