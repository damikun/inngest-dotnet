using Inngest.Client;
using Inngest.Configuration;
using Inngest.Execution;
using Inngest.Registration;
using Inngest.Tests.AzureFunctions.Fakes;
using Inngest.Tests.Serving;
using Inngest.Tests.Serving.Fakes;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Testing;
using TestTimeProvider = Inngest.Tests.Execution.TestTimeProvider;

namespace Inngest.Tests.AzureFunctions;

// The fakes that stand in for the registry, the engine and Inngest's API in a Functions app that called AddInngest, and
// the clock the streaming of a call is timed by.
internal sealed class FunctionsFakes
{
    public FakeFunctionRegistry Registry { get; } = new();

    public FakeExecutionEngine Engine { get; } = new();

    public FakeInngestApi Api { get; } = new();

    public TestTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero));

    // AddInngest as Program.cs calls it, then the pieces that would reach Inngest or run functions replaced.
    public void AddTo(IServiceCollection services, InngestSettings settings)
    {
        services.AddFakeLogging();
        services.AddInngest(options => options.AppId = settings.AppId);
        services.Replace(ServiceDescriptor.Singleton(settings));
        services.Replace(ServiceDescriptor.Singleton<TimeProvider>(Time));
        services.Replace(ServiceDescriptor.Singleton<IFunctionRegistry>(Registry));
        services.Replace(ServiceDescriptor.Singleton<IExecutionEngine>(Engine));
        services.Replace(ServiceDescriptor.Singleton<IInngestApi>(Api));
        services.AddScoped<InvocationMarker>();
    }
}

// A Functions app with those fakes, and a way to send it a request the way the worker hands it to a function.
internal sealed class FunctionsHarness : IAsyncDisposable
{
    // Where the host receives Inngest: the app's own host name, and the route prefix of the host, /api.
    public const string EndpointUrl = "https://shop.azurewebsites.net/api/inngest";

    public const string CallUrl = EndpointUrl + "?fnId=shop-send-email&stepId=step";

    public const string CallBody = """{"ctx":{"attempt":0,"run_id":"01J0000000000000000000000"},"event":{"name":"user/signed.up"},"steps":{},"version":2}""";

    private readonly FunctionsFakes _fakes = new();
    private readonly ServiceProvider _services;
    private readonly List<AsyncServiceScope> _scopes = [];

    public FunctionsHarness(InngestSettings? settings = null)
    {
        var services = new ServiceCollection();
        _fakes.AddTo(services, settings ?? CommHandlerHarness.DevSettings);
        _services = services.BuildServiceProvider();
        Logs = _services.GetFakeLogCollector();
    }

    public FakeFunctionRegistry Registry => _fakes.Registry;

    public FakeExecutionEngine Engine => _fakes.Engine;

    public FakeInngestApi Api => _fakes.Api;

    public TestTimeProvider Time => _fakes.Time;

    public FakeLogCollector Logs { get; }

    // A request in a context of its own invocation, with the scope that Functions creates for it.
    public FakeHttpRequestData NewRequest(string method, string url, Stream? body = null, CancellationToken cancellationToken = default)
    {
        var scope = _services.CreateAsyncScope();
        _scopes.Add(scope);
        return new FakeHttpRequestData(new FakeFunctionContext(scope.ServiceProvider, cancellationToken), method, url, body);
    }

    // The function that serves Inngest: it hands the request to the SDK and returns what it gets back.
    public static async Task<FakeHttpResponseData> ServeAsync(FakeHttpRequestData request) =>
        (FakeHttpResponseData)await request.ServeInngestAsync();

    public Task<FakeHttpResponseData> SendAsync(string method, string url, string? body = null, CancellationToken cancellationToken = default) =>
        ServeAsync(NewRequest(method, url, body is null ? null : FakeHttpRequestData.BodyOf(body), cancellationToken));

    public async ValueTask DisposeAsync()
    {
        foreach (var scope in _scopes)
        {
            await scope.DisposeAsync();
        }

        await _services.DisposeAsync();
    }
}

// Something a function can only see if it runs in the scope of the invocation.
internal sealed class InvocationMarker
{
    public string? Value { get; set; }
}
