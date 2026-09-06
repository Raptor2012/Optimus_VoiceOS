namespace Optimus.Providers.Tests;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Optimus.Providers.Ao;
using Xunit;

public class AoProjectBridgeTests
{
    private sealed class FakeAoClient : IAoClient
    {
        public string BaseUrl => "http://localhost:3001";

        public List<AoProject> Projects { get; set; } = new();
        public List<AoSession> Sessions { get; set; } = new();

        public Task<bool> IsHealthyAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<IReadOnlyList<AoProject>> GetProjectsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AoProject>>(Projects);

        public Task<AoProject?> GetProjectAsync(string projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Projects.Find(p => p.Id == projectId));

        public Task<IReadOnlyList<AoSession>> GetSessionsAsync(string? projectId = null, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(projectId))
                return Task.FromResult<IReadOnlyList<AoSession>>(Sessions);

            return Task.FromResult<IReadOnlyList<AoSession>>(
                Sessions.FindAll(s => string.Equals(s.ProjectId, projectId, StringComparison.OrdinalIgnoreCase)));
        }

        public Task<AoSession?> GetSessionAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Sessions.Find(s => s.Id == sessionId));

        public Task<AoConversationResponse?> GetSessionConversationAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<AoConversationResponse?>(null);

        public Task<AoSendMessageResponse?> SendSessionMessageAsync(string sessionId, string message, string? clientMessageId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<AoSendMessageResponse?>(new AoSendMessageResponse(Ok: true, SessionId: sessionId));

        public Task<bool> ResolveApprovalAsync(string sessionId, string requestId, string decisionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<bool> InterruptAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public async IAsyncEnumerable<AoCdcEvent> StreamEventsAsync(long? afterSeq = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    [Fact]
    public async Task GetLiveProjectCardsAsync_MapsProjectsAndSessionsCorrectly()
    {
        var fakeClient = new FakeAoClient
        {
            Projects = new List<AoProject>
            {
                new("optimus_voiceos", "Optimus Voice OS", "C:\\repo", "single_repo", "optimus_voic", "claude-code")
            },
            Sessions = new List<AoSession>
            {
                new("optimus_voiceos-1", "optimus_voiceos", "orchestrator", "claude-code", "Orchestrator", "opus", "idle", "Awaiting PR"),
                new("optimus_voiceos-2", "optimus_voiceos", "worker", "agy", "Design tokens+theme", "gemini-3.8-flash-high", "merged", "Merged"),
                new("optimus_voiceos-5", "optimus_voiceos", "worker", "agy", "Live project exp", "gemini-3.8-flash-high", "working", "Working",
                    Activity: new AoSessionActivity("active", DateTimeOffset.UtcNow))
            }
        };

        var bridge = new AoProjectBridge(fakeClient);
        var cards = await bridge.GetLiveProjectCardsAsync();

        Assert.Single(cards);
        var card = cards[0];
        Assert.Equal("optimus_voiceos", card.Id);
        Assert.Equal("Optimus Voice OS", card.Name);
        Assert.Equal("ao:optimus_voiceos", card.DestinationId);
        Assert.Equal(3, card.TotalTasks);
        Assert.Equal(1, card.CompletedTasks); // 1 merged task
        Assert.Single(card.ActiveWorkers); // optimus_voiceos-5 is active worker
        Assert.Equal("Live project exp", card.ActiveWorkers[0].Name);
        Assert.Equal(3, card.Tasks.Count);
        Assert.Single(card.Conversations); // 1 orchestrator conversation
    }
}
