// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text.Json;

namespace Inngest.Errors;

// Errors on the wire are {"name","message","stack","cause"}, which is what Inngest stores, shows and hands back
// in memoized step state.
internal static class ErrorSerializer
{
    private const int MaxCauseDepth = 5;

    private static readonly JsonEncodedText TypeProperty = JsonEncodedText.Encode("type");
    private static readonly JsonEncodedText InternalType = JsonEncodedText.Encode("internal");
    private static readonly JsonEncodedText NameProperty = JsonEncodedText.Encode("name");
    private static readonly JsonEncodedText MessageProperty = JsonEncodedText.Encode("message");
    private static readonly JsonEncodedText StackProperty = JsonEncodedText.Encode("stack");
    private static readonly JsonEncodedText CauseProperty = JsonEncodedText.Encode("cause");

    public static void Write(Utf8JsonWriter writer, Exception exception)
    {
        writer.WriteStartObject();
        WriteProperties(writer, exception, depth: 0);
        writer.WriteEndObject();
    }

    // A failure of the SDK itself rather than of the function, such as an unreadable request.
    public static void WriteInternal(Utf8JsonWriter writer, Exception exception)
    {
        writer.WriteStartObject();
        writer.WriteString(TypeProperty, InternalType);
        WriteProperties(writer, exception, depth: 0);
        writer.WriteEndObject();
    }

    public static void Write(Utf8JsonWriter writer, FunctionError error)
    {
        writer.WriteStartObject();
        WriteProperties(writer, error, depth: 0);
        writer.WriteEndObject();
    }

    // Reads the error of a failed step and its causes. Missing fields get the defaults the TypeScript SDK uses, and
    // an error given as plain text becomes the message.
    public static FunctionError Read(JsonElement error) => Read(error, depth: 0);

    private static FunctionError Read(JsonElement error, int depth)
    {
        if (error.ValueKind == JsonValueKind.String)
        {
            return new FunctionError { Message = error.GetString()! };
        }

        if (error.ValueKind != JsonValueKind.Object)
        {
            return new FunctionError { Message = "Unknown error" };
        }

        return new FunctionError
        {
            Name = NonEmptyString(error, "name") ?? "Error",
            Message = NonEmptyString(error, "message") ?? NonEmptyString(error, "error") ?? "Unknown error",
            Stack = NonEmptyString(error, "stack"),
            Cause = depth < MaxCauseDepth && error.TryGetProperty("cause", out var cause) && cause.ValueKind != JsonValueKind.Null
                ? Read(cause, depth + 1)
                : null,
        };
    }

    private static void WriteProperties(Utf8JsonWriter writer, Exception exception, int depth)
    {
        // A step failure, or a cause rebuilt from one, is written as the error Inngest recorded, which is what the
        // run actually failed with, so a failure rethrown by the function keeps its whole chain.
        switch (exception)
        {
            case StepFailedException failed:
                WriteProperties(writer, failed.Error, depth);
                return;
            case FunctionErrorException recorded:
                WriteProperties(writer, recorded.Error, depth);
                return;
        }

        writer.WriteString(NameProperty, exception.GetType().Name);
        writer.WriteString(MessageProperty, exception.Message);
        writer.WriteString(StackProperty, exception.StackTrace ?? string.Empty);
        if (exception.InnerException is { } cause && depth < MaxCauseDepth)
        {
            writer.WriteStartObject(CauseProperty);
            WriteProperties(writer, cause, depth + 1);
            writer.WriteEndObject();
        }
    }

    private static void WriteProperties(Utf8JsonWriter writer, FunctionError error, int depth)
    {
        writer.WriteString(NameProperty, error.Name);
        writer.WriteString(MessageProperty, error.Message);
        writer.WriteString(StackProperty, error.Stack ?? string.Empty);
        if (error.Cause is { } cause && depth < MaxCauseDepth)
        {
            writer.WriteStartObject(CauseProperty);
            WriteProperties(writer, cause, depth + 1);
            writer.WriteEndObject();
        }
    }

    private static string? NonEmptyString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
            ? text
            : null;
}
