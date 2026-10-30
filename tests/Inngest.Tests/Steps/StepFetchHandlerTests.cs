using System.Net;
using Inngest.Tests.Client;
using Inngest.Tests.Execution;
using Microsoft.Extensions.DependencyInjection;

namespace Inngest.Tests.Steps;

public sealed class StepFetchHandlerTests : IDisposable
{
    private const string Url = "https://api.example.com/users/1?verbose=true";
    private const string StepId = "fetch GET api.example.com/users/1";

    private readonly FakeHttpHandler _network = new();
    private readonly EngineHost _host;

    public StepFetchHandlerTests()
    {
        _host = new EngineHost(AddClient);
    }

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task Outside_a_function_a_request_goes_to_the_network()
    {
        _network.Respond(HttpStatusCode.OK, """{"id":1}""");

        using var response = await Http(_host.Services).GetAsync(Url, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        _network.Single.Uri.ShouldBe(new Uri(Url));
    }

    [Fact]
    public async Task In_a_function_a_request_becomes_a_fetch_step_named_after_its_method_host_and_path()
    {
        var function = TestFunction.Define(async context =>
        {
            using var response = await Http(context.Services).GetAsync(Url);
            return (int)response.StatusCode;
        });

        var result = await _host.CallAsync(function, Call.New());

        result.Body.ShouldBe(
            $$$"""[{"id":"{{{Call.Hash(StepId)}}}","op":"Gateway","displayName":"{{{StepId}}}","opts":{"url":"{{{Url}}}","method":"GET","headers":{}},"userland":{"id":"{{{StepId}}}"}}]""");
        _network.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task On_replay_the_request_gets_the_memoized_response()
    {
        var function = TestFunction.Define(async context =>
        {
            using var response = await Http(context.Services).GetAsync(Url);
            return $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}";
        });

        var result = await _host.CallAsync(
            function,
            Call.New().Data(StepId, """{"body":"Ada","headers":{"Content-Type":"text/plain"},"status_code":200,"url":"https://api.example.com/users/1?verbose=true"}"""));

        result.Body.ShouldBe("\"200 Ada\"");
        _network.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Repeats_of_a_request_get_ids_of_their_own_in_call_order()
    {
        var function = TestFunction.Define(async context =>
        {
            var http = Http(context.Services);
            await Task.WhenAll(http.GetAsync(Url), http.GetAsync(Url), http.PostAsync(Url, null));
            return null;
        });

        var result = await _host.CallAsync(function, Call.New());

        result.Ops.Select(op => op.GetProperty("id").GetString()).ShouldBe(
            [Call.Hash(StepId), Call.Hash(StepId + ":1"), Call.Hash("fetch POST api.example.com/users/1")]);
    }

    [Fact]
    public async Task Code_that_left_the_functions_thread_still_makes_steps()
    {
        var function = TestFunction.Define(async context =>
        {
            var http = Http(context.Services);
            return await Task.Run(async () =>
            {
                await Task.Yield();
                using var response = await http.GetAsync(Url).ConfigureAwait(false);
                return (int)response.StatusCode;
            });
        });

        var result = await _host.CallAsync(function, Call.New());

        result.OpCodes.ShouldBe(["Gateway"]);
        _network.Requests.ShouldBeEmpty();
    }

    // Steps cannot be nested, so inside a step handler the request is part of the step and goes to the network.
    [Fact]
    public async Task Inside_a_step_handler_a_request_goes_to_the_network()
    {
        _network.Respond(HttpStatusCode.Accepted, "{}");
        var function = TestFunction.Define(async context => await context.Step.RunAsync("load", async () =>
        {
            using var response = await Http(context.Services).GetAsync(Url);
            return (int)response.StatusCode;
        }));

        var result = await _host.CallAsync(function, Call.New());

        result.OpCodes.ShouldBe(["StepRun"]);
        result.Op().GetProperty("data").GetInt32().ShouldBe(202);
        _network.Single.Uri.ShouldBe(new Uri(Url));
    }

    [Fact]
    public async Task Observer_hooks_cannot_call_steps_so_their_requests_go_to_the_network()
    {
        var observed = new List<string>();
        using var host = new EngineHost(services =>
        {
            AddClient(services);
            services.AddSingleton<InngestMiddleware>(new CallingObserver(observed));
        });
        _network.Respond(HttpStatusCode.OK, "{}");

        var result = await host.CallAsync(TestFunction.Define(_ => Task.FromResult<object?>("done")), Call.New());

        result.Body.ShouldBe("\"done\"");
        observed.ShouldBe(["OnRunStart 200"]);
    }

    [Fact]
    public async Task The_sdks_own_requests_to_inngest_are_never_steps()
    {
        _network.Respond(HttpStatusCode.OK, "{}");
        var function = TestFunction.Define(async context =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "http://localhost:8288/e/key");
            request.Headers.Add("X-Inngest-SDK", "inngest-dotnet:v1.0.0");
            using var response = await Http(context.Services).SendAsync(request);
            return (int)response.StatusCode;
        });

        var result = await _host.CallAsync(function, Call.New());

        result.Body.ShouldBe("200");
        _network.Single.Uri.ShouldBe(new Uri("http://localhost:8288/e/key"));
    }

    [Fact]
    public async Task Code_the_function_started_makes_ordinary_requests_once_the_call_answered()
    {
        _network.Respond(HttpStatusCode.OK, "{}");
        var release = new TaskCompletionSource();
        Task<HttpStatusCode>? later = null;
        var function = TestFunction.Define(async context =>
        {
            // Like a refresh loop a singleton starts: it goes on after the call, in the call's async flow.
            later = Task.Run(async () =>
            {
                await release.Task;
                using var response = await Http(_host.Services).GetAsync(Url);
                return response.StatusCode;
            });
            await context.Step.SleepAsync("wait", TimeSpan.FromHours(1));
            return null;
        });

        var result = await _host.CallAsync(function, Call.New());
        release.SetResult();

        result.OpCodes.ShouldBe(["Sleep"]);
        (await later!.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).ShouldBe(HttpStatusCode.OK);
        _network.Single.Uri.ShouldBe(new Uri(Url));
    }

    private static HttpClient Http(IServiceProvider services) => services.GetRequiredService<IHttpClientFactory>().CreateClient("api");

    private void AddClient(IServiceCollection services) =>
        services.AddHttpClient("api").AddInngestStepFetch().ConfigurePrimaryHttpMessageHandler(() => _network);

    private sealed class CallingObserver(List<string> observed) : InngestMiddleware
    {
        public override async ValueTask OnRunStartAsync(FunctionContext context)
        {
            using var response = await Http(context.Services).GetAsync(Url);
            observed.Add($"OnRunStart {(int)response.StatusCode}");
        }
    }
}
