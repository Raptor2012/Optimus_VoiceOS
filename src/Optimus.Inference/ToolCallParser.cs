namespace Optimus.Inference;

using System.Text.Json;

/// <summary>
/// Parses the complete structured tool response emitted by an OpenAI-compatible llama server.
/// Invalid or incomplete JSON produces no calls, so a streamed/partial request can never run.
/// </summary>
public static class ToolCallParser
{
    public static bool TryParse(string output, out IReadOnlyList<LlamaServerProcess.ToolCall> toolCalls)
    {
        toolCalls = Array.Empty<LlamaServerProcess.ToolCall>();
        if (string.IsNullOrWhiteSpace(output)) return false;
        string candidate = StripEnvelope(output.Trim());
        try
        {
            using JsonDocument document = JsonDocument.Parse(candidate);
            IReadOnlyList<LlamaServerProcess.ToolCall> parsed = ParseRoot(document.RootElement);
            if (parsed.Count == 0) return false;
            toolCalls = parsed;
            return true;
        }
        catch (JsonException)
        {
            // JsonDocument.Parse requires the entire document. Do not attempt a prefix parse.
            return false;
        }
    }

    public static IReadOnlyList<LlamaServerProcess.ToolCall> Parse(string output) =>
        TryParse(output, out IReadOnlyList<LlamaServerProcess.ToolCall> calls) ? calls : Array.Empty<LlamaServerProcess.ToolCall>();

    internal static IReadOnlyList<LlamaServerProcess.ToolCall> ParseJson(JsonElement element)
    {
        try { return ParseRoot(element); }
        catch (JsonException) { return Array.Empty<LlamaServerProcess.ToolCall>(); }
    }

    private static IReadOnlyList<LlamaServerProcess.ToolCall> ParseRoot(JsonElement root)
    {
        JsonElement calls = root;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("tool_calls", out JsonElement toolCalls)) calls = toolCalls;
        if (calls.ValueKind == JsonValueKind.Object)
        {
            return TryParseCall(calls, out LlamaServerProcess.ToolCall? single)
                ? [single!]
                : Array.Empty<LlamaServerProcess.ToolCall>();
        }
        if (calls.ValueKind != JsonValueKind.Array) return Array.Empty<LlamaServerProcess.ToolCall>();

        var result = new List<LlamaServerProcess.ToolCall>();
        foreach (JsonElement item in calls.EnumerateArray())
        {
            if (!TryParseCall(item, out LlamaServerProcess.ToolCall? call)) return Array.Empty<LlamaServerProcess.ToolCall>();
            result.Add(call!);
        }
        return result;
    }

    private static bool TryParseCall(JsonElement item, out LlamaServerProcess.ToolCall? call)
    {
        call = null;
        if (item.ValueKind != JsonValueKind.Object) return false;
        string? id = item.TryGetProperty("id", out JsonElement idElement) && idElement.ValueKind == JsonValueKind.String ? idElement.GetString() : null;
        JsonElement function = item.TryGetProperty("function", out JsonElement nested) ? nested : item;
        if (function.ValueKind != JsonValueKind.Object || !function.TryGetProperty("name", out JsonElement nameElement) || nameElement.ValueKind != JsonValueKind.String) return false;
        string? name = nameElement.GetString();
        if (string.IsNullOrWhiteSpace(name)) return false;

        JsonElement arguments = default;
        if (function.TryGetProperty("arguments", out JsonElement argumentElement))
        {
            if (argumentElement.ValueKind == JsonValueKind.String)
            {
                string raw = argumentElement.GetString() ?? string.Empty;
                try
                {
                    using JsonDocument parsed = JsonDocument.Parse(raw);
                    arguments = parsed.RootElement.Clone();
                }
                catch (JsonException) { return false; }
            }
            else if (argumentElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                arguments = argumentElement.Clone();
            }
            else return false;
        }
        else
        {
            using JsonDocument empty = JsonDocument.Parse("{}");
            arguments = empty.RootElement.Clone();
        }

        call = new LlamaServerProcess.ToolCall(id ?? string.Empty, name, arguments);
        return true;
    }

    private static string StripEnvelope(string value)
    {
        if (value.StartsWith("<tool_call>", StringComparison.Ordinal) && value.EndsWith("</tool_call>", StringComparison.Ordinal))
            return value["<tool_call>".Length..^"</tool_call>".Length].Trim();
        if (value.StartsWith("```", StringComparison.Ordinal) && value.EndsWith("```", StringComparison.Ordinal))
        {
            int newline = value.IndexOf('\n');
            if (newline >= 0) return value[(newline + 1)..^3].Trim();
        }
        return value;
    }

}