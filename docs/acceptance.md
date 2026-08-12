# Acceptance Criteria

## Functional acceptance

The implementation is accepted when all of the following are true.

### Circuit breaker

- Starts in `Closed`.
- Trips to `Open` after the configured number of consecutive failures.
- Rejects calls in `Open` before the open interval expires.
- Rejected calls provably do not invoke the downstream delegate.
- Transitions lazily from `Open` to `HalfOpen` when an eligible call arrives.
- Admits exactly one half-open probe even under a race.
- Rejects competing calls while the half-open probe is in flight.
- Successful probe transitions to `Closed`.
- Failed probe transitions back to `Open` and restarts the open duration.
- Successful `Closed` requests reset the consecutive-failure count.

### Deterministic simulation

- Uses virtual time.
- Contains no real sleeping to model scenario passage.
- Baseline and breaker runs consume identical request and availability schedules.
- Repeated runs with identical inputs produce equivalent events/metrics.

### Scenarios

Built-ins exist for:
- short outage;
- long outage;
- failure before threshold;
- flapping;
- failed half-open probe;
- successful recovery.

At least three JSON examples are included.

### Metrics

The program reports:
- received;
- attempted;
- successful;
- failed;
- rejected;
- breaker openings;
- probes;
- successful/failed probes;
- downstream calls avoided during outage;
- outage load avoided percentage;
- recovery latency or unavailable state.

### Output

Each executed scenario generates:
- baseline JSON;
- breaker JSON;
- comparison JSON;
- Mermaid sequence diagram;
- Mermaid state diagram;
- standalone HTML timeline.

### Documentation

Markdown documentation uses Mermaid diagrams rather than ASCII diagrams. Documentation avoids text tables.

## Educational acceptance

A reader should be able to infer the following from the default generated artifacts without reading Core source code:

1. `Open` rejects work rather than scheduling retries.
2. A rejection is different from a failed attempted call.
3. `HalfOpen` is a controlled probe state.
4. The breaker can reduce load sent into an outage.
5. A longer open duration can increase recovery latency.

## Build acceptance

These commands must succeed from repository root:

```bash
dotnet restore circuit-breaker-state-machine.slnx
dotnet build circuit-breaker-state-machine.slnx -c Release --no-restore
dotnet test circuit-breaker-state-machine.slnx -c Release --no-build
dotnet run --project src/CircuitBreaker.Cli -c Release -- --scenario long-outage
```
