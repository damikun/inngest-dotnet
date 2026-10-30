// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

namespace Inngest.Testing.Containers;

// The settings that point an app at the server, kept apart from the container so that they can be checked without a
// running one.
internal static class SdkSettings
{
    // BaseUrl first, because it wins over every other address in the options, including one the app set in code.
    public static void Apply(InngestOptions options, Uri baseAddress, InngestServerConfiguration configuration)
    {
        options.BaseUrl = baseAddress;
        if (configuration.SelfHosted)
        {
            options.IsDev = false;
            options.SigningKey = configuration.SigningKey;
            options.EventKey = configuration.EventKey;
        }
        else
        {
            options.IsDev = true;
            options.DevServerUrl = baseAddress;
        }
    }

    // INNGEST_DEV carries the address of a dev server, as the SDKs of every language read it.
    public static IReadOnlyDictionary<string, string> ToEnvironment(Uri baseAddress, InngestServerConfiguration configuration) =>
        configuration.SelfHosted
            ? new Dictionary<string, string>
            {
                ["INNGEST_BASE_URL"] = baseAddress.AbsoluteUri,
                ["INNGEST_SIGNING_KEY"] = configuration.SigningKey!,
                ["INNGEST_EVENT_KEY"] = configuration.EventKey!,
            }
            : new Dictionary<string, string> { ["INNGEST_DEV"] = baseAddress.AbsoluteUri };
}
