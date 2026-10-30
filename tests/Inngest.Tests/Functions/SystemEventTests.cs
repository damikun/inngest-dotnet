using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Inngest.Json;
using Inngest.Tests.Client;
using Inngest.Tests.Execution;

namespace Inngest.Tests.Functions;

// The data of the events Inngest sends about runs, in the shape the server's executor builds it: the function's
// composite id, the run id, the triggering event and its batch, the result or the error, and internal fields under
// "_inngest".
public sealed class SystemEventTests : IDisposable
{
    private const string Triggering = """{"name":"app/import","data":{"file":"a.csv"},"id":"01JTRIGGER","ts":1793268000000}""";

    private readonly EngineHost _host = new();

    public void Dispose() => _host.Dispose();

    [Fact]
    public void The_names_are_the_ones_inngest_sends()
    {
        SystemEvents.FunctionFailed.ShouldBe("inngest/function.failed");
        SystemEvents.FunctionFinished.ShouldBe("inngest/function.finished");
        SystemEvents.FunctionCancelled.ShouldBe("inngest/function.cancelled");
        SystemEvents.FunctionInvoked.ShouldBe("inngest/function.invoked");
        SystemEvents.ScheduledTimer.ShouldBe("inngest/scheduled.timer");
    }

    [Fact]
    public async Task A_cleanup_function_reads_the_cancelled_run_and_its_event()
    {
        FunctionCancelled? seen = null;
        var function = TestFunction.Define<FunctionCancelled>(context =>
        {
            seen = context.Event.Data;
            return Task.FromResult<object?>(null);
        });

        var result = await _host.CallAsync(function, Call.New().Event(SystemEvent(
            SystemEvents.FunctionCancelled,
            $$$"""{"function_id":"test-app-import","run_id":"01JCANCELLED","event":{{{Triggering}}},"events":[{{{Triggering}}}],"_inngest":{"status":"Cancelled"}}""")));

        result.StatusCode.ShouldBe(200);
        seen.ShouldNotBeNull();
        seen.FunctionId.ShouldBe("test-app-import");
        seen.RunId.ShouldBe("01JCANCELLED");
        seen.CorrelationId.ShouldBeNull();
        seen.Event.GetProperty("data").GetProperty("file").GetString().ShouldBe("a.csv");
    }

    [Fact]
    public async Task A_finished_run_carries_its_result()
    {
        FunctionFinished? seen = null;
        var function = TestFunction.Define<FunctionFinished>(context =>
        {
            seen = context.Event.Data;
            return Task.FromResult<object?>(null);
        });

        await _host.CallAsync(function, Call.New().Event(SystemEvent(
            SystemEvents.FunctionFinished,
            $$$"""{"function_id":"test-app-import","run_id":"01JDONE","event":{{{Triggering}}},"events":[{{{Triggering}}}],"result":{"rows":3},"correlation_id":"01JPARENT.step","_inngest":{"status":"Completed"}}""")));

        seen.ShouldNotBeNull();
        seen.Result.GetProperty("rows").GetInt32().ShouldBe(3);
        seen.Error.ShouldBeNull();
        seen.CorrelationId.ShouldBe("01JPARENT.step");
        seen.Event.GetProperty("name").GetString().ShouldBe("app/import");
    }

    [Fact]
    public async Task A_finished_run_that_failed_carries_its_error_and_the_body_of_the_failed_call_as_its_result()
    {
        FunctionFinished? seen = null;
        var function = TestFunction.Define<FunctionFinished>(context =>
        {
            seen = context.Event.Data;
            return Task.FromResult<object?>(null);
        });
        const string Body = """{"cause":{"message":"not a number","name":"FormatException"},"message":"bad row","name":"InvalidOperationException","stack":"at Import()"}""";

        await _host.CallAsync(function, Call.New().Event(SystemEvent(
            SystemEvents.FunctionFinished,
            $$$"""{"_inngest":{"status":"Failed"},"error":{"cause":{"message":"not a number","name":"FormatException"},"error":"NonRetriableError","message":"bad row","name":"InvalidOperationException","stack":"at Import()"},"event":{{{Triggering}}},"function_id":"test-app-import","result":{{{Body}}},"run_id":"01JFAILED"}""")));

        seen.ShouldNotBeNull();
        seen.Result.GetRawText().ShouldBe(Body);
        seen.Error.ShouldBe(new FunctionError
        {
            Name = "InvalidOperationException",
            Message = "bad row",
            Stack = "at Import()",
            Cause = new FunctionError { Name = "FormatException", Message = "not a number" },
        });
    }

    // inngest-js keeps a cause that is not an Error as it is, so a string cause reaches Inngest as a string.
    [Fact]
    public void Errors_written_by_other_sdks_are_read_even_when_a_cause_is_not_an_error_object()
    {
        var options = SerializerOptionsSetup.Prepare(new JsonSerializerOptions(JsonSerializerDefaults.Web));

        var finished = Deserialize<FunctionFinished>(
            """{"function_id":"js-app-charge","run_id":"01JJS","error":{"name":"Error","message":"declined","stack":"Error: declined","cause":"card expired","__serialized":true}}""",
            options);
        var failure = Deserialize<FunctionFailure>("""{"function_id":"js-app-charge","run_id":"01JJS","error":"plain text"}""", options);

        finished.Error.ShouldBe(new FunctionError { Name = "Error", Message = "declined", Stack = "Error: declined", Cause = new FunctionError { Message = "card expired" } });
        failure.Error.ShouldBe(new FunctionError { Message = "plain text" });
    }

    [Fact]
    public void An_app_without_reflection_reads_them_through_the_sdks_own_metadata()
    {
        var options = SerializerOptionsSetup.Prepare(new JsonSerializerOptions(JsonSerializerDefaults.Web) { TypeInfoResolver = ClientTestJsonContext.Default });

        var cancelled = Deserialize<FunctionCancelled>("""{"function_id":"app-fn","run_id":"01JAOT","event":{"name":"a"}}""", options);
        var finished = Deserialize<FunctionFinished>("""{"function_id":"app-fn","run_id":"01JAOT","error":{"message":"m","cause":"text"}}""", options);

        cancelled.RunId.ShouldBe("01JAOT");
        finished.Error!.Cause!.Message.ShouldBe("text");
    }

    private static string SystemEvent(string name, string data) =>
        $$"""{"name":"{{name}}","data":{{data}},"id":"01JSYSTEM","ts":1793268000000}""";

    private static T Deserialize<T>(string json, JsonSerializerOptions options) =>
        JsonSerializer.Deserialize(json, (JsonTypeInfo<T>)options.GetTypeInfo(typeof(T)))!;
}
