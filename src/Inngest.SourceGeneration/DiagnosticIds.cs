// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

namespace Inngest.SourceGeneration;

internal static class DiagnosticIds
{
    public const string NotAFunction = "INNGEST001";
    public const string AbstractFunction = "INNGEST002";
    public const string GenericFunction = "INNGEST003";
    public const string InaccessibleFunction = "INNGEST004";
    public const string NoPublicConstructor = "INNGEST005";
    public const string InvalidOption = "INNGEST006";
    public const string TypeNotInJsonContext = "INNGEST010";
    public const string AnonymousType = "INNGEST011";
}
