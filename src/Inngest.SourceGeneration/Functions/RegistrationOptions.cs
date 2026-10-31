// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Inngest.SourceGeneration.Functions;

// What the project says about the generated method, through the MSBuild properties that the Inngest package makes
// visible to the compiler. The generator writes nothing, and the analyzer of the function classes reports nothing, until
// the project turns generation on. A value that cannot be used is kept, so that the generator can report it, and the
// default is used in its place.
internal sealed record RegistrationOptions(
    bool Enabled,
    string MethodName,
    bool IsPublic,
    string? RejectedEnabled,
    string? RejectedMethodName,
    string? RejectedAccessibility)
{
    public const string DefaultMethodName = "AddInngestFunctions";
    public const string EnabledProperty = "InngestGenerateFunctionRegistration";
    public const string MethodNameProperty = "InngestFunctionsMethodName";
    public const string AccessibilityProperty = "InngestFunctionsAccessibility";

    // What the analyzer asks, so that a project that has not opted in costs it nothing.
    public static bool IsEnabled(AnalyzerConfigOptions globalOptions) =>
        TryGet(globalOptions, EnabledProperty, out var value) && string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    public static RegistrationOptions Read(AnalyzerConfigOptions globalOptions)
    {
        var enabled = false;
        string? rejectedEnabled = null;
        if (TryGet(globalOptions, EnabledProperty, out var switchValue))
        {
            if (string.Equals(switchValue, "true", StringComparison.OrdinalIgnoreCase))
            {
                enabled = true;
            }
            else if (!string.Equals(switchValue, "false", StringComparison.OrdinalIgnoreCase))
            {
                rejectedEnabled = switchValue;
            }
        }

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

        return new RegistrationOptions(enabled, methodName, isPublic, rejectedEnabled, rejectedMethodName, rejectedAccessibility);
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
