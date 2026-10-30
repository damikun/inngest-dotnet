// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Net;
using System.Text;
using System.Text.Json;
using Inngest.Configuration;
using Inngest.Execution;

namespace Inngest.Steps;

// Inngest makes the request and memoizes {"status_code", "headers", "body", "url"}. Bodies travel as text both ways,
// and Inngest joins repeated response headers with commas.
internal sealed class FetchStep(string id, HttpRequestMessage request, KeyValuePair<string, string>[] headers, string? body)
    : FoundStep<HttpResponseMessage>(id)
{
    private static readonly JsonEncodedText UrlProperty = JsonEncodedText.Encode("url");
    private static readonly JsonEncodedText MethodProperty = JsonEncodedText.Encode("method");
    private static readonly JsonEncodedText HeadersProperty = JsonEncodedText.Encode("headers");
    private static readonly JsonEncodedText BodyProperty = JsonEncodedText.Encode("body");

    public override StepType Type => StepType.Fetch;

    public override JsonEncodedText Op => Ops.Gateway;

    public override string? GetName() => null;

    // The request as Inngest will make it: what the caller set, with its content headers, except the length, which
    // Inngest computes from the body it sends.
    public static KeyValuePair<string, string>[] ReadHeaders(HttpRequestMessage request)
    {
        var headers = new List<KeyValuePair<string, string>>();
        foreach (var (name, values) in request.Headers)
        {
            headers.Add(new(name, string.Join(", ", values)));
        }

        if (request.Content is { } content)
        {
            foreach (var (name, values) in content.Headers)
            {
                if (!string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    headers.Add(new(name, string.Join(", ", values)));
                }
            }
        }

        return [.. headers];
    }

    protected override void WriteOptions(Utf8JsonWriter writer, string? name, InngestSettings settings)
    {
        writer.WriteString(UrlProperty, request.RequestUri!.AbsoluteUri);
        writer.WriteString(MethodProperty, request.Method.Method);
        writer.WriteStartObject(HeadersProperty);
        foreach (var (header, value) in headers)
        {
            writer.WriteString(header, value);
        }

        writer.WriteEndObject();
        if (body is not null)
        {
            writer.WriteString(BodyProperty, body);
        }
    }

    protected override HttpResponseMessage Read(JsonElement value, JsonSerializerOptions options)
    {
        if (value.ValueKind != JsonValueKind.Object
            || !value.TryGetProperty("status_code", out var status)
            || !status.TryGetInt32(out var statusCode))
        {
            throw new JsonException($"The stored result of fetch step \"{FinalId}\" has no \"status_code\".");
        }

        var text = value.TryGetProperty("body", out var content) && content.ValueKind == JsonValueKind.String
            ? content.GetString()!
            : string.Empty;

        var response = new HttpResponseMessage((HttpStatusCode)statusCode)
        {
            RequestMessage = request,
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(text)),
        };

        if (value.TryGetProperty("headers", out var received) && received.ValueKind == JsonValueKind.Object)
        {
            foreach (var header in received.EnumerateObject())
            {
                // The length of the original body may not match the text Inngest kept.
                if (header.Value.ValueKind != JsonValueKind.String
                    || string.Equals(header.Name, "Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var headerValue = header.Value.GetString();
                if (!response.Headers.TryAddWithoutValidation(header.Name, headerValue))
                {
                    response.Content.Headers.TryAddWithoutValidation(header.Name, headerValue);
                }
            }
        }

        return response;
    }
}
