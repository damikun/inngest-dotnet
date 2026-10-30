using System.Net.Http.Json;
using System.Security.Claims;
using Inngest.IntegrationTests.Harness;
using Inngest.UserContext;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Inngest.IntegrationTests.UserContext;

// The user behind an event, through a real Inngest server: a web app that sends an event for a signed-in user, and a
// worker app that shares its key ring and restores the user in the run. The runs sleep, send events and invoke, so the
// user has to survive the replays and travel with them.
public sealed class UserContextTests(UserContextFixture fixture) : IntegrationTest<UserContextFixture>(fixture), IClassFixture<UserContextFixture>
{
    // The web app allows the email too; the worker restores whatever the sender allowed. The password claim is never allowed.
    private const string Ada = "Ada Lovelace|sub=u-42,name=Ada Lovelace,email=ada@example.com,role=admin,role=support|admin=True";

    [Fact(Timeout = 90_000)]
    public async Task A_user_sent_from_a_request_is_the_user_of_every_call_step_event_and_invocation_of_the_run()
    {
        await using var web = await StartWebAppAsync();
        var sentAt = DateTimeOffset.UtcNow;

        var eventId = await web.SendAsync(UserContextFunctions.Report, TestId, signedIn: true);
        var run = await Api.WaitForRunEndAsync(eventId, Cancellation);

        run.Status.ShouldBe(RunStatus.Completed);
        var result = run.OutputAs<ReportResult>();
        result.Direct.ShouldBe(Ada);
        result.InStep.ShouldBe(Ada);
        result.Child.ShouldBe(Ada);

        // The top of the function ran on every call of the run, before the sleep and after it, and saw the same user.
        var starts = Observed.Values<string>("start");
        starts.Count.ShouldBeGreaterThan(1);
        starts.Distinct().ShouldBe([Ada]);
        Observed.Values<string>("after-sleep").Distinct().ShouldBe([Ada]);

        // The event the step sent and the function it invoked ran as the same user.
        (await Observed.WaitForAsync("followup", TimeSpan.FromSeconds(20), Cancellation)).Value.ShouldBe(Ada);
        Observed.Values<string>("child").Distinct().ShouldBe([Ada]);

        // Inngest makes the id of a run from the time it created it, which is what the expiry is judged by.
        var created = RunIds.StartOf(result.RunId).ShouldNotBeNull();
        created.ShouldBeInRange(sentAt.AddSeconds(-5), DateTimeOffset.UtcNow.AddSeconds(5));
    }

    [Fact(Timeout = 60_000)]
    public async Task A_failure_handler_runs_as_the_user_who_started_the_run_that_failed()
    {
        await using var web = await StartWebAppAsync();

        var eventId = await web.SendAsync(UserContextFunctions.Failing, TestId, signedIn: true);
        var run = await Api.WaitForRunEndAsync(eventId, Cancellation);

        run.Status.ShouldBe(RunStatus.Failed);
        (await Observed.WaitForAsync("failure-handler", TimeSpan.FromSeconds(20), Cancellation)).Value.ShouldBe(Ada);
    }

    [Fact(Timeout = 90_000)]
    public async Task An_event_sent_for_nobody_runs_as_nobody_all_the_way()
    {
        await using var web = await StartWebAppAsync();

        var eventId = await web.SendAsync(UserContextFunctions.Report, TestId, signedIn: false);
        var run = await Api.WaitForRunEndAsync(eventId, Cancellation);

        run.Status.ShouldBe(RunStatus.Completed);
        var result = run.OutputAs<ReportResult>();
        new[] { result.Direct, result.InStep, result.Child }.ShouldAllBe(seen => seen == "nobody");
        (await Observed.WaitForAsync("followup", TimeSpan.FromSeconds(20), Cancellation)).Value.ShouldBe("nobody");
        Observed.Values<string>("start").Distinct().ShouldBe(["nobody"]);
    }

