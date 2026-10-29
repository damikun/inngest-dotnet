// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Globalization;
using Inngest.Internal;

namespace Inngest.Connect;

// ConnectOptions resolved once at startup, with the INNGEST_CONNECT_* variables applied to what the options leave
// unset. Invalid values stop the host from starting rather than surfacing on the first connection.
internal sealed record ConnectSettings
{
    private const string MaxWorkerConcurrencyVariable = "INNGEST_CONNECT_MAX_WORKER_CONCURRENCY";
    private const string GatewayUrlVariable = "INNGEST_CONNECT_GATEWAY_URL";

    public required string InstanceId { get; init; }

    public int? MaxWorkerConcurrency { get; init; }

    public Uri? GatewayUrl { get; init; }

    public static ConnectSettings Create(ConnectOptions options, Func<string, string?> getVariable)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(getVariable);

        var variables = new VariableSource(getVariable);
        if (options.MaxWorkerConcurrency is <= 0)
        {
            throw new InvalidOperationException("ConnectOptions.MaxWorkerConcurrency must be a positive number.");
        }

        return new ConnectSettings
        {
            InstanceId = string.IsNullOrWhiteSpace(options.InstanceId) ? Environment.MachineName : options.InstanceId.Trim(),
            MaxWorkerConcurrency = options.MaxWorkerConcurrency ?? ReadConcurrency(variables.Get(MaxWorkerConcurrencyVariable)),
            GatewayUrl = options.GatewayUrl is { } url
                ? RequireWebSocketUrl(url, $"ConnectOptions.{nameof(ConnectOptions.GatewayUrl)}")
                : variables.Get(GatewayUrlVariable) is { } text ? ParseWebSocketUrl(text) : null,
        };
    }

    private static int? ReadConcurrency(string? text)
    {
        if (text is null)
        {
            return null;
        }

        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : throw new InvalidOperationException($"{MaxWorkerConcurrencyVariable} must be a positive whole number, but it is '{text}'.");
    }

    private static Uri ParseWebSocketUrl(string text) =>
        Uri.TryCreate(text, UriKind.Absolute, out var url)
            ? RequireWebSocketUrl(url, GatewayUrlVariable)
            : throw new InvalidOperationException($"{GatewayUrlVariable} must be a ws or wss URL, but it is '{text}'.");

    private static Uri RequireWebSocketUrl(Uri url, string name) =>
        url.IsAbsoluteUri && url.Scheme is "ws" or "wss"
            ? url
            : throw new InvalidOperationException($"{name} must be an absolute ws or wss URL, but it is '{url}'.");
}
