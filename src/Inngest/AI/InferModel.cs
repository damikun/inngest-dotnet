// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Inngest;

/// <summary>
/// A model of a provider, as <see cref="AIModels"/> makes it: where Inngest sends the requests of an inference step,
/// with which key and headers, and what every request gets unless it says otherwise. Pass it to
/// <c>InferAsync(id, model, request)</c>, which reads the response as <typeparamref name="TResponse"/>.
/// </summary>
/// <typeparam name="TRequest">The body of a request in the provider's format.</typeparam>
/// <typeparam name="TResponse">The provider's response.</typeparam>
public sealed class InferModel<TRequest, TResponse>
    where TRequest : class
{
    private const string ModelProperty = "model";

    private readonly JsonTypeInfo<TRequest> _requestType;
    private readonly JsonTypeInfo<TResponse> _responseType;
    private readonly JsonElement? _defaults;

    // Nested objects whose properties merge with those of the defaults instead of replacing them, as inngest-js merges
    // Gemini's generationConfig and its thinkingConfig.
    private readonly string[] _mergedObjects;

    // Whether the body names the model; Gemini's URL does instead.
    private readonly bool _bodyHasModel;

    internal InferModel(
        AIFormat format,
        Uri url,
        string authKey,
        IReadOnlyDictionary<string, string>? headers,
        string? model,
        TRequest? defaultParameters,
        JsonTypeInfo<TRequest> requestType,
        JsonTypeInfo<TResponse> responseType,
        bool bodyHasModel = true,
        string[]? mergedObjects = null)
    {
        Format = format;
        Url = url;
        AuthKey = authKey;
        Headers = headers;
        Model = model;
        DefaultParameters = defaultParameters;
        _requestType = requestType;
        _responseType = responseType;
        _bodyHasModel = bodyHasModel;
        _mergedObjects = mergedObjects ?? [];
        _defaults = defaultParameters is null ? null : JsonSerializer.SerializeToElement(defaultParameters, requestType);
    }

    /// <summary>The provider API Inngest calls.</summary>
    public AIFormat Format { get; }

    /// <summary>The address of the API, base URL and path included.</summary>
    public Uri Url { get; }

    /// <summary>
    /// The API key, from the options or the provider's environment variable; empty when neither has one. Inngest stores it
    /// with the run.
    /// </summary>
    public string AuthKey { get; }

    /// <summary>Headers Inngest sends with every request, such as anthropic-version.</summary>
    public IReadOnlyDictionary<string, string>? Headers { get; }

    /// <summary>The model a request gets when it names none.</summary>
    public string? Model { get; }

    /// <summary>What a request gets for every property it leaves unset.</summary>
    public TRequest? DefaultParameters { get; }

    /// <summary>
    /// The options of an inference step that sends <paramref name="request"/>: the default parameters fill in what it
    /// leaves unset, and the model when it names none. Properties the request sets win over the defaults.
    /// </summary>
    public InferOptions CreateOptions(TRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var body = JsonSerializer.SerializeToElement(request, _requestType);
        return new InferOptions(Format, Url, Merge(body))
        {
            AuthKey = AuthKey,
            Headers = Headers,
            ResponseType = _responseType,
        };
    }

    private JsonElement Merge(JsonElement body)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            WriteMerged(writer, body, _defaults, depth: 0);
            if (_bodyHasModel && Model is not null && !HasValue(body, ModelProperty, depth: 0) && !HasValue(_defaults, ModelProperty, depth: 0))
            {
                writer.WriteString(ModelProperty, Model);
            }

            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    // The properties of the body, then those of the defaults it lacks. A null, and an empty model, count as unset.
    private void WriteMerged(Utf8JsonWriter writer, JsonElement body, JsonElement? defaults, int depth)
    {
        foreach (var property in body.EnumerateObject())
        {
            if (IsUnset(property.Name, property.Value, depth))
            {
                continue;
            }

            if (depth < _mergedObjects.Length
                && property.NameEquals(_mergedObjects[depth])
                && property.Value.ValueKind == JsonValueKind.Object
                && defaults is { } outer
                && outer.TryGetProperty(property.Name, out var inner)
                && inner.ValueKind == JsonValueKind.Object)
            {
                writer.WriteStartObject(property.Name);
                WriteMerged(writer, property.Value, inner, depth + 1);
                writer.WriteEndObject();
                continue;
            }

            property.WriteTo(writer);
        }

        if (defaults is not { ValueKind: JsonValueKind.Object } values)
        {
            return;
        }

        foreach (var property in values.EnumerateObject())
        {
            if (!IsUnset(property.Name, property.Value, depth) && !HasValue(body, property.Name, depth))
            {
                property.WriteTo(writer);
            }
        }
    }

    private static bool HasValue(JsonElement? json, string name, int depth) =>
        json is { ValueKind: JsonValueKind.Object } value
        && value.TryGetProperty(name, out var property)
        && !IsUnset(name, property, depth);

    private static bool IsUnset(string name, JsonElement value, int depth) =>
        value.ValueKind == JsonValueKind.Null
        || (depth == 0 && name == ModelProperty && value.ValueKind == JsonValueKind.String && value.GetString()!.Length == 0);
}
