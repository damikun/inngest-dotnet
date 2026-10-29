// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest.Internal;

// Builds the "if" expression of a wait or a cancellation. Match compares one property of the triggering event
// ("event") with the awaited one ("async"). The TypeScript SDK lets Match replace If; here both apply when both
// are set, so neither is silently dropped.
internal static class MatchExpression
{
    public static string? Combine(string? match, string? @if)
    {
        var hasMatch = !string.IsNullOrWhiteSpace(match);
        var hasIf = !string.IsNullOrWhiteSpace(@if);
        return (hasMatch, hasIf) switch
        {
            (false, false) => null,
            (false, true) => @if,
            (true, false) => $"event.{match} == async.{match}",
            (true, true) => $"event.{match} == async.{match} && ({@if})",
        };
    }
}
