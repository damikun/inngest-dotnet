using System.Collections.Concurrent;
using Inngest.IntegrationTests.Harness;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Inngest.IntegrationTests.DurableEndpoints;

// The dev server and an app in dev mode with durable endpoints and no functions. ServeOrigin is the address the dev
// server reaches the app at from its container, which every run records as its domain, so that Inngest can call the
// endpoints back. Each endpoint takes the test's id in its route and counts its attempts under it.
public sealed class DurableEndpointFixture : InngestFixture
{
    private readonly ConcurrentDictionary<string, int> _attempts = new(StringComparer.Ordinal);

    public override string AppId => "durable-endpoints";

    public override IReadOnlyCollection<string> ExpectedFunctions => [];

    public Uri AppUrl => new($"http://localhost:{AppPort}");

    protected override string EventKey => "itests";

    // How often the named part of the test's endpoint ran.
    public int Attempts(string testId, string name) => _attempts.GetValueOrDefault($"{testId}/{name}");

    protected override InngestServer CreateServer(int hostPort, Uri appUrl) => InngestServer.Dev(hostPort, appUrl);

    protected override void ConfigureInngest(InngestOptions options, Uri serverUrl)
    {
        options.AppId = AppId;
        options.IsDev = true;
        options.DevServerUrl = serverUrl;
        options.ServeOrigin = new Uri($"http://host.docker.internal:{AppPort}");
    }

    protected override void AddFunctions(IInngestBuilder inngest)
    {
    }

    protected override void ConfigureApp(WebApplication app)
    {
        app.UseInngestDurableEndpoints();

        app.MapPost("/orders/{testId}", async (string testId, Order order, IStepTools step) =>
        {
            var total = await step.RunAsync("price", () =>
            {
                Count(testId, "price");
                return 42;
            });
            return Results.Ok(new { testId, total, note = order.Note });
        }).WithDurableEndpoint("orders");

        // Echoes what the request carried after a sleep, which only a call that resumes the run reaches.
        app.MapPost("/ship/{testId}", async (string testId, HttpRequest request, IStepTools step) =>
        {
            using var reader = new StreamReader(request.Body);
            var body = await reader.ReadToEndAsync();
            var packed = await step.RunAsync("pack", () => Count(testId, "pack"));
            await step.SleepAsync("cool-off", TimeSpan.FromSeconds(2));
            Count(testId, "after-sleep");
            return Results.Json(new { body, query = request.QueryString.Value, packed }, statusCode: StatusCodes.Status202Accepted);
        }).WithDurableEndpoint("ship");

        // The step fails on its first attempt; Inngest retries it and the client gets the result through the redirect.
        app.MapPost("/flaky/{testId}", async (string testId, IStepTools step) =>
        {
            var reserved = await step.RunAsync("reserve", () => Count(testId, "reserve") == 1
                ? throw new InvalidOperationException("inventory service unavailable")
                : "reserved");
            return Results.Ok(new { reserved, attempts = Attempts(testId, "reserve") });
        }).WithDurableEndpoint("flaky");

        // The endpoint itself throws on its first attempt, before any step.
        app.MapPost("/unstable/{testId}", (string testId) => Count(testId, "unstable") == 1
            ? throw new InvalidOperationException("database timeout")
            : Results.Ok(new { attempts = Attempts(testId, "unstable") })).WithDurableEndpoint("unstable");

        // Without retries the failed step comes back as a StepFailedException, which the endpoint turns into a 409.
        app.MapPost("/refused/{testId}", async (string testId, IStepTools step) =>
        {
            Func<string> charge = () =>
            {
                Count(testId, "charge");
                throw new InvalidOperationException("card declined");
            };
            try
            {
                await step.RunAsync("charge", charge);
                return Results.Ok();
            }
            catch (StepFailedException exception)
            {
                return Results.Conflict(new { error = exception.Error.Message });
            }
        }).WithDurableEndpoint(new DurableEndpointOptions { Id = "refused", Retries = 0 });

        // Waits for an approval event that carries the test's id.
        app.MapPost("/approve/{testId}", async (string testId, IStepTools step) =>
        {
            var approval = await step.WaitForEventAsync(
                "approval",
                new WaitForEventOptions("test/approved", TimeSpan.FromMinutes(1)) { If = $"async.data.testId == \"{testId}\"" });
            return Results.Ok(new { approvedBy = approval?.Data.GetProperty("by").GetString() });
        }).WithDurableEndpoint("approve");
    }

    private int Count(string testId, string name) => _attempts.AddOrUpdate($"{testId}/{name}", 1, static (_, count) => count + 1);

    public sealed record Order(string Note);
}
