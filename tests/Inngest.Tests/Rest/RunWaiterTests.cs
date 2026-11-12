using System.Net;
using Inngest.Rest;
using Inngest.Tests.Client;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;

namespace Inngest.Tests.Rest;

// The waits of IInngestRunsClient on fake time: each read answers from a script, and the test moves the clock on until
// the wait is over.
public sealed class RunWaiterTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(Start);
    private readonly List<string> _reads = [];
    private readonly Dictionary<string, Queue<Func<FunctionRun>>> _runs = new(StringComparer.Ordinal);
    private readonly Queue<Func<EventRunsRead>> _eventRuns = new();

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Waits_for_a_run_until_it_has_an_end_time()
    {
        Script("r1", Run("r1", FunctionRunStatus.Running), Run("r1", FunctionRunStatus.Completed), Run("r1", FunctionRunStatus.Completed, ended: true));

        var run = await RunAsync(waiter => waiter.WaitForRunAsync("r1", TimeSpan.FromMinutes(1), Cancellation));

        run.Status.ShouldBe(FunctionRunStatus.Completed);
        run.EndedAt.ShouldNotBeNull();
        _reads.ShouldBe(["run r1", "run r1", "run r1"]);
    }

    [Fact]
    public async Task Reads_further_apart_each_time_up_to_the_largest_delay()
    {
        var times = new List<TimeSpan>();
        _runs["r1"] = new Queue<Func<FunctionRun>>(Enumerable.Range(0, 12).Select(i => (Func<FunctionRun>)(() =>
        {
            times.Add(_time.GetUtcNow() - Start);
            return Run("r1", FunctionRunStatus.Running, ended: i == 11);
        })));

        await RunAsync(waiter => waiter.WaitForRunAsync("r1", TimeSpan.FromMinutes(1), Cancellation));

        var gaps = times.Zip(times.Skip(1), (first, second) => second - first).ToList();
        gaps[0].ShouldBe(TimeSpan.FromMilliseconds(250));
        gaps[1].ShouldBe(TimeSpan.FromMilliseconds(375));
        gaps.ShouldBeInOrder(SortDirection.Ascending);
        gaps[^1].ShouldBe(TimeSpan.FromSeconds(2));
    }

    [Theory]
    [InlineData(404)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    public async Task Counts_an_error_that_can_pass_as_not_ended_yet(int status)
    {
        Script("r1", Throws(new InngestException("Failed to get run r1: no", status)), Run("r1", FunctionRunStatus.Failed, ended: true));

        var run = await RunAsync(waiter => waiter.WaitForRunAsync("r1", TimeSpan.FromMinutes(1), Cancellation));

        run.Status.ShouldBe(FunctionRunStatus.Failed);
    }

    [Fact]
    public async Task Counts_no_connection_as_not_ended_yet()
    {
        Script("r1", Throws(new InngestException("Failed to get run r1: refused", new HttpRequestException("refused"))), Run("r1", FunctionRunStatus.Cancelled, ended: true));

        var run = await RunAsync(waiter => waiter.WaitForRunAsync("r1", TimeSpan.FromMinutes(1), Cancellation));

        run.Status.ShouldBe(FunctionRunStatus.Cancelled);
    }

    [Fact]
    public async Task Throws_an_error_that_cannot_pass()
    {
        Script("r1", Throws(new InngestException("Failed to get run r1: Unauthorized", 401)));

        var exception = await Should.ThrowAsync<InngestException>(RunAsync(waiter => waiter.WaitForRunAsync("r1", TimeSpan.FromMinutes(1), Cancellation)));

        exception.StatusCode.ShouldBe(401);
    }

    [Fact]
    public async Task Times_out_with_what_it_last_saw_and_the_last_error()
    {
        Script("r1", Run("r1", FunctionRunStatus.Running));
        for (var i = 0; i < 100; i++)
        {
            _runs["r1"].Enqueue(Throws(new InngestException("Failed to get run r1: busy", 503)));
        }

        var exception = await Should.ThrowAsync<TimeoutException>(
            RunAsync(waiter => waiter.WaitForRunAsync("r1", TimeSpan.FromSeconds(30), Cancellation)));

        exception.Message.ShouldBe("Run r1 did not end within 30 s. Last seen: run r1 Running. Last error: Failed to get run r1: busy");
        exception.InnerException.ShouldBeOfType<InngestException>().StatusCode.ShouldBe(503);
        (_time.GetUtcNow() - Start).ShouldBeInRange(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30.125));
    }

    [Fact]
    public async Task Keeps_waiting_without_a_timeout_until_cancelled()
    {
        Script("r1", Enumerable.Repeat(Run("r1", FunctionRunStatus.Running), 500).ToArray());
        using var cancellation = new CancellationTokenSource();
        var wait = CreateWaiter().WaitForRunAsync("r1", Timeout.InfiniteTimeSpan, cancellation.Token);

        await Should.ThrowAsync<TimeoutException>(Drive(wait, TimeSpan.FromMinutes(5)));
        await cancellation.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(wait);
        _reads.Count.ShouldBeGreaterThan(100);
    }

    [Fact]
    public async Task Refuses_a_timeout_that_is_not_positive()
    {
        var waiter = CreateWaiter();

        await Should.ThrowAsync<ArgumentOutOfRangeException>(waiter.WaitForRunAsync("r1", TimeSpan.Zero, Cancellation));
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => waiter.WaitForEventRunsAsync("e1", 0, TimeSpan.FromSeconds(1), Cancellation));
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => waiter.WaitForEventRunsToSettleAsync("e1", TimeSpan.FromSeconds(-1), TimeSpan.FromSeconds(1), Cancellation));
    }

    [Fact]
    public async Task Waits_for_the_expected_runs_of_an_event_to_show_up_and_end()
    {
        // The list is cached: it says nothing at first, then names both runs; each run is read on its own until it ends.
        var both = Listed(Start.AddSeconds(15), Run("a", FunctionRunStatus.Running), Run("b", FunctionRunStatus.Completed, ended: true));
        ScriptEvent(Listed(Start), Listed(Start), both, both);
        Script("a", Run("a", FunctionRunStatus.Running), Run("a", FunctionRunStatus.Completed, ended: true));

        var runs = await RunAsync(waiter => waiter.WaitForEventRunsAsync("e1", 2, TimeSpan.FromMinutes(1), Cancellation));

        runs.Select(run => (run.RunId, run.Status, run.EndedAt is not null)).ShouldBe(
            [("a", FunctionRunStatus.Completed, true), ("b", FunctionRunStatus.Completed, true)]);
        _reads.ShouldBe(["event e1", "event e1", "event e1", "run a", "event e1", "run a"]);
    }

    [Fact]
    public async Task Does_not_take_a_completed_run_without_an_end_time_as_ended()
    {
        // The runs of an event can read Completed while a sleep of the run is still pending.
        ScriptEvent(Listed(Start, Run("a", FunctionRunStatus.Completed)), Listed(Start, Run("a", FunctionRunStatus.Completed)));
        Script("a", Run("a", FunctionRunStatus.Completed), Run("a", FunctionRunStatus.Completed, ended: true));

        var runs = await RunAsync(waiter => waiter.WaitForEventRunsAsync("e1", 1, TimeSpan.FromMinutes(1), Cancellation));

        runs.ShouldHaveSingleItem().EndedAt.ShouldNotBeNull();
        _reads.Count(read => read == "run a").ShouldBe(2);
    }

    [Fact]
    public async Task Returns_more_runs_than_expected_when_the_list_has_them()
    {
        ScriptEvent(Listed(Start, Run("a", FunctionRunStatus.Completed, ended: true), Run("b", FunctionRunStatus.Failed, ended: true)));

        var runs = await RunAsync(waiter => waiter.WaitForEventRunsAsync("e1", 1, TimeSpan.FromMinutes(1), Cancellation));

        runs.Select(run => run.RunId).ShouldBe(["a", "b"]);
    }

    [Fact]
    public async Task Times_out_when_too_few_runs_start()
    {
        for (var i = 0; i < 40; i++)
        {
            ScriptEvent(Listed(Start, Run("a", FunctionRunStatus.Completed, ended: true)));
        }

        var exception = await Should.ThrowAsync<TimeoutException>(
            RunAsync(waiter => waiter.WaitForEventRunsAsync("e1", 2, TimeSpan.FromSeconds(10), Cancellation)));

        exception.Message.ShouldBe("Event e1 did not start 2 run(s) that end within 10 s. Last seen: run a Completed (ended).");
    }

    [Fact]
    public async Task Settles_an_event_that_started_no_run_only_once_inngest_read_the_list_again()
    {
        // A cached answer repeats the time of its read, so the list only counts as unchanged once Inngest read it again.
        ScriptEvent(Listed(Start), Listed(Start), Listed(Start), Listed(Start.AddSeconds(15)));

        var runs = await RunAsync(waiter => waiter.WaitForEventRunsToSettleAsync("e1", TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(1), Cancellation));

        runs.ShouldBeEmpty();
        _reads.Count.ShouldBe(4);
    }

    [Fact]
    public async Task Settles_once_the_runs_have_ended_and_no_other_run_shows_up()
    {
        ScriptEvent(
            Listed(Start, Run("a", FunctionRunStatus.Running)),
            Listed(Start.AddSeconds(2), Run("a", FunctionRunStatus.Running), Run("b", FunctionRunStatus.Running)),
            Listed(Start.AddSeconds(4), Run("a", FunctionRunStatus.Completed, ended: true), Run("b", FunctionRunStatus.Running)),
            Listed(Start.AddSeconds(6), Run("a", FunctionRunStatus.Completed, ended: true), Run("b", FunctionRunStatus.Completed, ended: true)),
            Listed(Start.AddSeconds(8), Run("a", FunctionRunStatus.Completed, ended: true), Run("b", FunctionRunStatus.Completed, ended: true)));
        Script("a", Run("a", FunctionRunStatus.Running), Run("a", FunctionRunStatus.Running));
        Script("b", Run("b", FunctionRunStatus.Running), Run("b", FunctionRunStatus.Running), Run("b", FunctionRunStatus.Running));

        var runs = await RunAsync(waiter => waiter.WaitForEventRunsToSettleAsync("e1", TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(1), Cancellation));

        runs.Select(run => run.RunId).ShouldBe(["a", "b"]);
        runs.ShouldAllBe(run => run.EndedAt != null);
        _reads.Count(read => read == "event e1").ShouldBe(5);
    }

    [Fact]
    public async Task Measures_settling_on_its_own_clock_when_the_reads_say_nothing_of_their_time()
    {
        for (var i = 0; i < 40; i++)
        {
            _eventRuns.Enqueue(() => new EventRunsRead([], FetchedAt: null));
        }

        await RunAsync(waiter => waiter.WaitForEventRunsToSettleAsync("e1", TimeSpan.FromSeconds(3), TimeSpan.FromMinutes(1), Cancellation));

        (_time.GetUtcNow() - Start).ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(3));
        (_time.GetUtcNow() - Start).ShouldBeLessThan(TimeSpan.FromSeconds(6));
    }

    [Fact]
    public async Task A_client_that_only_reads_runs_gets_the_waits_too()
    {
        var client = new ReadOnlyRunsClient(Run("r1", FunctionRunStatus.Completed, ended: true));

        IInngestRunsClient runs = client;
        var run = await runs.WaitForRunAsync("r1", TimeSpan.FromSeconds(5), Cancellation);
        var eventRuns = await runs.WaitForEventRunsAsync("e1", 1, TimeSpan.FromSeconds(5), Cancellation);

        run.RunId.ShouldBe("r1");
        eventRuns.ShouldHaveSingleItem().RunId.ShouldBe("r1");
    }

    [Fact]
    public async Task The_runs_client_waits_with_the_time_inngest_reports_for_its_reads()
    {
        var handler = new FakeHttpHandler();
        const string Empty = """{"data":[],"metadata":{"fetched_at":"2026-11-12T09:00:00Z","cached_until":"2026-11-12T09:00:15Z"}}""";
        handler.Respond(HttpStatusCode.OK, Empty).Respond(HttpStatusCode.OK, Empty)
            .RespondAlways(HttpStatusCode.OK, """{"data":[],"metadata":{"fetched_at":"2026-11-12T09:00:15Z"}}""");
        var client = new InngestRunsClient(
            new FakeHttpClientFactory(handler), TestSettings.Create(), new FakeLogger<InngestRunsClient>(), _time);

        var runs = await Drive(client.WaitForEventRunsToSettleAsync("01M61VV209SMJYG2RA0687EJY1", TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(1), Cancellation), TimeSpan.FromMinutes(2));

        runs.ShouldBeEmpty();
        handler.Requests.Count.ShouldBe(3);
        handler.Requests.ShouldAllBe(request => request.Uri.AbsolutePath == "/v1/events/01M61VV209SMJYG2RA0687EJY1/runs");
    }

    private static FunctionRun Run(string id, FunctionRunStatus status, bool ended = false) => new()
    {
        RunId = id,
        Status = status,
        FunctionId = "f",
        EventId = "e1",
        StartedAt = Start,
        EndedAt = ended ? Start.AddSeconds(1) : null,
    };

    private static Func<FunctionRun> Throws(Exception exception) => () => throw exception;

    private static EventRunsRead Listed(DateTimeOffset fetchedAt, params FunctionRun[] runs) => new(runs, fetchedAt);

    private void Script(string runId, params FunctionRun[] answers) =>
        _runs[runId] = new Queue<Func<FunctionRun>>(answers.Select(answer => (Func<FunctionRun>)(() => answer)));

    private void Script(string runId, Func<FunctionRun> first, params FunctionRun[] answers) =>
        _runs[runId] = new Queue<Func<FunctionRun>>([first, .. answers.Select(answer => (Func<FunctionRun>)(() => answer))]);

    private void ScriptEvent(params EventRunsRead[] answers)
    {
        foreach (var answer in answers)
        {
            _eventRuns.Enqueue(() => answer);
        }
    }

    private RunWaiter CreateWaiter() => new(
        (runId, _) =>
        {
            _reads.Add($"run {runId}");
            return Task.FromResult(_runs[runId].Dequeue()());
        },
        (eventId, _) =>
        {
            _reads.Add($"event {eventId}");
            return Task.FromResult(_eventRuns.Dequeue()());
        },
        _time,
        RunPolling.Default);

    private Task<T> RunAsync<T>(Func<RunWaiter, Task<T>> wait, TimeSpan? until = null) =>
        Drive(wait(CreateWaiter()), until ?? TimeSpan.FromMinutes(2));

    // Moves the fake clock on in small steps until the wait is over; a wait still going after until fails the test.
    private async Task<T> Drive<T>(Task<T> wait, TimeSpan until)
    {
        var end = _time.GetUtcNow() + until;
        while (!wait.IsCompleted && _time.GetUtcNow() < end)
        {
            await Task.WhenAny(wait, Task.Delay(1, Cancellation));
            if (!wait.IsCompleted)
            {
                _time.Advance(TimeSpan.FromMilliseconds(125));
            }
        }

        return wait.IsCompleted ? await wait : throw new TimeoutException("The wait was still going on.");
    }

    private sealed class ReadOnlyRunsClient(FunctionRun run) : IInngestRunsClient
    {
        public Task<FunctionRun> GetRunAsync(string runId, CancellationToken cancellationToken = default) => Task.FromResult(run);

        public Task<IReadOnlyList<FunctionRun>> GetEventRunsAsync(string eventId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<FunctionRun>>([run]);

        public Task CancelRunAsync(string runId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
