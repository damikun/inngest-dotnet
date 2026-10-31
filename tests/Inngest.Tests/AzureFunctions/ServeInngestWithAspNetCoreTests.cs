using System.Net;
using System.Text;
using System.Text.Json;
using Inngest.Configuration;
using Inngest.Internal;
using Inngest.Signing;
using Inngest.Tests.AzureFunctions.Fakes;
using Inngest.Tests.Serving;
using Inngest.Tests.Serving.Fakes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;

namespace Inngest.Tests.AzureFunctions;

// The ASP.NET Core integration of the isolated worker: the function runs inside a request of Kestrel, whose HttpContext the
// integration puts in the items of the FunctionContext. The tests stand in for the integration, which is what hands the
// function a request, its context and the per-invocation services, and run the adapter in a TestServer.
public sealed class ServeInngestWithAspNetCoreTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_call_is_answered_on_the_response_of_the_request_with_the_framework_of_Azure_Functions()
    {
        const string Operations = """[{"id":"e7d8a2f140845095749d60246ff1110c9d01d76a","op":"StepError"}]""";
        var fakes = new FunctionsFakes();
        fakes.Engine.Handler = (_, _) => Task.FromResult(FakeExecutionEngine.Result(206, Operations, noRetry: false, retryAfter: "30"));
        await using var host = await StartAsync(fakes, CommHandlerHarness.DevSettings);

        using var response = await host.App.GetTestClient().PostAsync("/api/inngest?fnId=shop-send-email&stepId=step", new StringContent(FunctionsHarness.CallBody), CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.PartialContent);
        (await response.Content.ReadAsStringAsync(CancellationToken)).ShouldBe(Operations);
        response.Content.Headers.ContentLength.ShouldBe(Encoding.UTF8.GetByteCount(Operations));
        response.Headers.GetValues("X-Inngest-Framework").ShouldBe(["azure-functions"]);
        response.Headers.GetValues("X-Inngest-No-Retry").ShouldBe(["false"]);
        response.Headers.GetValues("Retry-After").ShouldBe(["30"]);
        var call = fakes.Engine.Calls.ShouldHaveSingleItem();
        call.BodyText.ShouldBe(FunctionsHarness.CallBody);
        call.StepId.ShouldBe("step");
    }

    [Fact]
    public async Task The_function_gets_a_response_of_the_request_that_the_integration_has_nothing_to_add_to()
    {
        var fakes = new FunctionsFakes();
        await using var host = await StartAsync(fakes, CommHandlerHarness.DevSettings);

        using var response = await host.App.GetTestClient().GetAsync("/api/inngest", CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var returned = host.Returned.ShouldHaveSingleItem();
        returned.ShouldBeSameAs(host.Requests.ShouldHaveSingleItem().Response);

        // Nothing was written to it; the answer went to the response of the request directly.
        var fake = returned.ShouldBeOfType<FakeHttpResponseData>();
        fake.BodyBytes.ShouldBeEmpty();
        fake.Headers.ShouldBeEmpty();
    }

    [Fact]
    public async Task PUT_syncs_the_url_of_the_request_with_the_framework_of_Azure_Functions()
    {
        var fakes = new FunctionsFakes();
        await using var host = await StartAsync(fakes, CommHandlerHarness.DevSettings);
        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/inngest?deployId=deploy-1");
        request.Headers.Add("X-Inngest-Server-Kind", "dev");

        using var response = await host.App.GetTestClient().SendAsync(request, CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        fakes.Registry.Registrations.ShouldBe([(new Uri("http://localhost/api/inngest"), "azure-functions")]);
    }

    [Fact]
    public async Task A_call_still_running_after_three_seconds_is_streamed_the_way_Inngest_reads_a_201()
    {
        const string Operations = """[{"id":"e7d8a2f140845095749d60246ff1110c9d01d76a","op":"StepError"}]""";
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fakes = new FunctionsFakes();
        fakes.Engine.Handler = async (_, _) =>
        {
            await release.Task;
            return FakeExecutionEngine.Result(206, Operations, noRetry: false, retryAfter: "30");
        };
        await using var host = await StartAsync(fakes, CommHandlerHarness.DevSettings with { Streaming = true });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/inngest?fnId=shop-send-email&stepId=step") { Content = new StringContent(FunctionsHarness.CallBody) };

        var sending = host.App.GetTestClient().SendAsync(request, HttpCompletionOption.ResponseHeadersRead, CancellationToken);
        await fakes.Time.TimersCreated(1).WaitAsync(TimeSpan.FromSeconds(10), CancellationToken);
        fakes.Time.Advance(TimeSpan.FromSeconds(3));
        using var response = await sending.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken);
        await fakes.Time.TimersCreated(2).WaitAsync(TimeSpan.FromSeconds(10), CancellationToken);
        fakes.Time.Advance(TimeSpan.FromSeconds(3));
        await fakes.Time.TimersCreated(3).WaitAsync(TimeSpan.FromSeconds(10), CancellationToken);
        release.SetResult();
        var text = await response.Content.ReadAsStringAsync(CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        response.Headers.GetValues("X-Accel-Buffering").ShouldBe(["no"]);
        text.ShouldStartWith("  {");
        using var payload = JsonDocument.Parse(text);
        payload.RootElement.GetProperty("status").GetInt32().ShouldBe(206);
        payload.RootElement.GetProperty("body").GetString().ShouldBe(Operations);
        var headers = payload.RootElement.GetProperty("headers");
        headers.GetProperty("X-Inngest-SDK").GetString().ShouldBe(SdkInfo.Header);
        headers.GetProperty("X-Inngest-Framework").GetString().ShouldBe("azure-functions");
        headers.GetProperty("Retry-After").GetString().ShouldBe("30");
    }

    [Fact]
    public async Task A_streamed_call_whose_caller_hangs_up_keeps_the_function_running_until_Inngests_function_ends()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fakes = new FunctionsFakes();
        fakes.Engine.Handler = async (_, cancellationToken) =>
        {
            using var registration = cancellationToken.Register(() => cancelled.TrySetResult());
            started.SetResult();

            // A step that does not honour its cancellation token.
            await release.Task;
            return FakeExecutionEngine.Result(200, "\"done\"");
        };
        await using var host = await StartAsync(fakes, CommHandlerHarness.DevSettings with { Streaming = true });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/inngest?fnId=shop-send-email&stepId=step") { Content = new StringContent(FunctionsHarness.CallBody) };

        var sending = host.App.GetTestClient().SendAsync(request, HttpCompletionOption.ResponseHeadersRead, CancellationToken);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken);
        await fakes.Time.TimersCreated(1).WaitAsync(TimeSpan.FromSeconds(10), CancellationToken);
        fakes.Time.Advance(TimeSpan.FromSeconds(3));
        var response = await sending.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        // The caller hangs up, which the function learns of through the token of the request.
        response.Dispose();
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken);

        // The function of the app must not return while Inngest's function is running: the worker would end the invocation
        // and dispose its services under it, and the pooled body it reads would go back to the pool.
        var winner = await Task.WhenAny(host.Ended.Task, Task.Delay(TimeSpan.FromMilliseconds(500), CancellationToken));
        winner.ShouldNotBeSameAs(host.Ended.Task, "the function returned while Inngest's function was still running");
        release.SetResult();
        (await host.Ended.Task.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken)).ShouldBeAssignableTo<OperationCanceledException>();
        fakes.Engine.LastResult.ShouldNotBeNull().Body.WrittenCount.ShouldBe(0);
    }

    [Fact]
    public async Task A_call_that_ends_within_three_seconds_is_answered_as_usual_with_streaming_on()
    {
        var fakes = new FunctionsFakes();
        await using var host = await StartAsync(fakes, CommHandlerHarness.DevSettings with { Streaming = true });

        using var response = await host.App.GetTestClient().PostAsync("/api/inngest?fnId=shop-send-email&stepId=step", new StringContent(FunctionsHarness.CallBody), CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(CancellationToken)).ShouldBe("\"done\"");

        // No warning that streaming has no effect: here it has one.
        host.Logs.GetSnapshot().Where(record => record.Id.Id == 370).ShouldBeEmpty();
    }

    [Fact]
    public async Task Cancelling_the_invocation_cancels_the_call()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fakes = new FunctionsFakes();
        fakes.Engine.Handler = async (_, cancellationToken) =>
        {
            using var registration = cancellationToken.Register(() => cancelled.TrySetResult());
            started.SetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return FakeExecutionEngine.Result(200, "null");
        };
        using var invocation = new CancellationTokenSource();
        await using var host = await StartAsync(fakes, CommHandlerHarness.DevSettings, invocation.Token);

        var sending = host.App.GetTestClient().PostAsync("/api/inngest?fnId=shop-send-email&stepId=step", new StringContent(FunctionsHarness.CallBody), CancellationToken);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken);
        await invocation.CancelAsync();

        // The token of the invocation is what the host cancels at a function timeout or when it shuts down. The function
        // ends with the cancellation, which the worker reports as a cancelled invocation.
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken);
        await Should.ThrowAsync<OperationCanceledException>(sending);
    }

    [Fact]
    public async Task Aborting_the_request_cancels_the_call()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fakes = new FunctionsFakes();
        fakes.Engine.Handler = async (_, cancellationToken) =>
        {
            using var registration = cancellationToken.Register(() => cancelled.TrySetResult());
            started.SetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return FakeExecutionEngine.Result(200, "null");
        };
        await using var host = await StartAsync(fakes, CommHandlerHarness.DevSettings);
        using var abort = new CancellationTokenSource();

        var sending = host.App.GetTestClient().PostAsync("/api/inngest?fnId=shop-send-email&stepId=step", new StringContent(FunctionsHarness.CallBody), abort.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken);
        await abort.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(sending);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken);
    }

    [Fact]
    public async Task Functions_run_in_the_services_of_the_invocation()
    {
        var fakes = new FunctionsFakes();
        string? marker = null;
        fakes.Engine.Handler = (request, _) =>
        {
            marker = request.Services.GetService(typeof(InvocationMarker)) is InvocationMarker found ? found.Value : null;
            return Task.FromResult(FakeExecutionEngine.Result(200, "null"));
        };
        await using var host = await StartAsync(fakes, CommHandlerHarness.DevSettings, configure: functionContext =>
            ((InvocationMarker)functionContext.InstanceServices.GetService(typeof(InvocationMarker))!).Value = "set by the integration");

        using var response = await host.App.GetTestClient().PostAsync("/api/inngest?fnId=shop-send-email&stepId=step", new StringContent(FunctionsHarness.CallBody), CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        marker.ShouldBe("set by the integration");
    }

    [Fact]
    public async Task Cloud_mode_verifies_the_signature_and_signs_the_response()
    {
        var fakes = new FunctionsFakes();
        await using var host = await StartAsync(fakes, CommHandlerHarness.CloudSettings);
        var signer = new RequestSigner(fakes.Time);
        var body = Encoding.UTF8.GetBytes(FunctionsHarness.CallBody);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/inngest?fnId=shop-send-email&stepId=step") { Content = new ByteArrayContent(body) };
        request.Headers.Add("X-Inngest-Signature", signer.Sign(body, CommHandlerHarness.PrimaryKey));

        using var response = await host.App.GetTestClient().SendAsync(request, CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var responseBody = await response.Content.ReadAsByteArrayAsync(CancellationToken);
        var signature = response.Headers.GetValues("X-Inngest-Signature").ShouldHaveSingleItem();
        signer.Verify(signature, responseBody, CommHandlerHarness.PrimaryKey, fallbackKey: null).IsValid.ShouldBeTrue();
    }

    [Fact]
    public async Task An_entry_that_is_not_an_HttpContext_is_not_the_integration()
    {
        var fakes = new FunctionsFakes();
        await using var host = await StartAsync(fakes, CommHandlerHarness.DevSettings, configure: functionContext =>
            functionContext.Items["HttpRequestContext"] = "something else");

        using var response = await host.App.GetTestClient().GetAsync("/api/inngest", CancellationToken);

        // The call is answered the way the built-in model does it: on a response of the function, not on the request's.
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsByteArrayAsync(CancellationToken)).ShouldBeEmpty();
        var returned = host.Returned.ShouldHaveSingleItem().ShouldBeOfType<FakeHttpResponseData>();
        returned.BodyText.ShouldContain("\"mode\":\"dev\"");
    }

    private async Task<AspNetCoreHost> StartAsync(
        FunctionsFakes fakes,
        InngestSettings settings,
        CancellationToken invocationToken = default,
        Action<FakeFunctionContext>? configure = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        fakes.AddTo(builder.Services, settings);
        var host = new AspNetCoreHost();

        // What the integration does for a function: it keeps the HttpContext of the request in the items of the context,
        // whose services are those of the request, and calls the function.
        var app = builder.Build();
        app.Run(async context =>
        {
            var functionContext = new FakeFunctionContext(context.RequestServices, invocationToken);
            functionContext.Items["HttpRequestContext"] = context;
            configure?.Invoke(functionContext);
            var request = new FakeHttpRequestData(functionContext, context.Request.Method, context.Request.GetEncodedUrl(), context.Request.Body);
            host.Requests.Add(request);
            try
            {
                host.Returned.Add(await request.ServeInngestAsync());
                host.Ended.TrySetResult(null);
            }
            catch (Exception exception)
            {
                host.Ended.TrySetResult(exception);
                throw;
            }
        });
        await app.StartAsync(CancellationToken);
        host.Start(app);
        return host;
    }

    private sealed class AspNetCoreHost : IAsyncDisposable
    {
        public WebApplication App { get; private set; } = null!;

        public List<FakeHttpRequestData> Requests { get; } = [];

        public List<HttpResponseData> Returned { get; } = [];

        // Completes when the function returns, or throws, with the exception it throws.
        public TaskCompletionSource<Exception?> Ended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public FakeLogCollector Logs { get; private set; } = null!;

        public void Start(WebApplication app)
        {
            App = app;
            Logs = app.Services.GetFakeLogCollector();
        }

        public ValueTask DisposeAsync() => App.DisposeAsync();
    }
}
