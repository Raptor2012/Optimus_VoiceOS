namespace Optimus.Providers.Ao;

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Optimus.Providers.Windows;

public sealed class AoDestinationAdapter : IDestinationAdapter, IObservableDestinationAdapter
{
    private readonly IAoClient _aoClient;
    private readonly string _projectId;
    private string? _sessionId;
    private string? _lastActiveSessionId;
    private readonly string _displayName;

    public AoDestinationAdapter(
        IAoClient aoClient,
        string projectId,
        string? sessionId = null,
        string? displayName = null)
    {
        ArgumentNullException.ThrowIfNull(aoClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        _aoClient = aoClient;
        _projectId = projectId;
        _sessionId = sessionId;
        _lastActiveSessionId = sessionId;
        _displayName = displayName ?? (sessionId != null ? $"AO: {projectId} ({sessionId})" : $"AO: {projectId}");

        DestinationId = sessionId != null ? $"ao:{projectId}:{sessionId}" : $"ao:{projectId}";
    }

    public string DestinationId { get; }

    public string DisplayName => _displayName;

    public string ProcessName => "ao";

    public string ProjectId => _projectId;

    public string? SessionId => _sessionId;

    public WindowCandidate? BoundWindow => null;

    public DestinationStatus Probe()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(800));
            bool healthy = _aoClient.IsHealthyAsync(cts.Token).GetAwaiter().GetResult();
            if (!healthy)
            {
                return new DestinationStatus(
                    DestinationReadiness.NotRunning,
                    Array.Empty<WindowCandidate>(),
                    null,
                    $"AO daemon at {_aoClient.BaseUrl} is not running");
            }

            if (!string.IsNullOrWhiteSpace(_sessionId))
            {
                var session = _aoClient.GetSessionAsync(_sessionId, cts.Token).GetAwaiter().GetResult();
                if (session == null || session.IsTerminated)
                {
                    return new DestinationStatus(
                        DestinationReadiness.NotRunning,
                        Array.Empty<WindowCandidate>(),
                        null,
                        $"AO session {_sessionId} is not running");
                }
            }

            return new DestinationStatus(
                DestinationReadiness.Ready,
                Array.Empty<WindowCandidate>(),
                null,
                $"AO daemon ready at {_aoClient.BaseUrl}");
        }
        catch (Exception ex)
        {
            return new DestinationStatus(
                DestinationReadiness.NotRunning,
                Array.Empty<WindowCandidate>(),
                null,
                $"AO daemon probe failed: {ex.Message}");
        }
    }

    public void Bind(WindowCandidate candidate)
    {
        // AO destinations are session/service-bound, not window-bound.
    }

    public void Unbind()
    {
    }

    public void SetActiveSessionId(string sessionId)
    {
        _sessionId = sessionId;
        _lastActiveSessionId = sessionId;
    }

    public async Task<SendResult> SendAsync(ConfirmedDraft draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);

        if (!string.Equals(draft.DestinationId, DestinationId, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(draft.DestinationId, $"ao:{_projectId}", StringComparison.OrdinalIgnoreCase))
        {
            return new SendResult(
                SendStatus.NotReady,
                $"Draft was confirmed for '{draft.DestinationId}', not '{DestinationId}'.",
                0);
        }

        var stopwatch = Stopwatch.StartNew();
        string targetSessionId = _sessionId ?? _lastActiveSessionId ?? string.Empty;

        if (string.IsNullOrWhiteSpace(targetSessionId))
        {
            try
            {
                var sessions = await _aoClient.GetSessionsAsync(_projectId, cancellationToken).ConfigureAwait(false);
                if (sessions.Count > 0)
                {
                    AoSession? chosen = null;
                    foreach (var s in sessions)
                    {
                        if (string.Equals(s.Activity?.State, "active", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(s.Status, "working", StringComparison.OrdinalIgnoreCase))
                        {
                            chosen = s;
                            break;
                        }
                    }

                    chosen ??= sessions[0];
                    targetSessionId = chosen.Id;
                }
            }
            catch (Exception ex)
            {
                return new SendResult(
                    SendStatus.Failed,
                    $"Could not resolve session for AO project {_projectId}: {ex.Message}",
                    stopwatch.ElapsedMilliseconds);
            }
        }

        if (string.IsNullOrWhiteSpace(targetSessionId))
        {
            return new SendResult(
                SendStatus.NotReady,
                $"No active session found for AO project {_projectId}.",
                stopwatch.ElapsedMilliseconds);
        }

        _lastActiveSessionId = targetSessionId;

        // Idempotency token for confirmed voice commands
        string clientMessageId = $"voice-{draft.ConfirmedAtUtc.ToUnixTimeMilliseconds()}-{Guid.NewGuid():N}";

        try
        {
            var response = await _aoClient.SendSessionMessageAsync(targetSessionId, draft.Text, clientMessageId, cancellationToken).ConfigureAwait(false);
            if (response != null && response.Ok)
            {
                return new SendResult(
                    SendStatus.Sent,
                    $"Prompt sent to AO session {targetSessionId}.",
                    stopwatch.ElapsedMilliseconds);
            }

            return new SendResult(
                SendStatus.Failed,
                $"AO session {targetSessionId} rejected the message.",
                stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            return new SendResult(
                SendStatus.Failed,
                $"Failed sending to AO session {targetSessionId}: {ex.Message}",
                stopwatch.ElapsedMilliseconds);
        }
    }

    public IAgentObserver CreateObserver()
    {
        string sessionId = _lastActiveSessionId ?? _sessionId ?? string.Empty;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                var sessions = _aoClient.GetSessionsAsync(_projectId, cts.Token).GetAwaiter().GetResult();
                if (sessions.Count > 0)
                {
                    sessionId = sessions[0].Id;
                    _lastActiveSessionId = sessionId;
                }
            }
            catch
            {
                // Fall back to project-1 convention
                sessionId = $"{_projectId}-1";
            }
        }

        return new AoSessionObserver(_aoClient, sessionId);
    }
}
