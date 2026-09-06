namespace Optimus.Inference.Tests;

using Optimus.Inference;
using System.Net;
using System.Net.Http;
using Xunit;

public sealed class LlamaServerProcessTests
{
    [Fact]
    public void BuildArgumentsRequestsGpuOffloadAndFlashAttention()
    {
        string[] args = LlamaServerProcess.BuildArguments("model.gguf", 1234, 8192, 99).ToArray();

        int gpuIndex = Array.IndexOf(args, "-ngl");
        Assert.True(gpuIndex >= 0);
        Assert.Equal("99", args[gpuIndex + 1]);
        Assert.Contains("--flash-attn", args);
    }

    [Theory]
    [InlineData("{\"n_gpu_layers\":99}", true)]
    [InlineData("{\"n_gpu_layers\":0}", false)]
    [InlineData("{\"backend\":\"CUDA\"}", true)]
    public void PropsReportCudaDetectsBackendEvidence(string props, bool expected)
    {
        Assert.Equal(expected, LlamaServerProcess.PropsReportCuda(props));
    }

    [Fact]
    public void ImageInputProducesAnInMemoryDataUri()
    {
        var image = new LlamaServerProcess.ImageInput([1, 2, 3], "image/jpeg");

        Assert.Equal("data:image/jpeg;base64,AQID", image.ToDataUri());
    }

    [Theory]
    [InlineData("data: {\"choices\":[{\"delta\":{\"content\":\"hello\"}}]}", "hello")]
    [InlineData("event: done", null)]
    [InlineData("data: {\"choices\":[{\"delta\":{\"content\":\"partial", null)]
    public void StreamingDeltaParserOnlyReturnsCompleteContent(string line, string? expected)
    {
        Assert.Equal(expected is not null, LlamaServerProcess.TryReadStreamDelta(line, out string? delta));
        Assert.Equal(expected, delta);
    }

    [Fact]
    public void ToolCallParserRequiresCompleteArgumentsBeforeReturningACall()
    {
        const string partial = "{\"tool_calls\":[{\"id\":\"1\",\"function\":{\"name\":\"click\",\"arguments\":\"{\\\"x\\\":1";
        Assert.False(ToolCallParser.TryParse(partial, out _));

        const string complete = "{\"tool_calls\":[{\"id\":\"1\",\"function\":{\"name\":\"click\",\"arguments\":\"{\\\"x\\\":1}\"}}]}";
        Assert.True(ToolCallParser.TryParse(complete, out IReadOnlyList<LlamaServerProcess.ToolCall> calls));
        Assert.Equal("click", Assert.Single(calls).Name);
        Assert.Equal(1, calls[0].Arguments.GetProperty("x").GetInt32());
    }

    [Fact]
    public void ToolCallParserRejectsOneMalformedCallInsteadOfExecutingTheValidPrefix()
    {
        const string mixed = "{\"tool_calls\":[{\"function\":{\"name\":\"first\",\"arguments\":{}}},{\"function\":{\"name\":\"second\",\"arguments\":\"{\"}}]}";
        Assert.Empty(ToolCallParser.Parse(mixed));
    }

    [Fact]
    public void OptionsDefaultToFourKContextAndExplicitGpuOffload()
    {
        var options = new LlamaServerProcess.LlamaServerOptions();
        string[] args = LlamaServerProcess.BuildArguments("model.gguf", 1234, options.ContextSize, options.GpuLayers).ToArray();

        Assert.Equal("4096", args[Array.IndexOf(args, "-c") + 1]);
        Assert.Equal("99", args[Array.IndexOf(args, "-ngl") + 1]);
    }

    [Fact]
    public async Task StreamChatYieldsServerDeltasInOrder()
    {
        using var client = new HttpClient(new StaticResponseHandler(
            "data: {\"choices\":[{\"delta\":{\"content\":\"one\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\" two\"}}]}\n\n" +
            "data: [DONE]\n\n")) { BaseAddress = new Uri("http://localhost/") };
        using var server = new LlamaServerProcess(client);
        var chunks = new List<string>();

        await foreach (string chunk in server.StreamChatAsync([new("user", "hello")], 8)) chunks.Add(chunk);

        Assert.Equal(["one", " two"], chunks);
    }

    [Fact]
    public async Task StopCancelsTheActiveStreamingRequest()
    {
        using var client = new HttpClient(new BlockingHandler()) { BaseAddress = new Uri("http://localhost/") };
        using var server = new LlamaServerProcess(client);
        await using IAsyncEnumerator<string> enumerator = server.StreamChatAsync([new("user", "hello")], 8).GetAsyncEnumerator();
        Task<bool> pending = enumerator.MoveNextAsync().AsTask();
        await Task.Delay(50);

        server.Stop();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
    }

    private sealed class StaticResponseHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
    }

    private sealed class BlockingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
