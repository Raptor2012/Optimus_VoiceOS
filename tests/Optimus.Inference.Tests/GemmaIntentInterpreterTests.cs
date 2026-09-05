namespace Optimus.Inference.Tests;

using System.Collections.Generic;
using System.Linq;
using Optimus.Inference;
using Xunit;

/// <summary>
/// Model-free tests for the intent interpreter prompt shape and JSON output sanitizer.
/// </summary>
public class GemmaIntentInterpreterTests
{
    [Fact]
    public void BuildMessages_PutsFewShotBeforeTheLiveUtterance()
    {
        IReadOnlyList<LlamaServerProcess.ChatMessage> messages =
            GemmaIntentInterpreter.BuildMessages("can you switch to claude");

        Assert.Equal("system", messages[0].Role);
        Assert.Equal("user", messages[^1].Role);
        Assert.Equal("can you switch to claude", messages[^1].Content);
        Assert.True(messages.Count > 10);
        Assert.True(messages.Count(m => m.Role == "assistant") > 10);
    }

    [Fact]
    public void BuildMessages_ExamplesIncludeDisambiguationAndProductNames()
    {
        IReadOnlyList<LlamaServerProcess.ChatMessage> messages =
            GemmaIntentInterpreter.BuildMessages("anything");

        string userExamples = string.Join(" ", messages.Where(m => m.Role == "user").Select(m => m.Content));
        string assistantExamples = string.Join(" ", messages.Where(m => m.Role == "assistant").Select(m => m.Content));

        // Verifies prompt covers key approval and command phrases
        Assert.Contains("anti-gravity", userExamples);
        Assert.Contains("Antigravity", assistantExamples);
        Assert.Contains("Claude", assistantExamples);
        Assert.Contains("Codex", assistantExamples);
        Assert.Contains("affirmative", assistantExamples);
        Assert.Contains("cancel", assistantExamples);
        Assert.Contains("redictate", assistantExamples);
        Assert.Contains("useOriginal", assistantExamples);
        Assert.Contains("none", assistantExamples);
    }

    [Theory]
    [InlineData("{\"intent\": \"affirmative\"}", "affirmative", null)]
    [InlineData("{\"intent\": \"cancel\"}", "cancel", null)]
    [InlineData("{\"intent\": \"redictate\"}", "redictate", null)]
    [InlineData("{\"intent\": \"useOriginal\"}", "useOriginal", null)]
    [InlineData("{\"intent\": \"switch\", \"target\": \"Claude\"}", "switch", "Claude")]
    [InlineData("{\"intent\": \"cleanupOn\"}", "cleanupOn", null)]
    [InlineData("{\"intent\": \"mute\"}", "mute", null)]
    [InlineData("{\"intent\": \"none\"}", "none", null)]
    public void SanitizeAndParse_ValidJson_ReturnsInterpretedIntent(string modelOutput, string expectedIntent, string? expectedTarget)
    {
        InterpretedIntent? result = GemmaIntentInterpreter.SanitizeAndParse(modelOutput, 42);

        Assert.NotNull(result);
        Assert.Equal(expectedIntent, result.Intent);
        Assert.Equal(expectedTarget, result.Target);
        Assert.Equal(42, result.ElapsedMilliseconds);
    }

    [Fact]
    public void SanitizeAndParse_MarkdownCodeBlocks_StripsFences()
    {
        const string fenced = "```json\n{\"intent\": \"switch\", \"target\": \"Antigravity\"}\n```";
        InterpretedIntent? result = GemmaIntentInterpreter.SanitizeAndParse(fenced, 50);

        Assert.NotNull(result);
        Assert.Equal("switch", result.Intent);
        Assert.Equal("Antigravity", result.Target);
    }

    [Fact]
    public void SanitizeAndParse_WithThinkingScaffolding_StripsThinkingTags()
    {
        const string output = "<think>The user said switch to codex so I should output switch.</think>{\"intent\": \"switch\", \"target\": \"Codex\"}";
        InterpretedIntent? result = GemmaIntentInterpreter.SanitizeAndParse(output, 60);

        Assert.NotNull(result);
        Assert.Equal("switch", result.Intent);
        Assert.Equal("Codex", result.Target);
    }

    [Fact]
    public void SanitizeAndParse_WithParameters_ParsesAllFields()
    {
        const string output = "{\"intent\": \"replace\", \"old\": \"getUser\", \"new\": \"fetchUser\"}";
        InterpretedIntent? result = GemmaIntentInterpreter.SanitizeAndParse(output);

        Assert.NotNull(result);
        Assert.Equal("replace", result.Intent);
        Assert.Equal("getUser", result.OldText);
        Assert.Equal("fetchUser", result.NewText);
    }

    [Fact]
    public void SanitizeAndParse_AppendIntent_ParsesText()
    {
        const string output = "{\"intent\": \"append\", \"text\": \"and add tests\"}";
        InterpretedIntent? result = GemmaIntentInterpreter.SanitizeAndParse(output);

        Assert.NotNull(result);
        Assert.Equal("append", result.Intent);
        Assert.Equal("and add tests", result.Text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("<think>reasoning only</think>")]
    [InlineData("{}")]
    public void SanitizeAndParse_InvalidOrMissingIntent_ReturnsNull(string modelOutput)
    {
        InterpretedIntent? result = GemmaIntentInterpreter.SanitizeAndParse(modelOutput);
        Assert.Null(result);
    }
}
