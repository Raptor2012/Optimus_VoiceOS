namespace Optimus.Core.Voice;

using System;
using Optimus.Inference;

/// <summary>
/// Maps Gemma-interpreted intents into Optimus voice domain commands.
/// </summary>
public static class InterpretedIntentExtensions
{
    public static ConversationCommand? ToConversationCommand(this InterpretedIntent? intent)
    {
        if (intent == null || string.IsNullOrWhiteSpace(intent.Intent))
        {
            return null;
        }

        return intent.Intent.ToLowerInvariant() switch
        {
            "switch" => new ConversationCommand("switch", intent.Target ?? string.Empty),
            "cleanupon" => new ConversationCommand("cleanupOn"),
            "cleanupoff" => new ConversationCommand("cleanupOff"),
            "interrupton" => new ConversationCommand("interruptOn"),
            "interruptoff" => new ConversationCommand("interruptOff"),
            "fullreview" => new ConversationCommand("fullReview"),
            "shortreview" => new ConversationCommand("shortReview"),
            "repeat" => new ConversationCommand("repeat"),
            "removelast" => new ConversationCommand("removeLast"),
            "detailson" => new ConversationCommand("detailsOn"),
            "detailsoff" => new ConversationCommand("detailsOff"),
            "mute" => new ConversationCommand("mute"),
            "unmute" => new ConversationCommand("unmute"),
            "concise" => new ConversationCommand("concise"),
            "comprehensive" => new ConversationCommand("comprehensive"),
            "toolson" => new ConversationCommand("toolsOn"),
            "toolsoff" => new ConversationCommand("toolsOff"),
            "status" => new ConversationCommand("status"),
            "alias" => new ConversationCommand("alias", intent.Target ?? string.Empty),
            "append" => new ConversationCommand("append", intent.Text ?? string.Empty),
            "replace" => new ConversationCommand("replace", intent.OldText ?? string.Empty, intent.NewText ?? string.Empty),
            _ => null
        };
    }

    public static ApprovalCommand ToApprovalCommand(this InterpretedIntent? intent)
    {
        if (intent == null || string.IsNullOrWhiteSpace(intent.Intent))
        {
            return ApprovalCommand.Unknown;
        }

        return intent.Intent.ToLowerInvariant() switch
        {
            "affirmative" => ApprovalCommand.Affirmative,
            "cancel" => ApprovalCommand.Cancel,
            "redictate" => ApprovalCommand.Redictate,
            "useoriginal" => ApprovalCommand.UseOriginal,
            _ => ApprovalCommand.Unknown
        };
    }
}
