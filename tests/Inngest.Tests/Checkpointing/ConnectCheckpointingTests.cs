using Inngest.Connect.Protocol;
using Inngest.Execution;
using Inngest.Functions;
using Inngest.Tests.Connect.Support;

namespace Inngest.Tests.Checkpointing;

// A Connect worker has no HTTP request timeout to stay under, so checkpointing runs steps longer there by default.
public sealed class ConnectCheckpointingTests : IAsyncLifetime
{
    private FakeGateway _gateway = null!;

    public async ValueTask InitializeAsync() => _gateway = await FakeGateway.StartAsync();

    public async ValueTask DisposeAsync() => await _gateway.DisposeAsync();

    [Fact]
    public async Task A_Connect_call_lets_checkpointing_run_steps_for_five_minutes()
    {
        var engine = new ControlledEngine();
        await using var worker = new WorkerHost(_gateway, engine: engine);
        await worker.StartAsync();
        var session = await _gateway.NextReadySessionAsync();

        await session.SendAsync(GatewayMessageType.GatewayExecutorRequest, GatewayMessages.Request("req-1"));

        var execution = await engine.NextAsync();
        execution.Request.DefaultCheckpointMaxRuntime.ShouldBe(Checkpointer.ConnectMaxRuntime);
        execution.Complete(200, "null");
        await session.NextAsync(GatewayMessageType.WorkerReply);
    }

    [Fact]
    public void A_call_over_HTTP_gets_ten_seconds()
    {
        var request = new ExecutionRequest
        {
            Function = new FunctionDefinition(WorkerHost.Orders, _ => throw new InvalidOperationException()),
            Body = ReadOnlyMemory<byte>.Empty,
            Services = new ServiceProviderStub(),
        };

        request.DefaultCheckpointMaxRuntime.ShouldBe(Checkpointer.ServeMaxRuntime);
    }

    private sealed class ServiceProviderStub : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
