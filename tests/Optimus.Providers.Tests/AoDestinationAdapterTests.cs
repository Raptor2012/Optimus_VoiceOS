namespace Optimus.Providers.Tests;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Optimus.Providers;
using Optimus.Providers.Ao;
using Xunit;

public class AoDestinationAdapterTests
{
    private sealed class FakeAoClient : IAoClient
    {
        public string BaseUrl => "http://127.0.0.1:3001";
        public List<AoProject> Projects { get; set; } = new();
        public List<AoSession> Sessions { get; set; } = new();
        public List<(string sessionId, string text, string? clientMsgId)> SentMessages { get; } = new();

        public Task<bool> IsHealthyAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<IReadOnlyList<AoProject>> GetProjectsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AoProject>>(Projects);

        public Task<AoProject?> GetProjectAsync(string projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult<AoProject?>(null);

        public Task<IReadOnlyList<AoSession>> GetSessionsAsync(string? projectId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AoSession>>(Sessions);

        public Task<AoSession?> GetSessionAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Sessions.Find(s => s.Id == sessionId));

        public Task<AoConversationResponse?> GetSessionConversationAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<AoConversationResponse?>(new AoConversationResponse());

        public Task<AoSendMessageResponse?> SendSessionMessageAsync(string sessionId, string message, string? clientMessageId = null, CancellationToken cancellationToken = default)
        {
            SentMessages.Add((sessionId, message, clientMessageId));
            return Task.FromResult<AoSendMessageResponse?>(new AoSendMessageResponse(Ok: true, SessionId: sessionId));
        }

        public Task<bool> ResolveApprovalAsync(string sessionId, string requestId, string decisionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<bool> InterruptAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public IAsyncEnumerable<AoCdcEvent> StreamEventsAsync(long? afterSeq = null, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }

    [Fact]
    public void Constructor_SetsDestinationProperties()
    {
        var client = new FakeAoClient();
        var adapter = new AoDestinationAdapter(client, "proj-1", "sess-1", "AO · Voice-to-AO int");

        Assert.Equal("ao:proj-1:sess-1", adapter.DestinationId);
        Assert.Equal("AO · Voice-to-AO int", adapter.DisplayName);
        Assert.Equal("ao", adapter.ProcessName);
        Assert.Equal("sess-1", adapter.SessionId);
        Assert.Equal("proj-1", adapter.ProjectId);
    }

    [Fact]
    public void Probe_ReturnsReadyWhenSessionActive()
    {
        var client = new FakeAoClient
        {
            Sessions = new List<AoSession>
            {
                new("sess-1", "proj-1", "worker", "gemini", DisplayName: "Active Session", Status: "running")
            }
        };

        var adapter = new AoDestinationAdapter(client, "proj-1", "sess-1", "Active Session");
        DestinationStatus status = adapter.Probe();

        Assert.Equal(DestinationReadiness.Ready, status.Readiness);
        Assert.True(status.CanSend);
    }

    [Fact]
    public void Probe_ReturnsNotRunningWhenSessionNotFound()
    {
        var client = new FakeAoClient
        {
            Sessions = new List<AoSession>()
        };

        var adapter = new AoDestinationAdapter(client, "proj-1", "sess-99", "Missing Session");
        DestinationStatus status = adapter.Probe();

        Assert.Equal(DestinationReadiness.NotRunning, status.Readiness);
        Assert.False(status.CanSend);
    }

    [Fact]
    public async Task SendAsync_RejectsMismatchDestination()
    {
        var client = new FakeAoClient
        {
            Sessions = new List<AoSession>
            {
                new("sess-1", "proj-1", "worker", "gemini", Status: "running")
            }
        };

        var adapter = new AoDestinationAdapter(client, "proj-1", "sess-1", "Test");
        SendResult result = await adapter.SendAsync(new ConfirmedDraft("hello", "claude"));

        Assert.False(result.Succeeded);
        Assert.Equal(SendStatus.NotReady, result.Status);
        Assert.Empty(client.SentMessages);
    }

    [Fact]
    public async Task SendAsync_DeliversMessageWithUniqueClientMessageId()
    {
        var client = new FakeAoClient
        {
            Sessions = new List<AoSession>
            {
                new("sess-1", "proj-1", "worker", "gemini", Status: "running")
            }
        };

        var adapter = new AoDestinationAdapter(client, "proj-1", "sess-1", "Test");
        SendResult result = await adapter.SendAsync(new ConfirmedDraft("Refactor tests now", "ao:proj-1:sess-1"));

        Assert.True(result.Succeeded);
        Assert.Equal(SendStatus.Sent, result.Status);
        Assert.Single(client.SentMessages);
        Assert.Equal("sess-1", client.SentMessages[0].sessionId);
        Assert.Equal("Refactor tests now", client.SentMessages[0].text);
        Assert.False(string.IsNullOrWhiteSpace(client.SentMessages[0].clientMsgId));
    }

    [Fact]
    public void CreateObserver_ReturnsValidObserver()
    {
        var client = new FakeAoClient();
        var adapter = new AoDestinationAdapter(client, "proj-1", "sess-1", "Test");

        IAgentObserver observer = adapter.CreateObserver();
        Assert.NotNull(observer);
        Assert.Equal(0, observer.CapturedNodeCount);
    }
}
