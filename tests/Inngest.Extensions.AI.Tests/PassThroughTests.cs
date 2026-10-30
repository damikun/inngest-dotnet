using Inngest.Testing;
using Microsoft.Extensions.AI;
using static Inngest.Tests.Testing.TestHosts;

namespace Inngest.Extensions.AI.Tests;

// Where there is no step to make, outside a run and inside a step's handler, the chat client changes nothing but the
// options, which it hands on without the settings that are meant for it.
public sealed class PassThroughTests
{
    [Fact]
    public async Task Outside_a_run_a_call_goes_to_the_inner_client_as_it_is()
    {
        var model = new FakeChatClient();
        var chat = new ChatClientBuilder(model).UseInngestSteps().Build();
        var options = new ChatOptions { Temperature = 0.3f };
        List<ChatMessage> messages = [new(ChatRole.User, "Hello")];

        var reply = await chat.GetResponseAsync(messages, options, TestContext.Current.CancellationToken);

        reply.Text.ShouldBe("Reply 0");
        model.CallCount.ShouldBe(1);
        model.Messages.ShouldHaveSingleItem().ShouldBe(messages);
        model.Options.ShouldHaveSingleItem().ShouldBeSameAs(options);
    }

    [Fact]
    public async Task Outside_a_run_a_stream_comes_from_the_inner_client_as_it_is()
    {
        var model = new FakeChatClient((_, _) => new ChatResponse(new ChatMessage(ChatRole.Assistant, "Hello there world")));
        var chat = new ChatClientBuilder(model).UseInngestSteps().Build();

        var updates = await chat.GetStreamingResponseAsync("Hi", cancellationToken: TestContext.Current.CancellationToken).CollectAsync();

        updates.Select(update => update.Text).Where(text => text.Length > 0).ShouldBe(["Hello ", "there ", "world"]);
        model.StreamCount.ShouldBe(1);
    }

    [Fact]
    public async Task The_settings_for_the_client_never_reach_the_inner_client()
    {
        var model = new FakeChatClient();
        var chat = new ChatClientBuilder(model).UseInngestSteps().Build();
        var options = new ChatOptions { AdditionalProperties = new() { ["provider_option"] = "kept" } }
            .WithInngestStepId("summarize")
            .WithInngestRealtimeStream(new RealtimeTopic("chat:1", "tokens"));

        await chat.GetResponseAsync("Hello", options, TestContext.Current.CancellationToken);
        await chat.GetStreamingResponseAsync("Hello", options, TestContext.Current.CancellationToken).CollectAsync();

        model.Options.Count.ShouldBe(2);
        model.Options.ShouldAllBe(received => received!.AdditionalProperties!.Count == 1 && (string?)received.AdditionalProperties["provider_option"] == "kept");

        // The caller's options are not changed: they still carry the settings for its next call, as plain strings, which a cache or a
        // logger outside the client can write as JSON, also under Native AOT.
        var settings = CallSettings.Of(options);
        (settings.StepId, settings.StreamTo).ShouldBe(("summarize", new RealtimeTopic("chat:1", "tokens")));
        options.AdditionalProperties!.Where(property => property.Key != "provider_option").ShouldAllBe(property => property.Value is string);
    }

    [Fact]
    public async Task Inside_a_step_handler_a_call_is_not_a_step_of_its_own()
    {
        var model = new FakeChatClient();
        var chat = new ChatClientBuilder(model).UseInngestSteps().Build();
        await using var host = For(async context =>
        {
            var viaGetResponse = await context.Step.RunAsync("agent", async () => (await chat.GetResponseAsync("Hello")).Text);
            var viaStream = await context.Step.RunAsync("streaming-agent", async () =>
            {
                var text = string.Empty;
                await foreach (var update in chat.GetStreamingResponseAsync("Hello"))
                {
                    text += update.Text;
                }

                return text;
            });
            return $"{viaGetResponse}|{viaStream}";
        });

        var run = await host.RunAsync("fn", Event(), Cancel);

        run.Status.ShouldBe(TestRunStatus.Completed);
        run.GetOutput<string>().ShouldBe("Reply 0|Reply 1");
        run.Steps.Select(step => (step.Id, step.Type)).ShouldBe([("agent", StepType.Run), ("streaming-agent", StepType.Run)]);
        model.CallCount.ShouldBe(2);
    }

    [Fact]
    public async Task A_stream_enumerated_inside_a_step_handler_is_a_plain_stream_even_when_it_was_made_in_the_function()
    {
        var model = new FakeChatClient();
        var chat = new ChatClientBuilder(model).UseInngestSteps().Build();
        await using var host = For(async context =>
        {
            var stream = chat.GetStreamingResponseAsync("Hello");
            return await context.Step.RunAsync("read", async () =>
            {
                var text = string.Empty;
                await foreach (var update in stream)
                {
                    text += update.Text;
                }

                return text;
            });
        });

        var run = await host.RunAsync("fn", Event(), Cancel);

        run.GetOutput<string>().ShouldBe("Reply 0");
        run.Steps.Select(step => step.Id).ShouldBe(["read"]);
    }
}
