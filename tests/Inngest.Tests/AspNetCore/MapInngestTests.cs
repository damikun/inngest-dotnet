using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Inngest.Client;
using Inngest.Configuration;
using Inngest.Execution;
using Inngest.Internal;
using Inngest.Registration;
using Inngest.Signing;
using Inngest.Tests.Serving;
using Inngest.Tests.Serving.Fakes;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Inngest.Tests.AspNetCore;

public sealed class MapInngestTests
{
    private const string CallPath = "/api/inngest?fnId=shop-send-email&stepId=step";
    private const string CallBody = """{"ctx":{"attempt":0,"run_id":"01J0000000000000000000000"},"event":{"name":"user/signed.up"},"steps":{},"version":2}""";

    private readonly FakeFunctionRegistry _registry = new();
    private readonly FakeExecutionEngine _engine = new();
    private readonly FakeInngestApi _api = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero));

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task GET_answers_the_introspection_body()
    {
        await using var app = await StartAsync(CommHandlerHarness.DevSettings);

        using var response = await app.GetTestClient().GetAsync("/api/inngest", CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync(CancellationToken);
        body.ShouldBe(
            """{"authentication_succeeded":null,"capabilities":{"trust_probe":"v1"},"function_count":2,"has_event_key":false,"has_signing_key":false,"has_signing_key_fallback":false,"mode":"dev","schema_version":"2024-05-24"}""");
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/json");
        response.Content.Headers.ContentLength.ShouldBe(Encoding.UTF8.GetByteCount(body));
        response.Headers.GetValues("X-Inngest-SDK").ShouldBe([SdkInfo.Header]);
        response.Headers.GetValues("X-Inngest-Framework").ShouldBe(["aspnetcore"]);
        response.Headers.GetValues("X-Inngest-Req-Version").ShouldBe(["2"]);
    }

    [Fact]
    public async Task PUT_syncs_the_url_the_request_arrived_on()
    {
        await using var app = await StartAsync(CommHandlerHarness.DevSettings);
        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/inngest?region=eu&deployId=deploy-1");
        request.Headers.Add("X-Inngest-Server-Kind", "dev");

        using var response = await app.GetTestClient().SendAsync(request, CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(CancellationToken)).ShouldBe("""{"message":"Successfully registered","modified":true}""");
        response.Headers.GetValues("X-Inngest-Sync-Kind").ShouldBe(["out_of_band"]);
        var serveUrl = new Uri("http://localhost/api/inngest?region=eu");
        _registry.Registrations.ShouldBe([(serveUrl, "aspnetcore")]);
        _api.Registrations.ShouldBe([new FakeInngestApi.Registration(FakeFunctionRegistry.RegistrationJson(serveUrl, "aspnetcore"), "deploy-1", "dev")]);
    }

    [Fact]
    public async Task PUT_in_band_answers_with_the_configuration()
    {
        await using var app = await StartAsync(CommHandlerHarness.DevSettings);
        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/inngest")
        {
            Content = new StringContent("""{"url":"https://shop.example.com/api/inngest"}"""),
        };
        request.Headers.Add("X-Inngest-Sync-Kind", "in_band");

        using var response = await app.GetTestClient().SendAsync(request, CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.GetValues("X-Inngest-Sync-Kind").ShouldBe(["in_band"]);
        (await response.Content.ReadAsStringAsync(CancellationToken)).ShouldEndWith("\"url\":\"https://shop.example.com/api/inngest\"}");
        _api.Registrations.ShouldBeEmpty();
    }

    [Fact]
    public async Task POST_hands_the_raw_body_and_step_to_the_engine_and_returns_its_result()
    {
        const string Operations = """[{"id":"e7d8a2f140845095749d60246ff1110c9d01d76a","op":"StepError"}]""";
        _engine.Handler = (_, _) => Task.FromResult(FakeExecutionEngine.Result(206, Operations, noRetry: false, retryAfter: "30"));
        await using var app = await StartAsync(CommHandlerHarness.DevSettings);

        using var response = await app.GetTestClient().PostAsync(CallPath, new StringContent(CallBody), CancellationToken);

        var call = _engine.Calls.ShouldHaveSingleItem();
        call.Function.ShouldBeSameAs(_registry.Definitions[0]);
        call.BodyText.ShouldBe(CallBody);
        call.StepId.ShouldBe("step");
        response.StatusCode.ShouldBe(HttpStatusCode.PartialContent);
        (await response.Content.ReadAsStringAsync(CancellationToken)).ShouldBe(Operations);
        response.Content.Headers.ContentLength.ShouldBe(Encoding.UTF8.GetByteCount(Operations));
        response.Headers.GetValues("X-Inngest-No-Retry").ShouldBe(["false"]);
        response.Headers.GetValues("Retry-After").ShouldBe(["30"]);
    }

    [Theory]
    [InlineData(5 * 1024 * 1024, true)]
    [InlineData(5 * 1024 * 1024, false)]
    [InlineData(64 * 1024, false)]
    public async Task Large_bodies_arrive_intact(int size, bool declareLength)
    {
        var payload = new byte[size];
        new Random(size).NextBytes(payload);
        await using var app = await StartAsync(CommHandlerHarness.DevSettings);
        using var content = declareLength
            ? (HttpContent)new ByteArrayContent(payload)
            : new StreamContent(new UnseekableStream(payload));

        using var response = await app.GetTestClient().PostAsync(CallPath, content, CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var received = _engine.Calls.ShouldHaveSingleItem().Body;
        received.Length.ShouldBe(size);
        SHA256.HashData(received).ShouldBe(SHA256.HashData(payload));
    }

    [Fact]
    public async Task Functions_run_in_the_request_scope()
    {
        string? marker = null;
        _engine.Handler = (request, _) =>
        {
            marker = request.Services.GetRequiredService<RequestMarker>().Value;
            return Task.FromResult(FakeExecutionEngine.Result(200, "null"));
        };
        await using var app = await StartAsync(CommHandlerHarness.DevSettings, app => app.Use(async (context, next) =>
        {
            context.RequestServices.GetRequiredService<RequestMarker>().Value = "set by middleware";
            await next(context);
        }));

        using var response = await app.GetTestClient().PostAsync(CallPath, new StringContent(CallBody), CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        marker.ShouldBe("set by middleware");
    }

    [Fact]
    public async Task Aborting_the_request_cancels_the_function()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _engine.Handler = async (_, cancellationToken) =>
        {
            using var registration = cancellationToken.Register(() => cancelled.TrySetResult());
            started.SetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return FakeExecutionEngine.Result(200, "null");
        };
        await using var app = await StartAsync(CommHandlerHarness.DevSettings);
        using var abort = new CancellationTokenSource();

        var send = app.GetTestClient().PostAsync(CallPath, new StringContent(CallBody), abort.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken);
        await abort.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(send);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken);
    }

    [Fact]
    public async Task Cloud_mode_verifies_the_raw_body_and_signs_the_response()
    {
        await using var app = await StartAsync(CommHandlerHarness.CloudSettings);
        var signer = new RequestSigner(_time);
        var body = Encoding.UTF8.GetBytes(CallBody);
        using var request = new HttpRequestMessage(HttpMethod.Post, CallPath) { Content = new ByteArrayContent(body) };
        request.Headers.Add("X-Inngest-Signature", signer.Sign(body, CommHandlerHarness.PrimaryKey));

        using var response = await app.GetTestClient().SendAsync(request, CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var responseBody = await response.Content.ReadAsByteArrayAsync(CancellationToken);
        var signature = response.Headers.GetValues("X-Inngest-Signature").ShouldHaveSingleItem();
        signer.Verify(signature, responseBody, CommHandlerHarness.PrimaryKey, fallbackKey: null).IsValid.ShouldBeTrue();
    }

    [Fact]
    public async Task Cloud_mode_rejects_an_unsigned_call()
    {
        await using var app = await StartAsync(CommHandlerHarness.CloudSettings);

        using var response = await app.GetTestClient().PostAsync(CallPath, new StringContent(CallBody), CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await response.Content.ReadAsStringAsync(CancellationToken)).ShouldBe("""{"message":"Unauthorized"}""");
        response.Headers.Contains("X-Inngest-SDK").ShouldBeFalse();
        _engine.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_empty_response_declares_a_zero_length()
    {
        await using var app = await StartAsync(CommHandlerHarness.DevSettings);

        using var response = await app.GetTestClient().PostAsync("/api/inngest?probe=trust", new StringContent("{}"), CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentLength.ShouldBe(0);
        (await response.Content.ReadAsByteArrayAsync(CancellationToken)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Only_GET_PUT_and_POST_are_mapped()
    {
        await using var app = await StartAsync(CommHandlerHarness.DevSettings);

        using var response = await app.GetTestClient().DeleteAsync("/api/inngest", CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.MethodNotAllowed);
        _engine.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_endpoint_can_be_mapped_on_another_path()
    {
        await using var app = await StartAsync(CommHandlerHarness.DevSettings, pattern: "/inngest");
        var client = app.GetTestClient();

        using var mapped = await client.GetAsync("/inngest", CancellationToken);
        using var unmapped = await client.GetAsync("/api/inngest", CancellationToken);

        mapped.StatusCode.ShouldBe(HttpStatusCode.OK);
        unmapped.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task MapInngest_requires_AddInngest()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        await using var app = builder.Build();

        Should.Throw<InvalidOperationException>(() => app.MapInngest())
            .Message.ShouldBe("Call services.AddInngest() before app.MapInngest().");
    }

    [Fact]
    public async Task Inngest_gets_past_an_authorization_policy_that_covers_every_endpoint()
    {
        await using var app = await StartAsync(
            CommHandlerHarness.DevSettings,
            configure: app =>
            {
                app.UseAuthorization();
                app.MapGet("/orders", () => "orders");
            },
            services: services =>
            {
                services.AddAuthentication(Nobody.Name).AddScheme<AuthenticationSchemeOptions, Nobody>(Nobody.Name, null);
                services.AddAuthorization(options => options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
            });
        var client = app.GetTestClient();

        using var inngest = await client.GetAsync("/api/inngest", CancellationToken);
        using var orders = await client.GetAsync("/orders", CancellationToken);

        inngest.StatusCode.ShouldBe(HttpStatusCode.OK);
        orders.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private async Task<WebApplication> StartAsync(
        InngestSettings settings,
        Action<WebApplication>? configure = null,
        string pattern = "/api/inngest",
        Action<IServiceCollection>? services = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        services?.Invoke(builder.Services);
        builder.Services.AddInngest(options => options.AppId = settings.AppId);
        builder.Services.Replace(ServiceDescriptor.Singleton(settings));
        builder.Services.Replace(ServiceDescriptor.Singleton<TimeProvider>(_time));
        builder.Services.Replace(ServiceDescriptor.Singleton<IFunctionRegistry>(_registry));
        builder.Services.Replace(ServiceDescriptor.Singleton<IExecutionEngine>(_engine));
        builder.Services.Replace(ServiceDescriptor.Singleton<IInngestApi>(_api));
        builder.Services.AddScoped<RequestMarker>();

        var app = builder.Build();
        configure?.Invoke(app);
        app.MapInngest(pattern);
        await app.StartAsync(CancellationToken);
        return app;
    }

    private sealed class RequestMarker
    {
        public string? Value { get; set; }
    }

    // Authenticates nobody, so a policy that requires a user answers 401.
    private sealed class Nobody(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string Name = "Nobody";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.NoResult());
    }

    // Has no length, so the client sends the body chunked, as a proxy might.
    private sealed class UnseekableStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
    }
}
