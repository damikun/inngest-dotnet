using System.Net;
using Inngest.Signing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Testing;

namespace Inngest.Tests.AspNetCore;

// What a readiness probe learns from the health check of an app that serves functions: the configuration is complete,
// and Inngest answers a request that carries the signing key.
public sealed class InngestHealthCheckTests
{
    private const string AppsUrl = "https://api.inngest.com/v2/apps?limit=1";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    public enum Answer
    {
        Accepted,
        AcceptedByFallback,
        Refused,
        Unreachable,
        ServerError,
        RateLimited,
    }

    [Fact]
    public async Task Is_healthy_when_Inngest_accepts_the_signing_key()
    {
        await using var app = HealthHarness.Cloud();
        app.Handler.Respond(HttpStatusCode.OK, """{"metadata":{"fetchedAt":"2026-10-30T12:00:00Z"},"page":{"limit":1}}""");

        var entry = await app.CheckAsync(cancellationToken: Cancellation);

        entry.Status.ShouldBe(HealthStatus.Healthy);
        entry.Description.ShouldBe("Inngest accepted the signing key.");
        entry.Data.ShouldBe(
            new Dictionary<string, object>
            {
                ["mode"] = "cloud",
                ["apiBaseUrl"] = "https://api.inngest.com",
                ["signingKey"] = "set",
                ["signingKeyFallback"] = "not set",
                ["eventKey"] = "not set",
                ["api"] = "ok",
                ["apiStatusCode"] = 200,
                ["acceptedKey"] = "signingKey",
                ["checkedAt"] = "2026-10-30T12:00:00Z",
            },
            ignoreOrder: true);
        var request = app.Handler.Single;
        request.Method.ShouldBe(HttpMethod.Get);
        request.Uri.ShouldBe(new Uri(AppsUrl));
        request.Header("Authorization").ShouldBe(Bearer(HealthHarness.SigningKey));
        request.Header("X-Inngest-SDK").ShouldStartWith("inngest-dotnet:v");
    }

    [Fact]
    public async Task Reaches_the_dev_server_without_any_key()
    {
        await using var app = HealthHarness.Dev();
        app.Handler.Respond(HttpStatusCode.OK, """{"status":200,"message":"OK"}""");

        var entry = await app.CheckAsync(cancellationToken: Cancellation);

        entry.Status.ShouldBe(HealthStatus.Healthy);
        entry.Description.ShouldBe("Reached the Inngest dev server.");
        entry.Data["mode"].ShouldBe("dev");
        entry.Data["apiBaseUrl"].ShouldBe("http://localhost:8288");
        entry.Data["signingKey"].ShouldBe("not set");
        entry.Data["api"].ShouldBe("ok");
        entry.Data.ContainsKey("acceptedKey").ShouldBeFalse();

        // The dev server's own health endpoint, which does not depend on the REST API that /v2/apps belongs to.
        app.Handler.Single.Uri.ShouldBe(new Uri("http://localhost:8288/health"));
        app.Handler.Single.Header("Authorization").ShouldBeNull();
    }

