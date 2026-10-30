using Inngest.Steps;

namespace Inngest.Tests.FanOut;

// The window of the fan-out without the engine: units are tasks the test completes, and it completes them inline, so
// every lane continues the moment its unit does.
public sealed class FanOutRunTests
{
    [Fact]
    public async Task The_first_units_start_up_to_the_width_and_each_finished_unit_makes_room_for_the_next()
    {
        var units = new Units(5);

        var run = FanOutRun<int>.RunAsync(5, 2, failFast: true, units.Start, TestContext.Current.CancellationToken);

        units.Started.ShouldBe([0, 1]);
        units.Complete(1, 10);
        units.Started.ShouldBe([0, 1, 2]);
        units.Complete(0, 0);
        units.Started.ShouldBe([0, 1, 2, 3]);
        units.Complete(3, 30);
        units.Complete(2, 20);
        units.Started.ShouldBe([0, 1, 2, 3, 4]);
        run.IsCompleted.ShouldBeFalse();
        units.Complete(4, 40);

        var result = await run;
        result.Values.ShouldBe([0, 10, 20, 30, 40]);
        result.Settled.ShouldAllBe(settled => settled);
        result.First.ShouldBeNull();
    }

    [Fact]
    public async Task Without_a_limit_every_unit_starts_at_once()
    {
        var units = new Units(4);

        var run = FanOutRun<int>.RunAsync(4, 4, failFast: true, units.Start, TestContext.Current.CancellationToken);

        units.Started.ShouldBe([0, 1, 2, 3]);
        foreach (var index in new[] { 3, 1, 0, 2 })
        {
            units.Complete(index, index * 2);
        }

        (await run).Values.ShouldBe([0, 2, 4, 6]);
    }

    [Fact]
    public async Task A_limit_above_the_count_starts_each_unit_once()
    {
        var units = new Units(2);

        var run = FanOutRun<int>.RunAsync(2, 2, failFast: false, units.Start, TestContext.Current.CancellationToken);
        units.Complete(0, 1);
        units.Complete(1, 2);

        (await run).Values.ShouldBe([1, 2]);
        units.Started.ShouldBe([0, 1]);
    }

    [Fact]
    public async Task A_run_without_units_completes_at_once()
    {
        var run = await FanOutRun<int>.RunAsync(0, 0, failFast: true, _ => throw new InvalidOperationException(), TestContext.Current.CancellationToken);

        run.Values.ShouldBeEmpty();
        run.First.ShouldBeNull();
    }

    [Fact]
    public async Task A_fail_fast_run_starts_nothing_after_a_failure_and_does_not_wait_for_the_units_in_flight()
    {
        var units = new Units(6);

        var run = FanOutRun<int>.RunAsync(6, 3, failFast: true, units.Start, TestContext.Current.CancellationToken);
        units.Complete(0, 100);
        units.Started.ShouldBe([0, 1, 2, 3]);
        var failure = Failure("item[1]", "boom");
        units.Fail(1, failure);

        // Unit 2 and unit 3 are still in flight; unit 4 and unit 5 will never start.
        var result = await run;
        result.First.ShouldBeSameAs(failure);
        result.Settled.ShouldBe([true, true, false, false, false, false]);
        result.Failures[1].ShouldBeSameAs(failure);
        units.Complete(2, 2);
        units.Started.ShouldBe([0, 1, 2, 3]);
    }

    [Fact]
    public async Task A_collecting_run_goes_on_after_a_failure_and_reports_the_first_one_it_saw()
    {
        var units = new Units(4);
        var first = Failure("item[3]", "first");
        var second = Failure("item[1]", "second");

        var run = FanOutRun<int>.RunAsync(4, 2, failFast: false, units.Start, TestContext.Current.CancellationToken);
        units.Fail(1, first);
        units.Complete(0, 7);
        units.Fail(3, second);
        units.Complete(2, 9);

        var result = await run;
        result.Values.ShouldBe([7, 0, 9, 0]);
        result.Settled.ShouldAllBe(settled => settled);
        result.Failures.ShouldBe([null, first, null, second]);
        result.First.ShouldBeSameAs(first);
        Should.Throw<StepFailedException>(result.ThrowIfFailed).ShouldBeSameAs(first);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_unit_that_throws_something_other_than_a_step_failure_ends_the_run_with_that_exception(bool failFast)
    {
        var units = new Units(4);

        var run = FanOutRun<int>.RunAsync(4, 2, failFast, units.Start, TestContext.Current.CancellationToken);
        units.Fail(0, new FormatException("unreadable"));

        (await Should.ThrowAsync<FormatException>(run)).Message.ShouldBe("unreadable");
        units.Started.ShouldBe([0, 1]);
    }

    [Fact]
    public async Task A_start_that_throws_ends_the_run_with_that_exception()
    {
        var run = FanOutRun<int>.RunAsync(3, 3, failFast: false, index => index == 1 ? throw new ArgumentException("no id") : Task.FromResult(index), TestContext.Current.CancellationToken);

        (await Should.ThrowAsync<ArgumentException>(run)).Message.ShouldBe("no id");
    }

    [Fact]
    public async Task A_token_canceled_while_units_are_left_stops_the_next_unit_and_ends_the_run_with_the_cancellation()
    {
        using var source = new CancellationTokenSource();
        var units = new Units(5);

        var run = FanOutRun<int>.RunAsync(5, 2, failFast: false, units.Start, source.Token);
        units.Complete(0, 0);
        units.Started.ShouldBe([0, 1, 2]);
        await source.CancelAsync();
        units.Complete(1, 1);

        await Should.ThrowAsync<OperationCanceledException>(run);
        units.Started.ShouldBe([0, 1, 2]);
    }

    [Fact]
    public async Task A_token_that_is_canceled_at_the_start_starts_nothing()
    {
        var units = new Units(3);

        var run = FanOutRun<int>.RunAsync(3, 3, failFast: true, units.Start, new CancellationToken(canceled: true));

        await Should.ThrowAsync<OperationCanceledException>(run);
        units.Started.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_token_canceled_after_the_last_unit_started_changes_nothing()
    {
        using var source = new CancellationTokenSource();
        var units = new Units(2);

        var run = FanOutRun<int>.RunAsync(2, 2, failFast: true, units.Start, source.Token);
        await source.CancelAsync();
        units.Complete(0, 5);
        units.Complete(1, 6);

        (await run).Values.ShouldBe([5, 6]);
    }

    private static StepFailedException Failure(string stepId, string message) =>
        new(stepId, new FunctionError { Name = "Error", Message = message });

    // Units the test completes, with the order they were started in. A unit completes inline.
    private sealed class Units(int count)
    {
        private readonly TaskCompletionSource<int>[] _tasks = [.. Enumerable.Range(0, count).Select(_ => new TaskCompletionSource<int>())];
        private readonly List<int> _started = [];

        public IReadOnlyList<int> Started => _started;

        public Task<int> Start(int index)
        {
            _started.Add(index);
            return _tasks[index].Task;
        }

        public void Complete(int index, int value) => _tasks[index].SetResult(value);

        public void Fail(int index, Exception exception) => _tasks[index].SetException(exception);
    }
}
