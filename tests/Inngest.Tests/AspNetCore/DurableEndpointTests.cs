using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Inngest.Client;
using Inngest.Execution;
using Inngest.Signing;
using Inngest.Tests.Checkpointing;
using Inngest.Tests.Execution;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using FakeInngestApi = Inngest.Tests.Execution.FakeInngestApi;

namespace Inngest.Tests.AspNetCore;

// Durable endpoints over HTTP in memory, with the real engine and a fake Inngest API: what the client gets, what the run
// records, and how a call from Inngest resumes the run at the endpoint's own URL.
public sealed class DurableEndpointTests : IAsyncDisposable
{
    private const string EncryptionKey = "0123456789abcdef0123456789abcdef";
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
        _checkpoints.Saves.Select(save => save.OpNames.ShouldHaveSingleItem()).ShouldBe(["StepRun", "Sleep"]);
    }

    // A test of the app may give it a fake clock that nothing moves. The hand-over still waits for Inngest to store the
    // endpoint, which happens in real time.
    [Fact]
    public async Task The_first_hand_over_of_an_app_whose_clock_never_moves_still_reaches_Inngest()
    {
        var client = await StartAsync(clock: new FakeTimeProvider(EngineHost.Now));

        using var response = await client.PostAsync("/orders/42/ship", Json(OrderBody), CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.SeeOther);
        _checkpoints.Saves.Select(save => save.OpNames.ShouldHaveSingleItem()).ShouldBe(["StepRun", "Sleep"]);
    }

    // A dev server that keeps nothing, inngest dev without --persist, forgets the endpoints it stored when it restarts,
    // and the first sync after that is a change to it.
    [Fact]
    public async Task After_a_sync_Inngest_takes_as_a_change_the_next_hand_over_waits_for_Inngest_to_store_the_endpoint_again()
    {
        var clock = new TestTimeProvider(EngineHost.Now);
        _api.Registered = new RegisterResult(Success: true, StatusCode: 200, Modified: true, Error: null);
        var client = await StartAsync(firstRuns: new EndpointFirstRuns(clock), serve: true);
        await HandOverAsync(client, clock);
        clock.Advance(TimeSpan.FromMinutes(1));

        using var sync = await client.PutAsync("/api/inngest", content: null, CancellationToken);
        var next = client.PostAsync("/orders/43/ship", Json(OrderBody), CancellationToken);
        await clock.TimersCreated(2).WaitAsync(TimeSpan.FromSeconds(10), CancellationToken);
        next.IsCompleted.ShouldBeFalse();
        clock.Advance(TimeSpan.FromSeconds(1));
        using var response = await next.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken);

        sync.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.StatusCode.ShouldBe(HttpStatusCode.SeeOther);
        _checkpoints.Saves.Select(save => save.OpNames.ShouldHaveSingleItem()).ShouldBe(["StepRun", "Sleep", "StepRun", "Sleep"]);
    }

    // The dev server syncs the app every few seconds, and Inngest takes those syncs as no change.
    [Fact]
    public async Task A_sync_Inngest_takes_as_no_change_leaves_the_next_hand_over_without_a_wait()
    {
        var clock = new TestTimeProvider(EngineHost.Now);
        _api.Registered = new RegisterResult(Success: true, StatusCode: 200, Modified: false, Error: null);
        var client = await StartAsync(firstRuns: new EndpointFirstRuns(clock), serve: true);
        await HandOverAsync(client, clock);
        clock.Advance(TimeSpan.FromMinutes(1));

        using var sync = await client.PutAsync("/api/inngest", content: null, CancellationToken);
        using var response = await client.PostAsync("/orders/43/ship", Json(OrderBody), CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), CancellationToken);

        sync.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.StatusCode.ShouldBe(HttpStatusCode.SeeOther);
        clock.TimerCount.ShouldBe(1);
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
    public async Task The_apps_error_handling_answers_before_the_failed_run_is_recorded()
    {
        var client = await StartAsync();
        var (reached, release) = _checkpoints.HoldNext();

        using var response = await client.PostAsync("/closed", Json(OrderBody), CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken);

        (await response.Content.ReadAsStringAsync(CancellationToken)).ShouldBe("handled: the order is closed");
        await reached;
        _checkpoints.Saves.ShouldBeEmpty();
        release.SetResult();
        await _checkpoints.WaitForSavesAsync(1);
    }

    [Fact]
    public async Task A_run_still_being_recorded_when_the_app_stops_is_recorded_before_it_stops()
    {
        var client = await StartAsync();
        var (reached, release) = _checkpoints.HoldNext();
        using var response = await client.PostAsync("/orders/42", Json(OrderBody), CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken);
        var runId = response.Headers.GetValues("X-Run-ID").Single();
        await reached;

        // The host cancels ApplicationStopping before it stops its services.
        var stoppingSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = _app!.Lifetime.ApplicationStopping.Register(() => stoppingSignal.TrySetResult());
        var stopping = _app.StopAsync(CancellationToken);
        await stoppingSignal.Task.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken);
        stopping.IsCompleted.ShouldBeFalse();
        release.SetResult();
        await stopping;

        _checkpoints.Saves.ShouldHaveSingleItem().RunId.ShouldBe(runId);
    }

    [Fact]
    public async Task A_run_still_being_recorded_when_the_shutdown_timeout_ends_is_logged_as_maybe_not_recorded()
    {
        var client = await StartAsync(shutdownTimeout: TimeSpan.FromMilliseconds(200));
        var (reached, _) = _checkpoints.HoldNext();
        using var response = await client.PostAsync("/orders/42", Json(OrderBody), CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken);
        var runId = response.Headers.GetValues("X-Run-ID").Single();
        await reached;

        await _app!.StopAsync(CancellationToken);

        _checkpoints.Saves.ShouldBeEmpty();
        var warning = _app.Services.GetFakeLogCollector().GetSnapshot().Where(record => record.Id.Id is 153 or 156).ShouldHaveSingleItem();
        warning.Id.Id.ShouldBe(156);
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.Message.ShouldContain(runId);
        warning.Message.ShouldContain("POST /orders/{id}");
    }

    // Past the most saves under way, the connection waits for the run to be recorded once the client has its response.
    [Fact]
    public async Task While_Inngest_is_behind_the_client_still_gets_its_response_and_the_run_is_recorded()
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = await StartAsync(maxRunningSaves: 0, responseCompleted: completed);
        var (reached, release) = _checkpoints.HoldNext();

        using var response = await client.PostAsync("/orders/42", Json(OrderBody), CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var order = await response.Content.ReadFromJsonAsync<JsonElement>(CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken);
        order.GetProperty("total").GetInt32().ShouldBe(42);
        await reached;
        (await Task.WhenAny(completed.Task, Task.Delay(TimeSpan.FromMilliseconds(200), CancellationToken))).ShouldNotBe(completed.Task);
        _checkpoints.Saves.ShouldBeEmpty();
        release.SetResult();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken);
        await _checkpoints.WaitForSavesAsync(1);
        _checkpoints.Saves.ShouldHaveSingleItem().RunId.ShouldBe(response.Headers.GetValues("X-Run-ID").Single());
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

    [Fact]
    public async Task With_encryption_the_output_of_a_request_that_finishes_at_once_is_recorded_encrypted()
    {
        var client = await StartAsync(encrypt: true);

        using var response = await client.PostAsync("/orders/42", Json(OrderBody), CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<JsonElement>(CancellationToken)).GetProperty("note").GetString().ShouldBe("gift");
        await _checkpoints.WaitForSavesAsync(1);
        var save = _checkpoints.Saves.ShouldHaveSingleItem();
        save.OpNames.ShouldBe(["StepRun", "RunComplete"]);
        var output = save.Ops[1].GetProperty("data").GetRawText();
        output.ShouldContain("__ENCRYPTED__");
        output.ShouldNotContain("gift");
    }

    [Fact]
    public async Task In_cloud_mode_the_app_does_not_start_without_a_serve_origin()
    {
        var error = await Should.ThrowAsync<InvalidOperationException>(() => StartAsync(cloud: true, serveOrigin: null));

        error.Message.ShouldContain("ServeOrigin");
    }

    [Fact]
    public async Task In_cloud_mode_the_run_is_resumed_at_the_serve_origin_whatever_the_host_header()
    {
        var client = await StartAsync(cloud: true);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/orders/42") { Content = Json(OrderBody) };
        request.Headers.Host = "attacker.example";

        using var response = await client.SendAsync(request, CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await _checkpoints.WaitForSavesAsync(1);
        using var @event = JsonDocument.Parse(_checkpoints.Saves.ShouldHaveSingleItem().Event!);
        @event.RootElement.GetProperty("data").GetProperty("domain").GetString().ShouldBe("https://api.example.com");
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
    }

    private async Task<HttpClient> StartAsync(
        bool cloud = false,
        string? serveOrigin = "https://api.example.com",
        bool encrypt = false,
        TimeSpan? shutdownTimeout = null,
        int? maxRunningSaves = null,
        TaskCompletionSource? responseCompleted = null,
        TimeProvider? clock = null,
        EndpointFirstRuns? firstRuns = null,
        bool serve = false)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.AddFakeLogging();
        if (shutdownTimeout is { } timeout)
        {
            builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = timeout);
        }

        var inngest = builder.Services.AddInngest(options =>
        {
            options.AppId = "shop";
            options.IsDev = !cloud;
            if (cloud)
            {
                options.SigningKey = Key;
                options.ServeOrigin = serveOrigin is null ? null : new Uri(serveOrigin);
            }
        });
        if (encrypt)
        {
            inngest.AddEncryption(options => options.Key = EncryptionKey);
        }

        builder.Services.AddSingleton<IEndpointCheckpointApi>(_checkpoints);
        builder.Services.AddSingleton<IInngestApi>(_api);
        if (maxRunningSaves is { } maxRunning)
        {
            builder.Services.AddSingleton(provider => new EndpointRunSaves(provider.GetRequiredService<ILogger<EndpointRunSaves>>()) { MaxRunning = maxRunning });
        }

        if (clock is not null)
        {
            builder.Services.AddSingleton(clock);
        }

        if (firstRuns is not null)
        {
            builder.Services.AddSingleton(firstRuns);
        }

        var app = _app = builder.Build();
        if (serve)
        {
            app.MapInngest();
        }

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

        // Callbacks run in the reverse order of their registration, so this one runs once the durable endpoints' own
        // have, which is when the connection takes its next request.
        if (responseCompleted is not null)
        {
            app.Use((context, next) =>
            {
                context.Response.OnCompleted(() =>
                {
                    responseCompleted.TrySetResult();
                    return Task.CompletedTask;
                });
                return next(context);
            });
        }

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

    // A hand-over that waits a second for Inngest to store the endpoint, which the test lets pass.
    private static async Task HandOverAsync(HttpClient client, TestTimeProvider clock)
    {
        var timers = clock.TimerCount;
        var request = client.PostAsync("/orders/42/ship", Json(OrderBody), CancellationToken);
        await clock.TimersCreated(timers + 1).WaitAsync(TimeSpan.FromSeconds(10), CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(1));
        using var response = await request.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.SeeOther);
    }

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
