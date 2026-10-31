using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Inngest.SignalR;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Inngest.Tests.SignalR;

// SignalR's JSON protocol as a Native AOT app has it, with no reflection to find a type with, so that a context is the
// only way to one. The tests give the protocol a context and nothing else.
public sealed class SignalRJsonTests
{
    [Fact]
    public void A_message_and_the_news_of_a_loss_are_written_with_the_context_alone()
    {
        var protocol = ProtocolOf(SignalRJsonContext.Default);

        using var message = Written(protocol, new InvocationMessage("Realtime", ["""{"kind":"data","data":"a \"quote\""}"""]));
        using var lost = Written(protocol, new InvocationMessage("RealtimeError", ["order:o-1", new[] { "status", "tokens" }, "behind"]));

        var sent = message.RootElement.GetProperty("arguments");
        sent.GetArrayLength().ShouldBe(1);
        sent[0].GetString().ShouldBe("""{"kind":"data","data":"a \"quote\""}""");
        var told = lost.RootElement.GetProperty("arguments");
        told[0].GetString().ShouldBe("order:o-1");
        told[1].EnumerateArray().Select(topic => topic.GetString()).ShouldBe(["status", "tokens"]);
        told[2].GetString().ShouldBe("behind");
    }

    [Fact]
    public void The_arguments_of_Subscribe_are_read_with_the_context_alone()
    {
        var protocol = ProtocolOf(SignalRJsonContext.Default);
        var input = new ReadOnlySequence<byte>(Encoding.UTF8.GetBytes("""{"type":1,"target":"Subscribe","arguments":["order:o-1",["status","tokens"]]}""" + "\u001e"));

        protocol.TryParseMessage(ref input, new SubscribeBinder(), out var parsed).ShouldBeTrue();

        var invocation = parsed.ShouldBeOfType<InvocationMessage>();
        invocation.Target.ShouldBe("Subscribe");
        invocation.Arguments[0].ShouldBe("order:o-1");
        invocation.Arguments[1].ShouldBe(new[] { "status", "tokens" });
    }

    [Fact]
    public void A_context_without_the_types_could_do_neither()
    {
        var protocol = ProtocolOf(IntegersOnlyJsonContext.Default);
        var input = new ReadOnlySequence<byte>(Encoding.UTF8.GetBytes("""{"type":1,"target":"Subscribe","arguments":["order:o-1",["status"]]}""" + "\u001e"));

        Should.Throw<NotSupportedException>(() => protocol.GetMessageBytes(new InvocationMessage("Realtime", ["text"])));

        // SignalR answers an invocation whose arguments it cannot read with an error to the caller.
        protocol.TryParseMessage(ref input, new SubscribeBinder(), out var parsed).ShouldBeTrue();
        parsed.ShouldBeOfType<InvocationBindingFailureMessage>();
    }

    [Fact]
    public void In_an_app_without_reflection_the_context_is_added_to_the_protocol_once_however_many_hubs_ask()
    {
        var services = new ServiceCollection();
        services.AddSignalR();

        InngestSignalRServiceCollectionExtensions.AddJsonContext(services, reflectionEnabled: false);
        InngestSignalRServiceCollectionExtensions.AddJsonContext(services, reflectionEnabled: false);
        using var provider = services.BuildServiceProvider();

        var resolvers = provider.GetRequiredService<IOptions<JsonHubProtocolOptions>>().Value.PayloadSerializerOptions.TypeInfoResolverChain;
        resolvers.Count(resolver => resolver == SignalRJsonContext.Default).ShouldBe(1);
    }

    [Fact]
    public void In_an_app_with_reflection_nothing_is_added()
    {
        var services = new ServiceCollection();
        services.AddSignalR();

        InngestSignalRServiceCollectionExtensions.AddJsonContext(services, reflectionEnabled: true);
        using var provider = services.BuildServiceProvider();

        var resolvers = provider.GetRequiredService<IOptions<JsonHubProtocolOptions>>().Value.PayloadSerializerOptions.TypeInfoResolverChain;
        resolvers.ShouldNotContain(SignalRJsonContext.Default);
    }

    private static JsonHubProtocol ProtocolOf(JsonSerializerContext context) =>
        new(Options.Create(new JsonHubProtocolOptions { PayloadSerializerOptions = new JsonSerializerOptions { TypeInfoResolver = context } }));

    private static JsonDocument Written(JsonHubProtocol protocol, HubMessage message) =>
        JsonDocument.Parse(Encoding.UTF8.GetString(protocol.GetMessageBytes(message).Span).TrimEnd('\u001e'));

    private sealed class SubscribeBinder : IInvocationBinder
    {
        public IReadOnlyList<Type> GetParameterTypes(string methodName) => [typeof(string), typeof(string[])];

        public Type GetReturnType(string invocationId) => throw new NotSupportedException();

        public Type GetStreamItemType(string streamId) => throw new NotSupportedException();
    }
}

// A context that knows integers only, which is what a Native AOT app has of the types of the bridge before it registers any.
[JsonSerializable(typeof(int))]
internal sealed partial class IntegersOnlyJsonContext : JsonSerializerContext;
