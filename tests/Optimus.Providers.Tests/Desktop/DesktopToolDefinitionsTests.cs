namespace Optimus.Providers.Tests.Desktop;

using System.Text.Json;
using Optimus.Providers.Desktop;
using Xunit;

public sealed class DesktopToolDefinitionsTests
{
    [Fact]
    public void DesktopToolDefinitions_ContainsAllElevenRequiredTools()
    {
        string[] expectedNames = new[]
        {
            "list_windows",
            "focus_window",
            "inspect_controls",
            "capture_window",
            "invoke_element",
            "click",
            "scroll",
            "enter_text",
            "press_shortcut",
            "read_content",
            "observe_result"
        };

        Assert.Equal(11, DesktopToolDefinitions.All.Count);

        foreach (string expectedName in expectedNames)
        {
            DesktopToolDefinition? tool = DesktopToolDefinitions.Get(expectedName);
            Assert.NotNull(tool);
            Assert.Equal(expectedName, tool.Name);
            Assert.NotEmpty(tool.Description);
            Assert.NotNull(tool.Parameters);
        }
    }

    [Fact]
    public void DesktopToolDefinitions_ProducesValidJsonToolSchema()
    {
        string json = DesktopToolDefinitions.ToJsonSchema(indented: true);
        Assert.NotEmpty(json);

        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.Equal(11, doc.RootElement.GetArrayLength());

        foreach (JsonElement item in doc.RootElement.EnumerateArray())
        {
            Assert.Equal("function", item.GetProperty("type").GetString());
            JsonElement fn = item.GetProperty("function");
            Assert.True(fn.TryGetProperty("name", out JsonElement nameEl));
            Assert.NotEmpty(nameEl.GetString()!);
            Assert.True(fn.TryGetProperty("description", out _));
            Assert.True(fn.TryGetProperty("parameters", out JsonElement paramsEl));
            Assert.Equal("object", paramsEl.GetProperty("type").GetString());
        }
    }
}
