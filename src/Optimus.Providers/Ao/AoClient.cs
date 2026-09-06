namespace Optimus.Providers.Ao;

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

public sealed class AoClient : IAoClient, IDisposable
{
    public const string DefaultBaseUrl = "http://127.0.0.1:3001";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    public string BaseUrl { get; }

    public AoClient(string? baseUrl = null, HttpClient? httpClient = null)
    {
        BaseUrl = !string.IsNullOrWhiteSpace(baseUrl)
            ? baseUrl.TrimEnd('/')
            : ResolveBaseUrlFromRunningJson();

        if (httpClient != null)
        {
            _httpClient = httpClient;
            _ownsHttpClient = false;
        }
        else
        {
            _httpClient = new HttpClient
            {
                BaseAddress = new Uri(BaseUrl),
                Timeout = TimeSpan.FromSeconds(15)
            };
            _ownsHttpClient = true;
        }
    }

    public static string ResolveBaseUrlFromRunningJson()
    {
        try
        {
            string userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string runningJsonPath = Path.Combine(userHome, ".ao", "running.json");
            if (File.Exists(runningJsonPath))
            {
                string json = File.ReadAllText(runningJsonPath);
                using JsonDocument doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("port", out JsonElement portElem) &&
                    portElem.TryGetInt32(out int port) && port > 0)
                {
                    return $"http://127.0.0.1:{port}";
                }
            }
        }
        catch
        {
            // Fall back to default if unreadable
        }

