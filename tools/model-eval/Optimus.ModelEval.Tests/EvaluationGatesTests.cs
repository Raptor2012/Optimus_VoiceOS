namespace Optimus.ModelEval.Tests;

using Optimus.ModelEval;
using Xunit;

public sealed class EvaluationGatesTests
{
    [Fact]
    public void ReportPassesOnlyWhenAllSelectionGatesPass()
    {
        IReadOnlyList<EvaluationScenario> scenarios = ScenarioCatalog.Load(FindScenarioFile());
        var observations = scenarios.Select(scenario => new ScenarioObservation(
            Completed: true,
            WrongTargetSend: false,
            StopHandledCorrectly: true,
            FrontierInferenceDetected: false,
            EndOfSpeechToFirstActionMilliseconds: 180,
            TaskCompletionMilliseconds: 900,
            PeakVramMiB: 5200)).ToArray();

        EvaluationReport report = EvaluationGates.EvaluateReport("test-model", scenarios, observations);

        Assert.True(report.SelectionGatesPassed);
        Assert.Equal(1, report.CompletionRate);
        Assert.True(report.ZeroWrongTargetSends);
        Assert.True(report.CorrectStopHandling);
        Assert.True(report.NoFrontierInference);
    }

    [Theory]
    [InlineData(4, 0, 0, 0, false)]
    [InlineData(0, 1, 0, 0, false)]
    [InlineData(0, 0, 1, 0, false)]
    [InlineData(0, 0, 0, 1, false)]
    public void ReportFailsTheRelevantGate(int incomplete, int wrongTarget, int badStop, int frontier, bool expected)
    {
        IReadOnlyList<EvaluationScenario> scenarios = ScenarioCatalog.Load(FindScenarioFile());
        var observations = scenarios.Select((scenario, index) => new ScenarioObservation(
            Completed: index >= incomplete,
            WrongTargetSend: index < wrongTarget,
            StopHandledCorrectly: badStop == 0 || !scenario.RequiresStopHandling,
            FrontierInferenceDetected: index < frontier,
            EndOfSpeechToFirstActionMilliseconds: 180,
            TaskCompletionMilliseconds: 900,
            PeakVramMiB: 5200)).ToArray();

        EvaluationReport report = EvaluationGates.EvaluateReport("test-model", scenarios, observations);

        Assert.Equal(expected, report.SelectionGatesPassed);
    }

    private static string FindScenarioFile() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "scenarios.json"));
}
