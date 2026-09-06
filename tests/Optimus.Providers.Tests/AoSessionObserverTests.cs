namespace Optimus.Providers.Tests;

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Optimus.Providers;
using Optimus.Providers.Ao;
using Optimus.Providers.Windows;
using Xunit;

public class AoSessionObserverTests
{
    private sealed class FakeAoClient : IAoClient
    {
        public string BaseUrl => "http://127.0.0.1:3001";
        public AoConversationResponse? ConversationResponse { get; set; }

        public Task<bool> IsHealthyAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<IReadOnlyList<AoProject>> GetProjectsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AoProject>>(Array.Empty<AoProject>());

        public Task<AoProject?> GetProjectAsync(string projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult<AoProject?>(null);

        public Task<IReadOnlyList<AoSession>> GetSessionsAsync(string? projectId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AoSession>>(Array.Empty<AoSession>());

        public Task<AoSession?> GetSessionAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<AoSession?>(null);

        public Task<AoConversationResponse?> GetSessionConversationAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(ConversationResponse);

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
    public void Poll_ExtractsToolCallsAndAssistantResponses()
    {
        using var toolDoc = JsonDocument.Parse("{\"toolName\":\"run_command\"}");
        using var cmdDoc = JsonDocument.Parse("{\"command\":\"dotnet test\"}");

        var client = new FakeAoClient
        {
            ConversationResponse = new AoConversationResponse
            {
                SessionId = "sess-1",
                Activities = new List<AoConversationActivity>
                {
                    new(
                        Id: "act-1",
                        Kind: "tool_call",
                        Detail: toolDoc.RootElement
                    ),
                    new(
                        Id: "act-2",
                        Kind: "tool_call",
                        Detail: cmdDoc.RootElement
                    ),
                    new(
                        Id: "act-3",
                        Kind: "delta",
                        Delta: "Building project..."
                    )
                },
                Messages = new List<AoConversationMessage>
                {
                    new(
                        Id: "msg-1",
                        Sequence: 1,
                        Role: "assistant",
                        Text: "I have verified all unit tests pass."
                    )
                }
            }
        };

        var observer = new AoSessionObserver(client, "sess-1");
        var updates = observer.Poll();

        Assert.Equal(4, updates.Count);
        Assert.Equal(VisibleAgentActivity.ToolOrSkill, updates[0].Activity);
        Assert.Equal("Tool: run_command", updates[0].Text);

        Assert.Equal(VisibleAgentActivity.ToolOrSkill, updates[1].Activity);
        Assert.Equal("Running: dotnet test", updates[1].Text);

        Assert.Equal(VisibleAgentActivity.Progress, updates[2].Activity);
        Assert.Equal("Building project...", updates[2].Text);

        Assert.Equal(VisibleAgentActivity.FinalResponseCandidate, updates[3].Activity);
        Assert.Equal("I have verified all unit tests pass.", updates[3].Text);
    }

    [Fact]
    public void Poll_DeduplicatesAcrossMultipleCycles()
    {
        var activities = new List<AoConversationActivity>
        {
            new(
                Id: "act-1",
                Kind: "delta",
                Delta: "Reading files..."
            )
        };

        var messages = new List<AoConversationMessage>
        {
            new(
                Id: "msg-1",
                Sequence: 1,
                Role: "assistant",
                Text: "Initial message"
            )
        };

        var conversation = new AoConversationResponse
        {
            SessionId = "sess-1",
            Activities = activities,
            Messages = messages
        };

        var client = new FakeAoClient { ConversationResponse = conversation };
        var observer = new AoSessionObserver(client, "sess-1");

        // First poll: 2 items
        var first = observer.Poll();
        Assert.Equal(2, first.Count);

        // Second poll with same conversation data: 0 new items
        var second = observer.Poll();
        Assert.Empty(second);

        // Third poll with 1 new activity: only 1 new item returned
        activities.Add(new AoConversationActivity(
            Id: "act-2",
            Kind: "delta",
            Delta: "Finished view"
        ));

        var third = observer.Poll();
        Assert.Single(third);
        Assert.Equal("Finished view", third[0].Text);
    }
}
