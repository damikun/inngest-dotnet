using System.Net;
using System.Text;
using Inngest.Functions;
using Inngest.Tests.Execution;

namespace Inngest.Tests.Steps;

public sealed class FetchStepTests : IDisposable
{
    private const string Memo =
        """{"body":"{\"id\":1,\"name\":\"Ada\"}","headers":{"Content-Length":"999","Content-Type":"application/json; charset=utf-8","Date":"Thu, 29 Oct 2026 10:00:00 GMT","X-Request-Id":"req-1"},"status_code":201,"url":"https://api.example.com/users?page=2"}""";

    private readonly EngineHost _host = new();

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task Fetch_sends_the_request_for_inngest_to_make()
    {
        var result = await Report(step =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "https://api.example.com/users?page=2")
            {
                Content = new StringContent("""{"name":"Ada"}""", Encoding.UTF8, "application/json"),
            };
            request.Headers.Add("Accept", "application/json");
            request.Headers.Add("X-Trace", ["a", "b"]);
            return step.FetchAsync("create-user", request);
        });

        result.Body.ShouldBe(
            $$$"""[{"id":"{{{Call.Hash("create-user")}}}","op":"Gateway","displayName":"create-user","opts":{"url":"https://api.example.com/users?page=2","method":"POST","headers":{"Accept":"application/json","X-Trace":"a, b","Content-Type":"application/json; charset=utf-8"},"body":"{\u0022name\u0022:\u0022Ada\u0022}"},"userland":{"id":"create-user"}}]""");
    }

    [Fact]
    public async Task A_request_without_content_sends_no_body()
    {
        var result = await Report(step => step.FetchAsync("get-user", new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/users/1")));

        result.Op().GetProperty("opts").GetRawText().ShouldBe("""{"url":"https://api.example.com/users/1","method":"GET","headers":{}}""");
    }

    [Fact]
    public async Task Fetch_needs_an_absolute_uri()
    {
        Exception? error = null;
        var function = TestFunction.Define(context =>
        {
            error = Should.Throw<ArgumentException>(() => context.Step.FetchAsync("get-user", new HttpRequestMessage(HttpMethod.Get, "/users/1")));
            return Task.FromResult<object?>(null);
        });

        await _host.CallAsync(function, Call.New());

        error!.Message.ShouldStartWith("The request of fetch step \"get-user\" needs an absolute URI.");
    }

    [Fact]
    public async Task The_memoized_response_becomes_an_http_response()
    {
        HttpRequestMessage? sent = null;
        HttpResponseMessage? received = null;
        var function = TestFunction.Define(async context =>
        {
            sent = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/users?page=2");
            received = await context.Step.FetchAsync("get-users", sent);
            return await received.Content.ReadAsStringAsync();
        });

        var result = await _host.CallAsync(function, Call.New().Data("get-users", Memo));

        result.Json.GetString().ShouldBe("""{"id":1,"name":"Ada"}""");
        received!.StatusCode.ShouldBe(HttpStatusCode.Created);
        received.RequestMessage.ShouldBeSameAs(sent);
        received.Headers.GetValues("X-Request-Id").ShouldBe(["req-1"]);
        received.Headers.Date.ShouldBe(new DateTimeOffset(2026, 10, 29, 10, 0, 0, TimeSpan.Zero));
        received.Content.Headers.ContentType!.ToString().ShouldBe("application/json; charset=utf-8");

        // The length of the text Inngest kept, not the one the server sent.
        received.Content.Headers.ContentLength.ShouldBe(21);
    }

    [Fact]
    public async Task A_status_that_is_not_success_is_a_response_like_any_other()
    {
        var function = TestFunction.Define(async context =>
        {
            using var response = await context.Step.FetchAsync("get-user", new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/users/1"));
            return (int)response.StatusCode;
        });

        var result = await _host.CallAsync(function, Call.New().Data("get-user", """{"body":"","headers":{},"status_code":503,"url":"https://api.example.com/users/1"}"""));

        result.Body.ShouldBe("503");
    }

    [Fact]
    public async Task A_request_inngest_could_not_make_fails_the_step()
    {
        var function = TestFunction.Define(async context =>
        {
            try
            {
                await context.Step.FetchAsync("get-user", new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/users/1"));
                return "fetched";
            }
            catch (StepFailedException exception)
            {
                return $"{exception.Error.Name}: {exception.Error.Message}";
            }
        });

        var result = await _host.CallAsync(function, Call.New().Failed("get-user", "GatewayError", "Error making gateway request: connection refused"));

        result.Body.ShouldBe("\"GatewayError: Error making gateway request: connection refused\"");
    }

    [Fact]
    public async Task A_memo_without_a_status_fails_the_call()
    {
        var function = TestFunction.Define(async context =>
            await context.Step.FetchAsync("get-user", new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/users/1")));

        var result = await _host.CallAsync(function, Call.New().Data("get-user", """{"body":"x"}"""));

        result.StatusCode.ShouldBe(500);
        result.Json.GetProperty("message").GetString().ShouldBe(
            "The result of step \"get-user\" could not be read as HttpResponseMessage: The stored result has no \"status_code\".");
    }

    [Fact]
    public async Task A_body_read_later_is_reported_with_the_steps_found_with_it()
    {
        var result = await Report(step => Task.WhenAll(
            step.RunAsync("load", _ => Task.FromResult(1)),
            step.FetchAsync("create-user", Post("https://api.example.com/users", Later("""{"name":"Ada"}""", Task.Delay(50))))));

        result.Ops.Select(op => op.GetProperty("op").GetString()).ShouldBe(["StepPlanned", "Gateway"]);
        result.Op(1).GetProperty("opts").GetProperty("body").GetString().ShouldBe("""{"name":"Ada"}""");
    }

    [Fact]
    public async Task Repeated_ids_follow_the_order_of_the_calls_whichever_body_is_read_first()
    {
        var discovery = await _host.CallAsync(TwoCalls(firstDelay: 200, secondDelay: 5, _ => { }), Call.New());

        discovery.Ops.Select(op => (op.GetProperty("id").GetString(), op.GetProperty("opts").GetProperty("url").GetString())).ShouldBe(
            [(Call.Hash("call"), "https://api.example.com/first"), (Call.Hash("call:1"), "https://api.example.com/second")],
            ignoreOrder: true);

        // The next call reads the bodies the other way round, and each fetch still gets the response made for it.
        string? first = null;
        var replay = await _host.CallAsync(
            TwoCalls(firstDelay: 5, secondDelay: 200, body => first = body),
            Call.New()
                .Data("call", """{"body":"to first","headers":{},"status_code":200,"url":"https://api.example.com/first"}""")
                .Data("call:1", """{"body":"to second","headers":{},"status_code":200,"url":"https://api.example.com/second"}"""));

        replay.StatusCode.ShouldBe(200);
        first.ShouldBe("to first");
    }

    [Fact]
    public async Task A_memoized_fetch_does_not_wait_for_its_body()
    {
        var never = new TaskCompletionSource();
        var function = TestFunction.Define(async context =>
        {
            using var response = await context.Step.FetchAsync("get-users", Post("https://api.example.com/users?page=2", Later("{}", never.Task)));
            return await response.Content.ReadAsStringAsync();
        });

        var result = await _host.CallAsync(function, Call.New().Data("get-users", Memo));

        result.Json.GetString().ShouldBe("""{"id":1,"name":"Ada"}""");
        never.SetResult();
    }

    [Fact]
    public async Task A_body_that_cannot_be_read_fails_the_fetch_and_is_not_reported()
    {
        var function = TestFunction.Define(async context =>
        {
            try
            {
                await context.Step.FetchAsync("upload", Post("https://api.example.com/upload", Later("x", FailLaterAsync())));
                return "fetched";
            }
            catch (HttpRequestException exception)
            {
                return exception.InnerException!.Message;
            }
        });

        var result = await _host.CallAsync(function, Call.New());

        result.StatusCode.ShouldBe(200);
        result.Json.GetString().ShouldBe("The connection was reset.");
    }

    [Fact]
    public async Task A_fetch_the_function_did_not_wait_for_is_reported_once_its_body_is_read()
    {
        var function = TestFunction.Define(context =>
        {
            _ = context.Step.FetchAsync("notify", Post("https://api.example.com/notify", Later("hello", Task.Delay(50))));
            return Task.FromResult<object?>("done");
        });

        var result = await _host.CallAsync(function, Call.New());

        result.StatusCode.ShouldBe(206);
        result.Op().GetProperty("op").GetString().ShouldBe("Gateway");
        result.Op().GetProperty("opts").GetProperty("body").GetString().ShouldBe("hello");
    }

    private static FunctionDefinition TwoCalls(int firstDelay, int secondDelay, Action<string> onFirst) =>
        TestFunction.Define(async context =>
        {
            var first = context.Step.FetchAsync("call", Post("https://api.example.com/first", Later("first", Task.Delay(firstDelay))));
            var second = context.Step.FetchAsync("call", Post("https://api.example.com/second", Later("second", Task.Delay(secondDelay))));
            using (var response = await first)
            {
                onFirst(await response.Content.ReadAsStringAsync());
            }

            (await second).Dispose();
            return null;
        });

    private static HttpRequestMessage Post(string url, HttpContent content) => new(HttpMethod.Post, url) { Content = content };

    // Content that is not buffered, as from a file or the network: its read completes once the task does.
    private static StreamContent Later(string body, Task ready) => new(new LateStream(Encoding.UTF8.GetBytes(body), ready));

    private static async Task FailLaterAsync()
    {
        await Task.Delay(20);
        throw new IOException("The connection was reset.");
    }

    private Task<CallResult> Report(Func<IStepTools, Task> step) =>
        _host.CallAsync(
            TestFunction.Define(async context =>
            {
                await step(context.Step);
                return null;
            }),
            Call.New());

    private sealed class LateStream(byte[] data, Task ready) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await ready.WaitAsync(cancellationToken).ConfigureAwait(false);
            var count = Math.Min(buffer.Length, data.Length - _position);
            data.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
