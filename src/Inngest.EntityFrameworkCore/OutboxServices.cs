// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Inngest.EntityFrameworkCore;

// Where a context finds the outbox's services. The application's service provider is where AddDbContext, AddDbContextPool
// and AddDbContextFactory leave them in the options of every context they make, so AddInngestEvent and the interceptor need
// nothing from the code that configured the context but the registration of the outbox.
internal static class OutboxServices
{
    public static IServiceProvider? ApplicationServicesOf(DbContext context) =>
        context.GetService<IDbContextOptions>().FindExtension<CoreOptionsExtension>()?.ApplicationServiceProvider;

    public static bool HasInterceptor(DbContext context) =>
        context.GetService<IDbContextOptions>().FindExtension<CoreOptionsExtension>()?.Interceptors?.OfType<OutboxInterceptor>().Any() == true;
}
