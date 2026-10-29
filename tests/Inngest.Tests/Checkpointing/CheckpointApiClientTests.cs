using System.Net;
using System.Text;
using Inngest.Client;
using Inngest.Signing;
using Inngest.Tests.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;

namespace Inngest.Tests.Checkpointing;

public sealed class CheckpointApiClientTests
{
    private const string Key = "signkey-prod-0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string Steps = """[{"id":"6dcd4ce23d88e2ee9568ba546c007c63d9131c1b","op":"StepRun","data":"A"}]""";

    private static readonly DateTimeOffset StartedAt = new(2026, 10, 29, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = StartedAt.AddMilliseconds(1500);

    private readonly FakeHttpHandler _handler = new();
    private readonly FakeTimeProvider _time = new(Now);
    private readonly FakeLogger<CheckpointApiClient> _logger = new();

    private static AsyncCheckpoint Checkpoint => new("01JRUN", "0b3c7d2a-uuid", "cWktMQ==", "01JREQUEST", 3, StartedAt, Encoding.UTF8.GetBytes(Steps));

    [Fact]
    public async Task Posts_the_steps_and_the_call_that_ran_them_to_the_async_checkpoint_endpoint()
    {
        _handler.Respond(HttpStatusCode.OK);

        await Create().CheckpointAsync(Checkpoint, TestContext.Current.CancellationToken);

        var request = _handler.Single;
        request.Method.ShouldBe(HttpMethod.Post);
        request.Uri.ShouldBe(new Uri("https://api.inngest.com/v1/checkpoint/01JRUN/async"));
        request.Header("Authorization").ShouldBe("Bearer " + SigningKey.Create(Key)!.BearerToken);
        request.Header("Content-Type").ShouldBe("application/json");
        request.Header("X-Inngest-SDK").ShouldStartWith("inngest-dotnet:v");
        request.BodyText.ShouldBe(
            """{"run_id":"01JRUN","fn_id":"0b3c7d2a-uuid","qi_id":"cWktMQ==","request_id":"01JREQUEST","generation_id":3,"request_started_at":1793268000000,"steps":"""
            + Steps + ""","ts":1793268001500}""");
    }

    [Fact]
    public async Task Ids_Inngest_did_not_send_are_left_out()
    {
        _handler.Respond(HttpStatusCode.OK);

        await Create().CheckpointAsync(Checkpoint with { RequestId = null, GenerationId = null }, TestContext.Current.CancellationToken);

        _handler.Single.BodyText.ShouldBe(
            """{"run_id":"01JRUN","fn_id":"0b3c7d2a-uuid","qi_id":"cWktMQ==","request_started_at":1793268000000,"steps":""" + Steps + ""","ts":1793268001500}""");
    }

    [Fact]
    public async Task Goes_to_the_dev_server_in_dev_mode_and_escapes_the_run_id()
    {
        _handler.Respond(HttpStatusCode.OK);
        var client = new CheckpointApiClient(new FakeHttpClientFactory(_handler), TestSettings.Create(), _time, _logger);

        await client.CheckpointAsync(Checkpoint with { RunId = "01J RUN" }, TestContext.Current.CancellationToken);

        _handler.Single.Uri.AbsoluteUri.ShouldBe("http://localhost:8288/v1/checkpoint/01J%20RUN/async");
        _handler.Single.Header("Authorization").ShouldBeNull();
    }

    [Fact]
    public async Task Retries_server_errors_and_lost_connections_with_backoff_and_a_fresh_timestamp()
    {
        _handler
            .Respond(HttpStatusCode.InternalServerError, """{"error":"state store unavailable"}""")
            .Fail(new HttpRequestException("connection reset"))
            .Respond(HttpStatusCode.OK);

        await _time.AdvanceUntilCompletedAsync(Create().CheckpointAsync(Checkpoint, TestContext.Current.CancellationToken));

        _handler.Requests.Count.ShouldBe(3);
        _handler.Requests.Select(request => request.BodyText).Distinct().Count().ShouldBe(3);
        var retries = _logger.Collector.GetSnapshot().Where(record => record.Id.Id == 340).ToList();
        retries.Count.ShouldBe(2);
        retries[0].Level.ShouldBe(LogLevel.Warning);
        retries[0].Message.ShouldStartWith("Checkpointing run 01JRUN failed on attempt 1 of 5: Inngest answered 500 to the checkpoint of run 01JRUN: state store unavailable Retrying in ");
    }

    [Fact]
    public async Task Gives_up_after_five_attempts()
    {
        _handler.RespondAlways(HttpStatusCode.BadGateway, "<html>bad gateway</html>", "text/html");

        var exception = await Should.ThrowAsync<InngestException>(
            _time.AdvanceUntilCompletedAsync(Create().CheckpointAsync(Checkpoint, TestContext.Current.CancellationToken)));

        exception.StatusCode.ShouldBe(502);
        exception.Message.ShouldBe("Inngest answered 502 to the checkpoint of run 01JRUN: <html>bad gateway</html>");
        _handler.Requests.Count.ShouldBe(CheckpointApiClient.MaxAttempts);
    }

    [Fact]
    public async Task A_conflict_means_the_run_was_dispatched_again_and_is_not_retried()
    {
        _handler.Respond(HttpStatusCode.Conflict, """{"error":"stale dispatch","status":409}""");

        var exception = await Should.ThrowAsync<InngestException>(Create().CheckpointAsync(Checkpoint, TestContext.Current.CancellationToken));

        exception.StatusCode.ShouldBe(409);
        exception.Message.ShouldBe("Inngest refused the checkpoint of run 01JRUN because the run was dispatched again: stale dispatch");
        _handler.Requests.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge)]
    public async Task Other_client_errors_fail_at_once(HttpStatusCode status)
    {
        _handler.Respond(status, """{"error":"run not found in state store","status":400}""");

        var exception = await Should.ThrowAsync<InngestException>(Create().CheckpointAsync(Checkpoint, TestContext.Current.CancellationToken));

        exception.StatusCode.ShouldBe((int)status);
        _handler.Requests.Count.ShouldBe(1);
    }

    private CheckpointApiClient Create() =>
        new(new FakeHttpClientFactory(_handler), TestSettings.Cloud(options => options.SigningKey = Key), _time, _logger);
}
