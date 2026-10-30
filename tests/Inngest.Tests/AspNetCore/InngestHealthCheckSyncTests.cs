using System.Net;
using System.Text.Json.Nodes;
using Inngest.Signing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Inngest.Tests.AspNetCore;

// With CheckSync the check also asks Inngest's Get app endpoint about the app: registered, not archived, no failed sync,
// and the same app version and number of functions as this instance.
public sealed class InngestHealthCheckSyncTests
{
    private const string AppsList = """{"data":[],"metadata":{"fetchedAt":"2026-10-30T12:00:00Z"},"page":{"limit":1}}""";
    private const string NotFound = """{"errors":[{"code":"not_found","message":"App not found"}]}""";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    // Inngest leaves out what is false, zero or empty, so this does too.
    private static string AppBody(int? functionCount, string? version = null, string? status = null, string? syncedAt = null, string? error = null, bool archived = false)
    {
        var latest = new JsonObject { ["sdkLanguage"] = "dotnet", ["url"] = "https://shop.example.com/api/inngest" };
        if (version is not null)
        {
            latest["appVersion"] = version;
        }

        if (status is not null)
        {
            latest["status"] = status;
        }

        if (syncedAt is not null)
        {
            latest["syncedAt"] = syncedAt;
        }

        if (error is not null)
        {
            latest["error"] = error;
        }

        var app = new JsonObject { ["id"] = "test-app", ["name"] = "test-app", ["method"] = "SERVE", ["latestSync"] = latest };
        if (functionCount is { } count)
        {
            app["functionCount"] = count;
        }

        if (version is not null)
        {
            app["appVersion"] = version;
        }

        if (archived)
        {
            app["isArchived"] = true;
        }

        return new JsonObject { ["data"] = app, ["metadata"] = new JsonObject { ["fetchedAt"] = "2026-10-30T12:00:00Z" } }.ToJsonString();
    }

    private static Action<IHealthChecksBuilder> WithSync(HealthStatus? failureStatus = null) =>
        checks => checks.AddInngest(options => options.CheckSync = true, failureStatus: failureStatus);

    [Fact]
    public async Task Is_healthy_when_Inngest_knows_the_app_as_this_instance_serves_it()
    {
        await using var app = HealthHarness.Cloud(WithSync(), options => options.AppVersion = "2026.10.30", functions: 2);
        app.Handler
            .Respond(HttpStatusCode.OK, AppsList)
            .Respond(HttpStatusCode.OK, AppBody(2, "2026.10.30", status: "success", syncedAt: "2026-10-30T11:58:00Z"));

        var entry = await app.CheckAsync(cancellationToken: Cancellation);

        entry.Status.ShouldBe(HealthStatus.Healthy);
        entry.Description.ShouldBe("Inngest accepted the signing key. The app \"test-app\" is synced with 2 functions.");
        entry.Data["api"].ShouldBe("ok");
        entry.Data["sync"].ShouldBe("synced");
        entry.Data["functionsServed"].ShouldBe(2);
        entry.Data["functionsInInngest"].ShouldBe(2);
        entry.Data["syncStatus"].ShouldBe("success");
        entry.Data["syncedAt"].ShouldBe("2026-10-30T11:58:00Z");
        entry.Data["appVersion"].ShouldBe("2026.10.30");
        entry.Data["syncedAppVersion"].ShouldBe("2026.10.30");
        app.Handler.Requests.Select(request => request.Uri.AbsoluteUri).ShouldBe(
            ["https://api.inngest.com/v2/apps?limit=1", "https://api.inngest.com/v2/apps/test-app"]);
        app.Handler.Requests.ShouldAllBe(request => request.Header("Authorization") == Bearer(HealthHarness.SigningKey));
    }

