// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest;

/// <summary>
/// A request to the Inngest endpoint as <see cref="InngestCommHandler"/> reads it, whatever web framework or transport
/// received it. A host adapter implements it over the request of its platform.
/// </summary>
public interface IServeRequest
{
    /// <summary>The HTTP method, such as "POST".</summary>
    string Method { get; }

    /// <summary>
    /// The URL as the app received it, with its query. A sync tells Inngest to call this URL unless
    /// <see cref="InngestOptions.ServeOrigin"/> or <see cref="InngestOptions.ServePath"/> say otherwise.
    /// </summary>
    Uri Url { get; }

    /// <summary>The body exactly as it arrived; the signature is checked over these bytes.</summary>
    ReadOnlyMemory<byte> Body { get; }

    /// <summary>The name of the host, reported to Inngest in the X-Inngest-Framework header, such as "aspnetcore".</summary>
    string Framework { get; }

    /// <summary>
    /// The scope that functions and middleware are resolved from, such as the request's services, or null to have a
    /// scope created for each call.
    /// </summary>
    IServiceProvider? Services { get; }

    /// <summary>The value of a header, or null when the request does not have it. Names are compared without regard to case.</summary>
    string? GetHeader(string name);

    /// <summary>The decoded value of a query parameter, or null when the URL does not have it.</summary>
    string? GetQuery(string name);
}
