// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest.Serving;

// A request to the serve endpoint, independent of the web framework or transport that received it.
internal interface IServeRequest
{
    string Method { get; }

    // The URL as the app received it. Used to tell Inngest where to call back unless ServeOrigin/ServePath are set.
    Uri Url { get; }

    // The raw body. Signatures are computed over these exact bytes.
    ReadOnlyMemory<byte> Body { get; }

    // Reported to Inngest in the X-Inngest-Framework header, such as "aspnetcore".
    string Framework { get; }

    // The scope to resolve functions and middleware from, or null to have the handler create one.
    IServiceProvider? Services { get; }

    // Null when the header is absent. Header names are case-insensitive.
    string? GetHeader(string name);

    // The decoded value of a query parameter, or null when it is absent.
    string? GetQuery(string name);
}
