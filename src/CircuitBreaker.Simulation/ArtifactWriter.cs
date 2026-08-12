using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using CircuitBreaker.Core;

namespace CircuitBreaker.Simulation;

public static class ArtifactWriter
{
    public static async Task<IReadOnlyList<string>> WriteAsync(SimulationComparison result, string outputRoot,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.Combine(outputRoot, SafeName(result.Scenario.Name));
        Directory.CreateDirectory(directory);
        var options = ScenarioJson.Options;
        var paths = new[]
        {
            Path.Combine(directory, "baseline.json"), Path.Combine(directory, "breaker.json"),
            Path.Combine(directory, "summary.json"), Path.Combine(directory, "sequence.mmd"),
            Path.Combine(directory, "state-machine.mmd"), Path.Combine(directory, "timeline.html")
        };
        await File.WriteAllTextAsync(paths[0], JsonSerializer.Serialize(result.Baseline, options), cancellationToken);
        await File.WriteAllTextAsync(paths[1], JsonSerializer.Serialize(result.BreakerProtected, options), cancellationToken);
        await File.WriteAllTextAsync(paths[2], JsonSerializer.Serialize(result, options), cancellationToken);
        await File.WriteAllTextAsync(paths[3], SequenceMermaid(result), cancellationToken);
        await File.WriteAllTextAsync(paths[4], StateMachineMermaid(), cancellationToken);
        await File.WriteAllTextAsync(paths[5], TimelineHtml(result), cancellationToken);
        return paths;
    }

    public static string StateMachineMermaid() => """
        stateDiagram-v2
            [*] --> Closed
            Closed --> Closed: success / reset failures
            Closed --> Open: consecutive failures reach threshold
            Open --> Open: request before openUntil / reject
            Open --> HalfOpen: eligible request reserves probe
            HalfOpen --> Closed: probe succeeds
            HalfOpen --> Open: probe fails / restart interval
        """;

    public static string SequenceMermaid(SimulationComparison result)
    {
        var builder = new StringBuilder("sequenceDiagram\n    autonumber\n    participant Client\n    participant Breaker\n    participant Downstream\n");
        var significant = result.BreakerProtected.Events.Where(x => x.Type is SimulationEventType.RequestFailed or
            SimulationEventType.RequestRejected or SimulationEventType.BreakerStateChanged or
            SimulationEventType.HalfOpenProbeStarted or SimulationEventType.RequestSucceeded).ToArray();
        var rejected = 0;
        foreach (var item in significant)
        {
            if (item.Type == SimulationEventType.RequestRejected) { rejected++; continue; }
            if (rejected > 0)
            {
                builder.AppendLine($"    Note over Client,Breaker: {rejected} request(s) rejected without downstream work");
                rejected = 0;
            }
            var id = Escape(item.RequestId ?? "breaker");
            switch (item.Type)
            {
                case SimulationEventType.RequestFailed:
                    builder.AppendLine($"    Downstream-->>Breaker: {id} failed at {item.Elapsed:c}"); break;
                case SimulationEventType.RequestSucceeded:
                    builder.AppendLine($"    Downstream-->>Breaker: {id} succeeded at {item.Elapsed:c}"); break;
                case SimulationEventType.BreakerStateChanged:
                    builder.AppendLine($"    Note over Breaker: {item.StateBefore} to {item.StateAfter} at {item.Elapsed:c}"); break;
                case SimulationEventType.HalfOpenProbeStarted:
                    builder.AppendLine($"    Breaker->>Downstream: {id} half-open probe"); break;
            }
        }
        if (rejected > 0) builder.AppendLine($"    Note over Client,Breaker: {rejected} request(s) rejected without downstream work");
        return builder.ToString();
    }

