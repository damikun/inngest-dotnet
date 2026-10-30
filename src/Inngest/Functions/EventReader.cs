// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Inngest.Events;
using Inngest.Middleware;

namespace Inngest.Functions;

internal static class EventReader
{
    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement;

    // Reads a triggering event once the payload transforms of the call's middleware have rewritten it. What is read
    // is copied out, so the rewritten JSON is released right away.
    public static InngestEvent<TData> Read<TData>(JsonElement element, JsonSerializerOptions options, IPayloadTransform[]? transforms)
    {
        if (transforms is null)
        {
            return Read<TData>(element, options);
        }

        using var transformed = PayloadTransforms.TransformEvent(transforms, element, options);
        return Read<TData>(transformed.Value, options);
    }

    public static InngestEvent<TData> Read<TData>(JsonElement element, JsonSerializerOptions options)
    {
        var typeInfo = (JsonTypeInfo<TData>)options.GetTypeInfo(typeof(TData));

        // Inngest treats a missing or null data field as an empty object.
        var data = element.TryGetProperty("data", out var dataElement) && dataElement.ValueKind != JsonValueKind.Null
            ? dataElement
            : EmptyObject;

        return new InngestEvent<TData>
        {
            Name = element.TryGetProperty("name", out var name) ? name.GetString() ?? string.Empty : string.Empty,
            Data = data.Deserialize(typeInfo)!,
            Id = element.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null,
            User = element.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object && user.EnumerateObject().MoveNext()
                ? user.Clone()
                : null,
            Timestamp = element.TryGetProperty("ts", out var ts) && ts.TryGetInt64(out var milliseconds)
                ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds)
                : default,
            Version = element.TryGetProperty("v", out var version) && version.ValueKind == JsonValueKind.String ? version.GetString() : null,
            Meta = element.TryGetProperty("meta", out var meta) ? EventSessions.ReadMeta(meta) : null,
        };
    }
}
