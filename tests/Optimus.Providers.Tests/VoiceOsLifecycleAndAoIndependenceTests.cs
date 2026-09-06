namespace Optimus.Providers.Tests;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Optimus.Providers;
using Optimus.Providers.Ao;
using Xunit;

public sealed class VoiceOsLifecycleAndAoIndependenceTests
{
    private sealed class PersistentAoDaemonSimulator : IAoClient
    {
        public string BaseUrl => "http://127.0.0.1:3001";

        public bool DaemonRunning { get; set; } = true;
        public Dictionary<string, AoSession> ActiveSessions { get; } = new(StringComparer.OrdinalIgnoreCase);

        public PersistentAoDaemonSimulator()
        {
            // Seed a long-running background worker session
            ActiveSessions["optimus_voiceos-longrun"] = new AoSession(
                Id: "optimus_voiceos-longrun",
                ProjectId: "optimus_voiceos",
                Kind: "worker",
                Harness: "codex",
                DisplayName: "Background Build Worker",
                Status: "working",
                Activity: new AoSessionActivity("active", DateTimeOffset.UtcNow),
                IsTerminated: false
            );
        }

        public Task<bool> IsHealthyAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(DaemonRunning);

        public Task<IReadOnlyList<AoProject>> GetProjectsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AoProject>>(new[]
            {
                new AoProject("optimus_voiceos", "Optimus VoiceOS", "/path")
            });

        public Task<AoProject?> GetProjectAsync(string projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult<AoProject?>(new AoProject("optimus_voiceos", "Optimus VoiceOS", "/path"));

        public Task<IReadOnlyList<AoSession>> GetSessionsAsync(string? projectId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AoSession>>(ActiveSessions.Values.ToArray());

        public Task<AoSession?> GetSessionAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            ActiveSessions.TryGetValue(sessionId, out var s);
            return Task.FromResult(s);
        }

        public Task<AoConversationResponse?> GetSessionConversationAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<AoConversationResponse?>(new AoConversationResponse(SessionId: sessionId));

        public Task<AoSendMessageResponse?> SendSessionMessageAsync(
            string sessionId,
            string message,
            string? clientMessageId = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<AoSendMessageResponse?>(new AoSendMessageResponse(Ok: true, SessionId: sessionId));

        public Task<bool> ResolveApprovalAsync(string sessionId, string requestId, string decisionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<bool> InterruptAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public IAsyncEnumerable<AoCdcEvent> StreamEventsAsync(long? afterSeq = null, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }

    [Fact]
    public async Task StopVoiceOs_WhileAoWorkContinues_LeavesAoSessionRunning()
    {
        // Persistent AO daemon simulator represents the independent background AO service
        var aoDaemon = new PersistentAoDaemonSimulator();

        // Step 1: Voice OS launches and connects to AO
        DestinationRegistry? voiceOsDestinations = new DestinationRegistry(aoDaemon);
        await voiceOsDestinations.RefreshAoDestinationsAsync();

        IDestinationAdapter? adapter = voiceOsDestinations.Find("ao:optimus_voiceos:optimus_voiceos-longrun");
        Assert.NotNull(adapter);
        Assert.True(adapter.Probe().CanSend);

        // Step 2: Stop Voice OS completely (simulate App.OnExit / disposal of Voice OS shell)
        // Disposing Voice OS client structures must NOT affect the background AO worker session
        voiceOsDestinations = null;
        GC.Collect();
        GC.WaitForPendingFinalizers();

        // Step 3: Verify AO daemon and worker session continue running untouched
        Assert.True(aoDaemon.DaemonRunning);
        AoSession? sessionDuringVoiceOsStop = await aoDaemon.GetSessionAsync("optimus_voiceos-longrun");
        Assert.NotNull(sessionDuringVoiceOsStop);
        Assert.False(sessionDuringVoiceOsStop.IsTerminated);
        Assert.Equal("working", sessionDuringVoiceOsStop.Status);
        Assert.Equal("active", sessionDuringVoiceOsStop.Activity?.State);

        // Step 4: Voice OS restarts later and reconnects to AO seamlessly
        var newVoiceOsDestinations = new DestinationRegistry(aoDaemon);
        await newVoiceOsDestinations.RefreshAoDestinationsAsync();

        IDestinationAdapter? reconnectedAdapter = newVoiceOsDestinations.Find("ao:optimus_voiceos:optimus_voiceos-longrun");
        Assert.NotNull(reconnectedAdapter);
        Assert.True(reconnectedAdapter.Probe().CanSend);
    }
}
