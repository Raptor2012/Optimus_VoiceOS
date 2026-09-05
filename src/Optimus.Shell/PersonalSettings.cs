namespace Optimus.Shell;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

public sealed record SavedVoiceTarget(string DestinationId, string WindowTitle);

/// <summary>One user's preferences. No prompts or audio are saved.</summary>
public sealed class PersonalSettings
{
    public string? DestinationId { get; set; }
    public bool CleanupEnabled { get; set; }
    public bool ShortReview { get; set; }

    /// <summary>
    /// Whether speaking may interrupt the tool mid-sentence.
    /// </summary>
    /// <remarks>
    /// Off by default because it requires the microphone to stay open while the speaker plays.
    /// On headphones nothing leaks back and anything heard is genuinely the user. On speakers the
    /// microphone hears the tool itself, so this must stay off until echo cancellation exists.
    /// </remarks>
    public bool BargeInEnabled { get; set; }
    public Dictionary<string, string> WindowTitles { get; set; } = new();
    public Dictionary<string, SavedVoiceTarget> Aliases { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OptimusVoiceOS", "preferences.json");

    public static PersonalSettings Load(string path)
    {
        if (!File.Exists(path)) return new();
        try { return JsonSerializer.Deserialize<PersonalSettings>(File.ReadAllText(path)) ?? new(); }
        catch (JsonException) { return new(); }
        catch (IOException) { return new(); }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this));
        File.Move(temporary, path, overwrite: true);
    }
}
