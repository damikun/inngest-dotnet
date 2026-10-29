using System.Text.Json;
using Inngest.IntegrationTests.Evaluation.Functions;
using Inngest.IntegrationTests.Evaluation.Harness;
using Inngest.IntegrationTests.Harness;
using Microsoft.Extensions.DependencyInjection;

namespace Inngest.IntegrationTests.Evaluation;

[Trait("Category", "Evaluation")]
[Trait("Area", "Triggers and events")]
public sealed class TriggerScenarios(EvaluationEnvironment environment) : EvaluationScenario<EvaluationDevFixture>(environment)
{
    private IInngestClient Client => Fixture.App.Services.GetRequiredService<IInngestClient>();

    [Fact(Timeout = 60_000)]
    public async Task Several_events_in_one_send_start_one_run_each()
    {
        var ids = await SendManyAsync(Enumerable.Range(1, 5).Select(value => new EventPayload(TriggerFunctions.Echo, new ValueEvent(TestId, value))));

        var runs = await Task.WhenAll(ids.Select(id => WaitForEndAsync(id)));

        ids.Distinct().Count().ShouldBe(5);
        runs.Select(run => run.RunId).Distinct().Count().ShouldBe(5);
        runs.ShouldAllBe(run => run.Status == RunStatus.Completed);
        runs.Select(run => run.OutputAs<EchoResult>().Value).ShouldBe([1, 2, 3, 4, 5]);
        Observed.Values<int>("ran").ShouldBe([1, 2, 3, 4, 5], ignoreOrder: true);
    }

    // Inngest drops a second event with an id it has seen within 24 hours: it is stored, but starts no run. The dev
    // server may list the one run under either event, so the runs of both are counted together.
    [Fact(Timeout = 60_000)]
    public async Task An_event_id_seen_before_does_not_start_another_run()
    {
        var id = $"order-{TestId}";

        var first = await Client.SendAsync(new InngestEvent(TriggerFunctions.Echo, new ValueEvent(TestId, 1)) { Id = id }, Cancellation);
        var firstRun = await WaitForEndAsync(first.Ids.ShouldHaveSingleItem());
        var duplicate = await Client.SendAsync(new InngestEvent(TriggerFunctions.Echo, new ValueEvent(TestId, 2)) { Id = id }, Cancellation);
        var other = await Client.SendAsync(new InngestEvent(TriggerFunctions.Echo, new ValueEvent(TestId, 3)) { Id = $"other-{TestId}" }, Cancellation);
        await WaitForEndAsync(other.Ids.ShouldHaveSingleItem());
        await Task.Delay(NoRunWindow, Cancellation);

        firstRun.OutputAs<EchoResult>().ShouldBe(new EchoResult(TriggerFunctions.Echo, 1, id));
        (await RunsOfAsync(first.Ids[0], duplicate.Ids.ShouldHaveSingleItem())).ShouldHaveSingleItem().RunId.ShouldBe(firstRun.RunId);
        Observed.Values<int>("ran").ShouldBe([1, 3]);
    }

    [Fact(Timeout = 60_000)]
    public async Task A_trigger_expression_starts_runs_only_for_events_that_match_it()
    {
        int[] values = [50, 150, 99, 100];
        var ids = await SendManyAsync(values.Select(value => new EventPayload(TriggerFunctions.Filtered, new ValueEvent(TestId, value))));

        var matched = await Task.WhenAll(WaitForEndAsync(ids[1]), WaitForEndAsync(ids[3]));

        matched.Select(run => run.OutputAs<int>()).ShouldBe([150, 100]);
        await ShouldStartNoRunAsync(ids[0], ids[2]);
        Observed.Values<int>("ran").ShouldBe([150, 100], ignoreOrder: true);
    }

    [Fact(Timeout = 60_000)]
    public async Task A_function_with_several_triggers_runs_for_each_of_them()
    {
        var ids = await SendManyAsync(
        [
            new EventPayload(TriggerFunctions.MultiA, new ValueEvent(TestId, 1)),
            new EventPayload(TriggerFunctions.MultiB, new ValueEvent(TestId, 1)),
            new EventPayload(TriggerFunctions.MultiC, new ValueEvent(TestId, 5)),
            new EventPayload(TriggerFunctions.MultiC, new ValueEvent(TestId, 50)),
        ]);

        var runs = await Task.WhenAll(WaitForEndAsync(ids[0]), WaitForEndAsync(ids[1]), WaitForEndAsync(ids[3]));

        runs.Select(run => run.OutputAs<string>()).ShouldBe([TriggerFunctions.MultiA, TriggerFunctions.MultiB, TriggerFunctions.MultiC]);
        await ShouldStartNoRunAsync(ids[2]);
        Observed.Named("ran").Count.ShouldBe(3);
    }

