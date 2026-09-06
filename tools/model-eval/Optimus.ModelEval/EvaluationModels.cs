namespace Optimus.ModelEval;

using System.Collections.Generic;

/// <summary>A fixed workflow that must be identical for every candidate model.</summary>
public sealed record EvaluationScenario(
    string Id,
    string App,
    string Name,
    string Input,
    IReadOnlyList<string> ExpectedActions,
    IReadOnlyList<string> PassCriteria,
    IReadOnlyList<string> FailCriteria,
    bool RequiresStopHandling = false,
    string? ExpectedTarget = null);

/// <summary>One model-run observation, including the measurements needed for selection.</summary>
public sealed record ScenarioObservation(
    bool Completed,
    bool WrongTargetSend,
    bool StopHandledCorrectly,
    bool FrontierInferenceDetected,
    long EndOfSpeechToFirstActionMilliseconds,
    long TaskCompletionMilliseconds,
    long PeakVramMiB,
    IReadOnlyList<string>? ActualActions = null,
    string? Notes = null);

public sealed record ScenarioResult(
    EvaluationScenario Scenario,
    ScenarioObservation Observation,
    bool Passed,
    IReadOnlyList<string> FailedGates);

public sealed record EvaluationReport(
    string ModelId,
    IReadOnlyList<ScenarioResult> Results,
    double CompletionRate,
    bool ZeroWrongTargetSends,
    bool CorrectStopHandling,
    bool NoFrontierInference,
    bool SelectionGatesPassed)
{
    public int CompletedCount => Results.Count(result => result.Observation.Completed);
}

public static class EvaluationGates
{
    public const double MinimumCompletionRate = 0.90;

    public static ScenarioResult Evaluate(EvaluationScenario scenario, ScenarioObservation observation)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentNullException.ThrowIfNull(observation);

        var failures = new List<string>();
        if (!observation.Completed) failures.Add("completion");
        if (observation.WrongTargetSend) failures.Add("wrong-target-send");
        if (scenario.RequiresStopHandling && !observation.StopHandledCorrectly)
            failures.Add("stop-handling");
        if (observation.FrontierInferenceDetected) failures.Add("frontier-inference");

        return new ScenarioResult(scenario, observation, failures.Count == 0, failures);
    }

    public static EvaluationReport EvaluateReport(
        string modelId,
        IReadOnlyList<EvaluationScenario> scenarios,
        IReadOnlyList<ScenarioObservation> observations)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentNullException.ThrowIfNull(scenarios);
        ArgumentNullException.ThrowIfNull(observations);
        if (scenarios.Count != observations.Count)
            throw new ArgumentException("There must be one observation for every scenario.", nameof(observations));

        var results = new List<ScenarioResult>(scenarios.Count);
        for (var i = 0; i < scenarios.Count; i++)
            results.Add(Evaluate(scenarios[i], observations[i]));

        var completed = results.Count(result => result.Observation.Completed);
        var completionRate = results.Count == 0 ? 0 : (double)completed / results.Count;
        var zeroWrongTargetSends = results.All(result => !result.Observation.WrongTargetSend);
        var correctStopHandling = results
            .Where(result => result.Scenario.RequiresStopHandling)
            .All(result => result.Observation.StopHandledCorrectly);
        var noFrontierInference = results.All(result => !result.Observation.FrontierInferenceDetected);

        return new EvaluationReport(
            modelId,
            results,
            completionRate,
            zeroWrongTargetSends,
            correctStopHandling,
            noFrontierInference,
            completionRate >= EvaluationGates.MinimumCompletionRate &&
            zeroWrongTargetSends && correctStopHandling && noFrontierInference);
    }
}
