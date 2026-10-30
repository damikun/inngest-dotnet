using System.Text.Json;
using Inngest.Client;
using Inngest.Functions;
using Inngest.Tests.Checkpointing;
using Inngest.Tests.Execution;
using Microsoft.Extensions.DependencyInjection;
using Sentry;

namespace Inngest.Tests.Sentry;

// What reaches Sentry when a function or a step fails, and when it does not.
[Collection(SentryHost.Collection)]
public sealed class ReportingTests
{
    [Fact]
    public async Task A_function_that_fails_for_good_is_reported_with_the_call_as_tags()
    {
        await using var host = new SentryHost();
        var function = TestFunction.Define(_ => throw new NonRetriableException("no"));

        (await host.CallAsync(function, Call.New().Attempt(2, maxAttempts: 4))).ShouldBe(400);

        var report = host.Events.ShouldHaveSingleItem();
        report.Exception.ShouldBeOfType<NonRetriableException>().Message.ShouldBe("no");
        report.TransactionName.ShouldBe("inngest:fn");
        var expected = new Dictionary<string, string>
        {
            ["inngest.client.id"] = "shop",
            ["inngest.function.id"] = "shop-fn",
            ["inngest.run.id"] = SentryHost.RunId,
            ["inngest.attempt"] = "2",
            ["inngest.event.name"] = "test/event",
            ["inngest.event.id"] = "01JTEST",
            ["inngest.error.source"] = "run",
        };
        TagsOf(report).ShouldBe(expected, ignoreOrder: true);
    }

