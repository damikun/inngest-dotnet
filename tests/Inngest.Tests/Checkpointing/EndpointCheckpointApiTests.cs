using System.Net;
using System.Text;
using Inngest.Client;
using Inngest.Signing;
using Inngest.Tests.Client;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;

namespace Inngest.Tests.Checkpointing;

// The calls that create and extend the runs of durable endpoint requests, in the shape the spike against the dev
// server settled: the event's body in base64, request_version 2 and the retries.
public sealed class EndpointCheckpointApiTests
{
    private const string Key = "signkey-prod-0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string Event = """{"name":"inngest/http.request","data":{"domain":"https://api.example.com","method":"POST","path":"/orders","body":"e30="}}""";
    private const string Steps = """[{"id":"6dcd4ce23d88e2ee9568ba546c007c63d9131c1b","op":"StepRun","data":"A"}]""";
    private const string Created = """{"data":{"fn_id":"a03aad99-d147","app_id":"b8b8bfc9-96e8","run_id":"01JRUN","token":"eyJ.token","realtime_token":"eyJ.rt"}}""";

    private static readonly DateTimeOffset Now = new(2026, 10, 30, 21, 0, 0, TimeSpan.Zero);

    private readonly FakeHttpHandler _handler = new();
    private readonly FakeTimeProvider _time = new(Now);
    private readonly FakeLogger<CheckpointApiClient> _logger = new();

    private static NewEndpointRun NewRun => new("01JRUN", Encoding.UTF8.GetBytes(Event), Encoding.UTF8.GetBytes(Steps), Retries: 3);

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Creates_the_run_with_the_request_event_the_steps_and_the_retries()
    {
        _handler.Respond(HttpStatusCode.OK, Created);

        var run = await Create().CheckpointNewRunAsync(NewRun, CancellationToken);

        run.ShouldBe(new EndpointRun("01JRUN", "a03aad99-d147", "b8b8bfc9-96e8", "eyJ.token"));
        var request = _handler.Single;
        request.Method.ShouldBe(HttpMethod.Post);
        request.Uri.ShouldBe(new Uri("https://api.inngest.com/v1/checkpoint"));
        request.Header("Authorization").ShouldBe("Bearer " + SigningKey.Create(Key)!.BearerToken);
        request.BodyText.ShouldBe(
            """{"run_id":"01JRUN","event":""" + Event + ""","steps":""" + Steps + ""","ts":1793394000000,"request_version":2,"retries":3}""");
    }

    [Fact]
    public async Task A_run_without_steps_yet_sends_an_empty_list()
    {
        _handler.Respond(HttpStatusCode.OK, Created);

        await Create().CheckpointNewRunAsync(NewRun with { Steps = ReadOnlyMemory<byte>.Empty }, CancellationToken);

        _handler.Single.BodyText.ShouldContain(""","steps":[],""");
    }

    [Fact]
    public async Task Adds_steps_to_the_run_under_its_function_and_app()
    {
        _handler.Respond(HttpStatusCode.OK);
        var run = new EndpointRun("01J RUN", "a03aad99-d147", "b8b8bfc9-96e8", "eyJ.token");

        await Create().CheckpointEndpointStepsAsync(run, Encoding.UTF8.GetBytes(Steps), CancellationToken);

        var request = _handler.Single;
        request.Uri.AbsoluteUri.ShouldBe("https://api.inngest.com/v1/checkpoint/01J%20RUN/steps");
        request.BodyText.ShouldBe(
            """{"fn_id":"a03aad99-d147","app_id":"b8b8bfc9-96e8","run_id":"01J RUN","steps":""" + Steps + ""","ts":1793394000000}""");
    }

    [Fact]
    public async Task Retries_a_server_error_when_creating_the_run()
    {
        _handler
            .Respond(HttpStatusCode.ServiceUnavailable, """{"error":"try later"}""")
            .Respond(HttpStatusCode.OK, Created);

        var run = await _time.AdvanceUntilCompletedAsync(Create().CheckpointNewRunAsync(NewRun, CancellationToken));

        run.Token.ShouldBe("eyJ.token");
        _handler.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_run_that_exists_already_is_a_conflict_that_is_not_retried()
    {
        _handler.Respond(HttpStatusCode.Conflict, """{"error":"Run already exists","status":409}""");

        var exception = await Should.ThrowAsync<InngestException>(Create().CheckpointNewRunAsync(NewRun, CancellationToken));

        exception.StatusCode.ShouldBe(409);
        exception.Message.ShouldBe("Inngest refused the checkpoint of run 01JRUN because the run already exists: Run already exists");
        _handler.Requests.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData("")]
    [InlineData("""{"data":{"run_id":"01JRUN"}}""")]
    [InlineData("""{"error":null}""")]
    public async Task A_reply_without_the_ids_of_the_run_fails(string reply)
    {
        _handler.Respond(HttpStatusCode.OK, reply);

        var exception = await Should.ThrowAsync<InngestException>(Create().CheckpointNewRunAsync(NewRun, CancellationToken));

        exception.Message.ShouldStartWith("Inngest created run 01JRUN but its reply did not say which function and app it belongs to");
    }

    [Fact]
    public async Task A_reply_without_a_token_still_gives_the_run()
    {
        _handler.Respond(HttpStatusCode.OK, """{"data":{"fn_id":"f","app_id":"a","run_id":"01JRUN"}}""");

        var run = await Create().CheckpointNewRunAsync(NewRun, CancellationToken);

        run.ShouldBe(new EndpointRun("01JRUN", "f", "a", Token: null));
    }

    private CheckpointApiClient Create() =>
        new(new FakeHttpClientFactory(_handler), TestSettings.Cloud(options => options.SigningKey = Key), _time, _logger);
}
