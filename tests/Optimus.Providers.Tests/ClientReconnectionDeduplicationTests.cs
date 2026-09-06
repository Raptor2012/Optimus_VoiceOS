namespace Optimus.Providers.Tests;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Optimus.Providers.Ao;
using Optimus.Providers.Windows;
using Xunit;

public sealed class ClientReconnectionDeduplicationTests
{
    private sealed class ReconnectingAoClient : IAoClient
    {
        public string BaseUrl => "http://127.0.0.1:3001";

        public HashSet<string> SeenClientMessageIds { get; } = new(StringComparer.Ordinal);
        public List<string> ReceivedMessages { get; } = new();
        public HashSet<string> ResolvedApprovals { get; } = new(StringComparer.Ordinal);
        public int SendCallCount { get; private set; }
        public int DuplicateCount { get; private set; }

        public AoConversationResponse Conversation { get; set; } = new();

        public Task<bool> IsHealthyAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<IReadOnlyList<AoProject>> GetProjectsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AoProject>>(new[] { new AoProject("proj-1", "Project 1", "/p1") });

        public Task<AoProject?> GetProjectAsync(string projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult<AoProject?>(new AoProject("proj-1", "Project 1", "/p1"));

        public Task<IReadOnlyList<AoSession>> GetSessionsAsync(string? projectId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AoSession>>(new[]
            {
                new AoSession("sess-1", "proj-1", "worker", "codex", Status: "working", Activity: new AoSessionActivity("active"))
            });

        public Task<AoSession?> GetSessionAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<AoSession?>(new AoSession(sessionId, "proj-1", "worker", "codex"));

        public Task<AoConversationResponse?> GetSessionConversationAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<AoConversationResponse?>(Conversation);

        public Task<AoSendMessageResponse?> SendSessionMessageAsync(
            string sessionId,
            string message,
            string? clientMessageId = null,
            CancellationToken cancellationToken = default)
        {
            SendCallCount++;

            if (!string.IsNullOrWhiteSpace(clientMessageId) && !SeenClientMessageIds.Add(clientMessageId))
            {
                DuplicateCount++;
                return Task.FromResult<AoSendMessageResponse?>(new AoSendMessageResponse(
                    Ok: true,
                    SessionId: sessionId,
                    Duplicate: true,
                    Message: "Message was already processed."
                ));
            }

            ReceivedMessages.Add(message);
            return Task.FromResult<AoSendMessageResponse?>(new AoSendMessageResponse(
                Ok: true,
                SessionId: sessionId,
                Duplicate: false
            ));
        }

        public Task<bool> ResolveApprovalAsync(string sessionId, string requestId, string decisionId, CancellationToken cancellationToken = default)
        {
            string key = $"{sessionId}:{requestId}";
            if (!ResolvedApprovals.Add(key))
            {
                // Already resolved; duplicate approval resolution is harmless and returns true
                return Task.FromResult(true);
            }
            return Task.FromResult(true);
        }

        public Task<bool> InterruptAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public IAsyncEnumerable<AoCdcEvent> StreamEventsAsync(long? afterSeq = null, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }

    [Fact]
    public async Task Reconnection_DoesNotSendDuplicateMessages_WhenClientMessageIdIsReused()
    {
        var client = new ReconnectingAoClient();
        string clientMessageId = "voice-test-msg-12345";

        // First send
        var resp1 = await client.SendSessionMessageAsync("sess-1", "Hello agent", clientMessageId);
        Assert.NotNull(resp1);
        Assert.True(resp1.Ok);
        Assert.False(resp1.Duplicate);
        Assert.Single(client.ReceivedMessages);

        // Reconnect: client retries the same message with the same clientMessageId
        var resp2 = await client.SendSessionMessageAsync("sess-1", "Hello agent", clientMessageId);
        Assert.NotNull(resp2);
        Assert.True(resp2.Ok);
        Assert.True(resp2.Duplicate); // Server acknowledged deduplication!
        Assert.Single(client.ReceivedMessages); // No duplicate entry in ReceivedMessages!
        Assert.Equal(1, client.DuplicateCount);
    }

    [Fact]
    public async Task Reconnection_DoesNotDoubleResolveApprovals()
    {
        var client = new ReconnectingAoClient();

        // User approves plan revision
        bool res1 = await client.ResolveApprovalAsync("sess-1", "req-plan-1", "decision-approve");
        Assert.True(res1);
        Assert.Single(client.ResolvedApprovals);

        // Reconnect: phone or widget reconnects and repeats resolution
        bool res2 = await client.ResolveApprovalAsync("sess-1", "req-plan-1", "decision-approve");
        Assert.True(res2);
        Assert.Single(client.ResolvedApprovals); // Idempotent; only resolved once!
    }

    [Fact]
    public void Reconnection_DoesNotEmitDuplicateNarration()
    {
        var client = new ReconnectingAoClient
        {
            Conversation = new AoConversationResponse(
                SessionId: "sess-1",
                Messages: new[]
                {
                    new AoConversationMessage("m1", 1, "assistant", Text: "Running unit test suite"),
                    new AoConversationMessage("m2", 2, "assistant", Text: "All 504 tests passed.")
                },
                Activities: new[]
                {
                    new AoConversationActivity("act1", "tool_call", Delta: "dotnet test")
                }
            )
        };

        var observer = new AoSessionObserver(client, "sess-1");

        // Initial poll before reconnection: emits 3 updates
        IReadOnlyList<VisibleAgentUpdate> initialUpdates = observer.Poll();
        Assert.Equal(3, initialUpdates.Count);

        // Client disconnects and reconnects. Observer polls again with the exact same conversation:
        IReadOnlyList<VisibleAgentUpdate> reconnectedUpdates = observer.Poll();

        // All previously emitted items are deduplicated; 0 duplicates emitted!
        Assert.Empty(reconnectedUpdates);

        // When a brand new message arrives, only the new message is emitted
        client.Conversation = new AoConversationResponse(
            SessionId: "sess-1",
            Messages: new[]
            {
                new AoConversationMessage("m1", 1, "assistant", Text: "Running unit test suite"),
                new AoConversationMessage("m2", 2, "assistant", Text: "All 504 tests passed."),
                new AoConversationMessage("m3", 3, "assistant", Text: "Build completed successfully.")
            }
        );

        IReadOnlyList<VisibleAgentUpdate> newUpdates = observer.Poll();
        Assert.Single(newUpdates);
        Assert.Equal("Build completed successfully.", newUpdates[0].Text);
    }
}
