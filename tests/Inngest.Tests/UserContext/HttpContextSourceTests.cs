using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Inngest.Execution;
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
        http.Single.BodyText.ShouldNotContain("u-42");
        http.Single.BodyText.ShouldNotContain("hunter2");
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
