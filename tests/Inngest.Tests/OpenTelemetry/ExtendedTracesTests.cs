using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Inngest.Execution;
using Inngest.Functions;
using Inngest.OpenTelemetry.ExtendedTraces;
using Inngest.Tests.Client;
using Inngest.Tests.Execution;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;

namespace Inngest.Tests.OpenTelemetry;

#pragma warning disable INNGESTEXP002

// The spans of a run go to Inngest's /v1/traces/userland, tagged the way the TypeScript SDK tags them, at the end of
// the call. The call arrives as Inngest sends it: under the host's span for the request, which continues the
// traceparent and tracestate Inngest sent.
public sealed class ExtendedTracesTests
{
    private const string ExecutorSpanId = "b7ad6b7169203331";
    private const string FunctionId = "2b6a2c8e-8752-56c4-bafe-1d61d4780711";
    private const string AppId = "34ee71cf-d26b-5925-a750-eb95b9daf65f";
    private const string TraceRef = "%7B%22tp%22%3A%22%22%7D";
    private const string TraceState = $"inngest@traceref={TraceRef},inngest@fn={FunctionId},inngest@app={AppId}";

    // Go's DeterministicSpanConfig(seed).SpanID, from the TypeScript SDK's tests of the same function.
    [Theory]
    [InlineData("test-seed-for-deterministic-span", "1f9f1fae2c8b51c4")]
    [InlineData("hello", "b5c2db5b06a644c5")]
    [InlineData("00000000-0000-0000-0000-000000000000", "51706347ebf6ef86")]
    [InlineData("86f7e437faa5a7fce15d1ddcb9eaeaea377667b8:0", "b017a66031df8079")]
    [InlineData("", "dd9fc5ea353468a4")]
    [InlineData("hello\0world", "8999f00b74c5d24d")]
    [InlineData("emoji-\U0001F600-test", "1af63e4dd8245a1d")]
    [InlineData("cd59ee9a8137151d1499d3d2eb40ba51aa91e0aa:1", "66ccae63f003f30c")]
    public void The_span_id_of_a_step_is_the_one_the_executor_derives(string seed, string expected) =>
        DeterministicSpanId.Create(seed).ShouldBe(expected);

    [Fact]
    public void A_long_seed_gets_the_executor_s_span_id_as_well() =>
        DeterministicSpanId.Create(new string('a', 10_000)).ShouldBe("955541c801f27c55");

