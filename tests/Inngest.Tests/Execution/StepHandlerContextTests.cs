using System.Diagnostics;

namespace Inngest.Tests.Execution;

// A step's handler runs in the execution context of the code that called the step, so what the function set around
// the call is there in the handler, as it would be for a plain method call (inngest-js#1436).
public sealed class StepHandlerContextTests : IDisposable
{
    private static readonly AsyncLocal<string?> Ambient = new();

    private readonly EngineHost _host = new();

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task An_async_local_the_function_set_before_the_step_is_there_in_the_handler()
    {
        var function = TestFunction.Define(async context =>
        {
            Ambient.Value = "tenant-1";
            return await context.Step.RunAsync("a", () => Ambient.Value ?? "<null>");
        });

        var result = await _host.CallAsync(function, Call.New());

        result.Op().GetProperty("data").GetString().ShouldBe("tenant-1");
    }

    [Fact]
    public async Task An_async_handler_keeps_the_context_after_its_first_await()
    {
        var function = TestFunction.Define(async context =>
        {
            Ambient.Value = "tenant-1";
            return await context.Step.RunAsync("a", async () =>
            {
                await Task.Yield();
                return Ambient.Value ?? "<null>";
            });
        });

        var result = await _host.CallAsync(function, Call.New());

        result.Op().GetProperty("data").GetString().ShouldBe("tenant-1");
    }

    [Fact]
    public async Task What_the_handler_sets_does_not_leak_back_into_the_function()
    {
        var function = TestFunction.Define(async context =>
        {
            Ambient.Value = "function";
            await context.Step.RunAsync("a", () =>
            {
                Ambient.Value = "handler";
                return 1;
            });
            return await context.Step.RunAsync("b", () => Ambient.Value ?? "<null>");
        });

        var result = await _host.CallAsync(function, Call.New().Data("a", "1"));

        result.Op().GetProperty("data").GetString().ShouldBe("function");
    }

    [Fact]
    public async Task Parallel_steps_each_get_the_context_of_their_own_branch()
    {
        var function = TestFunction.Define(async context =>
        {
            var results = await Task.WhenAll(Branch("a", "first"), Branch("b", "second"));
            return string.Join(",", results);

            async Task<string> Branch(string id, string value)
            {
                Ambient.Value = value;
                return await context.Step.RunAsync(id, () => Ambient.Value ?? "<null>");
            }
        });

        var a = await _host.CallAsync(function, Call.New().DisableImmediateExecution(), stepId: Call.Hash("a"));
        var b = await _host.CallAsync(function, Call.New().DisableImmediateExecution(), stepId: Call.Hash("b"));

        a.Op().GetProperty("data").GetString().ShouldBe("first");
        b.Op().GetProperty("data").GetString().ShouldBe("second");
    }

    [Fact]
    public async Task A_step_called_from_the_thread_pool_gets_the_context_of_that_branch()
    {
        var function = TestFunction.Define(async context => await Task.Run(() =>
        {
            Ambient.Value = "pool";
            return context.Step.RunAsync("a", () => Ambient.Value ?? "<null>");
        }));

        var result = await _host.CallAsync(function, Call.New());

        result.Op().GetProperty("data").GetString().ShouldBe("pool");
    }

    [Fact]
    public async Task The_activity_the_function_started_is_current_in_the_handler()
    {
        using var source = new ActivitySource(nameof(StepHandlerContextTests) + Guid.NewGuid());
        using var listener = new ActivityListener
        {
            ShouldListenTo = candidate => candidate == source,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        var function = TestFunction.Define(async context =>
        {
            using var activity = source.StartActivity("checkout");
            return await context.Step.RunAsync("a", () => Activity.Current?.DisplayName ?? "<none>");
        });

        var result = await _host.CallAsync(function, Call.New());

        result.Op().GetProperty("data").GetString().ShouldBe("checkout");
    }
}
