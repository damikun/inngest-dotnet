// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text.Json;

namespace Inngest.Execution;

internal enum MemoKind : byte
{
    Data,
    Error,

    // Edited input for a step that has not finished yet; not a result.
    Input,
}

// What Inngest sent for one step under "steps". Value points into the request document and is deserialized only
// when the step's result is handed back to the function.
internal struct MemoEntry(MemoKind kind, JsonElement value)
{
    public readonly MemoKind Kind = kind;

    public readonly JsonElement Value = value;

    // Set once the entry has its place in the order memoized results are handed back in.
    public bool Ordered;

    public readonly bool IsResult => Kind != MemoKind.Input;

    // An object with only "data", "error" or "input" (optionally tagged with a matching "type") wraps the value.
    // Anything else, including null and the raw event a waitForEvent matched, is the value itself.
    public static MemoEntry Parse(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return new MemoEntry(MemoKind.Data, value);
        }

        JsonProperty? wrapper = null;
        var type = default(JsonElement);
        var count = 0;
        foreach (var property in value.EnumerateObject())
        {
            count++;
            if (property.NameEquals("type") && type.ValueKind == JsonValueKind.Undefined)
            {
                type = property.Value;
            }
            else if (wrapper is null)
            {
                wrapper = property;
            }
            else
            {
                return new MemoEntry(MemoKind.Data, value);
            }
        }

        if (wrapper is not { } inner || count > 2)
        {
            return new MemoEntry(MemoKind.Data, value);
        }

        if (inner.NameEquals("data") && TypeMatches(type, "data"))
        {
            return new MemoEntry(MemoKind.Data, inner.Value);
        }

        if (inner.NameEquals("error") && TypeMatches(type, "error"))
        {
            return new MemoEntry(MemoKind.Error, inner.Value);
        }

        if (inner.NameEquals("input") && TypeMatches(type, "input"))
        {
            return new MemoEntry(MemoKind.Input, inner.Value);
        }

        return new MemoEntry(MemoKind.Data, value);
    }

    private static bool TypeMatches(JsonElement type, string expected) =>
        type.ValueKind == JsonValueKind.Undefined ||
        (type.ValueKind == JsonValueKind.String && type.ValueEquals(expected));
}
