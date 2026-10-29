using System.Net.WebSockets;
using Google.Protobuf.WellKnownTypes;
using Inngest.Connect.Protocol;
using Inngest.Internal;
using Inngest.Signing;
using Inngest.Tests.Connect.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Inngest.Tests.Connect;

public sealed class HandshakeTests : IAsyncLifetime
{
    private FakeGateway _gateway = null!;

    public async ValueTask InitializeAsync() => _gateway = await FakeGateway.StartAsync();

    public async ValueTask DisposeAsync() => await _gateway.DisposeAsync();

    [Fact]
    public async Task Asks_for_a_gateway_with_an_empty_protobuf_start_request()
    {
        await using var worker = new WorkerHost(_gateway);

        await worker.StartAsync();
        await _gateway.NextReadySessionAsync();

        var start = _gateway.Starts.ShouldHaveSingleItem();
        start.ContentType.ShouldBe("application/protobuf");
        start.BodyLength.ShouldBe(0);
        start.Request.ExcludeGateways.ShouldBeEmpty();
        start.Sdk.ShouldBe(SdkInfo.Header);

        // The dev server needs no key, and there is none to send.
        start.Authorization.ShouldBeEmpty();
        start.Environment.ShouldBeEmpty();
    }

    [Fact]
    public async Task Opens_the_websocket_with_the_connect_subprotocol_and_reads_the_empty_hello()
    {
        await using var worker = new WorkerHost(_gateway);

        await worker.StartAsync();
        var session = await _gateway.NextReadySessionAsync();

        session.RequestedProtocols.ShouldBe(["v0.connect.inngest.com"]);
        session.AcceptedProtocol.ShouldBe("v0.connect.inngest.com");
        FrameTestExtensions.Encode(GatewayMessageType.GatewayHello).ShouldBeEmpty();

        // WORKER_CONNECT answers the hello, and WORKER_READY follows GATEWAY_CONNECTION_READY.
        session.Received.Select(message => message.Kind).ShouldBe([GatewayMessageType.WorkerConnect, GatewayMessageType.WorkerReady]);
        session.TextMessages.ShouldBe(0);
    }

    [Fact]
    public async Task Describes_the_worker_and_its_functions_in_worker_connect()
    {
        await using var worker = new WorkerHost(
            _gateway,
            inngest: options => options.AppVersion = "1.4.0",
            connect: options => options.MaxWorkerConcurrency = 8);

        await worker.StartAsync();
        var hello = (await _gateway.NextReadySessionAsync()).Hello;

        hello.ConnectionId.ShouldBe("01JCONNECTION0000000000001");
        hello.InstanceId.ShouldBe(WorkerHost.InstanceId);
        hello.AuthData.SessionToken.ShouldBe("session-1");
        hello.AuthData.SyncToken.ShouldBe("sync-1");
        hello.Capabilities.ToStringUtf8().ShouldBe("""{"trust_probe":"v1","connect":"v1"}""");
        hello.WorkerManualReadinessAck.ShouldBeTrue();
        hello.Framework.ShouldBe("connect");
        hello.SdkLanguage.ShouldBe("dotnet");
        hello.SdkVersion.ShouldBe($"v{SdkInfo.Version}");
        hello.StartedAt.ShouldBe(Timestamp.FromDateTimeOffset(WorkerHost.Now));
        hello.HasMaxWorkerConcurrency.ShouldBeTrue();
        hello.MaxWorkerConcurrency.ShouldBe(8);
        hello.HasEnvironment.ShouldBeFalse();
        hello.HasPlatform.ShouldBeFalse();
        hello.SystemAttributes.CpuCores.ShouldBe(Environment.ProcessorCount);
        hello.SystemAttributes.MemBytes.ShouldBeGreaterThan(0);
        hello.SystemAttributes.Os.ShouldNotBeNullOrWhiteSpace();

        var app = hello.Apps.ShouldHaveSingleItem();
        app.AppName.ShouldBe(WorkerHost.AppId);
        app.AppVersion.ShouldBe("1.4.0");
        app.FeatureObservations.ShouldBeNull();
        app.Functions.ToStringUtf8().ShouldBe(
            """[{"id":"shop-orders","name":"orders","triggers":[{"event":"order/created"}],"steps":{"step":{"id":"step","name":"step","runtime":{"type":"ws","url":"wss://connect/?fnId=shop-orders&stepId=step"}}}}]""");
    }

    [Fact]
    public async Task Leaves_out_what_is_not_configured()
    {
        await using var worker = new WorkerHost(_gateway);

        await worker.StartAsync();
        var hello = (await _gateway.NextReadySessionAsync()).Hello;

        hello.HasMaxWorkerConcurrency.ShouldBeFalse();
        hello.Apps[0].HasAppVersion.ShouldBeFalse();
    }

