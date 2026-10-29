using System.Text.Json;
using Inngest.Execution;
using Inngest.Registration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Inngest.Benchmarks.Support;

// The real engine behind the container AddInngest builds, called the way the serve handler calls it: one request
// for one function, resolved from a scope. Only the HTTP handler is replaced.
internal sealed class EngineHarness : IDisposable
{
    private const string AppId = "shop";
    private const string FunctionId = "process-order";

    private readonly ServiceProvider _provider;
    private readonly IServiceScope _scope;
    private readonly IExecutionEngine _engine;
    private readonly ExecutionRequest _request;
    private readonly int _expectedStatus;

    private EngineHarness(ServiceProvider provider, byte[] body, string stepId, int expectedStatus)
    {
        _provider = provider;
        _scope = provider.CreateScope();
        _engine = provider.GetRequiredService<IExecutionEngine>();
        _expectedStatus = expectedStatus;

        Expect.That(provider.GetRequiredService<IFunctionRegistry>().TryGet($"{AppId}-{FunctionId}", out var function), "the function is registered");
        _request = new ExecutionRequest { Function = function, Body = body, StepId = stepId, Services = _scope.ServiceProvider };
    }

    public int MiddlewareCount => _scope.ServiceProvider.GetServices<InngestMiddleware>().Count();

    public static EngineHarness For<TFunction>(StepPlan plan, byte[] body, int expectedStatus, bool noOpMiddleware = false)
        where TFunction : InngestFunction
    {
        var services = new ServiceCollection();

        // Keeps the process environment out of the settings.
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton(plan);

        var builder = services.AddInngest(options =>
        {
            options.AppId = AppId;
            options.IsDev = true;
        });
        services.AddHttpClient("Inngest").ConfigurePrimaryHttpMessageHandler(static () => new NoOpHttpHandler());
        builder.AddFunction<TFunction>(new FunctionOptions { Id = FunctionId, Triggers = [Trigger.Event(Samples.EventName)] });
        if (noOpMiddleware)
        {
            builder.AddMiddleware<NoOpMiddleware>();
        }

        return new EngineHarness(services.BuildServiceProvider(), body, "step", expectedStatus);
    }

    public Task<ExecutionResult> ExecuteAsync() => _engine.ExecuteAsync(_request, CancellationToken.None);

    // Runs the function once and checks that it returned the summary of this many steps.
    public async Task ExpectSummaryAsync(int steps)
    {
        using var result = await ExecuteAsync();
        using var answer = Expect.Answer(result, 200);
        Expect.Equal(Samples.ExpectedSummaryJson(steps), answer.RootElement.GetRawText(), "the function output");
    }

    // Runs the function once and checks that it answered with a single op for this step. Returns the op.
    public async Task<JsonElement> ExpectOpAsync(string op, string stepId)
    {
        using var result = await ExecuteAsync();
        using var answer = Expect.Answer(result, 206);
        Expect.Equal(1, answer.RootElement.GetArrayLength(), "the number of ops reported");
        var reported = answer.RootElement[0].Clone();
        Expect.Equal(op, Expect.Property(reported, "op").GetString(), "the op");
        Expect.Equal(StepIds.Hash(stepId), Expect.Property(reported, "id").GetString(), "the id of the step reported");
        return reported;
    }

    // One invocation. The result is disposed here because its body is a pooled buffer, which the serve handler
    // also returns after writing the response.
    public async ValueTask<int> RunAsync()
    {
        using var result = await _engine.ExecuteAsync(_request, CancellationToken.None).ConfigureAwait(false);
        if (result.StatusCode != _expectedStatus)
        {
            throw new InvalidOperationException($"The engine answered {result.StatusCode}, expected {_expectedStatus}.");
        }

        return result.StatusCode;
    }

    public void Dispose()
    {
        _scope.Dispose();
        _provider.Dispose();
    }
}
