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

    // The variables are meant for a process that inherits its parent's, so each one the machine may have set to something
    // that would win is set here too. The SDK reads the address of the API from INNGEST_API_BASE_URL, then
    // INNGEST_BASE_URL, then the dev server's address, and that of the event API from INNGEST_EVENT_API_BASE_URL first,
    // and INNGEST_DEV decides the mode: 0 for cloud mode, with signatures checked, or the address of the dev server.
    // Those are the names of every Inngest SDK.
    public static IReadOnlyDictionary<string, string> ToEnvironment(Uri baseAddress, InngestServerConfiguration configuration)
    {
        var address = baseAddress.AbsoluteUri;
        var variables = new Dictionary<string, string>
        {
            ["INNGEST_BASE_URL"] = address,
            ["INNGEST_API_BASE_URL"] = address,
            ["INNGEST_EVENT_API_BASE_URL"] = address,
        };

        if (configuration.SelfHosted)
        {
            variables["INNGEST_DEV"] = "0";
            variables["INNGEST_SIGNING_KEY"] = configuration.SigningKey!;
            variables["INNGEST_EVENT_KEY"] = configuration.EventKey!;
        }
        else
        {
            variables["INNGEST_DEV"] = address;
        }

        return variables;
    }
}
