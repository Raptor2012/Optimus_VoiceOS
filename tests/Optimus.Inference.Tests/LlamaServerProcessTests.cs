namespace Optimus.Inference.Tests;

using Optimus.Inference;
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
}
