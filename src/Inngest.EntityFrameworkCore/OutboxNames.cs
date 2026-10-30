// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using Microsoft.EntityFrameworkCore;

namespace Inngest.EntityFrameworkCore;

// Each context has settings of its own, as named options under its type's name.
internal static class OutboxNames
{
    public static string For<TContext>()
        where TContext : DbContext =>
        typeof(TContext).FullName ?? typeof(TContext).Name;
}