    [Fact(Timeout = 60_000)]
    public async Task A_wildcard_trigger_matches_every_event_under_its_prefix()
    {
        var ids = await SendManyAsync(
        [
            new EventPayload(TriggerFunctions.Wildcard + "created", new ValueEvent(TestId, 1)),
            new EventPayload(TriggerFunctions.Wildcard + "deleted", new ValueEvent(TestId, 2)),
            new EventPayload("eval/trg.wildcard", new ValueEvent(TestId, 3)),
        ]);

        var runs = await Task.WhenAll(WaitForEndAsync(ids[0]), WaitForEndAsync(ids[1]));

        runs.Select(run => run.OutputAs<string>()).ShouldBe([TriggerFunctions.Wildcard + "created", TriggerFunctions.Wildcard + "deleted"]);
        await ShouldStartNoRunAsync(ids[2]);
    }

    [Fact(Timeout = 60_000)]
    public async Task A_function_without_triggers_runs_when_another_function_invokes_it()
    {
        var run = await RunToEndAsync(TriggerFunctions.Invoker, new ValueEvent(TestId, 21));

        run.Status.ShouldBe(RunStatus.Completed);
        run.OutputAs<int>().ShouldBe(42);
        Observed.Values<string>("invoked").ShouldBe(["inngest/function.invoked"]);
        var registered = (await Api.GetAppFunctionsAsync(Fixture.AppId, Cancellation)).Single(function => function.Slug == $"eval-{TriggerFunctions.InvokeOnlyId}");
        registered.Config.TryGetProperty("triggers", out var triggers).ShouldBeTrue();
        triggers.ValueKind.ShouldBeOneOf(JsonValueKind.Array, JsonValueKind.Null);
        if (triggers.ValueKind == JsonValueKind.Array)
        {
            triggers.GetArrayLength().ShouldBe(0);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task The_user_object_of_an_event_reaches_the_function()
    {
        var sent = await Client.SendAsync(
            new InngestEvent(TriggerFunctions.Meta, new ValueEvent(TestId, 7)) { User = new { external_id = $"user-{TestId}", plan = "pro" } },
            Cancellation);

        var run = await WaitForEndAsync(sent.Ids.ShouldHaveSingleItem());

        var user = run.OutputAs<EventSeen>().User.ShouldNotBeNull();
        user.GetProperty("external_id").GetString().ShouldBe($"user-{TestId}");
        user.GetProperty("plan").GetString().ShouldBe("pro");
    }

    [Fact(Timeout = 60_000)]
    public async Task The_function_sees_the_id_timestamp_and_version_the_event_was_sent_with()
    {
        var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeMilliseconds());
        var sent = await Client.SendAsync(
            new InngestEvent(TriggerFunctions.Meta, new ValueEvent(TestId, 3)) { Id = $"meta-{TestId}", Timestamp = timestamp, Version = "2026-10-01.1" },
            Cancellation);

        var run = await WaitForEndAsync(sent.Ids.ShouldHaveSingleItem());

        var seen = run.OutputAs<EventSeen>();
        seen.Name.ShouldBe(TriggerFunctions.Meta);
        seen.Id.ShouldBe($"meta-{TestId}");
        seen.Timestamp.ShouldBe(timestamp);
        seen.Version.ShouldBe("2026-10-01.1");
        seen.Value.ShouldBe(3);
    }

    // The server schedules the run at the event's ts. It compares ts with its own clock, which runs in Docker's VM and
    // can be off from the host's by a few hundred milliseconds, so a second of slack still proves the delay.
    [Fact(Timeout = 60_000)]
    public async Task An_event_with_a_future_timestamp_starts_its_run_at_that_time()
    {
        var sentAt = DateTimeOffset.UtcNow;
        var startAt = sentAt.AddSeconds(3);

        var sent = await Client.SendAsync(new InngestEvent(TriggerFunctions.Delayed, new RunEvent(TestId)) { Timestamp = startAt }, Cancellation);
        var run = await WaitForEndAsync(sent.Ids.ShouldHaveSingleItem(), TimeSpan.FromSeconds(20));

        run.Status.ShouldBe(RunStatus.Completed);
        Observed.Named("ran").ShouldHaveSingleItem().At.ShouldBeGreaterThan(startAt - TimeSpan.FromSeconds(1));
    }

    [Fact(Timeout = 60_000)]
    public async Task One_event_starts_every_function_it_triggers()
    {
        var eventId = await SendAsync(TriggerFunctions.FanOut, new RunEvent(TestId));

        var runs = await WaitForRunsToEndAsync(eventId, count: 2);

        runs.Select(run => run.OutputAs<string>()).ShouldBe(["a", "b"], ignoreOrder: true);
        Observed.Values<string>("ran").ShouldBe(["a", "b"], ignoreOrder: true);
    }
}
