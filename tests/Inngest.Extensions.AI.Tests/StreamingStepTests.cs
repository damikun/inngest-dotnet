using System.Text.Json;
using Inngest.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using static Inngest.Tests.Testing.TestHosts;

namespace Inngest.Extensions.AI.Tests;

// A streaming call of a run is one step that reads the model's whole stream; the function gets the memoized response
// back as updates, and a replay gets the same without the model.
public sealed class StreamingStepTests
{
    [Fact]
    public async Task A_stream_in_a_run_is_one_step_and_a_replay_does_not_stream_again()
    {
        var model = new FakeChatClient((_, _) => new ChatResponse(new ChatMessage(ChatRole.Assistant, "The quick brown fox"))
        {
            ModelId = "gpt-test",
            Usage = new UsageDetails { InputTokenCount = 4, OutputTokenCount = 4, TotalTokenCount = 8 },
        });
        var chat = new ChatClientBuilder(model).UseInngestSteps().Build();
        List<ChatResponseUpdate> received = [];
        await using var host = For(async context =>
        {
            received.Clear();
            await foreach (var update in chat.GetStreamingResponseAsync("Tell me a story"))
            {
                received.Add(update);
            }

            await context.Step.SleepAsync("pause", TimeSpan.FromMinutes(1));
            return string.Concat(received.Select(update => update.Text));
        });

        var run = await host.RunAsync("fn", Event(), new StepMocks().Sleep("pause"), Cancel);

        run.GetOutput<string>().ShouldBe("The quick brown fox");
        model.StreamCount.ShouldBe(1);
        run.Steps.Select(step => (step.Id, step.Type)).ShouldBe([("chat", StepType.AIWrap), ("pause", StepType.Sleep)]);

        // What the step saved is the complete response, from which the function read its updates, usage included.
        var saved = run.GetStep("chat").Output!.Value;
        saved.GetProperty("messages")[0].GetProperty("contents")[0].GetProperty("text").GetString().ShouldBe("The quick brown fox");
        saved.GetProperty("modelId").GetString().ShouldBe("gpt-test");
        saved.GetProperty("usage").GetProperty("totalTokenCount").GetInt64().ShouldBe(8);
        received.SelectMany(update => update.Contents).OfType<UsageContent>().Single().Details.TotalTokenCount.ShouldBe(8);
        received.Any(update => update.ModelId == "gpt-test").ShouldBeTrue();
    }

    [Fact]
    public async Task A_stream_is_a_step_only_once_it_is_enumerated()
    {
        var model = new FakeChatClient();
        var chat = new ChatClientBuilder(model).UseInngestSteps().Build();
        await using var host = For(async context =>
        {
            _ = chat.GetStreamingResponseAsync("Never read");
            return await context.Step.RunAsync("other", () => "done");
        });

        var run = await host.RunAsync("fn", Event(), Cancel);

        run.GetOutput<string>().ShouldBe("done");
        run.Steps.Select(step => step.Id).ShouldBe(["other"]);
        model.CallCount.ShouldBe(0);
    }

    [Fact]
    public async Task A_stream_and_a_response_in_one_run_number_their_steps_together()
    {
        var model = new FakeChatClient();
        var chat = new ChatClientBuilder(model).UseInngestSteps().Build();
        await using var host = For(async context =>
        {
            var first = await chat.GetResponseAsync("one");
            var second = await chat.GetStreamingResponseAsync("two").ToChatResponseAsync();
            return $"{first.Text}|{second.Text}";
        });

        var run = await host.RunAsync("fn", Event(), Cancel);

        run.GetOutput<string>().ShouldBe("Reply 0|Reply 1");
        run.Steps.Select(step => step.Id).ShouldBe(["chat", "chat:1"]);
    }

