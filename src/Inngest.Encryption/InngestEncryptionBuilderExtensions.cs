// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using Inngest.Configuration;
using Inngest.Encryption;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Inngest;

public static class InngestEncryptionBuilderExtensions
{
    /// <summary>
    /// Adds the middleware that encrypts what leaves the app and decrypts what comes back, in the format of
    /// @inngest/middleware-encryption, so that .NET and TypeScript services can read each other's data. Step results
    /// and the function's output are encrypted as a whole, and events are encrypted in the field that
    /// <see cref="EncryptionOptions.EventEncryptionField"/> names. Incoming events and memoized step results are
    /// decrypted before the function reads them. Register it after middleware that add or change events, so that it
    /// encrypts the events they send. Options can also be set with <c>Configure&lt;EncryptionOptions&gt;</c>.
    /// </summary>
    public static IInngestBuilder AddEncryption(this IInngestBuilder builder, Action<EncryptionOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var services = builder.Services;
        var options = services.AddOptions<EncryptionOptions>();
        if (configure is not null)
        {
            options.Configure(configure);
        }

        // Fails the host at startup rather than on the first call that is encrypted.
        options.ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<EncryptionOptions>, EncryptionOptionsValidator>());
        services.TryAddSingleton(static provider => new PayloadEncryptor(
            provider.GetRequiredService<IOptions<EncryptionOptions>>().Value,
            provider.GetRequiredService<InngestSettings>().SerializerOptions));
        return builder.AddMiddleware<EncryptionMiddleware>();
    }
}
