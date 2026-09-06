namespace Optimus.Providers.Tests;

using System.Collections.Generic;
using System.Threading;
using Optimus.Providers.Antigravity;
using Xunit;

public sealed class AntigravityChatDriverTests
{
    private sealed class FixtureProcess(IReadOnlyList<string> lines) : IStructuredChatProcess
    {
        public HeadlessProcessStart? Start { get; private set; }

        public async IAsyncEnumerable<string> RunAsync(HeadlessProcessStart start,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Start = start;
            foreach (string line in lines)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return line;
                await Task.Yield();
            }
        }
    }

    [Fact]
    public async Task Driver_SendsStructuredPromptAndParsesStreamEvents()
    {
        var process = new FixtureProcess([
            "{\"type\":\"system\",\"conversation_id\":\"conv-7\"}",
            "{\"type\":\"assistant\",\"delta\":\"Hello \"}",
            "{\"type\":\"assistant\",\"delta\":\"world\"}",
            "{\"type\":\"result\",\"result\":\"Hello world\"}"
        ]);
        var driver = new AntigravityChatDriver(process, new AntigravityChatDriverOptions("agy-fixture"));

        HeadlessChatResult result = await driver.SendAsync(new("Write the test", "Optimus", Model: "flash"));

        Assert.True(result.Succeeded);
        Assert.Equal("conv-7", result.ConversationId);
        Assert.Equal("Hello world", result.Text);
        Assert.NotNull(process.Start);
        Assert.Equal("agy-fixture", process.Start!.Executable);
        Assert.Contains("--input-format", process.Start.Arguments);
        Assert.Contains("stream-json", process.Start.Arguments);
        Assert.Contains("Write the test", process.Start.Input);
    }

    [Fact]
    public async Task Driver_ReportsStructuredErrorsWithoutThrowing()
    {
        var process = new FixtureProcess(["{\"type\":\"error\",\"error\":\"quota exceeded\"}"]);
        var driver = new AntigravityChatDriver(process);

        HeadlessChatResult result = await driver.SendAsync(new("Continue"));

        Assert.False(result.Succeeded);
        Assert.Equal("quota exceeded", result.Error);
        Assert.Contains(result.Events, item => item.Kind == HeadlessChatEventKind.Error);
    }
}
