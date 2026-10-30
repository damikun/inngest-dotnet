// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Inngest.Errors;

namespace Inngest.Json;

internal static class SerializerOptionsSetup
{
    private static readonly Type[] PublicTypes =
    [
        typeof(JsonElement),

        // Steps without a result replay as object.
        typeof(object),
        typeof(FunctionFailure),
        typeof(FunctionError),
        typeof(FunctionFinished),
        typeof(FunctionCancelled),
        typeof(SendEventsResult),
    ];

    // Returns a locked copy of the app's options that can also serialize the SDK's public types, so it can be shared
    // between threads. The app's own instance is never changed or locked.
    public static JsonSerializerOptions Prepare(JsonSerializerOptions options)
    {
        var prepared = new JsonSerializerOptions(options);

        // Added last, so a converter of the app's own for FunctionError still wins.
        prepared.Converters.Add(new FunctionErrorConverter());

        // Checking the feature switch first lets the trimmer drop the reflection resolver from AOT builds.
        if (JsonSerializer.IsReflectionEnabledByDefault && prepared.TypeInfoResolver is null)
        {
            prepared.TypeInfoResolver = CreateReflectionResolver();
        }

        if (!ResolvesPublicTypes(prepared))
        {
            prepared.TypeInfoResolverChain.Add(PublicTypesJsonContext.Default);
        }

        prepared.MakeReadOnly();
        return prepared;
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Only reached when reflection-based serialization is enabled, which trimmed and AOT apps turn off.")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Only reached when reflection-based serialization is enabled, which trimmed and AOT apps turn off.")]
    private static DefaultJsonTypeInfoResolver CreateReflectionResolver() => new();

    private static bool ResolvesPublicTypes(JsonSerializerOptions options)
    {
        var resolver = options.TypeInfoResolver;
        if (resolver is null)
        {
            return false;
        }

        foreach (var type in PublicTypes)
        {
            if (resolver.GetTypeInfo(type, options) is null)
            {
                return false;
            }
        }

        return true;
    }
}
