// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using Microsoft.Extensions.Options;

namespace Inngest.Encryption;

// Reports every problem at once, so that the app fails at startup instead of on the first call that is encrypted.
internal sealed class EncryptionOptionsValidator : IValidateOptions<EncryptionOptions>
{
    public ValidateOptionsResult Validate(string? name, EncryptionOptions options)
    {
        List<string> failures = [];
        if (options.Service is null && EncryptionKeys.Normalize(options.Key, null).Length == 0)
        {
            failures.Add("EncryptionOptions.Key is required unless EncryptionOptions.Service is set, for example AddEncryption(options => options.Key = key).");
        }

        if (string.IsNullOrEmpty(options.EventEncryptionField))
        {
            failures.Add("EncryptionOptions.EventEncryptionField must not be empty.");
        }

        if (options.Service is { } service && string.IsNullOrWhiteSpace(service.Identifier))
        {
            failures.Add("EncryptionOptions.Service needs an Identifier.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
