// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Inngest.SourceGeneration.Functions;

// Compares the event triggers of a function class with the event its data type names. The name is known when the type
// implements IInngestEventData with an EventName that returns a string literal, as event types are written; any other
// type, or a name computed some other way, is left alone.
internal static class EventTriggers
{
    public static void Check(SymbolAnalysisContext context, INamedTypeSymbol type)
    {
        if (EventDataType(type) is not { } data || EventName(data, context.CancellationToken) is not { } expected)
        {
            return;
        }

        var triggers = new List<(string Name, Location Location)>();
        foreach (var attribute in type.GetAttributes())
        {
            if (attribute.AttributeClass is not { Name: "EventTriggerAttribute" } attributeClass || !InngestTypes.IsDeclaredBySdk(attributeClass)
                || attribute.ApplicationSyntaxReference?.GetSyntax(context.CancellationToken) is not AttributeSyntax syntax)
            {
                continue;
            }

            string? name;
            Location location;
            if (attributeClass.IsGenericType)
            {
                name = attributeClass.TypeArguments[0] is INamedTypeSymbol other ? EventName(other, context.CancellationToken) : null;
                location = syntax.Name.GetLocation();
            }
            else
            {
                name = attribute.ConstructorArguments.Length == 1 ? attribute.ConstructorArguments[0].Value as string : null;
                location = syntax.ArgumentList is { Arguments.Count: > 0 } list ? list.Arguments[0].Expression.GetLocation() : syntax.GetLocation();
            }

            // A trigger whose name is not known could be the right one.
            if (name is null)
            {
                return;
            }

            if (Matches(name, expected))
            {
                return;
            }

            triggers.Add((name, location));
        }

        var display = type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
        var dataDisplay = data.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
        foreach (var (name, location) in triggers)
        {
            context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.EventNameMismatch, location, display, dataDisplay, expected, name));
        }
    }

    // A trigger such as "shop/order.*" runs for every event whose name starts with what comes before the star.
    private static bool Matches(string trigger, string name) =>
        trigger.EndsWith("*", StringComparison.Ordinal)
            ? name.StartsWith(trigger.Substring(0, trigger.Length - 1), StringComparison.Ordinal)
            : string.Equals(trigger, name, StringComparison.Ordinal);

    // TEventData of InngestFunction<TEventData, TResult>, which InngestFunction<TEventData> derives from.
    private static INamedTypeSymbol? EventDataType(INamedTypeSymbol type)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (InngestTypes.IsFunctionWithResult(current))
            {
                return current.TypeArguments[0] as INamedTypeSymbol;
            }
        }

        return null;
    }

    private static string? EventName(INamedTypeSymbol data, System.Threading.CancellationToken cancellationToken)
    {
        IPropertySymbol? property = null;
        foreach (var candidate in data.AllInterfaces)
        {
            if (!InngestTypes.IsEventDataInterface(candidate))
            {
                continue;
            }

            foreach (var member in candidate.GetMembers("EventName"))
            {
                property = data.FindImplementationForInterfaceMember(member) as IPropertySymbol;
            }
        }

        if (property is null)
        {
            return null;
        }

        foreach (var reference in property.DeclaringSyntaxReferences)
        {
            if (reference.GetSyntax(cancellationToken) is not PropertyDeclarationSyntax declaration)
            {
                continue;
            }

            var getter = declaration.AccessorList is { Accessors.Count: 1 } accessors ? accessors.Accessors[0] : null;
            var value = declaration.ExpressionBody?.Expression
                ?? declaration.Initializer?.Value
                ?? getter?.ExpressionBody?.Expression
                ?? (getter?.Body is { Statements.Count: 1 } body && body.Statements[0] is ReturnStatementSyntax returned ? returned.Expression : null);

            if (value is LiteralExpressionSyntax literal && literal.Token.Value is string name)
            {
                return name;
            }
        }

        return null;
    }
}
