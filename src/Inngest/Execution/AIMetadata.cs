// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Inngest.Execution;

// The "inngest.ai" metadata of a step: what the OpenTelemetry GenAI attributes (gen_ai.*) of the model calls made in
// its handler say, as inngest-js reads them (execution/otel/aiExtractor.ts). Only the attributes below are read, so
// prompts, messages and tool calls never leave the app this way. Token counts and latency add up over the calls of a
// step; everything else is that of the last call.
internal sealed class AIMetadata : MetadataValues
{
    private static readonly (string Attribute, string? Fallback, JsonEncodedText Key)[] TextFields =
    [
        ("gen_ai.request.model", null, JsonEncodedText.Encode("request_model")),
        ("gen_ai.response.model", null, JsonEncodedText.Encode("response_model")),

        // gen_ai.system is what gen_ai.provider.name was called before.
        ("gen_ai.provider.name", "gen_ai.system", JsonEncodedText.Encode("provider")),
        ("gen_ai.operation.name", null, JsonEncodedText.Encode("operation_name")),
        ("gen_ai.response.id", null, JsonEncodedText.Encode("response_id")),
    ];

    private const string FinishReasonsAttribute = "gen_ai.response.finish_reasons";
    private static readonly JsonEncodedText FinishReasonsKey = JsonEncodedText.Encode("finish_reasons");

    private static readonly (string Attribute, bool Sum, JsonEncodedText Key)[] NumberFields =
    [
        ("gen_ai.usage.input_tokens", true, JsonEncodedText.Encode("input_tokens")),
        ("gen_ai.usage.output_tokens", true, JsonEncodedText.Encode("output_tokens")),
        ("gen_ai.usage.total_tokens", true, JsonEncodedText.Encode("total_tokens")),
        ("gen_ai.usage.cache_read.input_tokens", true, JsonEncodedText.Encode("cache_read_tokens")),
        ("gen_ai.usage.cache_creation.input_tokens", true, JsonEncodedText.Encode("cache_creation_tokens")),
        ("gen_ai.usage.reasoning.output_tokens", true, JsonEncodedText.Encode("reasoning_tokens")),

        // What the request asked for, so a later call replaces them.
        ("gen_ai.request.temperature", false, JsonEncodedText.Encode("temperature")),
        ("gen_ai.request.top_p", false, JsonEncodedText.Encode("top_p")),
        ("gen_ai.request.max_tokens", false, JsonEncodedText.Encode("max_tokens")),
        ("gen_ai.request.frequency_penalty", false, JsonEncodedText.Encode("frequency_penalty")),
        ("gen_ai.request.presence_penalty", false, JsonEncodedText.Encode("presence_penalty")),
        ("gen_ai.request.seed", false, JsonEncodedText.Encode("seed")),
    ];

    private static readonly JsonEncodedText LatencyKey = JsonEncodedText.Encode("latency_ms");

    private readonly string?[] _texts = new string?[TextFields.Length];
    private readonly double?[] _numbers = new double?[NumberFields.Length];
    private string[]? _finishReasons;
    private double? _latencyMs;

    private AIMetadata()
    {
    }

    // Null for an activity without any of the attributes, which is most of them. The latency alone does not count,
    // since every activity has one.
    public static AIMetadata? From(Activity activity)
    {
        if (!HasGenAITag(activity))
        {
            return null;
        }

        var metadata = new AIMetadata();
        var found = false;
        for (var i = 0; i < TextFields.Length; i++)
        {
            var (attribute, fallback, _) = TextFields[i];
            metadata._texts[i] = ReadText(activity.GetTagItem(attribute)) ?? (fallback is null ? null : ReadText(activity.GetTagItem(fallback)));
            found |= metadata._texts[i] is not null;
        }

        metadata._finishReasons = ReadList(activity.GetTagItem(FinishReasonsAttribute));
        found |= metadata._finishReasons is not null;
        for (var i = 0; i < NumberFields.Length; i++)
        {
            metadata._numbers[i] = ReadNumber(activity.GetTagItem(NumberFields[i].Attribute));
            found |= metadata._numbers[i] is not null;
        }

        if (!found)
        {
            return null;
        }

        metadata._latencyMs = activity.Duration.TotalMilliseconds;
        return metadata;
    }

    // Folds a later call of the same step into this one.
    public void Add(AIMetadata later)
    {
        for (var i = 0; i < _texts.Length; i++)
        {
            _texts[i] = later._texts[i] ?? _texts[i];
        }

        _finishReasons = later._finishReasons ?? _finishReasons;
        for (var i = 0; i < _numbers.Length; i++)
        {
            if (later._numbers[i] is not { } value)
            {
                continue;
            }

            _numbers[i] = NumberFields[i].Sum ? _numbers[i].GetValueOrDefault() + value : value;
        }

        if (later._latencyMs is { } latency)
        {
            _latencyMs = _latencyMs.GetValueOrDefault() + latency;
        }
    }

    public override void Write(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        for (var i = 0; i < _texts.Length; i++)
        {
            if (_texts[i] is { } text)
            {
                writer.WriteString(TextFields[i].Key, text);
            }
        }

        if (_finishReasons is { } reasons)
        {
            writer.WriteStartArray(FinishReasonsKey);
            foreach (var reason in reasons)
            {
                writer.WriteStringValue(reason);
            }

            writer.WriteEndArray();
        }

        for (var i = 0; i < _numbers.Length; i++)
        {
            if (_numbers[i] is { } number)
            {
                writer.WriteNumber(NumberFields[i].Key, number);
            }
        }

        if (_latencyMs is { } latency)
        {
            writer.WriteNumber(LatencyKey, latency);
        }

        writer.WriteEndObject();
    }

    private static bool HasGenAITag(Activity activity)
    {
        foreach (var tag in activity.TagObjects)
        {
            if (tag.Key.StartsWith("gen_ai.", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string? ReadText(object? value) => value switch
    {
        null => null,
        string text => text.Length == 0 ? null : text,
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => null,
    };

    // Numbers may come as any numeric type, or as text, as OTLP/JSON writes 64-bit integers.
    private static double? ReadNumber(object? value)
    {
        var number = value switch
        {
            string text => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : (double?)null,
            bool => null,

            // Microsoft.Extensions.AI has the temperature as a float, whose 0.2 would otherwise become 0.200000003.
            float single => double.Parse(single.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture),
            IConvertible convertible => convertible.GetTypeCode() is >= TypeCode.SByte and <= TypeCode.Decimal
                ? convertible.ToDouble(CultureInfo.InvariantCulture)
                : null,
            _ => null,
        };

        return number is { } finite && double.IsFinite(finite) ? finite : null;
    }

    // Microsoft.Extensions.AI writes the finish reasons as the text of a JSON array, such as ["stop"]; OpenTelemetry
    // has string arrays for this.
    private static string[]? ReadList(object? value)
    {
        IEnumerable<string?>? items = value switch
        {
            string text when text.StartsWith('[') => ParseArray(text),
            string text => [text],
            IEnumerable<string?> strings => strings,
            _ => null,
        };

        if (items is null)
        {
            return null;
        }

        var list = items.Where(static item => !string.IsNullOrEmpty(item)).Select(static item => item!).ToArray();
        return list.Length > 0 ? list : null;
    }

    private static List<string?>? ParseArray(string text)
    {
        try
        {
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(text));
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray)
            {
                return null;
            }

            var items = new List<string?>();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType == JsonTokenType.String)
                {
                    items.Add(reader.GetString());
                }
                else
                {
                    reader.Skip();
                }
            }

            return items;
        }
        catch (JsonException)
        {
            return [text];
        }
    }
}
