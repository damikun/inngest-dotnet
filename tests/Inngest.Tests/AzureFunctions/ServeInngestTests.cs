using System.Net;
using System.Security.Cryptography;
using System.Text;
using Inngest.Internal;
using Inngest.Signing;
using Inngest.Tests.AzureFunctions.Fakes;
using Inngest.Tests.Serving;
using Inngest.Tests.Serving.Fakes;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Inngest.Tests.AzureFunctions;

// The built-in HTTP model of the isolated worker: the function receives HttpRequestData and returns HttpResponseData, and
// the worker keeps the response until the function returns.
public sealed class ServeInngestTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task GET_answers_the_introspection_body_and_names_Azure_Functions_as_the_framework()
    {
        await using var harness = new FunctionsHarness();

        var response = await harness.SendAsync("GET", FunctionsHarness.EndpointUrl, cancellationToken: CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.BodyText.ShouldBe(
            """{"authentication_succeeded":null,"capabilities":{"trust_probe":"v1"},"function_count":2,"has_event_key":false,"has_signing_key":false,"has_signing_key_fallback":false,"mode":"dev","schema_version":"2024-05-24"}""");
        response.Header("Content-Type").ShouldBe("application/json");
        response.Header("X-Inngest-SDK").ShouldBe(SdkInfo.Header);
        response.Header("X-Inngest-Framework").ShouldBe("azure-functions");
        response.Header("X-Inngest-Req-Version").ShouldBe("2");
    }

    [Fact]
    public async Task PUT_syncs_the_url_the_host_received_with_its_route_prefix_and_query()
    {
        await using var harness = new FunctionsHarness();
        var request = harness.NewRequest("PUT", FunctionsHarness.EndpointUrl + "?region=eu&deployId=deploy-1")
            .WithHeader("X-Inngest-Server-Kind", "dev");

        var response = await FunctionsHarness.ServeAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.BodyText.ShouldBe("""{"message":"Successfully registered","modified":true}""");
        response.Header("X-Inngest-Sync-Kind").ShouldBe("out_of_band");
        var serveUrl = new Uri("https://shop.azurewebsites.net/api/inngest?region=eu");
        harness.Registry.Registrations.ShouldBe([(serveUrl, "azure-functions")]);
        harness.Api.Registrations.ShouldBe([new FakeInngestApi.Registration(FakeFunctionRegistry.RegistrationJson(serveUrl, "azure-functions"), "deploy-1", "dev")]);
    }

    [Fact]
    public async Task PUT_in_band_answers_with_the_configuration_and_the_url_Inngest_called()
    {
        await using var harness = new FunctionsHarness();
        var request = harness.NewRequest("PUT", FunctionsHarness.EndpointUrl, FakeHttpRequestData.BodyOf("""{"url":"https://shop.example.com/api/inngest"}"""))
            .WithHeader("X-Inngest-Sync-Kind", "in_band");

        var response = await FunctionsHarness.ServeAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Header("X-Inngest-Sync-Kind").ShouldBe("in_band");
        response.BodyText.ShouldEndWith("\"url\":\"https://shop.example.com/api/inngest\"}");
        response.BodyText.ShouldContain("\"framework\":\"azure-functions\"");
        harness.Api.Registrations.ShouldBeEmpty();
    }

    [Fact]
    public async Task POST_hands_the_raw_body_and_step_to_the_engine_and_returns_its_result()
    {
        const string Operations = """[{"id":"e7d8a2f140845095749d60246ff1110c9d01d76a","op":"StepError"}]""";
        await using var harness = new FunctionsHarness();
        harness.Engine.Handler = (_, _) => Task.FromResult(FakeExecutionEngine.Result(206, Operations, noRetry: false, retryAfter: "30"));

        var response = await harness.SendAsync("POST", FunctionsHarness.CallUrl, FunctionsHarness.CallBody, CancellationToken);

        var call = harness.Engine.Calls.ShouldHaveSingleItem();
        call.Function.ShouldBeSameAs(harness.Registry.Definitions[0]);
        call.BodyText.ShouldBe(FunctionsHarness.CallBody);
        call.StepId.ShouldBe("step");
        response.StatusCode.ShouldBe(HttpStatusCode.PartialContent);
        response.BodyText.ShouldBe(Operations);
        response.Header("Content-Type").ShouldBe("application/json");
        response.Header("X-Inngest-No-Retry").ShouldBe("false");
        response.Header("Retry-After").ShouldBe("30");
        response.Header("X-Inngest-SDK-Handled").ShouldBe("true");
    }

    [Fact]
    public async Task A_Retry_After_that_is_a_time_goes_out_as_Inngest_wrote_it()
    {
        // HttpHeaders validates Retry-After as a delta or an HTTP date, and an RFC 3339 time is neither.
        await using var harness = new FunctionsHarness();
        harness.Engine.Handler = (_, _) => Task.FromResult(FakeExecutionEngine.Result(500, "{}", noRetry: false, retryAfter: "2026-10-29T12:30:00Z"));

        var response = await harness.SendAsync("POST", FunctionsHarness.CallUrl, FunctionsHarness.CallBody, CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        response.Header("Retry-After").ShouldBe("2026-10-29T12:30:00Z");
    }

    [Fact]
    public async Task Request_headers_are_found_whatever_their_case()
    {
        // The host puts every header name in lower case before the worker sees it.
        await using var harness = new FunctionsHarness();
        var request = harness.NewRequest("POST", FunctionsHarness.CallUrl, FakeHttpRequestData.BodyOf(FunctionsHarness.CallBody))
            .WithHeader("X-Request-Id", "request-1")
            .WithHeader("X-Inngest-Job-Id", "job-1")
            .WithHeader("X-Inngest-Generation-Id", "3")
            .WithHeader("traceparent", "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01")
            .WithHeader("tracestate", "vendor=value");

        var response = await FunctionsHarness.ServeAsync(request);

        var call = harness.Engine.Calls.ShouldHaveSingleItem();
        call.RequestId.ShouldBe("request-1");
        call.JobId.ShouldBe("job-1");
        call.GenerationId.ShouldBe(3);
        response.Header("traceparent").ShouldBe("00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01");
        response.Header("tracestate").ShouldBe("vendor=value");
    }

    [Fact]
    public async Task The_step_can_come_from_a_header_when_the_query_has_none()
    {
        await using var harness = new FunctionsHarness();
        var request = harness.NewRequest("POST", FunctionsHarness.EndpointUrl + "?fnId=shop-send-email", FakeHttpRequestData.BodyOf(FunctionsHarness.CallBody))
            .WithHeader("X-Inngest-Step-ID", "e7d8a2f140845095749d60246ff1110c9d01d76a");

        await FunctionsHarness.ServeAsync(request);

        harness.Engine.Calls.ShouldHaveSingleItem().StepId.ShouldBe("e7d8a2f140845095749d60246ff1110c9d01d76a");
    }

    [Fact]
    public async Task Query_values_are_decoded()
    {
        await using var harness = new FunctionsHarness();

        // "shop%2Dsend%2Demail" is the function id with its hyphens escaped; "%2B" is a plus sign that must stay one.
        await harness.SendAsync("POST", FunctionsHarness.EndpointUrl + "?fnId=shop%2Dsend%2Demail&stepId=a%2Bb", FunctionsHarness.CallBody, CancellationToken);

        var call = harness.Engine.Calls.ShouldHaveSingleItem();
        call.Function.ShouldBeSameAs(harness.Registry.Definitions[0]);
        call.StepId.ShouldBe("a+b");
    }

    [Fact]
    public async Task The_trust_probe_is_answered_with_an_empty_body()
    {
        await using var harness = new FunctionsHarness();

        var response = await harness.SendAsync("POST", FunctionsHarness.EndpointUrl + "?probe=trust", "{}", CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.BodyBytes.ShouldBeEmpty();
        harness.Engine.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_POST_without_a_body_is_refused()
    {
        // A chunked request without a Content-Length reaches the built-in model as an empty body.
        await using var harness = new FunctionsHarness();

        var response = await harness.SendAsync("POST", FunctionsHarness.CallUrl, cancellationToken: CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.BodyText.ShouldBe("""{"message":"Unauthorized"}""");
        harness.Engine.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Other_methods_are_not_allowed()
    {
        await using var harness = new FunctionsHarness();

        var response = await harness.SendAsync("DELETE", FunctionsHarness.EndpointUrl, cancellationToken: CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.MethodNotAllowed);
        harness.Engine.Calls.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(5 * 1024 * 1024, BodyKind.InMemory)]
    [InlineData(5 * 1024 * 1024, BodyKind.Seekable)]
    [InlineData(5 * 1024 * 1024, BodyKind.Unseekable)]
    [InlineData(1, BodyKind.Unseekable)]
    [InlineData(64 * 1024, BodyKind.Seekable)]
    public async Task Large_bodies_arrive_intact(int size, BodyKind kind)
    {
        var payload = new byte[size];
        new Random(size).NextBytes(payload);
        await using var harness = new FunctionsHarness();
        Stream body = kind switch
        {
            BodyKind.InMemory => FakeHttpRequestData.BodyOf(payload),
            BodyKind.Seekable => new SeekableStream(payload),
            _ => new UnseekableStream(payload),
        };

        var response = await FunctionsHarness.ServeAsync(harness.NewRequest("POST", FunctionsHarness.CallUrl, body));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var received = harness.Engine.Calls.ShouldHaveSingleItem().Body;
        received.Length.ShouldBe(size);
        SHA256.HashData(received).ShouldBe(SHA256.HashData(payload));
    }

    [Fact]
    public async Task A_body_that_was_read_partly_is_taken_from_where_the_stream_is()
    {
        // The worker rewinds the stream before it hands it over. If something has read from it, the rest is the body.
        await using var harness = new FunctionsHarness();
        var body = FakeHttpRequestData.BodyOf("skipped" + FunctionsHarness.CallBody);
        body.Position = "skipped".Length;

        await FunctionsHarness.ServeAsync(harness.NewRequest("POST", FunctionsHarness.CallUrl, body));

        harness.Engine.Calls.ShouldHaveSingleItem().BodyText.ShouldBe(FunctionsHarness.CallBody);
    }

    [Fact]
    public async Task A_stream_that_fails_while_it_is_read_fails_the_function_and_returns_its_buffer()
    {
        await using var harness = new FunctionsHarness();
        var request = harness.NewRequest("POST", FunctionsHarness.CallUrl, new FailingStream());

        await Should.ThrowAsync<IOException>(FunctionsHarness.ServeAsync(request));

        harness.Engine.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Functions_run_in_the_scope_of_the_invocation()
    {
        await using var harness = new FunctionsHarness();
        string? marker = null;
        harness.Engine.Handler = (request, _) =>
        {
            marker = request.Services.GetRequiredService<InvocationMarker>().Value;
            return Task.FromResult(FakeExecutionEngine.Result(200, "null"));
        };
        var request = harness.NewRequest("POST", FunctionsHarness.CallUrl, FakeHttpRequestData.BodyOf(FunctionsHarness.CallBody));
        request.FunctionContext.InstanceServices.GetRequiredService<InvocationMarker>().Value = "set during the invocation";

        await FunctionsHarness.ServeAsync(request);

        marker.ShouldBe("set during the invocation");
        harness.Engine.Calls.ShouldHaveSingleItem().Services.ShouldBeSameAs(request.FunctionContext.InstanceServices);
    }

    [Fact]
    public async Task Cancelling_the_invocation_cancels_the_function()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = new FunctionsHarness();
        harness.Engine.Handler = async (_, cancellationToken) =>
        {
            using var registration = cancellationToken.Register(() => cancelled.TrySetResult());
            started.SetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return FakeExecutionEngine.Result(200, "null");
        };
        using var invocation = new CancellationTokenSource();
        var request = harness.NewRequest("POST", FunctionsHarness.CallUrl, FakeHttpRequestData.BodyOf(FunctionsHarness.CallBody), invocation.Token);

        var serving = FunctionsHarness.ServeAsync(request);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken);
        await invocation.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(serving);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken);
    }

    [Fact]
    public async Task The_result_of_the_engine_is_returned_to_the_pool_once_the_response_is_written()
    {
        await using var harness = new FunctionsHarness();

        var response = await harness.SendAsync("POST", FunctionsHarness.CallUrl, FunctionsHarness.CallBody, CancellationToken);

        // The response owns a copy of the body now, and the pooled buffer the engine wrote it in has been disposed.
        response.BodyText.ShouldBe("\"done\"");
        harness.Engine.LastResult.ShouldNotBeNull().Body.WrittenCount.ShouldBe(0);
    }

    [Fact]
    public async Task Without_AddInngest_the_function_fails_and_says_what_is_missing()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var request = new FakeHttpRequestData(new FakeFunctionContext(services), "GET", FunctionsHarness.EndpointUrl);

        var failure = await Should.ThrowAsync<InvalidOperationException>(request.ServeInngestAsync());

        failure.Message.ShouldBe("Call services.AddInngest() before serving Inngest from a function.");
    }

    [Fact]
    public async Task A_request_is_required()
    {
        HttpRequestData? request = null;

        await Should.ThrowAsync<ArgumentNullException>(() => request!.ServeInngestAsync());
    }

    [Fact]
    public async Task Cloud_mode_verifies_the_signature_over_the_raw_body_and_signs_the_response()
    {
        await using var harness = new FunctionsHarness(CommHandlerHarness.CloudSettings);
        var signer = new RequestSigner(harness.Time);
        var body = Encoding.UTF8.GetBytes(FunctionsHarness.CallBody);
        var request = harness.NewRequest("POST", FunctionsHarness.CallUrl, FakeHttpRequestData.BodyOf(body))
            .WithHeader("X-Inngest-Signature", signer.Sign(body, CommHandlerHarness.PrimaryKey));

        var response = await FunctionsHarness.ServeAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var signature = response.Header("X-Inngest-Signature");
        signer.Verify(signature, response.BodyBytes, CommHandlerHarness.PrimaryKey, fallbackKey: null).IsValid.ShouldBeTrue();
    }

    [Fact]
    public async Task Cloud_mode_refuses_an_unsigned_call_and_tells_the_caller_nothing()
    {
        await using var harness = new FunctionsHarness(CommHandlerHarness.CloudSettings);

        var response = await harness.SendAsync("POST", FunctionsHarness.CallUrl, FunctionsHarness.CallBody, CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Header("X-Inngest-SDK").ShouldBeNull();
        harness.Engine.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task With_streaming_on_the_built_in_model_answers_when_the_function_returns_and_warns_once()
    {
        await using var harness = new FunctionsHarness(CommHandlerHarness.DevSettings with { Streaming = true });

        var first = await harness.SendAsync("POST", FunctionsHarness.CallUrl, FunctionsHarness.CallBody, CancellationToken);
        var second = await harness.SendAsync("POST", FunctionsHarness.CallUrl, FunctionsHarness.CallBody, CancellationToken);
        await harness.SendAsync("GET", FunctionsHarness.EndpointUrl, cancellationToken: CancellationToken);

        // A 201 and a space every three seconds would only be buffered by the worker until the function ends.
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        first.BodyText.ShouldBe("\"done\"");
        var warning = harness.Logs.GetSnapshot().Where(record => record.Id.Id == 370).ShouldHaveSingleItem();
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.Category.ShouldBe("Inngest.AzureFunctions");
        warning.Message.ShouldContain("ConfigureFunctionsWebApplication()");
    }

    [Fact]
    public async Task Without_streaming_nothing_is_warned()
    {
        await using var harness = new FunctionsHarness();

        await harness.SendAsync("POST", FunctionsHarness.CallUrl, FunctionsHarness.CallBody, CancellationToken);

        harness.Logs.GetSnapshot().Where(record => record.Id.Id == 370).ShouldBeEmpty();
    }

    public enum BodyKind
    {
        InMemory,
        Seekable,
        Unseekable,
    }

    // Has a length, but is not a MemoryStream, so that its bytes are copied into a pooled buffer.
    private sealed class SeekableStream(byte[] data) : Stream
    {
        private readonly MemoryStream _inner = new(data);

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }
    }

    // Has no length, as a body that arrives in chunks would.
    private sealed class UnseekableStream(byte[] data) : Stream
    {
        private readonly MemoryStream _inner = new(data);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        // Hands out at most 8 KiB at a time.
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, Math.Min(count, 8192));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(buffer[..Math.Min(buffer.Length, 8192)], cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }
    }

    // Delivers some bytes and then fails, as a connection that drops would.
    private sealed class FailingStream : Stream
    {
        private int _reads;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_reads++ > 0)
            {
                throw new IOException("The connection was lost.");
            }

            buffer.Span[..16].Fill((byte)'x');
            return new ValueTask<int>(16);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }
    }
}
