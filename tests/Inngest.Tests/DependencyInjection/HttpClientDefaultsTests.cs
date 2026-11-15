using System.Net;
using System.Text;
using Inngest.Client;
using Inngest.Tests.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace Inngest.Tests.DependencyInjection;

// Handlers that an app adds to every HttpClient with ConfigureHttpClientDefaults, as Aspire's service defaults add the
// standard resilience handler, meet the SDK's own client, which retries and waits on its own terms.
public sealed class HttpClientDefaultsTests
{
    private const string ServerError = """{"ids":[],"status":500,"error":"upstream is down"}""";

    private readonly FakeHttpHandler _inngest = new();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UnixEpoch);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_resilience_handler_for_every_client_stays_off_the_sdk_client(bool defaultsFirst)
    {
        _inngest.RespondAlways(HttpStatusCode.InternalServerError, ServerError);
        Action<IHttpClientBuilder> resilience = Resilience;
        await using var provider = defaultsFirst ? Build(defaults: resilience) : Build(defaultsAfter: resilience);

        await Should.ThrowAsync<InngestException>(
            _time.AdvanceUntilCompletedAsync(provider.GetRequiredService<IInngestClient>().SendAsync(new InngestEvent("shop/order.placed"), Cancel)));

        _inngest.Requests.Count.ShouldBe(InngestClient.MaxAttempts);
    }

    // Inngest consumes the wait on the first delivery, so a resent signal would find no run and report null.
    [Fact]
    public async Task A_signal_is_sent_once_under_a_resilience_handler_for_every_client()
    {
        _inngest.RespondAlways(HttpStatusCode.InternalServerError, """{"error":"try later"}""");
        await using var provider = Build(defaults: Resilience);

        await Should.ThrowAsync<InngestException>(provider.GetRequiredService<IInngestClient>().SendSignalAsync(new InngestSignal("order/approved"), Cancel));

        _inngest.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Other_clients_keep_the_resilience_handler_for_every_client()
    {
        _inngest.RespondAlways(HttpStatusCode.InternalServerError, ServerError);
        await using var provider = Build(defaults: Resilience);

        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("other");
        using var response = await client.GetAsync(new Uri("http://localhost:8288/health"), Cancel);

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        _inngest.Requests.Count.ShouldBe(4);
    }

    [Fact]
    public async Task A_resilience_handler_added_to_the_sdk_client_after_AddInngest_stays()
    {
        _inngest.RespondAlways(HttpStatusCode.InternalServerError, ServerError);
        await using var provider = Build(after: services => Resilience(services.AddHttpClient(InngestServiceCollectionExtensions.HttpClientName)));

        await Should.ThrowAsync<InngestException>(
            _time.AdvanceUntilCompletedAsync(provider.GetRequiredService<IInngestClient>().SendAsync(new InngestEvent("shop/order.placed"), Cancel)));

        _inngest.Requests.Count.ShouldBe(InngestClient.MaxAttempts * 4);
    }

    [Fact]
    public async Task A_send_that_a_handler_fails_with_its_own_exception_is_retried_and_then_throws_InngestException()
    {
        _inngest.RespondAlways(HttpStatusCode.OK, ClientFixture.Accepted);
        await using var provider = Build(defaults: builder => builder.AddHttpMessageHandler(() => new Throwing(() => new TimeoutRejectedException())));

        var exception = await Should.ThrowAsync<InngestException>(
            _time.AdvanceUntilCompletedAsync(provider.GetRequiredService<IInngestClient>().SendAsync(new InngestEvent("shop/order.placed"), Cancel)));

        exception.StatusCode.ShouldBeNull();
        exception.InnerException?.InnerException.ShouldBeOfType<TimeoutRejectedException>();
    }

    [Fact]
    public async Task A_signal_that_a_handler_fails_with_its_own_exception_throws_InngestException()
    {
        await using var provider = Build(defaults: builder => builder.AddHttpMessageHandler(() => new Throwing(() => new BrokenCircuitException())));

        var exception = await Should.ThrowAsync<InngestException>(
            provider.GetRequiredService<IInngestClient>().SendSignalAsync(new InngestSignal("order/approved"), Cancel));

        exception.StatusCode.ShouldBeNull();
        exception.Message.ShouldStartWith("Failed to send signal \"order/approved\": ");
    }

    // A realtime subscription takes a failure without a status for a lost connection and subscribes again.
    [Fact]
    public async Task A_realtime_call_that_a_handler_fails_with_its_own_exception_throws_InngestException_without_a_status()
    {
        await using var provider = Build(defaults: builder => builder.AddHttpMessageHandler(() => new Throwing(() => new BrokenCircuitException())));

        var exception = await Should.ThrowAsync<InngestException>(
            provider.GetRequiredService<IInngestRealtime>().GetSubscriptionTokenAsync("order:7", ["status"], Cancel));

        exception.StatusCode.ShouldBeNull();
        exception.InnerException?.InnerException.ShouldBeOfType<BrokenCircuitException>();
    }

    // A stream is read from its producer as it is sent, so sending it again would call the producer again, such as a
    // paid model, and subscribers would get the text twice.
    [Fact]
    public async Task A_handler_for_every_client_that_resends_cannot_send_a_stream_twice()
    {
        var primary = new Draining(HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK);
        await using var provider = Build(defaults: builder => builder.AddHttpMessageHandler(() => new Resending()), primary: primary);
        var reads = 0;

        var exception = await Should.ThrowAsync<InngestException>(
            provider.GetRequiredService<IInngestRealtime>().PublishStreamAsync(new RealtimeTopic("chat:42", "tokens"), Tokens(), Cancel));

        reads.ShouldBe(1);
        primary.Bodies.ShouldBe(["Hello"]);
        exception.Message.ShouldStartWith("Failed to publish to topic \"tokens\" of channel \"chat:42\": ");

        async IAsyncEnumerable<string> Tokens()
        {
            reads++;
            await Task.Yield();
            yield return "Hello";
        }
    }

    private static void Resilience(IHttpClientBuilder builder) =>
        builder.AddStandardResilienceHandler(options => options.Retry.Delay = TimeSpan.Zero);

    private ServiceProvider Build(
        Action<IHttpClientBuilder>? defaults = null,
        Action<IHttpClientBuilder>? defaultsAfter = null,
        Action<IServiceCollection>? after = null,
        HttpMessageHandler? primary = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton<TimeProvider>(_time);
        if (defaults is not null)
        {
            services.ConfigureHttpClientDefaults(defaults);
        }

        services.AddInngest(options =>
        {
            options.AppId = "shop";
            options.IsDev = true;
        });
        services.ConfigureHttpClientDefaults(builder => builder.ConfigurePrimaryHttpMessageHandler(() => primary ?? _inngest));
        if (defaultsAfter is not null)
        {
            services.ConfigureHttpClientDefaults(defaultsAfter);
        }

        after?.Invoke(services);
        return services.BuildServiceProvider();
    }

    // Sends a request again when Inngest answers with a 5xx, as a retry policy does.
    private sealed class Resending : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            if ((int)response.StatusCode < 500)
            {
                return response;
            }

            response.Dispose();
            return await base.SendAsync(request, cancellationToken);
        }
    }

    // Reads each body as the network stack does, without keeping it, and answers with the statuses in turn.
    private sealed class Draining(params HttpStatusCode[] answers) : HttpMessageHandler
    {
        private int _calls;

        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var body = new MemoryStream();
            await request.Content!.CopyToAsync(body, cancellationToken);
            Bodies.Add(Encoding.UTF8.GetString(body.ToArray()));
            return new HttpResponseMessage(answers[Math.Min(_calls++, answers.Length - 1)]) { Content = new StringContent("{}") };
        }
    }

    // Fails every request with an exception of its own, as a resilience pipeline does when it gives up.
    private sealed class Throwing(Func<Exception> exception) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(exception());
    }
}
