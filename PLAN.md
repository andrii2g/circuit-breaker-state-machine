# Implementation Plan

This plan is intended to be executable by Codex in small, reviewable phases. Do not skip the verification gate at the end of a phase.

## Phase 0 — Bootstrap the repository

### Goals

Create a .NET 10 solution and projects with strict dependency direction.

### Tasks

1. Create `circuit-breaker-state-machine.slnx`.
2. Create projects:
   - `src/CircuitBreaker.Core/CircuitBreaker.Core.csproj`;
   - `src/CircuitBreaker.Simulation/CircuitBreaker.Simulation.csproj`;
   - `src/CircuitBreaker.Cli/CircuitBreaker.Cli.csproj`;
   - `tests/CircuitBreaker.Core.Tests/CircuitBreaker.Core.Tests.csproj`;
   - `tests/CircuitBreaker.Simulation.Tests/CircuitBreaker.Simulation.Tests.csproj`.
3. Add references:
   - Simulation → Core;
   - CLI → Core and Simulation;
   - Core.Tests → Core;
   - Simulation.Tests → Simulation and Core.
4. Add central package management using `Directory.Packages.props`.
5. Add repository-wide compiler settings using `Directory.Build.props`.
6. Add `.editorconfig`, `.gitignore`, and `global.json` if a specific installed .NET 10 SDK should be pinned.
7. Add an empty `artifacts/.gitkeep` if desired, while ignoring generated files beneath it.

### Verification

```bash
dotnet restore circuit-breaker-state-machine.slnx
dotnet build circuit-breaker-state-machine.slnx -c Release --no-restore
```

## Phase 1 — Define the Core domain

### Goals

Create the smallest explicit domain vocabulary before implementing behavior.

### Required types

`CircuitBreakerState`
- `Closed`
- `Open`
- `HalfOpen`

`CircuitBreakerOptions`
- `FailureThreshold : int`
- `OpenDuration : TimeSpan`
- validation rejecting threshold less than 1 and open duration less than or equal to zero.

`CircuitBreakerExecutionStatus`
- `Succeeded`
- `Failed`
- `Rejected`

`CircuitBreakerExecutionResult<T>`
- status;
- optional value for success;
- optional captured exception/error for failure;
- state observed after completion;
- whether downstream was attempted;
- whether call was the half-open probe.

`CircuitBreakerSnapshot`
- current state;
- consecutive failures;
- `OpenedAt` / `OpenUntil` when meaningful;
- whether a half-open probe is currently reserved/in-flight.

### API decision

Use a single breaker instance with an `ExecuteAsync<T>` method that receives a delegate. The public API must preserve the distinction between downstream failure and breaker rejection.

Do not make reporting events part of the core result API unless required. State-change observation may be provided as an internal callback/event sink if needed by Simulation, but avoid making the core depend on simulation event types.

### Verification

Add option-validation tests and initial-state tests.

## Phase 2 — Implement the state machine

### Goals

Implement exact admission and transition semantics.

### Algorithm

For each call:

1. Honor pre-cancelled `CancellationToken` according to a documented rule before reserving a probe.
2. Enter a short synchronization section.
3. Read virtual/current time from injected `TimeProvider`.
4. If `Closed`, admit the call.
5. If `Open` and `now < openUntil`, reject.
6. If `Open` and `now >= openUntil`:
   - if no half-open probe is reserved, transition to `HalfOpen`, reserve probe, admit exactly that call;
   - otherwise reject.
7. If `HalfOpen`:
   - reserved probe caller continues;
   - all other calls reject.
8. Leave synchronization before invoking downstream delegate.
9. Execute delegate.
10. Re-enter synchronization to commit outcome:
    - Closed success → reset failure counter;
    - Closed failure → increment, and open when threshold reached;
    - HalfOpen probe success → Closed and reset;
    - HalfOpen probe failure → Open and restart timing.
11. Return a typed execution result.

### Critical concurrency rule

Never execute user/downstream code while holding the breaker synchronization lock.

### Failure classification

For MVP, any exception thrown by the supplied downstream delegate is a failed downstream attempt, except cancellation propagated from the caller's cancellation token if the chosen API distinguishes caller cancellation. Document this behavior and test it.

### Verification

