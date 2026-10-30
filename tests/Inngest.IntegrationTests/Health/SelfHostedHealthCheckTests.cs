using System.Security.Cryptography;
using Inngest.IntegrationTests.Harness;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Inngest.IntegrationTests.Health;

// The health check of an app in cloud mode, against a self-hosted server that wants its signing key on every API
// request. This is where the request of the check proves that Inngest accepts the key the SDK holds: Inngest Cloud
// answers /v2/health to anyone, so only a request that needs the key can tell.
[Collection(SigningCollection.Name)]
public sealed class SelfHostedHealthCheckTests(SigningFixture fixture) : IntegrationTest<SigningFixture>(fixture)
{
    private static string NewKey() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    [Fact(Timeout = 30_000)]
    public async Task Is_healthy_when_the_server_accepts_the_signing_key()
    {
        await using var app = HealthApp.Create(Configure(Fixture.SigningKey), checks => checks.AddInngest());

        var entry = await app.CheckAsync(Cancellation);

        entry.Status.ShouldBe(HealthStatus.Healthy);
        entry.Description.ShouldBe("Inngest accepted the signing key.");
        entry.Data["mode"].ShouldBe("cloud");
        entry.Data["acceptedKey"].ShouldBe("signingKey");
        entry.Data["apiStatusCode"].ShouldBe(200);
    }

    [Fact(Timeout = 30_000)]
    public async Task Is_unhealthy_when_the_server_refuses_the_signing_key()
    {
        var wrongKey = NewKey();
        await using var app = HealthApp.Create(Configure(wrongKey), checks => checks.AddInngest());

        var entry = await app.CheckAsync(Cancellation);

        entry.Status.ShouldBe(HealthStatus.Unhealthy);
        entry.Description.ShouldBe("Inngest refused the signing key (HTTP 401).");
        entry.Data["api"].ShouldBe("refused");
        entry.Description.ShouldNotBeNull().ShouldNotContain(wrongKey);
    }

    [Fact(Timeout = 30_000)]
    public async Task Falls_back_to_the_fallback_key_when_the_signing_key_is_refused()
    {
        await using var app = HealthApp.Create(Configure(NewKey(), Fixture.SigningKey), checks => checks.AddInngest());

        var entry = await app.CheckAsync(Cancellation);

        entry.Status.ShouldBe(HealthStatus.Healthy);
        entry.Description.ShouldBe("Inngest refused the signing key and accepted the fallback signing key.");
        entry.Data["acceptedKey"].ShouldBe("signingKeyFallback");
    }

    [Fact(Timeout = 30_000)]
    public async Task Is_unhealthy_when_neither_key_is_accepted()
    {
        await using var app = HealthApp.Create(Configure(NewKey(), NewKey()), checks => checks.AddInngest());

        var entry = await app.CheckAsync(Cancellation);

        entry.Status.ShouldBe(HealthStatus.Unhealthy);
        entry.Description.ShouldBe("Inngest refused the signing key and the fallback signing key (HTTP 401).");
    }

    private Action<InngestOptions> Configure(string signingKey, string? fallbackKey = null) =>
        options =>
        {
            options.AppId = Fixture.AppId;
            options.IsDev = false;
            options.SigningKey = signingKey;
            options.SigningKeyFallback = fallbackKey;
            options.BaseUrl = Fixture.Server.Url;
        };
}
