// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Runtime.InteropServices;

namespace Inngest.OpenTelemetry.ExtendedTraces;

// The resource of the exported spans: the service, the host and the process, as the TypeScript SDK detects them, with
// OTEL_SERVICE_NAME and OTEL_RESOURCE_ATTRIBUTES on top as OpenTelemetry's SDKs read them. Inngest shows the service's
// name with each span.
internal static class ExtendedTraceResource
{
    public static IReadOnlyList<KeyValuePair<string, object?>> Attributes { get; } = Detect();

    private static List<KeyValuePair<string, object?>> Detect()
    {
        var attributes = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["service.name"] = Environment.ProcessPath is { } path ? "unknown_service:" + Path.GetFileNameWithoutExtension(path) : "unknown_service",
            ["telemetry.sdk.name"] = "inngest-dotnet",
            ["telemetry.sdk.language"] = "dotnet",
            ["telemetry.sdk.version"] = InngestTelemetry.Version,
            ["host.name"] = Environment.MachineName,
            ["os.type"] = OperatingSystemType(),
            ["process.pid"] = Environment.ProcessId,
            ["process.runtime.name"] = ".NET",
            ["process.runtime.version"] = Environment.Version.ToString(),
        };

        if (Environment.GetEnvironmentVariable("OTEL_RESOURCE_ATTRIBUTES") is { Length: > 0 } pairs)
        {
            foreach (var pair in pairs.Split(','))
            {
                var separator = pair.IndexOf('=', StringComparison.Ordinal);
                if (separator > 0)
                {
                    attributes[Uri.UnescapeDataString(pair[..separator].Trim())] = Uri.UnescapeDataString(pair[(separator + 1)..].Trim());
                }
            }
        }

        if (Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME") is { Length: > 0 } serviceName)
        {
            attributes["service.name"] = serviceName;
        }

        return [.. attributes];
    }

    private static string OperatingSystemType()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return "windows";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return "darwin";
        }

        return RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "linux" : RuntimeInformation.OSDescription;
    }
}
