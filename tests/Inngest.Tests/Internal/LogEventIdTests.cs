using System.Reflection;
using Inngest.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;

namespace Inngest.Tests.Internal;

public sealed class LogEventIdTests
{
    // Apps filter and alert on event ids, so each id of the SDK's messages must mean one thing.
    [Fact]
    public void Every_log_message_of_the_sdk_has_an_event_id_of_its_own()
    {
        Assembly[] packages =
        [
            typeof(InngestOptions).Assembly,
            typeof(InngestEndpointRouteBuilderExtensions).Assembly,
            typeof(ConnectOptions).Assembly,
            typeof(InngestInstrumentation).Assembly,
            typeof(InngestTestHost).Assembly,
        ];

        var ids = packages
            .SelectMany(static assembly => assembly.GetTypes())
            .SelectMany(static type => type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            .Select(static method => method.GetCustomAttribute<LoggerMessageAttribute>())
            .OfType<LoggerMessageAttribute>()
            .Select(static message => message.EventId)
            .ToList();

        ids.ShouldNotBeEmpty();
        ids.GroupBy(static id => id).Where(static group => group.Count() > 1).Select(static group => group.Key).ShouldBeEmpty();
    }
}
