// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System.Collections.Immutable;
using System.Linq;
using Inngest.Internal;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Inngest.SourceGeneration.Functions;

/// <summary>
/// Checks what the SDK would refuse at startup, or Inngest at the sync, in the options a function class declares: the
/// cron schedules and time strings of the attributes, and event triggers that do not match the type of the function's
/// event data. The schedules and the durations are read with the SDK's own parsers, which this assembly compiles too.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class FunctionOptionsAnalyzer : DiagnosticAnalyzer
{
    private const string NotATimeString = "is not a time string; use whole numbers with w, d, h, m or s, such as \"30s\", \"5m\" or \"1h30m\"";

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(
        DiagnosticDescriptors.InvalidCronSchedule,
        DiagnosticDescriptors.InvalidTimeString,
        DiagnosticDescriptors.EventNameMismatch);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSymbolAction(AnalyzeType, SymbolKind.NamedType);
        context.RegisterOperationAction(AnalyzeCronTrigger, OperationKind.ObjectCreation, OperationKind.Invocation);
    }

    private static void AnalyzeType(SymbolAnalysisContext context)
    {
        var type = (INamedTypeSymbol)context.Symbol;
        foreach (var attribute in type.GetAttributes())
        {
            if (attribute.AttributeClass is not { } attributeClass || !InngestTypes.IsDeclaredBySdk(attributeClass)
                || attribute.ApplicationSyntaxReference?.GetSyntax(context.CancellationToken) is not AttributeSyntax syntax)
            {
                continue;
            }

            switch (attributeClass.Name)
            {
                case "CronTriggerAttribute":
                    CheckCron(context, attribute, syntax);
                    CheckDuration(context, attribute, syntax, "Jitter", DurationLimits.CronJitter, required: false);
                    break;
                case "ThrottleAttribute":
                    CheckDuration(context, attribute, syntax, "period", DurationLimits.ThrottlePeriod, required: true);
                    break;
                case "RateLimitAttribute":
                    CheckDuration(context, attribute, syntax, "period", DurationLimits.RateLimitPeriod, required: true);
                    break;
                case "DebounceAttribute":
                    CheckDuration(context, attribute, syntax, "period", DurationLimits.DebouncePeriod, required: true);
                    CheckDuration(context, attribute, syntax, "Timeout", DurationLimits.DebounceTimeout, required: false);
                    break;
                case "BatchEventsAttribute":
                    CheckDuration(context, attribute, syntax, "timeout", DurationLimits.BatchEventsTimeout, required: true);
                    break;
                case "CancelOnAttribute":
                    CheckDuration(context, attribute, syntax, "Timeout", DurationLimits.CancelOnTimeout, required: false);
                    break;
                case "TimeoutsAttribute":
                    CheckDuration(context, attribute, syntax, "Start", DurationLimits.TimeoutsStart, required: false);
                    CheckDuration(context, attribute, syntax, "Finish", DurationLimits.TimeoutsFinish, required: false);
                    break;
            }
        }

        EventTriggers.Check(context, type);
    }

    private static void CheckCron(SymbolAnalysisContext context, AttributeData attribute, AttributeSyntax syntax)
    {
        if (Argument(attribute, "schedule") is not { Value: string schedule } || string.IsNullOrWhiteSpace(schedule))
        {
            return;
        }

        var timezone = Argument(attribute, "Timezone")?.Value as string;
        if (CronExpression.FindProblem(CronExpression.WithTimezone(schedule, timezone)) is { } problem)
        {
            context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.InvalidCronSchedule, ArgumentLocation(attribute, syntax, "schedule"), schedule, problem));
        }
    }

    // A time string the attribute parses at startup. An optional one may be left out or null.
    private static void CheckDuration(SymbolAnalysisContext context, AttributeData attribute, AttributeSyntax syntax, string name, string setting, bool required)
    {
        if (Argument(attribute, name) is not { } argument || (!required && argument.IsNull))
        {
            return;
        }

        var text = argument.Value as string;
        var problem = !TimeString.TryParse(text, out var duration) ? NotATimeString : DurationLimits.FindProblem(setting, duration);
        if (problem is null)
        {
            return;
        }

        var parts = setting.Split('.');
        context.ReportDiagnostic(Diagnostic.Create(
            DiagnosticDescriptors.InvalidTimeString,
            ArgumentLocation(attribute, syntax, name),
            parts[1],
            parts[0],
            text ?? "null",
            problem));
    }

    // new CronTrigger("...") and Trigger.Cron("..."), with a constant schedule, in the options of a delegate function.
    private static void AnalyzeCronTrigger(OperationAnalysisContext context)
    {
        var (method, arguments) = context.Operation switch
        {
            IObjectCreationOperation creation => (creation.Constructor, creation.Arguments),
            IInvocationOperation invocation => (invocation.TargetMethod, invocation.Arguments),
            _ => (null, ImmutableArray<IArgumentOperation>.Empty),
        };

        if (method is null || !IsCronTriggerFactory(method))
        {
            return;
        }

        IArgumentOperation? scheduleArgument = null;
        string? timezone = null;
        foreach (var argument in arguments)
        {
            switch (argument.Parameter?.Name)
            {
                case "Schedule" or "schedule":
                    scheduleArgument = argument;
                    break;
                case "timezone" when argument.Value.ConstantValue is { HasValue: true, Value: var value }:
                    timezone = value as string;
                    break;
            }
        }

        // An initializer can set Timezone on a new CronTrigger.
        if (context.Operation is IObjectCreationOperation { Initializer: { } initializer })
        {
            foreach (var member in initializer.Initializers)
            {
                if (member is ISimpleAssignmentOperation { Target: IPropertyReferenceOperation { Property.Name: "Timezone" }, Value.ConstantValue: { HasValue: true, Value: var value } })
                {
                    timezone = value as string;
                }
            }
        }

        if (scheduleArgument?.Value.ConstantValue is { HasValue: true, Value: string schedule }
            && !string.IsNullOrWhiteSpace(schedule)
            && CronExpression.FindProblem(CronExpression.WithTimezone(schedule, timezone)) is { } problem)
        {
            context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.InvalidCronSchedule, scheduleArgument.Value.Syntax.GetLocation(), schedule, problem));
        }
    }

    private static bool IsCronTriggerFactory(IMethodSymbol method) =>
        InngestTypes.IsDeclaredBySdk(method.ContainingType)
        && method.ContainingType.ContainingNamespace is { Name: "Inngest", ContainingNamespace.IsGlobalNamespace: true }
        && ((method.MethodKind == MethodKind.Constructor && method.ContainingType.Name == "CronTrigger")
            || (method.Name == "Cron" && method.ContainingType.Name == "Trigger"));

    // The value of a constructor argument by the name of its parameter, or of a named argument by the property.
    private static TypedConstant? Argument(AttributeData attribute, string name)
    {
        if (attribute.AttributeConstructor is { } constructor)
        {
            for (var i = 0; i < constructor.Parameters.Length && i < attribute.ConstructorArguments.Length; i++)
            {
                if (constructor.Parameters[i].Name == name)
                {
                    return attribute.ConstructorArguments[i];
                }
            }
        }

        foreach (var pair in attribute.NamedArguments)
        {
            if (pair.Key == name)
            {
                return pair.Value;
            }
        }

        return null;
    }

    private static Location ArgumentLocation(AttributeData attribute, AttributeSyntax syntax, string name)
    {
        var arguments = syntax.ArgumentList?.Arguments ?? default;
        foreach (var argument in arguments)
        {
            if (argument.NameEquals?.Name.Identifier.ValueText == name || argument.NameColon?.Name.Identifier.ValueText == name)
            {
                return argument.Expression.GetLocation();
            }
        }

        var index = -1;
        if (attribute.AttributeConstructor is { } constructor)
        {
            for (var i = 0; i < constructor.Parameters.Length; i++)
            {
                if (constructor.Parameters[i].Name == name)
                {
                    index = i;
                }
            }
        }

        var positional = arguments.Where(static argument => argument.NameEquals is null && argument.NameColon is null).ToList();
        return index >= 0 && index < positional.Count ? positional[index].Expression.GetLocation() : syntax.GetLocation();
    }
}