    [Fact]
    public async Task The_text_is_published_to_the_realtime_topic_while_the_model_streams_it()
    {
        var model = new FakeChatClient((_, _) => new ChatResponse(new ChatMessage(ChatRole.Assistant, "Once upon a time")));
        var chat = new ChatClientBuilder(model).UseInngestSteps().Build();
        var topic = new RealtimeTopic("chat:42", "tokens");
        var options = new ChatOptions().WithInngestRealtimeStream(topic);
        await using var host = For(async context =>
        {
            var reply = await chat.GetStreamingResponseAsync("Tell me a story", options).ToChatResponseAsync();
            await context.Step.SleepAsync("pause", TimeSpan.FromMinutes(1));
            return reply.Text;
        });

        var run = await host.RunAsync("fn", Event(), new StepMocks().Sleep("pause"), Cancel);

        run.GetOutput<string>().ShouldBe("Once upon a time");

        // Published once, by the step, and not again by the replay that followed.
        var published = run.Published.ShouldHaveSingleItem();
        published.Topic.ShouldBe(topic);
        published.IsStream.ShouldBeTrue();
        published.Data.GetString().ShouldBe("Once upon a time");
        model.StreamCount.ShouldBe(1);
    }

    [Fact]
    public async Task A_topic_does_nothing_for_a_call_that_does_not_stream()
    {
        var model = new FakeChatClient();
        var chat = new ChatClientBuilder(model).UseInngestSteps().Build();
        var options = new ChatOptions().WithInngestRealtimeStream(new RealtimeTopic("chat:42", "tokens"));
        await using var host = For(async context => (await chat.GetResponseAsync("Hello", options)).Text);

        var run = await host.RunAsync("fn", Event(), Cancel);

        run.GetOutput<string>().ShouldBe("Reply 0");
        run.Published.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_realtime_failure_is_logged_and_does_not_fail_the_model_call()
    {
        var model = new FakeChatClient((_, _) => new ChatResponse(new ChatMessage(ChatRole.Assistant, "Once upon a time")));
        var chat = new ChatClientBuilder(model).UseInngestSteps().Build();
        var options = new ChatOptions().WithInngestRealtimeStream(new RealtimeTopic("chat:42", "tokens"));
        var services = Services(async context => (await chat.GetStreamingResponseAsync("Tell me a story", options).ToChatResponseAsync()).Text).AddFakeLogging();
        services.AddSingleton<IInngestRealtime>(new BrokenRealtime());
        await using var host = new InngestTestHost(services);

        var run = await host.RunAsync("fn", Event(), Cancel);

        run.Status.ShouldBe(TestRunStatus.Completed);
        run.GetOutput<string>().ShouldBe("Once upon a time");
        run.GetStep("chat").Attempts.ShouldBe(1);
        model.StreamCount.ShouldBe(1);
        host.Services.GetFakeLogCollector().GetSnapshot().ShouldContain(record => record.Message.Contains("could not be streamed to topic tokens of channel chat:42"));
    }

    [Fact]
    public async Task A_stream_that_fails_is_retried_as_a_step()
    {
        var model = new FakeChatClient { Fail = call => call == 0 ? new HttpRequestException("connection reset") : null };
        var chat = new ChatClientBuilder(model).UseInngestSteps().Build();
        await using var host = For(async context => (await chat.GetStreamingResponseAsync("Hello").ToChatResponseAsync()).Text);

        var run = await host.RunAsync("fn", Event(), Cancel);

        run.GetOutput<string>().ShouldBe("Reply 1");
        run.GetStep("chat").Attempts.ShouldBe(2);
    }

    private sealed class BrokenRealtime : IInngestRealtime
    {
        public Task PublishAsync<T>(RealtimeTopic topic, T data, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task PublishAsync<T>(RealtimeTopic<T> topic, T data, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async Task<string> PublishStreamAsync(RealtimeTopic topic, IAsyncEnumerable<string> chunks, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            throw new InngestException("Inngest answered 500.");
        }

        public Task<RealtimeToken> GetSubscriptionTokenAsync(string channel, IReadOnlyList<string> topics, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<RealtimeMessage<JsonElement>> SubscribeAsync(string channel, IReadOnlyList<string> topics, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<RealtimeMessage<T>> SubscribeAsync<T>(RealtimeTopic<T> topic, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
