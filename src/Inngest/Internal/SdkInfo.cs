// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Reflection;

namespace Inngest.Internal;

internal static class SdkInfo
{
    public static readonly string Version = typeof(SdkInfo).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    // Sent as X-Inngest-SDK and User-Agent on every request and response.
    public static readonly string Header = $"inngest-dotnet:v{Version}";

    // The "sdk" field of a sync body. Inngest splits it on ':' into language and version.
    public static readonly string RegistrationValue = $"dotnet:v{Version}";

    public const string Language = "dotnet";
}
