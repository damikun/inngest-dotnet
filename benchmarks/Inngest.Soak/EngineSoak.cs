using Inngest.Execution;
using Inngest.Registration;
using Inngest.Soak.Workload;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Inngest.Soak;

// The engine as the serve handler calls it: a scope per invocation, the function and middleware resolved from it.
internal static class EngineSoak
{
    public static async Task<int> RunAsync(CommandLine options, CancellationToken cancellationToken)
    {
        var services = new ServiceCollection();

        // An empty configuration keeps the machine's INNGEST_* variables out of the settings.
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddLogging();
        services.AddSoakApp(static options => options.IsDev = true);

        await using var provider = services.BuildServiceProvider();
        var engine = provider.GetRequiredService<IExecutionEngine>();
        var scopes = provider.GetRequiredService<IServiceScopeFactory>();
        if (!provider.GetRequiredService<IFunctionRegistry>().TryGet(SoakServices.CheckoutCompositeId, out var function))
        {
            throw new InvalidOperationException("The checkout function is not registered.");
        }

        return await SoakRunner.RunAsync(
            "engine",
            options,
            defaultInvocations: 2_000_000,
            async (call, checkBody, token) =>
            {
                var scope = scopes.CreateAsyncScope();
                await using (scope.ConfigureAwait(false))
                {
                    var request = new ExecutionRequest { Function = function, Body = call.Body, StepId = call.StepId, Services = scope.ServiceProvider };
                    using var result = await engine.ExecuteAsync(request, token).ConfigureAwait(false);
                    return SoakRunner.Check(call, result.StatusCode, result.Body.WrittenSpan, checkBody);
                }
            },
            cancellationToken).ConfigureAwait(false);
    }
}
