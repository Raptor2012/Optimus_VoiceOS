namespace Optimus.Core.Tests.Memory;

using System;
using System.Collections.Generic;
using Optimus.Core.Memory;
using Xunit;

public sealed class MemoryStoreTests : IDisposable
{
    private readonly MemoryStore _store;

    public MemoryStoreTests()
    {
        _store = MemoryStore.CreateInMemory();
    }

    public void Dispose()
    {
        _store.Dispose();
    }

    [Fact]
    public void SaveAlias_And_ResolveAlias_ReturnsStoredValue()
    {
        _store.SaveAlias("my repo", "Optimus_VoiceOS");

        string? resolved = _store.ResolveAlias("my repo");

        Assert.Equal("Optimus_VoiceOS", resolved);
    }

    [Fact]
    public void ResolveAlias_IsCaseInsensitive()
    {
        _store.SaveAlias("My Repo", "Optimus_VoiceOS");

        Assert.Equal("Optimus_VoiceOS", _store.ResolveAlias("my repo"));
        Assert.Equal("Optimus_VoiceOS", _store.ResolveAlias("MY REPO"));
        Assert.Equal("Optimus_VoiceOS", _store.ResolveAlias("mY rEpO"));
    }

    [Fact]
    public void SaveAlias_ExistingKey_UpdatesValue()
    {
        _store.SaveAlias("my repo", "First_Value");
        _store.SaveAlias("my repo", "Updated_Value");

        Assert.Equal("Updated_Value", _store.ResolveAlias("my repo"));
    }

    [Fact]
    public void ResolveAlias_NonExistentKey_ReturnsNull()
    {
        Assert.Null(_store.ResolveAlias("non_existent_alias"));
    }

    [Fact]
    public void DeleteAlias_RemovesAlias()
    {
        _store.SaveAlias("temp", "temp_val");
        Assert.NotNull(_store.ResolveAlias("temp"));

        bool deleted = _store.DeleteAlias("temp");
        Assert.True(deleted);
        Assert.Null(_store.ResolveAlias("temp"));
    }

    [Fact]
    public void GetAllAliases_ReturnsAllStoredAliasesSorted()
    {
        _store.SaveAlias("zebra", "z_val");
        _store.SaveAlias("apple", "a_val");

        IReadOnlyList<AliasRecord> all = _store.GetAllAliases();

        Assert.Equal(2, all.Count);
        Assert.Equal("apple", all[0].Key);
        Assert.Equal("zebra", all[1].Key);
    }

    [Fact]
    public void SetPreference_And_GetPreference_ReturnsStoredValue()
    {
        _store.SetPreference("preferred_voice", "en_GB-northern_english_male");

        string? value = _store.GetPreference("preferred_voice");

        Assert.Equal("en_GB-northern_english_male", value);
    }

    [Fact]
    public void GetPreference_IsCaseInsensitive()
    {
        _store.SetPreference("Narrate_Responses", "true");

        Assert.Equal("true", _store.GetPreference("narrate_responses"));
        Assert.Equal("true", _store.GetPreference("NARRATE_RESPONSES"));
    }

    [Fact]
    public void GetPreference_NonExistentKey_ReturnsDefaultValue()
    {
        string? missing = _store.GetPreference("non_existent_pref", defaultValue: "default_val");
        Assert.Equal("default_val", missing);

        Assert.Null(_store.GetPreference("non_existent_pref"));
    }

    [Fact]
    public void SetPreference_ExistingKey_UpdatesValue()
    {
        _store.SetPreference("default_provider", "Claude");
        _store.SetPreference("default_provider", "Antigravity");

        Assert.Equal("Antigravity", _store.GetPreference("default_provider"));
    }

    [Fact]
    public void DeletePreference_RemovesPreference()
    {
        _store.SetPreference("temp_pref", "val");
        Assert.NotNull(_store.GetPreference("temp_pref"));

        bool deleted = _store.DeletePreference("temp_pref");
        Assert.True(deleted);
        Assert.Null(_store.GetPreference("temp_pref"));
    }

