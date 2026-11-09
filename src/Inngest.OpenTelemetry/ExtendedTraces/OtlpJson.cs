// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Inngest.OpenTelemetry.ExtendedTraces;

// Writes spans as an OTLP/HTTP JSON export request, the encoding the TypeScript SDK's exporter sends. Trace and span ids
// are hex, as OTLP/JSON has them, and Inngest reads them that way: the parent of each span it stores is the span id it
// read for the parent. 64-bit integers are strings, as protobuf's JSON mapping writes them.
internal static class OtlpJson
{
    private static readonly DateTime UnixEpoch = DateTime.UnixEpoch;

    public static void Write(IBufferWriter<byte> output, IReadOnlyList<KeyValuePair<string, object?>> resource, IEnumerable<Activity> spans)
    {
        using var writer = new Utf8JsonWriter(output);
        writer.WriteStartObject();
        writer.WriteStartArray("resourceSpans");
        writer.WriteStartObject();
        writer.WriteStartObject("resource");
        WriteAttributes(writer, resource);
        writer.WriteEndObject();
        writer.WriteStartArray("scopeSpans");

        foreach (var scope in spans.GroupBy(span => (span.Source.Name, span.Source.Version)))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("scope");
            writer.WriteString("name", scope.Key.Name);
            if (!string.IsNullOrEmpty(scope.Key.Version))
            {
                writer.WriteString("version", scope.Key.Version);
            }

            writer.WriteEndObject();
            writer.WriteStartArray("spans");
            foreach (var span in scope)
            {
                WriteSpan(writer, span);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteSpan(Utf8JsonWriter writer, Activity span)
    {
        writer.WriteStartObject();
        writer.WriteString("traceId", span.TraceId.ToHexString());
        writer.WriteString("spanId", span.SpanId.ToHexString());
        if (span.ParentSpanId != default)
        {
            writer.WriteString("parentSpanId", span.ParentSpanId.ToHexString());
        }

        if (!string.IsNullOrEmpty(span.TraceStateString))
        {
            writer.WriteString("traceState", span.TraceStateString);
        }

        writer.WriteString("name", span.DisplayName);
        writer.WriteNumber("kind", Kind(span.Kind));
        writer.WriteString("startTimeUnixNano", Nanoseconds(span.StartTimeUtc));
        writer.WriteString("endTimeUnixNano", Nanoseconds(span.StartTimeUtc + span.Duration));
        WriteAttributes(writer, span.TagObjects);

        writer.WriteStartArray("events");
        foreach (var @event in span.Events)
        {
            writer.WriteStartObject();
            writer.WriteString("timeUnixNano", Nanoseconds(@event.Timestamp.UtcDateTime));
            writer.WriteString("name", @event.Name);
            WriteAttributes(writer, @event.Tags);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteStartArray("links");
        foreach (var link in span.Links)
        {
            writer.WriteStartObject();
            writer.WriteString("traceId", link.Context.TraceId.ToHexString());
            writer.WriteString("spanId", link.Context.SpanId.ToHexString());
            WriteAttributes(writer, link.Tags ?? []);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteStartObject("status");
        writer.WriteNumber("code", span.Status switch
        {
            ActivityStatusCode.Ok => 1,
            ActivityStatusCode.Error => 2,
            _ => 0,
        });
        if (span.Status == ActivityStatusCode.Error && !string.IsNullOrEmpty(span.StatusDescription))
        {
            writer.WriteString("message", span.StatusDescription);
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteAttributes(Utf8JsonWriter writer, IEnumerable<KeyValuePair<string, object?>> attributes)
    {
        writer.WriteStartArray("attributes");
        foreach (var (key, value) in attributes)
        {
            if (value is null)
            {
                continue;
            }

            writer.WriteStartObject();
            writer.WriteString("key", key);
            writer.WritePropertyName("value");
            WriteValue(writer, value);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    // OTLP's AnyValue. Arrays become array values, and anything else that is not a primitive its invariant string.
    private static void WriteValue(Utf8JsonWriter writer, object value)
    {
        writer.WriteStartObject();
        switch (value)
        {
            case string text:
                writer.WriteString("stringValue", text);
                break;
            case bool flag:
                writer.WriteBoolean("boolValue", flag);
                break;
            case byte or sbyte or short or ushort or int or uint or long:
                writer.WriteString("intValue", Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture));
                break;
            case float or double or decimal:
                writer.WriteNumber("doubleValue", Convert.ToDouble(value, CultureInfo.InvariantCulture));
                break;
            case System.Collections.IEnumerable items and not System.Collections.IDictionary:
                writer.WriteStartObject("arrayValue");
                writer.WriteStartArray("values");
                foreach (var item in items)
                {
                    if (item is not null)
                    {
                        WriteValue(writer, item);
                    }
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
                break;
            default:
                writer.WriteString("stringValue", Convert.ToString(value, CultureInfo.InvariantCulture));
                break;
        }

        writer.WriteEndObject();
    }

    // SPAN_KIND_INTERNAL is 1; ActivityKind starts at Internal = 0, in the same order.
    private static int Kind(ActivityKind kind) => (int)kind + 1;

    private static string Nanoseconds(DateTime utc) =>
        ((utc - UnixEpoch).Ticks * 100).ToString(CultureInfo.InvariantCulture);
}
