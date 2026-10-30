using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging.Abstractions;

namespace Inngest.Aspire.Hosting.Tests.AppModel;

// What the tests need to read from an application model without running it: the arguments and environment of a
// resource as Aspire resolves them for the process or container, and endpoints that have the addresses DCP gives them.
internal static class Model
{
    public static IDistributedApplicationBuilder CreateBuilder() => DistributedApplication.CreateBuilder();

    public static async Task<IReadOnlyList<string>> ArgumentsAsync(IResource resource)
    {
        var configuration = await ExecutionConfigurationBuilder.Create(resource)
            .WithArgumentsConfig()
            .BuildAsync(new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run), NullLogger.Instance, TestContext.Current.CancellationToken);

        ThrowIfFailed(configuration);
        return [.. configuration.Arguments.Select(argument => argument.Value)];
    }

    public static async Task<IReadOnlyDictionary<string, string>> EnvironmentAsync(IResource resource)
    {
        var configuration = await ExecutionConfigurationBuilder.Create(resource)
            .WithEnvironmentVariablesConfig()
            .BuildAsync(new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run), NullLogger.Instance, TestContext.Current.CancellationToken);

        ThrowIfFailed(configuration);
        return configuration.EnvironmentVariables.ToDictionary(variable => variable.Key, variable => variable.Value);
    }

    // The builder reports a value it cannot resolve in its result rather than by throwing, wrapped in an AggregateException.
    private static void ThrowIfFailed(IExecutionConfigurationResult configuration)
    {
        if (configuration.Exception is { } exception)
        {
            ExceptionDispatchInfo.Capture(exception is AggregateException { InnerExceptions: [var inner] } ? inner : exception).Throw();
        }
    }

    // A secret parameter that is not part of the model. One that is waits for the AppHost to start before it has a value.
    public static IResourceBuilder<ParameterResource> Secret(IDistributedApplicationBuilder builder, string name, string value) =>
        builder.CreateResourceBuilder(new ParameterResource(name, _ => value, secret: true));

    public static async Task<string> ValueAsync(ParameterResource parameter) =>
        await parameter.GetValueAsync(TestContext.Current.CancellationToken) ?? throw new InvalidOperationException($"The parameter {parameter.Name} has no value.");

    // The addresses of an endpoint once DCP has allocated it: from the host, localhost and the port the proxy listens
    // on, and from a container, the name the host or the other container has on the container network.
    public static void Allocate(IResourceWithEndpoints resource, string endpointName, int hostPort, string containerHost, int containerPort)
    {
        var endpoint = resource.Annotations.OfType<EndpointAnnotation>().Single(annotation => annotation.Name == endpointName);
        endpoint.AllAllocatedEndpoints.AddOrUpdateAllocatedEndpoint(
            KnownNetworkIdentifiers.LocalhostNetwork,
            new AllocatedEndpoint(endpoint, "localhost", hostPort, EndpointBindingMode.SingleAddress, targetPortExpression: null, KnownNetworkIdentifiers.LocalhostNetwork));
        endpoint.AllAllocatedEndpoints.AddOrUpdateAllocatedEndpoint(
            KnownNetworkIdentifiers.DefaultAspireContainerNetwork,
            new AllocatedEndpoint(endpoint, containerHost, containerPort, EndpointBindingMode.SingleAddress, targetPortExpression: null, KnownNetworkIdentifiers.DefaultAspireContainerNetwork));
    }
}
