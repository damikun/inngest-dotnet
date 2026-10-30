using System.Net;
using System.Text;
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
        result.Json.GetProperty("message").GetString().ShouldBe("The stored result of fetch step \"get-user\" has no \"status_code\".");
    }

    private Task<CallResult> Report(Func<IStepTools, Task> step) =>
        _host.CallAsync(
            TestFunction.Define(async context =>
            {
                await step(context.Step);
                return null;
            }),
            Call.New());
}
