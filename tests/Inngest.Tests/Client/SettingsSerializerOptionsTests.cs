using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Inngest.Configuration;

namespace Inngest.Tests.Client;

public sealed class SettingsSerializerOptionsTests
{
    private static InngestSettings Create(JsonSerializerOptions serializerOptions) =>
        InngestSettings.Create(new InngestOptions { AppId = "shop", SerializerOptions = serializerOptions }, _ => null);

    private static readonly Type[] SdkTypes = [typeof(JsonElement), typeof(FunctionFailure), typeof(FunctionError), typeof(SendEventsResult)];

    [Fact]
    public void The_default_options_are_locked_and_serialize_the_sdk_types()
    {
        var options = new InngestOptions { AppId = "shop" };
        var settings = InngestSettings.Create(options, _ => null);

        settings.SerializerOptions.ShouldNotBeSameAs(options.SerializerOptions);
        options.SerializerOptions.IsReadOnly.ShouldBeFalse();
        settings.SerializerOptions.IsReadOnly.ShouldBeTrue();
        foreach (var type in SdkTypes)
        {
            settings.SerializerOptions.GetTypeInfo(type).ShouldNotBeNull();
        }
    }

    [Fact]
    public void An_app_with_only_its_own_source_generated_context_still_serializes_the_sdk_types()
    {
        var options = new JsonSerializerOptions { TypeInfoResolver = ClientTestJsonContext.Default };

        var settings = Create(options);

        settings.SerializerOptions.ShouldNotBeSameAs(options);
        options.IsReadOnly.ShouldBeFalse();
        settings.SerializerOptions.IsReadOnly.ShouldBeTrue();
        foreach (var type in SdkTypes)
        {
            settings.SerializerOptions.GetTypeInfo(type).ShouldNotBeNull();
        }

        settings.SerializerOptions.GetTypeInfo(typeof(OrderPlaced)).ShouldNotBeNull();
    }

    [Fact]
    public void The_sdk_types_round_trip_through_the_options_of_such_an_app()
    {
        var settings = Create(new JsonSerializerOptions { TypeInfoResolver = ClientTestJsonContext.Default });
        var typeInfo = (JsonTypeInfo<FunctionFailure>)settings.SerializerOptions.GetTypeInfo(typeof(FunctionFailure));

        var failure = JsonSerializer.Deserialize(
            """{"function_id":"shop-checkout","run_id":"01J","error":{"name":"Error","message":"boom"},"event":{"name":"cart/paid"}}""",
            typeInfo)!;

        failure.FunctionId.ShouldBe("shop-checkout");
        failure.Error.Message.ShouldBe("boom");
        failure.Event.GetProperty("name").GetString().ShouldBe("cart/paid");
        JsonSerializer.Serialize(failure, typeInfo).ShouldContain("\"function_id\":\"shop-checkout\"");
    }

    [Fact]
    public void Results_of_sending_events_serialize_with_the_naming_policy_of_the_app()
    {
        var settings = Create(new JsonSerializerOptions(JsonSerializerDefaults.Web) { TypeInfoResolver = ClientTestJsonContext.Default });
        var typeInfo = (JsonTypeInfo<SendEventsResult>)settings.SerializerOptions.GetTypeInfo(typeof(SendEventsResult));

        JsonSerializer.Serialize(new SendEventsResult(["a", "b"]), typeInfo).ShouldBe("""{"ids":["a","b"]}""");
        JsonSerializer.Deserialize("""{"ids":["a","b"]}""", typeInfo)!.Ids.ShouldBe(["a", "b"]);
    }

    [Fact]
    public void The_apps_own_context_keeps_priority_over_the_sdk_fallback()
    {
        var options = new JsonSerializerOptions { TypeInfoResolver = ClientTestJsonContext.Default };

        var typeInfo = Create(options).SerializerOptions.GetTypeInfo(typeof(OrderPlaced));

        typeInfo.OriginatingResolver.ShouldBeSameAs(ClientTestJsonContext.Default);
    }

    [Fact]
    public void Nothing_is_added_when_the_app_already_covers_the_sdk_types()
    {
        var options = new JsonSerializerOptions { TypeInfoResolver = CompleteTestJsonContext.Default };

        var settings = Create(options);

        settings.SerializerOptions.TypeInfoResolverChain.ShouldHaveSingleItem().ShouldBeSameAs(CompleteTestJsonContext.Default);
        settings.SerializerOptions.IsReadOnly.ShouldBeTrue();
    }

    [Fact]
    public void Options_that_are_already_locked_are_copied_instead_of_changed()
    {
        var original = new JsonSerializerOptions(JsonSerializerDefaults.Web) { TypeInfoResolver = ClientTestJsonContext.Default };
        original.MakeReadOnly();

        var settings = Create(original);

        settings.SerializerOptions.ShouldNotBeSameAs(original);
        settings.SerializerOptions.IsReadOnly.ShouldBeTrue();
        settings.SerializerOptions.PropertyNamingPolicy.ShouldBeSameAs(JsonNamingPolicy.CamelCase);
        settings.SerializerOptions.GetTypeInfo(typeof(FunctionFailure)).ShouldNotBeNull();
        settings.SerializerOptions.GetTypeInfo(typeof(OrderPlaced)).ShouldNotBeNull();

        // The caller's instance still behaves as before.
        original.TryGetTypeInfo(typeof(FunctionFailure), out _).ShouldBeFalse();
        original.TypeInfoResolverChain.ShouldHaveSingleItem();
    }

    [Fact]
    public void Options_that_need_nothing_are_still_copied()
    {
        var original = new JsonSerializerOptions { TypeInfoResolver = CompleteTestJsonContext.Default };

        var prepared = Create(original).SerializerOptions;

        prepared.ShouldNotBeSameAs(original);
        prepared.TypeInfoResolverChain.ShouldHaveSingleItem().ShouldBeSameAs(CompleteTestJsonContext.Default);
        original.IsReadOnly.ShouldBeFalse();
    }

    [Fact]
    public void Reflection_based_options_are_not_extended()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };

        var settings = Create(options);

        settings.SerializerOptions.ShouldNotBeSameAs(options);
        options.IsReadOnly.ShouldBeFalse();
        settings.SerializerOptions.TypeInfoResolverChain.ShouldHaveSingleItem().ShouldBeOfType<DefaultJsonTypeInfoResolver>();
        settings.SerializerOptions.GetTypeInfo(typeof(OrderPlaced)).ShouldNotBeNull();
    }

    [Fact]
    public void Options_without_a_resolver_keep_using_reflection()
    {
        var settings = Create(new JsonSerializerOptions());

        settings.SerializerOptions.IsReadOnly.ShouldBeTrue();
        settings.SerializerOptions.GetTypeInfo(typeof(OrderPlaced)).ShouldNotBeNull();
        settings.SerializerOptions.GetTypeInfo(typeof(FunctionFailure)).ShouldNotBeNull();
    }

    [Fact]
    public void The_resulting_options_cannot_be_changed()
    {
        var settings = Create(new JsonSerializerOptions { TypeInfoResolver = ClientTestJsonContext.Default });

        Should.Throw<InvalidOperationException>(() => settings.SerializerOptions.PropertyNamingPolicy = null);
        Should.Throw<InvalidOperationException>(() => settings.SerializerOptions.TypeInfoResolverChain.Add(CompleteTestJsonContext.Default));
    }
}
