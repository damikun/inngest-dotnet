using Inngest.Tests.Checkpointing;
using Inngest.Tests.Execution;

namespace Inngest.Tests.FanOut;

// Checkpointing runs consecutive steps in one call, and goes back to one call per step at parallel steps. A list that
// runs one item at a time is consecutive steps; a window of several is a parallel group.
public sealed class FanOutCheckpointingTests : IDisposable
{
    private static readonly int[] Numbers = [1, 2, 3];

    private readonly EngineHost _host = new();

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task A_list_that_runs_one_item_at_a_time_runs_in_a_single_call_and_saves_each_item()
    {
        var function = Checkpointed.Define(async context =>
            (object?)await context.Step.MapAsync("square", Numbers, (n, _) => Task.FromResult(n * n), new FanOutOptions { MaxParallelism = 1 }));

        var result = await _host.CallCheckpointedAsync(function, Call.New());

        result.StatusCode.ShouldBe(200);
        result.Body.ShouldBe("[1,4,9]");
        _host.Checkpoints.Checkpoints.Count.ShouldBe(3);
    }

    [Fact]
    public async Task A_window_of_several_items_is_planned_for_Inngest_and_nothing_is_checkpointed()
    {
        var function = Checkpointed.Define(async context =>
            (object?)await context.Step.MapAsync("square", Numbers, (n, _) => Task.FromResult(n * n), new FanOutOptions { MaxParallelism = 2 }));

        var result = await _host.CallCheckpointedAsync(function, Call.New());

        result.StatusCode.ShouldBe(206);
        result.DisplayNames.ShouldBe(["square[0]", "square[1]"]);
        _host.Checkpoints.Checkpoints.ShouldBeEmpty();
    }
}
