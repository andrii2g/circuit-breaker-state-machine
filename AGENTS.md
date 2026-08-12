# AGENTS.md

## Mission
Implement `circuit-breaker-state-machine` as a small, deterministic .NET 10 educational laboratory for circuit-breaker behavior.

The repository must demonstrate that a circuit breaker is an explicit state machine with admission control, not a retry policy with a delay.

## Non-negotiable constraints

- Target .NET 10.
- Use a `.slnx` solution.
- Keep the production circuit-breaker implementation dependency-light.
- Use `TimeProvider` in production code.
- Use `FakeTimeProvider` from `Microsoft.Extensions.TimeProvider.Testing` in deterministic tests and simulations where appropriate.
- Implement only `Closed`, `Open`, and `HalfOpen` states for the MVP.
- `HalfOpen` permits exactly one trial request at a time.
- Requests rejected by an open breaker must not invoke the downstream service.
- Do not implement retry, bulkhead, rate limiting, hedging, timeout policies, distributed state, rolling failure percentages, or HTTP integration in the MVP.
- Use consecutive failures as the trip criterion.
- Successful calls in `Closed` reset the consecutive-failure counter.
- A failed `HalfOpen` probe returns the breaker to `Open` and restarts the open interval.
- A successful `HalfOpen` probe closes the breaker and resets failure state.
- The transition from `Open` to `HalfOpen` is lazy: it occurs when an eligible call arrives after the open interval has elapsed. Do not create a background timer solely to change breaker state.
- All simulation output must be reproducible.
- The baseline and breaker-protected runs must use the same request schedule and same downstream availability schedule.
- HTML reporting must be standalone and require no CDN, JavaScript framework, or network access.
- Mermaid must be used for diagrams in Markdown. Do not add ASCII diagrams or text-art diagrams.
- Avoid Markdown text tables in documentation. Prefer lists, definition-style sections, and Mermaid diagrams.

## Design priorities

1. Correct state-machine semantics.
2. Determinism.
3. Clear separation between admission decision, downstream execution, event recording, metrics, and rendering.
4. Small public API.
5. Strong invariants and tests.
6. Educational observability.
7. Minimal infrastructure and dependencies.

## Repository structure

```text
src/
  CircuitBreaker.Core/
  CircuitBreaker.Simulation/
  CircuitBreaker.Cli/
tests/
  CircuitBreaker.Core.Tests/
  CircuitBreaker.Simulation.Tests/
examples/
docs/
artifacts/
```

`artifacts/` is generated output and should be ignored by Git except for an optional `.gitkeep`.

## Layering rules

- `CircuitBreaker.Core` must not reference Simulation or CLI.
- `CircuitBreaker.Simulation` may reference Core.
- `CircuitBreaker.Cli` may reference Core and Simulation.
- Tests may reference only the projects they test plus test-only packages.
- Core must contain no console, file-system, HTML, Mermaid, or JSON scenario-loading responsibilities.
- Reporting code must consume recorded events/results rather than inspect mutable breaker internals.

## Core semantics

### Closed
- Admit calls.
- On success: set consecutive failures to zero.
- On failure: increment consecutive failures.
- When failures reach threshold: transition to `Open` and record `openedAt` / `openUntil`.

### Open
- Before `openUntil`: reject immediately.
- At or after `openUntil`: one caller may atomically become the `HalfOpen` probe.
- Other callers while a probe is reserved/in-flight are rejected.
- Merely advancing time must not transition directly to `Closed`.

### HalfOpen
- Exactly one probe may execute.
- Probe success: transition to `Closed`, clear failure state.
- Probe failure: transition to `Open`, restart the open interval.

## Concurrency invariant

The implementation must remain correct if multiple callers arrive at the first instant at or after `openUntil`. Exactly one may execute the trial request. All other competing calls must be rejected until the probe resolves.

Prefer the simplest correct synchronization mechanism. A small lock around state transitions is acceptable and preferable to obscure lock-free code.

Do not hold a lock while executing downstream user code.

## Time rules

- Never call `DateTime.UtcNow` or `DateTimeOffset.UtcNow` in breaker logic.
- Read time through injected `TimeProvider`.
- Do not use `Task.Delay` for simulation progression.
- Scenario time is logical/virtual time.
- Persist event timestamps as elapsed scenario time plus optional absolute virtual timestamp.

## Result model

The public execution result must distinguish:
- success after an attempted downstream invocation;
- failure after an attempted downstream invocation;
- rejection where no downstream invocation occurred.

Do not collapse failures and rejections into the same result.

## Event-sourcing rule for simulation/reporting

The simulation event stream is the authoritative source for metrics and rendered artifacts. Metrics should be derivable from immutable events.

Required event categories:
- request arrived;
- request attempted;
- request succeeded;
- request failed;
- request rejected;
- breaker state changed;
- half-open probe started;
- half-open probe succeeded;
- half-open probe failed;
- downstream availability changed (optional but recommended for report clarity).

