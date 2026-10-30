// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Buffers;
using System.Text;
using System.Text.Json;
using Inngest.Realtime;

namespace Inngest.Testing;

// Takes the place of Inngest's realtime API: published messages are recorded, and tokens are placeholders that no
// Inngest accepts.
internal sealed class RecordingRealtimeApi(InngestTestHost host) : IRealtimeApi
{
    public const string PlaceholderToken = "test-host-token";

    public Task PublishAsync(RealtimeTopic topic, string? runId, ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
    {
        host.RecordPublished(new PublishedMessage(topic, Read(body)), RunDriver.Current);
        return Task.CompletedTask;
    }

    public Task<string> GetTokenAsync(string channel, IReadOnlyList<string> topics, CancellationToken cancellationToken) =>
        Task.FromResult(PlaceholderToken);

    // Text goes out as it is, so a message that is not JSON was a string.
    private static JsonElement Read(ReadOnlyMemory<byte> body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            var text = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(text))
            {
                writer.WriteStringValue(Encoding.UTF8.GetString(body.Span));
            }

            using var document = JsonDocument.Parse(text.WrittenMemory);
            return document.RootElement.Clone();
        }
    }
}
