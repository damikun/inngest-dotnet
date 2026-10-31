using Inngest.Testing;
using Inngest.Tests.Execution;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static Inngest.Tests.Testing.TestHosts;

namespace Inngest.Extensions.AI.Tests;

// A step's id is claimed in the order the calls reach the chat client. Calls that are made after something that takes a varying
// time reach it in another order on a replay, and the call that claims an id then gets the result made for another. Every
// result is saved with a digest of the input it was made for, so that a replay can tell.
public sealed class InputDigestTests : IDisposable
{
    private const string Category = "Inngest.Extensions.AI.InngestStepsChatClient";

    private readonly EngineHost _host = new();

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task A_result_made_for_another_input_is_returned_with_a_warning_that_names_the_fix()
    {
        var model = new FakeChatClient();
        var chat = new ChatClientBuilder(model).UseInngestSteps().Build();
        var function = TestFunction.Define(async context => (await chat.GetResponseAsync("Say hello")).Text);

        var result = await _host.CallAsync(function, Call.New().Data("chat", Saved("Remembered", "00000000000000000000000000000000")));

        result.StatusCode.ShouldBe(200);
        result.Body.ShouldBe("\"Remembered\"");
        model.CallCount.ShouldBe(0);
        var warning = _host.LogsOf(Category).ShouldHaveSingleItem();
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.Message.ShouldContain("Step chat of run 01JRUN");
        warning.Message.ShouldContain("WithInngestStepId");
    }

