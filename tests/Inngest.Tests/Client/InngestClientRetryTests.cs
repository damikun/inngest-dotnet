using System.Net;
using Inngest.Client;
using Microsoft.Extensions.Logging;

namespace Inngest.Tests.Client;

public sealed class InngestClientRetryTests
{
    private const string ServerError = """{"ids":[],"status":500,"error":"upstream is down"}""";

    private static Task<SendEventsResult> SendOne(ClientFixture fixture, CancellationToken cancellationToken = default) =>
        fixture.Client.SendAsync(new InngestEvent("app/user.created", new { Id = 1 }), cancellationToken);

    [Fact]
    public async Task Retries_a_failed_send_with_the_same_seed_and_the_same_body()
    {
        var fixture = new ClientFixture();
        fixture.Handler
            .Respond(HttpStatusCode.InternalServerError, ServerError)
            .Respond(HttpStatusCode.BadGateway, "<html>bad gateway</html>", "text/html")
            .Respond(HttpStatusCode.OK, ClientFixture.Accepted);

        var result = await fixture.Time.AdvanceUntilCompletedAsync(SendOne(fixture));

        result.Ids.ShouldBe(["01HQ0000000000000000000001"]);
        var requests = fixture.Handler.Requests;
        requests.Count.ShouldBe(3);
        requests.Select(request => request.Header("X-Inngest-Event-Id-Seed")).Distinct().Count().ShouldBe(1);
        requests.Select(request => request.BodyText).Distinct().Count().ShouldBe(1);
        requests.Select(request => request.Uri).Distinct().Count().ShouldBe(1);
    }