    [Fact]
    public async Task Does_not_ask_about_the_app_unless_asked_to()
    {
        await using var app = HealthHarness.Cloud(functions: 2);
        app.Handler.Respond(HttpStatusCode.OK, AppsList);

        var entry = await app.CheckAsync(cancellationToken: Cancellation);

        entry.Status.ShouldBe(HealthStatus.Healthy);
        entry.Data.ContainsKey("sync").ShouldBeFalse();
        app.Handler.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Fails_while_Inngest_has_no_such_app_and_says_the_key_was_accepted()
    {
        await using var app = HealthHarness.Cloud(WithSync(HealthStatus.Degraded), functions: 2);
        app.Handler.Respond(HttpStatusCode.OK, AppsList).Respond(HttpStatusCode.NotFound, NotFound);

        var entry = await app.CheckAsync(cancellationToken: Cancellation);

        entry.Status.ShouldBe(HealthStatus.Degraded);
        entry.Description.ShouldBe("Inngest has no app \"test-app\". Sync the app so that Inngest knows its functions.");
        entry.Data["api"].ShouldBe("ok");
        entry.Data["acceptedKey"].ShouldBe("signingKey");
        entry.Data["sync"].ShouldBe("not registered");
        entry.Data["functionsServed"].ShouldBe(2);
        entry.Data.ContainsKey("functionsInInngest").ShouldBeFalse();
    }

    [Fact]
    public async Task Fails_while_the_app_is_archived()
    {
        await using var app = HealthHarness.Cloud(WithSync(), functions: 2);
        app.Handler.Respond(HttpStatusCode.OK, AppsList).Respond(HttpStatusCode.OK, AppBody(2, archived: true));

        var entry = await app.CheckAsync(cancellationToken: Cancellation);

        entry.Status.ShouldBe(HealthStatus.Unhealthy);
        entry.Description.ShouldBe("The app \"test-app\" is archived in Inngest.");
        entry.Data["sync"].ShouldBe("archived");
    }

    // Inngest Cloud reports the status of the latest sync. The dev server does not, and an error is all it has to say.
    [Theory]
    [InlineData("error", null)]
    [InlineData("error", "could not reach the app")]
    [InlineData(null, "could not reach the app")]
    public async Task Fails_when_the_latest_sync_failed(string? status, string? error)
    {
        await using var app = HealthHarness.Cloud(WithSync(), functions: 2);
        app.Handler.Respond(HttpStatusCode.OK, AppsList).Respond(HttpStatusCode.OK, AppBody(2, status: status, error: error));

        var entry = await app.CheckAsync(cancellationToken: Cancellation);

        entry.Status.ShouldBe(HealthStatus.Unhealthy);
        entry.Description.ShouldBe("Inngest reports that the latest sync of the app \"test-app\" failed.");
        entry.Data["sync"].ShouldBe("failed");
        entry.Description.ShouldNotBeNull().ShouldNotContain("could not reach the app");
    }

    [Fact]
    public async Task Goes_by_the_status_of_the_sync_when_an_old_error_is_still_reported()
    {
        await using var app = HealthHarness.Cloud(WithSync(), functions: 2);
        app.Handler.Respond(HttpStatusCode.OK, AppsList).Respond(HttpStatusCode.OK, AppBody(2, status: "success", error: "from an earlier sync"));

        var entry = await app.CheckAsync(cancellationToken: Cancellation);

        entry.Status.ShouldBe(HealthStatus.Healthy);
        entry.Data["sync"].ShouldBe("synced");
    }

    [Fact]
    public async Task Fails_when_Inngest_synced_another_version_of_the_app()
    {
        await using var app = HealthHarness.Cloud(WithSync(), options => options.AppVersion = "2026.10.31", functions: 2);
        app.Handler.Respond(HttpStatusCode.OK, AppsList).Respond(HttpStatusCode.OK, AppBody(2, "2026.10.30"));

        var entry = await app.CheckAsync(cancellationToken: Cancellation);

        entry.Status.ShouldBe(HealthStatus.Unhealthy);
        entry.Description.ShouldBe("Inngest last synced version \"2026.10.30\" of the app \"test-app\", and this instance is version \"2026.10.31\". Sync the app.");
        entry.Data["sync"].ShouldBe("version mismatch");
        entry.Data["appVersion"].ShouldBe("2026.10.31");
        entry.Data["syncedAppVersion"].ShouldBe("2026.10.30");
    }

    // Without a version on one side there is nothing to compare, and the number of functions is all that is left.
    [Theory]
    [InlineData(null, "2026.10.30")]
    [InlineData("2026.10.30", null)]
    [InlineData(null, null)]
    public async Task Does_not_compare_versions_unless_both_sides_have_one(string? served, string? synced)
    {
        await using var app = HealthHarness.Cloud(WithSync(), options => options.AppVersion = served, functions: 2);
        app.Handler.Respond(HttpStatusCode.OK, AppsList).Respond(HttpStatusCode.OK, AppBody(2, synced));

        var entry = await app.CheckAsync(cancellationToken: Cancellation);

        entry.Status.ShouldBe(HealthStatus.Healthy);
    }

    [Theory]
    [InlineData(1, 2, "Inngest has 1 function for the app \"test-app\", and this instance serves 2. Sync the app.")]
    [InlineData(3, 2, "Inngest has 3 functions for the app \"test-app\", and this instance serves 2. Sync the app.")]
    [InlineData(null, 2, "Inngest has 0 functions for the app \"test-app\", and this instance serves 2. Sync the app.")]
    public async Task Fails_when_Inngest_knows_another_number_of_functions(int? inInngest, int served, string expected)
    {
        await using var app = HealthHarness.Cloud(WithSync(), functions: served);
        app.Handler.Respond(HttpStatusCode.OK, AppsList).Respond(HttpStatusCode.OK, AppBody(inInngest));

        var entry = await app.CheckAsync(cancellationToken: Cancellation);

        entry.Status.ShouldBe(HealthStatus.Unhealthy);
        entry.Description.ShouldBe(expected);
        entry.Data["sync"].ShouldBe("function count mismatch");
        entry.Data["functionsServed"].ShouldBe(served);
    }

    [Theory]
    [InlineData(1, "The app \"test-app\" is synced with 1 function.")]
    [InlineData(0, "The app \"test-app\" is synced with 0 functions.")]
    public async Task Counts_the_functions_of_an_app_in_the_words_of_its_number(int functions, string expectedEnd)
    {
        await using var app = HealthHarness.Cloud(WithSync(), functions: functions);
        app.Handler.Respond(HttpStatusCode.OK, AppsList).Respond(HttpStatusCode.OK, AppBody(functions == 0 ? null : functions));

        var entry = await app.CheckAsync(cancellationToken: Cancellation);

        entry.Status.ShouldBe(HealthStatus.Healthy);
        entry.Description.ShouldNotBeNull().ShouldEndWith(expectedEnd);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("""{"data":"not an app"}""")]
    [InlineData("<html>a gateway answered</html>")]
    [InlineData("")]
    public async Task Fails_when_the_answer_about_the_app_cannot_be_read(string body)
    {
        await using var app = HealthHarness.Cloud(WithSync(), functions: 2);
        app.Handler.Respond(HttpStatusCode.OK, AppsList).Respond(HttpStatusCode.OK, body);

        var entry = await app.CheckAsync(cancellationToken: Cancellation);

        entry.Status.ShouldBe(HealthStatus.Unhealthy);
        entry.Description.ShouldBe("Inngest's answer about the app \"test-app\" could not be read.");
        entry.Data["sync"].ShouldBe("unreadable");
    }

    // Once the first request has shown that the API takes the key, what goes wrong with the second is a problem of the
    // API like any other.
    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, HealthStatus.Unhealthy, "Inngest at https://api.inngest.com answered with HTTP 500.")]
    [InlineData(HttpStatusCode.Unauthorized, HealthStatus.Unhealthy, "Inngest refused the signing key (HTTP 401).")]
    [InlineData(HttpStatusCode.TooManyRequests, HealthStatus.Degraded, "Inngest rate limited the request (HTTP 429), so the signing key was not checked this time.")]
    public async Task Reports_a_failing_second_request_as_a_failure_of_the_API(HttpStatusCode status, HealthStatus expected, string description)
    {
        await using var app = HealthHarness.Cloud(WithSync(), functions: 2);
        app.Handler.Respond(HttpStatusCode.OK, AppsList).Respond(status);

        var entry = await app.CheckAsync(cancellationToken: Cancellation);

        entry.Status.ShouldBe(expected);
        entry.Description.ShouldBe(description);
        entry.Data.ContainsKey("sync").ShouldBeFalse();
    }

    [Fact]
    public async Task Reports_a_second_request_that_cannot_reach_Inngest_as_unreachable()
    {
        await using var app = HealthHarness.Cloud(WithSync(), functions: 2);
        app.Handler.Respond(HttpStatusCode.OK, AppsList).Fail(new HttpRequestException("The connection was reset."));

        var entry = await app.CheckAsync(cancellationToken: Cancellation);

        entry.Status.ShouldBe(HealthStatus.Unhealthy);
        entry.Description.ShouldBe("Could not reach Inngest at https://api.inngest.com: The connection was reset.");
        entry.Data["api"].ShouldBe("unreachable");
    }

    [Fact]
    public async Task Shares_the_timeout_between_the_two_requests()
    {
        await using var app = HealthHarness.Cloud(WithSync(), functions: 2);
        app.Handler.Respond(HttpStatusCode.OK, AppsList).Hang();

        var pending = app.CheckAsync(cancellationToken: Cancellation);
        await app.WaitForRequestsAsync(2);
        app.Time.Advance(TimeSpan.FromSeconds(9.9));
        pending.IsCompleted.ShouldBeFalse();
        app.Time.Advance(TimeSpan.FromSeconds(0.1));
        var entry = await pending;

        entry.Data["api"].ShouldBe("timed out");
    }

    [Fact]
    public async Task Asks_the_dev_server_about_the_app_with_what_it_reports()
    {
        await using var app = HealthHarness.Dev(WithSync(), functions: 2);
        app.Handler
            .Respond(HttpStatusCode.OK, """{"status":200,"message":"OK"}""")
            .Respond(HttpStatusCode.OK, AppBody(2));

        var entry = await app.CheckAsync(cancellationToken: Cancellation);

        entry.Status.ShouldBe(HealthStatus.Healthy);
        entry.Description.ShouldBe("Reached the Inngest dev server. The app \"test-app\" is synced with 2 functions.");
        entry.Data.ContainsKey("syncedAt").ShouldBeFalse();
        entry.Data.ContainsKey("syncStatus").ShouldBeFalse();
        app.Handler.Requests.Select(request => request.Uri.AbsoluteUri).ShouldBe(
            ["http://localhost:8288/health", "http://localhost:8288/v2/apps/test-app"]);
        app.Handler.Requests.ShouldAllBe(request => request.Header("Authorization") == null);
    }

    [Fact]
    public async Task Keeps_a_missing_app_for_the_failure_cache_duration_and_a_synced_one_for_the_cache_duration()
    {
        await using var app = HealthHarness.Cloud(WithSync(), functions: 2);
        app.Handler
            .Respond(HttpStatusCode.OK, AppsList).Respond(HttpStatusCode.NotFound, NotFound)
            .Respond(HttpStatusCode.OK, AppsList).Respond(HttpStatusCode.OK, AppBody(2));

        var missing = await app.CheckAsync(cancellationToken: Cancellation);
        app.Time.Advance(TimeSpan.FromSeconds(4.9));
        var stillMissing = await app.CheckAsync(cancellationToken: Cancellation);
        app.Handler.Requests.Count.ShouldBe(2);

        app.Time.Advance(TimeSpan.FromSeconds(0.1));
        var synced = await app.CheckAsync(cancellationToken: Cancellation);
        app.Time.Advance(TimeSpan.FromSeconds(29));
        var stillSynced = await app.CheckAsync(cancellationToken: Cancellation);

        missing.Data["sync"].ShouldBe("not registered");
        stillMissing.Data["sync"].ShouldBe("not registered");
        synced.Data["sync"].ShouldBe("synced");
        stillSynced.Data["sync"].ShouldBe("synced");
        app.Handler.Requests.Count.ShouldBe(4);
    }

    [Fact]
    public async Task Uses_the_fallback_key_for_both_requests_when_the_signing_key_is_refused()
    {
        await using var app = HealthHarness.Cloud(WithSync(), options => options.SigningKeyFallback = HealthHarness.FallbackSigningKey, functions: 2);
        app.Handler
            .Respond(HttpStatusCode.Unauthorized).Respond(HttpStatusCode.OK, AppsList)
            .Respond(HttpStatusCode.Unauthorized).Respond(HttpStatusCode.OK, AppBody(2));

        var entry = await app.CheckAsync(cancellationToken: Cancellation);

        entry.Status.ShouldBe(HealthStatus.Healthy);
        entry.Data["acceptedKey"].ShouldBe("signingKeyFallback");
        entry.Data["sync"].ShouldBe("synced");
        app.Handler.Requests.Select(request => request.Header("Authorization")).ShouldBe(
            [
                Bearer(HealthHarness.SigningKey),
                Bearer(HealthHarness.FallbackSigningKey),
                Bearer(HealthHarness.SigningKey),
                Bearer(HealthHarness.FallbackSigningKey),
            ]);
    }

    // Inngest holds a failure handler as a function of its own, and so does the registry that the count comes from.
    [Fact]
    public async Task Counts_a_failure_handler_as_a_function_like_Inngest_does()
    {
        await using var app = HealthHarness.Cloud(
            WithSync(),
            addFunctions: inngest => inngest.AddFunction(
                new FunctionOptions { Id = "charge", Triggers = [Trigger.Event("tests/charge")] },
                static (_, _) => Task.FromResult<object?>(null),
                static (_, _) => Task.CompletedTask));
        app.Handler.Respond(HttpStatusCode.OK, AppsList).Respond(HttpStatusCode.OK, AppBody(2));

        var entry = await app.CheckAsync(cancellationToken: Cancellation);

        entry.Status.ShouldBe(HealthStatus.Healthy);
        entry.Data["functionsServed"].ShouldBe(2);
    }

    [Fact]
    public async Task Escapes_the_app_id_in_the_path()
    {
        await using var app = HealthHarness.Cloud(WithSync(), options => options.AppId = "shop/eu west", functions: 2);
        app.Handler.Respond(HttpStatusCode.OK, AppsList).Respond(HttpStatusCode.NotFound, NotFound);

        await app.CheckAsync(cancellationToken: Cancellation);

        app.Handler.Requests[1].Uri.AbsoluteUri.ShouldBe("https://api.inngest.com/v2/apps/shop%2Feu%20west");
    }

    private static string Bearer(string signingKey) => "Bearer " + SigningKey.Create(signingKey)!.BearerToken;
}
