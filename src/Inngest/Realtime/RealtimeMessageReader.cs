// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Globalization;
using System.Text.Json;

namespace Inngest.Realtime;

// Turns the JSON of Inngest's realtime messages into RealtimeMessage<T>. A message is {"kind", "data", "channel",
// "topic", "run_id", "created_at"}; a stream is a "datastream-start" and a "datastream-end" whose data is the stream id,
// and "chunk" messages in between, {"kind": "chunk", "stream_id", "data"}, which name no channel or topic, so the reader
// keeps those of each stream from its start. A chunk of a stream that started before the subscription is skipped.
internal sealed class RealtimeMessageReader<T>(Func<JsonElement, T?> readData, bool readDataOfEveryKind)
{
    private readonly Dictionary<string, (string Channel, string Topic)> _streams = new(StringComparer.Ordinal);

    public RealtimeMessage<T>? Read(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var runId = Text(root, "run_id");
        if (Text(root, "kind") is "chunk")
        {
            return Text(root, "stream_id") is { } chunkStream && _streams.TryGetValue(chunkStream, out var origin)
                ? new RealtimeMessage<T>(RealtimeMessageKind.StreamChunk, origin.Channel, origin.Topic)
                {
                    StreamId = chunkStream,
                    Text = Text(root, "data") ?? string.Empty,
                    RunId = runId,
                }
                : null;
        }

        var kind = Text(root, "kind") switch
        {
            "data" => RealtimeMessageKind.Data,
            "datastream-start" => RealtimeMessageKind.StreamStart,
            "datastream-end" => RealtimeMessageKind.StreamEnd,
            "run" => RealtimeMessageKind.Run,
            _ => RealtimeMessageKind.Other,
        };
        var channel = Text(root, "channel") ?? string.Empty;
        var topic = Text(root, "topic") ?? string.Empty;
        var hasData = root.TryGetProperty("data", out var data);
        string? streamId = null;
        if (kind is RealtimeMessageKind.StreamStart or RealtimeMessageKind.StreamEnd)
        {
            streamId = hasData && data.ValueKind == JsonValueKind.String ? data.GetString() : null;
            if (streamId is null)
            {
                return null;
            }

            if (kind == RealtimeMessageKind.StreamStart)
            {
                _streams[streamId] = (channel, topic);
            }
            else
            {
                _streams.Remove(streamId);
            }
        }

        return new RealtimeMessage<T>(kind, channel, topic)
        {
            Data = hasData && (kind == RealtimeMessageKind.Data || readDataOfEveryKind) ? readData(data) : default,
            StreamId = streamId,
            RunId = runId,
            CreatedAt = Time(Text(root, "created_at")),
        };
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    // Inngest writes nanoseconds, two digits more than DateTimeOffset holds.
    private static DateTimeOffset? Time(string? text)
    {
        if (text is null)
        {
            return null;
        }

        var dot = text.IndexOf('.', StringComparison.Ordinal);
        if (dot >= 0)
        {
            var end = dot + 1;
            while (end < text.Length && char.IsAsciiDigit(text[end]))
            {
                end++;
            }

            if (end - dot - 1 > 7)
            {
                text = string.Concat(text.AsSpan(0, dot + 8), text.AsSpan(end));
            }
        }

        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time) ? time : null;
    }
}
