// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

namespace Inngest.SourceGeneration.Functions;

// Why a class with [InngestFunction] is not registered by AddInngestFunctions().
internal enum FunctionClassProblem
{
    None,

    // A base type is missing from the compilation. The code is being written or a reference is gone, and whatever the
    // compiler reports for that is more useful than a second message about the same thing.
    Unresolved,
    NotAFunction,
    Abstract,
    Generic,
    Inaccessible,
    NoPublicConstructor,
}
