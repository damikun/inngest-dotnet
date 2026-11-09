// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text.Json;

namespace Inngest.Execution;

// What a metadata write is attached to, as inngest-js names the scopes.
internal enum MetadataScope
{
    Run,
    Step,
    ExtendedTrace,
}

// The values of one kind of metadata, written as a JSON object.
internal abstract class MetadataValues
{
    public abstract void Write(Utf8JsonWriter writer);
}

// The metadata a step's op carries to Inngest: {"metadata": [{"kind", "scope", "op", "values"}]}, as inngest-js sends it
// with StepRun, StepError and StepFailed. Inngest replaces the values of a kind on every write, so each kind and scope
// is sent once, and whatever combines several writes has to do it before the op goes out.
internal sealed class StepMetadata
{
    // The Inngest kinds the SDK writes; apps get "userland." kinds of their own.
    public const string AIKind = "inngest.ai";

    private static readonly JsonEncodedText MetadataProperty = JsonEncodedText.Encode("metadata");
    private static readonly JsonEncodedText KindProperty = JsonEncodedText.Encode("kind");
    private static readonly JsonEncodedText ScopeProperty = JsonEncodedText.Encode("scope");
    private static readonly JsonEncodedText OpProperty = JsonEncodedText.Encode("op");
    private static readonly JsonEncodedText ValuesProperty = JsonEncodedText.Encode("values");

    // inngest-js still sends "merge" for update(), which the server treats as "set" as well.
    private static readonly JsonEncodedText SetOp = JsonEncodedText.Encode("set");

    private readonly List<(string Kind, MetadataScope Scope, MetadataValues Values)> _updates = [];

    public int Count => _updates.Count;

    // Replaces the values written earlier for the same kind and scope, keeping the order of first writes.
    public void Set(string kind, MetadataScope scope, MetadataValues values)
    {
        var index = _updates.FindIndex(update => update.Kind == kind && update.Scope == scope);
        if (index < 0)
        {
            _updates.Add((kind, scope, values));
        }
        else
        {
            _updates[index] = (kind, scope, values);
        }
    }

    public void Write(Utf8JsonWriter writer)
    {
        if (_updates.Count == 0)
        {
            return;
        }

        writer.WriteStartArray(MetadataProperty);
        foreach (var (kind, scope, values) in _updates)
        {
            writer.WriteStartObject();
            writer.WriteString(KindProperty, kind);
            writer.WriteString(ScopeProperty, ScopeName(scope));
            writer.WriteString(OpProperty, SetOp);
            writer.WritePropertyName(ValuesProperty);
            values.Write(writer);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static string ScopeName(MetadataScope scope) => scope switch
    {
        MetadataScope.Run => "run",
        MetadataScope.Step => "step",
        MetadataScope.ExtendedTrace => "extended_trace",
        _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, null),
    };
}
