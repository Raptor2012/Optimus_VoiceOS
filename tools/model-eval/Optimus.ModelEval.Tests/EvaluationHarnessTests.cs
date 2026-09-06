namespace Optimus.ModelEval.Tests;

using Optimus.ModelEval;
using Xunit;

public sealed class EvaluationHarnessTests
{
    [Fact]
    public async Task HarnessRunsEveryScenarioInCatalogOrder()
    {
        IReadOnlyList<EvaluationScenario> scenarios = ScenarioCatalog.Load(ScenarioPath());
        var seen = new List<string>();
        var harness = new EvaluationHarness(scenarios);

        EvaluationReport report = await harness.RunAsync("dry-run", (scenario, _) =>
        {
            seen.Add(scenario.Id);
            return Task.FromResult(new ScenarioObservation(true, false, true, false, 12, -1, 4096));
        });

        Assert.Equal(scenarios.Select(scenario => scenario.Id), seen);
        Assert.Equal(30, report.Results.Count);
        Assert.All(report.Results, result => Assert.True(result.Passed));
        Assert.All(report.Results, result => Assert.Equal(0, result.Observation.TaskCompletionMilliseconds));
    }

    private static string ScenarioPath() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "scenarios.json"));
}
