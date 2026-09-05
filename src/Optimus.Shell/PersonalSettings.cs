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
