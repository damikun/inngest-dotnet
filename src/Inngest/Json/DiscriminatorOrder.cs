// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Inngest.Json;

// System.Text.Json 8 reads a type discriminator only as the first property of its object, and Inngest sends every
// object with its keys sorted. Where JsonSerializerOptions has no AllowOutOfOrderMetadataProperties, the value is walked
// along the type it is read as, and each object of a polymorphic type gets that type's discriminator moved to its front.
internal static class DiscriminatorOrder
{
    private static readonly ConditionalWeakTable<JsonTypeInfo, Shape> Shapes = new();

    // The value with each discriminator first in its object, or the value itself when none has to move.
    public static JsonElement MoveFirst(JsonElement value, JsonTypeInfo typeInfo)
    {
        if (!MeetsDiscriminators(typeInfo) || !NeedsMove(value, typeInfo))
        {
            return value;
        }

        var maxDepth = JsonPayload.DocumentOptions(typeInfo.Options).MaxDepth;
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { MaxDepth = maxDepth }))
        {
            Write(writer, value, typeInfo);
        }

        var reader = new Utf8JsonReader(buffer.WrittenSpan, new JsonReaderOptions { MaxDepth = maxDepth });
        return JsonElement.ParseValue(ref reader);
    }

    private static Shape ShapeOf(JsonTypeInfo info) => Shapes.GetValue(info, static info => new Shape(info));

    // Whether a polymorphic type can be met anywhere through the type's properties, derived types and collections,
    // worked out once per type, so that the values of types without one are not walked.
    private static bool MeetsDiscriminators(JsonTypeInfo root)
    {
        var shape = ShapeOf(root);
        if (shape.Meets is { } known)
        {
            return known;
        }

        var meets = false;
        var seen = new HashSet<JsonTypeInfo>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<JsonTypeInfo>();
        pending.Push(root);
        while (!meets && pending.TryPop(out var info))
        {
            if (!seen.Add(info))
            {
                continue;
            }

            var current = ShapeOf(info);
            meets = current.Discriminator is not null;
            foreach (var next in current.Reachable())
            {
                pending.Push(next);
            }
        }

        shape.Meets = meets;
        return meets;
    }

    private static bool NeedsMove(JsonElement value, JsonTypeInfo info)
    {
        var shape = ShapeOf(info);
        if (value.ValueKind == JsonValueKind.Array)
        {
            if (shape.Items is { } items)
            {
                foreach (var item in value.EnumerateArray())
                {
                    if (NeedsMove(item, items))
                    {
                        return true;
                    }
                }
            }
        }
        else if (value.ValueKind == JsonValueKind.Object)
        {
            var members = shape.Resolve(value, out var discriminator);
            if (discriminator > 0)
            {
                return true;
            }

            foreach (var property in value.EnumerateObject())
            {
                if (members.Member(property) is { } member && NeedsMove(property.Value, member))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static void Write(Utf8JsonWriter writer, JsonElement value, JsonTypeInfo? info)
    {
        if (info is null)
        {
            value.WriteTo(writer);
            return;
        }

        var shape = ShapeOf(info);
        if (value.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in value.EnumerateArray())
            {
                Write(writer, item, shape.Items);
            }

            writer.WriteEndArray();
        }
        else if (value.ValueKind == JsonValueKind.Object)
        {
            var members = shape.Resolve(value, out var discriminator);
            writer.WriteStartObject();
            var index = 0;
            if (discriminator >= 0)
            {
                foreach (var property in value.EnumerateObject())
                {
                    if (index++ == discriminator)
                    {
                        property.WriteTo(writer);
                        break;
                    }
                }
            }

            index = 0;
            foreach (var property in value.EnumerateObject())
            {
                if (index++ != discriminator)
                {
                    writer.WritePropertyName(property.Name);
                    Write(writer, property.Value, members.Member(property));
                }
            }

            writer.WriteEndObject();
        }
        else
        {
            value.WriteTo(writer);
        }
    }

    // A type as far as reading it goes: its discriminator and derived types if it is polymorphic, and the types of its
    // properties, items or values.
    private sealed class Shape
    {
        private readonly Dictionary<object, JsonTypeInfo>? _derived;
        private readonly Dictionary<string, JsonTypeInfo>? _properties;
        private readonly JsonTypeInfo? _values;

        // 0 until MoveFirst has looked through the type, then 1 when it meets no discriminator and 2 when it does.
        private int _meets;

        public Shape(JsonTypeInfo info)
        {
            var options = info.Options;
            if (Nullable.GetUnderlyingType(info.Type) is { } underlying)
            {
                info = Info(options, underlying) ?? info;
            }

            if (info.Kind == JsonTypeInfoKind.None)
            {
                return;
            }

            if (info.PolymorphismOptions is { } polymorphism)
            {
                Discriminator = polymorphism.TypeDiscriminatorPropertyName;
                _derived = [];
                foreach (var derived in polymorphism.DerivedTypes)
                {
                    if (derived.TypeDiscriminator is { } id && Info(options, derived.DerivedType) is { } derivedInfo)
                    {
                        _derived[id] = derivedInfo;
                    }
                }
            }

            switch (info.Kind)
            {
                case JsonTypeInfoKind.Object:
                    _properties = new(options.PropertyNameCaseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
                    foreach (var property in info.Properties)
                    {
                        // A converter of the property's own reads it however it likes.
                        if (property.CustomConverter is null && !property.IsExtensionData && Info(options, property.PropertyType) is { } propertyInfo)
                        {
                            _properties.TryAdd(property.Name, propertyInfo);
                        }
                    }

                    break;

                case JsonTypeInfoKind.Enumerable:
                    Items = ElementInfo(info);
                    break;

                case JsonTypeInfoKind.Dictionary:
                    _values = ElementInfo(info);
                    break;
            }
        }

        public string? Discriminator { get; }

        public JsonTypeInfo? Items { get; }

        // Two threads that find it unset both work it out, to the same answer.
        public bool? Meets
        {
            get => Volatile.Read(ref _meets) switch
            {
                0 => null,
                var meets => meets == 2,
            };
            set => Volatile.Write(ref _meets, value is true ? 2 : 1);
        }

        // The shape the members of the object are read by, which is that of the derived type its discriminator names or
        // this one, and the position of the discriminator among its properties, -1 without one. As in System.Text.Json,
        // a derived type is not looked into for a discriminator of its own.
        public Shape Resolve(JsonElement value, out int discriminator)
        {
            discriminator = -1;
            if (Discriminator is null)
            {
                return this;
            }

            var index = 0;
            foreach (var property in value.EnumerateObject())
            {
                if (property.NameEquals(Discriminator))
                {
                    discriminator = index;
                    return Derived(property.Value) is { } derived ? ShapeOf(derived) : this;
                }

                index++;
            }

            return this;
        }

        public JsonTypeInfo? Member(JsonProperty property) =>
            _values ?? (_properties is not null && _properties.TryGetValue(property.Name, out var info) ? info : null);

        public IEnumerable<JsonTypeInfo> Reachable()
        {
            IEnumerable<JsonTypeInfo> reachable = [];
            if (_derived is not null)
            {
                reachable = reachable.Concat(_derived.Values);
            }

            if (_properties is not null)
            {
                reachable = reachable.Concat(_properties.Values);
            }

            if ((Items ?? _values) is { } element)
            {
                reachable = reachable.Append(element);
            }

            return reachable;
        }

        // String and integer discriminators, the two kinds System.Text.Json has.
        private JsonTypeInfo? Derived(JsonElement id) => id.ValueKind switch
        {
            JsonValueKind.String => _derived!.GetValueOrDefault(id.GetString()!),
            JsonValueKind.Number when id.TryGetInt32(out var number) => _derived!.GetValueOrDefault(number),
            _ => null,
        };

        private static JsonTypeInfo? ElementInfo(JsonTypeInfo info) => ElementType(info) is { } type ? Info(info.Options, type) : null;
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

#if NET9_0_OR_GREATER
    private static Type? ElementType(JsonTypeInfo info) => info.ElementType;
#else
    // System.Text.Json 8 knows what a collection holds, but keeps it internal. Later versions have the same property public.
    private static Type? ElementType(JsonTypeInfo info)
    {
        try
        {
            return GetElementType(info);
        }
        catch (MissingMemberException)
        {
            return null;
        }
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_ElementType")]
    private static extern Type? GetElementType(JsonTypeInfo info);
#endif
}
