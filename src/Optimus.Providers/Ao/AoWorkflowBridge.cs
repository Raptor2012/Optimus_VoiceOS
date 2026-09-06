namespace Optimus.Providers.Ao;

using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Optimus.Providers;

/// <summary>Snapshot of AO's projects and sessions, ready to be narrated by the local model.</summary>
public sealed record AoProjectStatus(
    bool IsAvailable,
    IReadOnlyList<AoProject> Projects,
    IReadOnlyList<AoSession> Sessions,
    string Summary);

public sealed record AoSessionDetails(
    AoSession Session,
    AoConversationResponse? Conversation,
    string Summary);

public sealed record AoWorkerSpawnResult(
    bool Success,
    string? SessionId,
    AoSession? Session,
    string Summary);

public sealed record AoPrState(
    int Number,
    string? State,
    string? Mergeability,
    string? Url,
    string Summary);

/// <summary>
/// The workflow-facing AO API. It deliberately uses the existing read/send client for normal
/// operations and a small HTTP surface for daemon operations that are not part of conversation IO.
/// All failures become plain-language results so a stopped AO daemon does not take down voice OS.
/// </summary>
public sealed class AoWorkflowBridge : IProviderAdapter, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly IAoClient _client;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private string? _lastSentMessage;

    public AoWorkflowBridge(IAoClient client, HttpClient? httpClient = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        if (httpClient != null)
        {
            _httpClient = httpClient;
            _ownsHttpClient = false;
        }
        else
        {
            _httpClient = new HttpClient { BaseAddress = new Uri(client.BaseUrl), Timeout = TimeSpan.FromSeconds(10) };
            _ownsHttpClient = true;
        }
    }

    public string BaseUrl => _client.BaseUrl;

    public async Task<AoProjectStatus> GetProjectStatus(CancellationToken cancellationToken = default)
    {
        bool healthy = await _client.IsHealthyAsync(cancellationToken).ConfigureAwait(false);
        var projects = await _client.GetProjectsAsync(cancellationToken).ConfigureAwait(false);
        var sessions = await _client.GetSessionsAsync(null, cancellationToken).ConfigureAwait(false);
        string summary = BuildProjectSummary(healthy, projects, sessions);
        return new AoProjectStatus(healthy, projects, sessions, summary);
    }

    public async Task<AoProjectStatus> GetProjectStatus(string projectId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        bool healthy = await _client.IsHealthyAsync(cancellationToken).ConfigureAwait(false);
        AoProject? project = await _client.GetProjectAsync(projectId, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<AoSession> sessions = await _client.GetSessionsAsync(projectId, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<AoProject> projects = project == null ? Array.Empty<AoProject>() : new[] { project };
        string summary = BuildProjectSummary(healthy, projects, sessions);
        return new AoProjectStatus(healthy, projects, sessions, summary);
    }

    public async Task<IReadOnlyList<AoSession>> GetActiveSessions(CancellationToken cancellationToken = default)
    {
        var sessions = await _client.GetSessionsAsync(null, cancellationToken).ConfigureAwait(false);
        return sessions.Where(IsActive).ToArray();
    }

    public async Task<IReadOnlyList<AoSession>> GetActiveSessions(string? projectId, CancellationToken cancellationToken = default)
    {
        var sessions = await _client.GetSessionsAsync(projectId, cancellationToken).ConfigureAwait(false);
        return sessions.Where(IsActive).ToArray();
    }

    public async Task<AoSessionDetails?> GetSessionDetails(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        AoSession? session = await _client.GetSessionAsync(id, cancellationToken).ConfigureAwait(false);
        if (session == null) return null;
        AoConversationResponse? conversation = await _client.GetSessionConversationAsync(id, cancellationToken).ConfigureAwait(false);
        return new AoSessionDetails(session, conversation, BuildSessionSummary(session, conversation));
    }

    public async Task<AoWorkerSpawnResult> SpawnWorker(string name, string prompt, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        var request = new SpawnWorkerRequest(name.Trim(), prompt.Trim());
        try
        {
            using var content = new StringContent(JsonSerializer.Serialize(request, JsonOptions), Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await _httpClient.PostAsync("/api/v1/sessions", content, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return new(false, null, null, $"AO could not start worker {name}: HTTP {(int)response.StatusCode}.");

            string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            AoSession? session = ReadSession(json);
            string? sessionId = session?.Id ?? ReadString(json, "sessionId", "id");
            return new(true, sessionId, session, sessionId == null
                ? $"Worker {name} was started."
                : $"Worker {name} was started as session {sessionId}.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new(false, null, null, $"I could not reach AO to start worker {name}: {ex.Message}");
        }
    }

    public async Task<string> SendToWorker(string id, string message, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        AoSendMessageResponse? response = await _client.SendSessionMessageAsync(id, message.Trim(), $"voice-workflow-{Guid.NewGuid():N}", cancellationToken).ConfigureAwait(false);
        return response?.Ok == true
            ? $"Sent the message to worker {id}."
            : $"AO did not accept the message for worker {id}.";
    }

    public async Task<AoPrState?> GetPrState(int prNumber, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(prNumber);
        // Prefer the daemon's PR endpoint when available. Older AO versions expose PRs on sessions.
        try
        {
            using HttpResponseMessage response = await _httpClient.GetAsync($"/api/v1/prs/{prNumber}", cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var pr = JsonSerializer.Deserialize<AoPrPayload>(json, JsonOptions);
                if (pr != null)
                    return new(pr.Number == 0 ? prNumber : pr.Number, pr.State, pr.Mergeability, pr.Url, BuildPrSummary(pr.Number == 0 ? prNumber : pr.Number, pr.State, pr.Mergeability));
            }
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Fall back to the session snapshot below.
        }

        var sessions = await _client.GetSessionsAsync(null, cancellationToken).ConfigureAwait(false);
        AoSessionPr? found = sessions.SelectMany(session => session.Prs ?? Array.Empty<AoSessionPr>())
            .FirstOrDefault(pr => pr.Number == prNumber);
        return found == null ? null : new(prNumber, found.State, found.Mergeability, found.Url, BuildPrSummary(prNumber, found.State, found.Mergeability));
    }

    // Async-suffixed aliases make the bridge convenient for callers following .NET naming norms.
    public Task<AoProjectStatus> GetProjectStatusAsync(CancellationToken cancellationToken = default) => GetProjectStatus(cancellationToken);
    public Task<AoProjectStatus> GetProjectStatusAsync(string projectId, CancellationToken cancellationToken = default) => GetProjectStatus(projectId, cancellationToken);
    public Task<IReadOnlyList<AoSession>> GetActiveSessionsAsync(CancellationToken cancellationToken = default) => GetActiveSessions(cancellationToken);
    public Task<IReadOnlyList<AoSession>> GetActiveSessionsAsync(string? projectId, CancellationToken cancellationToken = default) => GetActiveSessions(projectId, cancellationToken);
    public Task<AoSessionDetails?> GetSessionDetailsAsync(string id, CancellationToken cancellationToken = default) => GetSessionDetails(id, cancellationToken);
    public Task<AoWorkerSpawnResult> SpawnWorkerAsync(string name, string prompt, CancellationToken cancellationToken = default) => SpawnWorker(name, prompt, cancellationToken);
    public Task<string> SendToWorkerAsync(string id, string message, CancellationToken cancellationToken = default) => SendToWorker(id, message, cancellationToken);
    public Task<AoPrState?> GetPrStateAsync(int prNumber, CancellationToken cancellationToken = default) => GetPrState(prNumber, cancellationToken);

    // IProviderAdapter integration lets ProviderRouter route explicit "AO workflow" requests here.
    public async Task<bool> IsAvailable() => await _client.IsHealthyAsync().ConfigureAwait(false);

    public async Task<ProviderState> Observe()
    {
        AoProjectStatus status = await GetProjectStatus().ConfigureAwait(false);
        return status.IsAvailable
            ? new ProviderState(true, "ao-workflow", status.Summary, true, Detail: status.Summary, ObservedAtUtc: DateTimeOffset.UtcNow)
            : ProviderState.Unavailable(status.Summary);
    }

    public async Task SendMessage(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        IReadOnlyList<AoSession> sessions = await GetActiveSessions().ConfigureAwait(false);
        AoSession? session = sessions.Count == 0 ? null : sessions[0];
        if (session == null) throw new InvalidOperationException("No active AO worker is available.");
        _lastSentMessage = text;
        string result = await SendToWorker(session.Id, text).ConfigureAwait(false);
        if (!result.StartsWith("Sent ", StringComparison.Ordinal)) throw new InvalidOperationException(result);
    }

    public async Task<string> ReadLastResponse()
    {
        IReadOnlyList<AoSession> sessions = await GetActiveSessions().ConfigureAwait(false);
        AoSession? session = sessions.Count == 0 ? null : sessions[0];
        if (session == null) return string.Empty;
        AoSessionDetails? details = await GetSessionDetails(session.Id).ConfigureAwait(false);
        return details?.Conversation?.Messages?.LastOrDefault(message => string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase))?.Text ?? details?.Summary ?? string.Empty;
    }

    public async Task<bool> VerifySent()
    {
        if (string.IsNullOrWhiteSpace(_lastSentMessage)) return false;
        IReadOnlyList<AoSession> sessions = await GetActiveSessions().ConfigureAwait(false);
        AoSession? session = sessions.Count == 0 ? null : sessions[0];
        if (session == null) return false;
        AoConversationResponse? conversation = await _client.GetSessionConversationAsync(session.Id).ConfigureAwait(false);
        return conversation?.Messages?.Any(message => string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(message.Text?.Trim(), _lastSentMessage.Trim(), StringComparison.Ordinal)) == true;
    }

    public void Dispose()
    {
        if (_ownsHttpClient) _httpClient.Dispose();
    }

    private static bool IsActive(AoSession session) =>
        !session.IsTerminated && (string.Equals(session.Activity?.State, "active", StringComparison.OrdinalIgnoreCase) ||
        session.Status is "working" or "running" or "queued" or "active");

    private static string BuildProjectSummary(bool healthy, IReadOnlyList<AoProject> projects, IReadOnlyList<AoSession> sessions)
    {
        if (!healthy) return "AO is not reachable at its local daemon endpoint.";
        int active = sessions.Count(IsActive);
        int workers = sessions.Count(session => string.Equals(session.Kind, "worker", StringComparison.OrdinalIgnoreCase) && IsActive(session));
        int pullRequests = sessions.Sum(session => session.Prs?.Count ?? 0);
        return $"AO is online with {projects.Count} project{(projects.Count == 1 ? "" : "s")}, {active} active session{(active == 1 ? "" : "s")}, {workers} active worker{(workers == 1 ? "" : "s")}, and {pullRequests} tracked pull request{(pullRequests == 1 ? "" : "s")}.";
    }

    private static string BuildSessionSummary(AoSession session, AoConversationResponse? conversation)
    {
        string state = session.DisplayStatus ?? session.Status ?? session.Activity?.State ?? "unknown";
        int messages = conversation?.Messages?.Count ?? 0;
        return $"{session.DisplayName ?? session.Id} is {state} on {session.Harness}; the conversation has {messages} message{(messages == 1 ? "" : "s")}.";
    }

    private static string BuildPrSummary(int number, string? state, string? mergeability) =>
        $"Pull request {number} is {state ?? "in an unknown state"}{(string.IsNullOrWhiteSpace(mergeability) ? "" : $" and {mergeability}")}.";

    private static AoSession? ReadSession(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement value = doc.RootElement.TryGetProperty("session", out JsonElement session) ? session : doc.RootElement;
        return value.ValueKind == JsonValueKind.Object && value.TryGetProperty("id", out _) ? value.Deserialize<AoSession>(JsonOptions) : null;
    }

    private static string? ReadString(string json, params string[] names)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        foreach (string name in names)
            if (doc.RootElement.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String) return value.GetString();
        return null;
    }

    private sealed record SpawnWorkerRequest(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("prompt")] string Prompt);

    private sealed record AoPrPayload(
        [property: JsonPropertyName("number")] int Number,
        [property: JsonPropertyName("state")] string? State,
        [property: JsonPropertyName("mergeability")] string? Mergeability,
        [property: JsonPropertyName("url")] string? Url);
}
