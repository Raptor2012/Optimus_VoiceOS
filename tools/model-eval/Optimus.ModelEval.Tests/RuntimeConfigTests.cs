namespace Optimus.ModelEval.Tests;

using System.Text.Json;
using Xunit;

public sealed class RuntimeConfigTests
{
    [Fact]
    public void CandidateConfigsDeclareTheComparableRuntimeSettings()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "configs");
        string[] expected = ["gemma-4-e2b-q4.json", "qwen3.5-4b-q4_k_m.json", "holo3.1-4b-q4_k_m.json"];

        foreach (string file in expected)
        {
            string path = Path.Combine(directory, file);
            Assert.True(File.Exists(path), $"Missing candidate config: {path}");
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = document.RootElement;
            Assert.True(root.GetProperty("contextWindow").GetInt32() > 0);
            Assert.True(root.GetProperty("gpuLayers").GetInt32() > 0);
            Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("chatFormat").GetString()));
            Assert.NotEmpty(root.GetProperty("llamaServerArguments").EnumerateArray());
        }
    }
}
