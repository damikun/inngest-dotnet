// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text.Json;
using Inngest.Internal;

namespace Inngest.Serving;

// The JSON a durable endpoint request becomes: the inngest/http.request event that Inngest creates the run with, and
// the call bodies the engine reads in place of a call from Inngest.
internal static class EndpointCalls
{
    public const string EventName = "inngest/http.request";

    // Inngest sends both on the call that resumes a run, at the endpoint's own URL. The dev server signs nothing and
    // sends the signature header empty.
    public const string RunIdHeader = "X-Run-ID";
    public const string ForceStepPlanHeader = "X-Inngest-Force-Step-Plan";

    // As in inngest-js, a durable endpoint retries a failed step three times unless it says otherwise.
    public const int DefaultRetries = 3;

    // Whether a request is Inngest resuming a run rather than a client starting one. The dev server signs nothing and
    // sends the signature header empty, which a proxy may drop, so in dev mode the run id is enough.
    public static bool IsResume(IServeRequest request, bool isDev) =>
        request.GetHeader(RunIdHeader) is { Length: > 0 } && (isDev || request.GetHeader(Headers.Signature) is { Length: > 0 });

    // The fields of inngest-js. Inngest reads the body as bytes, so it goes in base64, and sends it back unchanged
    // when it resumes the run, together with the method, the content type and the query.
    public static byte[] WriteEvent(EndpointRequest request, DateTimeOffset now)
    {
        using var buffer = new PooledBufferWriter(request.Body.Length * 4 / 3 + 512);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("name"u8, EventName);
            writer.WriteStartObject("data"u8);
            writer.WriteString("domain"u8, request.Domain);
            writer.WriteString("method"u8, request.Method);
            writer.WriteString("path"u8, request.Path);
            writer.WriteString("ip"u8, request.Ip);
            writer.WriteString("content_type"u8, request.ContentType);
            writer.WriteString("query_params"u8, request.Query);
            writer.WriteBase64String("body"u8, request.Body.Span);
            writer.WriteString("fn"u8, request.FunctionId);
            writer.WriteEndObject();
            writer.WriteNumber("ts"u8, now.ToUnixTimeMilliseconds());
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    // The first call of the run: the request's event, no memoized steps, and the attempts the retries allow.
    public static byte[] WriteStartCall(ReadOnlySpan<byte> @event, string runId, int retries)
    {
        using var buffer = new PooledBufferWriter(@event.Length * 2 + 256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("event"u8);
            writer.WriteRawValue(@event, skipInputValidation: true);
            writer.WriteStartArray("events"u8);
            writer.WriteRawValue(@event, skipInputValidation: true);
            writer.WriteEndArray();
            writer.WriteStartObject("steps"u8);
            writer.WriteEndObject();
            WriteContext(writer, runId, maxAttempts: retries + 1, disableImmediateExecution: false, useApi: false);
            writer.WriteNumber("version"u8, 2);
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    // A call from Inngest that resumes the run. Like inngest-js, the run's events and steps come from the API, and
    // Inngest, not the attempt count, decides when the run stops being retried.
    public static byte[] WriteResumeCall(string runId, bool disableImmediateExecution)
    {
        using var buffer = new PooledBufferWriter(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("event"u8);
            writer.WriteEndObject();
            writer.WriteStartArray("events"u8);
            writer.WriteEndArray();
            writer.WriteStartObject("steps"u8);
            writer.WriteEndObject();
            WriteContext(writer, runId, maxAttempts: null, disableImmediateExecution, useApi: true);
            writer.WriteNumber("version"u8, 2);
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteContext(Utf8JsonWriter writer, string runId, int? maxAttempts, bool disableImmediateExecution, bool useApi)
    {
        writer.WriteStartObject("ctx"u8);
        writer.WriteString("run_id"u8, runId);
        writer.WriteNumber("attempt"u8, 0);
        if (maxAttempts is { } attempts)
        {
            writer.WriteNumber("max_attempts"u8, attempts);
        }

        writer.WriteBoolean("disable_immediate_execution"u8, disableImmediateExecution);
        writer.WriteBoolean("use_api"u8, useApi);
        writer.WriteStartObject("stack"u8);
        writer.WriteStartArray("stack"u8);
        writer.WriteEndArray();
        writer.WriteNumber("current"u8, 0);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }
}

// What the event of a durable endpoint run records about the request. Domain is the origin Inngest calls back, such as
// "https://api.example.com"; Path keeps its escapes; Query has no leading "?".
internal readonly record struct EndpointRequest(
    string Domain,
    string Method,
    string Path,
    string Ip,
    string ContentType,
    string Query,
    ReadOnlyMemory<byte> Body,
    string FunctionId);
