// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace Inngest.Json;

internal static class JsonPayload
{
    // What JsonSerializerOptions.MaxDepth 0 stands for.
    private const int DefaultMaxDepth = 64;

    // Inngest sends back what the SDK wrote a few levels deeper than it was written, a step's output under
    // steps.<id>.data, and other producers may nest event data further than this serializer would.
    private const int MinDocumentDepth = 256;
    private const int Nesting = 8;

    // How deep a document from Inngest may be, so that whatever the serializer could write can be read back.
    public static JsonDocumentOptions DocumentOptions(JsonSerializerOptions options) =>
        new() { MaxDepth = Math.Max(MinDocumentDepth, (options.MaxDepth == 0 ? DefaultMaxDepth : options.MaxDepth) + Nesting) };

    // Inngest decodes event data, event user and invoke data as JSON objects, and rejects anything else far from
    // the code that sent it. Values with custom converters are given the benefit of the doubt.
    // Throws "<part> of <owner> "<name>" must serialize to a JSON object", for example the data of event "shop/order".
    public static void EnsureObject(object? value, JsonSerializerOptions options, string part, string owner, string name)
    {
        if (value is null || IsObject(value, options))
        {
            return;
        }

        throw new ArgumentException($"The {part} of {owner} \"{name}\" must serialize to a JSON object, but {value.GetType().Name} does not.");
    }

    private static bool IsObject(object value, JsonSerializerOptions options) => value switch
    {
        JsonElement element => element.ValueKind == JsonValueKind.Object,
        JsonDocument document => document.RootElement.ValueKind == JsonValueKind.Object,
        JsonNode node => node is JsonObject,
        string or bool or char or decimal or Enum or DateTime or DateTimeOffset or Guid or TimeSpan => false,
        _ when value.GetType().IsPrimitive => false,
        _ => options.GetTypeInfo(value.GetType()).Kind is not JsonTypeInfoKind.Enumerable,
    };
}
