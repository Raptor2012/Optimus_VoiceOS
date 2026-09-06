namespace Optimus.Providers.Tests;

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Optimus.Providers;
using Optimus.Providers.Ao;
using Xunit;

public class AoClientTests
{
    private sealed class MockHttpMessageHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Handler { get; set; } =
            _ => new HttpResponseMessage(HttpStatusCode.OK);

        public List<HttpRequestMessage> RecordedRequests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RecordedRequests.Add(request);
            return Task.FromResult(Handler(request));
        }
    }

    [Fact]
    public async Task IsHealthyAsync_ReturnsTrue_When200Ok()
    {
        var mockHandler = new MockHttpMessageHandler
        {
            Handler = req =>
            {
                Assert.Equal("/healthz", req.RequestUri!.AbsolutePath);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
        };
        using var httpClient = new HttpClient(mockHandler) { BaseAddress = new Uri("http://localhost:3001") };
        using var client = new AoClient("http://localhost:3001", httpClient);

        bool healthy = await client.IsHealthyAsync();
        Assert.True(healthy);
    }

    [Fact]
    public async Task IsHealthyAsync_ReturnsFalse_WhenServerError()
    {
        var mockHandler = new MockHttpMessageHandler
        {
            Handler = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        };
        using var httpClient = new HttpClient(mockHandler) { BaseAddress = new Uri("http://localhost:3001") };
        using var client = new AoClient("http://localhost:3001", httpClient);

        bool healthy = await client.IsHealthyAsync();
        Assert.False(healthy);
    }

    [Fact]
    public async Task GetProjectsAsync_ParsesProjectsSuccessfully()
    {
        string json = """
        {
            "projects": [
                {
                    "id": "optimus_voiceos",
                    "name": "Optimus Voice OS",
                    "path": "C:\\repo\\Optimus_VoiceOS",
                    "kind": "single_repo",
                    "sessionPrefix": "optimus_voic",
                    "orchestratorAgent": "claude-code"
                }
            ]
        }
        """;

        var mockHandler = new MockHttpMessageHandler
        {
            Handler = req =>
            {
                Assert.Equal("/api/v1/projects", req.RequestUri!.AbsolutePath);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
            }
        };
        using var httpClient = new HttpClient(mockHandler) { BaseAddress = new Uri("http://localhost:3001") };
        using var client = new AoClient("http://localhost:3001", httpClient);

        var projects = await client.GetProjectsAsync();
        Assert.Single(projects);
        Assert.Equal("optimus_voiceos", projects[0].Id);
        Assert.Equal("Optimus Voice OS", projects[0].Name);
        Assert.Equal("claude-code", projects[0].OrchestratorAgent);
    }

    [Fact]
    public async Task GetSessionsAsync_ParsesSessionsAndFilters()
    {
        string json = """
        {
            "sessions": [
                {
                    "id": "optimus_voiceos-5",
                    "projectId": "optimus_voiceos",
                    "kind": "worker",
                    "harness": "agy",
                    "displayName": "Live project exp",
                    "status": "working",
                    "activity": { "state": "active" },
                    "prs": [
                        { "number": 1, "state": "merged" }
                    ]
                }
            ]
        }
        """;

        var mockHandler = new MockHttpMessageHandler
        {
            Handler = req =>
            {
                Assert.Contains("projectId=optimus_voiceos", req.RequestUri!.Query);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
            }
        };
        using var httpClient = new HttpClient(mockHandler) { BaseAddress = new Uri("http://localhost:3001") };
        using var client = new AoClient("http://localhost:3001", httpClient);

        var sessions = await client.GetSessionsAsync("optimus_voiceos");
        Assert.Single(sessions);
        Assert.Equal("optimus_voiceos-5", sessions[0].Id);
        Assert.Equal("Live project exp", sessions[0].DisplayName);
        Assert.Equal("active", sessions[0].Activity?.State);
        Assert.Single(sessions[0].Prs!);
        Assert.Equal(1, sessions[0].Prs![0].Number);
    }

    [Fact]
    public async Task SendSessionMessageAsync_PostsMessagePayload()
    {
        string recordedBody = "";
        var mockHandler = new MockHttpMessageHandler
        {
            Handler = req =>
            {
                Assert.Equal(HttpMethod.Post, req.Method);
                Assert.Equal("/api/v1/sessions/optimus_voiceos-5/send", req.RequestUri!.AbsolutePath);
                recordedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"ok\": true, \"sessionId\": \"optimus_voiceos-5\"}", Encoding.UTF8, "application/json")
                };
            }
        };
        using var httpClient = new HttpClient(mockHandler) { BaseAddress = new Uri("http://localhost:3001") };
        using var client = new AoClient("http://localhost:3001", httpClient);

        bool ok = await client.SendSessionMessageAsync("optimus_voiceos-5", "Hello worker");
        Assert.True(ok);
        Assert.Contains("Hello worker", recordedBody);
    }

    [Fact]
    public void DestinationRegistry_DynamicallyResolvesAoDestination()
    {
        var mockHandler = new MockHttpMessageHandler();
        using var httpClient = new HttpClient(mockHandler) { BaseAddress = new Uri("http://localhost:3001") };
        using var client = new AoClient("http://localhost:3001", httpClient);

        var registry = new DestinationRegistry(client);

        var adapter = registry.Find("ao:optimus_voiceos");
        Assert.NotNull(adapter);
        Assert.IsType<AoDestinationAdapter>(adapter);
        Assert.Equal("ao:optimus_voiceos", adapter.DestinationId);
    }

    [Fact]
    public async Task AoDestinationAdapter_SendAsync_DeliversExactConfirmedDraft()
    {
        string sentText = "";
        string sentSession = "";
        var mockHandler = new MockHttpMessageHandler
        {
            Handler = req =>
            {
                if (req.RequestUri!.AbsolutePath.Contains("/send"))
                {
                    sentSession = req.RequestUri.AbsolutePath;
                    sentText = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"ok\": true, \"sessionId\": \"optimus_voiceos-5\"}", Encoding.UTF8, "application/json")
                    };
                }
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
        };
        using var httpClient = new HttpClient(mockHandler) { BaseAddress = new Uri("http://localhost:3001") };
        using var client = new AoClient("http://localhost:3001", httpClient);

        var adapter = new AoDestinationAdapter(client, "optimus_voiceos", "optimus_voiceos-5");
        var draft = new ConfirmedDraft("Ship Slice 2 now", adapter.DestinationId);

        var result = await adapter.SendAsync(draft);
        Assert.True(result.Succeeded);
        Assert.Equal(SendStatus.Sent, result.Status);
        Assert.Contains("Ship Slice 2 now", sentText);
        Assert.Contains("optimus_voiceos-5", sentSession);
    }
}
