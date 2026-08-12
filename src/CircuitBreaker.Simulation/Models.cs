using System.Text.Json;
using System.Text.Json.Serialization;
using CircuitBreaker.Core;

namespace CircuitBreaker.Simulation;

public enum DownstreamAvailability { Healthy, Failing }
public enum SimulationRunKind { Baseline, BreakerProtected }
public enum SimulationEventType
{
    RequestArrived, RequestAttempted, RequestSucceeded, RequestFailed, RequestRejected,
    BreakerStateChanged, HalfOpenProbeStarted, HalfOpenProbeSucceeded, HalfOpenProbeFailed,
    DownstreamAvailabilityChanged
}

public sealed record AvailabilityWindow(TimeSpan Start, TimeSpan End, DownstreamAvailability Status);
public sealed record RequestDefinition(string Id, TimeSpan ScheduledOffset);
public sealed record PeriodicRequests(TimeSpan Start, TimeSpan Interval, int Count);
public sealed record SimulationScenario(int SchemaVersion, string Name, string Description, TimeSpan Duration,
    CircuitBreakerOptions Breaker, IReadOnlyList<RequestDefinition> Requests, IReadOnlyList<AvailabilityWindow> Availability);
public sealed record ScenarioDocument(int SchemaVersion, string Name, string Description, TimeSpan Duration,
    CircuitBreakerOptions Breaker, PeriodicRequests Requests, IReadOnlyList<AvailabilityWindow> Availability);

public sealed record SimulationEvent(long Sequence, TimeSpan Elapsed, SimulationRunKind RunKind,
    SimulationEventType Type, string? RequestId = null, CircuitBreakerState? StateBefore = null,
    CircuitBreakerState? StateAfter = null, DownstreamAvailability? Availability = null,
    bool DownstreamAttempted = false, string? Detail = null);

public sealed record RunMetrics(int ReceivedRequests, int DownstreamAttempts, int RejectedRequests,
    int SuccessfulAttempts, int FailedAttempts, int BreakerOpenings, int HalfOpenProbes,
    int SuccessfulProbes, int FailedProbes);
public sealed record SimulationRunResult(int SchemaVersion, string ScenarioName, SimulationRunKind RunKind,
    IReadOnlyList<RequestDefinition> Requests, IReadOnlyList<SimulationEvent> Events, RunMetrics Metrics);
public sealed record ComparisonMetrics(int DownstreamAttemptsAvoided, int OutageAttemptsAvoided,
    double? OutageLoadAvoidedPercentage, TimeSpan? DownstreamRecoveryAt,
    TimeSpan? FirstSuccessfulProtectedRequestAfterRecovery, TimeSpan? RecoveryLatency);
public sealed record SimulationComparison(int SchemaVersion, SimulationScenario Scenario,
    SimulationRunResult Baseline, SimulationRunResult BreakerProtected, ComparisonMetrics Comparison);

public static class ScenarioValidator
{
    public static void Validate(SimulationScenario scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(scenario.Name)) errors.Add("Scenario name is required.");
        if (scenario.Duration <= TimeSpan.Zero) errors.Add("Duration must be positive.");
        if (scenario.Requests.Count == 0) errors.Add("At least one request is required.");
        if (scenario.Requests.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != scenario.Requests.Count)
            errors.Add("Request IDs must be unique.");
        if (scenario.Requests.Any(x => x.ScheduledOffset < TimeSpan.Zero || x.ScheduledOffset >= scenario.Duration))
            errors.Add("Requests must be scheduled inside the scenario duration.");
        var windows = scenario.Availability.OrderBy(x => x.Start).ToArray();
        if (windows.Length == 0 || windows[0].Start != TimeSpan.Zero || windows[^1].End != scenario.Duration)
            errors.Add("Availability windows must cover the entire scenario duration.");
        for (var i = 0; i < windows.Length; i++)
        {
            if (windows[i].Start < TimeSpan.Zero || windows[i].End <= windows[i].Start || windows[i].End > scenario.Duration)
                errors.Add($"Availability window {i} has invalid boundaries.");
            if (i > 0 && windows[i - 1].End != windows[i].Start)
                errors.Add("Availability windows must be contiguous and non-overlapping.");
        }
        if (errors.Count > 0) throw new ArgumentException(string.Join(Environment.NewLine, errors));
    }
}

public static class ScenarioJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static SimulationScenario Load(string path)
    {
        var document = JsonSerializer.Deserialize<ScenarioDocument>(File.ReadAllText(path), Options)
            ?? throw new InvalidDataException("Scenario JSON was empty.");
        if (document.Requests.Interval <= TimeSpan.Zero) throw new ArgumentException("Request interval must be positive.");
        if (document.Requests.Count < 1) throw new ArgumentException("Request count must be positive.");
        var requests = Enumerable.Range(1, document.Requests.Count)
            .Select(i => new RequestDefinition($"request-{i:D4}", document.Requests.Start + ((i - 1) * document.Requests.Interval)))
            .ToArray();
        var scenario = new SimulationScenario(document.SchemaVersion, document.Name, document.Description,
            document.Duration, document.Breaker, requests, document.Availability);
        ScenarioValidator.Validate(scenario);
        return scenario;
    }

    public static JsonSerializerOptions CreateOptions(bool indented = true)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = indented };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