    [Fact]
    public async Task Authenticates_with_the_hashed_signing_key_and_names_the_environment_in_cloud_mode()
    {
        const string Key = "signkey-prod-0123456789abcdef";
        await using var worker = new WorkerHost(_gateway, inngest: options =>
        {
            options.IsDev = false;
            options.SigningKey = Key;
            options.Environment = "feature-x";
            options.ApiBaseUrl = _gateway.BaseUrl;
        });

        await worker.StartAsync();
        var hello = (await _gateway.NextReadySessionAsync()).Hello;

        var start = _gateway.Starts.ShouldHaveSingleItem();
        start.Authorization.ShouldBe($"Bearer {SigningKey.Create(Key)!.BearerToken}");
        start.Authorization.ShouldNotContain("0123456789abcdef");
        start.Environment.ShouldBe("feature-x");
        hello.Environment.ShouldBe("feature-x");
    }

    [Fact]
    public async Task Dials_the_configured_gateway_instead_of_the_one_inngest_assigns()
    {
        _gateway.AdvertisedEndpoint = "ws://127.0.0.1:9/v0/connect";
        await using var worker = new WorkerHost(_gateway, connect: options => options.GatewayUrl = _gateway.GatewayUrl);

        await worker.StartAsync();

        (await _gateway.NextReadySessionAsync()).Hello.AuthData.SessionToken.ShouldBe("session-1");
    }

    [Fact]
    public async Task Reports_a_failed_sync_and_tries_again()
    {
        var attempts = 0;
        _gateway.OnWorkerConnect = async session =>
        {
            if (Interlocked.Increment(ref attempts) > 1)
            {
                return true;
            }

            await session.SendAsync(GatewayMessageType.SyncFailed, new SystemError { Code = "invalid_function", Message = "Function \"orders\" has no triggers" });
            await session.CloseAsync(WebSocketCloseStatus.PolicyViolation, "invalid_function");
            return false;
        };
        await using var worker = new WorkerHost(_gateway);

        await worker.StartAsync();
        await worker.AdvanceUntilAsync(() => _gateway.Starts.Count == 2, TimeSpan.FromMilliseconds(500));
        await _gateway.NextReadySessionAsync();

        var error = worker.LogsWithId(505).ShouldHaveSingleItem();
        error.Message.ShouldContain("invalid_function");
        error.Message.ShouldContain("Function \"orders\" has no triggers");
    }

    [Fact]
    public async Task Gives_up_on_a_gateway_that_does_not_start_with_hello_and_closes_it_as_unexpected()
    {
        var attempts = 0;
        _gateway.FirstMessage = () => Interlocked.Increment(ref attempts) == 1 ? GatewayMessageType.GatewayHeartbeat : GatewayMessageType.GatewayHello;
        await using var worker = new WorkerHost(_gateway);

        await worker.StartAsync();
        await worker.AdvanceUntilAsync(() => _gateway.Starts.Count == 2, TimeSpan.FromMilliseconds(500));
        await _gateway.NextReadySessionAsync();

        var first = _gateway.Sessions[0];
        await first.Closed.WaitAsync(Waits.Timeout);
        first.CloseStatus.ShouldBe((WebSocketCloseStatus)4001);
        first.CloseDescription.ShouldBe("UNEXPECTED");
        _gateway.Starts[1].Request.ExcludeGateways.ShouldBe([FakeGateway.GatewayGroup]);
    }

    [Fact]
    public async Task Fails_at_startup_in_cloud_mode_without_a_signing_key()
    {
        await using var worker = new WorkerHost(_gateway, inngest: options => options.IsDev = false);

        Should.Throw<InvalidOperationException>(() => worker.Worker).Message.ShouldContain("INNGEST_SIGNING_KEY");
    }

    [Fact]
    public async Task Fails_at_startup_with_a_branch_key_but_no_environment()
    {
        await using var worker = new WorkerHost(_gateway, inngest: options =>
        {
            options.IsDev = false;
            options.SigningKey = "signkey-branch-0123456789abcdef";
        });

        Should.Throw<InvalidOperationException>(() => worker.Worker).Message.ShouldContain("INNGEST_ENV");
    }

    [Fact]
    public async Task Rejects_an_invalid_option_when_the_worker_is_created()
    {
        await using var worker = new WorkerHost(_gateway, connect: options => options.MaxWorkerConcurrency = 0);

        Should.Throw<InvalidOperationException>(() => worker.Worker).Message.ShouldContain("MaxWorkerConcurrency");
    }

    [Fact]
    public void AddConnect_registers_one_worker_however_often_it_is_called()
    {
        var services = new ServiceCollection();

        services.AddInngest(options => options.AppId = "shop").AddConnect().AddConnect();

        services.Count(descriptor => descriptor.ServiceType == typeof(IHostedService)).ShouldBe(1);
    }
}