    [Fact]
    public void SaveTurn_StoresAllFieldsAndAssignsId()
    {
        var timestamp = new DateTimeOffset(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);
        long id = _store.SaveTurn(
            turnId: "turn-001",
            device: "pixel",
            transcript: "open my repo",
            objective: "open repo",
            response: "opened Optimus_VoiceOS",
            toolCallsJson: "[\"ListWindows\"]",
            timestamp: timestamp);

        Assert.True(id > 0);

        IReadOnlyList<ConversationTurnRecord> turns = _store.GetRecentTurns(10);
        Assert.Single(turns);

        ConversationTurnRecord turn = turns[0];
        Assert.Equal("turn-001", turn.TurnId);
        Assert.Equal("pixel", turn.Device);
        Assert.Equal("open my repo", turn.Transcript);
        Assert.Equal("open repo", turn.Objective);
        Assert.Equal("opened Optimus_VoiceOS", turn.Response);
        Assert.Equal("[\"ListWindows\"]", turn.ToolCallsJson);
        Assert.Equal(timestamp, turn.Timestamp);
    }

    [Fact]
    public void GetRecentTurns_RespectsCountAndOrdersNewestFirst()
    {
        var t1 = new DateTimeOffset(2026, 9, 6, 10, 0, 0, TimeSpan.Zero);
        var t2 = new DateTimeOffset(2026, 9, 6, 11, 0, 0, TimeSpan.Zero);
        var t3 = new DateTimeOffset(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

        _store.SaveTurn("t1", "pc", "first", timestamp: t1);
        _store.SaveTurn("t2", "pc", "second", timestamp: t2);
        _store.SaveTurn("t3", "pc", "third", timestamp: t3);

        IReadOnlyList<ConversationTurnRecord> recent = _store.GetRecentTurns(2);

        Assert.Equal(2, recent.Count);
        Assert.Equal("t3", recent[0].TurnId);
        Assert.Equal("t2", recent[1].TurnId);
    }

    [Fact]
    public void GetTurnsBetween_FiltersCorrectTimeRange()
    {
        var t1 = new DateTimeOffset(2026, 9, 6, 8, 0, 0, TimeSpan.Zero);
        var t2 = new DateTimeOffset(2026, 9, 6, 10, 0, 0, TimeSpan.Zero);
        var t3 = new DateTimeOffset(2026, 9, 6, 14, 0, 0, TimeSpan.Zero);

        _store.SaveTurn("t1", "pc", "morning", timestamp: t1);
        _store.SaveTurn("t2", "pc", "midday", timestamp: t2);
        _store.SaveTurn("t3", "pc", "afternoon", timestamp: t3);

        IReadOnlyList<ConversationTurnRecord> filtered = _store.GetTurnsBetween(
            new DateTimeOffset(2026, 9, 6, 9, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 6, 12, 0, 0, TimeSpan.Zero));

        Assert.Single(filtered);
        Assert.Equal("t2", filtered[0].TurnId);
    }

    [Fact]
    public void UpdateResponse_UpdatesResponseForSpecificTurn()
    {
        _store.SaveTurn("turn-abc", "pc", "check status", response: null);

        bool updated = _store.UpdateResponse("turn-abc", "All 35 unit tests passed.");
        Assert.True(updated);

        IReadOnlyList<ConversationTurnRecord> turns = _store.GetRecentTurns(1);
        Assert.Single(turns);
        Assert.Equal("All 35 unit tests passed.", turns[0].Response);
    }

    [Fact]
    public void SaveSummary_And_GetSummaries_StoresAndRetrievesInOrder()
    {
        var start1 = new DateTimeOffset(2026, 9, 6, 0, 0, 0, TimeSpan.Zero);
        var end1 = new DateTimeOffset(2026, 9, 6, 4, 0, 0, TimeSpan.Zero);
        var created1 = new DateTimeOffset(2026, 9, 6, 4, 1, 0, TimeSpan.Zero);

        var start2 = new DateTimeOffset(2026, 9, 6, 4, 0, 0, TimeSpan.Zero);
        var end2 = new DateTimeOffset(2026, 9, 6, 8, 0, 0, TimeSpan.Zero);
        var created2 = new DateTimeOffset(2026, 9, 6, 8, 1, 0, TimeSpan.Zero);

        _store.SaveSummary(start1, end1, "Early morning summary.", created1);
        _store.SaveSummary(start2, end2, "Mid morning summary.", created2);

        IReadOnlyList<SummaryRecord> summaries = _store.GetSummaries(10);

        Assert.Equal(2, summaries.Count);
        Assert.Equal("Mid morning summary.", summaries[0].SummaryText);
        Assert.Equal(end2, summaries[0].PeriodEnd);
        Assert.Equal("Early morning summary.", summaries[1].SummaryText);
    }
}
