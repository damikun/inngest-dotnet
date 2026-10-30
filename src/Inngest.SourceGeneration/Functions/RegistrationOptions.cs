// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Inngest.SourceGeneration.Functions;

// What the project says about the generated method, through the MSBuild properties that the Inngest package makes
// visible to the compiler. A value that cannot be used is kept, so that the generator can report it, and the default
// is used in its place.
internal sealed record RegistrationOptions(string MethodName, bool IsPublic, string? RejectedMethodName, string? RejectedAccessibility)
{
    public const string DefaultMethodName = "AddInngestFunctions";
    public const string MethodNameProperty = "InngestFunctionsMethodName";
    public const string AccessibilityProperty = "InngestFunctionsAccessibility";

    public static RegistrationOptions Default { get; } = new(DefaultMethodName, IsPublic: false, null, null);

    public static RegistrationOptions Read(AnalyzerConfigOptions globalOptions)
    {
        var methodName = DefaultMethodName;
        string? rejectedMethodName = null;
        if (TryGet(globalOptions, MethodNameProperty, out var name))
        {
            if (SyntaxFacts.IsValidIdentifier(name) && SyntaxFacts.GetKeywordKind(name) == SyntaxKind.None)
            {
                methodName = name;
            }
            else
            {
                rejectedMethodName = name;
            }
        }

        var isPublic = false;
        string? rejectedAccessibility = null;
        if (TryGet(globalOptions, AccessibilityProperty, out var accessibility))
        {
            if (string.Equals(accessibility, "public", StringComparison.OrdinalIgnoreCase))
            {
                isPublic = true;
            }
            else if (!string.Equals(accessibility, "internal", StringComparison.OrdinalIgnoreCase))
            {
                rejectedAccessibility = accessibility;
            }
        }

        return new RegistrationOptions(methodName, isPublic, rejectedMethodName, rejectedAccessibility);
    }

    private static bool TryGet(AnalyzerConfigOptions options, string property, out string value)
    {
        // An unset property reaches the compiler as an empty value.
        if (options.TryGetValue("build_property." + property, out var text) && !string.IsNullOrWhiteSpace(text))
        {
            value = text.Trim();
            return true;
        }

        value = string.Empty;
        return false;
    }
}
