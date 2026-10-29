using System.Text;
using Inngest.Execution;
using Inngest.Functions;
using Inngest.Registration;
using Inngest.Tests.Execution;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Inngest.Tests.Stress;

// The container AddInngest builds, in dev mode, with the network replaced by a handler. Calls run the way the serve
// handler runs them: each in a scope of its own.
internal static class StressServices
{
    public static ServiceProvider Build(HttpMessageHandler handler, Action<IInngestBuilder> configure)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddLogging();
        configure(services.AddInngest(static options =>
        {
            options.AppId = "test-app";
            options.IsDev = true;
        }));
        services.AddHttpClient("Inngest").ConfigurePrimaryHttpMessageHandler(() => handler);
        return services.BuildServiceProvider();
    }

    public static IExecutionEngine Engine(IServiceProvider provider, string functionId, out FunctionDefinition function)
    {
        provider.GetRequiredService<IFunctionRegistry>().TryGet($"test-app-{functionId}", out function).ShouldBeTrue();
        return provider.GetRequiredService<IExecutionEngine>();
    }

    public static async Task<CallResult> CallAsync(IServiceProvider provider, IExecutionEngine engine, FunctionDefinition function, byte[] body, string stepId = "step")
    {
        await using var scope = provider.CreateAsyncScope();
        using var result = await engine.ExecuteAsync(
            new ExecutionRequest { Function = function, Body = body, StepId = stepId, Services = scope.ServiceProvider },
            TestContext.Current.CancellationToken);
        return new CallResult(result.StatusCode, Encoding.UTF8.GetString(result.Body.WrittenSpan), result.NoRetry, result.RetryAfter);
    }
}
