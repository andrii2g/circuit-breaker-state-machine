using CircuitBreaker.Core;
using CircuitBreaker.Simulation;

return await Cli.RunAsync(args);

internal static class Cli
{
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var options = Parse(args);
            if (options.Help) { PrintHelp(); return 0; }
            if (options.List)
            {
                Console.WriteLine("Built-in scenarios:");
                foreach (var name in BuiltInScenarios.Names) Console.WriteLine($"- {name}");
                return 0;
            }
            if (options.Scenario is not null && options.File is not null)
                throw new ArgumentException("--scenario and --file are mutually exclusive.");
            var scenario = options.File is not null ? ScenarioJson.Load(options.File) : BuiltInScenarios.Get(options.Scenario ?? "long-outage");
            if (options.Threshold is not null || options.OpenDuration is not null)
                scenario = scenario with { Breaker = new CircuitBreakerOptions(options.Threshold ?? scenario.Breaker.FailureThreshold, options.OpenDuration ?? scenario.Breaker.OpenDuration) };
            ScenarioValidator.Validate(scenario);
            var comparison = await SimulationRunner.RunAsync(scenario);
            var paths = await ArtifactWriter.WriteAsync(comparison, options.Output ?? "artifacts");
            PrintSummary(comparison, paths);
            return 0;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"error: {exception.Message}");
            return 2;
        }
    }

    private static Options Parse(string[] args)
    {
        var result = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            string Value() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"Missing value for {args[i]}.");
            result = args[i] switch
            {
                "--help" or "-h" => result with { Help = true },
                "--list-scenarios" => result with { List = true },
                "--scenario" => result with { Scenario = Value() },
                "--file" => result with { File = Value() },
                "--threshold" => result with { Threshold = int.TryParse(Value(), out var n) ? n : throw new ArgumentException("Threshold must be an integer.") },
                "--open-duration" => result with { OpenDuration = TimeSpan.TryParse(Value(), out var duration) ? duration : throw new ArgumentException("Open duration must be a TimeSpan.") },
                "--output" => result with { Output = Value() },
                _ => throw new ArgumentException($"Unknown option '{args[i]}'. Use --help.")
            };
        }
        return result;
    }

    private static void PrintSummary(SimulationComparison result, IReadOnlyList<string> paths)
    {
        Console.WriteLine($"Scenario: {result.Scenario.Name}");
        Console.WriteLine($"- Configuration: threshold {result.Scenario.Breaker.FailureThreshold}, open {result.Scenario.Breaker.OpenDuration:c}");
        Console.WriteLine($"- Baseline: {result.Baseline.Metrics.DownstreamAttempts} attempts, {result.Baseline.Metrics.FailedAttempts} failures");
        Console.WriteLine($"- Protected: {result.BreakerProtected.Metrics.DownstreamAttempts} attempts, {result.BreakerProtected.Metrics.RejectedRequests} rejections, {result.BreakerProtected.Metrics.BreakerOpenings} openings");
        Console.WriteLine($"- Outage load avoided: {(result.Comparison.OutageLoadAvoidedPercentage is double p ? $"{p:F1}%" : "unavailable")}");
        Console.WriteLine($"- Recovery latency: {result.Comparison.RecoveryLatency?.ToString("c") ?? "unavailable"}");
        Console.WriteLine("Generated artifacts:");
        foreach (var path in paths) Console.WriteLine($"- {Path.GetFullPath(path)}");
    }

    private static void PrintHelp() => Console.WriteLine("""
        Circuit breaker deterministic laboratory
          --list-scenarios
          --scenario <name> | --file <path>
          --threshold <integer>
          --open-duration <TimeSpan>
          --output <directory>
        """);

    private sealed record Options(bool Help = false, bool List = false, string? Scenario = null,
        string? File = null, int? Threshold = null, TimeSpan? OpenDuration = null, string? Output = null);
}