    [Fact]
    public async Task A_result_made_for_the_same_input_raises_no_warning()
    {
        var model = new FakeChatClient();
        var chat = new ChatClientBuilder(model).UseInngestSteps().Build();
        var function = TestFunction.Define(async context => (await chat.GetResponseAsync("Say hello")).Text);
        var digest = ChatJson.WriteInput([new ChatMessage(ChatRole.User, "Say hello")], null).Digest;

        var result = await _host.CallAsync(function, Call.New().Data("chat", Saved("Remembered", digest)));

        result.Body.ShouldBe("\"Remembered\"");
        _host.LogsOf(Category).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_result_saved_without_a_digest_is_not_checked()
    {
        var model = new FakeChatClient();
        var chat = new ChatClientBuilder(model).UseInngestSteps().Build();
        var function = TestFunction.Define(async context => (await chat.GetResponseAsync("Say hello")).Text);

        var result = await _host.CallAsync(function, Call.New().Data("chat", Saved("Remembered", digest: null)));

        result.Body.ShouldBe("\"Remembered\"");
        _host.LogsOf(Category).ShouldBeEmpty();
    }

    [Fact]
    public async Task The_digest_is_of_the_input_of_the_code_and_an_edit_of_it_does_not_change_it()
    {
        var model = new FakeChatClient();
        var chat = new ChatClientBuilder(model).UseInngestSteps().Build();
        var function = TestFunction.Define(async context => (await chat.GetResponseAsync("Say hello")).Text);
        const string Edited = """[{"messages":[{"role":"user","contents":[{"$type":"text","text":"Say goodbye"}]}]}]""";

        var run = await _host.CallAsync(function, Call.New().Input("chat", Edited));

        run.OpCodes.ShouldBe(["StepRun"]);
        model.Messages.ShouldHaveSingleItem()[0].Text.ShouldBe("Say goodbye");
        var saved = run.Op().GetProperty("data");
        ChatJson.InputDigestOf(saved).ShouldBe(ChatJson.WriteInput([new ChatMessage(ChatRole.User, "Say hello")], null).Digest);

        // The code asks for the same input on the replay, so nothing is wrong.
        var replay = await _host.CallAsync(function, Call.New().Data("chat", saved.GetRawText()));
        replay.StatusCode.ShouldBe(200);
        _host.LogsOf(Category).ShouldBeEmpty();
    }

    [Fact]
    public async Task The_digest_is_saved_also_when_the_input_is_not_recorded()
    {
        var model = new FakeChatClient();
        var chat = new ChatClientBuilder(model).UseInngestSteps(options => options.RecordInput = false).Build();
        var function = TestFunction.Define(async context => (await chat.GetResponseAsync("A secret prompt")).Text);

        var run = await _host.CallAsync(function, Call.New());

        run.Op().GetProperty("opts").TryGetProperty("input", out _).ShouldBeFalse();
        ChatJson.InputDigestOf(run.Op().GetProperty("data")).ShouldBe(ChatJson.WriteInput([new ChatMessage(ChatRole.User, "A secret prompt")], null).Digest);
    }

    [Fact]
    public async Task A_tool_result_made_for_other_arguments_is_returned_with_a_warning_too()
    {
        var model = new FakeChatClient();
        var chat = new ChatClientBuilder(model).UseInngestFunctionInvocation().UseInngestSteps().Build();
        var weather = AIFunctionFactory.Create((string city) => $"Sunny in {city}", "get_weather", "Gets the weather of a city");
        var function = TestFunction.Define(async context => (await chat.GetResponseAsync("Weather?", new ChatOptions { Tools = [weather] })).Text);

        // The tool runs for Prague once, and what it saved is what the replay finds for Brno.
        var forPrague = await _host.CallAsync(function, Call.New().Data("chat", Calling("Prague")));
        var saved = forPrague.Op().GetProperty("data").GetRawText();
        var forBrno = await _host.CallAsync(function, Call.New().Data("chat", Calling("Brno")).Data("tool get_weather", saved));

        forBrno.StatusCode.ShouldNotBe(500);
        var warning = _host.LogsOf(Category).ShouldHaveSingleItem();
        warning.Message.ShouldContain("Step tool get_weather of run 01JRUN");
    }

    [Fact]
    public async Task Calls_that_reach_the_client_in_another_order_on_a_replay_are_caught_and_named_calls_are_not_affected()
    {
        var model = new FakeChatClient((_, messages) => new ChatResponse(new ChatMessage(ChatRole.Assistant, $"Answer to {messages[^1].Text}")));
        var chat = new ChatClientBuilder(model).UseInngestSteps().Build();
        var executions = 0;
        var unnamed = new List<string>();
        var named = new List<string>();
        var services = Services(async context =>
        {
            // Two calls that each wait for something first, which finishes in another order from one execution to the next.
            var execution = Interlocked.Increment(ref executions);
            string[] languages = ["cs", "de"];
            var gates = languages.ToDictionary(language => language, _ => new TaskCompletionSource());
            var calls = languages.Select(async language =>
            {
                await gates[language].Task;
                var first = await chat.GetResponseAsync($"Translate to {language}");
                var second = await chat.GetResponseAsync($"Say goodbye in {language}", new ChatOptions().WithInngestStepId($"goodbye-{language}"));
                return (first.Text, second.Text);
            }).ToArray();
            foreach (var language in execution % 2 == 1 ? languages : (string[])[languages[1], languages[0]])
            {
                gates[language].SetResult();
            }

            var results = await Task.WhenAll(calls);
            unnamed = [.. results.Select(result => result.Item1)];
            named = [.. results.Select(result => result.Item2)];
            return null;
        });
        await using var host = new InngestTestHost(services.AddFakeLogging());

        var run = await host.RunAsync("fn", Event(), Cancel);

        run.Status.ShouldBe(TestRunStatus.Completed);

        // Each call named for itself got its own answer, however the calls arrived. The others claimed ids in the order they arrived,
        // and the warning says so.
        named.ShouldBe(["Answer to Say goodbye in cs", "Answer to Say goodbye in de"]);
        host.Services.GetFakeLogCollector().GetSnapshot().Where(record => record.Category == Category).ShouldNotBeEmpty();
        host.Services.GetFakeLogCollector().GetSnapshot().Where(record => record.Category == Category)
            .ShouldAllBe(record => record.Message.Contains("WithInngestStepId") && record.Message.Contains("Step chat"));
        unnamed.Count.ShouldBe(2);
    }

    // The response of the model as a step saves it, with the digest of the input it was made for.
    private static string Saved(string text, string? digest) =>
        "{\"messages\":[{\"role\":\"assistant\",\"contents\":[{\"$type\":\"text\",\"text\":\"" + text + "\"}]}]"
        + (digest is null ? string.Empty : ",\"inngest\":{\"inputDigest\":\"" + digest + "\"}")
        + "}";

    private static string Calling(string city) =>
        $$$"""{"messages":[{"role":"assistant","contents":[{"$type":"functionCall","callId":"call_1","name":"get_weather","arguments":{"city":"{{{city}}}"}}]}],"finishReason":"tool_calls"}""";
}
