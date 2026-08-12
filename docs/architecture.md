# Architecture

## Component view

```mermaid
flowchart TB
    subgraph Presentation
        CLI[CircuitBreaker.Cli]
        Console[Console summary]
        Mermaid[Mermaid exporters]
        Html[HTML timeline exporter]
        Json[JSON result exporter]
    end

    subgraph Simulation
        Loader[Scenario loader]
        Validator[Scenario validator]
        Scheduler[Deterministic scheduler]
        Baseline[Baseline runner]
        Protected[Breaker runner]
        Fake[Fake downstream service]
        Events[Immutable event stream]
        Metrics[Metrics calculator]
    end

    subgraph Core
        Breaker[CircuitBreaker]
        State[CircuitBreakerState]
        Options[CircuitBreakerOptions]
        Result[Execution result]
        Time[TimeProvider]
    end

    CLI --> Loader
    CLI --> Validator
    CLI --> Scheduler
    Scheduler --> Baseline
    Scheduler --> Protected
    Baseline --> Fake
    Protected --> Breaker
    Breaker --> Fake
    Breaker --> Time
    Baseline --> Events
    Protected --> Events
    Events --> Metrics
    Events --> Mermaid
    Events --> Html
    Events --> Json
    Metrics --> Console
    Metrics --> Html
    Metrics --> Json
```

## Dependency direction

```mermaid
flowchart LR
    Core[CircuitBreaker.Core]
    Simulation[CircuitBreaker.Simulation]
    CLI[CircuitBreaker.Cli]
    CoreTests[CircuitBreaker.Core.Tests]
    SimulationTests[CircuitBreaker.Simulation.Tests]

    Simulation --> Core
    CLI --> Simulation
    CLI --> Core
    CoreTests --> Core
    SimulationTests --> Simulation
    SimulationTests --> Core
```

No arrow may point from Core to Simulation or CLI.

## Protected execution sequence

```mermaid
sequenceDiagram
    autonumber
    participant Caller
    participant CB as CircuitBreaker
    participant Clock as TimeProvider
    participant Downstream

    Caller->>CB: ExecuteAsync(operation)
    CB->>Clock: GetUtcNow()

    alt Closed
        CB-->>CB: Admit request
        CB->>Downstream: operation()
        alt success
            Downstream-->>CB: value
            CB-->>CB: reset consecutive failures
            CB-->>Caller: Succeeded
        else failure
            Downstream-->>CB: exception/failure
            CB-->>CB: increment failures
            opt threshold reached
                CB-->>CB: Closed → Open
            end
            CB-->>Caller: Failed
        end
    else Open before openUntil
        CB-->>Caller: Rejected
    else Open at/after openUntil
        CB-->>CB: atomically reserve one probe
        CB-->>CB: Open → HalfOpen
        CB->>Downstream: probe operation()
        alt probe succeeds
            Downstream-->>CB: value
            CB-->>CB: HalfOpen → Closed
            CB-->>Caller: Succeeded
        else probe fails
            Downstream-->>CB: failure
            CB-->>CB: HalfOpen → Open
            CB-->>CB: restart open interval
            CB-->>Caller: Failed
        end
    else HalfOpen probe already reserved
        CB-->>Caller: Rejected
    end
```

## Deterministic simulation flow

```mermaid
flowchart TD
    Start[Load or create scenario]
    Validate[Validate configuration]
    Requests[Build immutable request schedule]
    Baseline[Run baseline]
    Protected[Reset virtual clock and run protected]
    Check[Verify schedules are identical]
    Metrics[Derive metrics from events]
    Export[Export JSON, Mermaid, HTML]
    Done[Return CLI summary]

    Start --> Validate
    Validate --> Requests
    Requests --> Baseline
    Baseline --> Protected
    Protected --> Check
    Check --> Metrics
    Metrics --> Export
    Export --> Done
```

## Synchronization model

The breaker uses synchronization only to make admission decisions and commit outcomes. User code runs outside the critical section.

```mermaid
sequenceDiagram
    participant A as Caller A
    participant B as Caller B
    participant CB as Breaker lock/state
    participant D as Downstream

    A->>CB: arrive at openUntil
    B->>CB: arrive at openUntil
    CB-->>A: reserve HalfOpen probe
    CB-->>B: reject
    A->>D: execute outside lock
    D-->>A: probe result
    A->>CB: commit result
```

## Design rationale

### Why no background timer?

A breaker does not need a timer to mutate itself at the instant the open duration expires. The expiry only changes whether a future call may attempt a probe. A lazy transition therefore avoids timer lifecycle complexity while preserving semantics.

### Why immutable events?

The educational outputs must agree. If console metrics, HTML, and JSON each maintain independent counters they can drift. A single ordered event stream lets all reporting derive from the same facts.

### Why a baseline run?

A circuit breaker's benefit is not merely fewer reported failures. The important engineering effect is fewer expensive calls sent to an already failing dependency. A no-breaker baseline quantifies that avoided load.
