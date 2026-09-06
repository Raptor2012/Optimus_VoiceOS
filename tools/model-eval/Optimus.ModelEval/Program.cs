namespace Optimus.ModelEval;

using System.Text.Json;

internal static class Program
{
    public static int Main(string[] args)
    {
        if (args.Any(arg => string.Equals(arg, "--help", StringComparison.OrdinalIgnoreCase)))
        {
            Console.WriteLine("Model evaluation catalog: 30 fixed scenarios, evaluated by the test project or a local runner.");
            Console.WriteLine("No model is started by this command; use EvaluationHarness.RunAsync from a runner.");
            return 0;
        }

        IReadOnlyList<EvaluationScenario> scenarios = ScenarioCatalog.Load();
        ScenarioCatalog.Validate(scenarios);
        Console.WriteLine(JsonSerializer.Serialize(new { scenarioCount = scenarios.Count, scenarios = scenarios.Select(s => s.Id) }));
        return 0;
    }
}
