// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using Microsoft.EntityFrameworkCore;

namespace Inngest.EntityFrameworkCore;

// ExecuteUpdate and ExecuteDelete are in Microsoft.EntityFrameworkCore.Relational in EF Core 8 and in
// Microsoft.EntityFrameworkCore from EF Core 9 on, so code compiled against one line does not find them in the other.
// The net8.0 build is compiled against EF Core 8 and the net10.0 build against EF Core 10. An app on .NET 9 gets the
// net8.0 build, and with EF Core 9 the first update would fail with a MissingMethodException that names no cause.
internal static class EntityFrameworkLine
{
    public static void EnsureSupported()
    {
        var loaded = typeof(DbContext).Assembly.GetName().Version?.Major ?? 0;
#if NET10_0_OR_GREATER
        if (loaded < 10)
        {
            throw new InvalidOperationException(
                $"This build of Inngest.EntityFrameworkCore needs Entity Framework Core 10 or later, but version {loaded} is loaded.");
        }
#else
        if (loaded != 8)
        {
            throw new InvalidOperationException(
                $"This build of Inngest.EntityFrameworkCore is made for Entity Framework Core 8, but version {loaded} is loaded. " +
                "ExecuteUpdate moved to another assembly in EF Core 9, so the outbox needs EF Core 8 on .NET 8 and 9, or EF Core 10 on .NET 10.");
        }
#endif
    }
}
