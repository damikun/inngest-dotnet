// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Inngest.Connect.Protocol;

namespace Inngest.Connect;

// A server span for each executor request, parented to the trace context Inngest sends with it, as ASP.NET Core parents
// a request's span to its traceparent header. The source has the name of Inngest.OpenTelemetry's source, so a single
// AddSource collects both, and the spans of that package for the call nest under this one.
internal static class ConnectTelemetry
{
    public const string RequestSpan = "inngest.connect.request";

    private static readonly ActivitySource Source = new(
        "inngest",
        typeof(ConnectTelemetry).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]);

    public static Activity? StartRequest(GatewayExecutorRequestData data)
    {
        if (!Source.HasListeners())
        {
            return null;
        }

        var activity = Source.StartActivity(RequestSpan, ActivityKind.Server, ParentOf(data.UserTraceCtx.Span));
        if (activity is { IsAllDataRequested: true })
        {
            activity.SetTag("sys.function.slug", data.FunctionSlug);
            activity.SetTag("sdk.run.id", data.RunId);
        }

        return activity;
    }

    // user_trace_ctx holds the traceparent and tracestate headers that Inngest would send over HTTP, as JSON.
    private static ActivityContext ParentOf(ReadOnlySpan<byte> json)
    {
        string? traceparent = null;
        string? tracestate = null;
        try
        {
            var reader = new Utf8JsonReader(json);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return default;
            }

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var isParent = reader.ValueTextEquals("traceparent"u8);
                var isState = !isParent && reader.ValueTextEquals("tracestate"u8);
                if (!reader.Read())
                {
                    break;
                }

                if (reader.TokenType != JsonTokenType.String)
                {
                    // Objects and arrays are skipped whole; other values are a single token.
                    reader.Skip();
                }
                else if (isParent)
                {
                    traceparent = reader.GetString();
                }
                else if (isState)
                {
                    tracestate = reader.GetString();
                }
            }
        }
        catch (JsonException)
        {
            return default;
        }

        return ActivityContext.TryParse(traceparent, tracestate, isRemote: true, out var parent) ? parent : default;
    }
}
