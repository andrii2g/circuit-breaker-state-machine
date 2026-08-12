namespace CircuitBreaker.Core;

/// <summary>States in the circuit-breaker admission state machine.</summary>
public enum CircuitBreakerState { Closed, Open, HalfOpen }

/// <summary>The outcome of a circuit-breaker execution request.</summary>
public enum CircuitBreakerExecutionStatus { Succeeded, Failed, Rejected }

/// <summary>Configuration for a circuit breaker.</summary>
public sealed record CircuitBreakerOptions
{
    /// <summary>Creates validated circuit-breaker options.</summary>
    public CircuitBreakerOptions(int failureThreshold, TimeSpan openDuration)
    {
        if (failureThreshold < 1) throw new ArgumentOutOfRangeException(nameof(failureThreshold));
        if (openDuration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(openDuration));
        FailureThreshold = failureThreshold;
        OpenDuration = openDuration;
    }

    /// <summary>Number of consecutive failures required to open the breaker.</summary>
    public int FailureThreshold { get; }
    /// <summary>Minimum time for which calls are rejected after opening.</summary>
    public TimeSpan OpenDuration { get; }
}

/// <summary>An immutable diagnostic view of a circuit breaker.</summary>
public sealed record CircuitBreakerSnapshot(
    CircuitBreakerState State,
    int ConsecutiveFailures,
    DateTimeOffset? OpenedAt,
    DateTimeOffset? OpenUntil,
    bool IsHalfOpenProbeInFlight);

/// <summary>The typed result of requesting execution through a circuit breaker.</summary>
public sealed record CircuitBreakerExecutionResult<T>(
    CircuitBreakerExecutionStatus Status,
    T? Value,
    Exception? Exception,
    CircuitBreakerState StateAfterCompletion,
    bool WasDownstreamAttempted,
    bool WasHalfOpenProbe)
{
    /// <summary>Whether downstream execution succeeded.</summary>
    public bool IsSuccess => Status == CircuitBreakerExecutionStatus.Succeeded;
}

/// <summary>Describes an observed state transition.</summary>
public sealed record CircuitBreakerTransition(
    DateTimeOffset Timestamp,
    CircuitBreakerState PreviousState,
    CircuitBreakerState NewState);

/// <summary>A deterministic, thread-safe circuit breaker with lazy half-open admission.</summary>
public sealed class CircuitBreaker
{
    private readonly object _gate = new();
    private readonly CircuitBreakerOptions _options;
    private readonly TimeProvider _timeProvider;
    private CircuitBreakerState _state = CircuitBreakerState.Closed;
    private int _consecutiveFailures;
    private DateTimeOffset? _openedAt;
    private DateTimeOffset? _openUntil;
    private bool _probeInFlight;

    /// <summary>Creates a circuit breaker.</summary>
    public CircuitBreaker(CircuitBreakerOptions options, TimeProvider? timeProvider = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Raised synchronously after an atomic state transition.</summary>
    public event Action<CircuitBreakerTransition>? StateChanged;

    /// <summary>Returns a consistent snapshot of the current state.</summary>
    public CircuitBreakerSnapshot GetSnapshot()
    {
        lock (_gate)
            return new(_state, _consecutiveFailures, _openedAt, _openUntil, _probeInFlight);
    }

    /// <summary>
    /// Executes an operation when admitted. Operation exceptions are captured as failed attempts;
    /// caller cancellation is propagated and leaves Closed failure accounting unchanged. A cancelled
    /// half-open probe reopens the breaker to avoid leaving a probe reservation behind.
    /// </summary>
    public async ValueTask<CircuitBreakerExecutionResult<T>> ExecuteAsync<T>(
        Func<CancellationToken, ValueTask<T>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();

        var admission = Admit();
        Publish(admission.Transition);
        if (!admission.Admitted)
            return new(CircuitBreakerExecutionStatus.Rejected, default, null, admission.State, false, false);

        try
        {
            var value = await operation(cancellationToken).ConfigureAwait(false);
            var completion = Complete(success: true, admission.IsProbe);
            Publish(completion.Transition);
            return new(CircuitBreakerExecutionStatus.Succeeded, value, null, completion.State, true, admission.IsProbe);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (admission.IsProbe)
            {
                var completion = Complete(success: false, isProbe: true);
                Publish(completion.Transition);
            }
            throw;
        }
        catch (Exception exception)
        {
            var completion = Complete(success: false, admission.IsProbe);
            Publish(completion.Transition);
            return new(CircuitBreakerExecutionStatus.Failed, default, exception, completion.State, true, admission.IsProbe);
        }
    }

    private Admission Admit()
    {
        lock (_gate)
        {
            var now = _timeProvider.GetUtcNow();
            if (_state == CircuitBreakerState.Closed)
                return new(true, false, _state, null);

            if (_state == CircuitBreakerState.Open && now >= _openUntil)
            {
                _state = CircuitBreakerState.HalfOpen;
                _probeInFlight = true;
                return new(true, true, _state,
                    new(now, CircuitBreakerState.Open, CircuitBreakerState.HalfOpen));
            }

            return new(false, false, _state, null);
        }
    }

    private Completion Complete(bool success, bool isProbe)
    {
        lock (_gate)
        {
            var now = _timeProvider.GetUtcNow();
            CircuitBreakerTransition? transition = null;
            if (isProbe)
            {
                _probeInFlight = false;
                if (success)
                {
                    transition = new(now, CircuitBreakerState.HalfOpen, CircuitBreakerState.Closed);
                    _state = CircuitBreakerState.Closed;
                    _consecutiveFailures = 0;
                    _openedAt = null;
                    _openUntil = null;
                }
                else
                {
                    transition = new(now, CircuitBreakerState.HalfOpen, CircuitBreakerState.Open);
                    Open(now);
                }
            }
            else if (_state == CircuitBreakerState.Closed)
            {
                if (success) _consecutiveFailures = 0;
                else if (++_consecutiveFailures >= _options.FailureThreshold)
                {
                    transition = new(now, CircuitBreakerState.Closed, CircuitBreakerState.Open);
                    Open(now);
                }
            }

            return new(_state, transition);
        }
    }

    private void Open(DateTimeOffset now)
    {
        _state = CircuitBreakerState.Open;
        _openedAt = now;
        _openUntil = now + _options.OpenDuration;
        _probeInFlight = false;
    }

    private void Publish(CircuitBreakerTransition? transition)
    {
        if (transition is not null) StateChanged?.Invoke(transition);
    }

    private sealed record Admission(bool Admitted, bool IsProbe, CircuitBreakerState State, CircuitBreakerTransition? Transition);
    private sealed record Completion(CircuitBreakerState State, CircuitBreakerTransition? Transition);
}
