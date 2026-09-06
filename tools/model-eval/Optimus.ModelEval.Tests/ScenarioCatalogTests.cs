namespace Optimus.ModelEval.Tests;

using Optimus.ModelEval;
using Xunit;

public sealed class ScenarioCatalogTests
{
    [Fact]
    public void CatalogHasSixScenariosPerRequiredApp()
    {
        IReadOnlyList<EvaluationScenario> scenarios = ScenarioCatalog.Load(FindScenarioFile());

        Assert.Equal(30, scenarios.Count);
        foreach (string app in new[] { "Codex", "Claude", "Antigravity", "AO", "Cross-app" })
            Assert.Equal(6, scenarios.Count(s => string.Equals(s.App, app, StringComparison.OrdinalIgnoreCase)));

        Assert.Equal(30, scenarios.Select(s => s.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(scenarios, scenario =>
        {
            Assert.False(string.IsNullOrWhiteSpace(scenario.Input));
            Assert.NotEmpty(scenario.ExpectedActions);
            Assert.NotEmpty(scenario.PassCriteria);
            Assert.NotEmpty(scenario.FailCriteria);
        });
    }

    [Fact]
    public void CatalogIncludesStopAndTargetSafetyCoverage()
    {
        IReadOnlyList<EvaluationScenario> scenarios = ScenarioCatalog.Load(FindScenarioFile());

        Assert.True(scenarios.Count(scenario => scenario.RequiresStopHandling) >= 5);
        Assert.Contains(scenarios, scenario => scenario.ExpectedTarget is null);
        Assert.Contains(scenarios, scenario => scenario.PassCriteria.Any(criteria => criteria.Contains("frontier", StringComparison.OrdinalIgnoreCase)));
        Assert.All(scenarios.Where(scenario => scenario.ExpectedTarget is not null), scenario =>
        {
            Assert.NotNull(scenario.ExpectedTarget);
            Assert.NotEmpty(scenario.ExpectedTarget!);
        });
    }

    private static string FindScenarioFile()
    {
        string path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "scenarios.json"));
        Assert.True(File.Exists(path), $"Scenario catalog was not copied to {path}");
        return path;
    }
}
