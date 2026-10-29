// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text;
using System.Text.Json;
using Inngest.Internal;

namespace Inngest.Client;

// What the event API client and the REST API client both need to build requests and read replies.
internal static class ApiHttp
{
    // Long enough to show what a gateway or proxy answered, short enough for an exception message.
    private const int MaxErrorTextLength = 300;

    public static void AddSdkHeaders(this HttpRequestMessage request, string? environment)
    {
        request.Headers.TryAddWithoutValidation(Headers.Sdk, SdkInfo.Header);

        // The value is not a valid product token ("inngest-dotnet:v1.2.3"), so it cannot go through the typed header.
        request.Headers.TryAddWithoutValidation("User-Agent", SdkInfo.Header);

        if (!string.IsNullOrEmpty(environment))
        {
            request.Headers.TryAddWithoutValidation(Headers.Environment, environment);
        }
    }

    public static ApiReply? ParseReply(ReadOnlySpan<byte> body)
    {
        try
        {
            return JsonSerializer.Deserialize(body, ClientJsonContext.Default.ApiReply);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // The reason Inngest gave for a failed call: the "error" of a JSON reply, else whatever text the body holds.
    public static string DescribeError(int statusCode, ReadOnlySpan<byte> body) =>
        ParseReply(body)?.Error is { Length: > 0 } error
            ? error
            : ToText(body) ?? $"Inngest answered with HTTP {statusCode}.";

    public static string? ToText(ReadOnlySpan<byte> body)
    {
        var text = Encoding.UTF8.GetString(body[..Math.Min(body.Length, MaxErrorTextLength * 4)]).Trim();
        if (text.Length == 0)
        {
            return null;
        }

        return text.Length <= MaxErrorTextLength ? text : text[..MaxErrorTextLength] + "...";
    }
}
