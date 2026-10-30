using System.Net;
using Inngest.Realtime;
using Inngest.Tests.Execution;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Inngest.Tests.Realtime;

// MapInngestRealtime on an in-memory server, with Inngest's realtime API scripted.
public sealed class RealtimeRelayTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly FakeRealtimeApi _api = new();
    private readonly TestTimeProvider _time = new(EngineHost.Now);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Browsers_watching_the_same_topics_share_one_subscription_and_get_Inngests_messages()
    {
        var feed = new SseFeed();
        _api.Answer(feed);
        await using var app = await StartAsync();
        var client = app.GetTestClient();

        using var first = await OpenAsync(client, "/realtime/o-1");
        using var second = await OpenAsync(client, "/realtime/o-1");
        const string Message = """{"kind":"data","data":{"text":"Packed"},"channel":"order:o-1","topic":"status"}""";
        await feed.SendAsync(Message);

        (await NextDataAsync(first)).ShouldBe(Message);
        (await NextDataAsync(second)).ShouldBe(Message);
        first.Response.Content.Headers.ContentType!.MediaType.ShouldBe("text/event-stream");
        first.Response.Headers.CacheControl!.NoCache.ShouldBeTrue();
        _api.TokensMinted.ShouldBe(1);
    }

    [Fact]
    public async Task A_request_the_callback_refuses_gets_403()
    {
        await using var app = await StartAsync();

        using var response = await app.GetTestClient().GetAsync("/realtime/forbidden", Cancel);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        _api.TokensMinted.ShouldBe(0);
    }

    [Fact]
    public async Task The_subscription_ends_when_the_last_browser_leaves()
    {
        var feed = new SseFeed();
        _api.Answer(feed);
        await using var app = await StartAsync();
        var client = app.GetTestClient();

        var first = await OpenAsync(client, "/realtime/o-2");
        var second = await OpenAsync(client, "/realtime/o-2");
        await Until(() => _api.Subscriptions.Count == 1);
        first.Dispose();
        await Task.Delay(100, Cancel);
        _api.Subscriptions[0].IsCancellationRequested.ShouldBeFalse();
        second.Dispose();

        await Until(() => _api.Subscriptions[0].IsCancellationRequested);
    }

    [Fact]
    public async Task A_quiet_connection_gets_a_comment_every_15_seconds()
    {
        _api.Answer(new SseFeed());
        await using var app = await StartAsync();

        using var connection = await OpenAsync(app.GetTestClient(), "/realtime/o-3");
        (await connection.Reader.ReadLineAsync(Cancel)).ShouldBe(":");
        (await connection.Reader.ReadLineAsync(Cancel)).ShouldBe(string.Empty);
        await _time.TimersCreated(1).WaitAsync(Wait, Cancel);
        _time.Advance(TimeSpan.FromSeconds(15));

        (await connection.Reader.ReadLineAsync(Cancel).AsTask().WaitAsync(Wait, Cancel)).ShouldBe(":");
    }

    [Fact]
    public async Task A_refused_subscription_ends_the_response()
    {
        _api.TokenFailure = new InngestException("Failed to get a subscription token for channel \"order:o-4\": 401 Unauthorized", 401);
        await using var app = await StartAsync();

        using var connection = await OpenAsync(app.GetTestClient(), "/realtime/o-4");

        (await connection.Reader.ReadToEndAsync(Cancel).WaitAsync(Wait, Cancel)).ShouldBe(":\n\n");
    }

    private async Task<WebApplication> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddInngest(options =>
        {
            options.AppId = "shop";
            options.IsDev = true;
        });
        builder.Services.Replace(ServiceDescriptor.Singleton<IRealtimeApi>(_api));
        builder.Services.Replace(ServiceDescriptor.Singleton<TimeProvider>(_time));
        var app = builder.Build();
        app.MapInngestRealtime("/realtime/{order}", context =>
        {
            var order = (string)context.Request.RouteValues["order"]!;
            return ValueTask.FromResult(order == "forbidden" ? null : new RealtimeSubscription($"order:{order}", ["status"]));
        });
        await app.StartAsync(Cancel);
        return app;
    }

    private static async Task<Connection> OpenAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, Cancel);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return new Connection(response, new StreamReader(await response.Content.ReadAsStreamAsync(Cancel)));
    }

    // The data of the next event, skipping comments.
    private static async Task<string> NextDataAsync(Connection connection)
    {
        while (await connection.Reader.ReadLineAsync(Cancel).AsTask().WaitAsync(Wait, Cancel) is { } line)
        {
            if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                return line["data: ".Length..];
            }
        }

        throw new InvalidOperationException("The response ended.");
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The condition was not met in time.");
            }

            await Task.Delay(10, Cancel);
        }
    }

    private sealed record Connection(HttpResponseMessage Response, StreamReader Reader) : IDisposable
    {
        public void Dispose()
        {
            Reader.Dispose();
            Response.Dispose();
        }
    }
}
