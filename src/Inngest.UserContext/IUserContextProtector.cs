// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System.Diagnostics.CodeAnalysis;

namespace Inngest;

/// <summary>
/// Protects the text that carries a user's claims in an event, and reads it back. The default uses ASP.NET Core Data
/// Protection. Register another implementation as a singleton to sign or encrypt some other way, for example with a key
/// from a vault; every app that sends or restores users then needs a compatible one. The text is visible to anyone who
/// can see the event, in the Inngest dashboard for one, so an implementation has to detect changes (a MAC or a
/// signature) and should encrypt. It must keep the text out of exception messages and logs.
/// </summary>
public interface IUserContextProtector
{
    /// <summary>Protects the JSON of a user's claims and returns text that is safe to put in an event.</summary>
    string Protect(string plaintext);

    /// <summary>
    /// Reads text that <see cref="Protect"/> returned. Returns false for text that is not valid: changed, made with
    /// another key or purpose, or not protected text at all. Throws for failures that are not the text's fault, such as
    /// a key store that cannot be reached, so that the call is retried instead of running without a user.
    /// </summary>
    bool TryUnprotect(string protectedText, [NotNullWhen(true)] out string? plaintext);
}
