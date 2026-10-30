using System.Text;
using System.Text.Json;
using Inngest.Configuration;
using Inngest.Serving;
using Inngest.Signing;
using Inngest.Tests.Serving.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;

namespace Inngest.Tests.Serving;

// An InngestCommHandler over fakes. The handler's signer and the one tests sign requests with share a fake clock.
internal sealed class CommHandlerHarness
{
    // The keys of the TypeScript SDK's introspection test, whose hashes start with 66687aadf862 and 02d449a31fbb.
    public static readonly SigningKey PrimaryKey = SigningKey.Create("signkey-test-" + new string('0', 64))!;
    public static readonly SigningKey FallbackKey = SigningKey.Create("signkey-test-" + new string('1', 64))!;
    public static readonly SigningKey UnknownKey = SigningKey.Create("signkey-test-" + new string('2', 64))!;

    private readonly ServiceProvider _services = new ServiceCollection().AddScoped<ScopedDependency>().AddScoped<ThrowsOnDispose>().BuildServiceProvider();

    public static InngestSettings DevSettings { get; } = new()
    {
        AppId = "shop",
        Mode = InngestMode.Dev,
        ApiBaseUrl = new Uri("http://localhost:8288/"),
        EventApiBaseUrl = new Uri("http://localhost:8288/"),
        SerializerOptions = JsonSerializerOptions.Default,
    };

    public static InngestSettings CloudSettings { get; } = new()
    {
        AppId = "shop",
        Mode = InngestMode.Cloud,
        SigningKey = PrimaryKey,
        ApiBaseUrl = new Uri("https://api.inngest.com/"),
        EventApiBaseUrl = new Uri("https://inn.gs/"),
        SerializerOptions = JsonSerializerOptions.Default,
    };

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero));

    public FakeFunctionRegistry Registry { get; } = new();

    public FakeExecutionEngine Engine { get; } = new();

    public FakeInngestApi Api { get; } = new();

    public FakeLogger<InngestCommHandler> Logger { get; } = new();

    public RequestSigner Signer => new(Time);

    public IServiceProvider RootServices => _services;

    public InngestCommHandler CreateHandler(InngestSettings settings) =>
        new(settings, Registry, Engine, Api, new RequestSigner(Time), _services.GetRequiredService<IServiceScopeFactory>(), Logger);

    public Task<ServeResponse> SendAsync(InngestSettings settings, FakeServeRequest request) =>
        CreateHandler(settings).HandleAsync(request, TestContext.Current.CancellationToken);

    public FakeServeRequest Sign(FakeServeRequest request, SigningKey key) =>
        request.WithHeader("X-Inngest-Signature", Signer.Sign(request.Body.Span, key));

    // Checks the response signature the way Inngest would, against exactly one key.
    public bool IsSignedWith(ServeResponse response, SigningKey key) =>
        Signer.Verify(response.Header("X-Inngest-Signature"), response.Body.Span, key, fallbackKey: null).IsValid;
}

internal sealed class ScopedDependency : IDisposable
{
    public bool IsDisposed { get; private set; }

    public void Dispose() => IsDisposed = true;
}

internal sealed class ThrowsOnDispose : IDisposable
{
    public void Dispose() => throw new InvalidOperationException("Disposing failed");
}

internal static class ServeResponseExtensions
{
    public static string BodyText(this ServeResponse response) => Encoding.UTF8.GetString(response.Body.Span);

    public static string? Header(this ServeResponse response, string name)
    {
        string? value = null;
        foreach (var header in response.Headers)
        {
            if (string.Equals(header.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                value.ShouldBeNull($"{name} is set twice");
                value = header.Value;
            }
        }

        return value;
    }

    public static string[] HeaderNames(this ServeResponse response) => [.. response.Headers.Select(header => header.Key)];
}
