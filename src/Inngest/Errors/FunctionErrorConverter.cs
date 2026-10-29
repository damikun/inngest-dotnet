// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Inngest.Errors;

// Reads errors in system events the way ErrorSerializer reads step errors. Other SDKs record causes that are not
// error objects, such as a JavaScript cause given as a string, which would otherwise fail the whole event.
internal sealed class FunctionErrorConverter : JsonConverter<FunctionError>
{
    public override FunctionError Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        return ErrorSerializer.Read(document.RootElement);
    }

    public override void Write(Utf8JsonWriter writer, FunctionError value, JsonSerializerOptions options) =>
        ErrorSerializer.Write(writer, value);
}