    [Fact]
    public async Task Waits_on_the_time_provider_not_on_the_wall_clock()
    {
        var fixture = new ClientFixture();
        fixture.Handler.Respond(HttpStatusCode.InternalServerError, ServerError).Respond(HttpStatusCode.OK, ClientFixture.Accepted);

        var send = SendOne(fixture);
        await WaitForRequestsAsync(fixture, 1);

        // Longer than the longest first backoff of 200 ms, but the fake clock has not moved.
        await Task.Delay(300);
        fixture.Handler.Requests.Count.ShouldBe(1);

        // Shorter than the shortest first backoff of 100 ms.
        fixture.Time.Advance(TimeSpan.FromMilliseconds(99));
        await Task.Delay(50);
        fixture.Handler.Requests.Count.ShouldBe(1);
        send.IsCompleted.ShouldBeFalse();

        await fixture.Time.AdvanceUntilCompletedAsync(send);
        fixture.Handler.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Gives_up_after_five_attempts_with_the_last_error()
    {
        var fixture = new ClientFixture();
        fixture.Handler.RespondAlways(HttpStatusCode.InternalServerError, ServerError);

        var exception = await Should.ThrowAsync<InngestException>(fixture.Time.AdvanceUntilCompletedAsync(SendOne(fixture)));

        exception.StatusCode.ShouldBe(500);
        exception.Message.ShouldBe("Inngest API error: 500 upstream is down");
        fixture.Handler.Requests.Count.ShouldBe(InngestClient.MaxAttempts);
        fixture.Handler.Requests.Select(request => request.Header("X-Inngest-Event-Id-Seed")).Distinct().Count().ShouldBe(1);
    }

    [Fact]
    public async Task Logs_each_retry_without_the_event_data()
    {
        var fixture = new ClientFixture();
        fixture.Handler.Respond(HttpStatusCode.InternalServerError, ServerError).Respond(HttpStatusCode.OK, ClientFixture.Accepted);

        await fixture.Time.AdvanceUntilCompletedAsync(SendOne(fixture));

        var records = fixture.Logger.Collector.GetSnapshot();
        var retry = records.Single(record => record.Id.Id == 301);
        retry.Level.ShouldBe(LogLevel.Warning);
        retry.Message.ShouldContain("attempt 1 of 5");
        retry.Message.ShouldContain("upstream is down");
        retry.Message.ShouldNotContain("user.created");
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData((HttpStatusCode)425)]
    public async Task Retries_transient_http_statuses(HttpStatusCode status)
    {
        var fixture = new ClientFixture();
        fixture.Handler.Respond(status, "{}").Respond(HttpStatusCode.OK, ClientFixture.Accepted);

        await fixture.Time.AdvanceUntilCompletedAsync(SendOne(fixture));

        fixture.Handler.Requests.Count.ShouldBe(2);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "", "Cannot process event payload")]
    [InlineData(HttpStatusCode.Unauthorized, "", "Event key not found")]
    [InlineData(HttpStatusCode.Forbidden, "", "Forbidden")]
    [InlineData(HttpStatusCode.NotFound, "", "Event key not found")]
    [InlineData(HttpStatusCode.Conflict, "", "Event transformation failed")]
    [InlineData(HttpStatusCode.PreconditionFailed, "", "Event transformation failed")]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, "", "Event payload too large")]
    [InlineData(HttpStatusCode.NotAcceptable, """{"ids":[""],"status":406}""", """{"ids":[""],"status":406}""")]
    [InlineData(HttpStatusCode.UnprocessableEntity, "gateway said no", "gateway said no")]
    [InlineData(HttpStatusCode.UnprocessableEntity, "", "Unexpected response")]
    public async Task Does_not_retry_client_errors_and_describes_them_from_the_status(HttpStatusCode status, string body, string expectedMessage)
    {
        var fixture = new ClientFixture();
        fixture.Handler.Respond(status, body);

        var exception = await Should.ThrowAsync<InngestException>(SendOne(fixture));

        exception.StatusCode.ShouldBe((int)status);
        exception.Message.ShouldBe($"Inngest API error: {(int)status} {expectedMessage}");
        fixture.Handler.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Prefers_the_error_of_the_server_to_the_text_for_the_status()
    {
        var fixture = new ClientFixture();
        fixture.Handler.Respond(HttpStatusCode.Unauthorized, """{"status":401,"message":"","error":"Event key not found"}""");

        var exception = await Should.ThrowAsync<InngestException>(SendOne(fixture));

        exception.StatusCode.ShouldBe(401);
        exception.Message.ShouldBe("Inngest API error: 401 Event key not found");
    }

    [Fact]
    public async Task Takes_success_from_the_status_in_the_body_not_the_http_status()
    {
        var fixture = new ClientFixture();
        fixture.Handler.Respond(HttpStatusCode.Accepted, ClientFixture.Accepted);

        var result = await SendOne(fixture);

        result.Ids.ShouldBe(["01HQ0000000000000000000001"]);
    }

    [Fact]
    public async Task Fails_when_the_body_reports_an_error_even_though_the_http_status_is_ok()
    {
        var fixture = new ClientFixture();
        fixture.Handler.Respond(HttpStatusCode.OK, """{"ids":["","01HQ0000000000000000000002"],"status":406,"error":"event name is empty"}""");

        var exception = await Should.ThrowAsync<InngestException>(SendOne(fixture));

        exception.StatusCode.ShouldBe(406);
        exception.Message.ShouldBe("Inngest API error: 406 event name is empty");
        fixture.Handler.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Fails_when_the_body_has_a_status_of_200_and_an_error()
    {
        var fixture = new ClientFixture();
        fixture.Handler.RespondAlways(HttpStatusCode.OK, """{"ids":["x"],"status":200,"error":"partial"}""");

        var exception = await Should.ThrowAsync<InngestException>(fixture.Time.AdvanceUntilCompletedAsync(SendOne(fixture)));

        exception.Message.ShouldBe("Inngest API error: 200 partial");
    }

    [Theory]
    [InlineData("""{"ids":["x"]}""")]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    public async Task Treats_a_reply_without_a_usable_status_as_a_failure_and_retries_it(string body)
    {
        var fixture = new ClientFixture();
        fixture.Handler.RespondAlways(HttpStatusCode.OK, body);

        var exception = await Should.ThrowAsync<InngestException>(fixture.Time.AdvanceUntilCompletedAsync(SendOne(fixture)));

        exception.StatusCode.ShouldBe(200);
        exception.Message.ShouldStartWith("Inngest API error: 200 ");
        fixture.Handler.Requests.Count.ShouldBe(InngestClient.MaxAttempts);
    }

    [Fact]
    public async Task Shortens_the_text_of_a_proxy_error_page()
    {
        var fixture = new ClientFixture();
        fixture.Handler.Respond(HttpStatusCode.UnprocessableEntity, "<html>" + new string('x', 5000) + "</html>", "text/html");

        var exception = await Should.ThrowAsync<InngestException>(SendOne(fixture));

        exception.Message.Length.ShouldBeLessThan(400);
        exception.Message.ShouldEndWith("...");
    }

    [Fact]
    public async Task Retries_network_failures()
    {
        var fixture = new ClientFixture();
        fixture.Handler
            .Fail(new HttpRequestException("Connection refused"))
            .Fail(new HttpRequestException("Connection reset"))
            .Respond(HttpStatusCode.OK, ClientFixture.Accepted);

        await fixture.Time.AdvanceUntilCompletedAsync(SendOne(fixture));

        fixture.Handler.Requests.Count.ShouldBe(3);
    }

    [Fact]
    public async Task Reports_the_last_network_failure_with_its_cause()
    {
        var fixture = new ClientFixture();
        var failure = new HttpRequestException("Connection refused");
        fixture.Handler.RespondAlways(HttpStatusCode.OK);
        for (var i = 0; i < InngestClient.MaxAttempts; i++)
        {
            fixture.Handler.Fail(failure);
        }

        var exception = await Should.ThrowAsync<InngestException>(fixture.Time.AdvanceUntilCompletedAsync(SendOne(fixture)));

        exception.StatusCode.ShouldBeNull();
        exception.InnerException.ShouldBeSameAs(failure);
        exception.Message.ShouldContain("Connection refused");
        fixture.Handler.Requests.Count.ShouldBe(InngestClient.MaxAttempts);
    }

    [Fact]
    public async Task Retries_when_the_answer_is_cut_short()
    {
        var fixture = new ClientFixture();
        fixture.Handler.RespondWithTruncatedBody(HttpStatusCode.OK, """{"ids":["01HQ""").Respond(HttpStatusCode.OK, ClientFixture.Accepted);

        var result = await fixture.Time.AdvanceUntilCompletedAsync(SendOne(fixture));

        result.Ids.ShouldBe(["01HQ0000000000000000000001"]);
        fixture.Handler.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Retries_when_the_http_client_times_out()
    {
        var fixture = new ClientFixture();
        fixture.Handler.Fail(new TaskCanceledException("The request timed out.", new TimeoutException())).Respond(HttpStatusCode.OK, ClientFixture.Accepted);

        await fixture.Time.AdvanceUntilCompletedAsync(SendOne(fixture));

        fixture.Handler.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Reports_a_timeout_that_does_not_go_away()
    {
        var fixture = new ClientFixture();
        var timeout = new TaskCanceledException("The request timed out.", new TimeoutException());
        for (var i = 0; i < InngestClient.MaxAttempts; i++)
        {
            fixture.Handler.Fail(timeout);
        }

        var exception = await Should.ThrowAsync<InngestException>(fixture.Time.AdvanceUntilCompletedAsync(SendOne(fixture)));

        exception.Message.ShouldContain("timed out");
        exception.InnerException.ShouldBeSameAs(timeout);
    }

    [Fact]
    public async Task Does_not_report_a_send_cancelled_by_the_caller_as_a_timeout()
    {
        var fixture = new ClientFixture();
        using var cancellation = new CancellationTokenSource();
        fixture.Handler.Hang();

        var send = SendOne(fixture, cancellation.Token);
        await WaitForRequestsAsync(fixture, 1);
        await cancellation.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(send);
        fixture.Handler.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Stops_retrying_when_cancelled_during_the_backoff()
    {
        var fixture = new ClientFixture();
        using var cancellation = new CancellationTokenSource();
        fixture.Handler.RespondAlways(HttpStatusCode.InternalServerError, ServerError);

        var send = SendOne(fixture, cancellation.Token);
        await WaitForRequestsAsync(fixture, 1);
        await cancellation.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(send);
        fixture.Handler.Requests.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData(1, 0.0, 100)]
    [InlineData(2, 0.0, 200)]
    [InlineData(3, 0.0, 400)]
    [InlineData(4, 0.0, 800)]
    [InlineData(1, 0.5, 150)]
    [InlineData(3, 0.25, 425)]
    public void The_backoff_doubles_from_100_milliseconds_and_adds_up_to_100_milliseconds_of_jitter(int failedAttempts, double jitter, double expectedMilliseconds) =>
        InngestClient.GetBackoffDelay(failedAttempts, jitter).TotalMilliseconds.ShouldBe(expectedMilliseconds);

    [Fact]
    public void The_jitter_stays_below_the_base_delay()
    {
        InngestClient.GetBackoffDelay(1, 0.999999).TotalMilliseconds.ShouldBeLessThan(200);
        InngestClient.GetBackoffDelay(4, 0.999999).TotalMilliseconds.ShouldBeLessThan(900);
    }

    private static async Task WaitForRequestsAsync(ClientFixture fixture, int count)
    {
        using var giveUp = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (fixture.Handler.Requests.Count < count)
        {
            await Task.Delay(1, giveUp.Token);
        }
    }
}
