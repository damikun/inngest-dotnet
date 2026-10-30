// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

namespace System.Runtime.CompilerServices;

// netstandard2.0 has no init-only setters, which records use; the compiler only needs the type to exist.
internal static class IsExternalInit;
