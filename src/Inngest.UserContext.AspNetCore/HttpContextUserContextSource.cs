// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace Inngest.UserContext.AspNetCore;

// The user of the request that is being served. IHttpContextAccessor flows with the async context, so it also works
// where the SDK sends from a scope of its own, such as a send through IInngestClient from an endpoint.
internal sealed class HttpContextUserContextSource(IHttpContextAccessor accessor) : IUserContextSource
{
    public ValueTask<ClaimsPrincipal?> GetUserAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(accessor.HttpContext?.User);
}
