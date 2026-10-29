// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Inngest;

// Settings left null fall back to the INNGEST_* environment variables (INNGEST_EVENT_KEY, INNGEST_SIGNING_KEY,
// INNGEST_DEV, ...) and then to Inngest Cloud defaults.
public sealed class InngestOptions
{
    public string? AppId { get; set; }

    public string? AppVersion { get; set; }

    public string? EventKey { get; set; }

    public string? SigningKey { get; set; }

    // Accepted alongside SigningKey while a signing key is being rotated.
    public string? SigningKeyFallback { get; set; }

    // The Inngest branch environment to use, such as a preview branch name.
    public string? Environment { get; set; }

    // True targets a local dev server and skips signature checks; false targets Inngest Cloud. Null reads
    // INNGEST_DEV and defaults to cloud mode.
    public bool? IsDev { get; set; }

    // The dev server address in dev mode. Defaults to http://localhost:8288.
    public Uri? DevServerUrl { get; set; }

    // Overrides both the API and the Event API address, for self-hosted Inngest.
    public Uri? BaseUrl { get; set; }

    public Uri? ApiBaseUrl { get; set; }

    public Uri? EventApiBaseUrl { get; set; }

    // The public origin Inngest should call, when it differs from the origin the request arrived on, for
    // example behind a reverse proxy.
    public Uri? ServeOrigin { get; set; }

    public string? ServePath { get; set; }

    // Lets Inngest sync the app by calling it directly instead of the app calling Inngest back. Null reads
    // INNGEST_ALLOW_IN_BAND_SYNC and defaults to true.
    public bool? AllowInBandSync { get; set; }

    // In cloud mode, lets an unsigned PUT trigger an out-of-band sync, as the TypeScript SDK does by default. The
    // app then only calls the Inngest it is configured for. Null reads INNGEST_ENABLE_UNAUTHED_SYNC and defaults
    // to true.
    public bool? AllowUnsignedSync { get; set; }

    // Runs consecutive steps of every function in one call from Inngest, saving each as it completes. Null leaves it
    // off; FunctionOptions.Checkpointing overrides it for a single function.
    public CheckpointingOptions? Checkpointing { get; set; }

    // Used for event data, step results and function output. Add source-generated contexts to its
    // TypeInfoResolverChain to run without reflection, for example under Native AOT.
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

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Only used when reflection-based serialization is enabled, which trimmed and AOT apps turn off.")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Only used when reflection-based serialization is enabled, which trimmed and AOT apps turn off.")]
    private static DefaultJsonTypeInfoResolver CreateReflectionResolver() => new();
}
