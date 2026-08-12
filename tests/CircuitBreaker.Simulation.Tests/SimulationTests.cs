using System.Text.Json;
using CircuitBreaker.Simulation;

namespace CircuitBreaker.Simulation.Tests;

public sealed class SimulationTests
{
    [Fact]
    public async Task Deterministic_simulation_repeated_twice_is_equivalent()
    {
        var scenario = BuiltInScenarios.Get("long-outage");
        var first = await SimulationRunner.RunAsync(scenario);
        var second = await SimulationRunner.RunAsync(scenario);
        Assert.Equal(JsonSerializer.Serialize(first, ScenarioJson.Options), JsonSerializer.Serialize(second, ScenarioJson.Options));
        Assert.Equal(first.Baseline.Requests, first.BreakerProtected.Requests);
    }

    [Fact]
    public async Task Long_outage_has_exact_metrics_and_avoids_load()
    {
        var result = await SimulationRunner.RunAsync(BuiltInScenarios.Get("long-outage"));
        Assert.Equal(new RunMetrics(90, 90, 0, 50, 40, 0, 0, 0, 0), result.Baseline.Metrics);
        Assert.Equal(new RunMetrics(90, 54, 36, 48, 6, 4, 4, 1, 3), result.BreakerProtected.Metrics);
        Assert.Equal(36, result.Comparison.DownstreamAttemptsAvoided);
        Assert.Equal(34, result.Comparison.OutageAttemptsAvoided);
        Assert.Equal(85d, result.Comparison.OutageLoadAvoidedPercentage);
        Assert.Equal(TimeSpan.FromSeconds(2), result.Comparison.RecoveryLatency);
    }

    [Fact]
    public async Task Protected_rejections_never_create_attempt_events()
    {
        var result = await SimulationRunner.RunAsync(BuiltInScenarios.Get("long-outage"));
        var attempted = result.BreakerProtected.Events.Where(x => x.Type == SimulationEventType.RequestAttempted).Select(x => x.RequestId).ToHashSet();
        var rejected = result.BreakerProtected.Events.Where(x => x.Type == SimulationEventType.RequestRejected).ToArray();
        Assert.NotEmpty(rejected);
        Assert.All(rejected, x => Assert.DoesNotContain(x.RequestId, attempted));
    }

    [Fact]
    public void Example_json_loads_and_validates()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var scenario = ScenarioJson.Load(Path.Combine(root, "examples", "long-outage.json"));
        Assert.Equal("long-outage", scenario.Name);
        Assert.Equal(90, scenario.Requests.Count);
    }

    [Fact]
    public async Task Reports_are_created_and_contain_required_lanes()
    {
        var output = Path.Combine(Path.GetTempPath(), $"circuit-breaker-tests-{Guid.NewGuid():N}");
        try
        {
            var result = await SimulationRunner.RunAsync(BuiltInScenarios.Get("short-outage"));
            var paths = await ArtifactWriter.WriteAsync(result, output);
            Assert.Equal(6, paths.Count);
            Assert.All(paths, path => Assert.True(File.Exists(path), path));
            var html = await File.ReadAllTextAsync(paths.Single(x => x.EndsWith("timeline.html", StringComparison.Ordinal)));
            Assert.Contains("Downstream availability", html);
            Assert.Contains("Protected request outcomes", html);
            Assert.Contains("State transitions", html);
            var mermaid = await File.ReadAllTextAsync(paths.Single(x => x.EndsWith("state-machine.mmd", StringComparison.Ordinal)));
            Assert.StartsWith("stateDiagram-v2", mermaid);
        }
        finally
        {
            if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
        }
    }
}
