// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Globalization;
using System.Text.Json;

namespace Inngest.Errors;

// Errors on the wire are {"name","message","stack","cause"}, with "code" and {"data":{"code"}} when the error has a
// code. That is what Inngest stores, shows and hands back in memoized step state. The TypeScript SDK reads and writes
// the same fields, apart from data.
internal static class ErrorSerializer
{
    private const int MaxCauseDepth = 5;

    private static readonly JsonEncodedText TypeProperty = JsonEncodedText.Encode("type");
    private static readonly JsonEncodedText InternalType = JsonEncodedText.Encode("internal");
    private static readonly JsonEncodedText NameProperty = JsonEncodedText.Encode("name");
    private static readonly JsonEncodedText MessageProperty = JsonEncodedText.Encode("message");
    private static readonly JsonEncodedText StackProperty = JsonEncodedText.Encode("stack");
    private static readonly JsonEncodedText CodeProperty = JsonEncodedText.Encode("code");
    private static readonly JsonEncodedText DataProperty = JsonEncodedText.Encode("data");
    private static readonly JsonEncodedText CauseProperty = JsonEncodedText.Encode("cause");

    public static void Write(Utf8JsonWriter writer, Exception exception) => Write(writer, exception, int.MaxValue);

    // With each text cut to maxLength characters, for an error too large for Inngest to keep.
    public static void Write(Utf8JsonWriter writer, Exception exception, int maxLength)
    {
        writer.WriteStartObject();
        WriteProperties(writer, exception, depth: 0, maxLength);
        writer.WriteEndObject();
    }

    // A failure of the SDK itself rather than of the function, such as an unreadable request.
    public static void WriteInternal(Utf8JsonWriter writer, Exception exception)
    {
        writer.WriteStartObject();
        writer.WriteString(TypeProperty, InternalType);
        WriteProperties(writer, exception, depth: 0, int.MaxValue);
        writer.WriteEndObject();
    }

    public static void Write(Utf8JsonWriter writer, FunctionError error)
    {
        writer.WriteStartObject();
        WriteProperties(writer, error, depth: 0, int.MaxValue);
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
            Code = ReadCode(error),
            Cause = depth < MaxCauseDepth && error.TryGetProperty("cause", out var cause) && cause.ValueKind != JsonValueKind.Null
                ? Read(cause, depth + 1)
                : null,
        };
    }

    private static void WriteProperties(Utf8JsonWriter writer, Exception exception, int depth, int maxLength)
    {
        // A step failure, or a cause rebuilt from one, is written as the error Inngest recorded, which is what the
        // run actually failed with, so a failure rethrown by the function keeps its whole chain.
        switch (exception)
        {
            case StepFailedException failed:
                WriteProperties(writer, failed.Error, depth, maxLength);
                return;
            case FunctionErrorException recorded:
                WriteProperties(writer, recorded.Error, depth, maxLength);
                return;
        }

        writer.WriteString(NameProperty, Cut(exception.GetType().Name, maxLength));
        writer.WriteString(MessageProperty, Cut(exception.Message, maxLength));
        writer.WriteString(StackProperty, Cut(exception.StackTrace ?? string.Empty, maxLength));
        if (CodeOf(exception) is { } code)
        {
            WriteCode(writer, Cut(code, maxLength));
        }

        if (exception.InnerException is { } cause && depth < MaxCauseDepth)
        {
            writer.WriteStartObject(CauseProperty);
            WriteProperties(writer, cause, depth + 1, maxLength);
            writer.WriteEndObject();
        }
    }

    private static void WriteProperties(Utf8JsonWriter writer, FunctionError error, int depth, int maxLength)
    {
        writer.WriteString(NameProperty, Cut(error.Name, maxLength));
        writer.WriteString(MessageProperty, Cut(error.Message, maxLength));
        writer.WriteString(StackProperty, Cut(error.Stack ?? string.Empty, maxLength));
        if (error.Code is { } code)
        {
            WriteCode(writer, Cut(code, maxLength));
        }

        if (error.Cause is { } cause && depth < MaxCauseDepth)
        {
            writer.WriteStartObject(CauseProperty);
            WriteProperties(writer, cause, depth + 1, maxLength);
            writer.WriteEndObject();
        }
    }

    // The start of a longer text, never ending inside a surrogate pair, and how much of it was left out.
    private static string Cut(string text, int maxLength)
    {
        if (text.Length <= maxLength)
        {
            return text;
        }

        var length = char.IsHighSurrogate(text[maxLength - 1]) ? maxLength - 1 : maxLength;
        return string.Create(CultureInfo.InvariantCulture, $"{text.AsSpan(0, length)}... ({text.Length - length} more characters left out)");
    }

    // "code" is where the TypeScript SDK writes it. Inngest drops fields it does not know from the error of a step, and
    // keeps "data" for the SDK's own use, so the code goes there too and comes back with the step's error.
    private static void WriteCode(Utf8JsonWriter writer, string code)
    {
        writer.WriteString(CodeProperty, code);
        writer.WriteStartObject(DataProperty);
        writer.WriteString(CodeProperty, code);
        writer.WriteEndObject();
    }

    private static string? CodeOf(Exception exception)
    {
        if (exception is IHasErrorCode { Code: { } code })
        {
            return code;
        }

        return exception.Data?["code"] switch
        {
            null => null,
            string text => text,
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            var other => other.ToString(),
        };
    }

    // JavaScript errors usually have string codes, but some libraries use numbers.
    private static string? ReadCode(JsonElement error)
    {
        if (!error.TryGetProperty("code", out var code) &&
            !(error.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object && data.TryGetProperty("code", out code)))
        {
            return null;
        }

        return code.ValueKind switch
        {
            JsonValueKind.String => code.GetString(),
            JsonValueKind.Number => code.GetRawText(),
            _ => null,
        };
    }

    private static string? NonEmptyString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
            ? text
            : null;
}
