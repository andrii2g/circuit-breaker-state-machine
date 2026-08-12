using CircuitBreaker.Core;
using Microsoft.Extensions.Time.Testing;

namespace CircuitBreaker.Simulation;

public sealed class FakeDownstreamService
{
    private readonly SimulationScenario _scenario;
    private readonly TimeProvider _timeProvider;
    private readonly DateTimeOffset _start;

    public FakeDownstreamService(SimulationScenario scenario, TimeProvider timeProvider, DateTimeOffset start)
    {
        _scenario = scenario;
        _timeProvider = timeProvider;
        _start = start;
    }

    public int InvocationCount { get; private set; }

    public DownstreamAvailability GetAvailability(TimeSpan elapsed) =>
        _scenario.Availability.Single(x => elapsed >= x.Start && elapsed < x.End).Status;

    public ValueTask<string> InvokeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        InvocationCount++;
        var elapsed = _timeProvider.GetUtcNow() - _start;
        if (GetAvailability(elapsed) == DownstreamAvailability.Failing)
            throw new DownstreamUnavailableException($"Downstream is failing at {elapsed}.");
        return ValueTask.FromResult("ok");
    }
}

public sealed class DownstreamUnavailableException : Exception
{
    public DownstreamUnavailableException(string message) : base(message) { }
}

public static class SimulationRunner
{
    private static readonly DateTimeOffset Start = new(2040, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static async Task<SimulationComparison> RunAsync(SimulationScenario scenario, CancellationToken cancellationToken = default)
    {
        ScenarioValidator.Validate(scenario);
        var baseline = await RunBaselineAsync(scenario, cancellationToken).ConfigureAwait(false);
        var protectedRun = await RunProtectedAsync(scenario, cancellationToken).ConfigureAwait(false);
        if (!baseline.Requests.SequenceEqual(protectedRun.Requests))
            throw new InvalidOperationException("Baseline and protected request schedules differ.");
        return new(1, scenario, baseline, protectedRun, MetricsCalculator.Compare(scenario, baseline, protectedRun));
    }

    private static async Task<SimulationRunResult> RunBaselineAsync(SimulationScenario scenario, CancellationToken cancellationToken)
    {
        var clock = new FakeTimeProvider(Start);
        var downstream = new FakeDownstreamService(scenario, clock, Start);
        var events = new List<SimulationEvent>();
        long sequence = 0;
        var current = TimeSpan.Zero;
        DownstreamAvailability? previousAvailability = null;
        foreach (var request in scenario.Requests.OrderBy(x => x.ScheduledOffset))
        {
            cancellationToken.ThrowIfCancellationRequested();
            clock.Advance(request.ScheduledOffset - current);
            current = request.ScheduledOffset;
            var availability = downstream.GetAvailability(current);
            AddAvailabilityChange(events, ref sequence, current, SimulationRunKind.Baseline, availability, ref previousAvailability);
            events.Add(new(++sequence, current, SimulationRunKind.Baseline, SimulationEventType.RequestArrived, request.Id, Availability: availability));
            events.Add(new(++sequence, current, SimulationRunKind.Baseline, SimulationEventType.RequestAttempted, request.Id, Availability: availability, DownstreamAttempted: true));
            try
            {
                await downstream.InvokeAsync(cancellationToken).ConfigureAwait(false);
                events.Add(new(++sequence, current, SimulationRunKind.Baseline, SimulationEventType.RequestSucceeded, request.Id, Availability: availability, DownstreamAttempted: true));
            }
            catch (DownstreamUnavailableException exception)
            {
                events.Add(new(++sequence, current, SimulationRunKind.Baseline, SimulationEventType.RequestFailed, request.Id, Availability: availability, DownstreamAttempted: true, Detail: exception.Message));
            }
        }
        return CreateResult(scenario, SimulationRunKind.Baseline, events);
    }

    private static async Task<SimulationRunResult> RunProtectedAsync(SimulationScenario scenario, CancellationToken cancellationToken)
    {
        var clock = new FakeTimeProvider(Start);
        var downstream = new FakeDownstreamService(scenario, clock, Start);
        var breaker = new Core.CircuitBreaker(scenario.Breaker, clock);
        var events = new List<SimulationEvent>();
        long sequence = 0;
        var current = TimeSpan.Zero;
        DownstreamAvailability? previousAvailability = null;
        foreach (var request in scenario.Requests.OrderBy(x => x.ScheduledOffset))
        {
            cancellationToken.ThrowIfCancellationRequested();
            clock.Advance(request.ScheduledOffset - current);
            current = request.ScheduledOffset;
            var availability = downstream.GetAvailability(current);
            AddAvailabilityChange(events, ref sequence, current, SimulationRunKind.BreakerProtected, availability, ref previousAvailability);
            events.Add(new(++sequence, current, SimulationRunKind.BreakerProtected, SimulationEventType.RequestArrived, request.Id, Availability: availability));
            var before = breaker.GetSnapshot();
            var result = await breaker.ExecuteAsync(downstream.InvokeAsync, cancellationToken).ConfigureAwait(false);
            var after = breaker.GetSnapshot();

            if (result.WasHalfOpenProbe)
            {
                events.Add(new(++sequence, current, SimulationRunKind.BreakerProtected, SimulationEventType.BreakerStateChanged,
                    request.Id, CircuitBreakerState.Open, CircuitBreakerState.HalfOpen, availability, Detail: "Open interval elapsed; one probe admitted."));
                events.Add(new(++sequence, current, SimulationRunKind.BreakerProtected, SimulationEventType.HalfOpenProbeStarted,
                    request.Id, CircuitBreakerState.Open, CircuitBreakerState.HalfOpen, availability));
            }

            if (result.WasDownstreamAttempted)
                events.Add(new(++sequence, current, SimulationRunKind.BreakerProtected, SimulationEventType.RequestAttempted,
                    request.Id, before.State, result.WasHalfOpenProbe ? CircuitBreakerState.HalfOpen : before.State, availability, true));

            switch (result.Status)
            {
                case CircuitBreakerExecutionStatus.Succeeded:
                    events.Add(new(++sequence, current, SimulationRunKind.BreakerProtected, SimulationEventType.RequestSucceeded,
                        request.Id, before.State, after.State, availability, true));
                    break;
                case CircuitBreakerExecutionStatus.Failed:
                    events.Add(new(++sequence, current, SimulationRunKind.BreakerProtected, SimulationEventType.RequestFailed,
                        request.Id, before.State, after.State, availability, true, result.Exception?.Message));
                    break;
                case CircuitBreakerExecutionStatus.Rejected:
                    events.Add(new(++sequence, current, SimulationRunKind.BreakerProtected, SimulationEventType.RequestRejected,
                        request.Id, before.State, after.State, availability, false, "Rejected without invoking downstream."));
                    break;
            }

            if (result.WasHalfOpenProbe)
            {
                var probeType = result.IsSuccess ? SimulationEventType.HalfOpenProbeSucceeded : SimulationEventType.HalfOpenProbeFailed;
                events.Add(new(++sequence, current, SimulationRunKind.BreakerProtected, probeType,
                    request.Id, CircuitBreakerState.HalfOpen, after.State, availability, true));
                events.Add(new(++sequence, current, SimulationRunKind.BreakerProtected, SimulationEventType.BreakerStateChanged,
                    request.Id, CircuitBreakerState.HalfOpen, after.State, availability, true));
            }
            else if (before.State != after.State)
            {
                events.Add(new(++sequence, current, SimulationRunKind.BreakerProtected, SimulationEventType.BreakerStateChanged,
                    request.Id, before.State, after.State, availability, true));
            }
        }
        return CreateResult(scenario, SimulationRunKind.BreakerProtected, events);
    }

    private static void AddAvailabilityChange(List<SimulationEvent> events, ref long sequence, TimeSpan elapsed,
        SimulationRunKind kind, DownstreamAvailability availability, ref DownstreamAvailability? previous)
    {
        if (previous == availability) return;
        events.Add(new(++sequence, elapsed, kind, SimulationEventType.DownstreamAvailabilityChanged,
            Availability: availability, Detail: $"Downstream became {availability}."));
        previous = availability;
    }

    private static SimulationRunResult CreateResult(SimulationScenario scenario, SimulationRunKind kind, List<SimulationEvent> events) =>
        new(1, scenario.Name, kind, scenario.Requests.ToArray(), events, MetricsCalculator.ForRun(events));
}

public static class MetricsCalculator
{
    public static RunMetrics ForRun(IReadOnlyCollection<SimulationEvent> events) => new(
        events.Count(x => x.Type == SimulationEventType.RequestArrived),
        events.Count(x => x.Type == SimulationEventType.RequestAttempted),
        events.Count(x => x.Type == SimulationEventType.RequestRejected),
        events.Count(x => x.Type == SimulationEventType.RequestSucceeded),
        events.Count(x => x.Type == SimulationEventType.RequestFailed),
        events.Count(x => x.Type == SimulationEventType.BreakerStateChanged && x.StateAfter == CircuitBreakerState.Open),
        events.Count(x => x.Type == SimulationEventType.HalfOpenProbeStarted),
        events.Count(x => x.Type == SimulationEventType.HalfOpenProbeSucceeded),
        events.Count(x => x.Type == SimulationEventType.HalfOpenProbeFailed));

