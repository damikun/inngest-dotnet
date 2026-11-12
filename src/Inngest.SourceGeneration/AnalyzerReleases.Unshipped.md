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
INNGEST020 | Inngest.Steps | Warning | A step's handler assigns a variable that the function reads outside the step
INNGEST021 | Inngest.Steps | Info | A finally block or a using declaration encloses a step
INNGEST022 | Inngest.Steps | Warning | A step id changes from one call to the next
INNGEST023 | Inngest.Steps | Warning | Blocking on the task of a step
INNGEST024 | Inngest.Steps | Warning | A step is called inside the handler of another step
INNGEST025 | Inngest.Steps | Warning | The task of a step or of SendAsync is dropped
INNGEST026 | Inngest.Steps | Warning | A function reads the clock or a random value outside a step
INNGEST027 | Inngest.Steps | Warning | A function waits with Task.Delay or Thread.Sleep
INNGEST028 | Inngest.Steps | Info | A function awaits a call that is not a step
INNGEST029 | Inngest.Steps | Info | A loop calls a step with the same id for each item of an unordered collection
INNGEST030 | Inngest.Functions | Warning | A cron schedule that Inngest does not accept
INNGEST031 | Inngest.Functions | Warning | A time string in a function attribute is not valid
INNGEST032 | Inngest.Steps | Warning | Two steps of a function have the same id
INNGEST033 | Inngest.Functions | Warning | A function is triggered by an event other than the one its event data is for
