namespace Optimus.Core.Voice;

/// <summary>Small explicit command vocabulary. Ordinary dictation is left untouched.</summary>
public sealed record ConversationCommand(string Kind, string Value = "", string Replacement = "")
{
    public static ConversationCommand? Parse(string text)
    {
        string input = text.Trim().TrimEnd('.', '!', '?');
        string lower = input.ToLowerInvariant();
        string? kind = lower switch
        {
            "interrupt on" or "let me interrupt" => "interruptOn",
            "interrupt off" or "stop interrupting" => "interruptOff",
            "cleanup on" => "cleanupOn",
            "cleanup off" => "cleanupOff",
            "full readback" or "read everything" => "fullReview",
            "short readback" or "destination only" => "shortReview",
            "read that again" or "repeat that" => "repeat",
            "remove the last sentence" => "removeLast",
            "show details" => "detailsOn",
            "hide details" => "detailsOff",
            "mute narration" or "mute speech" or "mute" => "mute",
            "unmute narration" or "unmute speech" or "unmute" => "unmute",
            "pause listening" or "pause" => "pause",
            "resume listening" or "resume" => "resume",
            "end conversation" or "end session" => "endConversation",
            "open ao" or "open orchestrator" or "launch ao" => "openAo",
            "approve plan" or "approve decision" or "approve and start" => "approveDecision",
            "request changes" or "reject plan" or "reject decision" => "rejectDecision",
            "summaries only" => "concise",
            "full commentary" => "comprehensive",
            "tools on" => "toolsOn",
            "tools off" => "toolsOff",
            "what's happening" or "what is happening" => "status",
            _ => null
        };
        if (kind != null) return new(kind);
        foreach ((string prefix, string command) in new[]
        {
            ("switch to ", "switch"), ("remember this as ", "alias"),
            ("add to the prompt ", "append"), ("add ", "append")
        })
        {
            if (input.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && input.Length > prefix.Length)
                return new(command, input[prefix.Length..].Trim());
        }
        if (input.StartsWith("replace ", StringComparison.OrdinalIgnoreCase))
        {
            int with = input.IndexOf(" with ", 8, StringComparison.OrdinalIgnoreCase);
            if (with > 8 && with + 6 < input.Length)
                return new("replace", input[8..with].Trim(), input[(with + 6)..].Trim());
        }
        return null;
    }
}
