// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using Microsoft.CodeAnalysis;

namespace Inngest.SourceGeneration;

internal static class DiagnosticDescriptors
{
    private const string FunctionsCategory = "Inngest.Functions";
    private const string SerializationCategory = "Inngest.Serialization";

    // Every diagnostic has a heading of its own in the guide, whose anchor is the lower-case id.
    private const string GuideUrl = "https://damikun.github.io/inngest-dotnet/guides/source-generator/";

    public static readonly DiagnosticDescriptor NotAFunction = new(
        DiagnosticIds.NotAFunction,
        title: "A class with [InngestFunction] is not a function",
        messageFormat: "'{0}' has [InngestFunction] but does not derive from InngestFunction<TEventData> or InngestFunction<TEventData, TResult>, so it is not a function",
        category: FunctionsCategory,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "The attribute configures a function class. On any other class it does nothing: the class cannot be registered, by AddFunction<T>() or by AddInngestFunctions(), and Inngest never runs it.",
        helpLinkUri: GuideUrl + "#" + DiagnosticIds.NotAFunction.ToLowerInvariant());

    public static readonly DiagnosticDescriptor AbstractFunction = new(
        DiagnosticIds.AbstractFunction,
        title: "AddInngestFunctions() does not register an abstract function class",
        messageFormat: "'{0}' is abstract, so AddInngestFunctions() does not register it, and the classes that derive from it do not inherit its attributes",
        category: FunctionsCategory,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "An abstract class cannot be created by the service provider, and [InngestFunction] is not inherited. Put the attribute on each concrete class instead.",
        helpLinkUri: GuideUrl + "#" + DiagnosticIds.AbstractFunction.ToLowerInvariant());

    public static readonly DiagnosticDescriptor GenericFunction = new(
        DiagnosticIds.GenericFunction,
        title: "AddInngestFunctions() does not register a generic function class",
        messageFormat: "'{0}' is generic, so AddInngestFunctions() does not register it; register each closed type with AddFunction<T>()",
        category: FunctionsCategory,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "The generator cannot know which type arguments the app uses. Register the closed types by hand, or suppress this warning for a class that is only a template.",
        helpLinkUri: GuideUrl + "#" + DiagnosticIds.GenericFunction.ToLowerInvariant());

    public static readonly DiagnosticDescriptor InaccessibleFunction = new(
        DiagnosticIds.InaccessibleFunction,
        title: "AddInngestFunctions() cannot reach a private function class",
        messageFormat: "'{0}' cannot be reached from generated code because it, or a type that contains it, is private, protected, private protected or file-local, so AddInngestFunctions() does not register it",
        category: FunctionsCategory,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "The generated method is a static method of its own. Make the class public or internal, with every type that contains it, or register it with AddFunction<T>() from inside the containing type.",
        helpLinkUri: GuideUrl + "#" + DiagnosticIds.InaccessibleFunction.ToLowerInvariant());

    public static readonly DiagnosticDescriptor NoPublicConstructor = new(
        DiagnosticIds.NoPublicConstructor,
        title: "A function class needs a public constructor",
        messageFormat: "'{0}' has no public constructor for the service provider to call, so AddInngestFunctions() does not register it",
        category: FunctionsCategory,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Function classes are created by the service provider, which only calls public constructors. Without one, every call of the function would fail.",
        helpLinkUri: GuideUrl + "#" + DiagnosticIds.NoPublicConstructor.ToLowerInvariant());

    public static readonly DiagnosticDescriptor InvalidOption = new(
        DiagnosticIds.InvalidOption,
        title: "An MSBuild property of the function registration has an invalid value",
        messageFormat: "The MSBuild property {0} is '{1}', which is not {2}; '{3}' is used instead",
        category: FunctionsCategory,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "InngestGenerateFunctionRegistration must be true or false, InngestFunctionsMethodName a C# identifier, and InngestFunctionsAccessibility public or internal.",
        helpLinkUri: GuideUrl + "#" + DiagnosticIds.InvalidOption.ToLowerInvariant());

    public static readonly DiagnosticDescriptor TypeNotInJsonContext = new(
        DiagnosticIds.TypeNotInJsonContext,
        title: "A type that Inngest serializes is missing from the JsonSerializerContext",
        messageFormat: "'{0}' is {1}, but no JsonSerializerContext lists it with [JsonSerializable], and reflection-based JSON is turned off",
        category: SerializationCategory,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "This project turns reflection-based System.Text.Json off, as Native AOT and trimming do, so Inngest can only read and write the types that a source-generated JsonSerializerContext knows. A type that is missing fails with a NotSupportedException when the function runs.",
        helpLinkUri: GuideUrl + "#" + DiagnosticIds.TypeNotInJsonContext.ToLowerInvariant());

    public static readonly DiagnosticDescriptor AnonymousType = new(
        DiagnosticIds.AnonymousType,
        title: "An anonymous type cannot be serialized without reflection",
        messageFormat: "{0} is an anonymous type, which no JsonSerializerContext can list, and reflection-based JSON is turned off",
        category: SerializationCategory,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A JsonSerializerContext lists types by name, and an anonymous type has none. Use a record, a class or a struct and list it with [JsonSerializable].",
        helpLinkUri: GuideUrl + "#" + DiagnosticIds.AnonymousType.ToLowerInvariant());
}
