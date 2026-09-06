namespace Optimus.Core.Tests.ExecutionPolicy;

using System;
using System.Threading;
using System.Threading.Tasks;
using Optimus.Core.ExecutionPolicy;
using Optimus.Providers.Ao;
using Xunit;

public sealed class LiveProviderSmokeTests
{
    [Fact]
    public async Task LiveAoDaemon_HealthAndProjects_SmokeTest()
    {
        string baseUrl = AoClient.ResolveBaseUrlFromRunningJson();
        using var client = new AoClient(baseUrl);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        bool isHealthy = false;
        try
        {
            isHealthy = await client.IsHealthyAsync(cts.Token);
        }
        catch
        {
            // Daemon not reachable in this test environment
        }

        if (!isHealthy)
        {
            // If local AO daemon is not currently active, pass gracefully
            return;
        }

        // Live daemon is running: verify live endpoints
        var projects = await client.GetProjectsAsync(cts.Token);
        Assert.NotNull(projects);

        var sessions = await client.GetSessionsAsync(cancellationToken: cts.Token);
        Assert.NotNull(sessions);
    }

    [Fact]
    public async Task LiveAntigravityCli_SmokeTest_WhenInstalled()
    {
        using var driver = new AntigravityHeadlessChatDriver();
        if (!driver.IsCliAvailable)
        {
            // Agy CLI not installed on this machine; pass smoke test gracefully
            return;
        }

        // Live agy is available on this system! Run a small non-destructive live command
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var options = new AntigravityChatOptions(
            Prompt: "echo ping",
            Timeout: TimeSpan.FromSeconds(10)
        );

        AntigravityChatResponse response = await driver.ExecuteAsync(options, cts.Token);

        // Driver must successfully execute without unhandled exception
        Assert.NotNull(response);
    }
}
