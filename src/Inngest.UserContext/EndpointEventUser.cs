// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System.Text.Json;
using Inngest.Configuration;
using Inngest.Serving;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Inngest.UserContext;

// The user of a request to a durable endpoint, for the event its run starts with: the one the source names, as for an
// event the app sends outside a run. The endpoint runs inside the run, so its code and the events it sends get the
// run's user. Inngest does not keep the user of that event, so a call that resumes the run has none.
internal sealed class EndpointEventUser(
    UserContextCodec codec,
    IOptions<UserContextOptions> options,
    InngestSettings settings,
    ILogger<EndpointEventUser> logger,
    IUserContextSource? source = null) : IEndpointEventUser
{
    public async ValueTask<JsonElement?> GetAsync(DateTimeOffset timestamp, CancellationToken cancellationToken)
    {
        if (source is null || options.Value.EventFilter?.Invoke(new InngestEvent(EndpointCalls.EventName) { Timestamp = timestamp }) == false)
        {
            return null;
        }

        if (codec.Capture(await source.GetUserAsync(cancellationToken).ConfigureAwait(false)) is not { } snapshot)
        {
            return null;
        }

        if (codec.Protect(snapshot, EndpointCalls.EventName) is not { } protectedText)
        {
            Log.UserTooLarge(logger, EndpointCalls.EventName, options.Value.MaxSize);
            return null;
        }

        return EventUsers.WithEnvelope(user: null, protectedText, settings.SerializerOptions);
    }
}
