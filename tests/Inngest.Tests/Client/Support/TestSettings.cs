using Inngest.Configuration;

namespace Inngest.Tests.Client;

internal static class TestSettings
{
    // Dev mode with the default addresses, which is what most tests want. Variables stand in for the process
    // environment.
    public static InngestSettings Create(Action<InngestOptions>? configure = null, IDictionary<string, string?>? variables = null)
    {
        var options = new InngestOptions { AppId = "test-app", IsDev = true };
        configure?.Invoke(options);
        return InngestSettings.Create(options, name => variables is not null && variables.TryGetValue(name, out var value) ? value : null);
    }

    public static InngestSettings Cloud(Action<InngestOptions>? configure = null, IDictionary<string, string?>? variables = null) =>
        Create(
            options =>
            {
                options.IsDev = false;
                configure?.Invoke(options);
            },
            variables);
}
