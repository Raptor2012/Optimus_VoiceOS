namespace Optimus.Providers.Ao;

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Optimus.Providers.Windows;

public sealed class AoDestinationAdapter : IDestinationAdapter
{
    private readonly IAoClient _aoClient;
    private readonly string _projectId;
    private string? _sessionId;
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
            // Quick synchronous check with short timeout
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            bool healthy = _aoClient.IsHealthyAsync(cts.Token).GetAwaiter().GetResult();
            if (healthy)
            {
                return new DestinationStatus(
                    DestinationReadiness.Ready,
                    Array.Empty<WindowCandidate>(),
                    null,
                    $"AO daemon ready at {_aoClient.BaseUrl}");
            }

            return new DestinationStatus(
                DestinationReadiness.NotRunning,
                Array.Empty<WindowCandidate>(),
                null,
                $"AO daemon at {_aoClient.BaseUrl} is not ready");
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
    }

    public async Task<SendResult> SendAsync(ConfirmedDraft draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var stopwatch = Stopwatch.StartNew();

        string targetSessionId = _sessionId ?? string.Empty;
        if (string.IsNullOrWhiteSpace(targetSessionId))
        {
            // Resolve latest active session for this project
            try
            {
                var sessions = await _aoClient.GetSessionsAsync(_projectId, cancellationToken).ConfigureAwait(false);
                if (sessions.Count > 0)
                {
                    // Prefer working/active session, or most recent
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

        try
        {
            bool ok = await _aoClient.SendSessionMessageAsync(targetSessionId, draft.Text, cancellationToken).ConfigureAwait(false);
            if (ok)
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
}
