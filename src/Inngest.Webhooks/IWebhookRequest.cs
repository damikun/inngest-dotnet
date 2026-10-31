// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

namespace Inngest;

/// <summary>
/// A request that carries a webhook, independent of the web framework that received it. Verifiers read the raw body
/// and the headers through it, so one verifier serves every host.
/// </summary>
public interface IWebhookRequest
{
    /// <summary>
    /// The body exactly as it arrived. Signatures are computed over these bytes, so a body that was parsed and written
    /// again does not verify. The host owns the memory, and the SDK reads it in place without copying it: keep it unchanged
    /// until the request has been handled. After that the host may reuse or clear it, so copy what must outlive the request.
    /// </summary>
    ReadOnlyMemory<byte> Body { get; }

    /// <summary>The services of the request, which a transform can resolve from, or null.</summary>
    IServiceProvider? Services => null;

    /// <summary>
    /// The value of a header, or null when it is absent. Names are case-insensitive. A header sent more than once comes
    /// back as its values joined with commas.
    /// </summary>
    string? GetHeader(string name);
}
