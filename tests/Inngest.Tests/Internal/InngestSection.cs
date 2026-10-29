using Microsoft.Extensions.Configuration;

namespace Inngest.Tests.Internal;

// Configuration the way an appsettings.json holding an "Inngest" section would give it.
internal static class InngestSection
{
    public const string Name = "Inngest";

    // The section with these keys; a null value is a key without a value.
    public static IConfigurationSection Of(params (string Key, string? Value)[] keys) => Root(keys).GetSection(Name);

    // The whole configuration: the section's keys, and any other entries, such as INNGEST_* variables.
    public static IConfigurationRoot Root((string Key, string? Value)[] keys, params (string Name, string? Value)[] others) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(
                keys
                    .Select(key => new KeyValuePair<string, string?>($"{Name}:{key.Key}", key.Value))
                    .Concat(others.Select(other => new KeyValuePair<string, string?>(other.Name, other.Value))))
            .Build();
}