    public static ComparisonMetrics Compare(SimulationScenario scenario, SimulationRunResult baseline, SimulationRunResult protectedRun)
    {
        var requestOffsets = scenario.Requests.ToDictionary(x => x.Id, x => x.ScheduledOffset, StringComparer.Ordinal);
        bool InOutage(SimulationEvent x) => x.RequestId is not null && requestOffsets.TryGetValue(x.RequestId, out var offset) &&
            scenario.Availability.Any(w => w.Status == DownstreamAvailability.Failing && offset >= w.Start && offset < w.End);
        var baselineOutage = baseline.Events.Count(x => x.Type == SimulationEventType.RequestAttempted && InOutage(x));
        var protectedOutage = protectedRun.Events.Count(x => x.Type == SimulationEventType.RequestAttempted && InOutage(x));
        var avoided = baselineOutage - protectedOutage;
        var recovery = scenario.Availability.Select((window, index) => (window, index))
            .Where(x => x.index > 0 && x.window.Status == DownstreamAvailability.Healthy && scenario.Availability[x.index - 1].Status == DownstreamAvailability.Failing)
            .Select(x => (TimeSpan?)x.window.Start).LastOrDefault();
        var firstSuccess = recovery is null ? null : protectedRun.Events
            .Where(x => x.Type == SimulationEventType.RequestSucceeded && x.Elapsed >= recovery.Value)
            .Select(x => (TimeSpan?)x.Elapsed).FirstOrDefault();
        return new(baseline.Metrics.DownstreamAttempts - protectedRun.Metrics.DownstreamAttempts, avoided,
            baselineOutage == 0 ? null : avoided * 100d / baselineOutage,
            recovery, firstSuccess, recovery is not null && firstSuccess is not null ? firstSuccess - recovery : null);
    }
}
