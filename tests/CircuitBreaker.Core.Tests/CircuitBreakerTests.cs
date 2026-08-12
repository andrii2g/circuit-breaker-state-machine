using CircuitBreaker.Core;
using Microsoft.Extensions.Time.Testing;
using Breaker = CircuitBreaker.Core.CircuitBreaker;

namespace CircuitBreaker.Core.Tests;

public sealed class CircuitBreakerTests
{
    private static readonly DateTimeOffset Start = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Options_reject_invalid_values()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CircuitBreakerOptions(0, TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CircuitBreakerOptions(1, TimeSpan.Zero));
    }

    [Fact]
    public void Starts_closed()
    {
        var breaker = Create(out _);
        Assert.Equal(CircuitBreakerState.Closed, breaker.GetSnapshot().State);
    }

    [Fact]
    public async Task Closed_success_remains_closed_and_resets_count()
    {
        var breaker = Create(out _);
        await Fail(breaker);
        var result = await breaker.ExecuteAsync(_ => ValueTask.FromResult(42));
        Assert.Equal(CircuitBreakerExecutionStatus.Succeeded, result.Status);
        Assert.Equal(42, result.Value);
        Assert.Equal(0, breaker.GetSnapshot().ConsecutiveFailures);
        Assert.Equal(CircuitBreakerState.Closed, breaker.GetSnapshot().State);
    }

    [Fact]
    public async Task Threshold_failure_opens_and_open_call_is_rejected_without_invocation()
    {
        var breaker = Create(out _);
        await Fail(breaker);
        await Fail(breaker);
        var invocations = 0;
        var rejected = await breaker.ExecuteAsync<int>(_ => { invocations++; return ValueTask.FromResult(1); });
        Assert.Equal(CircuitBreakerExecutionStatus.Rejected, rejected.Status);
        Assert.False(rejected.WasDownstreamAttempted);
        Assert.Equal(0, invocations);
    }

    [Fact]
    public async Task Expiry_allows_successful_probe_which_closes()
    {
        var breaker = Create(out var clock);
        await Fail(breaker); await Fail(breaker);
        clock.Advance(TimeSpan.FromSeconds(10));
        var result = await breaker.ExecuteAsync(_ => ValueTask.FromResult("ok"));
        Assert.True(result.WasHalfOpenProbe);
        Assert.Equal(CircuitBreakerState.Closed, result.StateAfterCompletion);
        Assert.Equal(0, breaker.GetSnapshot().ConsecutiveFailures);
    }

    [Fact]
    public async Task Failed_probe_reopens_and_restarts_duration()
    {
        var breaker = Create(out var clock);
        await Fail(breaker); await Fail(breaker);
        clock.Advance(TimeSpan.FromSeconds(10));
        var probe = await Fail(breaker);
        Assert.True(probe.WasHalfOpenProbe);
        var reopenedUntil = breaker.GetSnapshot().OpenUntil;
        clock.Advance(TimeSpan.FromSeconds(9));
        var rejected = await breaker.ExecuteAsync(_ => ValueTask.FromResult(1));
        Assert.Equal(CircuitBreakerExecutionStatus.Rejected, rejected.Status);
        Assert.Equal(Start + TimeSpan.FromSeconds(20), reopenedUntil);
    }

    [Fact]
    public async Task Competing_half_open_calls_admit_exactly_one_probe()
    {
        var breaker = Create(out var clock);
        await Fail(breaker); await Fail(breaker);
        clock.Advance(TimeSpan.FromSeconds(10));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var invocations = 0;
        var probe = breaker.ExecuteAsync<int>(async _ =>
        {
            Interlocked.Increment(ref invocations);
            entered.SetResult();
            await release.Task;
            return 1;
        }).AsTask();
        await entered.Task;
        var competitors = Enumerable.Range(0, 20)
            .Select(_ => breaker.ExecuteAsync<int>(_ => { Interlocked.Increment(ref invocations); return ValueTask.FromResult(1); }).AsTask())
            .ToArray();
        var rejected = await Task.WhenAll(competitors);
        release.SetResult();
        var admitted = await probe;
        Assert.True(admitted.WasHalfOpenProbe);
        Assert.All(rejected, x => Assert.Equal(CircuitBreakerExecutionStatus.Rejected, x.Status));
        Assert.Equal(1, invocations);
    }

    [Fact]
    public async Task Pre_cancelled_call_does_not_corrupt_state()
    {
        var breaker = Create(out _);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => breaker.ExecuteAsync(_ => ValueTask.FromResult(1), cancellation.Token).AsTask());
        Assert.Equal(new CircuitBreakerSnapshot(CircuitBreakerState.Closed, 0, null, null, false), breaker.GetSnapshot());
    }

    [Fact]
    public async Task User_exception_is_a_downstream_failure()
    {
        var breaker = Create(out _);
        var result = await breaker.ExecuteAsync<int>(_ => throw new InvalidOperationException("boom"));
        Assert.Equal(CircuitBreakerExecutionStatus.Failed, result.Status);
        Assert.IsType<InvalidOperationException>(result.Exception);
        Assert.True(result.WasDownstreamAttempted);
    }

    private static Breaker Create(out FakeTimeProvider clock)
    {
        clock = new FakeTimeProvider(Start);
        return new Breaker(new CircuitBreakerOptions(2, TimeSpan.FromSeconds(10)), clock);
    }

    private static ValueTask<CircuitBreakerExecutionResult<int>> Fail(Breaker breaker) =>
        breaker.ExecuteAsync<int>(_ => throw new InvalidOperationException("failure"));
}
