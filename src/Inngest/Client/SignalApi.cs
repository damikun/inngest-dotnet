// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Net;
using System.Text.Json;
using Inngest.Internal;
using Microsoft.Extensions.Logging;

namespace Inngest.Client;

// POST {api}/v1/signals with {"signal", "data"}, which resumes the run that waits for the signal.
internal interface ISignalApi
{
    // Returns the id of the resumed run, or null when Inngest answers 404 because no run waits for the signal. Any
    // other failure throws an InngestException. Not retried: Inngest consumes the wait on the first delivery, so a
    // retry after a lost reply would only find nothing to resume.
    Task<string?> SendAsync(string signal, ReadOnlyMemory<byte> body, string? environment, CancellationToken cancellationToken);
}

internal sealed class SignalApi : ISignalApi
{
    private readonly InngestApiTransport _transport;
    private readonly Uri _url;

    public SignalApi(IHttpClientFactory httpClientFactory, InngestSettings settings, ILogger<SignalApi> logger)
    {
        _transport = new InngestApiTransport(httpClientFactory, settings, logger);
        _url = _transport.Url("/v1/signals");
    }

    public async Task<string?> SendAsync(string signal, ReadOnlyMemory<byte> body, string? environment, CancellationToken cancellationToken)
    {
        int status;
        byte[] content;
        try
        {
            using var response = await _transport.SendAsync(
                new ApiCall(HttpMethod.Post, _url, body, Environment: environment),
                HttpCompletionOption.ResponseContentRead,
                cancellationToken).ConfigureAwait(false);
            status = (int)response.StatusCode;
            content = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            throw new InngestException($"Failed to send signal \"{signal}\": {exception.Message}", exception);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InngestException($"Failed to send signal \"{signal}\": the request to Inngest timed out.", exception);
        }

        if (status == (int)HttpStatusCode.NotFound)
        {
            return null;
        }

        if (status is < 200 or > 299)
        {
            throw new InngestException($"Failed to send signal \"{signal}\": {ApiHttp.DescribeError(status, content)}", status);
        }

        return ReadRunId(content) ?? throw new InngestException(
            $"Inngest accepted signal \"{signal}\" but did not say which run it resumed: {ApiHttp.ToText(content) ?? "empty reply"}",
            status);
    }

    // {"data": {"run_id": "01J..."}, "metadata": {...}}
    private static string? ReadRunId(byte[] content)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("data", out var data)
                && data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("run_id", out var runId)
                && runId.ValueKind == JsonValueKind.String
                && runId.GetString() is { Length: > 0 } id
                    ? id
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