    [Fact]
    public async Task An_error_that_Inngest_retries_is_not_reported_by_default()
    {
        await using var host = new SentryHost();
        var function = TestFunction.Define(_ => throw new InvalidOperationException("boom"));

        (await host.CallAsync(function, Call.New().Attempt(0, maxAttempts: 4))).ShouldBe(500);

        host.Events.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_last_attempt_is_reported_even_though_the_error_is_one_Inngest_would_retry()
    {
        await using var host = new SentryHost();
        var function = TestFunction.Define(_ => throw new InvalidOperationException("boom"));

        (await host.CallAsync(function, Call.New().Attempt(3, maxAttempts: 4))).ShouldBe(400);

        var report = host.Events.ShouldHaveSingleItem();
        TagsOf(report)["inngest.attempt"].ShouldBe("3");
    }

    [Fact]
    public async Task Every_attempt_is_reported_when_only_the_final_one_is_not_asked_for()
    {
        await using var host = new SentryHost(options => options.OnlyCaptureFinalAttempt = false);
        var function = TestFunction.Define(_ => throw new InvalidOperationException("boom"));

        (await host.CallAsync(function, Call.New().Attempt(0, maxAttempts: 4))).ShouldBe(500);
        (await host.CallAsync(function, Call.New().Attempt(1, maxAttempts: 4))).ShouldBe(500);

        host.Events.Select(report => TagsOf(report)["inngest.attempt"]).ShouldBe(["0", "1"]);
    }

    [Fact]
    public async Task A_step_error_is_not_reported_by_default()
    {
        await using var host = new SentryHost();
        var function = TestFunction.Define(async context =>
            await context.Step.RunAsync<int>("charge", _ => throw new TimeoutException("slow")));

        (await host.CallAsync(function, Call.New().Attempt(3, maxAttempts: 4))).ShouldBe(206);

        host.Events.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_step_error_is_reported_with_the_step_when_asked_for()
    {
        await using var host = new SentryHost(options => options.CaptureStepErrors = true);
        var function = TestFunction.Define(async context =>
            await context.Step.RunAsync<int>(new StepOptions("charge") { Name = "Charge the card" }, _ => throw new TimeoutException("slow")));

        (await host.CallAsync(function, Call.New().Attempt(3, maxAttempts: 4))).ShouldBe(206);

        var report = host.Events.ShouldHaveSingleItem();
        report.Exception.ShouldBeOfType<TimeoutException>();
        var tags = TagsOf(report);
        tags["inngest.error.source"].ShouldBe("step");
        tags["inngest.step.name"].ShouldBe("Charge the card");
        tags["inngest.run.id"].ShouldBe(SentryHost.RunId);
        var breadcrumb = report.Breadcrumbs.Single(breadcrumb => breadcrumb.Category == "inngest.step");
        breadcrumb.Message.ShouldBe("Charge the card");
        breadcrumb.Level.ShouldBe(BreadcrumbLevel.Error);
        breadcrumb.Data.ShouldNotBeNull()["type"].ShouldBe("Run");
    }

    [Fact]
    public async Task A_step_without_a_name_is_reported_by_its_id()
    {
        await using var host = new SentryHost(options => options.CaptureStepErrors = true);
        var function = TestFunction.Define(async context =>
            await context.Step.RunAsync<int>("charge", _ => throw new TimeoutException("slow")));

        await host.CallAsync(function, Call.New().Attempt(3, maxAttempts: 4));

        TagsOf(host.Events.ShouldHaveSingleItem())["inngest.step.name"].ShouldBe("charge");
    }

    [Fact]
    public async Task A_step_error_that_Inngest_retries_is_not_reported_even_when_asked_for()
    {
        await using var host = new SentryHost(options => options.CaptureStepErrors = true);
        var function = TestFunction.Define(async context =>
            await context.Step.RunAsync<int>("charge", _ => throw new TimeoutException("slow")));

        (await host.CallAsync(function, Call.New().Attempt(0, maxAttempts: 4))).ShouldBe(206);

        host.Events.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_step_error_that_Inngest_retries_is_reported_when_every_attempt_is()
    {
        await using var host = new SentryHost(options =>
        {
            options.CaptureStepErrors = true;
            options.OnlyCaptureFinalAttempt = false;
        });
        var function = TestFunction.Define(async context =>
            await context.Step.RunAsync<int>("charge", _ => throw new TimeoutException("slow")));

        (await host.CallAsync(function, Call.New().Attempt(0, maxAttempts: 4))).ShouldBe(206);

        TagsOf(host.Events.ShouldHaveSingleItem())["inngest.error.source"].ShouldBe("step");
    }

    [Fact]
    public async Task A_step_that_fails_for_good_is_reported_once_by_the_function_that_does_not_catch_it()
    {
        await using var host = new SentryHost();
        var function = TestFunction.Define(async context =>
            await context.Step.RunAsync<int>("charge", _ => throw new TimeoutException("slow")));

        await host.CallAsync(function, Call.New().Attempt(3, maxAttempts: 4));
        (await host.CallAsync(function, Call.New().Failed("charge", "TimeoutException", "slow"))).ShouldBe(400);

        var report = host.Events.ShouldHaveSingleItem();
        report.Exception.ShouldBeOfType<StepFailedException>();
        TagsOf(report)["inngest.error.source"].ShouldBe("run");
    }

    [Fact]
    public async Task A_step_that_fails_for_good_is_reported_twice_when_step_errors_are_asked_for()
    {
        await using var host = new SentryHost(options => options.CaptureStepErrors = true);
        var function = TestFunction.Define(async context =>
            await context.Step.RunAsync<int>("charge", _ => throw new TimeoutException("slow")));

        await host.CallAsync(function, Call.New().Attempt(3, maxAttempts: 4));
        await host.CallAsync(function, Call.New().Failed("charge", "TimeoutException", "slow"));

        host.Events.Select(report => TagsOf(report)["inngest.error.source"]).ShouldBe(["step", "run"]);
    }

    [Fact]
    public async Task The_steps_a_call_executed_are_breadcrumbs_of_the_error_that_follows()
    {
        var checkpoints = new FakeCheckpointApi();
        await using var host = new SentryHost(
            options => options.OnlyCaptureFinalAttempt = false,
            services: services => services.AddSingleton<ICheckpointApi>(checkpoints));
        var function = new FunctionDefinition(
            new FunctionOptions { Id = "fn", Triggers = [Trigger.Event("test/event")], Checkpointing = new CheckpointingOptions() },
            _ => new DelegateFunction<JsonElement>(async (context, _) =>
            {
                await context.Step.RunAsync("price", () => 40);
                await context.Step.RunAsync("charge", () => 2);
                throw new InvalidOperationException("declined");
            }));

        (await host.CallAsync(function, Call.New(), queueItemId: "cWktMQ==")).ShouldBe(500);

        var report = host.Events.ShouldHaveSingleItem();
        var steps = report.Breadcrumbs.Where(breadcrumb => breadcrumb.Category == "inngest.step").ToList();
        steps.Select(breadcrumb => breadcrumb.Message).ShouldBe(["price", "charge"]);
        steps.ShouldAllBe(breadcrumb => breadcrumb.Level == BreadcrumbLevel.Info);
    }

    [Fact]
    public async Task Nothing_is_reported_and_the_call_goes_on_while_Sentry_is_not_enabled()
    {
        await using var host = new SentryHost(initialize: false);
        var function = TestFunction.Define(async context =>
        {
            await context.Step.RunAsync("price", () => 40);
            throw new NonRetriableException("no");
        });

        SentrySdk.IsEnabled.ShouldBeFalse();
        (await host.CallAsync(function, Call.New().Data("price", "40"))).ShouldBe(400);

        host.Events.ShouldBeEmpty();
    }

    private static Dictionary<string, string> TagsOf(SentryEvent report) =>
        report.Tags.ToDictionary(tag => tag.Key, tag => tag.Value, StringComparer.Ordinal);
}
