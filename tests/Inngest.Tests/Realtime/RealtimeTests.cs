using System.Net;
using System.Text;
using Inngest.Realtime;
using Inngest.Testing;
using Inngest.Tests.Client;
using Inngest.Tests.Execution;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using static Inngest.Tests.Testing.TestHosts;

namespace Inngest.Tests.Realtime;

public sealed class RealtimeTests
{
    private static readonly RealtimeTopic Status = new("document:abc 123", "status");

    [Fact]
    public async Task Publishing_posts_the_message_to_the_channel_and_topic_like_inngest_js()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK);
        await using var provider = Build(handler);

        await provider.GetRequiredService<IInngestRealtime>().PublishAsync(Status, new { Message = "Analyzing" });

        var request = handler.Single;
        request.Method.ShouldBe(HttpMethod.Post);
        request.Uri.AbsoluteUri.ShouldBe("http://localhost:8288/v1/realtime/publish?channel=document%3Aabc%20123&topic=status");
        request.BodyText.ShouldBe("""{"message":"Analyzing"}""");
        request.Header("Content-Type").ShouldBe("application/json");
    }

    [Fact]
    public async Task Text_is_published_as_it_is()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK);
        await using var provider = Build(handler);

        await provider.GetRequiredService<IInngestRealtime>().PublishAsync(new RealtimeTopic("chat", "tokens"), "Hel");

        handler.Single.BodyText.ShouldBe("Hel");
    }

    [Fact]
    public async Task A_refused_publish_throws_with_what_Inngest_said()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.Unauthorized, """{"error":"invalid signing key"}""");
        await using var provider = Build(handler);

        var exception = await Should.ThrowAsync<InngestException>(() => provider.GetRequiredService<IInngestRealtime>().PublishAsync(Status, 1));

        exception.Message.ShouldStartWith("Failed to publish to topic \"status\" of channel \"document:abc 123\": ");
        exception.Message.ShouldContain("invalid signing key");
    }

    [Fact]
    public async Task A_token_is_asked_for_each_topic_and_comes_back_as_the_token_object_of_inngest_js()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK, """{"jwt":"eyJ.token"}""");
        await using var provider = Build(handler);

        var token = await provider.GetRequiredService<IInngestRealtime>().GetSubscriptionTokenAsync("document:abc", ["status", "result"]);

        handler.Single.Uri.AbsoluteUri.ShouldBe("http://localhost:8288/v1/realtime/token");
        handler.Single.BodyText.ShouldBe(
            """[{"channel":"document:abc","name":"status","kind":"run"},{"channel":"document:abc","name":"result","kind":"run"}]""");
        token.ShouldBe(new RealtimeToken("document:abc", ["status", "result"], "eyJ.token") { ApiBaseUrl = "http://localhost:8288/" }, RealtimeTokenComparer.Instance);
        System.Text.Json.JsonSerializer.Serialize(token, EngineHost.Settings.SerializerOptions)
            .ShouldBe("""{"channel":"document:abc","topics":["status","result"],"key":"eyJ.token","apiBaseUrl":"http://localhost:8288/"}""");
    }

    [Fact]
    public async Task An_answer_without_a_token_throws()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK, "{}");
        await using var provider = Build(handler);

        var exception = await Should.ThrowAsync<InngestException>(() =>
            provider.GetRequiredService<IInngestRealtime>().GetSubscriptionTokenAsync("document:abc", ["status"]));

        exception.Message.ShouldBe("Failed to get a subscription token for channel \"document:abc\": Inngest answered without a token: {}");
    }

    [Fact]
    public async Task A_publish_step_publishes_with_the_run_id_and_returns_the_data()
    {
        var api = new RecordingApi();
        using var host = new EngineHost(services => services.AddSingleton<IRealtimeApi>(api));
        var function = TestFunction.Define(async context => await context.Step.PublishAsync("status", Status, new Progress("Analyzing", 10)));

        var result = await host.CallAsync(function, Call.New());

        result.OpCodes.ShouldBe(["StepRun"]);
        result.Op().GetProperty("opts").GetRawText().ShouldBe("""{"type":"step.realtime.publish"}""");
        result.Op().GetProperty("data").GetRawText().ShouldBe("""{"text":"Analyzing","percent":10}""");
        api.Published.ShouldBe([(Status, (string?)"01JRUN", """{"text":"Analyzing","percent":10}""")]);
    }

    [Fact]
    public async Task A_memoized_publish_step_does_not_publish_again()
    {
        var api = new RecordingApi();
        using var host = new EngineHost(services => services.AddSingleton<IRealtimeApi>(api));
        var function = TestFunction.Define(async context => (object?)(await context.Step.PublishAsync("status", Status, new Progress("Analyzing", 10))).Percent);

        var result = await host.CallAsync(function, Call.New().Data("status", """{"text":"Analyzing","percent":10}"""));

        result.Body.ShouldBe("10");
        api.Published.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_test_host_records_what_a_run_publishes()
    {
        await using var host = For(async context =>
        {
            await context.Step.PublishAsync("status", Status, new Progress("Analyzing", 10));
            await context.Step.RunAsync("tokens", () => context.Services.GetRequiredService<IInngestRealtime>().PublishAsync(new RealtimeTopic("chat", "tokens"), "Hel"));
            return null;
        });

        var run = await host.RunAsync("fn", Event(), Cancel);

        run.Status.ShouldBe(TestRunStatus.Completed);
        run.Published.Select(message => (message.Topic, message.Data.GetRawText())).ShouldBe([
            (Status, """{"text":"Analyzing","percent":10}"""),
            (new RealtimeTopic("chat", "tokens"), "\"Hel\""),
        ]);
        run.GetStep("status").Type.ShouldBe(StepType.RealtimePublish);
        (await host.Services.GetRequiredService<IInngestRealtime>().GetSubscriptionTokenAsync("chat", ["tokens"])).Key.ShouldBe("test-host-token");
    }

    private static ServiceProvider Build(FakeHttpHandler handler) =>
        ClientServices.Build(handler, new FakeTimeProvider(new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero)));

    private sealed record Progress(string Text, int Percent);

    private sealed class RecordingApi : IRealtimeApi
    {
        public List<(RealtimeTopic Topic, string? RunId, string Body)> Published { get; } = [];

        public Task PublishAsync(RealtimeTopic topic, string? runId, ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
        {
            Published.Add((topic, runId, Encoding.UTF8.GetString(body.Span)));
            return Task.CompletedTask;
        }

        public Task<string> GetTokenAsync(string channel, IReadOnlyList<string> topics, CancellationToken cancellationToken) => Task.FromResult("jwt");
    }

    // Records compare lists by reference.
    private sealed class RealtimeTokenComparer : IEqualityComparer<RealtimeToken>
    {
        public static readonly RealtimeTokenComparer Instance = new();

        public bool Equals(RealtimeToken? x, RealtimeToken? y) =>
            x is not null && y is not null && x.Channel == y.Channel && x.Topics.SequenceEqual(y.Topics) && x.Key == y.Key && x.ApiBaseUrl == y.ApiBaseUrl;

        public int GetHashCode(RealtimeToken obj) => obj.Channel.GetHashCode(StringComparison.Ordinal);
    }
}