Implement all transition tests in `CircuitBreaker.Core.Tests` before proceeding.

## Phase 3 — Build deterministic simulation primitives

### Goals

Describe time, requests, and dependency health without randomness.

### Required types

`DownstreamAvailability`
- `Healthy`
- `Failing`

`AvailabilityWindow`
- start offset;
- end offset;
- availability.

`RequestDefinition`
- request ID;
- scheduled offset.

`SimulationScenario`
- name;
- description;
- total duration;
- breaker options;
- ordered request schedule;
- ordered availability windows.

`ScenarioValidator`
- validates non-overlap and coverage rules;
- validates IDs and time ranges;
- validates breaker options.

`FakeDownstreamService`
- evaluates availability from logical time;
- increments invocation count;
- returns deterministic success/failure;
- supports a configurable deterministic operation latency only if needed for concurrency demonstrations.

### Time model

Prefer a simulation start instant plus elapsed offsets. Advance `FakeTimeProvider` directly to the next scheduled event rather than stepping millisecond-by-millisecond.

### Verification

Tests must prove a scenario executes identically across repeated runs.

## Phase 4 — Define immutable simulation events

### Goals

Create an event stream that becomes the source of truth for metrics and visualization.

### Event shape

Each event should include:
- sequence number;
- logical timestamp/elapsed offset;
- run kind (`Baseline` or `BreakerProtected`);
- event type;
- optional request ID;
- breaker state before/after when relevant;
- downstream availability when relevant;
- whether downstream was attempted;
- concise machine-readable detail.

### Event types

At minimum:
- `RequestArrived`;
- `RequestAttempted`;
- `RequestSucceeded`;
- `RequestFailed`;
- `RequestRejected`;
- `BreakerStateChanged`;
- `HalfOpenProbeStarted`;
- `HalfOpenProbeSucceeded`;
- `HalfOpenProbeFailed`.

### Ordering

Events with the same timestamp must retain deterministic ordering through an incrementing sequence number.

### Verification

Assert exact event order for a small scenario that opens, rejects, probes, and closes.

## Phase 5 — Baseline and protected runners

### Goals

Compare identical demand against the same fake downstream.

### Baseline runner

For every scheduled request:
- record arrival;
- attempt downstream unconditionally;
- record success/failure.

### Breaker runner

For every scheduled request:
- record arrival;
- invoke through breaker;
- record attempted/rejected and result;
- record state transitions and probe events.

### Fair-comparison invariant

Both runs must receive the same ordered request IDs and timestamps and the same availability schedule. Add an assertion in tests and optionally a debug-time/internal guard.

### Verification

Create an outage scenario where protected attempts are strictly fewer than baseline attempts.

## Phase 6 — Metrics engine

### Goals

Derive metrics from completed run results/events, not mutable counters scattered across runners.

### Per-run metrics

Compute:
- requests received;
- attempts;
- successes;
- failures;
- rejections;
- breaker openings;
- half-open probes;
- successful probes;
- failed probes.

### Comparison metrics

Compute:
- downstream attempts avoided overall;
- downstream attempts avoided during failing windows;
- outage load avoided percentage;
- downstream recovery instant;
- first successful protected request after recovery;
- recovery latency.

### Edge cases

- no failing windows → outage-load percentage unavailable;
- dependency never recovers → recovery latency unavailable;
- dependency recovers after scenario ends → unavailable;
- zero baseline outage attempts → unavailable.

### Verification

Use hand-calculable scenarios and assert exact numeric outputs.

## Phase 7 — Built-in scenarios and JSON format

### Goals

Make examples reproducible from both code and files.

### Built-ins

Implement:
- `short-outage`;
- `long-outage`;
- `failure-before-threshold`;
- `flapping`;
- `failed-half-open-probe`;
- `successful-recovery`.

### JSON loader

Use `System.Text.Json` only.

Support either:
- explicit request times; or
- a compact request generator with start, interval, and count/duration.

Prefer a simple schema documented in `docs/scenario-format.md`.

### Verification

Round-trip/load example files and assert they validate.

## Phase 8 — CLI

### Goals

Expose scenarios without adding unnecessary infrastructure.

### Commands/options

