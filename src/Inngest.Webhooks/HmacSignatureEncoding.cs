// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

namespace Inngest;

/// <summary>How a provider writes the bytes of an HMAC in a header.</summary>
public enum HmacSignatureEncoding
{
    /// <summary>Hexadecimal digits, in either case, as GitHub and Slack write them.</summary>
    Hex,

    /// <summary>Standard base64 with padding, as Shopify writes it.</summary>
    Base64,
}