    [Fact(Timeout = 90_000)]
    public async Task A_user_made_up_by_whoever_sent_the_event_is_refused_and_the_run_has_nobody()
    {
        const string forged = """{"v":1,"t":"CfDJ8-this-was-not-made-by-the-key-ring"}""";
        using var http = new HttpClient { BaseAddress = Api.BaseUrl };
        using var response = await http.PostAsJsonAsync(
            "e/itests",
            new object[]
            {
                new Dictionary<string, object>
                {
                    ["name"] = UserContextFunctions.Report,
                    ["data"] = new UserEvent(TestId),
                    ["user"] = new Dictionary<string, object> { [UserContextCodec.Key] = System.Text.Json.JsonDocument.Parse(forged).RootElement },
                },
            },
            System.Text.Json.JsonSerializerOptions.Web,
            Cancellation);
        response.EnsureSuccessStatusCode();
        var eventId = (await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(Cancellation)).GetProperty("ids")[0].GetString()!;

        var run = await Api.WaitForRunEndAsync(eventId, Cancellation);

        run.Status.ShouldBe(RunStatus.Completed);
        run.OutputAs<ReportResult>().Direct.ShouldBe("nobody");
        var warnings = Fixture.App.Logs.GetSnapshot()
            .Where(record => record.Level == LogLevel.Warning && record.Message.Contains("could not be unprotected", StringComparison.Ordinal))
            .ToArray();
        warnings.ShouldNotBeEmpty();
        warnings.ShouldAllBe(record => !record.Message.Contains("CfDJ8", StringComparison.Ordinal));
    }

    private async Task<WebApp> StartWebAppAsync() => await WebApp.StartAsync(Api.BaseUrl);

    // The web app of the system: it serves requests of signed-in users, and sends events for them. It is not an app that
    // Inngest calls, so it is not synced; it only has a client.
    private sealed class WebApp : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private WebApp(WebApplication app) => _app = app;

        public static async Task<WebApp> StartAsync(Uri devServer)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            var inngest = builder.Services.AddInngest(options =>
            {
                options.AppId = "itests-user-web";
                options.IsDev = true;
                options.DevServerUrl = devServer;
            });
            builder.Services.AddDataProtection().SetApplicationName(UserContextFunctions.ApplicationName).PersistKeysToFileSystem(UserContextFixture.Keys);
            inngest.AddUserContext(options => options.Claims = ["sub", "name", "role", "email"]);
            inngest.AddUserContextFromHttpContext();
            var app = builder.Build();

            // Stands in for authentication: a header says who is signed in.
            app.Use((context, next) =>
            {
                if (context.Request.Headers.ContainsKey("X-Signed-In"))
                {
                    context.User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim("sub", "u-42"), new Claim("name", "Ada Lovelace"), new Claim("email", "ada@example.com"), new Claim("role", "admin"), new Claim("role", "support")],
                        "Cookies",
                        "name",
                        "role"));
                }

                return next(context);
            });
            app.MapPost("/events/{name}/{testId}", async (string name, string testId, IInngestClient client, CancellationToken cancellationToken) =>
            {
                var result = await client.SendAsync(new InngestEvent(Uri.UnescapeDataString(name), new UserEvent(testId)), cancellationToken);
                return Results.Ok(result.Ids[0]);
            });
            await app.StartAsync(TestContext.Current.CancellationToken);
            return new WebApp(app);
        }

        // Posts to the web app as a browser would, and returns the id Inngest gave the event.
        public async Task<string> SendAsync(string eventName, string testId, bool signedIn)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/events/{Uri.EscapeDataString(eventName)}/{testId}");
            if (signedIn)
            {
                request.Headers.Add("X-Signed-In", "1");
            }

            using var response = await _app.GetTestClient().SendAsync(request, Cancellation);
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadAsStringAsync(Cancellation)).Trim('"');
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
