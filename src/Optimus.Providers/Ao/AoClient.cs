namespace Optimus.Providers.Ao;

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
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
                Timeout = TimeSpan.FromSeconds(10)
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
            // Fall back to default
        }

        return DefaultBaseUrl;
    }

    public async Task<bool> IsHealthyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using HttpResponseMessage response = await _httpClient.GetAsync("/healthz", cancellationToken).ConfigureAwait(false);
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
            using HttpResponseMessage response = await _httpClient.GetAsync("/api/v1/projects", cancellationToken).ConfigureAwait(false);
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
            using HttpResponseMessage response = await _httpClient.GetAsync($"/api/v1/projects/{Uri.EscapeDataString(projectId)}", cancellationToken).ConfigureAwait(false);
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

            using HttpResponseMessage response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
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
            using HttpResponseMessage response = await _httpClient.GetAsync($"/api/v1/sessions/{Uri.EscapeDataString(sessionId)}", cancellationToken).ConfigureAwait(false);
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

    public async Task<IReadOnlyList<AoConversationMessage>> GetSessionConversationAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        try
        {
            using HttpResponseMessage response = await _httpClient.GetAsync($"/api/v1/sessions/{Uri.EscapeDataString(sessionId)}/conversation", cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return Array.Empty<AoConversationMessage>();
            }

            string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var result = JsonSerializer.Deserialize<AoConversationResponse>(json, JsonOptions);
            return result?.Messages ?? Array.Empty<AoConversationMessage>();
        }
        catch
        {
            return Array.Empty<AoConversationMessage>();
        }
    }

    public async Task<bool> SendSessionMessageAsync(string sessionId, string message, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        try
        {
            var req = new AoSendMessageRequest(message);
            string reqJson = JsonSerializer.Serialize(req, JsonOptions);
            using var content = new StringContent(reqJson, Encoding.UTF8, "application/json");

            using HttpResponseMessage response = await _httpClient.PostAsync($"/api/v1/sessions/{Uri.EscapeDataString(sessionId)}/send", content, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            string respJson = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var respObj = JsonSerializer.Deserialize<AoSendMessageResponse>(respJson, JsonOptions);
            return respObj?.Ok ?? true;
        }
        catch
        {
            return false;
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
