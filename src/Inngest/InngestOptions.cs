// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Configuration;

namespace Inngest;

/// <summary>
/// Settings left null fall back to the INNGEST_* environment variables (INNGEST_EVENT_KEY, INNGEST_SIGNING_KEY,
/// INNGEST_DEV, ...), then to the configuration section given to AddInngest, and then to Inngest Cloud defaults.
/// </summary>
public sealed class InngestOptions
{
    public string? AppId { get; set; }

    public string? AppVersion { get; set; }

    public string? EventKey { get; set; }

    public string? SigningKey { get; set; }

    /// <summary>Accepted alongside SigningKey while a signing key is being rotated.</summary>
    public string? SigningKeyFallback { get; set; }

    /// <summary>The Inngest branch environment to use, such as a preview branch name.</summary>
    public string? Environment { get; set; }

    /// <summary>
    /// True targets a local dev server and skips signature checks; false targets Inngest Cloud. Null reads
    /// INNGEST_DEV and defaults to cloud mode.
    /// </summary>
    public bool? IsDev { get; set; }

    /// <summary>The dev server address in dev mode. Defaults to http://localhost:8288.</summary>
    public Uri? DevServerUrl { get; set; }

    /// <summary>Overrides both the API and the Event API address, for self-hosted Inngest.</summary>
    public Uri? BaseUrl { get; set; }

    public Uri? ApiBaseUrl { get; set; }

    public Uri? EventApiBaseUrl { get; set; }

    /// <summary>
    /// The public origin Inngest should call, when it differs from the origin the request arrived on, for
    /// example behind a reverse proxy.
    /// </summary>
    public Uri? ServeOrigin { get; set; }

    public string? ServePath { get; set; }

    /// <summary>
    /// Lets Inngest sync the app by calling it directly instead of the app calling Inngest back. Null reads
    /// INNGEST_ALLOW_IN_BAND_SYNC and defaults to true.
    /// </summary>
    public bool? AllowInBandSync { get; set; }

    /// <summary>
    /// In cloud mode, lets an unsigned PUT trigger an out-of-band sync, as the TypeScript SDK does by default. The
    /// app then only calls the Inngest it is configured for, but without <see cref="ServeOrigin"/> the URL it
    /// registers comes from the request's Host header, which the caller chooses. Null reads
    /// INNGEST_ENABLE_UNAUTHED_SYNC and defaults to true.
    /// </summary>
    public bool? AllowUnsignedSync { get; set; }

    /// <summary>
    /// Keeps long calls from Inngest open behind proxies and load balancers that close idle connections, as streaming
    /// does in the TypeScript SDK: a call still running after three seconds is answered with a 201 at once, then a
    /// space every three seconds, then the result. Only the ASP.NET Core endpoint streams; Connect needs none. Null
    /// reads INNGEST_STREAMING and defaults to false.
    /// </summary>
    public bool? Streaming { get; set; }

    /// <summary>
    /// Lets events a run sends and functions it invokes inherit the run's sessions, as FunctionContext.Sessions
    /// lists them. Null reads INNGEST_SESSION_PROPAGATION and defaults to true, as in the TypeScript SDK.
    /// </summary>
    public bool? SessionPropagation { get; set; }

    /// <summary>
    /// Runs consecutive steps of every function in one call from Inngest, saving each as it completes. Null leaves it
    /// off; FunctionOptions.Checkpointing overrides it for a single function.
    /// </summary>
    public CheckpointingOptions? Checkpointing { get; set; }

    /// <summary>
    /// Used for event data, step results and function output. Add source-generated contexts to its
    /// TypeInfoResolverChain to run without reflection, for example under Native AOT.
    /// </summary>
    public JsonSerializerOptions SerializerOptions { get; set; } = CreateSerializerOptions();

    // The section AddInngest(IConfiguration) was given. InngestSettings reads it below the options and the variables,
    // so registering it needs no order against the callbacks that set the options.
    internal IConfiguration? Configuration { get; set; }

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
