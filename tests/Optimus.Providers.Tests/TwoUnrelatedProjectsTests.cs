namespace Optimus.Providers.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Optimus.Providers;
using Optimus.Providers.Ao;
using Optimus.Providers.Windows;
using Xunit;

public sealed class TwoUnrelatedProjectsTests
{
    private sealed class MultiProjectAoClient : IAoClient
    {
        public string BaseUrl => "http://127.0.0.1:3001";

        public List<AoProject> Projects { get; set; } = new();
        public List<AoSession> Sessions { get; set; } = new();
        public Dictionary<string, AoConversationResponse> Conversations { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public List<(string SessionId, string Message, string? ClientMessageId)> SentMessages { get; } = new();

        public Task<bool> IsHealthyAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<IReadOnlyList<AoProject>> GetProjectsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AoProject>>(Projects);

        public Task<AoProject?> GetProjectAsync(string projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Projects.Find(p => string.Equals(p.Id, projectId, StringComparison.OrdinalIgnoreCase)));

        public Task<IReadOnlyList<AoSession>> GetSessionsAsync(string? projectId = null, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(projectId))
            {
                return Task.FromResult<IReadOnlyList<AoSession>>(Sessions);
            }

            var filtered = Sessions.Where(s => string.Equals(s.ProjectId, projectId, StringComparison.OrdinalIgnoreCase)).ToList();
            return Task.FromResult<IReadOnlyList<AoSession>>(filtered);
        }

        public Task<AoSession?> GetSessionAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Sessions.Find(s => string.Equals(s.Id, sessionId, StringComparison.OrdinalIgnoreCase)));

        public Task<AoConversationResponse?> GetSessionConversationAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            Conversations.TryGetValue(sessionId, out var conv);
            return Task.FromResult(conv);
        }

        public Task<AoSendMessageResponse?> SendSessionMessageAsync(
            string sessionId,
            string message,
            string? clientMessageId = null,
            CancellationToken cancellationToken = default)
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
    public async Task RegisterAndOperate_TwoUnrelatedProjects_WithoutCodeChanges()
    {
        // Two completely unrelated projects with distinct IDs, names, paths, and harnesses
        var alphaProject = new AoProject("alpha_service", "Alpha Backend Service", "/repos/alpha-service", "go");
        var betaProject = new AoProject("beta_portal", "Beta Web Portal", "/repos/beta-portal", "react");

        var alphaSession = new AoSession(
            Id: "alpha_service-1",
            ProjectId: "alpha_service",
            Kind: "worker",
            Harness: "claude",
            DisplayName: "Alpha API Endpoint",
            Status: "working",
            Activity: new AoSessionActivity("active")
        );

        var betaSession = new AoSession(
            Id: "beta_portal-1",
            ProjectId: "beta_portal",
            Kind: "worker",
            Harness: "codex",
            DisplayName: "Beta UI Components",
            Status: "working",
            Activity: new AoSessionActivity("active")
        );

        var client = new MultiProjectAoClient
        {
            Projects = new List<AoProject> { alphaProject, betaProject },
            Sessions = new List<AoSession> { alphaSession, betaSession },
            Conversations = new Dictionary<string, AoConversationResponse>
            {
                ["alpha_service-1"] = new(
                    SessionId: "alpha_service-1",
                    Messages: new[]
                    {
                        new AoConversationMessage("m1", 1, "assistant", Text: "Alpha backend test suite passing.")
                    }
                ),
                ["beta_portal-1"] = new(
                    SessionId: "beta_portal-1",
                    Messages: new[]
                    {
                        new AoConversationMessage("m2", 1, "assistant", Text: "Beta React buttons rendered.")
                    }
                )
            }
        };

        // 1. DestinationRegistry discovers both unrelated projects dynamically without any code change
        var registry = new DestinationRegistry(client);
        await registry.RefreshAoDestinationsAsync();

        // Verify destination adapters were registered for both projects and sessions
        IDestinationAdapter? alphaAdapter = registry.Find("ao:alpha_service");
        IDestinationAdapter? betaAdapter = registry.Find("ao:beta_portal");
        IDestinationAdapter? alphaSessionAdapter = registry.Find("ao:alpha_service:alpha_service-1");
        IDestinationAdapter? betaSessionAdapter = registry.Find("ao:beta_portal:beta_portal-1");

        Assert.NotNull(alphaAdapter);
        Assert.NotNull(betaAdapter);
        Assert.NotNull(alphaSessionAdapter);
        Assert.NotNull(betaSessionAdapter);

        // 2. Both destinations probe as Ready independently
        DestinationStatus alphaStatus = alphaAdapter.Probe();
        DestinationStatus betaStatus = betaAdapter.Probe();

        Assert.True(alphaStatus.CanSend);
        Assert.True(betaStatus.CanSend);
        Assert.Contains("ready", alphaStatus.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ready", betaStatus.Detail, StringComparison.OrdinalIgnoreCase);

        // 3. Send confirmed prompts to both projects independently
        var alphaDraft = new ConfirmedDraft("Implement JWT authentication in Go", "ao:alpha_service");
        var betaDraft = new ConfirmedDraft("Implement dark theme toggle in React", "ao:beta_portal");

        SendResult alphaResult = await alphaAdapter.SendAsync(alphaDraft);
        SendResult betaResult = await betaAdapter.SendAsync(betaDraft);

        Assert.True(alphaResult.Succeeded);
        Assert.True(betaResult.Succeeded);

        // Verify sent messages routed to their respective sessions
        Assert.Equal(2, client.SentMessages.Count);
        Assert.Equal("alpha_service-1", client.SentMessages[0].SessionId);
        Assert.Equal("Implement JWT authentication in Go", client.SentMessages[0].Message);

        Assert.Equal("beta_portal-1", client.SentMessages[1].SessionId);
        Assert.Equal("Implement dark theme toggle in React", client.SentMessages[1].Message);

        // 4. Observe both sessions independently
        var alphaObserver = (AoSessionObserver)((IObservableDestinationAdapter)alphaSessionAdapter).CreateObserver();
        var betaObserver = (AoSessionObserver)((IObservableDestinationAdapter)betaSessionAdapter).CreateObserver();

        IReadOnlyList<VisibleAgentUpdate> alphaUpdates = alphaObserver.Poll();
        IReadOnlyList<VisibleAgentUpdate> betaUpdates = betaObserver.Poll();

        Assert.Single(alphaUpdates);
        Assert.Equal("Alpha backend test suite passing.", alphaUpdates[0].Text);

        Assert.Single(betaUpdates);
        Assert.Equal("Beta React buttons rendered.", betaUpdates[0].Text);

        // 5. AoProjectBridge produces project cards for both projects
        var bridge = new AoProjectBridge(client);
        IReadOnlyList<AoProjectCardItem> cards = await bridge.GetLiveProjectCardsAsync();

        Assert.Equal(2, cards.Count);
        Assert.Contains(cards, c => c.Id == "alpha_service" && c.Name == "Alpha Backend Service");
        Assert.Contains(cards, c => c.Id == "beta_portal" && c.Name == "Beta Web Portal");
    }
}