    [Fact]
    public async Task Fails_without_a_signing_key_in_cloud_mode_and_sends_no_request()
    {
        await using var app = HealthHarness.Cloud(
            checks => checks.AddInngest(failureStatus: HealthStatus.Degraded),
            options => options.SigningKey = null);

        var entry = await app.CheckAsync(cancellationToken: Cancellation);

        entry.Status.ShouldBe(HealthStatus.Degraded);
        entry.Description.ShouldNotBeNull().ShouldStartWith("No signing key is configured");
        entry.Data["signingKey"].ShouldBe("missing");
        entry.Data["api"].ShouldBe("not checked");
        app.Handler.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false, false, HealthStatus.Healthy, "not set")]
    [InlineData(false, true, HealthStatus.Healthy, "set")]
    [InlineData(true, true, HealthStatus.Healthy, "set")]
    [InlineData(true, false, HealthStatus.Unhealthy, "missing")]
    public async Task Wants_an_event_key_only_when_asked_to(bool requireEventKey, bool hasEventKey, HealthStatus expected, string expectedEntry)
    {
        await using var app = HealthHarness.Cloud(
            checks => checks.AddInngest(options => options.RequireEventKey = requireEventKey),
            options => options.EventKey = hasEventKey ? HealthHarness.EventKey : null);
        app.Handler.RespondAlways(HttpStatusCode.OK);

        var entry = await app.CheckAsync(cancellationToken: Cancellation);

        entry.Status.ShouldBe(expected);
        entry.Data["eventKey"].ShouldBe(expectedEntry);
        if (expected == HealthStatus.Unhealthy)
        {
            entry.Description.ShouldNotBeNull().ShouldStartWith("No event key is configured");
            app.Handler.Requests.ShouldBeEmpty();
        }
        else
        {
            app.Handler.Requests.Count.ShouldBe(1);
        }
    }

    [Fact]
    public async Task Names_every_missing_key_in_the_description()
    {
        await using var app = HealthHarness.Cloud(
            checks => checks.AddInngest(options => options.RequireEventKey = true),
            options => options.SigningKey = null);

        var entry = await app.CheckAsync(cancellationToken: Cancellation);

        entry.Status.ShouldBe(HealthStatus.Unhealthy);
        entry.Description.ShouldNotBeNull().ShouldContain("No signing key is configured");
        entry.Description.ShouldContain("No event key is configured");
    }

    [Fact]
    public async Task The_dev_server_needs_no_event_key_even_when_it_is_required()
    {
        await using var app = HealthHarness.Dev(checks => checks.AddInngest(options => options.RequireEventKey = true));
        app.Handler.Respond(HttpStatusCode.OK);

        var entry = await app.CheckAsync(cancellationToken: Cancellation);

        entry.Status.ShouldBe(HealthStatus.Healthy);
        entry.Data["eventKey"].ShouldBe("not set");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Fails_when_Inngest_refuses_the_signing_key(HttpStatusCode status)
    {
        await using var app = HealthHarness.Cloud();
        app.Handler.Respond(status, """{"errors":[{"code":"authorization_header_missing","message":"authorization header missing or invalid"}]}""");

        var entry = await app.CheckAsync(cancellationToken: Cancellation);

        entry.Status.ShouldBe(HealthStatus.Unhealthy);
        entry.Description.ShouldBe($"Inngest refused the signing key (HTTP {(int)status}).");
        entry.Data["api"].ShouldBe("refused");
        entry.Data["apiStatusCode"].ShouldBe((int)status);
        entry.Data.ContainsKey("acceptedKey").ShouldBeFalse();
    }

    [Fact]
    public async Task Falls_back_to_the_fallback_key_as_the_client_does()
    {
        await using var app = HealthHarness.Cloud(configure: options => options.SigningKeyFallback = HealthHarness.FallbackSigningKey);
        app.Handler.Respond(HttpStatusCode.Unauthorized).Respond(HttpStatusCode.OK);

        var entry = await app.CheckAsync(cancellationToken: Cancellation);

        entry.Status.ShouldBe(HealthStatus.Healthy);
        entry.Description.ShouldBe("Inngest refused the signing key and accepted the fallback signing key.");
        entry.Data["signingKeyFallback"].ShouldBe("set");
        entry.Data["acceptedKey"].ShouldBe("signingKeyFallback");
        app.Handler.Requests.Select(request => request.Header("Authorization")).ShouldBe(
            [Bearer(HealthHarness.SigningKey), Bearer(HealthHarness.FallbackSigningKey)]);
    }

    [Fact]
    public async Task Fails_when_Inngest_refuses_the_fallback_key_too()
    {
        await using var app = HealthHarness.Cloud(configure: options => options.SigningKeyFallback = HealthHarness.FallbackSigningKey);
        app.Handler.Respond(HttpStatusCode.Unauthorized).Respond(HttpStatusCode.Forbidden);

        var entry = await app.CheckAsync(cancellationToken: Cancellation);

        entry.Status.ShouldBe(HealthStatus.Unhealthy);
        entry.Description.ShouldBe("Inngest refused the signing key and the fallback signing key (HTTP 403).");
        entry.Data["apiStatusCode"].ShouldBe(403);
    }

    [Fact]
    public async Task Says_when_the_dev_server_wants_a_key_the_app_does_not_have()
    {
        await using var app = HealthHarness.Dev();
        app.Handler.Respond(HttpStatusCode.Unauthorized);

        var entry = await app.CheckAsync(cancellationToken: Cancellation);

        entry.Status.ShouldBe(HealthStatus.Unhealthy);
        entry.Description.ShouldNotBeNull().ShouldStartWith("Inngest requires a signing key (HTTP 401) and none is configured.");
    }

    [Fact]
    public async Task Fails_when_Inngest_cannot_be_reached()
    {
        await using var app = HealthHarness.Cloud(checks => checks.AddInngest(failureStatus: HealthStatus.Degraded));
        app.Handler.Fail(new HttpRequestException("Connection refused (api.inngest.com:443)"));

        var entry = await app.CheckAsync(cancellationToken: Cancellation);

        entry.Status.ShouldBe(HealthStatus.Degraded);
        entry.Description.ShouldBe("Could not reach Inngest at https://api.inngest.com: Connection refused (api.inngest.com:443).");
        entry.Data["api"].ShouldBe("unreachable");
        entry.Data.ContainsKey("apiStatusCode").ShouldBeFalse();
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task Fails_when_Inngest_answers_with_something_else(HttpStatusCode status)
    {
        await using var app = HealthHarness.Cloud();
        app.Handler.Respond(status);

        var entry = await app.CheckAsync(cancellationToken: Cancellation);

        entry.Status.ShouldBe(HealthStatus.Unhealthy);
        entry.Description.ShouldBe($"Inngest at https://api.inngest.com answered with HTTP {(int)status}.");
        entry.Data["api"].ShouldBe("unexpected response");
        entry.Data["apiStatusCode"].ShouldBe((int)status);
    }

    // The request is not proof of anything when Inngest limits it, and taking every replica out of rotation because the
    // probes ask too often would be an outage of the check's own making.
    [Theory]
    [InlineData(HealthStatus.Unhealthy, HealthStatus.Degraded)]
    [InlineData(HealthStatus.Degraded, HealthStatus.Degraded)]
    [InlineData(HealthStatus.Healthy, HealthStatus.Healthy)]
    public async Task Is_at_most_degraded_when_Inngest_rate_limits_the_request(HealthStatus failureStatus, HealthStatus expected)
    {
        await using var app = HealthHarness.Cloud(checks => checks.AddInngest(failureStatus: failureStatus));
        app.Handler.Respond(HttpStatusCode.TooManyRequests);

        var entry = await app.CheckAsync(cancellationToken: Cancellation);

        entry.Status.ShouldBe(expected);
        entry.Description.ShouldNotBeNull().ShouldContain("rate limited");
        entry.Data["api"].ShouldBe("rate limited");
    }

    [Fact]
    public async Task Reports_a_request_that_outlasts_the_timeout_with_the_failure_status()
    {
        await using var app = HealthHarness.Cloud(checks => checks.AddInngest(failureStatus: HealthStatus.Degraded));
        app.Handler.Hang();

        var pending = app.CheckAsync(cancellationToken: Cancellation);
        await app.WaitForRequestsAsync(1);

        // A registration has no timeout of its own, so the request is cut off after 10 seconds.
        app.Time.Advance(TimeSpan.FromSeconds(9.9));
        pending.IsCompleted.ShouldBeFalse();
        app.Time.Advance(TimeSpan.FromSeconds(0.1));
        var entry = await pending;

        entry.Status.ShouldBe(HealthStatus.Degraded);
        entry.Description.ShouldBe("Inngest at https://api.inngest.com did not answer within the timeout of the check.");
        entry.Data["api"].ShouldBe("timed out");
    }

    // The health check service cancels a check at the registration's timeout and reports it in words of its own, so the
    // request gets 90 percent of the timeout, and the result keeps the failure status the registration asks for.
    [Fact]
    public async Task Takes_its_timeout_from_the_registration()
    {
        await using var app = HealthHarness.Cloud(checks => checks.AddInngest(failureStatus: HealthStatus.Degraded, timeout: TimeSpan.FromSeconds(60)));
        app.Handler.Hang();

        var pending = app.CheckAsync(cancellationToken: Cancellation);
        await app.WaitForRequestsAsync(1);
        app.Time.Advance(TimeSpan.FromSeconds(53.9));
        pending.IsCompleted.ShouldBeFalse();
        app.Time.Advance(TimeSpan.FromSeconds(0.2));
        var entry = await pending;

        entry.Status.ShouldBe(HealthStatus.Degraded);
        entry.Data["api"].ShouldBe("timed out");
    }

    [Fact]
    public async Task Does_not_hang_on_a_request_that_never_answers_when_the_registration_has_a_timeout()
    {
        await using var app = HealthHarness.Cloud(checks => checks.AddInngest(timeout: TimeSpan.FromMilliseconds(300)), systemTime: true);
        app.Handler.Hang();

        var entry = await app.CheckAsync(cancellationToken: Cancellation).WaitAsync(TimeSpan.FromSeconds(30), Cancellation);

        // Either the check or the health check service ended it, depending on which timer ran first.
        entry.Status.ShouldBe(HealthStatus.Unhealthy);
        entry.Duration.ShouldBeLessThan(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Stops_when_the_probe_is_cancelled_and_leaves_nothing_for_the_next_probe()
    {
        await using var app = HealthHarness.Cloud();
        app.Handler.Hang().Respond(HttpStatusCode.OK);
        using var cancelProbe = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);

        var cancelled = app.CheckAsync(cancellationToken: cancelProbe.Token);
        await app.WaitForRequestsAsync(1);
        await cancelProbe.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(cancelled);
        var next = await app.CheckAsync(cancellationToken: Cancellation);
        next.Status.ShouldBe(HealthStatus.Healthy);
        app.Handler.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Reuses_a_successful_answer_for_the_cache_duration()
    {
        await using var app = HealthHarness.Cloud();
        app.Handler.RespondAlways(HttpStatusCode.OK);

        var first = await app.CheckAsync(cancellationToken: Cancellation);
        app.Time.Advance(TimeSpan.FromSeconds(29));
        var second = await app.CheckAsync(cancellationToken: Cancellation);
        app.Handler.Requests.Count.ShouldBe(1);

        app.Time.Advance(TimeSpan.FromSeconds(1));
        var third = await app.CheckAsync(cancellationToken: Cancellation);

        app.Handler.Requests.Count.ShouldBe(2);
        first.Status.ShouldBe(HealthStatus.Healthy);
        second.Data["checkedAt"].ShouldBe(first.Data["checkedAt"]);
        third.Data["checkedAt"].ShouldBe("2026-10-30T12:00:30Z");
    }

    [Fact]
    public async Task Reuses_a_failure_for_the_shorter_failure_cache_duration()
    {
        await using var app = HealthHarness.Cloud();
        app.Handler.Respond(HttpStatusCode.ServiceUnavailable).Respond(HttpStatusCode.OK);

        var failed = await app.CheckAsync(cancellationToken: Cancellation);
        app.Time.Advance(TimeSpan.FromSeconds(4.9));
        var stillFailed = await app.CheckAsync(cancellationToken: Cancellation);
        app.Handler.Requests.Count.ShouldBe(1);

        app.Time.Advance(TimeSpan.FromSeconds(0.1));
        var recovered = await app.CheckAsync(cancellationToken: Cancellation);

        failed.Status.ShouldBe(HealthStatus.Unhealthy);
        stillFailed.Status.ShouldBe(HealthStatus.Unhealthy);
        recovered.Status.ShouldBe(HealthStatus.Healthy);
        app.Handler.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Never_keeps_a_failure_longer_than_a_success()
    {
        await using var app = HealthHarness.Cloud(checks => checks.AddInngest(options => options.CacheDuration = TimeSpan.FromSeconds(2)));
        app.Handler.Respond(HttpStatusCode.ServiceUnavailable).Respond(HttpStatusCode.OK);

        await app.CheckAsync(cancellationToken: Cancellation);
        app.Time.Advance(TimeSpan.FromSeconds(2));
        var recovered = await app.CheckAsync(cancellationToken: Cancellation);

        recovered.Status.ShouldBe(HealthStatus.Healthy);
    }

    [Fact]
    public async Task Keeps_a_rate_limited_answer_for_the_full_cache_duration()
    {
        await using var app = HealthHarness.Cloud();
        app.Handler.Respond(HttpStatusCode.TooManyRequests).Respond(HttpStatusCode.OK);

        await app.CheckAsync(cancellationToken: Cancellation);
        app.Time.Advance(TimeSpan.FromSeconds(29));
        var limited = await app.CheckAsync(cancellationToken: Cancellation);
        app.Handler.Requests.Count.ShouldBe(1);

        app.Time.Advance(TimeSpan.FromSeconds(1));
        var recovered = await app.CheckAsync(cancellationToken: Cancellation);

        limited.Data["api"].ShouldBe("rate limited");
        recovered.Status.ShouldBe(HealthStatus.Healthy);
    }

    [Fact]
    public async Task Asks_on_every_probe_when_the_cache_duration_is_zero()
    {
        await using var app = HealthHarness.Cloud(checks => checks.AddInngest(options => options.CacheDuration = TimeSpan.Zero));
        app.Handler.RespondAlways(HttpStatusCode.OK);

        await app.CheckAsync(cancellationToken: Cancellation);
        await app.CheckAsync(cancellationToken: Cancellation);
        await app.CheckAsync(cancellationToken: Cancellation);

        app.Handler.Requests.Count.ShouldBe(3);
    }

    [Fact]
    public async Task Probes_that_overlap_share_one_request()
    {
        await using var app = HealthHarness.Cloud();
        app.Handler.Hang();

        var probes = Enumerable.Range(0, 5).Select(_ => app.CheckAsync(cancellationToken: Cancellation)).ToArray();
        await app.WaitForRequestsAsync(1);
        await Task.Delay(50, Cancellation);
        app.Time.Advance(TimeSpan.FromSeconds(10));
        var entries = await Task.WhenAll(probes);

        app.Handler.Requests.Count.ShouldBe(1);
        entries.ShouldAllBe(entry => (string)entry.Data["api"] == "timed out");
    }

    [Fact]
    public async Task Registrations_do_not_share_answers()
    {
        await using var app = HealthHarness.Cloud(checks => checks.AddInngest("first").AddInngest("second"));
        app.Handler.RespondAlways(HttpStatusCode.OK);

        var report = await app.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync(Cancellation);

        report.Entries.Keys.ShouldBe(["first", "second"], ignoreOrder: true);
        app.Handler.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Reports_a_missing_AddInngest_as_a_result()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHealthChecks().AddInngest(failureStatus: HealthStatus.Degraded);
        await using var provider = services.BuildServiceProvider();

        var report = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync(Cancellation);

        report.Entries["inngest"].Status.ShouldBe(HealthStatus.Degraded);
        report.Entries["inngest"].Description.ShouldNotBeNull().ShouldContain("Call services.AddInngest()");
    }

    [Fact]
    public async Task Shows_where_Inngest_is_without_what_the_address_carries()
    {
        await using var app = HealthHarness.Cloud(
            configure: options => options.BaseUrl = new Uri("https://reader:hunter2-sentinel@inngest.example.com:8443/prefix?token=query-sentinel"));
        app.Handler.Fail(new HttpRequestException("Name or service not known (inngest.example.com:8443)"));

        var entry = await app.CheckAsync(cancellationToken: Cancellation);

        entry.Data["apiBaseUrl"].ShouldBe("https://inngest.example.com:8443");
        Everything(entry, app.Logs).ShouldNotContain("hunter2-sentinel");
        Everything(entry, app.Logs).ShouldNotContain("query-sentinel");
    }

    // The result goes to whoever can reach the health endpoint and to every log sink, so it names the keys as set or
    // missing and never as values, and nothing derived from a key leaves either: not the secret, not the hashed token
    // that Inngest is sent, and not the event key.
    [Theory]
    [InlineData(Answer.Accepted)]
    [InlineData(Answer.AcceptedByFallback)]
    [InlineData(Answer.Refused)]
    [InlineData(Answer.Unreachable)]
    [InlineData(Answer.ServerError)]
    [InlineData(Answer.RateLimited)]
    public async Task Keeps_keys_and_tokens_out_of_the_result_and_the_logs(Answer answer)
    {
        await using var app = HealthHarness.Cloud(
            checks => checks.AddInngest(options => options.RequireEventKey = true),
            options =>
            {
                options.SigningKeyFallback = HealthHarness.FallbackSigningKey;
                options.EventKey = HealthHarness.EventKey;
            });
        switch (answer)
        {
            case Answer.Accepted:
                app.Handler.Respond(HttpStatusCode.OK);
                break;
            case Answer.AcceptedByFallback:
                app.Handler.Respond(HttpStatusCode.Unauthorized).Respond(HttpStatusCode.OK);
                break;
            case Answer.Refused:
                app.Handler.RespondAlways(HttpStatusCode.Unauthorized);
                break;
            case Answer.Unreachable:
                app.Handler.Fail(new HttpRequestException("The SSL connection could not be established."));
                break;
            case Answer.ServerError:
                app.Handler.Respond(HttpStatusCode.InternalServerError);
                break;
            default:
                app.Handler.Respond(HttpStatusCode.TooManyRequests);
                break;
        }

        var entry = await app.CheckAsync(cancellationToken: Cancellation);

        var text = Everything(entry, app.Logs);
        text.ShouldContain("inngest", Case.Insensitive);
        foreach (var key in new[] { HealthHarness.SigningKey, HealthHarness.FallbackSigningKey })
        {
            var parsed = SigningKey.Create(key)!;
            var hashed = parsed.BearerToken[(parsed.BearerToken.LastIndexOf('-') + 1)..];
            text.ShouldNotContain(key);
            text.ShouldNotContain(key[(key.LastIndexOf('-') + 1)..]);
            text.ShouldNotContain(parsed.BearerToken);
            text.ShouldNotContain(hashed);
        }

        text.ShouldNotContain(HealthHarness.EventKey);
        text.ShouldNotContain("Bearer");
    }

    private static string Bearer(string signingKey) => "Bearer " + SigningKey.Create(signingKey)!.BearerToken;

    // Everything a probe, a dashboard or a log reader can see of the check.
    private static string Everything(HealthReportEntry entry, FakeLogCollector logs) => string.Join(
        '\n',
        entry.Description,
        entry.Exception?.ToString(),
        string.Join('\n', entry.Data.Select(pair => $"{pair.Key}={pair.Value}")),
        string.Join('\n', logs.GetSnapshot().Select(record => $"{record.Message} {record.Exception}")));
}
