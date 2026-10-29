// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Inngest.Internal;

namespace Inngest;

// A function run as the REST API reports it. Only EndedAt says for sure that a run is over: the runs of an event can
// already read Completed while a sleep of the run is still pending.
public sealed record FunctionRun
{
    // For runs that were not read by IInngestRunsClient, such as fakes in tests: web defaults, as the app's options
    // have unless it changes them.
    private static readonly Lazy<JsonSerializerOptions> DefaultSerializerOptions =
        new(static () => SerializerOptionsSetup.Prepare(new JsonSerializerOptions(JsonSerializerDefaults.Web)));

    public required string RunId { get; init; }

    public required FunctionRunStatus Status { get; init; }

    // Inngest's own id of the function, a UUID, rather than the id the app registered it with.
    public required string FunctionId { get; init; }

    public int FunctionVersion { get; init; }

    // When the run was scheduled.
    public DateTimeOffset StartedAt { get; init; }

    // Null while the run goes on.
    public DateTimeOffset? EndedAt { get; init; }

    // The id that sending the triggering event returned; null for runs a cron schedule started.
    public string? EventId { get; init; }

    public string? BatchId { get; init; }

    // The run that this run replays.
    public string? OriginalRunId { get; init; }

    // The schedule that started the run.
    public string? Cron { get; init; }

    public string? EnvironmentId { get; init; }

    // What the function returned, or the error a failed run reported. Its ValueKind is Null or Undefined until the run
    // has ended.
    public JsonElement Output { get; init; }

    internal JsonSerializerOptions? SerializerOptions { get; init; }

    // Reads Output with the app's serializer options, as step results and function output are written. Default when
    // there is no output.
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

public enum FunctionRunStatus
{
    // A status this SDK does not know yet.
    Unknown,

    // Waiting to start. The dev server reports it in the runs of an event.
    Scheduled,

    Running,
    Completed,
    Failed,
    Cancelled,
}
