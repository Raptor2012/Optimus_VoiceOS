namespace Optimus.ModelEval;

using System.Diagnostics;

public delegate Task<ScenarioObservation> ScenarioRunner(EvaluationScenario scenario, CancellationToken cancellationToken);

/// <summary>Runs the fixed catalog against a local adapter and evaluates the selection gates.</summary>
public sealed class EvaluationHarness
{
    private readonly IReadOnlyList<EvaluationScenario> _scenarios;

    public EvaluationHarness(IReadOnlyList<EvaluationScenario> scenarios)
    {
        ScenarioCatalog.Validate(scenarios);
        _scenarios = scenarios;
    }

    public IReadOnlyList<EvaluationScenario> Scenarios => _scenarios;

    public async Task<EvaluationReport> RunAsync(
        string modelId,
        ScenarioRunner runner,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentNullException.ThrowIfNull(runner);

        var observations = new List<ScenarioObservation>(_scenarios.Count);
        foreach (EvaluationScenario scenario in _scenarios)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stopwatch = Stopwatch.StartNew();
            ScenarioObservation observation = await runner(scenario, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();

            // Adapters should report measured values. Fill only a missing completion time so
            // a test/dry runner still produces a useful report without changing its intent.
            if (observation.TaskCompletionMilliseconds < 0)
                observation = observation with { TaskCompletionMilliseconds = stopwatch.ElapsedMilliseconds };
            observations.Add(observation);
        }

        return EvaluationGates.EvaluateReport(modelId, _scenarios, observations);
    }
}
