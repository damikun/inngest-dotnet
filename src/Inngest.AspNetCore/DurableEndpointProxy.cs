// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Buffers;
using System.Text;
using System.Text.Json;
using Inngest.Client;
using Inngest.Configuration;
using Inngest.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Inngest.AspNetCore;

// Serves the output of a durable endpoint's run on the app's own domain, as endpointProxy() does in inngest-js: it asks
// GET {api}/v1/http/runs/{runId}/output?token=… for it, which needs no signing key and waits for the run to end, and
// hands the answer on with CORS headers so that a page on another origin can read it. Inngest answers with the response
// the endpoint recorded. A run whose output the encryption middleware encrypted has no response Inngest can replay, so
// the proxy decrypts it and replays the recorded response itself.
internal sealed class DurableEndpointProxy(IHttpClientFactory httpClientFactory, InngestSettings settings, IValueDecryptor? decryptor, ILogger logger)
{
    private const string RunIdParameter = "runId";
    private const string TokenParameter = "token";

    public static DurableEndpointProxy Create(IServiceProvider services)
    {
        var settings = services.GetService<InngestSettings>()
            ?? throw new InvalidOperationException("Call services.AddInngest() before app.MapInngestDurableEndpointProxy().");
        return new DurableEndpointProxy(
            services.GetRequiredService<IHttpClientFactory>(),
            settings,
            services.GetService<IValueDecryptor>(),
            (services.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance).CreateLogger("Inngest.AspNetCore.DurableEndpoints"));
    }

    public async Task HandleAsync(HttpContext context)
    {
        var response = context.Response;
        var cancellationToken = context.RequestAborted;
        response.Headers.AccessControlAllowOrigin = "*";
        response.Headers.AccessControlAllowMethods = "GET, OPTIONS";
        response.Headers.AccessControlAllowHeaders = "Content-Type";
        if (HttpMethods.IsOptions(context.Request.Method))
        {
            response.Headers.AccessControlMaxAge = "86400";
            response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        var query = context.Request.Query;
        var runId = query[RunIdParameter].ToString();
        var token = query[TokenParameter].ToString();
        if (runId.Length == 0 || token.Length == 0)
        {
            await WriteErrorAsync(response, StatusCodes.Status400BadRequest, "Missing runId or token query parameter", cancellationToken).ConfigureAwait(false);
            return;
        }

        int status;
        string? contentType;
        byte[] body;
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                new Uri(settings.ApiBaseUrl, $"/v1/http/runs/{Uri.EscapeDataString(runId)}/output?token={Uri.EscapeDataString(token)}"));
            request.AddSdkHeaders(settings.Environment);

            // Inngest holds the request until the run ends, for up to five minutes, which is longer than an HttpClient
            // waits by default. The client of the proxy decides how long to wait.
            var client = httpClientFactory.CreateClient(InngestServiceCollectionExtensions.HttpClientName);
            client.Timeout = Timeout.InfiniteTimeSpan;
            using var upstream = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
            status = (int)upstream.StatusCode;
            contentType = upstream.Content.Headers.ContentType?.ToString();
            body = await upstream.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException && !cancellationToken.IsCancellationRequested)
        {
            Log.EndpointOutputFailed(logger, runId, exception);
            await WriteErrorAsync(response, StatusCodes.Status502BadGateway, "Failed to fetch run output", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (status is >= 200 and < 300 && decryptor is not null && Decrypt(body) is { } decrypted)
        {
            using var document = JsonDocument.Parse(decrypted);
            if (TryReadRecorded(document.RootElement, out var recordedStatus, out var recordedType, out var recordedBody))
            {
                status = recordedStatus;
                contentType = recordedType;
                body = Encoding.UTF8.GetBytes(recordedBody);
            }
            else
            {
                status = StatusCodes.Status200OK;
                contentType = "application/json";
                body = decrypted;
            }
        }

        response.StatusCode = status;
        if (contentType is not null)
        {
            response.ContentType = contentType;
        }

        response.ContentLength = body.Length;
        await response.Body.WriteAsync(body, cancellationToken).ConfigureAwait(false);
    }

    // The decrypted output as JSON, or null when it is not an encrypted value.
    private byte[]? Decrypt(byte[] body)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                if (!decryptor!.TryDecrypt(document.RootElement, writer))
                {
                    return null;
                }
            }

            return buffer.WrittenSpan.ToArray();
        }
    }

    // The response a resumed endpoint recorded as its run's output: {"data": {"status", "headers", "body"}}.
    private static bool TryReadRecorded(JsonElement output, out int status, out string? contentType, out string body)
    {
        status = 0;
        contentType = null;
        body = string.Empty;
        if (output.ValueKind != JsonValueKind.Object ||
            !output.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("status", out var statusElement) || !statusElement.TryGetInt32(out status) ||
            !data.TryGetProperty("body", out var bodyElement) || bodyElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        body = bodyElement.GetString()!;
        if (data.TryGetProperty("headers", out var headers) && headers.ValueKind == JsonValueKind.Object)
        {
            foreach (var header in headers.EnumerateObject())
            {
                if (string.Equals(header.Name, "Content-Type", StringComparison.OrdinalIgnoreCase) && header.Value.ValueKind == JsonValueKind.String)
                {
                    contentType = header.Value.GetString();
                }
            }
        }

        return true;
    }

    // {"error": <message>}, as inngest-js answers.
    private static async Task WriteErrorAsync(HttpResponse response, int status, string message, CancellationToken cancellationToken)
    {
        response.StatusCode = status;
        response.ContentType = "application/json";
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("error"u8, message);
            writer.WriteEndObject();
        }

        response.ContentLength = buffer.WrittenCount;
        await response.Body.WriteAsync(buffer.WrittenMemory, cancellationToken).ConfigureAwait(false);
    }
}
