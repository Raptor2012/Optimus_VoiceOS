namespace Optimus.ModelEval;

using System.IO;
using System.Text.Json;

public static class ScenarioCatalog
{
    public static IReadOnlyList<EvaluationScenario> Load(string? path = null)
    {
        path ??= Path.Combine(AppContext.BaseDirectory, "scenarios.json");
        using FileStream stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<List<EvaluationScenario>>(stream, JsonOptions)
            ?? throw new InvalidDataException($"Scenario file is empty: {path}");
    }

    public static void Validate(IReadOnlyList<EvaluationScenario> scenarios)
    {
        ArgumentNullException.ThrowIfNull(scenarios);
        if (scenarios.Count != 30) throw new InvalidDataException("The evaluation catalog must contain exactly 30 scenarios.");

        var expectedApps = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Codex"] = 6, ["Claude"] = 6, ["Antigravity"] = 6, ["AO"] = 6, ["Cross-app"] = 6
        };
        foreach (IGrouping<string, EvaluationScenario> group in scenarios.GroupBy(s => s.App, StringComparer.OrdinalIgnoreCase))
        {
            if (!expectedApps.TryGetValue(group.Key, out var expected) || group.Count() != expected)
                throw new InvalidDataException($"Unexpected scenario distribution for app '{group.Key}'.");
        }

        if (scenarios.Select(s => s.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != scenarios.Count)
            throw new InvalidDataException("Scenario ids must be unique.");

        foreach (EvaluationScenario scenario in scenarios)
        {
            if (string.IsNullOrWhiteSpace(scenario.Input) || scenario.ExpectedActions.Count == 0 ||
                scenario.PassCriteria.Count == 0 || scenario.FailCriteria.Count == 0)
                throw new InvalidDataException($"Scenario {scenario.Id} needs input, actions, and pass/fail criteria.");
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip
    };
}
