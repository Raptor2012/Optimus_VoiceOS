namespace Optimus.Core.Tests;

using System;
using System.IO;
using Optimus.Core.Voice;
using Optimus.Shell;
using Xunit;

public class ConversationCommandTests
{
    [Theory]
    [InlineData("Switch to Claude.", "switch", "Claude")]
    [InlineData("Add unit tests", "append", "unit tests")]
    [InlineData("remember this as voice project", "alias", "voice project")]
    [InlineData("cleanup off", "cleanupOff", "")]
    [InlineData("short readback", "shortReview", "")]
    [InlineData("open ao", "openAo", "")]
    [InlineData("open orchestrator", "openAo", "")]
    [InlineData("launch ao", "openAo", "")]
    [InlineData("pause", "pause", "")]
    [InlineData("pause listening", "pause", "")]
    [InlineData("resume", "resume", "")]
    [InlineData("resume listening", "resume", "")]
    [InlineData("end conversation", "endConversation", "")]
    [InlineData("end session", "endConversation", "")]
    [InlineData("approve plan", "approveDecision", "")]
    [InlineData("approve and start", "approveDecision", "")]
    [InlineData("request changes", "rejectDecision", "")]
    [InlineData("reject plan", "rejectDecision", "")]
    public void RecognizesExplicitCommands(string input, string kind, string value)
    {
        var command = ConversationCommand.Parse(input);
        Assert.NotNull(command);
        Assert.Equal(kind, command.Kind);
        Assert.Equal(value, command.Value);
    }

    [Fact]
    public void OrdinaryPromptIsNotAControlCommand() =>
        Assert.Null(ConversationCommand.Parse("Please add a button that says switch to Claude."));

    [Fact]
    public void ReplacementPreservesBothPhrases()
    {
        var command = ConversationCommand.Parse("replace fetchUser with loadUser");
        Assert.Equal(new ConversationCommand("replace", "fetchUser", "loadUser"), command);
    }

    [Fact]
    public void PreferencesRoundTripWithoutPromptContent()
    {
        string path = Path.Combine(Path.GetTempPath(), "optimus-settings-" + Guid.NewGuid() + ".json");
        try
        {
            var settings = new PersonalSettings { DestinationId = "claude", ShortReview = true };
            settings.Aliases["voice project"] = new("claude", "Optimus");
            settings.WindowTitles["claude"] = "Optimus";
            settings.Save(path);
            var restored = PersonalSettings.Load(path);
            Assert.Equal("claude", restored.DestinationId);
            Assert.False(restored.CleanupEnabled);
            Assert.True(restored.ShortReview);
            Assert.Equal("Optimus", restored.Aliases["voice project"].WindowTitle);
            Assert.Equal("Optimus", restored.WindowTitles["claude"]);
        }
        finally { File.Delete(path); }
    }
}
