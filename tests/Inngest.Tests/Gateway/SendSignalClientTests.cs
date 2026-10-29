using System.Net;
using Inngest.Signing;
using Inngest.Tests.Client;
using Microsoft.Extensions.Logging;

namespace Inngest.Tests.Gateway;

public sealed class SendSignalClientTests
{
    private const string Key = "signkey-prod-0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string Resumed = """{"data":{"run_id":"01JRESUMED"},"metadata":{"fetched_at":"2026-10-29T10:00:00Z"}}""";

    [Fact]
    public async Task Posts_the_signal_and_its_data_and_returns_the_resumed_run()
    {
        var fixture = new ClientFixture();
        fixture.Handler.Respond(HttpStatusCode.OK, Resumed);

        var runId = await fixture.Client.SendSignalAsync(new InngestSignal("approval/order-1", new { Approver = "alice" }), TestContext.Current.CancellationToken);

        runId.ShouldBe("01JRESUMED");
        var request = fixture.Handler.Single;
        request.Method.ShouldBe(HttpMethod.Post);
        request.Uri.ShouldBe(new Uri("http://localhost:8288/v1/signals"));
        request.Header("Content-Type").ShouldBe("application/json");
        request.BodyText.ShouldBe("""{"signal":"approval/order-1","data":{"approver":"alice"}}""");
        fixture.Factory.RequestedNames.ShouldBe(["Inngest"]);
        fixture.Logger.Collector.GetSnapshot().ShouldContain(record => record.Id.Id == 330 && record.Level == LogLevel.Debug);
    }

    [Fact]
    public async Task Leaves_out_data_the_signal_does_not_have()
    {
        var fixture = new ClientFixture();
        fixture.Handler.Respond(HttpStatusCode.OK, Resumed);

        await fixture.Client.SendSignalAsync(new InngestSignal("approval/order-1"), TestContext.Current.CancellationToken);

        fixture.Handler.Single.BodyText.ShouldBe("""{"signal":"approval/order-1"}""");
    }

    [Fact]
    public async Task Data_can_be_any_json_value()
    {
        var fixture = new ClientFixture();
        fixture.Handler.Respond(HttpStatusCode.OK, Resumed);

        await fixture.Client.SendSignalAsync(new InngestSignal("approval/order-1", 42), TestContext.Current.CancellationToken);

        fixture.Handler.Single.BodyText.ShouldBe("""{"signal":"approval/order-1","data":42}""");
    }

    // Inngest answers 404 when no run waits for the signal, which is an outcome rather than a failure: the wait may
    // have timed out, been replaced or not started yet.
    [Fact]
    public async Task Returns_null_when_no_run_waits_for_the_signal()
    {
        var fixture = new ClientFixture();
        fixture.Handler.Respond(HttpStatusCode.NotFound, """{"error":"No signal found","data":null,"status":404}""");

        var runId = await fixture.Client.SendSignalAsync(new InngestSignal("approval/order-1"), TestContext.Current.CancellationToken);

        runId.ShouldBeNull();
        fixture.Logger.Collector.GetSnapshot().ShouldContain(record => record.Id.Id == 331);
    }

    [Fact]
    public async Task Fails_with_inngests_reason_and_does_not_retry()
    {
        var fixture = new ClientFixture();
        fixture.Handler.Respond(HttpStatusCode.InternalServerError, """{"error":"Failed to receive signal","status":500}""");

        var exception = await Should.ThrowAsync<InngestException>(
            () => fixture.Client.SendSignalAsync(new InngestSignal("approval/order-1"), TestContext.Current.CancellationToken));

        exception.Message.ShouldBe("Failed to send signal \"approval/order-1\": Failed to receive signal");
        exception.StatusCode.ShouldBe(500);
        fixture.Handler.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Fails_when_inngest_accepts_the_signal_without_naming_the_run()
    {
        var fixture = new ClientFixture();
        fixture.Handler.Respond(HttpStatusCode.OK, """{"data":{}}""");

        var exception = await Should.ThrowAsync<InngestException>(
            () => fixture.Client.SendSignalAsync(new InngestSignal("approval/order-1"), TestContext.Current.CancellationToken));

        exception.Message.ShouldStartWith("Inngest accepted signal \"approval/order-1\" but did not say which run it resumed");
    }

    [Fact]
    public async Task Fails_when_inngest_cannot_be_reached()
    {
        var fixture = new ClientFixture();
        fixture.Handler.Fail(new HttpRequestException("Connection refused"));

        var exception = await Should.ThrowAsync<InngestException>(
            () => fixture.Client.SendSignalAsync(new InngestSignal("approval/order-1"), TestContext.Current.CancellationToken));

        exception.Message.ShouldBe("Failed to send signal \"approval/order-1\": Connection refused");
        exception.InnerException.ShouldBeOfType<HttpRequestException>();
    }

    [Fact]
    public async Task Authenticates_with_the_hashed_signing_key_and_sends_the_app_environment()
    {
        var fixture = new ClientFixture(TestSettings.Cloud(options =>
        {
            options.SigningKey = Key;
            options.Environment = "branch-1";
        }));
        fixture.Handler.Respond(HttpStatusCode.OK, Resumed);

        await fixture.Client.SendSignalAsync(new InngestSignal("approval/order-1"), TestContext.Current.CancellationToken);

        var request = fixture.Handler.Single;
        request.Uri.ShouldBe(new Uri("https://api.inngest.com/v1/signals"));
        request.Header("Authorization").ShouldBe("Bearer " + SigningKey.Create(Key)!.BearerToken);
        request.Header("X-Inngest-Env").ShouldBe("branch-1");
    }

    [Fact]
    public async Task Options_send_the_signal_to_another_environment()
    {
        var fixture = new ClientFixture(TestSettings.Cloud(options =>
        {
            options.SigningKey = Key;
            options.Environment = "branch-1";
        }));
        fixture.Handler.Respond(HttpStatusCode.OK, Resumed).Respond(HttpStatusCode.OK, Resumed);

        await fixture.Client.SendSignalAsync(new InngestSignal("approval/order-1"), new SendOptions { Environment = "branch-2" }, TestContext.Current.CancellationToken);
        await fixture.Client.SendSignalAsync(new InngestSignal("approval/order-1"), new SendOptions(), TestContext.Current.CancellationToken);

        fixture.Handler.Requests[0].Header("X-Inngest-Env").ShouldBe("branch-2");
        fixture.Handler.Requests[1].Header("X-Inngest-Env").ShouldBe("branch-1");
    }

    [Fact]
    public async Task Checks_its_arguments()
    {
        var client = new ClientFixture().Client;

        await Should.ThrowAsync<ArgumentNullException>(() => client.SendSignalAsync(null!, TestContext.Current.CancellationToken));
        await Should.ThrowAsync<ArgumentException>(() => client.SendSignalAsync(new InngestSignal(""), TestContext.Current.CancellationToken));
        await Should.ThrowAsync<ArgumentNullException>(() => client.SendSignalAsync(new InngestSignal("s"), null!, TestContext.Current.CancellationToken));
    }
}