    public static string TimelineHtml(SimulationComparison result)
    {
        var scenario = result.Scenario;
        var width = 1000d;
        double X(TimeSpan time) => time.TotalMilliseconds / scenario.Duration.TotalMilliseconds * width;
        var availability = string.Join("", scenario.Availability.Select(w =>
            $"<rect class='{(w.Status == DownstreamAvailability.Healthy ? "healthy" : "failing")}' x='{X(w.Start):F2}' y='20' width='{X(w.End - w.Start):F2}' height='28'><title>{w.Status}: {w.Start:c}–{w.End:c}</title></rect>"));
        var requests = string.Join("", result.BreakerProtected.Events.Where(e => e.Type is SimulationEventType.RequestSucceeded or SimulationEventType.RequestFailed or SimulationEventType.RequestRejected)
            .Select(e => $"<g><circle class='{e.Type}' cx='{X(e.Elapsed):F2}' cy='145' r='5'><title>{WebUtility.HtmlEncode(e.RequestId)}: {e.Type} at {e.Elapsed:c}</title></circle></g>"));
        var transitions = result.BreakerProtected.Events.Where(e => e.Type == SimulationEventType.BreakerStateChanged).ToArray();
        var stateStarts = new List<(TimeSpan Start, CircuitBreakerState State)> { (TimeSpan.Zero, CircuitBreakerState.Closed) };
        stateStarts.AddRange(transitions.Select(e => (e.Elapsed, e.StateAfter ?? CircuitBreakerState.Closed)));
        var stateSegments = string.Join("", stateStarts.Select((item, index) =>
        {
            var end = index + 1 < stateStarts.Count ? stateStarts[index + 1].Start : scenario.Duration;
            return $"<rect class='{item.State}' x='{X(item.Start):F2}' y='70' width='{X(end - item.Start):F2}' height='28'><title>{item.State}: {item.Start:c} to {end:c}</title></rect>";
        }));

        var transitionList = string.Join("", transitions.Select(e => $"<li><time>{e.Elapsed:c}</time> — {e.StateBefore} → {e.StateAfter}</li>"));
        var eventRows = string.Join("", result.BreakerProtected.Events.Select(e =>
            $"<tr><td>{e.Sequence}</td><td>{e.Elapsed:c}</td><td>{e.Type}</td><td>{WebUtility.HtmlEncode(e.RequestId ?? "—")}</td><td>{WebUtility.HtmlEncode(e.Detail ?? "")}</td></tr>"));
        var recovery = result.Comparison.RecoveryLatency?.ToString("c", CultureInfo.InvariantCulture) ?? "unavailable";
        var avoided = result.Comparison.OutageLoadAvoidedPercentage?.ToString("F1", CultureInfo.InvariantCulture) + "%" ?? "unavailable";
        return $$"""
            <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <title>{{WebUtility.HtmlEncode(scenario.Name)}} circuit-breaker timeline</title><style>
            :root{color-scheme:light;font-family:system-ui,sans-serif;background:#f7f8fa;color:#15202b}body{max-width:1180px;margin:auto;padding:2rem}h1,h2{line-height:1.2}.cards{display:grid;grid-template-columns:repeat(auto-fit,minmax(180px,1fr));gap:1rem}.card{background:white;border:1px solid #ccd3da;border-radius:8px;padding:1rem}.card strong{display:block;font-size:1.5rem}svg{background:white;border:1px solid #ccd3da;width:100%;height:auto}.healthy,.Closed{fill:#3a9d5d}.failing,.Open{fill:#c94b40}.HalfOpen{fill:#d88b19}.RequestSucceeded{fill:#147d3f}.RequestFailed{fill:#b42318}.RequestRejected{fill:#6b4ba1;stroke:white;stroke-width:2}.lane{font-size:14px}.legend span{margin-right:1rem}table{width:100%;border-collapse:collapse;background:white}th,td{text-align:left;border-bottom:1px solid #ddd;padding:.45rem;vertical-align:top}code{background:#e9edf1;padding:.1rem .3rem}small{color:#4a5968}
            </style></head><body><main><h1>{{WebUtility.HtmlEncode(scenario.Name)}}</h1><p>{{WebUtility.HtmlEncode(scenario.Description)}}</p>
            <section aria-labelledby="configuration"><h2 id="configuration">Configuration</h2><p>Duration <code>{{scenario.Duration:c}}</code>; failure threshold <code>{{scenario.Breaker.FailureThreshold}}</code>; open duration <code>{{scenario.Breaker.OpenDuration:c}}</code>; requests <code>{{scenario.Requests.Count}}</code>.</p></section>
            <section aria-labelledby="metrics"><h2 id="metrics">Metrics</h2><div class="cards">
            <div class="card"><small>Baseline attempts</small><strong>{{result.Baseline.Metrics.DownstreamAttempts}}</strong></div>
            <div class="card"><small>Protected attempts</small><strong>{{result.BreakerProtected.Metrics.DownstreamAttempts}}</strong></div>
            <div class="card"><small>Rejected requests</small><strong>{{result.BreakerProtected.Metrics.RejectedRequests}}</strong></div>
            <div class="card"><small>Outage load avoided</small><strong>{{avoided}}</strong></div>
            <div class="card"><small>Recovery latency</small><strong>{{recovery}}</strong></div></div></section>
            <section aria-labelledby="timeline"><h2 id="timeline">Shared timeline</h2><p class="legend"><span>■ Healthy</span><span>■ Failing</span><span>● Success</span><span>● Failure</span><span>● Rejected</span></p>
            <svg viewBox="0 0 1000 180" role="img" aria-labelledby="timeline-title timeline-desc"><title id="timeline-title">Dependency, breaker state, and protected request timeline</title><desc id="timeline-desc">Availability windows, breaker states, and every protected request outcome aligned to scenario time.</desc>
            <text class="lane" x="8" y="16">Downstream availability</text>{{availability}}<text class="lane" x="8" y="66">Breaker state</text>{{stateSegments}}<text class="lane" x="8" y="120">Protected request outcomes</text>{{requests}}</svg></section>
            <section aria-labelledby="transitions"><h2 id="transitions">State transitions</h2><ol>{{transitionList}}</ol></section>
            <section aria-labelledby="events"><h2 id="events">Protected event log</h2><table><thead><tr><th>#</th><th>Time</th><th>Event</th><th>Request</th><th>Detail</th></tr></thead><tbody>{{eventRows}}</tbody></table></section>
            <section aria-labelledby="definitions"><h2 id="definitions">Metric definitions</h2><p><strong>Outage load avoided</strong> is baseline attempts minus protected attempts scheduled inside failing availability windows, divided by baseline outage attempts.</p><p><strong>Recovery latency</strong> is the interval from the final actual downstream recovery to the first successful protected request at or after it.</p></section>
            </main></body></html>
            """;
    }

    private static string SafeName(string name) => string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-'));
    private static string Escape(string text) => text.Replace(":", "-").Replace(";", "-").Replace("\n", " ");
}
