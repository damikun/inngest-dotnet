; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
INNGEST001 | Inngest.Functions | Warning | A class with [InngestFunction] is not a function
INNGEST002 | Inngest.Functions | Warning | AddInngestFunctions() does not register an abstract function class
INNGEST003 | Inngest.Functions | Warning | AddInngestFunctions() does not register a generic function class
INNGEST004 | Inngest.Functions | Warning | AddInngestFunctions() cannot reach a private function class
INNGEST005 | Inngest.Functions | Warning | A function class needs a public constructor
INNGEST006 | Inngest.Functions | Warning | An MSBuild property of the function registration has an invalid value
INNGEST010 | Inngest.Serialization | Warning | A type that Inngest serializes is missing from the JsonSerializerContext
INNGEST011 | Inngest.Serialization | Warning | An anonymous type cannot be serialized without reflection
