using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Inngest.Execution;
using Inngest.Functions;
using Inngest.Tests.Execution;

namespace Inngest.Tests.Checkpointing;

// Functions with checkpointing turned on, and calls shaped like the ones Inngest makes for them.
internal static class Checkpointed
{
    // What Call sends as ctx.fn_id, and the queue item a real call carries next to it.
    public const string FunctionUuid = "0b3c7d2a-uuid";
    public const string QueueItemId = "cWktMTo6ZGVmYXVsdA==";
    public const string RequestId = "01JREQUEST";
    public const int GenerationId = 3;

    public static FunctionDefinition Define(Func<FunctionContext<JsonElement>, Task<object?>> handler, CheckpointingOptions? options = null) =>
        new(
            new FunctionOptions { Id = "fn", Triggers = [Trigger.Event("test/event")], Checkpointing = options ?? new CheckpointingOptions() },
            _ => new DelegateFunction<JsonElement>((context, _) => handler(context)));

    // The body of Call with the ctx.qi_id that Inngest sends along with ctx.fn_id.
    public static byte[] Body(Call call, string? queueItemId = QueueItemId)
    {
        var body = JsonNode.Parse(call.ToBytes())!;
        if (queueItemId is not null)
        {
            body["ctx"]!["qi_id"] = queueItemId;
        }

        return Encoding.UTF8.GetBytes(body.ToJsonString());
    }

    public static ExecutionRequest Request(EngineHost host, FunctionDefinition function, Call call, string stepId = "step") =>
        new()
        {
            Function = function,
            Body = Body(call),
            StepId = stepId,
            Services = host.Services,
            RequestId = RequestId,
            GenerationId = GenerationId,
        };

    public static Task<ExecutionResult> Start(this EngineHost host, ExecutionRequest request) =>
        host.Engine.ExecuteAsync(request, TestContext.Current.CancellationToken);

    public static Task<CallResult> CallCheckpointedAsync(this EngineHost host, FunctionDefinition function, Call call) =>
        EngineHost.ResultOf(host.Start(Request(host, function, call)));

    public static string StepRun(string id, string data, long durationNanoseconds = 0) =>
        $$$"""{"id":"{{{Call.Hash(id)}}}","op":"StepRun","name":"{{{id}}}","displayName":"{{{id}}}","opts":{"type":"step.run"},"userland":{"id":"{{{id}}}"},"data":{{{data}}},"timing":{"a":{{{EngineHost.NowNanoseconds}}},"b":{{{durationNanoseconds}}}}}""";
}
