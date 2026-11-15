// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Inngest.Json;

// System.Text.Json 8 reads a type discriminator only as the first property of its object, and Inngest sends every
// object with its keys sorted. Where JsonSerializerOptions has no AllowOutOfOrderMetadataProperties, the
// discriminators a type can meet are moved to the front of their objects before the value is read.
internal static class DiscriminatorOrder
{
    private static readonly ConditionalWeakTable<JsonTypeInfo, string[]> Names = new();

    // The value with the type's discriminators first in every object, or the value itself when none has to move.
    public static JsonElement MoveFirst(JsonElement value, JsonTypeInfo typeInfo)
    {
        var names = Names.GetValue(typeInfo, static info => Collect(info));
        if (names.Length == 0 || !NeedsMove(value, names))
        {
            return value;
        }

        var maxDepth = JsonPayload.DocumentOptions(typeInfo.Options).MaxDepth;
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { MaxDepth = maxDepth }))
        {
            Write(writer, value, names);
        }

        var reader = new Utf8JsonReader(buffer.WrittenSpan, new JsonReaderOptions { MaxDepth = maxDepth });
        return JsonElement.ParseValue(ref reader);
    }

    // The discriminator names of every polymorphic type the type can hold, through its properties, its derived types
    // and the type arguments of its collections. .NET 8 does not say what a collection holds, so a collection that is
    // not generic itself and does not derive from a generic type is not looked into.
    private static string[] Collect(JsonTypeInfo root)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<Type>();
        var pending = new Stack<Type>();
        pending.Push(root.Type);
        while (pending.TryPop(out var type))
        {
            if (!seen.Add(type) || Info(root.Options, type) is not { } info)
            {
                continue;
            }

            if (info.PolymorphismOptions is { } polymorphism)
            {
                names.Add(polymorphism.TypeDiscriminatorPropertyName);
                foreach (var derived in polymorphism.DerivedTypes)
                {
                    pending.Push(derived.DerivedType);
                }
            }

            if (info.Kind == JsonTypeInfoKind.Object)
            {
                foreach (var property in info.Properties)
                {
                    pending.Push(property.PropertyType);
                }
            }
            else if (info.Kind is JsonTypeInfoKind.Enumerable or JsonTypeInfoKind.Dictionary)
            {
                for (var current = type; current is not null; current = current.BaseType)
                {
                    if (current.GetElementType() is { } element)
                    {
                        pending.Push(element);
                    }

                    foreach (var argument in current.GenericTypeArguments)
                    {
                        pending.Push(argument);
                    }
                }
            }
        }

        return [.. names];
    }

    // A type the serializer cannot handle fails where it is read, with the serializer's own message.
    private static JsonTypeInfo? Info(JsonSerializerOptions options, Type type)
    {
        try
        {
            return options.GetTypeInfo(type);
        }
        catch (Exception exception) when (exception is NotSupportedException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private static bool NeedsMove(JsonElement value, string[] names)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                if (NeedsMove(item, names))
                {
                    return true;
                }
            }
        }
        else if (value.ValueKind == JsonValueKind.Object)
        {
            var first = true;
            foreach (var property in value.EnumerateObject())
            {
                if ((!first && IsDiscriminator(property, names)) || NeedsMove(property.Value, names))
                {
                    return true;
                }

                first = false;
            }
        }

        return false;
    }

    private static void Write(Utf8JsonWriter writer, JsonElement value, string[] names)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in value.EnumerateArray())
            {
                Write(writer, item, names);
            }

            writer.WriteEndArray();
        }
        else if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            WriteProperties(writer, value, names, discriminators: true);
            WriteProperties(writer, value, names, discriminators: false);
            writer.WriteEndObject();
        }
        else
        {
            value.WriteTo(writer);
        }
    }

    private static void WriteProperties(Utf8JsonWriter writer, JsonElement value, string[] names, bool discriminators)
    {
        foreach (var property in value.EnumerateObject())
        {
            if (IsDiscriminator(property, names) == discriminators)
            {
                writer.WritePropertyName(property.Name);
                Write(writer, property.Value, names);
            }
        }
    }

    private static bool IsDiscriminator(JsonProperty property, string[] names)
    {
        foreach (var name in names)
        {
            if (property.NameEquals(name))
            {
                return true;
            }
        }

        return false;
    }
}
