// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System;
using System.Collections.Immutable;
using System.Linq;
using Inngest.SourceGeneration.Compat;

namespace Inngest.SourceGeneration.Functions;

// Everything the emitter needs, as values: no symbol or syntax node, so the pipeline can tell that an edit left the
// registration as it was and stop there.
internal sealed record Registration(string AssemblyName, EquatableArray<string> TypeNames, RegistrationOptions Options)
{
    public static Registration Create(ImmutableArray<string> typeNames, string assemblyName, RegistrationOptions options)
    {
        // A partial class is found once per part. The order is the names', so adding a file or moving a class
        // between files never reorders the registrations.
        var sorted = typeNames.Distinct(StringComparer.Ordinal).OrderBy(static name => name, StringComparer.Ordinal);
        return new Registration(assemblyName, new EquatableArray<string>(sorted.ToImmutableArray()), options);
    }
}
