// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Inngest;

/// <summary>Settings of the source AddInngestRealtimeRelaySource registers, which reads the app's MapInngestRealtime endpoint.</summary>
public sealed class RealtimeRelayOptions
{
    /// <summary>
    /// Maps the channel and topics a component watches to the address of the endpoint that serves them, which the
    /// component's request must reach; a relative address is relative to the base address of the HttpClient, or of the
    /// page when the client has none. Required. The endpoint decides what a request may receive and answers 403 to
    /// one it refuses, so a channel in the address needs a callback that checks it. The topics are only a request:
    /// messages of any others the endpoint serves are dropped.
    /// </summary>
    public Func<string, IReadOnlyList<string>, Uri>? Url { get; set; }

    /// <summary>
    /// Used to read the data of typed topics. By default it reads them by reflection, which stays enabled in Blazor
    /// WebAssembly; an app that is trimmed with TrimMode full, or compiled ahead of time, sets its TypeInfoResolver to a
    /// source-generated context for the data types instead.
    /// </summary>
    public JsonSerializerOptions SerializerOptions { get; set; } = CreateSerializerOptions();

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        if (JsonSerializer.IsReflectionEnabledByDefault)
        {
            options.TypeInfoResolver = CreateReflectionResolver();
        }

        return options;
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Only used when reflection-based serialization is enabled, which apps that are fully trimmed or compiled ahead of time turn off.")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Only used when reflection-based serialization is enabled, which apps that are fully trimmed or compiled ahead of time turn off.")]
    private static DefaultJsonTypeInfoResolver CreateReflectionResolver() => new();
}