## Metrics contract

Calculate at least:
- received requests;
- attempted downstream requests;
- rejected requests;
- successful attempts;
- failed attempts;
- breaker openings;
- half-open probes;
- successful probes;
- failed probes;
- downstream attempts avoided compared with baseline;
- outage load avoided percentage;
- recovery latency.

Recovery latency definition:

`first successful breaker-protected request at or after actual downstream recovery - actual downstream recovery time`

If recovery never occurs, report recovery latency as unavailable rather than zero.

Outage load avoided definition:

Use only calls whose scheduled request time falls inside configured failing availability windows.

`baseline outage attempts - breaker outage attempts`

Percentage denominator is baseline outage attempts. If the denominator is zero, report unavailable.

## Scenario requirements

Built-ins must include:
- short outage;
- long outage;
- failure before threshold;
- flapping dependency;
- failed half-open probe;
- successful recovery.

Provide equivalent JSON examples for at least short outage, long outage, and intermittent/flapping behavior.

Validate scenario data before simulation. Reject overlapping availability windows, invalid thresholds, non-positive open duration, non-positive request interval, duplicate request IDs, or schedules outside the declared duration.

## Reporting requirements

Generate into `artifacts/<scenario-name>/`:
- `baseline.json`;
- `breaker.json`;
- `summary.json`;
- `sequence.mmd`;
- `state-machine.mmd`;
- `timeline.html`.

The HTML report must contain:
- scenario configuration;
- baseline metrics;
- breaker metrics;
- comparison metrics;
- downstream availability timeline;
- breaker state timeline;
- request outcome timeline;
- state-transition/event log;
- definitions for load avoided and recovery latency.

Do not use a text table in generated Markdown docs. HTML may use semantic HTML tables for machine-like detailed event listings if useful, but the primary visualization must be the timeline.

## Mermaid requirements

Use Mermaid for Markdown diagrams. Required documentation diagrams:
- component architecture;
- state machine;
- protected request execution sequence;
- simulation flow;
- reporting/data flow.

Generated `.mmd` output must be valid Mermaid source.

## CLI requirements

The CLI must support:
- run a built-in scenario by name;
- load a scenario JSON file;
- override failure threshold;
- override open duration;
- choose output directory;
- list built-in scenarios;
- print a concise narrative summary;
- return non-zero on invalid input or simulation/report generation failure.

Suggested commands:

```bash
dotnet run --project src/CircuitBreaker.Cli -- --list-scenarios
dotnet run --project src/CircuitBreaker.Cli -- --scenario long-outage
dotnet run --project src/CircuitBreaker.Cli -- --file examples/long-outage.json
dotnet run --project src/CircuitBreaker.Cli -- --scenario long-outage --threshold 3 --open-duration 00:00:10
```

Do not add a CLI framework unless parsing becomes materially clearer than a small manual parser.

## Testing rules

Tests must be deterministic and contain no real sleeps.

Core tests must cover every state transition and concurrency invariant.
Simulation tests must verify identical schedules between baseline and protected runs, exact metrics for curated scenarios, deterministic repeated output, report creation, and event ordering.

At minimum, include tests named around these behaviors:
- starts closed;
- closed success remains closed;
- closed failure increments consecutive count;
- closed success resets count;
- threshold failure opens;
- open call before expiry is rejected without invocation;
- expiry allows one half-open probe;
- successful probe closes;
- failed probe reopens and restarts duration;
- competing half-open calls admit exactly one probe;
- cancellation before admission does not corrupt state;
- user-operation exception is classified as downstream failure according to the chosen API contract;
- deterministic simulation repeated twice yields equivalent result objects/events;
- breaker avoids downstream outage load;
- recovery latency is calculated from actual recovery time.

## Quality gates

Before considering implementation complete, run:

```bash
dotnet restore circuit-breaker-state-machine.slnx
dotnet build circuit-breaker-state-machine.slnx -c Release --no-restore
dotnet test circuit-breaker-state-machine.slnx -c Release --no-build
dotnet run --project src/CircuitBreaker.Cli -c Release -- --scenario long-outage
```

Verify generated `.mmd` files can be rendered by Mermaid and `timeline.html` opens offline.

## Style

- Nullable reference types enabled.
- Implicit usings enabled.
- Warnings treated as errors for repository code where practical.
- Prefer `record`/`record struct` for immutable simulation values and events.
- Prefer explicit domain names over generic `Manager`, `Helper`, or `Util` classes.
- XML documentation is required for the Core public API, but not for obvious internal members.
- Avoid premature abstractions.
- Avoid reflection and dynamic dispatch.
- Avoid hidden global state.

## Definition of done

The MVP is complete when a user can run one deterministic command, inspect the console narrative, open a standalone HTML timeline, inspect Mermaid diagrams, and clearly see why rejected calls avoided downstream work while half-open probing introduced a measurable recovery-latency trade-off.
