using System.Net;
using Inngest.Client;
using Inngest.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Inngest.Tests.Client;

public sealed class ClientRegistrationTests
{
    private static FakeTimeProvider NewClock() => new(new DateTimeOffset(2024, 1, 18, 14, 1, 44, TimeSpan.Zero));

    [Fact]
    public async Task The_client_and_the_api_client_read_their_settings_from_configuration()
    {
        var handler = new FakeHttpHandler()
            .Respond(HttpStatusCode.OK, ClientFixture.Accepted)
            .Respond(HttpStatusCode.OK, """{"ok":true,"modified":true}""");
        var variables = new Dictionary<string, string?>
        {
            ["INNGEST_DEV"] = "dev-box:9000",
            ["INNGEST_EVENT_KEY"] = "configured-key",
            ["INNGEST_SIGNING_KEY"] = "signkey-prod-12345678",
            ["INNGEST_ENV"] = "configured-env",
        };
        await using var provider = ClientServices.Build(handler, NewClock(), variables: variables, configureOptions: options => options.AppId = "shop");

        var result = await provider.GetRequiredService<IInngestClient>().SendAsync(new InngestEvent("app/user.created"));
        var registered = await provider.GetRequiredService<IInngestApi>().RegisterAsync("{}"u8.ToArray(), null, "dev", CancellationToken.None);

        result.Ids.ShouldNotBeEmpty();
        registered.Success.ShouldBeTrue();
        var requests = handler.Requests;
        requests[0].Uri.AbsoluteUri.ShouldBe("http://dev-box:9000/e/configured-key");
        requests[0].Header("X-Inngest-Env").ShouldBe("configured-env");
        requests[1].Uri.AbsoluteUri.ShouldBe("http://dev-box:9000/fn/register");
        requests[1].Header("X-Inngest-Env").ShouldBe("configured-env");
        requests[1].Header("Authorization").ShouldBe("Bearer signkey-prod-b2ed992186a5cb19f6668aade821f502c1d00970dfd0e35128d51bac4649916c");
        provider.GetRequiredService<IInngestClient>().AppId.ShouldBe("shop");
    }

    [Fact]
    public async Task Options_set_in_code_win_over_configuration()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK, ClientFixture.Accepted);
        var variables = new Dictionary<string, string?>
        {
            ["INNGEST_DEV"] = "1",
            ["INNGEST_EVENT_KEY"] = "configured-key",
        };
        await using var provider = ClientServices.Build(
            handler,
            NewClock(),
            variables: variables,
            configureOptions: options =>
            {
                options.AppId = "shop";
                options.IsDev = false;
                options.EventKey = "key-from-code";
            });

        await provider.GetRequiredService<IInngestClient>().SendAsync(new InngestEvent("app/user.created"));

        handler.Single.Uri.AbsoluteUri.ShouldBe("https://inn.gs/e/key-from-code");
    }

    [Fact]
    public async Task The_clients_are_shared()
    {
        await using var provider = ClientServices.Build(new FakeHttpHandler(), NewClock());

        provider.GetRequiredService<IInngestClient>().ShouldBeSameAs(provider.GetRequiredService<InngestClient>());
        provider.GetRequiredService<InngestClient>().ShouldBeSameAs(provider.GetRequiredService<InngestClient>());
        provider.GetRequiredService<IInngestApi>().ShouldBeSameAs(provider.GetRequiredService<IInngestApi>());
        provider.GetRequiredService<InngestSettings>().ShouldBeSameAs(provider.GetRequiredService<InngestSettings>());
    }

    [Fact]
    public async Task A_missing_app_id_is_reported_when_the_settings_are_first_needed()
    {
        await using var provider = ClientServices.Build(new FakeHttpHandler(), NewClock(), configureOptions: options => options.IsDev = true);

        var exception = Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<IInngestClient>());

        exception.Message.ShouldContain("InngestOptions.AppId");
    }
}
