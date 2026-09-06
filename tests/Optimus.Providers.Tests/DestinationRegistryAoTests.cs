namespace Optimus.Providers.Tests;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Optimus.Providers;
using Optimus.Providers.Ao;
using Xunit;

public class DestinationRegistryAoTests
{
    private sealed class FakeAoClient : IAoClient
    {
        public string BaseUrl => "http://127.0.0.1:3001";
        public List<AoSession> Sessions { get; set; } = new();

        public Task<bool> IsHealthyAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<IReadOnlyList<AoProject>> GetProjectsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AoProject>>(Array.Empty<AoProject>());

        public Task<AoProject?> GetProjectAsync(string projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult<AoProject?>(null);

        public Task<IReadOnlyList<AoSession>> GetSessionsAsync(string? projectId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AoSession>>(Sessions);

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

        public IAsyncEnumerable<AoCdcEvent> StreamEventsAsync(long? afterSeq = null, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }

    [Fact]
    public void Find_AutoRegistersAoTargetOnDemand()
    {
        var client = new FakeAoClient();
        var registry = new DestinationRegistry(client);

        IDestinationAdapter? adapter = registry.Find("ao:proj-1:sess-1");
        Assert.NotNull(adapter);
        Assert.Equal("ao:proj-1:sess-1", adapter.DestinationId);
        Assert.Contains(adapter, registry.Adapters);
    }

    [Fact]
    public async Task RefreshAoDestinationsAsync_RegistersDiscoveredSessions()
    {
        var client = new FakeAoClient
        {
            Sessions = new List<AoSession>
            {
                new("sess-1", "proj-1", "worker", "gemini", DisplayName: "Session One", Status: "running"),
                new("sess-2", "proj-1", "worker", "gemini", DisplayName: "Session Two", Status: "running")
            }
        };

        var registry = new DestinationRegistry(client);
        Assert.Equal(3, registry.Adapters.Count); // Default 3 Windows adapters

        await registry.RefreshAoDestinationsAsync();

        // 3 Windows + 2 AO sessions = 5
        Assert.Equal(5, registry.Adapters.Count);
        Assert.NotNull(registry.Find("ao:proj-1:sess-1"));
        Assert.NotNull(registry.Find("ao:proj-1:sess-2"));
    }
}
