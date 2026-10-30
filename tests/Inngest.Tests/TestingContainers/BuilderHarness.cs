using Docker.DotNet.Models;
using Inngest.Testing.Containers;

namespace Inngest.Tests.TestingContainers;

// Builds containers without Docker: the builder only needs a Docker endpoint to be configured, and nothing connects to
// it before a container starts. What a container would be started with is read back from its configuration.
internal static class BuilderHarness
{
    public const string Image = "inngest/inngest:v1.49.0";

    public static InngestServerBuilder Builder() => new InngestServerBuilder(Image).WithDockerEndpoint("tcp://127.0.0.1:1");

    public static string[] CommandOf(this InngestServerContainer container) => [.. container.Configuration.Command];

    public static string[] CommandOf(this InngestServerBuilder builder) => builder.Build().CommandOf();

    // Null when the key is absent, which no expected value here is.
    public static string? ValueOf(this IReadOnlyDictionary<string, string> dictionary, string key) =>
        dictionary.TryGetValue(key, out var value) ? value : null;

    // The create parameters Docker would be sent, after the builder's modifiers have run.
    public static CreateContainerParameters CreateParametersOf(this InngestServerContainer container)
    {
        var parameters = new CreateContainerParameters();
        foreach (var modifier in container.Configuration.ParameterModifiers)
        {
            modifier(parameters);
        }

        return parameters;
    }
}
