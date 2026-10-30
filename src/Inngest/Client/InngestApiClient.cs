// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Net;
using System.Text.Json;
using Inngest.Configuration;
using Microsoft.Extensions.Logging;

namespace Inngest.Client;

internal sealed class InngestApiClient : IInngestApi
{
    private const string OutOfBandSyncKind = "out_of_band";

    private readonly InngestApiTransport _transport;
    private readonly Uri _registerUrl;

    public InngestApiClient(IHttpClientFactory httpClientFactory, InngestSettings settings, ILogger<InngestApiClient> logger)
    {
        _transport = new InngestApiTransport(httpClientFactory, settings, logger);
        _registerUrl = _transport.Url("/fn/register");
    }

    public async Task<RegisterResult> RegisterAsync(
        ReadOnlyMemory<byte> payload,
        string? deployId,
        string? serverKind,
        CancellationToken cancellationToken)
    {
        var url = string.IsNullOrEmpty(deployId)
            ? _registerUrl
            : new Uri($"{_registerUrl.AbsoluteUri}?deployId={Uri.EscapeDataString(deployId)}");

        try
        {
            using var response = await _transport.SendAsync(
                new ApiCall(HttpMethod.Post, url, payload, serverKind, OutOfBandSyncKind),
                HttpCompletionOption.ResponseContentRead,
                cancellationToken).ConfigureAwait(false);
            var content = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            return ReadRegisterResponse((int)response.StatusCode, content);
        }
        catch (HttpRequestException exception)
        {
            return Unreachable($"Failed to register; {exception.Message}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Unreachable("Failed to register; the request to Inngest timed out.");
        }
    }

    public Task<JsonDocument> GetRunStepsAsync(string runId, CancellationToken cancellationToken) =>
        GetRunDataAsync(runId, "actions", "steps", cancellationToken);

    public Task<JsonDocument> GetRunEventsAsync(string runId, CancellationToken cancellationToken) =>
        GetRunDataAsync(runId, "batch", "events", cancellationToken);

    // There is no reply to report, so the status is the one the sync is answered with.
    private static RegisterResult Unreachable(string error) => new(false, 500, false, error);

    private RegisterResult ReadRegisterResponse(int status, byte[] content)
    {
        if (status == (int)HttpStatusCode.OK)
        {
            return new RegisterResult(true, status, ApiHttp.ParseReply(content)?.Modified ?? false, null);
        }

        var error = ApiHttp.DescribeError(status, content);
        return new RegisterResult(false, status, false, error);
    }

    private async Task<JsonDocument> GetRunDataAsync(string runId, string resource, string description, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(runId);

        var url = _transport.Url($"/v0/runs/{Uri.EscapeDataString(runId)}/{resource}");
        try
        {
            using var response = await _transport.SendAsync(new ApiCall(HttpMethod.Get, url), HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (response.StatusCode != HttpStatusCode.OK)
            {
                var status = (int)response.StatusCode;
                var content = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                throw new InngestException($"Failed to retrieve the {description} of run {runId}: {ApiHttp.DescribeError(status, content)}", status);
            }

            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            throw new InngestException($"Failed to retrieve the {description} of run {runId}: {exception.Message}", exception);
        }
        catch (JsonException exception)
        {
            throw new InngestException($"Failed to retrieve the {description} of run {runId}: Inngest returned invalid JSON.", exception);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InngestException($"Failed to retrieve the {description} of run {runId}: the request to Inngest timed out.", exception);
        }
    }
}
