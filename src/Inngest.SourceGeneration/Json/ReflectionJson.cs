// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Inngest.SourceGeneration.Json;

// Whether the app turns reflection-based System.Text.Json off, from the MSBuild properties that the Inngest package makes
// visible to the compiler. The SDK flips the switch when the app is published trimmed or ahead of time, but only while
// it publishes, so the properties it flips it from are read here.
internal static class ReflectionJson
{
    public static bool IsDisabled(AnalyzerConfigOptions options)
    {
        var reflection = Read(options, "JsonSerializerIsReflectionEnabledByDefault");
        if (reflection is not null)
        {
            return string.Equals(reflection, "false", StringComparison.OrdinalIgnoreCase);
        }

        return IsTrue(Read(options, "PublishAot")) || IsTrue(Read(options, "PublishTrimmed"));
    }

    private static string? Read(AnalyzerConfigOptions options, string property) =>
        options.TryGetValue("build_property." + property, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    private static bool IsTrue(string? value) => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
}
