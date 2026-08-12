using CircuitBreaker.Core;

namespace CircuitBreaker.Simulation;

public static class BuiltInScenarios
{
    private static readonly IReadOnlyDictionary<string, Func<SimulationScenario>> Factories =
        new Dictionary<string, Func<SimulationScenario>>(StringComparer.OrdinalIgnoreCase)
        {
            ["short-outage"] = () => Create("short-outage", "A brief outage that exposes recovery latency.", 40, 3, 10, (0, 10, false), (10, 15, true), (15, 40, false)),
            ["long-outage"] = () => Create("long-outage", "A sustained outage where the breaker avoids failed work.", 90, 3, 10, (0, 10, false), (10, 50, true), (50, 90, false)),
            ["failure-before-threshold"] = () => Create("failure-before-threshold", "Failures stop before the trip threshold.", 30, 3, 8, (0, 10, false), (10, 12, true), (12, 30, false)),
            ["flapping"] = () => Create("flapping", "Alternating healthy and failing periods.", 60, 2, 6, (0, 10, false), (10, 18, true), (18, 26, false), (26, 36, true), (36, 44, false), (44, 52, true), (52, 60, false)),
            ["failed-half-open-probe"] = () => Create("failed-half-open-probe", "A half-open probe fails before recovery.", 40, 2, 6, (0, 8, false), (8, 24, true), (24, 40, false)),
            ["successful-recovery"] = () => Create("successful-recovery", "A half-open probe closes the breaker after recovery.", 35, 2, 5, (0, 8, false), (8, 15, true), (15, 35, false))
        };

    public static IReadOnlyCollection<string> Names => Factories.Keys.OrderBy(x => x).ToArray();
    public static SimulationScenario Get(string name) => Factories.TryGetValue(name, out var factory) ? factory() :
        throw new ArgumentException($"Unknown scenario '{name}'. Available: {string.Join(", ", Names)}");

    private static SimulationScenario Create(string name, string description, int duration, int threshold, int openSeconds,
        params (int Start, int End, bool Failing)[] windows)
    {
        var scenario = new SimulationScenario(1, name, description, TimeSpan.FromSeconds(duration),
            new CircuitBreakerOptions(threshold, TimeSpan.FromSeconds(openSeconds)),
            Enumerable.Range(0, duration).Select(i => new RequestDefinition($"request-{i + 1:D4}", TimeSpan.FromSeconds(i))).ToArray(),
            windows.Select(x => new AvailabilityWindow(TimeSpan.FromSeconds(x.Start), TimeSpan.FromSeconds(x.End),
                x.Failing ? DownstreamAvailability.Failing : DownstreamAvailability.Healthy)).ToArray());
        ScenarioValidator.Validate(scenario);
        return scenario;
    }
}