Support:
- `--list-scenarios`;
- `--scenario <name>`;
- `--file <path>`;
- `--threshold <int>`;
- `--open-duration <TimeSpan>`;
- `--output <directory>`;
- `--help`.

Rules:
- `--scenario` and `--file` are mutually exclusive;
- default to `long-outage` when neither is supplied;
- command-line breaker overrides apply after scenario loading and before validation;
- invalid values produce clear stderr diagnostics and non-zero exit code.

### Console output

Use concise narrative sections and bullet-like lines. Do not print large text tables.

Include:
- selected scenario;
- configuration;
- baseline summary;
- protected summary;
- avoided load;
- recovery latency;
- generated artifact paths.

## Phase 9 — Mermaid exporters

### State-machine exporter

Write the canonical state machine to `state-machine.mmd`.

### Sequence exporter

Generate a scenario-specific Mermaid sequence diagram from significant events.

To prevent pathological output for large scenarios:
- include all state changes;
- include all probes;
- include representative initial failures;
- aggregate long runs of rejected requests into Mermaid notes when necessary;
- include final recovery.

The exporter must escape labels that could invalidate Mermaid.

## Phase 10 — Standalone HTML timeline

### Goals

Provide the most useful educational visualization.

### Layout

Use semantic HTML plus inline CSS and inline SVG.

Required visual lanes:
- downstream availability;
- breaker state;
- request outcomes.

Required secondary sections:
- scenario configuration;
- metric cards;
- state-transition list;
- event details;
- metric definitions.

### Timeline rendering

Normalize scenario elapsed time to a shared horizontal scale.

Render:
- availability windows as contiguous SVG rectangles;
- breaker states as segments determined by state-change events;
- requests as positioned glyphs/markers with accessible labels;
- half-open probes with visually distinct marker shape or annotation;
- recovery time and first successful post-recovery request as vertical markers.

Do not depend on Mermaid in the HTML report. The HTML must remain fully offline.

### Accessibility

- meaningful document title;
- high-contrast text/background choices;
- SVG elements with labels or companion textual descriptions;
- do not rely solely on color to distinguish result categories;
- include textual metric definitions.

## Phase 11 — Serialization artifacts

### Goals

Make results inspectable and reusable.

`baseline.json` and `breaker.json` should contain:
- scenario identity;
- run kind;
- request/event stream;
- metrics;
- schema/version field.

`summary.json` should contain:
- scenario configuration;
- baseline metrics;
- protected metrics;
- comparison metrics;
- artifact schema version.

Use stable camelCase JSON property naming.

## Phase 12 — Test hardening

### Core tests

Cover:
- all state transitions;
- counter reset;
- no invocation on rejection;
- only one half-open probe;
- timing boundary exactly at `openUntil`;
- failed probe restarts duration;
- cancellation semantics;
- synchronization under racing callers.

### Simulation tests

Cover:
- schedule equivalence;
- deterministic repeated runs;
- availability lookup boundaries;
- exact metrics;
- recovery-latency edge cases;
- JSON validation;
- output file generation;
- Mermaid escaping;
- HTML contains all required lanes and embedded data.

### Concurrency test design

Use barriers/task coordination rather than sleeps. The test must force multiple callers to race at an eligible half-open boundary and assert exactly one downstream invocation.

## Phase 13 — Documentation pass

Ensure README and docs explain:
- breaker versus retry;
- lazy half-open transition;
- exactly-one-probe rule;
- failure versus rejection;
- avoided load;
- recovery-latency trade-off;
- why deterministic time matters.

All diagrams in Markdown must be Mermaid.

## Phase 14 — Final verification

Run:

```bash
dotnet restore circuit-breaker-state-machine.slnx
dotnet build circuit-breaker-state-machine.slnx -c Release --no-restore
dotnet test circuit-breaker-state-machine.slnx -c Release --no-build
dotnet run --project src/CircuitBreaker.Cli -c Release -- --scenario long-outage
dotnet run --project src/CircuitBreaker.Cli -c Release -- --file examples/flapping.json
```

Inspect generated output and verify:
- no real-time waits occurred;
- baseline/protected schedules match;
- rejected requests never invoked downstream;
- `.mmd` files contain valid Mermaid;
- HTML renders offline;
- metrics are internally consistent;
- generated artifacts are deterministic apart from explicitly excluded environment metadata.