        return DefaultBaseUrl;
    }

    public async Task<bool> IsHealthyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using HttpResponseMessage response = await _httpClient.GetAsync("/healthz", cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return true;
            }
        }
        catch
        {
            // Fall through to try /api/v1/projects
        }

        try
        {
            using var response = await _httpClient.GetAsync("/api/v1/projects", cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<AoProject>> GetProjectsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _httpClient.GetAsync("/api/v1/projects", cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return Array.Empty<AoProject>();
            }

            string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var result = JsonSerializer.Deserialize<AoProjectsResponse>(json, JsonOptions);
            return result?.Projects ?? Array.Empty<AoProject>();
        }
        catch
        {
            return Array.Empty<AoProject>();
        }
    }

    public async Task<AoProject?> GetProjectAsync(string projectId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        try
        {
            using var response = await _httpClient.GetAsync($"/api/v1/projects/{Uri.EscapeDataString(projectId)}", cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("project", out JsonElement projectElem))
            {
                return JsonSerializer.Deserialize<AoProject>(projectElem.GetRawText(), JsonOptions);
            }

            return JsonSerializer.Deserialize<AoProject>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<AoSession>> GetSessionsAsync(string? projectId = null, CancellationToken cancellationToken = default)
    {
        try
        {
            string url = string.IsNullOrWhiteSpace(projectId)
                ? "/api/v1/sessions"
                : $"/api/v1/sessions?projectId={Uri.EscapeDataString(projectId)}";

            using var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return Array.Empty<AoSession>();
            }

            string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var result = JsonSerializer.Deserialize<AoSessionsResponse>(json, JsonOptions);
            return result?.Sessions ?? Array.Empty<AoSession>();
        }
        catch
        {
            return Array.Empty<AoSession>();
        }
    }

    public async Task<AoSession?> GetSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        try
        {
            using var response = await _httpClient.GetAsync($"/api/v1/sessions/{Uri.EscapeDataString(sessionId)}", cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("session", out JsonElement sessionElem))
            {
                return JsonSerializer.Deserialize<AoSession>(sessionElem.GetRawText(), JsonOptions);
            }

            return JsonSerializer.Deserialize<AoSession>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public async Task<AoConversationResponse?> GetSessionConversationAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        try
        {
            using var response = await _httpClient.GetAsync($"/api/v1/sessions/{Uri.EscapeDataString(sessionId)}/conversation", cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<AoConversationResponse>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public async Task<AoSendMessageResponse?> SendSessionMessageAsync(
        string sessionId,
        string message,
        string? clientMessageId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        // First attempt conversation messages endpoint (which supports deduplication via clientMessageId)
        try
        {
            var req = new AoSendMessageRequest(message, clientMessageId);
            string reqJson = JsonSerializer.Serialize(req, JsonOptions);
            using var content = new StringContent(reqJson, Encoding.UTF8, "application/json");

            using var response = await _httpClient.PostAsync(
                $"/api/v1/sessions/{Uri.EscapeDataString(sessionId)}/conversation/messages",
                content,
                cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                string respJson = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                return JsonSerializer.Deserialize<AoSendMessageResponse>(respJson, JsonOptions) ?? new AoSendMessageResponse(Ok: true, SessionId: sessionId);
            }
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Fall through to legacy /send
        }

        // Fallback to legacy sendSessionMessage endpoint: /api/v1/sessions/{id}/send
        try
        {
            var req = new AoLegacySendMessageRequest(message);
            string reqJson = JsonSerializer.Serialize(req, JsonOptions);
            using var content = new StringContent(reqJson, Encoding.UTF8, "application/json");

            using var response = await _httpClient.PostAsync(
                $"/api/v1/sessions/{Uri.EscapeDataString(sessionId)}/send",
                content,
                cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            string respJson = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<AoSendMessageResponse>(respJson, JsonOptions) ?? new AoSendMessageResponse(Ok: true, SessionId: sessionId);
        }
        catch
        {
            return null;
        }
    }

    public async Task<bool> ResolveApprovalAsync(
        string sessionId,
        string requestId,
        string decisionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(decisionId);

        try
        {
            var req = new AoResolveApprovalRequest(decisionId);
            string reqJson = JsonSerializer.Serialize(req, JsonOptions);
            using var content = new StringContent(reqJson, Encoding.UTF8, "application/json");

            using var response = await _httpClient.PostAsync(
                $"/api/v1/sessions/{Uri.EscapeDataString(sessionId)}/conversation/approvals/{Uri.EscapeDataString(requestId)}/resolve",
                content,
                cancellationToken).ConfigureAwait(false);

            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> InterruptAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        try
        {
            using var response = await _httpClient.PostAsync(
                $"/api/v1/sessions/{Uri.EscapeDataString(sessionId)}/conversation/interrupt",
                null,
                cancellationToken).ConfigureAwait(false);

            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async IAsyncEnumerable<AoCdcEvent> StreamEventsAsync(
        long? afterSeq = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string url = afterSeq.HasValue && afterSeq.Value >= 0
            ? $"/api/v1/events?after={afterSeq.Value}"
            : "/api/v1/events";

        HttpRequestMessage request = new(HttpMethod.Get, url);
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
        }
        catch
        {
            yield break;
        }

        using (response)
        using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        using (var reader = new StreamReader(stream, Encoding.UTF8))
        {
            long currentSeq = 0;
            string currentEvent = "message";
            string? currentData = null;

            while (!cancellationToken.IsCancellationRequested && !reader.EndOfStream)
            {
                string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line == null) break;

                line = line.Trim();
                if (line.StartsWith("id:", StringComparison.OrdinalIgnoreCase))
                {
                    string idVal = line[3..].Trim();
                    if (long.TryParse(idVal, out long parsedSeq))
                    {
                        currentSeq = parsedSeq;
                    }
                }
                else if (line.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
                {
                    currentEvent = line[6..].Trim();
                }
                else if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    currentData = line[5..].Trim();
                }
                else if (line.Length == 0 && currentData != null)
                {
                    string? projId = null;
                    string? sessId = null;
                    try
                    {
                        using var doc = JsonDocument.Parse(currentData);
                        if (doc.RootElement.TryGetProperty("projectId", out var pElem)) projId = pElem.GetString();
                        if (doc.RootElement.TryGetProperty("sessionId", out var sElem)) sessId = sElem.GetString();
                        if (doc.RootElement.TryGetProperty("seq", out var seqElem) && seqElem.TryGetInt64(out var parsedSeq)) currentSeq = parsedSeq;
                    }
                    catch
                    {
                        // best effort payload parsing
                    }

                    yield return new AoCdcEvent(
                        currentSeq,
                        projId,
                        sessId,
                        currentEvent,
                        currentData,
                        DateTimeOffset.UtcNow);

                    currentData = null;
                    currentEvent = "message";
                }
            }
        }
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}
