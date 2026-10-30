// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Inngest.Json;

namespace Inngest;

/// <summary>
/// A function run as the REST API reports it. Only EndedAt says for sure that a run is over: the runs of an event can
/// already read Completed while a sleep of the run is still pending.
/// </summary>
public sealed record FunctionRun
{
    // For runs that were not read by IInngestRunsClient, such as fakes in tests: web defaults, as the app's options
    // have unless it changes them.
    private static readonly Lazy<JsonSerializerOptions> DefaultSerializerOptions =
        new(static () => SerializerOptionsSetup.Prepare(new JsonSerializerOptions(JsonSerializerDefaults.Web)));

    public required string RunId { get; init; }

    public required FunctionRunStatus Status { get; init; }

    /// <summary>Inngest's own id of the function, a UUID, rather than the id the app registered it with.</summary>
    public required string FunctionId { get; init; }

    public int FunctionVersion { get; init; }

    /// <summary>When the run was scheduled.</summary>
    public DateTimeOffset StartedAt { get; init; }

    /// <summary>Null while the run goes on.</summary>
    public DateTimeOffset? EndedAt { get; init; }

    /// <summary>The id that sending the triggering event returned; null for runs a cron schedule started.</summary>
    public string? EventId { get; init; }

    public string? BatchId { get; init; }

    /// <summary>The run that this run replays.</summary>
    public string? OriginalRunId { get; init; }

    /// <summary>The schedule that started the run.</summary>
    public string? Cron { get; init; }

    public string? EnvironmentId { get; init; }

    /// <summary>
    /// What the function returned, or the error a failed run reported. Its ValueKind is Null or Undefined until the run
    /// has ended.
    /// </summary>
    public JsonElement Output { get; init; }

    internal JsonSerializerOptions? SerializerOptions { get; init; }

    /// <summary>
    /// Reads Output with the app's serializer options, as step results and function output are written. Default when
    /// there is no output.
    /// </summary>
    public T? GetOutput<T>()
    {
        if (Output.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return default;
        }

        var options = SerializerOptions ?? DefaultSerializerOptions.Value;
        return Output.Deserialize((JsonTypeInfo<T>)options.GetTypeInfo(typeof(T)));
    }
}
