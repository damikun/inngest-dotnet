// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

namespace Inngest;

/// <summary>
/// Decides whether a request really comes from a webhook provider. Implement it for a provider the SDK does not know,
/// and pass it to <see cref="WebhookProvider"/>.
/// </summary>
public interface IWebhookVerifier
{
    /// <summary>
    /// Checks the signature of the request, and its timestamp when the provider signs one. Anyone on the internet can
    /// send a request, so this must give an invalid result, and never throw, for a header or a body that is missing,
    /// malformed or hostile. Compare signatures in constant time, with
    /// <see cref="System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(ReadOnlySpan{byte}, ReadOnlySpan{byte})"/>.
    /// </summary>
    /// <param name="request">The request, whose body has not been parsed.</param>
    /// <param name="now">When the request arrived, which a signed timestamp is compared with.</param>
    WebhookVerification Verify(IWebhookRequest request, DateTimeOffset now);
}
