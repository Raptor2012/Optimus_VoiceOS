namespace Optimus.Core.Tests.Memory;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Optimus.Core.Memory;
using Xunit;

public sealed class SummaryGeneratorTests : IDisposable
{
    private readonly MemoryStore _memoryStore;

    public SummaryGeneratorTests()
    {
        _memoryStore = MemoryStore.CreateInMemory();
    }

    public void Dispose()
    {
        _memoryStore.Dispose();
    }

    [Fact]
    public async Task GenerateSummaryAsync_SummarizesTurns_AndSavesToStore()
    {
        var now = DateTimeOffset.UtcNow;
        _memoryStore.SaveTurn("t1", "pc", "Run unit tests", objective: "test run", response: "all passed", timestamp: now.AddHours(-2));
        _memoryStore.SaveTurn("t2", "pixel", "Check git status", objective: "status", response: "clean working tree", timestamp: now.AddHours(-1));

        string? promptReceived = null;
        using var generator = new SummaryGenerator(
            _memoryStore,
            summarizeDelegate: (history, ct) =>
            {
                promptReceived = history;
                return Task.FromResult("Completed test run and checked repository status with clean working tree.");
            });

        SummaryRecord? summary = await generator.GenerateSummaryAsync(
            periodStart: now.AddHours(-3),
            periodEnd: now);

        Assert.NotNull(summary);
        Assert.Equal("Completed test run and checked repository status with clean working tree.", summary.SummaryText);
        Assert.NotNull(promptReceived);
        Assert.Contains("Run unit tests", promptReceived);
        Assert.Contains("Check git status", promptReceived);

        // Verify stored in DB
        IReadOnlyList<SummaryRecord> stored = _memoryStore.GetSummaries(5);
        Assert.Single(stored);
        Assert.Equal("Completed test run and checked repository status with clean working tree.", stored[0].SummaryText);
    }

    [Fact]
    public async Task GenerateSummaryAsync_NoTurns_ReturnsNull()
    {
        var now = DateTimeOffset.UtcNow;
        using var generator = new SummaryGenerator(
            _memoryStore,
            summarizeDelegate: (history, ct) => Task.FromResult("Should not be called"));

        SummaryRecord? summary = await generator.GenerateSummaryAsync(
            periodStart: now.AddHours(-1),
            periodEnd: now);

        Assert.Null(summary);
        Assert.Empty(_memoryStore.GetSummaries(5));
    }
}
