using Inngest.IntegrationTests.Functions;
using Inngest.IntegrationTests.Harness;

namespace Inngest.IntegrationTests;

// What the dev server stored when it synced the app, read back through its API.
public sealed class SyncTests(DevServerFixture fixture) : IntegrationTest<DevServerFixture>(fixture)
{
    [Fact(Timeout = 30_000)]
    public async Task Registers_every_function_under_the_app_id()
    {
        var functions = await Api.GetAppFunctionsAsync(TestFunctions.AppId, Cancellation);

        functions.Select(function => function.Slug).ShouldBe(TestFunctions.Slugs, ignoreOrder: true);
    }

    [Fact(Timeout = 30_000)]
    public async Task Reports_the_sdk_the_framework_and_the_url_it_was_synced_through()
    {
        var app = (await Api.GetAppsAsync(Cancellation)).Single(app => app.ExternalId == TestFunctions.AppId);

        app.Error.ShouldBeNull();
        app.Url.ShouldBe(Fixture.App.ServerFacingUrl.AbsoluteUri);
        app.SdkLanguage.ShouldBe("dotnet");
        app.SdkVersion.ShouldNotBeNull().ShouldStartWith("v");
        app.Framework.ShouldBe("aspnetcore");
        app.FunctionCount.ShouldBe(TestFunctions.Slugs.Count);
    }

    [Fact(Timeout = 30_000)]
    public async Task Points_every_step_at_the_url_the_server_synced_through()
    {
        foreach (var function in (await FunctionsAsync()).Values)
        {
            function.StepUrl.ShouldBe($"{Fixture.App.ServerFacingUrl}?fnId={function.Slug}&stepId=step");
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task Registers_names_and_event_triggers()
    {
        var functions = await FunctionsAsync();

        functions["itests-hello"].Name.ShouldBe("Say hello");
        functions["itests-hello"].Config.ShouldContainJson("""{ "triggers": [{ "event": "itests/hello" }] }""");

        // Without a name, the function id is used.
        functions["itests-step-sequence"].Name.ShouldBe("step-sequence");
    }

    [Fact(Timeout = 30_000)]
    public async Task Registers_retries_as_the_number_of_retries_after_the_first_attempt()
    {
        var functions = await FunctionsAsync();

        functions["itests-retry-function"].Retries.ShouldBe(2);
        functions["itests-failing-child"].Retries.ShouldBe(0);

        // Left out, so the server's default applies.
        functions["itests-hello"].Retries.ShouldBeNull();
    }

    [Fact(Timeout = 30_000)]
    public async Task Registers_cancellation_with_the_matched_field_as_an_expression()
    {
        var functions = await FunctionsAsync();

        functions["itests-cancellable"].Config.ShouldContainJson("""
            { "cancel": [{ "event": "itests/cancel.stop", "if": "event.data.testId == async.data.testId" }] }
            """);
    }

    [Fact(Timeout = 30_000)]
    public async Task Registers_flow_control_with_durations_as_time_strings()
    {
        var configured = (await FunctionsAsync())["itests-configured"];

        configured.Retries.ShouldBe(5);
        configured.Config.ShouldContainJson("""
            {
              "name": "Flow control",
              "triggers": [
                { "event": "itests/configured", "expression": "event.data.priority > 2" },
                { "cron": "TZ=Europe/Prague 0 0 1 1 *" }
              ],
              "concurrency": [{ "limit": 3, "key": "event.data.testId" }],
              "throttle": { "limit": 10, "period": "1m", "burst": 2, "key": "event.data.testId" },
              "rateLimit": { "limit": 100, "period": "1h", "key": "event.data.testId" },
              "debounce": { "period": "5s", "key": "event.data.testId", "timeout": "1m" },
              "priority": { "run": "event.data.priority * 10" },
              "timeouts": { "start": "10m", "finish": "1h" },
              "singleton": { "mode": "skip", "key": "event.data.testId" }
            }
            """);
    }

    [Fact(Timeout = 30_000)]
    public async Task Registers_batching_and_idempotency_which_the_server_stores_as_a_day_long_rate_limit()
    {
        var batched = (await FunctionsAsync())["itests-batched"];

        batched.Config.ShouldContainJson("""
            {
              "batchEvents": { "maxSize": 10, "timeout": "5s", "key": "event.data.testId" },
              "rateLimit": { "limit": 1, "period": "24h0m0s", "key": "event.data.testId" }
            }
            """);
    }

    [Fact(Timeout = 30_000)]
    public async Task Registers_a_failure_handler_as_a_function_triggered_by_function_failed()
    {
        var failure = (await FunctionsAsync())["itests-always-fails-failure"];

        failure.Name.ShouldBe("always-fails (failure)");
        failure.Retries.ShouldBe(1);
        failure.Config.ShouldContainJson("""
            { "triggers": [{ "event": "inngest/function.failed", "expression": "event.data.function_id == 'itests-always-fails'" }] }
            """);
    }

    private async Task<Dictionary<string, RegisteredFunction>> FunctionsAsync() =>
        (await Api.GetAppFunctionsAsync(TestFunctions.AppId, Cancellation)).ToDictionary(function => function.Slug);
}
