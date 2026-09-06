namespace Optimus.Providers.Ao;

using System;
using System.Linq;
using System.Threading.Tasks;
using Optimus.Providers.Desktop;

/// <summary>
/// Desktop adapter for Agent Orchestrator. REST conversation access is preferred, while the AO
/// window is used for sidebar navigation and for installations where the API is unavailable.
/// </summary>
public sealed class AoDesktopAdapter : DesktopProviderAdapter
{
    private readonly IAoClient _aoClient;
    private readonly string? _projectId;
    private string? _sessionId;

    /// <summary>Creates an AO adapter for an optional project or session target.</summary>
    public AoDesktopAdapter(
        IAoClient aoClient,
        string? projectId = null,
        string? sessionId = null,
        IDesktopObserver? observer = null,
        IDesktopExecutor? executor = null)
        : base(
            "ao",
            "Agent Orchestrator",
            ["ao", "AgentOrchestrator", "Agent Orchestrator"],
            ["Agent Orchestrator", "AO"],
            observer,
            executor)
    {
        _aoClient = aoClient ?? throw new ArgumentNullException(nameof(aoClient));
        _projectId = string.IsNullOrWhiteSpace(projectId) ? null : projectId;
        _sessionId = string.IsNullOrWhiteSpace(sessionId) ? null : sessionId;
    }

    /// <summary>Creates an AO adapter with injected desktop services and no fixed project.</summary>
    public AoDesktopAdapter(IAoClient aoClient, IDesktopObserver observer, IDesktopExecutor? executor = null)
        : this(aoClient, null, null, observer, executor)
    {
    }

    /// <summary>Gets the project target selected for this adapter.</summary>
    public string? ProjectId => _projectId;

    /// <summary>Gets the currently selected AO session, if one has been resolved.</summary>
    public string? SessionId => _sessionId;

    /// <summary>Selects a session for subsequent UI and REST operations.</summary>
    public void SetActiveSession(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        _sessionId = sessionId;
    }

    /// <inheritdoc />
    public override async Task<bool> IsAvailable()
    {
        if (await base.IsAvailable().ConfigureAwait(false))
        {
            return true;
        }

        try
        {
            return await _aoClient.IsHealthyAsync().ConfigureAwait(false);
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc />
    public override async Task<ProviderState> Observe()
    {
        ProviderState uiState = await base.Observe().ConfigureAwait(false);
        string? sessionId = await ResolveSessionId().ConfigureAwait(false);
        AoConversationResponse? conversation = sessionId == null
            ? null
            : await GetConversation(sessionId).ConfigureAwait(false);
        string? lastResponse = conversation?.Messages?
            .Where(message => string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase))
            .Select(message => message.Text)
            .LastOrDefault(text => !string.IsNullOrWhiteSpace(text));

        if (uiState.IsAvailable || conversation != null)
        {
            return uiState with
            {
                IsAvailable = uiState.IsAvailable || conversation != null,
                ActiveConversation = sessionId ?? uiState.ActiveConversation,
                LastMessage = lastResponse ?? uiState.LastMessage,
                InputReady = uiState.IsAvailable ? uiState.InputReady : true,
                Detail = conversation == null
                    ? uiState.Detail
                    : $"Observed AO session {sessionId} through REST and desktop UI."
            };
        }

        return ProviderState.Unavailable("Agent Orchestrator is not running.");
    }

    /// <inheritdoc />
    public override async Task SendMessage(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        string? sessionId = await ResolveSessionId().ConfigureAwait(false);
        bool selected = SelectSessionInUi(sessionId);

        if (sessionId != null)
        {
            try
            {
                AoSendMessageResponse? result = await _aoClient.SendSessionMessageAsync(
                    sessionId,
                    text,
                    clientMessageId: $"desktop-{Guid.NewGuid():N}").ConfigureAwait(false);
                if (result?.Ok == true)
                {
                    RecordProviderSend(text, true);
                    return;
                }
            }
            catch
            {
                // Fall back to the visible AO composer below.
            }
        }

        bool sentThroughUi = selected && TrySendMessageThroughUi(text);
        RecordProviderSend(text, sentThroughUi);
    }

    /// <inheritdoc />
    public override async Task<string> ReadLastResponse()
    {
        string? sessionId = await ResolveSessionId().ConfigureAwait(false);
        if (sessionId != null)
        {
            AoConversationResponse? conversation = await GetConversation(sessionId).ConfigureAwait(false);
            string? response = conversation?.Messages?
                .Where(message => string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase))
                .Select(message => message.Text)
                .LastOrDefault(text => !string.IsNullOrWhiteSpace(text));
            if (!string.IsNullOrWhiteSpace(response))
            {
                return response.Trim();
            }
        }

        return await base.ReadLastResponse().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async Task<bool> VerifySent()
    {
        if (string.IsNullOrWhiteSpace(LastSentText) || !await IsAvailable().ConfigureAwait(false))
        {
            return false;
        }

        string? sessionId = await ResolveSessionId().ConfigureAwait(false);
        if (sessionId != null)
        {
            AoConversationResponse? conversation = await GetConversation(sessionId).ConfigureAwait(false);
            bool inConversation = conversation?.Messages?.Any(message =>
                string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(Normalize(message.Text), Normalize(LastSentText), StringComparison.OrdinalIgnoreCase)) == true;
            if (inConversation)
            {
                return true;
            }
        }

        return await base.VerifySent().ConfigureAwait(false);
    }

    private async Task<string?> ResolveSessionId()
    {
        if (!string.IsNullOrWhiteSpace(_sessionId))
        {
            return _sessionId;
        }

        try
        {
            var sessions = await _aoClient.GetSessionsAsync(_projectId).ConfigureAwait(false);
            var selected = sessions
                .Where(session => !session.IsTerminated)
                .OrderByDescending(session => string.Equals(session.Activity?.State, "active", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(session => string.Equals(session.Status, "working", StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault();
            _sessionId = selected?.Id;
        }
        catch
        {
            // A missing daemon is a normal unavailable state for this adapter.
        }

        return _sessionId;
    }

    private async Task<AoConversationResponse?> GetConversation(string sessionId)
    {
        try
        {
            return await _aoClient.GetSessionConversationAsync(sessionId).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    private bool SelectSessionInUi(string? sessionId)
    {
        WindowInfo? window = FindWindow();
        if (window == null)
        {
            return false;
        }

        try
        {
            ObservationSnapshot observation = Observer.ObserveWindow(window.Hwnd);
            if (!observation.IsValid)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(sessionId) &&
                TryInvokeControl(window, observation.Accessibility, sessionId, "session", "conversation"))
            {
                return true;
            }

            return !string.IsNullOrWhiteSpace(_projectId) &&
                   TryInvokeControl(window, observation.Accessibility, _projectId, "project", "workspace");
        }
        catch
        {
            return false;
        }
    }

    private static string Normalize(string? text) =>
        string.Join(' ', (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
