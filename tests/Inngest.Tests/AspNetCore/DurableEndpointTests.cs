using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Inngest.Client;
using Inngest.Execution;
using Inngest.Signing;
using Inngest.Tests.Checkpointing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using FakeInngestApi = Inngest.Tests.Execution.FakeInngestApi;

namespace Inngest.Tests.AspNetCore;

// Durable endpoints over HTTP in memory, with the real engine and a fake Inngest API: what the client gets, what the run
// records, and how a call from Inngest resumes the run at the endpoint's own URL.
public sealed class DurableEndpointTests : IAsyncDisposable
{
    private const string Key = "signkey-prod-0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string OrderBody = """{"note":"gift"}""";

    private readonly FakeEndpointCheckpointApi _checkpoints = new();
    private readonly FakeInngestApi _api = new();
    private int _priced;
    private WebApplication? _app;

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_client_gets_the_response_and_the_run_is_recorded_after_it()
    {
        var client = await StartAsync();

        using var response = await client.PostAsync("/orders/42?source=test", Json(OrderBody), CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var order = await response.Content.ReadFromJsonAsync<JsonElement>(CancellationToken);
        order.GetProperty("id").GetString().ShouldBe("42");
        order.GetProperty("total").GetInt32().ShouldBe(42);
        order.GetProperty("note").GetString().ShouldBe("gift");
        var runId = response.Headers.GetValues("X-Run-ID").ShouldHaveSingleItem();

        await _checkpoints.WaitForSavesAsync(1);
        var save = _checkpoints.Saves.ShouldHaveSingleItem();
        save.RunId.ShouldBe(runId);
        save.Retries.ShouldBe(3);
        save.OpNames.ShouldBe(["StepRun", "RunComplete"]);
        var result = save.Ops[1].GetProperty("data");
        result.GetProperty("status").GetInt32().ShouldBe(200);
        result.GetProperty("headers").GetProperty("Content-Type").GetString()!.ShouldStartWith("application/json");
        JsonDocument.Parse(result.GetProperty("body").GetString()!).RootElement.GetProperty("total").GetInt32().ShouldBe(42);

        using var @event = JsonDocument.Parse(save.Event!);
        var data = @event.RootElement.GetProperty("data");
        data.GetProperty("domain").GetString().ShouldBe("http://localhost");
        data.GetProperty("method").GetString().ShouldBe("POST");
        data.GetProperty("path").GetString().ShouldBe("/orders/42");
        data.GetProperty("query_params").GetString().ShouldBe("source=test");
        data.GetProperty("content_type").GetString()!.ShouldStartWith("application/json");
        Encoding.UTF8.GetString(data.GetProperty("body").GetBytesFromBase64()).ShouldBe(OrderBody);
        data.GetProperty("fn").GetString().ShouldBe("POST /orders/{id}");
    }

    [Fact]
    public async Task A_request_that_runs_no_steps_records_no_run()
    {
        var client = await StartAsync();

        using var response = await client.GetAsync("/ping", CancellationToken);

        (await response.Content.ReadAsStringAsync(CancellationToken)).ShouldBe("pong");
        response.Headers.Contains("X-Run-ID").ShouldBeFalse();
        _checkpoints.Saves.ShouldBeEmpty();
    }

    [Fact]
    public async Task Endpoints_that_are_not_durable_run_as_usual()
    {
        var client = await StartAsync();

        using var response = await client.GetAsync("/plain", CancellationToken);

        (await response.Content.ReadAsStringAsync(CancellationToken)).ShouldBe("plain");
        _checkpoints.Saves.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_sleep_redirects_the_client_to_wait_for_the_output()
    {
        var client = await StartAsync();

        using var response = await client.PostAsync("/orders/42/ship", Json(OrderBody), CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.SeeOther);
        var runId = response.Headers.GetValues("X-Run-ID").ShouldHaveSingleItem();
        response.Headers.Location.ShouldBe(new Uri($"http://localhost:8288/v1/http/runs/{runId}/output?token=eyJ.token"));
        (await response.Content.ReadAsStringAsync(CancellationToken)).ShouldBeEmpty();
        _checkpoints.Saves.ShouldHaveSingleItem().OpNames.ShouldBe(["StepRun", "Sleep"]);
    }

    [Fact]
    public async Task A_hand_over_keeps_the_headers_of_earlier_middleware_and_drops_the_endpoints_own()
    {
        var client = await StartAsync();

        using var redirect = await client.PostAsync("/orders/42/ship", Json(OrderBody), CancellationToken);
        using var token = await client.PostAsync("/orders/42/ship-with-token", Json(OrderBody), CancellationToken);

        redirect.StatusCode.ShouldBe(HttpStatusCode.SeeOther);
        redirect.Headers.GetValues("X-Frame-Options").ShouldHaveSingleItem().ShouldBe("DENY");
        redirect.Headers.Contains("X-Packed-By").ShouldBeFalse();
        token.Headers.GetValues("X-Frame-Options").ShouldHaveSingleItem().ShouldBe("DENY");
    }

    [Fact]
    public async Task A_finished_request_keeps_the_headers_of_earlier_middleware()
    {
        var client = await StartAsync();

        using var response = await client.GetAsync("/ping", CancellationToken);

        response.Headers.GetValues("X-Frame-Options").ShouldHaveSingleItem().ShouldBe("DENY");
    }

    [Fact]
    public async Task The_token_response_gives_the_run_and_its_token()
    {
        var client = await StartAsync();

        using var response = await client.PostAsync("/orders/42/ship-with-token", Json(OrderBody), CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(CancellationToken);
        body.GetProperty("run_id").GetString().ShouldBe(response.Headers.GetValues("X-Run-ID").Single());
        body.GetProperty("token").GetString().ShouldBe("eyJ.token");
    }

    [Fact]
    public async Task The_redirect_can_go_to_a_url_of_the_app()
    {
        var client = await StartAsync();

        using var response = await client.PostAsync("/orders/42/ship-to-app", Json(OrderBody), CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.SeeOther);
        var runId = response.Headers.GetValues("X-Run-ID").Single();
        response.Headers.Location.ShouldBe(new Uri($"https://app.example.com/wait/{runId}?token=eyJ.token"));
    }

    [Fact]
    public async Task A_failing_step_redirects_the_client_while_Inngest_retries_it()
    {
        var client = await StartAsync();

        using var response = await client.PostAsync("/flaky", Json(OrderBody), CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.SeeOther);
        _checkpoints.Saves.ShouldHaveSingleItem().OpNames.ShouldBe(["StepError"]);
    }

    [Fact]
    public async Task A_final_failure_goes_to_the_apps_error_handling_and_records_the_run()
    {
        var client = await StartAsync();

        using var response = await client.PostAsync("/closed", Json(OrderBody), CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        (await response.Content.ReadAsStringAsync(CancellationToken)).ShouldBe("handled: the order is closed");
        await _checkpoints.WaitForSavesAsync(1);
        var result = _checkpoints.Saves.ShouldHaveSingleItem().Ops[^1].GetProperty("data");
        result.GetProperty("status").GetInt32().ShouldBe(500);
    }

    [Fact]
    public async Task A_body_larger_than_the_run_may_store_is_refused()
    {
        var client = await StartAsync();

        using var response = await client.PostAsync("/small", Json(new string('x', 100)), CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        _checkpoints.Saves.ShouldBeEmpty();
    }

    [Fact]
    public async Task Inngest_resumes_the_run_at_the_endpoint_and_gets_the_response_as_the_output()
    {
        _api.Steps = $$$"""{"{{{StepIds.Hash("price")}}}":{"data":42}}""";
        _api.Events = Events("/orders/42");
        var client = await StartAsync();

        using var response = await client.SendAsync(Resume("/orders/42?source=test", OrderBody), CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.GetValues("X-Inngest-SDK").Single().ShouldStartWith("inngest-dotnet:v");
        response.Headers.GetValues("X-Inngest-Req-Version").Single().ShouldBe("2");
        var output = (await response.Content.ReadFromJsonAsync<JsonElement>(CancellationToken)).GetProperty("data");
        output.GetProperty("status").GetInt32().ShouldBe(200);
        JsonDocument.Parse(output.GetProperty("body").GetString()!).RootElement.GetProperty("total").GetInt32().ShouldBe(42);
        _priced.ShouldBe(0);
        _api.Requests.ShouldBe(["actions:01JRESUMEDRUN000000000000000", "batch:01JRESUMEDRUN000000000000000"], ignoreOrder: true);
    }

    [Fact]
    public async Task A_resumed_run_runs_its_next_step_and_answers_with_it()
    {
        _api.Steps = "{}";
        _api.Events = Events("/orders/42");
        var client = await StartAsync();

        using var response = await client.SendAsync(Resume("/orders/42", OrderBody), CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.PartialContent);
        var op = (await response.Content.ReadFromJsonAsync<JsonElement>(CancellationToken)).EnumerateArray().ShouldHaveSingleItem();
        op.GetProperty("op").GetString().ShouldBe("StepRun");
        op.GetProperty("id").GetString().ShouldBe(StepIds.Hash("price"));
        op.GetProperty("data").GetInt32().ShouldBe(42);
        _priced.ShouldBe(1);
        _checkpoints.Saves.ShouldBeEmpty();
    }

    [Fact]
    public async Task In_cloud_mode_a_resume_needs_a_valid_signature_over_the_body()
    {
        _api.Steps = $$$"""{"{{{StepIds.Hash("price")}}}":{"data":42}}""";
        _api.Events = Events("/orders/42");
        var client = await StartAsync(cloud: true);

        using var forged = await client.SendAsync(Resume("/orders/42", OrderBody, signature: "t=1&s=00"), CancellationToken);
        forged.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var signature = new RequestSigner(TimeProvider.System).Sign(Encoding.UTF8.GetBytes(OrderBody), SigningKey.Create(Key)!);
        using var signed = await client.SendAsync(Resume("/orders/42", OrderBody, signature), CancellationToken);

        signed.StatusCode.ShouldBe(HttpStatusCode.OK);
        signed.Headers.GetValues("X-Inngest-Signature").ShouldHaveSingleItem();
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
    }

    private async Task<HttpClient> StartAsync(bool cloud = false)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddInngest(options =>
        {
            options.AppId = "shop";
            options.IsDev = !cloud;
            if (cloud)
            {
                options.SigningKey = Key;
            }
        });
        builder.Services.AddSingleton<IEndpointCheckpointApi>(_checkpoints);
        builder.Services.AddSingleton<IInngestApi>(_api);

        var app = _app = builder.Build();
        app.UseExceptionHandler(errors => errors.Run(context =>
        {
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            var error = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
            return context.Response.WriteAsync($"handled: {error?.Message}");
        }));

        // A header that middleware before the durable one sets, which every answer keeps.
        app.Use((context, next) =>
        {
            context.Response.Headers["X-Frame-Options"] = "DENY";
            return next(context);
        });
        app.UseInngestDurableEndpoints();

        app.MapPost("/orders/{id}", async (string id, Order order, IStepTools step) =>
        {
            var total = await step.RunAsync("price", () =>
            {
                Interlocked.Increment(ref _priced);
                return 42;
            });
            return Results.Ok(new { id, total, note = order.Note });
        }).WithDurableEndpoint();

        app.MapPost("/orders/{id}/ship", async (HttpResponse response, IStepTools step) =>
        {
            // Set by the endpoint before its run is handed over, so the redirect leaves it out.
            response.Headers["X-Packed-By"] = "endpoint";
            await step.RunAsync("pack", () => "packed");
            await step.SleepAsync("cool-off", TimeSpan.FromMinutes(5));
            return Results.Ok("shipped");
        }).WithDurableEndpoint();

        app.MapPost("/orders/{id}/ship-with-token", async (IStepTools step) =>
        {
            await step.SleepAsync("cool-off", TimeSpan.FromMinutes(5));
            return Results.Ok("shipped");
        }).WithDurableEndpoint(new DurableEndpointOptions { AsyncResponse = DurableEndpointResponse.Token });

        app.MapPost("/orders/{id}/ship-to-app", async (IStepTools step) =>
        {
            await step.SleepAsync("cool-off", TimeSpan.FromMinutes(5));
            return Results.Ok("shipped");
        }).WithDurableEndpoint(new DurableEndpointOptions
        {
            RedirectUrl = run => new Uri($"https://app.example.com/wait/{run.RunId}?token={run.Token}"),
        });

        app.MapPost("/flaky", async (IStepTools step) =>
        {
            Func<int> reserve = () => throw new InvalidOperationException("inventory service unavailable");
            await step.RunAsync("reserve", reserve);
            return Results.Ok("reserved");
        }).WithDurableEndpoint();

        app.MapPost("/closed", async (IStepTools step) =>
        {
            await step.RunAsync("load", () => 1);
            throw new NonRetriableException("the order is closed");
        }).WithDurableEndpoint();

        app.MapPost("/small", () => "small").WithDurableEndpoint(new DurableEndpointOptions { MaxRequestBodySize = 10 });
        app.MapGet("/ping", () => "pong").WithDurableEndpoint();
        app.MapGet("/plain", () => "plain");

        await app.StartAsync(CancellationToken);
        return app.GetTestClient();
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    // A call from Inngest that resumes a run: the original request again, with the run id and a signature.
    private static HttpRequestMessage Resume(string pathAndQuery, string body, string signature = "")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, pathAndQuery) { Content = Json(body) };
        request.Headers.Add("X-Run-ID", "01JRESUMEDRUN000000000000000");
        request.Headers.TryAddWithoutValidation("X-Inngest-Signature", signature);
        return request;
    }

    private static string Events(string path) =>
        $$"""[{"name":"inngest/http.request","data":{"domain":"http://localhost","method":"POST","path":"{{path}}","body":"{{Convert.ToBase64String(Encoding.UTF8.GetBytes(OrderBody))}}","fn":"POST /orders/{id}"},"id":"01JEVENT","ts":1793394000000}]""";

    private sealed record Order(string Note);
}
