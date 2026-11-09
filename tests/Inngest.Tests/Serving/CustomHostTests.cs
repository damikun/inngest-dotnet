using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Inngest.Tests.Serving;

// A host adapter written against the public API only, as a user would write one for a host the SDK does not cover.
public sealed class CustomHostTests
{
    private const string SigningKey = "signkey-test-0a1b2c3d4e5f60718293a4b5c6d7e8f9";
    private const string Body = """{"ctx":{"attempt":0,"run_id":"01J0000000000000000000000"},"event":{"data":{"name":"Ada"},"name":"user/signed.up"},"steps":{},"version":2}""";

    [Fact]
    public async Task A_signed_call_runs_the_function_and_comes_back_signed()
    {
        await using var services = Services();
        var handler = services.GetRequiredService<InngestCommHandler>();
        var verifier = services.GetRequiredService<InngestSignatureVerifier>();
        var signer = new InngestSignatureVerifier(SigningKey);
        var request = new HostRequest("POST", "https://shop.example/inngest?fnId=shop-greet&stepId=step", Body)
        {
            Signature = Sign(Body),
        };

        using var response = await handler.HandleAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(200);
        Encoding.UTF8.GetString(response.Body.Span).ShouldBe("\"Hello, Ada\"");
        var signature = response.Headers.Single(header => header.Key == "X-Inngest-Signature").Value;
        verifier.Verify(signature, response.Body.Span).ShouldBeTrue();
        signer.Verify(signature, response.Body.Span).ShouldBeTrue();
        response.Headers.ShouldContain(new KeyValuePair<string, string>("X-Inngest-Framework", "my-host"));
    }

    [Fact]
    public async Task An_unsigned_call_is_refused()
    {
        await using var services = Services();
        var handler = services.GetRequiredService<InngestCommHandler>();

        using var response = await handler.HandleAsync(new HostRequest("POST", "https://shop.example/inngest?fnId=shop-greet&stepId=step", Body));

        response.StatusCode.ShouldBe(401);
    }

    [Fact]
    public async Task Signed_introspection_describes_the_app()
    {
        await using var services = Services();
        var handler = services.GetRequiredService<InngestCommHandler>();

        using var response = await handler.HandleAsync(new HostRequest("GET", "https://shop.example/inngest", "") { Signature = Sign("") });

        response.StatusCode.ShouldBe(200);
        using var body = JsonDocument.Parse(response.Body);
        body.RootElement.GetProperty("function_count").GetInt32().ShouldBe(1);
    }

    private static ServiceProvider Services()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInngest(options =>
        {
            options.AppId = "shop";
            options.IsDev = false;
            options.SigningKey = SigningKey;
        }).AddFunction<JsonElement>(
            new FunctionOptions { Id = "greet", Triggers = [Trigger.Event("user/signed.up")] },
            (context, _) => Task.FromResult<object?>($"Hello, {context.Event.Data.GetProperty("name").GetString()}"));
        return services.BuildServiceProvider();
    }

    // Signs as Inngest does, with the internal signer the SDK checks against.
    private static string Sign(string body) =>
        new Inngest.Signing.RequestSigner(TimeProvider.System).Sign(Encoding.UTF8.GetBytes(body), Inngest.Signing.SigningKey.Create(SigningKey)!);

    private sealed class HostRequest(string method, string url, string body) : IServeRequest
    {
        private readonly Uri _url = new(url);

        public string? Signature { get; init; }

        public string Method => method;

        public Uri Url => _url;

        public ReadOnlyMemory<byte> Body { get; } = Encoding.UTF8.GetBytes(body);

        public string Framework => "my-host";

        public IServiceProvider? Services => null;

        public string? GetHeader(string name) =>
            string.Equals(name, "X-Inngest-Signature", StringComparison.OrdinalIgnoreCase) ? Signature : null;

        public string? GetQuery(string name)
        {
            foreach (var pair in _url.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = pair.Split('=', 2);
                if (Uri.UnescapeDataString(parts[0]) == name)
                {
                    return parts.Length == 2 ? Uri.UnescapeDataString(parts[1]) : string.Empty;
                }
            }

            return null;
        }
    }
}
