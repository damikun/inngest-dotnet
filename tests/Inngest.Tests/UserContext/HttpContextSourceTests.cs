using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Inngest.Client;
using Inngest.Execution;
using Inngest.Tests.Checkpointing;
using Inngest.Tests.Client;
using Inngest.Tests.Execution;
using Inngest.UserContext;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Inngest.Tests.UserContext;

// A web app on a TestServer that sends events from its endpoints, with the user of the request as the source.
public sealed class HttpContextSourceTests
{
    private static async Task<WebApplication> StartAsync(
        IDataProtectionProvider keys,
        FakeHttpHandler http,
        Action<IInngestBuilder>? configure = null,
        Action<WebApplication>? map = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(keys);
        builder.Services.AddSingleton<TimeProvider>(new FakeTimeProvider(UserContextApp.Now));
        var inngest = builder.Services.AddInngest(options =>
        {
            options.AppId = "test-app";
            options.IsDev = true;
        });
        builder.Services.AddHttpClient("Inngest").ConfigurePrimaryHttpMessageHandler(() => http);
        inngest.AddUserContext(options => options.Claims = ["sub", "name", "role"]);
        configure?.Invoke(inngest);
        var app = builder.Build();

        // Stands in for authentication: a header names the user.
        app.Use((context, next) =>
        {
            if (context.Request.Headers.ContainsKey("X-Test-User"))
            {
                context.User = UserContextApp.Ada();
            }

            return next(context);
        });
        app.MapPost("/orders", async (IInngestClient client, CancellationToken cancellationToken) =>
        {
            await client.SendAsync(new InngestEvent("shop/order.paid", new { OrderId = 7 }), cancellationToken);
            return Results.Ok();
        });
        map?.Invoke(app);
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    private static Task<HttpResponseMessage> PostAsync(WebApplication app, string path, bool signedIn)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        if (signedIn)
        {
            request.Headers.Add("X-Test-User", "ada");
        }

        return app.GetTestClient().SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static JsonElement[] Sent(FakeHttpHandler http, int request = 0) =>
        [.. JsonDocument.Parse(http.Requests[request].BodyText).RootElement.EnumerateArray().Select(@event => @event.Clone())];

    [Fact]
    public async Task An_event_sent_while_serving_a_signed_in_request_carries_that_user()
    {
        var http = new FakeHttpHandler().RespondAlways(HttpStatusCode.OK, ClientFixture.Accepted);
        await using var app = await StartAsync(new EphemeralDataProtectionProvider(), http, inngest => inngest.AddUserContextFromHttpContext());

        using var response = await PostAsync(app, "/orders", signedIn: true);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var user = Sent(http).Single().GetProperty("user");
        user.GetProperty(UserContextCodec.Key).GetProperty("v").GetInt32().ShouldBe(1);
        // The protected text is random and can hold a short value by chance, so it is taken out before the search.
        var body = http.Single.BodyText.Replace(user.GetProperty(UserContextCodec.Key).GetProperty("t").GetString()!, "", StringComparison.Ordinal);
        body.ShouldNotContain("u-42");
        body.ShouldNotContain("hunter2");
    }

    [Fact]
    public async Task An_event_sent_while_serving_an_anonymous_request_carries_no_user()
    {
        var http = new FakeHttpHandler().RespondAlways(HttpStatusCode.OK, ClientFixture.Accepted);
        await using var app = await StartAsync(new EphemeralDataProtectionProvider(), http, inngest => inngest.AddUserContextFromHttpContext());

        using var response = await PostAsync(app, "/orders", signedIn: false);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        Sent(http).Single().TryGetProperty("user", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task A_protected_user_the_app_did_not_write_is_taken_out_of_the_event()
    {
        var http = new FakeHttpHandler().RespondAlways(HttpStatusCode.OK, ClientFixture.Accepted);
        await using var app = await StartAsync(
            new EphemeralDataProtectionProvider(),
            http,
            inngest => inngest.AddUserContextFromHttpContext(),
            app => app.MapPost("/track", async (IInngestClient client, CancellationToken cancellationToken) =>
            {
                // A user the app took from what the caller sent, with a protected text copied from someone else's event.
                var user = JsonDocument.Parse($$$"""{"plan":"pro","{{{UserContextCodec.Key}}}":{"v":1,"t":"copied"}}""").RootElement;
                await client.SendAsync(new InngestEvent("shop/order.paid", new { OrderId = 7 }) { User = user }, cancellationToken);
                return Results.Ok();
            }));

        using var response = await PostAsync(app, "/track", signedIn: false);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var sent = Sent(http).Single().GetProperty("user");
        sent.GetProperty("plan").GetString().ShouldBe("pro");
        sent.TryGetProperty(UserContextCodec.Key, out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Without_the_source_the_web_app_sends_without_a_user()
    {
        var http = new FakeHttpHandler().RespondAlways(HttpStatusCode.OK, ClientFixture.Accepted);
        await using var app = await StartAsync(new EphemeralDataProtectionProvider(), http);

        using var response = await PostAsync(app, "/orders", signedIn: true);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        Sent(http).Single().TryGetProperty("user", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task The_user_a_request_sent_reaches_the_run_in_another_app_that_shares_the_key_ring()
    {
        var keys = new EphemeralDataProtectionProvider();
        var http = new FakeHttpHandler().RespondAlways(HttpStatusCode.OK, ClientFixture.Accepted);
        await using var web = await StartAsync(keys, http, inngest => inngest.AddUserContextFromHttpContext());
        await using var worker = new UserContextApp(keys, options => options.Claims = ["sub", "name", "role"]);
        using var response = await PostAsync(web, "/orders", signedIn: true);
        var user = Sent(http).Single().GetProperty("user").GetRawText();

        var result = await worker.CallAsync(
            UserContextApp.Observe(),
            Call.New().Event(UserContextApp.EventJson(user, "shop/order.paid")),
            worker.Time.GetUtcNow());

        var seen = UserContextApp.SeenIn(result);
        seen.IsAuthenticated.ShouldBeTrue();
        seen.Claims.ShouldBe(["sub=u-42", "name=Ada Lovelace", "role=admin", "role=support"]);
        seen.IsAdmin.ShouldBeTrue();
    }

    [Fact]
    public async Task Events_sent_from_a_run_never_take_the_user_of_the_request_that_called_the_app()
    {
        var http = new FakeHttpHandler().RespondAlways(HttpStatusCode.OK, ClientFixture.Accepted);
        var function = TestFunction.Define(async context =>
        {
            await context.Step.SendEventAsync("notify", [new InngestEvent("shop/notified", new { })]);
            return "done";
        });
        await using var app = await StartAsync(
            new EphemeralDataProtectionProvider(),
            http,
            inngest => inngest.AddUserContextFromHttpContext(),
            map: web => web.MapPost("/call", async (IExecutionEngine engine, IServiceProvider services, HttpContext context) =>
            {
                await using var scope = services.CreateAsyncScope();
                using var result = await engine.ExecuteAsync(
                    new ExecutionRequest { Function = function, Body = Call.New().ToBytes(), StepId = "step", Services = scope.ServiceProvider },
                    context.RequestAborted);
                return Results.Ok(result.StatusCode);
            }));

        // A request that is authenticated as someone, as one reaching the app by a mistake of the app's own setup could be.
        using var response = await PostAsync(app, "/call", signedIn: true);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        Sent(http).Single().TryGetProperty("user", out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_durable_endpoint_gives_its_run_and_the_events_it_sends_the_user_of_the_request(bool signedIn)
    {
        var http = new FakeHttpHandler().RespondAlways(HttpStatusCode.OK, ClientFixture.Accepted);
        var checkpoints = new FakeEndpointCheckpointApi();
        await using var app = await StartAsync(
            new EphemeralDataProtectionProvider(),
            http,
            inngest =>
            {
                inngest.AddUserContextFromHttpContext();
                inngest.Services.AddSingleton<IEndpointCheckpointApi>(checkpoints);
            },
            web =>
            {
                web.UseInngestDurableEndpoints();
                web.MapPost("/checkout", async (IInngestClient client, ICurrentUser user, IStepTools step, CancellationToken cancellationToken) =>
                {
                    await step.RunAsync("reserve", () => 1);
                    await client.SendAsync(new InngestEvent("shop/order.placed", new { OrderId = 3 }), cancellationToken);
                    return Results.Ok(user.Principal.FindFirst("sub")?.Value ?? "anonymous");
                }).WithDurableEndpoint();
            });

        using var response = await PostAsync(app, "/checkout", signedIn);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<string>(TestContext.Current.CancellationToken)).ShouldBe(signedIn ? "u-42" : "anonymous");
        Sent(http).Single().TryGetProperty("user", out var sent).ShouldBe(signedIn);
        await checkpoints.WaitForSavesAsync(1);
        using var run = JsonDocument.Parse(checkpoints.Saves[0].Event!);
        run.RootElement.TryGetProperty("user", out var recorded).ShouldBe(signedIn);
        if (signedIn)
        {
            sent.GetProperty(UserContextCodec.Key).GetProperty("v").GetInt32().ShouldBe(1);
            recorded.GetProperty(UserContextCodec.Key).GetProperty("v").GetInt32().ShouldBe(1);
        }
    }

    // Inngest keeps the event without its user, so the code outside steps sees the user only in the request that
    // starts the run. What a step read before the wait comes back from Inngest.
    [Fact]
    public async Task A_durable_endpoint_resumed_after_a_wait_has_no_user_but_keeps_what_a_step_read_before_it()
    {
        var http = new FakeHttpHandler().RespondAlways(HttpStatusCode.OK, ClientFixture.Accepted);
        var checkpoints = new FakeEndpointCheckpointApi();
        var api = new FakeInngestApi();
        var tops = new List<string>();
        await using var app = await StartAsync(
            new EphemeralDataProtectionProvider(),
            http,
            inngest =>
            {
                inngest.AddUserContextFromHttpContext();
                inngest.Services.AddSingleton<IEndpointCheckpointApi>(checkpoints);
                inngest.Services.AddSingleton<IInngestApi>(api);
            },
            web =>
            {
                web.UseInngestDurableEndpoints();
                web.MapPost("/ship", async (ICurrentUser user, IStepTools step) =>
                {
                    var top = Name(user);
                    tops.Add(top);
                    var saved = await step.RunAsync("read-user", () => Name(user));
                    await step.SleepAsync("wait-for-packing", TimeSpan.FromMinutes(5));
                    return Results.Ok(new[] { top, saved, Name(user) });
                }).WithDurableEndpoint();
            });

        using var started = await PostAsync(app, "/ship", signedIn: true);

        started.StatusCode.ShouldBe(HttpStatusCode.SeeOther);
        var runId = started.Headers.GetValues("X-Run-ID").Single();
        var created = checkpoints.Saves[0];
        created.Ops[0].GetProperty("data").GetString().ShouldBe("u-42");
        var stored = JsonNode.Parse(created.Event!)!.AsObject();
        stored.Remove("user").ShouldBeTrue();
        api.Events = $"[{stored.ToJsonString()}]";
        api.Steps = $$$"""{"{{{StepIds.Hash("read-user")}}}":{"data":"u-42"},"{{{StepIds.Hash("wait-for-packing")}}}":{"data":null}}""";

        using var resume = new HttpRequestMessage(HttpMethod.Post, "/ship");
        resume.Headers.Add("X-Run-ID", runId);
        using var resumed = await app.GetTestClient().SendAsync(resume, TestContext.Current.CancellationToken);

        resumed.StatusCode.ShouldBe(HttpStatusCode.OK);
        var output = (await resumed.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("data");
        JsonSerializer.Deserialize<string[]>(output.GetProperty("body").GetString()!).ShouldBe(["anonymous", "u-42", "anonymous"]);
        tops.ShouldBe(["u-42", "anonymous"]);
    }

    private static string Name(ICurrentUser user) => user.Principal.FindFirst("sub")?.Value ?? "anonymous";

    [Fact]
    public async Task An_app_that_registered_its_own_source_keeps_it()
    {
        var http = new FakeHttpHandler().RespondAlways(HttpStatusCode.OK, ClientFixture.Accepted);
        var own = new SendTests.FixedSource(UserContextApp.Principal("Cookies", new Claim("sub", "from-my-source")));
        await using var app = await StartAsync(new EphemeralDataProtectionProvider(), http, inngest =>
        {
            inngest.Services.AddSingleton<IUserContextSource>(own);
            inngest.AddUserContextFromHttpContext();
        });

        using var response = await PostAsync(app, "/orders", signedIn: true);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        own.Calls.ShouldBe(1);
        app.Services.GetRequiredService<IUserContextSource>().ShouldBeSameAs(own);
    }
}
