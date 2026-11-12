using Inngest.IntegrationTests.Evaluation.Functions;
using Inngest.IntegrationTests.Evaluation.Harness;
using Inngest.IntegrationTests.Harness;
using Microsoft.Extensions.DependencyInjection;

namespace Inngest.IntegrationTests.Rest;

// The app's IInngestManagementClient against the inngest/inngest:v1.52.0 dev server, which serves the v2 REST API with
// no cache and no key. The tests of a class run one after another, so pausing a function does not reach another test.
public sealed class ManagementClientTests(EvaluationEnvironment environment) : EvaluationScenario<RunsFixture>(environment)
{
    private const string AppId = "eval";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private IInngestManagementClient Management => Fixture.App.Services.GetRequiredService<IInngestManagementClient>();

    [Fact(Timeout = 60_000)]
    public async Task A_paused_function_holds_its_new_runs_until_it_is_resumed()
    {
        var paused = await Management.PauseFunctionAsync(AppId, RunsFunctions.PausableId, Cancellation);
        try
        {
            paused.Id.ShouldBe(RunsFunctions.PausableId);
            paused.AppId.ShouldBe(AppId);
            paused.IsPaused.ShouldBeTrue();
            paused.Triggers.ShouldBe([new FunctionTriggerInfo("EVENT", RunsFunctions.Pausable)]);

            var eventId = await SendAsync(RunsFunctions.Pausable, new RunEvent(TestId));
            await Should.ThrowAsync<TimeoutException>(() => Api.Runs.WaitForEventRunsAsync(eventId, 1, TimeSpan.FromSeconds(3), Cancellation));

            var resumed = await Management.ResumeFunctionAsync(AppId, RunsFunctions.PausableId, Cancellation);
            resumed.IsPaused.ShouldBeFalse();

            var runs = await Api.Runs.WaitForEventRunsAsync(eventId, 1, Timeout, Cancellation);
            runs.ShouldHaveSingleItem().GetOutput<Greeting>().ShouldBe(new Greeting("ran", TestId));
        }
        finally
        {
            await Management.ResumeFunctionAsync(AppId, RunsFunctions.PausableId, CancellationToken.None);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task Pauses_and_resumes_several_functions_and_reports_one_that_is_not_there()
    {
        try
        {
            var paused = await Management.PauseFunctionsAsync(AppId, [RunsFunctions.PausableId, "not-a-function"], Cancellation);

            paused.Select(result => result.FunctionId).ShouldBe([RunsFunctions.PausableId, "not-a-function"]);
            paused[0].Function.ShouldNotBeNull().IsPaused.ShouldBeTrue();
            paused[1].Function.ShouldBeNull();
            paused[1].Error.ShouldNotBeNull().Code.ShouldBe("not_found");
        }
        finally
        {
            var resumed = await Management.ResumeFunctionsAsync(AppId, [RunsFunctions.PausableId], CancellationToken.None);
            resumed.ShouldHaveSingleItem().Function.ShouldNotBeNull().IsPaused.ShouldBeFalse();
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task Cancels_the_queued_runs_of_a_function_that_match_the_expression()
    {
        // A paused function keeps its new runs queued, and a bulk cancellation reaches runs that have not started.
        await Management.PauseFunctionAsync(AppId, RunsFunctions.PausableId, Cancellation);
        string eventId;
        RunCancellation cancellation;
        try
        {
            var from = DateTimeOffset.UtcNow.AddMinutes(-1);
            eventId = await SendAsync(RunsFunctions.Pausable, new RunEvent(TestId));
            await Task.Delay(TimeSpan.FromSeconds(1), Cancellation);

            cancellation = await Management.CancelRunsAsync(
                AppId,
                RunsFunctions.PausableId,
                new CancelRunsRequest(from, DateTimeOffset.UtcNow) { If = $"event.data.testId == '{TestId}'", Name = $"itests {TestId}" },
                Cancellation);
        }
        finally
        {
            await Management.ResumeFunctionAsync(AppId, RunsFunctions.PausableId, CancellationToken.None);
        }

        cancellation.Id.ShouldNotBeNullOrEmpty();
        cancellation.AppId.ShouldBe(AppId);
        cancellation.FunctionId.ShouldBe(RunsFunctions.PausableId);
        cancellation.Name.ShouldBe($"itests {TestId}");
        cancellation.If.ShouldBe($"event.data.testId == '{TestId}'");

        var runs = await Api.Runs.WaitForEventRunsAsync(eventId, 1, Timeout, Cancellation);
        runs.ShouldHaveSingleItem().Status.ShouldBe(FunctionRunStatus.Cancelled);
    }

    [Fact(Timeout = 60_000)]
    public async Task Gets_and_lists_the_events_it_received()
    {
        var eventId = await SendAsync(RunsFunctions.Greet, new RunEvent(TestId));

        // Inngest stores an event a moment after it accepts it, and answers 404 until then.
        var received = (await Poll.UntilAsync(
            $"event {eventId} to be readable",
            async token =>
            {
                try
                {
                    return await Management.GetEventAsync(eventId, includePayload: true, token);
                }
                catch (InngestException exception) when (exception.StatusCode == 404)
                {
                    return null;
                }
            },
            found => found is not null,
            Timeout,
            Fixture.DescribeAsync,
            Cancellation))!;
        var listed = await Management.ListEventsAsync(new EventQuery { Names = [RunsFunctions.Greet] }, Cancellation)
            .FirstOrDefaultAsync(e => e.Id == eventId, Cancellation);

        received.Id.ShouldBe(eventId);
        received.Name.ShouldBe(RunsFunctions.Greet);
        received.Payload.GetProperty("data").GetProperty("testId").GetString().ShouldBe(TestId);
        listed.ShouldNotBeNull().Name.ShouldBe(RunsFunctions.Greet);
        listed.ReceivedAt.ShouldBe(received.ReceivedAt);
    }

    [Fact(Timeout = 60_000)]
    public async Task Fails_to_get_an_event_it_does_not_have()
    {
        var exception = await Should.ThrowAsync<InngestException>(() => Management.GetEventAsync("01K8ZZZZZZZZZZZZZZZZZZZZZZ", cancellationToken: Cancellation));

        exception.StatusCode.ShouldBe(404);
        exception.Message.ShouldEndWith("(not_found)");
    }

    [Fact(Timeout = 60_000)]
    public async Task Lists_the_runs_of_a_function_page_by_page_with_their_output()
    {
        var first = await SendAsync(RunsFunctions.Greet, new RunEvent(TestId));
        var second = await SendAsync(RunsFunctions.Greet, new RunEvent(TestId));
        var ended = (await Api.Runs.WaitForEventRunsAsync(first, 1, Timeout, Cancellation))
            .Concat(await Api.Runs.WaitForEventRunsAsync(second, 1, Timeout, Cancellation))
            .ToList();

        var runs = await Management.ListRunsAsync(
            new RunQuery
            {
                From = DateTimeOffset.UtcNow.AddMinutes(-5),
                AppIds = [AppId],
                FunctionIds = [RunsFunctions.GreetId],
                Statuses = [FunctionRunStatus.Completed],
                Query = $"event.data.testId == '{TestId}'",
                IncludeOutput = true,
                PageSize = 1,
            },
            Cancellation).ToListAsync(Cancellation);

        runs.Select(run => run.RunId).ShouldBe(ended.Select(run => run.RunId), ignoreOrder: true);
        runs.ShouldAllBe(run => run.Status == FunctionRunStatus.Completed && run.FunctionId == RunsFunctions.GreetId && run.AppId == AppId);
        runs.SelectMany(run => run.EventIds).ShouldBe([first, second], ignoreOrder: true);
        runs.ShouldAllBe(run => run.Output.GetProperty("testId").GetString() == TestId);
    }

    [Fact(Timeout = 60_000)]
    public async Task The_dev_server_has_no_list_of_syncs()
    {
        var exception = await Should.ThrowAsync<InngestException>(async () =>
            await Management.ListAppSyncsAsync(AppId, cancellationToken: Cancellation).ToListAsync(Cancellation));

        exception.StatusCode.ShouldBe(501);
        exception.Message.ShouldBe($"Failed to list the syncs of app {AppId}: List app syncs is not yet implemented (not_implemented)");
    }
}
