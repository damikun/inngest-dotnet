using System.Net;
using System.Text;
using Inngest.Client;
using Inngest.Configuration;
using Inngest.Tests.Client;
using Inngest.Tests.Connect.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;

namespace Inngest.Tests.Checkpointing;

// Checkpoint requests name the app instance in X-Inngest-Instance-Id, as inngest-js does since inngest-js#1878: the
// Connect worker's id once it starts, or else INNGEST_CONNECT_INSTANCE_ID.
public sealed class CheckpointInstanceIdTests
{
    private const string Steps = """[{"id":"6dcd4ce23d88e2ee9568ba546c007c63d9131c1b","op":"StepRun","data":"A"}]""";
    private const string Created = """{"data":{"fn_id":"a03aad99-d147","app_id":"b8b8bfc9-96e8","run_id":"01JRUN","token":"eyJ.token"}}""";

    private static readonly DateTimeOffset Now = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);

    private readonly FakeHttpHandler _handler = new();
    private readonly FakeTimeProvider _time = new(Now);

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Without_an_instance_id_no_header_is_sent()
    {
        var (client, _) = Create(TestSettings.Create());

        await SendAllAsync(client);

        _handler.Requests.Count.ShouldBe(3);
        _handler.Requests.ShouldAllBe(request => request.Header("X-Inngest-Instance-Id") == null);
    }

    [Fact]
    public async Task Every_checkpoint_names_the_instance_from_the_variable()
    {
        var (client, _) = Create(TestSettings.Create(variables: new Dictionary<string, string?> { ["INNGEST_CONNECT_INSTANCE_ID"] = " pod-7 " }));

        await SendAllAsync(client);

        _handler.Requests.Select(request => request.Uri.AbsolutePath).ShouldBe(["/v1/checkpoint/01JRUN/async", "/v1/checkpoint", "/v1/checkpoint/01JRUN/steps"]);
        _handler.Requests.ShouldAllBe(request => request.Header("X-Inngest-Instance-Id") == "pod-7");
    }

    [Fact]
    public async Task The_connect_worker_id_wins_over_the_variable()
    {
        var (client, instance) = Create(TestSettings.Create(variables: new Dictionary<string, string?> { ["INNGEST_CONNECT_INSTANCE_ID"] = "pod-7" }));
        instance.UseConnectInstanceId("worker-1");

        await SendAllAsync(client);

        _handler.Requests.ShouldAllBe(request => request.Header("X-Inngest-Instance-Id") == "worker-1");
    }

    [Fact]
    public async Task Other_api_requests_carry_no_instance_id()
    {
        var settings = TestSettings.Create(variables: new Dictionary<string, string?> { ["INNGEST_CONNECT_INSTANCE_ID"] = "pod-7" });
        _handler.Respond(HttpStatusCode.OK, "{}");
        var api = new InngestApiClient(new FakeHttpClientFactory(_handler), settings, new FakeLogger<InngestApiClient>());

        (await api.GetRunStepsAsync("01JRUN", CancellationToken)).Dispose();

        _handler.Single.Header("X-Inngest-Instance-Id").ShouldBeNull();
    }

    [Fact]
    public async Task A_connect_worker_hands_its_id_to_the_checkpoints_when_it_starts()
    {
        await using var gateway = await FakeGateway.StartAsync();
        await using var worker = new WorkerHost(gateway);
        var instance = worker.Services.GetRequiredService<CheckpointInstance>();
        instance.Id.ShouldBeNull();

        await worker.StartAsync();
        await gateway.NextReadySessionAsync();

        instance.Id.ShouldBe(WorkerHost.InstanceId);
    }

    private (CheckpointApiClient Client, CheckpointInstance Instance) Create(InngestSettings settings)
    {
        var instance = new CheckpointInstance(settings);
        return (new CheckpointApiClient(new FakeHttpClientFactory(_handler), settings, _time, new FakeLogger<CheckpointApiClient>(), instance), instance);
    }

    // The three checkpoints inngest-js sends the header with: an async checkpoint, and the creation and extension of a
    // durable endpoint's run.
    private async Task SendAllAsync(CheckpointApiClient client)
    {
        _handler.Respond(HttpStatusCode.OK).Respond(HttpStatusCode.OK, Created).Respond(HttpStatusCode.OK);
        var steps = Encoding.UTF8.GetBytes(Steps);
        await client.CheckpointAsync(new AsyncCheckpoint("01JRUN", "0b3c7d2a-uuid", "cWktMQ==", null, null, Now, steps), CancellationToken);
        var run = await client.CheckpointNewRunAsync(new NewEndpointRun("01JRUN", "{}"u8.ToArray(), steps, Retries: 3), CancellationToken);
        await client.CheckpointEndpointStepsAsync(run, steps, CancellationToken);
    }
}
