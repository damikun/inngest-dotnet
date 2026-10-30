// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Buffers;
using System.Text;
using System.Text.Json;
using Inngest.Realtime;

namespace Inngest.Testing;

// Takes the place of Inngest's realtime API: published messages and streams are recorded, tokens are placeholders that
// no Inngest accepts, and subscribing fails, since nothing is published anywhere a subscriber could read it.
internal sealed class RecordingRealtimeApi(InngestTestHost host) : IRealtimeApi
{
    public const string PlaceholderToken = "test-host-token";

    public Task PublishAsync(RealtimeTopic topic, string? runId, ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
    {
        host.RecordPublished(new PublishedMessage(topic, Read(body)), RunDriver.Current);
        return Task.CompletedTask;
    }

    public async Task<string> PublishStreamAsync(RealtimeTopic topic, string? runId, IAsyncEnumerable<string> chunks, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        await foreach (var chunk in chunks.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            text.Append(chunk);
        }

        host.RecordPublished(new PublishedMessage(topic, Text(text.ToString())) { IsStream = true }, RunDriver.Current);
        return text.ToString();
    }

    public Task<string> GetTokenAsync(string channel, IReadOnlyList<string> topics, CancellationToken cancellationToken) =>
        Task.FromResult(PlaceholderToken);

    public Task<HttpResponseMessage> OpenSubscriptionAsync(string token, CancellationToken cancellationToken) =>
        Task.FromException<HttpResponseMessage>(new InvalidOperationException(
            "The test host records realtime messages in run.Published and host.Published and delivers them to no subscriber."));

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
            return Text(Encoding.UTF8.GetString(body.Span));
        }
    }

    private static JsonElement Text(string text)
    {
        var json = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(json))
        {
            writer.WriteStringValue(text);
        }

        using var document = JsonDocument.Parse(json.WrittenMemory);
        return document.RootElement.Clone();
    }
}