    [Fact]
    public async Task The_spans_of_a_run_go_to_Inngest_tagged_with_the_run_and_the_step()
    {
        await using var host = new Host();
        var function = TestFunction.Define(async context =>
        {
            using (host.Source.StartActivity("outside-steps"))
            {
            }

            return await context.Step.RunAsync("charge", () =>
            {
                using var span = host.Source.StartActivity("charge-card");
                span?.SetTag("card.last4", "4242");
                return 42;
            });
        });
        using (host.Source.StartActivity("not-in-a-run"))
        {
        }

        (await host.CallAsync(function, TraceState)).ShouldBe(206);

        var request = host.Http.Single;
        request.Method.ShouldBe(HttpMethod.Post);
        request.Uri.AbsolutePath.ShouldBe("/v1/traces/userland");
        request.Header("Content-Type").ShouldStartWith("application/json");
        using var json = JsonDocument.Parse(request.Body);
        var resource = Attributes(json.RootElement.GetProperty("resourceSpans")[0].GetProperty("resource"));
        resource.ShouldContainKey("service.name");
        var spans = Spans(json.RootElement);
        spans.Keys.ShouldBe(["inngest.execution", "outside-steps", "inngest.step", "charge-card"], ignoreOrder: true);

        var call = spans["inngest.execution"];
        call.Scope.ShouldBe("inngest");
        call.Span.GetProperty("traceId").GetString().ShouldBe(host.TraceId);
        call.Attributes["inngest.traceref"].ShouldBe(TraceRef);
        call.Attributes["inngest.traceparent"].ShouldBe($"00-{host.TraceId}-{ExecutorSpanId}-01");
        call.Attributes["sdk.run.id"].ShouldBe(host.RunId);
        call.Attributes["sys.function.id"].ShouldBe(FunctionId);
        call.Attributes["sys.app.id"].ShouldBe(AppId);
        call.Attributes["sdk.app.id"].ShouldBe(AppId);
        call.Attributes.ShouldContainKey("host.name");
        call.Attributes.ShouldNotContainKey("inngest.step.id");

        spans["outside-steps"].Span.GetProperty("parentSpanId").GetString().ShouldBe(call.Id);
        spans["outside-steps"].Attributes["inngest.traceref"].ShouldBe(TraceRef);
        spans["outside-steps"].Attributes.ShouldNotContainKey("inngest.step.id");

        var hash = Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes("charge")));
        var step = spans["inngest.step"];
        step.Span.GetProperty("parentSpanId").GetString().ShouldBe(call.Id);
        step.Attributes["inngest.step.id"].ShouldBe("charge");
        step.Attributes["inngest.step.index"].ShouldBe("0");
        step.Attributes["inngest.step.hash"].ShouldBe(hash);
        step.Attributes["inngest.step.attempt"].ShouldBe("0");
        step.Attributes["inngest.step.parentSpanId"].ShouldBe(DeterministicSpanId.Create(hash));

        var charge = spans["charge-card"];
        charge.Span.GetProperty("parentSpanId").GetString().ShouldBe(step.Id);
        charge.Attributes["card.last4"].ShouldBe("4242");
        charge.Attributes["inngest.step.hash"].ShouldBe(hash);
        charge.Attributes["sdk.run.id"].ShouldBe(host.RunId);
        charge.Attributes.ShouldNotContainKey("inngest.step.parentSpanId");
    }

    [Fact]
    public async Task A_call_without_Inngest_s_trace_reference_sends_nothing()
    {
        await using var host = new Host();
        var function = TestFunction.Define(async context => await context.Step.RunAsync("charge", () => 42));

        (await host.CallAsync(function, traceState: null)).ShouldBe(206);

        host.Http.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_refused_export_drops_the_spans_and_says_so_once()
    {
        await using var host = new Host(HttpStatusCode.TooManyRequests);
        var function = TestFunction.Define(async context => await context.Step.RunAsync("charge", () => 42));

        (await host.CallAsync(function, TraceState)).ShouldBe(206);
        (await host.CallAsync(function, TraceState)).ShouldBe(206);

        host.Http.Requests.Count.ShouldBe(2);
        var warnings = host.Logs.GetSnapshot().Where(log => log.Level == LogLevel.Warning).ToList();
        warnings.ShouldHaveSingleItem().Message.ShouldContain("429");
    }

    private static Dictionary<string, (string Scope, string Id, JsonElement Span, Dictionary<string, string?> Attributes)> Spans(JsonElement root)
    {
        var spans = new Dictionary<string, (string, string, JsonElement, Dictionary<string, string?>)>(StringComparer.Ordinal);
        foreach (var scope in root.GetProperty("resourceSpans")[0].GetProperty("scopeSpans").EnumerateArray())
        {
            var name = scope.GetProperty("scope").GetProperty("name").GetString()!;
            foreach (var span in scope.GetProperty("spans").EnumerateArray())
            {
                spans.Add(span.GetProperty("name").GetString()!, (name, span.GetProperty("spanId").GetString()!, span.Clone(), Attributes(span)));
            }
        }

        return spans;
    }

    // Every value as the text it holds: strings and 64-bit integers are strings in OTLP's JSON.
    private static Dictionary<string, string?> Attributes(JsonElement owner)
    {
        var attributes = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var attribute in owner.GetProperty("attributes").EnumerateArray())
        {
            var value = attribute.GetProperty("value").EnumerateObject().Single().Value;
            attributes[attribute.GetProperty("key").GetString()!] = value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
        }

        return attributes;
    }

    private sealed class Host : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;

        public Host(HttpStatusCode answer = HttpStatusCode.OK)
        {
            Http = new FakeHttpHandler().RespondAlways(answer, "{}");
            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
            services.AddFakeLogging();
            services.AddInngest(options =>
            {
                options.AppId = "shop";
                options.IsDev = true;
            }).AddExtendedTraces();
            services.AddHttpClient(InngestServiceCollectionExtensions.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => Http);
            _provider = services.BuildServiceProvider();
            Logs = _provider.GetFakeLogCollector();
        }

        public FakeHttpHandler Http { get; }

        public FakeLogCollector Logs { get; }

        // Stands in for the app's own instrumentation, such as an HTTP client.
        public ActivitySource Source { get; } = new($"Inngest.Tests.{Guid.NewGuid():N}");

        public string RunId { get; } = $"01JRUN{Guid.NewGuid():N}";

        // A trace of its own, so that the listeners of other tests leave its spans alone.
        public string TraceId { get; } = ActivityTraceId.CreateRandom().ToHexString();

        // Calls the function under a span for the request that continues Inngest's trace, as ASP.NET Core starts one.
        public async Task<int> CallAsync(FunctionDefinition function, string? traceState)
        {
            using var request = new Activity("POST /api/inngest");
            request.SetParentId(ActivityTraceId.CreateFromString(TraceId), ActivitySpanId.CreateFromString(ExecutorSpanId), ActivityTraceFlags.Recorded);
            request.TraceStateString = traceState;
            request.Start();

            var body = Encoding.UTF8.GetString(Call.New().ToBytes()).Replace("\"run_id\":\"01JRUN\"", $"\"run_id\":\"{RunId}\"", StringComparison.Ordinal);
            var scope = _provider.CreateAsyncScope();
            await using (scope)
            {
                using var result = await _provider.GetRequiredService<IExecutionEngine>().ExecuteAsync(
                    new ExecutionRequest
                    {
                        Function = function,
                        Body = Encoding.UTF8.GetBytes(body),
                        StepId = "step",
                        Services = scope.ServiceProvider,
                    },
                    TestContext.Current.CancellationToken);
                return result.StatusCode;
            }
        }

        public async ValueTask DisposeAsync()
        {
            Source.Dispose();
            await _provider.DisposeAsync();
        }
    }
}
