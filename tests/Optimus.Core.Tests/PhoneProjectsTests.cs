namespace Optimus.Core.Tests;

using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Optimus.Core.Phone;
using Xunit;

public class PhoneProjectsTests : IDisposable
{
    private readonly PhoneEndpoint _endpoint;
    private readonly int _port;

    public PhoneProjectsTests()
    {
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            _port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
        }

        _endpoint = new PhoneEndpoint(_port, IPAddress.Loopback);
        _endpoint.Start();
    }

    public void Dispose()
    {
        _endpoint.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task RequestProjects_TriggersProjectsRequestedEvent()
    {
        var requestedEvent = new TaskCompletionSource<bool>();
        _endpoint.ProjectsRequested += (_, _) => requestedEvent.TrySetResult(true);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, _port);
        using NetworkStream stream = client.GetStream();

        byte[] frame = PhoneFraming.Encode(PhoneFrameKind.Json, Encoding.UTF8.GetBytes("{\"t\":\"requestProjects\"}"));
        await stream.WriteAsync(frame);

        var completed = await Task.WhenAny(requestedEvent.Task, Task.Delay(2000));
        Assert.True(completed == requestedEvent.Task, "ProjectsRequested did not fire within timeout.");
    }

    [Fact]
    public async Task SendProjects_DeliversJsonPayloadToClient()
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, _port);
        using NetworkStream stream = client.GetStream();

        // Send projects from endpoint
        var sampleProjects = new[]
        {
            new { id = "optimus_voiceos", name = "Optimus Voice OS", destinationId = "ao:optimus_voiceos" }
        };

        _endpoint.SendProjects(sampleProjects);

        var frame = await PhoneFraming.ReadAsync(stream, CancellationToken.None);
        Assert.NotNull(frame);
        Assert.Equal(PhoneFrameKind.Json, frame.Value.Kind);

        string json = Encoding.UTF8.GetString(frame.Value.Payload);
        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.Equal("projects", doc.RootElement.GetProperty("t").GetString());
        Assert.True(doc.RootElement.TryGetProperty("projects", out JsonElement projectsElem));
        Assert.Equal(1, projectsElem.GetArrayLength());
        Assert.Equal("optimus_voiceos", projectsElem[0].GetProperty("id").GetString());
    }
}
