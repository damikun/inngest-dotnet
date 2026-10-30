using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Inngest.Encryption;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Inngest.Tests.Encryption;

public sealed class AddEncryptionTests
{
    [Fact]
    public void Adding_it_registers_one_middleware_however_often_it_is_added()
    {
        var services = new ServiceCollection();

        services.AddInngest(options => options.AppId = "shop")
            .AddEncryption(options => options.Key = "key")
            .AddEncryption();

        services.Count(descriptor => descriptor.ServiceType == typeof(InngestMiddleware)).ShouldBe(1);
    }

    [Fact]
    public async Task One_middleware_serves_a_call_and_all_calls_share_the_keys()
    {
        await using var provider = Build(options => options.Key = "key");

        await using var first = provider.CreateAsyncScope();
        await using var second = provider.CreateAsyncScope();
        var forFirst = first.ServiceProvider.GetServices<InngestMiddleware>().ShouldHaveSingleItem();
        var again = first.ServiceProvider.GetServices<InngestMiddleware>().ShouldHaveSingleItem();
        var forSecond = second.ServiceProvider.GetServices<InngestMiddleware>().ShouldHaveSingleItem();

        forFirst.ShouldBeOfType<EncryptionMiddleware>();
        again.ShouldBeSameAs(forFirst);
        forSecond.ShouldNotBeSameAs(forFirst);
        provider.GetRequiredService<PayloadEncryptor>().ShouldBeSameAs(provider.GetRequiredService<PayloadEncryptor>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("\n\t ")]
    public async Task Without_a_key_the_options_do_not_validate(string? key)
    {
        await using var provider = Build(options => options.Key = key);

        Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<IOptions<EncryptionOptions>>().Value)
            .Message.ShouldContain("EncryptionOptions.Key is required unless EncryptionOptions.Service is set");
    }

    [Fact]
    public async Task Fallback_keys_are_no_substitute_for_the_key()
    {
        await using var provider = Build(options => options.FallbackDecryptionKeys = ["old"]);

        Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<PayloadEncryptor>())
            .Message.ShouldContain("EncryptionOptions.Key is required");
    }

    [Fact]
    public async Task A_service_needs_no_key()
    {
        await using var provider = Build(options => options.Service = new ReversingService());

        provider.GetRequiredService<IOptions<EncryptionOptions>>().Value.Service.ShouldBeOfType<ReversingService>();
        provider.GetRequiredService<PayloadEncryptor>().ShouldNotBeNull();
    }

    [Fact]
    public async Task Every_problem_is_reported_at_once()
    {
        await using var provider = Build(options => options.EventEncryptionField = "");

        var message = Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<IOptions<EncryptionOptions>>().Value).Message;

        message.ShouldContain("EncryptionOptions.Key is required");
        message.ShouldContain("EncryptionOptions.EventEncryptionField must not be empty.");
    }

    [Fact]
    public async Task A_service_needs_an_identifier()
    {
        await using var provider = Build(options => options.Service = new NamelessService());

        Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<IOptions<EncryptionOptions>>().Value)
            .Message.ShouldContain("EncryptionOptions.Service needs an Identifier.");
    }

    [Fact]
    public async Task The_host_does_not_start_without_a_key()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Services.AddInngest(options => options.AppId = "shop").AddEncryption();
        using var host = builder.Build();

        var exception = await Should.ThrowAsync<OptionsValidationException>(() => host.StartAsync(TestContext.Current.CancellationToken));

        exception.Message.ShouldContain("EncryptionOptions.Key is required");
    }

    [Fact]
    public async Task The_host_starts_with_a_key()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Services.AddInngest(options => options.AppId = "shop").AddEncryption(options => options.Key = "key");
        using var host = builder.Build();

        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task The_options_can_also_be_configured_with_the_options_pattern()
    {
        var services = new ServiceCollection();
        services.AddInngest(options => options.AppId = "shop").AddEncryption();
        services.Configure<EncryptionOptions>(options => options.Key = "key");
        await using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<PayloadEncryptor>().ShouldNotBeNull();
    }

    [Fact]
    public async Task The_serializer_options_of_the_app_decide_how_event_data_is_named()
    {
        var snake = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        };
        var services = new ServiceCollection();
        services.AddInngest(options =>
        {
            options.AppId = "shop";
            options.SerializerOptions = snake;
        }).AddEncryption(options =>
        {
            options.Key = "key";
            options.EventEncryptionField = "secret_part";
        });
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var middleware = scope.ServiceProvider.GetServices<InngestMiddleware>().Single();

        IReadOnlyList<InngestEvent>? sent = null;
        await middleware.WrapSendEventAsync(
            null,
            [new InngestEvent("app/a", new { SecretPart = "hidden", Plain = 1 })],
            next =>
            {
                sent = next;
                return Task.FromResult(new SendEventsResult([]));
            },
            TestContext.Current.CancellationToken);

        var data = (JsonElement)sent.ShouldNotBeNull().Single().Data!;
        data.GetProperty("secret_part").GetProperty("__ENCRYPTED__").GetBoolean().ShouldBeTrue();
        data.GetProperty("plain").GetInt32().ShouldBe(1);
    }

    private static ServiceProvider Build(Action<EncryptionOptions> configure)
    {
        var services = new ServiceCollection();
        services.AddInngest(options => options.AppId = "shop").AddEncryption(configure);
        return services.BuildServiceProvider();
    }

    private sealed class NamelessService : EncryptionService
    {
        public override string Identifier => " ";

        public override string Encrypt(ReadOnlySpan<byte> json) => throw new NotSupportedException();

        public override byte[] Decrypt(string data) => throw new NotSupportedException();
    }
}
